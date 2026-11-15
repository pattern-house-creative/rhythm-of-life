using System.Globalization;

namespace Knowledge.Tests;

// SB-P5-10 (WRT-12, TIM-08, D-043): golden date phrases. An explicit when is an ISO date or date and time, read on the
// install's clocks when it has no offset, refused when those clocks skip it or pass it twice; a day is due from its first
// moment there, across summer time and in a zone whose clocks skip or repeat midnight; leap days exist only in leap
// years. The words give one date or none, and an ambiguous one (two dates, a weekday without its date, a relative day, a
// date that reads two ways, a range, a weekday that disagrees with its date) is never guessed: the reading says
// why. Relative phrases are not read (a refusal of their own). In Europe/London, UTC and two synthetic zones, at fixed
// clocks. DEMO words only.
public sealed class DueDateTests
{
    static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    // Nine hours ahead of UTC all year.
    static readonly TimeZoneInfo Ahead = TimeZoneInfo.CreateCustomTimeZone("Test/Ahead", TimeSpan.FromHours(9), "Test/Ahead", "Test ahead");
    // Four hours behind UTC, an hour less in summer 2026; summer time starts at 00:00 on 6 September (the clocks skip
    // midnight to 01:00) and ends at 01:00 summer time on 1 November (back to 00:00, so midnight comes twice).
    static readonly TimeZoneInfo Midnight = TimeZoneInfo.CreateCustomTimeZone("Test/Midnight", TimeSpan.FromHours(-4), "Test/Midnight",
        "Test standard", "Test summer",
        [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 9, 6),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 11, 1))]);

    // Wednesday 4 November 2026, noon UTC: London is on GMT since 25 October.
    static readonly DateTimeOffset Now = At("2026-11-04T12:00:00Z");
    static DateTimeOffset At(string instant) => DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture);
    static string Iso(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    static string When(string when, TimeZoneInfo? zone = null) => Iso(DueDates.When(when, zone ?? London, Now));
    static string Refused(string when, TimeZoneInfo? zone = null, DateTimeOffset? now = null)
    {
        var refused = Assert.Throws<MemoryRefused>(() => DueDates.When(when, zone ?? London, now ?? Now));
        Assert.Equal("invalid", refused.Code);
        return refused.Message;
    }

    [Fact] public void A_date_is_due_from_its_first_moment_on_the_installs_clocks()
    {
        Assert.Equal("2026-12-01T00:00:00Z", When("2026-12-01"));
        Assert.Equal("2026-12-01T00:00:00Z", When("2026-12-01", Utc));
        // London's summer: midnight on 1 July is 23:00 UTC the day before.
        Assert.Equal("2026-06-30T23:00:00Z", When("2026-07-01"));
        Assert.Equal("2026-07-01T00:00:00Z", When("2026-07-01", Utc));
        // Nine hours ahead: the day starts at 15:00 UTC the day before.
        Assert.Equal("2026-11-30T15:00:00Z", When("2026-12-01", Ahead));
        // The DEMO reminder reads the same moment everywhere when it says its own zone.
        foreach (var zone in new[] { London, Utc, Ahead, Midnight })
        {
            Assert.Equal("2026-12-01T09:30:00Z", When("2026-12-01T09:30:00Z", zone));
            Assert.Equal("2026-12-01T07:30:00Z", When("2026-12-01T09:30:00+02:00", zone));
            Assert.Equal("2026-12-01T12:30:00Z", When("2026-12-01T09:30:00-03:00", zone));
            Assert.Equal("2026-12-01T07:00:00Z", When("2026-12-01T09:30+0230", zone));
        }
    }

    [Fact] public void A_time_of_day_without_a_zone_is_read_on_the_installs_clocks()
    {
        Assert.Equal("2026-12-01T09:30:00Z", When("2026-12-01T09:30"));
        Assert.Equal("2026-12-01T09:30:00Z", When("2026-12-01 09:30"));
        Assert.Equal("2026-07-01T08:30:00Z", When("2026-07-01T09:30:15.5"[..16]));
        Assert.Equal("2026-07-01T08:30:15Z", When("2026-07-01T09:30:15.5"));
        Assert.Equal("2026-12-01T00:30:00Z", When("2026-12-01T09:30", Ahead));
    }

    [Fact] public void Summer_time_changes_move_the_day_and_a_time_the_clocks_skip_or_repeat_is_refused()
    {
        // London's clocks go forward at 01:00 GMT on 29 March 2026 and back at 02:00 BST on 25 October 2026.
        Assert.Equal("2026-03-29T00:00:00Z", When("2026-03-29"));
        Assert.Equal("2026-03-29T23:00:00Z", When("2026-03-30"));
        Assert.Equal("2026-10-24T23:00:00Z", When("2026-10-25"));
        Assert.Equal("2026-10-26T00:00:00Z", When("2026-10-26"));
        Assert.Equal("2026-03-29T00:59:00Z", When("2026-03-29T00:59"));
        Assert.Equal("2026-03-29T01:00:00Z", When("2026-03-29T02:00"));
        Assert.Contains("does not happen in Europe/London (the clocks go forward then)", Refused("2026-03-29T01:30"));
        Assert.Contains("happens twice in Europe/London (the clocks go back then); say which with its offset (+01:00 or +00:00)", Refused("2026-10-25T01:30"));
        // Its offset says which.
        Assert.Equal("2026-10-25T00:30:00Z", When("2026-10-25T01:30+01:00"));
        Assert.Equal("2026-10-25T01:30:00Z", When("2026-10-25T01:30+00:00"));
    }

    [Fact] public void A_day_whose_midnight_is_skipped_or_repeated_starts_at_its_first_moment()
    {
        // 6 September: 00:00 to 00:59 never happen, so the day starts at 01:00 summer time (UTC-3).
        Assert.True(Midnight.IsInvalidTime(new DateTime(2026, 9, 6, 0, 30, 0)));
        Assert.Equal("2026-09-06T04:00:00Z", When("2026-09-06", Midnight));
        Assert.Equal("2026-09-06T04:00:00Z", Iso(DueDates.StartOf(new DateOnly(2026, 9, 6), Midnight)));
        Assert.Equal("2026-09-05T04:00:00Z", When("2026-09-05", Midnight));
        Assert.Contains("does not happen in Test/Midnight", Refused("2026-09-06T00:30", Midnight));
        // 1 November: 00:00 to 00:59 happen twice; the day starts at the first, still summer time.
        Assert.True(Midnight.IsAmbiguousTime(new DateTime(2026, 11, 1, 0, 30, 0)));
        Assert.Equal("2026-11-01T03:00:00Z", When("2026-11-01", Midnight));
        Assert.Equal("2026-11-02T04:00:00Z", When("2026-11-02", Midnight));
        Assert.Contains("happens twice in Test/Midnight", Refused("2026-11-01T00:30", Midnight));
    }

    [Fact] public void Leap_days_exist_only_in_leap_years()
    {
        Assert.Equal("2028-02-29T00:00:00Z", When("2028-02-29"));
        Assert.Contains("there is no such day as 2027-02-29", Refused("2027-02-29"));
        Assert.Contains("there is no such day as 2026-04-31", Refused("2026-04-31"));
        Assert.Contains("there is no such day as 2026-13-01", Refused("2026-13-01"));
        Assert.Equal(new DateOnly(2028, 2, 29), DueDates.FromText("DEMO: renew the Pellow allotment lease on 29 February 2028.", London, Now).Day);
        Assert.Equal(DueDates.NoSuchDay, DueDates.FromText("DEMO: renew the Pellow allotment lease on 2027-02-29.", London, Now).Reason);
        // No year means this year, and 2026 has no 29 February.
        Assert.Equal(DueDates.NoSuchDay, DueDates.FromText("DEMO: Pellow leap day party on 29 February.", London, Now).Reason);
        Assert.Equal(DueDates.NoSuchDay, DueDates.FromText("DEMO: the Pellow fete is on 31 April 2027.", London, Now).Reason);
    }

    [Fact] public void An_explicit_when_takes_an_iso_date_up_to_five_years_ahead_and_nothing_else()
    {
        // A date that was due already is taken (a retroactive deadline, as upstream); five years ahead and no further.
        Assert.Equal("2026-01-01T00:00:00Z", When("2026-01-01"));
        Assert.Equal("2031-11-04T00:00:00Z", When("2031-11-04"));
        Assert.Contains("at most 5 years ahead", Refused("2031-11-05"));
        Assert.Contains("until is at most 5 years ahead", Assert.Throws<MemoryRefused>(() => DueDates.When("2040-01-01", London, Now, "until")).Message);
        foreach (var wrong in new[] { "", "  ", "tomorrow", "next Friday", "1 December 2026", "12/01/2026", "2026-12-1", "2026-12-01T25:00",
                     "2026-12-01T09:60", "2026-12-01Z", "2026-12-01T09:30+15:00", "20261201" })
            Assert.Contains("when is a date (2026-12-01) or a date and time (2026-12-01T09:30), read on the install's clocks (Europe/London)",
                Refused(wrong));
    }

    // The words: one date, in a token of its own, read in the zone at the clock.
    static DueDates.Reading Read(string text, TimeZoneInfo? zone = null, DateTimeOffset? now = null) =>
        DueDates.FromText(text, zone ?? London, now ?? Now);

    [Theory]
    [InlineData("DEMO: renew the Pellow allotment lease by 2026-12-01.", "2026-12-01")]
    [InlineData("DEMO: pay the Pellow parish 1 December 2026", "2026-12-01")]
    [InlineData("DEMO: pay the Pellow parish on the 1st of December, 2026!", "2026-12-01")]
    [InlineData("DEMO: Pellow choir concert Dec 1, 2026 at the hall", "2026-12-01")]
    [InlineData("DEMO: Pellow choir concert December 1st", "2026-12-01")]
    [InlineData("DEMO: Pellow choir concert dec. 1", "2026-12-01")]
    [InlineData("DEMO: Pellow choir concert (2026-12-01)", "2026-12-01")]
    [InlineData("DEMO: the Pellow bins go out 30/11/2026", "2026-11-30")]
    [InlineData("DEMO: the Pellow bins go out 11/30/2026", "2026-11-30")]
    [InlineData("DEMO: the Pellow carols are 12/12/2026", "2026-12-12")]
    [InlineData("DEMO: the Pellow fete, 2026-12-05 (5 December 2026), on the green", "2026-12-05")]
    [InlineData("DEMO: Pellow quiz Tuesday 1 December 2026", "2026-12-01")]
    [InlineData("DEMO: Pellow quiz Tue, Dec 1", "2026-12-01")]
    [InlineData("DEMO: Pellow quiz 2026-12-01 (Tuesday)", "2026-12-01")]
    [InlineData("DEMO: post the Pellow letter 2026-11-04", "2026-11-04")]
    [InlineData("DEMO: Alex may 2 times a week walk the Pellow canal; renewal May 20", "2027-05-20")]
    [InlineData("DEMO: Pellow party on 1 December, with 30 guests", "2026-12-01")]
    // A time of day after the date is no day and no year (the time itself is not read).
    [InlineData("DEMO: Pellow dentist 3 December 10:30", "2026-12-03")]
    [InlineData("DEMO: Pellow dentist 3 December 10 am", "2026-12-03")]
    [InlineData("DEMO: Pellow dentist the 1st of December 3pm", "2026-12-01")]
    [InlineData("DEMO: Pellow dentist Dec 1 9.30", "2026-12-01")]
    // A month name that is also a word is read written as a month, or with an ordinal day or a year.
    [InlineData("DEMO: the Pellow fete is March 20", "2027-03-20")]
    [InlineData("DEMO: the Pellow fete is march 20th", "2027-03-20")]
    [InlineData("DEMO: the Pellow fete is march 20, 2027", "2027-03-20")]
    [InlineData("DEMO: the Pellow fete is on the 20th of march", "2027-03-20")]
    [InlineData("DEMO: the Pellow fete is MAR 20", "2027-03-20")]
    public void One_date_in_the_words_is_taken(string words, string day)
    {
        var now = day is "2027-05-20" or "2027-03-20" ? At("2027-01-10T12:00:00Z") : Now;
        var read = Read(words, now: now);
        Assert.Equal((DateOnly.Parse(day, CultureInfo.InvariantCulture), (string?)null, (string?)null), (read.Day!.Value, read.Reason, read.Why));
        Assert.Equal(DueDates.StartOf(read.Day.Value, London), read.At);
    }

    [Fact] public void A_date_inside_another_token_is_no_date_and_words_without_one_say_nothing()
    {
        foreach (var words in new[] { "DEMO: see reports/2026-12-01/summary.pdf", "DEMO: build_id=2026-12-01-04 passed", "DEMO: Pellow app v3.4.0 shipped",
                     "DEMO: the Pellow bakery sells rye", "DEMO: Pellow ticket #2026-12", "DEMO: room 12/12 is free", "DEMO: Alex may 5 times say no" })
            Assert.Equal(DueDates.Reading.None, Read(words));
    }

    // may, march and mar are words as well as months: written in lower case they are a month only with an ordinal day or a
    // year ("march 20th", "20 may 2027"), so a march of 20 miles is no 20 March (before run 18's fix review it was).
    [Fact] public void A_month_name_that_is_also_a_word_is_no_month_without_an_ordinal_or_a_year()
    {
        var february = At("2027-02-01T12:00:00Z");
        foreach (var words in new[] { "DEMO: Sam will march 20 miles for the Pellow fete", "DEMO: the 20 may come to the Pellow fete",
                     "DEMO: scratches mar 3 Pellow doors", "DEMO: Alex may 2 times a week walk the Pellow canal" })
            Assert.Equal(DueDates.Reading.None, Read(words, now: february));
        Assert.Equal(new DateOnly(2027, 3, 20), Read("DEMO: Sam will March 20 miles for the Pellow fete", now: february).Day);
        Assert.Equal(new DateOnly(2027, 5, 2), Read("DEMO: the Pellow fete is may 2nd", now: february).Day);
        Assert.Equal(new DateOnly(2027, 5, 20), Read("DEMO: the Pellow fete is 20 may 2027", now: february).Day);
    }

    [Theory]
    [InlineData("DEMO: book the Pellow hall for 2026-12-05 and pay by 2026-12-01", "two_dates", "it names more than one day (2026-12-01, 2026-12-05)")]
    [InlineData("DEMO: moved the Pellow fete from 1 December 2026 to 2026-12-08", "two_dates", "(2026-12-01, 2026-12-08)")]
    [InlineData("DEMO: bring the Pellow ladder back on Friday", "weekday", "it names a weekday without its date (Friday)")]
    [InlineData("DEMO: next Tuesday the Pellow choir meets", "weekday", "(Tuesday)")]
    [InlineData("DEMO: on Friday Sam said the Pellow fete is 2026-12-05", "weekday", "(Friday) as well as 2026-12-05")]
    [InlineData("DEMO: Pellow bins every Mondays", "weekday", "(Mondays)")]
    [InlineData("DEMO: call the Pellow vet tomorrow", "relative", "it names a day relative to when it was said (tomorrow)")]
    [InlineData("DEMO: fix the Pellow gate next week", "relative", "(next week)")]
    [InlineData("DEMO: return the Pellow books in 3 days", "relative", "(in 3 days)")]
    [InlineData("DEMO: Pellow report by end of the month", "relative", "(end of the month)")]
    [InlineData("DEMO: today Sam said the Pellow fete is 2026-12-05", "relative", "(today) as well as 2026-12-05")]
    [InlineData("DEMO: Pellow rota 4/11/2026", "two_ways", "4/11/2026 reads as 4 November 2026 or 11 April 2026")]
    [InlineData("DEMO: Pellow rota 30/11/26", "two_ways", "30/11/26 has a two-digit year")]
    // A day number on each side of a month name, or a number after a date with no year: which day, or which year, is
    // not guessed (the month-first reading took "Dec 10" here before run 18's fix review).
    [InlineData("DEMO: Pellow party 5 Dec 10 guests", "two_ways", "5 Dec 10 has a number on each side of its month")]
    [InlineData("DEMO: pay the Pellow parish 3 December 15 pounds", "two_ways", "(3 December 15 has")]
    [InlineData("DEMO: Pellow dinner 6 December 8 people", "two_ways", "(6 December 8 has")]
    [InlineData("DEMO: order 4 Dec 12 Pellow boxes", "two_ways", "(4 Dec 12 has")]
    [InlineData("DEMO: the Pellow lease ends 1 Dec 27", "two_ways", "(1 Dec 27 has")]
    [InlineData("DEMO: the Pellow lease ends the 1st of Dec 27", "two_ways", "(1st of Dec 27 has")]
    [InlineData("DEMO: renew the Pellow lease on 1 December '27", "two_ways", "1 December '27 has a two-digit year")]
    [InlineData("DEMO: Pellow party Dec 1 10 guests", "two_ways", "Dec 1 is followed by 10, which may be a two-digit year")]
    [InlineData("DEMO: Pellow quiz Friday 1 December 2026", "weekday_mismatch", "1 December 2026 is a Tuesday, not a Friday")]
    [InlineData("DEMO: Pellow quiz 2026-12-01 (Sat)", "weekday_mismatch", "is a Tuesday, not a Saturday")]
    [InlineData("DEMO: Pellow festival Dec 1-3", "range", "it names a range of days (Dec 1-3)")]
    [InlineData("DEMO: Pellow festival 1-3 December 2026", "range", "it names a range of days (1-3 December 2026)")]
    // Days joined by a word are a range or a list too, never the one day the date names.
    [InlineData("DEMO: Alex is away at the Pellow camp from 16 to 18 November 2026", "range", "(16 to 18 November 2026)")]
    [InlineData("DEMO: the Pellow hall is booked Dec 1 to 3", "range", "(Dec 1 to 3)")]
    [InlineData("DEMO: Pellow choir rehearsals 1 through 3 December", "range", "(1 through 3 December)")]
    [InlineData("DEMO: the Pellow hall is booked Dec 1 to 3pm", "range", "(Dec 1 to 3)")]
    [InlineData("DEMO: Pellow first-aid training on 23 and 24 November", "two_dates", "it names more than one day (23 and 24 November)")]
    [InlineData("DEMO: Pellow trial imports on 3 and 10 November", "two_dates", "(3 and 10 November)")]
    [InlineData("DEMO: the Pellow fete is either the 1st or 2nd of December", "two_dates", "(1st or 2nd of December)")]
    [InlineData("DEMO: the Pellow fete is between 3 and 5 December 2026", "two_dates", "(3 and 5 December 2026)")]
    [InlineData("DEMO: the Pellow bins go out 3, 4 or 5 December", "two_dates", "(4 or 5 December)")]
    [InlineData("DEMO: Pellow choir concert Dec 1 & 8", "two_dates", "(Dec 1 & 8)")]
    [InlineData("DEMO: paid the Pellow parish on 2026-10-01", "past", "the day it names (2026-10-01) has passed")]
    [InlineData("DEMO: Pellow harvest supper 1 October", "past", "it names no year, and that day this year (2026-10-01) has passed")]
    [InlineData("DEMO: Pellow time capsule opens 2032-01-01", "too_far", "the day it names (2032-01-01) is more than 5 years ahead")]
    [InlineData("DEMO: Pellow village launch in December 2026", "no_day", "it names a month without a day (December 2026)")]
    public void An_ambiguous_date_is_never_guessed_and_the_reading_says_why(string words, string reason, string why)
    {
        var read = Read(words);
        Assert.Equal((reason, (DateOnly?)null, (DateTimeOffset?)null), (read.Reason, read.Day, read.At));
        Assert.Contains(why, read.Why);
        // An answer that may not repeat the words (a held memory's) says why without them.
        var unquoted = DueDates.FromText(words, London, Now, quote: false);
        Assert.Equal(reason, unquoted.Reason);
        Assert.DoesNotContain("Pellow", unquoted.Why);
        Assert.DoesNotContain("(", unquoted.Why);
    }

    // The parity corpus's two trips name ranges in words: neither is given a due date (they were read as their last day).
    [Fact] public void The_corpus_trips_that_name_a_range_in_words_take_no_date()
    {
        var corpus = Parity.ParityCorpus.Load();
        foreach (var id in new[] { "mem-family-leeds", "mem-sam-plants" })
        {
            var read = Read(corpus.Memories.Single(m => m.Id == id).Text, now: At("2026-10-30T12:00:00Z"));
            Assert.Equal((DueDates.Range, (DateOnly?)null), (read.Reason, read.Day));
        }
    }

    // A remember reads the words of every memory without a when, up to 128 KiB of them: the reading takes time in line
    // with their length (before run 18's review it grew with its square: 13 s for 128 KiB of "1 Dec"). "1 Dec 1 Dec" has a
    // number on each side of a month, so it reads two ways since the fix review; "1 Dec; 1 Dec" names one day.
    [Theory]
    [InlineData("1 Dec ", DueDates.TwoWays)]
    [InlineData("1 Dec; ", null)]
    [InlineData("Tue, 1 Dec ", null)]
    [InlineData("2026-12-01 ", null)]
    [InlineData("1 Dec; 2 Dec; ", DueDates.TwoDates)]
    [InlineData("3 and 4 Dec; ", DueDates.TwoDates)]
    [InlineData("Friday ", DueDates.Weekday)]
    public void Reading_the_largest_memory_takes_time_in_line_with_its_length(string unit, string? reason)
    {
        var words = string.Concat(Enumerable.Repeat(unit, 131072 / unit.Length));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var read = Read(words);
        clock.Stop();
        Assert.True(clock.ElapsedMilliseconds < 2000, $"{unit.Trim()}: {clock.ElapsedMilliseconds} ms for {words.Length} characters");
        Assert.Equal(reason, read.Reason);
        if (reason is null) Assert.Equal(new DateOnly(2026, 12, 1), read.Day);
    }

    [Fact] public void The_words_are_read_in_the_installs_zone_and_on_its_day()
    {
        // 23:30 UTC on 1 July is 00:30 on 2 July in London: there, 1 July has passed; in UTC it is today.
        var late = At("2026-07-01T23:30:00Z");
        Assert.Equal(DueDates.Past, Read("DEMO: Pellow fete 2026-07-01", London, late).Reason);
        var utc = Read("DEMO: Pellow fete 2026-07-01", Utc, late);
        Assert.Equal((new DateOnly(2026, 7, 1), At("2026-07-01T00:00:00Z")), (utc.Day!.Value, utc.At!.Value));
        // A day after London's clocks went forward starts an hour earlier in UTC.
        Assert.Equal(At("2026-03-29T23:00:00Z"), Read("DEMO: Pellow fete 2026-03-30", London, At("2026-03-01T12:00:00Z")).At);
        Assert.Equal(At("2026-03-30T00:00:00Z"), Read("DEMO: Pellow fete 2026-03-30", Utc, At("2026-03-01T12:00:00Z")).At);
        Assert.Equal(At("2026-09-06T04:00:00Z"), Read("DEMO: Pellow fete 6 September 2026", Midnight, At("2026-08-01T12:00:00Z")).At);
        // A year-less day is this year in the zone: on 31 December in UTC, 1 January's year is 2027 nine hours ahead.
        var eve = At("2026-12-31T20:00:00Z");
        Assert.Equal(DueDates.Past, Read("DEMO: Pellow party 1 January", Utc, eve).Reason);
        Assert.Equal(new DateOnly(2027, 1, 1), Read("DEMO: Pellow party 1 January", Ahead, eve).Day);
        Assert.Equal(new DateOnly(2027, 1, 1), DueDates.Today(Ahead, eve));
    }
}
