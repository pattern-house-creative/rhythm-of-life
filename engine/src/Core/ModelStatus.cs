using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Knowledge.Adapters;
using Knowledge.Relationships;

namespace Knowledge;

// What each model in the stack is doing (SB-P2-01; docs/models.md lists every model and its job): the embeddings model
// that search, saved documents and the map's similar links use, a shadow one being built beside it when the settings
// name one, and the titles model. `/health` adds it to its answer, `app_status` gives it to the app's Sources view, and
// `rhythm doctor` prints it. States and counts only: never a title, a text, an id of a record or a vector.
//
// One entry per model: {job, role, name, id, state, index, failure}.
// - job `embeddings`: role `live` (always listed) or `shadow` (only when adapters.embeddings.shadow names one); `name` is
//   the settings' short name, `id` the tag its vectors carry (EmbeddingModels). `state` is on, off (the settings turn it
//   off), starting (not loaded yet since the start), downloading, download-failed, unverified (its files' checksums do
//   not match) or wrong-model. `index` is {passages, indexing, pending, pausedUntil}: the passages stored under its id,
//   whether the index job covers it (adapters.embeddings.backfill, .sources or .shadow; off for an existing install),
//   the rows still without its passages while it does (null otherwise), and when a paused job runs again; null before the
//   counts were first read, and always while memory.scopes is on (below).
//   Role `map` (only when graph.similarity.model names one, D-256): the model whose vectors make the map's similar and
//   copy links, with the same states; `off` while the map takes no vectors at all (similar links or the embeddings
//   adapter off). While it is not `on` the map's similar links use the live model's vectors and graph.similarity's
//   thresholds, as without it. It has no passages, so its `index` is null.
// - job `titles`: role `live`; `name` is adapters.titles.model, `id` null. `state` is off (the adapter is off), on, or
//   failing (the newest rebuild that asked it for a title stopped on a failure); `failure` is that failure's kind
//   (TitleFailures) and when it happened, null otherwise. `index` is null.
// Index counts are read at most once a minute, beside the request: an answer never waits for them, so a slow count
// never makes the engine look unhealthy; until they are first read `index` is null. They are install-wide, every
// scope's rows together, so while the recorded memory.scopes mode is on (anything but exactly off, as Database.ScopesOn
// reads it) they are left out and `index` is null for every caller, `/health` included (docs/dev/memory.md 6.2: no
// count across scopes its reader can't read); the operator reads them with `rhythm doctor`, from the database.
public sealed partial class ModelStatus(Database db, RhythmConfig config, Embeddings embeddings, AdapterRegistry adapters, RelationshipHost? host = null,
    TimeProvider? clock = null)
{
    public static readonly TimeSpan CountEvery = TimeSpan.FromMinutes(1);
    static readonly TimeSpan CountLimit = TimeSpan.FromSeconds(20);
    readonly TimeProvider time = clock ?? TimeProvider.System;
    readonly object gate = new();
    (DateTimeOffset At, IReadOnlyDictionary<string, ModelIndex>? Value)? counted;
    Task<IReadOnlyDictionary<string, ModelIndex>?>? counting;

    // The engine's version, as its MCP serverInfo gives it, and its schema: the number of the newest migration this
    // build carries (the database holds every one of them whenever the engine answers).
    public static string Version => typeof(ModelStatus).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public static int Schema { get; } = Database.Migrations().Select(m => MigrationNumber().Match(m.Version))
        .Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max();

    public sealed record ModelIndex(long Passages, bool Indexing, long? Pending, DateTimeOffset? PausedUntil);

    // `/health`'s answer while the engine is ready: the fields it always had (status, embeddings, adapters), then the
    // version, the schema and the models.
    public async Task<JsonObject> Health(CancellationToken ct, bool wait = false) => new()
    {
        ["status"] = "ok",
        ["embeddings"] = embeddings.Status,
        ["adapters"] = new JsonArray([.. adapters.Enabled.Select(a => (JsonNode?)a.Name)]),
        ["version"] = Version,
        ["schema"] = Schema,
        ["models"] = await Models(ct, wait),
    };

    // app_status's structuredContent (contracts/mcp/app_status.output.schema.json).
    public async Task<JsonObject> App(CancellationToken ct, bool wait = false) => new()
    {
        ["version"] = Version,
        ["schema"] = Schema,
        ["models"] = await Models(ct, wait),
    };

    public async Task<JsonArray> Models(CancellationToken ct, bool wait = false)
    {
        var settings = EmbeddingSettings.From(config);
        var counts = await Counts(settings, wait, ct);
        var models = new JsonArray
        {
            Embedding("live", settings.Model, settings.Enabled ? embeddings.Status : "off", settings, counts),
        };
        if (settings.Shadow is { } shadow)
            models.Add(Embedding("shadow", shadow, settings.Enabled ? embeddings.Shadow?.Status ?? "off" : "off", settings, counts));
        if (MapModels.Named(config.Graph.Similarity.Model) is { } map)
        {
            var wanted = MapModels.Wanted(config) is not null;
            models.Add(new JsonObject
            {
                ["job"] = "embeddings", ["role"] = "map", ["name"] = map.Name, ["id"] = map.Id,
                ["state"] = !wanted ? "off" : embeddings.Map?.Status is { } status && status != "off" ? status : "starting",
                ["index"] = null, ["failure"] = null,
            });
        }
        var titles = config.AdapterEnabled(Adapters.Titles.TitlesAdapter.AdapterName) && adapters.Titles is not null;
        var failure = titles ? host?.TitleFailure : null;
        models.Add(new JsonObject
        {
            ["job"] = "titles", ["role"] = "live",
            ["name"] = titles ? config.Adapter(Adapters.Titles.TitlesAdapter.AdapterName).String("model") : null,
            ["id"] = null,
            ["state"] = !titles ? "off" : failure is null ? "on" : "failing",
            ["index"] = null,
            ["failure"] = failure is null ? null : new JsonObject { ["kind"] = failure.Kind, ["at"] = failure.At.ToString("O") },
        });
        return models;
    }

    static JsonObject Embedding(string role, EmbeddingModel model, string status, EmbeddingSettings settings,
        IReadOnlyDictionary<string, ModelIndex>? counts) => new()
    {
        ["job"] = "embeddings", ["role"] = role, ["name"] = model.Name, ["id"] = model.Id,
        // Settings that turn the model on and a holder not yet told to load it: the start has not reached it yet.
        ["state"] = settings.Enabled && status == "off" ? "starting" : status,
        ["index"] = counts is null ? null : Index(counts.GetValueOrDefault(model.Id) ?? new(0, false, null, null)),
        ["failure"] = null,
    };
    static JsonObject Index(ModelIndex i) => new()
    {
        ["passages"] = i.Passages, ["indexing"] = i.Indexing, ["pending"] = i.Pending, ["pausedUntil"] = i.PausedUntil?.ToString("O"),
    };

    // What the index job covers for one model, as EmbeddingBackfill.Targets runs it: the live model when the backfill is
    // on or sources are listed, the shadow one whenever it is named. Null when the job does not cover it.
    PassageSources? Covered(EmbeddingSettings settings, string role)
    {
        if (!settings.Enabled) return null;
        var listed = PassageSources.For(settings, adapters.Enabled, settings.Backfill);
        return role == "live" ? settings.Backfill || listed.Sources ? listed : null : listed with { Copies = true };
    }

    // The last counts, read again in the background when they are older than CountEvery; `wait` waits for fresh ones.
    async Task<IReadOnlyDictionary<string, ModelIndex>?> Counts(EmbeddingSettings settings, bool wait, CancellationToken ct)
    {
        Task<IReadOnlyDictionary<string, ModelIndex>?> task;
        lock (gate)
        {
            if (counted is { } last && time.GetUtcNow() - last.At < CountEvery) return last.Value;
            task = counting ??= Task.Run(() => Count(settings));
            if (!wait) return counted?.Value;
        }
        return await task.WaitAsync(ct);
    }

    async Task<IReadOnlyDictionary<string, ModelIndex>?> Count(EmbeddingSettings settings)
    {
        using var limit = new CancellationTokenSource(CountLimit);
        try
        {
            var ct = limit.Token;
            // With scopes on nothing is counted: null, kept as the last answer like a count.
            if (await db.ScopesOn(ct)) { lock (gate) { counted = (time.GetUtcNow(), null); counting = null; } return null; }
            var passages = new Dictionary<string, long>(StringComparer.Ordinal);
            await using (var cmd = db.Source.CreateCommand("SELECT model,count(*) FROM resource_chunks GROUP BY model"))
            await using (var row = await cmd.ExecuteReaderAsync(ct))
                while (await row.ReadAsync(ct)) passages[row.GetString(0)] = row.GetInt64(1);
            var backfill = new EmbeddingBackfill(db, time);
            var result = new Dictionary<string, ModelIndex>(StringComparer.Ordinal);
            foreach (var (role, model) in new[] { ("live", settings.Model), ("shadow", settings.Shadow) })
            {
                if (model is null || result.ContainsKey(model.Id)) continue;
                var covered = Covered(settings, role);
                var paused = (await backfill.Cursor(model.Id, ct)).PausedUntil;
                result[model.Id] = new(passages.GetValueOrDefault(model.Id), covered is not null,
                    covered is null ? null : await backfill.Unindexed(model.Id, covered, ct), paused > time.GetUtcNow() ? paused : null);
            }
            lock (gate) { counted = (time.GetUtcNow(), result); counting = null; }
            return result;
        }
        // A database that does not answer in time keeps the last counts; the next request tries again.
        catch (Exception e) when (e is Npgsql.NpgsqlException or OperationCanceledException or InvalidOperationException or TimeoutException)
        {
            lock (gate) counting = null;
            return counted?.Value;
        }
    }

    [GeneratedRegex(@"(?:^|\.)(\d{3})_[a-z0-9_]+\.sql$")] private static partial Regex MigrationNumber();
}

// Why the titles model gave no title, by kind (SB-P2-01): logged with the rebuild's notes and shown by ModelStatus.
// - timeout: no answer within the adapter's 30 s, the likeliest one being a model still loading after a cold start;
// - unreachable: no connection to the titles service, or one that broke off;
// - http-<status>: the service answered with an error status (404 when the model named is not pulled into it);
// - too-large or unreadable: an answer past 64 KiB, or one that is not the generate API's JSON.
public static class TitleFailures
{
    public static string Kind(Exception e) => e switch
    {
        OperationCanceledException or TimeoutException => "timeout",
        HttpRequestException { StatusCode: { } status } => $"http-{(int)status}",
        HttpRequestException or IOException => "unreachable",
        InvalidDataException => "too-large",
        _ => "unreadable",
    };
    // One of the kinds above, as a stored failure is read back (RelationshipHost.TitleFailure).
    public static bool Known(string kind) => kind is "timeout" or "unreachable" or "too-large" or "unreadable"
        || System.Text.RegularExpressions.Regex.IsMatch(kind, "^http-[0-9]{3}$");
}

// One rebuild's titles: how many the model was asked for, how many it gave, how many of its answers were unusable (the
// excerpt kept), and the kind of failure that stopped the pass asking, if one did.
public sealed record TitlePass(int Asked, int Titled, int Unusable, string? Failure);
// The newest failure that stopped titling, until a rebuild asks again without one.
public sealed record TitleFailure(string Kind, DateTimeOffset At);
