using System.Text;
using System.Text.RegularExpressions;

namespace Knowledge;

// The full-text part of search_knowledge and search_resources: Postgres text search over a resource's title and
// content, in one text search configuration.
//
// Ranked (the default): the query's words less the function words the configuration never indexes (Terms), each
// matched on its own, so a row holding some of them is found and a row holding all of them comes first. Matching rows
// are ordered by
//   1. identifier hits: each identifier-shaped word of the query ("RHY-4127", "v2.14.3") found whole in the row's title
//      (2) or text (1), so an exact id puts its record first;
//   2. which of the words the row holds, each weighted by how rare it is among the candidates (log(1 + n / (df + 1))),
//      so one rare name outweighs a common word;
//   3. ts_rank_cd, Postgres's cover density: the words close together and often, normalised by the row's length;
//   4. newest indexed first, the order before ranking;
//   5. the source's own id (Stable): rows a map rebuild writes together share their indexed time, and a row's id is
//      random, so without it equal rows would come back in another order after every rebuild.
// The work is bounded: the newest AllWords rows holding every word, and for each word the newest rows holding it (Words
// shared among the words, at least PerWord each), are the candidates scored, so a rare word's rows are always among
// them however many rows a common word fills. Each tier is found through an index, and in the chosen configuration a
// row's title and content are parsed once, when the row is written: the tiers match and scoring reads the stored text
// vector (Stored, migration 047, SB-P2-21), so a long saved document is not parsed again at every search. A query with
// no word to search for (empty, only punctuation, or only function words) is answered as before: the simple
// configuration keeps function words, so "the who" still finds "The Who".
//
// Legacy (engine.legacySearch): every word of the query required, function words included, in the `simple`
// configuration, newest indexed first: what both tools did before ranking.
//
// Matching any word, dropping function words and weighting each word by its rarity are adapted from Second Brain's
// keyword arm (MIT), here on Postgres text search.
public sealed partial record LexicalSearch(string Configuration, bool Ranked)
{
    public const int AllWords = 200, Words = 400, PerWord = 50, MaxTerms = 12;
    // The fewest rows a word's rarity is weighed against for combined retrieval's keyword weight (lexical_weight).
    public const int IdfWindow = Words;
    const string Stable = "r.provider,r.external_id,r.id";
    // The configuration ranked search uses, chosen on the parity golden set in both load modes
    // (docs/dev/second-brain-parity/lexical-search.md); migration 021 gives it its index.
    public const string Chosen = "english";
    public static LexicalSearch Legacy { get; } = new("simple", false);
    public static LexicalSearch Standard { get; } = new(Chosen, true);
    public static LexicalSearch For(RhythmConfig config) => config.Engine.LegacySearch ? Legacy : Standard;

    // The stored parse of a row's title and content in the chosen configuration (migration 047): exactly the expression
    // it replaces, to_tsvector(Chosen, title || ' ' || content), kept by the row and indexed on its own. Another
    // configuration (the evaluation's variants) parses the text as before.
    public const string Stored = "search_english";
    string Parsed(string head, string body) => $"to_tsvector('{Configuration}',{head} || ' ' || {body})";
    string Doc => Configuration == Chosen ? $"r.{Stored}" : Parsed("r.title", "r.content");

    // Function words a question is made of. They say how something is asked, not what to find, and nearly every row
    // holds some of them. Unindexed are the stop words of Postgres's english configuration (Snowball's English list),
    // which it never indexes: a query made only of them could match nothing ranked, so it runs the query from before.
    // Modal are function words that configuration does index but that would only add noise ("whose plants" is about
    // plants). A word like "may" (the month) or "us" is searched for.
    public static readonly IReadOnlySet<string> Unindexed = new HashSet<string>(StringComparer.Ordinal)
    {
        "i", "me", "my", "myself", "we", "our", "ours", "ourselves", "you", "your", "yours", "yourself", "yourselves", "he",
        "him", "his", "himself", "she", "her", "hers", "herself", "it", "its", "itself", "they", "them", "their", "theirs",
        "themselves", "what", "which", "who", "whom", "this", "that", "these", "those", "am", "is", "are", "was", "were",
        "be", "been", "being", "have", "has", "had", "having", "do", "does", "did", "doing", "a", "an", "the", "and", "but",
        "if", "or", "because", "as", "until", "while", "of", "at", "by", "for", "with", "about", "against", "between",
        "into", "through", "during", "before", "after", "above", "below", "to", "from", "up", "down", "in", "out", "on",
        "off", "over", "under", "again", "further", "then", "once", "here", "there", "when", "where", "why", "how", "all",
        "any", "both", "each", "few", "more", "most", "other", "some", "such", "no", "nor", "not", "only", "own", "same",
        "so", "than", "too", "very", "s", "t", "can", "will", "just", "don", "should", "now",
    };
    public static readonly IReadOnlySet<string> Modal = new HashSet<string>(StringComparer.Ordinal)
    {
        "could", "would", "might", "must", "shall", "whose", "onto",
    };
    static readonly HashSet<string> Stop = [.. Unindexed, .. Modal];

    // The words to search for, lower case, at most MaxTerms: whitespace-separated, a possessive "'s" dropped, cut to
    // their first and last letter or digit; single letters and function words dropped, a word made only of function
    // words as the configuration splits it ("don't" is "don" and "t") included. None left: the query from before.
    public static IReadOnlyList<string> Terms(string query)
    {
        var words = new List<string>();
        foreach (var chunk in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var word = Possessive().Replace(chunk, "");
            var start = 0; var end = word.Length;
            while (start < end && !char.IsLetterOrDigit(word[start])) start++;
            while (end > start && !char.IsLetterOrDigit(word[end - 1])) end--;
            word = word[start..end].ToLowerInvariant();
            if (word.Length == 0 || word.Length == 1 && !char.IsDigit(word[0])) continue;
            words.Add(word);
        }
        return [.. words.Where(w => !NonWord().Split(w).Where(p => p.Length > 0).All(Stop.Contains))
            .Distinct(StringComparer.Ordinal).Take(MaxTerms)];
    }

    // A ticket, version, model or reference number: a digit beside a letter ("rhy-4127", "v2.14.3", "q9"), or digits in
    // three or more dotted parts ("2.4.1"). Not a plain number, a date, a time or an amount ("2026", "2026-10-31", "31/10",
    // "10:30", "12.50"), nor an ordinal or a clock hour ("40th", "3pm"): they are too common to boost.
    public static bool Identifier(string term) => term.Any(char.IsDigit)
        && (term.Any(char.IsLetter) ? !Ordinal().IsMatch(term) : term.Count(c => c == '.') >= 2);

    // The query for one search. `select` is the column list returned, `where` the conditions every row meets (visibility
    // included, on alias r), `limit` the row cap (a number or a parameter the caller adds). It binds @q, and for ranked
    // search @t0.. (the words), @n0.. (a number's signed form: Postgres reads "RHY-4127" as "rhy" and "-4127", so "4127"
    // finds it) and @x0.. (an identifier's whole-word pattern).
    // `weights` (combined retrieval, Recall) adds columns that change nothing in the order: lexical_exact (the identifier
    // hits above), lexical_weight (the words the row holds, each weighted by its rarity among the candidates as in 2.
    // above but against at least IdfWindow rows, so a word only a few rows hold weighs as rare even when the candidates
    // are only those rows), lexical_total (the same over every word of the query, held or not: lexical_weight's
    // ceiling), both 1 when the query has no word to weigh, and lexical_terms (the words the row holds).
    // `texts` (combined retrieval's keyword arm, SB-P2-19) adds the words the catalog's own text does not hold: see
    // LexicalTexts. Without it the query is the one before, word for word.
    public (string Sql, (string, object?)[] Parameters) Sql(string query, string select, string where, string limit, bool weights = false,
        LexicalTexts? texts = null)
    {
        var terms = Ranked ? Terms(query) : [];
        if (terms.Count == 0)
            return ($"""
                SELECT {select}{(weights ? ",0 AS lexical_exact,1::float8 AS lexical_weight,1::float8 AS lexical_total,ARRAY[]::text[] AS lexical_terms" : "")}{(texts is null ? "" : ",false AS lexical_passages,-1 AS lexical_part,NULL::text AS lexical_text")} FROM resources r
                WHERE {where}{(texts is null ? "" : " AND " + texts.Catalog)} AND (@q='' OR to_tsvector('simple',r.title || ' ' || r.content) @@ plainto_tsquery('simple',@q))
                ORDER BY r.indexed_at DESC,r.id LIMIT {limit}
                """, [("q", query)]);
        if (!ConfigurationName().IsMatch(Configuration)) throw new InvalidOperationException("Invalid text search configuration");
        var args = new List<(string, object?)> { ("q", query) };
        var term = new string[terms.Count];
        var identifiers = new List<string>();
        for (var i = 0; i < terms.Count; i++)
        {
            args.Add(($"t{i}", terms[i]));
            term[i] = $"plainto_tsquery('{Configuration}',@t{i})";
            if (terms[i].All(char.IsDigit)) { args.Add(($"n{i}", "-" + terms[i])); term[i] = $"({term[i]} || plainto_tsquery('{Configuration}',@n{i}))"; }
            if (Identifier(terms[i]))
            {
                args.Add(($"x{identifiers.Count}", "(^|[^[:alnum:]])" + Escaped(terms[i]) + "($|[^[:alnum:]])"));
                identifiers.Add($"x{identifiers.Count}");
            }
        }
        var doc = Doc;
        var any = string.Join(" || ", term);
        var every = string.Join(" && ", term);
        // With texts, a row's words are those of its text (below): its head (its title, none for a native memory) and its
        // body (its content, a memory's text, or one of a record's passages); without, its title and content.
        var (head, body) = texts is null ? ("r.title", "r.content") : ("t.head", "t.body");
        var exact = identifiers.Count == 0 ? "0"
            : string.Join("+", identifiers.Select(x => $"(CASE WHEN {head} ~* @{x} THEN 2 WHEN {body} ~* @{x} THEN 1 ELSE 0 END)"));
        // Each tier is found through an index: the catalog's text (021), a memory's text and a passage's (040).
        var catalog = texts is null ? where : $"{where} AND {texts.Catalog}";
        var tier = (string match, int rows) => $"(SELECT r.id FROM resources r WHERE {catalog} AND {doc} @@ {match} ORDER BY r.indexed_at DESC,{Stable} LIMIT {rows})";
        var memoryTier = (string match, int rows) => $"""
            (SELECT r.id FROM resources r JOIN memories m ON m.id=r.id WHERE {where} AND {MemoryRow} AND m.hold IS NULL
              AND to_tsvector('{Configuration}',m.content) @@ {match} ORDER BY r.indexed_at DESC,{Stable} LIMIT {rows})
            """;
        var passageTier = (string match, int rows) => $"""
            (SELECT r.id FROM resources r {texts!.From} WHERE {where} AND ({texts.Passages}) AND EXISTS(SELECT 1 FROM resource_chunks k
              WHERE k.resource_id=r.id AND k.source_revision=r.revision AND to_tsvector('{Configuration}',k.text_content) @@ {match})
              ORDER BY r.indexed_at DESC,{Stable} LIMIT {rows})
            """;
        // A record's passages count for the whole question or an identifier it names (Whole, below), so they are searched
        // for those: the every-word tier, and each identifier's; a one-word question's word is the whole question. With
        // AnyWord (a variant the evaluation measures, never a default) a passage holding any of the words counts.
        IEnumerable<string> Tiers(string match, int rows, bool passages) => [tier(match, rows),
            .. texts is { Memories: true } ? [memoryTier(match, rows)] : Array.Empty<string>(),
            .. (passages || texts is { AnyWord: true }) && texts is { Passages: not "false" } ? [passageTier(match, rows)] : Array.Empty<string>()];
        var each = Math.Max(PerWord, Words / terms.Count);
        var candidates = string.Join("\nUNION\n", [.. terms.Count > 1 ? Tiers($"({every})", AllWords, true) : Array.Empty<string>(),
            .. term.Select((t, i) => Tiers(t, each, terms.Count == 1 || Identifier(terms[i]))).SelectMany(t => t)]);
        var held = string.Join(",", Enumerable.Range(0, terms.Count).Select(i => $"(d.doc @@ {term[i]})::int AS m{i}"));
        var frequency = string.Join(",", Enumerable.Range(0, terms.Count).Select(i => $"sum(s.m{i}) OVER () AS d{i}"));
        var weight = string.Join("+", Enumerable.Range(0, terms.Count).Select(i => $"w.m{i}*ln(1+w.n::float8/(w.d{i}+1))"));
        var rarity = (int i) => $"ln(1+greatest(w.n,{IdfWindow})::float8/(w.d{i}+1))";
        var fused = !weights ? "" : ",w.exact AS lexical_exact,("
            + string.Join("+", Enumerable.Range(0, terms.Count).Select(i => $"w.m{i}*{rarity(i)}")) + ") AS lexical_weight,("
            + string.Join("+", Enumerable.Range(0, terms.Count).Select(rarity)) + ") AS lexical_total,array_remove(ARRAY["
            + string.Join(",", Enumerable.Range(0, terms.Count).Select(i => $"CASE WHEN w.m{i}=1 THEN @t{i} END")) + "]::text[],NULL) AS lexical_terms";
        if (texts is null)
            return ($"""
                WITH candidates AS ({candidates}),
                scored AS MATERIALIZED (
                  SELECT r.id,{exact} AS exact,ts_rank_cd(d.doc,{any},1) AS density,{held}
                  FROM candidates c JOIN resources r ON r.id=c.id CROSS JOIN LATERAL (SELECT {doc} AS doc OFFSET 0) d),
                weighted AS (SELECT s.*,count(*) OVER () AS n,{frequency} FROM scored s)
                SELECT {select}{fused} FROM weighted w JOIN resources r ON r.id=w.id
                ORDER BY w.exact DESC,({weight}) DESC,w.density DESC,r.indexed_at DESC,{Stable} LIMIT {limit}
                """, [.. args]);

        // With texts, each candidate is scored on one text of its own, its best: a memory on its text in memories (a held
        // one's never: the memory tier and this read both ask for no hold); a record whose passages are served on each
        // passage with its title (part, the passage's ordinal) that holds every word of the question or an identifier it
        // names (Whole), as the meaning arm takes a record at its nearest passage, else on its title alone (part -1);
        // every other row on its title and content (part -1), as before. A passage holding only some of the words takes
        // no part: as installed a record's passages are most of the text the keyword arm reads, and their partial matches
        // on common words outweighed the meaning arm's answers for paraphrase, graph-chain and health questions, which the
        // gate failed (combined-retrieval.md, SB-P2-19), while an exact id or a whole question is what only the words can
        // find. How rare a word is counts the records holding it in any of their texts (held, stats), so the statistics
        // are the ones before whenever each candidate has one text. The best text is the one ranked first by the order
        // below; the row is then ranked as before, by that text. A catalog row's own text (part -1, not a memory) is its
        // title and content, whose parse the row keeps (Doc); a memory's text and a passage are parsed here.
        var whole = texts.AnyWord ? $"(to_tsvector('{Configuration}',r.title || ' ' || k.text_content) @@ ({any}))"
            : $"(to_tsvector('{Configuration}',r.title || ' ' || k.text_content) @@ ({every})"
            + string.Concat(Enumerable.Range(0, identifiers.Count).Select(j => $" OR k.text_content ~* @x{j}")) + ")";
        var chosen = $"(SELECT pm.model FROM resource_chunks pm WHERE pm.resource_id=r.id AND pm.source_revision=r.revision ORDER BY pm.model={texts.Model} DESC,pm.model LIMIT 1)";
        var passageUnits = texts.Passages is "false" ? "" : $"""
            UNION ALL
              SELECT k.ordinal,r.title,k.text_content FROM resource_chunks k WHERE kx.passages AND k.resource_id=r.id AND k.source_revision=r.revision
                AND k.model={chosen} AND {whole}
            """;
        var own = texts.Passages is "false" ? "true"
            : $"NOT kx.passages OR NOT EXISTS(SELECT 1 FROM resource_chunks k WHERE k.resource_id=r.id AND k.source_revision=r.revision AND k.model={chosen} AND {whole})";
        // The passage a record was scored on, for its excerpt (Recall): the part that matched.
        var matched = texts.Passages is "false" ? "NULL::text" : $"""
            CASE WHEN w.part>=0 THEN (SELECT k.text_content FROM resource_chunks k WHERE k.resource_id=r.id AND k.source_revision=r.revision AND k.ordinal=w.part
              ORDER BY k.model={texts.Model} DESC,k.model LIMIT 1) END
            """;
        var maxima = string.Join(",", Enumerable.Range(0, terms.Count).Select(i => $"max(u.m{i}) AS m{i}"));
        var sums = string.Join(",", Enumerable.Range(0, terms.Count).Select(i => $"sum(h.m{i}) AS d{i}"));
        var unitWeight = string.Join("+", Enumerable.Range(0, terms.Count).Select(i => $"u.m{i}*ln(1+s.n::float8/(s.d{i}+1))"));
        var stats = string.Join(",", Enumerable.Range(0, terms.Count).Select(i => $"s.d{i}"));
        return ($"""
            WITH candidates AS ({candidates}),
            units AS MATERIALIZED (
              SELECT r.id,t.part,{exact} AS exact,ts_rank_cd(d.doc,{any},1) AS density,{held},kx.passages
              FROM candidates c JOIN resources r ON r.id=c.id {texts.From}
              CROSS JOIN LATERAL (SELECT {(texts.Memories ? MemoryRow : "false")} AS memory,({texts.Passages}) AS passages OFFSET 0) kx
              CROSS JOIN LATERAL (
                SELECT -1 AS part,CASE WHEN kx.memory THEN '' ELSE r.title END AS head,
                  CASE WHEN kx.memory THEN coalesce((SELECT m.content FROM memories m WHERE m.id=r.id AND m.hold IS NULL),'') ELSE r.content END AS body
                WHERE {own}
              {passageUnits}) t
              CROSS JOIN LATERAL (SELECT {(Configuration == Chosen ? $"CASE WHEN t.part=-1 AND NOT kx.memory THEN {Doc} ELSE {Parsed("t.head", "t.body")} END" : Parsed("t.head", "t.body"))} AS doc OFFSET 0) d),
            held AS (SELECT u.id,{maxima} FROM units u GROUP BY u.id),
            stats AS (SELECT count(*) AS n,{sums} FROM held h),
            weighted AS (
              SELECT DISTINCT ON (u.id) u.*,s.n,{stats} FROM units u CROSS JOIN stats s
              ORDER BY u.id,u.exact DESC,({unitWeight}) DESC,u.density DESC,u.part)
            SELECT {select}{fused},w.passages AS lexical_passages,w.part AS lexical_part,{matched} AS lexical_text FROM weighted w JOIN resources r ON r.id=w.id
            ORDER BY w.exact DESC,({weight}) DESC,w.density DESC,r.indexed_at DESC,{Stable} LIMIT {limit}
            """, [.. args]);
    }

    // A native memory's own row (migration 025: provider memory, no map snapshot); its words are in memories.
    const string MemoryRow = "(r.provider='memory' AND r.relationship_snapshot IS NULL)";

    // The text of a record's passages at its row's revision, in order: one model's, the one `model` (SQL, a model id or
    // '' for none) names when it has them, else the first by id. Every model's passages of a text are its same chunks
    // (one chunker, D-069), so the words are the same whichever model made them, and the keyword arm reads them with the
    // model off too.
    public static string PassageText(string model) => $"""
        coalesce((SELECT string_agg(k.text_content,' ' ORDER BY k.ordinal) FROM resource_chunks k WHERE k.resource_id=r.id AND k.source_revision=r.revision
          AND k.model=(SELECT pm.model FROM resource_chunks pm WHERE pm.resource_id=r.id AND pm.source_revision=r.revision ORDER BY pm.model={model} DESC,pm.model LIMIT 1)),'')
        """;

    // A literal for a Postgres regular expression: every character that is not a letter or digit escaped.
    static string Escaped(string term)
    {
        var text = new StringBuilder(term.Length * 2);
        foreach (var c in term) { if (!char.IsLetterOrDigit(c)) text.Append('\\'); text.Append(c); }
        return text.ToString();
    }

    [GeneratedRegex(@"['’][sS]$")] private static partial Regex Possessive();
    [GeneratedRegex(@"^[0-9]+(st|nd|rd|th|am|pm)$")] private static partial Regex Ordinal();
    [GeneratedRegex(@"[^\p{L}\p{N}]+")] private static partial Regex NonWord();
    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)?$")] private static partial Regex ConfigurationName();
}

// The words a row's own title and content do not hold, for combined retrieval's keyword arm (Recall, SB-P2-19, D-152,
// D-153): a native memory's are its text in memories, never its title (033 titles every one Memory), and a record whose
// text only its passages hold (a note's or a Drive file's star row, whose content is empty) has them in its passages.
// Each kind is found through an index of its own (migration 040) and scored with the catalog's rows, on its text, in one
// ranking, so their words weigh as the catalog's do. Every other row is found and scored on its title and content as
// before, and with no memory and no passage found the answer is the one before.
//   Catalog    the rows found by their own title and content (SQL over a resources row r): Recall leaves every
//              memory-provider row out of these, a memory's own and its star;
//   Memories   whether native memories are found by their text (not held: a held memory's words are never read);
//   Passages   the rows also found and scored by their passages (SQL over r, "false" for none): Recall's are the records
//              whose passages semantic_search serves; From the joins it reads (after `resources r`);
//   Model      SQL naming the passages' preferred model (PassageText);
//   AnyWord    a passage counts when it holds any of the question's words, not only the whole question or an identifier
//              it names: a variant the parity evaluation measures (combined-retrieval.md), never a default.
// The query's result then has columns lexical_passages (whether the row is one scored with its passages), lexical_part
// (the passage it was scored on, -1 for none) and lexical_text (that passage's text).
public sealed record LexicalTexts(string Catalog, bool Memories, string Passages, string From, string Model, bool AnyWord = false);
