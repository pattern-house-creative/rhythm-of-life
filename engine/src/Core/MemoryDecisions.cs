using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// The decision ledger for native memories (SB-P5-11; story 12; docs/dev/memory.md sections 5 and 9; migration 060): a
// decision the person made, how sure they were (a confidence, stated or inferred, set once with the memory) and when to
// look at it again; how it turned out (right, wrong, mixed, or unknown: too early to tell), recorded as a change of its
// state that history keeps and undo takes back; and calibration, which compares how sure the person was with how things
// turned out. One predicate (060's memory_decision_listed) defines every list the ledger has (open, due, resolved, all,
// and scored: what calibration rests on), and each count is read with its list in one statement, so a count is the length
// of its list and calibration's n is the length of the scored list. The queue a reader is served is the decisions it
// reads, may change and owns (058's memory_loop_mine, the open loops' rule): calibration is the person's own.
//
// Calibration is plain arithmetic over the scored decisions, adapted from Second Brain's (src/decisions/calibration.ts at
// the pinned commit, MIT; NOTICE): a confidence below 0.5 is a call on the other outcome (0.3 that it works is 0.7 that it
// does not), so it is folded onto 1 - p with the outcome turned over; mixed counts half; the folded calls fall into five
// bands (50-59, 60-69, 70-79, 80-89, 90-95); the Brier score is read on the calls as made; and whether the person was
// over- or under-confident is decided by an 80% Wilson interval around the folded hit rate. Every figure says how many
// decisions it rests on, and none is shown below its minimum: no figure at all below MinDecisions scored decisions, no
// band's figures below MinBand in it, and no topic below MinTopic.
//
// The memory tools (remember's decision, list_decisions, record_outcome and decision_calibration; SB-P3-09) and the app's
// app_memories (decisions, calibration, record_outcome; SB-P6-01) call it, with memory.tools on, off by default. With it off
// nothing is listed, answered or written.

// A decision as its reader is served it. Confidence: 0.05 to 0.95, or none; ConfidenceSource: stated or inferred, with it.
// Outcome: none yet, right, wrong, mixed or unknown; Rearms: how often unknown asked again (at most 2); ReviewAt: when to
// look at it again, none once nothing is left to ask; Due: that date has come (069: the one window rule's overdue); Window:
// which of that rule's windows it falls in (overdue, upcoming within 48 hours, later), none with no date. Current: its window is open (a scored list
// keeps decisions replaced since). Edited: its words differ from those it was recorded with (the version kept from then;
// without one, its words were written again since), so the confidence was given for other words. Content: a preview of at most the asked characters (Truncated when longer); a held memory is never listed.
public sealed record MemoryDecision(Guid Id, int Revision, string Subject, string Scope, string? Author, string Status, decimal? Confidence,
    string? ConfidenceSource, string? Outcome, int Rearms, DateTimeOffset? ReviewAt, bool Due, string? Window, bool Current, bool Edited, IReadOnlyList<string> Tags,
    MemoryProjectRef? Project, DateTimeOffset CreatedAt, string Content, bool Truncated);

public sealed record MemoryDecisionCursor(DateTimeOffset CreatedAt, Guid Id);

// The asked list's length for this reader (Total), the lengths of the open, due, upcoming, resolved and scored lists under the same
// filters, and one page of the asked list, newest first: all read in one statement.
public sealed record MemoryDecisionPage(int Total, int Open, int Due, int Upcoming, int Resolved, int Scored, IReadOnlyList<MemoryDecision> Items,
    MemoryDecisionCursor? Next);

// What remember with decision true asks beside the memory: a confidence as the call gives it (above 0 and at most 1), its
// source (stated or inferred; inferred when a confidence comes without one), and the review date's words (review_by).
public sealed record MemoryDecisionInput(double? Confidence = null, string? ConfidenceSource = null, string? ReviewBy = null);

// What remember with decision true answers: the write, the confidence as kept (rounded to 0.05 within 0.05 to 0.95), its
// source, the review date, and Clamped (highest or lowest) when the confidence given was outside that range.
public sealed record MemoryDecided(MemoryWrite Write, decimal? Confidence, string? ConfidenceSource, DateTimeOffset ReviewAt, string? Clamped);

// What record_outcome answers: the memory as a write answers it, the Result asked and the outcome before it (Previous),
// how often unknown has asked again, the next review date (none once nothing is left to ask), ReviewsDone when an unknown
// stopped the asking, and Changed ["outcome"] or none when Unchanged (it had that outcome already).
public sealed record MemoryOutcome(MemoryWrite Write, string Result, string? Previous, int Rearms, DateTimeOffset? ReviewAt, bool ReviewsDone);

// One scored decision as calibration reads it: the confidence as the person gave it, its source, how it turned out
// (right, wrong or mixed) and its tags (a topic's candidates).
public sealed record DecisionSample(decimal Confidence, string Source, string Outcome, IReadOnlyList<string> Tags);

// A band of folded confidences: how many decisions fall in it (N, of them NStated and NInferred), and only when Shown (N at
// least MinBand, and calibration ready) its figures: the mean folded confidence, the folded hit rate and its 80% Wilson
// interval (Low, High). Below the minimum the figures are null: counts only.
public sealed record CalibrationBand(string Band, int N, int NStated, int NInferred, bool Shown, double? MeanConfidence, double? HitRate,
    double? Low, double? High);

// The one tag (a topic) where the person was furthest from their own confidence, outside the interval, with at least
// MinTopic decisions: its N, Gap (mean folded confidence less folded hit rate) and Direction (over or under).
public sealed record CalibrationTopic(string Name, int N, double Gap, string Direction);

// Calibration for one reader's scored decisions. Ready: N is at least MinDecisions; below it only the counts are given and
// every figure is null. Brier: the mean squared gap between each confidence and its outcome (0 best, 0.25 always saying
// 50%); Gap: mean folded confidence less folded hit rate; Direction: over (sure more often than right), under, or in_line
// (inside the interval). Headline: the band the Line's rate comes from (the fullest shown one; null when none is shown);
// Kind: which line (rate, in_line, no_range, or not_ready); Stated and Hit: the percentages Line names (rate only).
public sealed record DecisionCalibration(bool Ready, int N, int NStated, int NInferred, int MinDecisions, int MinBand, int MinTopic, double? Brier,
    double? Gap, string? Direction, IReadOnlyList<CalibrationBand> Bands, string? Headline, CalibrationTopic? Topic, string Kind, string Line,
    string? TopicLine, int? Stated, int? Hit);

public static class MemoryDecisions
{
    public const string Right = "right", Wrong = "wrong", Mixed = "mixed", Unknown = "unknown";
    public static readonly IReadOnlyList<string> Results = [Right, Wrong, Mixed, Unknown];
    public const string Stated = "stated", Inferred = "inferred";
    // The ledger's lists (060's memory_decision_listed).
    public const string Open = "open", Due = "due", Upcoming = "upcoming", Resolved = "resolved", Scored = "scored", All = "all";
    public static readonly IReadOnlyList<string> Lists = [Open, Due, Upcoming, Resolved, Scored, All];
    // Upstream's minimums (CALIBRATION_MIN_N, CALIBRATION_MIN_BUCKET_N, CALIBRATION_MIN_TOPIC_N): no figure below them.
    public const int MinDecisions = 10, MinBand = 5, MinTopic = 5;
    // review_by's words at most.
    public const int ReviewByMax = 40;
    // The bands, by the folded confidence's whole percent.
    public static readonly IReadOnlyList<string> Bands = ["50-59", "60-69", "70-79", "80-89", "90-95"];
    // z for a two-sided 80% interval.
    public const double Z80 = 1.2815515655446004;
    // Tags that say what kind of note a memory is, not what it is about, so never a topic (upstream's axis tags), and the
    // open loops' own state tag.
    public static readonly IReadOnlySet<string> NotTopics = new HashSet<string>(StringComparer.Ordinal)
        { "personal", "work", "task", "idea", "context", "claude-response", "codex-response", "cursor-response", "task:done" };

    // A list a call names: open (the default), due, resolved, scored or all.
    public static string List(string? named) => (named ?? Open).Trim().ToLowerInvariant() switch
    {
        "" => Open,
        var l when Lists.Contains(l) => l,
        _ => throw new MemoryRefused("invalid", "state is open, due, upcoming, resolved, scored or all"),
    };

    // A calibration's source filter: all (the default), stated or inferred.
    public static string? Source(string? named) => (named ?? "all").Trim().ToLowerInvariant() switch
    {
        "all" or "" => null,
        Stated => Stated,
        Inferred => Inferred,
        _ => throw new MemoryRefused("invalid", "source is all, stated or inferred"),
    };

    // An outcome's share of a hit: right 1, wrong 0, mixed a half.
    static double Hit(string outcome) => outcome switch
    {
        Right => 1,
        Wrong => 0,
        Mixed => 0.5,
        _ => throw new ArgumentException($"{outcome} is not scored", nameof(outcome)),
    };

    static int Percent(double x) => (int)Math.Round(x * 100, MidpointRounding.AwayFromZero);
    static int Percent(decimal x) => (int)Math.Round(x * 100, MidpointRounding.AwayFromZero);
    // Figures as answers give them: four decimal places.
    static double Four(double x) => Math.Round(x, 4, MidpointRounding.AwayFromZero);

    // A call below 50% is a call on the other outcome: folded onto 1 - p, its hit turned over. 50% stays as it is.
    public static (decimal P, double Hit) Fold(decimal p, double hit) => Percent(p) < 50 ? (1 - p, 1 - hit) : (p, hit);

    // The band of a folded confidence (0.5 or more), by its whole percent.
    public static string Band(decimal folded) => Percent(folded) switch
    {
        < 60 => "50-59",
        < 70 => "60-69",
        < 80 => "70-79",
        < 90 => "80-89",
        _ => "90-95",
    };

    // The Wilson score interval for `successes` (a fraction allowed: mixed counts half) out of n, at z (80% by default).
    public static (double Low, double High) Wilson(double successes, int n, double z = Z80)
    {
        if (n <= 0) return (0, 1);
        var p = successes / n;
        var z2 = z * z;
        var denominator = 1 + z2 / n;
        var center = p + z2 / (2.0 * n);
        var margin = z * Math.Sqrt(p * (1 - p) / n + z2 / (4.0 * n * n));
        return (Math.Max(0, (center - margin) / denominator), Math.Min(1, (center + margin) / denominator));
    }

    static string Direction(double meanConfidence, double successes, int n)
    {
        var (low, high) = Wilson(successes, n);
        return meanConfidence > high ? "over" : meanConfidence < low ? "under" : "in_line";
    }

    static string Decisions(int n) => n == 1 ? "1 decision" : $"{n} decisions";

    // Calibration of these decisions, each scored (right, wrong or mixed, with a confidence): pure arithmetic, no reading.
    public static DecisionCalibration Calibrate(IReadOnlyList<DecisionSample> samples)
    {
        var scored = samples.Select(s =>
        {
            var hit = Hit(s.Outcome);
            var (p, h) = Fold(s.Confidence, hit);
            return (Raw: (double)s.Confidence, RawHit: hit, P: p, Hit: h, s.Source, s.Tags);
        }).ToList();
        var n = scored.Count;
        var nStated = scored.Count(s => s.Source == Stated);
        var nInferred = scored.Count(s => s.Source == Inferred);
        var ready = n >= MinDecisions;
        var bands = Bands.Select(band =>
        {
            var items = scored.Where(s => Band(s.P) == band).ToList();
            var shown = ready && items.Count >= MinBand;
            if (!shown) return new CalibrationBand(band, items.Count, items.Count(i => i.Source == Stated), items.Count(i => i.Source == Inferred), false,
                null, null, null, null);
            var successes = items.Sum(i => i.Hit);
            var (low, high) = Wilson(successes, items.Count);
            return new CalibrationBand(band, items.Count, items.Count(i => i.Source == Stated), items.Count(i => i.Source == Inferred), true,
                Four(items.Average(i => (double)i.P)), Four(successes / items.Count), Four(low), Four(high));
        }).ToList();
        if (!ready)
            return new(false, n, nStated, nInferred, MinDecisions, MinBand, MinTopic, null, null, null, bands, null, null, "not_ready",
                $"Calibration shows once {MinDecisions} decisions have an outcome (right, wrong or mixed) and a confidence; there {(n == 1 ? "is" : "are")} {n} so far.",
                null, null, null);

        var meanConfidence = scored.Average(s => (double)s.P);
        var meanHit = scored.Average(s => s.Hit);
        var direction = Direction(meanConfidence, scored.Sum(s => s.Hit), n);
        var brier = scored.Average(s => (s.Raw - s.RawHit) * (s.Raw - s.RawHit));
        // The fullest shown band; a tie goes to the surer band.
        var headline = bands.Where(b => b.Shown).Aggregate((CalibrationBand?)null, (best, b) => best is null || b.N >= best.N ? b : best);

        // At most one topic: of the tags on scored decisions (never a kind-of-note tag), each with at least MinTopic
        // decisions and outside its interval, the largest gap; then the most decisions; then the name.
        var topic = scored.SelectMany(s => s.Tags.Select(t => t.ToLowerInvariant())).Where(t => !NotTopics.Contains(t)).Distinct(StringComparer.Ordinal)
            .Select(name =>
            {
                var rows = scored.Where(s => s.Tags.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase))).ToList();
                var mean = rows.Average(r => (double)r.P);
                return new CalibrationTopic(name, rows.Count, mean - rows.Average(r => r.Hit), Direction(mean, rows.Sum(r => r.Hit), rows.Count));
            })
            .Where(t => t.N >= MinTopic && t.Direction != "in_line")
            .OrderByDescending(t => Math.Abs(t.Gap)).ThenByDescending(t => t.N).ThenBy(t => t.Name, StringComparer.Ordinal)
            .FirstOrDefault();

        var kind = direction == "in_line" ? "in_line" : headline is not null ? "rate" : "no_range";
        var line = kind switch
        {
            "in_line" => $"So far, how sure you were matches how things turned out, within the margin, over {Decisions(n)}.",
            "no_range" => $"A rate shows once {MinBand} decisions share a band of confidence; there are {n} scored so far, none of the bands with {MinBand}.",
            _ => $"So far, when you were about {Percent(headline!.MeanConfidence!.Value)}% sure you were right {Percent(headline.HitRate!.Value)}% of the time, over {Decisions(headline.N)}."
                + (headline.NInferred == 0 ? "" : $" For {headline.NInferred} of them the confidence was read from your words, not stated."),
        };
        var topicLine = topic is null ? null
            : $"On {topic.Name} you were right {(topic.Direction == "over" ? "less" : "more")} often than you expected so far, over {Decisions(topic.N)}.";
        return new(true, n, nStated, nInferred, MinDecisions, MinBand, MinTopic, Four(brier), Four(meanConfidence - meanHit), direction, bands,
            headline?.Band, topic is null ? null : topic with { Gap = Four(topic.Gap) }, kind, line, topicLine,
            kind == "rate" ? Percent(headline!.MeanConfidence!.Value) : null, kind == "rate" ? Percent(headline!.HitRate!.Value) : null);
    }
}

public sealed partial class MemoryStore
{
    public const int DecisionsMax = 50;

    // The decisions a reader is served (the one read predicate on each one's own row), that it owns and may change (058's
    // memory_loop_mine), optionally of one scope; each list is 060's memory_decision_listed on top.
    static (string Where, List<NpgsqlParameter> Args) DecisionQueue(Guid principal, string? scope)
    {
        var args = new List<NpgsqlParameter> { new("principal", NpgsqlDbType.Uuid) { Value = principal } };
        var where = $"""
            {Database.Visible("r")} AND EXISTS(SELECT 1 FROM memory_decisions d WHERE d.memory_id=m.id)
              AND memory_loop_mine(@principal,m.subject_id,m.author_id,scopes_on())
            """;
        string? named;
        try { named = MemorySettings.Named(scope); }
        catch (ArgumentException e) { throw new MemoryRefused("invalid", e.Message); }
        if (named is not null)
        {
            where += " AND m.scope=@scope";
            args.Add(new("scope", NpgsqlDbType.Text) { Value = named });
        }
        return (where, args);
    }

    // The length of a reader's list (open, due, resolved, scored or all).
    public async Task<int> DecisionCount(Guid principal, string? scope, string list, CancellationToken ct)
    {
        list = MemoryDecisions.List(list);
        var (where, args) = DecisionQueue(principal, scope);
        args.Add(new("list", NpgsqlDbType.Text) { Value = list });
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT count(*) {From} WHERE {where} AND memory_decision_listed(m.id,@list)", c);
        foreach (var arg in args) cmd.Parameters.Add(arg);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // One page of a list, newest first, with its whole length and the open, due, upcoming, resolved and scored lists' lengths, in one
    // statement (one snapshot). After continues from a page's Next.
    public async Task<MemoryDecisionPage> Decisions(Guid principal, string? scope, string list, int limit, MemoryDecisionCursor? after, int previewChars,
        CancellationToken ct)
    {
        list = MemoryDecisions.List(list);
        if (limit is < 1 or > DecisionsMax) throw new MemoryRefused("invalid", $"n is 1 to {DecisionsMax}");
        if (previewChars is < 0 or > MemoryInput.PreviewMax) throw new MemoryRefused("invalid", $"A preview is 0 to {MemoryInput.PreviewMax} characters");
        var (where, args) = DecisionQueue(principal, scope);
        args.AddRange([
            new("list", NpgsqlDbType.Text) { Value = list },
            new("limit", NpgsqlDbType.Integer) { Value = limit + 1 }, new("chars", NpgsqlDbType.Integer) { Value = previewChars },
            new("afterAt", NpgsqlDbType.TimestampTz) { Value = after is { } a ? a.CreatedAt.ToUniversalTime() : DBNull.Value },
            new("afterId", NpgsqlDbType.Uuid) { Value = after is { } b ? b.Id : DBNull.Value }]);
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            WITH q AS (
              SELECT m.id,m.created_at,memory_decision_listed(m.id,'open') AS is_open,memory_decision_listed(m.id,'due') AS is_due,
                memory_decision_listed(m.id,'upcoming') AS is_upcoming,
                memory_decision_listed(m.id,'resolved') AS is_resolved,memory_decision_listed(m.id,'scored') AS is_scored,
                memory_decision_listed(m.id,'all') AS is_all,memory_decision_listed(m.id,@list) AS asked
              {From} WHERE {where})
            SELECT t.total,t.open,t.due,t.upcoming,t.resolved,t.scored,x.* FROM (
              SELECT count(*) FILTER (WHERE q.asked) AS total,count(*) FILTER (WHERE q.is_open) AS open,
                count(*) FILTER (WHERE q.is_due) AS due,count(*) FILTER (WHERE q.is_upcoming) AS upcoming,count(*) FILTER (WHERE q.is_resolved) AS resolved,
                count(*) FILTER (WHERE q.is_scored) AS scored FROM q) t
            LEFT JOIN LATERAL (
              SELECT m.id,m.revision,m.subject_id,m.scope,m.author_id,m.status,d.confidence,d.confidence_source,s.outcome,s.rearms,s.review_at,
                coalesce(memory_due_window(s.review_at)='overdue',false),memory_due_window(s.review_at),(m.valid_until IS NULL OR m.valid_until>now()),
                CASE WHEN f.first>=m.revision THEN false WHEN f.words IS NOT NULL THEN f.words<>m.content ELSE m.text_revision>f.first END,
                m.tags,pr.id AS project,pr.slug,m.created_at,left(m.content,@chars),length(m.content)>@chars
              FROM q JOIN memories m ON m.id=q.id JOIN memory_decisions d ON d.memory_id=m.id
              CROSS JOIN LATERAL memory_decision_at(m.id,m.revision) s
              CROSS JOIN LATERAL (SELECT z.first,(SELECT v.content FROM memory_versions v WHERE v.memory_id=m.id AND v.revision=z.first) AS words
                FROM (SELECT min(z.revision) AS first FROM memory_decision_states z WHERE z.memory_id=m.id) z) f
              LEFT JOIN memory_projects pr ON pr.id=m.project_id
              WHERE q.asked AND (@afterAt::timestamptz IS NULL OR (q.created_at,q.id)<(@afterAt,@afterId))
              ORDER BY q.created_at DESC,q.id DESC LIMIT @limit) x ON true
            ORDER BY x.created_at DESC,x.id DESC
            """, c);
        foreach (var arg in args) cmd.Parameters.Add(arg);
        var items = new List<MemoryDecision>();
        int total = 0, open = 0, due = 0, upcoming = 0, resolved = 0, scored = 0;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                (total, open, due, upcoming, resolved, scored) = ((int)r.GetInt64(0), (int)r.GetInt64(1), (int)r.GetInt64(2), (int)r.GetInt64(3), (int)r.GetInt64(4), (int)r.GetInt64(5));
                if (r.IsDBNull(6)) continue;
                string? Text(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                items.Add(new(r.GetGuid(6), r.GetInt32(7), r.GetString(8), r.GetString(9), Text(10), r.GetString(11),
                    r.IsDBNull(12) ? null : r.GetDecimal(12), Text(13), Text(14), r.GetInt16(15), r.IsDBNull(16) ? null : r.GetFieldValue<DateTimeOffset>(16),
                    r.GetBoolean(17), Text(18), r.GetBoolean(19), !r.IsDBNull(20) && r.GetBoolean(20), r.GetFieldValue<string[]>(21),
                    r.IsDBNull(22) ? null : new(r.GetGuid(22), r.GetString(23)), r.GetFieldValue<DateTimeOffset>(24), r.GetString(25), r.GetBoolean(26)));
            }
        var next = items.Count > limit ? new MemoryDecisionCursor(items[limit - 1].CreatedAt, items[limit - 1].Id) : null;
        return new(total, open, due, upcoming, resolved, scored, items.Take(limit).ToList(), next);
    }

    // The scored decisions a reader's calibration rests on (the scored list: the same clause, so their number is that list's
    // length), optionally of one scope and one confidence source, read in one statement.
    public async Task<IReadOnlyList<DecisionSample>> DecisionSamples(Guid principal, string? scope, string? confidenceSource, CancellationToken ct)
    {
        if (confidenceSource is not (null or MemoryDecisions.Stated or MemoryDecisions.Inferred))
            throw new MemoryRefused("invalid", "source is all, stated or inferred");
        var (where, args) = DecisionQueue(principal, scope);
        args.Add(new("source", NpgsqlDbType.Text) { Value = (object?)confidenceSource ?? DBNull.Value });
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            SELECT d.confidence,d.confidence_source,s.outcome,m.tags
            {From} JOIN memory_decisions d ON d.memory_id=m.id CROSS JOIN LATERAL memory_decision_at(m.id,m.revision) s
            WHERE {where} AND memory_decision_listed(m.id,'scored') AND (@source::text IS NULL OR d.confidence_source=@source)
            ORDER BY m.created_at,m.id
            """, c);
        foreach (var arg in args) cmd.Parameters.Add(arg);
        var samples = new List<DecisionSample>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) samples.Add(new(r.GetDecimal(0), r.GetString(1), r.GetString(2), r.GetFieldValue<string[]>(3)));
        return samples;
    }

    // A reader's calibration: its scored decisions (of one scope and source when asked), reckoned.
    public async Task<DecisionCalibration> Calibration(Guid principal, string? scope, string? confidenceSource, CancellationToken ct) =>
        MemoryDecisions.Calibrate(await DecisionSamples(principal, scope, confidenceSource, ct));

    // A new memory that is a decision, written as Create writes a memory, with its confidence and first review date in the
    // same transaction (060's remember_memory_decision). The idempotency key covers the decision too.
    public async Task<MemoryDecided> Decide(Guid principal, MemoryDraft draft, MemoryDecisionInput decision, MemoryCall call, CancellationToken ct)
    {
        if (decision.Confidence is { } raw && (double.IsNaN(raw) || raw <= 0 || raw > 1))
            throw new MemoryRefused("invalid", "confidence is above 0 and at most 1, such as 0.7 for 70%: nothing was saved");
        var source = decision.ConfidenceSource?.Trim().ToLowerInvariant();
        if (source is not (null or MemoryDecisions.Stated or MemoryDecisions.Inferred))
            throw new MemoryRefused("invalid", "confidence_source is stated or inferred: nothing was saved");
        if (source is not null && decision.Confidence is null)
            throw new MemoryRefused("invalid", "confidence_source goes with a confidence: nothing was saved");
        var review = decision.ReviewBy?.Trim();
        if (review is { Length: 0 or > MemoryDecisions.ReviewByMax } || review is not null && review.Any(char.IsControl))
            throw new MemoryRefused("invalid", "review_by is a date (YYYY-MM-DD) or a date and time: nothing was saved");
        var (memory, dropped) = Drafted(draft);
        var asked = new JsonObject { ["confidence"] = decision.Confidence, ["confidenceSource"] = source, ["reviewBy"] = review };
        var (write, answer) = await WriteAnswer(
            "SELECT remember_memory_decision(@principal,@memory::jsonb,@decision::jsonb,@channel,@client,@key)::text", principal, call, dropped, ct,
            ("memory", NpgsqlDbType.Jsonb, memory.ToJsonString()), ("decision", NpgsqlDbType.Jsonb, asked.ToJsonString()));
        var made = answer.GetProperty("decision");
        string? Text(string name) => made.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new(await Linked(principal, write, true, call, ct), made.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDecimal() : null, Text("confidenceSource"),
            made.GetProperty("reviewAt").GetDateTimeOffset(), Text("clamped"));
    }

    // How a decision turned out: right, wrong, mixed or unknown, by whoever may change the memory, as a change of its state
    // (060's record_memory_outcome). Expected: the revision the caller read, refused (conflict) when it moved on. No
    // idempotency key: a repeat answers unchanged.
    public async Task<MemoryOutcome> RecordOutcome(Guid principal, Guid memory, string result, int? expected, MemoryCall call, CancellationToken ct)
    {
        if (call.Key is not null) throw new MemoryRefused("invalid", "An outcome takes no idempotency key: recording it again answers unchanged");
        var named = (result ?? "").Trim().ToLowerInvariant();
        if (!MemoryDecisions.Results.Contains(named)) throw new MemoryRefused("invalid", "result is right, wrong, mixed or unknown");
        var (write, answer) = await WriteAnswer("SELECT record_memory_outcome(@principal,@id,@result,@expected,@channel,@client)::text", principal, call, [], ct,
            ("id", NpgsqlDbType.Uuid, memory), ("result", NpgsqlDbType.Text, named), ("expected", NpgsqlDbType.Integer, expected));
        return OutcomeFrom(write, answer, named);
    }

    static MemoryOutcome OutcomeFrom(MemoryWrite write, JsonElement answer, string named)
    {
        var review = answer.TryGetProperty("reviewAt", out var at) && at.ValueKind == JsonValueKind.String ? at.GetDateTimeOffset() : (DateTimeOffset?)null;
        var rearms = answer.GetProperty("rearms").GetInt32();
        return new(write, answer.GetProperty("result").GetString()!,
            answer.TryGetProperty("previous", out var was) && was.ValueKind == JsonValueKind.String ? was.GetString() : null, rearms, review,
            named == MemoryDecisions.Unknown && review is null);
    }
}
