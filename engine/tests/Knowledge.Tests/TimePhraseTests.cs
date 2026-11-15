namespace Knowledge.Tests;

// SB-P2-16 (RET-01): time phrases become a window on the source's own time, read in the install's zone, and leave the
// question; a date that could be part of a name, an as-of date, a range and two dates standing alone are no window. In
// Europe/London and UTC, at fixed clocks. DEMO questions only.
public sealed class TimePhraseTests
{
    static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    static DateTimeOffset At(string instant) => DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture);

    [Fact] public void Yesterday_and_today_are_the_zones_own_days()
    {
        // 23:30 UTC on 1 July is 00:30 on 2 July in London (summer time): London's yesterday is 1 July, UTC's 30 June.
        var now = At("2026-07-01T23:30:00Z");
        var london = TimePhrases.Parse("what did alex save yesterday?", now, London)!;
        Assert.Equal((At("2026-06-30T23:00:00Z"), At("2026-07-01T23:00:00Z")), (london.After!.Value, london.Before!.Value));
        Assert.Equal("what did alex save ?", london.Query);
        var utc = TimePhrases.Parse("what did alex save yesterday?", now, Utc)!;
        Assert.Equal((At("2026-06-30T00:00:00Z"), At("2026-07-01T00:00:00Z")), (utc.After!.Value, utc.Before!.Value));
        var today = TimePhrases.Parse("notes today", now, London)!;
        Assert.Equal((At("2026-07-01T23:00:00Z"), (DateTimeOffset?)null), (today.After!.Value, today.Before));
        Assert.Equal("notes", today.Query);
    }

    [Fact] public void Weeks_and_months_start_where_the_zone_says()
    {
        // Saturday 31 October 2026, noon UTC: London is back on GMT since the 25th.
        var now = At("2026-10-31T12:00:00Z");
        Assert.Equal(At("2026-10-26T00:00:00Z"), TimePhrases.Parse("sam sleep this week", now, London)!.After);
        Assert.Equal("sam sleep", TimePhrases.Parse("sam sleep this week", now, London)!.Query);
        // A Sunday's week began the Monday before.
        Assert.Equal(At("2026-10-26T00:00:00Z"), TimePhrases.Parse("this week", At("2026-11-01T12:00:00Z"), Utc)!.After);
        Assert.Equal(now.AddDays(-7), TimePhrases.Parse("bills last week", now, London)!.After);
        Assert.Equal(now.AddDays(-3), TimePhrases.Parse("the last 3 days of steps", now, Utc)!.After);
        Assert.Equal(now.AddDays(-14), TimePhrases.Parse("runs in the last 2 weeks", now, Utc)!.After);
        // London's October began on summer time.
        var month = TimePhrases.Parse("what changed this month", now, London)!;
        Assert.Equal((At("2026-09-30T23:00:00Z"), (DateTimeOffset?)null), (month.After!.Value, month.Before));
        // Last month over a year's turn.
        var january = TimePhrases.Parse("invoices last month", At("2027-01-15T09:00:00Z"), London)!;
        Assert.Equal((At("2026-12-01T00:00:00Z"), At("2027-01-01T00:00:00Z")), (january.After!.Value, january.Before!.Value));
    }

    [Fact] public void A_named_day_is_a_window_only_when_it_stands_alone()
    {
        var now = At("2026-10-31T12:00:00Z");
        var day = TimePhrases.Parse("what did I save on Aug 8?", now, London)!;
        Assert.Equal((At("2026-08-07T23:00:00Z"), At("2026-08-08T23:00:00Z")), (day.After!.Value, day.Before!.Value));
        Assert.Equal("what did I save?", day.Query);
        var year = TimePhrases.Parse("the boiler service March 3rd, 2025", now, Utc)!;
        Assert.Equal((At("2025-03-03T00:00:00Z"), At("2025-03-04T00:00:00Z")), (year.After!.Value, year.Before!.Value));
        Assert.Equal("the boiler service", year.Query);
        Assert.NotNull(TimePhrases.Parse("call on Aug 8 New York time", now, Utc));
        var around = TimePhrases.Parse("photos around May 5th", now, Utc)!;
        Assert.Equal((At("2026-05-02T00:00:00Z"), At("2026-05-08T00:00:00Z")), (around.After!.Value, around.Before!.Value));
        // No window: part of a name, an as-of date, a range or an open end, two dates, a day that does not exist, none.
        foreach (var question in new[] { "dinner at the Aug 8 Cafe", "around May 5th Cafe", "where did alex work as of Aug 8",
            "bills from Aug 8", "since March 3 the meter", "notes Aug 8, Sep 9", "the Feb 30 invoice", "the heat pump quote",
            "who is invited to the party on 5 December?" })
            Assert.Null(TimePhrases.Parse(question, now, London));
        // A question that is only its phrase keeps its words.
        Assert.Equal("yesterday", TimePhrases.Parse("yesterday", now, Utc)!.Query);
    }
}

// The window on a database: off (the default) the question is asked as it stands; on, a phrase keeps only rows whose
// own time is inside it, and a window the caller gives wins over the phrase.
public sealed class TimePhraseDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [PostgresFact] public async Task A_time_phrase_narrows_recall_only_when_turned_on()
    {
        var db = RecallRows.Db(fixture.Source);
        var reader = await Passages.Harness(fixture.Source, "time-reader");
        var now = DateTimeOffset.Parse("2026-10-31T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var recent = await RecallRows.Add(db, fixture.Source, "Kestle invoice", "The Kestle invoice for the gutters.", updated: now.AddDays(-1));
        var older = await RecallRows.Add(db, fixture.Source, "Kestle invoice (spring)", "The Kestle invoice for the porch.", updated: now.AddDays(-60));
        async Task<Guid[]> Ask(RecallOptions o, RecallQuery q) => [.. (await new Recall(db, null, options: o).Search(reader, q, default)).Hits.Select(h => h.Id)];
        var on = RecallOptions.Default with { TimePhrases = true, Now = now, Zone = "Europe/London" };

        // Off: the phrase is a word of the question like any other, and both rows answer by their words.
        foreach (var question in new[] { "Kestle invoice", "Kestle invoice yesterday" })
        {
            var off = await Ask(RecallOptions.Default, new(question, 20));
            Assert.Contains(recent, off);
            Assert.Contains(older, off);
        }
        // On: the window, and the question without its phrase; the answer still says the question asked.
        var narrowed = await new Recall(db, null, options: on).Search(reader, new("Kestle invoice yesterday", 20), default);
        Assert.Equal("Kestle invoice yesterday", narrowed.Query);
        Assert.Contains(recent, narrowed.Hits.Select(h => h.Id));
        Assert.DoesNotContain(older, narrowed.Hits.Select(h => h.Id));
        var lastMonth = await Ask(on, new("Kestle invoice last month", 20));
        Assert.Contains(older, lastMonth);
        Assert.DoesNotContain(recent, lastMonth);
        // A window the caller gives wins: the phrase stays a word of the question.
        Assert.DoesNotContain(recent, await Ask(on, new("Kestle invoice yesterday", 20, After: now.AddDays(-90), Before: now.AddDays(-30))));
    }
}
