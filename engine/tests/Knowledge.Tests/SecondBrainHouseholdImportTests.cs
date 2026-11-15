using Knowledge.Adapters;
using Knowledge.Adapters.Demo;
using Knowledge.Cli;
using Knowledge.Relationships;
using Knowledge.Tests.Parity;

namespace Knowledge.Tests;

// SB-P8-08 (D-242, D-247; migration 074, Cli/SecondBrainImport.cs): the household import, `rhythm import-second-brain
// --household`, on SB-P8-02's synthetic export of Alex's (fixtures.md, Second Brain export fixtures). With memory scopes
// off every memory and project comes in as the household's, never refused, Alex its author, and each memory keeps with its
// origin where it lived in Second Brain: what the read-only connector's rows say of it (the real adapter here, after one
// map rebuild over the brain as its fake service answers Alex's token, BrainService), else Family's when the scope map
// names it, else unknown. A repeat adds nothing; without the option nothing changes; with scopes on it is refused. Each
// test makes its own throwaway install. Fictional people, data and service only.
[Collection(SharedStateCollection.Name)]
public sealed class SecondBrainHouseholdImportTests
{
    static readonly SbBrain Brain = SbBrain.Load();
    const string AlexFile = "sb-export-alex.v3.json";
    static SbFile Alex() => SbFile.Read(File.ReadAllBytes(Path.Combine(ParityCorpus.Folder, AlexFile)));
    static string Ws(string name) => Brain.Workspaces.Single(w => w.Name == name).Id;

    static Task<SecondBrainImportReport> Import(MemoryArchiveTests.Install i, bool write, SecondBrainScopeMap? map = null, bool household = true) =>
        new SecondBrainImport(i.Source).Import(Alex(), "alex", map ?? SecondBrainScopeMap.None, write, household, default);

    static string Canonical(SecondBrainImportReport r) => System.Text.Json.JsonSerializer.Serialize(new { r.Outcomes, r.Held, r.HeldFrom, r.Notes, r.Review, r.Missing, r.Workspaces });

    static int Refused(SecondBrainImportReport r) =>
        r.Outcomes.Values.SelectMany(s => s.Values).SelectMany(o => o).Where(x => x.Key.StartsWith("refused:", StringComparison.Ordinal)).Sum(x => x.Value);

    // Each memory the origin map holds of a Second Brain export, by its id there: its subject, author and where it lived.
    static async Task<Dictionary<string, string>> Origins(MemoryArchiveTests.Install i)
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var cmd = i.Source.CreateCommand("""
            SELECT o.source_id,m.subject_id||'/'||m.author_id||'/'||coalesce(o.workspace,'none')
            FROM memory_origins o JOIN memories m ON m.id=o.local_id WHERE o.origin=@origin AND o.kind='memory'
            """);
        cmd.Parameters.AddWithValue("origin", SecondBrainImport.Origin);
        await using var row = await cmd.ExecuteReaderAsync();
        while (await row.ReadAsync()) all[row.GetString(0)] = row.GetString(1);
        return all;
    }

    // The connector's rows, whole: what the import reads and must leave as they are.
    static async Task<string> Connector(MemoryArchiveTests.Install i) => (string)(await i.Scalar(
        "SELECT coalesce(jsonb_agg(r ORDER BY r.id)::text,'') FROM resources r WHERE r.provider='second-brain'"))!;

    // The read-only connector's rows after one map rebuild over the brain as its service answers Alex's token (her
    // personal workspace, hers, and the Family team, the household's), as SB-P8-03's comparison makes them.
    static async Task Listed(MemoryArchiveTests.Install i, RhythmConfig config)
    {
        using var secrets = new TestSecrets().With("second-brain_token", BrainService.Token);
        var service = new BrainService(Brain, Brain.Users.Single(u => u.Name == "Alex"));
        var adapter = SecondBrainComparison.Adapter(service, config, new Database(i.Source, new AdapterRegistry([new DemoAdapter()]).Policies(config)));
        var registry = new AdapterRegistry([adapter]);
        var db = new Database(i.Source, registry.Policies(config));
        var graph = await new Principals(i.Source).System(Principals.GraphName, default);
        var state = Directory.CreateTempSubdirectory("sb-household-").FullName;
        try
        {
            var host = new RelationshipHost(db, () => RelationshipHostConfiguration.From(config, graph, state),
                c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(db, c.Principal, wanted, ct)), registry);
            Assert.Equal("complete", await host.Refresh(false, default));
            Assert.All(service.Handler.Requests, r => Assert.StartsWith("GET " + BrainService.Host + "/", r));
        }
        finally { Directory.Delete(state, true); }
    }

    [PostgresFact] public async Task With_scopes_off_every_memory_comes_in_household_keeping_where_it_lived()
    {
        var config = TestSettings.Use();
        await using var i = await MemoryArchiveTests.Install.Make("off", "alex", "sam");
        await Listed(i, config);
        var connector = await Connector(i);
        var listed = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var cmd = i.Source.CreateCommand(
            "SELECT relationship_snapshot->'Key'->>'ExternalId',scope FROM resources WHERE provider='second-brain' AND kind='memory'"))
        await using (var row = await cmd.ExecuteReaderAsync())
            while (await row.ReadAsync()) listed[row.GetString(0)] = row.GetString(1);
        // The connector files Alex's personal workspace's memories as private, the Family team's as shared, as the brain has them.
        Assert.All(listed, x => Assert.Equal(Brain.Entries.Single(e => e.Id == x.Key).WorkspaceId == Ws("Family") ? "shared" : "private", x.Value));
        string Expected(SbFileEntry e) => listed.TryGetValue(e.Id!, out var scope) ? scope == "shared" ? SecondBrainImport.Team : SecondBrainImport.Personal
            : SecondBrainImport.Unknown;
        var expected = Alex().Entries.GroupBy(Expected).ToDictionary(g => g.Key, g => g.Count());

        // Without the option nothing changes, the connector's rows there or not: Alex's own memories are refused (D-048).
        var plain = await Import(i, write: false, SecondBrainImportTests.FamilyMap(), household: false);
        Assert.Equal((14, 15), (plain.Count("memory", "alex", "refused:private_off"), plain.Count("memory", "Family", "added")));
        Assert.False(plain.Household);
        Assert.Null(plain.Workspaces);

        // A dry run of the household import writes nothing and refuses nothing: all 29 memories, 6 projects and 13 links
        // in Family, the link across two workspaces among them, since both its ends are the household's now.
        var empty = await i.Rows();
        var dry = await Import(i, write: false);
        Assert.Equal(empty, await i.Rows());
        Assert.True(dry.Household);
        Assert.Equal(0, Refused(dry));
        Assert.Equal((29, 6, 13), (dry.Count("memory", "Family", "added"), dry.Count("project", "Family", "added"), dry.Count("link", "Family", "added")));
        Assert.Equal(0, dry.Count("link", "skipped:scopes"));
        // Where they lived: the connector lists 10 of Alex's own and 11 of Family's; the 8 it leaves off (held, a draft, a
        // retracted source, one no longer true, an id that is no uuid) are unknown, every held one among them.
        Assert.Equal((10, 11, 8), (dry.Lived(SecondBrainImport.Personal, "added"), dry.Lived(SecondBrainImport.Team, "added"),
            dry.Lived(SecondBrainImport.Unknown, "added")));
        Assert.Equal((expected[SecondBrainImport.Personal], expected[SecondBrainImport.Team], expected[SecondBrainImport.Unknown]),
            (dry.Lived(SecondBrainImport.Personal, "added"), dry.Lived(SecondBrainImport.Team, "added"), dry.Lived(SecondBrainImport.Unknown, "added")));
        Assert.Equal(4, dry.Lived(SecondBrainImport.Unknown, "held"));
        Assert.Equal(4, dry.Held[SecondBrainImportReport.FamilyScope].Values.Sum());
        Assert.Equal(29, dry.Note("family_memories_authored_by_the_person"));
        Assert.Contains(dry.Missing, m => m.Field == "memory workspace_id" && m.Instead.Contains("--household", StringComparison.Ordinal));

        var wrote = await Import(i, write: true);
        Assert.Equal(Canonical(dry), Canonical(wrote));
        // Each memory the household's, Alex its author, its origin saying where it lived; no other origin row says it.
        var origins = await Origins(i);
        Assert.Equal(29, origins.Count);
        foreach (var e in Alex().Entries) Assert.Equal($"household/alex/{Expected(e)}", origins[SecondBrainImport.MemoryKey(e.Id!)]);
        Assert.Equal("household/alex/personal", origins[Brain.Entry("alex-job").Id]);
        Assert.Equal("household/alex/team", origins[Brain.Entry("family-bake-sale").Id]);
        Assert.Equal("household/alex/unknown", origins[SecondBrainImport.MemoryKey(Brain.Entry("alex-running-plan").Id)]);
        Assert.Equal(0L, await i.Scalar("SELECT count(*) FROM memory_origins WHERE kind<>'memory' AND workspace IS NOT NULL"));
        Assert.Equal(0L, await i.Scalar("SELECT count(*) FROM memories WHERE subject_id<>'household' OR scope<>'shared' OR author_id<>'alex'"));
        Assert.Equal(0L, await i.Scalar("SELECT count(*) FROM memory_projects WHERE subject_id<>'household'"));
        // The connector's rows were read, never written.
        Assert.Equal(connector, await Connector(i));

        // A repeat adds nothing, and each memory keeps where it lived as the first import kept it.
        var once = await i.Rows();
        var again = await Import(i, write: true);
        Assert.Equal(once, await i.Rows());
        Assert.Equal((29, 6, 13), (again.Count("memory", "Family", "imported_before"), again.Count("project", "Family", "imported_before"),
            again.Count("link", "Family", "imported_before")));
        Assert.Equal(0, again.Count("memory", "added") + again.Count("project", "added") + again.Count("link", "added"));
        Assert.Equal((10, 11, 8), (again.Lived(SecondBrainImport.Personal, "imported_before"), again.Lived(SecondBrainImport.Team, "imported_before"),
            again.Lived(SecondBrainImport.Unknown, "imported_before")));

        // The report says it in words.
        var lines = SecondBrainImportCommand.Lines(wrote, AlexFile, null).ToList();
        Assert.Contains(lines, l => l.EndsWith("for alex, as household memories (--household, D-247):", StringComparison.Ordinal));
        Assert.Contains("  Where the memories lived in Second Brain, kept with each one's origin:", lines);
        Assert.Contains("    the login's personal workspace: 10 added", lines);
        Assert.Contains("    a team workspace: 11 added", lines);
        Assert.Contains(lines, l => l.StartsWith("    not known here (", StringComparison.Ordinal) && l.EndsWith(": 8 added (4 of them held for review)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("D-048 set aside for this install", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("no private one is imported (D-048)", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => System.Text.RegularExpressions.Regex.IsMatch(l, "(skipped|refused):[a-z]"));
    }

    // With no connector rows, the scope map says which memories were Family's (a team workspace); the rest are unknown.
    [PostgresFact] public async Task With_no_connector_rows_the_scope_map_marks_the_team_memories_and_the_rest_is_unknown()
    {
        await using var i = await MemoryArchiveTests.Install.Make("off", "alex", "sam");
        var map = SecondBrainImportTests.FamilyMap();
        var report = await Import(i, write: true, map);
        Assert.Equal(0, Refused(report));
        Assert.Equal(29, report.Count("memory", "Family", "added"));
        Assert.Equal((0, 15, 14), (report.Lived(SecondBrainImport.Personal, "added"), report.Lived(SecondBrainImport.Team, "added"),
            report.Lived(SecondBrainImport.Unknown, "added")));
        var origins = await Origins(i);
        foreach (var e in Alex().Entries)
            Assert.Equal("household/alex/" + (map.Memories.Contains(e.Id!) ? SecondBrainImport.Team : SecondBrainImport.Unknown), origins[SecondBrainImport.MemoryKey(e.Id!)]);
        Assert.Contains(SecondBrainImportCommand.Lines(report, AlexFile, "map.json"), l => l.EndsWith("as household memories (--household, D-247), with the scope map map.json:", StringComparison.Ordinal));
        // Sam's file after it: Family's 15 were imported before, Sam's own 10 come in, nothing refused. Its two choir
        // projects both land in Family, so the memories naming choir go into neither, which the review line says.
        var sam = await new SecondBrainImport(i.Source).Import(SbFile.Read(File.ReadAllBytes(Path.Combine(ParityCorpus.Folder, "sb-export-sam.v3.json"))),
            "sam", SecondBrainScopeMap.None, true, true, default);
        Assert.Equal(0, Refused(sam));
        Assert.Equal((15, 10), (sam.Count("memory", "Family", "imported_before"), sam.Count("memory", "Family", "added")));
        Assert.Contains(sam.Review, r => r.StartsWith("the project slug choir is in this file 2 times", StringComparison.Ordinal)
            && r.EndsWith("with --household both land in Family, and a memory naming it goes into neither, its project: tag kept beside it", StringComparison.Ordinal));
        Assert.Equal(39L, await i.Scalar("SELECT count(*) FROM memories WHERE subject_id='household'"));
    }

    // With memory scopes on the option is refused before anything is written, at the terminal and in the importer.
    [PostgresFact] public async Task With_scopes_on_the_household_import_is_refused()
    {
        await using var i = await MemoryArchiveTests.Install.Make("on", "alex", "sam");
        var before = await i.Rows();
        Assert.Equal(SecondBrainImport.HouseholdWithScopesOn, (await Assert.ThrowsAsync<SecondBrainImportRefused>(() => Import(i, write: true))).Message);
        Assert.Equal(before, await i.Rows());
        var folder = Directory.CreateTempSubdirectory("sb-household-").FullName;
        try
        {
            var path = Path.Combine(folder, AlexFile);
            File.Copy(Path.Combine(ParityCorpus.Folder, AlexFile), path);
            var config = RhythmConfig.Parse(TestSettings.Yaml + "\nmemory: { scopes: on, secondBrainImport: on }\n");
            foreach (var args in new[] { new[] { path, "--person", "alex", "--household", "--write" }, [path, "--household", "--person", "alex"] })
            {
                var o = new StringWriter();
                Assert.Equal(1, await SecondBrainImportCommand.Run(new DeployFolder(folder), args, o, i.Source, config));
                Assert.Equal($"Nothing imported: {SecondBrainImport.HouseholdWithScopesOn}.", o.ToString().Trim());
            }
            Assert.Equal(before, await i.Rows());
        }
        finally { Directory.Delete(folder, true); TestSettings.Use(); }
    }
}
