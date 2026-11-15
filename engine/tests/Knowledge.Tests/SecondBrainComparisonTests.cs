using System.Text.Encodings.Web;
using System.Text.Json;
using Knowledge.Adapters;
using Knowledge.Adapters.Demo;
using Knowledge.Cli;
using Knowledge.Relationships;
using Knowledge.Tests.Parity;

namespace Knowledge.Tests;

// SB-P8-03 (story 16, story 18): the comparison harness on
// the synthetic brain (Parity/SecondBrainComparison.cs). One throwaway install holds both sides: the read-only adapter
// reading the brain as its service answers Alex's token, and the native memories imported from Alex's and Sam's exports
// with the owner's scope map. The report (Parity/sb-comparison-report.json) is checked line for line, so a change of
// either side that moves any comparison shows here; to change it on purpose, run
// RHYTHM_SB_COMPARE_WRITE=<folder> sh engine/test-db.sh --filter SecondBrainComparisonTests and copy the file over the
// checked-in one. Fictional people, places and service only.
[Collection(SharedStateCollection.Name)]
public sealed class SecondBrainComparisonTests
{
    static SbFile Read(string file) => SbFile.Read(File.ReadAllBytes(Path.Combine(ParityCorpus.Folder, file)));

    [PostgresFact] public async Task The_comparison_report_on_the_synthetic_brain()
    {
        var config = TestSettings.Use();
        var brain = SbBrain.Load();
        await using var i = await MemoryArchiveTests.Install.Make("on", "alex", "sam");
        // The native side: both people's exports, with the owner's scope map (SB-P8-01).
        var map = SecondBrainImportTests.FamilyMap();
        await new SecondBrainImport(i.Source).Import(Read("sb-export-alex.v3.json"), "alex", map, true, default);
        await new SecondBrainImport(i.Source).Import(Read("sb-export-sam.v3.json"), "sam", map, true, default);

        // The adapter's side: the brain as a service answers Alex's token, on the map after one rebuild.
        using var secrets = new TestSecrets().With("second-brain_token", BrainService.Token);
        var people = new Dictionary<string, SbUser>(StringComparer.Ordinal)
        {
            ["alex"] = brain.Users.Single(u => u.Name == "Alex"), ["sam"] = brain.Users.Single(u => u.Name == "Sam"),
        };
        var service = new BrainService(brain, people["alex"]);
        var adapter = SecondBrainComparison.Adapter(service, config, new Database(i.Source, new AdapterRegistry([new DemoAdapter()]).Policies(config)));
        var registry = new AdapterRegistry([adapter]);
        var db = new Database(i.Source, registry.Policies(config));
        var graph = await new Principals(i.Source).System(Principals.GraphName, default);
        var state = Directory.CreateTempSubdirectory("sb-compare-").FullName;
        try
        {
            var host = new RelationshipHost(db, () => RelationshipHostConfiguration.From(config, graph, state),
                c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(db, c.Principal, wanted, ct)), registry);
            Assert.Equal("complete", await host.Refresh(false, default));
            var readers = new Dictionary<string, Guid>(StringComparer.Ordinal) { ["alex"] = i.Who.Alex, ["sam"] = i.Who.Sam, ["household"] = i.Who.House };
            var report = await SecondBrainComparison.Run(new(i, brain, host, service, registry, readers, people));
            if (Environment.GetEnvironmentVariable("RHYTHM_SB_COMPARE_WRITE") is { Length: > 0 } folder)
                File.WriteAllText(Path.Combine(folder, ComparisonReport.File), JsonSerializer.Serialize(report.Lines,
                    new JsonSerializerOptions { WriteIndented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");

            // Read-only throughout: every request the adapter made was a GET (the service fails a test on anything else).
            Assert.NotEmpty(service.Handler.Requests);
            Assert.All(service.Handler.Requests, r => Assert.StartsWith("GET " + BrainService.Host + "/", r));
            Assert.Equal(ComparisonReport.Expected(), report.Lines);

            // What the report says, in numbers. Paired by the origin map, both sides serve the same memories on the map
            // but for what one side leaves out on purpose, each with its owner and project.
            Assert.Equal((20, 1, 3), (report.Count("map_alex_both"), report.Count("map_alex_adapter_only"), report.Count("map_alex_native_only")));
            Assert.Equal((0, 0), (report.Count("pairs_owner_differs"), report.Count("pairs_project_differs")));
            // Who views what matches upstream's rule for both people; who edits does not where someone else wrote a Family
            // memory upstream: the export names no author, so the importing person is every memory's author here.
            foreach (var person in new[] { "alex", "sam" })
                Assert.Equal((0, 0), (report.Count($"view_{person}_upstream_only"), report.Count($"view_{person}_native_only")));
            Assert.Equal((0, 0), (report.Count("edit_alex_upstream_only"), report.Count("edit_alex_native_only")));
            Assert.Equal((5, 0), (report.Count("edit_sam_upstream_only"), report.Count("edit_sam_native_only")));
            // The capsule: the same memory in the same slot and the same text; the ETag differs, since the answer names each
            // side's own memory ids.
            Assert.Equal((1, 1, 0), (report.Count("capsule_private_same_slots"), report.Count("capsule_private_same_text"),
                report.Count("capsule_private_same_etag")));
            // Retention: nothing of upstream's trash, versions or audit comes across.
            Assert.Equal((2, 0, 5, 0), (report.Count("trash_upstream"), report.Count("trash_native"), report.Count("versions_upstream"),
                report.Count("versions_native")));
            // Recall finds every expected memory natively and none through the adapter, whose records it never serves.
            Assert.Equal((12, 0, 0), (report.Count("queries_recall_native"), report.Count("queries_recall_adapter"),
                report.Count("queries_recall_adapter_rows_served")));
        }
        finally { Directory.Delete(state, true); }
    }
}
