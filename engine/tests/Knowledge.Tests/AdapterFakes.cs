using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge;
using Microsoft.AspNetCore.WebUtilities;

namespace Knowledge.Tests;

// In-process fakes for the services the drive and second-brain adapters call. Every request ends here: nothing reaches
// the network. All data is fictional.
internal sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public readonly List<string> Requests = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        lock (Requests) Requests.Add($"{request.Method} {request.RequestUri!.Host}{request.RequestUri.AbsolutePath}");
        return respond(request, body);
    }
    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    public static HttpResponseMessage Json(JsonNode json, HttpStatusCode status = HttpStatusCode.OK) => Json(json.ToJsonString(), status);
}

// Tests that set process-wide state (the adapter secrets folder) run one at a time.
[CollectionDefinition(Name)]
public sealed class SharedStateCollection { public const string Name = "Shared engine state"; }

// A fresh adapter secrets folder (RHYTHM_ADAPTER_SECRETS_DIR) with fictional values.
internal sealed class TestSecrets : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "rhythm-test-secrets-" + Guid.NewGuid().ToString("N")[..8]);
    // Built at runtime, so no secret-shaped text sits in the repository.
    public static readonly string ClientJson = new JsonObject { ["installed"] = new JsonObject {
        ["client_id"] = "fictional-client" + ".apps.googleusercontent.com", ["client_secret"] = "fictional-" + "client-value",
        ["redirect_uris"] = new JsonArray("http://localhost") } }.ToJsonString();
    public TestSecrets()
    {
        System.IO.Directory.CreateDirectory(Directory);
        Environment.SetEnvironmentVariable("RHYTHM_ADAPTER_SECRETS_DIR", Directory);
    }
    public TestSecrets With(string name, string value) { File.WriteAllText(Path.Combine(Directory, name), value); return this; }
    public void Remove(string name) => File.Delete(Path.Combine(Directory, name));
    public string? Read(string name) => File.Exists(Path.Combine(Directory, name)) ? File.ReadAllText(Path.Combine(Directory, name)) : null;
    public TestSecrets Drive(bool edit = true)
    {
        With("drive_oauth_client", ClientJson).With("drive_read_token", FakeGoogle.ReadRefresh);
        if (edit) With("drive_edit_token", FakeGoogle.EditRefresh);
        return this;
    }
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("RHYTHM_ADAPTER_SECRETS_DIR", null);
        try { System.IO.Directory.Delete(Directory, true); } catch (IOException) { }
    }
}

// A fictional Google account: a Drive with folders and files, one Sheet (tab Budget), one Doc (headings and paragraphs),
// OAuth for the read and edit grants, and the consent flow's code exchange. Changes happen only through the fake API.
internal sealed class FakeGoogle
{
    public static readonly string ReadRefresh = "fictional-read-" + "refresh", EditRefresh = "fictional-edit-" + "refresh";
    public const string Folder = "application/vnd.google-apps.folder", SheetMime = "application/vnd.google-apps.spreadsheet",
        DocMime = "application/vnd.google-apps.document";
    // Version and Modified: the file's own, when a test gives them; else the fake's shared Version and a fixed time.
    public sealed record DriveFile(string Name, string Mime, string[] Parents, byte[]? Body = null, bool Trashed = false,
        int? Version = null, string? Modified = null);
    public readonly Dictionary<string, DriveFile> Files = new(StringComparer.Ordinal);
    // Files Drive refuses Rhythm (403), and Drive itself not answering (503): for citations' read-time checks.
    public readonly HashSet<string> Forbidden = new(StringComparer.Ordinal);
    // Files whose reads Drive refuses for a usage limit (a 403 whose reason is rateLimitExceeded), not for their sharing.
    public readonly HashSet<string> RateLimited = new(StringComparer.Ordinal);
    public bool Down;
    public readonly List<string> ContentReads = [];
    public readonly HashSet<string> Revoked = [];
    public readonly HashSet<string> FailingFolders = [];
    public int Version = 7;
    public List<List<JsonNode?>> Sheet = [["Item", "Amount"], ["Rent", 1000], ["Food", "=B2/10"]];
    public readonly List<(string Text, string Style)> Doc = [("Plan\n", "TITLE"), ("Week 1\n", "HEADING_1"), ("Easy runs.\n", "NORMAL_TEXT"),
        ("Week 2\n", "HEADING_1"), ("Long ride.\n", "NORMAL_TEXT"), ("Bricks.\n", "NORMAL_TEXT")];
    // The consent flow: the PKCE challenge the consent page saw, and the scopes Google says it granted.
    public string? Challenge;
    public string? GrantedScope;
    public string Account = "permission-1";
    public FakeHandler Handler { get; }

    public FakeGoogle()
    {
        Files["root-folder"] = new("Household", Folder, ["my-drive"]);
        Files["budget"] = new("Budget 2026", SheetMime, ["root-folder"]);
        Files["plan"] = new("Training plan", DocMime, ["root-folder"], Docx("Numbers live in https://docs.google.com/spreadsheets/d/budget/edit#gid=0 now."));
        Files["recipes"] = new("Recipes", Folder, ["root-folder"]);
        Files["soup"] = new("Soup.md", "text/markdown", ["recipes"], Encoding.UTF8.GetBytes("# Soup\nServe after https://docs.google.com/document/d/plan/edit"));
        Files["photo"] = new("Soup.jpg", "image/jpeg", ["recipes"]);
        Files["empty-folder"] = new("Nothing here", Folder, ["root-folder"]);
        Files["old-notes"] = new("Old notes", DocMime, ["root-folder"], Trashed: true);
        Handler = new FakeHandler(Respond);
    }

    // A Word document (what Drive exports a Google Doc as) holding one paragraph.
    public static byte[] Docx(string text)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
            writer.Write($"<w:document xmlns:w=\"urn:w\"><w:body><w:p><w:r><w:t>{WebUtility.HtmlEncode(text)}</w:t></w:r></w:p></w:body></w:document>");
        }
        return buffer.ToArray();
    }

    JsonObject Meta(string id, DriveFile f) => new()
    {
        ["id"] = id, ["name"] = f.Name, ["mimeType"] = f.Mime, ["modifiedTime"] = f.Modified ?? "2026-10-01T09:00:00.000Z", ["version"] = (f.Version ?? Version).ToString(),
        ["trashed"] = f.Trashed, ["parents"] = new JsonArray(f.Parents.Select(p => (JsonNode)p).ToArray()),
        ["size"] = (f.Body?.Length ?? 0).ToString(), ["webViewLink"] = f.Mime switch
        {
            SheetMime => $"https://docs.google.com/spreadsheets/d/{id}/edit?usp=drivesdk",
            DocMime => $"https://docs.google.com/document/d/{id}/edit?usp=drivesdk",
            Folder => $"https://drive.google.com/drive/folders/{id}",
            _ => $"https://drive.google.com/file/d/{id}/view?usp=drivesdk"
        }
    };

    HttpResponseMessage Respond(HttpRequestMessage r, string body)
    {
        var uri = r.RequestUri!;
        var query = QueryHelpers.ParseQuery(uri.Query);
        if (uri.Host == "oauth2.googleapis.com") return Token(QueryHelpers.ParseQuery(body));
        var auth = r.Headers.Authorization?.Parameter;
        if (uri.Host == "www.googleapis.com")
        {
            Assert.Equal(HttpMethod.Get, r.Method); // Drive is only ever read
            if (uri.AbsolutePath == "/drive/v3/about")
                return FakeHandler.Json(new JsonObject { ["user"] = new JsonObject { ["permissionId"] = Account, ["emailAddress"] = "alex@example.invalid" } });
            Assert.True(auth == ReadRefresh + "-access" || auth == EditRefresh + "-access");
            if (Down) return FakeHandler.Json("""{"error":{"code":503}}""", HttpStatusCode.ServiceUnavailable);
            if (uri.AbsolutePath == "/drive/v3/files")
            {
                var q = query["q"].ToString();
                var parent = q.Split('\'')[1];
                Assert.Equal($"'{parent}' in parents and trashed=false", q);
                if (FailingFolders.Contains(parent)) return FakeHandler.Json("{}", HttpStatusCode.InternalServerError);
                // Two per page, to exercise paging.
                var children = Files.Where(f => f.Value.Parents.Contains(parent) && !f.Value.Trashed).OrderBy(f => f.Key, StringComparer.Ordinal).ToArray();
                var start = query.TryGetValue("pageToken", out var token) ? int.Parse(token.ToString()) : 0;
                return FakeHandler.Json(new JsonObject
                {
                    ["nextPageToken"] = start + 2 < children.Length ? (start + 2).ToString() : null,
                    ["files"] = new JsonArray(children.Skip(start).Take(2).Select(f => (JsonNode)Meta(f.Key, f.Value)).ToArray())
                });
            }
            var path = uri.AbsolutePath["/drive/v3/files/".Length..];
            var id = path.Replace("/export", "");
            if (RateLimited.Contains(id))
                return FakeHandler.Json("""{"error":{"code":403,"message":"Rate Limit Exceeded","errors":[{"domain":"usageLimits","reason":"rateLimitExceeded","message":"Rate Limit Exceeded"}]}}""",
                    HttpStatusCode.Forbidden);
            if (Forbidden.Contains(id)) return FakeHandler.Json("""{"error":{"code":403,"message":"insufficientFilePermissions"}}""", HttpStatusCode.Forbidden);
            if (!Files.TryGetValue(id, out var file)) return FakeHandler.Json("""{"error":{"code":404}}""", HttpStatusCode.NotFound);
            if (path.EndsWith("/export") || query.ContainsKey("alt"))
            {
                lock (ContentReads) ContentReads.Add(id);
                if (file.Mime == SheetMime) return FakeHandler.Json("""{"error":{"code":403,"message":"exportSizeLimitExceeded"}}""", HttpStatusCode.Forbidden);
                if (query.TryGetValue("mimeType", out var asked) && asked == "text/markdown") return new(HttpStatusCode.OK) { Content = new StringContent(DocText()) };
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(file.Body ?? []) };
            }
            return FakeHandler.Json(Meta(id, file));
        }
        if (uri.Host == "sheets.googleapis.com") return Sheets(r, uri, query, body, auth);
        Assert.Equal("docs.googleapis.com", uri.Host);
        Assert.Equal(EditRefresh + "-access", auth);
        if (r.Method == HttpMethod.Get) return FakeHandler.Json(DocJson());
        var batch = JsonNode.Parse(body)!;
        if ((string?)batch["writeControl"]!["requiredRevisionId"] != $"r{Version}")
            return FakeHandler.Json("""{"error":{"message":"The required revision ID does not match the latest revision."}}""", HttpStatusCode.BadRequest);
        Apply(batch["requests"]!.AsArray()); Version++;
        return FakeHandler.Json("{}");
    }

    HttpResponseMessage Token(Dictionary<string, Microsoft.Extensions.Primitives.StringValues> form)
    {
        Assert.Equal("fictional-client.apps.googleusercontent.com", form["client_id"].ToString());
        Assert.Equal("fictional-client-value", form["client_secret"].ToString());
        if (form["grant_type"] == "authorization_code")
        {
            // PKCE: the verifier must hash to the challenge the consent page was given.
            var verifier = form["code_verifier"].ToString();
            var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            if (form["code"] != "fictional-code" || expected != Challenge || form["redirect_uri"] != Adapters.Drive.DriveAdapter.RedirectUri)
                return FakeHandler.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
            var refresh = GrantedScope!.Contains("spreadsheets") ? EditRefresh : ReadRefresh;
            return FakeHandler.Json(new JsonObject { ["access_token"] = refresh + "-access", ["expires_in"] = 3599, ["refresh_token"] = refresh,
                ["scope"] = GrantedScope, ["token_type"] = "Bearer" });
        }
        Assert.Equal("refresh_token", form["grant_type"].ToString());
        var grant = form["refresh_token"].ToString();
        if (Revoked.Contains(grant) || grant != ReadRefresh && grant != EditRefresh)
            return FakeHandler.Json("""{"error":"invalid_grant","error_description":"Token has been expired or revoked."}""", HttpStatusCode.BadRequest);
        return FakeHandler.Json(new JsonObject { ["access_token"] = grant + "-access", ["expires_in"] = 3599 });
    }

    HttpResponseMessage Sheets(HttpRequestMessage r, Uri uri, Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query, string body, string? auth)
    {
        var path = uri.AbsolutePath;
        Assert.StartsWith("/v4/spreadsheets/budget", path);
        if (r.Method != HttpMethod.Get) Assert.Equal(EditRefresh + "-access", auth); // changes use the edit grant
        if (path == "/v4/spreadsheets/budget")
            return FakeHandler.Json("""{"sheets":[{"properties":{"sheetId":0,"title":"Budget","index":0}}]}""");
        if (path == "/v4/spreadsheets/budget:batchUpdate")
        {
            var delete = JsonNode.Parse(body)!["requests"]![0]!["deleteDimension"]!["range"]!;
            Assert.Equal((0, "ROWS"), ((int)delete["sheetId"]!, (string?)delete["dimension"]));
            Sheet.RemoveRange((int)delete["startIndex"]!, (int)delete["endIndex"]! - (int)delete["startIndex"]!); Version++;
            return FakeHandler.Json("{}");
        }
        if (path == "/v4/spreadsheets/budget/values:batchGet")
            return FakeHandler.Json(new JsonObject { ["valueRanges"] = new JsonArray(new JsonObject { ["range"] = "Budget!A1:Z100", ["values"] = Grid(new("Budget", 1, 1, 26, 1000)) }) });
        var rangeText = Uri.UnescapeDataString(path["/v4/spreadsheets/budget/values/".Length..]);
        if (rangeText.EndsWith(":append"))
        {
            Assert.Equal(("RAW", "INSERT_ROWS"), (query["valueInputOption"].ToString(), query["insertDataOption"].ToString()));
            var row = JsonNode.Parse(body)!["values"]![0]!.AsArray().Select(v => v?.DeepClone()).ToList();
            Sheet.Add(row); Version++;
            return FakeHandler.Json(new JsonObject { ["updates"] = new JsonObject { ["updatedRange"] = $"Budget!A{Sheet.Count}:{A1Range.Letters(row.Count)}{Sheet.Count}" } });
        }
        var range = A1Range.Parse(rangeText)!;
        if (r.Method == HttpMethod.Get)
        {
            Assert.Equal("FORMULA", query["valueRenderOption"].ToString());
            return FakeHandler.Json(new JsonObject { ["values"] = Grid(range) });
        }
        var input = query["valueInputOption"].ToString();
        var rows = JsonNode.Parse(body)!["values"]!.AsArray();
        for (var i = 0; i < rows.Count; i++)
        {
            while (Sheet.Count < range.FirstRow + i) Sheet.Add([]);
            var target = Sheet[range.FirstRow - 1 + i];
            for (var j = 0; j < rows[i]!.AsArray().Count; j++)
            {
                while (target.Count < range.FirstColumn + j) target.Add("");
                var value = rows[i]![j]?.DeepClone();
                // USER_ENTERED reads a leading apostrophe as "text"; RAW keeps everything as given.
                if (input == "USER_ENTERED" && value is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith('\'')) value = s[1..];
                target[range.FirstColumn - 1 + j] = value;
            }
        }
        Version++;
        return FakeHandler.Json("{}");
    }

    JsonArray Grid(A1Range r)
    {
        var result = new JsonArray();
        for (var i = r.FirstRow - 1; i < Math.Min(Sheet.Count, r.LastRow); i++)
        {
            var row = new JsonArray();
            for (var j = r.FirstColumn - 1; j < Math.Min(Sheet[i].Count, r.LastColumn); j++) row.Add(Sheet[i][j]?.DeepClone());
            result.Add(row);
        }
        return result;
    }

    public string DocText() => string.Concat(Doc.Select(p => p.Text));
    JsonObject DocJson()
    {
        var content = new JsonArray(); var at = 1;
        foreach (var (text, style) in Doc)
        {
            content.Add(new JsonObject { ["startIndex"] = at, ["endIndex"] = at + text.Length, ["paragraph"] = new JsonObject {
                ["elements"] = new JsonArray(new JsonObject { ["textRun"] = new JsonObject { ["content"] = text } }),
                ["paragraphStyle"] = new JsonObject { ["namedStyleType"] = style } } });
            at += text.Length;
        }
        return new JsonObject { ["revisionId"] = $"r{Version}", ["body"] = new JsonObject { ["content"] = content } };
    }
    // Applies text edits to the flat body (index 1 is its first character) and splits it back into paragraphs; a
    // paragraph keeps the style of the one it grew from, and NORMAL_TEXT applies to the paragraphs it covers.
    void Apply(JsonArray requests)
    {
        var text = DocText(); var styles = Doc.SelectMany(p => Enumerable.Repeat(p.Style, p.Text.Length)).ToList();
        foreach (var r in requests)
        {
            if (r!["deleteContentRange"] is { } d) { int s = (int)d["range"]!["startIndex"]! - 1, e = (int)d["range"]!["endIndex"]! - 1; text = text.Remove(s, e - s); styles.RemoveRange(s, e - s); }
            else if (r["insertText"] is { } ins)
            {
                var t = (string)ins["text"]!; var s = ins["location"] is { } l ? (int)l["index"]! - 1 : text.Length - 1;
                var style = s < styles.Count ? styles[s] : styles[^1];
                text = text.Insert(s, t); styles.InsertRange(s, Enumerable.Repeat(style, t.Length));
            }
            else if (r["updateParagraphStyle"] is { } u)
                for (int i = (int)u["range"]!["startIndex"]! - 1; i < Math.Min((int)u["range"]!["endIndex"]! - 1, styles.Count); i++) styles[i] = "NORMAL_TEXT";
        }
        Doc.Clear(); var start = 0;
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') { Doc.Add((text[start..(i + 1)], styles[i])); start = i + 1; }
    }
}

// A fictional Second Brain: memories in a personal and a team workspace, a project registry, its own links, and the
// single-entry route a star's text is read from.
internal sealed class FakeBrain
{
    public static readonly string Token = "fictional-brain-" + "token";
    public const string Team = "team-shared";
    public sealed record Row(string Id, string Content, long Created, string Workspace = "personal", string? Team = null,
        string[]? Tags = null, string Validity = "current");
    public readonly List<Row> Rows = [];
    public readonly List<(string Source, string Target, string Type, double Weight, string Provenance)> Links = [];
    public readonly List<(string Workspace, string Id, string Name, string[] Aliases)> Projects = [];
    public bool Down, GraphDown;
    public FakeHandler Handler { get; }
    public static string Id(int n) => $"00000000-0000-4000-8000-{n:D12}";

    public FakeBrain()
    {
        Rows.Add(new(Id(1), "Morning walks by the canal help focus. Plan: https://docs.google.com/document/d/plan/edit", 1_790_000_000_000, Tags: ["project:fitness"]));
        Rows.Add(new(Id(2), "Batch cooking on Sundays saves the week.", 1_790_000_100_000, Tags: ["cooking"]));
        Rows.Add(new(Id(3), "A half-formed idea.", 1_790_000_200_000, Tags: ["status:draft"]));
        Rows.Add(new(Id(4), "Superseded advice.", 1_790_000_300_000, Validity: "superseded"));
        Rows.Add(new(Id(5), "Shared shopping list lives on the fridge.", 1_790_000_400_000, "company", Team));
        Projects.Add(("personal", "kitchen", "Kitchen", ["cooking"]));
        Links.Add((Id(1), Id(2), "supports", 0.8, "explicit"));
        Handler = new FakeHandler(Respond);
    }

    HttpResponseMessage Respond(HttpRequestMessage r, string _)
    {
        var uri = r.RequestUri!;
        Assert.Equal(HttpMethod.Get, r.Method); // never a write
        Assert.Equal("brain.example.invalid", uri.Host);
        Assert.Equal(Token, r.Headers.Authorization?.Parameter);
        if (Down) return FakeHandler.Json("{}", HttpStatusCode.ServiceUnavailable);
        var q = QueryHelpers.ParseQuery(uri.Query);
        if (uri.AbsolutePath == "/entry")
        {
            var row = Rows.SingleOrDefault(x => x.Id == q["id"].ToString());
            if (row is null) return FakeHandler.Json("""{"ok":false}""", HttpStatusCode.NotFound);
            return FakeHandler.Json(JsonSerializer.Serialize(new { ok = true, entry = new {
                id = row.Id, content = row.Content, tags = row.Tags ?? [], source = "api", created_at = row.Created, updated_at = row.Created,
                workspace = row.Workspace, validity_state = row.Validity, retracted_source = false, history = Array.Empty<object>() } }));
        }
        string workspace = q["workspace"].ToString(), team = q.TryGetValue("team", out var t) ? t.ToString() : "";
        Assert.True(workspace == "personal" ? team == "" : workspace == "company" && team != "");
        var inLayer = Rows.Where(x => x.Workspace == workspace && (x.Team ?? "") == team).ToList();
        if (uri.AbsolutePath == "/projects")
            return FakeHandler.Json(JsonSerializer.Serialize(new { projects = Projects.Where(p => p.Workspace == workspace)
                .Select(p => new { id = p.Id, name = p.Name, aliases = p.Aliases }) }));
        if (uri.AbsolutePath == "/graph")
        {
            if (GraphDown) return FakeHandler.Json("{}", HttpStatusCode.ServiceUnavailable);
            var ids = inLayer.Select(x => x.Id).ToHashSet();
            var edges = Links.Where(l => ids.Contains(l.Source)).ToArray();
            return FakeHandler.Json(JsonSerializer.Serialize(new { ok = true,
                nodes = edges.SelectMany(e => new[] { e.Source, e.Target }).Distinct().Select(id => new { id, content = "graph node text the engine ignores" }),
                edges = edges.Select(e => new { source = e.Source, target = e.Target, type = e.Type, weight = e.Weight, provenance = e.Provenance }) }));
        }
        Assert.Equal("/list", uri.AbsolutePath);
        var before = q.TryGetValue("before", out var b) ? long.Parse(b.ToString()) : long.MaxValue;
        var page = inLayer.Where(x => x.Created <= before).OrderByDescending(x => x.Created).Take(int.Parse(q["n"].ToString()));
        return FakeHandler.Json(JsonSerializer.Serialize(page.Select(x => new {
            id = x.Id, content = x.Content, tags = JsonSerializer.Serialize(x.Tags ?? []), source = "api", created_at = x.Created,
            workspace_id = x.Team ?? "ws-personal", workspace = x.Workspace, validity_state = x.Validity, retracted_source = false })));
    }
}
