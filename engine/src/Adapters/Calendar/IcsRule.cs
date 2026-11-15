using System.Globalization;

namespace Knowledge.Adapters.Calendar;

// How much a feed may cost to expand: one candidate time or period each. A rule written to walk for ever ends here, and the
// event it belongs to counts as unreadable rather than holding up the map.
internal sealed class IcsBudget(int total)
{
    public int Left { get; private set; } = total;
    public bool Exhausted => Left <= 0;
    public bool Take(int amount = 1)
    {
        if (Left < amount) { Left = 0; return false; }
        Left -= amount;
        return Left > 0;
    }
}

// One RRULE (RFC 5545 section 3.3.10), read on the event's own wall clock: nine in the morning stays nine in the morning
// across a clock change, and the time zone is applied to each occurrence afterwards (IcsZones). Each period of the rule's
// frequency is one set of days filtered by its BY parts, as the RFC's table asks: a year's or month's days, a week's from its
// week start, a single day. FREQ=SECONDLY is refused: nothing a person keeps in a calendar repeats by the second.
internal sealed class IcsRule
{
    enum Frequency { Minutely, Hourly, Daily, Weekly, Monthly, Yearly }
    const int MaxInterval = 1000, MaxTimesInADay = 1440;

    Frequency frequency;
    int interval = 1;
    DayOfWeek weekStart = DayOfWeek.Monday;
    int[]? months, monthDays, yearDays, weeks, hours, minutes, seconds, positions;
    (DayOfWeek Day, int Nth)[]? days;

    public int? Count { get; private set; }
    // The rule's UNTIL as the feed wrote it, which the event's zone turns into a wall-clock limit.
    public string? Until { get; private set; }
    // The only rule a time zone's clock change has any use for (IcsDefinedZone).
    public bool Yearly => frequency == Frequency.Yearly;

    public static IcsRule? Parse(string value)
    {
        var rule = new IcsRule();
        var known = false;
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = part.IndexOf('=');
            if (at <= 0) return null;
            var (name, text) = (part[..at].ToUpperInvariant(), part[(at + 1)..].ToUpperInvariant());
            switch (name)
            {
                case "FREQ":
                    known = true;
                    switch (text)
                    {
                        case "MINUTELY": rule.frequency = Frequency.Minutely; break;
                        case "HOURLY": rule.frequency = Frequency.Hourly; break;
                        case "DAILY": rule.frequency = Frequency.Daily; break;
                        case "WEEKLY": rule.frequency = Frequency.Weekly; break;
                        case "MONTHLY": rule.frequency = Frequency.Monthly; break;
                        case "YEARLY": rule.frequency = Frequency.Yearly; break;
                        default: return null;
                    }
                    break;
                case "INTERVAL":
                    if (!int.TryParse(text, CultureInfo.InvariantCulture, out rule.interval) || rule.interval is < 1 or > MaxInterval) return null;
                    break;
                case "COUNT":
                    if (!int.TryParse(text, CultureInfo.InvariantCulture, out var count) || count < 1) return null;
                    rule.Count = count;
                    break;
                case "UNTIL":
                    if (IcsTimes.Read(text, null) is null) return null;
                    rule.Until = text;
                    break;
                case "WKST":
                    if (Day(text) is not { } start) return null;
                    rule.weekStart = start;
                    break;
                case "BYMONTH": if ((rule.months = Numbers(text, 1, 12, false)) is null) return null; break;
                case "BYMONTHDAY": if ((rule.monthDays = Numbers(text, 1, 31, true)) is null) return null; break;
                case "BYYEARDAY": if ((rule.yearDays = Numbers(text, 1, 366, true)) is null) return null; break;
                case "BYWEEKNO": if ((rule.weeks = Numbers(text, 1, 53, true)) is null) return null; break;
                case "BYHOUR": if ((rule.hours = Numbers(text, 0, 23, false)) is null) return null; break;
                case "BYMINUTE": if ((rule.minutes = Numbers(text, 0, 59, false)) is null) return null; break;
                case "BYSECOND": if ((rule.seconds = Numbers(text, 0, 60, false)) is null) return null; break;
                case "BYSETPOS": if ((rule.positions = Numbers(text, 1, 366, true)) is null) return null; break;
                case "BYDAY":
                    var listed = new List<(DayOfWeek, int)>();
                    foreach (var item in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (item.Length < 2 || Day(item[^2..]) is not { } day) return null;
                        var nth = 0;
                        if (item.Length > 2 && (!int.TryParse(item[..^2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out nth) || nth is 0 or < -53 or > 53)) return null;
                        listed.Add((day, nth));
                    }
                    if (listed.Count == 0) return null;
                    rule.days = [.. listed];
                    break;
                // The format lets a service add parts of its own: they are left out, as it asks.
            }
        }
        if (!known) return null;
        if ((rule.hours?.Length ?? 1) * (rule.minutes?.Length ?? 1) * (rule.seconds?.Length ?? 1) > MaxTimesInADay) return null;
        return rule;
    }

    static DayOfWeek? Day(string text) => text switch
    {
        "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday, "TH" => DayOfWeek.Thursday,
        "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday, "SU" => DayOfWeek.Sunday, _ => null,
    };

    static int[]? Numbers(string text, int low, int high, bool negative)
    {
        var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return null;
        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) return null;
            // The smallest int has no size of its own: Math.Abs would throw.
            if (n == int.MinValue) return null;
            var size = Math.Abs(n);
            if (size < low || size > high || n < 0 && !negative) return null;
            numbers[i] = n;
        }
        return numbers;
    }

    // Every occurrence from `start` on, oldest first, with `start` always the first and counted (the event's DTSTART is its
    // first instance, whether the rule would have given it or not). `until` and `horizon` end it. Reach says nothing before it
    // is wanted: a rule with no count needs no walk from its start, so whole periods before it are stepped over, on its
    // interval. A budget used up ends it too, and says so (IcsBudget.Exhausted).
    public IEnumerable<DateTime> Walls(DateTime start, DateTime? until, DateTime reach, DateTime horizon, IcsBudget budget, bool dateOnly = false)
    {
        if ((until is null || start <= until) && start <= horizon && reach <= start) yield return start;
        var produced = 1;
        if (Count is 1) yield break;

        var noDayPart = days is null && monthDays is null && yearDays is null && weeks is null;
        var (useMonths, useMonthDays, useDays) = (months, monthDays, days);
        if (noDayPart)
        {
            if (frequency == Frequency.Yearly) useMonths ??= [start.Month];
            if (frequency is Frequency.Yearly or Frequency.Monthly) useMonthDays = [start.Day];
            if (frequency == Frequency.Weekly) useDays = [(start.DayOfWeek, 0)];
        }

        var period = Align(start);
        if (Count is null) period = Forward(period, reach);
        while (period <= horizon && (until is null || period <= until))
        {
            var candidates = new List<DateTime>();
            foreach (var day in DaysOf(period))
            {
                if (!budget.Take()) yield break;
                if (!Keeps(day, useMonths, useMonthDays, useDays)) continue;
                foreach (var time in TimesOf(period, start, dateOnly))
                {
                    candidates.Add(day.Date + time);
                    if (!budget.Take()) yield break;
                }
            }
            candidates.Sort();
            foreach (var wall in Positioned(candidates))
            {
                if (wall <= start) continue;
                if (until is { } end && wall > end || wall > horizon) yield break;
                yield return wall;
                if (Count is { } limit && ++produced >= limit) yield break;
            }
            period = Next(period);
        }
    }

    IEnumerable<DateTime> Positioned(List<DateTime> candidates)
    {
        if (positions is null) return candidates;
        var chosen = new SortedSet<DateTime>();
        foreach (var position in positions)
        {
            var index = position > 0 ? position - 1 : candidates.Count + position;
            if (index >= 0 && index < candidates.Count) chosen.Add(candidates[index]);
        }
        return chosen;
    }

    // The start of the period `at` falls in.
    DateTime Align(DateTime at) => frequency switch
    {
        Frequency.Yearly => new(at.Year, 1, 1),
        Frequency.Monthly => new(at.Year, at.Month, 1),
        Frequency.Weekly => at.Date.AddDays(-Offset(at.DayOfWeek)),
        Frequency.Daily => at.Date,
        Frequency.Hourly => at.Date.AddHours(at.Hour),
        _ => at.Date.AddHours(at.Hour).AddMinutes(at.Minute),
    };
    DateTime Next(DateTime period) => Step(period, interval);
    DateTime Step(DateTime period, long periods) => frequency switch
    {
        Frequency.Yearly => period.AddYears(Limited(periods, 8000)),
        Frequency.Monthly => period.AddMonths(Limited(periods, 96000)),
        Frequency.Weekly => period.AddDays(7d * periods),
        Frequency.Daily => period.AddDays(periods),
        Frequency.Hourly => period.AddHours(periods),
        _ => period.AddMinutes(periods),
    };
    static int Limited(long periods, int most) => (int)Math.Min(periods, most);
    int Offset(DayOfWeek day) => ((int)day - (int)weekStart + 7) % 7;

    // The last period on this rule's interval that ends before `reach`, or `period` itself.
    DateTime Forward(DateTime period, DateTime reach)
    {
        if (reach <= period) return period;
        var span = reach - period;
        long whole = frequency switch
        {
            Frequency.Yearly => (reach.Year - period.Year) / interval,
            Frequency.Monthly => (reach.Year * 12L + reach.Month - period.Year * 12L - period.Month) / interval,
            Frequency.Weekly => (long)(span.TotalDays / 7) / interval,
            Frequency.Daily => (long)span.TotalDays / interval,
            Frequency.Hourly => (long)span.TotalHours / interval,
            _ => (long)span.TotalMinutes / interval,
        } - 1;
        return whole > 0 ? Step(period, whole * interval) : period;
    }

    IEnumerable<DateTime> DaysOf(DateTime period)
    {
        var (first, length) = frequency switch
        {
            Frequency.Yearly => (period, DateTime.IsLeapYear(period.Year) ? 366 : 365),
            Frequency.Monthly => (period, DateTime.DaysInMonth(period.Year, period.Month)),
            Frequency.Weekly => (period, 7),
            _ => (period.Date, 1),
        };
        for (var i = 0; i < length; i++) yield return first.AddDays(i);
    }

    bool Keeps(DateTime day, int[]? useMonths, int[]? useMonthDays, (DayOfWeek Day, int Nth)[]? useDays)
    {
        if (useMonths is not null && !useMonths.Contains(day.Month)) return false;
        if (yearDays is not null)
        {
            var length = DateTime.IsLeapYear(day.Year) ? 366 : 365;
            if (!yearDays.Contains(day.DayOfYear) && !yearDays.Contains(day.DayOfYear - length - 1)) return false;
        }
        if (useMonthDays is not null)
        {
            var length = DateTime.DaysInMonth(day.Year, day.Month);
            if (!useMonthDays.Contains(day.Day) && !useMonthDays.Contains(day.Day - length - 1)) return false;
        }
        if (weeks is not null)
        {
            var (week, total) = WeekOf(day);
            if (!weeks.Contains(week) && !weeks.Contains(week - total - 1)) return false;
        }
        return useDays is null || useDays.Any(d => d.Day == day.DayOfWeek && Nth(day, d.Nth, useMonths));
    }

    // A numbered weekday counts within the month for a monthly rule or a yearly one naming months, else within the year; in a
    // shorter period, or with BYWEEKNO, the number means nothing (the format forbids it there).
    bool Nth(DateTime day, int nth, int[]? useMonths)
    {
        if (nth == 0 || frequency is not (Frequency.Monthly or Frequency.Yearly) || weeks is not null) return true;
        var inMonth = frequency == Frequency.Monthly || useMonths is not null;
        var (position, length) = inMonth ? (day.Day, DateTime.DaysInMonth(day.Year, day.Month))
            : (day.DayOfYear, DateTime.IsLeapYear(day.Year) ? 366 : 365);
        return nth > 0 ? (position - 1) / 7 + 1 == nth : (length - position) / 7 + 1 == -nth;
    }

    // A day's week number (the week holding four or more of its days in the year is week 1, weeks begin at WKST) and how many
    // weeks its year has.
    (int Week, int Total) WeekOf(DateTime day)
    {
        var start = day.AddDays(-Offset(day.DayOfWeek));
        var year = start.AddDays(3).Year;
        DateTime First(int y) { var fourth = new DateTime(y, 1, 4); return fourth.AddDays(-Offset(fourth.DayOfWeek)); }
        return ((start - First(year)).Days / 7 + 1, (First(year + 1) - First(year)).Days / 7);
    }

    IEnumerable<TimeSpan> TimesOf(DateTime period, DateTime start, bool dateOnly)
    {
        if (dateOnly) { yield return TimeSpan.Zero; yield break; }
        int[] Own(int[]? by, int fallback) => by is null ? [fallback] : by;
        // A rule that steps in hours or minutes has its own hour (or minute) in the period; BY parts then only limit it.
        int[] hourly = frequency is Frequency.Hourly or Frequency.Minutely
            ? hours is null || hours.Contains(period.Hour) ? [period.Hour] : []
            : Own(hours, start.Hour);
        int[] minutely = frequency == Frequency.Minutely
            ? minutes is null || minutes.Contains(period.Minute) ? [period.Minute] : []
            : Own(minutes, start.Minute);
        foreach (var hour in hourly.Distinct().Order())
            foreach (var minute in minutely.Distinct().Order())
                foreach (var second in Own(seconds, start.Second).Distinct().Order())
                    yield return new TimeSpan(hour, minute, Math.Min(second, 59));
    }
}
