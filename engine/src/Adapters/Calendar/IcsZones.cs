using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Knowledge.Adapters.Calendar;

// How a feed wrote a time: a date with no time (an all-day event), a wall clock with no zone (the install's), a UTC instant,
// or a wall clock in a named zone.
internal enum IcsTimeKind { Date, Floating, Utc, Zoned }
internal readonly record struct IcsTime(DateTime Wall, IcsTimeKind Kind, string? Zone = null)
{
    public bool IsDate => Kind == IcsTimeKind.Date;
}

// The zones of one feed. A TZID is read as the system knows it (an IANA name, or where the system's data allows a Windows
// name), else from the feed's own VTIMEZONE, which is how a service's private name (Outlook's among them) is written, else
// on the install's clock, and the event then says its zone is not known here. A name reaches the system only in the plain
// form its zone names take, never as a path. A time the clocks skip is read with the offset before the gap and a time they
// pass twice as the first of the two, as RFC 5545 section 3.3.5 asks. One feed's zones serve the map's rebuild and a star's
// opening at once, so what they keep is safe to share.
internal sealed partial class IcsZones(TimeZoneInfo install, IReadOnlyDictionary<string, IcsDefinedZone> defined)
{
    readonly ConcurrentDictionary<string, TimeZoneInfo?> system = new(StringComparer.Ordinal);

    public TimeZoneInfo Install => install;
    internal long OnsetsKept => defined.Values.Sum(z => (long)z.OnsetsKept);

    TimeZoneInfo? SystemZone(string name) => system.GetOrAdd(name, Find);

    static TimeZoneInfo? Find(string name)
    {
        TimeZoneInfo? zone = null;
        if (Plain(name) is { } plain)
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(plain); }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException or System.Security.SecurityException or ArgumentException or UnauthorizedAccessException) { }
        return zone;
    }

    // The name to ask the system for, or null for one that may not reach it. Some services write a zone under their own path,
    // the zone's name last, and only those last two parts go on, looked for among the system's zones and nowhere else.
    internal static string? Plain(string name)
    {
        var plain = name;
        if (plain.StartsWith('/') && plain.Split('/', StringSplitOptions.RemoveEmptyEntries) is { Length: >= 2 } parts) plain = string.Join('/', parts[^2..]);
        return PlainName().IsMatch(plain) && !plain.Contains("..", StringComparison.Ordinal) ? plain : null;
    }

    public bool Known(IcsTime time) => time.Kind != IcsTimeKind.Zoned || SystemZone(time.Zone!) is not null
        || defined.TryGetValue(time.Zone!, out var own) && own.Readable(time.Wall.Year);

    // A feed's time as an instant, in UTC. A date is its midnight on the install's clock.
    public DateTimeOffset Instant(IcsTime time)
    {
        var wall = DateTime.SpecifyKind(time.Wall, DateTimeKind.Unspecified);
        if (time.Kind == IcsTimeKind.Utc) return new(wall, TimeSpan.Zero);
        if (time.Kind == IcsTimeKind.Zoned && time.Zone is { } name)
        {
            if (SystemZone(name) is { } zone) return FromZone(wall, zone);
            if (defined.TryGetValue(name, out var own) && own.OffsetAt(wall) is { } offset) return new(wall - offset, TimeSpan.Zero);
        }
        return FromZone(wall, install);
    }

    static DateTimeOffset FromZone(DateTime wall, TimeZoneInfo zone)
    {
        TimeSpan offset;
        if (zone.IsInvalidTime(wall)) offset = zone.GetUtcOffset(wall.AddDays(-1));
        else if (zone.IsAmbiguousTime(wall)) offset = zone.GetAmbiguousTimeOffsets(wall).Max();
        else offset = zone.GetUtcOffset(wall);
        return new(wall - offset, TimeSpan.Zero);
    }

    // An instant as the wall clock of the zone `like` is written in, for a rule's UNTIL (always UTC when the start has a zone).
    public DateTime WallOf(DateTimeOffset instant, IcsTime like)
    {
        if (like.Kind == IcsTimeKind.Utc) return instant.UtcDateTime;
        if (like.Kind == IcsTimeKind.Zoned && like.Zone is { } name)
        {
            if (SystemZone(name) is { } zone) return TimeZoneInfo.ConvertTime(instant, zone).DateTime;
            if (defined.TryGetValue(name, out var own) && own.WallOf(instant) is { } wall) return wall;
        }
        return TimeZoneInfo.ConvertTime(instant, install).DateTime;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_+\-][A-Za-z0-9_+\- /.]{0,99}$")] private static partial Regex PlainName();
}

// What all of one feed's defined zones may cost to work out, together: a feed's own definitions count against nothing else, so
// without this a small hostile feed could spend gigabytes and minutes on them, none of it cancellable. Shared by the map's
// rebuild and a star's opening at once.
internal sealed class IcsZoneWork
{
    long left = 10_000_000;
    public int Left => (int)Math.Clamp(Volatile.Read(ref left), 0, int.MaxValue);
    public void Spend(int amount) => Interlocked.Add(ref left, -amount);
}

// One VTIMEZONE: its observances' onsets, as the instants the offset changes. A rule gives them from its DTSTART, so a zone
// that changed its rules (or stopped changing its clocks) is read as it was at each time. A zone that costs more than a real one
// could is no definition at all, and its events say their zone is not known here. A real one changes its clocks twice a year:
// from 1900 to 2300 that is about 800 onsets and 150 000 days of a yearly rule's looking.
internal sealed class IcsDefinedZone
{
    internal sealed record Observance(DateTime Start, TimeSpan From, TimeSpan To, IcsRule? Rule, IReadOnlyList<DateTime> Dates);
    public const int MaxZones = 500;
    public const int MaxOnsets = 2000;
    const int MaxWork = 200_000;
    readonly IReadOnlyList<Observance> observances;
    readonly IcsZoneWork work;
    readonly Lock gate = new();
    List<(DateTime Utc, TimeSpan From, TimeSpan To)>? onsets = [];
    int coveredTo;

    public IcsDefinedZone(IReadOnlyList<Observance> observances, IcsZoneWork work) => (this.observances, this.work) = (observances, work);

    // The onsets up to the end of the year after `year`, oldest first, or null for a zone past what it may cost.
    List<(DateTime Utc, TimeSpan From, TimeSpan To)>? Onsets(int year)
    {
        lock (gate)
        {
            if (onsets is not null && coveredTo <= year) (onsets, coveredTo) = (Made(year + 2), year + 2);
            return onsets;
        }
    }

    List<(DateTime Utc, TimeSpan From, TimeSpan To)>? Made(int upTo)
    {
        var horizon = new DateTime(upTo, 1, 1);
        var found = new List<(DateTime, TimeSpan, TimeSpan)>();
        foreach (var o in observances)
        {
            var budget = new IcsBudget(Math.Min(work.Left, MaxWork));
            var had = budget.Left;
            DateTime? until = null;
            if (o.Rule is { Until: { } text } && IcsTimes.Instant(text) is { } limit) until = limit.UtcDateTime + o.From;
            var walls = o.Rule is { } rule ? rule.Walls(o.Start, until, DateTime.MinValue, horizon, budget) : [o.Start];
            foreach (var wall in walls.Concat(o.Dates))
            {
                found.Add((wall - o.From, o.From, o.To));
                if (found.Count > MaxOnsets) break;
            }
            work.Spend(had - budget.Left);
            if (found.Count > MaxOnsets || budget.Exhausted) { work.Spend(found.Count); return null; }
        }
        work.Spend(found.Count);
        return [.. found.Distinct().OrderBy(f => f.Item1)];
    }

    // Whether this zone can be read as far as `year`.
    public bool Readable(int year) => Onsets(year) is not null;
    internal int OnsetsKept { get { lock (gate) return onsets?.Count ?? 0; } }

    // The offset at a wall-clock time. Each stretch between two onsets covers a span of wall clock: the earliest stretch
    // holding the time is the first of a doubled one, and one in no stretch is in a gap, read with the offset before it.
    // Null for a zone past what it may cost.
    public TimeSpan? OffsetAt(DateTime wall)
    {
        if (Onsets(wall.Year) is not { } list) return null;
        if (list.Count == 0) return TimeSpan.Zero;
        if (wall - list[0].From < list[0].Utc) return list[0].From;
        for (var i = 0; i < list.Count; i++)
        {
            var at = wall - list[i].To;
            if (at >= list[i].Utc && (i + 1 == list.Count || at < list[i + 1].Utc)) return list[i].To;
        }
        foreach (var onset in list)
            if (wall >= onset.Utc + onset.From && wall < onset.Utc + onset.To) return onset.From;
        return list[^1].To;
    }

    public DateTime? WallOf(DateTimeOffset instant)
    {
        var utc = instant.UtcDateTime;
        if (Onsets(utc.Year) is not { } list) return null;
        if (list.Count == 0) return utc;
        var offset = list[0].From;
        foreach (var onset in list)
        {
            if (onset.Utc > utc) break;
            offset = onset.To;
        }
        return utc + offset;
    }

    // An offset as a feed writes it: +0100, -0500, +053000.
    public static TimeSpan? Offset(string? text)
    {
        if (text is null || text.Trim() is not { Length: 5 or 7 } value || value[0] is not ('+' or '-')) return null;
        if (!int.TryParse(value[1..3], CultureInfo.InvariantCulture, out var hours) || !int.TryParse(value[3..5], CultureInfo.InvariantCulture, out var minutes)) return null;
        var seconds = 0;
        if (value.Length == 7 && !int.TryParse(value[5..7], CultureInfo.InvariantCulture, out seconds)) return null;
        if (hours > 23 || minutes > 59 || seconds > 59) return null;
        var size = new TimeSpan(hours, minutes, seconds);
        return value[0] == '-' ? -size : size;
    }
}
