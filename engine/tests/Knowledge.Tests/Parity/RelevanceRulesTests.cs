using Knowledge.Relationships;

namespace Knowledge.Tests.Parity;

// SB-P4-12: the two measured relevance ideas that change how the map links, by their written rules (RelevanceRules):
// shared names and the hub correction. Neither was kept, so these hold the measurement, not the map
// (docs/dev/second-brain-parity/relevance.md). All of it DEMO data.
public sealed class RelevanceRulesTests
{
    static readonly IReadOnlySet<string> People = SharedNames.People(["alex", "Alex", "sam", "Sam"]);

    static (DiscoveryRecord Star, IReadOnlyList<string> Candidates) Star(string subject, string text, string title = "") =>
        (SyntheticRelationshipSource.New("notes", "note") with { Key = new ResourceKey(subject, "notes", "notes", Guid.NewGuid().ToString("N")) },
            SharedNames.Candidates(title, text));

    static Dictionary<(ResourceKey, ResourceKey), string[]> Links(params (DiscoveryRecord Star, IReadOnlyList<string> Candidates)[] stars) =>
        SharedNames.Links([.. stars.Select(s => s.Star)], stars.ToDictionary(s => s.Star.Key, s => s.Candidates), People);

    [Fact] public void Candidates_are_runs_of_capitalised_words_marked_where_they_open_a_segment()
    {
        Assert.Equal(["^Booked Thornfield Joinery"],
            SharedNames.Candidates("", "Booked Thornfield Joinery to fit the kitchen from 12 to 16 October 2026, as in their quote KR-5521."));
        // A possessive is its name; calendar words, codes and all capitals are no names and end a run.
        Assert.Equal(["^Switched", "Ravenmoor"], SharedNames.Candidates("", "Switched to Ravenmoor's tariff from 1 April 2026 at 19.8p per kWh."));
        Assert.Equal(["^Milk", "Dairyhill"], SharedNames.Candidates("", "Milk is delivered by Dairyhill on Tuesdays and Fridays, 1.10 GBP a pint."));
        Assert.Equal(["^The Corvel Estra"], SharedNames.Candidates("", "The Corvel Estra failed its MOT on 19 May 2026."));
        // A segment opens at a line, a heading, a list item and after . ! ? : or ;.
        Assert.Equal(["^Heat", "^Clear", "^Ask Brightwater Heating", "^Stage", "^Wrenmouth", "Saltreach"],
            SharedNames.Candidates("", "# Heat pump checklist\n\n- Clear the side passage.\n- Ask Brightwater Heating about it. Stage 1: Wrenmouth to Saltreach."));
        // The title is read first, then the text; each run once, at most PerRecord.
        Assert.Equal(["^Pepper Care Plan", "^Pepper", "Pebblekin"], SharedNames.Candidates("Pepper Care Plan", "Pepper eats Pebblekin kibble; Pepper eats Pebblekin."));
        var many = string.Join(", ", Enumerable.Range(0, 40).Select(i => "near Place" + (char)('a' + i % 26) + (char)('a' + i / 26)));
        Assert.Equal(SharedNames.PerRecord, SharedNames.Candidates("", many).Count);
        Assert.Empty(SharedNames.Candidates("", "日本語クラスは毎週火曜日の夜七時から"));
    }

    [Fact] public void A_name_is_a_word_seen_where_its_place_does_not_explain_its_capital()
    {
        IReadOnlyList<string>[] pass =
        [
            SharedNames.Candidates("", "Home insurance is with Pellmoor Mutual."),
            SharedNames.Candidates("", "Home insurance moved to Ostrander Home Cover."),
            SharedNames.Candidates("", "Booked Pepper the cat in at Quillon Veterinary."),
            SharedNames.Candidates("", "While Alex is away, Sam feeds Pepper the cat and Sam Fenwick helps."),
        ];
        var words = SharedNames.NameWords(pass);
        Assert.Contains("Home", words);
        Assert.DoesNotContain("Booked", words);
        // "Home" opening a segment alone is dropped; inside a run it stays part of the name.
        Assert.Equal(["pellmoor mutual"], SharedNames.Names(pass[0], words, People));
        Assert.Equal(["ostrander home cover"], SharedNames.Names(pass[1], words, People));
        // The opening word that is no name word goes, and what follows is a name in its own right.
        Assert.Equal(["pepper", "quillon veterinary"], SharedNames.Names(pass[2], words, People).Order());
        // The household's own people are dropped by id or name as one word; a longer name keeps them.
        Assert.Equal(["pepper", "sam fenwick"], SharedNames.Names(pass[3], words, People).Order());
    }

    [Fact] public void Stars_that_share_a_rare_name_are_joined_and_a_common_one_or_two_people_never()
    {
        var plot = Star("sam", "Sam moved to plot 22 at Nettlebank allotments.");
        var rent = Star("sam", "Sam owes the Nettlebank allotment society the rent.");
        var rules = Star("sam", "These are the rules of Nettlebank.", "Allotment rules");
        var cafe = Star("alex", "Alex likes the flat white at Kelptide Café.");
        var links = Links(plot, rent, rules, cafe);
        Assert.Equal(3, links.Count);
        Assert.All(links.Values, names => Assert.Equal(["nettlebank"], names));
        // In more than MaxStars stars a name is a common word, and in one it joins nothing.
        var six = Enumerable.Range(0, 6).Select(i => Star("sam", $"Note {i} from the trip to Gullhaven harbour.")).ToArray();
        Assert.Empty(Links([.. six, cafe]));
        // Two names in common, both kept.
        var a = Star("household", "We met Rowan Pike at Fallow Lane.");
        var b = Star("household", "Rowan Pike quoted for the shed at Fallow Lane.");
        Assert.Equal(["fallow lane", "rowan pike"], Assert.Single(Links(a, b)).Value);
        // Two people's stars are never joined; one's and the household's are.
        var samCafe = Star("sam", "The Kelptide Café on the harbour.");
        var familyCafe = Star("household", "Family brunch at Kelptide Café.");
        Assert.Equal(2, Links(cafe, samCafe, familyCafe).Count);
        Assert.Empty(Links(cafe, samCafe));
    }

    [Fact] public void The_hub_correction_changes_which_pairs_pass_never_how_many()
    {
        static float[] Unit(params float[] v) { var norm = MathF.Sqrt(v.Sum(x => x * x)); return [.. v.Select(x => x / norm)]; }
        DiscoveryRecord Vec(string title, params float[] v) => SyntheticRelationshipSource.New("notes", "note") with { Title = title, Embedding = Unit(v) };
        // Three stars close to one another and so to everything near them, and a pair close only to each other.
        DiscoveryRecord[] stars =
        [
            Vec("Common", 1, 1, 1, 0, 0), Vec("Common two", 1, 1, 0.6f, 0, 0), Vec("Common three", 1, 0.6f, 1, 0, 0),
            Vec("Pair one", 0, 0, 0, 1, 0.2f), Vec("Pair two", 0, 0, 0, 0.75f, 1),
        ];
        string[] Titles(IEnumerable<(ResourceKey, ResourceKey)> links) => [.. links.Select(l => string.Join(" + ",
            new[] { stars.Single(s => s.Key == l.Item1).Title, stars.Single(s => s.Key == l.Item2).Title }.Order())).Order()];
        var plain = HubCorrection.Links(stars, 0.9, 3, null, null, null);
        var corrected = HubCorrection.Links(stars, 0.9, 3, null, null, 2);
        // 0.9 lets three pairs through, all among the common three; corrected, the pair (0.75) takes the weakest's place.
        Assert.Equal(["Common + Common three", "Common + Common two", "Common three + Common two"], Titles(plain));
        Assert.Equal(["Common + Common three", "Common + Common two", "Pair one + Pair two"], Titles(corrected));
    }

    [Fact] public async Task Without_the_correction_the_rule_is_the_maps_own()
    {
        static float[] Unit(params float[] v) { var norm = MathF.Sqrt(v.Sum(x => x * x)); return [.. v.Select(x => x / norm)]; }
        var random = new Random(12);
        DiscoveryRecord[] stars = [.. Enumerable.Range(0, 40).Select(i => SyntheticRelationshipSource.New("notes", i % 5 == 0 ? "document" : "note")
            with { Title = "Star " + i, Embedding = Unit([.. Enumerable.Range(0, 8).Select(_ => (float)random.NextDouble())]) })];
        var map = (await new SimilaritySuggestions(0.9, 3, 0.95, 0.85).Suggest(stars, default))
            .Select(s => string.CompareOrdinal(s.Source.GraphId, s.Target.GraphId) < 0 ? (s.Source, s.Target) : (s.Target, s.Source)).ToHashSet();
        var rule = HubCorrection.Links(stars, 0.9, 3, 0.95, 0.85, null)
            .Select(l => string.CompareOrdinal(l.Item1.GraphId, l.Item2.GraphId) < 0 ? l : (l.Item2, l.Item1)).ToHashSet();
        Assert.NotEmpty(map);
        Assert.True(map.SetEquals(rule));
        // With references: two records one references are never compared, by the map or the rule, which frees their
        // neighbour slots for others.
        var stated = new Dictionary<ResourceKey, ResourceKey>();
        foreach (var (from, to) in map.OrderBy(p => p.Item1.GraphId, StringComparer.Ordinal).ThenBy(p => p.Item2.GraphId, StringComparer.Ordinal))
            if (stated.Count < 3) stated.TryAdd(from, to);
        var referenced = stars.Select(r => stated.TryGetValue(r.Key, out var to) ? r with { References = [new Reference(to, null, "relates_to")] } : r).ToArray();
        var mapWith = (await new SimilaritySuggestions(0.9, 3, 0.95, 0.85).Suggest(referenced, default))
            .Select(s => string.CompareOrdinal(s.Source.GraphId, s.Target.GraphId) < 0 ? (s.Source, s.Target) : (s.Target, s.Source)).ToHashSet();
        var ruleWith = HubCorrection.Links(referenced, 0.9, 3, 0.95, 0.85, null)
            .Select(l => string.CompareOrdinal(l.Item1.GraphId, l.Item2.GraphId) < 0 ? l : (l.Item2, l.Item1)).ToHashSet();
        Assert.False(mapWith.SetEquals(map), "the references should change what the map links");
        Assert.True(mapWith.SetEquals(ruleWith));
    }
}
