using System.Globalization;
using System.Text.RegularExpressions;

namespace Knowledge;

// Time phrases in a question (SB-P2-16, RET-01): "yesterday", "last week", "this month", "around May 5th", "on Aug 8"
// become a window on the source's own time, read in the install's time zone, and are taken out of the question. Built
// for combined retrieval behind RecallOptions.TimePhrases, which is off: the gate showed no gain to keep it on
// (combined-retrieval.md), so no tool or setting turns it on.
// A wrong window hides the right record, a missing one only widens the answer, so a date that could be part of a name
// ("Aug 8 Cafe") is no window: a month and day become one only when nothing but a time-of-day or zone word follows
// (Safe), never after "as of" (an as-of read, not a window), and none at all when one follows by, until, before,
// after, since or from (a range or an open end this has no meaning for), or when two dates stand alone. Relative phrases
// ("yesterday", "this week") cannot be read as a name and are taken as they come; the first one found decides.
// Adapted from Second Brain's time phrases (MIT; src/text/temporal.ts at 228cea49): the same phrases, windows and
// guards, restated for .NET; no code is copied.
public static partial class TimePhrases
{
    // A window and the question without its phrase (the question itself if nothing else is left).
    public sealed record Window(DateTimeOffset? After, DateTimeOffset? Before, string Query);

    const string Months = "jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:tember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?";
    static readonly string[] MonthKeys = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
    const int MaxDays = 36_500;

    public static Window? Parse(string query, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        DateTimeOffset Midnight(DateOnly day)
        {
            var local = day.ToDateTime(TimeOnly.MinValue);
            return new DateTimeOffset(local, zone.GetUtcOffset(local));
        }
        var weekStart = today.AddDays(today.DayOfWeek == DayOfWeek.Sunday ? -6 : 1 - (int)today.DayOfWeek);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        int? Count(Match m) => int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n <= MaxDays ? n : null;
        DateOnly? Day(int year, string month, string day) =>
            int.TryParse(day, NumberStyles.None, CultureInfo.InvariantCulture, out var d) && year is >= 1 and <= 9999
                && Array.IndexOf(MonthKeys, month.ToLowerInvariant()[..3]) + 1 is var m && d >= 1 && d <= DateTime.DaysInMonth(year, m)
                ? new DateOnly(year, m, d) : null;

        (Regex Pattern, Func<Match, (DateTimeOffset? After, DateTimeOffset? Before)?> Window)[] relative =
        [
            (LastDays(), m => Count(m) is { } n ? (now.AddDays(-n), null) : null),
            (LastWeeks(), m => Count(m) is { } n && n * 7 <= MaxDays ? (now.AddDays(-7 * n), null) : null),
            (LastWeek(), _ => (now.AddDays(-7), null)),
            (ThisWeek(), _ => (Midnight(weekStart), null)),
            (LastMonth(), _ => (Midnight(monthStart.AddMonths(-1)), Midnight(monthStart))),
            (ThisMonth(), _ => (Midnight(monthStart), null)),
            (Yesterday(), _ => (Midnight(today.AddDays(-1)), Midnight(today))),
            (Today(), _ => (Midnight(today), null)),
            (Around(), m => Safe(query, m) && Day(today.Year, m.Groups[1].Value, m.Groups[2].Value) is { } day
                ? (Midnight(day.AddDays(-3)), Midnight(day.AddDays(3))) : null),
        ];
        foreach (var (pattern, window) in relative)
            if (pattern.Match(query) is { Success: true } match && window(match) is { } found)
                return new(found.After, found.Before, Clean(query.Remove(match.Index, match.Length), query));

        var dates = Explicit().Matches(query).Where(m => Day(m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : today.Year,
            m.Groups[1].Value, m.Groups[2].Value) is not null).ToArray();
        if (dates.Any(m => Blocked().IsMatch(query[..m.Index]))) return null;
        var genuine = dates.Where(m => !AsOf().IsMatch(query[..m.Index]) && Safe(query, m)).ToArray();
        if (genuine.Length != 1) return null;
        var date = genuine[0];
        var named = Day(date.Groups[3].Success ? int.Parse(date.Groups[3].Value, CultureInfo.InvariantCulture) : today.Year,
            date.Groups[1].Value, date.Groups[2].Value)!.Value;
        var rest = DanglingComma().Replace(query.Remove(date.Index, date.Length), "");
        return new(Midnight(named), Midnight(named.AddDays(1)), Clean(SpaceBeforePunctuation().Replace(Spaces().Replace(rest, " "), "$1"), query));
    }

    // Whether a month-and-day match stands alone: nothing follows it but punctuation, or what follows (after any
    // capitalised words) is a time of day or a zone: the word time ("New York time"), o'clock, am or pm, UTC or GMT
    // (upstream also lists regional abbreviations; Rhythm reads zones by name).
    static bool Safe(string query, Match m)
    {
        var rest = query[(m.Index + m.Length)..];
        return TimeWords().IsMatch(rest) || !Follows().IsMatch(rest);
    }

    static string Clean(string rest, string query) => Spaces().Replace(rest, " ").Trim() is { Length: > 0 } clean ? clean : query;

    [GeneratedRegex(@"\blast\s+(\d+)\s+days?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex LastDays();
    [GeneratedRegex(@"\blast\s+(\d+)\s+weeks?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex LastWeeks();
    [GeneratedRegex(@"\blast\s+week\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex LastWeek();
    [GeneratedRegex(@"\bthis\s+week\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex ThisWeek();
    [GeneratedRegex(@"\blast\s+month\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex LastMonth();
    [GeneratedRegex(@"\bthis\s+month\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex ThisMonth();
    [GeneratedRegex(@"\byesterday\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Yesterday();
    [GeneratedRegex(@"\btoday\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Today();
    [GeneratedRegex(@"\baround\s+(" + Months + @")\s+(\d{1,2})(?:st|nd|rd|th)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Around();
    [GeneratedRegex(@"\b(?:on\s+)?(" + Months + @")\s+(\d{1,2})(?:st|nd|rd|th)?(?:,\s*(\d{4}))?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Explicit();
    [GeneratedRegex(@"\b(?:by|until|before|after|since|from)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Blocked();
    [GeneratedRegex(@"\bas\s+of\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex AsOf();
    [GeneratedRegex(@"^\s+(?:[A-Z][a-zA-Z]*\s+)*(?:time\b|o'?clock\b|[ap]\.?m\.?\b|(?:UTC|GMT)\b)", RegexOptions.CultureInvariant)] private static partial Regex TimeWords();
    [GeneratedRegex(@"^\s+\S", RegexOptions.CultureInvariant)] private static partial Regex Follows();
    [GeneratedRegex(@",\s*(?=[?!.,;:]|$)", RegexOptions.CultureInvariant)] private static partial Regex DanglingComma();
    [GeneratedRegex(@"\s+([?!.,;:])", RegexOptions.CultureInvariant)] private static partial Regex SpaceBeforePunctuation();
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)] private static partial Regex Spaces();
}
