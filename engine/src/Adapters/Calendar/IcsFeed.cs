using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Knowledge.Adapters.Calendar;

// Why a feed is no whole calendar: words of our own, never the feed's. Nothing of it comes from a caller's text.
internal sealed class IcsInvalid(string message) : Exception(message);

// A feed's times, as RFC 5545 section 3.3 writes them.
internal static class IcsTimes
{
    const int FirstYear = 1900, LastYear = 2300;

    // yyyyMMdd, yyyyMMddTHHmmss or the same with Z; null for anything else, or a year out of reach.
    public static IcsTime? Read(string text, string? zone)
    {
        var value = text.Trim();
        if (value.Length == 8 && Exact(value, "yyyyMMdd") is { } date) return new IcsTime(date, IcsTimeKind.Date);
        var utc = value.EndsWith('Z') || value.EndsWith('z');
        var body = utc ? value[..^1] : value;
        // A leap second is the second before it.
        if (body.Length == 15 && body.EndsWith("60", StringComparison.Ordinal)) body = body[..13] + "59";
        if (body.Length != 15 || Exact(body, "yyyyMMdd'T'HHmmss") is not { } wall) return null;
        return utc ? new IcsTime(wall, IcsTimeKind.Utc) : zone is { Length: > 0 } ? new IcsTime(wall, IcsTimeKind.Zoned, zone) : new IcsTime(wall, IcsTimeKind.Floating);
    }

    static DateTime? Exact(string text, string format) =>
        DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) && at.Year is >= FirstYear and <= LastYear ? at : null;

    public static DateTimeOffset? Instant(string text) =>
        Read(text, null) is { Kind: IcsTimeKind.Utc } time ? new DateTimeOffset(DateTime.SpecifyKind(time.Wall, DateTimeKind.Utc)) : null;

    // A DURATION: [+]P[nW][nD][T[nH][nM][nS]]. Null for a negative one or one past a year, which no event holds.
    public static TimeSpan? Duration(string text)
    {
        var match = DurationPattern.Match(text.Trim());
        if (!match.Success || !match.Groups.Cast<Group>().Skip(1).Any(g => g.Success)) return null;
        long Part(string name) => match.Groups[name].Success && long.TryParse(match.Groups[name].Value, CultureInfo.InvariantCulture, out var n) ? Math.Min(n, 100_000) : 0;
        var total = TimeSpan.FromDays(Part("w") * 7 + Part("d")) + TimeSpan.FromHours(Part("h")) + TimeSpan.FromMinutes(Part("m")) + TimeSpan.FromSeconds(Part("s"));
        return total <= TimeSpan.FromDays(366) ? total : null;
    }
    static readonly Regex DurationPattern = new(@"^\+?P(?:(?<w>\d{1,6})W)?(?:(?<d>\d{1,6})D)?(?:T(?:(?<h>\d{1,6})H)?(?:(?<m>\d{1,6})M)?(?:(?<s>\d{1,6})S)?)?$", RegexOptions.CultureInvariant);
}

// One VEVENT, read. Its text is the feed's own and untrusted: nothing logs it and nothing follows it.
internal sealed record IcsEvent(string Uid, IcsTime Start, IcsTime? End, TimeSpan? Duration, string Summary, string Location, string Description, string Url,
    string Status, int Sequence, DateTimeOffset? Created, DateTimeOffset? Modified, IcsRule? Rule, IReadOnlyList<IcsTime> Added, IReadOnlyList<IcsTime> Except,
    IcsTime? RecurrenceId, bool ThisAndFuture)
{
    public bool Cancelled => Status == "CANCELLED";
    public bool Tentative => Status == "TENTATIVE";
    public bool Recurs => Rule is not null || Added.Count > 0;
}

// One occurrence of an event in a window. Id is the same at every read: the event's UID and, in a series, the time this
// occurrence was due, so a moved occurrence keeps its star and a changed one is the same star at a new revision. Days: how
// many calendar days an all-day occurrence covers.
internal sealed record IcsOccurrence(string Id, IcsEvent Event, DateTimeOffset Start, DateTimeOffset End, bool AllDay, bool ZoneUnknown, int Days = 1);

// What a window of a feed holds. Capped: an event gave more occurrences than one lists. Unreadable: events a rule could not
// give, on top of those the feed's reading already counted.
internal sealed record IcsWindow(IReadOnlyList<IcsOccurrence> Occurrences, bool Capped, int Unreadable);

// One calendar feed, read whole or not at all. Its structure must be whole: a feed that ends early, or an event that never
// ends, is a failed read rather than a calendar with less in it, or it would look as if events had gone. Inside that, what a
// service gets wrong is left out one line or one event at a time: a line of no form, a property it does not know, an event
// with no time (counted in Unreadable).
internal sealed partial class IcsFeed(IReadOnlyList<IcsEvent> events, int unreadable, IcsZones zones)
{
    // MaxText: how many characters of a property are kept, past what is ever shown (CalendarAdapter.MaxWords) by more than a
    // link is long, so the link is looked for before the words are cut to what shows.
    const int MaxDepth = 8, MaxEvents = 50_000, MaxText = CalendarAdapter.MaxWords + 8192, PerSeries = 500_000;
    public const int MaxPerEvent = 200;
    public IReadOnlyList<IcsEvent> Events => events;
    public int Unreadable => unreadable;
    internal IcsZones Zones => zones;

    sealed class Component(string name)
    {
        public string Name { get; } = name;
        public List<Line> Lines { get; } = [];
        public List<Component> Children { get; } = [];
        public Line? First(string property) => Lines.Find(l => l.Name == property);
        public IEnumerable<Line> All(string property) => Lines.Where(l => l.Name == property);
    }
    sealed record Line(string Name, Dictionary<string, string> Parameters, string Value)
    {
        public string? Parameter(string name) => Parameters.GetValueOrDefault(name);
        public string? Zone => Parameter("TZID");
    }

    public static IcsFeed Parse(string text, TimeZoneInfo install)
    {
        var calendars = Structure(text);
        var defined = new Dictionary<string, IcsDefinedZone>(StringComparer.Ordinal);
        var work = new IcsZoneWork();
        var events = new List<IcsEvent>();
        var unreadable = 0;
        foreach (var calendar in calendars)
        {
            foreach (var zone in calendar.Children.Where(c => c.Name == "VTIMEZONE"))
                if (defined.Count < IcsDefinedZone.MaxZones && zone.First("TZID")?.Value.Trim() is { Length: > 0 } id && Defined(zone, work) is { } own) defined.TryAdd(id, own);
            var cancelsAll = string.Equals(calendar.First("METHOD")?.Value.Trim(), "CANCEL", StringComparison.OrdinalIgnoreCase);
            foreach (var one in calendar.Children.Where(c => c.Name == "VEVENT"))
            {
                if (events.Count >= MaxEvents) throw new IcsInvalid("the calendar is too large");
                if (Event(one, cancelsAll) is { } read) events.Add(read); else unreadable++;
            }
        }
        return new(events, unreadable, new(install, defined));
    }

    // The feed's components, or IcsInvalid. Lines of no form are left out: a broken fold reads as one.
    static List<Component> Structure(string text)
    {
        var roots = new List<Component>();
        var open = new Stack<Component>();
        foreach (var raw in Unfolded(text))
        {
            if (Content(raw) is not { } line) continue;
            if (line.Name == "BEGIN")
            {
                var name = line.Value.Trim().ToUpperInvariant();
                if (open.Count >= MaxDepth) throw new IcsInvalid("not a whole calendar");
                var component = new Component(name);
                if (open.Count == 0)
                {
                    if (name != "VCALENDAR") throw new IcsInvalid("not a whole calendar");
                    roots.Add(component);
                }
                else open.Peek().Children.Add(component);
                open.Push(component);
            }
            else if (line.Name == "END")
            {
                if (open.Count == 0 || open.Peek().Name != line.Value.Trim().ToUpperInvariant()) throw new IcsInvalid("not a whole calendar");
                open.Pop();
            }
            else if (open.Count > 0) open.Peek().Lines.Add(line);
        }
        if (open.Count > 0 || roots.Count == 0) throw new IcsInvalid("not a whole calendar");
        return roots;
    }

    // A line folded over two is one line: its next begins with a space or a tab. Either line ending is taken.
    static IEnumerable<string> Unfolded(string text)
    {
        var line = new StringBuilder();
        var any = false;
        var at = text.Length > 0 && text[0] == (char)0xFEFF ? 1 : 0;
        while (true)
        {
            var end = text.IndexOfAny(['\r', '\n'], at);
            var stop = end < 0 ? text.Length : end;
            if (any && stop > at && text[at] is ' ' or '\t') line.Append(text, at + 1, stop - at - 1);
            else
            {
                if (any) yield return line.ToString();
                line.Clear();
                line.Append(text, at, stop - at);
                any = true;
            }
            if (end < 0) break;
            at = end + (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1);
        }
        yield return line.ToString();
    }

    // NAME[;PARAMETER=VALUE...]:VALUE. The first colon outside quotes ends the name and its parameters. Null for no such line.
    static Line? Content(string raw)
    {
        var quoted = false;
        var colon = -1;
        var breaks = new List<int>();
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '"') quoted = !quoted;
            else if (!quoted && c == ';') breaks.Add(i);
            else if (!quoted && c == ':') { colon = i; break; }
        }
        if (colon <= 0) return null;
        var end = breaks.Count > 0 ? breaks[0] : colon;
        var name = raw[..end];
        if (!NamePattern().IsMatch(name)) return null;
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        breaks.Add(colon);
        for (var i = 0; i + 1 < breaks.Count; i++)
        {
            var part = raw[(breaks[i] + 1)..breaks[i + 1]];
            var equals = part.IndexOf('=');
            if (equals > 0) parameters[part[..equals]] = part[(equals + 1)..].Trim('"');
        }
        return new(name.ToUpperInvariant(), parameters, raw[(colon + 1)..]);
    }

    // A property's words. Characters that hide words are gone before any is counted, so they cannot push what matters past
    // the limit, and it never ends on half a character.
    static string Words(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var visible = CalendarText.Visible(value);
        var text = new StringBuilder(Math.Min(visible.Length, MaxText));
        for (var i = 0; i < visible.Length && text.Length < MaxText; i++)
        {
            if (visible[i] != '\\' || i + 1 == visible.Length) { text.Append(visible[i]); continue; }
            var next = visible[++i];
            text.Append(next is 'n' or 'N' ? '\n' : next);
        }
        if (text.Length > 0 && char.IsHighSurrogate(text[^1])) text.Length--;
        return text.ToString();
    }

    // Null when the event has no time this reads, or a rule it cannot; such an event counts as unreadable.
    static IcsEvent? Event(Component one, bool cancelled)
    {
        if (one.First("DTSTART") is not { } startLine || IcsTimes.Read(startLine.Value, startLine.Zone) is not { } start) return null;
        var uid = one.First("UID")?.Value.Trim();
        var summary = Words(one.First("SUMMARY")?.Value);
        // The format requires a UID; one a service left out is made from what it does say, so it stays the same event at each read.
        if (string.IsNullOrEmpty(uid)) uid = "no-uid:" + Hashing.Hash(startLine.Value + "\n" + summary);
        IcsTime? end = one.First("DTEND") is { } endLine ? IcsTimes.Read(endLine.Value, endLine.Zone) : null;
        var duration = one.First("DURATION") is { } length ? IcsTimes.Duration(length.Value) : null;
        IcsRule? rule = null;
        if (one.First("RRULE") is { } ruleLine && (rule = IcsRule.Parse(ruleLine.Value)) is null) return null;
        var recurrence = one.First("RECURRENCE-ID");
        // A changed occurrence that cannot say which it is would read as the event itself, and replace its whole series.
        if (recurrence is not null && IcsTimes.Read(recurrence.Value, recurrence.Zone) is null) return null;
        var status = cancelled ? "CANCELLED" : (one.First("STATUS")?.Value.Trim().ToUpperInvariant() ?? "");
        _ = int.TryParse(one.First("SEQUENCE")?.Value.Trim(), CultureInfo.InvariantCulture, out var sequence);
        return new(uid, start, end, duration, summary, Words(one.First("LOCATION")?.Value), Words(one.First("DESCRIPTION")?.Value), one.First("URL")?.Value.Trim() ?? "",
            status, sequence, IcsTimes.Instant(one.First("CREATED")?.Value ?? ""), IcsTimes.Instant(one.First("LAST-MODIFIED")?.Value ?? ""), rule,
            Times(one.All("RDATE")), Times(one.All("EXDATE")),
            recurrence is null ? null : IcsTimes.Read(recurrence.Value, recurrence.Zone),
            string.Equals(recurrence?.Parameter("RANGE"), "THISANDFUTURE", StringComparison.OrdinalIgnoreCase));
    }

    // A list of times; a period (start/end) counts by its start. What does not read is left out.
    static List<IcsTime> Times(IEnumerable<Line> lines)
    {
        var times = new List<IcsTime>();
        foreach (var line in lines)
            foreach (var item in line.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (IcsTimes.Read(item.Split('/')[0], line.Zone) is { } time) times.Add(time);
        return times;
    }

    // A zone is defined only by what a clock change is: a yearly rule, or dates. Any other rule, or one that does not read, and
    // the zone is no definition at all: its events say their zone is not known here.
    static IcsDefinedZone? Defined(Component zone, IcsZoneWork work)
    {
        var observances = new List<IcsDefinedZone.Observance>();
        foreach (var part in zone.Children.Where(c => c.Name is "STANDARD" or "DAYLIGHT"))
        {
            if (part.First("DTSTART")?.Value is not { } text || IcsTimes.Read(text, null) is not { } start
                || IcsDefinedZone.Offset(part.First("TZOFFSETFROM")?.Value) is not { } from || IcsDefinedZone.Offset(part.First("TZOFFSETTO")?.Value) is not { } to) continue;
            IcsRule? rule = null;
            if (part.First("RRULE") is { } line && (rule = IcsRule.Parse(line.Value)) is not { Yearly: true }) return null;
            observances.Add(new(start.Wall, from, to, rule, [.. Times(part.All("RDATE")).Select(t => t.Wall)]));
        }
        return observances.Count > 0 ? new(observances, work) : null;
    }

    // ---- a window ----------------------------------------------------------------------------------------------------

    static string Key(IcsTime time, DateTimeOffset instant) => time.IsDate ? "d" + time.Wall.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : "t" + instant.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    // The newest copy of an event wins: its sequence, then its last change, then the later in the feed.
    static IcsEvent Newest(IEnumerable<IcsEvent> copies) =>
        copies.Select((e, i) => (e, i)).OrderBy(x => x.e.Sequence).ThenBy(x => x.e.Modified ?? DateTimeOffset.MinValue).ThenBy(x => x.i).Last().e;

    // The occurrences that overlap [from, to), oldest first within each event, at most MaxPerEvent of an event. Budget bounds
    // what expanding costs across the feed, and no one series may spend more than PerSeries of it.
    public IcsWindow Within(DateTimeOffset from, DateTimeOffset to, IcsBudget budget)
    {
        var found = new List<IcsOccurrence>();
        var (capped, broken) = (false, 0);
        foreach (var group in events.GroupBy(e => e.Uid, StringComparer.Ordinal))
        {
            var master = group.Where(e => e.RecurrenceId is null).ToArray() is { Length: > 0 } copies ? Newest(copies) : null;
            if (master is { Cancelled: true }) continue;
            var overrides = group.Where(e => e.RecurrenceId is not null)
                .GroupBy(e => Key(e.RecurrenceId!.Value, zones.Instant(e.RecurrenceId.Value)), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => Newest(g), StringComparer.Ordinal);
            var occurrences = new List<IcsOccurrence>();
            foreach (var one in overrides.Values)
                if (!one.Cancelled && (!one.ThisAndFuture || master is null)) Keep(occurrences, Occurrence(one, one.Start, null, Id(group.Key, one.RecurrenceId!.Value), master), from, to);
            if (master is not null)
            {
                if (master.Recurs) { if (!Series(master, overrides, from, to, budget, occurrences)) broken++; }
                else if (!overrides.ContainsKey(Key(master.Start, zones.Instant(master.Start)))) Keep(occurrences, Occurrence(master, master.Start, null, Id(group.Key, null)), from, to);
            }
            occurrences.Sort((a, b) => a.Start.CompareTo(b.Start));
            if (occurrences.Count > MaxPerEvent) { capped = true; occurrences.RemoveRange(MaxPerEvent, occurrences.Count - MaxPerEvent); }
            found.AddRange(occurrences);
        }
        return new(found, capped, broken);
    }

    static void Keep(List<IcsOccurrence> list, IcsOccurrence one, DateTimeOffset from, DateTimeOffset to)
    {
        // A moment with no length is in the window from its start on; anything else if it overlaps at all.
        if (one.Start < to && (one.End > from || one.End == one.Start && one.Start >= from)) list.Add(one);
    }

    string Id(string uid, IcsTime? due) => Hashing.Hash(uid + "\n" + (due is { } t ? Key(t, zones.Instant(t)) : ""));

    // One occurrence of `e` at `at`, lasting as the event does, or as its series does when it says nothing of its own length.
    // Shift: how far a change to this and all later occurrences moved them.
    IcsOccurrence Occurrence(IcsEvent e, IcsTime at, TimeSpan? shift, string id, IcsEvent? series = null)
    {
        var own = e.End is null && e.Duration is null && series is not null ? series : e;
        if (at.IsDate)
        {
            // Whole days on the clock a date is read on, never a fixed length: a day is 23 or 25 hours when the clocks change.
            var first = at with { Wall = at.Wall.AddDays(Math.Round((shift ?? TimeSpan.Zero).TotalDays)) };
            var days = Days(own);
            return new(id, e, zones.Instant(first), zones.Instant(first with { Wall = first.Wall.AddDays(days) }), true, !zones.Known(at), days);
        }
        var start = zones.Instant(at) + (shift ?? TimeSpan.Zero);
        return new(id, e, start, start + Length(own), false, !zones.Known(at));
    }

    // How many days an all-day event covers: its dates' own difference, or its length in whole days, and never none.
    int Days(IcsEvent e) => e.Start.IsDate && e.End is { IsDate: true } end && (end.Wall - e.Start.Wall).Days is > 0 and <= 366 and var whole
        ? whole : Math.Max(1, (int)Math.Ceiling(Length(e).TotalDays));

    TimeSpan Length(IcsEvent e)
    {
        if (e.End is { } end && zones.Instant(end) - zones.Instant(e.Start) is var span && span > TimeSpan.Zero && span <= TimeSpan.FromDays(366)) return span;
        if (e.Duration is { } duration) return duration;
        return e.Start.IsDate ? TimeSpan.FromDays(1) : TimeSpan.Zero;
    }

    // A series' occurrences into `into`, all but those an override stands for and those excepted. False when the budget ran out.
    bool Series(IcsEvent master, Dictionary<string, IcsEvent> overrides, DateTimeOffset from, DateTimeOffset to, IcsBudget budget, List<IcsOccurrence> into)
    {
        var except = new HashSet<string>(StringComparer.Ordinal);
        foreach (var time in master.Except) except.Add(master.Start.IsDate ? Key(time with { Kind = IcsTimeKind.Date }, default) : Key(time, zones.Instant(time)));
        var length = Length(master);
        // Two days either side of the window more than covers any difference between a wall clock and an instant.
        var margin = TimeSpan.FromDays(2);
        var reach = zones.WallOf(from - length - margin, master.Start);
        var horizon = zones.WallOf(to + margin, master.Start);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var later = overrides.Values.Where(o => o.ThisAndFuture).Select(o => (Due: zones.Instant(o.RecurrenceId!.Value), Event: o)).OrderBy(o => o.Due).ToArray();

        void Add(IcsTime time)
        {
            var instant = zones.Instant(time);
            var key = Key(time, instant);
            if (!seen.Add(key) || except.Contains(key) || except.Contains("d" + time.Wall.ToString("yyyyMMdd", CultureInfo.InvariantCulture)) || overrides.TryGetValue(key, out var exact) && !exact.ThisAndFuture) return;
            var id = Id(master.Uid, time);
            var change = later.LastOrDefault(l => l.Due <= instant).Event;
            if (change is null) { Keep(into, Occurrence(master, time, null, id), from, to); return; }
            if (!change.Cancelled) Keep(into, Occurrence(change, time, zones.Instant(change.Start) - zones.Instant(change.RecurrenceId!.Value), id, master), from, to);
        }

        foreach (var added in master.Added) Add(added);
        if (master.Rule is null) { Add(master.Start); return true; }
        DateTime? until = null;
        if (master.Rule.Until is { } text && IcsTimes.Read(text, null) is { } limit)
            until = limit.IsDate || master.Start.IsDate ? limit.Wall.Date.AddDays(1).AddSeconds(-1)
                : limit.Kind == IcsTimeKind.Utc ? zones.WallOf(new DateTimeOffset(DateTime.SpecifyKind(limit.Wall, DateTimeKind.Utc)), master.Start)
                : limit.Wall;
        var own = new IcsBudget(Math.Min(budget.Left, PerSeries));
        var had = own.Left;
        foreach (var wall in master.Rule.Walls(master.Start.Wall, until, reach, horizon, own, master.Start.IsDate))
        {
            Add(master.Start with { Wall = wall });
            // Well past one event's cap: enough to say it was cut.
            if (into.Count > MaxPerEvent * 2) break;
        }
        budget.Take(had - own.Left);
        return !own.Exhausted;
    }

    [GeneratedRegex("^[A-Za-z0-9-]+$")] private static partial Regex NamePattern();
}
