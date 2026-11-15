using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Relationships;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// Insight candidates for native memories (SB-P5-05; story 11, its candidate half; docs/dev/memory.md sections 5 and 9;
// migration 064): pairs of an older and a newer memory of one scope, written 30 or more days apart, that a reasoning
// client should look at together, found by a bounded nightly pass and kept until the client uses or rejects them.
// Adapted from Second Brain's nightly candidate accrual and its candidate score (src/insight/candidates.ts and
// src/insight/score.ts at the pinned commit, MIT; NOTICE).
//
// What makes a pair worth a look (Signals), measured on the labelled pairs (Parity/pairs.json and insight-pairs.json,
// ThresholdTuningTests; docs/dev/second-brain-parity/benchmarks.md, "Insight candidates"):
//   similar  the two memories' words are this alike under the live model: at or above the insight threshold the install
//            reads (EmbeddingThresholds.For: today's 0.80, or the live model's own with adapters.embeddings.thresholds
//            tuned), the words alone, as a save compares them (D-211).
//   weighty  both carry importance 3 or more, the caller's own word for how much a memory matters.
// Insight pairs are not alike under either model (D-211), so similarity alone finds next to none; topic signals (a
// shared tag, project or rare word) find related pairs rather than insights, so they only order the list (Score's
// cross-topic factor, as upstream's) and decide nothing.
//
// The pass (Job, on the nightly job runner, SB-P5-14) walks each scope's eligible memories (064's
// memory_insight_eligible) oldest first from that scope's cursor (job_cursors, scope = the subject), a piece at a time,
// at most PerScope a scope a run, reading only those (Seeds). Each memory it reaches is compared with the eligible
// memories of its own scope written 30 or more days before it (the latest Partners of them, read for each piece:
// EarlierOf), and keeps its best PerSeed pairs that a signal names, by
// upstream's score. Each piece's pairs and the place it reached are written in one transaction through 064's
// memory_insight_accrue, which checks each pair again and never writes a pair it holds already, whatever became of it:
// a repeated run adds nothing, and a rejected pair never comes back. A scope walked to its end starts again from its
// first memory at its next run, so a memory released from review, restored from the trash or imported with an earlier
// date is reached in time. A scope with no eligible memory after its place (every one held, trashed, marked wrong, ended
// or the engine's own, or those after its place gone since) is at its end too: its place is cleared and the pass goes on
// to the next scope. The vectors are made as it goes with the live model and kept for one run only.
//
// The pass needs the live embeddings model: without it (off, or still loading) it waits, touches nothing and keeps its
// place, and says it waited (NightlyRun.Waited), so the scheduler runs it once more as soon as the model is on: the
// engine's start, whose daily turn most often finds the model still loading, never puts it off a whole day, and an
// engine restarted every day still runs it. Only with memory.tools on is it a job at all (NightlyJobs.Daily); off, as
// by default, the daily turn, its log lines and the night summary are as they were. A dry run (Preview) walks the same
// way from each scope's place and writes nothing (no candidate, no place, no history), but it is a sample: fewer
// memories a scope, each compared with fewer earlier ones (PreviewPartners, where the pass compares Partners), so where
// a memory has more earlier memories than that the pass may keep better pairs for it than the dry run names.

// One eligible memory as the pass reads it: its words cut to what a vector reads (Text, at most 8000 characters) and
// their whole length.
public sealed record InsightMemory(Guid Id, int Revision, DateTimeOffset CreatedAt, int? Importance, string? Volatility, IReadOnlyList<string> Tags,
    Guid? Project, string Text, int Length);

// A pair the pass found: the older and the newer memory, their similarity as the model scored it, what said it is worth
// a look, upstream's score and the whole days between their writing.
public sealed record InsightPair(InsightMemory Older, InsightMemory Newer, double Similarity, IReadOnlyList<string> Signals, double Score, int GapDays);

// Where a scope's walk stands: the last memory it reached, by when it was written and its id.
public sealed record InsightPlace(DateTimeOffset At, Guid Id);

public static class InsightCandidates
{
    public const string JobId = "insight-candidates";
    public const string Similar = "similar", Weighty = "weighty";
    // A piece of memories reached, written with its place in one transaction; at most PerScope a scope a run; each
    // compared with at most Partners earlier memories; at most PerSeed candidates each. Importance from WeightyImportance
    // up is weighty.
    public const int Piece = 25, PerScope = 200, Partners = 2000, PerSeed = 3, WeightyImportance = 3;
    // A dry run answers a call, so it reaches fewer memories a scope and compares each with fewer.
    public const int PreviewSeeds = 10, PreviewPartners = 300;
    // How far apart two memories' writing must be (upstream's 30 days, D-028).
    public static readonly TimeSpan Gap = TimeSpan.FromDays(30);
    // What a vector reads of a memory's words (SimilaritySuggestions.Vector's own cut).
    const int Read = 8000;

    // What says a pair is worth a look: similar at or above the threshold, weighty when both are important.
    public static IReadOnlyList<string> Signals(double similarity, double threshold, int? olderImportance, int? newerImportance)
    {
        var signals = new List<string>(2);
        if (similarity >= threshold) signals.Add(Similar);
        if (olderImportance >= WeightyImportance && newerImportance >= WeightyImportance) signals.Add(Weighty);
        return signals;
    }

    // A memory's vector as the thresholds read one on a save: its words alone, no title (D-211).
    public static float[]? Vector(IEmbedder model, string text) => SimilaritySuggestions.Vector(model.Embed, model.Chunks, "", text);

    // Two vectors' similarity, rounded to 4 places as the thresholds were tuned on (ThresholdTuning.Score); 0 without one.
    public static double Similarity(float[]? a, float[]? b)
    {
        if (a is null || b is null || a.Length != b.Length) return 0;
        double sum = 0;
        for (var i = 0; i < a.Length; i++) sum += (double)a[i] * b[i];
        return Math.Round(sum, 4);
    }

    // Upstream's candidate score: the similarity (here 0 to 1), times the log of the days between them, times 1 + 0.1 of
    // the greater importance, times 1.25 when they share no tag or project (an unlikely connection, which a same-topic
    // pair would outscore on similarity alone), times 0.4 when both are state facts (two snapshots of one quantity).
    public static double Score(InsightMemory older, InsightMemory newer, double similarity)
    {
        var days = Math.Max(0, (newer.CreatedAt - older.CreatedAt).TotalDays);
        var importance = 1 + 0.1 * Math.Max(older.Importance ?? 0, newer.Importance ?? 0);
        var shared = older.Tags.Intersect(newer.Tags, StringComparer.Ordinal).Any() || older.Project is { } p && p == newer.Project;
        var state = older.Volatility == "state" && newer.Volatility == "state";
        return Math.Round(Math.Clamp(similarity, 0, 1) * Math.Log(1 + days) * importance * (shared ? 1.0 : 1.25) * (state ? 0.4 : 1.0), 6);
    }

    // The candidates of one memory: its pairs with `earlier` (eligible memories of its scope, oldest first) written Gap or
    // more before it, the latest `partners` of them, that a signal names; the best `perSeed` by score, then by the older id.
    public static IReadOnlyList<InsightPair> Of(InsightMemory seed, IReadOnlyList<InsightMemory> earlier, Func<InsightMemory, float[]?> vector,
        double threshold, int partners = Partners, int perSeed = PerSeed)
    {
        var mine = vector(seed);
        var found = new List<InsightPair>();
        foreach (var older in earlier.Where(m => m.Id != seed.Id && seed.CreatedAt - m.CreatedAt >= Gap).TakeLast(partners))
        {
            var similarity = Similarity(mine, vector(older));
            var signals = Signals(similarity, threshold, older.Importance, seed.Importance);
            if (signals.Count == 0) continue;
            found.Add(new(older, seed, similarity, signals, Score(older, seed, similarity), (int)Math.Floor((seed.CreatedAt - older.CreatedAt).TotalDays)));
        }
        return [.. found.OrderByDescending(p => p.Score).ThenBy(p => p.Older.Id).Take(perSeed)];
    }

    // The job, added to the daily turn's list only with memory.tools on (NightlyJobs.Daily). It reads the threshold the
    // install's settings give the live model, and the model the engine has loaded when it runs.
    public static NightlyJob Job(NpgsqlDataSource source, ILogger log, Embeddings? embeddings, RhythmConfig? config) =>
        new(JobId, "insight candidate accrual", NightlyJobs.DailyBudget, (context, ct) =>
            Pass(source, context, log, embeddings?.Model, Threshold(config), ct));

    // The insight threshold the install reads (D-211): today's unless adapters.embeddings.thresholds is tuned.
    public static double Threshold(RhythmConfig? config) =>
        config is null ? EmbeddingThresholds.Today.Insight : EmbeddingThresholds.For(EmbeddingSettings.From(config)).Insight;

    // One run of the pass. afterPiece: called with the scope once each of its pieces has committed (a test stops a run there).
    public static async Task<NightlyRun> Pass(NpgsqlDataSource source, NightlyContext context, ILogger log, IEmbedder? model, double threshold,
        CancellationToken ct, int piece = Piece, int perScope = PerScope, int partners = Partners, Action<string>? afterPiece = null)
    {
        if (piece is < 1 or > 1000 || perScope < piece || partners < 1) throw new ArgumentException("a piece of 1 to 1000 memories, a run of at least one piece a scope, and at least one earlier memory");
        var scopes = await Scopes(source, null, ct);
        if (scopes.Count == 0) return NightlyRun.Of(("memories reached", 0), ("candidates added", 0));
        if (model is null)
        {
            // Nothing is read or written, and each scope keeps its place, until the model has loaded.
            log.LogInformation("Insight candidates: waiting for the embeddings model");
            return NightlyRun.Of(("runs waiting for the embeddings model", 1)) with { Waited = true };
        }
        var vectors = new Dictionary<Guid, float[]?>();
        float[]? VectorOf(InsightMemory m)
        {
            if (vectors.TryGetValue(m.Id, out var v)) return v;
            ct.ThrowIfCancellationRequested();
            return vectors[m.Id] = Vector(model, m.Text);
        }
        long reached = 0, added = 0, known = 0, refused = 0;
        var more = false;
        foreach (var subject in scopes)
        {
            var place = Place(await context.Cursor(subject, ct));
            // The memories after its place, at most a run's worth and one more, which says whether more wait.
            var seeds = await Seeds(source, subject, place, perScope + 1, ct);
            if (seeds.Count == 0)
            {
                // None eligible after its place (a scope whose memories are all held, trashed, marked wrong, ended or the
                // engine's own, or a place whose later memories left since): the walk has reached its end, so a saved
                // place is cleared and the next run starts again from the top; nothing else is read or written.
                if (place is not null) await context.Save(null, subject, ct);
                continue;
            }
            var taken = 0;
            while (true)
            {
                if (taken >= perScope) { more |= taken < seeds.Count; break; }
                ct.ThrowIfCancellationRequested();
                var batch = seeds.Skip(taken).Take(Math.Min(piece, perScope - taken)).ToArray();
                var earlier = await EarlierOf(source, subject, batch, partners, ct);
                var pairs = batch.SelectMany(seed => Of(seed, earlier, VectorOf, threshold, partners)).ToArray();
                var ended = taken + batch.Length >= seeds.Count;
                // The place the next run starts from: none once the walk has reached the end, so it starts again.
                var next = ended ? null : new InsightPlace(batch[^1].CreatedAt, batch[^1].Id);
                // A piece with no pair and the same place writes nothing (a scope walked whole from its start, again).
                if (pairs.Length > 0 || next != place)
                {
                    await using var c = await source.OpenConnectionAsync(ct);
                    await using var tx = await c.BeginTransactionAsync(ct);
                    if (pairs.Length > 0)
                    {
                        await using var cmd = new NpgsqlCommand("SELECT memory_insight_accrue(@subject,@model,@pairs)::text", c, tx);
                        cmd.Parameters.Add(new("subject", NpgsqlDbType.Text) { Value = subject });
                        cmd.Parameters.Add(new("model", NpgsqlDbType.Text) { Value = model.Id });
                        cmd.Parameters.Add(new("pairs", NpgsqlDbType.Jsonb) { Value = Json(pairs) });
                        using var answer = JsonDocument.Parse((string)(await cmd.ExecuteScalarAsync(ct))!);
                        added += answer.RootElement.GetProperty("added").GetInt32();
                        known += answer.RootElement.GetProperty("known").GetInt32();
                        refused += answer.RootElement.GetProperty("refused").GetInt32();
                    }
                    // The piece and its place commit together, with no token the budget cancels once the piece is done; a
                    // place that has not changed is not written again.
                    if (next != place) await context.Save(c, tx, Cursor(next), subject, CancellationToken.None);
                    await tx.CommitAsync(CancellationToken.None);
                }
                place = next;
                taken += batch.Length;
                reached += batch.Length;
                afterPiece?.Invoke(subject);
                if (ended) break;
            }
        }
        if (added + refused > 0)
            log.LogInformation("Insight candidates: {Reached} memories reached, {Added} candidates added, {Refused} no longer eligible", reached, added, refused);
        return NightlyRun.Of(("memories reached", reached), ("candidates added", added), ("pairs kept already", known), ("pairs no longer eligible", refused))
            with { More = more };
    }

    // A dry run, a sample of the pairs the next run would add in these scopes (each a subject): from each scope's place, at
    // most `seeds` memories a scope, each compared with at most the latest `partners` earlier memories (the pass compares
    // Partners, so a memory with more earlier ones than `partners` may get better pairs from the pass); nothing is
    // written. Best first.
    public static async Task<IReadOnlyList<(string Subject, InsightPair Pair)>> Preview(NpgsqlDataSource source, IEmbedder model, double threshold,
        IReadOnlyCollection<string> subjects, CancellationToken ct, int seeds = PreviewSeeds, int partners = PreviewPartners)
    {
        var context = new NightlyContext(source, JobId, DateOnly.FromDateTime(DateTime.UtcNow));
        var vectors = new Dictionary<Guid, float[]?>();
        float[]? VectorOf(InsightMemory m)
        {
            if (vectors.TryGetValue(m.Id, out var v)) return v;
            ct.ThrowIfCancellationRequested();
            return vectors[m.Id] = Vector(model, m.Text);
        }
        var found = new List<(string, InsightPair)>();
        foreach (var subject in await Scopes(source, subjects, ct))
        {
            var reached = await Seeds(source, subject, Place(await context.Cursor(subject, ct)), seeds, ct);
            if (reached.Count == 0) continue;
            var earlier = await EarlierOf(source, subject, reached, partners, ct);
            var kept = await Kept(source, reached, ct);
            foreach (var seed in reached)
                found.AddRange(Of(seed, earlier, VectorOf, threshold, partners)
                    .Where(p => !kept.Contains((p.Older.Id, p.Newer.Id)) && !kept.Contains((p.Newer.Id, p.Older.Id))).Select(p => (subject, p)));
        }
        return [.. found.OrderByDescending(f => f.Item2.Score).ThenBy(f => f.Item2.Newer.Id).ThenBy(f => f.Item2.Older.Id)];
    }

    // The scopes holding a native memory, in order; only those named when `only` is given.
    static async Task<IReadOnlyList<string>> Scopes(NpgsqlDataSource source, IReadOnlyCollection<string>? only, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("SELECT DISTINCT subject_id FROM memories WHERE @only::text[] IS NULL OR subject_id=ANY(@only) ORDER BY subject_id");
        cmd.Parameters.Add(new NpgsqlParameter("only", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = only is null ? DBNull.Value : only.ToArray() });
        var scopes = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) scopes.Add(r.GetString(0));
        return scopes;
    }

    static readonly string Columns = $"m.id,m.revision,m.created_at,m.importance,m.volatility,m.tags,m.project_id,left(m.content,{Read}),length(m.content)";

    static async Task<IReadOnlyList<InsightMemory>> Rows(NpgsqlCommand cmd, CancellationToken ct)
    {
        var memories = new List<InsightMemory>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            memories.Add(new(r.GetGuid(0), r.GetInt32(1), r.GetFieldValue<DateTimeOffset>(2), r.IsDBNull(3) ? null : r.GetInt16(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetFieldValue<string[]>(5), r.IsDBNull(6) ? null : r.GetGuid(6), r.GetString(7), r.GetInt32(8)));
        return memories;
    }

    // A scope's next `n` eligible memories after a place (all from its first without one), oldest first: when written,
    // then id, as the database orders ids. Only these are read.
    static async Task<IReadOnlyList<InsightMemory>> Seeds(NpgsqlDataSource source, string subject, InsightPlace? place, int n, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand($"""
            SELECT {Columns} FROM memories m
            WHERE m.subject_id=@s AND (@at::timestamptz IS NULL OR (m.created_at,m.id)>(@at,@id)) AND memory_insight_eligible(m.id)
            ORDER BY m.created_at,m.id LIMIT @n
            """);
        cmd.Parameters.AddWithValue("s", subject);
        cmd.Parameters.Add(new("at", NpgsqlDbType.TimestampTz) { Value = place is null ? DBNull.Value : place.At });
        cmd.Parameters.Add(new("id", NpgsqlDbType.Uuid) { Value = place is null ? DBNull.Value : place.Id });
        cmd.Parameters.AddWithValue("n", n);
        return await Rows(cmd, ct);
    }

    // The earlier memories a piece of seeds (oldest first) is compared with, oldest first: for each seed the latest
    // `partners` eligible memories of its scope written Gap or more before it are among them. Read once for the piece:
    // the latest `partners` before the first seed's cut, and every one between its cut and the last seed's.
    static async Task<IReadOnlyList<InsightMemory>> EarlierOf(NpgsqlDataSource source, string subject, IReadOnlyList<InsightMemory> seeds, int partners,
        CancellationToken ct)
    {
        if (seeds.Count == 0) return [];
        await using var cmd = source.CreateCommand($"""
            SELECT {Columns} FROM memories m
            WHERE m.subject_id=@s AND m.created_at<=@last AND memory_insight_eligible(m.id)
            ORDER BY m.created_at DESC,m.id DESC
            LIMIT @partners+(SELECT count(*) FROM memories x
              WHERE x.subject_id=@s AND x.created_at>@first AND x.created_at<=@last AND memory_insight_eligible(x.id))
            """);
        cmd.Parameters.AddWithValue("s", subject);
        cmd.Parameters.Add(new("first", NpgsqlDbType.TimestampTz) { Value = seeds[0].CreatedAt - Gap });
        cmd.Parameters.Add(new("last", NpgsqlDbType.TimestampTz) { Value = seeds[^1].CreatedAt - Gap });
        cmd.Parameters.AddWithValue("partners", partners);
        return [.. (await Rows(cmd, ct)).Reverse()];
    }

    // The pairs held already at these memories that the pass would not write again: every one, whatever became of it,
    // but a waiting one no list serves because its two memories have both left its scope (memory_insight_accrue finds that
    // one again where they are now).
    static async Task<HashSet<(Guid, Guid)>> Kept(NpgsqlDataSource source, IReadOnlyList<InsightMemory> seeds, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("""
            SELECT c.older_id,c.newer_id FROM memory_insight_candidates c
            WHERE (c.older_id=ANY(@ids) OR c.newer_id=ANY(@ids)) AND NOT (c.status='pending' AND NOT memory_insight_live(c.id))
            """);
        cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = seeds.Select(m => m.Id).ToArray() });
        var kept = new HashSet<(Guid, Guid)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) kept.Add((r.GetGuid(0), r.GetGuid(1)));
        return kept;
    }

    // A saved place, read as it was written; one in any other shape starts the walk again from the top, which repeats
    // nothing it wrote (upstream's way: trusting a bad place could skip memories).
    static InsightPlace? Place(JsonElement? saved)
    {
        if (saved is not { ValueKind: JsonValueKind.Object } o) return null;
        return o.TryGetProperty("at", out var at) && at.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
            && o.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var g) ? new(when, g) : null;
    }

    static JsonObject? Cursor(InsightPlace? place) => place is null ? null
        : new() { ["at"] = place.At.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), ["id"] = place.Id.ToString() };

    static string Json(IEnumerable<InsightPair> pairs) => new JsonArray([.. pairs.Select(p => (JsonNode)new JsonObject
    {
        ["older"] = p.Older.Id.ToString(), ["newer"] = p.Newer.Id.ToString(), ["similarity"] = p.Similarity, ["score"] = p.Score,
        ["signals"] = new JsonArray([.. p.Signals.Select(s => (JsonNode?)s)]),
    })]).ToJsonString();
}
