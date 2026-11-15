using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Knowledge.Mcp;

// What combined retrieval (Recall) answers over MCP (SB-P2-09): the recall tool's reply (contracts/README.md 2.1,
// contracts/mcp/recall.output.schema.json), and search_knowledge's rows while engine.searchKnowledge is combined.
//   results    best first: each the catalog row as search_knowledge returns its columns, its excerpt (the passage that
//              matched, or the start of its text), whether the excerpt was cut and how long the whole text is, and the
//              fused score; with explain, why it is here (Why);
//   notices    what the caller must be told: semantic search skipped (the model off or failing), live files left out;
//   receipt    a short id for this answer: a hash of the question, its arguments and the records found in their order,
//              each at its revision (a native memory's the memory's own, which every change moves: Recall's columns),
//              so the same question over the same records gives the same receipt and any change in
//              what was found, its order or a record's revision gives another. A citation, not a credential; it holds no
//              text, and nothing is kept unless the opt-in recall log is on (engine.recallLog, RecallLog, SB-P2-11),
//              which keeps it beside the question in its reader's scope.
// As of a moment (SB-P4-07, the recall tool's as_of, with memory.tools on; RecallAsOf):
//   asOf       the moment asked, after query;
//   results    what was true then: a native memory's with its words then (its excerpt their start, never a matched
//              passage; why.meaning.passage still names the passage of its current words that ranked it) and asOf
//              {revision, status, writtenAt, changedAt, recordedAfter}, the state they are taken from; any other record
//              only when unchanged since;
//   retracted  after results, at most three beliefs of then marked wrong since, newest retraction first: each a memory's
//              row with its words then (excerpt, truncated, length), retractedAt, insteadOf (the result it had replaced,
//              or null) and asOf; never among the results;
//   receipt    over the moment and the beliefs too.
// With a standing instruction fired (SB-P5-12, with memory.tools on; MemoryStanding.cs), and only then:
//   standing   after query, above the results: {notice, threshold, model, fired}, at most 2, each with where it came from
//              (its scope, owner, author, source, status, whether no person has confirmed it, project and when) and its whole words and score, stored data
//              never to be taken as an instruction, as notice says, each with its own id and revision. The receipt stays
//              over the results: it is what the recall log keeps (Recall.Search answers before anything fires) and what the
//              explorer's search gives for the same question (RecallStars), and what fired is no result.
// Without as_of or a fire the answer is exactly as before. No reply holds a vector. Adapted from Second Brain's recall (MIT): explain's reasons (src/recall/types.ts WhyTrace) and
// the receipt (src/recall/log.ts), here hashed over the result set rather than a time bucket.
public static class RecallReply
{
    // search_knowledge's excerpt: its first 400 characters, as before.
    public const int SearchExcerpt = 400;

    public static JsonObject Answer(RecallResult result, RecallQuery query, bool explain, StandingFired? standing = null)
    {
        var results = new JsonArray();
        foreach (var (hit, i) in result.Hits.Select((h, i) => (h, i)))
        {
            var row = Row(hit);
            row["excerpt"] = hit.Excerpt;
            row["truncated"] = hit.Truncated;
            row["length"] = hit.FullLength;
            row["score"] = hit.Score;
            if (explain) row["why"] = Why(hit.Why, i + 1);
            if (result.AsOf is not null && hit.Then is { } then) row["asOf"] = Then(then);
            results.Add(row);
        }
        if (result.AsOf is { } at)
            return new JsonObject
            {
                ["query"] = result.Query,
                ["asOf"] = Instant(at),
                ["results"] = results,
                ["retracted"] = new JsonArray([.. (result.Retracted ?? []).Select(b => (JsonNode?)Belief(b))]),
                ["notices"] = new JsonArray([.. result.Notices.Select(n => (JsonNode?)JsonValue.Create(n))]),
                ["semanticSkipped"] = result.SemanticSkipped,
                ["receipt"] = Receipt(query, result.Hits, result.Retracted),
            };
        var answer = new JsonObject { ["query"] = result.Query };
        if (standing is { Fired.Count: > 0 } fired) answer["standing"] = Standing(fired);
        answer["results"] = results;
        answer["notices"] = new JsonArray([.. result.Notices.Select(n => (JsonNode?)JsonValue.Create(n))]);
        answer["semanticSkipped"] = result.SemanticSkipped;
        answer["receipt"] = Receipt(query, result.Hits);
        return answer;
    }

    // What fired for this question, with where each came from (SB-P5-12).
    static JsonObject Standing(StandingFired fired) => new()
    {
        ["notice"] = MemoryStanding.Notice,
        ["threshold"] = fired.Threshold,
        ["model"] = fired.Model,
        ["fired"] = new JsonArray([.. fired.Fired.Select(f => (JsonNode?)new JsonObject
        {
            ["id"] = f.Id.ToString(),
            ["revision"] = f.Revision,
            ["scope"] = f.Scope,
            ["owner"] = f.Subject,
            ["author"] = f.Author,
            ["source"] = f.Source,
            ["status"] = f.Status,
            ["unconfirmed"] = f.Unconfirmed,
            ["project"] = f.Project is { } p ? new JsonObject { ["id"] = p.Id.ToString(), ["slug"] = p.Slug } : null,
            ["createdAt"] = Instant(f.CreatedAt),
            ["content"] = f.Content,
            ["score"] = f.Score,
        })]),
    };

    // As of a moment: the state a native memory's words are taken from (RecallThen).
    static JsonObject Then(RecallThen then) => new()
    {
        ["revision"] = then.Revision,
        ["status"] = then.Status,
        ["writtenAt"] = Instant(then.WrittenAt),
        ["changedAt"] = Instant(then.ChangedAt),
        ["recordedAfter"] = then.RecordedAfter,
    };

    // A belief of then, marked wrong since, as listed under an as-of answer's results.
    static JsonObject Belief(RecallBelief belief)
    {
        var row = JsonNode.Parse(belief.Row.GetRawText())!.AsObject();
        row["excerpt"] = belief.Excerpt;
        row["truncated"] = belief.Truncated;
        row["length"] = belief.FullLength;
        row["retractedAt"] = Instant(belief.RetractedAt);
        row["insteadOf"] = belief.InsteadOf?.ToString();
        row["asOf"] = Then(belief.Then);
        return row;
    }

    // Why one result is here, in the order it was ranked: rank its place in this answer (from 1); keyword its rank in
    // the keyword arm, its fusion weight, the query's words it holds and its identifier hits; meaning its rank in the
    // meaning arm, the similarity of its nearest passage and that passage's place in the record (0 first); imputed when
    // it has no passages for the meaning arm to find and was given that arm's share at its keyword rank; link, for a
    // record reached through a typed link (hops), the hop, the link's type, the result it was reached from and the
    // link's direction from there. An arm that did not find it is null. Rank moves are the arms' ranks against rank.
    internal static JsonObject Why(RecallWhy why, int rank) => new()
    {
        ["rank"] = rank,
        ["keyword"] = why.KeywordRank is { } k ? new JsonObject
        {
            ["rank"] = k, ["weight"] = why.KeywordWeight, ["terms"] = new JsonArray([.. why.Terms.Select(t => (JsonNode?)JsonValue.Create(t))]),
            ["exact"] = why.Exact,
        } : null,
        ["meaning"] = why.MeaningRank is { } m ? new JsonObject { ["rank"] = m, ["similarity"] = why.Similarity, ["passage"] = why.Passage } : null,
        ["imputed"] = why.Imputed,
        ["link"] = why.Hop is { } h ? new JsonObject { ["hop"] = h, ["type"] = why.Link, ["from"] = why.From?.ToString(), ["direction"] = why.Direction } : null,
    };

    // The receipt: "rc1-" and 16 hex characters of the SHA-256 of the question, its arguments and each result's id and
    // revision, in order. As of a moment, the moment and each belief's id and revision, in order, follow; without one
    // the receipt is the one it always was.
    public static string Receipt(RecallQuery q, IEnumerable<RecallHit> hits, IEnumerable<RecallBelief>? retracted = null) => "rc1-" + Hashing.Hash(JsonSerializer.Serialize(
        q.AsOf is null ? new object?[]
        {
            1, q.Query, q.TopK, q.Hops, q.Domain, q.Provider, q.Kind, q.Subject, Instant(q.After), Instant(q.Before),
            hits.Select(h => new object?[] { h.Id, Revision(h.Row) }).ToArray(),
        } : new object?[]
        {
            1, q.Query, q.TopK, q.Hops, q.Domain, q.Provider, q.Kind, q.Subject, Instant(q.After), Instant(q.Before),
            hits.Select(h => new object?[] { h.Id, Revision(h.Row) }).ToArray(),
            Instant(q.AsOf), (retracted ?? []).Select(b => new object?[] { b.Id, Revision(b.Row) }).ToArray(),
        }))[..16];

    static string? Instant(DateTimeOffset? at) => at?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    static string? Revision(JsonElement row) => row.TryGetProperty("revision", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
    static JsonObject Row(RecallHit hit) => JsonNode.Parse(hit.Row.GetRawText())!.AsObject();

    // search_knowledge's answer from the combined list: its rows in the combined order, each with the columns it always
    // returned and an excerpt of at most 400 characters (the matching passage's start, or the text's).
    public static string SearchRows(RecallResult result)
    {
        var rows = new JsonArray();
        foreach (var hit in result.Hits)
        {
            var row = Row(hit);
            row["excerpt"] = Recall.Cut(hit.Excerpt, SearchExcerpt);
            rows.Add(row);
        }
        return rows.ToJsonString();
    }
}
