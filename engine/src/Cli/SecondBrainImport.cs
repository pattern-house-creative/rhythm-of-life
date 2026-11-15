using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Knowledge.Adapters.SecondBrain;
using Knowledge.Relationships;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge.Cli;

// The Second Brain export importer (SB-P8-01; story 16; D-035, D-037, D-048, D-049, D-120 to D-124; docs/dev/memory.md
// sections 9 and 10.3): `rhythm import-second-brain`, the operator's
// step on the host, only with memory.secondBrainImport on. It reads one file a Second Brain service exported (upstream's
// GET /export, src/routes/entries.ts at the pinned commit: version 3, and the older shapes upstream's importer still
// reads, version 2 and a file with no version, which carry no projects, no updated_at and no validity window), never
// the service itself, and writes only through migration 059's checked functions, as the operator. A dry run, the
// default, does everything in one transaction and rolls it back, so its report is the one a real import would give.
//
// What the export holds is one member's readable set: memories (id, text, tags, source, dates, counters, validity),
// edges and projects. What it leaves out (Missing) the report names each time, with what the import does instead.
//   Scopes. The file says nothing of which workspace a memory was in, so each lands in the importing person's private
//     scope unless the owner's reviewed scope map (SecondBrainScopeMap) names it for Family (D-049); with memory scopes
//     off a private one is refused (D-048). A project lands in Family when the map names it, or when the memories of
//     this file that name it all land in Family (upstream's rule: a memory's project is one of its own workspace's);
//     else privately. The author of each is the importing person (the file names none).
//   Ids. Each record gets a new native id; its own goes into the origin map under second-brain:default (MemoryKey,
//     ProjectKey, LinkKey), so a repeated import, or another member's file holding the same Family memories, adds
//     nothing again. Nothing is matched by title or text.
//   Tags. Kind, status, volatility, capsule slots and project membership are Rhythm's own fields, filled from the tags
//     that carry them; a quarantine: tag holds the memory (D-120); the other tags in a namespace Rhythm's engine sets
//     itself are kept beside the memory as data (D-121; migration 059's memory_upstream_tags); the rest are its tags.
//   Projects. A slug repeated within one file is two projects (D-123): the first of a scope keeps it, a later one takes
//     the slug with -2 (and so on), as does one whose slug an earlier project of the file kept as an alias, so two
//     projects of one file are never joined by a name; a project tag then names whichever is the only one of its slug
//     in the memory's scope, and no project when two are, which the report lists for the scope map. A project here
//     that is held for review (046) is joined by nothing, and a new one whose names read like instructions comes in
//     held, its memories with it (D-037).
//   Links. An edge whose end the file does not hold is skipped and counted, its foreign id kept nowhere (D-122); one
//     whose ends landed in two scopes is skipped (two scopes never link), and so is one with an end an earlier import
//     put in a private scope this file does not mean (listed for review, never counted under that scope's name); the
//     rest become made links, verified, inferred or suggested by upstream's provenance (LinkTypes.Verification),
//     upstream's weight as their confidence. A link has no maker here and is marked imported, which the person's views
//     say (MemoryConnection.Imported).
//   Holds. Every memory is screened as any write is (036), the importer's channel scored as an AI client's (D-037), so
//     a held row stays held and one upstream served is held here when its words read like instructions.
//   Commitments. Upstream's owed-to-me and counterparty: tags, kept beside the memory, also make its commitment (058):
//     owed to the person, or owed by them to the one the counterparty: tag names, so its open loop lists that way.
// The report counts, per scope, what was imported, held (by reason) and skipped (by reason), and names no record's words.
// Its notes and review lines are about what this run adds: a record imported before, or refused, is counted only by its
// outcome, so a repeated import notes none of them again (what it says of the file and the scope map, such as a slug the
// file holds twice, it says each time).
//   The household import (--household; SB-P8-08, D-242, D-247; migration 074), only while memory scopes are off: D-048
//     set aside, every memory and project lands in Family, never refused, the importing person the author; each memory
//     keeps where it lived in Second Brain with its origin (Lived), and the report counts them by that too.
public sealed class SecondBrainExportInvalid(string message) : Exception(message);
// An import the install does not take as asked, before anything is written: the household import with scopes on.
public sealed class SecondBrainImportRefused(string message) : Exception(message);

// The owner's reviewed scope map (D-049): which memories (by their id in the file) and which projects (by slug, or by
// slug and creation time, ProjectKey, where one file holds a slug twice) go to Family. Everything else lands private.
//   { "format": "rhythm-second-brain-scope-map", "version": 1, "family": { "memories": [...], "projects": [...] } }
public sealed record SecondBrainScopeMap(IReadOnlySet<string> Memories, IReadOnlySet<string> Projects)
{
    public const string Format = "rhythm-second-brain-scope-map";
    public const int Version = 1;
    public static readonly SecondBrainScopeMap None = new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

    public static SecondBrainScopeMap Read(JsonDocument file)
    {
        var root = file.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String
            || format.GetString() != Format)
            throw new SecondBrainExportInvalid($"the scope map is not one ({Format})");
        if (!root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var v) || v != Version)
            throw new SecondBrainExportInvalid($"the scope map is of another version: this one reads version {Version}");
        foreach (var p in root.EnumerateObject())
            if (p.Name is not ("format" or "version" or "family")) throw new SecondBrainExportInvalid($"the scope map has a field it does not take: {p.Name}");
        if (!root.TryGetProperty("family", out var family)) return None;
        if (family.ValueKind != JsonValueKind.Object) throw new SecondBrainExportInvalid("the scope map's family is a list of memories and projects");
        HashSet<string> List(string name)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (!family.TryGetProperty(name, out var list)) return set;
            if (list.ValueKind != JsonValueKind.Array || list.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String || e.GetString()!.Length == 0))
                throw new SecondBrainExportInvalid($"the scope map's family {name} is a list of ids");
            foreach (var e in list.EnumerateArray()) set.Add(e.GetString()!);
            return set;
        }
        foreach (var p in family.EnumerateObject())
            if (p.Name is not ("memories" or "projects")) throw new SecondBrainExportInvalid($"the scope map's family has a field it does not take: {p.Name}");
        return new(List("memories"), List("projects"));
    }
}

// One record of the file as read. Problem: why the importer does not take it (its reason in the report), or null.
public sealed record SbFileEntry(int Index, string? Id, string? Content, IReadOnlyList<string> Tags, string? Source, long? CreatedAt, long? UpdatedAt,
    int? Importance, long? ValidFrom, long? ValidUntil, string? Problem);
public sealed record SbFileEdge(int Index, string? From, string? To, string Type, double? Weight, string? Provenance, long? CreatedAt, string? Problem);
public sealed record SbFileProject(int Index, string? Slug, string? Name, string? Description, IReadOnlyList<string> Aliases, string? Status,
    long? CreatedAt, long? UpdatedAt, string? Problem);

// The file: its version (0 for one that names none), when it was taken, its checksum and the export id derived from it,
// and the three lists. Projects, updated_at and the validity window are there only from the versions that carry them.
public sealed partial record SbFile(int Version, DateTimeOffset ExportedAt, string Sha256, Guid Export, IReadOnlyList<SbFileEntry> Entries,
    IReadOnlyList<SbFileEdge> Edges, IReadOnlyList<SbFileProject> Projects, bool HasProjects, bool HasUpdatedAt, bool HasValidity)
{
    public static readonly int[] Versions = [0, 1, 2, 3];

    static string? Text(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static bool Present(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;
    static long? Millis(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d)
        && d >= -62_135_596_800_000 && d <= 253_402_300_799_999 ? (long)Math.Floor(d) : null;
    static bool BadNumber(JsonElement o, string name) => Present(o, name) && (o.GetProperty(name).ValueKind != JsonValueKind.Number || Millis(o, name) is null);

    public static SbFile Read(byte[] bytes)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(bytes); }
        catch (JsonException) { throw new SecondBrainExportInvalid("not a whole JSON file"); }
        // Half a surrogate pair outside any row (a row holding one is refused on its own, Entry) cannot be read as text.
        try { return Read(doc, bytes); }
        catch (InvalidOperationException) { throw new SecondBrainExportInvalid("it holds text that cannot be read (half a surrogate pair)"); }
        finally { doc.Dispose(); }
    }

    static SbFile Read(JsonDocument doc, byte[] bytes)
    {
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new SecondBrainExportInvalid("not a Second Brain export (an object with entries)");
            if (root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String && format.GetString() == MemoryArchive.Format)
                throw new SecondBrainExportInvalid("a Rhythm export: rhythm import reads it");
            var version = 0;
            if (Present(root, "version"))
            {
                var v = root.GetProperty("version");
                if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out version) || !Versions.Contains(version))
                    throw new SecondBrainExportInvalid("an export of a version this importer does not read: it reads version 3 and older");
            }
            if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                throw new SecondBrainExportInvalid("not a Second Brain export: it has no entries list");
            if (Millis(root, "exported_at") is not { } at) throw new SecondBrainExportInvalid("it does not say when it was taken (exported_at)");
            if (Present(root, "edges") && root.GetProperty("edges").ValueKind != JsonValueKind.Array)
                throw new SecondBrainExportInvalid("its edges are not a list");
            if (Present(root, "projects") && root.GetProperty("projects").ValueKind != JsonValueKind.Array)
                throw new SecondBrainExportInvalid("its projects are not a list");

            var list = entries.EnumerateArray().Select((e, i) => Entry(i, e)).ToList();
            var edges = Present(root, "edges") ? root.GetProperty("edges").EnumerateArray().Select((e, i) => Edge(i, e)).ToList() : [];
            var projects = Present(root, "projects") ? root.GetProperty("projects").EnumerateArray().Select((p, i) => Project(i, p)).ToList() : [];
            var objects = entries.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
            var sha = Hashing.Hash(bytes);
            return new(version, DateTimeOffset.FromUnixTimeMilliseconds(at), sha, ExportId(sha), list, edges, projects, Present(root, "projects"),
                objects.Any(e => Present(e, "updated_at")), objects.Any(e => e.TryGetProperty("valid_from", out _) || e.TryGetProperty("valid_until", out _)));
        }
    }

    // The export's id in the origin map: the first half of the file's SHA-256, so one file is always one export.
    public static Guid ExportId(string sha256) => Guid.ParseExact(sha256[..8] + "-" + sha256[8..12] + "-" + sha256[12..16] + "-" + sha256[16..20] + "-" + sha256[20..32], "D");

    // A row as upstream's importer checks it (src/entries/import.ts parseEntryRow): an id and some text are required,
    // tags a list of words, numbers numbers. A row upstream would refuse is not taken here either.
    static SbFileEntry Entry(int i, JsonElement e)
    {
        // A row holding text no database text holds (a NUL, half a surrogate pair) is one this importer cannot read, refused as
        // invalid with the rest of the file read (MemoryArchive.Storable; SB-P7-10).
        if (e.ValueKind != JsonValueKind.Object || !MemoryArchive.Storable(e)) return new(i, null, null, [], null, null, null, null, null, null, "invalid");
        var id = Text(e, "id")?.Trim();
        var content = Text(e, "content");
        string? problem = null;
        if (string.IsNullOrEmpty(id) || content is null || content.Trim().Length == 0) problem = "invalid";
        var tags = new List<string>();
        if (Present(e, "tags"))
        {
            if (e.GetProperty("tags").ValueKind != JsonValueKind.Array || e.GetProperty("tags").EnumerateArray().Any(t => t.ValueKind != JsonValueKind.String))
                problem ??= "invalid";
            else tags.AddRange(e.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!));
        }
        if (Present(e, "source") && e.GetProperty("source").ValueKind != JsonValueKind.String) problem ??= "invalid";
        foreach (var name in new[] { "created_at", "updated_at", "valid_from", "valid_until", "recall_count", "importance_score",
                     "contradiction_wins", "contradiction_losses" })
            if (BadNumber(e, name)) problem ??= "invalid";
        int? importance = Present(e, "importance_score") && e.GetProperty("importance_score").TryGetDouble(out var imp)
            && imp >= 1 && imp <= 5 && imp == Math.Floor(imp) ? (int)imp : null;
        return new(i, id, content, tags, Text(e, "source"), Millis(e, "created_at"), Millis(e, "updated_at"), importance,
            Millis(e, "valid_from"), Millis(e, "valid_until"), problem);
    }

    static SbFileEdge Edge(int i, JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !MemoryArchive.Storable(e)) return new(i, null, null, "relates_to", null, null, null, "invalid");
        var from = Text(e, "source_id")?.Trim();
        var to = Text(e, "target_id")?.Trim();
        var type = Text(e, "type")?.Trim() is { Length: > 0 } t ? t : "relates_to";
        string? problem = string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || from == to ? "invalid" : null;
        if (BadNumber(e, "created_at") || Present(e, "weight") && (e.GetProperty("weight").ValueKind != JsonValueKind.Number
            || !e.GetProperty("weight").TryGetDouble(out var w) || !double.IsFinite(w))) problem ??= "invalid";
        double? weight = Present(e, "weight") && e.GetProperty("weight").TryGetDouble(out var x) && double.IsFinite(x) ? Math.Clamp(x, 0, 1) : null;
        return new(i, from, to, type, weight, Text(e, "provenance"), Millis(e, "created_at"), problem);
    }

    // As upstream's parseImportedProject: a slug id, a name, a description, aliases, active or archived.
    static SbFileProject Project(int i, JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !MemoryArchive.Storable(p)) return new(i, null, null, null, [], null, null, null, "invalid");
        var slug = Text(p, "id");
        var name = Text(p, "name");
        var status = Present(p, "status") ? Text(p, "status") : "active";
        string? problem = slug is null || !Slug().IsMatch(slug) || name is null || name.Trim().Length is 0 or > 120 || name.Contains('\0')
            || status is not ("active" or "archived") || BadNumber(p, "created_at") || BadNumber(p, "updated_at") ? "invalid" : null;
        var aliases = new List<string>();
        if (Present(p, "aliases"))
        {
            if (p.GetProperty("aliases").ValueKind != JsonValueKind.Array || p.GetProperty("aliases").EnumerateArray().Any(a => a.ValueKind != JsonValueKind.String))
                problem ??= "invalid";
            else aliases.AddRange(p.GetProperty("aliases").EnumerateArray().Select(a => a.GetString()!.Trim().ToLowerInvariant()).Where(a => a.Length > 0).Distinct());
        }
        var description = Present(p, "description") ? Text(p, "description") : "";
        if (description is null) problem ??= "invalid";
        return new(i, slug, name?.Trim(), description?.Trim(), aliases, status, Millis(p, "created_at"), Millis(p, "updated_at"), problem);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")] private static partial Regex Slug();
}

// What one run did, or would do (a dry run): for each kind (memory, project, link), each scope it landed in (a person's
// id, or Family) and each outcome (added, joined, present, imported_before, skipped:<why>, refused:<why>), a count; the
// memories it added held for review, by scope and Rhythm's reason, and by the reason the file gave (or "screening" for
// one Rhythm's own screening held); what the file leaves out and what it carries that is not kept; and what the owner
// should review. Memories maps each memory of the file (by its id there) to the memory here it came in as, or was
// imported as before; it is for tests and the comparison harness, and never printed. Household says it was the household
// import; Workspaces then counts the file's memories by where they lived in Second Brain (personal, team, unknown) and
// outcome, with held for those added held.
public sealed record SecondBrainImportReport(string Person, bool ScopesOn, bool Wrote, int Version, string Sha256, Guid Export, DateTimeOffset ExportedAt,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>> Outcomes,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Held, IReadOnlyDictionary<string, int> HeldFrom,
    IReadOnlyList<(string Field, string Instead)> Missing, IReadOnlyList<(string Field, string Instead)> NotKept,
    IReadOnlyDictionary<string, int> Notes, IReadOnlyList<string> Review, IReadOnlyDictionary<string, Guid> Memories,
    IReadOnlyDictionary<string, Guid> Projects, bool Household = false,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>? Workspaces = null)
{
    public const string FamilyScope = "Family";
    public int Count(string kind, string scope, string outcome) =>
        Outcomes.TryGetValue(kind, out var s) && s.TryGetValue(scope, out var o) && o.TryGetValue(outcome, out var n) ? n : 0;
    public int Count(string kind, string outcome) =>
        Outcomes.TryGetValue(kind, out var s) ? s.Values.Sum(o => o.TryGetValue(outcome, out var n) ? n : 0) : 0;
    public int Note(string name) => Notes.TryGetValue(name, out var n) ? n : 0;
    public int Lived(string workspace, string outcome) =>
        Workspaces is { } w && w.TryGetValue(workspace, out var o) && o.TryGetValue(outcome, out var n) ? n : 0;
}

public sealed partial class SecondBrainImport(NpgsqlDataSource source)
{
    // The origin every Second Brain record is kept under in the origin map: one deployment's, since the export names none.
    public const string Origin = "second-brain:default";
    public const string Client = "rhythm import-second-brain";
    // The scope an edge is counted under when neither of its ends is in the file.
    public const string NoScope = "no scope";
    // Where a memory lived in Second Brain, as the household import keeps it (074): the login's personal workspace, a team
    // workspace, or not known here.
    public const string Personal = "personal", Team = "team", Unknown = "unknown";
    public const string HouseholdWithScopesOn = "--household is for an install with memory scopes off; with them on, a person's own memories "
        + "land in their private scope (D-049), so import without it";

    // Upstream's five hold reasons (src/quarantine/tags.ts) and the ones Rhythm holds the same way (MemoryHolds).
    public static readonly string[] UpstreamHolds = ["instruction", "hidden", "burst", "capsule", "too_long"];
    static readonly string[] SameHolds = [MemoryHolds.Instruction, MemoryHolds.Hidden, MemoryHolds.Burst, MemoryHolds.TooLong];

    // A memory's id in the origin map: as the file gives it when the map's alphabet takes it, else its SHA-256 (D-124).
    public static string MemoryKey(string id) => SourceId().IsMatch(id) ? id : "sha256:" + Hashing.Hash(id);
    // A project's: its slug and when it was made, since one file can hold one slug from two workspaces (D-123).
    public static string ProjectKey(string slug, long? createdAt) => createdAt is { } t ? $"{slug}:{t.ToString(CultureInfo.InvariantCulture)}" : slug;
    // An edge's: its type and ends, hashed (an edge has no id in the export).
    public static string LinkKey(string type, string from, string to) => "sha256:" + Hashing.Hash($"{type}\n{from}\n{to}");

    // What upstream's export leaves out (fixtures.md, What upstream's export leaves out), and what the import does
    // instead; then what an older version leaves out besides.
    public static readonly IReadOnlyList<(string Field, string Instead)> AlwaysMissing =
    [
        ("memory workspace_id", "every memory lands in the importing person's private scope, Family only through the reviewed scope map (D-049)"),
        ("memory actor_id", "the importing person is the author; the original id is kept in the origin map"),
        ("memory when_at, when_kind, when_source, when_label", "due dates and event times are not restored; the tags that mark commitments, decisions and standing instructions are kept"),
        ("memory staleness_checked_at", "not restored; a stale: tag is kept beside the memory"),
        ("memory vector_ids", "none needed: the index makes passages again from the text"),
        ("edge id", "a link is known by its type and ends, hashed into the origin map"),
        ("edge metadata", "not restored"),
        ("edge updated_at", "not restored"),
        ("edge workspace_id", "an edge whose end the file does not hold is skipped (D-122); one across two scopes is skipped"),
        ("project workspace_id", "a project lands where the scope map or its memories say; a slug held twice is two projects (D-123)"),
        ("users, workspaces, memberships", "not imported: Rhythm's people and Family are its own"),
        ("entry_versions", "history starts at the import, which says earlier changes were not recorded (VER-11)"),
        ("entry_events", "not imported: the audit trail here starts with the import's own row"),
        ("entries_trash", "memories in the trash are not exported and not imported"),
        ("prompt_capsule_revisions, insight_candidates, recall_log, admin_events", "not imported"),
    ];
    // What the household import does instead, for the two rows above that say where a record lands.
    static readonly IReadOnlyDictionary<string, string> HouseholdInstead = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["memory workspace_id"] = "with --household every memory lands in Family; where it lived is kept with its origin, from the Second Brain connector's rows or the scope map, else as unknown (D-247)",
        ["project workspace_id"] = "with --household every project lands in Family; a slug held twice is two projects (D-123)",
    };
    static readonly (string, string) NoProjects = ("projects", "a file older than upstream's 3.4 has none: a project tag joins a project of its slug here, if there is one, else it is kept beside the memory");
    static readonly (string, string) NoUpdatedAt = ("memory updated_at", "a file older than upstream's 4.0 has none: it is taken as the memory's creation time");
    static readonly (string, string) NoValidity = ("memory valid_from, valid_until", "a file older than upstream's 4.0 has none: each memory is taken as true since it was written");
    public static readonly IReadOnlyList<(string Field, string Instead)> InFileNotKept =
    [
        ("memory recall_count", "not kept: recalls are counted here from the import on (engine.recallLog)"),
        ("memory contradiction_wins, contradiction_losses", "not kept: Rhythm keeps no contradiction counters"),
        ("edge weight", "kept as the link's confidence"),
    ];

    sealed class Tally
    {
        public readonly SortedDictionary<string, SortedDictionary<string, SortedDictionary<string, int>>> Outcomes = new(StringComparer.Ordinal);
        public readonly SortedDictionary<string, SortedDictionary<string, int>> Held = new(StringComparer.Ordinal);
        public readonly SortedDictionary<string, int> HeldFrom = new(StringComparer.Ordinal);
        public readonly SortedDictionary<string, int> Notes = new(StringComparer.Ordinal);
        public readonly List<string> Review = [];
        public readonly SortedDictionary<string, SortedDictionary<string, int>> Lived = new(StringComparer.Ordinal);
        public void Add(string kind, string scope, string outcome)
        {
            var s = Outcomes.TryGetValue(kind, out var x) ? x : Outcomes[kind] = new(StringComparer.Ordinal);
            var o = s.TryGetValue(scope, out var y) ? y : s[scope] = new(StringComparer.Ordinal);
            o[outcome] = o.GetValueOrDefault(outcome) + 1;
        }
        public void Live(string? workspace, string outcome)
        {
            if (workspace is null) return;
            var o = Lived.TryGetValue(workspace, out var x) ? x : Lived[workspace] = new(StringComparer.Ordinal);
            o[outcome] = o.GetValueOrDefault(outcome) + 1;
        }
        public void Note(string name, int by = 1) { if (by > 0) Notes[name] = Notes.GetValueOrDefault(name) + by; }
    }

    // A memory's tags sorted into what Rhythm keeps in its own fields, what it keeps beside the memory, and its tags.
    internal sealed record Sorted(List<string> Tags, List<string> Upstream, string? Kind, string? Status, string? Volatility, string? Capsule,
        string? Slot, List<string> ProjectSlugs, List<string> Holds, int LeftOut);

    internal static Sorted Sort(IEnumerable<string> given)
    {
        var tags = new List<string>();
        var upstream = new List<string>();
        var slugs = new List<string>();
        var holds = new List<string>();
        string? kind = null, status = null, volatility = null, capsule = null, slot = null;
        var leftOut = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in given)
        {
            var t = raw.Trim().ToLowerInvariant();
            if (t.Length == 0 || !seen.Add(t)) continue;
            if (t.Length > MemoryInput.MaxTagLength || t.Any(char.IsControl)) { leftOut++; continue; }
            string Value(string prefix) => t[prefix.Length..];
            if (t.StartsWith("kind:", StringComparison.Ordinal) && kind is null && Value("kind:") is "episodic" or "semantic") kind = Value("kind:");
            else if (t.StartsWith("status:", StringComparison.Ordinal) && status is null && Value("status:") is "canonical" or "draft" or "deprecated") status = Value("status:");
            else if (t.StartsWith("volatility:", StringComparison.Ordinal) && volatility is null && Value("volatility:") is "durable" or "state" or "volatile")
                volatility = Value("volatility:");
            else if (t.StartsWith("project:", StringComparison.Ordinal) && Value("project:").Length > 0) slugs.Add(Value("project:"));
            else
            {
                if (t.StartsWith("capsule:", StringComparison.Ordinal) && capsule is null) capsule = Value("capsule:");
                if (t.StartsWith("capsule-slot:", StringComparison.Ordinal) && slot is null) slot = Value("capsule-slot:");
                if (t.StartsWith("quarantine:", StringComparison.Ordinal)) holds.Add(Value("quarantine:"));
                if (MemoryInput.Reserved(t)) upstream.Add(t);
                else tags.Add(t);
            }
        }
        if (tags.Count > MemoryInput.MaxTags) { leftOut += tags.Count - MemoryInput.MaxTags; tags = tags[..MemoryInput.MaxTags]; }
        return new(tags, upstream, kind, status, volatility, capsule, slot, slugs, holds, leftOut);
    }

    // The hold a memory comes in with (D-120): upstream's reason where Rhythm holds the same way, else instruction (an
    // AI tool's change to the core capsule, which Rhythm refuses outright rather than holds, D-172, and a quarantine: tag
    // of a reason upstream no longer writes, such as review from before 4.0). Null when the file holds it for nothing.
    internal static (string? Native, string? Upstream) Hold(IReadOnlyList<string> holds)
    {
        if (holds.Count == 0) return (null, null);
        foreach (var reason in holds) if (SameHolds.Contains(reason)) return (reason, reason);
        return (MemoryHolds.Instruction, holds[0]);
    }

    static readonly string[] CoreSlots = ["identity", "preferences", "constraints", "principles"];
    // The most aliases a project takes (035).
    const int MaxAliases = 16;
    static readonly string[] ProjectSlots = ["current-state", "decisions", "open-questions"];

    // A time as the database takes it, to the millisecond upstream keeps, in UTC.
    static string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    // What the origin map holds of this origin, with where each record is here now (none when it is gone).
    sealed record Known(Guid Local, string? Subject, string? Text);

    public Task<SecondBrainImportReport> Import(SbFile file, string person, SecondBrainScopeMap map, bool write, CancellationToken ct) =>
        Import(file, person, map, write, false, ct);

    // household: the household import (D-247), refused with memory scopes on before anything is written.
    public async Task<SecondBrainImportReport> Import(SbFile file, string person, SecondBrainScopeMap map, bool write, bool household, CancellationToken ct)
    {
        var tally = new Tally();
        string OriginJson(string? workspace)
        {
            var origin = new Dictionary<string, string>
            {
                ["origin"] = Origin, ["export"] = file.Export.ToString(), ["exportedAt"] = Iso(file.ExportedAt.ToUnixTimeMilliseconds()),
            };
            if (workspace is not null) origin["workspace"] = workspace;
            return JsonSerializer.Serialize(origin);
        }
        var originJson = OriginJson(null);
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        bool scopes;
        await using (var mode = Database.Command(c, tx, "SELECT scopes_on()")) scopes = (bool)(await mode.ExecuteScalarAsync(ct))!;
        if (household && scopes) throw new SecondBrainImportRefused(HouseholdWithScopesOn);
        string Scope(string subject) => subject == RhythmConfig.Household ? SecondBrainImportReport.FamilyScope : subject;

        // Where each memory lived in Second Brain, for the household import only: what the read-only connector's rows say
        // of it by its Second Brain id (its personal workspace's memories private, a team workspace's shared; the current
        // row first, then the newest), else a team workspace when the scope map names it for Family, else unknown. Read
        // only: nothing here writes a connector's row.
        var lived = new Dictionary<string, string>(StringComparer.Ordinal);
        if (household)
            await using (var cmd = Database.Command(c, tx, """
                SELECT DISTINCT ON (r.relationship_snapshot->'Key'->>'ExternalId') r.relationship_snapshot->'Key'->>'ExternalId',r.scope
                FROM resources r WHERE r.provider=@provider AND r.kind='memory' AND r.relationship_snapshot->'Key'->>'ExternalId' IS NOT NULL
                ORDER BY r.relationship_snapshot->'Key'->>'ExternalId',r.status='current' DESC,r.indexed_at DESC
                """, ("provider", SecondBrainAdapter.Provider)))
            await using (var row = await cmd.ExecuteReaderAsync(ct))
                while (await row.ReadAsync(ct)) lived[row.GetString(0)] = row.GetString(1) == "shared" ? Team : Personal;
        string? Lived(string? id) => !household ? null : id is null ? Unknown
            : lived.TryGetValue(id, out var w) ? w : map.Memories.Contains(id) ? Team : Unknown;

        // What this origin brought before, and where it is now.
        var known = new Dictionary<(string Kind, string Key), Known>();
        await using (var cmd = Database.Command(c, tx, """
            SELECT o.kind,o.source_id,o.local_id,coalesce(m.subject_id,p.subject_id),encode(sha256(convert_to(m.content,'UTF8')),'hex')
            FROM memory_origins o LEFT JOIN memories m ON o.kind='memory' AND m.id=o.local_id LEFT JOIN memory_projects p ON o.kind='project' AND p.id=o.local_id
            WHERE o.origin=@origin
            """, ("origin", Origin)))
        await using (var row = await cmd.ExecuteReaderAsync(ct))
            while (await row.ReadAsync(ct))
                known[(row.GetString(0), row.GetString(1))] = new(row.GetGuid(2), row.IsDBNull(3) ? null : row.GetString(3), row.IsDBNull(4) ? null : row.GetString(4));
        // The projects here, by scope, with their slugs, and every name they answer to (slug and aliases), for a slug the
        // file lists no project of; never a held one (046), which no import's memory goes into.
        var slugsHere = new Dictionary<Guid, string>();
        var here = new List<(Guid Id, string Subject, string Name)>();
        await using (var cmd = Database.Command(c, tx, """
            SELECT p.id,p.subject_id,p.slug,true FROM memory_projects p WHERE NOT p.held
            UNION ALL SELECT a.project_id,a.subject_id,a.alias,false FROM memory_project_aliases a JOIN memory_projects p ON p.id=a.project_id
            WHERE NOT p.held
            """))
        await using (var row = await cmd.ExecuteReaderAsync(ct))
            while (await row.ReadAsync(ct))
            {
                here.Add((row.GetGuid(0), row.GetString(1), row.GetString(2)));
                if (row.GetBoolean(3)) slugsHere[row.GetGuid(0)] = row.GetString(2);
            }

        async Task<JsonElement> Call(string sql, string record, bool? family, string? origin = null)
        {
            await using var cmd = Database.Command(c, tx, sql, ("person", person), ("record", record));
            cmd.Parameters["record"].NpgsqlDbType = NpgsqlDbType.Jsonb;
            cmd.Parameters.Add(new NpgsqlParameter("origin", NpgsqlDbType.Jsonb) { Value = origin ?? originJson });
            if (family is { } f) cmd.Parameters.AddWithValue("family", f);
            using var answer = JsonDocument.Parse((string)(await cmd.ExecuteScalarAsync(ct))!);
            return answer.RootElement.Clone();
        }
        static string Outcome(JsonElement a) => a.GetProperty("outcome").GetString() is "refused" ? "refused:" + a.GetProperty("reason").GetString()
            : a.GetProperty("outcome").GetString()!;
        static Guid? Id(JsonElement a) => a.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetGuid() : null;
        static string? Subject(JsonElement a) => a.TryGetProperty("subject", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;

        // Where each memory lands: Family when the scope map names it, or in the household import, else the person's
        // private scope. Each memory's outcome is counted where it lived too (CountMemory), in the household import.
        void CountMemory(string scope, string? id, string outcome)
        {
            tally.Add("memory", scope, outcome);
            tally.Live(Lived(id), outcome);
        }
        var entries = new List<SbFileEntry>();
        var inFile = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in file.Entries)
        {
            var at = household || e.Id is not null && map.Memories.Contains(e.Id) ? SecondBrainImportReport.FamilyScope : person;
            if (e.Problem is not null || e.Id is null) { CountMemory(at, e.Id, "skipped:" + (e.Problem ?? "invalid")); continue; }
            if (!inFile.Add(e.Id)) { CountMemory(at, e.Id, "skipped:duplicate"); continue; }
            entries.Add(e);
        }
        bool Family(SbFileEntry e) => household || map.Memories.Contains(e.Id!);
        tally.Note("scope_map_memories_not_in_file", map.Memories.Count(id => !inFile.Contains(id)));
        var sorted = entries.ToDictionary(e => e.Id!, e => Sort(e.Tags), StringComparer.Ordinal);

        // Projects: where each lands, the slug it takes, and the projects each scope holds of each upstream slug.
        var bySlug = file.Projects.Where(p => p.Problem is null).GroupBy(p => p.Slug!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var resolution = new Dictionary<(string Subject, string Slug), HashSet<Guid>>();
        var projectIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var occupied = new HashSet<(string Subject, string Slug)>();
        var refused = new HashSet<(string Subject, string Slug)>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in map.Projects.Where(n => !file.Projects.Any(p => p.Problem is null && (n == p.Slug || n == ProjectKey(p.Slug!, p.CreatedAt)))))
            tally.Note("scope_map_projects_not_in_file");
        foreach (var name in map.Projects.Where(n => bySlug.GetValueOrDefault(n) > 1)) tally.Review.Add($"the scope map names the project slug {name}, which this file holds more than once: name it with its creation time ({name}:<created_at>)");
        foreach (var p in file.Projects)
        {
            if (p.Problem is not null) { tally.Add("project", person, "skipped:" + p.Problem); continue; }
            var key = ProjectKey(p.Slug!, p.CreatedAt);
            if (!keys.Add(key)) { tally.Add("project", person, "skipped:duplicate"); continue; }
            // Family when the scope map names it (by its slug, if the file holds that slug once, or by its key), or when
            // the memories of this file that name it all land in Family; else the person's. One brought before stays
            // where it is.
            var named = map.Projects.Contains(key) || map.Projects.Contains(p.Slug!) && bySlug[p.Slug!] == 1;
            var members = entries.Where(e => sorted[e.Id!].ProjectSlugs.Contains(p.Slug!)).ToList();
            var family = household || named || bySlug[p.Slug!] == 1 && members.Count > 0 && members.All(Family);
            var subject = family ? RhythmConfig.Household : person;
            var meant = subject;
            var before = known.TryGetValue(("project", key), out var k) && k.Subject is not null ? k : null;
            if (before is not null) subject = before.Subject!;
            // The slug it takes here: upstream's, with an underscore as a hyphen, and with -2 (and so on) when another
            // project of this file in the same scope took it first, as its slug or an alias (D-123), so the two are never
            // joined by their name.
            var plain = p.Slug!.Replace('_', '-');
            var slug = plain;
            if (before is null)
                for (var n = 2; occupied.Contains((subject, slug)); n++)
                {
                    var suffix = "-" + n.ToString(CultureInfo.InvariantCulture);
                    slug = plain[..Math.Min(plain.Length, 64 - suffix.Length)] + suffix;
                }
            // The aliases Rhythm takes (035): another name than the slug it takes here, a plain tag of 1 to 128
            // characters, at most 16; the rest left out and counted, never the reason the project is refused.
            var aliases = p.Aliases.Where(x => x != slug && x.Length <= 128 && !x.Any(char.IsControl) && !MemoryInput.Reserved(x))
                .Distinct(StringComparer.Ordinal).Take(MaxAliases).ToList();
            var a = await Call("SELECT import_second_brain_project(@person,@family,@origin,@record)::text", JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["key"] = key, ["slug"] = slug, ["name"] = p.Name, ["description"] = p.Description, ["status"] = p.Status, ["aliases"] = aliases,
                ["createdAt"] = p.CreatedAt is { } made ? Iso(made) : null, ["updatedAt"] = p.UpdatedAt is { } changed ? Iso(changed) : null,
            }), subject == RhythmConfig.Household);
            var outcome = Outcome(a);
            var landed = Subject(a) ?? subject;
            // One another import placed in a private scope this file does not mean (another member's, or one made
            // without the scope map) is counted where this one meant it, naming no other scope, and listed for review;
            // one in Family, which this person reads, stays counted there.
            if (outcome == "imported_before" && landed != meant && landed != RhythmConfig.Household)
            {
                tally.Add("project", Scope(meant), "imported_before_elsewhere");
                tally.Review.Add($"project {key} was imported before into another scope than {Scope(meant)}: share it there, or correct the scope map");
            }
            else tally.Add("project", Scope(landed), outcome);
            // A changed slug and the aliases not taken are noted only for a project this import adds: one imported before,
            // joined or refused takes neither.
            if (outcome == "added")
            {
                if (slug != p.Slug) tally.Note("project_slugs_changed");
                tally.Note("project_aliases_not_taken", p.Aliases.Count - aliases.Count);
            }
            if (a.TryGetProperty("aliasesLeftOut", out var left) && left.TryGetInt32(out var leftOut)) tally.Note("project_aliases_left_out", leftOut);
            if (a.TryGetProperty("held", out var heldProject) && heldProject.ValueKind == JsonValueKind.True)
            {
                tally.Note("projects_held");
                tally.Review.Add($"project {key} ({Scope(landed)}) held for review: its name reads like instructions to an AI client");
            }
            if (Id(a) is not { } id)
            {
                if (outcome.StartsWith("refused:", StringComparison.Ordinal)) refused.Add((subject, p.Slug!));
                continue;
            }
            projectIds[key] = id;
            if (outcome == "added")
            {
                slugsHere[id] = slug;
                here.Add((id, landed, slug));
                foreach (var alias in aliases) occupied.Add((landed, alias));
            }
            occupied.Add((landed, slugsHere.GetValueOrDefault(id, slug)));
            occupied.Add((landed, slug));
            var set = resolution.TryGetValue((landed, p.Slug!), out var s) ? s : resolution[(landed, p.Slug!)] = [];
            set.Add(id);
        }
        if (bySlug.Any(s => s.Value > 1))
            foreach (var slug in bySlug.Where(s => s.Value > 1).Select(s => s.Key).Order(StringComparer.Ordinal))
                tally.Review.Add($"the project slug {slug} is in this file {bySlug[slug]} times (two workspaces' projects): each is its own project; "
                    + (household ? "with --household both land in Family, and a memory naming it goes into neither, its project: tag kept beside it" :$"name the Family one in the scope map by {slug}:<created_at>"));

        // Memories, oldest first as the file lists them.
        var memoryIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var memorySubjects = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            var s = sorted[e.Id!];
            var family = Family(e);
            var subject = family ? RhythmConfig.Household : person;
            var key = MemoryKey(e.Id!);
            if (e.Content!.Contains('\0')) { CountMemory(Scope(subject), e.Id, "skipped:invalid"); continue; }
            if (Encoding.UTF8.GetByteCount(e.Content) > MemoryInput.MaxBytes) { CountMemory(Scope(subject), e.Id, "skipped:too_large"); continue; }
            var created = e.CreatedAt ?? file.ExportedAt.ToUnixTimeMilliseconds();
            if (e.ValidUntil is { } until && until <= (e.ValidFrom ?? created)) { CountMemory(Scope(subject), e.Id, "skipped:window"); continue; }
            // What this memory's import notes and lists for review, kept until it is added: one imported before (by this
            // file or another) or refused adds nothing, so the report says only its outcome.
            var notes = new List<(string Name, int By)>();
            var review = new List<string>();
            if (key != e.Id) notes.Add(("ids_kept_as_their_sha256", 1));
            if (Encoding.UTF8.GetByteCount(e.Id!) > 40) notes.Add(("ids_over_upstreams_40_bytes", 1));
            if (e.CreatedAt is null) notes.Add(("created_at_missing", 1));

            // Its project: the only project of one of its slugs in its own scope, from this file or, for a slug the file
            // lists no project of, here already.
            Guid? project = null;
            var slugs = s.ProjectSlugs.Distinct(StringComparer.Ordinal).ToList();
            var unresolved = new List<string>();
            if (slugs.Count > 0)
            {
                var candidates = slugs.SelectMany(slug => resolution.TryGetValue((subject, slug), out var set) ? set
                    : bySlug.ContainsKey(slug) ? [] : here.Where(h => h.Subject == subject && h.Name == slug).Select(h => h.Id)).Distinct().ToList();
                if (candidates.Count == 1) project = candidates[0];
                else
                {
                    unresolved.AddRange(slugs);
                    notes.Add((candidates.Count > 1 ? "membership_ambiguous" : slugs.Any(slug => refused.Contains((subject, slug))) ? "membership_refused_project"
                        : slugs.Any(bySlug.ContainsKey) ? "membership_elsewhere" : "membership_unknown", 1));
                }
            }
            if (s.Status is null) notes.Add(("status_absent", 1));
            var (hold, heldFor) = Hold(s.Holds);
            if (s.Holds.Any(r => !UpstreamHolds.Contains(r))) notes.Add(("holds_of_a_reason_upstream_no_longer_writes", 1));
            // A capsule slot only for a memory not held, as Rhythm's capsules take one (a person's step, never an AI's).
            string? capsule = null, slot = null;
            if (s.Capsule is not null || s.Slot is not null)
            {
                if (hold is null && (s.Capsule == "core" && CoreSlots.Contains(s.Slot) || s.Capsule == "project" && project is not null && ProjectSlots.Contains(s.Slot)))
                    (capsule, slot) = (s.Capsule, s.Slot);
                else notes.Add(("capsule_slots_not_taken", 1));
            }
            var upstream = s.Upstream.Where(t => capsule is null || !t.StartsWith("capsule:", StringComparison.Ordinal) && !t.StartsWith("capsule-slot:", StringComparison.Ordinal))
                .Concat(unresolved.Select(slug => "project:" + slug)).Where(t => t.Length <= MemoryInput.MaxTagLength).Distinct(StringComparer.Ordinal).Take(MemoryInput.MaxTags).ToList();
            if (upstream.Any(t => t.StartsWith("standing:", StringComparison.Ordinal))) review.Add($"standing instruction: {key} ({Scope(subject)})");
            var source0 = e.Source?.Trim().ToLowerInvariant();
            var capture = source0 is { Length: > 0 } && source0 != MemoryStore.DigestSource && CaptureSource().IsMatch(source0) ? source0 : "second-brain";
            if (capture != source0) notes.Add(("sources_renamed", 1));
            notes.Add(("tags_left_out", s.LeftOut));
            var a = await Call("SELECT import_second_brain_memory(@person,@family,@origin,@record)::text", JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["key"] = key, ["content"] = e.Content, ["tags"] = s.Tags, ["upstream"] = upstream, ["kind"] = s.Kind, ["status"] = s.Status,
                ["importance"] = e.Importance, ["volatility"] = s.Volatility, ["source"] = capture, ["project"] = project?.ToString(),
                ["capsule"] = capsule, ["capsuleSlot"] = slot, ["hold"] = hold, ["createdAt"] = Iso(created),
                ["updatedAt"] = Iso(Math.Max(created, e.UpdatedAt ?? created)),
                ["validFrom"] = e.ValidFrom is { } from ? Iso(from) : null, ["validUntil"] = e.ValidUntil is { } end ? Iso(end) : null,
            }), family, household ? OriginJson(Lived(e.Id)) : null);
            var outcome = Outcome(a);
            var landed = Subject(a) ?? subject;
            // As for a project: one another import placed in a private scope this file does not mean is counted where this
            // file meant it, and listed.
            if (outcome == "imported_before" && landed != subject && landed != RhythmConfig.Household)
            {
                CountMemory(Scope(subject), e.Id, "imported_before_elsewhere");
                tally.Review.Add($"memory {key} was imported before into another scope than {Scope(subject)}: share it there, or correct the scope map");
            }
            else CountMemory(Scope(landed), e.Id, outcome);
            if (Id(a) is { } id)
            {
                memoryIds[e.Id!] = id;
                memorySubjects[e.Id!] = landed;
            }
            if (outcome == "imported_before" && known.TryGetValue(("memory", key), out var was) && was.Text is { } text && text != Hashing.Hash(e.Content))
                tally.Note("imported_before_with_other_text_here");
            if (outcome != "added") continue;
            foreach (var (name, by) in notes) tally.Note(name, by);
            tally.Review.AddRange(review);
            // The author of every memory it adds is the importing person (the export names none), so with scopes on a Family
            // one another member wrote upstream is that member's to change here only as a Family admin (Q-160). The file
            // does not say which those are, so the report counts every Family memory it adds.
            if (landed == RhythmConfig.Household) tally.Note("family_memories_authored_by_the_person");
            // A slot 036's screening left, the memory being held as it went in (059), is not taken; the rest are for review.
            if (capsule is not null)
            {
                if (a.TryGetProperty("slotDropped", out var dropped) && dropped.ValueKind == JsonValueKind.True) tally.Note("capsule_slots_not_taken");
                else tally.Review.Add($"capsule slot {capsule}/{slot}: {key} ({Scope(landed)})");
            }
            if (upstream.Count > 0 || a.TryGetProperty("slotDropped", out var gone) && gone.ValueKind == JsonValueKind.True) tally.Note("memories_with_upstream_tags");
            // A commitment 059 made from upstream's owed-to-me and counterparty: tags (058), counted by its direction.
            if (a.TryGetProperty("commitment", out var commitment) && commitment.ValueKind == JsonValueKind.Object)
                tally.Note(commitment.GetProperty("direction").GetString() == "in" ? "commitments_owed_to_the_person" : "commitments_the_person_owes");
            if (a.GetProperty("hold").ValueKind == JsonValueKind.String)
            {
                var reason = a.GetProperty("hold").GetString()!;
                var scope = Scope(landed);
                var held = tally.Held.TryGetValue(scope, out var h) ? h : tally.Held[scope] = new(StringComparer.Ordinal);
                held[reason] = held.GetValueOrDefault(reason) + 1;
                var why = heldFor is null ? "screening" : heldFor;
                tally.HeldFrom[$"{why}:{reason}"] = tally.HeldFrom.GetValueOrDefault($"{why}:{reason}") + 1;
                tally.Live(Lived(e.Id), "held");
            }
        }

        // Links: both ends in the file, both here, in one scope. An end is counted where this file meant it (Family when
        // the scope map names it, else the person's private scope), or in Family when an earlier import put it there,
        // which this person reads; never under the name of a private scope this file does not mean, where an import of
        // another file (another member's, or one made without the scope map) may have put it. A link with such an end is
        // not imported, and is listed for review as its memory is.
        var byId = entries.ToDictionary(e => e.Id!, StringComparer.Ordinal);
        string Meant(string id) => Family(byId[id]) ? RhythmConfig.Household : person;
        bool Elsewhere(string id) => memorySubjects.TryGetValue(id, out var s) && s != RhythmConfig.Household && s != Meant(id);
        string ScopeOf(string? id) => id is null || !byId.ContainsKey(id) ? NoScope
            : memorySubjects.TryGetValue(id, out var s) && !Elsewhere(id) ? Scope(s) : Scope(Meant(id));
        var linkKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in file.Edges)
        {
            var at = edge.From is not null && inFile.Contains(edge.From) ? ScopeOf(edge.From) : ScopeOf(edge.To);
            if (edge.Problem is not null) { tally.Add("link", at, "skipped:" + edge.Problem); continue; }
            if (!inFile.Contains(edge.From!) || !inFile.Contains(edge.To!)) { tally.Add("link", at, "skipped:outside_file"); continue; }
            if (LinkTypes.Find(edge.Type) is not { Upstream: true }) { tally.Add("link", at, "skipped:type"); continue; }
            if (!memoryIds.TryGetValue(edge.From!, out var from) || !memoryIds.TryGetValue(edge.To!, out var to))
            { tally.Add("link", at, "skipped:end_not_imported"); continue; }
            if (Elsewhere(edge.From!) || Elsewhere(edge.To!))
            {
                tally.Add("link", at, "skipped:end_elsewhere");
                tally.Review.Add($"link {edge.Type} from memory {MemoryKey(edge.From!)} to memory {MemoryKey(edge.To!)} not imported: an end was imported before into another scope than this file means; share it there, or correct the scope map");
                continue;
            }
            if (memorySubjects[edge.From!] != memorySubjects[edge.To!]) { tally.Add("link", at, "skipped:scopes"); continue; }
            var key = LinkKey(edge.Type, edge.From!, edge.To!);
            if (!linkKeys.Add(key)) { tally.Add("link", at, "skipped:duplicate"); continue; }
            var verification = LinkTypes.Verification(edge.Provenance);
            var a = await Call("SELECT import_second_brain_link(@person,@origin,@record)::text", JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["key"] = key, ["from"] = from.ToString(), ["to"] = to.ToString(), ["type"] = edge.Type, ["verification"] = verification,
                ["confidence"] = edge.Weight, ["createdAt"] = Iso(edge.CreatedAt ?? file.ExportedAt.ToUnixTimeMilliseconds()),
            }), null);
            var outcome = Outcome(a);
            tally.Add("link", Subject(a) is { } landed ? Scope(landed) : at, outcome);
            if (outcome == "added" && verification == "suggested") tally.Note("links_added_as_suggested");
        }

        if (write) await tx.CommitAsync(ct);
        else await tx.RollbackAsync(ct);

        var missing = AlwaysMissing.Select(m => household && HouseholdInstead.TryGetValue(m.Field, out var instead) ? (m.Field, instead) : m).ToList();
        if (!file.HasProjects) missing.Add(NoProjects);
        if (!file.HasUpdatedAt) missing.Add(NoUpdatedAt);
        if (!file.HasValidity) missing.Add(NoValidity);
        return new(person, scopes, write, file.Version, file.Sha256, file.Export, file.ExportedAt,
            tally.Outcomes.ToDictionary(k => k.Key, k => (IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>)k.Value.ToDictionary(
                s => s.Key, s => (IReadOnlyDictionary<string, int>)s.Value, StringComparer.Ordinal), StringComparer.Ordinal),
            tally.Held.ToDictionary(k => k.Key, k => (IReadOnlyDictionary<string, int>)k.Value, StringComparer.Ordinal), tally.HeldFrom,
            missing, InFileNotKept, tally.Notes, tally.Review, memoryIds, projectIds, household,
            household ? tally.Lived.ToDictionary(k => k.Key, k => (IReadOnlyDictionary<string, int>)k.Value, StringComparer.Ordinal) : null);
    }

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,200}$")] private static partial Regex SourceId();
    [GeneratedRegex("^[a-z][a-z0-9_.-]{0,63}$")] private static partial Regex CaptureSource();
}
