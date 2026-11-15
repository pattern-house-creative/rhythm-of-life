using System.Globalization;
using System.Net;
using System.Text.Json;
using Knowledge.Adapters;
using Knowledge.Adapters.SecondBrain;
using Knowledge.Cli;
using Knowledge.Relationships;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;

namespace Knowledge.Tests.Parity;

// The comparison harness of the Second Brain move (SB-P8-03;
// story 16, story 18): one throwaway install holding both sides of the synthetic brain (sb-export-brain.json) at once,
// the read-only adapter reading the brain as a service answers Alex's token (BrainService), and the native memories the
// importer brought from Alex's and Sam's exports with the owner's scope map (SB-P8-01). It pairs each record of one side
// with the other's by the origin map, never by title or text, and reports per synthetic person what each side serves,
// owns, groups and links, who may view and edit what (upstream's rule as the brain holds it against Rhythm's predicate
// and author lock), each scope's core capsule, retention, and where the same questions rank the expected memory on each
// side. Nothing here is real: the brain's people, places and service are invented, and the service is a fake that
// answers GET alone.
internal sealed class BrainService
{
    public static readonly string Token = "fictional-brain-comparison-" + "token";
    public const string Host = "brain.example.invalid";
    readonly SbBrain brain;
    readonly string personal;
    public IReadOnlyList<string> Teams { get; }
    public FakeHandler Handler { get; }

    // The brain as its service answers one member at the brain's moment (Now): their personal workspace and each team they
    // are on, live memories only (the trash is not listed), each with the state upstream's routes give it.
    public BrainService(SbBrain brain, SbUser member)
    {
        this.brain = brain;
        personal = brain.PersonalWorkspace(member);
        Teams = [.. brain.Memberships.Where(m => m.UserId == member.Id && m.CreatedAt <= brain.Now && brain.Workspace(m.WorkspaceId).Kind == "company")
            .Select(m => m.WorkspaceId).Order(StringComparer.Ordinal)];
        Handler = new FakeHandler(Respond);
    }

    // Upstream's validity state at Now (src/recall/validity-view.ts): current, replaced (a supersedes edge names it) or
    // ended (its window closed); a window not yet open is not current either.
    public string Validity(SbEntry e) =>
        e.ValidUntil is { } until && until <= brain.Now ? brain.Edges.Any(x => x.Type == "supersedes" && x.TargetId == e.Id) ? "replaced" : "ended"
        : e.ValidFrom is { } from && from > brain.Now ? "future" : "current";
    public static bool Retracted(SbEntry e) => e.Tags.Contains("retracted-source");

    string Layer(SbEntry e) => e.WorkspaceId == personal ? "personal" : "company";
    bool Readable(SbEntry e) => e.WorkspaceId == personal || Teams.Contains(e.WorkspaceId);

    HttpResponseMessage Respond(HttpRequestMessage r, string _)
    {
        var uri = r.RequestUri!;
        Assert.Equal(HttpMethod.Get, r.Method); // read-only: never a write
        Assert.Equal(Host, uri.Host);
        if (r.Headers.Authorization?.Parameter != Token) return FakeHandler.Json("""{"ok":false}""", HttpStatusCode.Unauthorized);
        var q = QueryHelpers.ParseQuery(uri.Query);
        if (uri.AbsolutePath == "/entry")
        {
            var e = brain.Entries.SingleOrDefault(x => x.Id == q["id"].ToString() && Readable(x));
            if (e is null) return FakeHandler.Json("""{"ok":false}""", HttpStatusCode.NotFound);
            return FakeHandler.Json(JsonSerializer.Serialize(new { ok = true, entry = new {
                id = e.Id, content = e.Content, tags = e.Tags, workspace = Layer(e), validity_state = Validity(e), retracted_source = Retracted(e),
                updated_at = (e.UpdatedAt ?? e.CreatedAt).ToUnixTimeMilliseconds() } }));
        }
        string layer = q["workspace"].ToString(), team = q.TryGetValue("team", out var t) ? t.ToString() : "";
        var workspace = layer == "personal" && team == "" ? personal : layer == "company" && Teams.Contains(team) ? team : null;
        if (workspace is null) return FakeHandler.Json("""{"ok":false}""", HttpStatusCode.Forbidden);
        if (uri.AbsolutePath == "/projects")
            return FakeHandler.Json(JsonSerializer.Serialize(new { projects = brain.Projects.Where(p => p.WorkspaceId == workspace)
                .Select(p => new { id = p.Id, name = p.Name, aliases = p.Aliases }) }));
        if (uri.AbsolutePath == "/graph")
        {
            var edges = brain.Edges.Where(x => x.WorkspaceId == workspace).ToArray();
            return FakeHandler.Json(JsonSerializer.Serialize(new { ok = true,
                nodes = edges.SelectMany(x => new[] { x.SourceId, x.TargetId }).Distinct().Select(id => new { id }),
                edges = edges.Select(x => new { source = x.SourceId, target = x.TargetId, type = x.Type, weight = x.Weight, provenance = x.Provenance }) }));
        }
        Assert.Equal("/list", uri.AbsolutePath);
        var before = q.TryGetValue("before", out var b) ? long.Parse(b.ToString(), CultureInfo.InvariantCulture) : long.MaxValue;
        var page = brain.Entries.Where(e => e.WorkspaceId == workspace && e.CreatedAt.ToUnixTimeMilliseconds() <= before)
            .OrderByDescending(e => e.CreatedAt).Take(int.Parse(q["n"].ToString(), CultureInfo.InvariantCulture));
        return FakeHandler.Json(JsonSerializer.Serialize(page.Select(e => new {
            id = e.Id, content = e.Content, tags = JsonSerializer.Serialize(e.Tags), source = e.Source, created_at = e.CreatedAt.ToUnixTimeMilliseconds(),
            workspace_id = e.WorkspaceId, workspace = Layer(e), validity_state = Validity(e), retracted_source = Retracted(e) })));
    }
}

// One question, the words a person would type into the map's search box for it, and the memory of the brain it should
// find (by key).
internal sealed record ComparisonQuery(string Query, string Term, string Expect);

// The report: lines of plain text, keyed by the brain's own keys (never a native id, which each run makes anew), so the
// same brain and the same code give the same report.
internal sealed record ComparisonReport(IReadOnlyList<string> Lines, IReadOnlyDictionary<string, int> Counts)
{
    public int Count(string name) => Counts.TryGetValue(name, out var n) ? n : 0;
    public const string File = "sb-comparison-report.json";
    public static IReadOnlyList<string> Expected() =>
        JsonSerializer.Deserialize<string[]>(System.IO.File.ReadAllText(Path.Combine(ParityCorpus.Folder, File)))!;
}

internal static class SecondBrainComparison
{
    // Questions a person would ask of the brain, each naming one memory: words from its opening sentence, which is all the
    // adapter keeps of it (its excerpt, the map's title), and words further in, which only the native text holds.
    public static readonly IReadOnlyList<ComparisonQuery> Queries =
    [
        new("Larkspur Labs audio lead", "audio lead", "alex-job"),
        new("lemon drizzle bake sale", "lemon drizzle", "family-bake-sale"),
        new("heat pump quote grant", "grant", "family-heatpump-quote"),
        new("Roadwise breakdown cover", "Roadwise", "family-car"),
        new("BW-5521", "BW-5521", "family-boiler-visit"),
        new("Marram coast path", "Marram", "alex-sabbatical-decision"),
        new("summer fair Saturday 19 June", "19 June", "family-fair-date"),
        new("physio knee appointment", "knee", "alex-knee"),
        new("electricity tariff per kWh", "Lumen Agile", "family-tariff-new"),
        new("Saltreach Singers", "Saltreach", "family-about-sam"),
        new("KW-20931 pottery", "pottery", "alex-present"),
        new("half marathon long run", "Half marathon", "alex-running-plan"),
    ];

    public sealed record Setup(MemoryArchiveTests.Install Install, SbBrain Brain, RelationshipHost Host, BrainService Service, AdapterRegistry Registry,
        IReadOnlyDictionary<string, Guid> Readers, IReadOnlyDictionary<string, SbUser> People);

    // The adapter as an install configures it for Alex's token: her personal workspace (hers) and each team she is on
    // (the household's).
    public static SecondBrainAdapter Adapter(BrainService service, RhythmConfig config, Database db)
    {
        var adapter = new SecondBrainAdapter(service.Handler);
        adapter.Configure(new(new("second-brain", new Dictionary<string, object?>
            {
                ["enabled"] = "true", ["url"] = $"https://{BrainService.Host}/", ["owner"] = "alex",
                ["teams"] = service.Teams.Cast<object>().ToList(),
            }),
            config, db, new Archive(Path.Combine(Path.GetTempPath(), "rhythm-test-archive"))));
        return adapter;
    }

    static async Task<(GraphNode[] Nodes, GraphEdge[] Edges)> Map(RelationshipHost host, Guid reader)
    {
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();
        for (string? cursor = null; ;)
        {
            var page = await host.Read(reader, null, null, default, new GraphQuery(Cursor: cursor));
            nodes.AddRange(page.nodes);
            edges.AddRange(page.edges);
            if (page.nextCursor is not { } next) return ([.. nodes], [.. edges]);
            cursor = next;
        }
    }

    static bool FromAdapter(GraphNode n) => n.provenance.sourceId == SecondBrainAdapter.Provider;
    static bool Native(GraphNode n) => n.provenance.sourceId == MemoryStars.Provider;

    // A native memory here as the comparison reads it: its brain key (through the origin map), scope, author, status,
    // hold, validity, project and capsule slot.
    sealed record Here(Guid Id, string Key, string Subject, string Author, string Status, string? Hold, bool Ended, string? Project, string? Capsule, string? Slot);

    public static async Task<ComparisonReport> Run(Setup s, CancellationToken ct = default)
    {
        var lines = new List<string>();
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Count(string name, int n) => counts[name] = n;
        var brain = s.Brain;
        var keyOf = brain.AllEntries.ToDictionary(e => SecondBrainImport.MemoryKey(e.Id), e => e.Key, StringComparer.Ordinal);
        var byId = brain.AllEntries.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var byKey = brain.AllEntries.ToDictionary(e => e.Key, StringComparer.Ordinal);
        string Ws(string id) => id == "" ? "unscoped" : brain.Workspace(id).Name;

        // The native side: every memory the importer brought, by its brain key.
        var here = new Dictionary<string, Here>(StringComparer.Ordinal);
        await using (var cmd = s.Install.Source.CreateCommand("""
            SELECT m.id,o.source_id,m.subject_id,m.author_id,m.status,m.hold,coalesce(m.valid_until<=now(),false),p.slug,m.capsule,m.capsule_slot
            FROM memory_origins o JOIN memories m ON m.id=o.local_id LEFT JOIN memory_projects p ON p.id=m.project_id
            WHERE o.origin=@origin AND o.kind='memory'
            """))
        {
            cmd.Parameters.AddWithValue("origin", SecondBrainImport.Origin);
            await using var row = await cmd.ExecuteReaderAsync(ct);
            while (await row.ReadAsync(ct))
            {
                var key = keyOf[row.GetString(1)];
                here[key] = new(row.GetGuid(0), key, row.GetString(2), row.GetString(3), row.GetString(4), row.IsDBNull(5) ? null : row.GetString(5),
                    row.GetBoolean(6), row.IsDBNull(7) ? null : row.GetString(7), row.IsDBNull(8) ? null : row.GetString(8), row.IsDBNull(9) ? null : row.GetString(9));
            }
        }
        var hereById = here.Values.ToDictionary(h => h.Id);
        string Scope(string subject) => subject == RhythmConfig.Household ? "Family" : subject;

        lines.Add("Second Brain comparison: the read-only adapter (Alex's token) against the native memories imported from Alex's and Sam's exports with the owner's scope map.");
        Count("brain_live_memories", brain.Entries.Length);
        Count("native_memories", here.Count);
        lines.Add($"Brain: {brain.Entries.Length} live memories in {brain.Workspaces.Length} workspaces, {brain.Trash.Length} in the trash; native: {here.Count} imported.");

        // ---- 1. What each side serves on the map, per reader --------------------------------------------------------------
        lines.Add("");
        lines.Add("1. Memories on the map, per reader (paired by the origin map, never by title)");
        var maps = new Dictionary<string, (GraphNode[] Nodes, GraphEdge[] Edges)>(StringComparer.Ordinal);
        foreach (var (name, reader) in s.Readers)
        {
            var map = maps[name] = await Map(s.Host, reader);
            var adapter = map.Nodes.Where(n => FromAdapter(n) && n.type == "memory").Select(n => byId[n.resource.identity.externalId].Key).ToHashSet(StringComparer.Ordinal);
            var native = map.Nodes.Where(n => Native(n) && n.type == "memory" && Guid.TryParse(n.resource.identity.externalId, out var id) && hereById.ContainsKey(id))
                .Select(n => hereById[Guid.Parse(n.resource.identity.externalId)].Key).ToHashSet(StringComparer.Ordinal);
            var both = adapter.Intersect(native).Count();
            Count($"map_{name}_both", both);
            Count($"map_{name}_adapter_only", adapter.Except(native).Count());
            Count($"map_{name}_native_only", native.Except(adapter).Count());
            lines.Add($"  {name}: {both} on both sides; {adapter.Except(native).Count()} on the adapter's side only; {native.Except(adapter).Count()} on the native side only");
            foreach (var key in adapter.Except(native).Order(StringComparer.Ordinal)) lines.Add($"    adapter only: {key} ({WhyNotNative(key)})");
            foreach (var key in native.Except(adapter).Order(StringComparer.Ordinal)) lines.Add($"    native only: {key} ({WhyNotAdapter(key)})");
        }
        string WhyNotNative(string key)
        {
            if (!here.TryGetValue(key, out var h)) return "not imported";
            if (h.Hold is { } hold) return $"held here for review ({hold}), which upstream serves";
            if (h.Status == "deprecated") return "marked wrong: Rhythm's map leaves it out, the adapter does not filter deprecated";
            if (h.Ended) return "no longer true here";
            return $"here in {Scope(h.Subject)}, not this reader's";
        }
        string WhyNotAdapter(string key)
        {
            var e = byKey[key];
            if (!Guid.TryParse(e.Id, out _)) return "its id is not a uuid (chosen by a client before 4.0): the adapter leaves it off";
            if (e.HeldReason is { } held) return $"held upstream ({held}): the adapter leaves held rows off";
            if (e.Status is "draft") return "a draft: the adapter leaves drafts off";
            if (BrainService.Retracted(e)) return "built on a retracted source: the adapter leaves it off";
            if (s.Service.Validity(e) != "current") return $"{s.Service.Validity(e)} upstream: the adapter lists current memories only";
            if (e.WorkspaceId != brain.PersonalWorkspace(s.People["alex"]) && !s.Service.Teams.Contains(e.WorkspaceId))
                return $"in {Ws(e.WorkspaceId)}, which Alex's token does not read";
            return "other";
        }

        // ---- 2. Owner, project and scope of each pair (Alex's map: the widest) --------------------------------------------
        lines.Add("");
        lines.Add("2. Owner and project of each memory on both sides (Alex's map)");
        var alexMap = maps["alex"];
        var adapterNodes = alexMap.Nodes.Where(n => FromAdapter(n) && n.type == "memory").ToDictionary(n => byId[n.resource.identity.externalId].Key, StringComparer.Ordinal);
        var nativeNodes = alexMap.Nodes.Where(n => Native(n) && n.type == "memory" && Guid.TryParse(n.resource.identity.externalId, out var id) && hereById.ContainsKey(id))
            .ToDictionary(n => hereById[Guid.Parse(n.resource.identity.externalId)].Key, StringComparer.Ordinal);
        var pairs = adapterNodes.Keys.Intersect(nativeNodes.Keys).Order(StringComparer.Ordinal).ToList();
        var ownerDiffer = pairs.Where(k => adapterNodes[k].resource.owner != nativeNodes[k].resource.owner).ToList();
        string? AdapterProject(GraphNode n) => n.groups?.Skip(1).Select(g => g.id[(g.id.LastIndexOf(':') + 1)..]).FirstOrDefault();
        var projectDiffer = pairs.Where(k => AdapterProject(adapterNodes[k]) is var a && a != here[k].Project).ToList();
        Count("pairs", pairs.Count);
        Count("pairs_owner_differs", ownerDiffer.Count);
        Count("pairs_project_differs", projectDiffer.Count);
        lines.Add($"  {pairs.Count} pairs: owner the same for {pairs.Count - ownerDiffer.Count}, project the same for {pairs.Count - projectDiffer.Count}");
        foreach (var k in ownerDiffer) lines.Add($"    owner differs: {k} (adapter {adapterNodes[k].resource.owner}, native {nativeNodes[k].resource.owner})");
        foreach (var k in projectDiffer) lines.Add($"    project differs: {k} (adapter {AdapterProject(adapterNodes[k]) ?? "none"}, native {here[k].Project ?? "none"})");

        // ---- 3. Neighbourhoods: each side's links between two memories, against the brain's own edges --------------------
        lines.Add("");
        lines.Add("3. Links between two memories (type, verification), against the brain's edges Alex reads");
        static string Edge(string type, string from, string to, string verification) =>
            type == "relates_to" && string.CompareOrdinal(from, to) > 0 ? $"{type} {to} {from} {verification}" : $"{type} {from} {to} {verification}";
        var starKey = alexMap.Nodes.Where(n => n.type == "memory" && (FromAdapter(n) || Native(n) && Guid.TryParse(n.resource.identity.externalId, out var g) && hereById.ContainsKey(g)))
            .ToDictionary(n => n.id, n => FromAdapter(n) ? byId[n.resource.identity.externalId].Key : hereById[Guid.Parse(n.resource.identity.externalId)].Key);
        var adapterStars = alexMap.Nodes.Where(n => FromAdapter(n)).Select(n => n.id).ToHashSet(StringComparer.Ordinal);
        var adapterLinks = alexMap.Edges.Where(e => adapterStars.Contains(e.source) && adapterStars.Contains(e.target) && starKey.ContainsKey(e.source) && starKey.ContainsKey(e.target))
            .Select(e => Edge(e.relationship, starKey[e.source], starKey[e.target], e.verification)).ToHashSet(StringComparer.Ordinal);
        var nativeLinks = new HashSet<string>(StringComparer.Ordinal);
        await using (var cmd = s.Install.Source.CreateCommand("SELECT source_id,target_id,type,verification FROM made_links WHERE imported"))
        await using (var row = await cmd.ExecuteReaderAsync(ct))
            while (await row.ReadAsync(ct))
                if (hereById.TryGetValue(row.GetGuid(0), out var a) && hereById.TryGetValue(row.GetGuid(1), out var b))
                    nativeLinks.Add(Edge(row.GetString(2), a.Key, b.Key, row.GetString(3)));
        var alexReads = brain.Readable(s.People["alex"], brain.Now);
        var brainLinks = brain.Edges.Where(e => alexReads.Contains(e.WorkspaceId)).Select(e => Edge(e.Type, byId[e.SourceId].Key, byId[e.TargetId].Key, LinkTypes.Verification(e.Provenance)))
            .ToHashSet(StringComparer.Ordinal);
        Count("links_brain", brainLinks.Count);
        Count("links_adapter", adapterLinks.Count);
        Count("links_native", nativeLinks.Count);
        lines.Add($"  the brain: {brainLinks.Count}; the adapter's map: {adapterLinks.Count}; native (every imported link): {nativeLinks.Count}");
        var onAdapter = alexMap.Nodes.Where(FromAdapter).Where(n => n.type == "memory").Select(n => byId[n.resource.identity.externalId].Key).ToHashSet(StringComparer.Ordinal);
        foreach (var e in brain.Edges.Where(e => alexReads.Contains(e.WorkspaceId))
                     .OrderBy(e => Edge(e.Type, byId[e.SourceId].Key, byId[e.TargetId].Key, LinkTypes.Verification(e.Provenance)), StringComparer.Ordinal))
        {
            var (from, to) = (byId[e.SourceId].Key, byId[e.TargetId].Key);
            var l = Edge(e.Type, from, to, LinkTypes.Verification(e.Provenance));
            var adapterWhy = adapterLinks.Contains(l) ? "yes" : !onAdapter.Contains(from) || !onAdapter.Contains(to) ? "no (an end is off the adapter's map)"
                : byId[e.SourceId].WorkspaceId != byId[e.TargetId].WorkspaceId ? "no (two workspaces)" : "no";
            var nativeWhy = nativeLinks.Contains(l) ? "yes" : !here.ContainsKey(from) || !here.ContainsKey(to) ? "no (an end was not imported)"
                : here[from].Subject != here[to].Subject ? $"no (its ends are in two scopes here, {Scope(here[from].Subject)} and {Scope(here[to].Subject)}, which never link)" : "no";
            lines.Add($"    {l}: adapter {adapterWhy}, native {nativeWhy}");
        }
        foreach (var l in adapterLinks.Union(nativeLinks).Except(brainLinks).Order(StringComparer.Ordinal))
            lines.Add($"    {l}: not one of the brain's edges Alex reads (adapter {(adapterLinks.Contains(l) ? "yes" : "no")}, native {(nativeLinks.Contains(l) ? "yes" : "no")})");

        // ---- 4. Who may view and edit: upstream's rule as the brain holds it, against Rhythm's ---------------------------
        lines.Add("");
        lines.Add("4. Who may view and edit each memory: upstream's rule (the brain's workspaces, memberships and authors) against Rhythm's predicate and author lock");
        foreach (var person in new[] { "alex", "sam" })
        {
            var user = s.People[person];
            var reads = brain.Readable(user, brain.Now);
            var upstreamView = brain.Entries.Where(e => reads.Contains(e.WorkspaceId)).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
            // Upstream's author lock (SEC-04, src/lib/entry-access.ts): a personal memory its owner's, a team's its author's
            // or an admin's.
            bool UpstreamEdits(SbEntry e) => reads.Contains(e.WorkspaceId)
                && (brain.Workspace(e.WorkspaceId).Kind == "personal" || e.ActorId == user.Id || user.Role == "admin");
            var nativeView = new Dictionary<string, bool>(StringComparer.Ordinal);
            await using (var cmd = s.Install.Source.CreateCommand($"""
                SELECT m.id,memory_may_change(@principal,m.subject_id,m.author_id,scopes_on())
                FROM memories m JOIN resources r ON r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL AND r.external_id=m.id::text
                WHERE {Database.Visible("r")}
                """))
            {
                cmd.Parameters.AddWithValue("principal", s.Readers[person]);
                await using var row = await cmd.ExecuteReaderAsync(ct);
                while (await row.ReadAsync(ct))
                    if (hereById.TryGetValue(row.GetGuid(0), out var h)) nativeView[h.Key] = row.GetBoolean(1);
            }
            var viewBoth = upstreamView.Intersect(nativeView.Keys).Count();
            var upstreamOnly = upstreamView.Except(nativeView.Keys).Order(StringComparer.Ordinal).ToList();
            var nativeOnly = nativeView.Keys.Except(upstreamView).Order(StringComparer.Ordinal).ToList();
            Count($"view_{person}_both", viewBoth);
            Count($"view_{person}_upstream_only", upstreamOnly.Count);
            Count($"view_{person}_native_only", nativeOnly.Count);
            lines.Add($"  {person} views: {viewBoth} on both; {upstreamOnly.Count} upstream only; {nativeOnly.Count} native only");
            foreach (var k in upstreamOnly) lines.Add($"    upstream only: {k} ({(here.TryGetValue(k, out var h) ? $"here in {Scope(h.Subject)}" : "not imported")})");
            foreach (var k in nativeOnly) lines.Add($"    native only: {k} (upstream in {Ws(byKey[k].WorkspaceId)})");
            var editBoth = upstreamView.Intersect(nativeView.Keys).Where(k => UpstreamEdits(byKey[k]) && nativeView[k]).Count();
            var editUpstreamOnly = upstreamView.Intersect(nativeView.Keys).Where(k => UpstreamEdits(byKey[k]) && !nativeView[k]).Order(StringComparer.Ordinal).ToList();
            var editNativeOnly = upstreamView.Intersect(nativeView.Keys).Where(k => !UpstreamEdits(byKey[k]) && nativeView[k]).Order(StringComparer.Ordinal).ToList();
            Count($"edit_{person}_both", editBoth);
            Count($"edit_{person}_upstream_only", editUpstreamOnly.Count);
            Count($"edit_{person}_native_only", editNativeOnly.Count);
            lines.Add($"  {person} edits (of those both sides let them view): {editBoth} on both; {editUpstreamOnly.Count} upstream only; {editNativeOnly.Count} native only");
            foreach (var k in editUpstreamOnly)
                lines.Add($"    upstream only: {k} (written upstream by {brain.User(byKey[k].ActorId).Name}; here its author is {here[k].Author}, the importing person)");
            foreach (var k in editNativeOnly)
                lines.Add($"    native only: {k} (written upstream by {brain.User(byKey[k].ActorId).Name}; here its author is {here[k].Author})");
        }

        // ---- 5. Capsules: each scope's core capsule on each side ----------------------------------------------------------
        lines.Add("");
        lines.Add("5. Core capsules: the same canonical memories in the same slots, the same text, and the ETag");
        foreach (var (scope, workspace, reader) in new[] { ("private", brain.PersonalWorkspace(s.People["alex"]), s.Readers["alex"]),
                     ("shared", brain.Workspaces.Single(w => w.Name == "Family").Id, s.Readers["alex"]) })
        {
            var candidates = brain.Entries.Where(e => e.WorkspaceId == workspace && e.Tags.Contains("capsule:core") && e.Status == "canonical"
                    && e.HeldReason is null && s.Service.Validity(e) == "current" && Guid.TryParse(e.Id, out _))
                .Select(e => new CapsuleCandidate(Guid.Parse(e.Id), e.Tags.First(t => t.StartsWith("capsule-slot:", StringComparison.Ordinal))["capsule-slot:".Length..], e.Content))
                .Where(c => CapsuleSlots.CoreSlots.Contains(c.Slot)).ToList();
            var upstream = Capsule.Answer(CapsuleSlots.Core, null, scope, Capsule.Select(CapsuleSlots.Core, candidates));
            var native = await new PromptCapsules(s.Install.Source).Build(reader, new CapsuleRequest(CapsuleSlots.Core, Scope: scope), ct);
            static IEnumerable<(string Slot, string Id)> Sections(BuiltCapsule c) =>
                c.Payload["sections"]!.AsArray().Select(x => ((string)x!["slot"]!, (string)x["source_entry_id"]!));
            var upSlots = Sections(upstream).Select(x => $"{x.Slot}={byId[x.Id].Key}").ToList();
            var nativeSlots = Sections(native).Select(x => $"{x.Slot}={hereById[Guid.Parse(x.Id)].Key}").ToList();
            var sameSlots = upSlots.SequenceEqual(nativeSlots);
            var sameText = (string)upstream.Payload["prompt_hash"]! == (string)native.Payload["prompt_hash"]!;
            Count($"capsule_{scope}_same_slots", sameSlots ? 1 : 0);
            Count($"capsule_{scope}_same_text", sameText ? 1 : 0);
            Count($"capsule_{scope}_same_etag", upstream.Etag == native.Etag ? 1 : 0);
            lines.Add($"  {(scope == "private" ? "Alex's private" : "Family's")} core: upstream [{string.Join(", ", upSlots)}], native [{string.Join(", ", nativeSlots)}]; "
                + $"slots {(sameSlots ? "the same" : "differ")}, text (prompt_hash) {(sameText ? "the same" : "differs")}, ETag {(upstream.Etag == native.Etag ? "the same" : "differs")}"
                + (upstream.Etag != native.Etag && sameText ? " (the answer names each side's own memory ids)" : ""));
        }

        // ---- 6. Retention: trash and versions -------------------------------------------------------------------------------
        lines.Add("");
        lines.Add("6. Retention");
        long Scalar(string sql) => (long)s.Install.Scalar(sql).GetAwaiter().GetResult()!;
        var nativeTrash = Scalar("SELECT count(*) FROM memory_trash t JOIN memory_origins o ON o.local_id=t.memory_id AND o.kind='memory'");
        var nativeVersions = Scalar("SELECT count(*) FROM memory_versions v JOIN memory_origins o ON o.local_id=v.memory_id AND o.kind='memory'");
        var importedEvents = Scalar("SELECT count(*) FROM memory_events e JOIN memory_origins o ON o.local_id=e.memory_id AND o.kind='memory'");
        Count("trash_upstream", brain.Trash.Length);
        Count("trash_native", (int)nativeTrash);
        Count("versions_upstream", brain.Versions.Length);
        Count("versions_native", (int)nativeVersions);
        lines.Add($"  trash: upstream {brain.Trash.Length} ({string.Join(", ", brain.Trash.Select(t => $"{t.Key} in {Ws(t.WorkspaceId)}"))}), native {nativeTrash}: the export carries none");
        lines.Add($"  versions: upstream {brain.Versions.Length} of {brain.Versions.Select(v => v.EntryId).Distinct().Count()} memories, native {nativeVersions}: history starts at the import ({importedEvents} imported audit rows)");
        lines.Add($"  audit events: upstream {brain.Events.Length}, not exported");

        // ---- 7. Queries: the expected memory's rank on each side ----------------------------------------------------------
        lines.Add("");
        lines.Add("7. Questions asked as Alex: recall, as an AI client asks it (words only, no model), and the map's search box (a star's title)");
        var config = RhythmConfig.Current;
        var db = new Database(s.Install.Source, s.Registry.Policies(config));
        // As the engine serves recall: a live record only while its source confirms it (SourceValidation).
        var recall = new Recall(db, null, PassageSources.For(EmbeddingSettings.From(config), s.Registry.Enabled, true), new SourceValidation(s.Registry),
            RecallOptions.Default, "off");
        // Which memory of the brain a result stands for: a native memory's own row or its star (by the origin map), or an
        // adapter star (by the service's id it keeps).
        async Task<(bool Native, string? Key)> Stands(Guid resource)
        {
            await using var cmd = s.Install.Source.CreateCommand(
                "SELECT provider,external_id,relationship_snapshot->'Key'->>'ExternalId' FROM resources WHERE id=@id");
            cmd.Parameters.AddWithValue("id", resource);
            await using var row = await cmd.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct)) return (false, null);
            var (provider, external, keyed) = (row.GetString(0), row.GetString(1), row.IsDBNull(2) ? null : row.GetString(2));
            if (provider == MemoryStars.Provider)
                foreach (var candidate in new[] { external, keyed })
                    if (Guid.TryParse(candidate, out var id) && hereById.TryGetValue(id, out var h)) return (true, h.Key);
            if (provider == SecondBrainAdapter.Provider && keyed is not null && byId.TryGetValue(keyed, out var e)) return (false, e.Key);
            return (false, null);
        }
        int nativeFound = 0, adapterFound = 0, adapterServed = 0, mapNative = 0, mapAdapter = 0;
        foreach (var q in Queries)
        {
            var found = await recall.Search(s.Readers["alex"], new RecallQuery(q.Query, RecallOptions.Default.MaxTopK), ct);
            int? nativeRank = null, adapterRank = null;
            foreach (var (hit, rank) in found.Hits.Select((h, i) => (h, i + 1)))
            {
                if (hit.Row.GetProperty("provider").GetString() == SecondBrainAdapter.Provider) adapterServed++;
                var (native, key) = await Stands(hit.Id);
                if (key != q.Expect) continue;
                if (native) nativeRank ??= rank;
                else adapterRank ??= rank;
            }
            if (nativeRank is not null) nativeFound++;
            if (adapterRank is not null) adapterFound++;
            var searched = (await s.Host.Read(s.Readers["alex"], null, null, default, new GraphQuery("search", q.Term))).nodes;
            bool OnMap(Func<GraphNode, bool> side) => searched.Where(n => n.type == "memory").Where(side).Any(n => FromAdapter(n)
                ? byId.TryGetValue(n.resource.identity.externalId, out var e) && e.Key == q.Expect
                : Guid.TryParse(n.resource.identity.externalId, out var id) && hereById.TryGetValue(id, out var h) && h.Key == q.Expect);
            var (nativeOnMap, adapterOnMap) = (OnMap(Native), OnMap(FromAdapter));
            if (nativeOnMap) mapNative++;
            if (adapterOnMap) mapAdapter++;
            lines.Add($"  \"{q.Query}\" -> {q.Expect}: recall native {Rank(nativeRank)}, adapter {Rank(adapterRank)}; "
                + $"map search \"{q.Term}\" native {(nativeOnMap ? "found" : "missing")}, adapter {(adapterOnMap ? "found" : "missing")}");
        }
        static string Rank(int? r) => r is { } n ? "#" + n.ToString(CultureInfo.InvariantCulture) : "missing";
        Count("queries", Queries.Count);
        Count("queries_recall_native", nativeFound);
        Count("queries_recall_adapter", adapterFound);
        Count("queries_recall_adapter_rows_served", adapterServed);
        Count("queries_map_native", mapNative);
        Count("queries_map_adapter", mapAdapter);
        lines.Add($"  recall found native {nativeFound} of {Queries.Count}, adapter {adapterFound} of {Queries.Count} ({adapterServed} adapter rows served in all): "
            + "a reference-only record passes no source check (Retrieval.Validate serves an archive's rows and a live record its document source confirms), "
            + "so recall and search serve none of the adapter's memories");
        lines.Add($"  map search found native {mapNative} of {Queries.Count}, adapter {mapAdapter} of {Queries.Count}: a star's title is its opening words, "
            + "so words further in are on neither side's map, and the adapter misses besides a memory it leaves off its map");
        return new(lines, counts);
    }
}
