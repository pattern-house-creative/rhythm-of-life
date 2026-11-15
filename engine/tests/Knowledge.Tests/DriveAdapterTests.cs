using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge;
using Knowledge.Adapters;
using Knowledge.Adapters.Drive;
using Knowledge.Relationships;
using Microsoft.AspNetCore.WebUtilities;

namespace Knowledge.Tests;

[Collection(SharedStateCollection.Name)]
public sealed class DriveAdapterTests : IDisposable
{
    readonly TestSecrets secrets = new TestSecrets().Drive();
    readonly FakeGoogle google = new();
    public void Dispose() => secrets.Dispose();

    static Dictionary<string, object?> Settings(bool edits = true, params string[] roots) => new()
    {
        ["enabled"] = "true", ["edits"] = edits ? "true" : "false", ["roots"] = roots.Cast<object>().ToList()
    };
    DriveAdapter Drive(Dictionary<string, object?>? values = null)
    {
        var drive = new DriveAdapter(google.Handler);
        drive.Configure(new(new AdapterSettings("drive", values ?? Settings(true, "root-folder", "gone-folder")), TestSettings.Use(), null!, null!));
        return drive;
    }
    static SourcePass Pass(IEnumerable<DiscoveryRecord>? previous = null, int documents = 1000) =>
        new((previous ?? []).ToDictionary(r => r.Key), (_, _, _, _) => null, 3000, documents);
    static ResourceKey Key(string id) => new(RhythmConfig.Household, "drive", "drive", id);
    static JsonElement Change(object change) => JsonSerializer.SerializeToElement(change);

    [Fact] public void Roots_are_folder_ids_and_checked_at_start()
    {
        foreach (var bad in new[] { "../up", "has space", "" })
            Assert.Throws<ConfigException>(() => Drive(Settings(true, bad)));
        Assert.Throws<ConfigException>(() => Drive(Settings(true, "same", "same")));
        Assert.Throws<ConfigException>(() => Drive(Settings(true, Enumerable.Range(0, 21).Select(i => $"folder-{i}").ToArray())));
        Assert.Equal("drive", Drive().Name);
    }

    [Fact] public async Task The_files_under_its_roots_become_stars_with_their_folders_and_links()
    {
        var drive = Drive();
        var listing = await drive.List(Pass(), default);
        // A root that no longer exists lists nothing and keeps the listing complete; trashed, empty and media are left out.
        Assert.True(listing.Complete);
        var byId = listing.Records.ToDictionary(r => r.Key.ExternalId);
        Assert.Equal(["budget", "plan", "recipes", "root-folder", "soup"], byId.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(("theme", "Household", "https://drive.google.com/drive/folders/root-folder"), (byId["root-folder"].Kind, byId["root-folder"].Title, byId["root-folder"].Url));
        Assert.Equal(("document", "7", "https://docs.google.com/spreadsheets/d/budget/edit?usp=drivesdk"), (byId["budget"].Kind, byId["budget"].Revision, byId["budget"].Url));
        Assert.All(listing.Records, r => Assert.Equal(("household", "shared"), (r.Key.Subject, r.Scope)));
        // Each file and folder hangs from its folder, as a verified structural link.
        Assert.Equal(Key("recipes"), Assert.Single(byId["soup"].References, x => x.NativeType == "in_folder").Target);
        Assert.Equal(Key("root-folder"), Assert.Single(byId["recipes"].References).Target);
        Assert.Empty(byId["root-folder"].References);
        // Links inside each document: the Doc (exported as Word) cites the Sheet, the Markdown file cites the Doc.
        Assert.Contains(byId["plan"].References, x => x.Locator.StartsWith("content:") && RelationshipDiscovery.DriveFileId(x.Url) == "budget");
        Assert.Contains(byId["soup"].References, x => RelationshipDiscovery.DriveFileId(x.Url) == "plan");
        Assert.Equal(["budget", "plan", "soup"], google.ContentReads.Order(StringComparer.Ordinal));
        RelationshipDiscovery.Validate(new(listing.Records));
        var links = RelationshipDiscovery.Discover(listing.Records);
        Assert.Contains(links, l => l.Source == Key("plan") && l.Target == Key("budget") && l.Verification == "verified");

        // Unchanged files are not read again; their links and content revision carry over.
        google.ContentReads.Clear();
        var again = await drive.List(Pass(listing.Records), default);
        Assert.Empty(google.ContentReads);
        Assert.Contains(again.Records.Single(r => r.Key.ExternalId == "plan").References, x => RelationshipDiscovery.DriveFileId(x.Url) == "budget");

        // A folder that can't be listed makes the listing incomplete, so nothing leaves the map because of it.
        google.FailingFolders.Add("recipes");
        Assert.False((await drive.List(Pass(listing.Records), default)).Complete);
        // Over the document budget: newest first, and incomplete.
        google.FailingFolders.Clear();
        Assert.False((await drive.List(Pass(documents: 2), default)).Complete);
    }

    // SB-P2-05: a document's text for semantic_search passages, read again on request (only when adapters.embeddings.sources
    // lists drive), with the revision the map gives the file; only a body that reads as words, as for its vector.
    [Fact] public async Task A_document_gives_its_text_for_passages_only_when_it_reads_as_words()
    {
        var drive = Drive();
        google.Files["essay"] = new("Essay.md", "text/markdown", ["root-folder"],
            Encoding.UTF8.GetBytes("DEMO: a fictional essay about the allotment shed, the seed order and the watering rota."));
        var records = (await drive.List(Pass(), default)).Records.ToDictionary(r => r.Key.ExternalId);
        var essay = await drive.SearchText(records["essay"], default);
        Assert.NotNull(essay);
        Assert.Equal((records["essay"].Revision, true), (essay.Revision, essay.Text.Contains("watering rota")));
        // A file whose version moved past its star's is not downloaded: its new revision comes back, with no text.
        google.ContentReads.Clear();
        var behind = await drive.SearchText(records["essay"] with { Revision = "an-older-version" }, default);
        Assert.Equal((records["essay"].Revision, ""), (behind!.Revision, behind.Text));
        Assert.Empty(google.ContentReads);
        // A folder, a body that does not read as words (the Doc: a link and a few words), a Sheet Drive will not export.
        Assert.Null(await drive.SearchText(records["root-folder"], default));
        Assert.Null(await drive.SearchText(records["plan"], default));
        Assert.Null(await drive.SearchText(records["budget"], default));
        // In the trash, or gone: no text either.
        google.Files["essay"] = google.Files["essay"] with { Trashed = true };
        Assert.Null(await drive.SearchText(records["essay"], default));
        google.Files.Remove("essay");
        Assert.Null(await drive.SearchText(records["essay"], default));
        // Signed out: a failure, which the index job counts and tries again later.
        google.Revoked.Add(FakeGoogle.ReadRefresh);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Drive().SearchText(records["soup"], default));
    }

    // A kept vector is reused only while it is this pass's model's: after a model change an unchanged file is read again
    // for a new vector, and while embeddings are off it keeps the one it has (SB-P2-02).
    [Fact] public async Task An_unchanged_file_is_read_again_when_its_vector_is_another_models()
    {
        // Text that reads like words, so a read of it gives a vector (SimilaritySuggestions.Readable).
        google.Files["soup"] = google.Files["soup"] with
        {
            Body = Encoding.UTF8.GetBytes("# Soup\nServe the leek and potato soup hot after the walk, with bread: https://docs.google.com/document/d/plan/edit"),
        };
        var drive = Drive();
        var first = await drive.List(Pass(), default);
        var kept = first.Records.Select(r => r.Key.ExternalId is "plan" or "soup" ? r with { Embedding = [1f, 0f], EmbeddingModel = "model-a" } : r)
            .ToDictionary(r => r.Key);
        var vectors = 0;
        SourcePass With(string? model, IReadOnlyDictionary<ResourceKey, DiscoveryRecord> previous) =>
            new(previous, (_, _, _, _) => { vectors++; return [0f, 1f]; }, 3000, 1000, model);
        DiscoveryRecord Of(SourceListing listing, string id) => listing.Records.Single(r => r.Key.ExternalId == id);

        foreach (var model in new[] { null, "model-a" })
        {
            google.ContentReads.Clear();
            var same = await drive.List(With(model, kept), default);
            Assert.Empty(google.ContentReads);
            Assert.Equal([1f, 0f], Of(same, "soup").Embedding ?? []);
        }
        Assert.Equal(0, vectors);

        google.ContentReads.Clear();
        var changed = await drive.List(With("model-b", kept), default);
        // Only the files whose kept vector is model-a's; the Sheet kept no vector and is not read.
        Assert.Equal(["plan", "soup"], google.ContentReads.Order(StringComparer.Ordinal));
        Assert.Equal([0f, 1f], Of(changed, "soup").Embedding ?? []);
        Assert.Contains(Of(changed, "soup").References, x => RelationshipDiscovery.DriveFileId(x.Url) == "plan");
        // The plan's text is too short to compare, so its read gives no vector, and with none it is not read again; the
        // soup's new vector, tagged with this pass's model as the host tags it, is reused.
        Assert.Null(Of(changed, "plan").Embedding);
        var tagged = changed.Records.Select(r => r.Embedding is null ? r : r with { EmbeddingModel = "model-b" }).ToDictionary(r => r.Key);
        google.ContentReads.Clear();
        await drive.List(With("model-b", tagged), default);
        Assert.Empty(google.ContentReads);
    }

    [Fact] public async Task An_older_engines_star_account_can_be_kept()
    {
        // An install moved from an older engine keeps the account part of its Drive star ids (docs/dev/migration-compat.md).
        Environment.SetEnvironmentVariable("RHYTHM_DRIVE_STAR_ACCOUNT", "legacy-account");
        try
        {
            var listing = await Drive().List(Pass(), default);
            Assert.All(listing.Records, r => Assert.Equal("legacy-account", r.Key.Account));
            Assert.StartsWith("drive:legacy-account:", listing.Records.Single(r => r.Key.ExternalId == "soup").Key.GraphId);
            Assert.All(listing.Records.SelectMany(r => r.References).Where(x => x.Target is not null), x => Assert.Equal("legacy-account", x.Target!.Account));
        }
        finally { Environment.SetEnvironmentVariable("RHYTHM_DRIVE_STAR_ACCOUNT", null); }
        Assert.All((await Drive().List(Pass(), default)).Records, r => Assert.Equal("drive", r.Key.Account));
    }

    [Fact] public async Task No_roots_list_nothing_and_a_lost_sign_in_fails_the_listing()
    {
        var none = await Drive(Settings(true)).List(Pass(), default);
        Assert.True(none.Complete);
        Assert.Empty(none.Records);
        Assert.DoesNotContain(google.Handler.Requests, r => r.Contains("googleapis"));
        google.Revoked.Add(FakeGoogle.ReadRefresh);
        var e = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Drive().List(Pass(), default));
        Assert.Contains("rhythm connect drive again", e.Message);
        secrets.Remove("drive_read_token");
        Assert.Contains("isn't connected", (await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Drive().List(Pass(), default))).Message);
    }

    [Fact] public async Task Contents_read_a_Sheet_as_rows_and_a_Doc_as_Markdown_with_the_revision()
    {
        var drive = Drive();
        DiscoveryRecord Star(string id) => new(Key(id), "shared", "document", id, null, "7", null, DateTimeOffset.UtcNow, []);
        var sheet = (await drive.Contents(Star("budget"), default))!;
        Assert.Equal(("ok", "sheet", "7", "Budget 2026"), ((string?)sheet["status"], (string?)sheet["format"], (string?)sheet["revision"], (string?)sheet["title"]));
        var tab = sheet["sheets"]![0]!;
        Assert.Equal("Budget", (string?)tab["title"]);
        Assert.Equal("[[\"Item\",\"Amount\"],[\"Rent\",1000],[\"Food\",\"=B2/10\"]]", tab["rows"]!.ToJsonString());
        var doc = (await drive.Contents(Star("plan"), default))!;
        Assert.Equal(("markdown", "Plan\nWeek 1\n"), ((string?)doc["format"], ((string)doc["text"]!)[..12]));
        Assert.Equal("unavailable", (string?)(await drive.Contents(Star("missing"), default))!["status"]);
        Assert.Null(await drive.Contents(new(new("household", "notes", "notes", "x"), "shared", "note", "x", null, null, null, DateTimeOffset.UtcNow, []), default));
        // A file whose version moved on is no longer current for retrieval.
        Assert.True(await drive.Current("drive", "budget", "7", default));
        Assert.False(await drive.Current("drive", "budget", "6", default));
        Assert.False(await drive.Current("drive", "missing", "7", default));
        Assert.False(await drive.Current("notes", "budget", "7", default));
    }

    // SB-P4-10 (story 7): a file a memory cites, as Drive describes it now, from its metadata alone. Edited, moved and
    // renamed show in its version, modified time, folders and name; the trash, a file gone, one Drive refuses and a sign-in
    // Google no longer accepts each say so; Drive not answering is unchecked. Nothing is downloaded and nothing is listed,
    // so the map's sync is untouched.
    [Fact] public async Task A_cited_file_is_checked_from_its_metadata_alone()
    {
        var drive = Drive();
        var ok = await drive.Check("drive", "budget", default);
        Assert.Equal((CitedCheck.Ok, "7", "Budget 2026"), (ok.State, ok.Revision, ok.Title));
        Assert.Equal(["root-folder"], ok.Parents!);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture), ok.ModifiedAt);
        google.Files["budget"] = google.Files["budget"] with { Name = "Budget 2027", Parents = ["recipes"], Version = 9, Modified = "2026-11-02T10:00:00.000Z" };
        var changed = await drive.Check("drive", "budget", default);
        Assert.Equal((CitedCheck.Ok, "9", "Budget 2027"), (changed.State, changed.Revision, changed.Title));
        Assert.Equal(["recipes"], changed.Parents!);
        Assert.Equal(DateTimeOffset.Parse("2026-11-02T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture), changed.ModifiedAt);
        google.Files["budget"] = google.Files["budget"] with { Trashed = true };
        Assert.Equal((CitedCheck.Trashed, (string?)null), ((await drive.Check("drive", "budget", default)).State, (await drive.Check("drive", "budget", default)).Revision));
        var gone = await drive.Check("drive", "missing", default);
        Assert.Equal(CitedCheck.Gone, gone.State);
        Assert.Contains("no longer shared", gone.Reason);
        google.Forbidden.Add("plan");
        Assert.Equal(CitedCheck.Unreadable, (await drive.Check("drive", "plan", default)).State);
        // A 403 for one of Drive's usage limits says nothing about the file's sharing: unchecked, not unreadable.
        google.RateLimited.Add("soup");
        var limited = await drive.Check("drive", "soup", default);
        Assert.Equal((CitedCheck.Unchecked, (string?)null), (limited.State, limited.Revision));
        Assert.Contains("usage limits", limited.Reason);
        google.RateLimited.Clear();
        Assert.True(DriveAdapter.RateLimited(System.Text.Encoding.UTF8.GetBytes("""{"error":{"code":403,"status":"RESOURCE_EXHAUSTED"}}""")));
        foreach (var refusal in new[] { """{"error":{"code":403,"errors":[{"reason":"insufficientFilePermissions"}]}}""",
            """{"error":{"code":403,"message":"appNotAuthorizedToFile"}}""", "not json", "" })
            Assert.False(DriveAdapter.RateLimited(System.Text.Encoding.UTF8.GetBytes(refusal)));
        google.Down = true;
        var down = await drive.Check("drive", "soup", default);
        Assert.Equal((CitedCheck.Unchecked, (string?)null), (down.State, down.Revision));
        google.Down = false;
        Assert.Equal(CitedCheck.Gone, (await drive.Check("notes", "budget", default)).State);
        Assert.Equal(CitedCheck.Gone, (await drive.Check("drive", "../budget", default)).State);
        // A sign-in Google no longer accepts: unreadable, saying why (a fresh adapter, with no access token kept).
        google.Revoked.Add(FakeGoogle.ReadRefresh);
        var revoked = await Drive().Check("drive", "soup", default);
        Assert.Equal(CitedCheck.Unreadable, revoked.State);
        Assert.Contains("no longer accepts", revoked.Reason);
        // Metadata only: no body read, no folder listed, every call a GET.
        Assert.Empty(google.ContentReads);
        Assert.DoesNotContain(google.Handler.Requests, r => r.EndsWith("/drive/v3/files", StringComparison.Ordinal));
        Assert.All(google.Handler.Requests.Where(r => r.Contains("www.googleapis.com", StringComparison.Ordinal)), r => Assert.StartsWith("GET ", r));
    }

    [Fact] public async Task Sheet_and_Doc_writes_check_the_revision_and_Undo_puts_back_what_they_replaced()
    {
        var drive = Drive();
        Assert.Equal(["sheet", "doc"], drive.WriteKinds);
        DiscoveryRecord Star(string id) => new(Key(id), "shared", "document", id, null, google.Version.ToString(), null, DateTimeOffset.UtcNow, []);
        var cells = new DashboardWrite("cells", "sheet", Star: "drive:drive:budget", Range: "'Budget'!B2:C10", AppendTo: "Budget");
        var week2 = new DashboardWrite("week2", "doc", Star: "drive:drive:plan", Section: "Week 2");
        var notes = new DashboardWrite("notes", "doc", Star: "drive:drive:plan", Append: true);
        Task<WriteOutcome> Write(DashboardWrite declared, string id, object change) => drive.Write(new(declared, Star(id), Change(change)), default);
        Task Undo(DashboardWrite declared, WriteOutcome done) => drive.Undo(new(declared.Kind, done.Target, done.Before, done.After), default);
        async Task<string> Refusal(DashboardWrite declared, string id, object change) =>
            (await Assert.ThrowsAsync<DashboardWriteRefused>(() => Write(declared, id, change))).Message;

        // Cells: only within the declared range, and only while the Sheet is as the dashboard read it.
        Assert.Contains("changed after the dashboard read it", await Refusal(cells, "budget", new { revision = "6", cells = "B2", values = new[] { new object[] { 1 } } }));
        Assert.Contains("outside the cells", await Refusal(cells, "budget", new { revision = "7", cells = "A2", values = new[] { new object[] { 1 } } }));
        Assert.Contains("must be 1 row(s) of 2", await Refusal(cells, "budget", new { revision = "7", cells = "B3:C3", values = new[] { new object[] { 1 } } }));
        Assert.Contains("Only a Google Sheet", await Refusal(cells, "plan", new { revision = "7", cells = "B3", values = new[] { new object[] { 1 } } }));
        var set = await Write(cells, "budget", new { revision = "7", cells = "B3:C3", values = new[] { new object[] { 120, "groceries" } } });
        Assert.Equal("Set B3:C3 in Budget 2026 › Budget", set.Summary);
        Assert.Equal(("budget", "Budget 2026", "'Budget'!B3:C3"), ((string?)set.Target["file"], (string?)set.Target["title"], (string?)set.Target["range"]));
        Assert.Equal("[[\"=B2/10\",\"\"]]", set.Before!.ToJsonString());
        Assert.Equal(("120", "groceries"), (google.Sheet[2][1]!.ToJsonString(), (string?)google.Sheet[2][2]));
        // Undo restores the formula and empties the cell that was empty.
        await Undo(cells, set);
        Assert.Equal(("=B2/10", ""), ((string?)google.Sheet[2][1], (string?)google.Sheet[2][2]));
        // A row added at the end and taken out again; a row changed by hand since is left alone.
        var added = await Write(cells, "budget", new { revision = google.Version.ToString(), row = new object[] { "Fuel", 60 } });
        Assert.Equal(("Added a row to Budget 2026 › Budget", 4), (added.Summary, google.Sheet.Count));
        await Undo(cells, added);
        Assert.Equal(3, google.Sheet.Count);
        var again = await Write(cells, "budget", new { revision = google.Version.ToString(), row = new object[] { "Fuel", 60 } });
        google.Sheet[3][1] = 65;
        Assert.Contains("has changed since", (await Assert.ThrowsAsync<DashboardWriteRefused>(() => Undo(cells, again))).Message);
        Assert.Equal(4, google.Sheet.Count);

        // A Doc section: the text under "Week 2" to the end of the Doc, replaced as body text and put back by Undo.
        Assert.Contains("changed after the dashboard read it", await Refusal(week2, "plan", new { revision = "1", text = "x" }));
        var rewrote = await Write(week2, "plan", new { revision = google.Version.ToString(), text = "Swim.\nRun.\n" });
        Assert.Equal(("Rewrote \"Week 2\" in Training plan", "Long ride.\nBricks."), (rewrote.Summary, (string?)rewrote.Before));
        Assert.Equal("Plan\nWeek 1\nEasy runs.\nWeek 2\nSwim.\nRun.\n", google.DocText());
        Assert.Equal(["NORMAL_TEXT", "NORMAL_TEXT"], google.Doc.Skip(4).Select(p => p.Style));
        Assert.Equal("HEADING_1", google.Doc[3].Style);
        await Undo(week2, rewrote);
        Assert.Equal("Plan\nWeek 1\nEasy runs.\nWeek 2\nLong ride.\nBricks.\n", google.DocText());
        // Added to the end, and taken back off.
        var appended = await Write(notes, "plan", new { revision = google.Version.ToString(), text = "Taper week." });
        Assert.EndsWith("Bricks.\nTaper week.\n", google.DocText());
        await Undo(notes, appended);
        Assert.EndsWith("Bricks.\n", google.DocText());

        // Without the edit grant nothing changes, and the reason says how to set it up.
        secrets.Remove("drive_edit_token");
        Assert.Contains("rhythm connect drive edit", await Refusal(notes, "plan", new { revision = google.Version.ToString(), text = "x" }));
        Assert.Contains("isn't a Google Drive file", (await Assert.ThrowsAsync<DashboardWriteRefused>(() => drive.Write(new(notes,
            new(new("household", "notes", "notes", "x"), "shared", "note", "x", null, null, null, DateTimeOffset.UtcNow, []), Change(new { text = "x" })), default))).Message);
        // With edits off the adapter makes no writes at all.
        Assert.Empty(Drive(Settings(false, "root-folder")).WriteKinds);
    }

    // Stands in for the person at the terminal: reads the consent address the adapter printed, allows access in a
    // (fake) browser and pastes back the address Google would send that browser to.
    sealed class Person(StringWriter output, FakeGoogle google, Func<string, string>? paste = null, Func<string, string>? grant = null) : TextReader
    {
        public int Consents { get; private set; }
        public override string? ReadLine()
        {
            Consents++;
            var consent = output.ToString().Split('\n').Last(l => l.StartsWith("https://accounts.google.com/", StringComparison.Ordinal)).Trim();
            var q = QueryHelpers.ParseQuery(new Uri(consent).Query);
            Assert.Equal(("code", "offline", "S256", DriveAdapter.RedirectUri), (q["response_type"].ToString(), q["access_type"].ToString(),
                q["code_challenge_method"].ToString(), q["redirect_uri"].ToString()));
            google.Challenge = q["code_challenge"];
            google.GrantedScope = grant?.Invoke(q["scope"]!) ?? q["scope"];
            var state = q["state"].ToString();
            return paste?.Invoke(state) ?? $"{DriveAdapter.RedirectUri}?state={Uri.EscapeDataString(state)}&code=fictional-code&scope={Uri.EscapeDataString(q["scope"]!)}";
        }
        public override ValueTask<string?> ReadLineAsync(CancellationToken ct) => ValueTask.FromResult(ReadLine());
    }

    async Task<(int Code, string Output, Person Person)> Connect(bool edits, string[] args, Func<string, string>? paste = null, Func<string, string>? grant = null)
    {
        var output = new StringWriter();
        var person = new Person(output, google, paste, grant);
        var settings = new AdapterSettings("drive", Settings(edits, "root-folder"));
        var code = await new DriveAdapter(google.Handler).Connect(new(settings, TestSettings.Use(), secrets.Directory, person, output, args), default);
        return (code, output.ToString(), person);
    }

    [Fact] public async Task Connect_saves_a_read_grant_from_the_pasted_address_then_an_edit_grant_when_edits_are_on()
    {
        secrets.Remove("drive_read_token"); secrets.Remove("drive_edit_token");
        var (code, output, person) = await Connect(false, []);
        Assert.Equal((0, 1), (code, person.Consents));
        Assert.Equal(FakeGoogle.ReadRefresh, secrets.Read("drive_read_token"));
        Assert.Null(secrets.Read("drive_edit_token"));
        Assert.Contains("scope=" + Uri.EscapeDataString("https://www.googleapis.com/auth/drive.readonly"), output);
        Assert.Contains("Saved secrets/adapters/drive_read_token for alex@example.invalid", output);
        Assert.DoesNotContain(FakeGoogle.ReadRefresh, output);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(secrets.Directory, "drive_read_token")));

        // With edits on, a second consent for Sheets and Docs; `edit` alone renews just that one.
        secrets.Remove("drive_read_token");
        (code, output, person) = await Connect(true, []);
        Assert.Equal((0, 2), (code, person.Consents));
        Assert.Equal((FakeGoogle.ReadRefresh, FakeGoogle.EditRefresh), (secrets.Read("drive_read_token"), secrets.Read("drive_edit_token")));
        Assert.Contains(Uri.EscapeDataString("https://www.googleapis.com/auth/spreadsheets"), output);
        Assert.Equal(2, (await Connect(false, ["edit"])).Code);
        Assert.Equal((0, 1), ((await Connect(true, ["edit"])).Code, (await Connect(true, ["edit"])).Person.Consents));
    }

    [Fact] public async Task Connect_saves_nothing_for_a_wrong_address_a_partial_grant_or_another_account()
    {
        secrets.Remove("drive_read_token"); secrets.Remove("drive_edit_token");
        // Another attempt's state, a refusal on Google's page, a pasted address that isn't Google's redirect, no code.
        foreach (var (paste, expected) in new (Func<string, string>, string)[] {
            (_ => $"{DriveAdapter.RedirectUri}?state=another&code=fictional-code", "another sign-in attempt"),
            (s => $"{DriveAdapter.RedirectUri}?state={s}&error=access_denied", "wasn't allowed"),
            (_ => "https://example.invalid/?code=fictional-code", "isn't the address"),
            (s => $"{DriveAdapter.RedirectUri}?state={s}", "no sign-in code"),
            (s => $"{DriveAdapter.RedirectUri}?state={s}&code=wrong-code", "didn't accept the code") })
        {
            var (code, output, _) = await Connect(false, [], paste);
            Assert.Equal(1, code);
            Assert.Contains(expected, output);
            Assert.Null(secrets.Read("drive_read_token"));
        }
        // Google's granular consent can leave a box unticked.
        var partial = await Connect(true, [], grant: s => s.Split(' ')[0]);
        Assert.Contains("granted less", partial.Output);
        // The edit grant must come from the account that allowed reading.
        var consents = 0;
        var other = await Connect(true, [], grant: s => { if (++consents == 2) google.Account = "permission-2"; return s; });
        Assert.Equal(1, other.Code);
        Assert.Contains("different Google account", other.Output);
        Assert.Null(secrets.Read("drive_edit_token"));
        // Without the OAuth client there is nothing to sign in with.
        secrets.Remove("drive_oauth_client");
        Assert.Contains("OAuth client is missing", (await Connect(false, [])).Output);
        Assert.Equal(2, (await Connect(false, ["write"])).Code);
    }
}
