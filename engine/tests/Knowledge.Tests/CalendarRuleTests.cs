using System.Globalization;
using Knowledge.Adapters.Calendar;

namespace Knowledge.Tests;

// The calendar adapter's recurrence rules (SB-P7-05), against the examples RFC 5545 section 3.8.5.3 publishes. Times are
// wall-clock readings: a rule runs on the event's own clock, and the zone is read after it (CalendarAdapterTests).
public sealed class CalendarRuleTests
{
    static DateTime At(string text) => DateTime.ParseExact(text, text.Length == 8 ? "yyyyMMdd" : "yyyyMMdd'T'HHmm", CultureInfo.InvariantCulture);
    static string[] Walls(string start, string rule, int take = 40, string? until = null, string? reach = null, string horizon = "20300101")
    {
        var parsed = IcsRule.Parse(rule);
        Assert.NotNull(parsed);
        return parsed!.Walls(At(start), until is null ? null : At(until), reach is null ? DateTime.MinValue : At(reach), At(horizon), new IcsBudget(1_000_000))
            .Take(take).Select(w => w.ToString("yyyyMMdd'T'HHmm", CultureInfo.InvariantCulture)).ToArray();
    }
    static string[] Days(params string[] days) => [.. days.Select(d => d + "T0900")];

    [Fact] public void Daily_with_a_count_and_an_interval()
    {
        Assert.Equal(Days("19970902", "19970903", "19970904"), Walls("19970902T0900", "FREQ=DAILY;COUNT=3"));
        Assert.Equal(Days("19970902", "19970912", "19970922", "19971002", "19971012"), Walls("19970902T0900", "FREQ=DAILY;INTERVAL=10;COUNT=5"));
    }

    [Fact] public void Weekly_keeps_its_week_start()
    {
        Assert.Equal(Days("19970902", "19970904", "19970916", "19970918", "19970930", "19971002", "19971014", "19971016"),
            Walls("19970902T0900", "FREQ=WEEKLY;INTERVAL=2;WKST=SU;BYDAY=TU,TH;COUNT=8"));
        // The RFC's example of WKST changing which dates a rule gives.
        Assert.Equal(Days("19970805", "19970810", "19970819", "19970824"), Walls("19970805T0900", "FREQ=WEEKLY;INTERVAL=2;COUNT=4;BYDAY=TU,SU;WKST=MO"));
        Assert.Equal(Days("19970805", "19970817", "19970819", "19970831"), Walls("19970805T0900", "FREQ=WEEKLY;INTERVAL=2;COUNT=4;BYDAY=TU,SU;WKST=SU"));
    }

    [Fact] public void Monthly_by_the_nth_weekday_and_from_the_months_end()
    {
        Assert.Equal(Days("19970905", "19971003", "19971107", "19971205", "19980102", "19980206", "19980306", "19980403", "19980501", "19980605"),
            Walls("19970905T0900", "FREQ=MONTHLY;COUNT=10;BYDAY=1FR"));
        Assert.Equal(Days("19970922", "19971020", "19971117", "19971222", "19980119", "19980216"), Walls("19970922T0900", "FREQ=MONTHLY;COUNT=6;BYDAY=-2MO"));
        Assert.Equal(Days("19970928", "19971029", "19971128", "19971229", "19980129", "19980226"), Walls("19970928T0900", "FREQ=MONTHLY;BYMONTHDAY=-3;COUNT=6"));
    }

    [Fact] public void A_month_without_that_day_is_skipped_never_moved()
    {
        Assert.Equal(Days("20070115", "20070130", "20070215", "20070315", "20070330"), Walls("20070115T0900", "FREQ=MONTHLY;BYMONTHDAY=15,30;COUNT=5"));
        Assert.Equal(Days("20260131", "20260331", "20260531"), Walls("20260131T0900", "FREQ=MONTHLY;COUNT=3"));
    }

    [Fact] public void A_set_position_picks_within_each_month()
    {
        Assert.Equal(Days("19970904", "19971007", "19971106"), Walls("19970904T0900", "FREQ=MONTHLY;COUNT=3;BYDAY=TU,WE,TH;BYSETPOS=3"));
        Assert.Equal(Days("19970929", "19971030", "19971127", "19971230", "19980129", "19980226", "19980330"),
            Walls("19970929T0900", "FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-2;COUNT=7"));
    }

    [Fact] public void Yearly_by_month_week_and_day_of_the_year()
    {
        Assert.Equal(Days("19970610", "19970710", "19980610", "19980710"), Walls("19970610T0900", "FREQ=YEARLY;COUNT=4;BYMONTH=6,7"));
        Assert.Equal(Days("19970101", "19970410", "19970719", "20000101", "20000409", "20000718", "20030101", "20030410", "20030719", "20060101"),
            Walls("19970101T0900", "FREQ=YEARLY;INTERVAL=3;COUNT=10;BYYEARDAY=1,100,200"));
        Assert.Equal(Days("19970519", "19980518", "19990517"), Walls("19970519T0900", "FREQ=YEARLY;BYDAY=20MO;COUNT=3"));
        Assert.Equal(Days("19970512", "19980511", "19990517"), Walls("19970512T0900", "FREQ=YEARLY;BYWEEKNO=20;BYDAY=MO;COUNT=3"));
        Assert.Equal(Days("19970313", "19970320", "19970327", "19980305", "19980312", "19980319"), Walls("19970313T0900", "FREQ=YEARLY;BYMONTH=3;BYDAY=TH;COUNT=6"));
    }

    [Fact] public void The_first_of_a_series_is_its_start_even_where_the_rule_would_not_give_it()
    {
        // The RFC's Friday the 13th, without its EXDATE: the start counts as the first, and a count includes it.
        Assert.Equal(Days("19970902", "19980213", "19980313", "19981113"), Walls("19970902T0900", "FREQ=MONTHLY;BYDAY=FR;BYMONTHDAY=13;COUNT=4"));
    }

    [Fact] public void Hours_and_minutes_within_a_day_and_rules_that_step_in_them()
    {
        Assert.Equal(["19970902T0900", "19970902T0920", "19970902T0940", "19970902T1000", "19970902T1020", "19970902T1040", "19970903T0900"],
            Walls("19970902T0900", "FREQ=DAILY;BYHOUR=9,10;BYMINUTE=0,20,40;COUNT=7"));
        Assert.Equal(["19970902T0900", "19970902T0915", "19970902T0930", "19970902T0945", "19970902T1000", "19970902T1015"], Walls("19970902T0900", "FREQ=MINUTELY;INTERVAL=15;COUNT=6"));
        Assert.Equal(["19970902T0900", "19970902T1200"], Walls("19970902T0900", "FREQ=HOURLY;INTERVAL=3", until: "19970902T1300"));
    }

    [Fact] public void An_end_date_is_inclusive_and_a_rule_that_never_matches_ends_at_the_horizon()
    {
        Assert.Equal(Days("20261101", "20261102", "20261103"), Walls("20261101T0900", "FREQ=DAILY", until: "20261103T0900"));
        Assert.Equal(Days("20260130"), Walls("20260130T0900", "FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=30"));
    }

    [Fact] public void A_series_with_no_count_starts_near_where_it_is_wanted_and_stays_on_its_interval()
    {
        var budget = new IcsBudget(1000);
        var daily = IcsRule.Parse("FREQ=DAILY;INTERVAL=3")!.Walls(At("19000101T0900"), null, At("20260101"), At("20260110"), budget).ToArray();
        Assert.All(daily.Where(w => w > At("19000101T0900")), w => Assert.Equal(0, (w - At("19000101T0900")).Days % 3));
        Assert.Contains(daily, w => w >= At("20260101"));
        Assert.True(budget.Left > 500, "it does not walk from 1900");
        // A count has to be walked from the start, within the budget, and a budget used up says so.
        var counted = IcsRule.Parse("FREQ=MINUTELY;COUNT=100000")!;
        var small = new IcsBudget(100);
        Assert.InRange(counted.Walls(At("20260101T0900"), null, At("20260101"), At("20300101"), small).Count(), 1, 100);
        Assert.True(small.Exhausted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("FREQ=SOMETIMES")]
    [InlineData("INTERVAL=2")]
    [InlineData("FREQ=DAILY;INTERVAL=0")]
    [InlineData("FREQ=DAILY;INTERVAL=many")]
    [InlineData("FREQ=DAILY;COUNT=0")]
    [InlineData("FREQ=WEEKLY;BYDAY=NOTADAY")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=0")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=32")]
    [InlineData("FREQ=YEARLY;BYMONTH=13")]
    [InlineData("FREQ=SECONDLY")]
    // The smallest int, which has no size of its own, and one past what a numbered weekday can be: refused, never thrown.
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=-2147483648")]
    [InlineData("FREQ=YEARLY;BYYEARDAY=-2147483648")]
    [InlineData("FREQ=YEARLY;BYWEEKNO=-2147483648")]
    [InlineData("FREQ=MONTHLY;BYDAY=MO;BYSETPOS=-2147483648")]
    [InlineData("FREQ=MONTHLY;BYDAY=-2147483648MO")]
    [InlineData("FREQ=MONTHLY;BYDAY=-54MO")]
    [InlineData("FREQ=MONTHLY;BYDAY=54MO")]
    [InlineData("FREQ=DAILY;BYHOUR=0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23;BYMINUTE=0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20;BYSECOND=0,1,2,3,4,5,6,7,8,9")]
    public void A_rule_it_cannot_read_is_no_rule(string rule) => Assert.Null(IcsRule.Parse(rule));

    [Fact] public void Parts_it_does_not_know_are_left_out_as_the_format_allows()
    {
        Assert.Equal(Days("20261101", "20261102"), Walls("20261101T0900", "FREQ=DAILY;COUNT=2;X-OTHER=1"));
    }
}
