using System.Globalization;

namespace Knowledge;

public record MetricMeaning(string Unit,string Aggregation,string Meaning,bool Event=false,double? Low=null,double? High=null,string? Guidance=null);
public static class HealthSemantics
{
    public const string Version="health-semantics-1";
    public const string GlycemiaGuidance="https://doi.org/10.2337/dc26-s006";
    // The health tools' glucoseSource: with @glucose set, glucose readings come from that one provider only and every
    // other metric is left as it is (r is the resource, m the measurement).
    public const string GlucoseFrom="(@glucose='' OR m.metric NOT IN ('glucose','sensor_glucose','blood_glucose') OR r.provider=@glucose)";
    static readonly Dictionary<string,MetricMeaning> Registered=Build();
    // Metric meanings: the ingest contract's common names (contracts/README.md 4.4) and the names device adapters
    // already use, each with its canonical unit and how a day aggregates it. Unknown metrics stay usable, without
    // semantics; units are never guessed.
    static Dictionary<string,MetricMeaning> Build()
    {
        var result=new Dictionary<string,MetricMeaning>(StringComparer.Ordinal);
        const string Daily="Daily summary from the source; missing days remain unknown.";
        const string Recorded="Recorded activity summary; only recorded events, not all activity.";
        foreach(var (metric,unit) in new[]{("steps","count"),("calories_total","kcal"),("calories_active","kcal"),("resting_heart_rate","bpm"),
            ("heart_rate_min","bpm"),("heart_rate_max","bpm"),("stress_mean","score"),("stress_max","score"),("stress","score"),
            ("body_battery_max","score"),("body_battery_min","score"),("intensity_minutes","min"),("intensity_moderate","min"),("intensity_vigorous","min"),
            ("daily_distance","m"),("spo2_mean","%"),("respiration_awake","breaths/min"),("hydration","mL"),("weight","kg"),
            ("sleep_minutes","min"),("sleep_duration","s"),("sleep_deep","s"),("sleep_light","s"),("sleep_rem","s"),("sleep_awake","s"),
            ("sleep_score","score"),("hrv_overnight","ms"),("hrv_weekly","ms")})
            result[metric]=new(unit,"mean",Daily);
        foreach(var (metric,unit) in new[]{("activity_distance","m"),("activity_duration","s"),("activity_calories","kcal"),
            ("activity_heart_rate","bpm"),("activity_training_load","score")})
            result[metric]=new(unit,unit is "s" or "m" or "kcal"?"sum":"mean",Recorded,true);
        result["heart_rate"]=new("bpm","mean","Sample mean, not time weighted.");
        result["hrv"]=new("ms","mean","Sample mean; distinct from overnight and weekly summaries.");
        result["body_battery"]=new("score","mean","Source proprietary score, sample mean.");
        foreach(var metric in new[]{"glucose","sensor_glucose","blood_glucose"})
            result[metric]=new("mg/dL","mean","Sample mean, not time in range, time weighted glucose or estimated A1c.",Low:54,High:250,Guidance:GlycemiaGuidance);
        result["basal_rate"]=new("U/h","mean","Sampled rate; never delivered insulin or a total.");
        result["insulin"]=new("U","sum","Sum of recorded doses; not total insulin unless the source says so.",true);
        result["bolus_delivered"]=new("U","sum","Sum of recorded individual boluses; not total insulin.",true);
        result["carbs"]=new("g","sum","Sum of entered carbohydrate; not all meals or consumed food.",true);
        result["carbohydrate_input"]=new("g","sum","Sum of pump-entered carbohydrate; not all meals or consumed food.",true);
        result["auto_insulin_aggregate"]=new("U","mean","Source daily aggregate; cannot combine with individual boluses or infer complete insulin delivery.");
        return result;
    }
    public static MetricMeaning? For(string metric)=>Registered.GetValueOrDefault(metric);
    public static double? Canonical(string metric,double value,string unit)
    {
        var meaning=For(metric);if(meaning is null||!double.IsFinite(value)||value<0)return null;
        if(unit==meaning.Unit)return value;
        if(metric is "glucose" or "sensor_glucose" or "blood_glucose" && unit=="mmol/L")return value*18.0182;
        if(meaning.Unit=="bpm" && unit=="beats/min" || meaning.Unit=="min" && unit=="s")return meaning.Unit=="min"?value/60:value;
        if(meaning.Unit=="s" && unit=="min")return value*60;
        if(meaning.Unit=="m" && unit=="km")return value*1000;
        return null; // No dimensional or name-based guesses.
    }
    public static string? Quality(string quality,string key)=>quality.Split(';').FirstOrDefault(x=>x.StartsWith(key+"=",StringComparison.Ordinal))?[(key.Length+1)..];
    public static DateTime? LocalTime(string quality)=>DateTime.TryParseExact(Quality(quality,"local_time"),"yyyy-MM-ddTHH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out var value)?value:null;
    public static DateTimeOffset? VerifiedInstant(DateTime local,string zone)
    {
        var timezone=TimeZoneInfo.FindSystemTimeZoneById(zone);local=DateTime.SpecifyKind(local,DateTimeKind.Unspecified);
        if(timezone.IsInvalidTime(local)||timezone.IsAmbiguousTime(local))return null;
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local,timezone),TimeSpan.Zero);
    }
}

public record HealthSample(Guid ResourceId,string Provider,string Metric,string Unit,double Value,DateOnly Date,
    DateTimeOffset? At,string Key,string? Device,string Quality,string? Zone,string? Url,string? Revision,
    DateTimeOffset? SourceUpdated,DateTimeOffset Imported);
public record EvidenceRef(Guid ResourceId,string? SourceUrl,string? Revision,string SampleKey,DateOnly Date,DateTimeOffset? ObservedAt,
    string? LocalTime,string Metric,double OriginalValue,string OriginalUnit,double? CanonicalValue,string? CanonicalUnit,DateTimeOffset? SourceUpdated,DateTimeOffset Imported,string Quality,string? SourceTimezone);
public record CleanSample(HealthSample Source,double? Value,bool Conflict,int Copies,HealthSample[] Originals)
{
    public bool Exact=>Value is not null && !Conflict && HealthSemantics.Quality(Source.Quality,"comparison") is null
        && !Source.Quality.Contains("invalid",StringComparison.OrdinalIgnoreCase) && !Source.Quality.Contains("unreliable",StringComparison.OrdinalIgnoreCase);
    public EvidenceRef Evidence=>new(Source.ResourceId,Source.Url,Source.Revision,Source.Key,Source.Date,Source.At,
        HealthSemantics.Quality(Source.Quality,"local_time"),Source.Metric,Source.Value,Source.Unit,Value,HealthSemantics.For(Source.Metric)?.Unit,Source.SourceUpdated,Source.Imported,Source.Quality,Source.Zone);
}
public record DayPoint(DateOnly Date,double Value,int Samples,CleanSample[] Evidence);
public record HealthSeries(string Id,string Provider,string Metric,string Unit,string? Zone,MetricMeaning? Meaning,
    CleanSample[] Samples,DayPoint[] Days);

public static class HealthData
{
    public static HealthSeries[] Prepare(IEnumerable<HealthSample> samples)
    {
        // A sample is its key within its source resource. Keep separate providers.
        var clean=samples.GroupBy(s=>(s.Provider,s.Metric,Identity:s.ResourceId+":"+s.Key))
            .Select(g=>
            {
                var ordered=g.OrderByDescending(s=>s.SourceUpdated).ThenByDescending(s=>s.Imported).ThenBy(s=>s.ResourceId).ToArray();
                var latest=ordered[0];var value=HealthSemantics.Canonical(latest.Metric,latest.Value,latest.Unit);
                var conflict=ordered.Any(s=>HealthSemantics.Canonical(s.Metric,s.Value,s.Unit)!=value ||
                    (value is null && (s.Value!=latest.Value||s.Unit!=latest.Unit)) || s.At!=latest.At || s.Quality!=latest.Quality);
                return new CleanSample(latest,value,conflict,ordered.Length,ordered);
            });
        return clean.GroupBy(s=>(s.Source.Provider,s.Source.Metric,Unit:HealthSemantics.For(s.Source.Metric)?.Unit??s.Source.Unit,s.Source.Zone))
            .Select(g=>
            {
                var meaning=HealthSemantics.For(g.Key.Metric);var all=g.OrderBy(s=>s.Source.Date).ThenBy(s=>s.Source.At).ThenBy(s=>s.Source.Key).ToArray();
                var days=all.Where(s=>s.Exact).GroupBy(s=>s.Source.Date).Select(d=>new DayPoint(d.Key,
                    meaning?.Aggregation=="sum"?d.Sum(s=>s.Value!.Value):d.Average(s=>s.Value!.Value),d.Count(),d.ToArray())).OrderBy(d=>d.Date).ToArray();
                return new HealthSeries($"{g.Key.Provider}:{g.Key.Metric}:{g.Key.Unit}:{g.Key.Zone}",g.Key.Provider,g.Key.Metric,g.Key.Unit,g.Key.Zone,meaning,all,days);
            }).OrderBy(s=>s.Id,StringComparer.Ordinal).ToArray();
    }
    public static bool CalendarCompatible(HealthSeries a,HealthSeries b)=>a.Zone is not null && a.Zone!="unspecified" && a.Zone==b.Zone;
    public static double Median(IEnumerable<double> input)
    {var values=input.Order().ToArray();if(values.Length==0)throw new ArgumentException("Empty series");return values.Length%2==1?values[values.Length/2]:(values[values.Length/2-1]+values[values.Length/2])/2;}
    public static double? Correlation(double[] a,double[] b)
    {
        if(a.Length<3||a.Length!=b.Length)return null;var ma=a.Average();var mb=b.Average();
        var aa=a.Sum(x=>(x-ma)*(x-ma));var bb=b.Sum(x=>(x-mb)*(x-mb));
        return aa<=0||bb<=0?null:Math.Clamp(a.Zip(b).Sum(p=>(p.First-ma)*(p.Second-mb))/Math.Sqrt(aa*bb),-1,1);
    }
}
