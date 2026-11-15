using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Knowledge.Relationships;

namespace Knowledge.Adapters.SecondBrain;

// A Second Brain memory service as a MemorySource, by reference (docs/adapters/second-brain.md). Optional: the Markdown
// notes adapter is the default memory source. adapters.second-brain.url is the service's address (there is no default),
// and its bearer token is the secret file secrets/adapters/second-brain_token.
//   List:     every eligible memory in the token's personal workspace, and in each team workspace listed in
//             adapters.second-brain.teams, as a memory star; their projects as project stars they hang from; Second
//             Brain's own links between memories; and the Drive links in a memory's text (they join Drive stars). The
//             text itself is not stored: only those links, a short excerpt as the title and a similarity vector.
//   ReadText: a memory's current text, read from the service when its star opens. Nothing is kept.
// Held, draft, retracted or no-longer-current memories never become stars. No writes, ever.
public sealed partial class SecondBrainAdapter(HttpMessageHandler? handler = null) : IMemorySource
{
    public const string AdapterName = "second-brain", Provider = "second-brain";
    const int Page = 100, MaxReferences = 100, MaxTeams = 10, MaxText = 40_000;
    const string MembershipLocator = "second-brain:project-membership";

    public string Name => AdapterName;
    public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } =
        new Dictionary<string, ProviderPolicy> { [Provider] = new(SourceAuthority.ReferenceOnly) };

    Uri? origin;
    HttpClient? http;
    AdapterSettings? settings;
    string owner = RhythmConfig.Household, account = Provider;
    IReadOnlyList<Layer> layers = [];

    // One workspace to list: the token's personal one (the owner's), or a team workspace by its id (the household's,
    // as a shared workspace is). Its memories share one group.
    sealed record Layer(string Workspace, string? Team, string Subject)
    {
        public string Query => $"workspace={Workspace}" + (Team is null ? "" : "&team=" + Uri.EscapeDataString(Team));
        public string Scope => Team is null ? "private" : "shared";
        public RecordGroup Group => Team is null ? new("second-brain:personal", "Personal") : new("second-brain:" + Team, "Shared");
        // A project's record id carries its workspace, so same-named projects in two workspaces stay apart.
        public string ProjectGroup(string project) => $"second-brain:project:{Team ?? "personal"}:{project}";
    }

    public void Configure(AdapterContext context)
    {
        settings = context.Settings;
        if (!Uri.TryCreate(settings.String("url"), UriKind.Absolute, out var url) || url.Scheme != "https" || !url.IsDefaultPort
            || url.AbsolutePath != "/" || url.UserInfo != "" || url.Query != "" || url.Fragment != "")
            throw new ConfigException("adapters.second-brain.url is the service's https address, such as https://brain.example.invalid/");
        var teams = settings.List("teams");
        if (teams.Count > MaxTeams || teams.Any(t => !TeamId().IsMatch(t)) || teams.Distinct(StringComparer.Ordinal).Count() != teams.Count)
            throw new ConfigException($"adapters.second-brain.teams lists up to {MaxTeams} distinct workspace ids (letters, digits, '-' and '_')");
        origin = url;
        owner = settings.Owner ?? RhythmConfig.Household;
        layers = [new("personal", null, owner), .. teams.Select(t => new Layer("company", t, RhythmConfig.Household))];
        account = settings.StarAccount(Provider);
        http = AdapterHttp.Client(handler, TimeSpan.FromSeconds(20));
    }

    ResourceKey Key(Layer layer, string id) => new(layer.Subject, Provider, account, id);

    // The bearer token, read on every pass so a replaced file takes effect without a restart.
    string? Token()
    {
        var path = settings?.SecretFile("token");
        if (path is null || Directory.Exists(path) || !File.Exists(path)) return null;
        try { return SecretFiles.Read(path) is { Length: > 0 } value ? value : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    async Task<HttpResponseMessage> Get(string path, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin!, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    // ---- List ----------------------------------------------------------------------------------------------------------
    public async Task<SourceListing> List(SourcePass pass, CancellationToken ct)
    {
        var token = Token() ?? throw new UnauthorizedAccessException("Second Brain isn't connected: its token file is missing");
        var records = new List<DiscoveryRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var complete = true;
        foreach (var layer in layers)
        {
            var (listed, done) = await ListLayer(layer, token, seen, counts, await Projects(layer, token, ct), pass, ct);
            if (listed.Count > 0) listed = await LinkLayer(layer, token, listed, pass, ct);
            records.AddRange(listed);
            records.AddRange(ProjectRecords(layer, listed));
            complete &= done;
        }
        return new(records, complete);
    }

    // Newest first, keyset-paged on created_at. A failed or malformed page ends this workspace's pass as incomplete, so
    // nothing leaves the map because of it. graph.maxMemories counts every workspace's memories together; with scopes on
    // (SourcePass.MemoryShares) each subject's workspaces count against that subject's own share instead, so the
    // personal workspace never uses up what the team workspaces (the household's) may list, nor they the owner's.
    async Task<(List<DiscoveryRecord>, bool)> ListLayer(Layer layer, string token, HashSet<string> seen, Dictionary<string, int> counts,
        IReadOnlyDictionary<string, (string Id, string Name)> projects, SourcePass pass, CancellationToken ct)
    {
        var listed = new List<DiscoveryRecord>();
        bool Full() => pass.MemoryShares is null ? seen.Count >= pass.MaxMemories
            : counts.GetValueOrDefault(layer.Subject) >= pass.MemoriesOf(layer.Subject);
        long? before = null;
        while (!Full())
        {
            var observed = DateTimeOffset.UtcNow;
            JsonElement[] rows;
            try
            {
                using var response = await Get($"list?{layer.Query}&n={Page}" + (before is null ? "" : $"&before={before}"), token, ct);
                if (!response.IsSuccessStatusCode) return (listed, false);
                using var page = JsonDocument.Parse(await AdapterHttp.Read(response, 16 * 1024 * 1024, ct));
                if (page.RootElement.ValueKind != JsonValueKind.Array) return (listed, false);
                rows = page.RootElement.EnumerateArray().Select(r => r.Clone()).ToArray();
            }
            catch (Exception e) when (e is HttpRequestException or IOException or JsonException
                || e is OperationCanceledException && !ct.IsCancellationRequested) { return (listed, false); }
            long? oldest = null;
            var capped = false;
            foreach (var row in rows)
            {
                if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("created_at", out var created) && created.TryGetInt64(out var time))
                    oldest = oldest is null ? time : Math.Min(oldest.Value, time);
                // Past the cap the rest is unlisted, so the listing can't be complete.
                if (Full()) { capped = true; continue; }
                if (Memory(layer, row, observed, projects, pass) is { } record && seen.Add(record.Key.ExternalId))
                {
                    listed.Add(record);
                    counts[layer.Subject] = counts.GetValueOrDefault(layer.Subject) + 1;
                }
            }
            if (capped) return (listed, false); // graph.maxMemories
            // `before` is inclusive: boundary rows repeat and are skipped; a page that makes no progress ends the workspace.
            if (rows.Length < Page) return (listed, true);
            if (oldest is null || oldest == before) return (listed, false);
            before = oldest;
        }
        return (listed, false); // capped by graph.maxMemories
    }

    // One listed row as a record, or null when it is outside this workspace, not eligible or malformed.
    DiscoveryRecord? Memory(Layer layer, JsonElement row, DateTimeOffset observed, IReadOnlyDictionary<string, (string Id, string Name)> projects,
        SourcePass pass)
    {
        try
        {
            var id = row.GetProperty("id").GetString();
            // The service already scopes the listing; the workspace is checked again so a row from another one is dropped.
            if (!Guid.TryParse(id, out _) || row.GetProperty("workspace").GetString() != layer.Workspace
                || layer.Team is not null && row.GetProperty("workspace_id").GetString() != layer.Team) return null;
            var text = row.GetProperty("content").GetString();
            var listedTags = row.GetProperty("tags");
            var tags = listedTags.ValueKind == JsonValueKind.String
                ? JsonSerializer.Deserialize<string[]>(listedTags.GetString()!) : listedTags.Deserialize<string[]>();
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaxText || tags is null || tags.Any(t => t is null)
                || !Eligible(tags, row.GetProperty("retracted_source").GetBoolean(), row.GetProperty("validity_state").GetString())) return null;
            var created = row.GetProperty("created_at").GetInt64();
            // Only Drive file and folder links, as their exact address; never the prose around them.
            var references = RelationshipDiscovery.ExtractLinks(text)
                .Where(r => RelationshipDiscovery.DriveFileId(r.Url) is not null || RelationshipDiscovery.DriveFolderId(r.Url) is not null)
                .Select(r => r with { Excerpt = r.Url!.Length <= 1000 ? r.Url : "" }).ToArray();
            // A listing carries no modification time: the fingerprint of the listed content and attribution is the revision.
            // Its shape is the private version's (the workspace under "Layer"), so a moved install's memories keep their
            // revisions and do not all read as changed at its first rebuild.
            var fingerprint = JsonSerializer.Serialize(new { id, text, tags = tags.Order(StringComparer.Ordinal), created,
                source = row.TryGetProperty("source", out var capture) && capture.ValueKind == JsonValueKind.String ? capture.GetString() : null,
                Layer = layer.Workspace, layer.Team });
            var revision = "observed-sha256:" + Hashing.Hash(fingerprint);
            var key = Key(layer, id!);
            var excerpt = Title(text);
            var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(created);
            var groups = Groups(layer, tags, projects);
            // Membership is the source's own structure: a verified, directed link to each project's record.
            var inProject = groups.Skip(1).Select(g => new Reference(Key(layer, g.Id["second-brain:".Length..]), null, MembershipLocator, "In project",
                "in_project", true, "verified", 1, true)).ToArray();
            var record = new DiscoveryRecord(key, layer.Scope, "memory", excerpt, null, revision, null, observed,
                [.. inProject, .. references.Take(MaxReferences - inProject.Length)],
                CreatedAt: createdAt <= observed ? createdAt : null, ContentRevision: revision,
                Embedding: pass.Vector(key, revision, excerpt, text), Groups: groups, Overview: excerpt);
            // The memory's text, for the titles model when one is on (D-255): only for an eligible memory, never kept.
            pass.TitleText(record, text);
            return record;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException)
        { return null; }
    }

    // The workspace's group first, then each project the memory belongs to: tagged `project:<id>` or with one of the
    // project's aliases, named from the project registry (or by its id when the registry is unavailable).
    static IReadOnlyList<RecordGroup> Groups(Layer layer, IEnumerable<string> tags, IReadOnlyDictionary<string, (string Id, string Name)> projects)
    {
        var groups = new List<RecordGroup> { layer.Group };
        foreach (var tag in tags.Select(t => t.Trim().ToLowerInvariant()).Distinct())
        {
            (string Id, string Name)? project = projects.TryGetValue(tag, out var known) ? known
                : tag.StartsWith("project:", StringComparison.Ordinal) && tag.Length > 8 ? (tag[8..], tag[8..]) : null;
            if (project is not { } found || found.Id.Length > 100) continue;
            var id = layer.ProjectGroup(found.Id);
            if (groups.All(g => g.Id != id))
                groups.Add(new(id, string.IsNullOrWhiteSpace(found.Name) ? found.Id : found.Name.Length <= 200 ? found.Name : found.Name[..200]));
        }
        // Ordered by id, which never changes, so a memory's first project is stable across passes.
        return [groups[0], .. groups.Skip(1).OrderBy(g => g.Id, StringComparer.Ordinal).Take(19)];
    }

    // One record per project that has at least one listed memory, named from the registry and grouped with its memories.
    // No address: a project has no page of its own. A project left with no memories leaves the map.
    IEnumerable<DiscoveryRecord> ProjectRecords(Layer layer, IEnumerable<DiscoveryRecord> listed)
    {
        var observed = DateTimeOffset.UtcNow;
        return listed.Where(r => r.Status == "current" && r.Kind == "memory").SelectMany(r => r.Groups?.Skip(1) ?? [])
            .GroupBy(g => g.Id, StringComparer.Ordinal).Select(g => g.First())
            .Select(g => new DiscoveryRecord(Key(layer, g.Id["second-brain:".Length..]), layer.Scope, "project", g.Label, null,
                "observed-sha256:" + Hashing.Hash(JsonSerializer.Serialize(new { g.Id, g.Label })), null, observed, [], Groups: [g]))
            .ToArray();
    }

    // The workspace's project registry: each project tag and alias, to its project. Unavailable or malformed means none;
    // `project:` tags still group, named by their id.
    async Task<IReadOnlyDictionary<string, (string Id, string Name)>> Projects(Layer layer, string token, CancellationToken ct)
    {
        var map = new Dictionary<string, (string Id, string Name)>(StringComparer.Ordinal);
        try
        {
            using var response = await Get($"projects?{layer.Query}", token, ct);
            if (!response.IsSuccessStatusCode) return map;
            using var json = JsonDocument.Parse(await AdapterHttp.Read(response, 4 * 1024 * 1024, ct));
            foreach (var project in json.RootElement.GetProperty("projects").EnumerateArray())
            {
                if (Text(project, "id") is not { Length: > 0 } id) continue;
                var entry = (id, Text(project, "name") ?? id);
                map.TryAdd("project:" + id.ToLowerInvariant(), entry);
                if (project.TryGetProperty("aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Array)
                    foreach (var alias in aliases.EnumerateArray().Select(a => a.ValueKind == JsonValueKind.String ? a.GetString() : null).OfType<string>())
                        map.TryAdd(alias.Trim().ToLowerInvariant(), entry);
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidOperationException or KeyNotFoundException
            || e is OperationCanceledException && !ct.IsCancellationRequested) { map.Clear(); }
        return map;
    }

    // Second Brain's own links between memories of one workspace, labelled by who made them: a link the person (or an
    // assistant on their behalf) made is verified, a system link inferred, an automatic similarity link only suggested.
    // Only links between two memories this workspace listed count (byId), so a memory's revision, which names its links,
    // never names a record of another scope: a team memory's (Family's) never moves with the owner's personal memories,
    // whatever the service reports (docs/dev/memory.md section 8).
    async Task<List<DiscoveryRecord>> LinkLayer(Layer layer, string token, List<DiscoveryRecord> listed, SourcePass pass, CancellationToken ct)
    {
        JsonElement[]? edges = null;
        var truncated = false;
        try
        {
            using var response = await Get($"graph?limit=1500&{layer.Query}", token, ct);
            if (response.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(await AdapterHttp.Read(response, 32 * 1024 * 1024, ct));
                edges = json.RootElement.GetProperty("edges").EnumerateArray().Select(e => e.Clone()).ToArray();
                truncated = json.RootElement.GetProperty("nodes").GetArrayLength() >= 1500; // node content is ignored
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidOperationException or KeyNotFoundException
            || e is OperationCanceledException && !ct.IsCancellationRequested) { edges = null; }
        var byId = listed.ToDictionary(r => r.Key.ExternalId, StringComparer.Ordinal);
        var added = new Dictionary<string, List<Reference>>(StringComparer.Ordinal);
        var complete = edges is not null && !truncated;
        // Without a complete link list, carry the links seen last pass so nothing is retired.
        if (!complete)
            foreach (var r in listed)
                if (pass.Previous.TryGetValue(r.Key, out var previous))
                    added[r.Key.ExternalId] = previous.References.Where(x => x.Locator.StartsWith("second-brain:", StringComparison.Ordinal)
                        && x.Locator != MembershipLocator).ToList();
        foreach (var edge in complete ? edges! : [])
        {
            if (edge.ValueKind != JsonValueKind.Object) continue;
            string? from = Text(edge, "source"), to = Text(edge, "target"), type = Text(edge, "type"), provenance = Text(edge, "provenance");
            if (from is null || to is null || from == to || type is null || !LinkType().IsMatch(type)
                || !byId.ContainsKey(from) || !byId.TryGetValue(to, out var target)) continue;
            var weight = edge.TryGetProperty("weight", out var w) && w.TryGetDouble(out var value) && double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0.5;
            // Its provenance as a verification, and its direction by upstream's own vocabulary (only relates_to has none); a
            // type upstream does not list keeps its direction (LinkTypes).
            var verification = LinkTypes.Verification(provenance);
            if (!added.TryGetValue(from, out var list)) added[from] = list = [];
            list.Add(new(target.Key, null, $"second-brain:{type}:{provenance ?? "inferred"}", $"Second Brain {type} ({provenance ?? "inferred"})",
                type, true, verification, Math.Round(weight, 2), LinkTypes.Find(type) is { Upstream: true } known ? known.Directed : true));
        }
        return listed.Select(r =>
        {
            if (!added.TryGetValue(r.Key.ExternalId, out var links) || links.Count == 0) return complete ? r : r with { ReferencesComplete = false };
            links = links.OrderBy(l => l.Target!.ExternalId, StringComparer.Ordinal).ThenBy(l => l.NativeType, StringComparer.Ordinal).ToList();
            var all = r.References.Concat(links).ToList();
            // A memory's links are part of its observed state, so a link change is a new revision.
            var revision = "observed-sha256:" + Hashing.Hash(r.Revision + JsonSerializer.Serialize(
                links.Select(l => new { l.Target!.ExternalId, l.NativeType, l.Verification, l.Strength })));
            return r with { References = all.Take(MaxReferences).ToList(), Revision = revision,
                ReferencesComplete = complete && r.ReferencesComplete && all.Count <= MaxReferences };
        }).ToList();
    }
    static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ---- ReadText ------------------------------------------------------------------------------------------------------
    // One memory's current text when its star opens, with the same eligibility and size checks as the listing. Nothing is
    // kept, cached or logged; anything unexpected reads as unavailable.
    public async Task<string?> ReadText(DiscoveryRecord record, CancellationToken ct)
    {
        if (record.Key.Provider != Provider || record.Kind != "memory" || !Guid.TryParse(record.Key.ExternalId, out _) || Token() is not { } token) return null;
        try
        {
            using var response = await Get("entry?id=" + Uri.EscapeDataString(record.Key.ExternalId), token, ct);
            if (!response.IsSuccessStatusCode) return null;
            // The entry also carries its edit history, so allow more than the text itself needs.
            using var payload = JsonDocument.Parse(await AdapterHttp.Read(response, 1024 * 1024, ct));
            if (!payload.RootElement.GetProperty("ok").GetBoolean()) return null;
            var entry = payload.RootElement.GetProperty("entry");
            var text = entry.GetProperty("content").GetString();
            var tags = entry.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToArray();
            var workspace = entry.GetProperty("workspace").GetString();
            if (entry.GetProperty("id").GetString() != record.Key.ExternalId || string.IsNullOrWhiteSpace(text) || text.Length > MaxText
                || tags.Any(t => t is null) || !layers.Any(l => l.Workspace == workspace)
                || !Eligible(tags!, entry.GetProperty("retracted_source").GetBoolean(), entry.GetProperty("validity_state").GetString()))
                return null;
            return text;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or KeyNotFoundException or InvalidOperationException
            or FormatException || e is OperationCanceledException && !ct.IsCancellationRequested) { return null; }
    }

    // Held, draft, retracted or no-longer-current memories are never stars.
    internal static bool Eligible(IEnumerable<string> tags, bool retractedSource, string? validity) =>
        !tags.Any(t => t.StartsWith("quarantine:", StringComparison.Ordinal) || t is "status:draft" or "status:retracted")
        && !retractedSource && validity == "current";

    public const int TitleLength = 80;
    // A short label, never the body: the first of the opening lines that still has text once links and markup are
    // removed, its first sentence, cut at a word to at most 80 characters.
    public static string Title(string text)
    {
        foreach (var line in text.Replace("\r\n", "\n").Split('\n').Take(5))
        {
            var label = MarkdownLink().Replace(line, "$1");
            label = EmptyParentheses().Replace(Link().Replace(label, " "), " ").Replace("**", "").Replace("`", "");
            label = Whitespace().Replace(label, " ").Trim().TrimStart('#', '>', '*', '-', ' ');
            label = label.Split(". ", 2)[0].Trim().TrimEnd(':', ';', ',', '-', ' ');
            if (label.Length == 0) continue;
            if (label.Length <= TitleLength) return label;
            var cut = label.LastIndexOf(' ', TitleLength - 1);
            if (cut < TitleLength / 2) cut = TitleLength - 1; // one long token: cut it, but never split a character
            if (char.IsHighSurrogate(label[cut - 1])) cut--;
            return label[..cut].TrimEnd(':', ';', ',', '-', ' ') + "…";
        }
        return "Untitled memory";
    }
    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")] private static partial Regex MarkdownLink();
    [GeneratedRegex(@"\b[a-zA-Z][a-zA-Z0-9+.-]*://[^\s)\]>]+")] private static partial Regex Link();
    [GeneratedRegex(@"\(\s*\)")] private static partial Regex EmptyParentheses();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
    [GeneratedRegex("^[a-z][a-z0-9_-]{0,63}$")] private static partial Regex LinkType();
    [GeneratedRegex("^[A-Za-z0-9_-]{1,100}$")] private static partial Regex TeamId();
}
