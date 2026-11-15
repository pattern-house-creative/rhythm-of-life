using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Relationships;
using Npgsql;

namespace Knowledge.Adapters.Demo;

// The fictional demo dataset as an adapter, and the worked example in docs/dev/adapters.md. On when `demo.enabled`
// (the default), off otherwise. Everything it writes belongs to the synthetic subject "fixture", so the app labels it
// DEMO and it never mixes with a person's own data.
//   MeasurementSource: everyday wellbeing numbers (steps, sleep, resting heart rate, an evening mood) from
//                      demo/engine/measurements.json, generated deterministically and written every 6 hours through
//                      the same rules as a plugin's ingest_measurements (so the catalog shows them as data stars).
//                      After the first sync each source writes only what came since its last write, so archived
//                      originals stay small. A demo source the seed no longer has (an older build's glucose) leaves.
//   MemorySource:      notes from demo/engine/notes.json, with their links and projects, as stars on the map.
// It also saves the seed's dashboards, each card in its corner while that corner is free, and one insight
// (demo/engine/showcase.json); with the demo off, Retire takes away the ones nobody changed.
public sealed class DemoAdapter : IMeasurementSource, IMemorySource
{
    public const string AdapterName = "demo", Provider = "demo";
    // An older build's live feed of fictional glucose, retired with the rest of what the seed no longer has.
    const string RetiredProvider = "demo-live";
    public string Name => AdapterName;
    public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } = new Dictionary<string, ProviderPolicy>
    {
        [Provider] = new(SourceAuthority.AuthorizedArchive, "health"),
    };
    public TimeSpan Interval => TimeSpan.FromMinutes(5);
    // Its rows are the fictional `fixture` subject's, which every reader reads (IAdapter.Owner).
    public string Owner => RhythmConfig.Synthetic;
    static readonly TimeSpan Full = TimeSpan.FromHours(6);
    DateTimeOffset nextFull = DateTimeOffset.MinValue;
    readonly Dictionary<string, DateTimeOffset> written = [];
    AdapterContext? context;

    public void Configure(AdapterContext context) => this.context = context;

    static JsonDocument Seed(string file)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Knowledge.Demo.{file}")
            ?? throw new InvalidOperationException($"Demo seed {file} is missing from this build");
        return JsonDocument.Parse(stream);
    }

    // ---- MeasurementSource -------------------------------------------------------------------------------------
    public async Task Sync(MeasurementSink sink, CancellationToken ct)
    {
        // Everything, and the showcase, every 6 hours.
        var now = DateTimeOffset.UtcNow;
        if (now < nextFull) return;
        using var spec = Seed("measurements.json");
        var zone = spec.RootElement.GetProperty("timezone").GetString()!;
        static string ProviderOf(JsonElement s) => s.TryGetProperty("provider", out var p) ? p.GetString()! : Provider;
        var kept = new List<string>();
        foreach (var source in spec.RootElement.GetProperty("series").EnumerateArray()
            .GroupBy(s => (Name: s.GetProperty("source").GetString()!, Provider: ProviderOf(s))))
        {
            // Since an hour before this source's last write (the same values again, as they are deterministic).
            DateTimeOffset? since = written.TryGetValue(source.Key.Name, out var last) ? last.AddHours(-1) : null;
            var samples = source.SelectMany(series => Generate(series, now, since)).ToArray();
            var externalId = "demo-" + string.Join('-', source.Key.Name.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
            foreach (var (chunk, i) in samples.Chunk(Ingest.MaxMeasurements).Select((c, i) => (c, i)))
            {
                var args = new JsonObject
                {
                    ["provider"] = source.Key.Provider, ["subject"] = RhythmConfig.Synthetic,
                    ["idempotencyKey"] = $"demo:{externalId}:{now:yyyyMMddHHmmss}:{i}",
                    ["source"] = new JsonObject
                    {
                        ["externalId"] = externalId, ["kind"] = "demo-readings", ["title"] = "DEMO · " + source.Key.Name,
                        ["fetchedAt"] = Iso(now), ["timezone"] = zone, ["device"] = new JsonObject { ["id"] = externalId, ["model"] = "Demo device" }
                    },
                    ["measurements"] = new JsonArray(chunk.Select(m => (JsonNode)m).ToArray())
                };
                using var json = JsonDocument.Parse(args.ToJsonString());
                await sink.Write(json.RootElement, ct);
            }
            written[source.Key.Name] = now;
            kept.Add(externalId);
        }
        await Retire(kept, ct);
        await Showcase(ct);
        nextFull = now + Full;
    }

    // Demo sources the seed no longer has leave the map and the glucose views; their archived originals stay.
    async Task Retire(IReadOnlyList<string> kept, CancellationToken ct)
    {
        if (context is null) return;
        await using var cmd = context.Db.Source.CreateCommand("""
            UPDATE resources SET status='deleted',indexed_at=now()
            WHERE subject_id=@s AND provider=ANY(@p) AND status='current' AND external_id LIKE 'demo-%' AND NOT external_id=ANY(@kept)
            """);
        cmd.Parameters.AddWithValue("s", RhythmConfig.Synthetic);
        cmd.Parameters.AddWithValue("p", new[] { Provider, RetiredProvider });
        cmd.Parameters.AddWithValue("kept", kept.ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Deterministic, fictional numbers: a daily rhythm plus noise derived from the metric and the time, so the same
    // slot always gets the same value and a repeated sync changes nothing.
    static IEnumerable<JsonObject> Generate(JsonElement s, DateTimeOffset now, DateTimeOffset? since = null)
    {
        var metric = s.GetProperty("metric").GetString()!; var unit = s.GetProperty("unit").GetString()!;
        double Num(string name) => s.GetProperty(name).GetDouble();
        var every = s.GetProperty("every").GetString()!;
        var daily = every == "1d";
        var step = daily ? TimeSpan.FromDays(1) : TimeSpan.FromMinutes(int.Parse(every.TrimEnd('m'), CultureInfo.InvariantCulture));
        var end = daily ? new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddHours(7)
            : new DateTimeOffset(now.UtcTicks - now.UtcTicks % step.Ticks, TimeSpan.Zero);
        if (daily && end > now) end = end.AddDays(-1);
        var start = end - TimeSpan.FromDays(Num("days")) + step;
        for (var at = since is { } from && from > start ? start + (from - start).Ticks / step.Ticks * step : start; at <= end; at += step)
        {
            var phase = daily ? at.DayOfYear / 7.0 * Math.PI : at.TimeOfDay.TotalHours / 24 * 2 * Math.PI;
            var value = Num("base") + Num("swing") * Math.Sin(phase) + Num("noise") * (Noise(metric, at) * 2 - 1);
            value = Math.Round(Math.Clamp(value, Num("min"), Num("max")));
            yield return new JsonObject
            {
                ["metric"] = metric, ["value"] = value, ["unit"] = unit, ["observedAt"] = Iso(at), ["key"] = Iso(at), ["quality"] = "ok"
            };
        }
    }
    static double Noise(string metric, DateTimeOffset at)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{metric}|{at.ToUnixTimeSeconds()}"));
        return BitConverter.ToUInt32(hash, 0) / (double)uint.MaxValue;
    }

    // The demo's dashboards and its insight. A dashboard's seed version is saved as its next version unless the newest
    // already has that page or someone has changed it since (a version, or an arrangement of one); a newly saved one
    // takes its corner while the corner is free. Seed versions have no layout: each is its page alone.
    async Task Showcase(CancellationToken ct)
    {
        if (context is null) return;
        using var seed = Seed("showcase.json");
        var root = seed.RootElement;
        var principal = await new Principals(context.Db.Source).System("demo", ct);
        // {data:<group>} is the demo subject's data star for that group; {note:<id>} one demo note.
        string Star(string placeholder) =>
            placeholder.StartsWith("{data:", StringComparison.Ordinal) ? new ResourceKey(RhythmConfig.Synthetic, CatalogStars.Provider,
                CatalogStars.DataAccount(Provider), $"{RhythmConfig.Synthetic}:{placeholder[6..^1]}").GraphId
            : placeholder.StartsWith("{note:", StringComparison.Ordinal) ? Key(placeholder[6..^1]).GraphId : placeholder;
        JsonNode? Replace(JsonNode? node) => node switch
        {
            JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, Replace(p.Value)))),
            JsonArray a => new JsonArray(a.Select(Replace).ToArray()),
            JsonValue v when v.TryGetValue<string>(out var text) && text.StartsWith('{') && text.EndsWith('}') => Star(text),
            _ => node?.DeepClone()
        };
        var insight = (JsonObject)Replace(JsonNode.Parse(root.GetProperty("insight").GetRawText()))!;
        await using var c = await context.Db.Source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        foreach (var seeded in root.GetProperty("dashboards").EnumerateArray())
        {
            var dashboard = (JsonObject)Replace(JsonNode.Parse(seeded.GetRawText()))!;
            var stars = new JsonArray(dashboard["feeds"]!.AsArray().Select(f => (JsonNode)new JsonObject { ["graphId"] = (string)f!["star"]!, ["title"] = "DEMO data" }).ToArray());
            var id = Guid.Parse((string)dashboard["id"]!);
            await using (var cmd = Database.Command(c, tx, "INSERT INTO dashboards(id,created_by) VALUES(@id,@p) ON CONFLICT DO NOTHING", ("id", id), ("p", principal)))
                await cmd.ExecuteNonQueryAsync(ct);
            await using (var save = Database.Command(c, tx, """
                WITH newest AS (SELECT version,html,saved_by FROM dashboard_versions WHERE dashboard_id=@id ORDER BY version DESC LIMIT 1)
                INSERT INTO dashboard_versions(dashboard_id,version,title,description,html,feeds,stars,note,saved_by)
                SELECT @id,coalesce((SELECT version FROM newest),0)+1,@title,@description,@html,@feeds::jsonb,@stars::jsonb,@note,@p
                WHERE NOT EXISTS(SELECT 1 FROM newest WHERE html=@html OR saved_by<>@p)
                  AND NOT EXISTS(SELECT 1 FROM dashboard_layouts WHERE dashboard_id=@id AND saved_by<>@p)
                """, ("id", id), ("p", principal), ("title", (string)dashboard["title"]!), ("description", (string)dashboard["description"]!),
                ("html", (string)dashboard["html"]!), ("feeds", dashboard["feeds"]!.ToJsonString()), ("stars", stars.ToJsonString()), ("note", (string)dashboard["note"]!)))
                if (await save.ExecuteNonQueryAsync(ct) == 0) continue;
            await using (var cmd = Database.Command(c, tx, """
                UPDATE dashboards SET updated_at=now() WHERE id=@id;
                INSERT INTO ambient_cards(corner,dashboard_id,set_by) SELECT @corner::text,@id,@p WHERE @corner::text IS NOT NULL ON CONFLICT DO NOTHING
                """, ("id", id), ("corner", (string?)dashboard["corner"]), ("p", principal)))
                await cmd.ExecuteNonQueryAsync(ct);
        }
        var now = DateTimeOffset.UtcNow;
        var insightStars = new JsonArray(insight["stars"]!.AsArray().Select(s => (JsonNode)new JsonObject { ["graphId"] = (string)s!, ["title"] = "DEMO" }).ToArray());
        var sources = new JsonArray(insight["sources"]!.AsArray().Select(s => { var o = (JsonObject)s!.DeepClone(); o["retrievedAt"] = Iso(now); return (JsonNode)o; }).ToArray());
        var saved = Guid.Parse((string)insight["id"]!);
        await using (var cmd = Database.Command(c, tx, """
            INSERT INTO insights(id,kind,title,summary,why_it_matters,next_step,points,evidence_level,limitations,stars,sources,generated_by,generated_at,sources_as_of,saved_by)
            VALUES(@id,@kind,@title,@summary,@why,@next,@points::jsonb,@level,@limits,@stars::jsonb,@sources::jsonb,@by,now(),now(),@p) ON CONFLICT DO NOTHING;
            INSERT INTO insight_runs(id,generated_by,outcome,note,insight_id)
            SELECT gen_random_uuid(),@by,'saved','Demo run',@id WHERE NOT EXISTS(SELECT 1 FROM insight_runs WHERE insight_id=@id)
            """, ("id", saved), ("kind", (string)insight["kind"]!), ("title", (string)insight["title"]!), ("summary", (string)insight["summary"]!),
            ("why", (string)insight["whyItMatters"]!), ("next", (string)insight["nextStep"]!), ("points", insight["points"]!.ToJsonString()),
            ("level", (string)insight["evidenceLevel"]!), ("limits", (string)insight["limitations"]!), ("stars", insightStars.ToJsonString()),
            ("sources", sources.ToJsonString()), ("by", (string)insight["generatedBy"]!), ("p", principal)))
            await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    // With the demo off: its dashboards that only the demo ever saved, arranged or wrote through, with the corners they
    // hold, and its insight if nobody gave feedback on it or reviewed it (migration 066; with memory.tools off no row has a
    // review or an event). Anything someone changed stays. Returns how many rows went.
    public static async Task<int> Retire(Database db, CancellationToken ct)
    {
        using var seed = Seed("showcase.json");
        var root = seed.RootElement;
        var dashboards = root.GetProperty("dashboards").EnumerateArray().Select(d => Guid.Parse(d.GetProperty("id").GetString()!)).ToArray();
        var insight = Guid.Parse(root.GetProperty("insight").GetProperty("id").GetString()!);
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await using var cmd = Database.Command(c, tx, """
            DELETE FROM dashboards d WHERE d.id=ANY(@dashboards)
              AND NOT EXISTS(SELECT 1 FROM dashboard_versions v WHERE v.dashboard_id=d.id
                AND v.saved_by NOT IN (SELECT id FROM principals WHERE name='demo' AND role='system'))
              AND NOT EXISTS(SELECT 1 FROM dashboard_layouts l WHERE l.dashboard_id=d.id
                AND l.saved_by NOT IN (SELECT id FROM principals WHERE name='demo' AND role='system'))
              AND NOT EXISTS(SELECT 1 FROM dashboard_writes w WHERE w.dashboard_id=d.id);
            DELETE FROM insight_runs WHERE insight_id=@insight AND EXISTS(SELECT 1 FROM insights WHERE id=@insight AND feedback IS NULL AND review='proposed')
              AND NOT EXISTS(SELECT 1 FROM insight_events e WHERE e.insight_id=@insight);
            DELETE FROM insights WHERE id=@insight AND feedback IS NULL AND review='proposed'
              AND NOT EXISTS(SELECT 1 FROM insight_events e WHERE e.insight_id=@insight);
            """, ("dashboards", dashboards), ("insight", insight));
        var gone = await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return gone;
    }

    // ---- MemorySource --------------------------------------------------------------------------------------------
    static ResourceKey Key(string id) => new(RhythmConfig.Synthetic, Provider, Provider, id);

    public Task<SourceListing> List(SourcePass pass, CancellationToken ct)
    {
        using var seed = Seed("notes.json");
        var now = DateTimeOffset.UtcNow;
        var projects = seed.RootElement.GetProperty("projects").EnumerateArray()
            .ToDictionary(p => p.GetProperty("id").GetString()!, p => p.GetProperty("name").GetString()!);
        var records = new List<DiscoveryRecord>();
        foreach (var note in seed.RootElement.GetProperty("notes").EnumerateArray().Take(pass.MemoriesOf(RhythmConfig.Synthetic)))
        {
            var id = note.GetProperty("id").GetString()!; var text = note.GetProperty("text").GetString()!;
            var title = "DEMO · " + note.GetProperty("title").GetString()!;
            var inProjects = note.GetProperty("projects").EnumerateArray().Select(p => p.GetString()!).Where(projects.ContainsKey).ToArray();
            var revision = "demo-sha256:" + Hashing.Hash(note.GetRawText());
            var modified = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(-note.GetProperty("daysAgo").GetInt32()).AddHours(8);
            var references = note.GetProperty("links").EnumerateArray().Select(l => new Reference(Key(l.GetString()!), null, "demo:link", "Links to it", "references"))
                .Concat(inProjects.Select(p => new Reference(Key("project:" + p), null, "demo:project-membership", "In project", "in_project", true, "verified", 1, true)))
                .ToArray();
            records.Add(new(Key(id), "private", "note", title, null, revision, modified, now, references,
                ContentRevision: revision, Embedding: pass.Vector(Key(id), revision, title, text),
                Groups: [new("demo:notes", "Demo notes"), .. inProjects.Select(p => new RecordGroup("demo:" + p, projects[p]))],
                Overview: text.Length <= 300 ? text : text[..299] + "…"));
        }
        foreach (var (id, name) in projects.Where(p => records.Any(r => r.Groups!.Any(g => g.Id == "demo:" + p.Key))))
            records.Add(new(Key("project:" + id), "private", "project", "DEMO · " + name, null,
                "demo-sha256:" + Hashing.Hash(id + name), null, now, [], Groups: [new("demo:" + id, name)]));
        return Task.FromResult(new SourceListing(records, Complete: true));
    }

    public Task<string?> ReadText(DiscoveryRecord record, CancellationToken ct)
    {
        using var seed = Seed("notes.json");
        var note = seed.RootElement.GetProperty("notes").EnumerateArray().FirstOrDefault(n => n.GetProperty("id").GetString() == record.Key.ExternalId);
        return Task.FromResult(note.ValueKind == JsonValueKind.Object ? note.GetProperty("text").GetString() : null);
    }

    static string Iso(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
