using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// The native memory store's core (docs/dev/memory.md sections 2, 5, 6.2 and 10; SB-P3-01 and SB-P3-02): create, get,
// list and filter, append, update and set status. Writes go through the checked database functions of migration 033,
// which decide where a memory lands, who may change it and whether a request was answered before, under the recorded
// memory.scopes mode locked FOR SHARE; reads go through the one read predicate (Database.Visible) on the memory's own
// resources row. Rhythm is the system of record: a memory's words live only in `memories` and its versions.
//
// Its callers came after the holds that had to ship before any memory write does (SB-P3-18, D-050; migration 036 screens
// every memory the database stores, MemoryHolds): the MCP memory tools (SB-P3-09, as an AI client) and the app's Memories
// view (SB-P3-12, as the app), both only with memory.tools on, off by default, and rhythm import as the operator; the
// terminal's commands are SB-P3-19's. With memory.tools off native memories stay inert, and every existing answer is as
// it was.

// Why the store refused: `unknown` (no such memory, one the caller does not read, or one in the trash, alike),
// `forbidden` (not this login's to write, or to change: the author lock), `invalid`, `too_large`, `conflict` (a stale
// expected revision; Revision holds the current one), `key_reused` (an idempotency key used for another request) and
// `unknown_project` (a list filter naming no project the caller reads; Known holds up to 10 it does) and `taken` (a
// project slug or alias another project of that scope answers to, MemoryProjects.cs); and an undo's (MemoryUndo.cs):
// `nothing_to_undo`, `no_version`, `not_visible`, `pruned` (Revision holds the oldest revision still kept) and `moved`;
// and a share's (MemoryShare.cs): `scopes_off`, `held` and `no_author`; and a supersede's (MemoryTruth.cs): `held`,
// `retracted` and `protected`; and a contradiction's (MemoryContradictions.cs): `not_offered`, `settled` and `needs_person`.
// The message is the engine's own words, never another scope's data.
public sealed class MemoryRefused(string code, string message, int? revision = null, IReadOnlyList<string>? known = null) : Exception(message)
{
    public string Code { get; } = code;
    public int? Revision { get; } = revision;
    public IReadOnlyList<string>? Known { get; } = known;
}

// What a caller hands the store, prepared the way every write path will (MemoryInput): the text as given, trimmed; its
// tags lower-cased, trimmed and de-duplicated, any in a namespace the engine sets itself dropped (and reported), and the
// text's #hashtags added. The text itself keeps its hashtags: a person's words are not rewritten.
public static partial class MemoryInput
{
    public const int MaxBytes = 131_072, MaxTags = 64, MaxTagLength = 128, PreviewDefault = 500, PreviewMax = 4000;

    // Upstream's reserved namespaces and pipeline markers (D-030; src/tags/system.ts and t7.ts at the pinned commit),
    // and project:, which Rhythm keeps as a field: a caller can't forge what the engine records in its own columns
    // (kind, status, volatility, capsule slots, holds, projects) or its own markers. Migration 033's
    // memory_reserved_tags() lists the same, and a test keeps the two equal. Matched by prefix, case aside: Rhythm has no
    // older tags to keep that merely share a prefix (an import maps those, SB-P8-01).
    public static readonly IReadOnlyList<string> ReservedNames =
        ["auto-pattern", "auto-insight", "synthesized", "rolled-up", "duplicate-candidate", "contradiction-resolved",
         "user-edited", "conflict-held", "owed-to-me", "retracted-source"];
    public static readonly IReadOnlyList<string> ReservedPrefixes =
        ["kind:", "status:", "volatility:", "stale:", "capsule:", "capsule-slot:", "quarantine:", "edited-canonical:",
         "standing:", "ledger:", "confidence:", "confidence-source:", "outcome:", "review-rearms:", "counterparty:", "project:"];

    // The sources a session transcript is saved from (upstream's TRANSCRIPT_SOURCES, src/constants.ts, and gemini-session for
    // the fourth agent harness/hooks knows): a memory of one never replaces a memory of another source (SB-P5-04). Migration
    // 063's memory_transcript_sources() lists the same, harness/hooks/agents.mjs names one for each agent, and tests keep the
    // three equal.
    public static readonly IReadOnlyList<string> TranscriptSources = ["claude-code", "codex-session", "cursor-session", "gemini-session"];

    public static bool Reserved(string tag)
    {
        var t = tag.Trim().ToLowerInvariant();
        return ReservedNames.Contains(t) || ReservedPrefixes.Any(p => t.StartsWith(p, StringComparison.Ordinal));
    }

    // A #hashtag a person meant as a tag: '#' after a space, a line's start or punctuation (never inside a word, an
    // address such as example.invalid/page#part, or an entity such as &#39;), then letters, digits or underscores. All
    // digits ("#298") is a reference, not a tag, and stays only in the text (as upstream's src/text/hashtags.ts).
    [GeneratedRegex(@"(?<![\w/&#])#(\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex Hashtag();

    public static IReadOnlyList<string> Hashtags(string text)
    {
        var found = new List<string>();
        foreach (Match match in Hashtag().Matches(text))
        {
            var tag = match.Groups[1].Value.ToLowerInvariant();
            if (tag.All(char.IsDigit) || tag.Length > MaxTagLength || Reserved(tag) || found.Contains(tag)) continue;
            found.Add(tag);
        }
        return found;
    }

    // A memory's text, trimmed: refused when blank, when it holds a NUL (which Postgres text can't), or when it is over
    // 131 072 bytes of UTF-8 (D-042, as upstream: measured in bytes, so a script of wide characters meets it sooner).
    public static string Text(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) throw new MemoryRefused("invalid", "A memory needs some text");
        if (t.Contains('\0')) throw new MemoryRefused("invalid", "A memory's text holds no NUL character");
        if (Encoding.UTF8.GetByteCount(t) > MaxBytes)
            throw new MemoryRefused("too_large", $"Too long for one memory: the limit is {MaxBytes} bytes of text (about 20 000 words); split it into smaller memories");
        return t;
    }

    // The tags a write stores: the caller's, then the text's hashtags, each once, at most 64; and the caller's tags left
    // off because the engine sets them itself, which the reply names.
    public static (IReadOnlyList<string> Kept, IReadOnlyList<string> Dropped) Tags(IEnumerable<string>? given, string? text)
    {
        var list = given?.ToList() ?? [];
        if (list.Count > MaxTags) throw new MemoryRefused("invalid", $"A memory takes at most {MaxTags} tags");
        var kept = new List<string>();
        var dropped = new List<string>();
        foreach (var raw in list)
        {
            var tag = (raw ?? "").Trim().ToLowerInvariant();
            if (tag.Length == 0) continue;
            if (tag.Length > MaxTagLength || tag.Any(char.IsControl))
                throw new MemoryRefused("invalid", $"A tag is at most {MaxTagLength} characters, with no control characters");
            if (Reserved(tag)) { if (!dropped.Contains(tag)) dropped.Add(tag); continue; }
            if (!kept.Contains(tag)) kept.Add(tag);
        }
        foreach (var tag in text is null ? [] : Hashtags(text))
            if (!kept.Contains(tag)) kept.Add(tag);
        if (kept.Count > MaxTags) throw new MemoryRefused("invalid", $"A memory takes at most {MaxTags} tags, its hashtags included");
        return (kept, dropped);
    }

    // A project slug as a write names it: lower case, trimmed; null or blank for none.
    public static string? Project(string? slug) => string.IsNullOrWhiteSpace(slug) ? null : slug.Trim().ToLowerInvariant();
}

// A new memory. Scope: null (the writer's own: private with scopes on, the household's with them off), private or
// shared, or upstream's personal and company, or Family (D-044). Kind episodic or semantic, importance 1 to 5, volatility
// durable, state or volatile, each optional and stored as given (D-033: no model classifies). Project: a slug, made in
// the memory's scope when it names none there yet. Source: the capturing client ("claude"), else the channel.
public sealed record MemoryDraft(string Content, IReadOnlyList<string>? Tags = null, string? Scope = null, string? Kind = null,
    int? Importance = null, string? Volatility = null, string? Project = null, string? Source = null);

// A change to a memory: only what it names. Content replaces the text (its hashtags join the tags); Tags replace the
// tags. Kind, importance, volatility and project are set when given; Clear names those to empty instead.
public sealed record MemoryChange(string? Content = null, IReadOnlyList<string>? Tags = null, string? Kind = null,
    int? Importance = null, string? Volatility = null, string? Project = null, IReadOnlySet<string>? Clear = null)
{
    public static readonly IReadOnlySet<string> Clearable = new HashSet<string>(StringComparer.Ordinal) { "kind", "importance", "volatility", "project" };
}

// How a write arrived: the channel (mcp for an AI client, app for the app's server, cli for the terminal's own login,
// bound to a person: migration 044, rhythm remember), the client's name if it gave one (recorded on the write's audit
// row as AdminAudit.Client cleans it, never a reason to refuse the write), and an idempotency key (8 to 200 of A-Z a-z
// 0-9 . _ : -) when the caller may retry: the same key and request apply once.
public sealed record MemoryCall(string Channel, string? Client = null, string? Key = null);

public sealed record MemoryProjectRef(Guid Id, string Slug, string? Name = null, bool Created = false);

// What a write answers. Scopes "off": the memory is visible to everyone in the household, which a reply saving one
// says (D-048). Changed: the fields an update (or an undo) changed; Unchanged: nothing needed writing; Replayed: the
// answer to an earlier request with the same idempotency key; DroppedTags: the caller's tags left off. Hold: the memory
// is held for review after this write (MemoryHolds.Notice says so in words), held by it (Now) or before; null when it is
// not held, or when a replayed answer's memory is no longer the caller's to read. To: the revision an undo went back to
// (MemoryUndo.cs); null for every other write. ProjectLeft: a share's memory had a project that could not come with it
// (MemoryShare.cs); false for every other write. Propagated: the memories it had replaced whose end moved with its start
// (a change of when it was true, MemoryTruth.cs). Cascade: what retracting it, or bringing it back, did to the memories
// it had replaced and those built on it (SB-P4-08); null when it did nothing. Duplicates: the memories of its scope a new
// memory was flagged as a copy of (SB-P5-02, MemoryDuplicates.cs), closest first; null when no check ran. Candidates: the
// memories of its scope that stand against it as possible contradictions (SB-P5-03, MemoryContradictions.cs), closest
// first; null when no check ran.
public sealed record MemoryWrite(Guid Id, int Revision, string Subject, string Scope, string Status, string Scopes,
    MemoryProjectRef? Project, IReadOnlyList<string> Changed, bool Unchanged, bool Replayed, IReadOnlyList<string> DroppedTags,
    MemoryHold? Hold = null, int? To = null, bool ProjectLeft = false, IReadOnlyList<Guid>? Propagated = null, MemoryCascade? Cascade = null,
    IReadOnlyList<MemoryCopy>? Duplicates = null, IReadOnlyList<MemoryCandidate>? Candidates = null);

// A memory as a reader is served it. Subject is its owner (a person, or household for Family); Author the person who
// wrote it, or none (a principal with no person). Content: the whole text from Get; in a list a preview of at most the
// asked characters (Truncated when longer), and none for a held memory, whose text waits for a person (D-039).
// Capsule and Slot: the capsule slot a person put it in (core or project, and the slot; migration 039), or none; in a
// list none for a held memory, as its project (SB-P3-22). State: what it is now (migration 049, SB-P4-06): current,
// replaced (ReplacedBy names the live memory of its scope that replaced it), ended or retracted (deprecated).
// RetractedSources: the retracted memories it was built on, which flagged it for a check (SB-P4-08), oldest flag first.
// Due: its due date (migration 061, SB-P5-10), or none. DuplicateOf: the memories of its scope it was flagged as a copy
// of on save (migration 063, SB-P5-02) that stand now, closest first; empty when none, and for one held, marked wrong,
// ended or in the trash. Standing: it is a standing instruction (migration 065, SB-P5-12); in a list false for a held one.
// Contradictions: the memories of its scope that stand against it as possible contradictions
// (migration 068, SB-P5-03), closest first, at most 10; empty when none, and for one held, marked wrong, ended or in the trash.
public sealed record MemoryRecord(Guid Id, string Subject, string Scope, string? Author, string Source, string? Kind,
    int? Importance, string? Volatility, string Status, IReadOnlyList<string> Tags, MemoryProjectRef? Project, string? Content,
    bool Truncated, string? Hold, DateTimeOffset? ValidFrom, bool UnknownStart, DateTimeOffset? ValidUntil, int Revision,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? Capsule = null, string? Slot = null, string State = "current",
    Guid? ReplacedBy = null, IReadOnlyList<Guid>? RetractedSources = null, MemoryDue? Due = null, IReadOnlyList<MemoryCopy>? DuplicateOf = null,
    bool Standing = false, IReadOnlyList<MemoryCandidate>? Contradictions = null);

public sealed record MemoryCursor(DateTimeOffset CreatedAt, Guid Id);

// A list's filters, every one narrowing what the caller reads already (memory.md section 2: a filter that names a scope
// never widens). Tags: memories holding all of them. Project: a slug or alias the caller reads, else unknown_project.
// Since and Before bound when it was written. Newest first, Limit at most 50; After continues from a page's Next. Query (the app's
// Memories list, SB-P6-01): memories whose words hold it, whatever the case; the store trims it and takes 1 to MemoryStore.QueryMax
// characters, and app_memories' door, its one caller, takes at most QueryMax as sent and something besides spaces. A held memory's
// words never select it. Every caller that names no query lists as before.
public sealed record MemoryFilter(string? Scope = null, IReadOnlyList<string>? Tags = null, string? Kind = null, string? Status = null,
    string? Project = null, string? Author = null, string? Source = null, DateTimeOffset? Since = null, DateTimeOffset? Before = null,
    int Limit = 10, MemoryCursor? After = null, int PreviewChars = MemoryInput.PreviewDefault, string? Query = null);

public sealed record MemoryPage(IReadOnlyList<MemoryRecord> Items, MemoryCursor? Next);

// With a recall log (engine.recallLog on, SB-P2-11), a get, append or update of a memory the reader's latest recall
// returned within 30 minutes marks it followed (RecallLog.Followed); without one, as by default, nothing is marked.
public sealed partial class MemoryStore(NpgsqlDataSource source, RecallLog? followed = null)
{
    // Links on save (SB-P4-03, migration 062): the memory tools' and the app's store has one with memory.tools on, the
    // live model loaded and adapters.embeddings.sources listing memories (MemoryLinkInference.For). Every other store
    // (a test's, the terminal's) has none, and its writes write exactly what they wrote before.
    public MemoryLinkInference? Inference { get; init; }

    // The check for copies and contradiction candidates (SB-P5-02, SB-P5-03, MemoryDuplicates.cs): the memory tools' and the
    // app's store has one with memory.tools on; every other store (a test's, the terminal's) has none unless it asks, and its
    // writes write exactly what they wrote before. CheckFailed hears of a check that failed: the write has committed by then,
    // and its answer names nothing.
    public DuplicateCheck? Check { get; init; }
    public Action<Exception>? CheckFailed { get; init; }

    // After a write that saved a memory's words (Words: a new memory, an append, a change or an undo of its content or
    // kind, a release of its hold), not a retry answered again, not one that changed nothing, and of a memory not held:
    // its links on save, a step of its own once the write has committed, which never fails the write.
    async Task<MemoryWrite> Linked(Guid principal, MemoryWrite write, bool words, MemoryCall call, CancellationToken ct)
    {
        // The check first: a pair it flags as a copy for certain takes no link (068).
        if (Check is { } check && (words || write.Replayed))
            try { write = await Checked(principal, write, null, call with { Key = null }, check, ct); }
            catch (Exception e) when (e is not OperationCanceledException) { CheckFailed?.Invoke(e); }
        if (Inference is { } inference && words && !write.Replayed && !write.Unchanged && write.Hold is null)
            await inference.Run(principal, write.Id, call, ct);
        return write;
    }

    // A pair a person has just kept both of may be linked now that nothing is flagged between them (068).
    internal async Task<MemoryWrite> Redrawn(Guid principal, MemoryWrite write, MemoryCall call, CancellationToken ct)
    {
        if (Inference is { } inference && !write.Unchanged && write.Hold is null) await inference.Run(principal, write.Id, call, ct);
        return write;
    }
    static bool Words(MemoryWrite write) => write.Changed.Any(f => f is "content" or "kind" or "hold");

    public async Task<MemoryWrite> Create(Guid principal, MemoryDraft draft, MemoryCall call, CancellationToken ct)
    {
        var (memory, dropped) = Drafted(draft);
        return await Linked(principal, await Write("SELECT create_memory(@principal,@memory::jsonb,@channel,@client,@key)::text", principal, call, dropped, ct,
            ("memory", NpgsqlDbType.Jsonb, memory.ToJsonString())), true, call, ct);
    }

    // A new memory's fields as create_memory takes them (033), prepared the way every write path prepares them
    // (MemoryInput), and the caller's tags left off; a commitment's writer (MemoryLoops.cs) takes the same.
    static (JsonObject Memory, IReadOnlyList<string> Dropped) Drafted(MemoryDraft draft)
    {
        var content = MemoryInput.Text(draft.Content);
        var (tags, dropped) = MemoryInput.Tags(draft.Tags, content);
        return (new JsonObject
        {
            ["content"] = content,
            ["tags"] = new JsonArray([.. tags.Select(t => (JsonNode?)t)]),
            ["scope"] = draft.Scope,
            ["kind"] = draft.Kind,
            ["importance"] = draft.Importance,
            ["volatility"] = draft.Volatility,
            ["project"] = MemoryInput.Project(draft.Project),
            ["source"] = draft.Source,
        }, dropped);
    }

    // A dated update at the end of the text ("[Update 2026-11-01]: ..." in the install's time zone). Expected: the
    // revision the caller read, refused (conflict) when the memory has moved on; null appends to the current text.
    public async Task<MemoryWrite> Append(Guid principal, Guid memory, string text, int? expected, MemoryCall call, CancellationToken ct) =>
        await Followed(principal, await Linked(principal, await Write("SELECT append_memory(@principal,@id,@text,@expected,@channel,@client,@key)::text", principal, call, [], ct,
            ("id", NpgsqlDbType.Uuid, memory), ("text", NpgsqlDbType.Text, MemoryInput.Text(text)), ("expected", NpgsqlDbType.Integer, expected)), true, call, ct), ct);

    // A change that wrote something, not a retry answered again, is a follow-up on the reader's latest recall.
    async Task<MemoryWrite> Followed(Guid principal, MemoryWrite write, CancellationToken ct)
    {
        if (followed is not null && !write.Replayed && !write.Unchanged) await followed.Followed(principal, [write.Id], ct);
        return write;
    }

    public async Task<MemoryWrite> Update(Guid principal, Guid memory, MemoryChange change, int? expected, MemoryCall call, CancellationToken ct)
    {
        var clear = change.Clear ?? new HashSet<string>();
        if (clear.Any(f => !MemoryChange.Clearable.Contains(f)))
            throw new MemoryRefused("invalid", "Only kind, importance, volatility and project are cleared");
        var body = new JsonObject();
        IReadOnlyList<string> dropped = [];
        if (change.Content is not null)
        {
            var content = MemoryInput.Text(change.Content);
            body["content"] = content;
            body["hashtags"] = new JsonArray([.. MemoryInput.Hashtags(content).Select(t => (JsonNode?)t)]);
        }
        if (change.Tags is not null)
        {
            (var kept, dropped) = MemoryInput.Tags(change.Tags, null);
            body["tags"] = new JsonArray([.. kept.Select(t => (JsonNode?)t)]);
        }
        void Set(string field, JsonNode? value, bool given)
        {
            if (clear.Contains(field)) body[field] = null;
            else if (given) body[field] = value;
        }
        Set("kind", change.Kind, change.Kind is not null);
        Set("importance", change.Importance, change.Importance is not null);
        Set("volatility", change.Volatility, change.Volatility is not null);
        Set("project", MemoryInput.Project(change.Project), MemoryInput.Project(change.Project) is not null);
        if (body.Count == 0) throw new MemoryRefused("invalid", "A change names content, tags, kind, importance, volatility or project");
        var write = await Write("SELECT update_memory(@principal,@id,@change::jsonb,@expected,@channel,@client,@key)::text", principal, call, dropped, ct,
            ("id", NpgsqlDbType.Uuid, memory), ("change", NpgsqlDbType.Jsonb, body.ToJsonString()), ("expected", NpgsqlDbType.Integer, expected));
        return await Followed(principal, await Linked(principal, write, Words(write), call, ct), ct);
    }

    // canonical, draft or deprecated (D-030), by name.
    public Task<MemoryWrite> SetStatus(Guid principal, Guid memory, string status, int? expected, MemoryCall call, CancellationToken ct) =>
        Write("SELECT set_memory_status(@principal,@id,@status,@expected,@channel,@client,@key)::text", principal, call, [], ct,
            ("id", NpgsqlDbType.Uuid, memory), ("status", NpgsqlDbType.Text, status), ("expected", NpgsqlDbType.Integer, expected));

    // A person lifts a memory's hold (D-039): from the app (channel app) or the terminal (cli, a login bound to a
    // person), never an AI client's channel (mcp: forbidden). With scopes on only those who may change the memory may
    // (memory.md section 5). The held state is kept as a version. One not held answers Unchanged. No idempotency key: a
    // second release of the same memory answers unchanged.
    public async Task<MemoryWrite> Release(Guid principal, Guid memory, int? expected, MemoryCall call, CancellationToken ct) =>
        call.Key is not null
            ? throw new MemoryRefused("invalid", "A release takes no idempotency key: releasing again answers unchanged")
            : await Linked(principal, await Write("SELECT release_memory_hold(@principal,@id,@expected,@channel,@client)::text", principal, call, [], ct,
                ("id", NpgsqlDbType.Uuid, memory), ("expected", NpgsqlDbType.Integer, expected)), true, call, ct);

    // One memory, whole, as the caller is served it; null when there is none it reads (an unknown id, one of another
    // scope and one in the trash answer alike).
    public async Task<MemoryRecord?> Get(Guid principal, Guid id, CancellationToken ct)
    {
        MemoryRecord? found;
        await using (var c = await source.OpenConnectionAsync(ct))
        await using (var cmd = Database.Command(c, null, $"{Select(null)} WHERE m.id=@id AND {Database.Visible("r")}",
            ("principal", principal), ("id", id)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            found = await reader.ReadAsync(ct) ? Record(reader) : null;
        if (found is not null && followed is not null) await followed.Followed(principal, [id], ct);
        return found;
    }

    public async Task<MemoryPage> List(Guid principal, MemoryFilter filter, CancellationToken ct)
    {
        if (filter.Limit is < 1 or > 50) throw new MemoryRefused("invalid", "limit is 1 to 50");
        if (filter.PreviewChars is < 0 or > MemoryInput.PreviewMax) throw new MemoryRefused("invalid", $"A preview is 0 to {MemoryInput.PreviewMax} characters");
        await using var c = await source.OpenConnectionAsync(ct);
        var (where, args) = await Filtered(c, principal, filter, ct);
        args.AddRange([("limit", filter.Limit + 1), ("chars", filter.PreviewChars)]);
        await using var cmd = Database.Command(c, null, $"{Select("@chars")} WHERE {string.Join(" AND ", where)} ORDER BY m.created_at DESC,m.id DESC LIMIT @limit",
            [.. args]);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var items = new List<MemoryRecord>();
        while (await reader.ReadAsync(ct)) items.Add(Record(reader));
        var next = items.Count > filter.Limit ? new MemoryCursor(items[filter.Limit - 1].CreatedAt, items[filter.Limit - 1].Id) : null;
        return new(items.Take(filter.Limit).ToList(), next);
    }

    // The predicates a list, a count and a tag vocabulary share (SB-P3-17), over From: the read predicate on the memory's
    // own resources row, then each filter, every one narrowing. So a count is the length of the list it summarises, and
    // a tag's count the length of the list that filters by it too. Limit and PreviewChars are a list's own.
    async Task<(List<string> Where, List<(string, object?)> Args)> Filtered(NpgsqlConnection c, Guid principal, MemoryFilter filter, CancellationToken ct)
    {
        var where = new List<string> { Database.Visible("r") };
        var args = new List<(string, object?)> { ("principal", principal) };
        void Add(string clause, string name, object? value) { where.Add(clause); args.Add((name, value)); }
        if (filter.Scope is not null)
        {
            string? scope;
            try { scope = MemorySettings.Named(filter.Scope); }
            catch (ArgumentException e) { throw new MemoryRefused("invalid", e.Message); }
            if (scope is not null) Add("m.scope=@scope", "scope", scope);
        }
        // A held memory is listed without its tags and project (Select), so they never select it either: a filter that
        // matched one would tell the caller what it holds (D-149).
        if (filter.Tags is { Count: > 0 } tags) Add("(m.hold IS NULL AND m.tags @> @tags)", "tags", tags.Select(t => t.Trim().ToLowerInvariant()).ToArray());
        // Words a memory holds (SB-P6-01): a plain match of the trimmed query anywhere in its text, case aside, with no wildcards, so what
        // a person types is what is looked for. A held memory is listed without its words, so they never select it either (D-149).
        if (filter.Query is { } query)
        {
            var words = query.Trim();
            if (words.Length is 0 or > QueryMax) throw new MemoryRefused("invalid", $"A search is 1 to {QueryMax} characters");
            Add("(m.hold IS NULL AND strpos(lower(m.content),lower(@query))>0)", "query", words);
        }
        if (filter.Kind is not null) Add("m.kind=@kind", "kind", filter.Kind);
        if (filter.Status is not null) Add("m.status=@status", "status", filter.Status);
        if (filter.Author is not null) Add("m.author_id=@author", "author", filter.Author);
        if (filter.Source is not null) Add("m.source=@source", "source", filter.Source);
        if (filter.Since is { } since) Add("m.created_at>=@since", "since", since.ToUniversalTime());
        if (filter.Before is { } before) Add("m.created_at<@before", "before", before.ToUniversalTime());
        if (filter.After is { } after)
        {
            Add("(m.created_at,m.id)<(@afterAt,@afterId)", "afterAt", after.CreatedAt.ToUniversalTime());
            args.Add(("afterId", after.Id));
        }
        if (MemoryInput.Project(filter.Project) is { } project)
        {
            // A project counts only in a scope the caller reads, through the same predicate as the memories in it, and
            // never while it is held (SB-P3-21). One it does not know is answered with up to 10 it does (CTX-07).
            await using (var known = Database.Command(c, null, $"""
                SELECT EXISTS(SELECT 1 FROM memory_projects p WHERE {ProjectNamed} AND {ProjectListed()})
                """, ("principal", principal), ("name", project)))
                if (await known.ExecuteScalarAsync(ct) is not true)
                {
                    var names = await KnownProjects(c, principal, 10, ct);
                    throw new MemoryRefused("unknown_project", names.Count == 0 ? "No project by that name"
                        : $"No project by that name; known projects: {string.Join(", ", names)}", known: names);
                }
            // Its members: the memories in it, and those of its scope tagged with one of its aliases, which a project claims
            // without rewriting them (upstream's membership by tag, SB-P3-07).
            Add($"""
                (m.hold IS NULL AND EXISTS(SELECT 1 FROM memory_projects p WHERE {ProjectNamed} AND NOT p.held AND (p.id=m.project_id
                  OR p.subject_id=m.subject_id AND m.tags && ARRAY(SELECT a.alias FROM memory_project_aliases a WHERE a.project_id=p.id))))
                """, "name", project);
        }
        return (where, args);
    }

    // The most retracted sources a memory is served with (049's flags have no bound of their own).
    public const int RetractedSourcesMax = 100;

    // The longest words a list looks for (MemoryFilter.Query).
    public const int QueryMax = 200;

    // A memory with its own resources row, which the read predicate looks at, and its project.
    const string From = """
        FROM memories m JOIN resources r ON r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL AND r.external_id=m.id::text
        LEFT JOIN memory_projects p ON p.id=m.project_id
        """;

    // The columns Record reads. With a preview length, the text cut to it; else whole. A held memory is listed without
    // its words (D-039): no text, and no tags or project either, since a caller's tags and the hashtags lifted from the
    // held text can carry the same words, nor its capsule slot. Get serves it whole, with its hold and slot.
    // Its retracted sources (049's flags) the oldest first, at most RetractedSourcesMax, which every answer's contract
    // allows (app_memories' and the memory tools').
    static string Select(string? chars) => $"""
        SELECT m.id,m.subject_id,m.scope,m.author_id,m.source,m.kind,m.importance,m.volatility,m.status,
          {(chars is null ? "m.tags" : "CASE WHEN m.hold IS NULL THEN m.tags ELSE '{}'::text[] END")},
          {(chars is null ? "m.content,false" : $"CASE WHEN m.hold IS NULL THEN left(m.content,{chars}) END,m.hold IS NULL AND length(m.content)>{chars}")},
          m.hold,CASE WHEN isfinite(m.valid_from) THEN m.valid_from END,m.valid_from='-infinity',
          CASE WHEN isfinite(m.valid_until) THEN m.valid_until END,m.revision,m.created_at,m.updated_at,
          {(chars is null ? "p.id,p.slug,p.name" : "CASE WHEN m.hold IS NULL THEN p.id END,CASE WHEN m.hold IS NULL THEN p.slug END,CASE WHEN m.hold IS NULL THEN p.name END")},
          {(chars is null ? "m.capsule,m.capsule_slot" : "CASE WHEN m.hold IS NULL THEN m.capsule END,CASE WHEN m.hold IS NULL THEN m.capsule_slot END")},
          memory_validity_state(m.id),memory_replaced_by(m.id),
          ARRAY(SELECT f.retracted_id FROM memory_retraction_flags f LEFT JOIN memories s ON s.id=f.retracted_id
            WHERE f.memory_id=m.id AND (s.id IS NULL OR s.subject_id=m.subject_id) ORDER BY f.flagged_at,f.retracted_id LIMIT {RetractedSourcesMax}),
          memory_due_json(m.due_at,m.due_kind,m.due_source)::text,
          {CopiesColumn},
          {(chars is null ? "m.standing" : "m.hold IS NULL AND m.standing")},
          {CandidatesColumn}
        {From}
        """;

    static MemoryRecord Record(NpgsqlDataReader r)
    {
        string? Text(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        DateTimeOffset? At(int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
        return new(r.GetGuid(0), r.GetString(1), r.GetString(2), Text(3), r.GetString(4), Text(5), r.IsDBNull(6) ? null : r.GetInt16(6),
            Text(7), r.GetString(8), r.GetFieldValue<string[]>(9), r.IsDBNull(19) ? null : new(r.GetGuid(19), r.GetString(20), r.GetString(21)),
            Text(10), r.GetBoolean(11), Text(12), At(13), !r.IsDBNull(14) && r.GetBoolean(14), At(15), r.GetInt32(16),
            r.GetFieldValue<DateTimeOffset>(17), r.GetFieldValue<DateTimeOffset>(18), Text(22), Text(23), r.GetString(24),
            r.IsDBNull(25) ? null : r.GetGuid(25), r.GetFieldValue<Guid[]>(26), MemoryDue.From(r.IsDBNull(27) ? null : r.GetString(27)),
            Copies(r.IsDBNull(28) ? null : r.GetString(28)), r.GetBoolean(29), Candidates(r.IsDBNull(30) ? null : r.GetString(30)));
    }

    async Task<MemoryWrite> Write(string sql, Guid principal, MemoryCall call, IReadOnlyList<string> dropped, CancellationToken ct,
        params (string Name, NpgsqlDbType Type, object? Value)[] args) => (await WriteAnswer(sql, principal, call, dropped, ct, args)).Write;

    // A write, with the database's whole answer beside it, for a writer whose answer says more than a write's does (a
    // digest's, MemoryDigests.cs).
    async Task<(MemoryWrite Write, JsonElement Answer)> WriteAnswer(string sql, Guid principal, MemoryCall call, IReadOnlyList<string> dropped,
        CancellationToken ct, params (string Name, NpgsqlDbType Type, object? Value)[] args)
    {
        // The write and the read of its hold in one transaction, so the reply says what this write left: the hold the
        // database's screening put on it now (migration 036 notes it for the transaction), or one it had before. The
        // hold is read through the one read predicate, so a replayed answer about a memory the caller no longer reads
        // says nothing of it.
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        string json;
        await using (var cmd = new NpgsqlCommand(sql, c, tx))
        {
            (string Name, NpgsqlDbType Type, object? Value)[] all =
                [("principal", NpgsqlDbType.Uuid, principal), .. args, ("channel", NpgsqlDbType.Text, call.Channel),
                 ("client", NpgsqlDbType.Text, AdminAudit.Client(call.Client)), ("key", NpgsqlDbType.Text, call.Key)];
            foreach (var (name, type, value) in all.Where(a => sql.Contains("@" + a.Name, StringComparison.Ordinal)))
                cmd.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
            try { json = (string)(await cmd.ExecuteScalarAsync(ct))!; }
            catch (PostgresException e) when (Refusal(e) is { } refused) { throw refused; }
        }
        using var doc = JsonDocument.Parse(json);
        var o = doc.RootElement;
        var id = o.GetProperty("id").GetGuid();
        MemoryHold? hold = null;
        await using (var held = Database.Command(c, tx, $"""
            SELECT m.hold,m.hold_score,(SELECT n.held->'signals' FROM (SELECT nullif(current_setting('rhythm.memory_held',true),'')::jsonb->(m.id::text) AS held) n
              WHERE n.held->>'revision'=m.revision::text)
            FROM memories m JOIN resources r ON r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL
            WHERE m.id=@id AND m.hold IS NOT NULL AND {Database.Visible("r")}
            """, ("principal", principal), ("id", id)))
        await using (var row = await held.ExecuteReaderAsync(ct))
            if (await row.ReadAsync(ct))
            {
                var now = !row.IsDBNull(2);
                hold = new(row.GetString(0), Math.Round(row.IsDBNull(1) ? 0 : row.GetFloat(1), 2),
                    now ? [.. JsonDocument.Parse(row.GetString(2)).RootElement.EnumerateArray().Select(s => s.GetString()!)] : [], now);
            }
        var cascade = await Cascaded(c, tx, id, ct);
        await tx.CommitAsync(ct);
        MemoryProjectRef? project = o.TryGetProperty("project", out var p) && p.ValueKind == JsonValueKind.Object
            ? new(p.GetProperty("id").GetGuid(), p.GetProperty("slug").GetString()!, null,
                p.TryGetProperty("created", out var made) && made.ValueKind == JsonValueKind.True)
            : null;
        bool Flag(string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        return (new(id, o.GetProperty("revision").GetInt32(), o.GetProperty("subject").GetString()!,
            o.GetProperty("scope").GetString()!, o.GetProperty("status").GetString()!, o.GetProperty("scopes").GetString()!, project,
            o.TryGetProperty("changed", out var changed) ? [.. changed.EnumerateArray().Select(x => x.GetString()!)] : [],
            Flag("unchanged"), Flag("replayed"), dropped, hold,
            o.TryGetProperty("to", out var to) && to.ValueKind == JsonValueKind.Number ? to.GetInt32() : null, Flag("projectLeft"),
            o.TryGetProperty("propagated", out var moved) && moved.ValueKind == JsonValueKind.Array ? [.. moved.EnumerateArray().Select(x => x.GetGuid())] : null,
            cascade), o.Clone());
    }

    [GeneratedRegex(@"at revision (\d+) now", RegexOptions.CultureInvariant)]
    private static partial Regex Current();

    // The database's refusals (migration 033) as the store's; anything else is the engine's own failure. An undo's
    // refusals of a revision (041) are 22023 with a hint naming which: nothing_to_undo, no_version, not_visible, moved,
    // and pruned:<n>, n the oldest revision still kept, carried as Revision; a share's (048) likewise: scopes_off, held
    // and no_author; a supersede's (049): held, retracted and protected; and a digest's (054): not_due, and
    // unknown_project, whose known projects the digest's writer adds; and a standing instruction's (065): limit, its words
    // past the constraint on the row, by an update, an append or an undo, and a due date or the task tag, which the row's
    // other two constraints refuse it, by a snooze or an update.
    internal static MemoryRefused? Refusal(PostgresException e) => e.SqlState switch
    {
        "P0002" => new("unknown", "Unknown memory"),
        "42501" => new("forbidden", e.MessageText),
        "22023" when e.Hint is "nothing_to_undo" or "no_version" or "not_visible" or "moved" or "scopes_off" or "held" or "no_author"
            or "retracted" or "protected" or "not_due" or "unknown_project" or "limit" or "not_offered" or "settled" or "needs_person" =>
            new(e.Hint, e.MessageText),
        "22023" when e.Hint is { } hint && hint.StartsWith("pruned:", StringComparison.Ordinal) && int.TryParse(hint[7..], out var kept) =>
            new("pruned", e.MessageText, kept),
        "22023" => new("invalid", e.MessageText),
        "54000" => new("too_large", e.MessageText),
        "23514" when e.ConstraintName == "memories_standing_short" =>
            new("too_large", $"A standing instruction is at most {MemoryStanding.MaxChars} characters: nothing was changed. Say it shorter, or stop it first (stop_standing)"),
        "23514" when e.ConstraintName == "memories_standing_undated" =>
            new("invalid", "A standing instruction takes no due date, since it fires when its topic comes up: nothing was changed. Stop it first (stop_standing) to date it as an ordinary memory"),
        "23514" when e.ConstraintName == "memories_standing_no_task" =>
            new("invalid", "A standing instruction can't also be a task, something owed until it is done: nothing was changed. Leave the tag task and any #task out, or stop it first (stop_standing)"),
        "40001" => new("conflict", e.MessageText, Current().Match(e.MessageText) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null),
        "23505" when e.ConstraintName is null => new("key_reused", e.MessageText),
        _ => null,
    };
}
