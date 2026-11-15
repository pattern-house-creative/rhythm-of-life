using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Knowledge.Adapters.Nightscout;

// A Nightscout site as a live CGM source (docs/adapters/nightscout.md). Every pollSeconds (default 300) it reads the
// sensor glucose entries (/api/v1/entries/sgv.json) newer than the last reading it stored, less a 3-hour overlap for
// readings an uploader fills in late (14 days back on the first sync), and writes them for adapters.nightscout.owner
// in mg/dL through the sink, one source per local day. A failed poll backs off, doubling up to an hour.
// The provider `nightscout` is a live feed: the site keeps the originals, so nothing is archived, and the 14-day
// glucose view shows it after any archive the same person has (contracts/README.md 3.2).
// The credential is the file secrets/adapters/nightscout_token: an access token (sent as a bearer) or the API secret
// (sent as its SHA-1 in the api-secret header). Never in a URL, never logged. A site readable without one needs none.
public sealed partial class NightscoutAdapter(HttpMessageHandler? handler = null) : IMeasurementSource, ISyncNow
{
    public const string AdapterName = "nightscout", Provider = "nightscout";
    public string Name => AdapterName;
    public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } =
        new Dictionary<string, ProviderPolicy> { [Provider] = new(SourceAuthority.LiveSource, "health") };
    public TimeSpan Interval { get; private set; } = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Overlap = TimeSpan.FromHours(3), Backfill = TimeSpan.FromDays(14), MaxBackoff = TimeSpan.FromHours(1);
    const int Page = 1000, MaxPages = 25, PerCall = 2000;
    const double MmolToMgdl = 18.0182;
    AdapterContext? context;
    Uri? site;
    string owner = "";
    // The person whose readings these are (adapters.nightscout.owner): Sync now shows it to their readers (IAdapter.Owner).
    public string Owner => owner;
    HttpClient? http;
    int failures;
    DateTimeOffset notBefore = DateTimeOffset.MinValue;

    public void Configure(AdapterContext context)
    {
        var settings = context.Settings;
        site = Site(settings.String("url"));
        owner = settings.Owner ?? context.Config.FirstPerson;
        if (owner == RhythmConfig.Household) throw new ConfigException("adapters.nightscout.owner is the person whose readings these are, not household");
        var poll = settings.Int("pollSeconds", 300);
        if (poll is < 60 or > 86400) throw new ConfigException("adapters.nightscout.pollSeconds is 60-86400");
        Interval = TimeSpan.FromSeconds(poll);
        this.context = context;
        // No redirects: a credential header must never follow one to another address.
        http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15), PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        }, disposeHandler: handler is null) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
    }

    // https, or plain http only to a host on the install's own network: a single-label name (a container or machine,
    // http://nightscout:1337), or a loopback or private address. No credentials, query or fragment in the URL.
    public static Uri Site(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ConfigException("adapters.nightscout.url is the site's address, such as https://nightscout.example.invalid/");
        if (uri.Scheme == "http" && !Local(uri))
            throw new ConfigException("adapters.nightscout.url uses https unless the site is on this network (a single-label host or a private address)");
        return new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
    }
    internal static bool Local(Uri uri)
    {
        if (uri.HostNameType == UriHostNameType.Dns) return !uri.Host.Contains('.') || uri.Host == "localhost";
        if (!IPAddress.TryParse(uri.DnsSafeHost, out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6UniqueLocal || ip.IsIPv6LinkLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254;
    }

    public async Task Sync(MeasurementSink sink, CancellationToken ct)
    {
        if (context is null || site is null || http is null) return;
        var now = DateTimeOffset.UtcNow;
        if (now < notBefore) return;
        try
        {
            var entries = await Fetch(await Since(now, ct), now, ct);
            foreach (var call in Calls(entries, owner, context.Config.Timezone, now))
            {
                using var json = JsonDocument.Parse(call.ToJsonString());
                await sink.Write(json.RootElement, ct);
            }
            failures = 0;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            failures++;
            notBefore = now + TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, Interval.Ticks << Math.Min(failures, 10)));
            // A site that does not answer in time is a failed poll, never the engine stopping.
            if (e is OperationCanceledException) throw new TimeoutException("nightscout: the site did not answer in time", e);
            throw;
        }
    }

    // Sync now from the Sources view (SB-P6-09): one poll, as the clock's, asked for at most once per pollSeconds (besides
    // the clock's own poll, so the site may be read twice within one pollSeconds). While a failed poll's back-off holds,
    // Sync asks the site nothing, so the batch reaches nothing: it ends failed, never complete, and the log says why.
    public TimeSpan SyncLimit => Interval;
    public async Task<bool> SyncNow(Guid principal, MeasurementSink sink, CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < notBefore) return false;
        await Sync(sink, ct);
        return true;
    }

    // Where this poll starts: the last stored reading less the overlap, but never more than the backfill ago.
    async Task<DateTimeOffset> Since(DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = context!.Db.Source.CreateCommand("""
            SELECT max(m.observed_at) FROM measurements m JOIN resources r ON r.id=m.resource_id
            WHERE r.provider=@p AND r.subject_id=@s AND m.metric='glucose'
            """);
        cmd.Parameters.AddWithValue("p", Provider);
        cmd.Parameters.AddWithValue("s", owner);
        var floor = now - Backfill;
        return await cmd.ExecuteScalarAsync(ct) is DateTime last && new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Utc)) - Overlap is var from && from > floor
            ? from : floor;
    }

    public sealed record Entry(DateTimeOffset At, double Mgdl, string Quality);

    // The site's sgv entries after `since`, newest page first: Nightscout returns at most `count`, newest first, so
    // each further page asks for what is older than the oldest one seen.
    internal async Task<List<Entry>> Fetch(DateTimeOffset since, DateTimeOffset now, CancellationToken ct)
    {
        var entries = new List<Entry>();
        var after = since.ToUnixTimeMilliseconds();
        var before = now.AddMinutes(5).ToUnixTimeMilliseconds();
        for (var page = 0; page < MaxPages; page++)
        {
            var query = $"api/v1/entries/sgv.json?count={Page}&find%5Bdate%5D%5B%24gt%5D={after}&find%5Bdate%5D%5B%24lt%5D={before}";
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(site!, query));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            Authorize(request);
            using var response = await http!.SendAsync(request, ct);
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new ConfigException($"nightscout: the site redirects (HTTP {(int)response.StatusCode}); set adapters.nightscout.url to the address it redirects to");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ConfigException($"nightscout: the site refused the credential (HTTP {(int)response.StatusCode}); check secrets/adapters/nightscout_token");
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new ConfigException("nightscout: no entries API at adapters.nightscout.url (HTTP 404)");
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (body.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("nightscout: the entries answer is not a list");
            var oldest = long.MaxValue;
            var count = 0;
            foreach (var item in body.RootElement.EnumerateArray())
            {
                count++;
                if (Millis(item) is not { } at) continue;
                oldest = Math.Min(oldest, at);
                if (at > after && at < before && Read(item, at) is { } entry) entries.Add(entry);
            }
            if (count < Page || oldest == long.MaxValue || oldest <= after) break;
            before = oldest;
        }
        return entries;
    }

    // The credential file, read on every poll so a replaced file takes effect without a restart.
    void Authorize(HttpRequestMessage request)
    {
        var path = context!.Settings.SecretFile("token");
        if (path is null || Directory.Exists(path) || !File.Exists(path)) return;
        var secret = SecretFiles.Read(path);
        if (secret.Length == 0) return;
        if (AccessToken().IsMatch(secret)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        else request.Headers.Add("api-secret", Sha1Hex().IsMatch(secret) ? secret.ToLowerInvariant()
            : Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(secret))));
    }

    static long? Millis(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        if (item.TryGetProperty("date", out var date) && date.ValueKind == JsonValueKind.Number && date.TryGetDouble(out var ms) && double.IsFinite(ms))
            return (long)ms;
        return item.TryGetProperty("dateString", out var text) && text.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(text.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            && Offset().IsMatch(text.GetString()!) ? at.ToUnixTimeMilliseconds() : null;
    }

    // One sgv entry in mg/dL. Nightscout stores sgv in mg/dL; an entry that says its units are mmol is converted.
    // Values under 39 are device status codes, not readings; 39 and 401 or more are the sensor's LOW and HIGH bounds.
    public static Entry? Read(JsonElement item, long at)
    {
        if (item.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() != "sgv") return null;
        if (!item.TryGetProperty("sgv", out var sgv) || sgv.ValueKind != JsonValueKind.Number || !sgv.TryGetDouble(out var value) || !double.IsFinite(value))
            return null;
        if (item.TryGetProperty("units", out var units) && units.ValueKind == JsonValueKind.String
            && units.GetString()!.StartsWith("mmol", StringComparison.OrdinalIgnoreCase))
            value = Math.Round(value * MmolToMgdl, 1);
        if (value < 39 || value > 1000) return null;
        return new(DateTimeOffset.FromUnixTimeMilliseconds(at), value, value <= 39 ? "censored_low" : value >= 401 ? "censored_high" : "ok");
    }

    // ingest_measurements arguments: one source per local day of the install's time zone, at most PerCall readings
    // a call, one reading per second (the time is the sample's key). The idempotency key is the call's own hash.
    public static IEnumerable<JsonObject> Calls(IEnumerable<Entry> entries, string subject, string timezone, DateTimeOffset fetched)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timezone);
        var unique = entries.GroupBy(e => Iso(e.At)).Select(g => g.First()).OrderBy(e => e.At);
        foreach (var day in unique.GroupBy(e => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(e.At, zone).DateTime)))
            foreach (var chunk in day.Chunk(PerCall))
            {
                var call = new JsonObject
                {
                    ["provider"] = Provider, ["subject"] = subject,
                    ["source"] = new JsonObject
                    {
                        ["externalId"] = $"nightscout-{day.Key:yyyy-MM-dd}", ["kind"] = "glucose-readings",
                        ["title"] = "Nightscout glucose, " + day.Key.ToString("d MMMM yyyy", CultureInfo.InvariantCulture),
                        ["fetchedAt"] = Iso(fetched), ["sourceUpdatedAt"] = Iso(day.Max(e => e.At)), ["timezone"] = timezone,
                    },
                    ["measurements"] = new JsonArray(chunk.Select(e => (JsonNode)new JsonObject
                    {
                        ["metric"] = "glucose", ["value"] = e.Mgdl, ["unit"] = "mg/dL", ["observedAt"] = Iso(e.At), ["key"] = Iso(e.At), ["quality"] = e.Quality,
                    }).ToArray()),
                };
                call["idempotencyKey"] = $"nightscout:{subject}:{day.Key:yyyy-MM-dd}:{Hashing.Hash(call.ToJsonString())[..16]}";
                yield return call;
            }
    }

    static string Iso(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    // Nightscout's access tokens are a lower-case name, a dash and 16 hex digits; an api-secret may be sent already hashed.
    [GeneratedRegex("^[a-z0-9][a-z0-9_.-]{0,80}-[0-9a-f]{16}$")] private static partial Regex AccessToken();
    [GeneratedRegex("^[0-9a-fA-F]{40}$")] private static partial Regex Sha1Hex();
    [GeneratedRegex("(Z|[+-][0-9]{2}:?[0-9]{2})$")] private static partial Regex Offset();
}
