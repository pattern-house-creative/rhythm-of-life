using System.Collections.Concurrent;
using System.Text.Json;
using Knowledge.Relationships;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// Duplicate copies on save (SB-P5-02; story 9's duplicate case; docs/dev/memory.md section 5; migration 063): when a new
// memory's words are close to a memory already in its own scope, it is saved as any other and flagged for a person's
// review naming that memory, at most Named of them, closest first. Nothing is refused, merged or dropped (D-211: no
// threshold tells a duplicate from a contradiction that changes one number or day), and neither memory changes.
//
// The score is the one the thresholds were tuned on (SB-P2-13, ThresholdTuning): the dot product of the two texts'
// vectors made by the map's recipe from the words alone (SimilaritySuggestions.Vector with no title: the mean of the
// first four passages, unit length, rounded), under the live model. The threshold is DuplicateFlag of what
// EmbeddingThresholds.Override reads: with adapters.embeddings.thresholds off, as on every install, today's 0.85
// (Second Brain's starting point, D-028, no looser than before: nothing flagged at all before); with `tuned`, the live
// model's own (MiniLM 0.63). With no model, only the same words count (case and spacing aside: every run of white space,
// newlines and tabs included, is one space, and none at either end), scored 1.
//
// What is compared: the memories of the new one's own scope that are live (in the catalog, not in the trash, not held,
// not marked wrong, their window open: 063's memory_duplicate_live), the Window newest, and any older one with the same
// words. A held memory is neither compared nor flagged (its words wait for a person). Each memory's vector is made once
// per text revision and model and kept in this process (never stored, never served), so the first check after a start
// pays for the window: at most Budget of embedding a save, after which only the vectors made already are scored (the
// same words always count) and the log says how many were left unscored. A check that fails flags nothing and never
// undoes the save.
//
// Who calls it: since migration 068 the store itself after every write that saved a memory's words, through the memory tools
// and the app's Memories view (MemoryStore.Check: a new memory, an append, a change or an undo of its words, a release of its
// hold), and rhythm release and the two imports, each only with memory.tools on, off by default. The terminal's rhythm
// remember reads no settings and checks nothing, as before.
//
// Which wins between a flag and a link on save (D-215 left it open; 068): the check runs before the links, and a pair flagged at
// 0.95 or more, a copy for certain, takes no automatic link; below that the flag is only a possible copy, and both stand. Once a
// person keeps both, the links are drawn again (MemoryStore.Redrawn).

// A memory a flag names as close to another, with how close: 1 for the same words.
public sealed record MemoryCopy(Guid Id, double Score);

// One memory in the duplicate review, as a reader who may act on it is served it: a preview of its words of at most the
// asked characters (Truncated when longer), never a held one's (none is listed), and the copies its flags name that
// stand now, closest first.
public sealed record MemoryDuplicate(Guid Id, int Revision, string Subject, string Scope, string? Author, string Status, DateTimeOffset CreatedAt,
    string Content, bool Truncated, IReadOnlyList<MemoryCopy> Copies);

// The review's length for this reader and one page of it, newest first, read in one statement: Total is the length of
// the whole list the pages walk.
public sealed record MemoryDuplicatePage(int Total, IReadOnlyList<MemoryDuplicate> Items, MemoryCursor? Next);

// What a check found: the copies a flag names, at most Named, and the candidates a write offers as possible contradictions
// (SB-P5-03, migration 068), at most Offered, each closest first. The same words are a copy and no candidate: they can't
// contradict each other.
public sealed record MemoryNear(IReadOnlyList<MemoryCopy> Copies, IReadOnlyList<MemoryCopy> Candidates);

public sealed class DuplicateCheck(IEmbedder? model, double threshold, int window = DuplicateCheck.Window, TimeSpan? budget = null,
    ILogger? log = null, double? offer = null)
{
    // The newest live memories of a scope compared with each save, the most copies a flag names, and the most candidates a
    // write offers.
    public const int Window = 500, Named = 3, Offered = 5;
    // The longest a save waits for vectors this process has not made yet (as the model reads elsewhere: D-085's 5 s for a
    // search, less here since the save has committed and its answer waits).
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(2);
    // The model a same-words copy is recorded with when no model scored it.
    public const string Exact = "exact";

    public IEmbedder? Model { get; } = model;
    public double Threshold { get; } = threshold;
    // From what score a close memory is offered as a possible contradiction: never above the flag's, since no score tells a
    // copy from a contradiction (D-211), so the calling AI may name any flagged copy as one.
    public double Offer { get; } = Math.Min(offer ?? threshold, threshold);
    public TimeSpan Budget { get; } = budget ?? DefaultBudget;

    // The duplicate threshold the settings read (EmbeddingThresholds.Override decides, once): today's, unless
    // adapters.embeddings.thresholds is tuned (or the live model is not MiniLM, which the settings refuse, D-025).
    public static double ThresholdFor(EmbeddingSettings settings) => (EmbeddingThresholds.Override(settings) ?? EmbeddingThresholds.Today).DuplicateFlag;

    // The contradiction threshold they read, through the same switch: today's 0.85, the duplicate flag's, unless tuned.
    public static double OfferFor(EmbeddingSettings settings) => (EmbeddingThresholds.Override(settings) ?? EmbeddingThresholds.Today).Contradiction;

    // The engine's check: the live model when the settings have it on and it loaded, else same words only.
    public static DuplicateCheck For(RhythmConfig config, Embeddings? embeddings, ILogger? log = null)
    {
        var settings = EmbeddingSettings.From(config);
        return new(settings.Enabled && embeddings?.Spec == settings.Model ? embeddings.Model : null, ThresholdFor(settings), log: log, offer: OfferFor(settings));
    }

    // A text's vector by the thresholds' recipe: the words alone, as a save has no title.
    public static float[]? Vector(IEmbedder model, string text) => SimilaritySuggestions.Vector(model.Embed, model.Chunks, "", text);

    // Two vectors' score, as ThresholdTuning.Score gives it, kept within 0 to 1.
    public static double Score(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length && i < b.Length; i++) sum += (double)a[i] * b[i];
        return Math.Clamp(Math.Round(sum, 4), 0, 1);
    }

    // Vectors made in this process: by memory, the text revision its words were written at, and the model.
    static readonly ConcurrentDictionary<(Guid Id, int Revision, string Model), float[]> Vectors = new();
    const int Kept = 20_000;

    // The copies of the memory `memory` (its scope `subject`, its words `content`) at or above the threshold, and the
    // candidates at or above Offer, each closest first.
    internal async Task<MemoryNear> Find(NpgsqlConnection c, Guid memory, string subject, string content, CancellationToken ct)
    {
        var candidates = new List<(Guid Id, int Revision, string Text, bool Same)>();
        await using (var cmd = Database.Command(c, null, """
            WITH live AS (
              SELECT m.id,m.created_at,m.text_revision,m.content FROM memories m
              WHERE m.subject_id=@subject AND m.id<>@id AND memory_duplicate_live(m.id)),
            newest AS (SELECT l.id FROM live l ORDER BY l.created_at DESC,l.id DESC LIMIT @window)
            SELECT l.id,l.text_revision,left(l.content,8000),
              btrim(regexp_replace(lower(l.content),'\s+',' ','g'))=btrim(regexp_replace(lower(@content),'\s+',' ','g'))
            FROM live l
            WHERE l.id IN (SELECT n.id FROM newest n)
              OR btrim(regexp_replace(lower(l.content),'\s+',' ','g'))=btrim(regexp_replace(lower(@content),'\s+',' ','g'))
            """, ("subject", subject), ("id", memory), ("window", window), ("content", content)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) candidates.Add((r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GetBoolean(3)));
        var found = new List<MemoryCopy>();
        var offered = new List<MemoryCopy>();
        var mine = Model is { } m ? Vector(m, content) : null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var unscored = 0;
        foreach (var (id, revision, text, same) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (same) { found.Add(new(id, 1)); continue; }
            if (mine is null) continue;
            // Past the budget only the vectors made already are read: the save's answer does not wait for the rest.
            var vector = Vectors.TryGetValue((id, revision, Model!.Id), out var known) ? known
                : clock.Elapsed < Budget ? Of(Model!, id, revision, text) : null;
            if (vector is null) { if (clock.Elapsed >= Budget) unscored++; continue; }
            var score = Score(mine, vector);
            if (score >= Threshold) found.Add(new(id, score));
            if (score >= Offer) offered.Add(new(id, score));
        }
        if (unscored > 0)
            log?.LogInformation("Duplicates: {Unscored} memories of the scope were not scored within the save's {Budget} s; their vectors are made at the next save",
                unscored, Budget.TotalSeconds);
        return new([.. found.OrderByDescending(f => f.Score).ThenBy(f => f.Id).Take(Named)],
            [.. offered.OrderByDescending(f => f.Score).ThenBy(f => f.Id).Take(Offered)]);
    }

    // Of what a memory's flags and the offers nobody has named, each either way round, name now, those its new words no longer
    // reach (a later check of the same memory: an append, an update, a release). Each is scored as Find scores it, by its text as
    // it stands: the same words reach anything; with no model a same-words flag that no longer is the same words lapses, and any other can't be told
    // and stays; past the budget, what can't be scored stays.
    internal async Task<(Guid[] Copies, Guid[] Candidates)> Lapsed(NpgsqlConnection c, Guid memory, string content, CancellationToken ct)
    {
        var rows = new List<(Guid Id, bool Copy, string Model, int Revision, string Text)>();
        await using (var cmd = Database.Command(c, null, """
            SELECT o.id,true,f.model,o.text_revision,left(o.content,8000) FROM memory_duplicate_flags f
            JOIN memories o ON o.id=CASE WHEN f.memory_id=@id THEN f.copy_id ELSE f.memory_id END
            WHERE f.memory_id=@id OR f.copy_id=@id
            UNION ALL
            SELECT o.id,false,k.model,o.text_revision,left(o.content,8000) FROM memory_contradictions k
            JOIN memories o ON o.id=CASE WHEN k.memory_id=@id THEN k.conflict_id ELSE k.memory_id END
            WHERE (k.memory_id=@id OR k.conflict_id=@id) AND k.proposed_at IS NULL AND k.settled_at IS NULL
            """, ("id", memory)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) rows.Add((r.GetGuid(0), r.GetBoolean(1), r.GetString(2), r.GetInt32(3), r.GetString(4)));
        if (rows.Count == 0) return ([], []);
        static string Words(string text) => System.Text.RegularExpressions.Regex.Replace(text.Trim().ToLowerInvariant(), @"\s+", " ");
        var mine = Model is { } m ? Vector(m, content) : null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var copies = new List<Guid>();
        var candidates = new List<Guid>();
        foreach (var (id, copy, model, revision, text) in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (Words(text) == Words(content)) continue;
            if (mine is null)
            {
                if (copy && model == Exact) copies.Add(id);
                continue;
            }
            var vector = Vectors.TryGetValue((id, revision, Model!.Id), out var known) ? known
                : clock.Elapsed < Budget ? Of(Model!, id, revision, text) : null;
            if (vector is null) continue;
            var score = Score(mine, vector);
            if (copy && score < Threshold) copies.Add(id);
            if (!copy && score < Offer) candidates.Add(id);
        }
        return ([.. copies], [.. candidates]);
    }

    // A vector made already, or none: the standing check reads the same ones (MemoryStanding.cs), by the same recipe.
    internal static float[]? Known(IEmbedder model, Guid id, int revision) => Vectors.GetValueOrDefault((id, revision, model.Id));

    internal static float[]? Of(IEmbedder model, Guid id, int revision, string text)
    {
        if (Vectors.TryGetValue((id, revision, model.Id), out var known)) return known;
        var made = Vector(model, text);
        if (made is null) return null;
        if (Vectors.Count >= Kept) Vectors.Clear();
        return Vectors[(id, revision, model.Id)] = made;
    }
}

// What the check of an import did: how many memories it flagged as possible copies, how many it offered candidates for, and
// how many it did not reach within its budget. Queued: how many of the pairs it offered the contradiction review lists once
// the check is done (068's memory_contradiction_listed, the predicate every list and count reads): a pair offered that is also
// flagged as a copy waits among the copies until somebody names it a contradiction, and one memory may be offered several.
public sealed record MemoryImportReview(int Flagged, int Offered, int Left, int Queued = 0);

public sealed partial class MemoryStore
{
    public const int DuplicatesMax = 50;

    // A write a caller has just made, checked for copies and candidates (DuplicateCheck) and flagged and offered when it has
    // any (068's flag_memory_duplicates and offer_memory_contradictions, which keep only those still live in its scope): the
    // write with the copies and candidates that stand now. A later check of the same memory (an append, an update, a
    // release: 068) takes off the flags and the offers nobody has named that its words no longer reach. A write answered again
    // (an idempotency key used again) or that changed nothing is checked no more: it names what stands now. A held memory is not checked.
    // Without a check (memory.tools off, or a caller that checks nothing) the write as it was. Content: the words, when the
    // caller has them; else they are read here.
    public async Task<MemoryWrite> Checked(Guid principal, MemoryWrite write, string? content, MemoryCall call, DuplicateCheck? check, CancellationToken ct)
    {
        if (check is null || write.Hold is not null) return write;
        if (write.Replayed || write.Unchanged) return await Standing(principal, write, ct);
        await using var c = await source.OpenConnectionAsync(ct);
        var words = content is not null ? MemoryInput.Text(content) : await Words(c, write.Id, ct);
        if (words is null) return write;
        var near = await check.Find(c, write.Id, write.Subject, words, ct);
        (Guid[] Copies, Guid[] Candidates) lapsed = write.Revision > 1 ? await check.Lapsed(c, write.Id, words, ct) : ([], []);
        if (near.Copies.Count > 0 || near.Candidates.Count > 0 || lapsed.Copies.Length > 0 || lapsed.Candidates.Length > 0)
        {
            // Scored by the live model against the thresholds, or with no model the same words only, which reach 1.
            var (model, threshold, offer) = check.Model is { } scorer ? (scorer.Id, check.Threshold, check.Offer) : (DuplicateCheck.Exact, 1.0, 1.0);
            await using var tx = await c.BeginTransactionAsync(ct);
            try
            {
                if (near.Copies.Count > 0)
                    await Run(Database.Command(c, tx, "SELECT flag_memory_duplicates(@principal,@id,@revision,@items::jsonb,@model,@threshold::real,@channel,@client)",
                        ("principal", principal), ("id", write.Id), ("revision", write.Revision), ("items", Items(near.Copies)), ("model", model),
                        ("threshold", threshold), ("channel", call.Channel), ("client", AdminAudit.Client(call.Client))), ct);
                if (near.Candidates.Count > 0)
                    await Run(Database.Command(c, tx, "SELECT offer_memory_contradictions(@principal,@id,@revision,@items::jsonb,@model,@threshold::real,@channel,@client)",
                        ("principal", principal), ("id", write.Id), ("revision", write.Revision), ("items", Items(near.Candidates)), ("model", model),
                        ("threshold", offer), ("channel", call.Channel), ("client", AdminAudit.Client(call.Client))), ct);
                if (lapsed.Copies.Length > 0 || lapsed.Candidates.Length > 0)
                    await Run(Database.Command(c, tx, "SELECT recheck_memory_review(@principal,@id,@revision,@copies,@candidates,@channel,@client)",
                        ("principal", principal), ("id", write.Id), ("revision", write.Revision), ("copies", lapsed.Copies), ("candidates", lapsed.Candidates),
                        ("channel", call.Channel), ("client", AdminAudit.Client(call.Client))), ct);
            }
            catch (PostgresException e) when (Refusal(e) is { } refused) { throw refused; }
            await tx.CommitAsync(ct);
        }
        return await Standing(principal, write, ct);
    }

    static string Items(IEnumerable<MemoryCopy> items) => JsonSerializer.Serialize(items.Select(x => new { id = x.Id, score = x.Score }));

    static async Task Run(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using (cmd) await cmd.ExecuteScalarAsync(ct);
    }

    // A memory's words, unless it is held or gone.
    static async Task<string?> Words(NpgsqlConnection c, Guid memory, CancellationToken ct)
    {
        await using var cmd = Database.Command(c, null, "SELECT content FROM memories WHERE id=@id AND hold IS NULL", ("id", memory));
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    // A write with what stands now, as its reader is served it.
    async Task<MemoryWrite> Standing(Guid principal, MemoryWrite write, CancellationToken ct) =>
        write with { Duplicates = await CopiesOf(principal, write.Id, ct), Candidates = await CandidatesOf(principal, write.Id, ct) };

    // What rhythm import or rhythm import-second-brain has just added, checked as a save is, newest first so that of two
    // copies in the file the newer is the one flagged: 068's review_imported_memory as the operator, naming the command as
    // its client. A held memory is not checked, and one in the trash is not live. Stops at the budget and says how many it
    // did not reach; the rest stay unchecked until their words change. Nothing here fails the import, which has committed.
    public async Task<MemoryImportReview> ReviewImported(IReadOnlyCollection<Guid> memories, DuplicateCheck check, string client, TimeSpan budget, CancellationToken ct)
    {
        int flagged = 0, offered = 0, left = 0;
        var rows = new List<(Guid Id, string Subject, int Revision, string Text)>();
        await using var c = await source.OpenConnectionAsync(ct);
        await using (var cmd = Database.Command(c, null, """
            SELECT m.id,m.subject_id,m.revision,m.content FROM memories m WHERE m.id=ANY(@ids) AND memory_duplicate_live(m.id) ORDER BY m.created_at DESC,m.id DESC
            """, ("ids", memories.ToArray())))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) rows.Add((r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetString(3)));
        var (model, threshold, offer) = check.Model is { } scorer ? (scorer.Id, check.Threshold, check.Offer) : (DuplicateCheck.Exact, 1.0, 1.0);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var pairs = new List<(Guid Memory, Guid Conflict)>();
        foreach (var (id, subject, revision, text) in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (clock.Elapsed >= budget) { left++; continue; }
            var near = await check.Find(c, id, subject, text, ct);
            if (near.Copies.Count == 0 && near.Candidates.Count == 0) continue;
            await using var cmd = Database.Command(c, null,
                "SELECT review_imported_memory(@id,@revision,@copies::jsonb,@candidates::jsonb,@model,@threshold::real,@offer::real,@client)::text",
                ("id", id), ("revision", revision), ("copies", Items(near.Copies)), ("candidates", Items(near.Candidates)), ("model", model),
                ("threshold", threshold), ("offer", offer), ("client", client));
            using var doc = JsonDocument.Parse((string)(await cmd.ExecuteScalarAsync(ct))!);
            if (doc.RootElement.GetProperty("copies").GetArrayLength() > 0) flagged++;
            var candidates = doc.RootElement.GetProperty("candidates");
            if (candidates.GetArrayLength() > 0) offered++;
            foreach (var candidate in candidates.EnumerateArray()) pairs.Add((id, candidate.GetProperty("id").GetGuid()));
        }
        // Counted once every memory is checked, since a later one's flag can put an earlier pair among the copies.
        var queued = 0;
        if (pairs.Count > 0)
        {
            await using var count = Database.Command(c, null, """
                SELECT count(*) FROM memory_contradictions k JOIN unnest(@memories,@conflicts) AS p(memory_id,conflict_id)
                  ON k.memory_id=p.memory_id AND k.conflict_id=p.conflict_id
                WHERE memory_contradiction_listed(k)
                """, ("memories", pairs.Select(p => p.Memory).ToArray()), ("conflicts", pairs.Select(p => p.Conflict).ToArray()));
            queued = (int)(long)(await count.ExecuteScalarAsync(ct))!;
        }
        return new(flagged, offered, left, queued);
    }

    // The copies a memory's flags name that stand now, as its reader is served them (none for one it does not read).
    public async Task<IReadOnlyList<MemoryCopy>> CopiesOf(Guid principal, Guid memory, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = Database.Command(c, null, $"""
            SELECT {CopiesColumn} {From} WHERE m.id=@id AND {Database.Visible("r")}
            """, ("principal", principal), ("id", memory));
        return Copies(await cmd.ExecuteScalarAsync(ct) as string);
    }

    // A memory's standing copies as one JSON array (the read predicate on the memory's own row decides who reads them, and
    // a copy is of its own scope), or none while it takes no part in the review (held, marked wrong, ended, trashed).
    const string CopiesColumn = """
        CASE WHEN memory_duplicate_live(m.id) THEN (SELECT coalesce(jsonb_agg(jsonb_build_object('id',d.copy_id,'score',round(d.score::numeric,4))
          ORDER BY d.score DESC,d.copy_id),'[]'::jsonb) FROM memory_duplicate_copies(m.id) d) END::text
        """;

    static IReadOnlyList<MemoryCopy> Copies(string? json)
    {
        if (json is null) return [];
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.EnumerateArray().Select(x => new MemoryCopy(x.GetProperty("id").GetGuid(), x.GetProperty("score").GetDouble()))];
    }

    // The review a reader acts on: memories it reads (the one read predicate) and may change (033's author lock: keeping
    // both is a change's step) that 063's memory_duplicate_listed lists, optionally of one scope. One clause for the count
    // and the list, so the count is the length of the list.
    static (string Where, List<NpgsqlParameter> Args) DuplicateQueue(Guid principal, string? scope)
    {
        var args = new List<NpgsqlParameter> { new("principal", NpgsqlDbType.Uuid) { Value = principal } };
        var where = $"""
            {Database.Visible("r")} AND memory_duplicate_listed(m.id)
              AND memory_may_change(@principal,m.subject_id,m.author_id,NOT coalesce({Database.ScopesOff},false))
            """;
        string? named;
        try { named = MemorySettings.Named(scope); }
        catch (ArgumentException e) { throw new MemoryRefused("invalid", e.Message); }
        if (named is not null)
        {
            where += " AND m.scope=@scope";
            args.Add(new("scope", NpgsqlDbType.Text) { Value = named });
        }
        return (where, args);
    }

    // One page of the review and its whole length, in one statement (one snapshot): newest first, After continuing from a
    // page's Next.
    public async Task<MemoryDuplicatePage> Duplicates(Guid principal, string? scope, int limit, MemoryCursor? after, int previewChars, CancellationToken ct)
    {
        if (limit is < 1 or > DuplicatesMax) throw new MemoryRefused("invalid", $"n is 1 to {DuplicatesMax}");
        if (previewChars is < 0 or > MemoryInput.PreviewMax) throw new MemoryRefused("invalid", $"A preview is 0 to {MemoryInput.PreviewMax} characters");
        var (where, args) = DuplicateQueue(principal, scope);
        args.AddRange([
            new("limit", NpgsqlDbType.Integer) { Value = limit + 1 }, new("chars", NpgsqlDbType.Integer) { Value = previewChars },
            new("afterAt", NpgsqlDbType.TimestampTz) { Value = after is { } a ? a.CreatedAt.ToUniversalTime() : DBNull.Value },
            new("afterId", NpgsqlDbType.Uuid) { Value = after is { } b ? b.Id : DBNull.Value }]);
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            WITH q AS (SELECT m.id,m.created_at {From} WHERE {where})
            SELECT t.total,p.* FROM (SELECT count(*) AS total FROM q) t
            LEFT JOIN LATERAL (
              SELECT m.id,m.revision,m.subject_id,m.scope,m.author_id,m.status,m.created_at,left(m.content,@chars),length(m.content)>@chars,
                {CopiesColumn}
              FROM q JOIN memories m ON m.id=q.id
              WHERE @afterAt::timestamptz IS NULL OR (q.created_at,q.id)<(@afterAt,@afterId)
              ORDER BY q.created_at DESC,q.id DESC LIMIT @limit) p ON true
            ORDER BY p.created_at DESC,p.id DESC
            """, c);
        foreach (var arg in args) cmd.Parameters.Add(arg);
        var items = new List<MemoryDuplicate>();
        var total = 0;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                total = (int)r.GetInt64(0);
                if (r.IsDBNull(1)) continue;
                items.Add(new(r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6),
                    r.GetFieldValue<DateTimeOffset>(7), r.GetString(8), r.GetBoolean(9), Copies(r.IsDBNull(10) ? null : r.GetString(10))));
            }
        var next = items.Count > limit ? new MemoryCursor(items[limit - 1].CreatedAt, items[limit - 1].Id) : null;
        return new(total, items.Take(limit).ToList(), next);
    }

    // Keep both: the memory's duplicate flags come off (063's clear_memory_duplicates) once a person has looked, by
    // whoever may change it. Expected: the revision the caller read, refused (conflict) when it moved on. Unchanged when it
    // had none. No idempotency key: a repeat answers unchanged.
    public async Task<MemoryWrite> KeepCopy(Guid principal, Guid memory, int? expected, MemoryCall call, CancellationToken ct)
    {
        if (call.Key is not null) throw new MemoryRefused("invalid", "Keeping both takes no idempotency key: asking again answers unchanged");
        var write = await Write("SELECT clear_memory_duplicates(@principal,@id,@expected,@channel,@client)::text", principal, call, [], ct,
            ("id", NpgsqlDbType.Uuid, memory), ("expected", NpgsqlDbType.Integer, expected));
        return await Redrawn(principal, write, call, ct);
    }
}
