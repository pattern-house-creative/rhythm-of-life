using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// The embeddings index job (SB-P2-04, SB-P2-05): bounded runs that give the rows PassageSources lists their passages under
// one model, through SemanticSearch.Index and IndexRecord. It serves three settings, all off for an existing install
// (docs/configuration.md):
// - `adapters.embeddings.backfill`: the live model, for documents saved while it was off or before it loaded;
// - `adapters.embeddings.sources`: the live model, for the texts listed there (uploads and dropped files, notes, readable
//   Drive files, native memories); the example settings list all but Drive's for a new install;
// - `adapters.embeddings.shadow`: a second model's index built beside the live one (a shadow index), which no search
//   reads, so a model can be measured and switched to with its passages already in place (D-025). It covers the copies
//   and the same sources, so the two models are measured on the same passages.
// Each run takes the rows after its model's cursor, in id order, that hold no passages of that model at their current
// revision; at most `rows` of them, and it stops once it has embedded `passages` passages (the first row always runs,
// or one long document would stall it). The cursor is written after every row, so a run stopped at any point resumes
// at the next row, and a row cut off half way keeps nothing of that run (Index commits a row's passages at once).
// Runs are idempotent: a row that has its passages is never embedded again. A row that fails stays pending and the
// cursor moves past it, so it never blocks the rest; the next sweep tries it again, and a sweep in which nothing
// succeeded pauses the model's runs for `Pause`. Each model has its own cursor, so changing the shadow model starts that
// model's own sweep and never finishes another's with it: no row holds passages one model embedded under another's id.
// Adapted from Second Brain's re-embedding run and pending-vector backfill (src/migration/embedding.ts,
// src/vectorize/pending.ts): a keyset cursor, a per-run budget that always takes the first entry, a cursor per target
// model, and a stop instead of a retry loop when nothing succeeds. Summaries are counts only (D-017).
public sealed class EmbeddingBackfill(Database db, TimeProvider? clock = null)
{
    public const int Rows = 25, Passages = 200;
    public static readonly TimeSpan Pause = TimeSpan.FromHours(1);
    readonly TimeProvider time = clock ?? TimeProvider.System;

    // What one run did, for the log and the tests. `Sweep` is the sweep the run worked in; `Finished` says a sweep ended
    // (or there was nothing to do); `Left` counts the rows still without this model's passages, failed ones included.
    // `Skipped` counts rows that turned out not to be the index's, or whose text gives no passage (recorded, so they are
    // not read again until they change).
    public sealed record Run(string Model, int Sweep, int Indexed, int Failed, int Skipped, int Passages, long Left, bool Finished, bool Paused)
    {
        public bool Worth => Indexed > 0 || Failed > 0 || Skipped > 0 || Finished && Left > 0;
    }

    sealed record State(Guid? After, int Sweep, int Indexed, int Failed, DateTimeOffset? PausedUntil);

    // The models the scheduler gives one run each minute, each with what it covers: the live one when `backfill` is on (the
    // copies) or `sources` lists anything (those texts), the shadow one when the settings name it (the copies and the
    // sources); each only once it has loaded. None with the settings an existing install has. `adapters` are the ones on,
    // whose records `notes` and `drive` name.
    public static IEnumerable<(string Role, T Model, PassageSources Sources)> Targets<T>(EmbeddingSettings settings, T? live, T? shadow,
        IEnumerable<Adapters.IAdapter>? adapters = null) where T : class, IEmbedder
    {
        if (!settings.Enabled) yield break;
        var listed = PassageSources.For(settings, adapters ?? [], settings.Backfill);
        if ((settings.Backfill || listed.Sources) && live is not null && live.Id == settings.Model.Id) yield return ("live", live, listed);
        if (settings.Shadow is { } named && shadow is not null && shadow.Id == named.Id) yield return ("shadow", shadow, listed with { Copies = true });
    }

    // A row the index wants and this model has not covered: the rows SemanticSearch.Index and IndexRecord accept, less
    // those whose text gave no passage at their current revision (no_passages, migration 026).
    static string Pending => $"""
        r.status='current'
          AND ((@copies AND {PassageSources.Copy}) OR (@documents AND {PassageSources.Document}) OR (@memories AND {PassageSources.Memory})
            OR ({PassageSources.Record} AND r.provider=ANY(@providers) AND {PassageSources.Fresh}))
          AND NOT EXISTS(SELECT 1 FROM resource_chunks k WHERE k.resource_id=r.id AND k.model=@model AND k.source_revision={PassageSources.Revision})
          AND NOT EXISTS(SELECT 1 FROM no_passages n WHERE n.resource_id=r.id AND n.source_revision IS NOT DISTINCT FROM {PassageSources.Revision})
        """;

    // `sources`: what this run covers; by default the copies only, as before SB-P2-05.
    public async Task<Run> Step(IEmbedder model, CancellationToken ct, int rows = Rows, int passages = Passages, PassageSources? sources = null)
    {
        if (rows < 1 || passages < 1) throw new ArgumentOutOfRangeException(nameof(rows), "A run takes at least one row and one passage");
        sources ??= PassageSources.CopiesOnly;
        var state = await Read(model.Id, ct);
        if (state.PausedUntil is { } until && until > time.GetUtcNow())
            return new(model.Id, state.Sweep, 0, 0, 0, 0, await Left(model.Id, sources, ct), false, true);
        var next = await Next(model.Id, sources, state.After, rows, ct);
        if (next.Count == 0)
        {
            // Nothing after the cursor: the sweep is over. The next one starts from the first row, after a pause when
            // this one only failed, so a row that cannot be indexed is retried once an hour rather than every minute.
            var left = await Left(model.Id, sources, ct);
            await Stale(model.Id, ct);
            if (state.After is not null || state.Indexed + state.Failed > 0)
                await Write(model.Id, new(null, state.Sweep + 1, 0, 0,
                    state.Indexed == 0 && state.Failed > 0 ? time.GetUtcNow() + Pause : null), ct);
            return new(model.Id, state.Sweep, 0, 0, 0, 0, left, true, false);
        }
        var search = new SemanticSearch(db, model);
        int indexed = 0, failed = 0, skipped = 0, embedded = 0;
        foreach (var (id, provider, record) in next)
        {
            if (embedded >= passages) break;
            try
            {
                var outcome = !record ? await search.Index(id, ct, sources)
                    : sources.Records.TryGetValue(provider, out var source) ? await search.IndexRecord(id, source, ct) : null;
                switch (outcome)
                {
                    case null or 0: skipped++; break;
                    case var n: indexed++; embedded += n.Value; break;
                }
            }
            // A row the model cannot embed (too long, a token limit, a vector of the wrong size, a passage another writer
            // stored meanwhile), or whose source could not be read now (down, timed out, not signed in), is that row's
            // failure. A lost database, a broken schema or a stop ends the run, with the cursor where the last finished row
            // left it.
            catch (Exception e) when (e is ArgumentException or InvalidDataException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException
                || e is HttpRequestException or IOException or System.Text.Json.JsonException or UnauthorizedAccessException
                || e is OperationCanceledException && !ct.IsCancellationRequested
                || e is PostgresException p && (p.SqlState.StartsWith("22", StringComparison.Ordinal) || p.SqlState.StartsWith("23", StringComparison.Ordinal))) { failed++; }
            await Write(model.Id, new(id, state.Sweep, state.Indexed + indexed, state.Failed + failed, null), ct);
        }
        return new(model.Id, state.Sweep, indexed, failed, skipped, embedded, await Left(model.Id, sources, ct), false, false);
    }

    // The model's cursor and counts, as the last run left them.
    public async Task<(Guid? After, int Sweep, int Indexed, int Failed, DateTimeOffset? PausedUntil)> Cursor(string model, CancellationToken ct)
    {
        var s = await Read(model, ct);
        return (s.After, s.Sweep, s.Indexed, s.Failed, s.PausedUntil);
    }

    async Task<State> Read(string model, CancellationToken ct)
    {
        await using var cmd = db.Source.CreateCommand("SELECT after_id,sweep,indexed,failed,paused_until FROM embedding_backfill WHERE model=@model");
        cmd.Parameters.AddWithValue("model", model);
        await using var row = await cmd.ExecuteReaderAsync(ct);
        return await row.ReadAsync(ct)
            ? new(row.IsDBNull(0) ? null : row.GetGuid(0), row.GetInt32(1), row.GetInt32(2), row.GetInt32(3), row.IsDBNull(4) ? null : row.GetFieldValue<DateTimeOffset>(4))
            : new(null, 1, 0, 0, null);
    }

    async Task Write(string model, State s, CancellationToken ct)
    {
        await using var cmd = db.Source.CreateCommand("""
            INSERT INTO embedding_backfill(model,after_id,sweep,indexed,failed,paused_until,updated_at)
            VALUES(@model,@after,@sweep,@indexed,@failed,@paused,now())
            ON CONFLICT(model) DO UPDATE SET after_id=excluded.after_id,sweep=excluded.sweep,indexed=excluded.indexed,
              failed=excluded.failed,paused_until=excluded.paused_until,updated_at=now()
            """);
        cmd.Parameters.AddWithValue("model", model);
        cmd.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.Uuid) { Value = s.After is { } a ? a : DBNull.Value });
        cmd.Parameters.AddWithValue("sweep", s.Sweep);
        cmd.Parameters.AddWithValue("indexed", s.Indexed);
        cmd.Parameters.AddWithValue("failed", s.Failed);
        cmd.Parameters.Add(new NpgsqlParameter("paused", NpgsqlDbType.TimestampTz) { Value = s.PausedUntil is { } p ? p.UtcDateTime : DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // The next rows after the cursor, each with its provider and whether it is an adapter record's star row.
    async Task<List<(Guid Id, string Provider, bool Record)>> Next(string model, PassageSources sources, Guid? after, int rows, CancellationToken ct)
    {
        await using var cmd = db.Source.CreateCommand($"SELECT r.id,r.provider,r.relationship_snapshot IS NOT NULL FROM resources r WHERE {Pending} AND (@after IS NULL OR r.id>@after) ORDER BY r.id LIMIT @rows");
        Covers(cmd, model, sources);
        cmd.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.Uuid) { Value = after is { } a ? a : DBNull.Value });
        cmd.Parameters.AddWithValue("rows", rows);
        var ids = new List<(Guid, string, bool)>();
        await using var row = await cmd.ExecuteReaderAsync(ct);
        while (await row.ReadAsync(ct)) ids.Add((row.GetGuid(0), row.GetString(1), row.GetBoolean(2)));
        return ids;
    }

    // The rows still without this model's passages among those `sources` covers, failed ones included: what a run's
    // `Left` counts, for the model status (ModelStatus).
    public Task<long> Unindexed(string model, PassageSources sources, CancellationToken ct) => Left(model, sources, ct);

    async Task<long> Left(string model, PassageSources sources, CancellationToken ct)
    {
        await using var cmd = db.Source.CreateCommand($"SELECT count(*) FROM resources r WHERE {Pending}");
        Covers(cmd, model, sources);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    static void Covers(NpgsqlCommand cmd, string model, PassageSources sources)
    {
        cmd.Parameters.AddWithValue("model", model);
        cmd.Parameters.AddWithValue("copies", sources.Copies);
        cmd.Parameters.AddWithValue("documents", sources.Documents);
        cmd.Parameters.AddWithValue("memories", sources.Memories);
        cmd.Parameters.AddWithValue("providers", sources.Providers);
        cmd.Parameters.AddWithValue("fresh", (double)SemanticSearch.Freshness);
    }

    // At the end of a sweep: removes this model's passages of adapter records whose star no map pass has seen within
    // graph.freshnessSeconds (a file the source stopped listing, an adapter left out for its budget), which search no
    // longer serves either, so a source's text is not kept past the map's window. They come again once the star is fresh.
    async Task Stale(string model, CancellationToken ct)
    {
        await using var cmd = db.Source.CreateCommand($"""
            DELETE FROM resource_chunks k USING resources r WHERE k.resource_id=r.id AND k.model=@model AND {PassageSources.Record}
              AND NOT {PassageSources.Fresh}
            """);
        cmd.Parameters.AddWithValue("model", model);
        cmd.Parameters.AddWithValue("fresh", (double)SemanticSearch.Freshness);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // At start: removes every model's passages of the texts the settings no longer cover (a source taken out of
    // adapters.embeddings.sources, or whose adapter is off), so a text an install stops indexing is not kept; search never
    // serves it meanwhile (SemanticSearch serves only what the settings cover). Copies keep theirs, as before. In batches
    // of `batch` passages, each its own short statement, so a large index never runs into a statement's time limit; the
    // scheduler tries again each minute until it has finished, before the index's first run. Returns how many passages
    // went: a count only.
    public static async Task<int> Forget(Database db, PassageSources covered, CancellationToken ct, int batch = 5000)
    {
        var gone = 0;
        for (;;)
        {
            await using var cmd = db.Source.CreateCommand($"""
                DELETE FROM resource_chunks WHERE ctid=ANY(ARRAY(SELECT k.ctid FROM resource_chunks k JOIN resources r ON r.id=k.resource_id WHERE
                  (NOT @documents AND {PassageSources.DocumentRow}) OR (NOT @memories AND {PassageSources.MemoryRow})
                  OR ({PassageSources.Record} AND NOT (r.provider=ANY(@providers))) LIMIT @batch))
                """);
            cmd.Parameters.AddWithValue("documents", covered.Documents);
            cmd.Parameters.AddWithValue("memories", covered.Memories);
            cmd.Parameters.AddWithValue("providers", covered.Providers);
            cmd.Parameters.AddWithValue("batch", batch);
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            gone += deleted;
            if (deleted < batch) return gone;
        }
    }
}
