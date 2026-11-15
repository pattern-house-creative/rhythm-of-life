using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Adapters;
using Knowledge.Adapters.Nightscout;
using Knowledge.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Knowledge.Tests;

// A fictional Nightscout site in this process: sgv entries newest first, at most `count`, filtered by
// find[date][$gt] and find[date][$lt], readable with one access token (as a bearer) or the API secret's SHA-1.
public sealed class FakeNightscout : IAsyncDisposable
{
    // Built at runtime, so no file holds a credential-shaped value.
    public static readonly string Token = string.Join("-", "rhythm", "0123456789abcdef"), Secret = string.Join("-", "fictional", "api", "secret");
    readonly WebApplication app;
    public List<(long? After, long? Before, int Count, bool Authorized)> Requests { get; } = [];
    public List<JsonObject> Entries { get; } = [];
    public string Url { get; private set; } = "";

    FakeNightscout()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        app = builder.Build();
        app.MapGet("/api/v1/entries/sgv.json", (HttpContext context) =>
        {
            var q = context.Request.Query;
            long? Number(string key) => long.TryParse(q[key], out var n) ? n : null;
            var authorized = context.Request.Headers.Authorization == "Bearer " + Token
                || context.Request.Headers["api-secret"] == Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(Secret)));
            var (after, before, count) = (Number("find[date][$gt]"), Number("find[date][$lt]"), (int)(Number("count") ?? 10));
            lock (Requests) Requests.Add((after, before, count, authorized));
            if (!authorized) return Results.StatusCode(401);
            var page = Entries.Where(e => (after is null || (long)e["date"]! > after) && (before is null || (long)e["date"]! < before))
                .OrderByDescending(e => (long)e["date"]!).Take(count).ToArray();
            return Results.Text(new JsonArray(page.Select(e => (JsonNode)e.DeepClone()).ToArray()).ToJsonString(), "application/json");
        });
    }
    public static async Task<FakeNightscout> Start()
    {
        var fake = new FakeNightscout();
        await fake.app.StartAsync();
        fake.Url = fake.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/";
        return fake;
    }
    // Fictional readings every 5 minutes from `from` to `until`, with a gentle daily curve.
    public void Fill(DateTimeOffset from, DateTimeOffset until)
    {
        for (var at = from; at <= until; at = at.AddMinutes(5))
            Entries.Add(new JsonObject { ["_id"] = Guid.NewGuid().ToString("N"), ["type"] = "sgv", ["date"] = at.ToUnixTimeMilliseconds(),
                ["sgv"] = 110 + (int)(30 * Math.Sin(at.TimeOfDay.TotalHours / 24 * 2 * Math.PI)), ["direction"] = "Flat", ["device"] = "demo-uploader" });
    }
    public async ValueTask DisposeAsync() => await app.DisposeAsync();
}

// The adapter's secret file is named by a process-wide variable, so these run one at a time.
[CollectionDefinition("nightscout", DisableParallelization = true)] public sealed class NightscoutCollection;

[Collection("nightscout")]
public sealed class NightscoutTests
{
    [Theory]
    [InlineData("https://nightscout.example.invalid", true)]
    [InlineData("https://nightscout.example.invalid/ns/", true)]
    [InlineData("http://nightscout:1337/", true)]
    [InlineData("http://192.168.1.20/", true)]
    [InlineData("http://127.0.0.1:5000", true)]
    [InlineData("http://nightscout.example.invalid/", false)]
    [InlineData("http://8.8.8.8/", false)]
    [InlineData("https://user" + ":pass@nightscout.example.invalid/", false)]
    [InlineData("https://nightscout.example.invalid/?token=x", false)]
    [InlineData("ftp://nightscout.example.invalid/", false)]
    [InlineData(null, false)]
    public void The_site_is_https_or_a_plain_address_on_this_network(string? url, bool accepted)
    {
        if (accepted) Assert.EndsWith("/", NightscoutAdapter.Site(url).ToString());
        else Assert.Throws<ConfigException>(() => NightscoutAdapter.Site(url));
    }

    [Fact] public void Entries_become_mg_dl_readings_with_the_sensor_bounds()
    {
        static NightscoutAdapter.Entry? Read(string json) => NightscoutAdapter.Read(JsonDocument.Parse(json).RootElement, 1793188500000);
        Assert.Equal(112, Read("""{"type":"sgv","sgv":112}""")!.Mgdl);
        Assert.Equal(111.7, Read("""{"type":"sgv","sgv":6.2,"units":"mmol"}""")!.Mgdl);
        Assert.Equal("censored_low", Read("""{"sgv":39}""")!.Quality);
        Assert.Equal("censored_high", Read("""{"sgv":401}""")!.Quality);
        Assert.Equal("ok", Read("""{"sgv":250}""")!.Quality);
        Assert.Null(Read("""{"sgv":5}"""));           // a device status code, not a reading
        Assert.Null(Read("""{"type":"mbg","sgv":120}"""));
        Assert.Null(Read("""{"sgv":"120"}"""));
    }

    [Fact] public void One_source_per_local_day_with_a_key_from_the_call_itself()
    {
        var fetched = DateTimeOffset.Parse("2026-10-28T12:00:05Z");
        NightscoutAdapter.Entry At(string time, double value) => new(DateTimeOffset.Parse(time), value, "ok");
        // Days are the install's local days; London is on GMT again by 26 October.
        var entries = new[] { At("2026-10-26T23:55:00Z", 100), At("2026-10-27T00:05:00Z", 101), At("2026-10-27T00:05:00.400Z", 102), At("2026-10-28T11:55:00Z", 112) };
        var calls = NightscoutAdapter.Calls(entries, "alex", "Europe/London", fetched).ToArray();
        Assert.Equal(["nightscout-2026-10-26", "nightscout-2026-10-27", "nightscout-2026-10-28"], calls.Select(c => (string)c["source"]!["externalId"]!).ToArray());
        // Two entries in the same second are one reading: the time is the sample's key.
        Assert.Single(calls[1]["measurements"]!.AsArray());
        Assert.Equal("Nightscout glucose, 28 October 2026", (string?)calls[2]["source"]!["title"]);
        Assert.All(calls, c => Assert.Equal("mg/dL", (string?)c["measurements"]![0]!["unit"]));
        var again = NightscoutAdapter.Calls(entries, "alex", "Europe/London", fetched).ToArray();
        Assert.Equal(calls.Select(c => (string)c["idempotencyKey"]!).ToArray(), again.Select(c => (string)c["idempotencyKey"]!).ToArray());
        Assert.All(calls, c => Assert.Matches("^[A-Za-z0-9._:-]{8,200}$", (string)c["idempotencyKey"]!));
        var later = NightscoutAdapter.Calls(entries, "alex", "Europe/London", fetched.AddMinutes(5)).ToArray();
        Assert.NotEqual((string)calls[0]["idempotencyKey"]!, (string)later[0]["idempotencyKey"]!);
    }

    [Fact] public async Task It_pages_back_from_the_newest_entry_with_the_credential_in_a_header()
    {
        await using var site = await FakeNightscout.Start();
        var now = DateTimeOffset.UtcNow;
        site.Fill(now.AddDays(-5), now);
        var adapter = await Configured(site.Url, FakeNightscout.Token);
        var entries = await adapter.Fetch(now.AddDays(-5).AddMinutes(-1), now, default);
        Assert.Equal(site.Entries.Count, entries.Count);
        Assert.True(site.Requests.Count >= 2);  // 5 days of 5-minute readings are more than one page of 1000
        Assert.All(site.Requests, r => Assert.True(r.Authorized));
        Assert.All(site.Requests.Skip(1), r => Assert.NotNull(r.Before));
        // The API secret works too, sent as its SHA-1; a wrong credential is a settings problem named in the log.
        Assert.NotEmpty(await (await Configured(site.Url, FakeNightscout.Secret)).Fetch(now.AddHours(-1), now, default));
        var refused = await Assert.ThrowsAsync<ConfigException>(async () => await (await Configured(site.Url, "wrong-0000000000000000")).Fetch(now.AddHours(-1), now, default));
        Assert.Contains("HTTP 401", refused.Message);
        Assert.DoesNotContain("wrong", refused.Message);
    }

    static string? tokenFile;
    // An adapter configured as rhythm.yaml would, with the credential in a temporary secret file.
    internal static Task<NightscoutAdapter> Configured(string url, string? token, Database? db = null, HttpMessageHandler? handler = null)
    {
        var config = TestSettings.Use();
        tokenFile ??= Path.Combine(Path.GetTempPath(), "rhythm-test-" + Guid.NewGuid().ToString("N"), "nightscout_token");
        Directory.CreateDirectory(Path.GetDirectoryName(tokenFile)!);
        if (token is null) File.Delete(tokenFile); else File.WriteAllText(tokenFile, token + "\n");
        Environment.SetEnvironmentVariable("RHYTHM_NIGHTSCOUT_TOKEN_FILE", tokenFile);
        var adapter = new NightscoutAdapter(handler);
        var settings = new AdapterSettings("nightscout", new Dictionary<string, object?> { ["enabled"] = "true", ["url"] = url, ["owner"] = "alex", ["pollSeconds"] = "60" });
        adapter.Configure(new(settings, config, db ?? new Database(Npgsql.NpgsqlDataSource.Create("Host=example.invalid;Database=none")),
            new Archive(Path.GetTempPath())));
        return Task.FromResult(adapter);
    }
}

[Collection("nightscout")]
public sealed class GlucoseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    static readonly Dictionary<string, ProviderPolicy> Policies = new()
    {
        ["test-csv"] = new(SourceAuthority.AuthorizedArchive, "health"),
        ["test-live"] = new(SourceAuthority.LiveSource, "health"),
        ["nightscout"] = new(SourceAuthority.LiveSource, "health"),
    };
    Database Db => new(fixture.Source, new ProviderPolicies(Policies));
    static string Iso(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    // Readings every 5 minutes for one provider, written through the ingest rules in calls of at most 2000.
    async Task Write(string provider, string subject, DateTimeOffset from, DateTimeOffset until, Func<DateTimeOffset, double> value)
    {
        var ingest = new Ingest(Db, new Archive(Path.Combine(Path.GetTempPath(), "rhythm-test-archive")), TestSettings.Use());
        var times = new List<DateTimeOffset>();
        for (var at = from; at <= until; at = at.AddMinutes(5)) times.Add(at);
        foreach (var chunk in times.Chunk(2000))
        {
            var args = new JsonObject
            {
                ["provider"] = provider, ["subject"] = subject, ["idempotencyKey"] = $"{provider}:{subject}:{Guid.NewGuid():N}",
                ["source"] = new JsonObject { ["externalId"] = $"{provider}-{Iso(chunk[0])}", ["kind"] = "glucose-readings", ["title"] = "Fictional readings",
                    ["fetchedAt"] = Iso(DateTimeOffset.UtcNow), ["timezone"] = "Europe/London" },
                ["measurements"] = new JsonArray(chunk.Select(at => (JsonNode)new JsonObject
                {
                    ["metric"] = "glucose", ["value"] = value(at), ["unit"] = "mg/dL", ["observedAt"] = Iso(at), ["key"] = Iso(at)
                }).ToArray()),
            };
            using var json = JsonDocument.Parse(args.ToJsonString());
            await ingest.Measurements("test", json.RootElement, default, [provider]);
        }
    }

    [PostgresFact] public async Task Fourteen_days_join_the_archive_and_then_the_live_feed_by_policy()
    {
        var config = TestSettings.Use();
        var health = new AppHealth(Db, config);
        var app = (await new Principals(fixture.Source).SyncApp(Principals.NewToken(), default))!.Value;
        var now = DateTimeOffset.UtcNow;
        await Write("test-csv", "sam", now.AddDays(-15), now.AddMinutes(-30), at => 100 + at.Minute);
        // An archive alone: no current reading, and a 14-day history that is all archive.
        Assert.Empty((await health.Latest(app, "sam", default))["rows"]!.AsArray());
        var archived = await health.Recent(app, "sam", true, default);
        Schemas.Valid(Schemas.Folder("mcp"), "app_recent_glucose.output.schema.json", archived);
        Assert.Equal("archive", (string?)Assert.Single(archived["sources"]!.AsArray())!["kind"]);
        Assert.Null(archived["latest"]);

        await Write("test-live", "sam", now.AddHours(-6), now.AddMinutes(-2), at => 140);
        var latest = await health.Latest(app, "sam", default);
        Schemas.Valid(Schemas.Folder("mcp"), "app_latest_glucose.output.schema.json", latest);
        Assert.Equal("test-live", (string?)Assert.Single(latest["rows"]!.AsArray())!["provider"]);

        var fortnight = await health.Recent(app, "sam", true, default);
        Schemas.Valid(Schemas.Folder("mcp"), "app_recent_glucose.output.schema.json", fortnight);
        var sources = fortnight["sources"]!.AsArray();
        Assert.Equal(["archive", "live"], sources.Select(s => (string)s!["kind"]!).ToArray());
        Assert.Equal(["test-csv", "test-live"], sources.Select(s => (string)s!["provider"]!).ToArray());
        Assert.Equal(["device-local", "source-zone"], sources.Select(s => (string)s!["timeBasis"]!).ToArray());
        Assert.True(DateTimeOffset.Parse((string)sources[0]!["end"]!) < DateTimeOffset.Parse((string)sources[1]!["start"]!));
        var series = fortnight["series"]!.AsArray();
        Assert.Equal(series.Count, sources.Sum(s => (int)s!["readings"]!));
        Assert.Equal(series.Select(r => (long)r![0]!).Order().ToArray(), series.Select(r => (long)r![0]!).ToArray());
        Assert.Equal("test-live", (string?)fortnight["provider"]);
        Assert.False((bool)fortnight["demo"]!);
        Assert.Equal((string?)latest["rows"]![0]!["resource_id"], (string?)fortnight["latest"]!["resourceId"]);
        Assert.Equal(DateTimeOffset.Parse((string)fortnight["end"]!) - DateTimeOffset.Parse((string)fortnight["start"]!), TimeSpan.FromDays(14));
        // The totals are the archive's latest 14 complete days; the live feed never counts in them.
        var summary = fortnight["summary"]!;
        Assert.Equal("archive", (string?)summary["sourceKind"]);
        Assert.Equal(config.Today().AddDays(-1).ToString("yyyy-MM-dd"), (string?)summary["endDate"]);
        Assert.Equal(config.Today().AddDays(-14).ToString("yyyy-MM-dd"), (string?)summary["startDate"]);
        Assert.Equal(0L, (long)summary["bands"]!["veryHigh"]!);

        // The 24-hour rows are the live feed's only.
        var day = await health.Recent(app, "sam", false, default);
        Schemas.Valid(Schemas.Folder("mcp"), "app_recent_glucose.output.schema.json", day);
        Assert.All(day["rows"]!.AsArray(), r => Assert.Equal("test-live", (string?)r!["provider"]));
        Assert.NotEmpty(day["rows"]!.AsArray());
    }

    [PostgresFact] public async Task Nightscout_fills_the_live_feed_and_polls_from_the_last_reading()
    {
        var config = TestSettings.Use();
        await using var site = await FakeNightscout.Start();
        var now = DateTimeOffset.UtcNow;
        site.Fill(now.AddDays(-2), now.AddMinutes(-6));  // room for one more reading before now
        var adapter = await NightscoutTests.Configured(site.Url, FakeNightscout.Token, Db);
        Assert.Equal(TimeSpan.FromMinutes(1), adapter.Interval);
        await adapter.Sync(new MeasurementSink(new Ingest(Db, new Archive(Path.GetTempPath()), config), adapter), default);
        // The first poll reaches back 14 days.
        Assert.Equal(now.AddDays(-14).ToUnixTimeMilliseconds(), site.Requests[0].After ?? 0, 60_000d);
        var health = new AppHealth(Db, config);
        var app = (await new Principals(fixture.Source).SyncApp(Principals.NewToken(), default))!.Value;
        var latest = await health.Latest(app, "alex", default);
        Assert.Equal("nightscout", (string?)Assert.Single(latest["rows"]!.AsArray())!["provider"]);
        var fortnight = await health.Recent(app, "alex", true, default);
        Schemas.Valid(Schemas.Folder("mcp"), "app_recent_glucose.output.schema.json", fortnight);
        var live = Assert.Single(fortnight["sources"]!.AsArray())!;
        Assert.Equal("live", (string?)live["kind"]);
        Assert.Equal(site.Entries.Count, (int)live["readings"]!);
        Assert.Null(fortnight["summary"]);
        Assert.NotNull(fortnight["lastSyncedAt"]);

        // The next poll starts three hours before the newest stored reading, so late readings still arrive.
        var newest = DateTimeOffset.FromUnixTimeSeconds(site.Entries.Max(e => (long)e["date"]!) / 1000);
        site.Requests.Clear();
        site.Fill(newest.AddMinutes(5), newest.AddMinutes(5));
        await adapter.Sync(new MeasurementSink(new Ingest(Db, new Archive(Path.GetTempPath()), config), adapter), default);
        Assert.Equal((newest - NightscoutAdapter.Overlap).ToUnixTimeMilliseconds(), site.Requests[0].After);
        Assert.Equal(site.Entries.Count, (int)(await health.Recent(app, "alex", true, default))["sources"]![0]!["readings"]!);
    }

    [PostgresFact] public async Task A_failed_poll_backs_off()
    {
        var config = TestSettings.Use();
        await using var site = await FakeNightscout.Start();
        var adapter = await NightscoutTests.Configured(site.Url, "wrong-0000000000000000", Db);
        var sink = new MeasurementSink(new Ingest(Db, new Archive(Path.GetTempPath()), config), adapter);
        await Assert.ThrowsAsync<ConfigException>(() => adapter.Sync(sink, default));
        await adapter.Sync(sink, default);  // within the back-off: no request at all
        Assert.Single(site.Requests);
        // Sync now (SB-P6-09) within the back-off asks the site nothing either, and so ends failed, never complete; it may
        // be asked for once per pollSeconds, as the site is polled.
        var log = new LoggedLines<Scheduler>();
        Assert.Equal(SyncRequests.Failed, await SyncRequests.Run(new(adapter, Guid.NewGuid()), sink, log, default));
        Assert.Single(site.Requests);
        Assert.Equal(["nightscout sync now not run: the source's own limit refused it"], log.Lines);
        Assert.Equal(TimeSpan.FromSeconds(60), SyncRequests.Limit(adapter));
    }

    // A site that takes the request and never answers: the client's timeout is a failed poll that backs off, and it
    // reaches the scheduler as a timeout rather than a cancellation the engine could take for its own stop.
    sealed class Silent : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");
        }
    }
    [PostgresFact] public async Task A_site_that_does_not_answer_in_time_is_a_failed_poll()
    {
        var config = TestSettings.Use();
        var silent = new Silent();
        var adapter = await NightscoutTests.Configured("https://nightscout.example.invalid/", null, Db, silent);
        var sink = new MeasurementSink(new Ingest(Db, new Archive(Path.GetTempPath()), config), adapter);
        await Assert.ThrowsAsync<TimeoutException>(() => adapter.Sync(sink, default));
        await adapter.Sync(sink, default);
        Assert.Equal(1, silent.Calls);
    }
}

// glucoseSource in the health tools: glucose from one provider, every other metric as it is. Its own database, so its
// readings never reach the glucose views the other tests check.
public sealed class GlucoseSourceTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    static readonly Dictionary<string, ProviderPolicy> Policies = new()
    {
        ["carelink-csv"] = new(SourceAuthority.AuthorizedArchive, "health"), ["carelink-live"] = new(SourceAuthority.LiveSource, "health"),
        ["nightscout"] = new(SourceAuthority.LiveSource, "health"), ["csv"] = new(SourceAuthority.AuthorizedArchive, "health"),
    };
    Database Db => new(fixture.Source, new ProviderPolicies(Policies));

    async Task Write(string provider, string metric, string unit, params (string At, double Value)[] samples)
    {
        var args = new JsonObject
        {
            ["provider"] = provider, ["subject"] = "alex", ["idempotencyKey"] = $"{provider}:alex:{metric}",
            ["source"] = new JsonObject { ["externalId"] = $"{provider}-{metric}", ["kind"] = "readings", ["title"] = "Fictional readings",
                ["fetchedAt"] = "2026-09-01T12:00:00Z", ["timezone"] = "Europe/London" },
            ["measurements"] = new JsonArray(samples.Select(s => (JsonNode)new JsonObject
                { ["metric"] = metric, ["value"] = s.Value, ["unit"] = unit, ["observedAt"] = s.At, ["key"] = s.At }).ToArray()),
        };
        using var json = JsonDocument.Parse(args.ToJsonString());
        await new Ingest(Db, new Archive(Path.Combine(Path.GetTempPath(), "rhythm-test-archive")), TestSettings.Use()).Measurements("test", json.RootElement, default, [provider]);
    }

    [PostgresFact] public async Task Glucose_comes_from_the_chosen_provider_and_other_metrics_stay()
    {
        TestSettings.Use();
        await Write("carelink-csv", "sensor_glucose", "mg/dL", ("2026-09-01T08:00:00Z", 120), ("2026-09-01T08:05:00Z", 122));
        await Write("carelink-live", "sensor_glucose", "mg/dL", ("2026-09-01T08:00:00Z", 150));
        await Write("nightscout", "glucose", "mg/dL", ("2026-09-01T08:00:00Z", 99));
        await Write("csv", "steps", "count", ("2026-09-01T20:00:00Z", 8100));
        var harness = await new Principals(fixture.Source).Create("glucose-reader", "harness", null, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), default);
        var reader = new Retrieval(Db);
        var day = new DateOnly(2026, 9, 1);
        async Task<string[]> Providers(string metric, string glucose)
        {
            using var rows = JsonDocument.Parse(await reader.Metrics(harness, "alex", metric, day, day, 100, default, glucoseSource: glucose));
            return [.. rows.RootElement.EnumerateArray().Select(r => r.GetProperty("provider").GetString()!).Distinct().Order()];
        }
        Assert.Equal(["carelink-csv", "carelink-live"], await Providers("sensor_glucose", ""));
        Assert.Equal(["carelink-live"], await Providers("sensor_glucose", HarnessTools.GlucoseSource("", "live")));
        Assert.Equal(["carelink-csv"], await Providers("sensor_glucose", HarnessTools.GlucoseSource("carelink-csv", "live")));
        Assert.Empty(await Providers("glucose", "carelink-csv"));
        Assert.Equal(["csv"], await Providers("steps", "nightscout"));
        // Daily trends: glucose from the chosen provider beside steps from wherever they came.
        using var trends = JsonDocument.Parse(await reader.Trends(harness, "alex", "sensor_glucose", "steps", day, day, default, glucoseSource: "carelink-csv"));
        Assert.Equal([("carelink-csv", "sensor_glucose", 2L), ("csv", "steps", 1L)], trends.RootElement.EnumerateArray()
            .Select(r => (r.GetProperty("provider").GetString(), r.GetProperty("metric").GetString(), r.GetProperty("samples").GetInt64()))
            .OrderBy(r => r.Item2).ToArray());
        // investigate_health says which glucose source it kept.
        using var found = JsonDocument.Parse(await new HealthInvestigation(Db).Investigate(harness, "alex", day, day, "sensor_glucose,steps", 3, default, glucoseSource: "carelink-csv"));
        Assert.Equal("carelink-csv", found.RootElement.GetProperty("glucoseSource").GetString());
    }
}

// A database of its own: which person the app's glucose answers are about when it names none, as the app always does.
public sealed class GlucoseDefaultTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [PostgresFact] public async Task The_default_person_is_looked_up_at_most_once_a_minute()
    {
        var config = TestSettings.Use();
        var db = new Database(fixture.Source, new ProviderPolicies(new Dictionary<string, ProviderPolicy> { ["test-live"] = new(SourceAuthority.LiveSource, "health") }));
        var ingest = new Ingest(db, new Archive(Path.Combine(Path.GetTempPath(), "rhythm-test-archive")), config);
        async Task Reading(string subject)
        {
            var at = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            using var call = JsonDocument.Parse($$"""
                {"provider":"test-live","subject":"{{subject}}","idempotencyKey":"test-live:{{subject}}:default","source":{"externalId":"live-{{subject}}",
                 "kind":"glucose-readings","title":"Fictional readings","fetchedAt":"{{at}}","timezone":"Europe/London"},
                 "measurements":[{"metric":"glucose","value":110,"unit":"mg/dL","observedAt":"{{at}}","key":"{{at}}"}]}
                """);
            await ingest.Measurements("test", call.RootElement, default, ["test-live"]);
        }
        var health = new AppHealth(db, config);
        var app = (await new Principals(fixture.Source).SyncApp(Principals.NewToken(), default))!.Value;
        await Reading("sam");
        Assert.Equal("sam", (string?)(await health.Latest(app, null, default))["subject"]);
        // alex comes first in people. Finding the default reads every glucose reading, so a screen asking every minute
        // finds it once a minute, not on every call.
        await Reading("alex");
        Assert.Equal("sam", (string?)(await health.Latest(app, null, default))["subject"]);
        Assert.Equal("alex", (string?)(await new AppHealth(db, config).Latest(app, null, default))["subject"]);
        Assert.Equal("sam", (string?)(await health.Latest(app, "sam", default))["subject"]);
    }
}

// A database of its own, with no glucose until the test's plugin sends its first reading, the way a CareLink-style plugin does: its
// own token calling ingest_measurements for its live provider (IngestEndpoint), and the app asking as its server does
// (AppEndpoint), through one AppHealth as the engine keeps one. The glucose tools are listed only once a glucose source has data
// (Q-132); a "none" is kept for AppHealth.Absent (10 s, the app's server's own wait between listings while none is listed), so
// a source's first reading is listed, and read, at the app's first read after that, never a minute later. The test steps the
// engine's clock rather than waiting.
public sealed class GlucoseFirstReadingTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IDisposable
{
    // The folder the plugin's token is written into, removed after the test, pass or fail, as the other tests that make tokens
    // remove theirs.
    readonly string state = Path.Combine(Path.GetTempPath(), "rhythm-first-reading-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(state)) Directory.Delete(state, true);
    }

    [PostgresFact] public async Task A_plugins_first_reading_is_listed_and_read_at_the_apps_next_read()
    {
        var config = TestSettings.Use();
        var db = new Database(fixture.Source, new AdapterRegistry([new Knowledge.Adapters.Demo.DemoAdapter()]).Policies(config));
        var ingest = new Ingest(db, new Archive(Path.Combine(Path.GetTempPath(), "rhythm-test-archive")), config);
        var clock = DateTimeOffset.UtcNow;
        var health = new AppHealth(db, config, () => clock);
        var principals = new Principals(fixture.Source);
        var app = (await principals.SyncApp(Principals.NewToken(), default))!.Value;
        var plugin = await principals.Create("first-reading-pump", "ingest", "pump", null, Path.Combine(state, "plugin"), default);
        Assert.True(Directory.Exists(state));
        var services = new ServiceCollection().BuildServiceProvider();
        static async Task<(int Status, JsonNode? Answer)> Send(HttpContext context, Func<HttpContext, Task> handle, object body)
        {
            context.Request.Method = "POST";
            context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(body));
            context.Response.Body = new MemoryStream();
            await handle(context);
            var bytes = ((MemoryStream)context.Response.Body).ToArray();
            return (context.Response.StatusCode, bytes.Length == 0 ? null : JsonNode.Parse(bytes));
        }
        Task<(int Status, JsonNode? Answer)> App(string method, object? call = null) => Send(new DefaultHttpContext { RequestServices = services },
            c => AppEndpoint.Handle(c, new Caller(app, "app", "app", null), null!, health, AdapterRegistry.Empty),
            new { jsonrpc = "2.0", id = "a", method, @params = call ?? new { } });
        async Task<string[]> Listed() => [.. (await App("tools/list")).Answer!["result"]!["tools"]!.AsArray().Select(t => (string)t!["name"]!)];
        Task<(int Status, JsonNode? Answer)> Latest() => App("tools/call", new { name = AppEndpoint.LatestGlucose, arguments = new { } });

        // Before any reading: neither tool listed, and a call refused, each asked twice as a screen asks every minute.
        for (var i = 0; i < 2; i++)
        {
            var before = await Listed();
            Assert.DoesNotContain(AppEndpoint.LatestGlucose, before);
            Assert.DoesNotContain(AppEndpoint.RecentGlucose, before);
            Assert.Equal(403, (await Latest()).Status);
        }

        // The plugin's first reading, five minutes old, from its live provider.
        var at = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var (status, sent) = await Send(new DefaultHttpContext { RequestServices = services },
            c => IngestEndpoint.Handle(c, new Caller(plugin, "plugin:pump", "ingest", "pump"), ingest, config),
            new { jsonrpc = "2.0", id = "p", method = "tools/call", @params = new { name = IngestEndpoint.Measurements, arguments = new {
                provider = "pump-live", subject = "alex", idempotencyKey = "pump:alex:first-reading",
                source = new { externalId = "pump-live-first", kind = "glucose-readings", title = "Fictional readings", fetchedAt = at, timezone = "Europe/London" },
                measurements = new[] { new { metric = "sensor_glucose", value = 137, unit = "mg/dL", observedAt = at, key = at } } } } });
        Assert.Equal(200, status);
        Assert.Equal(1, (int?)sent!["result"]!["structuredContent"]!["accepted"]);

        // Within the 10 s the "none" is kept, the listing is as it was: one look, not one a listing.
        clock += AppHealth.Absent - TimeSpan.FromSeconds(1);
        Assert.DoesNotContain(AppEndpoint.LatestGlucose, await Listed());
        Assert.Equal(TimeSpan.FromSeconds(10), AppHealth.Absent);

        // The app's next read after that: both tools listed, and the reading is the current one, for the first person with
        // glucose; a "some" is then kept for a minute.
        clock += TimeSpan.FromSeconds(1);
        var after = await Listed();
        Assert.Contains(AppEndpoint.LatestGlucose, after);
        Assert.Contains(AppEndpoint.RecentGlucose, after);
        var (code, latest) = await Latest();
        Assert.Equal(200, code);
        var content = latest!["result"]!["structuredContent"]!;
        Assert.Equal("alex", (string?)content["subject"]);
        var row = Assert.Single(content["rows"]!.AsArray())!;
        Assert.Equal((137.0, "pump-live", false), ((double?)row["value"], (string?)row["provider"], (bool?)row["demo"]));
        Schemas.Valid(Schemas.Folder("mcp"), "app_latest_glucose.output.schema.json", content);
    }
}
