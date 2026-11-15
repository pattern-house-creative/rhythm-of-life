using System.Text.Json;

namespace Knowledge;

// `lexical` is the full-text search the two search tools use; by default the one the settings choose (LexicalSearch.For).
public sealed class Retrieval(Database db, ISourceValidation? validation = null, LexicalSearch? lexical = null)
{
    LexicalSearch Lexical => lexical ?? (RhythmConfig.Loaded ? LexicalSearch.For(RhythmConfig.Current) : LexicalSearch.Standard);
    // The columns every catalog read returns, combined retrieval (Recall) included.
    internal const string Columns = "r.id,r.subject_id,r.provider,r.external_id,r.domain,r.kind,r.title,r.source_url,r.revision,r.source_updated_at,r.indexed_at,r.local_date,r.timezone,r.metadata,(now()-r.indexed_at>interval '24 hours') AS stale";
    // One row per sample: a sample re-sent under the same key within its source counts once, the latest source
    // revision winning. Samples of different providers are never merged.
    static string Samples => $"""
        SELECT DISTINCT ON (r.provider,m.metric,m.unit,r.id::text || ':' || m.sample_key)
          r.id AS resource_id,r.subject_id,r.provider,r.source_url,r.source_updated_at,r.indexed_at,r.timezone,r.kind AS source_kind,
          m.metric,m.sample_key,m.observed_at,m.local_date,m.value,m.unit,m.device_id,m.quality
        FROM measurements m JOIN resources r ON r.id=m.resource_id
        WHERE {Database.Visible()} AND r.subject_id=@subject AND (@provider='' OR r.provider=@provider) AND {HealthSemantics.GlucoseFrom}
          AND m.local_date BETWEEN @from AND @until
        ORDER BY r.provider,m.metric,m.unit,r.id::text || ':' || m.sample_key,
          r.source_updated_at DESC NULLS LAST,r.indexed_at DESC,r.id
        """;
    // A native memory's rows (provider memory: its own wordless row and its map star) are never a catalog result: the
    // memory store and combined retrieval serve memories (memory.md 10.1), and Validate would drop them after the limit,
    // so they are left out before it and never take a result's place or weigh in the ranking.
    static readonly string NoMemory = $"r.provider<>'{Relationships.MemoryStars.Provider}'";
    public async Task<string> Search(Guid principal,string query,int limit,CancellationToken ct)
    {
        Bound(limit,50); if(query.Length>200) throw new ArgumentException("Query too long");
        var (sql,args)=Lexical.Sql(query,$"{Columns},left(r.content,400) AS excerpt",$"{Database.Visible()} AND {NoMemory}","@limit");
        return await Validate(await db.Read(principal,sql,ct,[..args,("limit",limit)]),ct);
    }
    public async Task<string> SearchResources(Guid principal,string query,string domain,string provider,string kind,string subject,int limit,CancellationToken ct)
    {
        Bound(limit,50);
        if(query.Length>200 || new[]{domain,provider,kind,subject}.Any(x=>x.Length>80)) throw new ArgumentException("Filter too long");
        // Overscan within a fixed budget so a stale live index cannot starve the first page.
        // No candidate count or cursor discloses resources that fail the live-source check.
        var (sql,args)=Lexical.Sql(query,$"{Columns},left(r.content,400) AS excerpt",$"""
            {Database.Visible()} AND {NoMemory} AND (@domain='' OR r.domain=@domain) AND (@provider='' OR r.provider=@provider)
              AND (@kind='' OR r.kind=@kind) AND (@subject='' OR r.subject_id=@subject)
            ""","200");
        var data=await db.Read(principal,sql,ct,[..args,("domain",domain),("provider",provider),("kind",kind),("subject",subject)]);
        return await Validate(data,ct,limit);
    }
    public async Task<string> Resource(Guid principal,Guid id,CancellationToken ct) => await Validate(await db.Read(principal,$"SELECT {Columns},left(r.content,8000) AS content,(length(r.content)>8000) AS truncated FROM resources r WHERE {Database.Visible()} AND r.id=@id",ct,("id",id)),ct);
    public async Task<string> Related(Guid principal,Guid id,int limit,CancellationToken ct)
    {
        Bound(limit,50);
        if(await Resource(principal,id,ct)=="[]")return "[]";
        // Links in both directions; `direction` says whether each one points away from this resource or to it.
        return await Validate(await db.Read(principal,$"SELECT {Columns},l.type,l.verification,l.evidence,CASE WHEN l.source_id=@id THEN 'outgoing' ELSE 'incoming' END AS direction FROM relationships l JOIN resources origin ON origin.id=@id JOIN resources r ON r.id=CASE WHEN l.source_id=@id THEN l.target_id ELSE l.source_id END WHERE (l.source_id=@id OR l.target_id=@id) AND l.lifecycle='active' AND {Database.Visible("origin")} AND {Database.Visible()} AND {NoMemory} ORDER BY l.created_at,r.id LIMIT @limit",ct,("id",id),("limit",limit)),ct);
    }

    public Task<string> Metrics(Guid principal,string subject,string metric,DateOnly from,DateOnly until,int limit,CancellationToken ct,string provider="",string glucoseSource="")
    {
        Bound(limit,500); Range(from,until); if(metric.Length>80 || provider.Length>80 || glucoseSource.Length>80) throw new ArgumentException("Metric or provider too long");
        return db.Read(principal,$"WITH samples AS ({Samples}) SELECT *,count(*) OVER() AS total_samples FROM samples WHERE metric=@metric ORDER BY local_date,observed_at NULLS LAST,quality,sample_key LIMIT @limit",ct,("subject",subject),("metric",metric),("from",from),("until",until),("limit",limit),("provider",provider),("glucose",glucoseSource));
    }

    public Task<string> Trends(Guid principal,string subject,string first,string second,DateOnly from,DateOnly until,CancellationToken ct,string provider="",string glucoseSource="")
    {
        Range(from,until); if(provider.Length>80 || glucoseSource.Length>80) throw new ArgumentException("Provider too long");
        // Aggregate separately by day AND unit; missing samples stay missing. Never mix people/units.
        return db.Read(principal,$"""
            WITH samples AS ({Samples})
            SELECT local_date,metric,unit,provider,timezone,count(*) AS samples,
            count(*) FILTER(WHERE quality LIKE '%;comparison=%') AS censored_samples,
            avg(value) FILTER(WHERE quality NOT LIKE '%;comparison=%') AS mean,
            min(value) FILTER(WHERE quality NOT LIKE '%;comparison=%') AS minimum,
            max(value) FILTER(WHERE quality NOT LIKE '%;comparison=%') AS maximum,
            array_agg(DISTINCT resource_id) AS resource_ids,array_agg(DISTINCT source_url) AS source_urls,min(indexed_at) AS oldest_index,
            bool_and(observed_at IS NOT NULL) AS all_sample_instants_known
            FROM samples WHERE metric IN (@first,@second)
            GROUP BY local_date,metric,unit,provider,timezone ORDER BY local_date,metric LIMIT 732
            """,ct,("subject",subject),("first",first),("second",second),("from",from),("until",until),("provider",provider),("glucose",glucoseSource));
    }

    // Connector jobs (adapters that keep sync jobs) and every plugin's or adapter's latest accepted ingest.
    public async Task<string> Status(Guid principal,CancellationToken ct)
    {
        var jobs=await db.Read(principal,$"""
            SELECT j.id,j.subject_id,j.provider,j.from_date,j.until_date,j.next_date,j.status,j.attempts,j.last_error,j.updated_at,j.metadata,
              (SELECT jsonb_agg(x) FROM (SELECT r.kind,count(*) AS resources,max(r.source_updated_at) AS latest_source_modified
                FROM resources r WHERE r.subject_id=j.subject_id AND r.provider=j.provider AND {Database.Visible()}
                GROUP BY r.kind) x) AS archive_coverage
            FROM sync_jobs j WHERE {Database.Allowed("j.subject_id","j.provider","'private'")}
            ORDER BY j.updated_at DESC LIMIT 20
            """,ct);
        var ingest=await db.Read(principal,$"""
            SELECT f.plugin,f.provider,f.subject_id,f.last_accepted_at,f.last_observed_at,
              (SELECT count(*) FROM ingest_calls c WHERE c.plugin=f.plugin AND c.provider=f.provider AND c.subject_id=f.subject_id
                AND c.received_at>now()-interval '24 hours') AS calls_last_24h
            FROM ingest_freshness f WHERE {Database.Allowed("f.subject_id","f.provider","CASE WHEN f.subject_id='household' THEN 'shared' ELSE 'private' END")}
            ORDER BY f.last_accepted_at DESC LIMIT 50
            """,ct);
        return "{\"jobs\":"+jobs+",\"ingest\":"+ingest+"}";
    }
    static void Bound(int n,int max) { if(n<1 || n>max) throw new ArgumentException($"Limit must be 1–{max}"); }
    // The rows of `data` a reader may be served: an archive's, or a live source's whose source still holds that revision.
    internal async Task<string> Validate(string data,CancellationToken ct,int maximum=int.MaxValue)
    {
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        using var json=JsonDocument.Parse(data);var allowed=new List<JsonElement>();
        foreach(var row in json.RootElement.EnumerateArray())
        {
            var provider=row.GetProperty("provider").GetString()!;
            // Archive copies kept in Rhythm are independent originals; other indexes still require live source checks.
            if(db.Policies.For(provider).Authority==SourceAuthority.AuthorizedArchive || validation is not null && await validation.Current(provider,row.GetProperty("external_id").GetString()!,row.GetProperty("revision").GetString(),budget.Token))allowed.Add(row.Clone());
            if(allowed.Count>=maximum)break;
        }
        return JsonSerializer.Serialize(allowed);
    }
    static void Range(DateOnly from,DateOnly until) { if(until<from || until.DayNumber-from.DayNumber>365) throw new ArgumentException("Range must be 0–365 days"); }
}

public interface ISourceValidation
{
    Task<bool> Current(string provider,string externalId,string? revision,CancellationToken ct);
}

// Live-source checks go to the DocumentSource adapter that owns the provider; with none, a live copy is not served.
public sealed class SourceValidation(Adapters.AdapterRegistry adapters) : ISourceValidation
{
    public async Task<bool> Current(string provider,string externalId,string? revision,CancellationToken ct)
    {
        foreach(var source in adapters.Documents.Where(a=>a.Providers.ContainsKey(provider)))
            try { return await source.Current(provider,externalId,revision,ct); }
            catch(Exception e) when(e is HttpRequestException or IOException or JsonException or TaskCanceledException) { return false; }
        return false;
    }
}
