using System.Globalization;
using System.Text.Json;
using Knowledge.Adapters;
using Knowledge.Relationships;

namespace Knowledge;

// Passages by meaning, for one model: `embeddings` makes the vectors and its id tags them, so a query is only ever
// compared with passages of the model that embedded it (resource_chunks.model), and a second model's passages (a shadow
// index, SB-P2-04) can sit beside the first's without either reading the other's. The passages are of the kinds
// PassageSources lists (SB-P2-05): save_document copies, the documents adapter's files, native memories, and adapter
// records whose text their source reads on request (notes, Drive). A search serves only the kinds `serving` covers, by
// default the copies alone, so a text the settings stop listing never answers, whether or not its passages were removed
// yet (EmbeddingBackfill.Forget); an adapter record of a live source (Drive) answers only while `validation` finds its
// revision still the source's current one, as every read of a live source's copy is checked (Retrieval.Validate, D-014),
// within `checkBudget` (by default CheckBudget) for each search.
public sealed class SemanticSearch(Database db,IEmbedder embeddings,PassageSources? serving=null,ISourceValidation? validation=null,TimeSpan? checkBudget=null)
{
    // The most text one row gets passages for: a longer text is indexed from its start, cut at a space. Copies hold at
    // most 40 000 characters and documents' pages as much; a memory at most 131 072 bytes; a Drive file can be longer.
    public const int MaxText=131_072;
    static string Vector(float[] value)=>"["+string.Join(',',value.Select(x=>x.ToString("R",CultureInfo.InvariantCulture)))+"]";

    // Gives one row whose text the database holds (a copy, a document, a memory) its passages under this model: the index
    // job's step (EmbeddingBackfill). Idempotent: a row that already holds this model's passages at its current revision
    // is left as it is and their count returned. Only this model's passages are replaced, all in one transaction with the
    // row locked, so a row never holds a part of a set, nor passages one model embedded under another's id. A memory's text
    // is read only once its row is locked, and a change to that text waits for the same lock (migration 026), so passages
    // are never made from text that has since changed. Null when the row is not one to index (gone, changed, not current,
    // not a kind `sources` covers; by default only a copy saved with a semantic index); 0 when its text gives no passage,
    // which is recorded so the job does not read it again at this revision. Ordinary reads and health sync never generate
    // embeddings.
    public async Task<int?> Index(Guid id,CancellationToken ct,PassageSources? sources=null)
    {
        sources??=PassageSources.CopiesOnly;
        await using var c=await db.Source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        string content;string? revision;bool memory;
        await using(var cmd=Database.Command(c,tx,$"""
            SELECT r.content,r.revision,r.provider='memory' FROM resources r WHERE r.id=@id AND r.status='current'
              AND ((@copies AND {PassageSources.Copy}) OR (@documents AND {PassageSources.Document}) OR (@memories AND {PassageSources.MemoryRow}))
            FOR UPDATE
            """,("id",id),("copies",sources.Copies),("documents",sources.Documents),("memories",sources.Memories)))
        await using(var row=await cmd.ExecuteReaderAsync(ct))
        {
            if(!await row.ReadAsync(ct))return null;
            content=row.GetString(0);revision=row.IsDBNull(1)?null:row.GetString(1);memory=row.GetBoolean(2);
        }
        // A memory's text and the revision it was written at, read only now that its row is held (PassageSources.Revision).
        if(memory)
            await using(var text=Database.Command(c,tx,"SELECT content,'memory:'||text_revision FROM memories WHERE id=@id AND hold IS NULL",("id",id)))
            await using(var row=await text.ExecuteReaderAsync(ct))
            {
                if(!await row.ReadAsync(ct)){await row.DisposeAsync();await tx.CommitAsync(ct);return null;}
                content=row.GetString(0);revision=row.GetString(1);
            }
        if(revision is null){await tx.CommitAsync(ct);return null;}
        if(await Held(c,tx,id,revision,ct) is { } count){await tx.CommitAsync(ct);return count;}
        if(content.Length>MaxText)throw new ArgumentException("Document too large");
        var passages=await Store(c,tx,id,revision,Embedded(content,ct),ct);
        await tx.CommitAsync(ct);return passages;
    }

    // Gives one adapter record its passages under this model, from the text its source reads now (ISearchableSource). The
    // text is read and embedded before the row is locked, so a slow source never holds it; the passages are kept only when
    // the source read the revision the star row still holds once it is locked. Null when the row is not this source's
    // current star, or the source read another revision than the row's (the next map pass brings the row up to date, and a
    // changed row loses its passages: migration 005); 0 when the source has no readable text at this revision, recorded as
    // for Index. A source's failure is thrown, for the job to count and try again.
    public async Task<int?> IndexRecord(Guid id,ISearchableSource source,CancellationToken ct)
    {
        DiscoveryRecord record;string revision;
        await using(var c=await db.Source.OpenConnectionAsync(ct))
        await using(var cmd=Database.Command(c,null,$"SELECT r.relationship_snapshot,r.revision FROM resources r WHERE r.id=@id AND r.status='current' AND {PassageSources.Record} AND {PassageSources.Fresh} AND r.provider=ANY(@providers)",
            ("id",id),("providers",source.Providers.Keys.ToArray()),("fresh",(double)Freshness)))
        await using(var row=await cmd.ExecuteReaderAsync(ct))
        {
            if(!await row.ReadAsync(ct)||row.IsDBNull(1))return null;
            record=JsonSerializer.Deserialize<DiscoveryRecord>(row.GetString(0))!;revision=row.GetString(1);
        }
        await using(var c=await db.Source.OpenConnectionAsync(ct))
        await using(var held=Database.Command(c,null,"SELECT count(*)::int FROM resource_chunks WHERE resource_id=@id AND model=@model AND source_revision=@revision",
            ("id",id),("model",embeddings.Id),("revision",revision)))
            if(await held.ExecuteScalarAsync(ct) is int count && count>0)return count;
        var read=await source.SearchText(record,ct);
        if(read is not null && read.Revision!=revision)return null;
        var embedded=read is null?[]:Embedded(Cut(read.Text),ct);
        await using var lc=await db.Source.OpenConnectionAsync(ct);await using var tx=await lc.BeginTransactionAsync(ct);
        await using(var locked=Database.Command(lc,tx,$"SELECT r.revision FROM resources r WHERE r.id=@id AND r.status='current' AND {PassageSources.Record} AND {PassageSources.Fresh} FOR UPDATE",
            ("id",id),("fresh",(double)Freshness)))
            if(await locked.ExecuteScalarAsync(ct) as string!=revision){await tx.CommitAsync(ct);return null;}
        if(await Held(lc,tx,id,revision,ct) is { } already){await tx.CommitAsync(ct);return already;}
        var passages=await Store(lc,tx,id,revision,embedded,ct);
        await tx.CommitAsync(ct);return passages;
    }

    // A text cut to MaxText characters, at a space when one is near the end, never inside a surrogate pair.
    internal static string Cut(string text)
    {
        if(text.Length<=MaxText)return text;
        var end=MaxText;
        var space=text.LastIndexOfAny([' ','\n','\t'],MaxText-1,1000);
        if(space>0)end=space;
        else if(char.IsHighSurrogate(text[end-1]))end--;
        return text[..end];
    }

    List<(string Text,string Vector)> Embedded(string content,CancellationToken ct)
    {
        var passages=new List<(string,string)>();
        foreach(var chunk in embeddings.Chunks(content))
        {
            ct.ThrowIfCancellationRequested();
            passages.Add((chunk,Vector(embeddings.Embed(chunk))));
        }
        return passages;
    }

    async Task<int?> Held(Npgsql.NpgsqlConnection c,Npgsql.NpgsqlTransaction tx,Guid id,string revision,CancellationToken ct)
    {
        await using var held=Database.Command(c,tx,"SELECT count(*)::int FROM resource_chunks WHERE resource_id=@id AND model=@model AND source_revision=@revision",
            ("id",id),("model",embeddings.Id),("revision",revision));
        return await held.ExecuteScalarAsync(ct) is int count && count>0?count:null;
    }

    // Replaces this model's passages of the row (inside the caller's transaction, the row locked). A text with no passage
    // is recorded in no_passages at this revision instead (migration 026), so the job passes it over until it changes.
    async Task<int> Store(Npgsql.NpgsqlConnection c,Npgsql.NpgsqlTransaction tx,Guid id,string revision,IReadOnlyList<(string Text,string Vector)> passages,CancellationToken ct)
    {
        await using(var clear=Database.Command(c,tx,"DELETE FROM resource_chunks WHERE resource_id=@id AND model=@model",("id",id),("model",embeddings.Id)))await clear.ExecuteNonQueryAsync(ct);
        if(passages.Count==0)
        {
            await using var none=Database.Command(c,tx,"""
                INSERT INTO no_passages(resource_id,source_revision) VALUES(@id,@revision)
                ON CONFLICT(resource_id) DO UPDATE SET source_revision=excluded.source_revision,checked_at=now()
                """,("id",id),("revision",revision));
            await none.ExecuteNonQueryAsync(ct);
            return 0;
        }
        var ordinal=0;
        foreach(var (text,vector) in passages)
        {
            ct.ThrowIfCancellationRequested();
            await using var insert=Database.Command(c,tx,"INSERT INTO resource_chunks(resource_id,ordinal,source_revision,content_hash,model,text_content,embedding) VALUES(@id,@ordinal,@revision,@hash,@model,@text,@vector::public.vector)",
                ("id",id),("ordinal",ordinal++),("revision",revision),("hash",Hashing.Hash(text)),("model",embeddings.Id),("text",text),("vector",vector));
            await insert.ExecuteNonQueryAsync(ct);
        }
        return ordinal;
    }

    // An adapter record's passages are served while the map would serve its star: current, at the revision they were read
    // from, and seen by a map pass within graph.freshnessSeconds, so a file a source stops listing, revokes or fails to
    // return stops answering once that window lapses, as its star does (RelationshipEngine.ReadSnapshot). Its passages
    // are a cached copy of the source's text, never a stand-in for the current document (D-014): `revision` and
    // `indexed_at` say which version and when it was last seen. A live source's record is also checked at read time. With
    // memory.scopes on, only the newest observation of the record's star answers, before the reader's filter, as the map
    // serves the star (RelationshipEngine.Newest): a record a pass lists under a new subject, while its old row is still
    // fresh (a Drive listing past its cap writes no missing notice), answers no reader under its old subject.
    internal static int Freshness=>RhythmConfig.Loaded?RhythmConfig.Current.Graph.FreshnessSeconds:new RhythmConfig.GraphSection().FreshnessSeconds;
    // How many passages a search reads when some may be a live source's, so the ones its check drops leave room for others.
    internal const int LiveOverscan=4,MaxRead=80;
    // A live source's checks run at most this many at once, and a search waits at most CheckBudget for them in all: a
    // record whose check has not answered by then is left out (fail closed) and counted in Unchecked, which the tool says.
    const int Checks=8;
    public static readonly TimeSpan CheckBudget=TimeSpan.FromSeconds(5);
    // How many of a live source's records the last Search left out because their check did not answer in time.
    public int Unchecked{get;private set;}

    public async Task<string> Search(Guid principal,string query,string domain,int limit,CancellationToken ct)
    {
        if(limit<1||limit>20||domain.Length>80||string.IsNullOrWhiteSpace(query)||query.Length>800)throw new ArgumentException("Use a short question and limit 1–20");
        return await Nearest(principal,embeddings.Embed(query),domain,limit,ct);
    }

    // The passages nearest `vector` that this reader is served, as Search reads them, up to `limit` (at most MaxRead).
    // Combined retrieval (Recall) reads its meaning arm here: `where` adds its filters (SQL over the passage's resource,
    // alias r, every row meeting them; `args` binds their parameters), and `vectors` adds each passage's vector as text
    // (a `vector` column) for its diversity step, which no reply returns. `confirmed` names the live records the search's
    // other arm already found current, each at the revision its source confirmed: served at that revision without asking
    // again, also once the search's budget is spent.
    internal async Task<string> Nearest(Guid principal,float[] vector,string domain,int limit,CancellationToken ct,string where="",(string,object?)[]? args=null,bool vectors=false,
        IReadOnlyDictionary<Guid,string>? confirmed=null)
    {
        if(limit<1||limit>MaxRead)throw new ArgumentException($"Limit must be 1–{MaxRead}");
        var served=serving??PassageSources.CopiesOnly;
        Unchecked=0;Answers=new Dictionary<Guid,bool?>();
        // The live sources among the records served, as their adapters declare them (Drive's is one; notes keep an archive).
        var live=served.Records.Where(r=>r.Value.Providers.TryGetValue(r.Key,out var policy)&&policy.Authority==SourceAuthority.LiveSource)
            .Select(r=>r.Key).ToHashSet(StringComparer.Ordinal);
        var rows=await db.Read(principal,$"""
            SELECT r.id,r.subject_id,r.provider,r.domain,r.kind,r.title,r.source_url,r.revision,r.source_updated_at,r.indexed_at,r.metadata,
              k.ordinal AS chunk_ordinal,k.text_content AS excerpt,k.model,
              1-(k.embedding OPERATOR(public.<=>) @vector::public.vector) AS similarity,
              (now()-r.indexed_at>interval '24 hours') AS stale{(vectors?",k.embedding::text AS vector":"")}
            FROM resource_chunks k JOIN resources r ON r.id=k.resource_id {RelationshipEngine.KeysOf("sk")}
            WHERE {Database.Visible()} AND k.source_revision={PassageSources.Revision} AND k.model=@model
              AND ((@copies AND r.metadata->>'semanticIndex'='true') OR (@documents AND ({PassageSources.Document})) OR (@memories AND ({PassageSources.Memory}))
                OR ({PassageSources.Record} AND r.provider=ANY(@providers) AND {PassageSources.Fresh}
                  AND ({Database.ScopesOff} OR {RelationshipEngine.Newest("r", "sk")})))
              AND (@domain='' OR r.domain=@domain){(where.Length==0?"":" AND "+where)}
            ORDER BY k.embedding OPERATOR(public.<=>) @vector::public.vector,r.id,k.ordinal LIMIT @limit
            """,ct,[("vector",Vector(vector)),("model",embeddings.Id),("domain",domain),
            ("limit",live.Count==0?limit:Math.Min(limit*LiveOverscan,MaxRead)),("fresh",(double)Freshness),
            ("copies",served.Copies),("documents",served.Documents),("memories",served.Memories),("providers",served.Providers),..args??[]]);
        return live.Count==0?rows:await Checked(rows,live,limit,ct,confirmed);
    }

    // The passages of `rows`, in order, up to `limit`, less those of a live source's record whose revision the source no
    // longer holds (edited, trashed, unshared or gone), or that could not be checked within the budget: fail closed. The
    // checks go in waves, each of the records the first `limit` passages still need (counting a record not yet checked as
    // current), Checks at a time, so a search asks the source about no more records than it may serve and waits for the
    // slowest of a few rather than for each in turn.
    async Task<string> Checked(string rows,IReadOnlySet<string> live,int limit,CancellationToken ct,IReadOnlyDictionary<Guid,string>? confirmed=null)
    {
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);
        // None left (combined retrieval's other arm spent it): nothing more is asked.
        if(checkBudget is { } given&&given<=TimeSpan.Zero)budget.Cancel();else budget.CancelAfter(checkBudget??CheckBudget);
        using var json=JsonDocument.Parse(rows);
        var all=json.RootElement.EnumerateArray().ToArray();
        // Per live record: true when current, false when not, null when its check did not answer in time.
        var current=new Dictionary<Guid,bool?>();
        Guid? Live(JsonElement row)=>live.Contains(row.GetProperty("provider").GetString()!)?row.GetProperty("id").GetGuid():null;
        // A record this search already confirmed at the revision its passages are of (Nearest's `confirmed`).
        if(confirmed is not null)
            foreach(var row in all)
                if(Live(row) is { } known&&confirmed.TryGetValue(known,out var at)&&at==row.GetProperty("revision").GetString())current[known]=true;
        using var gate=new SemaphoreSlim(Checks);
        while(true)
        {
            var wanted=new Dictionary<Guid,JsonElement>();
            var counted=0;
            foreach(var row in all)
            {
                if(counted>=limit)break;
                if(Live(row) is not { } id){counted++;continue;}
                if(current.TryGetValue(id,out var ok)){if(ok==true)counted++;continue;}
                wanted.TryAdd(id,row);
                counted++;
            }
            if(wanted.Count==0)break;
            foreach(var (id,ok) in await Task.WhenAll(wanted.Select(async w=>
            {
                try{await gate.WaitAsync(budget.Token);}
                catch(OperationCanceledException){return (w.Key,(bool?)null);}
                try
                {
                    var ok=await Current(w.Key,w.Value.GetProperty("provider").GetString()!,w.Value.GetProperty("revision").GetString(),budget.Token);
                    return (w.Key,ok?true:budget.IsCancellationRequested?(bool?)null:false);
                }
                finally{gate.Release();}
            })))
                current[id]=ok;
        }
        Unchecked=current.Count(c=>c.Value is null);
        Answers=current;
        return JsonSerializer.Serialize(all.Where(row=>Live(row) is not { } id||current.GetValueOrDefault(id)==true).Take(limit).ToArray());
    }
    // Each live record the last Search checked or took as confirmed: true current, false not, null not answered in time
    // (Unchecked counts those). Combined retrieval counts a record as left out only when no arm got its source's answer
    // (Recall.Search).
    internal IReadOnlyDictionary<Guid,bool?> Answers{get;private set;}=new Dictionary<Guid,bool?>();

    // Whether a live source still holds each record's revision, asked by the record's own source id (its star's
    // Key.ExternalId, never the namespaced catalog id), Checks at a time and within `budget` in all, as Search checks the
    // passages it serves: true when current, false when not or with no check, null when the check did not answer in
    // time. Served only when true (fail closed). Combined retrieval's keyword arm checks here the records it finds by their
    // passages (Recall, SB-P2-19).
    internal static async Task<Dictionary<Guid,bool?>> Confirm(ISourceValidation? validation,IReadOnlyList<(Guid Id,string Provider,string? Source,string? Revision)> records,
        TimeSpan budget,CancellationToken ct)
    {
        var answers=new Dictionary<Guid,bool?>();
        if(records.Count==0)return answers;
        using var timer=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(budget);
        using var gate=new SemaphoreSlim(Checks);
        foreach(var (id,ok) in await Task.WhenAll(records.DistinctBy(r=>r.Id).Select(async r=>
        {
            if(validation is null||r.Source is null||r.Revision is null)return (r.Id,(bool?)false);
            try{await gate.WaitAsync(timer.Token);}
            catch(OperationCanceledException){return (r.Id,(bool?)null);}
            try
            {
                var current=await validation.Current(r.Provider,r.Source,r.Revision,timer.Token);
                return (r.Id,current?true:timer.IsCancellationRequested?(bool?)null:false);
            }
            catch(OperationCanceledException){return (r.Id,(bool?)null);}
            finally{gate.Release();}
        })))
            answers[id]=ok;
        return answers;
    }

    // Whether a live source still holds the revision a record's passages were read at: its source id is the one its star
    // names (the snapshot's key), not the namespaced catalog id. False without a check, or when it fails or runs out of time.
    async Task<bool> Current(Guid id,string provider,string? revision,CancellationToken ct)
    {
        if(validation is null||revision is null||ct.IsCancellationRequested)return false;
        try
        {
            await using var cmd=db.Source.CreateCommand("SELECT r.relationship_snapshot->'Key'->>'ExternalId' FROM resources r WHERE r.id=@id AND r.relationship_snapshot IS NOT NULL");
            cmd.Parameters.AddWithValue("id",id);
            return await cmd.ExecuteScalarAsync(ct) is string source && await validation.Current(provider,source,revision,ct);
        }
        catch(OperationCanceledException){return false;}
    }
}
