using System.Globalization;
using System.Text.RegularExpressions;

namespace Knowledge;

// Due dates (SB-P5-10; migration 061; D-043): the moment a memory should come back to the person, from an explicit when
// (an ISO 8601 date or date and time) or from the one date its words name, read in the install's time zone. Adapted from
// Second Brain's time anchors (MIT; src/when/input.ts, heuristic.ts and timezone.ts at 228cea49): a date with no zone
// means that day, or that time of day, on the install's clocks; an offset given is honoured as given. Restated for .NET;
// no code is copied.
//
// A day is due from its first moment in the zone: midnight, or where the clocks skip midnight (a zone whose summer time
// starts at 00:00) the first moment that day has, and where midnight comes twice its first. A time of day that the clocks
// skip, or pass twice, is refused rather than moved: the caller gives an offset to say which.
//
// The words are read for one date only and never guessed (stricter than upstream, which takes the soonest of several):
// an ISO date (2026-12-01), a day and a month name with or without a year (1 December 2026, Dec 1, the 1st of December),
// or a numeric date with a four-digit year that reads one way only (30/11/2026; 4/11/2026 reads as two days and is
// refused). A weekday standing for a day (Friday), a relative day (tomorrow, next week), two different days (also days
// joined by and, or or a comma: "23 and 24 November"), a range (a dash, to, through or until: "from 16 to 18 November"), a
// date that reads two ways (a day number on each side of its month: "5 Dec 10 guests"; a number after a date with no
// year, which may be a two-digit year: "1 December '27", "Dec 1 10 guests"), a weekday that disagrees with its date, a
// day that does not exist, a month with no day, a day that has passed and one more than five years ahead give no date,
// and the answer says why. A time of day after a date ("3 December 10:30", "Dec 1 3pm") is neither its day nor its year
// (and is not read). may, march and mar are words too: in lower case they are a month only with an ordinal day or a year
// ("march 20th"), so "Sam will march 20 miles" names no date. A date inside another token (a path, an id, a version) is
// no date at all, as upstream's whitespace guard. Only English month and weekday names are read.
public static partial class DueDates
{
    // What kind of moment a due date is (upstream's when_kind): a deadline, something happening then, a plain reminder.
    public const string Due = "due", Event = "event", Wake = "wake";
    public static readonly IReadOnlyList<string> Kinds = [Due, Event, Wake];
    // Where it came from: the caller gave it, or the engine read it from the memory's words.
    public const string Explicit = "explicit", Text = "text";
    // resolve_due's actions (upstream's resolve actions of those names).
    public const string Snooze = "snooze", ClearDate = "clear_date";
    public static readonly IReadOnlyList<string> Actions = [Snooze, ClearDate];
    // The due list's windows (upstream's due feed): its moment has come, within 48 hours, or later.
    public const string Overdue = "overdue", Upcoming = "upcoming", Later = "later";
    public static readonly IReadOnlyList<string> Windows = [Overdue, Upcoming, Later];
    public const int YearsAhead = 5;
    // How far beside a date the words are read for a weekday or the other end of a range or list.
    const int Near = 40;

    // Why the words gave no date.
    public const string TwoDates = "two_dates", Weekday = "weekday", Relative = "relative", TwoWays = "two_ways", Range = "range",
        Mismatch = "weekday_mismatch", Past = "past", TooFar = "too_far", NoSuchDay = "no_such_day", NoDay = "no_day";

    // What the words gave: the one day they name and its first moment in the zone; or, when they name a date but none is
    // taken, why (Reason, a code; Why, in words); or nothing at all when they name no date. Days: every day they name.
    public sealed record Reading(DateOnly? Day, DateTimeOffset? At, string? Reason, string? Why, IReadOnlyList<DateOnly> Days)
    {
        public static readonly Reading None = new(null, null, null, null, []);
    }

    // The zone's calendar day at a moment.
    public static DateOnly Today(TimeZoneInfo zone, DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

    // A day's first moment in the zone: its midnight; where the clocks skip midnight, the first minute that day has; where
    // midnight comes twice, the first.
    public static DateTimeOffset StartOf(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        for (var minutes = 0; zone.IsInvalidTime(local) && minutes < 24 * 60; minutes++) local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    // How a moment reads on the zone's clocks: "2026-12-01 09:30".
    public static string Local(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    // An explicit when, or a snooze's until: an ISO 8601 date, or a date and time with or without seconds (a space or T
    // between), with Z or an offset or with none (then read on the zone's clocks). At most five years after now; any
    // moment before is taken (a date that was due already), and a snooze asks for one to come itself. Refused (invalid),
    // saying what it takes, otherwise.
    public static DateTimeOffset When(string? when, TimeZoneInfo zone, DateTimeOffset now, string argument = "when")
    {
        var text = (when ?? "").Trim();
        var format = $"{argument} is a date (2026-12-01) or a date and time (2026-12-01T09:30), read on the install's clocks ({zone.Id}), "
            + "or with Z or an offset such as +01:00";
        if (IsoWhen().Match(text) is not { Success: true } m) throw new MemoryRefused("invalid", format);
        int Part(string name) => m.Groups[name].Success ? int.Parse(m.Groups[name].Value, CultureInfo.InvariantCulture) : 0;
        var (year, month, day) = (Part("y"), Part("m"), Part("d"));
        if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
            throw new MemoryRefused("invalid", $"{argument}: there is no such day as {text[..10]}");
        DateTimeOffset at;
        if (!m.Groups["h"].Success) at = StartOf(new DateOnly(year, month, day), zone);
        else
        {
            var (hour, minute, second) = (Part("h"), Part("min"), Part("s"));
            if (hour > 23 || minute > 59 || second > 59) throw new MemoryRefused("invalid", format);
            var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
            if (m.Groups["z"].Success)
            {
                var z = m.Groups["z"].Value;
                TimeSpan offset = TimeSpan.Zero;
                if (z is not ("Z" or "z"))
                {
                    var digits = z[1..].Replace(":", "");
                    var (oh, om) = (int.Parse(digits[..2], CultureInfo.InvariantCulture), int.Parse(digits[2..], CultureInfo.InvariantCulture));
                    if (oh > 14 || om > 59 || oh == 14 && om > 0) throw new MemoryRefused("invalid", format);
                    offset = new TimeSpan(oh, om, 0) * (z[0] == '-' ? -1 : 1);
                }
                at = new DateTimeOffset(local, offset);
            }
            else if (zone.IsInvalidTime(local))
                throw new MemoryRefused("invalid", $"{argument}: {local:yyyy-MM-dd HH:mm} does not happen in {zone.Id} (the clocks go forward then); "
                    + "give another time, or the moment with an offset");
            else if (zone.IsAmbiguousTime(local))
                throw new MemoryRefused("invalid", $"{argument}: {local:yyyy-MM-dd HH:mm} happens twice in {zone.Id} (the clocks go back then); "
                    + $"say which with its offset ({string.Join(" or ", zone.GetAmbiguousTimeOffsets(local).OrderDescending().Select(Offset))})");
            else at = new DateTimeOffset(local, zone.GetUtcOffset(local));
        }
        if (at > now.AddYears(YearsAhead)) throw new MemoryRefused("invalid", $"{argument} is at most {YearsAhead} years ahead");
        return at.ToUniversalTime();
    }

    static string Offset(TimeSpan o) => (o < TimeSpan.Zero ? "-" : "+") + o.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    // The one date a memory's words name, read in the zone at now; Quote: whether the answer may repeat the words it read
    // (never for a memory held for review, whose words wait for a person, D-160).
    public static Reading FromText(string? text, TimeZoneInfo zone, DateTimeOffset now, bool quote = true)
    {
        var words = text ?? "";
        var today = Today(zone, now);
        var mentions = new List<Mention>();
        // The characters a mention has taken, one flag each: a check costs its own match's length, so the words are read
        // in time that grows with their length, never its square (a memory may hold 128 KiB of them).
        var taken = new bool[words.Length + 1];
        bool Free(int start, int end)
        {
            for (var i = start; i < end; i++) if (taken[i]) return false;
            return true;
        }
        void Take(int start, int end) => Array.Fill(taken, true, start, end - start);
        // The words just before or after a match, at most Near characters of them, for the patterns anchored at the match
        // (a weekday or the other end of a range beside it). Nothing before the window is read, and a match that starts at
        // the window's start inside a word is none: the window cut that word.
        Match Before(Regex pattern, int index)
        {
            var from = Math.Max(0, index - Near);
            var found = pattern.Match(words, from, index - from);
            return found.Success && found.Index == from && from > 0 && char.IsLetterOrDigit(words[from - 1]) ? Match.Empty : found;
        }
        Match After(Regex pattern, int index) => pattern.Match(words, index, Math.Min(Near, words.Length - index));
        // A date match: its day, unless that day does not exist; and the weekday written beside it, which must agree and
        // is then no weekday of its own.
        void Dated(Match m, DateOnly? day, bool yearless)
        {
            Take(m.Index, m.Index + m.Length);
            if (day is not { } d) { mentions.Add(new(NoSuchDay, null, m.Value, null, yearless)); return; }
            string? beside = null;
            if (Before(WeekdayBefore(), m.Index) is { Success: true } b)
            {
                beside = b.Groups["wd"].Value;
                Take(b.Index, m.Index);
            }
            else if (After(WeekdayAfter(), m.Index + m.Length) is { Success: true } a)
            {
                beside = a.Groups["wd"].Value;
                Take(m.Index + m.Length, m.Index + m.Length + a.Length);
            }
            mentions.Add(beside is not null && WeekdayOf(beside) != d.DayOfWeek
                ? new(Mismatch, d, m.Value, $"{Named(d)} is a {d.DayOfWeek}, not a {WeekdayOf(beside)}", yearless)
                : new("date", d, m.Value, null, yearless));
        }

        // Numeric dates first, then a day and month name either way round; each in a token of its own.
        foreach (Match m in IsoDay().Matches(words))
            if (Alone(words, m.Index, m.Length) && Free(m.Index, m.Index + m.Length))
                Dated(m, Day(Num(m, "y"), Num(m, "m"), Num(m, "d")), false);
        foreach (Match m in SlashDay().Matches(words))
        {
            if (!Alone(words, m.Index, m.Length) || !Free(m.Index, m.Index + m.Length)) continue;
            var (a, b, y) = (Num(m, "a"), Num(m, "b"), m.Groups["y"].Value);
            if (y.Length == 2)
            {
                Take(m.Index, m.Index + m.Length);
                mentions.Add(new(TwoWays, null, m.Value, $"{m.Value} has a two-digit year"));
                continue;
            }
            var year = int.Parse(y, CultureInfo.InvariantCulture);
            var (dayMonth, monthDay) = (Day(year, b, a), Day(year, a, b));
            if (dayMonth is { } dm && monthDay is { } md && dm != md)
            {
                Take(m.Index, m.Index + m.Length);
                mentions.Add(new(TwoWays, null, m.Value, $"{m.Value} reads as {Named(dm)} or {Named(md)}"));
            }
            else Dated(m, dayMonth ?? monthDay, false);
        }
        foreach (var (pattern, dayLast) in new[] { (MonthFirst(), true), (DayFirst(), false) })
            foreach (Match m in pattern.Matches(words))
            {
                if (!Free(m.Index, m.Index + m.Length)) continue;
                var month = m.Groups["mon"].Value;
                // A month name that is also a word is a month only written as one (May, March, MAR) or with an ordinal day
                // or a year: "you may 2 ...", "Sam will march 20 miles" are no dates; "march 20th" and "20 may 2027" are.
                if (WordMonth(month) && !m.Groups["ord"].Success && !m.Groups["y"].Success) continue;
                // Another day joined to this one: a range ("Dec 1-3", "1-3 December 2026", "from 16 to 18 November", "Dec 1
                // to 3"), or a list of days ("23 and 24 November", "the 1st or 2nd of December", "3, 4 or 5 December").
                // After a match only a dash is read, unless its day number comes last ("Dec 1"), and never after a year.
                var end = m.Index + m.Length;
                var (before, after) = (Before(JoinedBefore(), m.Index),
                    m.Groups["y"].Success ? Match.Empty : After(dayLast ? JoinedAfter() : DashAfter(), end));
                if (before.Success || after.Success)
                {
                    var (from, to) = (before.Success ? before.Index : m.Index, after.Success ? end + after.Length : end);
                    var ranged = (before.Success ? before : after).Groups["range"].Success;
                    Take(from, to);
                    mentions.Add(new(ranged ? Range : TwoDates, null, words[from..to].Trim(), null));
                    continue;
                }
                if (!Alone(words, m.Index, m.Length)) continue;
                var yearless = !m.Groups["y"].Success;
                // A day number just before a date whose day comes last ("5 Dec 10 guests", "1 Dec 27"): it reads as either
                // day, or as a day and a two-digit year, so neither is taken (the day-first reading never sees it).
                if (dayLast && Before(DayBefore(), m.Index) is { Success: true } day)
                {
                    Take(day.Index, end);
                    mentions.Add(new(TwoWays, null, words[day.Index..end], $"{words[day.Index..end]} has a number on each side of its month"));
                    continue;
                }
                // A number just after a date with no year ("1 December '27", "Dec 1 10 guests"): it may be the year's last
                // two digits, so the date is not taken. A time of day ("10:30", "3pm") is no year.
                if (yearless && After(NumberAfter(), end) is { Success: true } number)
                {
                    var said = number.Groups["n"].Value;
                    Take(m.Index, end + number.Length);
                    mentions.Add(new(TwoWays, null, words[m.Index..(end + number.Length)], said[0] is '\'' or '’'
                        ? $"{words[m.Index..(end + number.Length)]} has a two-digit year"
                        : $"{m.Value} is followed by {said}, which may be a two-digit year"));
                    continue;
                }
                Dated(m, Day(yearless ? today.Year : Num(m, "y"), MonthOf(month), Num(m, "d")), yearless);
            }
        // A month with a year and no day: no day to take.
        foreach (Match m in MonthYear().Matches(words))
            if (Free(m.Index, m.Index + m.Length) && m.Groups["mon"].Value != "may")
            {
                Take(m.Index, m.Index + m.Length);
                mentions.Add(new(NoDay, null, m.Value, null));
            }
        // A weekday not written beside its date, and a day relative to when the words were said.
        foreach (Match m in WeekdayWord().Matches(words))
            if (Free(m.Index, m.Index + m.Length)) mentions.Add(new(Weekday, null, m.Value, null));
        foreach (Match m in RelativeDay().Matches(words))
            if (Free(m.Index, m.Index + m.Length)) mentions.Add(new(Relative, null, m.Value, null));

        if (mentions.Count == 0) return Reading.None;
        var days = mentions.Where(x => x.Kind == "date" && x.Day is not null).Select(x => x.Day!.Value).Distinct().Order().ToList();
        Reading No(string reason, string why) => new(null, null, reason, quote ? why : Unquoted(reason), days);
        string Said(string kind) => string.Join(", ", mentions.Where(x => x.Kind == kind).Select(x => x.Words).Distinct());
        if (mentions.FirstOrDefault(x => x.Kind == Range) is { } range)
            return No(Range, $"it names a range of days ({range.Words}), and which day is due is not guessed");
        if (mentions.FirstOrDefault(x => x.Kind == Mismatch) is { } mismatch)
            return No(Mismatch, $"its weekday and date disagree ({mismatch.Detail}), and which is meant is not guessed");
        if (mentions.FirstOrDefault(x => x.Kind == TwoWays) is { } twoWays)
            return No(TwoWays, $"it names a date that does not read one way only ({twoWays.Detail}), which is not guessed");
        if (days.Count > 1)
            return No(TwoDates, $"it names more than one day ({string.Join(", ", days.Select(Iso))}), and which one is due is not guessed");
        if (mentions.FirstOrDefault(x => x.Kind == TwoDates) is { } list)
            return No(TwoDates, $"it names more than one day ({list.Words}), and which one is due is not guessed");
        var also = days.Count == 1 ? $" as well as {Iso(days[0])}" : "";
        if (mentions.Any(x => x.Kind == Weekday))
            return No(Weekday, $"it names a weekday without its date ({Said(Weekday)}){also}, which is not guessed");
        if (mentions.Any(x => x.Kind == Relative))
            return No(Relative, $"it names a day relative to when it was said ({Said(Relative)}){also}, which is not guessed");
        if (mentions.Any(x => x.Kind == NoSuchDay))
            return No(NoSuchDay, $"it names a day that does not exist ({Said(NoSuchDay)}){also}");
        if (mentions.Any(x => x.Kind == NoDay))
            return No(NoDay, $"it names a month without a day ({Said(NoDay)}){also}");
        var one = mentions.First(x => x.Kind == "date");
        var at = StartOf(days[0], zone);
        if (days[0] < today)
            return No(Past, one.Yearless ? $"it names no year, and that day this year ({Iso(days[0])}) has passed" : $"the day it names ({Iso(days[0])}) has passed");
        if (at > now.AddYears(YearsAhead)) return No(TooFar, $"the day it names ({Iso(days[0])}) is more than {YearsAhead} years ahead");
        return new(days[0], at.ToUniversalTime(), null, null, days);
    }

    // The reason alone, in words, for an answer that may not repeat the memory's words.
    public static string Unquoted(string reason) => reason switch
    {
        Range => "it names a range of days, and which day is due is not guessed",
        Mismatch => "its weekday and date disagree, and which is meant is not guessed",
        TwoWays => "it names a date that does not read one way only, which is not guessed",
        TwoDates => "it names more than one day, and which one is due is not guessed",
        Weekday => "it names a weekday without its date, which is not guessed",
        Relative => "it names a day relative to when it was said, which is not guessed",
        NoSuchDay => "it names a day that does not exist",
        NoDay => "it names a month without a day",
        Past => "the day it names has passed",
        _ => $"the day it names is more than {YearsAhead} years ahead",
    };

    // One thing the words name: a date (Day, or a NoSuchDay when that day does not exist), or a mention that is no date.
    sealed record Mention(string Kind, DateOnly? Day, string Words, string? Detail, bool Yearless = false);

    static int Num(Match m, string group) => int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);

    static DateOnly? Day(int year, int month, int day) =>
        year is >= 1 and <= 9999 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month) ? new DateOnly(year, month, day) : null;

    static int MonthOf(string name) => Array.IndexOf(MonthKeys, name[..3].ToLowerInvariant()) + 1;
    static readonly string[] MonthKeys = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    static DayOfWeek WeekdayOf(string name) => name[..3].ToLowerInvariant() switch
    {
        "mon" => DayOfWeek.Monday, "tue" => DayOfWeek.Tuesday, "wed" => DayOfWeek.Wednesday, "thu" => DayOfWeek.Thursday,
        "fri" => DayOfWeek.Friday, "sat" => DayOfWeek.Saturday, _ => DayOfWeek.Sunday,
    };

    static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    static string Named(DateOnly day) => day.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    // A match in a token of its own (upstream's whitespace guard, with an opening bracket or quote allowed before it):
    // nothing but white space, a line's start, ( or a quote before it, and nothing after it but white space, the end, or
    // one or two closing marks (. , ; : ! ? ) and quotes) before white space or the end. So reports/2026-12-01/x.pdf,
    // build=2026-12-01-04 and v2026-12-01 are no dates.
    static bool Alone(string text, int start, int length)
    {
        var end = start + length;
        if (start > 0 && !char.IsWhiteSpace(text[start - 1]) && text[start - 1] is not ('(' or '"' or '\'')) return false;
        for (var marks = 0; marks <= 2; marks++, end++)
        {
            if (end >= text.Length || char.IsWhiteSpace(text[end])) return true;
            if (marks == 2 || text[end] is not ('.' or ',' or ';' or ':' or '!' or '?' or ')' or '"' or '\'')) return false;
        }
        return false;
    }

    const string Months = "jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?";
    const string Weekdays = "mon(?:day)?|tue(?:s(?:day)?)?|wed(?:nesday)?|thu(?:r(?:s(?:day)?)?)?|fri(?:day)?|sat(?:urday)?|sun(?:day)?";

    [GeneratedRegex(@"^(?<y>\d{4})-(?<m>\d{2})-(?<d>\d{2})(?:[T ](?<h>\d{2}):(?<min>\d{2})(?::(?<s>\d{2})(?:\.\d{1,9})?)?(?<z>[Zz]|[+-]\d{2}:?\d{2})?)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex IsoWhen();

    [GeneratedRegex(@"(?<!\d)(?<y>\d{4})-(?<m>\d{2})-(?<d>\d{2})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDay();

    [GeneratedRegex(@"(?<![\d/])(?<a>\d{1,2})/(?<b>\d{1,2})/(?<y>\d{4}|\d{2})(?![\d/])", RegexOptions.CultureInvariant)]
    private static partial Regex SlashDay();

    // A number that is a time of day, read just after it: "10:30", "9.30", "3pm", "10 am", "3 p.m.".
    const string TimeOfDay = @"[:.]\d|\s?[ap]\.?m\b";

    // A month name and its day ("Dec 1", "December 1st, 2026"); a day that is a time of day ("Dec 10:30", "December 3pm")
    // is none.
    [GeneratedRegex(@"\b(?<mon>" + Months + @")\.?\s+(?<d>\d{1,2})(?<ord>st|nd|rd|th)?(?!\d)(?!" + TimeOfDay + @")(?:,?\s+(?<y>\d{4})(?!\d))?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MonthFirst();

    [GeneratedRegex(@"(?<!\w)(?<d>\d{1,2})(?<ord>st|nd|rd|th)?\s+(?:of\s+)?(?<mon>" + Months + @")\b\.?(?:,?\s+(?<y>\d{4})(?!\d))?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DayFirst();

    // A month name that is also an English word (may, march, mar), written otherwise than as a month (May, MAY).
    static bool WordMonth(string month) =>
        month.ToLowerInvariant() is "may" or "march" or "mar"
        && month != month.ToUpperInvariant() && month != char.ToUpperInvariant(month[0]) + month[1..].ToLowerInvariant();

    // A day number just before a date whose day comes last, alone in its token ("5 Dec 10", "the 1st of Dec 27").
    [GeneratedRegex(@"(?<![^\s(])\d{1,2}(?:st|nd|rd|th)?\s+(?:of\s+)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DayBefore();

    // A number just after a date with no year, alone in its token: a two-digit year with its apostrophe ('27, ’27), or a
    // number of one or two digits that is no time of day.
    [GeneratedRegex(@"^\s+(?<n>['’]\d{2}|\d{1,2}(?!" + TimeOfDay + @"))(?![\w'’])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NumberAfter();

    [GeneratedRegex(@"\b(?<mon>january|february|march|april|may|june|july|august|september|october|november|december)\s+(?<y>\d{4})\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MonthYear();

    // Another day joined to a date, read beside it (FromText's Before and After): a range, the day and a dash, to,
    // through, till or until ("1-3 December", "from 16 to 18 November", "Dec 1 to 3"); or a list, the day and a comma,
    // &, and or or ("23 and 24 November", "the 1st or 2nd of December", "3, 4 or 5 December"). A number that reads as a
    // time of day ("Dec 1 to 3pm", "to 3:30") is still the other end, so the date is refused rather than guessed.
    const string Joins = @"(?:(?<range>[-–]|\bto\b|\bthrough\b|\bthru\b|\btill\b|\buntil\b)|(?<list>,|&|\band\b|\bor\b))";

    // A day number and a join just before a date.
    [GeneratedRegex(@"(?<!\d)\d{1,2}(?:st|nd|rd|th)?\s*" + Joins + @"\s*(?:the\s+)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex JoinedBefore();

    // A join and a day number just after a date whose day number comes last ("Dec 1").
    [GeneratedRegex(@"^\s*" + Joins + @"\s*(?:the\s+)?\d{1,2}(?:st|nd|rd|th)?(?!\d)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex JoinedAfter();

    // A dash and a day number just after any other date ("1 December - 3").
    [GeneratedRegex(@"^\s*(?<range>[-–])\s*\d", RegexOptions.CultureInvariant)]
    private static partial Regex DashAfter();

    // A weekday written beside its date: before it ("Tue, 1 Dec", "Friday 4 December") or after it in brackets ("(Friday)").
    [GeneratedRegex(@"\b(?<wd>" + Weekdays + @")\.?,?\s+(?:the\s+)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WeekdayBefore();

    [GeneratedRegex(@"^,?\s*\((?<wd>" + Weekdays + @")\)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WeekdayAfter();

    // A weekday standing for a day, written out in full (the short forms sun, sat and wed are words too).
    [GeneratedRegex(@"\b(?:monday|tuesday|wednesday|thursday|friday|saturday|sunday)s?\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WeekdayWord();

    // A day relative to when the words were said.
    [GeneratedRegex(@"\b(?:today|tonight|tomorrow|yesterday|overmorrow|(?:this|next|last)\s+(?:morning|afternoon|evening|week|weekend|month|year)"
        + @"|(?:in|within)\s+(?:a|an|one|two|three|four|five|six|seven|\d{1,3})\s+(?:days?|weeks?|fortnights?|months?|years?)"
        + @"|\d{1,3}\s+(?:days?|weeks?)\s+(?:from\s+now|ago)|end\s+of\s+(?:the\s+)?(?:day|week|month|year)|eod|eow|eom)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RelativeDay();
}
