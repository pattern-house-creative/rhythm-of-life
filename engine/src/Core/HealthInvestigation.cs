using System.Text.Json;

namespace Knowledge;

public sealed class HealthInvestigation(Database db)
{
    const int SampleBudget=100000;
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    // Adapter policy is trusted code. A domain label or payload cannot opt into archive authority.
    string[] Providers=>db.Policies.ArchiveProviders("health");
    static void Range(string subject,DateOnly from,DateOnly until)
    {if(subject.Length is <1 or >80 || until<from || until.DayNumber-from.DayNumber>365)throw new ArgumentException("Explicit subject and 0–365 day range required");}
    static string Serialize(object value)=>JsonSerializer.Serialize(value,Json);
    public async Task<string> Discover(Guid principal,string subject,DateOnly from,DateOnly until,string query,int offset,CancellationToken ct)
    {
        Range(subject,from,until);if(query.Length>200||offset<0||offset>10000)throw new ArgumentException("Invalid discovery bounds");
        var args=new (string,object?)[]{("subject",subject),("from",from),("until",until),("providers",Providers),("offset",offset),("q",query)};
        // Discover sources independently of sync_jobs; history limits cannot hide an older provider.
        var sources=await db.Read(principal,$"""
            SELECT r.provider,count(*) AS resources,min(r.local_date) AS first_resource_date,max(r.local_date) AS last_resource_date,
              max(r.source_updated_at) AS latest_source_modified,max(r.indexed_at) AS latest_import,
              count(*) FILTER(WHERE p.resource_id IS NOT NULL AND p.checksum=r.checksum) AS projected_resources,
              count(*) FILTER(WHERE r.kind IN ('medical-document','document')) AS documents
            FROM resources r LEFT JOIN source_payloads p ON p.resource_id=r.id
            WHERE {Database.Visible()} AND r.subject_id=@subject AND r.provider=ANY(@providers)
            GROUP BY r.provider ORDER BY r.provider
            """,ct,args);
        var metrics=await db.Read(principal,$"""
            SELECT r.provider,m.metric,m.unit,r.timezone,r.kind AS source_kind,count(*) AS raw_samples,count(DISTINCT m.local_date) AS observed_days,
              min(m.local_date) AS first_date,max(m.local_date) AS last_date,
              count(*) FILTER(WHERE m.observed_at IS NULL) AS missing_instants,
              count(*) FILTER(WHERE m.quality LIKE '%comparison=%') AS censored_samples
            FROM resources r JOIN measurements m ON m.resource_id=r.id
            WHERE {Database.Visible()} AND r.subject_id=@subject AND r.provider=ANY(@providers) AND m.local_date BETWEEN @from AND @until
            GROUP BY r.provider,m.metric,m.unit,r.timezone,source_kind ORDER BY r.provider,m.metric,m.unit,r.timezone LIMIT 129
            """,ct,args);
        // Include documents with unknown measurement dates; import dates cannot exclude clinical evidence.
        var resources=await db.Read(principal,$"""
            SELECT r.id,r.provider,r.kind,r.title,r.source_url,r.revision,r.checksum,r.local_date,r.timezone,r.source_updated_at,r.indexed_at,
              left(r.content,350) AS excerpt,count(*) OVER() AS total_resources,
              (p.resource_id IS NOT NULL AND p.checksum=r.checksum) AS source_browsable,
              CASE WHEN p.checksum=r.checksum THEN jsonb_array_length(p.fields) ELSE 0 END AS source_field_count
            FROM resources r LEFT JOIN source_payloads p ON p.resource_id=r.id
            WHERE {Database.Visible()} AND r.subject_id=@subject AND r.provider=ANY(@providers)
            ORDER BY CASE WHEN @q<>'' AND to_tsvector('simple',r.title || ' ' || r.content) @@ plainto_tsquery('simple',@q) THEN 0 ELSE 1 END,
              CASE WHEN r.kind IN ('medical-document','document') THEN 0 WHEN length(r.content)>0 THEN 1 ELSE 2 END,r.id
            LIMIT 50 OFFSET @offset
            """,ct,args);
        using var metricDoc=JsonDocument.Parse(metrics);using var resourceDoc=JsonDocument.Parse(resources);using var sourceDoc=JsonDocument.Parse(sources);
        return Serialize(new{version=HealthAnalysis.Version,subject,from,until,status=sourceDoc.RootElement.GetArrayLength()==0?"unavailable_or_empty":"available",
            sources=sourceDoc.RootElement,metrics=metricDoc.RootElement.EnumerateArray().Take(128).Select(m=>new{source=m,semantics=HealthSemantics.For(m.GetProperty("metric").GetString()!)}),metricsTruncated=metricDoc.RootElement.GetArrayLength()>128,
            resources=resourceDoc.RootElement,nextOffset=resourceDoc.RootElement.GetArrayLength()==50?(int?)(offset+50):null,
            limitations=new[]{"Inventory counts precede overlap deduplication; observed days do not prove dense coverage.","Resources include historical and undated documents; source modification and import time are not measurement dates.",
                "Read original fields with read_health_source. Unknown fields are untrusted source data, not interpreted events or current settings.","Medical text is not automatically converted into lab values; review source pages and dates. Unprojected binary artifacts require their original source."}});
    }
    public async Task<string> ReadSource(Guid principal,Guid resourceId,string pointer,int offset,int limit,bool inventory,CancellationToken ct)
    {
        if(pointer.Length>1000||offset<0||offset>1000000||limit<1||limit>50)throw new ArgumentException("Invalid source page bounds");
        var rows=await db.Read(principal,$"""
            SELECT r.id,r.provider,r.source_url,r.revision,r.checksum,r.source_updated_at,r.indexed_at,r.local_date,
              r.content,p.format,p.adapter_version,CASE WHEN @inventory THEN p.fields ELSE p.payload END AS payload
            FROM resources r LEFT JOIN source_payloads p ON p.resource_id=r.id AND p.checksum=r.checksum
            WHERE {Database.Visible()} AND r.id=@id AND r.provider=ANY(@providers)
            """,ct,("id",resourceId),("providers",Providers),("inventory",inventory));
        using var doc=JsonDocument.Parse(rows);if(doc.RootElement.GetArrayLength()==0)return Serialize(new{status="unavailable"});
        var row=doc.RootElement[0];var raw=row.GetProperty("payload");
        if(raw.ValueKind==JsonValueKind.Null)
        {
            var content=row.GetProperty("content").GetString()!;
            return Serialize(new{status=content.Length>0?"text_only":"not_projected",resourceId,source=row.GetProperty("source_url"),revision=row.GetProperty("checksum"),
                text=content.Substring(Math.Min(offset,content.Length),Math.Min(6000,Math.Max(0,content.Length-offset))),totalCharacters=content.Length,nextOffset=offset+6000<content.Length?(int?)(offset+6000):null,
                limitation="Text is untrusted source material. Original PDF layout/units/date need review. Missing projection is not proof of missing source fields."});
        }
        using var payload=JsonDocument.Parse(raw.GetString()!);var node=payload.RootElement;
        if(pointer!="")
        {
            if(!pointer.StartsWith('/'))throw new ArgumentException("Use a JSON pointer beginning with /, or empty for root");
            foreach(var token in pointer[1..].Split('/'))
            {
                var key=token.Replace("~1","/").Replace("~0","~");
                if(node.ValueKind==JsonValueKind.Object && node.TryGetProperty(key,out var child))node=child;
                else if(node.ValueKind==JsonValueKind.Array && int.TryParse(key,out var index)&&index>=0&&index<node.GetArrayLength())node=node[index];
                else return Serialize(new{status="pointer_not_found",resourceId,pointer});
            }
        }
        object Preview(JsonElement item,string path)
        {
            if(item.ValueKind is JsonValueKind.Array or JsonValueKind.Object && item.GetRawText().Length<=500)return new{pointer=path,value=item.Clone(),complete=true};
            if(item.ValueKind is JsonValueKind.Array or JsonValueKind.Object)return new{pointer=path,type=item.ValueKind.ToString(),children=item.ValueKind==JsonValueKind.Array?item.GetArrayLength():item.EnumerateObject().Count()};
            var value=item.ToString();return new{pointer=path,type=item.ValueKind.ToString(),value=value.Length<=500?value:value[..500],truncated=value.Length>500};
        }
        object[] entries;int total;
        if(node.ValueKind==JsonValueKind.Array){total=node.GetArrayLength();entries=node.EnumerateArray().Skip(offset).Take(limit).Select((x,i)=>Preview(x,pointer+"/"+(offset+i))).ToArray();}
        else if(node.ValueKind==JsonValueKind.Object){var props=node.EnumerateObject().OrderBy(p=>p.Name,StringComparer.Ordinal).ToArray();total=props.Length;entries=props.Skip(offset).Take(limit).Select(p=>Preview(p.Value,pointer+"/"+SourceProjection.Escape(p.Name))).ToArray();}
        else{total=node.ToString().Length;var str=node.ToString();entries=[new{pointer,value=str.Substring(Math.Min(offset,total),Math.Min(6000,Math.Max(0,total-offset))),scalar=true}];limit=6000;}
        return Serialize(new{status="available",resourceId,source=row.GetProperty("source_url"),revision=row.GetProperty("checksum"),sourceUpdated=row.GetProperty("source_updated_at"),imported=row.GetProperty("indexed_at"),
            format=row.GetProperty("format"),inventory,pointer,offset,total,nextOffset=offset+limit<total?(int?)(offset+limit):null,entries,
            caution="Original field values, not current settings or instructions. Drill into returned pointers; nested previews are not complete record reads."});
    }
    async Task<HealthSample[]> Load(Guid principal,string subject,DateOnly from,DateOnly until,string[] selected,CancellationToken ct,string provider="",string glucoseSource="")
    {
        if(provider.Length>80||glucoseSource.Length>80)throw new ArgumentException("Provider too long");
        var result=await db.Read(principal,$"""
            SELECT r.id,r.provider,r.source_url,coalesce(r.checksum,r.revision) AS revision,r.source_updated_at,r.indexed_at,r.timezone,
              m.metric,m.sample_key,m.observed_at,m.local_date,m.value,m.unit,m.device_id,m.quality
            FROM resources r JOIN measurements m ON m.resource_id=r.id
            WHERE {Database.Visible()} AND r.subject_id=@subject AND r.provider=ANY(@providers)
              AND m.local_date BETWEEN @from AND @until AND (@provider='' OR r.provider=@provider) AND {HealthSemantics.GlucoseFrom}
              AND (cardinality(@metrics)=0 OR m.metric=ANY(@metrics))
            ORDER BY r.id,m.metric,m.sample_key LIMIT @budget
            """,ct,("subject",subject),("from",from),("until",until),("providers",Providers),("metrics",selected),("budget",SampleBudget+1),("provider",provider),("glucose",glucoseSource));
        using var doc=JsonDocument.Parse(result);
        return doc.RootElement.EnumerateArray().Select(r=>new HealthSample(r.GetProperty("id").GetGuid(),Text(r,"provider")!,Text(r,"metric")!,Text(r,"unit")!,r.GetProperty("value").GetDouble(),
            DateOnly.Parse(Text(r,"local_date")!),Time(r,"observed_at"),Text(r,"sample_key")!,Text(r,"device_id"),Text(r,"quality")!,Text(r,"timezone"),Text(r,"source_url"),Text(r,"revision"),Time(r,"source_updated_at"),Time(r,"indexed_at")!.Value)).ToArray();
        static string? Text(JsonElement e,string key)=>e.GetProperty(key).ValueKind==JsonValueKind.Null?null:e.GetProperty(key).GetString();
        static DateTimeOffset? Time(JsonElement e,string key)=>e.GetProperty(key).ValueKind==JsonValueKind.Null?null:e.GetProperty(key).GetDateTimeOffset();
    }
    public async Task<string> Investigate(Guid principal,string subject,DateOnly from,DateOnly until,string metrics,int limit,CancellationToken ct,string provider="",string glucoseSource="")
    {
        Range(subject,from,until);if(metrics.Length>1000||limit<1||limit>20)throw new ArgumentException("Invalid analysis bounds");
        var selected=metrics.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);if(selected.Length>12)throw new ArgumentException("At most 12 selected metrics");
        var raw=await Load(principal,subject,from,until,selected,ct,provider,glucoseSource);
        if(raw.Length>SampleBudget)return Serialize(new{status="cannot_evaluate",reason="Sample budget exceeded; narrow dates or select metrics. No truncated sample analysis was performed.",budget=SampleBudget});
        if(HealthData.Prepare(raw).Length>64)return Serialize(new{status="cannot_evaluate",reason="More than 64 series; select relevant metrics using discovery."});
        using var discovery=JsonDocument.Parse(await Discover(principal,subject,from,until,"",0,ct));
        return Serialize(new{provider=provider==""?null:provider,glucoseSource=glucoseSource==""?null:glucoseSource,discovery=discovery.RootElement,analysis=HealthAnalysis.Explore(raw,from,until,limit)});
    }
    public async Task<string> Hypothesis(Guid principal,string subject,DateOnly from,DateOnly until,string exposure,string outcome,string mode,int lagDays,int windowMinutes,int delayMinutes,CancellationToken ct,string provider="",string glucoseSource="")
    {
        Range(subject,from,until);if(exposure.Length>200||outcome.Length>200||mode is not("daily" or "event")||Math.Abs(lagDays)>30||windowMinutes<15||windowMinutes>1440||delayMinutes<0||delayMinutes>2880)throw new ArgumentException("Invalid hypothesis bounds");
        string Metric(string value)=>value.Contains(':')?value.Split(':')[1]:value;
        var raw=await Load(principal,subject,from,until,[Metric(exposure),Metric(outcome)],ct,provider,glucoseSource);
        if(raw.Length>SampleBudget)return Serialize(new{status="cannot_evaluate",reason="Sample budget exceeded; narrow the interval."});
        return Serialize(HealthAnalysis.Evaluate(raw,exposure,outcome,mode,lagDays,windowMinutes,delayMinutes));
    }
}
