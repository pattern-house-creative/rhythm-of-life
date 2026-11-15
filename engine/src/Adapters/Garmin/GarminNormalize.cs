using System.Globalization;
using System.Text.Json;

namespace Knowledge.Adapters.Garmin;

// One normalized reading. At is null for a daily total (it belongs to its calendar day); Key identifies it within
// its source (the day's totals use "daily", intraday readings their epoch milliseconds).
public sealed record GarminSample(string Metric, double Value, string Unit, string Key, DateTimeOffset? At, string? Device);

// What the adapter keeps from Garmin's JSON: the fields below and nothing else. Hand-written from the fields this
// adapter reads; no vendor schema is copied. Negative and non-numeric values are dropped, never guessed, and the
// position of a value inside Garmin's arrays comes from the descriptors Garmin sends with them.
public static class GarminNormalize
{
    // A UTC timestamp as Garmin writes it, or null when it is not one (the reading is then dropped, never guessed).
    public static DateTimeOffset? Utc(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;

    public static List<GarminSample> Metrics(string stream, JsonElement json)
    {
        var result = new List<GarminSample>();
        if (stream == "body-battery" && json.ValueKind == JsonValueKind.Array)
            return json.EnumerateArray().SelectMany(j => Metrics(stream, j)).DistinctBy(m => (m.Metric, m.Key)).ToList();
        if (json.ValueKind != JsonValueKind.Object) return result;
        var root = stream == "hrv" && json.TryGetProperty("hrvSummary", out var summary) ? summary : json;
        DateTimeOffset? at = null;
        var device = json.TryGetProperty("deviceId", out var d) && d.ValueKind is JsonValueKind.Number or JsonValueKind.String ? d.ToString() : null;
        if (stream == "activity" && json.TryGetProperty("startTimeGMT", out var start) && start.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(start.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var began))
            at = began;
        // The API sends epoch milliseconds; the account export a UTC timestamp.
        if (stream == "sleep" && json.TryGetProperty("sleepStartTimestampGMT", out var sleepStart))
            at = sleepStart.ValueKind == JsonValueKind.Number && sleepStart.TryGetInt64(out var epoch) ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                : sleepStart.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(sleepStart.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var slept) ? slept : null;
        foreach (var (field, metric, unit) in Fields(stream))
            if (Field(root, field) is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var n) && double.IsFinite(n) && n >= 0)
                result.Add(new(metric, n, unit, "daily", at, device));
        if (stream == "hrv" && json.TryGetProperty("hrvReadings", out var readings) && readings.ValueKind == JsonValueKind.Array)
            foreach (var reading in readings.EnumerateArray())
                if (reading.ValueKind == JsonValueKind.Object && reading.TryGetProperty("hrvValue", out var v) && v.ValueKind == JsonValueKind.Number
                    && v.TryGetDouble(out var n) && n > 0 && reading.TryGetProperty("readingTimeGMT", out var time) && time.ValueKind == JsonValueKind.String
                    && Utc(time.GetString()) is { } timestamp)
                {
                    result.Add(new("hrv", n, "ms", timestamp.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), timestamp, device));
                }
        if (stream == "body-battery")
            result.AddRange(Series(json, "bodyBatteryValuesArray", "bodyBatteryValueDescriptorDTOList", "bodyBatteryValueDescriptorKey",
                "bodyBatteryValueDescriptorIndex", "bodyBatteryLevel", "body_battery", "score", n => n is >= 0 and <= 100, device));
        if (stream == "heart")
            result.AddRange(Series(json, "heartRateValues", "heartRateValueDescriptors", "key", "index", "heartrate", "heart_rate", "bpm", n => n > 0, device));
        return result.DistinctBy(m => (m.Metric, m.Key)).ToList();
    }

    // [timestamp, value] rows whose columns are named by a descriptor list.
    static IEnumerable<GarminSample> Series(JsonElement json, string valuesName, string descriptorsName, string keyName, string indexName,
        string valueKey, string metric, string unit, Func<double, bool> valid, string? device)
    {
        if (!json.TryGetProperty(valuesName, out var values) || values.ValueKind != JsonValueKind.Array
            || !json.TryGetProperty(descriptorsName, out var descriptors) || descriptors.ValueKind != JsonValueKind.Array) yield break;
        int? ti = null, vi = null;
        foreach (var desc in descriptors.EnumerateArray())
        {
            if (desc.ValueKind != JsonValueKind.Object || !desc.TryGetProperty(keyName, out var k) || k.ValueKind != JsonValueKind.String
                || !desc.TryGetProperty(indexName, out var i) || !i.TryGetInt32(out var index)) continue;
            if (k.GetString() == "timestamp") ti = index;
            if (k.GetString() == valueKey) vi = index;
        }
        if (ti is not >= 0 || vi is not >= 0) yield break;
        foreach (var row in values.EnumerateArray())
            if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() > Math.Max(ti.Value, vi.Value)
                && row[ti.Value].ValueKind == JsonValueKind.Number && row[ti.Value].TryGetInt64(out var time)
                && row[vi.Value].ValueKind == JsonValueKind.Number && row[vi.Value].TryGetDouble(out var n) && double.IsFinite(n) && valid(n))
                yield return new(metric, n, unit, time.ToString(CultureInfo.InvariantCulture), DateTimeOffset.FromUnixTimeMilliseconds(time), device);
    }

    // A dotted field walks nested objects ("sleepScores.overall.value"); anything else is absent.
    static JsonElement? Field(JsonElement root, string field)
    {
        var current = root;
        foreach (var name in field.Split('.'))
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current)) return null;
        return current;
    }

    // Activity descriptors the app shows: Garmin's type key, a pool's unit and a multisport role. Never names,
    // locations or tracks.
    public static System.Text.Json.Nodes.JsonObject? Details(JsonElement activity)
    {
        var details = new System.Text.Json.Nodes.JsonObject();
        if (activity.TryGetProperty("activityType", out var type) && type.ValueKind == JsonValueKind.Object && type.TryGetProperty("typeKey", out var key)
            && key.ValueKind == JsonValueKind.String && TypeKey(key.GetString()) is { } typeKey)
            details["activityType"] = typeKey;
        if (activity.TryGetProperty("unitOfPoolLength", out var pool) && pool.ValueKind == JsonValueKind.Object && pool.TryGetProperty("unitKey", out var unit)
            && unit.GetString() is "meter" or "yard")
            details["poolUnit"] = unit.GetString();
        if (activity.TryGetProperty("parent", out var parent) && parent.ValueKind == JsonValueKind.True) details["multisport"] = "parent";
        else if (activity.TryGetProperty("parentId", out var parentId) && parentId.ValueKind is JsonValueKind.Number or JsonValueKind.String) details["multisport"] = "leg";
        return details.Count == 0 ? null : details;
    }
    public static string? TypeKey(string? value) =>
        value is { Length: > 0 and <= 40 } && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_') ? value : null;

    public static (string Field, string Metric, string Unit)[] Fields(string stream) => stream switch
    {
        "daily" => [("totalSteps", "steps", "count"), ("totalKilocalories", "calories_total", "kcal"), ("activeKilocalories", "calories_active", "kcal"),
            ("restingHeartRate", "resting_heart_rate", "bpm"), ("minHeartRate", "heart_rate_min", "bpm"), ("maxHeartRate", "heart_rate_max", "bpm"),
            ("averageStressLevel", "stress_mean", "score"), ("maxStressLevel", "stress_max", "score"), ("bodyBatteryHighestValue", "body_battery_max", "score"),
            ("bodyBatteryLowestValue", "body_battery_min", "score"), ("bodyBatteryChargedValue", "body_battery_charged", "score"),
            ("bodyBatteryDrainedValue", "body_battery_drained", "score"), ("moderateIntensityMinutes", "intensity_moderate", "min"),
            ("vigorousIntensityMinutes", "intensity_vigorous", "min"), ("totalDistanceMeters", "daily_distance", "m"), ("averageSpo2", "spo2_mean", "%"),
            ("avgWakingRespirationValue", "respiration_awake", "breaths/min"), ("hydrationValueInML", "hydration", "mL"),
            ("bodyBatteryAtWakeTime", "body_battery_wake", "score"), ("bodyBatteryDuringSleep", "body_battery_sleep_charge", "score")],
        // The sleep score and those of its parts that carry a number; the other parts are qualifiers only.
        "sleep" => [("sleepTimeSeconds", "sleep_duration", "s"), ("deepSleepSeconds", "sleep_deep", "s"), ("lightSleepSeconds", "sleep_light", "s"),
            ("remSleepSeconds", "sleep_rem", "s"), ("awakeSleepSeconds", "sleep_awake", "s"), ("unmeasurableSleepSeconds", "sleep_unclassified", "s"),
            ("napTimeSeconds", "nap_duration", "s"), ("avgSleepStress", "sleep_stress", "score"), ("avgHeartRate", "sleep_heart_rate", "bpm"),
            ("sleepScores.overall.value", "sleep_score", "score"), ("sleepScores.deepPercentage.value", "sleep_deep_percentage", "%"),
            ("sleepScores.lightPercentage.value", "sleep_light_percentage", "%"), ("sleepScores.remPercentage.value", "sleep_rem_percentage", "%"),
            ("sleepScores.awakeCount.value", "sleep_awake_count", "count")],
        // Garmin's balanced range is the baseline the app draws behind overnight HRV.
        "hrv" => [("lastNightAvg", "hrv_overnight", "ms"), ("weeklyAvg", "hrv_weekly", "ms"), ("lastNight5MinHigh", "hrv_five_min_high", "ms"),
            ("baseline.balancedLow", "hrv_baseline_low", "ms"), ("baseline.balancedUpper", "hrv_baseline_high", "ms")],
        "activity" => [("distance", "activity_distance", "m"), ("duration", "activity_duration", "s"), ("calories", "activity_calories", "kcal"),
            ("averageHR", "activity_heart_rate", "bpm"), ("maxHR", "activity_heart_rate_max", "bpm"), ("activityTrainingLoad", "activity_training_load", "score"),
            ("aerobicTrainingEffect", "training_effect_aerobic", "score"), ("anaerobicTrainingEffect", "training_effect_anaerobic", "score"),
            ("vO2MaxValue", "vo2_max", "mL/kg/min"), ("totalReps", "strength_repetitions", "count"), ("totalSets", "strength_sets", "count")],
        _ => []
    };
}
