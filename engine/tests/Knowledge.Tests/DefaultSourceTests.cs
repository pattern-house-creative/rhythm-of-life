using System.Text;
using System.Text.Json;
using Knowledge.Adapters;
using Knowledge.Adapters.Csv;
using Knowledge.Adapters.Documents;
using Knowledge.Adapters.Notes;
using Knowledge.Mcp;
using Knowledge.Relationships;

namespace Knowledge.Tests;

// The default sources a newcomer has with no vendor account: a notes folder, CSV files and documents. Fictional
// fixtures only, written to a temporary folder by each test (or the samples in demo/).
public sealed class NotesAdapterTests
{
    static SourcePass Pass(IReadOnlyDictionary<ResourceKey, DiscoveryRecord>? previous = null) =>
        new(previous ?? new Dictionary<ResourceKey, DiscoveryRecord>(), (_, _, _, _) => null, 3000, 1000);

    internal static NotesAdapter Notes(string root, string owner = "household")
    {
        var config = TestSettings.Use();
        var adapter = new NotesAdapter();
        var settings = new AdapterSettings("notes", new Dictionary<string, object?> { ["enabled"] = "true", ["path"] = root, ["owner"] = owner });
        adapter.Configure(new(settings, config, null!, null!));
        return adapter;
    }

    internal static string Folder(params (string Path, string Text)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "rhythm-notes-" + Guid.NewGuid().ToString("N")[..10]);
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact] public async Task Wikilinks_markdown_links_folders_and_tags_become_links_and_projects()
    {
        var root = Folder(
            ("Morning walks.md", "---\ntitle: Morning walks by the canal\ntags: [health]\n---\nWith [[Garden plan|the garden]] and [[Soup#Ingredients]].\n"
                + "Also [the soup](Recipes/Soup.md \"recipe\") and [[allotment]]. Not `[[Inline]]` and not https://example.invalid/#fragment. #routine #2026\n"
                + "```\n[[Hidden]] #notatag\n```\n"),
            ("Garden plan.md", "---\naliases: [allotment]\n---\n# Raised beds\nBack to [[Morning walks]]; [[Missing note]] does not exist.\n"),
            ("Recipes/Soup.md", "Leek soup. [Back](../Morning%20walks.md) and [out](../../outside.md). #cooking #Health\n"),
            ("Recipes/.hidden.md", "[[Morning walks]]"),
            (".obsidian/workspace.md", "[[Morning walks]]"),
            ("Recipes/list.txt", "not a note"));
        try
        {
            var listing = await Notes(root).List(Pass(), default);
            Assert.True(listing.Complete);
            var notes = listing.Records.Where(r => r.Kind == "note").ToDictionary(r => r.Key.ExternalId);
            Assert.Equal(["Garden plan.md", "Morning walks.md", "Recipes/Soup.md"], notes.Keys.Order(StringComparer.Ordinal));
            Assert.All(notes.Values, n => Assert.Equal("household", n.Key.Subject));
            Assert.Equal("Morning walks by the canal", notes["Morning walks.md"].Title);
            Assert.Equal("Garden plan", notes["Garden plan.md"].Title);
            Assert.StartsWith("With the garden and Soup. Also the soup and allotment.", notes["Morning walks.md"].Overview);
            var walks = notes["Morning walks.md"].References.Where(r => r.NativeType == "references").Select(r => r.Target!.ExternalId).ToArray();
            Assert.Equal(["Garden plan.md", "Recipes/Soup.md"], walks.Order(StringComparer.Ordinal));
            Assert.Equal(["Morning walks.md"], notes["Recipes/Soup.md"].References.Where(r => r.NativeType == "references").Select(r => r.Target!.ExternalId));
            Assert.Equal(["Morning walks.md"], notes["Garden plan.md"].References.Where(r => r.NativeType == "references").Select(r => r.Target!.ExternalId));
            // Folders and tags (case folded, numbers are not tags, code is not read) are projects.
            var projects = listing.Records.Where(r => r.Kind == "project").ToDictionary(r => r.Key.ExternalId, r => r.Title);
            Assert.Equal(new Dictionary<string, string> { ["folder:Recipes"] = "Recipes", ["tag:cooking"] = "#cooking", ["tag:health"] = "#health", ["tag:routine"] = "#routine" },
                projects);
            Assert.Contains(notes["Recipes/Soup.md"].References, r => r.NativeType == "in_project" && r.Target!.ExternalId == "tag:health");
            Assert.Equal("notes:folder:Recipes", notes["Recipes/Soup.md"].Groups![0].Id);
            // The core turns references into verified, directed links between the notes.
            var links = RelationshipDiscovery.Discover(listing.Records);
            Assert.Contains(links, l => l.Source.ExternalId == "Morning walks.md" && l.Target.ExternalId == "Garden plan.md" && l.Verification == "verified" && l.Type == "references");
            Assert.Contains(links, l => l.Source.ExternalId == "Recipes/Soup.md" && l.Target.ExternalId == "folder:Recipes" && l.Type == "in_project");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact] public async Task Opening_a_note_reads_the_file_again_and_a_removed_note_leaves()
    {
        var root = Folder(("a.md", "---\ntitle: First\n---\nFictional text about [[b]]."), ("b.md", "Second."));
        try
        {
            var notes = Notes(root, "alex");
            var listing = await notes.List(Pass(), default);
            var first = listing.Records.Single(r => r.Key.ExternalId == "a.md");
            Assert.Equal("private", first.Scope);
            Assert.Equal("Fictional text about [[b]].", await notes.ReadText(first, default));
            File.WriteAllText(Path.Combine(root, "a.md"), "Changed.");
            Assert.Equal("Changed.", await notes.ReadText(first, default));
            Assert.NotEqual(first.Revision, (await notes.List(Pass(), default)).Records.Single(r => r.Key.ExternalId == "a.md").Revision);
            // Only a plain note inside the folder is ever read.
            Assert.Null(await notes.ReadText(first with { Key = first.Key with { ExternalId = "../a.md" } }, default));
            Assert.Null(await notes.ReadText(first with { Key = first.Key with { ExternalId = ".obsidian/a.md" } }, default));
            File.Delete(Path.Combine(root, "b.md"));
            Assert.DoesNotContain((await notes.List(Pass(), default)).Records, r => r.Key.ExternalId == "b.md");
            // A folder that is not there fails the listing, so the map keeps what it had.
            Directory.Delete(root, true);
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() => notes.List(Pass(), default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact] public async Task The_sample_notes_folder_is_a_linked_sky_labelled_demo()
    {
        var listing = await Notes(Path.Combine(AppContext.BaseDirectory, "samples", "notes")).List(Pass(), default);
        var notes = listing.Records.Where(r => r.Kind == "note").ToArray();
        Assert.True(notes.Length >= 8);
        Assert.All(notes, n => Assert.StartsWith("DEMO · ", n.Title));
        var links = RelationshipDiscovery.Discover(listing.Records);
        // Every sample link resolves: by name, by alias, by path, with headings and display text.
        Assert.Contains(links, l => l.Source.ExternalId == "Weekly review.md" && l.Target.ExternalId == "Water intake.md");
        Assert.Contains(links, l => l.Source.ExternalId == "Weekly review.md" && l.Target.ExternalId == "Home/Boiler service.md");
        Assert.Contains(links, l => l.Source.ExternalId == "Welcome.md" && l.Target.ExternalId == "Reading/Next book.md");
        Assert.Contains(links, l => l.Source.ExternalId == "Home/Paint colours.md" && l.Target.ExternalId == "Home/Boiler service.md");
        Assert.Contains(listing.Records, r => r.Kind == "project" && r.Title == "Home");
        Assert.Contains(listing.Records, r => r.Kind == "project" && r.Title == "#reading");
    }
}

public sealed class CsvAdapterTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact] public void Times_are_instants_bare_local_times_or_dates_and_say_which()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var instant = CsvAdapter.Time("2026-10-20T07:30:00Z", zone, Now)!.Value;
        Assert.Equal(("2026-10-20T07:30:00Z", "explicit-offset"), (instant.Key, instant.Basis));
        Assert.Equal("2026-10-20T06:30:00Z", CsvAdapter.Time("2026-10-20T07:30+01:00", zone, Now)!.Value.Key);
        // A bare local time is read in the install's time zone: summer time before 25 October, winter time after.
        var summer = CsvAdapter.Time("2026-10-20 07:30", zone, Now)!.Value;
        Assert.Equal(("2026-10-20T06:30:00Z", "install-local"), (summer.Key, summer.Basis));
        Assert.Equal("2026-10-27T07:30:00Z", CsvAdapter.Time("2026-10-27T07:30:00", zone, Now)!.Value.Key);
        var day = CsvAdapter.Time("2026-10-20", zone, Now)!.Value;
        Assert.Equal(("2026-10-20", "local-date", new DateOnly(2026, 10, 20)), (day.Key, day.Basis, day.Date!.Value));
        // Never-happened local times, the future, nonsense and dates with an offset are refused.
        foreach (var bad in new[] { "2026-03-29T01:30", "2026-11-02T08:00Z", "2026-10-32", "yesterday", "2026-10-20Z", "20/10/2026" })
            Assert.Null(CsvAdapter.Time(bad, zone, Now));
    }

    [Fact] public void Bad_rows_are_reported_by_line_and_good_rows_kept()
    {
        var config = TestSettings.Use();
        var text = "﻿Time,Metric,Value,Unit,Subject,Source,Note\n"
            + "2026-10-20T07:30:00Z,weight,71.5,kg,alex,\"Scale, bathroom\",fictional\n"
            + "2026-10-20,steps,8200,count,,,\n"
            + "\n"
            + "not a time,steps,1,count,,,\n"
            + "2026-10-21,Bad Metric!,1,count,,,\n"
            + "2026-10-21,steps,many,count,,,\n"
            + "2026-10-21,steps,10,,,,\n"
            + "2026-10-21,steps,10,count,nobody,,\n"
            + "2026-10-21,steps,10,count,,,,extra\n"
            + "2026-10-20,steps,8300,count,,,\n";
        var parsed = CsvAdapter.Parse(text, config, "household", Now);
        Assert.Null(parsed.Problem);
        Assert.Equal([2, 11], parsed.Rows.Select(r => r.Line));
        Assert.Equal("Scale, bathroom", parsed.Rows[0].Source);
        Assert.Equal(("household", 8300.0), (parsed.Rows[1].Subject, parsed.Rows[1].Value));
        Assert.Equal([(3, "duplicate_row"), (5, "time_invalid"), (6, "metric_invalid"), (7, "value_invalid"), (8, "unit_invalid"), (9, "subject_not_allowed"), (10, "columns")],
            parsed.Issues.Select(i => (i.Line, i.Code)));
        Assert.Contains("unit", CsvAdapter.Parse("time,metric,value\n2026-10-20,steps,1\n", config, "household", Now).Problem);
        Assert.NotNull(CsvAdapter.Parse("", config, "household", Now).Problem);
    }

    // On 25 October 2026 London's clocks went back at 02:00, so 01:00-01:59 happened twice. A bare local time in that
    // hour could be either instant: it is refused by name, never folded onto one pass (which merged the two hours into
    // "duplicate rows" or put a reading an hour late). With its offset each pass reads; the hours around it read as before.
    [Fact] public void A_bare_local_time_in_the_repeated_hour_is_refused_as_ambiguous()
    {
        var config = TestSettings.Use();
        var text = "time,metric,value,unit\n"
            + "2026-10-25T00:55,heart_rate,60,bpm\n"
            + "2026-10-25T01:00,heart_rate,61,bpm\n"
            + "2026-10-25T01:55,heart_rate,62,bpm\n"
            + "2026-10-25T01:00,heart_rate,63,bpm\n"
            + "2026-10-25T01:00+01:00,heart_rate,64,bpm\n"
            + "2026-10-25T01:00+00:00,heart_rate,65,bpm\n"
            + "2026-10-25T02:00,heart_rate,66,bpm\n";
        var parsed = CsvAdapter.Parse(text, config, "household", Now);
        Assert.Equal([(3, "time_ambiguous"), (4, "time_ambiguous"), (5, "time_ambiguous")], parsed.Issues.Select(i => (i.Line, i.Code)));
        Assert.Equal([("2026-10-24T23:55:00Z", 60.0), ("2026-10-25T00:00:00Z", 64.0), ("2026-10-25T01:00:00Z", 65.0), ("2026-10-25T02:00:00Z", 66.0)],
            parsed.Rows.Select(r => (r.Key, r.Value)));
        Assert.Null(CsvAdapter.Time("2026-10-25 01:30", TimeZoneInfo.FindSystemTimeZoneById("Europe/London"), Now));
    }

    [Fact] public void The_sample_csv_reads_without_a_bad_row()
    {
        var parsed = CsvAdapter.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "import", "sample-readings.csv")),
            TestSettings.Use(), "household", Now);
        Assert.Null(parsed.Problem);
        Assert.Empty(parsed.Issues);
        Assert.Contains(parsed.Rows, r => r.Basis == "install-local");
        Assert.Equal(["alex", "sam"], parsed.Rows.Select(r => r.Subject).Distinct().Order());
    }

    // The import folder is shared: documents/ and garmin/ belong to their adapters, not to the CSV import.
    [Fact] public void The_import_folder_skips_the_folders_other_adapters_own()
    {
        var root = Path.Combine(Path.GetTempPath(), "rhythm-import-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            foreach (var dir in new[] { "documents", "garmin", "garden" }) Directory.CreateDirectory(Path.Combine(root, dir));
            foreach (var file in new[] { "a.csv", "documents/b.csv", "garmin/Activities.csv", "garden/c.csv" })
                File.WriteAllText(Path.Combine(root, file), "time,metric,value,unit\n");
            var files = new SourceFolder(root, "adapters.csv.path").Files(p => p.EndsWith(".csv"), skip: ["documents", "garmin"]);
            Assert.Equal(["a.csv", "garden/c.csv"], files.Select(f => f.Path));
        }
        finally { Directory.Delete(root, true); }
    }
}

public sealed class DocumentTextTests
{
    [Fact] public void Pdf_text_is_read_page_by_page()
    {
        var read = DocumentText.Extract("Fictional manual.pdf", FictionalPdf.Build("Fictional page one", "Second page (fictional)"));
        Assert.Equal("pdf", read.Format);
        Assert.Equal(2, read.Pages.Count);
        Assert.Contains("Fictional page one", read.Pages[0]);
        Assert.Contains("Second page (fictional)", read.Pages[1]);
    }

    [Fact] public void Only_readable_pdfs_and_utf8_text_are_accepted()
    {
        Assert.Equal("not_pdf", Assert.Throws<DocumentRefused>(() => DocumentText.Extract("a.pdf", Encoding.ASCII.GetBytes("hello there"))).Code);
        Assert.Equal("pdf_unreadable", Assert.Throws<DocumentRefused>(() => DocumentText.Extract("a.pdf", Encoding.ASCII.GetBytes("%PDF-1.4 broken"))).Code);
        Assert.Equal("not_text", Assert.Throws<DocumentRefused>(() => DocumentText.Extract("a.txt", [0xff, 0xfe, 0x00, 0x41])).Code);
        Assert.Equal("unsupported_type", Assert.Throws<DocumentRefused>(() => DocumentText.Extract("a.docx", [1, 2, 3])).Code);
        var md = DocumentText.Extract("rules.md", Encoding.UTF8.GetBytes("---\ntitle: DEMO · Rules\n---\n# Heading\nText"));
        Assert.Equal(("markdown", "DEMO · Rules"), (md.Format, md.Title));
        Assert.Equal("Heading", DocumentText.Extract("rules.md", Encoding.UTF8.GetBytes("Intro\n# Heading\nText")).Title);
        Assert.Null(DocumentText.Extract("rota.txt", Encoding.UTF8.GetBytes("# not a title in text")).Title);
    }

    [Fact] public void The_sample_documents_read_as_text()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "samples", "import", "documents");
        var pdf = DocumentText.Extract("DEMO Boiler manual.pdf", File.ReadAllBytes(Path.Combine(folder, "DEMO Boiler manual.pdf")));
        Assert.Equal(3, pdf.Pages.Count);
        Assert.Contains("between 1 and 2 bar", pdf.Pages[1]);
        Assert.Equal("DEMO · House rules for guests", DocumentText.Extract("House rules.md", File.ReadAllBytes(Path.Combine(folder, "House rules.md"))).Title);
        Assert.StartsWith("DEMO", DocumentText.Extract("DEMO Allotment rota.txt", File.ReadAllBytes(Path.Combine(folder, "DEMO Allotment rota.txt"))).Pages[0]);
    }

    [Fact] public void The_upload_tool_takes_exactly_a_name_and_base64_content()
    {
        TestSettings.Use();
        JsonElement Call(string args) => JsonDocument.Parse("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":\"tools/call\",\"params\":{\"name\":\"app_upload_document\",\"arguments\":" + args + "}}").RootElement;
        Assert.True(AppEndpoint.Allowed(Call("""{"name":"rota.txt","content":"aGVsbG8="}""")));
        Assert.False(AppEndpoint.Allowed(Call("""{"name":"rota.txt"}""")));
        Assert.False(AppEndpoint.Allowed(Call("""{"name":"","content":"aGVsbG8="}""")));
        Assert.False(AppEndpoint.Allowed(Call("""{"name":"rota.txt","content":"aGVsbG8=","subject":"alex"}""")));
    }
}

// The original file behind a document star: only a resource id reaches the route, only the catalog's path and checksum
// reach the archive, and the name the app saves it under is safe.
public sealed class DocumentOriginalTests
{
    [Fact] public void The_route_takes_one_resource_id_and_nothing_else()
    {
        var id = Guid.NewGuid();
        Assert.True(AppFiles.Matches(new($"/app/documents/{id}"), out var found));
        Assert.Equal(id, found);
        foreach (var path in new[] { "/app/documents/", $"/app/documents/{id}/x", $"/app/documents/{id:N}", "/app/documents/../archive",
            $"/app/documents/{id}.pdf", $"/app/document/{id}", $"/mcp/app/documents/{id}" })
            Assert.False(AppFiles.Matches(new(path), out _), path);
    }

    [Fact] public void A_saved_name_keeps_to_one_safe_file_name_of_its_format()
    {
        Assert.Equal("Fictional manual (copy).pdf", DocumentOriginals.FileName("Fictional manual (copy).pdf", "pdf"));
        Assert.Equal("Rota _final_.txt", DocumentOriginals.FileName("Rota \"final\".txt", "text"));
        Assert.Equal("passwd.txt", DocumentOriginals.FileName("../../etc/passwd", "text"));
        Assert.Equal("b.pdf", DocumentOriginals.FileName("a\\b.pdf", "pdf"));
        Assert.Equal("document.pdf", DocumentOriginals.FileName(null, "pdf"));
        Assert.Equal("document.md", DocumentOriginals.FileName("...", "markdown"));
        Assert.Equal("Rules.markdown", DocumentOriginals.FileName("Rules.markdown", "markdown"));
        Assert.Equal("Line_break.MD", DocumentOriginals.FileName("Line\nbreak.MD", "markdown"));
        Assert.Equal("scan.png.pdf", DocumentOriginals.FileName("scan.png", "pdf"));
        var longName = DocumentOriginals.FileName(new string('a', 300) + ".pdf", "pdf");
        Assert.True(longName.Length <= 200);
        Assert.EndsWith(".pdf", longName);
        Assert.Equal(("application/pdf", "text/markdown; charset=utf-8", "text/plain; charset=utf-8"),
            (DocumentOriginals.ContentType("pdf"), DocumentOriginals.ContentType("markdown"), DocumentOriginals.ContentType("text")));
    }

    [Fact] public void Only_an_archived_document_of_the_documents_provider_has_an_original()
    {
        var id = Guid.NewGuid();
        Assert.Equal(new GraphOriginal(id.ToString(), "pdf", 1541, "Manual.pdf"), DocumentOriginals.Describe(id, "documents", true, "pdf", "1541", "Manual.pdf"));
        Assert.Null(DocumentOriginals.Describe(id, "documents", false, "pdf", "1541", "Manual.pdf"));
        Assert.Null(DocumentOriginals.Describe(id, "drive", true, "pdf", "1541", "Manual.pdf"));
        Assert.Null(DocumentOriginals.Describe(id, "documents", true, "docx", "1541", "Manual.docx"));
        Assert.Null(DocumentOriginals.Describe(id, "documents", true, "pdf", null, "Manual.pdf"));
        Assert.Null(DocumentOriginals.Describe(id, "documents", true, "pdf", "0", "Manual.pdf"));
    }

    [Fact] public async Task The_archive_opens_only_what_it_saved_and_only_while_it_is_unchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "rhythm-archive-" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            var archive = new Archive(root);
            var bytes = FictionalPdf.Build("Fictional page one", "Fictional page two");
            var saved = await archive.Save(bytes);
            await using (var file = await archive.Open(saved.Path, saved.Checksum))
            {
                Assert.NotNull(file);
                using var copy = new MemoryStream();
                await file.CopyToAsync(copy);
                Assert.Equal(bytes, copy.ToArray());
            }
            // Anything but the recorded path and its own checksum names nothing.
            var other = Hashing.Hash([1, 2, 3]);
            foreach (var (path, sum) in new[] { ("../" + saved.Path, saved.Checksum), (saved.Path, other), ($"{other[..2]}/{other}", other),
                (saved.Path.Replace('/', '\\'), saved.Checksum), (Path.Combine(root, saved.Path), saved.Checksum), (saved.Path.ToUpperInvariant(), saved.Checksum.ToUpperInvariant()) })
                Assert.Null(await archive.Open(path, sum));
            // A file changed in place is never served.
            await File.WriteAllBytesAsync(Path.Combine(root, saved.Path), [.. bytes, 0x20]);
            await Assert.ThrowsAsync<IOException>(() => archive.Open(saved.Path, saved.Checksum));
        }
        finally { Directory.Delete(root, true); }
    }
}

// A small, valid PDF with lines of text on each page, built at runtime so no binary fixture is kept.
public static class FictionalPdf
{
    public static byte[] Build(params string[] pages)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>" };
        var kids = new List<string>();
        foreach (var text in pages)
        {
            var lines = text.Split('\n').Select(l => "(" + l.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)") + ") Tj T*");
            var stream = "BT /F1 12 Tf 14 TL 72 720 Td " + string.Join(" ", lines) + " ET";
            objects.Add($"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream");
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {objects.Count} 0 R >>");
            kids.Add($"{objects.Count} 0 R");
        }
        objects[1] = $"<< /Type /Pages /Kids [{string.Join(" ", kids)}] /Count {pages.Length} >>";
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++) { offsets.Add(pdf.Length); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
