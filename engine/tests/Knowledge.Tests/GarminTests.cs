using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Adapters;
using Knowledge.Adapters.Demo;
using Knowledge.Adapters.Garmin;
using Knowledge.Mcp;
using Microsoft.AspNetCore.Http;

namespace Knowledge.Tests;

// A fictional Garmin Personal API, in process: every request ends here. Each day's answers are made up from the date
// asked for, so a sync of any range has data. Nothing in it comes from a real account or device.
internal sealed class FakeGarmin : HttpMessageHandler
{
    public const string Key = "fictional-personal-key";
    public readonly List<string> Paths = [];
    public string? FailOn;                       // a path fragment answered 503 (Garmin down)
    public TimeSpan? WaitOn;                     // with FailOn: a 429 asking to wait this long instead
    public string? ThrowOn;                      // a path fragment whose request throws Throws (a timeout, say)
    public Exception? Throws;
    public int Tokens;
    static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    public static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, London).DateTime);
    // An activity on every day before today (today's would still be in the future early in the morning).
    public Func<DateOnly, bool> HasActivity = d => d < Today;

    // A London clock time on that day, as Garmin's epoch milliseconds.
    static long Ms(DateOnly date, int hour, int minute = 0) =>
        new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(new TimeOnly(hour, minute)), London), TimeSpan.Zero).ToUnixTimeMilliseconds();
    static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Assert.Equal("personal.connectapi.garmin.com", request.RequestUri!.Host);
        var path = request.RequestUri.PathAndQuery;
        if (request.RequestUri.AbsolutePath == GarminClient.TokenPath)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            if (request.Headers.Authorization?.ToString() != "ApiKey " + Key) return new(HttpStatusCode.Unauthorized);
            Tokens++;
            return new(HttpStatusCode.OK) { Content = new StringContent("fictional.access.token", Encoding.UTF8, "text/plain") };
        }
        Assert.Equal("Bearer fictional.access.token", request.Headers.Authorization?.ToString());
        Paths.Add(path);
        if (ThrowOn is not null && Throws is not null && path.Contains(ThrowOn, StringComparison.Ordinal)) throw Throws;
        if (FailOn is not null && path.Contains(FailOn, StringComparison.Ordinal))
            return WaitOn is { } wait ? new(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(wait) } } : new(HttpStatusCode.ServiceUnavailable);
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        var day = DateOnly.Parse(query["calendarDate"] ?? query["date"] ?? query["startDate"] ?? request.RequestUri.Segments[^1], CultureInfo.InvariantCulture);
        var d = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var n = day.DayNumber % 7;
        await Task.Yield();
        if (path.Contains("usersummary/daily"))
            return Json($$"""{"calendarDate":"{{d}}","totalSteps":{{8000 + n * 250}},"restingHeartRate":{{50 + n}},"moderateIntensityMinutes":20,"vigorousIntensityMinutes":{{n}},"totalKilocalories":2200,"averageStressLevel":-1,"deviceId":4242}""");
        if (path.Contains("sleep/dailySleep"))
            return Json($$"""{"calendarDate":"{{d}}","sleepTimeSeconds":{{25200 + n * 600}},"deepSleepSeconds":5400,"remSleepSeconds":5000,"sleepStartTimestampGMT":{{Ms(day.AddDays(-1), 22, 30)}},"sleepScores":{"overall":{"value":{{75 + n}} },"restlessness":{"qualifierKey":"FAIR"} } }""");
        if (path.Contains("hrv-service/hrv/"))
            return Json($$"""{"hrvSummary":{"lastNightAvg":{{44 + n}},"weeklyAvg":46,"baseline":{"balancedLow":40,"balancedUpper":55} },"hrvReadings":[{"hrvValue":48,"readingTimeGMT":"{{day.AddDays(-1):yyyy-MM-dd}}T23:00:00.0"}]}""");
        if (path.Contains("dailyHeartRate"))
            return Json($$"""{"heartRateValueDescriptors":[{"key":"timestamp","index":0},{"key":"heartrate","index":1}],"heartRateValues":[[{{Ms(day, 0)}},58],[{{Ms(day, 0, 2)}},null],[{{Ms(day, 0, 4)}},-1],[{{Ms(day, 0, 6)}},61]]}""");
        if (path.Contains("bodyBattery"))
            return Json($$"""[{"bodyBatteryValueDescriptorDTOList":[{"bodyBatteryValueDescriptorIndex":0,"bodyBatteryValueDescriptorKey":"timestamp"},{"bodyBatteryValueDescriptorIndex":1,"bodyBatteryValueDescriptorKey":"bodyBatteryLevel"}],"bodyBatteryValuesArray":[[{{Ms(day, 0, 1)}},71],[{{Ms(day, 0, 3)}},140]]}]""");
        if (path.Contains("activitylist"))
            return Json(query["start"] != "0" || !HasActivity(day) ? "[]" : day.DayNumber % 2 == 0
                ? $$"""[{"activityId":{{700000 + day.DayNumber % 1000}},"activityName":"Fictional pool session","activityType":{"typeKey":"lap_swimming"},"unitOfPoolLength":{"unitKey":"yard"},"startTimeGMT":"{{d}} 06:00:00","distance":1500.0,"duration":2400.0,"averageHR":128,"activityTrainingLoad":55.5,"aerobicTrainingEffect":2.7,"deviceId":4242}]"""
                : $$"""[{"activityId":{{800000 + day.DayNumber % 1000}},"activityName":"Fictional run","activityType":{"typeKey":"running"},"startTimeGMT":"{{d}} 06:30:00","distance":5000.0,"duration":1800.0,"averageHR":146,"maxHR":171,"activityTrainingLoad":82,"aerobicTrainingEffect":3.4,"anaerobicTrainingEffect":1.1,"deviceId":4242}]""");
        return new(HttpStatusCode.NotFound);
    }
}

public sealed class GarminNormalizeTests
{
    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Fact] public void Daily_sleep_and_heart_payloads_become_metrics_in_the_ingest_units()
    {
        var daily = GarminNormalize.Metrics("daily", Json("""{"totalSteps":9100,"restingHeartRate":52,"averageStressLevel":-1,"maxHeartRate":"high","deviceId":7}"""));
        Assert.Equal(["resting_heart_rate", "steps"], daily.Select(m => m.Metric).Order());
        Assert.All(daily, m => { Assert.Equal("daily", m.Key); Assert.Null(m.At); Assert.Equal("7", m.Device); });
        Assert.Equal("bpm", daily.Single(m => m.Metric == "resting_heart_rate").Unit);
        var sleep = GarminNormalize.Metrics("sleep", Json("""{"sleepTimeSeconds":27000,"sleepStartTimestampGMT":1792479600000,"sleepScores":{"overall":{"value":81},"awakeCount":{"qualifierKey":"GOOD"}}}"""));
        Assert.Equal(81, sleep.Single(m => m.Metric == "sleep_score").Value);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1792479600000), sleep[0].At);
        Assert.DoesNotContain(sleep, m => m.Metric == "sleep_awake_count");
        // The account export writes the same instant as text.
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 22, 0, 0, TimeSpan.Zero),
            GarminNormalize.Metrics("sleep", Json("""{"sleepTimeSeconds":1,"sleepStartTimestampGMT":"2026-10-19T22:00:00.0"}"""))[0].At);
        // Columns come from the descriptors, wherever Garmin puts them; nulls and negatives are dropped.
        var heart = GarminNormalize.Metrics("heart", Json("""{"heartRateValueDescriptors":[{"key":"heartrate","index":1},{"key":"timestamp","index":0}],"heartRateValues":[[1000,60],[2000,null],[3000,-1],[4000,64]]}"""));
        Assert.Equal([60.0, 64.0], heart.Select(m => m.Value));
        Assert.Equal(["1000", "4000"], heart.Select(m => m.Key));
        Assert.All(heart, m => Assert.Equal("bpm", m.Unit));
        Assert.Empty(GarminNormalize.Metrics("heart", Json("""{"heartRateValues":[[1000,60]]}""")));
    }

    [Fact] public void An_hrv_reading_with_an_unreadable_time_is_dropped_and_the_day_still_reads()
    {
        var hrv = GarminNormalize.Metrics("hrv", Json("""
            {"hrvSummary":{"lastNightAvg":44},"hrvReadings":[{"hrvValue":48,"readingTimeGMT":""},{"hrvValue":49,"readingTimeGMT":"last night"},
             {"hrvValue":50,"readingTimeGMT":"2026-10-19T23:00:00.0"}]}
            """));
        var reading = Assert.Single(hrv, m => m.Key != "daily");
        Assert.Equal((50.0, new DateTimeOffset(2026, 10, 19, 23, 0, 0, TimeSpan.Zero)), (reading.Value, reading.At!.Value));
        Assert.Contains(hrv, m => m.Key == "daily" && m.Value == 44);
    }

    [Fact] public void Activities_keep_their_type_and_role_but_never_their_name()
    {
        var activity = Json("""{"activityId":1,"activityName":"Fictional name","activityType":{"typeKey":"lap_swimming"},"unitOfPoolLength":{"unitKey":"yard"},"parentId":9,"startTimeGMT":"2026-10-20 06:00:00","duration":2400,"averageHR":128}""");
        var metrics = GarminNormalize.Metrics("activity", activity);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 6, 0, 0, TimeSpan.Zero), metrics[0].At);
        Assert.Equal("""{"activityType":"lap_swimming","poolUnit":"yard","multisport":"leg"}""", GarminNormalize.Details(activity)!.ToJsonString());
        Assert.Null(GarminNormalize.Details(Json("""{"activityType":{"typeKey":"Not A Key"}}""")));
    }
}

public sealed class GarminClientTests
{
    static GarminClient Client(FakeGarmin fake, string? key = FakeGarmin.Key) =>
        new(new HttpClient(fake), () => key, TimeSpan.Zero, (_, _) => Task.CompletedTask);

    [Fact] public async Task The_key_is_exchanged_once_for_a_token_and_never_sent_to_data_paths()
    {
        var fake = new FakeGarmin();
        var client = Client(fake);
        Assert.NotNull(await client.Get("/usersummary-service/usersummary/daily?calendarDate=2026-10-20"));
        Assert.NotNull(await client.Get("/hrv-service/hrv/2026-10-20"));
        Assert.Null(await client.Get("/unknown-service/x?date=2026-10-20"));
        Assert.Equal(1, fake.Tokens);
        await Assert.ThrowsAsync<ArgumentException>(() => client.Get("//elsewhere.example.invalid/x"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Get("/a/../b"));
    }

    [Fact] public async Task A_missing_or_wrong_key_and_a_long_wait_are_codes_not_retries()
    {
        Assert.Equal("garmin_credentials_missing", (await Assert.ThrowsAsync<GarminException>(() => Client(new(), null).Get("/hrv-service/hrv/2026-10-20"))).Code);
        Assert.Equal("garmin_credentials_rejected", (await Assert.ThrowsAsync<GarminException>(() => Client(new(), "wrong").Get("/hrv-service/hrv/2026-10-20"))).Code);
        var fake = new FakeGarmin { FailOn = "hrv-service", WaitOn = TimeSpan.FromMinutes(30) };
        var e = await Assert.ThrowsAsync<GarminException>(() => Client(fake).Get("/hrv-service/hrv/2026-10-20"));
        Assert.Equal("garmin_rate_limit_defer", e.Code);
        Assert.True(e.RetryAt > DateTimeOffset.UtcNow.AddMinutes(29));
        Assert.Single(fake.Paths);
        fake.WaitOn = null;
        Assert.Equal("garmin_retry_exhausted", (await Assert.ThrowsAsync<GarminException>(() => Client(fake).Get("/hrv-service/hrv/2026-10-20"))).Code);
    }
}

public sealed class GarminExportTests
{
    static GarminExport Export(string unit = "km")
    {
        var garmin = new GarminAdapter(new FakeGarmin());
        garmin.Configure(new(new AdapterSettings("garmin", new Dictionary<string, object?> { ["enabled"] = "true", ["owner"] = "alex" }),
            TestSettings.Use(), new Database(Npgsql.NpgsqlDataSource.Create("Host=example.invalid;Database=none")), new Archive(Path.GetTempPath())));
        return new GarminExport(garmin, Path.GetTempPath(), unit);
    }

    [Fact] public void The_activity_list_reads_its_english_columns_and_skips_what_it_cannot_place()
    {
        var csv = "\uFEFFActivity Type,Date,Favorite,Title,Distance,Calories,Time,Avg HR,Max HR,Aerobic TE,Total Reps\r\n"
            + "Running,2026-10-20 07:30:00,false,\"Fictional run, by the canal\",5.02,\"1,234\",00:28:30,146,171,3.4,--\r\n"
            + "Pool Swim,2026-10-21 06:00:00,false,Fictional swim,\"1,500\",400,00:40:00,128,--,2.7,--\r\n"
            + "Strength Training,2026-10-22 18:00:00,false,Gym,0.00,250,45:00,110,140,1.2,96\r\n"
            + "Running,not a date,false,Broken,5,1,00:10:00,1,1,1,--\r\n";
        var writes = Export().Activities(csv);
        Assert.Equal(3, writes.Count);
        var run = writes[0];
        Assert.Equal("export:activity:20261020T073000:running", run.ExternalId);
        Assert.Equal("activity", run.Kind);
        Assert.Equal(new DateOnly(2026, 10, 20), run.Date);
        // 07:30 in London in October is 06:30 UTC.
        Assert.All(run.Samples, s => Assert.Equal(new DateTimeOffset(2026, 10, 20, 6, 30, 0, TimeSpan.Zero), s.At));
        Assert.Equal(1710, run.Samples.Single(s => s.Metric == "activity_duration").Value);
        Assert.Equal(5020, run.Samples.Single(s => s.Metric == "activity_distance").Value, 6);
        Assert.Equal(1234, run.Samples.Single(s => s.Metric == "activity_calories").Value);
        Assert.DoesNotContain(run.Samples, s => s.Metric == "strength_repetitions");
        Assert.Equal("lap_swimming", (string?)writes[1].Details!["activityType"]);
        Assert.DoesNotContain(writes[1].Samples, s => s.Metric == "activity_distance");
        Assert.Equal(2700, writes[2].Samples.Single(s => s.Metric == "activity_duration").Value);
        Assert.Equal(96, writes[2].Samples.Single(s => s.Metric == "strength_repetitions").Value);
        Assert.Equal(5.02 * 1609.344, Export("mi").Activities(csv)[0].Samples.Single(s => s.Metric == "activity_distance").Value, 6);
        Assert.Throws<FormatException>(() => Export().Activities("time,metric,value,unit\n2026-10-20T07:00:00Z,steps,1,count\n"));
    }

    [Fact] public void Daily_files_of_the_account_export_use_the_api_fields()
    {
        var writes = Export().Daily("""
            [{"calendarDate":"2026-10-20","totalSteps":9100,"restingHeartRate":52,"sleepTimeSeconds":27000},
             {"calendarDate":"2026-10-21","totalSteps":7300},{"calendarDate":"yesterday","totalSteps":1},{"note":"no date"}]
            """);
        Assert.Equal(["export:daily:2026-10-20", "export:sleep:2026-10-20", "export:daily:2026-10-21"], writes.Select(w => w.ExternalId));
        Assert.All(writes, w => Assert.Equal("metric", w.Kind));
        Assert.Throws<FormatException>(() => Export().Daily("""{"calendarDate":"2026-10-20"}"""));
    }

    [Fact] public void Quoted_fields_may_hold_commas_quotes_and_line_breaks() =>
        Assert.Equal(new[] { new[] { "a", "b,c", "say \"hi\"\nthere" }, new[] { "1", "", "3" } },
            GarminExport.Csv("a,\"b,c\",\"say \"\"hi\"\"\nthere\"\n1,,3").Select(r => r.ToArray()).ToArray());
}

public sealed class GarminSettingsTests
{
    static GarminAdapter Configured(Dictionary<string, object?> values)
    {
        values["enabled"] = "true";
        var garmin = new GarminAdapter(new FakeGarmin());
        garmin.Configure(new(new AdapterSettings("garmin", values), TestSettings.Use(),
            new Database(Npgsql.NpgsqlDataSource.Create("Host=example.invalid;Database=none")), new Archive(Path.GetTempPath())));
        return garmin;
    }

    [Theory]
    [InlineData("owner", "household", "owner")]
    [InlineData("owner", "nobody", "owner")]
    [InlineData("pollSeconds", "600", "pollSeconds")]
    [InlineData("deviceTimezone", "Mars/Olympus", "deviceTimezone")]
    [InlineData("distanceUnit", "furlong", "distanceUnit")]
    public void Wrong_settings_stop_the_start_naming_the_key(string key, string value, string expected) =>
        Assert.Contains(expected, Assert.Throws<ConfigException>(() => Configured(new() { [key] = value })).Message);

    [Fact] public void The_owner_defaults_to_the_first_person_and_the_device_zone_to_the_install_zone()
    {
        var garmin = Configured([]);
        Assert.Equal("alex", garmin.Owner);
        Assert.Equal("Europe/London", garmin.ZoneName);
    }

    [Fact] public void Its_app_tools_take_only_the_owner_and_keep_their_old_names()
    {
        var registry = new AdapterRegistry([Configured([])]);
        JsonElement Call(string tool, string args) => JsonDocument.Parse("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + args + "}}").RootElement;
        Assert.True(AppEndpoint.Allowed(Call("app_garmin_summary", "{}"), registry.AppTools));
        Assert.True(AppEndpoint.Allowed(Call("app_garmin_summary", """{"subject":"alex"}"""), registry.AppTools));
        Assert.True(AppEndpoint.Allowed(Call("observatory_request_garmin_sync", "{}"), registry.AppTools));
        Assert.False(AppEndpoint.Allowed(Call("app_garmin_summary", """{"subject":"sam"}"""), registry.AppTools));
        Assert.False(AppEndpoint.Allowed(Call("app_request_garmin_sync", """{"days":2}"""), registry.AppTools));
        // Off, the names are not the app role's at all.
        Assert.False(AppEndpoint.Allowed(Call("app_garmin_summary", "{}"), AdapterRegistry.Empty.AppTools));
        Assert.Equal(["app_garmin_summary", "app_request_garmin_sync", "observatory_garmin_summary", "observatory_request_garmin_sync"],
            registry.AppTools.Keys.Order(StringComparer.Ordinal));
    }

    sealed class Clashing : IAdapter
    {
        public string Name => "clashing";
        public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } = new Dictionary<string, ProviderPolicy>();
        public IEnumerable<AppTool> AppTools() => [new(AppEndpoint.Graph, "x", new { }, true, _ => true, (_, _, _) => Task.FromResult(new JsonObject()), "x")];
    }
    [Fact] public void An_adapter_cannot_take_a_core_app_tool() =>
        Assert.Throws<ConfigException>(() => new AdapterRegistry([new Clashing()]).AppTools);
}

// The whole path against a real database: jobs, the fake API, the ingest rules, the tiles' reader and the app role.
public sealed class GarminDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "rhythm-garmin-" + Guid.NewGuid().ToString("N"));

    (GarminAdapter Garmin, MeasurementSink Sink, Database Db) Setup(FakeGarmin fake, string? key = FakeGarmin.Key)
    {
        var config = TestSettings.Use();
        Directory.CreateDirectory(Path.Combine(folder, "import"));
        var keyFile = Path.Combine(folder, "garmin_api_key");
        if (key is null) File.Delete(keyFile); else File.WriteAllText(keyFile, key + "\n");
        Environment.SetEnvironmentVariable("RHYTHM_GARMIN_API_KEY_FILE", keyFile);
        var garmin = new GarminAdapter(fake, (_, _) => Task.CompletedTask);
        var db = new Database(fixture.Source, new AdapterRegistry([new DemoAdapter(), garmin]).Policies(config));
        var archive = new Archive(Path.Combine(folder, "archive"));
        garmin.Configure(new(new AdapterSettings("garmin", new Dictionary<string, object?>
            { ["enabled"] = "true", ["owner"] = "alex", ["importPath"] = Path.Combine(folder, "import") }), config, db, archive));
        return (garmin, new MeasurementSink(new Ingest(db, archive, config), garmin), db);
    }
    async Task<T> Scalar<T>(string sql)
    {
        await using var cmd = fixture.Source.CreateCommand(sql);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
    async Task Sql(string sql) { await using var cmd = fixture.Source.CreateCommand(sql); await cmd.ExecuteNonQueryAsync(); }

    [PostgresFact] public async Task A_sync_fills_the_tiles_resumes_after_a_failure_and_writes_nothing_twice()
    {
        await Sql("DELETE FROM sync_events; DELETE FROM sync_jobs; DELETE FROM measurements WHERE resource_id IN (SELECT id FROM resources WHERE provider='garmin'); DELETE FROM resources WHERE provider='garmin'");
        var fake = new FakeGarmin { FailOn = "sleep/dailySleep" };
        var (garmin, sink, _) = Setup(fake);
        await Assert.ThrowsAsync<GarminException>(() => garmin.Sync(sink, default));
        Assert.Equal("failed", await Scalar<string>("SELECT status FROM sync_jobs"));
        Assert.Equal("garmin_retry_exhausted", await Scalar<string>("SELECT last_error FROM sync_jobs"));
        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM resources WHERE provider='garmin'"));
        // A failed job waits for the regular poll (and Garmin's requested delay); the next round resumes it from its
        // checkpoint. The fake does not wait, so the test moves the delay into the past.
        fake.FailOn = null;
        await Sql("UPDATE sync_jobs SET not_before=now()-interval '1 second'");
        var calls = fake.Paths.Count;
        await garmin.Sync(sink, default);
        Assert.Equal(calls, fake.Paths.Count);
        garmin.PollNow();
        await garmin.Sync(sink, default);
        Assert.Equal("complete", await Scalar<string>("SELECT status FROM sync_jobs"));
        Assert.Equal(14, await Scalar<int>("SELECT until_date-from_date+1 FROM sync_jobs"));
        var today = garmin.Today();
        Assert.Equal(13L * 5 + 13, await Scalar<long>($"SELECT count(DISTINCT r.id) FROM resources r JOIN measurements m ON m.resource_id=r.id WHERE r.provider='garmin' AND m.local_date<'{today:yyyy-MM-dd}'"));
        Assert.Equal("Europe/London", await Scalar<string>("SELECT DISTINCT timezone FROM resources WHERE provider='garmin'"));
        Assert.Equal("""{"poolUnit": "yard", "activityType": "lap_swimming"}""",
            await Scalar<string>("SELECT (metadata->'details')::text FROM resources WHERE provider='garmin' AND external_id LIKE 'activity:7%' LIMIT 1"));
        // Body battery 140 is out of range and dropped; heart rate null and -1 are dropped.
        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM measurements WHERE metric='body_battery' AND value>100"));
        Assert.Equal(26L, await Scalar<long>($"SELECT count(*) FROM measurements WHERE metric='heart_rate' AND local_date<'{today:yyyy-MM-dd}'"));

        // The tiles: the facade's shape (the contract's schema), heart rates as beats/min, every activity grouped and counted.
        var reader = (await new Principals(fixture.Source).SyncApp(Principals.NewToken(), default))!.Value;
        var summary = await GarminTiles.Read(garmin, reader, default);
        Schemas.Valid(Schemas.Folder("mcp"), "app_garmin_summary.output.schema.json", summary);
        Assert.Equal("alex", (string?)summary["subject"]);
        Assert.Equal("garmin", (string?)summary["selection"]);
        Assert.True((bool)summary["complete"]!);
        Assert.True((bool)summary["coverageComplete"]!);
        Assert.Equal(garmin.Today().ToString("yyyy-MM-dd"), (string?)summary["today"]);
        Assert.Equal(garmin.Today().AddDays(1).ToString("yyyy-MM-dd"), (string?)summary["endDate"]);
        Assert.EndsWith("Z", (string?)summary["lastSyncedAt"]);
        var rows = summary["rows"]!.AsArray().Select(r => r!.AsObject()).ToArray();
        Assert.Contains(rows, r => (string?)r["metric"] == "resting_heart_rate" && (string?)r["unit"] == "beats/min");
        Assert.Contains(rows, r => (string?)r["metric"] == "sleep_duration" && (string?)r["unit"] == "s");
        Assert.DoesNotContain(rows, r => (string?)r["metric"] == "heart_rate");
        var activities = rows.Where(r => (string?)r["source_kind"] == "activity").GroupBy(r => (string?)r["resource_id"]).ToArray();
        Assert.Equal(13, activities.Length);
        Assert.Equal(13L, (long)summary["activityCount"]!);
        Assert.Contains(rows, r => (string?)r["activity_type"] == "running");
        Assert.All(rows, r => Assert.Equal("alex", (string?)r["subject_id"]));
        Assert.All(rows, r => Assert.Matches("(Z|[+-]\\d\\d:\\d\\d)$", (string?)r["observed_at"]));

        // A second full sync changes nothing and archives nothing new.
        var measurements = await Scalar<long>("SELECT count(*) FROM measurements");
        var archived = Directory.EnumerateFiles(Path.Combine(folder, "archive"), "*", SearchOption.AllDirectories).Count();
        garmin.PollNow();
        await garmin.Sync(sink, default);
        Assert.Equal(2L, await Scalar<long>("SELECT count(*) FROM sync_jobs WHERE status='complete'"));
        Assert.Equal(7, await Scalar<int>("SELECT until_date-from_date+1 FROM sync_jobs ORDER BY updated_at DESC LIMIT 1"));
        Assert.Equal(measurements, await Scalar<long>("SELECT count(*) FROM measurements"));
        Assert.Equal(archived, Directory.EnumerateFiles(Path.Combine(folder, "archive"), "*", SearchOption.AllDirectories).Count());

        // An activity deleted at Garmin leaves the tiles once its day is synced again.
        var yesterday = garmin.Today().AddDays(-1);
        fake.HasActivity = d => d < FakeGarmin.Today && d != yesterday;
        garmin.PollNow();
        await garmin.Sync(sink, default);
        Assert.Equal(12L, (long)(await GarminTiles.Read(garmin, reader, default))["activityCount"]!);
    }

    [PostgresFact] public async Task A_timeout_or_an_unexpected_error_fails_the_job_so_its_attempts_count()
    {
        await Sql("DELETE FROM sync_events; DELETE FROM sync_jobs; DELETE FROM measurements WHERE resource_id IN (SELECT id FROM resources WHERE provider='garmin'); DELETE FROM resources WHERE provider='garmin'");
        // Garmin accepts the request and never answers: the client's timeout is a failed job, not the engine stopping.
        var fake = new FakeGarmin { ThrowOn = "sleep/dailySleep", Throws = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout") };
        var (garmin, sink, _) = Setup(fake);
        Assert.Equal("garmin_timeout", (await Assert.ThrowsAsync<GarminException>(() => garmin.Sync(sink, default))).Code);
        async Task<(string, string, int)> Job()
        {
            await using var cmd = fixture.Source.CreateCommand("SELECT status,last_error,attempts FROM sync_jobs");
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetString(0), reader.GetString(1), reader.GetInt32(2));
        }
        Assert.Equal(("failed", "garmin_timeout", 1), await Job());
        // An error of no expected kind fails it too, rather than leaving it running to be fetched again every minute.
        fake.Throws = new InvalidOperationException("an answer nobody expected");
        garmin.PollNow();
        await Assert.ThrowsAsync<InvalidOperationException>(() => garmin.Sync(sink, default));
        Assert.Equal(("failed", "garmin_unexpected", 2), await Job());
        var calls = fake.Paths.Count;
        await garmin.Sync(sink, default);
        Assert.Equal(calls, fake.Paths.Count);
    }

    [PostgresFact] public async Task Sync_now_is_the_app_roles_and_runs_within_the_next_tick()
    {
        await Sql("DELETE FROM sync_events; DELETE FROM sync_jobs");
        var fake = new FakeGarmin();
        var (garmin, sink, _) = Setup(fake);
        var config = TestSettings.Use();
        var app = (await new Principals(fixture.Source).SyncApp(Principals.NewToken(), default))!.Value;
        var registry = new AdapterRegistry([garmin]);
        async Task<(int Status, JsonElement Body)> Call(string method, string tool = "", string args = "{}")
        {
            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            var body = method == "tools/call"
                ? "{\"jsonrpc\":\"2.0\",\"id\":\"7\",\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + args + "}}"
                : "{\"jsonrpc\":\"2.0\",\"id\":\"7\",\"method\":\"" + method + "\"}";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            context.Response.Body = new MemoryStream();
            await AppEndpoint.Handle(context, new Caller(app, "app", "app", null), null!, new AppHealth(fixture.Db, config), registry);
            var text = ((MemoryStream)context.Response.Body).ToArray();
            return (context.Response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
        }
        var listed = (await Call("tools/list")).Body.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();
        Assert.Contains("app_garmin_summary", listed);
        Assert.Contains("app_request_garmin_sync", listed);
        Assert.DoesNotContain("observatory_garmin_summary", listed);

        var (status, answer) = await Call("tools/call", "app_request_garmin_sync");
        Assert.Equal(200, status);
        var asked = answer.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal(["accepted", "lastSyncedAt", "nextAllowedAt", "pendingSince"], asked.EnumerateObject().Select(p => p.Name).Order());
        Assert.True(asked.GetProperty("accepted").GetBoolean());
        var again = (await Call("tools/call", "observatory_request_garmin_sync")).Body.GetProperty("result").GetProperty("structuredContent");
        Assert.False(again.GetProperty("accepted").GetBoolean());
        foreach (var answered in new[] { asked, again })
            Schemas.Valid(Schemas.Folder("mcp"), "app_request_garmin_sync.output.schema.json", JsonNode.Parse(answered.GetRawText()));
        Assert.Equal(400, (await Call("tools/call", "app_garmin_summary", """{"subject":"sam"}""")).Status);
        // The request is picked up by the next tick, before the regular poll, and covers yesterday and today.
        await garmin.Sync(sink, default);
        Assert.Equal("complete", await Scalar<string>("SELECT status FROM sync_jobs WHERE metadata->>'reason'='on-demand'"));
        Assert.Contains(fake.Paths, p => p.Contains("calendarDate=" + garmin.Today().ToString("yyyy-MM-dd")));
        var (read, summary) = await Call("tools/call", "app_garmin_summary");
        Assert.Equal(200, read);
        Assert.Equal("garmin", summary.GetProperty("result").GetProperty("structuredContent").GetProperty("selection").GetString());
        Schemas.Valid(Schemas.Folder("mcp"), "app_garmin_summary.output.schema.json", JsonNode.Parse(summary.GetProperty("result").GetProperty("structuredContent").GetRawText()));
        // Off, the tools are not there and a call is refused without saying whether data exists.
        var off = new AdapterRegistry([]);
        registry = off;
        Assert.Equal(403, (await Call("tools/call", "app_garmin_summary")).Status);
        Assert.DoesNotContain("app_garmin_summary", (await Call("tools/list")).Body.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()));
    }

    // SB-P6-09: Sync now from the Sources view asks Garmin for its own sync now (the two-day job, as the caller), then
    // works one tick, which runs that job first against the stand-in (FakeGarmin): yesterday and today, nothing older.
    // Its limit is Garmin's own 15 minutes.
    [PostgresFact] public async Task Sync_now_from_the_sources_view_runs_the_two_day_job_in_one_batch()
    {
        await Sql("DELETE FROM sync_events; DELETE FROM sync_jobs");
        var fake = new FakeGarmin();
        var (garmin, sink, _) = Setup(fake);
        var app = (await new Principals(fixture.Source).SyncApp(Principals.NewToken(), default))!.Value;
        Assert.Equal(TimeSpan.FromMinutes(15), SyncRequests.Limit(garmin));
        Assert.Equal(SyncRequests.Complete, await SyncRequests.Run(new(garmin, app), sink, new LoggedLines<Scheduler>(), default));
        Assert.Equal(("complete", 1L), (await Scalar<string>("SELECT status FROM sync_jobs WHERE metadata->>'reason'='on-demand'"),
            await Scalar<long>("SELECT count(*) FROM sync_jobs")));
        Assert.Contains(fake.Paths, p => p.Contains("calendarDate=" + garmin.Today().ToString("yyyy-MM-dd")));
        Assert.Contains(fake.Paths, p => p.Contains("calendarDate=" + garmin.Today().AddDays(-1).ToString("yyyy-MM-dd")));
        Assert.DoesNotContain(fake.Paths, p => p.Contains("calendarDate=" + garmin.Today().AddDays(-2).ToString("yyyy-MM-dd")));
    }

    // The tiles' own sync now and the Sources view's share Garmin's 15 minutes, which the Sources view's limit knows
    // nothing of: asked for after the tiles' job has run, Garmin refuses the two-day job, so the batch reaches Garmin for
    // nothing and ends failed, never complete.
    [PostgresFact] public async Task Sync_now_after_the_tiles_own_says_failed_when_garmin_refuses_the_job()
    {
        await Sql("DELETE FROM sync_events; DELETE FROM sync_jobs");
        var fake = new FakeGarmin();
        var (garmin, sink, _) = Setup(fake);
        var app = (await new Principals(fixture.Source).SyncApp(Principals.NewToken(), default))!.Value;
        Assert.True((bool)(await GarminTiles.Request(garmin, app, default))["accepted"]!);
        await garmin.Sync(sink, default);
        Assert.Equal("complete", await Scalar<string>("SELECT status FROM sync_jobs WHERE metadata->>'reason'='on-demand'"));
        var fetched = fake.Paths.Count;
        var log = new LoggedLines<Scheduler>();
        Assert.Equal(SyncRequests.Failed, await SyncRequests.Run(new(garmin, app), sink, log, default));
        Assert.Equal(fetched, fake.Paths.Count);
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM sync_jobs"));
        Assert.Contains("garmin sync now not run: the source's own limit refused it", log.Lines);
    }

    [PostgresFact] public async Task Without_a_key_the_export_folder_fills_the_tiles_and_answers_sync_now()
    {
        await Sql("DELETE FROM sync_events; DELETE FROM sync_jobs; DELETE FROM measurements WHERE resource_id IN (SELECT id FROM resources WHERE provider='garmin'); DELETE FROM resources WHERE provider='garmin'");
        var fake = new FakeGarmin();
        var (garmin, sink, _) = Setup(fake, key: null);
        var day = garmin.Today().AddDays(-2);
        File.WriteAllText(Path.Combine(folder, "import", "Activities.csv"),
            $"Activity Type,Date,Favorite,Title,Distance,Calories,Time,Avg HR,Max HR,Aerobic TE\n"
            + $"Running,{day:yyyy-MM-dd} 07:30:00,false,Fictional run,5.0,300,00:30:00,140,165,3.1\n");
        File.WriteAllText(Path.Combine(folder, "import", "wellness.json"),
            $$"""[{"calendarDate":"{{day:yyyy-MM-dd}}","totalSteps":10500,"restingHeartRate":55,"sleepTimeSeconds":26100}]""");
        File.WriteAllText(Path.Combine(folder, "import", "notes.json"), "not json at all");
        await garmin.Sync(sink, default);
        Assert.Empty(fake.Paths);
        var reader = (await new Principals(fixture.Source).SyncApp(Principals.NewToken(), default))!.Value;
        var summary = await GarminTiles.Read(garmin, reader, default);
        Schemas.Valid(Schemas.Folder("mcp"), "app_garmin_summary.output.schema.json", summary);
        var rows = summary["rows"]!.AsArray().Select(r => r!.AsObject()).ToArray();
        Assert.Contains(rows, r => (string?)r["metric"] == "steps" && (double)r["value"]! == 10500);
        Assert.Contains(rows, r => (string?)r["activity_type"] == "running");
        Assert.Equal(1L, (long)summary["activityCount"]!);
        Assert.False((bool)summary["coverageComplete"]!);
        Assert.NotNull(summary["lastSyncedAt"]);
        // Sync now is answered by the folder scan, and never claims Garmin coverage. The same app principal asks and
        // reads: making a second one would disable the first (one shared app token), and a disabled reader reads nothing.
        Assert.True((bool)(await GarminTiles.Request(garmin, reader, default))["accepted"]!);
        await garmin.Sync(sink, default);
        Assert.Equal("complete", await Scalar<string>("SELECT status FROM sync_jobs"));
        var after = await GarminTiles.Read(garmin, reader, default);
        Assert.Contains(after["rows"]!.AsArray(), r => (string?)r!["metric"] == "steps" && (double)r["value"]! == 10500);
        Assert.False((bool)after["coverageComplete"]!);
        var count = await Scalar<long>("SELECT count(*) FROM measurements");
        await garmin.Sync(sink, default);
        Assert.Equal(count, await Scalar<long>("SELECT count(*) FROM measurements"));
    }
}
