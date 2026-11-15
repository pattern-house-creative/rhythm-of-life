using System.Globalization;
using System.Text.Json;
using Knowledge.Adapters;
using Npgsql;

namespace Knowledge;

// Combined retrieval (SB-P2-08, docs/dev/second-brain-parity/combined-retrieval.md): one ranked list, for one reader,
// from the two arms the separate tools read, with the read predicate on every path (docs/dev/memory.md section 7).
// It is served only behind its switches (RecallSettings): the `recall` tool (SB-P2-09) and the app's explorer search
// (SB-P2-10) with engine.recall on, and search_knowledge with engine.searchKnowledge combined. Off, as on every install
// whose settings predate them, search_knowledge, search_resources and semantic_search answer exactly as before.
//   keyword   ranked full text over the catalog, as search_knowledge ranks it (LexicalSearch.Standard: identifier hits,
//             rarity-weighted words, cover density, newest, the source's id), KeywordPool rows, a live source's row
//             served only while its source still holds that revision (Retrieval.Validate); with them, in one ranking,
//             a native memory by its text in memories (never by its own wordless row, migration 033, and never a held
//             one) and a record whose passages are served by its passages' words (SB-P2-19, migration 040; Keyword);
//   meaning   the passages nearest the question, as semantic_search serves them (SemanticSearch.Nearest: the kinds the
//             settings list, a live source's record only while its source holds the passages' revision), DensePool
//             passages, each resource at the rank of its nearest one.
// They are fused by reciprocal rank: each arm adds weight / (K + rank) to every resource it found, the meaning arm with
// weight 1 and the keyword arm with the rarity of the query's words the row holds (lexical_weight), so an exact id or a
// rare name outweighs a common word, and a resource both arms found gathers from both; a keyword result the meaning arm
// could never find (no passages: a data series, a reference-only record) gets the meaning arm's share at its keyword
// rank (Impute). Then, in order:
//   filters   domain, provider, kind, subject and a window on the source's own time, inside every arm's query; a
//             native memory held for review (D-039, D-050), deprecated or no longer true (valid_until passed) is never
//             a candidate, and with Memories off (search_knowledge's combined mode) no native memory is;
//   diversity each resource is one result, at its best; maximal marginal relevance over the meaning arm's passage
//             vectors is built (Lambda below 1) but off, as measured; greedy, so its first n picks never depend on how
//             many are asked for, in blocks of Block each ordered by score;
//   hops      with hops above 0 (the default is 0, D-040): typed links from the first Block results, each hop's score
//             Decay times its parent's, both ends readable by this reader and the neighbour checked as
//             get_related_resources checks it, but for a native memory's own row, Rhythm's own record, which no source
//             checks (a held one never reached); the two best neighbours not among the first two blocks take the 5th
//             and 10th places (GraphSlots);
//   prefix    the list is built to MaxTopK whatever topK asks for and cut last, so asking for more only extends it;
//   budget    excerpts within OutputBudget characters in all: the first FullMatches results scoring at least
//             StrongRatio of the first's get up to FullChars each, the rest Snippet, and a cut excerpt says so and how
//             long the whole text is, for get_resource to read the rest;
//   fallback  without the local model, or when the meaning arm fails, the keyword arm alone, with a notice that
//             semantic search was skipped (RET-24, story 5); it never falls back to an unfiltered read.
// Every statistic a ranking uses is over rows this reader may read: the keyword weight among the candidates its
// predicate admits (LexicalSearch), the ranks within each arm's readable rows, diversity among its own results
// (memory.md 6.2). No reply holds a vector.
// Adapted from Second Brain's recall (MIT): reciprocal rank fusion with k 60 and keyword weights from rarity
// (src/recall/rrf.ts, search.ts), maximal marginal relevance in blocks of five (math.ts), graph places 5 and 10 with
// decay 0.6 (neighborhood.ts, graph/traverse.ts), the output budget (snippet.ts) and the notice when vectors fail.
// A live source's checks wait at most `checkBudget` (by default SemanticSearch.CheckBudget, 5 s) in all for one search,
// both arms together, as semantic_search waits for its own (D-085).
// With `rerank` (engine.rerank, off by default; SB-P2-12, RecallRerank), a cross-encoder reorders the first results after
// the diversity step and before any hop; when it is not ready, rests after failures or fails, the list stays as fused
// and a notice says so.
public sealed class Recall(Database db, IEmbedder? embeddings, PassageSources? serving = null, ISourceValidation? validation = null,
    RecallOptions? options = null, string embeddingsStatus = "off", RecallLog? log = null, TimeSpan? checkBudget = null,
    RecallRerank? rerank = null)
{
    public RecallOptions Options { get; } = options ?? RecallOptions.Default;
    TimeSpan CheckBudget => checkBudget ?? SemanticSearch.CheckBudget;
    // The model this search reads questions with, for what else a question is held against (standing instructions, SB-P5-12).
    public IEmbedder? Embedder => embeddings;
    readonly Retrieval catalog = new(db, validation, LexicalSearch.Standard);
    // A native memory's own row: Rhythm is its system of record, so no source checks it (Retrieval.Validate would drop it
    // as an unknown live source). Its words are in memories, not in the row (033: the title Memory, no content).
    const string NativeMemory = "(r.provider='memory' AND r.relationship_snapshot IS NULL)";
    // A held memory (D-039) is in no arm and no hop: its own row, and its map star's row (SB-P3-11), which stands for it.
    static readonly string NotHeld = $"NOT (r.provider='memory' AND EXISTS(SELECT 1 FROM memories m WHERE m.id={Relationships.MemoryStars.Answering("r")} AND m.hold IS NOT NULL))";
    // Nor is a memory that is no longer true (its valid_until passed) or deprecated (D-030): the map drops its star at once
    // (MemoryStars.Served), and upstream's recall leaves such rows out so that they never take a place from one still
    // true. Judged at `at`, now; SB-P2-16's as-of question will judge it at another time.
    static string Current(string at = "now()") =>
        $"NOT (r.provider='memory' AND EXISTS(SELECT 1 FROM memories m WHERE m.id={Relationships.MemoryStars.Answering("r")} AND (m.valid_until<={at} OR m.status='deprecated')))";
    // The columns of a result: search_knowledge's, but a native memory's revision is the memory's own (`memory:` and its
    // revision in memories, which every change of it moves). Its resources row has none (033), so a receipt over it, which
    // hashes each result's revision, would stay the same however the memory changed.
    const string Revision = "CASE WHEN " + NativeMemory + " THEN (SELECT 'memory:'||m.revision FROM memories m WHERE m.id=r.id) ELSE r.revision END";
    static readonly string Columns = Retrieval.Columns.Replace("r.revision,", Revision + " AS revision,", StringComparison.Ordinal);
    const string Stable = "r.provider,r.external_id,r.id";

    // The passages semantic_search serves, by default the save_document copies alone (PassageSources.CopiesOnly).
    PassageSources Served => serving ?? PassageSources.CopiesOnly;
    // A record whose passages are served (SB-P2-19): a star row of a source the settings list for passages
    // (adapters.embeddings.sources), seen by a pass within graph.freshnessSeconds and, with scopes on, its record's newest
    // observation, as semantic_search serves its passages (SemanticSearch.Nearest). Its text is its passages: its row has
    // none. SQL over r, with the join PassageJoin and the parameters PassageArgs.
    static readonly string RecordPassages = $"{PassageSources.Record} AND r.provider=ANY(@lexical_providers) AND {PassageSources.Fresh} "
        + $"AND ({Database.ScopesOff} OR {Relationships.RelationshipEngine.Newest("r", "sk")})";
    bool Passages => Served.Records.Count > 0;
    string PassageJoin => Passages ? Relationships.RelationshipEngine.KeysOf("sk") : "";
    (string, object?)[] PassageArgs => Passages
        ? [("lexical_model", embeddings?.Id ?? ""), ("lexical_providers", Served.Providers), ("fresh", (double)SemanticSearch.Freshness)]
        : [];
    // The live sources among them (Drive's is one; notes keep an archive): their records are served only while the source
    // still holds the revision, asked at each read.
    IReadOnlySet<string> LiveSources => Served.Records.Where(r => r.Value.Providers.TryGetValue(r.Key, out var policy) && policy.Authority == SourceAuthority.LiveSource)
        .Select(r => r.Key).ToHashSet(StringComparer.Ordinal);

    // The start of a row's text and its length, for the excerpts: a native memory's from its text in memories (never a
    // held one's: NotHeld keeps those out of every arm), a record's whose passages are served from them (its row holds
    // none of its words), any other row's from its own content.
    // As of a moment (RecallAsOf), a native memory's are its words then, from the state it had at @asof.
    string Head(int chars, bool asOf = false) => $"CASE WHEN {NativeMemory} THEN {(asOf ? RecallAsOf.Head(chars) : $"(SELECT left(m.content,{chars}) FROM memories m WHERE m.id=r.id AND m.hold IS NULL)")} "
        + (Passages ? $"WHEN {RecordPassages} THEN left({LexicalSearch.PassageText("@lexical_model")},{chars}) " : "") + $"ELSE left(r.content,{chars}) END";
    string Length(bool asOf = false) => "CASE WHEN " + NativeMemory + " THEN " + (asOf ? RecallAsOf.Length : "coalesce((SELECT length(m.content) FROM memories m WHERE m.id=r.id AND m.hold IS NULL),0)") + " "
        + (Passages ? $"WHEN {RecordPassages} THEN length({LexicalSearch.PassageText("@lexical_model")}) " : "") + "ELSE length(r.content) END";

    public async Task<RecallResult> Search(Guid principal, RecallQuery q, CancellationToken ct)
    {
        Check(q, Options);
        var o = Options;
        var given = q;
        var asked = q.Query;
        // With TimePhrases (SB-P2-16, off: no gain shown), a time phrase in the question and no window asked for becomes
        // the window, read in the install's time zone, and leaves the question; never in a question asked as of a moment.
        if (o.TimePhrases && q.After is null && q.Before is null && q.AsOf is null
            && TimePhrases.Parse(q.Query, o.Now ?? DateTimeOffset.UtcNow, Zone(o.Zone)) is { } phrase)
            q = q with { Query = phrase.Query, After = phrase.After, Before = phrase.Before };
        // As of a moment (SB-P4-07, RecallAsOf): one that has passed, by the clock that stamps every memory.
        var asOf = q.AsOf is not null;
        if (q.AsOf is { } moment && !await Passed(principal, moment, ct)) throw new ArgumentException("as_of must be a time that has passed");
        var (filter, args) = Filters(q, o.Memories);
        var notices = new List<string>();
        // A live source's check, asked once per search for each record and revision it confirms, whichever arm asks first,
        // and within one budget for the whole search: the meaning arm waits only for what the keyword arm's checks left of
        // it, and serves without asking again what they confirmed, so a slow source holds a search up once, not once an arm.
        var checks = validation is null ? null : new ConfirmedChecks(validation);
        var (keyword, answers, confirmed, spent) = await Keyword(principal, q.Query, filter, args, checks, ct);
        IReadOnlyList<RecallArmHit> meaning = [];
        var skipped = false;
        // Each live record's check in this search: the source's answer from either arm wins over a check that did not
        // answer in time, so a file one arm could not check and the other confirmed (and served) is not counted as left out.
        var checkedRecords = new Dictionary<Guid, bool?>(answers);
        if (embeddings is null)
        {
            skipped = true;
            notices.Add($"Semantic search was skipped: the local embeddings model is {embeddingsStatus}. These are keyword results only: exact words, names and ids still match, a question in other words may not.");
        }
        else
            try
            {
                var left = CheckBudget - spent;
                var semantic = new SemanticSearch(db, embeddings, serving, checks, left > TimeSpan.Zero ? left : TimeSpan.Zero);
                meaning = Meaning(await semantic.Nearest(principal, embeddings.Embed(q.Query), "", o.DensePool, ct, filter, args, vectors: o.Lambda < 1,
                    confirmed: confirmed));
                foreach (var (id, answer) in semantic.Answers) checkedRecords[id] = checkedRecords.GetValueOrDefault(id) ?? answer;
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is NpgsqlException or TimeoutException or InvalidOperationException
                or Microsoft.ML.OnnxRuntime.OnnxRuntimeException)
            {
                skipped = true;
                meaning = [];
                notices.Add("Semantic search failed this time and was skipped. These are keyword results only: exact words, names and ids still match, a question in other words may not.");
            }
        var unchecked_ = checkedRecords.Count(c => c.Value is null);
        if (unchecked_ > 0)
            notices.Add($"{unchecked_} connected file{(unchecked_ == 1 ? "" : "s")} left out: the source did not confirm in time that the passages found are of its current version. Ask again, or open the file at its source.");

        var rows = await Rows(principal, [.. keyword.Select(k => k.Id).Concat(meaning.Select(m => m.Id)).Distinct()], skipped ? null : embeddings!.Id, ct,
            asOf, args);
        var fused = OneRecord(Fuse(keyword, meaning, rows, o, !skipped));
        // As of a moment, each native memory's state then; the beliefs (marked wrong since) leave the ranked list before
        // anything ranks by it, and are listed under it at the end.
        var then = new Dictionary<Guid, RecallThen>();
        List<RecallCandidate> believed = [];
        if (q.AsOf is { } at)
        {
            await Resolved(principal, at, fused, then, ct);
            believed = [.. fused.Where(c => then.TryGetValue(c.Id, out var t) && t.RetractedAt is not null)];
            fused = [.. fused.Where(c => !IsMemory(c) || then.TryGetValue(c.Id, out var t) && t.RetractedAt is null)];
        }
        var ordered = Diversify(fused, o);
        if (rerank is not null)
        {
            var (reranked, notice) = rerank.Apply(q.Query, ordered, ct);
            ordered = reranked;
            if (notice is not null) notices.Add(notice);
        }
        // A hop as of a moment reaches only what was true then: no belief takes a graph place.
        if (q.Hops > 0 && ordered.Count > 0)
            ordered = await Expand(principal, ordered, q.Hops, asOf ? Filters(q, o.Memories, beliefs: false).Sql : filter, args, ct, asOf);
        if (q.AsOf is { } hopped)
        {
            await Resolved(principal, hopped, ordered, then, ct);
            ordered = [.. ordered.Where(c => !IsMemory(c) || then.TryGetValue(c.Id, out var t) && t.RetractedAt is null)];
            foreach (var c in ordered.Concat(believed).Where(IsMemory)) Words(c);
        }
        var hits = Budget(ordered.Take(o.MaxTopK).ToList(), o);
        if (asOf) hits = [.. hits.Select(h => then.TryGetValue(h.Id, out var t) ? h with { Then = t } : h)];
        List<RecallHit> answered = [.. hits.Take(q.TopK)];
        var beliefs = q.AsOf is { } asked2 ? await Beliefs(principal, asked2, answered, believed, then, filter, args, skipped ? null : embeddings!.Id, ct) : null;
        RecallResult result = new(asked, answered, notices, skipped, unchecked_, q.AsOf, beliefs);
        // With the opt-in log on (SB-P2-11): kept in the reader's scope with the receipt the answer carries, which is over the
        // question as asked (given, before a time phrase leaves it); the answer is the same whether it was kept or not.
        if (log is not null && o.Logged) await log.Write(principal, given, result.Hits, Mcp.RecallReply.Receipt(given, result.Hits, result.Retracted), ct);
        return result;
    }

    // As of a moment (RecallAsOf): whether it has passed by the database's clock, which stamps every memory and its states.
    async Task<bool> Passed(Guid principal, DateTimeOffset at, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await db.Read(principal, "SELECT @asof<=clock_timestamp() AS passed", ct, ("asof", at.ToUniversalTime())));
        return json.RootElement[0].GetProperty("passed").GetBoolean();
    }

    // Whether a candidate is a native memory's own row (a star row answers as the memory's own, MemoryStars.Answering).
    static bool IsMemory(RecallCandidate c) => c.Row.Provider == Relationships.MemoryStars.Provider;

    // The state at `at` of each native memory among these candidates not resolved yet, into `then`; a memory none of
    // whose states then is served to this reader stays out of `then`, and so out of the answer.
    async Task Resolved(Guid principal, DateTimeOffset at, IEnumerable<RecallCandidate> candidates, Dictionary<Guid, RecallThen> then, CancellationToken ct)
    {
        var open = candidates.Where(c => IsMemory(c) && !then.ContainsKey(c.Id)).Select(c => c.Id).Distinct().ToList();
        foreach (var (id, t) in await RecallAsOf.Resolve(db, principal, at, open, ct)) then[id] = t;
    }

    // As of a moment, a native memory's excerpt is its words then (its row's head, RecallAsOf.Head), never the passage an
    // arm matched, which holds its words now.
    static void Words(RecallCandidate c)
    {
        if (c.Meaning is { Passage: not null } m) c.Meaning = m with { Passage = null };
        if (c.Keyword is { Passage: not null } k) c.Keyword = k with { Passage = null };
    }

    // The beliefs listed under an as-of answer (RecallAsOf): those the arms found and those that had replaced one of the
    // answer's memories (a supersedes made link onto it), at most BeliefsMax, newest retraction first, each with its
    // words then, cut to a snippet within what the output budget leaves.
    async Task<IReadOnlyList<RecallBelief>> Beliefs(Guid principal, DateTimeOffset at, IReadOnlyList<RecallHit> answered, List<RecallCandidate> believed,
        Dictionary<Guid, RecallThen> then, string filter, (string, object?)[] args, string? model, CancellationToken ct)
    {
        var replacing = await RecallAsOf.Replacing(db, principal, [.. answered.Where(h => then.ContainsKey(h.Id)).Select(h => h.Id)], filter, args, ct);
        var known = believed.ToDictionary(c => c.Id, c => c.Row);
        var linked = replacing.Keys.Where(id => !known.ContainsKey(id)).ToArray();
        if (linked.Length > 0)
        {
            foreach (var (id, row) in await Rows(principal, linked, model, ct, true, args)) known[id] = row;
            foreach (var (id, t) in await RecallAsOf.Resolve(db, principal, at, linked, ct)) then.TryAdd(id, t);
        }
        var left = Options.OutputBudget - answered.Sum(h => h.Excerpt.Length);
        var beliefs = new List<RecallBelief>();
        foreach (var (id, row) in known.Where(k => then.TryGetValue(k.Key, out var t) && t.RetractedAt is not null)
            .OrderByDescending(k => then[k.Key].RetractedAt).ThenBy(k => k.Key))
        {
            if (beliefs.Count == RecallAsOf.BeliefsMax) break;
            var excerpt = Cut(row.Head, Math.Min(Options.Snippet, left));
            left -= excerpt.Length;
            var full = Math.Max(row.ContentLength, row.Head.Length);
            beliefs.Add(new(id, row.Row, excerpt, excerpt.Length < full, full, then[id].RetractedAt!.Value,
                replacing.TryGetValue(id, out var instead) ? instead : null, then[id]));
        }
        return beliefs;
    }

    // The zone time phrases are read in: the one given, else the install's (timezone), else UTC.
    static TimeZoneInfo Zone(string? zone) =>
        TimeZoneInfo.FindSystemTimeZoneById(zone ?? (RhythmConfig.Loaded ? RhythmConfig.Current.Timezone : "UTC"));

    internal static void Check(RecallQuery q, RecallOptions o)
    {
        if (string.IsNullOrWhiteSpace(q.Query) || q.Query.Length > 800) throw new ArgumentException("Use a short question (at most 800 characters)");
        if (q.TopK < 1 || q.TopK > o.MaxTopK) throw new ArgumentException($"topK must be 1–{o.MaxTopK}");
        if (q.Hops < 0 || q.Hops > o.MaxHops) throw new ArgumentException($"hops must be 0–{o.MaxHops}");
        if (new[] { q.Domain, q.Provider, q.Kind, q.Subject }.Any(x => x.Length > 80)) throw new ArgumentException("Filter too long");
        if (q.After is { } a && q.Before is { } b && b <= a) throw new ArgumentException("before must be later than after");
        if (q.AsOf is not null && (q.After is not null || q.Before is not null))
            throw new ArgumentException("as_of takes no after or before: it answers what was true at that one time");
    }

    // The filters every arm applies, as SQL over a resources row aliased r, with their parameters. Without Memories, no
    // row of a native memory at all (search_knowledge's combined mode, a catalog tool, D-165). As of a moment, what was
    // true then in place of what is true now, and with `beliefs` what was believed then and marked wrong since
    // (RecallAsOf.Filter).
    static (string Sql, (string, object?)[] Args) Filters(RecallQuery q, bool memories = true, bool beliefs = true)
    {
        var parts = new List<string> { NotHeld, q.AsOf is null ? Current() : RecallAsOf.Filter(beliefs) };
        if (!memories) parts.Add("r.provider<>'memory'");
        var args = new List<(string, object?)>();
        if (q.AsOf is { } asOf) args.Add(("asof", asOf.ToUniversalTime()));
        foreach (var (column, name, value) in new[] { ("r.domain", "rf_domain", q.Domain), ("r.provider", "rf_provider", q.Provider), ("r.kind", "rf_kind", q.Kind), ("r.subject_id", "rf_subject", q.Subject) })
            if (value.Length > 0) { parts.Add($"{column}=@{name}"); args.Add((name, value)); }
        if (q.After is { } after) { parts.Add("coalesce(r.source_updated_at,r.indexed_at)>=@rf_after"); args.Add(("rf_after", after.ToUniversalTime())); }
        if (q.Before is { } before) { parts.Add("coalesce(r.source_updated_at,r.indexed_at)<@rf_before"); args.Add(("rf_before", before.ToUniversalTime())); }
        return (string.Join(" AND ", parts), [.. args]);
    }

    // The keyword arm (SB-P2-19): ranked full text over the catalog's rows, a native memory's text and the passages of the
    // records whose passages are served, in one ranking (LexicalSearch, LexicalTexts). A native memory's own row is found
    // by its text in memories, never by its title (Memory) and never a held one's (D-152); its map star's row, titled
    // with the start of its words, answers no search but the map, so it takes no place in this arm's pool. A record whose
    // words only its passages hold (a note's or a Drive file's star row) is found by them while semantic_search would
    // serve them. Which rows are then served: a native memory's own row as Rhythm's own record, which no source checks
    // (the predicate and NotHeld have decided it); a record scored on one of its passages, of a live source, while its
    // source still holds that revision, asked by the record's own source id as semantic_search asks (fail closed, and
    // counted when the check did not answer in time); every other row as search_knowledge serves it (Retrieval.Validate),
    // a live file's star found by its title alone included, which that check refuses as before (its namespaced id).
    // With the hits: each record's check (true current, false not, null not answered in time), the revision of each one
    // its source confirmed, and how long the checks took of the search's budget.
    async Task<(IReadOnlyList<RecallArmHit> Hits, IReadOnlyDictionary<Guid, bool?> Answers, IReadOnlyDictionary<Guid, string> Confirmed, TimeSpan Spent)> Keyword(
        Guid principal, string query, string filter, (string, object?)[] args, ISourceValidation? checks, CancellationToken ct)
    {
        var texts = new LexicalTexts($"r.provider<>'{Relationships.MemoryStars.Provider}'", Options.Memories && Options.KeywordTexts,
            Passages && Options.KeywordTexts ? RecordPassages : "false", PassageJoin, "@lexical_model", Options.AnyPassageWord);
        var (sql, lexical) = LexicalSearch.Standard.Sql(query,
            $"r.id,r.provider,r.external_id,r.revision,{NativeMemory} AS native,r.relationship_snapshot->'Key'->>'ExternalId' AS source_id",
            $"{Database.Visible()} AND {filter}", Options.KeywordPool.ToString(CultureInfo.InvariantCulture), weights: true, texts);
        using var json = JsonDocument.Parse(await db.Read(principal, sql, ct, [.. lexical, .. args, .. PassageArgs]));
        var rows = json.RootElement.EnumerateArray().ToArray();
        var live = LiveSources;
        static Guid Id(JsonElement r) => r.GetProperty("id").GetGuid();
        static bool Native(JsonElement r) => r.GetProperty("native").GetBoolean();
        bool BySource(JsonElement r) => !Native(r) && r.GetProperty("lexical_part").GetInt32() >= 0 && live.Contains(r.GetProperty("provider").GetString()!);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var confirmed = await SemanticSearch.Confirm(checks, [.. rows.Where(BySource).Select(r => (Id(r), r.GetProperty("provider").GetString()!,
            r.GetProperty("source_id").GetString(), r.GetProperty("revision").GetString()))], CheckBudget, ct);
        var spent = clock.Elapsed;
        using var archived = JsonDocument.Parse(await catalog.Validate(JsonSerializer.Serialize(rows.Where(r => !Native(r) && !BySource(r))), ct));
        var allowed = archived.RootElement.EnumerateArray().Select(Id).ToHashSet();
        return ([.. rows.Where(r => Native(r) || (BySource(r) ? confirmed.GetValueOrDefault(Id(r)) == true : allowed.Contains(Id(r)))).Select((r, i) => new RecallArmHit(Id(r), i + 1,
            Weight(Options.KeywordWeight, r.GetProperty("lexical_weight").GetDouble(), r.GetProperty("lexical_total").GetDouble(), r.GetProperty("lexical_exact").GetInt32()),
            r.GetProperty("lexical_exact").GetInt32(), [.. r.GetProperty("lexical_terms").EnumerateArray().Select(t => t.GetString()!)], null,
            r.GetProperty("lexical_part").GetInt32() is >= 0 and var part ? part : null, r.GetProperty("lexical_text").GetString(), null))],
            confirmed, rows.Where(r => BySource(r) && confirmed.GetValueOrDefault(Id(r)) == true && r.GetProperty("revision").GetString() is not null)
                .DistinctBy(Id).ToDictionary(Id, r => r.GetProperty("revision").GetString()!), spent);
    }

    // A keyword result's fusion weight (RecallWeight).
    internal static double Weight(RecallWeight mode, double held, double total, int exact) => mode switch
    {
        RecallWeight.Rarity => held,
        RecallWeight.Coverage => total > 0 ? held / total : 1,
        RecallWeight.CoverageExact => (total > 0 ? held / total : 1) + (exact > 0 ? 1 : 0),
        _ => 1,
    };

    // The meaning arm's passages, each resource once, at the rank of its nearest passage.
    static IReadOnlyList<RecallArmHit> Meaning(string rows)
    {
        using var json = JsonDocument.Parse(rows);
        var seen = new HashSet<Guid>();
        var hits = new List<RecallArmHit>();
        foreach (var r in json.RootElement.EnumerateArray())
        {
            var id = r.GetProperty("id").GetGuid();
            if (!seen.Add(id)) continue;
            hits.Add(new(id, hits.Count + 1, 1, 0, [], r.GetProperty("similarity").GetDouble(), r.GetProperty("chunk_ordinal").GetInt32(),
                r.GetProperty("excerpt").GetString(), r.TryGetProperty("vector", out var v) && v.ValueKind == JsonValueKind.String ? ParseVector(v.GetString()!) : null));
        }
        return hits;
    }

    internal static float[] ParseVector(string text) =>
        [.. text.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => float.Parse(x, NumberStyles.Float, CultureInfo.InvariantCulture))];

    // The catalog rows of the candidates, read again with the predicate (an arm's row that is no longer readable is
    // left out), with the start of their text for the excerpts, the source's own id for ties, and whether the row has
    // passages of `model` at all (Impute).
    async Task<IReadOnlyDictionary<Guid, RecallRow>> Rows(Guid principal, Guid[] ids, string? model, CancellationToken ct, bool asOf = false,
        (string, object?)[]? args = null)
    {
        if (ids.Length == 0) return new Dictionary<Guid, RecallRow>();
        (string, object?)[] moment = asOf ? [.. args!.Where(a => a.Item1 == "asof")] : [];
        using var json = JsonDocument.Parse(await db.Read(principal, $"""
            SELECT {Columns},{Head(Options.FullChars, asOf)} AS head,{Length(asOf)} AS content_length,{RecordIds},
              {(model is null ? "false" : "EXISTS(SELECT 1 FROM resource_chunks k WHERE k.resource_id=r.id AND k.model=@model)")} AS indexed
            FROM resources r {PassageJoin} WHERE r.id=ANY(@ids) AND {Database.Visible()}
            """, ct, [("ids", ids), ("model", model), .. PassageArgs, .. moment]));
        var rows = new Dictionary<Guid, RecallRow>();
        foreach (var r in json.RootElement.EnumerateArray())
        {
            var head = r.GetProperty("head").GetString() ?? "";
            var length = r.GetProperty("content_length").GetInt32();
            var row = new Dictionary<string, JsonElement>();
            foreach (var p in r.EnumerateObject()) if (p.Name is not ("head" or "content_length" or "indexed" or "copy_of" or "source_ids")) row[p.Name] = p.Value.Clone();
            rows[r.GetProperty("id").GetGuid()] = new(JsonSerializer.SerializeToElement(row), head, length,
                r.GetProperty("provider").GetString()!, r.GetProperty("external_id").GetString() ?? "", r.GetProperty("indexed").GetBoolean(),
                Ids(r, "copy_of"), Ids(r, "source_ids"));
        }
        return rows;
    }

    // Reciprocal rank fusion over the candidates that are still readable, best first. With Impute, and the meaning arm
    // run, a keyword result the meaning arm could never have found (a row with no passages of the model: a data series,
    // a reference-only record) gets that arm's share as if it had ranked it where the keyword arm did (1 / (K + its
    // keyword rank)), so the arm's silence about it does not mark it down. Ties: found by more arms, then the better rank
    // in either, then the keyword arm's, then the source's own id (never a random engine id, D-057).
    internal static List<RecallCandidate> Fuse(IReadOnlyList<RecallArmHit> keyword, IReadOnlyList<RecallArmHit> meaning,
        IReadOnlyDictionary<Guid, RecallRow> rows, RecallOptions o, bool meaningRan = true)
    {
        var all = new Dictionary<Guid, RecallCandidate>();
        foreach (var k in keyword)
            if (rows.TryGetValue(k.Id, out var row))
                all[k.Id] = new(k.Id, row, k.Weight / (o.K + k.Rank)) { Keyword = k };
        foreach (var m in meaning)
            if (rows.TryGetValue(m.Id, out var row))
            {
                if (all.TryGetValue(m.Id, out var c)) { c.Score += 1 / (o.K + m.Rank); c.Meaning = m; }
                else all[m.Id] = new(m.Id, row, 1 / (o.K + m.Rank)) { Meaning = m };
            }
        if (o.Impute && meaningRan)
            foreach (var c in all.Values.Where(c => c.Meaning is null && !c.Row.Indexed)) { c.Score += 1 / (o.K + c.Keyword!.Rank); c.Imputed = true; }
        return [.. all.Values.OrderByDescending(c => c.Score).ThenByDescending(c => (c.Keyword is null ? 0 : 1) + (c.Meaning is null ? 0 : 1))
            .ThenBy(c => Math.Min(c.Keyword?.Rank ?? int.MaxValue, c.Meaning?.Rank ?? int.MaxValue)).ThenBy(c => c.Keyword?.Rank ?? int.MaxValue)
            .ThenBy(c => c.Row.Provider, StringComparer.Ordinal).ThenBy(c => c.Row.ExternalId, StringComparer.Ordinal).ThenBy(c => c.Id)];
    }

    // A save_document copy and the record it copies are one record to a reader (SB-P2-20, D-153): a copy names its
    // original by the original's source id (its external id, a legacy copy's sourceFileId), as the copy rule finds it
    // (copy_readable, migration 023). Found together, by either arm, they come back once, at the better place either
    // took, as that row with its own reasons and excerpt (a copy's text is a dated snapshot, a star's the source's
    // revision, so neither speaks for the other); the other is left out, and so is any other copy of a record already
    // listed. Records that are not copies never fold, whatever ids they share, and a copy of a record not among the
    // results stays. With no copy among the results the list is the one given.
    const string Copy = "coalesce(r.metadata->>'retrievalMode','') IN (" + Database.CopyModes + ")";
    const string RecordIds = $"CASE WHEN {Copy} THEN array_remove(ARRAY[r.external_id,r.metadata->>'sourceFileId'],NULL) END AS copy_of,"
        + $"CASE WHEN {Copy} THEN NULL ELSE array_remove(ARRAY[r.external_id,r.relationship_snapshot->'Key'->>'ExternalId'],NULL) END AS source_ids";
    static string[]? Ids(JsonElement r, string column) =>
        r.TryGetProperty(column, out var ids) && ids.ValueKind == JsonValueKind.Array ? [.. ids.EnumerateArray().Select(x => x.GetString()!)] : null;

    internal static List<RecallCandidate> OneRecord(List<RecallCandidate> fused)
    {
        if (!fused.Any(c => c.Row.CopyOf is { Count: > 0 })) return fused;
        // The records a candidate stands for: itself, or for a copy the originals among the results (none: only itself).
        HashSet<Guid> Stands(RecallCandidate c) => c.Row.CopyOf is { Count: > 0 } named
            ? [.. fused.Where(x => x.Row.CopyOf is not { Count: > 0 } && x.Row.SourceIds is { } ids && ids.Intersect(named, StringComparer.Ordinal).Any()).Select(x => x.Id)]
            : [c.Id];
        var kept = new List<(RecallCandidate Candidate, HashSet<Guid> Stands)>();
        foreach (var c in fused)
        {
            var stands = Stands(c);
            if (stands.Count > 0 && kept.Any(k => k.Stands.Overlaps(stands))) continue;
            kept.Add((c, stands));
        }
        return [.. kept.Select(k => k.Candidate)];
    }

    // Whether a graph neighbour is a record the list already holds under another row (OneRecord).
    static bool Folds(IReadOnlyList<RecallCandidate> list, RecallCandidate neighbour)
    {
        var others = list.Where(c => c.Id != neighbour.Id).ToList();
        return OneRecord([.. others, neighbour]).All(c => c.Id != neighbour.Id);
    }

    // Maximal marginal relevance, greedy, to MaxTopK: each pick the candidate with the best Lambda x its score (as a
    // share of the first's) less (1 - Lambda) x its greatest cosine to a pick already made, among the meaning arm's
    // vectors (a candidate without one is never marked down). Then blocks of Block picks, each ordered by score, so the
    // first block is what topK 5 returns and no topK reorders a block it does not cut.
    internal static List<RecallCandidate> Diversify(List<RecallCandidate> pool, RecallOptions o)
    {
        var picked = new List<RecallCandidate>();
        if (o.Lambda >= 1 || pool.Count <= 1) picked.AddRange(pool.Take(o.MaxTopK));
        else
        {
            var left = new List<RecallCandidate>(pool);
            var top = pool[0].Score > 0 ? pool[0].Score : 1;
            picked.Add(left[0]); left.RemoveAt(0);
            while (picked.Count < o.MaxTopK && left.Count > 0)
            {
                var (best, bestValue) = (0, double.NegativeInfinity);
                for (var i = 0; i < left.Count; i++)
                {
                    var vector = left[i].Meaning?.Vector;
                    var similar = vector is null ? 0 : picked.Select(p => p.Meaning?.Vector is { } other ? Cosine(vector, other) : 0).DefaultIfEmpty(0).Max();
                    var value = o.Lambda * (left[i].Score / top) - (1 - o.Lambda) * similar;
                    if (value > bestValue) (best, bestValue) = (i, value);
                }
                picked.Add(left[best]); left.RemoveAt(best);
            }
        }
        var ordered = new List<RecallCandidate>(picked.Count);
        for (var b = 0; b < picked.Count; b += o.Block)
            ordered.AddRange(picked.Skip(b).Take(o.Block).Select((c, i) => (c, i)).OrderByDescending(x => x.c.Score).ThenBy(x => x.i).Select(x => x.c));
        return ordered;
    }

    internal static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }

    // Graph hops: typed links from the first Block results, both directions, active, both ends readable by this reader
    // and meeting the filters, each neighbour checked as get_related_resources checks it; a hop's score is Decay times
    // its parent's. The best neighbour not among the first two blocks takes the 5th place and the next the 10th (the
    // list is then longer by what they add; one already further down moves up from there).
    async Task<List<RecallCandidate>> Expand(Guid principal, List<RecallCandidate> ordered, int hops, string filter, (string, object?)[] args, CancellationToken ct,
        bool asOf = false)
    {
        var o = Options;
        var reached = new Dictionary<Guid, RecallCandidate>();
        var frontier = ordered.Take(o.Block).ToDictionary(c => c.Id, c => c.Score);
        var visited = new HashSet<Guid>(frontier.Keys);
        for (var hop = 1; hop <= hops && frontier.Count > 0; hop++)
        {
            // A native memory's links are its map star's (the rebuild writes them between star rows, SB-P3-11) and, with
            // the memory tools on (Options.MadeLinks), the links a person or an AI client made (made_links, SB-P4-02), which
            // join memories' own rows: a hop starts from a result's row and, for a memory, from its star row too, and a star
            // row it reaches answers as the memory's own row (MemoryStars.Answering), which the predicate and NotHeld then
            // decide (D-152). A suggestion between two memories that a person or an AI client dismissed is no link to follow
            // (RelationshipEngine.DismissedPair), nor one a person dismissed between a memory's star and another star
            // (StarDismissed, SB-P4-05), and, as on the map, a dismissal never hides a confirmed link. As of a moment, a
            // made link made after it is no link to follow then; the map's own links are followed as they stand now
            // (D-195). With no native memories, no made link read and no dismissal this is the join of before.
            using var json = JsonDocument.Parse(await db.Read(principal, $"""
                SELECT {Columns},{Head(o.FullChars, asOf)} AS head,{Length(asOf)} AS content_length,{RecordIds},{NativeMemory} AS native,
                  l.type AS link_type,s.own AS linked_from,
                  CASE WHEN l.source_id=s.start THEN 'outgoing' ELSE 'incoming' END AS direction
                FROM (SELECT f.id AS own,f.id AS start FROM unnest(@from) f(id)
                  UNION ALL SELECT f.id,star.id FROM unnest(@from) f(id)
                  JOIN resources star ON star.external_id={Relationships.MemoryStars.StarExternalId("f.id")} AND star.provider='memory'
                    AND star.relationship_snapshot IS NOT NULL) s
                JOIN (SELECT x.source_id,x.target_id,x.type,x.lifecycle,false AS made,x.created_at,x.verification FROM relationships x
                  {(o.MadeLinks ? "UNION ALL SELECT y.source_id,y.target_id,y.type,y.lifecycle,true,y.created_at,y.verification FROM made_links y" : "")}) l
                  ON l.source_id=s.start OR l.target_id=s.start
                JOIN resources origin ON origin.id=s.own
                JOIN resources far ON far.id=CASE WHEN l.source_id=s.start THEN l.target_id ELSE l.source_id END
                JOIN resources r ON r.id={Relationships.MemoryStars.Answering("far")} {PassageJoin}
                WHERE l.lifecycle='active' AND r.id<>ALL(@from)
                  AND (l.made OR NOT {Relationships.RelationshipEngine.DismissedPair("s.own", "r.id", "l.type")}
                    AND NOT {Relationships.RelationshipEngine.StarDismissed("l")})
                  {(asOf ? "AND (NOT l.made OR l.created_at<=@asof)" : "")}
                  AND {Database.Visible("origin")} AND {Database.Visible()} AND {filter}
                ORDER BY l.type,{Stable} LIMIT 200
                """, ct, [("from", frontier.Keys.ToArray()), .. args, .. PassageArgs]));
            // Each neighbour checked as get_related_resources checks it (Retrieval.Validate), once per resource; a native
            // memory's own row needs no source's word (NativeMemory), and the predicate and NotHeld have decided it.
            var others = json.RootElement.EnumerateArray().Where(r => !r.GetProperty("native").GetBoolean())
                .GroupBy(r => r.GetProperty("id").GetGuid()).Select(g => g.First()).ToList();
            using var current = JsonDocument.Parse(await catalog.Validate(JsonSerializer.Serialize(others), ct));
            var served = current.RootElement.EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToHashSet();
            var next = new Dictionary<Guid, double>();
            foreach (var r in json.RootElement.EnumerateArray())
            {
                var id = r.GetProperty("id").GetGuid();
                if (!r.GetProperty("native").GetBoolean() && !served.Contains(id)) continue;
                var from = r.GetProperty("linked_from").GetGuid();
                var score = frontier[from] * o.Decay;
                if (visited.Contains(id) && !next.ContainsKey(id)) continue;
                if (reached.TryGetValue(id, out var known) && known.Score >= score) continue;
                var row = new Dictionary<string, JsonElement>();
                foreach (var p in r.EnumerateObject()) if (p.Name is not ("head" or "content_length" or "copy_of" or "source_ids" or "native" or "link_type" or "linked_from" or "direction")) row[p.Name] = p.Value.Clone();
                reached[id] = new(id, new(JsonSerializer.SerializeToElement(row), r.GetProperty("head").GetString() ?? "", r.GetProperty("content_length").GetInt32(),
                    r.GetProperty("provider").GetString()!, r.GetProperty("external_id").GetString() ?? "", CopyOf: Ids(r, "copy_of"), SourceIds: Ids(r, "source_ids")), score)
                { Hop = new(hop, r.GetProperty("link_type").GetString()!, from, r.GetProperty("direction").GetString()!) };
                next[id] = Math.Max(next.GetValueOrDefault(id), score);
            }
            foreach (var id in next.Keys) visited.Add(id);
            frontier = next;
        }
        var leading = ordered.Take(o.Block * 2).Select(c => c.Id).ToHashSet();
        // A neighbour that is a record already listed or already chosen (a copy of one, or the record a copy of one
        // copies, SB-P2-20) takes no place.
        var chosen = new List<RecallCandidate>();
        foreach (var c in reached.Values.Where(c => !leading.Contains(c.Id)).OrderByDescending(c => c.Score)
            .ThenBy(c => c.Row.Provider, StringComparer.Ordinal).ThenBy(c => c.Row.ExternalId, StringComparer.Ordinal).ThenBy(c => c.Id))
        {
            if (chosen.Count == o.GraphSlots.Length) break;
            if (!Folds([.. ordered, .. chosen], c)) chosen.Add(c);
        }
        var neighbours = chosen.ToArray();
        var list = new List<RecallCandidate>(ordered);
        for (var i = 0; i < neighbours.Length; i++)
        {
            // A neighbour the fused list already holds further down keeps what each arm said of it.
            var at = list.FindIndex(c => c.Id == neighbours[i].Id);
            var placed = at < 0 ? neighbours[i] : list[at] with { Hop = neighbours[i].Hop };
            if (at >= 0) list.RemoveAt(at);
            list.Insert(Math.Min(o.GraphSlots[i], list.Count), placed);
        }
        return list;
    }

    // The excerpts, in order, within the output budget: each from the meaning arm's nearest passage when there is one
    // (the part that matched), else from the start of the row's text.
    internal static List<RecallHit> Budget(List<RecallCandidate> ordered, RecallOptions o)
    {
        var hits = new List<RecallHit>(ordered.Count);
        var left = o.OutputBudget;
        var top = ordered.Count > 0 ? ordered[0].Score : 0;
        foreach (var c in ordered)
        {
            var text = c.Meaning?.Passage is { Length: > 0 } passage ? passage : c.Keyword?.Passage is { Length: > 0 } words ? words : c.Row.Head;
            var full = Math.Max(c.Row.ContentLength, text.Length);
            var allowance = hits.Count < o.FullMatches && c.Score >= o.StrongRatio * top ? o.FullChars : o.Snippet;
            var excerpt = Cut(text, Math.Min(allowance, left));
            left -= excerpt.Length;
            hits.Add(new(c.Id, c.Row.Row, excerpt, excerpt.Length < full, full, Math.Round(c.Score, 6), new(
                c.Keyword?.Rank, c.Keyword is null ? null : Math.Round(c.Keyword.Weight, 6), c.Keyword?.Terms ?? [], c.Keyword?.Exact ?? 0,
                c.Meaning?.Rank, c.Meaning?.Similarity is { } s ? Math.Round(s, 6) : null, c.Meaning?.Ordinal,
                c.Hop?.Number, c.Hop?.Type, c.Hop?.From, c.Hop?.Direction, c.Imputed)));
        }
        return hits;
    }

    // At most `max` characters, cut at a space when one is near the end, never inside a surrogate pair.
    internal static string Cut(string text, int max)
    {
        if (max <= 0) return "";
        if (text.Length <= max) return text;
        var end = max;
        var space = text.LastIndexOfAny([' ', '\n', '\t'], max - 1, Math.Min(max, 80));
        if (space > 0) end = space;
        else if (char.IsHighSurrogate(text[end - 1])) end--;
        return text[..end];
    }
}

// The numbers combined retrieval runs with: Second Brain's (D-028, D-040), but for the two the gate changed (Lambda,
// Impute), each kept or changed only by measurement (docs/dev/second-brain-parity/combined-retrieval.md).
public sealed record RecallOptions
{
    public static RecallOptions Default { get; } = new();
    // Reciprocal rank fusion's constant: an arm adds weight / (K + rank).
    public double K { get; init; } = 60;
    // How a keyword result's weight is reckoned (RecallWeight).
    public RecallWeight KeywordWeight { get; init; } = RecallWeight.Rarity;
    public int KeywordPool { get; init; } = 50;
    // Whether a keyword result with no passages of the model gets the meaning arm's share at its keyword rank (Fuse): on,
    // since without it a data series or a reference-only record, which the meaning arm can never find, loses to any
    // row both arms found (combined-retrieval.md).
    public bool Impute { get; init; } = true;
    // Passages read for the meaning arm, whatever topK is (at most SemanticSearch.MaxRead).
    public int DensePool { get; init; } = 50;
    // Diversity's trade of score for variety; 1 turns it off. Off: Second Brain's 0.7 lost recall in both load modes,
    // since related answers to one question are alike (combined-retrieval.md); each resource is one result either way.
    public double Lambda { get; init; } = 1;
    public int Block { get; init; } = 5;
    public int MaxTopK { get; init; } = 20;
    public int MaxHops { get; init; } = 3;
    public double Decay { get; init; } = 0.6;
    // The places (from 0) graph neighbours take: the 5th and the 10th.
    public int[] GraphSlots { get; init; } = [4, 9];
    public int OutputBudget { get; init; } = 12_000;
    public int Snippet { get; init; } = 400;
    public int FullChars { get; init; } = 4000;
    public int FullMatches { get; init; } = 2;
    public double StrongRatio { get; init; } = 0.75;
    // Whether native memories are candidates: off for search_knowledge's combined mode, which stays a catalog tool and,
    // as the other catalog tools do, leaves every memory-provider row out before its limit (D-165).
    public bool Memories { get; init; } = true;
    // Whether the graph hops follow the links a person or an AI client made (made_links, SB-P4-02) beside the map's own:
    // set by RecallServing from memory.tools, off by default, so an install with every setting off follows only the
    // map's links, as before, even after an operator's import brought made links in (043).
    public bool MadeLinks { get; init; }
    // Whether the opt-in recall log keeps this search (SB-P2-11, RecallLog), when engine.recallLog is on: the recall
    // tool's (Tool) and the explorer's (RecallStars.Budget), which answer with a receipt to trace it by; never
    // search_knowledge's combined mode or the evaluation. It changes nothing in the answer.
    public bool Logged { get; init; }
    public static RecallOptions Tool { get; } = new() { Logged = true };
    // Whether the keyword arm also reads native memories' text and the passages of records whose passages are served
    // (SB-P2-19, migration 040): on. Off is the arm before it, the catalog's own rows alone, kept for the evaluation's
    // comparison (ParityRecall's catalog-words).
    public bool KeywordTexts { get; init; } = true;
    // Whether a passage holding any of the question's words counts for the keyword arm, not only one holding the whole
    // question or an identifier it names: off, as measured (combined-retrieval.md); a variant for the evaluation.
    public bool AnyPassageWord { get; init; }
    // Whether a time phrase in the question ("yesterday", "this week", "on Aug 8") becomes a window on the source's own
    // time when the caller gives none (TimePhrases, SB-P2-16): off, since the gate showed no gain (combined-retrieval.md);
    // a variant for the evaluation. Now and Zone fix the clock and the time zone it reads them by (null: the current time,
    // the install's zone).
    public bool TimePhrases { get; init; }
    public DateTimeOffset? Now { get; init; }
    public string? Zone { get; init; }
}

// A keyword result's weight in the fusion, the meaning arm's being 1: Rarity, the summed rarity of the query's words it
// holds (Second Brain's); Coverage, that as a share of every query word's rarity, so only a row holding the whole question
// counts as much as the nearest passage; CoverageExact, Coverage and 1 more for an identifier hit; Flat, 1.
public enum RecallWeight { Rarity, Coverage, CoverageExact, Flat }

// One question: its text, how many results (1 to MaxTopK), how many link hops (0 to MaxHops) and the filters; empty
// means any. `After` and `Before` bound the source's own time (its last change, else when Rhythm indexed it).
// `AsOf` asks what was true at that moment instead of now (SB-P4-07, RecallAsOf), never with After or Before.
public sealed record RecallQuery(string Query, int TopK = 5, int Hops = 0, string Domain = "", string Provider = "", string Kind = "",
    string Subject = "", DateTimeOffset? After = null, DateTimeOffset? Before = null, DateTimeOffset? AsOf = null);

// The answer: the hits, best first, and what the caller must be told (semantic search skipped, files left out). As of a
// moment (AsOf), the hits are what was true then, and Retracted the beliefs of then marked wrong since, listed under them.
public sealed record RecallResult(string Query, IReadOnlyList<RecallHit> Hits, IReadOnlyList<string> Notices, bool SemanticSkipped, int Unchecked,
    DateTimeOffset? AsOf = null, IReadOnlyList<RecallBelief>? Retracted = null);

// One result: the catalog row as search_knowledge returns its columns, an excerpt within the budget (Truncated when the
// text is longer, FullLength its length), the fused score, and why it is here. As of a moment, a native memory's Then:
// the state its words are taken from.
public sealed record RecallHit(Guid Id, JsonElement Row, string Excerpt, bool Truncated, int FullLength, double Score, RecallWhy Why,
    RecallThen? Then = null);

// Why a result is here: its keyword rank, weight, the query's words it holds and identifier hits; its meaning rank,
// similarity and nearest passage; and for a graph neighbour the hop, the link's type, the result it was reached from and
// the link's direction from there; Imputed when it was given the meaning arm's share at its keyword rank (a row with no
// passages of the model, Fuse).
public sealed record RecallWhy(int? KeywordRank, double? KeywordWeight, IReadOnlyList<string> Terms, int Exact, int? MeaningRank, double? Similarity,
    int? Passage, int? Hop, string? Link, Guid? From, string? Direction, bool Imputed = false);

// One arm's result for one resource: its rank in that arm (from 1), its fusion weight, and what the arm saw.
public sealed record RecallArmHit(Guid Id, int Rank, double Weight, int Exact, IReadOnlyList<string> Terms, double? Similarity, int? Ordinal,
    string? Passage, float[]? Vector);

// CopyOf: for a save_document copy, the source ids it names its original by (its external id, a legacy copy's
// sourceFileId); SourceIds: for any other row, the ids a copy names it by (its external id, its star's source id).
// A copy and a row it names are one record to a reader (Recall.OneRecord).
public sealed record RecallRow(JsonElement Row, string Head, int ContentLength, string Provider, string ExternalId, bool Indexed = false,
    IReadOnlyList<string>? CopyOf = null, IReadOnlyList<string>? SourceIds = null);
public sealed record RecallHop(int Number, string Type, Guid From, string Direction);

public sealed record RecallCandidate(Guid Id, RecallRow Row, double Score)
{
    public double Score { get; set; } = Score;
    public RecallArmHit? Keyword { get; set; }
    public RecallArmHit? Meaning { get; set; }
    public RecallHop? Hop { get; set; }
    public bool Imputed { get; set; }
}

// A live source's check for one search (Recall.Search): a record and revision its source confirmed is not asked again by
// the other arm. One it did not confirm, or whose check did not answer, is asked again, so a check that ran out of time
// in one arm is never taken as an answer in the other.
sealed class ConfirmedChecks(ISourceValidation inner) : ISourceValidation
{
    readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string, string?), bool> confirmed = new();
    public async Task<bool> Current(string provider, string externalId, string? revision, CancellationToken ct)
    {
        if (confirmed.ContainsKey((provider, externalId, revision))) return true;
        var current = await inner.Current(provider, externalId, revision, ct);
        if (current) confirmed[(provider, externalId, revision)] = true;
        return current;
    }
}
