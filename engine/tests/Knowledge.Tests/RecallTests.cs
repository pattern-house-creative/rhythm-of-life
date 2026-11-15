using System.Text.Json;

namespace Knowledge.Tests;

// Combined retrieval's steps on their own (SB-P2-08): the fusion, its weights and ties, diversity in blocks, the output
// budget, the bounds a question is checked against, and that asking for more only ever extends the list. DEMO data only.
public sealed class RecallTests
{
    static RecallRow Row(string external, bool indexed = true) =>
        new(JsonSerializer.SerializeToElement(new { external_id = external }), "", 0, "demo", external, indexed);
    static RecallArmHit Keyword(Guid id, int rank, double weight, int exact = 0) => new(id, rank, weight, exact, [], null, null, null, null);
    static RecallArmHit Meaning(Guid id, int rank, float[]? vector = null, string? passage = null) => new(id, rank, 1, 0, [], 0.5, 0, passage, vector);
    static Guid Id(int n) => new($"00000000-0000-0000-0000-{n:000000000000}");

    [Fact] public void Fusion_adds_each_arms_weight_over_k_plus_rank_and_both_arms_outrank_one()
    {
        var (a, b, c) = (Id(1), Id(2), Id(3));
        var rows = new Dictionary<Guid, RecallRow> { [a] = Row("a"), [b] = Row("b"), [c] = Row("c") };
        var o = RecallOptions.Default with { Impute = false };
        var fused = Recall.Fuse([Keyword(a, 1, 1), Keyword(c, 2, 1)], [Meaning(b, 1), Meaning(c, 2)], rows, o);
        Assert.Equal([c, a, b], fused.Select(x => x.Id));
        Assert.Equal(2 / 62.0, fused[0].Score, 9);
        // a and b tie on 1/61: the keyword arm's order decides, before the source's own id.
        Assert.Equal(1 / 61.0, fused[1].Score, 9);
        Assert.Equal(1 / 61.0, fused[2].Score, 9);
        // A rare word's weight puts a keyword-only result above one both arms found lower down.
        var weighted = Recall.Fuse([Keyword(a, 1, 5), Keyword(c, 2, 1)], [Meaning(b, 1), Meaning(c, 2)], rows, o);
        Assert.Equal(a, weighted[0].Id);
        // A candidate that is no longer readable (not among the rows read again with the predicate) is left out.
        Assert.Equal([a, b], Recall.Fuse([Keyword(a, 1, 1)], [Meaning(b, 1), Meaning(c, 2)], rows.Where(r => r.Key != c).ToDictionary(), o).Select(x => x.Id));
    }

    [Fact] public void Ties_go_to_the_sources_own_id_never_to_the_engines()
    {
        // Same score, same ranks: the order is the source's id whatever the random engine ids are.
        var (x, y) = (Id(9), Id(1));
        var rows = new Dictionary<Guid, RecallRow> { [x] = Row("alpha"), [y] = Row("beta") };
        var fused = Recall.Fuse([], [Meaning(y, 1), Meaning(x, 1)], rows, RecallOptions.Default);
        Assert.Equal([x, y], fused.Select(c => c.Id));
    }

    [Fact] public void A_row_the_meaning_arm_cannot_find_is_imputed_only_when_the_arm_ran()
    {
        var (series, both) = (Id(1), Id(2));
        var rows = new Dictionary<Guid, RecallRow> { [series] = Row("series", indexed: false), [both] = Row("note") };
        // The data series is the keyword arm's first; the note its second and the meaning arm's first. Imputed at the
        // meaning arm's own scale (weight 1 at its keyword rank), whatever its keyword weight.
        RecallArmHit[] keyword = [Keyword(series, 1, 1), Keyword(both, 2, 1)];
        RecallArmHit[] meaning = [Meaning(both, 1)];
        var plain = Recall.Fuse(keyword, meaning, rows, RecallOptions.Default with { Impute = false });
        Assert.Equal(both, plain[0].Id);
        var imputed = Recall.Fuse(keyword, meaning, rows, RecallOptions.Default);
        Assert.Equal(series, imputed[0].Id);
        Assert.Equal(2 / 61.0, imputed[0].Score, 9);
        // A row with passages is never imputed, and with the meaning arm skipped nothing is.
        Assert.Equal(1 / 62.0 + 1 / 61.0, imputed[1].Score, 9);
        var skipped = Recall.Fuse(keyword, [], rows, RecallOptions.Default, meaningRan: false);
        Assert.Equal(1 / 61.0, skipped[0].Score, 9);
        Assert.Equal(4 / 61.0, Recall.Fuse([Keyword(series, 1, 3)], [], rows, RecallOptions.Default)[0].Score, 9);
    }

    [Fact] public void Keyword_weights_by_mode()
    {
        Assert.Equal(3.5, Recall.Weight(RecallWeight.Rarity, 3.5, 7, 0));
        Assert.Equal(0.5, Recall.Weight(RecallWeight.Coverage, 3.5, 7, 0));
        Assert.Equal(1.5, Recall.Weight(RecallWeight.CoverageExact, 3.5, 7, 2));
        Assert.Equal(1, Recall.Weight(RecallWeight.Coverage, 0, 0, 0));
        Assert.Equal(1, Recall.Weight(RecallWeight.Flat, 3.5, 7, 2));
    }

    [Fact] public void Diversity_marks_down_a_near_copy_and_orders_each_block_by_score()
    {
        float[] Unit(params float[] v) { var n = (float)Math.Sqrt(v.Sum(x => x * x)); return [.. v.Select(x => x / n)]; }
        var ids = Enumerable.Range(1, 7).Select(Id).ToArray();
        var rows = ids.ToDictionary(i => i, i => Row(i.ToString()));
        // 1 and 2 are near copies; 3 to 7 point elsewhere.
        RecallArmHit[] meaning =
        [
            Meaning(ids[0], 1, Unit(1, 0, 0)), Meaning(ids[1], 2, Unit(1, 0.01f, 0)), Meaning(ids[2], 3, Unit(0, 1, 0)),
            Meaning(ids[3], 4, Unit(0, 0, 1)), Meaning(ids[4], 5, Unit(0, 1, 1)), Meaning(ids[5], 6, Unit(1, 1, 0)), Meaning(ids[6], 7, Unit(1, 0, 1)),
        ];
        var pool = Recall.Fuse([], meaning, rows, RecallOptions.Default);
        // Off (the default): the fused order.
        Assert.Equal(ids, Recall.Diversify(pool, RecallOptions.Default).Select(c => c.Id));
        // On: the near copy leaves the first block, which is still ordered by score.
        var diverse = Recall.Diversify(pool, RecallOptions.Default with { Lambda = 0.5 });
        Assert.DoesNotContain(ids[1], diverse.Take(5).Select(c => c.Id));
        Assert.Equal(diverse.Take(5).OrderByDescending(c => c.Score).Select(c => c.Id), diverse.Take(5).Select(c => c.Id));
        // Greedy, in whole blocks: a shorter list is the start of a longer one.
        foreach (var depth in new[] { 5, 10 })
            Assert.Equal(diverse.Take(depth).Select(c => c.Id), Recall.Diversify(pool, RecallOptions.Default with { Lambda = 0.5, MaxTopK = depth }).Select(c => c.Id));
        Assert.Equal(1.0, Recall.Cosine(Unit(1, 2, 3), Unit(2, 4, 6)), 6);
        Assert.Equal(0, Recall.Cosine([1, 0], [1, 0, 0]));
        Assert.Equal([0.25f, -1.5f, 3e-5f], Recall.ParseVector("[0.25,-1.5,3e-05]"));
    }

    // A property test over random arms: whatever the arms say, the list built to any whole number of blocks, its
    // excerpts included, is the start of the list built to 20, and no resource appears twice. Recall.Search builds to
    // MaxTopK and cuts to topK last, so any topK's results are the start of a larger topK's.
    [Fact] public void Asking_for_more_only_extends_the_list()
    {
        var random = new Random(20261101);
        for (var round = 0; round < 200; round++)
        {
            var ids = Enumerable.Range(1, 40).Select(Id).ToArray();
            var rows = ids.ToDictionary(i => i, i => Row(i.ToString(), indexed: random.Next(3) > 0));
            RecallArmHit[] keyword = [.. ids.OrderBy(_ => random.Next()).Take(random.Next(0, 30)).Select((id, i) => Keyword(id, i + 1, random.NextDouble() * 6))];
            RecallArmHit[] meaning = [.. ids.OrderBy(_ => random.Next()).Take(random.Next(0, 30))
                .Select((id, i) => Meaning(id, i + 1, [(float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble()], new string('x', random.Next(0, 5000))))];
            var o = RecallOptions.Default with { Lambda = round % 2 == 0 ? 1 : 0.7 };
            var full = Recall.Budget(Recall.Diversify(Recall.Fuse(keyword, meaning, rows, o), o), o);
            Assert.Equal(full.Count, full.Select(h => h.Id).Distinct().Count());
            Assert.True(full.Count <= o.MaxTopK);
            foreach (var depth in new[] { 5, 10, 15 })
            {
                var shorter = o with { MaxTopK = depth };
                Assert.Equal(full.Take(depth).Select(h => (h.Id, h.Excerpt, h.Score)),
                    Recall.Budget(Recall.Diversify(Recall.Fuse(keyword, meaning, rows, shorter), shorter), shorter).Select(h => (h.Id, h.Excerpt, h.Score)));
            }
        }
    }

    [Fact] public void Excerpts_stay_within_the_budget_and_say_when_they_are_cut()
    {
        var ids = Enumerable.Range(1, 20).Select(Id).ToArray();
        var rows = ids.ToDictionary(i => i, i => Row(i.ToString()) with { Head = "", ContentLength = 9000 });
        var long_ = string.Join(" ", Enumerable.Repeat("fictional words", 600));
        var o = RecallOptions.Default;
        var hits = Recall.Budget(Recall.Fuse([], [.. ids.Select((id, i) => Meaning(id, i + 1, passage: long_))], rows, o), o);
        // The first two are strong (within 0.75 of the first's score): up to 4000 each; the rest 400; 12 000 in all.
        Assert.InRange(hits[0].Excerpt.Length, 3900, 4000);
        Assert.InRange(hits[1].Excerpt.Length, 3900, 4000);
        Assert.All(hits.Skip(2), h => Assert.True(h.Excerpt.Length <= 400));
        Assert.True(hits.Sum(h => h.Excerpt.Length) <= o.OutputBudget);
        Assert.Contains(hits, h => h.Excerpt.Length == 0);
        Assert.All(hits, h => Assert.True(h.Truncated && h.FullLength == long_.Length));
        // Cut at a space, never inside a word or a surrogate pair.
        Assert.Equal("fictional", Recall.Cut("fictional words", 12));
        Assert.Equal("ab", Recall.Cut("ab\U0001F600", 3));
        Assert.Equal("", Recall.Cut("anything", 0));
        // A short text is whole and not marked cut.
        var whole = Recall.Budget(Recall.Fuse([], [Meaning(ids[0], 1, passage: "DEMO: short.")], rows.ToDictionary(r => r.Key, r => r.Value with { ContentLength = 12 }), o), o);
        Assert.Equal(("DEMO: short.", false, 12), (whole[0].Excerpt, whole[0].Truncated, whole[0].FullLength));
        // A weak second result gets a snippet only.
        var weak = Recall.Budget(Recall.Fuse([Keyword(ids[0], 1, 10)], [Meaning(ids[1], 30, passage: long_)], rows, o), o);
        Assert.True(weak[1].Excerpt.Length <= 400);
    }

    [Fact] public void A_question_is_checked_against_its_bounds()
    {
        var o = RecallOptions.Default;
        Recall.Check(new("boiler", 20, 3, After: DateTimeOffset.UnixEpoch, Before: DateTimeOffset.UnixEpoch.AddDays(1)), o);
        foreach (var bad in new RecallQuery[]
        {
            new(""), new("   "), new(new string('a', 801)), new("boiler", 0), new("boiler", 21), new("boiler", 5, -1), new("boiler", 5, 4),
            new("boiler", Kind: new string('k', 81)), new("boiler", After: DateTimeOffset.UnixEpoch, Before: DateTimeOffset.UnixEpoch),
        })
            Assert.Throws<ArgumentException>(() => Recall.Check(bad, o));
    }

    // The weights are columns added to ranked search's query; without them its query is what search_knowledge runs.
    [Fact] public void Fusion_weights_leave_ranked_searchs_order_as_it_was()
    {
        foreach (var query in new[] { "RHY-4127 export", "the who", "" })
        {
            var (plain, args) = LexicalSearch.Standard.Sql(query, "r.id", "true", "10");
            var (weighted, weightedArgs) = LexicalSearch.Standard.Sql(query, "r.id", "true", "10", weights: true);
            Assert.DoesNotContain("lexical_", plain, StringComparison.Ordinal);
            Assert.Contains("lexical_weight", weighted, StringComparison.Ordinal);
            Assert.Contains("lexical_total", weighted, StringComparison.Ordinal);
            Assert.Equal(plain[plain.LastIndexOf("ORDER BY", StringComparison.Ordinal)..], weighted[weighted.LastIndexOf("ORDER BY", StringComparison.Ordinal)..]);
            Assert.Equal(plain[..plain.LastIndexOf("SELECT", StringComparison.Ordinal)], weighted[..weighted.LastIndexOf("SELECT", StringComparison.Ordinal)]);
            Assert.Equal(args, weightedArgs);
        }
    }

    // SB-P2-20 (D-153): a save_document copy and the record it copies are one record to a reader, so they come back once,
    // at the better place either took, as that row with its own reasons and excerpt; the other is left out, and so is
    // any other copy of a record already listed. Records that are not copies never fold, whatever ids they share, and a
    // copy of a record not among the results stays. DEMO ids only.
    [Fact] public void A_copy_and_the_record_it_copies_come_back_once_at_the_better_place()
    {
        var (star, copy, other, legacy, twin, stray) = (Id(1), Id(2), Id(3), Id(4), Id(5), Id(6));
        RecallRow Original(string external, params string[] ids) => Row(external) with { SourceIds = ids };
        RecallRow CopyOf(string external, params string[] ids) => Row(external) with { CopyOf = ids };
        var rows = new Dictionary<Guid, RecallRow>
        {
            [star] = Original("drive:demo-quote", "drive:demo-quote", "demo-quote"),
            [copy] = CopyOf("demo-quote", "demo-quote"),
            [other] = Original("demo-other", "demo-other"),
            // A legacy household copy names its original by sourceFileId.
            [legacy] = CopyOf("legacy-17", "legacy-17", "demo-quote"),
            [twin] = Original("notes:demo-quote", "notes:demo-quote", "demo-quote"),
            [stray] = CopyOf("demo-gone", "demo-gone"),
        };
        var o = RecallOptions.Default with { Impute = false };
        // The copy ranks first: it stands for the record, so the star and the legacy copy go.
        var copyFirst = Recall.OneRecord(Recall.Fuse([Keyword(copy, 1, 1), Keyword(other, 2, 1), Keyword(stray, 3, 1)],
            [Meaning(star, 3), Meaning(legacy, 4)], rows, o));
        Assert.Equal([copy, other, stray], copyFirst.Select(c => c.Id));
        Assert.Equal(1, copyFirst[0].Keyword!.Rank);
        Assert.Null(copyFirst[0].Meaning);
        // The star ranks first: it stands for the record with its own reasons, and both copies go.
        var starFirst = Recall.OneRecord(Recall.Fuse([Keyword(star, 1, 1), Keyword(copy, 2, 1)], [Meaning(legacy, 1), Meaning(other, 2)], rows, o));
        Assert.Equal([star, other], starFirst.Select(c => c.Id));
        Assert.Equal(1, starFirst[0].Keyword!.Rank);
        Assert.Null(starFirst[0].Meaning);
        // Two records that are not copies stay, though they share an id; with no copy the list is the fused one.
        var plain = Recall.Fuse([Keyword(star, 1, 1), Keyword(other, 2, 1)], [Meaning(twin, 1)], rows, o);
        Assert.Equal(plain, Recall.OneRecord(plain));
        Assert.Equal(3, plain.Count);
        // Copies of a record not among the results stay, however many there are.
        Assert.Equal([copy, legacy], Recall.OneRecord(Recall.Fuse([Keyword(copy, 1, 1), Keyword(legacy, 2, 1)], [], rows, o)).Select(c => c.Id));
    }
}
