using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Npgsql;

namespace Knowledge;

// Which memories match: the filters of the app's Memories view and of a dashboard's memories feed (docs/dev/screens.md
// 10.12), defined once here so the view's list and the feed can never disagree. Nine filters: words (text its words hold,
// case aside), status, kind, project (a slug or an alias), tag, work (true: work items only; false: no work items),
// workType, state and priority (1 to 4, or 0 for the unranked). Within one filter any of its values matches (or); across
// filters every one given must (and); each filter but work can be negated instead, matching none of its values. There is
// no nesting: a dashboard that needs two unrelated sets reads two feeds. Any of workType, state and priority keeps work
// items only, negated or not. A held memory's words, tags and project never select it, by a filter or by its negation,
// since either would tell what it holds (D-149); its status, kind and work properties hold no words and select it as any.
// Every filter narrows what the caller reads already (the one read predicate, MemoryStore.Filtered); none widens it.
public sealed record MemoryAny<T>(IReadOnlyList<T> Values, bool Not = false);

// A memories feed's `where` as a harness writes it: each member one value, a list of values (any of them), or
// {"not": a value or a list} (none of them); work is true or false. A member it doesn't know is kept in Other only so
// MemoryMatch.Read can refuse it.
public sealed record MemoryWhere(JsonElement? Words = null, JsonElement? Status = null, JsonElement? Kind = null, JsonElement? Project = null,
    JsonElement? Tag = null, JsonElement? Work = null, JsonElement? WorkType = null, JsonElement? State = null, JsonElement? Priority = null)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Other { get; init; }
}

public sealed record MemoryMatch(MemoryAny<string>? Words = null, MemoryAny<string>? Status = null, MemoryAny<string>? Kind = null,
    MemoryAny<string>? Project = null, MemoryAny<string>? Tag = null, bool? Work = null, MemoryAny<string>? WorkType = null,
    MemoryAny<string>? State = null, MemoryAny<int>? Priority = null)
{
    // The most values one filter lists.
    public const int MaxValues = 20;
    public static readonly IReadOnlyList<string> Statuses = ["canonical", "draft", "deprecated"], Kinds = ["episodic", "semantic"];
    public static readonly IReadOnlyList<string> Names = ["words", "status", "kind", "project", "tag", "work", "workType", "state", "priority"];

    // It keeps work items only: work true, or any of workType, state and priority.
    public bool Worked => Work == true || WorkType is not null || State is not null || Priority is not null;
    // The projects it names, either way.
    public IReadOnlyList<string> Projects => Project?.Values ?? [];

    // The list's filters (MemoryFilter: the app's Memories view, the MCP list_recent and the terminal's list, one value each)
    // as a match, checked as the list always checked them: words trimmed, 1 to MemoryStore.QueryMax characters; a work word
    // (MemoryWork.Listed): all keeps work items, open those neither done nor cancelled, a state that state; a type and a
    // priority (0 the unranked) known. Any of the three keeps work items only. A status, kind, tag or project is matched as
    // given (a word the store doesn't know matches nothing, as before).
    public static MemoryMatch Of(MemoryFilter filter)
    {
        static MemoryAny<string>? One(string? value) => value is null ? null : new([value]);
        string? words = null;
        if (filter.Query is { } query)
        {
            words = query.Trim();
            if (words.Length is 0 or > MemoryStore.QueryMax) throw new MemoryRefused("invalid", $"A search is 1 to {MemoryStore.QueryMax} characters");
        }
        // One tag, as every list names, is the tag filter; more is the list's own (all of them, MemoryStore.Filtered).
        var match = new MemoryMatch(One(words), One(filter.Status), One(filter.Kind), One(MemoryInput.Project(filter.Project)),
            filter.Tags is [var tag] ? new([tag.Trim().ToLowerInvariant()]) : null);
        if (filter.Work is null && filter.WorkType is null && filter.WorkPriority is null) return match;
        var state = MemoryWork.Listed(filter.Work);
        var type = filter.WorkType is null ? null : MemoryWork.Type(filter.WorkType);
        if (filter.WorkPriority is { } rank && rank != 0) MemoryWork.Priority(rank);
        return match with
        {
            Work = true, WorkType = One(type),
            State = state is null ? null : state == MemoryWork.Open ? new([MemoryWork.Done, MemoryWork.Cancelled], true) : new([state]),
            Priority = filter.WorkPriority is { } priority ? new([priority]) : null,
        };
    }

    // A feed's `where` read and checked, or why not, naming it as `at` (feeds[0].where). None is every memory the reader reads.
    public static string? Read(MemoryWhere? where, string at, out MemoryMatch match)
    {
        match = new();
        if (where is null) return null;
        if (where.Other is { Count: > 0 }) return $"{at} takes {string.Join(", ", Names.Take(Names.Count - 1))} and {Names[^1]}";
        static string? Text(JsonElement e, int max, bool lower)
        {
            if (e.ValueKind != JsonValueKind.String) return null;
            var t = e.GetString()!.Trim();
            if (lower) t = t.ToLowerInvariant();
            return t.Length >= 1 && t.Length <= max && !t.Any(char.IsControl) ? t : null;
        }
        static Func<JsonElement, string?> Word(IReadOnlyList<string> words) => e => Text(e, 40, true) is { } w && words.Contains(w) ? w : null;
        string? problem;
        if ((problem = Values(where.Words, at, "words", e => Text(e, MemoryStore.QueryMax, false), $"each is 1-{MemoryStore.QueryMax} characters its words hold", out var words)) is not null
            || (problem = Values(where.Status, at, "status", Word(Statuses), "each is canonical, draft or deprecated", out var status)) is not null
            || (problem = Values(where.Kind, at, "kind", Word(Kinds), "each is episodic or semantic", out var kind)) is not null
            || (problem = Values(where.Project, at, "project", e => Text(e, 128, true), "each is a project's slug or alias, from list_projects", out var project)) is not null
            || (problem = Values(where.Tag, at, "tag", e => Text(e, MemoryInput.MaxTagLength, true), $"each is a tag of 1-{MemoryInput.MaxTagLength} characters", out var tag)) is not null
            || (problem = Values(where.WorkType, at, "workType", Word(MemoryWork.Types), "each is task, bug, feature or improvement", out var type)) is not null
            || (problem = Values(where.State, at, "state", Word(MemoryWork.States),
                "each is backlog, ready, active, review, done or cancelled; for open work use {\"not\": [\"done\", \"cancelled\"]}", out var state)) is not null
            || (problem = Values(where.Priority, at, "priority",
                e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) && n is >= 0 and <= MemoryWork.PriorityLast ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                "each is 1 (first) to 4 (last), or 0 for the unranked", out var priority)) is not null)
            return problem;
        bool? work = null;
        if (where.Work is { ValueKind: not JsonValueKind.Null } given)
        {
            if (given.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return $"{at}.work is true (work items only) or false (no work items)";
            work = given.GetBoolean();
        }
        if (work == false && (type is not null || state is not null || priority is not null))
            return $"{at}: work false keeps memories that are no work items, which have no workType, state or priority";
        match = new(words, status, kind, project, tag, work, type, state,
            priority is null ? null : new([.. priority.Values.Select(p => int.Parse(p, System.Globalization.CultureInfo.InvariantCulture))], priority.Not));
        return null;
    }

    // One filter: a value, a list of 1-MaxValues values (any of them), or {"not": either} (none of them); each value as
    // `one` reads it, null when it isn't one, and each kept once, in the order given.
    static string? Values(JsonElement? given, string at, string name, Func<JsonElement, string?> one, string expected, out MemoryAny<string>? any)
    {
        any = null;
        if (given is not { } g || g.ValueKind == JsonValueKind.Null) return null;
        var not = false;
        if (g.ValueKind == JsonValueKind.Object)
        {
            if (g.EnumerateObject().ToArray() is not [{ Name: "not" } only])
                return $"{at}.{name} is a value, a list of values (any of them) or {{\"not\": values}} (none of them)";
            not = true;
            g = only.Value;
        }
        JsonElement[] items = g.ValueKind == JsonValueKind.Array ? [.. g.EnumerateArray()] : [g];
        if (items.Length is < 1 or > MaxValues) return $"{at}.{name} lists 1-{MaxValues} values";
        var values = new List<string>();
        foreach (var item in items)
        {
            if (one(item) is not { } value) return $"{at}.{name}: {expected}";
            if (!values.Contains(value, StringComparer.Ordinal)) values.Add(value);
        }
        any = new(values, not);
        return null;
    }

    // As stored: each filter given, in Names' order, a list for any of its values and {"not": [...]} for none of them.
    public JsonObject ToJson()
    {
        static JsonNode List<T>(MemoryAny<T> any)
        {
            var values = new JsonArray([.. any.Values.Select(v => JsonValue.Create(v))]);
            return any.Not ? new JsonObject { ["not"] = values } : values;
        }
        var json = new JsonObject();
        if (Words is { } words) json["words"] = List(words);
        if (Status is { } status) json["status"] = List(status);
        if (Kind is { } kind) json["kind"] = List(kind);
        if (Project is { } project) json["project"] = List(project);
        if (Tag is { } tag) json["tag"] = List(tag);
        if (Work is { } work) json["work"] = work;
        if (WorkType is { } type) json["workType"] = List(type);
        if (State is { } state) json["state"] = List(state);
        if (Priority is { } priority) json["priority"] = List(priority);
        return json;
    }
    public MemoryWhere ToWhere() => JsonSerializer.Deserialize<MemoryWhere>(ToJson().ToJsonString(), DashboardDraft.Json)!;

    // The match as SQL over MemoryStore's From (m a memory, w its work row), each filter one clause of `where` with its
    // arguments: the one statement both the list and the feed run.
    internal void Clauses(List<string> where, List<(string, object?)> args)
    {
        static string Either(bool not, string test) => not ? $"NOT coalesce({test},false)" : $"coalesce({test},false)";
        void Add(string clause, string name, object value) { where.Add(clause); args.Add((name, value)); }
        if (Words is { } words)
            Add($"(m.hold IS NULL AND {Either(words.Not, "EXISTS(SELECT 1 FROM unnest(@matchWords::text[]) q(w) WHERE strpos(lower(m.content),lower(q.w))>0)")})",
                "matchWords", words.Values.ToArray());
        if (Status is { } status) Add(Either(status.Not, "m.status=ANY(@matchStatus::text[])"), "matchStatus", status.Values.ToArray());
        if (Kind is { } kind) Add(Either(kind.Not, "m.kind=ANY(@matchKind::text[])"), "matchKind", kind.Values.ToArray());
        if (Tag is { } tag) Add($"(m.hold IS NULL AND {Either(tag.Not, "m.tags && @matchTags::text[]")})", "matchTags", tag.Values.ToArray());
        // A project's members: the memories in it, and those of its scope tagged with one of its aliases, which a project
        // claims without rewriting them (upstream's membership by tag, SB-P3-07); a held project has none.
        if (Project is { } project)
            Add($"""
                (m.hold IS NULL AND {Either(project.Not, $"""
                  EXISTS(SELECT 1 FROM memory_projects p WHERE {MemoryStore.ProjectsNamed("@matchProjects")} AND NOT p.held AND (p.id=m.project_id
                    OR p.subject_id=m.subject_id AND m.tags && ARRAY(SELECT a.alias FROM memory_project_aliases a WHERE a.project_id=p.id)))
                  """)})
                """, "matchProjects", project.Values.ToArray());
        // Work items by the properties they have now (079), exactly, as list_work's filters are.
        if (Work == false) where.Add("NOT EXISTS(SELECT 1 FROM memory_work w WHERE w.memory_id=m.id)");
        else if (Worked)
        {
            var tests = new List<string> { "w.memory_id=m.id" };
            if (WorkType is { } type) { tests.Add(Either(type.Not, "w.work_type=ANY(@matchTypes::text[])")); args.Add(("matchTypes", type.Values.ToArray())); }
            if (State is { } state) { tests.Add(Either(state.Not, "w.state=ANY(@matchStates::text[])")); args.Add(("matchStates", state.Values.ToArray())); }
            if (Priority is { } priority) { tests.Add(Either(priority.Not, "coalesce(w.priority,0)=ANY(@matchPriorities::int[])")); args.Add(("matchPriorities", priority.Values.ToArray())); }
            where.Add($"EXISTS(SELECT 1 FROM memory_work w WHERE {string.Join(" AND ", tests)})");
        }
    }
}

// One memory a match found for a feed: as a list serves it (MemoryStore.Select, with its preview), and for a work item
// whether it is blocked and the unfinished work items it waits on (080's memory_work_waiting), first by priority, at most
// MemoryStore.BlockedByMax, and how far all the work items it depends on have come (084's memory_work_depends).
public sealed record MemoryMatched(MemoryRecord Memory, bool Blocked, IReadOnlyList<MemoryWorkDependency> BlockedBy,
    MemoryWorkDepends? Depends = null);

// What a feed's match found: how many memories match in all, the newest `limit` of them, newest first; or, when it names
// a project the reader doesn't read, those projects and nothing else.
public sealed record MemoryMatches(int Count, IReadOnlyList<MemoryMatched> Items, IReadOnlyList<string> UnknownProjects);

public sealed partial class MemoryStore
{
    // The most memories one feed reads, and the status of a memory marked wrong (D-030).
    public const int MatchMax = 1000;
    public const string Deprecated = "deprecated";

    // A project named by its slug or one of its aliases, any of the text[] `names`.
    internal static string ProjectsNamed(string names) =>
        $"(p.slug=ANY({names}::text[]) OR EXISTS(SELECT 1 FROM memory_project_aliases a WHERE a.project_id=p.id AND a.alias=ANY({names}::text[])))";

    // The names among `names` that are no project the caller reads: a project counts only in a scope the caller reads,
    // through the same predicate as the memories in it, and never while it is held (SB-P3-21).
    public async Task<IReadOnlyList<string>> UnknownProjects(Guid principal, IReadOnlyList<string> names, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        return await UnknownProjects(c, principal, names, ct);
    }
    static async Task<IReadOnlyList<string>> UnknownProjects(NpgsqlConnection c, Guid principal, IReadOnlyList<string> names, CancellationToken ct)
    {
        if (names.Count == 0) return [];
        await using var cmd = Database.Command(c, null, $"""
            SELECT n.name FROM unnest(@names::text[]) WITH ORDINALITY n(name,o)
            WHERE NOT EXISTS(SELECT 1 FROM memory_projects p WHERE {ProjectsNamed("ARRAY[n.name]")} AND {ProjectListed()}) ORDER BY n.o
            """, ("principal", principal), ("names", names.ToArray()));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var unknown = new List<string>();
        while (await reader.ReadAsync(ct)) unknown.Add(reader.GetString(0));
        return unknown;
    }

    // The projects `names` name (a slug or an alias each) that the caller reads, as a feed's project filter finds them: the
    // same predicates, so a name may stand for a project in each scope the caller reads, and never a held one. Each with
    // its subject (for its star, MemoryStars.ProjectKey) and name, by name.
    public async Task<IReadOnlyList<(Guid Id, string Subject, string Name)>> NamedProjects(Guid principal, IReadOnlyList<string> names,
        CancellationToken ct)
    {
        if (names.Count == 0) return [];
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = Database.Command(c, null, $"""
            SELECT p.id,p.subject_id,p.name FROM memory_projects p WHERE {ProjectsNamed("@names")} AND {ProjectListed()}
            ORDER BY lower(p.name),p.slug,p.subject_id
            """, ("principal", principal), ("names", names.ToArray()));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var named = new List<(Guid, string, string)>();
        while (await reader.ReadAsync(ct)) named.Add((reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
        return named;
    }

    // A dashboard's memories feed (DashboardFeeds): the memories the caller reads that match, through the list's own
    // predicates (Filtered) and never one held for review, newest first as the list orders them, at most `limit` with a
    // preview of `previewChars` as the list cuts it, and how many match in all, in one statement. A project the caller
    // doesn't read answers with its name and no memories.
    public async Task<MemoryMatches> Matching(Guid principal, MemoryMatch match, int limit, int previewChars, CancellationToken ct)
    {
        if (limit is < 1 or > MatchMax) throw new MemoryRefused("invalid", $"limit is 1 to {MatchMax}");
        if (previewChars is < 0 or > MemoryInput.PreviewMax) throw new MemoryRefused("invalid", $"A preview is 0 to {MemoryInput.PreviewMax} characters");
        await using var c = await source.OpenConnectionAsync(ct);
        if (await UnknownProjects(c, principal, match.Projects, ct) is { Count: > 0 } unknown) return new(0, [], unknown);
        var (where, args) = await Filtered(c, principal, new MemoryFilter(), ct, match);
        where.Add("m.hold IS NULL");
        args.AddRange([("limit", limit), ("chars", previewChars), ("blockedMax", BlockedByMax)]);
        await using var cmd = Database.Command(c, null, $"""
            WITH hit AS (SELECT m.id,count(*) OVER () AS total {From} WHERE {string.Join(" AND ", where)} ORDER BY m.created_at DESC,m.id DESC LIMIT @limit)
            {Select("@chars", Blocking)} JOIN hit h ON h.id=m.id ORDER BY m.created_at DESC,m.id DESC
            """, [.. args]);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var items = new List<MemoryMatched>();
        var total = 0;
        while (await reader.ReadAsync(ct))
        {
            total = (int)reader.GetInt64(32);
            items.Add(new(Record(reader), reader.GetBoolean(33), Dependencies(reader.GetString(34)), MemoryWorkDepends.From(reader.GetString(35))));
        }
        return new(total, items, []);
    }

    // The columns Matching adds after Select's: the whole count, whether the item is blocked (open and waiting on any),
    // what it waits on, and how far what it depends on has come.
    const string Blocking = $"""
        ,h.total,
          coalesce((SELECT k.state NOT IN ('done','cancelled') FROM memory_work k WHERE k.memory_id=m.id),false) AND EXISTS(SELECT 1 FROM memory_work_waiting(m.id)),
          (SELECT coalesce(jsonb_agg(jsonb_build_object('id',d.id,'type',d.work_type,'state',d.state,'priority',d.priority)
            ORDER BY coalesce(d.priority,5),d.created_at,d.id),'[]'::jsonb)::text
           FROM (SELECT * FROM memory_work_waiting(m.id) x ORDER BY coalesce(x.priority,5),x.created_at,x.id LIMIT @blockedMax) d),
          {DependsCounted}
        """;
}
