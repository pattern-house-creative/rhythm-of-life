using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Knowledge.Relationships;

namespace Knowledge.Adapters.Calendar;

// Why a feed could not be read, in words of our own: never a link, an address or a server's own words, and never chaining the
// exception it came from, whose message might name the address. An IOException, so the map's pass and the reader take it as
// any source being down.
internal sealed class CalendarFeedFailure(string message) : IOException(message);

// A calendar over the owner's own private ICS links, as a MemorySource read by reference (docs/adapters/calendar.md, SB-P7-05,
// D-159, adapted from Second Brain's calendar connector, src/integrations/calendar.ts; NOTICE). Off by default and unconnected:
// nothing reads a link until a person has entered theirs with `rhythm connect calendar`.
//
// On each map rebuild, at most once per pollSeconds, it reads each link (secrets/adapters/calendar_link, and optionally
// calendar_link_2 and calendar_link_3) with one GET, and lists every occurrence that overlaps the window (daysBack and
// daysAhead) as a star: the event's summary as its title, when and where as its overview, owned by adapters.calendar.owner. The
// words of its description are not kept: opening a star reads them from the feed, for that view only. No writes, ever, and no
// similarity vector: the occurrences of a series share their words, so they would all read as copies of each other.
//
// A listing is complete only when every link read whole and every event in it could be read, so a read that fails, or a feed
// cut off part way, removes nothing; what a pass could not read is said in the listing's note, by kind and count.
//
// The link is a secret, as a password is (D-038): a file, read on every read so a new one takes effect at once. It is sent
// only as the address of the GET, is in no setting, star, note or message, and a feed repeating it has it taken out. Every
// failure's message is our own (CalendarFeedFailure).
public sealed partial class CalendarAdapter(HttpMessageHandler? handler = null, Func<DateTimeOffset>? clock = null) : IMemorySource, IAdapterConnect
{
    public const string AdapterName = "calendar", Provider = "calendar";
    public const int MaxFeedBytes = 16 * 1024 * 1024, MaxPerEvent = IcsFeed.MaxPerEvent, MaxRedirects = 3;
    public static readonly string[] LinkPurposes = ["link", "link_2", "link_3"];
    internal const int MaxWords = 16_000;
    const int MaxEvents = 1000, ExpandBudget = 5_000_000, MaxTitle = 200, MaxPlace = 300, MaxUrl = 2000;
    const string Removed = "[link removed]", Untitled = "Untitled event", Unreadable = "the calendar could not be read";
    static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(6), FirstWait = TimeSpan.FromMinutes(15), Early = TimeSpan.FromMinutes(1);
    static readonly HttpRequestOptionsKey<bool> Redirected = new("calendar.redirected");
    // One link's whole read, redirects and all, so a feed that hangs costs the map's rebuild this and no more.
    internal TimeSpan ReadBudget { get; set; } = TimeSpan.FromSeconds(20);
    // What reads a feed's text and how a name finds its addresses; a test gives its own, and nothing reaches the network.
    internal Func<string, TimeZoneInfo, IcsFeed> Reader { get; set; } = IcsFeed.Parse;
    internal Func<string, CancellationToken, Task<IPAddress[]>> Addresses { get; set; } = Dns.GetHostAddressesAsync;
    static readonly RecordGroup Group = new("calendar:events", "Calendar");
    static readonly string[] Ordinals = ["first", "second", "third"];
    // What someone might use to name a link in the settings file, where it would be kept as plain text.
    static readonly string[] NotSettings = ["url", "link", "links", "feed", "ics"];
    // A link in the settings file, whether the calendar is on or off: RhythmConfig refuses it at start either way, so one pasted
    // under a section left off is not kept there in plain text with nothing said. Names the key, never the value.
    public static string? LinkRefusal(AdapterSettings settings) =>
        NotSettings.FirstOrDefault(key => settings.String(key) is not null) is { } key
            ? $"adapters.calendar.{key}: a calendar's link is a secret; it goes in secrets/adapters/calendar_link (./rhythm connect calendar), never in rhythm.yaml"
            : null;

    public string Name => AdapterName;
    public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } =
        new Dictionary<string, ProviderPolicy> { [Provider] = new(SourceAuthority.ReferenceOnly) };

    AdapterSettings? settings;
    HttpClient? http;
    TimeZoneInfo zone = TimeZoneInfo.Utc;
    string zoneName = "UTC", owner = RhythmConfig.Household;
    TimeSpan back = TimeSpan.FromDays(7), ahead = TimeSpan.FromDays(30), poll = TimeSpan.FromSeconds(900);
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly Slot[] slots = [new(), new(), new()];

    // Whose calendar this is (adapters.calendar.owner): a person, or the household for one the family shares.
    public string Owner => owner;
    string Scope => owner == RhythmConfig.Household ? "shared" : "private";

    public void Configure(AdapterContext context)
    {
        settings = context.Settings;
        if (LinkRefusal(settings) is { } link) throw new ConfigException(link);
        owner = settings.Owner ?? context.Config.FirstPerson;
        if (!context.Config.IsSubject(owner)) throw new ConfigException("adapters.calendar.owner is a person id from people, or household");
        var (daysBack, daysAhead, pollSeconds) = (settings.Int("daysBack", 7), settings.Int("daysAhead", 30), settings.Int("pollSeconds", 900));
        if (daysBack is < 0 or > 90) throw new ConfigException("adapters.calendar.daysBack is 0-90");
        if (daysAhead is < 1 or > 365) throw new ConfigException("adapters.calendar.daysAhead is 1-365");
        if (pollSeconds is < 300 or > 86400) throw new ConfigException("adapters.calendar.pollSeconds is 300-86400");
        (back, ahead, poll) = (TimeSpan.FromDays(daysBack), TimeSpan.FromDays(daysAhead), TimeSpan.FromSeconds(pollSeconds));
        (zone, zoneName) = (context.Config.Zone, context.Config.Timezone);
        http = AdapterHttp.Client(handler, TimeSpan.FromSeconds(30), (at, ct) => CalendarAddresses.Connect(at.DnsEndPoint.Host, at.DnsEndPoint.Port,
            at.InitialRequestMessage.Options.TryGetValue(Redirected, out var redirected) && redirected, Addresses, ct));
    }

    // ---- a link -------------------------------------------------------------------------------------------------------

    // https or webcal (https by another name), or plain http only to the install's own network, as Nightscout's address, since
    // plain http sends the link as it stands. No user name or password in it. The fragment is no part of what is asked for.
    // Null for anything else.
    public static Uri? Link(string? given)
    {
        var text = given?.Trim() ?? "";
        foreach (var form in new[] { "webcal://", "webcals://" })
            if (text.StartsWith(form, StringComparison.OrdinalIgnoreCase)) text = "https://" + text[form.Length..];
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0 || uri.Host.Length == 0) return null;
        if (uri.Scheme == "http" && !Nightscout.NightscoutAdapter.Local(uri)) return null;
        return new Uri(uri.GetLeftPart(UriPartial.Query));
    }

    // The link file's words, or null with no file: read on every read, so a new link takes effect at once.
    static string? Secret(string? path)
    {
        if (path is null || Directory.Exists(path) || !File.Exists(path)) return null;
        try { return SecretFiles.Read(path) is { Length: > 0 } value ? value : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // `rhythm connect calendar`: the prompts have written the link's file; this checks its form before anything turns on. It
    // does not ask the link: the command's container has no way out to the internet, and the engine reads it at the next rebuild.
    public Task<int> Connect(ConnectContext context, CancellationToken ct)
    {
        var given = LinkPurposes.Select(p => Secret(Path.Combine(context.SecretsDir, $"{AdapterName}_{p}"))).OfType<string>().ToArray();
        if (given.Length == 0)
        {
            context.Output.WriteLine("No link was entered. This adapter needs your calendar's own private ICS link (docs/adapters/calendar.md).");
            return Task.FromResult(1);
        }
        if (given.Any(g => Link(g) is null))
        {
            context.Output.WriteLine("That is not an https or webcal link. Copy your calendar's private ICS address again (docs/adapters/calendar.md), and run this again, not keeping that one; nothing of what you entered is shown here.");
            return Task.FromResult(1);
        }
        context.Output.WriteLine($"{given.Length} calendar link{(given.Length == 1 ? "" : "s")} kept in secrets/adapters/. They are read at the next map rebuild; ./rhythm logs engine says if one cannot be.");
        return Task.FromResult(0);
    }

    // ---- reading a feed ------------------------------------------------------------------------------------------------

    // One link's state: its last whole read, and when it may be asked again, kept for one reader at a time (the map's rebuild
    // and a star's opening may both want it). Nothing here outlives the engine.
    sealed class Slot
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public string? Given;
        public IcsFeed? Feed;
        public DateTimeOffset NextRead, NotBefore;
        public string? ETag, Modified;
        public int Failures;
        public CalendarFeedFailure? Failure;
        public void Reset(string given) { Given = given; Feed = null; NextRead = NotBefore = default; ETag = Modified = null; Failures = 0; Failure = null; }
    }

    IReadOnlyList<(int Index, string Given)> Links()
    {
        var links = new List<(int, string)>();
        for (var i = 0; i < LinkPurposes.Length; i++)
            if (Secret(settings?.SecretFile(LinkPurposes[i])) is { } given) links.Add((i, given));
        return links;
    }

    // The link's feed: the last read while it is within pollSeconds, else a new read. A failure waits longer each time, from
    // pollSeconds or a quarter of an hour, the shorter, doubling up to six hours, during which the link is not asked and the same
    // failure stands. The first wait is shorter than graph.freshnessSeconds' default (35 minutes), however long pollSeconds is,
    // so one failed read among good ones does not take its stars off the map.
    async Task<IcsFeed> Read(int index, string given, CancellationToken ct)
    {
        var slot = slots[index];
        await slot.Gate.WaitAsync(ct);
        try
        {
            if (slot.Given != given) slot.Reset(given);
            var at = now();
            if (slot.Failure is { } failed && at < slot.NotBefore) throw failed;
            if (slot.Failure is null && slot.Feed is { } fresh && at < slot.NextRead) return fresh;
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(ReadBudget);
                var link = Link(given) ?? throw new CalendarFeedFailure("not an https or webcal link");
                var feed = await Fetch(slot, link, budget.Token);
                (slot.Feed, slot.NextRead, slot.Failures, slot.Failure) = (feed, at + poll - Early, 0, null);
                return feed;
            }
            // A timeout of its own, an HTTP timeout or the budget above, never the engine stopping.
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Failed(slot, at, new("it did not answer in time")); }
            catch (CalendarFeedFailure failure) { throw Failed(slot, at, failure); }
        }
        finally { slot.Gate.Release(); }
    }

    CalendarFeedFailure Failed(Slot slot, DateTimeOffset at, CalendarFeedFailure failure)
    {
        slot.Failures++;
        slot.Failure = failure;
        var first = Math.Min(poll.Ticks, FirstWait.Ticks);
        slot.NotBefore = at + TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, first << Math.Min(slot.Failures - 1, 10))) - Early;
        return failure;
    }

    async Task<IcsFeed> Fetch(Slot slot, Uri link, CancellationToken ct)
    {
        var first = await Answer(slot, link, ct);
        if (first.Feed is { } kept) return kept;
        try { return Parsed(slot, first); }
        // iCloud's published calendars answer on one of two hosts, and either may say nothing useful: one more try.
        catch (CalendarFeedFailure) when (OtherHost(link) is not null) { return Parsed(slot, await Answer(slot, OtherHost(link)!, ct)); }
    }

    IcsFeed Parsed(Slot slot, Body body)
    {
        if (body.Feed is { } kept) return kept;
        if (body.Failure is { } failure) throw failure;
        IcsFeed feed;
        try { feed = Reader(body.Text, zone); }
        catch (IcsInvalid e) { throw new CalendarFeedFailure(e.Message); }
        // A mistake of the reader's costs this calendar, never the map's whole rebuild, and nothing of what it said is repeated.
        catch (Exception e) when (e is not OperationCanceledException) { throw new CalendarFeedFailure(Unreadable); }
        (slot.ETag, slot.Modified) = (body.ETag, body.Modified);
        return feed;
    }

    // A feed's occurrences in a window: as with reading it, a mistake here costs this calendar and no more.
    static IcsWindow Window(IcsFeed feed, DateTimeOffset from, DateTimeOffset to, IcsBudget budget)
    {
        try { return feed.Within(from, to, budget); }
        catch (Exception e) when (e is not OperationCanceledException) { throw new CalendarFeedFailure(Unreadable); }
    }

    // pNN-caldav.icloud.com's other name, pNN-calendars.icloud.com; null for any other host.
    static Uri? OtherHost(Uri link) => ICloudHost().Match(link.Host) is { Success: true } host
        ? new UriBuilder(link) { Host = $"p{host.Groups[1].Value}-calendars.icloud.com" }.Uri : null;

    sealed record Body(string Text, string? ETag = null, string? Modified = null, IcsFeed? Feed = null, CalendarFeedFailure? Failure = null);

    // One GET, following a few redirects, each a request of its own with nothing carried over; never down to plain http or into
    // the install's own network (CalendarAddresses: by the name asked for here, and by the address it turns out to have when the
    // connection is made), so a feed's server cannot turn the engine on it. Every failure is said in our own words, and comes
    // back rather than being thrown, as another host may mend it (Fetch).
    async Task<Body> Answer(Slot slot, Uri link, CancellationToken ct)
    {
        const string NotFollowed = "it redirects somewhere it may not be followed";
        static Body Failed(string why) => new("", Failure: new(why));
        var target = link;
        try
        {
            for (var hop = 0; ; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, target);
                request.Headers.Accept.ParseAdd("text/calendar");
                request.Headers.Accept.ParseAdd("text/plain;q=0.9");
                request.Headers.Accept.ParseAdd("*/*;q=0.5");
                // A calendar client's name: Second Brain found iCloud's published calendars particular about who asks.
                request.Headers.TryAddWithoutValidation("User-Agent", "CalendarAgent/1.0 RhythmOfLife");
                if (hop > 0) request.Options.Set(Redirected, true);
                if (hop == 0 && slot.Feed is not null)
                {
                    if (slot.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag)) request.Headers.IfNoneMatch.Add(tag);
                    if (slot.Modified is { } modified && DateTimeOffset.TryParse(modified, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var since))
                        request.Headers.IfModifiedSince = since;
                }
                using var response = await http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                var status = (int)response.StatusCode;
                if (response.StatusCode == HttpStatusCode.NotModified && slot.Feed is { } same) return new("", Feed: same);
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    if (hop == MaxRedirects || response.Headers.Location is not { } location) return Failed("it redirects too many times");
                    var next = location.IsAbsoluteUri ? location : new Uri(target, location);
                    if (next.Scheme != "https" || next.UserInfo.Length > 0 || !CalendarAddresses.PublicName(next)) return Failed(NotFollowed);
                    target = next;
                    continue;
                }
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return Failed("the calendar's server refused the link; it may have been reset");
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return Failed("the calendar's server no longer knows the link; it may have been reset");
                if (status == 429) return Failed("the calendar's server asks to be asked less often");
                if (status >= 500) return Failed("a server error at the calendar's end");
                if (!response.IsSuccessStatusCode) return Failed("the calendar's server would not answer");
                var bytes = await AdapterHttp.Read(response, MaxFeedBytes, ct);
                return new(Encoding.UTF8.GetString(bytes), response.Headers.ETag?.ToString(), response.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture));
            }
        }
        catch (ResponseTooLargeException) { return Failed("the calendar is too large"); }
        catch (HttpRequestException e) when (e.InnerException is CalendarAddressRefused) { return Failed(NotFollowed); }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException or UriFormatException) { return Failed("it could not be reached"); }
    }

    // ---- the map -------------------------------------------------------------------------------------------------------

    // What every link holds now, and what could not be read.
    async Task<(List<IcsOccurrence> Occurrences, List<string> Notes, bool Complete)> Occurrences(DateTimeOffset at, IReadOnlyList<(int Index, string Given)> links, CancellationToken ct)
    {
        var notes = new List<string>();
        var found = new List<IcsOccurrence>();
        var complete = links.Count > 0;
        if (links.Count == 0)
            notes.Add("no link yet: this adapter is on but waits for your own private ICS link in secrets/adapters/calendar_link (./rhythm connect calendar)");
        var (unreadable, capped, unknownZones) = (0, false, 0);
        var budget = new IcsBudget(ExpandBudget);
        foreach (var (index, given) in links)
        {
            try
            {
                var feed = await Read(index, given, ct);
                var window = Window(feed, at - back, at + ahead, budget);
                unreadable += feed.Unreadable + window.Unreadable;
                capped |= window.Capped;
                found.AddRange(window.Occurrences);
                unknownZones += window.Occurrences.Where(o => o.ZoneUnknown).Select(o => o.Event.Uid).Distinct(StringComparer.Ordinal).Count();
            }
            catch (CalendarFeedFailure failure)
            {
                complete = false;
                notes.Add((links.Count > 1 || index > 0 ? $"the {Ordinals[index]} calendar: " : "") + failure.Message);
            }
        }
        if (unreadable > 0)
        {
            complete = false;
            notes.Add($"{unreadable} event{(unreadable == 1 ? "" : "s")} could not be read");
        }
        if (capped)
        {
            complete = false;
            notes.Add($"a series past {MaxPerEvent} occurrences in the window was cut");
        }
        if (unknownZones > 0) notes.Add($"{unknownZones} event{(unknownZones == 1 ? "" : "s")} in a time zone not known here");
        return (found, notes, complete);
    }

    public async Task<SourceListing> List(SourcePass pass, CancellationToken ct)
    {
        var at = now();
        var observed = DateTimeOffset.UtcNow;
        var links = Links();
        var (occurrences, notes, complete) = await Occurrences(at, links, ct);
        // One event on two calendars is one star. What is soonest comes first when not all can be kept; what has passed last.
        var unique = occurrences.GroupBy(o => o.Id, StringComparer.Ordinal).Select(g => g.First()).ToArray();
        var room = Math.Min(MaxEvents, pass.MemoriesOf(owner));
        var kept = unique.OrderBy(o => o.End > at ? 0 : 1).ThenBy(o => o.End > at ? o.Start.Ticks : -o.Start.Ticks).Take(room).ToArray();
        if (kept.Length < unique.Length)
        {
            complete = false;
            var over = unique.Length - kept.Length;
            notes.Add($"{over} event{(over == 1 ? "" : "s")} past what the map holds left out");
        }
        var records = kept.OrderBy(o => o.Start).ThenBy(o => o.Id, StringComparer.Ordinal).Select(o => Star(o, observed, links)).ToArray();
        return new(records, complete) { Note = notes.Count == 0 ? null : "calendar: " + string.Join("; ", notes) };
    }

    static string Title(IcsEvent e, IReadOnlyList<(int Index, string Given)> links) => Shown(e.Summary, MaxTitle, false, links) is { Length: > 0 } words ? words : Untitled;

    DiscoveryRecord Star(IcsOccurrence one, DateTimeOffset observed, IReadOnlyList<(int Index, string Given)> links)
    {
        var e = one.Event;
        var title = Title(e, links);
        var place = Shown(e.Location, MaxPlace, false, links);
        var when = When(one);
        var overview = when + (place.Length > 0 ? "; at " + place : "") + (e.Tentative ? "; tentative" : "") + (one.ZoneUnknown ? "; its time zone is not known here" : "");
        // A changed time, place or word of the event is a new revision; its description counts, though it is not kept.
        var revision = "observed-sha256:" + Hashing.Hash(JsonSerializer.Serialize(new { title, when, place, e.Status, words = Scrub(e.Description, links), url = Scrub(e.Url, links), one.ZoneUnknown }));
        return new(new(owner, Provider, Provider, "event-" + one.Id), Scope, "memory", title, null, revision, Past(e.Modified, observed), observed, [],
            CreatedAt: Past(e.Created, observed), ContentRevision: revision, Groups: [Group], Overview: overview);
    }

    static DateTimeOffset? Past(DateTimeOffset? at, DateTimeOffset observed) => at <= observed ? at : null;

    // Opening a star reads its words from the feed, for this view only, nothing kept. Null when the feed no longer has it.
    public async Task<string?> ReadText(DiscoveryRecord record, CancellationToken ct)
    {
        if (record.Key.Provider != Provider) return null;
        var at = now();
        var links = Links();
        CalendarFeedFailure? failed = null;
        foreach (var (index, given) in links)
        {
            try
            {
                var feed = await Read(index, given, ct);
                // A day past the window on either side: a star listed at the last rebuild may just have left it.
                var one = Window(feed, at - back - TimeSpan.FromDays(1), at + ahead + TimeSpan.FromDays(1), new IcsBudget(ExpandBudget)).Occurrences
                    .FirstOrDefault(o => "event-" + o.Id == record.Key.ExternalId);
                if (one is not null) return Text(one, links);
            }
            catch (CalendarFeedFailure failure) { failed = failure; }
        }
        return failed is null ? null : throw failed;
    }

    string Text(IcsOccurrence one, IReadOnlyList<(int Index, string Given)> links)
    {
        var e = one.Event;
        var lines = new List<string> { Title(e, links), When(one) };
        if (Shown(e.Location, MaxPlace, false, links) is { Length: > 0 } place) lines.Add("At: " + place);
        if (e.Tentative) lines.Add("Tentative");
        var parts = new[] { Shown(e.Description, MaxWords, true, links), Shown(e.Url, MaxUrl, false, links) }.Where(p => p.Length > 0);
        return string.Join('\n', lines) + string.Concat(parts.Select(p => "\n\n" + p));
    }

    // When an occurrence is, on the install's clock: a date people read in a form a search finds (2026-03-24).
    string When(IcsOccurrence one)
    {
        static string Day(DateTime d) => d.ToString("ddd yyyy-MM-dd", CultureInfo.InvariantCulture);
        static string Clock(DateTime d) => d.ToString("HH:mm", CultureInfo.InvariantCulture);
        var (start, end) = (TimeZoneInfo.ConvertTime(one.Start, zone).DateTime, TimeZoneInfo.ConvertTime(one.End, zone).DateTime);
        if (one.AllDay) return (one.Days > 1 ? $"{Day(start)} to {Day(start.Date.AddDays(one.Days - 1))}" : Day(start)) + ", all day";
        var time = $"{Day(start)} {Clock(start)}";
        if (end <= start) return $"{time} {zoneName}";
        return (end.Date == start.Date ? $"{time} to {Clock(end)}" : $"{time} to {Day(end)} {Clock(end)}") + $" {zoneName}";
    }

    // A feed repeating its own link has it taken out, in each form it may take.
    static string Scrub(string text, IReadOnlyList<(int Index, string Given)> links)
    {
        if (text.Length == 0) return text;
        foreach (var (_, given) in links)
        {
            var link = Link(given);
            string?[] forms = [given, link?.AbsoluteUri, link?.PathAndQuery is { Length: >= 8 } path ? path : null];
            foreach (var form in forms.OfType<string>())
                text = text.Replace(form, Removed, StringComparison.OrdinalIgnoreCase).Replace(Uri.EscapeDataString(form), Removed, StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }

    // Words fit to show: no control or hidden characters (CalendarText), which would only serve to hide words from a reader; the
    // link taken out after them, as they could hide it too; then cut to a length, so a cut never leaves part of it.
    static string Shown(string text, int most, bool lines, IReadOnlyList<(int Index, string Given)> links)
    {
        var visible = CalendarText.Visible(text);
        var words = Scrub(lines ? visible : visible.Replace('\n', ' ').Replace('\t', ' '), links);
        if (words.Length > most) words = words[..(char.IsHighSurrogate(words[most - 1]) ? most - 1 : most)];
        words = words.Trim();
        return lines ? words : MultipleSpaces().Replace(words, " ");
    }

    [GeneratedRegex(@"^p(\d+)-caldav\.icloud\.com$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex ICloudHost();
    [GeneratedRegex(" {2,}")] private static partial Regex MultipleSpaces();
}
