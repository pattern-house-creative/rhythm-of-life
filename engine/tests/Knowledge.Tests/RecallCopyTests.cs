using Knowledge.Adapters;

namespace Knowledge.Tests;

// SB-P2-20 (D-153): a save_document copy and the record it copies are one record to a reader. Found together, by either
// arm or through a link between them, they come back once, at the better place either took, as that row with its own
// reasons and excerpt (Recall.OneRecord); an unrelated record found beside them stays. Words that are this file's own
// (Tamsworth, Orlick), DEMO text only.
public sealed class RecallCopyTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    const string Text = "DEMO: the Tamsworth shed plans, permit TM-4471, a felt roof and oak battens.";

    [PostgresFact] public async Task A_copy_and_its_originals_star_found_together_come_back_once()
    {
        TestSettings.Use();
        var notes = KeywordRows.NotesSource();
        var db = KeywordRows.Db(fixture.Source, notes);
        var model = new MeaningModel();
        var reader = await Passages.Harness(fixture.Source, "copy-reader");
        // The note's star (its words in its passages) and a save_document copy of it: the copy's external id is the
        // note's source id, its text the note's.
        var star = await KeywordRows.Record(fixture.Source, notes, "tamsworth.md", "DEMO shed plans", Text);
        var saved = System.Text.Json.JsonDocument.Parse(await new DocumentStore(fixture.Source, SavedDocuments.Pending).Save(reader,
            new DocumentSnapshot("tamsworth.md", "DEMO shed plans (copy)", null, Text, DateTimeOffset.UtcNow.AddDays(-1)), default)).RootElement;
        var copy = saved.GetProperty("resourceId").GetGuid();
        var unrelated = await RecallRows.Add(db, fixture.Source, "Orlick permit", "The Orlick permit TM-4471 was refused.");
        var covered = PassageSources.Of(true, false, false, [notes]);
        await Passages.Sweep(new EmbeddingBackfill(db), model, covered);
        await RecallRows.Link(fixture.Source, star, copy, "copy_of");

        foreach (var recall in new[] { new Recall(db, null, covered), new Recall(db, model, covered) })
            foreach (var hops in new[] { 0, 1, 2 })
                foreach (var question in new[] { "TM-4471", "Tamsworth shed plans", "felt roof oak battens" })
                {
                    var hits = (await recall.Search(reader, new(question, 20, hops), default)).Hits;
                    // One of the two, never both, first (but for the id, which the newer unrelated row holds too).
                    var one = Assert.Single(hits, h => h.Id == star || h.Id == copy);
                    Assert.Contains(one.Id, hits.Take(question == "TM-4471" ? 2 : 1).Select(h => h.Id));
                    Assert.Contains("TM-4471", one.Excerpt);
                    if (question == "TM-4471") Assert.Contains(unrelated, hits.Select(h => h.Id));
                }
        // Each alone is still found: with no star (its source not listed) the copy is the result.
        var copyAlone = (await new Recall(db, null).Search(reader, new("TM-4471", 20), default)).Hits;
        Assert.Contains(copy, copyAlone.Select(h => h.Id));
        Assert.DoesNotContain(star, copyAlone.Select(h => h.Id));
    }
}
