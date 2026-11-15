using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Knowledge.Relationships;

namespace Knowledge.Adapters.Drive;

// Google Drive, with the install's own Google Cloud project (docs/adapters/drive.md).
//   DocumentSource: the files under adapters.drive.roots (folder IDs; none by default) as document stars, each folder on
//                   the way down as a small folder star its files hang from, and the Drive links inside each document as
//                   links. Nothing is copied into Rhythm: a body is read in memory when its file's version changes, and
//                   only the links found in it and a similarity vector are kept. A dashboard's contents feed reads the
//                   file live, a Sheet's tabs as rows of cells and a Doc as Markdown, with the file's revision.
//   Searchable:     only when adapters.embeddings.sources lists drive (off by default): a document's text, read again by
//                   the index job and kept as semantic_search passages of that version (SB-P2-05, Q-097).
//   Writer:         a dashboard's declared sheet and doc writes, only while adapters.drive.edits is on (DriveWrites.cs).
//   Connect:        `rhythm connect drive` signs in to Google through its consent page (DriveConnect.cs).
//   CitedSource:    a native memory citing a document reads the file's metadata now, never its contents (Check, SB-P4-10).
// The app opens a Drive star in Google's own viewer, where Google checks the viewer's own access.
public sealed partial class DriveAdapter(HttpMessageHandler? handler = null) : IDocumentSource, ISearchableSource, IDashboardWriter, IAdapterConnect,
    ICitedSource
{
    public const string AdapterName = "drive", Provider = "drive";
    const string FolderMime = "application/vnd.google-apps.folder", Office = "application/vnd.openxmlformats-officedocument.";
    const int MaxRoots = 20, MaxFolders = 1000, MaxPages = 50, MaxReferences = 100, MaxContentLinks = 90, ContentReadsPerPass = 400,
        ContentConcurrency = 4, MaxText = 200_000, MaxCells = 50_000, MaxTabs = 20;
    static readonly TimeSpan ContentBudget = TimeSpan.FromSeconds(150);
    // The file version plus how its content was processed; changing the processing re-reads every document once.
    const string ContentPipeline = "content-v2";

    public string Name => AdapterName;
    public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } =
        new Dictionary<string, ProviderPolicy> { [Provider] = new(SourceAuthority.LiveSource) };

    GoogleApi? google;
    IReadOnlyList<string> roots = [];
    string owner = RhythmConfig.Household, account = Provider;
    bool edits;

    public void Configure(AdapterContext context)
    {
        var settings = context.Settings;
        roots = settings.List("roots");
        if (roots.Count > MaxRoots || roots.Any(r => !DriveId().IsMatch(r)) || roots.Distinct(StringComparer.Ordinal).Count() != roots.Count)
            throw new ConfigException($"adapters.drive.roots lists up to {MaxRoots} distinct folder IDs (letters, digits, '-' and '_')");
        owner = settings.Owner ?? RhythmConfig.Household;
        account = settings.StarAccount(Provider);
        edits = settings.Bool("edits", false);
        google = new(settings, AdapterHttp.Client(handler, TimeSpan.FromSeconds(30)));
    }
    GoogleApi Google => google ?? throw new InvalidOperationException("The drive adapter is not configured");

    ResourceKey Key(string id) => new(owner, Provider, account, id);
    string Scope => owner == RhythmConfig.Household ? "shared" : "private";

    sealed record Item(string Id, string Name, string Mime, string? Version, DateTimeOffset? Modified, string? Link, string[] Parents, long Size);
    static string Revision(Item item) => item.Version ?? item.Modified?.ToString("O") ?? "unversioned";
    static string ContentRevision(Item item) => Revision(item) + ";" + ContentPipeline;
    const string ItemFields = "id,name,mimeType,modifiedTime,version,webViewLink,parents,size";

    // ---- DocumentSource ----------------------------------------------------------------------------------------------
    public async Task<SourceListing> List(SourcePass pass, CancellationToken ct)
    {
        if (roots.Count == 0) return new([], Complete: true);
        var token = await Google.AccessToken(GoogleApi.ReadGrant, ct) ?? throw new UnauthorizedAccessException(Google.Problem(GoogleApi.ReadGrant));
        var (folders, files, complete) = await Crawl(token, ct);
        var observed = DateTimeOffset.UtcNow;
        // Newest first within the document budget; every folder between a chosen file and its root becomes a hub.
        var hubs = new HashSet<string>(StringComparer.Ordinal);
        var chosen = new List<Item>();
        foreach (var file in files.Values.OrderByDescending(f => f.Modified).ThenBy(f => f.Id, StringComparer.Ordinal))
        {
            var above = Ancestors(file, folders).Where(f => !hubs.Contains(f)).ToArray();
            if (chosen.Count + hubs.Count + above.Length + 1 > pass.DocumentsOf(owner)) { complete = false; break; }
            chosen.Add(file); hubs.UnionWith(above);
        }
        // A file is read again when it changed, or when its vector is another embedding model's.
        var reads = chosen.Where(f => !(pass.Previous.TryGetValue(Key(f.Id), out var previous) && previous.ContentRevision == ContentRevision(f)
                && !pass.Outdated(previous)))
            .Take(ContentReadsPerPass).ToList();
        var content = await ReadContents(token, reads, pass, ct);
        var records = hubs.Select(id => folders[id])
            .Select(f => Record(f, "theme", RelationshipDiscovery.DriveFolderPrefix + f.Id, InFolder(f, hubs), observed)).ToList();
        foreach (var file in chosen)
        {
            var record = Record(file, "document", ViewUrl(file), InFolder(file, hubs), observed);
            if (content.TryGetValue(file.Id, out var read))
                record = record with { References = [.. record.References, .. read.Links], ContentRevision = ContentRevision(file), Embedding = read.Vector };
            else if (pass.Previous.TryGetValue(record.Key, out var previous) && previous.ContentRevision is not null)
                // Unchanged, or not re-read this pass: the links and vector from the last read of its content.
                record = record with {
                    References = [.. record.References, .. previous.References.Where(r => r.Locator.StartsWith("content:", StringComparison.Ordinal))],
                    ContentRevision = previous.ContentRevision, Embedding = previous.Embedding,
                    ReferencesComplete = previous.ContentRevision == ContentRevision(file) };
            else record = record with { ReferencesComplete = false };
            records.Add(record with { References = record.References.Take(MaxReferences).ToList() });
        }
        return new(records, complete);
    }

    DiscoveryRecord Record(Item item, string kind, string url, IReadOnlyList<Reference> references, DateTimeOffset observed) =>
        new(Key(item.Id), Scope, kind, item.Name.Length <= 1000 ? item.Name : item.Name[..1000], url, Revision(item),
            item.Modified <= observed ? item.Modified : null, observed, references);
    static string ViewUrl(Item file) => RelationshipDiscovery.CanonicalUrl(file.Link) == $"https://drive.google.com/file/d/{file.Id}"
        ? file.Link! : $"https://drive.google.com/file/d/{file.Id}/view";
    IReadOnlyList<Reference> InFolder(Item item, HashSet<string> hubs) => item.Parents.Where(hubs.Contains).Distinct()
        .Select(p => new Reference(Key(p), null, "drive:parents", "In folder", "in_folder", true, "verified", 1, true)).ToList();
    static IEnumerable<string> Ancestors(Item item, Dictionary<string, Item> folders)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(item.Parents);
        while (pending.Count > 0 && found.Count < 200)
            if (pending.Pop() is var id && folders.TryGetValue(id, out var folder) && found.Add(id))
                foreach (var parent in folder.Parents) pending.Push(parent);
        return found;
    }

    // The roots, then every folder below them, breadth first. A root that no longer exists (or is in the trash) lists
    // nothing; any other failed call makes the listing incomplete, so nothing leaves the map because of it.
    async Task<(Dictionary<string, Item> Folders, Dictionary<string, Item> Files, bool Complete)> Crawl(string token, CancellationToken ct)
    {
        var folders = new Dictionary<string, Item>(StringComparer.Ordinal);
        var files = new Dictionary<string, Item>(StringComparer.Ordinal);
        var complete = true;
        var queue = new Queue<string>();
        foreach (var root in roots)
        {
            var (status, body) = await Google.Send(HttpMethod.Get,
                $"{GoogleApi.DriveUrl}/files/{root}?fields={ItemFields},trashed&supportsAllDrives=true", token, null, 65_536, ct);
            if (status == HttpStatusCode.NotFound) continue;
            if (status != HttpStatusCode.OK) { complete = false; continue; }
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("trashed", out var trashed) && trashed.ValueKind == JsonValueKind.True) continue;
            if (Parse(json.RootElement) is not { } item || item.Id != root) { complete = false; continue; }
            // A root may be one file rather than a folder.
            if (item.Mime != FolderMime) { if (Export(item.Mime) is not null) files.TryAdd(item.Id, item with { Parents = [] }); continue; }
            if (folders.TryAdd(root, item with { Parents = item.Parents.Where(roots.Contains).ToArray() })) queue.Enqueue(root);
        }
        while (queue.Count > 0)
        {
            if (folders.Count > MaxFolders) { complete = false; break; }
            var folder = queue.Dequeue();
            string? page = null;
            for (var pages = 0; ; pages++)
            {
                if (pages == MaxPages) { complete = false; break; }
                var query = new Dictionary<string, string>
                {
                    ["q"] = $"'{folder}' in parents and trashed=false", ["pageSize"] = "1000", ["corpora"] = "allDrives",
                    ["includeItemsFromAllDrives"] = "true", ["supportsAllDrives"] = "true", ["fields"] = $"nextPageToken,files({ItemFields})"
                };
                if (page is not null) query["pageToken"] = page;
                var (status, body) = await Google.Send(HttpMethod.Get,
                    $"{GoogleApi.DriveUrl}/files?" + string.Join('&', query.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value))),
                    token, null, 16 * 1024 * 1024, ct);
                if (status != HttpStatusCode.OK) { complete = false; break; }
                using var json = JsonDocument.Parse(body);
                if (!json.RootElement.TryGetProperty("files", out var listed) || listed.ValueKind != JsonValueKind.Array) { complete = false; break; }
                foreach (var child in listed.EnumerateArray().Select(Parse).OfType<Item>())
                {
                    if (child.Mime == FolderMime) { if (folders.TryAdd(child.Id, child)) queue.Enqueue(child.Id); }
                    else if (Export(child.Mime) is not null) files.TryAdd(child.Id, child);
                }
                page = Text(json.RootElement, "nextPageToken");
                if (page is null) break;
            }
        }
        return (folders, files, complete);
    }

    static Item? Parse(JsonElement f) => f.ValueKind == JsonValueKind.Object && Text(f, "id") is { } id && DriveId().IsMatch(id)
        && Text(f, "mimeType") is { } mime
        ? new(id, Text(f, "name") ?? "Untitled", mime, Text(f, "version"),
            Text(f, "modifiedTime") is { } modified && DateTimeOffset.TryParse(modified, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : null,
            Text(f, "webViewLink"),
            f.TryGetProperty("parents", out var parents) && parents.ValueKind == JsonValueKind.Array
                ? parents.EnumerateArray().Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() : null).OfType<string>().ToArray() : [],
            Text(f, "size") is { } size && long.TryParse(size, out var bytes) ? bytes : 0)
        : null;
    static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // Each changed document's links (and vector), read with bounded concurrency inside a time budget. A failed read is
    // tried again next pass; one that can't be read (too large, no exportable body) counts as read with no links.
    async Task<IReadOnlyDictionary<string, (IReadOnlyList<Reference> Links, float[]? Vector)>> ReadContents(
        string token, IReadOnlyList<Item> reads, SourcePass pass, CancellationToken ct)
    {
        var results = new ConcurrentDictionary<string, (IReadOnlyList<Reference>, float[]?)>(StringComparer.Ordinal);
        var deadline = DateTimeOffset.UtcNow + ContentBudget;
        using var gate = new SemaphoreSlim(ContentConcurrency);
        await Task.WhenAll(reads.Select(async file =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (DateTimeOffset.UtcNow > deadline) return;
                var (retry, body, format) = await Download(token, file, ct);
                if (retry) return;
                if (body is null) { results[file.Id] = ([], null); return; }
                var extracted = RelationshipContent.Extract(body, format, MaxContentLinks);
                // Only text that reads like words is compared: not mostly numeric sheets or undecodable PDF glyphs.
                results[file.Id] = (extracted.Links, SimilaritySuggestions.Readable(extracted.Text)
                    ? pass.Vector(Key(file.Id), ContentRevision(file), file.Name, extracted.Text) : null);
            }
            finally { gate.Release(); }
        }));
        return results;
    }

    async Task<(bool Retry, byte[]? Body, RelationshipContent.Format Format)> Download(string token, Item file, CancellationToken ct)
    {
        var (export, format) = Export(file.Mime)!.Value;
        if (export == "" || file.Size > RelationshipContent.MaximumDownload) return (false, null, format);
        var url = export == "media" ? $"{GoogleApi.DriveUrl}/files/{file.Id}?alt=media&supportsAllDrives=true"
            : $"{GoogleApi.DriveUrl}/files/{file.Id}/export?mimeType={Uri.EscapeDataString(export)}";
        try
        {
            var (status, body) = await Google.Send(HttpMethod.Get, url, token, null, RelationshipContent.MaximumDownload, ct);
            // Too large to export, or a body Google will not hand out, is final for this version.
            if (status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.BadRequest) return (false, null, format);
            return status == HttpStatusCode.OK ? (false, body, format) : (true, null, format);
        }
        catch (Exception e) when (e is HttpRequestException or IOException || e is OperationCanceledException && !ct.IsCancellationRequested)
        { return (true, null, format); }
    }

    // A document's text for its passages (ISearchableSource): the body the map reads for its links, read again now, when
    // it reads as words (the rule its vector follows), with the revision the map gives the file. Null for a folder, a file
    // gone or in the trash, or one with no readable body; a failed call is thrown, so the index job tries it again later.
    // A file whose version has moved past the record's is not downloaded: its new revision comes back with no text, which
    // the index takes as another revision than its star's and passes over until the next map pass.
    public async Task<SourceText?> SearchText(DiscoveryRecord record, CancellationToken ct)
    {
        var id = record.Key.ExternalId;
        if (record.Key.Provider != Provider || record.Kind != "document" || !DriveId().IsMatch(id)) return null;
        var token = await Google.AccessToken(GoogleApi.ReadGrant, ct) ?? throw new UnauthorizedAccessException(Google.Problem(GoogleApi.ReadGrant));
        var (status, body) = await Google.Send(HttpMethod.Get, $"{GoogleApi.DriveUrl}/files/{id}?fields={ItemFields},trashed&supportsAllDrives=true", token, null, 65_536, ct);
        if (status == HttpStatusCode.NotFound) return null;
        if (status != HttpStatusCode.OK) throw new HttpRequestException("Drive did not describe the file", null, status);
        Item? item;
        using (var json = JsonDocument.Parse(body))
        {
            if (json.RootElement.TryGetProperty("trashed", out var trashed) && trashed.ValueKind == JsonValueKind.True) return null;
            item = Parse(json.RootElement);
        }
        if (item is null || item.Id != id || Export(item.Mime) is null) return null;
        if (Revision(item) != record.Revision) return new("", Revision(item));
        var (retry, content, format) = await Download(token, item, ct);
        if (retry) throw new HttpRequestException("Drive did not hand out the file's contents");
        if (content is null) return null;
        var text = RelationshipContent.Extract(content, format, 0).Text;
        return SimilaritySuggestions.Readable(text) ? new(text, Revision(item)) : null;
    }

    // How a document's body is fetched for its links ("media" download, an export type, or "" for none) and parsed.
    static (string, RelationshipContent.Format)? Export(string mime) => mime switch
    {
        "application/vnd.google-apps.document" => (Office + "wordprocessingml.document", RelationshipContent.Format.OfficeOpenXml),
        "application/vnd.google-apps.spreadsheet" => (Office + "spreadsheetml.sheet", RelationshipContent.Format.OfficeOpenXml),
        "application/vnd.google-apps.presentation" => (Office + "presentationml.presentation", RelationshipContent.Format.OfficeOpenXml),
        "application/vnd.google-apps.drawing" or "application/vnd.google-apps.form" => ("", RelationshipContent.Format.Text),
        Office + "wordprocessingml.document" or Office + "spreadsheetml.sheet" or Office + "presentationml.presentation"
            => ("media", RelationshipContent.Format.OfficeOpenXml),
        "application/msword" or "application/vnd.ms-excel" or "application/vnd.ms-powerpoint" => ("media", RelationshipContent.Format.LegacyOffice),
        "application/pdf" => ("media", RelationshipContent.Format.Pdf),
        "text/plain" or "text/markdown" or "text/csv" => ("media", RelationshipContent.Format.Text),
        _ => null
    };

    // A dashboard's contents feed: the file as Google structures it, read now. A Sheet's tabs as rows of cells, a Doc as
    // Markdown, the rest as text; with revision, which a write to it passes back.
    public async Task<JsonObject?> Contents(DiscoveryRecord record, CancellationToken ct)
    {
        var id = record.Key.ExternalId;
        if (record.Key.Provider != Provider || !DriveId().IsMatch(id)) return null;
        if (await Google.AccessToken(GoogleApi.ReadGrant, ct) is not { } token) return Unavailable(Google.Problem(GoogleApi.ReadGrant));
        var (status, body) = await Google.Send(HttpMethod.Get,
            $"{GoogleApi.DriveUrl}/files/{id}?fields=id,name,mimeType,modifiedTime,trashed,webViewLink,version&supportsAllDrives=true", token, null, 65_536, ct);
        if (status != HttpStatusCode.OK) return Unavailable("This file can't be read from Drive.");
        using var meta = JsonDocument.Parse(body);
        var file = meta.RootElement;
        if (Text(file, "id") != id) return Unavailable("This file can't be read from Drive.");
        if (file.TryGetProperty("trashed", out var trashed) && trashed.ValueKind == JsonValueKind.True) return Unavailable("This file is in the Drive trash.");
        var mime = Text(file, "mimeType");
        var payload = new JsonObject
        {
            ["status"] = "ok", ["title"] = Text(file, "name") ?? record.Title, ["url"] = Text(file, "webViewLink"),
            ["modifiedAt"] = Text(file, "modifiedTime"), ["revision"] = Text(file, "version")
        };
        if (mime == "application/vnd.google-apps.spreadsheet") return await Sheet(payload, id, token, ct);
        var exported = $"{GoogleApi.DriveUrl}/files/{id}/export?mimeType=";
        var media = $"{GoogleApi.DriveUrl}/files/{id}?alt=media&supportsAllDrives=true";
        (string Url, string Format, RelationshipContent.Format? Extract)? read = mime switch
        {
            "application/vnd.google-apps.document" => (exported + Uri.EscapeDataString("text/markdown"), "markdown", null),
            "application/vnd.google-apps.presentation" => (exported + Uri.EscapeDataString("text/plain"), "text", null),
            "application/pdf" => (media, "text", RelationshipContent.Format.Pdf),
            Office + "wordprocessingml.document" or Office + "presentationml.presentation" or Office + "spreadsheetml.sheet"
                => (media, "text", RelationshipContent.Format.OfficeOpenXml),
            "text/markdown" => (media, "markdown", null),
            "text/csv" => (media, "csv", null),
            "text/plain" => (media, "text", null),
            _ => null
        };
        if (read is not { } source) return Unavailable("This kind of Drive file has no readable contents.");
        (status, body) = await Google.Send(HttpMethod.Get, source.Url, token, null, RelationshipContent.MaximumDownload, ct);
        if (status != HttpStatusCode.OK) return Unavailable("This file's contents can't be read from Drive right now.");
        payload["format"] = source.Format;
        var text = source.Extract is { } format ? RelationshipContent.Extract(body, format, 1).Text : Encoding.UTF8.GetString(body);
        payload["text"] = text.Length > MaxText ? text[..MaxText] : text;
        payload["truncated"] = text.Length > MaxText;
        return payload;
    }

    // Every tab's cells in rows: numbers as numbers, dates and times as Google shows them. Read with the Sheets API.
    async Task<JsonObject> Sheet(JsonObject payload, string id, string token, CancellationToken ct)
    {
        var (status, body) = await Google.Send(HttpMethod.Get, $"{GoogleApi.SheetsUrl}/{id}?fields=sheets.properties(title,index)", token, null, 1_000_000, ct);
        if (status != HttpStatusCode.OK) return SheetUnavailable(body);
        string[] tabs;
        using (var listed = JsonDocument.Parse(body))
            tabs = listed.RootElement.TryGetProperty("sheets", out var sheets) && sheets.ValueKind == JsonValueKind.Array
                ? sheets.EnumerateArray().Select(s => s.GetProperty("properties"))
                    .OrderBy(p => p.TryGetProperty("index", out var i) && i.TryGetInt32(out var n) ? n : 0)
                    .Select(p => Text(p, "title")).OfType<string>().Take(MaxTabs).ToArray() : [];
        var result = new JsonArray(); var cells = 0; var truncated = false;
        if (tabs.Length > 0)
        {
            var ranges = string.Join("&", tabs.Select(t => "ranges=" + Uri.EscapeDataString("'" + t.Replace("'", "''") + "'")));
            (status, body) = await Google.Send(HttpMethod.Get, $"{GoogleApi.SheetsUrl}/{id}/values:batchGet?{ranges}"
                + "&majorDimension=ROWS&valueRenderOption=UNFORMATTED_VALUE&dateTimeRenderOption=FORMATTED_STRING", token, null, 16_000_000, ct);
            if (status != HttpStatusCode.OK) return SheetUnavailable(body);
            using var values = JsonDocument.Parse(body);
            var ranged = values.RootElement.TryGetProperty("valueRanges", out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToArray() : [];
            for (var i = 0; i < tabs.Length; i++)
            {
                var rows = new JsonArray();
                if (i < ranged.Length && ranged[i].TryGetProperty("values", out var grid) && grid.ValueKind == JsonValueKind.Array)
                    foreach (var row in grid.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Array))
                    {
                        if (cells + row.GetArrayLength() > MaxCells) { truncated = true; break; }
                        cells += row.GetArrayLength();
                        rows.Add(JsonNode.Parse(row.GetRawText()));
                    }
                result.Add(new JsonObject { ["title"] = tabs[i], ["rows"] = rows });
                if (truncated) break;
            }
        }
        payload["format"] = "sheet"; payload["sheets"] = result; payload["truncated"] = truncated;
        return payload;
    }
    static JsonObject SheetUnavailable(byte[] body) => Unavailable(GoogleApi.Disabled(body)
        ? "The Google Sheets API isn't switched on in your Google Cloud project yet." : "This Sheet can't be read from Google right now.");
    static JsonObject Unavailable(string reason) => new() { ["status"] = "unavailable", ["reason"] = reason };

    // Whether a stored copy of a Drive file is still the file's current version (retrieval checks live providers).
    public async Task<bool> Current(string provider, string externalId, string? revision, CancellationToken ct)
    {
        if (provider != Provider || !DriveId().IsMatch(externalId) || revision is null) return false;
        if (await Google.AccessToken(GoogleApi.ReadGrant, ct) is not { } token) return false;
        var (status, body) = await Google.Send(HttpMethod.Get,
            $"{GoogleApi.DriveUrl}/files/{externalId}?fields=version,trashed&supportsAllDrives=true", token, null, 65_536, ct);
        if (status != HttpStatusCode.OK) return false;
        using var json = JsonDocument.Parse(body);
        return !(json.RootElement.TryGetProperty("trashed", out var trashed) && trashed.ValueKind == JsonValueKind.True)
            && Text(json.RootElement, "version") == revision;
    }

    // ---- CitedSource (SB-P4-10) ---------------------------------------------------------------------------------------
    // One file as Drive describes it now, for a native memory that cites it (Core/MemoryCitations.cs): its version, when it
    // was last modified, its name and its folders. One metadata read with the read grant, nothing downloaded and nothing
    // kept: List, SearchText and the map's stored rows are untouched, so the sync's schedule and its rows stay as they are.
    // Drive answers 404 both for a file deleted for good and for one no longer shared with Rhythm's Google account, so a
    // gone file says both; a 401 or 403, or a sign-in Google no longer accepts, is unreadable; Drive or Google's sign-in
    // service not answering is unchecked, which says nothing about the file, and so is a 403 or 429 that only says Rhythm
    // reached one of Drive's usage limits (RateLimited): that says nothing about the file's sharing.
    public async Task<CitedCheck> Check(string provider, string externalId, CancellationToken ct)
    {
        if (provider != Provider || !DriveId().IsMatch(externalId)) return CitedCheck.Not(CitedCheck.Gone, "This is not a Drive file id.");
        if (await Google.AccessToken(GoogleApi.ReadGrant, ct) is not { } token)
            return CitedCheck.Not(Google.Outage(GoogleApi.ReadGrant) ? CitedCheck.Unchecked : CitedCheck.Unreadable, Google.Problem(GoogleApi.ReadGrant));
        var (status, body) = await Google.Send(HttpMethod.Get,
            $"{GoogleApi.DriveUrl}/files/{externalId}?fields={ItemFields},trashed&supportsAllDrives=true", token, null, 65_536, ct);
        if (status == HttpStatusCode.NotFound)
            return CitedCheck.Not(CitedCheck.Gone, "Drive has no such file now: it was deleted for good, or is no longer shared with Rhythm's Google account.");
        if (status is HttpStatusCode.TooManyRequests || status is HttpStatusCode.Forbidden && RateLimited(body))
            return CitedCheck.Not(CitedCheck.Unchecked, "Drive asked Rhythm to slow down just now (one of its usage limits), so the file was not checked.");
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return CitedCheck.Not(CitedCheck.Unreadable, "Drive refuses Rhythm this file now: its sharing or Rhythm's access to it changed.");
        if (status != HttpStatusCode.OK) return CitedCheck.Not(CitedCheck.Unchecked, "Drive did not answer just now.");
        using var json = JsonDocument.Parse(body);
        if (Parse(json.RootElement) is not { } item || item.Id != externalId)
            return CitedCheck.Not(CitedCheck.Unchecked, "Drive gave an answer Rhythm could not read.");
        if (json.RootElement.TryGetProperty("trashed", out var trashed) && trashed.ValueKind == JsonValueKind.True)
            return CitedCheck.Not(CitedCheck.Trashed, "The file is in the Drive trash.");
        return new(CitedCheck.Ok, Revision(item), item.Modified, item.Name.Length <= 1000 ? item.Name : item.Name[..1000],
            [.. item.Parents.Where(p => DriveId().IsMatch(p)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(20)]);
    }

    // Whether a refusal's body says only that a usage limit was reached (Drive API v3's error reasons, and the newer error
    // status), not that the file's sharing or Rhythm's access refuses it. A body Rhythm cannot read counts as a refusal.
    static readonly HashSet<string> LimitReasons = new(StringComparer.Ordinal)
        { "userRateLimitExceeded", "rateLimitExceeded", "dailyLimitExceeded", "quotaExceeded", "sharingRateLimitExceeded" };

    internal static bool RateLimited(byte[] body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object) return false;
            if (error.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String && s.GetString() == "RESOURCE_EXHAUSTED") return true;
            return error.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
                && errors.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("reason", out var r)
                    && r.ValueKind == JsonValueKind.String && LimitReasons.Contains(r.GetString()!));
        }
        catch (JsonException) { return false; }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,200}$")] private static partial Regex DriveId();
}
