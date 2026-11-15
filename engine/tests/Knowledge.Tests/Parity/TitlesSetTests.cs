namespace Knowledge.Tests.Parity;

// The titles-model evaluation's set and rubric are test inputs, so they are checked like code in every ordinary run:
// D-027's minimum size, fictional people and scopes, reference titles that the rubric itself scores 4 of 4, private
// details that the star does not already show, and a scorer that catches what each criterion is for. The evaluation
// against a real model is opt-in (TitlesEvaluation).
public sealed class TitlesSetTests
{
    static readonly TitlesSet Set = TitlesSet.Load();

    [Fact] public void The_set_meets_D027s_minimum_with_fictional_people_and_every_lane()
    {
        Assert.StartsWith("DEMO", Set.Label);
        // The reviewer's judgements are demo data too, labelled like every other Parity fixture.
        Assert.True(File.Exists(TitlesReview.Path), TitlesReview.Path);
        Assert.StartsWith("DEMO", TitlesReview.Load().Label);
        Assert.Equal("D-027", Set.Rule);
        Assert.True(Set.Items.Length >= 30, $"D-027 needs 30 or more items, the set has {Set.Items.Length}");
        Assert.Equal(Set.Items.Length, Set.Items.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(Set.Items, i =>
        {
            Assert.Contains(i.Author, (string[])["alex", "sam"]);
            Assert.True(i.Scope == "family" || i.Scope == "personal:" + i.Author, $"{i.Id}: {i.Author} may not write {i.Scope}");
            Assert.Contains(i.Lane, Set.Lanes.Keys);
            Assert.Contains(i.Lang, (string[])["en", "fr", "de", "es"]);
            Assert.InRange(i.Text.Length, 1, 6000); // the adapter's own input limit
            Assert.NotEmpty(i.Subject);
            Assert.All(System.Text.RegularExpressions.Regex.Matches(i.Text, @"[a-z]+://([^/\s]+)").Select(m => m.Groups[1].Value),
                host => Assert.EndsWith("example.invalid", host));
        });
        Assert.All(Set.Lanes.Keys, lane => Assert.True(Set.Items.Count(i => i.Lane == lane) >= 8, $"lane {lane} has fewer than 8 items"));
    }

    [Fact] public void Every_reference_title_scores_four_of_four()
    {
        var misses = Set.Items.Select(i => (i.Id, Score: TitlesRubric.Score(i, i.Reference)))
            .Where(x => x.Score.Points != 4)
            .Select(x => $"{x.Id}: missed {x.Score.Missed} (unsupported {string.Join(",", x.Score.Unsupported)}; leaked {string.Join(",", x.Score.Leaked)})");
        Assert.Empty(misses);
    }

    [Fact] public void Private_details_lie_beyond_what_the_star_already_shows()
    {
        Assert.True(Set.Items.Count(i => i.Private.Length > 0) >= 15, "too few items test the private criterion");
        Assert.All(Set.Items, i => Assert.All(i.Private, detail =>
        {
            var parts = TitlesRubric.Tokens(detail).ToArray();
            Assert.NotEmpty(parts);
            var text = TitlesRubric.Tokens(i.Text).ToArray();
            Assert.True(parts.All(text.Contains), $"{i.Id}: '{detail}' is not in the text");
            var shown = TitlesRubric.Tokens(TitlesRubric.FirstLine(i.Text) + " " + TitlesRubric.Excerpt(i)).ToArray();
            Assert.False(parts.All(shown.Contains), $"{i.Id}: '{detail}' is already on the first line or in the excerpt");
        }));
    }

    [Fact] public void The_excerpt_is_the_adapters_first_line_label()
    {
        var item = Set.Items.Single(i => i.Id == "t35");
        Assert.StartsWith("The flat on Weir Street", TitlesRubric.Excerpt(item)); // a link alone is skipped
        Assert.Equal("Heat pump checklist", TitlesRubric.Excerpt(Set.Items.Single(i => i.Id == "t16")));
        Assert.All(Set.Items, i => Assert.InRange(TitlesRubric.Excerpt(i).Length, 1, TitlesRubric.MaxLength));
    }

    [Theory]
    [InlineData("t01", "Boiler service on 13th Nov", "")]                  // an ordinal and a month abbreviation still match
    [InlineData("t01", "Boiler repair on Tuesday", "a")]                    // "repair" and "Tuesday" are not in the text
    [InlineData("t19", "Note to self", "s")]                                // frame words name no subject
    [InlineData("t19", "Spare key under the blue plant pot", "p")]          // where the key is, beyond the first line
    [InlineData("t14", "Surprise party at the Ottervale boathouse", "p")]
    [InlineData("t21", "Pepper's flea treatment, 4.2 kg", "p")]
    [InlineData("t22", "From the call this morning: the Oakvale Media contract renewal is moving to January", "l")]
    [InlineData("t29", "Ablesung der Heizung am 14. November", "")]
    [InlineData("t25", "Dentist appointment moved to Thursday", "")]        // an accepted restatement
    public void The_rubric_catches_what_each_criterion_is_for(string id, string title, string missed) =>
        Assert.Equal(missed, TitlesRubric.Score(Set.Items.Single(i => i.Id == id), title).Missed);

    [Fact] public void A_review_judgement_overrides_one_word_or_one_title_for_every_candidate()
    {
        var boiler = Set.Items.Single(i => i.Id == "t01");
        var party = Set.Items.Single(i => i.Id == "t14");
        var review = new TitlesReview("test", [
            new("t01", "upcoming", null, "accurate", true, "the booking is in the future"),
            new("t01", null, "Heating engineer booked", "specific", true, "names the boiler visit by who comes"),
            new("t14", null, "Alex is planning a surprise party", "accurate", false, "Sam plans the party, not Alex"),
        ]);
        Assert.Equal("a", TitlesRubric.Score(boiler, "Upcoming boiler service").Missed);
        Assert.Equal("", TitlesRubric.Score(boiler, "Upcoming boiler service", review).Missed);
        Assert.Equal("s", TitlesRubric.Score(boiler, "Heating engineer booked").Missed);
        Assert.Equal("", TitlesRubric.Score(boiler, "Heating engineer booked", review).Missed);
        Assert.Equal("", TitlesRubric.Score(party, "Alex is planning a surprise party").Missed); // every word is in the text
        Assert.Equal("a", TitlesRubric.Score(party, "Alex is planning a surprise party", review).Missed);
    }

    [Fact] public void Review_judgements_name_real_items_and_give_reasons()
    {
        var review = TitlesReview.Load();
        var ids = Set.Items.Select(i => i.Id).ToHashSet();
        Assert.All(review.Judgements, j =>
        {
            Assert.Contains(j.Item, ids);
            Assert.True(j.Word is not null ^ j.Title is not null, $"{j.Item}: a judgement names a word or a title");
            Assert.Contains(j.Criterion, (string[])["accurate", "specific", "private"]);
            Assert.True(j.Word is null || j.Criterion == "accurate", $"{j.Item}: a word judgement is about accuracy");
            Assert.False(string.IsNullOrWhiteSpace(j.Reason), $"{j.Item}: a judgement gives its reason");
        });
    }
}
