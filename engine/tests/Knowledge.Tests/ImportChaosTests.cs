using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Cli;
using Knowledge.Tests.Parity;

namespace Knowledge.Tests;

// SB-P7-10 (stories 15 and 16; docs/dev/memory.md section 9; Cli/MemoryArchive.cs, Cli/SecondBrainImport.cs): imports that
// go wrong. Bad records among good ones are refused one by one, each said in the report, and the rest come in as a file
// without them would bring them; text no database text can hold (a NUL, half a surrogate pair) and a number past what the
// database's numeric holds are each such a record, never an error that stops the import, and half a pair in the file's own
// fields is a file not to import; an import stopped part way leaves nothing, and run again it gives what one whole run
// gives, a third run adding nothing; and an export from before a capture connection was disconnected brings none of its
// mirrors out of the trash. Each test makes its own throwaway installs on the test server. Fictional people only; every
// text is DEMO.
public sealed class ImportChaosTests
{
    // The file with the memories `change` picks rewritten by it, its counts kept true: a hand-edited file that still reads
    // as whole, so each record is judged on its own.
    static string Changed(string file, Func<JsonObject, int, bool> change)
    {
        var root = JsonNode.Parse(file)!.AsObject();
        var memories = root["memories"]!.AsArray();
        for (var i = 0; i < memories.Count; i++) change(memories[i]!.AsObject(), i);
        return root.ToJsonString();
    }

    static string Without(string file, IReadOnlySet<string> ids)
    {
        var root = JsonNode.Parse(file)!.AsObject();
        var memories = root["memories"]!.AsArray();
        foreach (var gone in memories.Where(m => ids.Contains((string)m!["id"]!)).ToList()) memories.Remove(gone);
        root["counts"]!["memories"] = memories.Count;
        return root.ToJsonString();
    }

    // JSON text with escapes no writer of ours makes: a NUL for each Nul marker, half a surrogate pair for each Half one.
    const string Nul = "@NUL@", Half = "@HALF@";
    static string Unholdable(string json) => json.Replace(Nul, "\\u0000").Replace(Half, "\\ud800");

    static async Task<string[]> Ids(MemoryArchiveTests.Install i) =>
        ((string?)await i.Scalar("SELECT string_agg(id::text,',' ORDER BY id) FROM memories") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);

    static async Task<MemoryArchiveTests.Install> Importer()
    {
        var b = await MemoryArchiveTests.Install.Make();
        await b.Run("UPDATE memberships SET role='admin' WHERE person_id='alex'");
        return b;
    }

    [PostgresFact] public async Task Bad_records_are_refused_one_by_one_and_the_rest_come_in_as_a_file_without_them_brings_them()
    {
        await using var a = await MemoryArchiveTests.Install.Make();
        await MemoryArchiveTests.Seed(a);
        var file = await a.Export("alex");
        var bad = new HashSet<string>(StringComparer.Ordinal);
        // A date that is no date, a revision that is no number, a scope its subject does not have, and a project that is
        // not a project's id: each a record a person editing the file by hand could leave.
        var broken = Changed(file, (m, i) =>
        {
            Action<JsonObject>? breaks = i switch
            {
                0 => x => x["createdAt"] = "DEMO: not a date",
                1 => x => x["revision"] = "DEMO: not a number",
                2 => x => x["scope"] = x["scope"]!.GetValue<string>() == "shared" ? "private" : "shared",
                3 => x => x["validUntil"] = "2030-13-45T99:00:00Z",
                _ => null,
            };
            if (breaks is null) return false;
            bad.Add((string)m["id"]!);
            breaks(m);
            return true;
        });
        Assert.Equal(4, bad.Count);

        await using var b = await Importer();
        var r = await b.Import(broken, "alex", family: true);
        await using var c = await Importer();
        var clean = await c.Import(Without(file, bad), "alex", family: true);
        Assert.Equal(4, r.Count("memory", "refused:invalid") - clean.Count("memory", "refused:invalid"));
        Assert.Equal(clean.Count("memory", "added"), r.Count("memory", "added"));
        Assert.Equal(clean.Outcomes["link"], r.Outcomes["link"]);
        Assert.Equal(clean.Outcomes["project"], r.Outcomes["project"]);
        Assert.Equal(await Ids(c), await Ids(b));
        Assert.DoesNotContain(await Ids(b), id => bad.Contains(id));
    }

    // A NUL or half a surrogate pair reads as JSON and as .NET text, but no database text holds it: such a record is refused
    // as invalid like any other bad record, in each list, and never fails the import; the file's other records come in.
    [PostgresFact] public async Task Text_no_database_can_hold_refuses_its_record_and_never_the_import()
    {
        await using var a = await MemoryArchiveTests.Install.Make();
        await MemoryArchiveTests.Seed(a);
        var file = await a.Export("alex");
        string? nul = null, half = null;
        var broken = Changed(file, (m, i) =>
        {
            if (i == 0) { nul = (string)m["id"]!; m["content"] = $"DEMO: a NUL {Nul} inside."; }
            if (i == 1) { half = (string)m["id"]!; m["tags"] = new JsonArray("demo", $"half {Half} pair"); }
            return i < 2;
        });
        await using var b = await Importer();
        var r = await b.Import(Unholdable(broken), "alex", family: true);
        await using var c = await Importer();
        var clean = await c.Import(Without(file, new HashSet<string> { nul!, half! }), "alex", family: true);
        Assert.Equal(2, r.Count("memory", "refused:invalid") - clean.Count("memory", "refused:invalid"));
        Assert.Equal(clean.Count("memory", "added"), r.Count("memory", "added"));
        Assert.Equal(await Ids(c), await Ids(b));

        // A project's and a link's line the same way.
        var root = JsonNode.Parse(file)!.AsObject();
        root["projects"]![0]!["description"] = $"DEMO: a NUL {Nul} here too.";
        root["links"]![0]!["evidence"] = $"DEMO: half {Half} a pair.";
        await using var d = await Importer();
        var p = await d.Import(Unholdable(root.ToJsonString()), "alex", family: true);
        Assert.Equal(1, p.Count("project", "refused:invalid"));
        Assert.Equal(1, p.Count("link", "refused:invalid"));
        Assert.True(p.Count("memory", "added") > 0);
    }

    // A number past what the database's numeric holds (jsonb keeps every number as one) reads as JSON and as .NET's, but
    // the server refuses it as the record arrives (22003): such a record is refused as invalid like any other, in each
    // list, and never fails the import.
    const string Big = "@BIG@", Tiny = "@TINY@";
    static string Unnumbered(string json) => json.Replace($"\"{Big}\"", "1e200000").Replace($"\"{Tiny}\"", "1e-20000");

    [PostgresFact] public async Task A_number_no_database_can_hold_refuses_its_record_and_never_the_import()
    {
        await using var a = await MemoryArchiveTests.Install.Make();
        await MemoryArchiveTests.Seed(a);
        var file = await a.Export("alex");
        string? big = null, tiny = null;
        var broken = Changed(file, (m, i) =>
        {
            if (i == 0) { big = (string)m["id"]!; m["importance"] = Big; }
            if (i == 1) { tiny = (string)m["id"]!; m["holdScore"] = Tiny; }
            return i < 2;
        });
        await using var b = await Importer();
        var r = await b.Import(Unnumbered(broken), "alex", family: true);
        await using var c = await Importer();
        var clean = await c.Import(Without(file, new HashSet<string> { big!, tiny! }), "alex", family: true);
        Assert.Equal(2, r.Count("memory", "refused:invalid") - clean.Count("memory", "refused:invalid"));
        Assert.Equal(clean.Count("memory", "added"), r.Count("memory", "added"));
        Assert.Equal(await Ids(c), await Ids(b));

        var root = JsonNode.Parse(file)!.AsObject();
        root["projects"]![0]!["revision"] = Big;
        root["links"]![0]!["confidence"] = Tiny;
        await using var d = await Importer();
        var p = await d.Import(Unnumbered(root.ToJsonString()), "alex", family: true);
        Assert.Equal(1, p.Count("project", "refused:invalid"));
        Assert.Equal(1, p.Count("link", "refused:invalid"));
        Assert.True(p.Count("memory", "added") > 0);
    }

    // MemoryArchive.Numeric says of each number what the server's jsonb does, at each of numeric's limits and either side.
    [PostgresFact] public async Task Numeric_refuses_exactly_the_numbers_the_database_refuses()
    {
        string[] numbers =
        [
            "0", "-1.5", "3e2", "1E+5", "4", "0.25",
            "1e131071", "1e131072", "-1e131072", "10e131070", "10e131071", "0.00001e131076", "0.00001e131077",
            "1" + new string('0', 131071), "1" + new string('0', 131072),
            "1e-16383", "1e-16384", "1.5e-16382", "1.5e-16383", "0e-16383", "0e-16384", "0e16384",
            "0." + new string('1', 16383), "0." + new string('1', 16384), "1.0e-16382", "1.00e-16382",
            "0e1073741822", "1e1073741824", "0e1073741824", "1e-1073741824", "1e99999999999999999999", "1e200000", "1e-20000",
        ];
        await using var server = Npgsql.NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("RHYTHM_TEST_DB")!);
        foreach (var n in numbers)
        {
            bool takes;
            try { takes = await MemoryRows.Scalar(server, "SELECT (@j::text)::jsonb IS NOT NULL", ("j", "{\"n\":" + n + "}")) is true; }
            catch (Npgsql.PostgresException e) when (e.SqlState.StartsWith("22", StringComparison.Ordinal)) { takes = false; }
            using var json = JsonDocument.Parse("{\"n\":" + n + "}");
            Assert.True(takes == MemoryArchive.Storable(json.RootElement), $"{(n.Length > 40 ? n[..20] + "…(" + n.Length + ")" : n)}: the server {(takes ? "takes" : "refuses")} it");
        }
    }

    // Half a surrogate pair in one of the file's own fields (its format, export or date) is a file that is not one to
    // import, said as such, never an error of the command's own.
    [Fact] public void Half_a_surrogate_pair_in_the_files_own_fields_is_not_a_file_to_import()
    {
        var install = Guid.NewGuid().ToString();
        string Header(string format, string export, string at) =>
            $"{{\"format\":\"{format}\",\"version\":1,\"install\":\"{install}\",\"export\":\"{export}\",\"exportedAt\":\"{at}\","
            + "\"reader\":{\"person\":null},\"scopes\":\"off\",\"projects\":[],\"memories\":[],\"links\":[],"
            + "\"counts\":{\"projects\":0,\"memories\":0,\"links\":0}}";
        var good = (MemoryArchive.Format, Guid.NewGuid().ToString(), "2026-11-08T10:00:00Z");
        using (var whole = JsonDocument.Parse(Header(good.Item1, good.Item2, good.Item3)))
            Assert.Equal("rhythm:" + install, MemoryArchive.Read(whole).Origin);
        foreach (var file in new[] { Header("\\ud800", good.Item2, good.Item3), Header(good.Item1, "\\ud800", good.Item3), Header(good.Item1, good.Item2, "\\udc00x") })
        {
            using var json = JsonDocument.Parse(file);
            var refused = Assert.Throws<ArchiveInvalid>(() => MemoryArchive.Read(json));
            Assert.Equal("it holds text that cannot be read (half a surrogate pair)", refused.Message);
        }
    }

    // The Second Brain importer takes such a row as upstream's importer would take a row it cannot read: refused, counted,
    // and the rest of the file in.
    [PostgresFact] public async Task A_Second_Brain_row_with_text_no_database_can_hold_is_refused_and_the_rest_come_in()
    {
        var bytes = File.ReadAllBytes(Path.Combine(ParityCorpus.Folder, "sb-export-alex.v3.json"));
        var root = JsonNode.Parse(bytes)!.AsObject();
        var entries = root["entries"]!.AsArray();
        entries[0]!["content"] = $"DEMO: a NUL {Nul} inside.";
        entries[1]!["content"] = $"DEMO: half {Half} a pair.";
        entries[2]!["tags"] = new JsonArray("demo", $"nul{Nul}tag");
        var changed = SbFile.Read(Encoding.UTF8.GetBytes(Unholdable(root.ToJsonString())));
        Assert.Equal(["invalid", "invalid", "invalid"], changed.Entries.Take(3).Select(e => e.Problem));
        var whole = SbFile.Read(bytes);
        await using var i = await MemoryArchiveTests.Install.Make("on", "alex", "sam");
        var dry = await new SecondBrainImport(i.Source).Import(whole, "alex", SecondBrainImportTests.FamilyMap(), false, default);
        var report = await new SecondBrainImport(i.Source).Import(changed, "alex", SecondBrainImportTests.FamilyMap(), true, default);
        Assert.Equal(dry.Count("memory", "added") - 3, report.Count("memory", "added"));
        Assert.True(report.Count("memory", "added") > 20);
    }

    // An import stopped part way (the operator's Ctrl+C, a lost connection) is one transaction rolled back: nothing of it is
    // here. Run again it brings what one whole run brings, and a third run adds nothing.
    [PostgresFact] public async Task An_import_stopped_part_way_leaves_nothing_and_run_again_brings_it_whole_once()
    {
        await using var a = await MemoryArchiveTests.Install.Make("off");
        for (var n = 0; n < 300; n++)
            await a.Store.Create(a.Who.House, new($"DEMO: fictional note {n} about the allotment."), MemoryLogins.Mcp, default);
        var file = await a.Export(null);
        await using var b = await MemoryArchiveTests.Install.Make("off");
        var before = await b.Rows();
        using (var json = JsonDocument.Parse(file))
        {
            using var stop = new CancellationTokenSource();
            var import = b.Archive.Import(json, null, false, true, false, stop.Token);
            // Stopped once it has written: its transaction has an id only once it has written a row, so the import is
            // stopped only when another backend of this database holds one, and the rollback below has rows to take back.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while ((long)(await b.Scalar("""
                SELECT count(*) FROM pg_stat_activity
                WHERE datname=current_database() AND backend_xid IS NOT NULL AND pid<>pg_backend_pid()
                """))! == 0)
            {
                Assert.False(import.IsCompleted, "the import ended before it was seen writing");
                Assert.True(DateTime.UtcNow < deadline, "the import was not seen writing within 15 seconds");
                await Task.Delay(5);
            }
            stop.Cancel();
            var stopped = await Record.ExceptionAsync(() => import);
            Assert.True(stopped is OperationCanceledException || stopped is Npgsql.NpgsqlException { InnerException: OperationCanceledException }
                || stopped is Npgsql.PostgresException { SqlState: "57014" },
                $"the import was not stopped part way: {stopped?.GetType().Name ?? "it finished"}");
        }
        Assert.Equal(before, await b.Rows());
        var whole = await b.Import(file, null, family: true);
        Assert.Equal(300, whole.Count("memory", "added"));
        var once = await b.Rows();
        var again = await b.Import(file, null, family: true);
        Assert.Equal(0, again.Count("memory", "added"));
        Assert.Equal(300, again.Count("memory", "present"));
        Assert.Equal(once, await b.Rows());
    }

    // A revoked integration's records: an export taken while a capture connection stood holds its mirror; once the
    // connection is disconnected (the mirror in the trash), importing that export back brings nothing out of the trash.
    [PostgresFact] public async Task An_export_from_before_a_disconnect_brings_no_mirror_back()
    {
        await using var a = await MemoryArchiveTests.Install.Make("off");
        var folder = Directory.CreateTempSubdirectory("sb-chaos-").FullName;
        try
        {
            var feed = await new Principals(a.Source).CreateCapture("chaos-feed", null, Path.Combine(folder, "capture"), default);
            var mirror = (await a.Store.Capture(feed, new("DEMO: a fictional tracker item.", Mirror: "chaos:item/1"), "chaos-feed", null, default)).Id;
            var file = await a.Export(null);
            Assert.Contains(mirror.ToString(), file);
            var said = new StringWriter();
            Assert.Equal(0, await CaptureCommand.Run(new DeployFolder(folder), ["disconnect", "chaos-feed"], said, a.Source,
                MemoryToolSettings.With("tools: on, capture: on")));
            Assert.Contains("1 mirror(s) moved to the trash", said.ToString());
            Assert.Equal("disconnect", await a.Scalar("SELECT reason FROM memory_trash WHERE memory_id=@id", ("id", mirror)));
            var rows = await a.Rows();
            var r = await a.Import(file, null, family: true);
            Assert.Equal(0, r.Count("memory", "added"));
            Assert.Equal(rows, await a.Rows());
            Assert.Equal("disconnect", await a.Scalar("SELECT reason FROM memory_trash WHERE memory_id=@id", ("id", mirror)));
            Assert.Equal(0L, await a.Scalar("SELECT count(*) FROM memory_mirrors"));
        }
        finally { Directory.Delete(folder, true); }
    }
}
