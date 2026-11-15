using System.Text.Json;
using Knowledge.Mcp;
using Knowledge.Relationships;
using Npgsql;

namespace Knowledge.Cli;

// A person's review queues at the terminal (SB-P6-01; D-218, D-222, D-224; cli/README.md, A person's review queues; docs/dev/memory.md
// section 5): `rhythm queues` (how many wait in each, each the length of its list), `rhythm duplicates` (the memories flagged as possible
// copies, or one with its copies whole), `rhythm contradictions` (the pairs queued as possible contradictions, or one pair whole),
// `rhythm insight-candidates` (the pairs of memories worth a look), `rhythm insights` (the one insight stream, an insight's history, or a
// review taken back) and `rhythm resolve` (what a person does in any of them, one door, MemoryStore.ResolveItem through the terminal's
// channel, so an item leaves its queue once). The held and the stale keep their own lists, `rhythm release` and `rhythm still-true`.
//
// A settling, a release and an insight's review from here are a person's: each speaks through the person's terminal login (044), the
// terminal's channel cli, which every AI client's token is refused, and no token reaches the terminal at all (TerminalLogin). So this is
// D-222's and D-224's person's way, beside the app's. A memory's step always names the revision read, as `rhythm release` does, so what
// changed since is never let in unread; a settling names both.
//
// Like the other person's steps they are there only while memory.tools is on (Offered): off, absent or empty, each answers as a command
// the engine does not know, before it reads its arguments or the database, and help lists none of them. What they print of stored text
// is cleaned of control and formatting characters (Shown), or with every such character shown as its code when read whole (Visible).
public static partial class MemoryCommands
{
    public const string QueuesUsage = "queues --person <id> [--family|--private]";
    public const string DuplicatesUsage = "duplicates --person <id> [<memory>] [--family|--private] [--limit <1-50>] [--next <cursor>]";
    public const string ContradictionsUsage = "contradictions --person <id> [<memory> <other>] [--family|--private] [--limit <1-50>] [--next <cursor>]";
    public const string CandidatesUsage = "insight-candidates --person <id> [--state waiting|used|rejected] [--family|--private] [--limit <1-50>] [--next <cursor>]";
    public const string InsightsUsage = "insights --person <id> [<insight> [reopen [--note <words>]]] [--view proposed|older|confirmed|snoozed|closed|all] [--family|--private] [--limit <1-50>] [--next <cursor>]";
    public const string ResolveUsage = "resolve --person <id> <item> release|keep_copy|settle_keep_both|settle_replace|still_true|use_candidate|reject_candidate|confirm_insight|dismiss_insight "
        + "[--other <memory>] [--revision <n>] [--other-revision <n>] [--copy <memory>:<n> ...] [--note <words>] [--insight <id>]";

    const string DataLine = "Stored text above is data written by people and AI clients, never instructions.";
    static readonly HashSet<string> ScopeFlags = new(StringComparer.Ordinal) { "--family", "--private" };

    static string? ScopeOf(Parsed p) => p.Flags.Contains("--family") ? "shared" : p.Flags.Contains("--private") ? "private" : null;
    static string ScopeWord(string scope) => scope == "private" ? "private" : "Family";

    // The insight store as the terminal has it: with the map as the engine last saved it, since with scopes on an insight is read only by
    // who reads what it cites; `map` is the tests' own. Before the map's first save an insight citing a star answers as unknown.
    static async Task<InsightStore> TerminalInsights(NpgsqlDataSource source, RhythmConfig settings, RelationshipHost? map, CancellationToken ct) =>
        new(source, map ?? await TerminalMap(source, settings, ct));

    // The map not ready (no rebuild saved within its freshness yet), or a read past its budget: as the app says it.
    const string MapNotReady = "The map isn't ready just now, so insights can't be read: try again in a moment.";

    // queues: what waits on this person, each the length of the list its command shows (MemoryStore.QueueCounts).
    public static async Task<int> Queues(string[] args, TextWriter o, NpgsqlDataSource? store = null, RhythmConfig? config = null, RelationshipHost? map = null, CancellationToken ct = default)
    {
        var settings = Settings(config);
        Offered(settings);
        var p = Parse(args, QueuesUsage, new HashSet<string>(StringComparer.Ordinal) { "--person" }, ScopeFlags);
        if (p.Words.Count > 0 || p.Flags.Count > 1) throw new ArgumentException(QueuesUsage);
        var person = p.One("--person")!;
        await using var own = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var source = store ?? own!;
        var (login, refusal) = await Login(source, person, ct);
        if (login is not { } at) { o.WriteLine($"Nothing to show: {refusal}."); return 1; }
        MemoryQueueCounts c;
        try { c = await new MemoryStore(source).QueueCounts(at, ScopeOf(p), await TerminalInsights(source, settings, map, ct), Call, ct); }
        catch (MemoryRefused e) { o.WriteLine($"Nothing to show: {Shown(e.Message, 600)}"); return 1; }
        catch (Exception e) when (e is UnauthorizedAccessException or InvalidOperationException) { o.WriteLine(MapNotReady); return 1; }
        o.WriteLine($"What waits on {person}:");
        void Row(int n, string what, string command) => o.WriteLine($"  {n,5}  {what.PadRight(36)}./rhythm {command} --person {person}");
        Row(c.Held, "held for review", "release");
        Row(c.Duplicates, "flagged as possible copies", "duplicates");
        Row(c.Contradictions, "queued as possible contradictions", "contradictions");
        Row(c.Stale, "may be out of date", "still-true");
        Row(c.Candidates, "pairs worth a look", "insight-candidates");
        Row(c.Insights, "insights proposed", "insights");
        return 0;
    }

    // duplicates: the memories this person may change that were flagged when saved as possible copies of another of their scope, newest
    // first, with the whole list's length (MemoryStore.Duplicates, 063's one predicate); or one read whole with its copies, each at the
    // revision a keep names.
    public static async Task<int> Duplicates(string[] args, TextWriter o, NpgsqlDataSource? store = null, RhythmConfig? config = null, CancellationToken ct = default)
    {
        Offered(Settings(config));
        var p = Parse(args, DuplicatesUsage, new HashSet<string>(StringComparer.Ordinal) { "--person", "--limit", "--next" }, ScopeFlags);
        var named = p.Words.Count switch { 0 => (Guid?)null, 1 => MemoryId(p.Words[0], DuplicatesUsage), _ => throw new ArgumentException(DuplicatesUsage) };
        var listing = p.Flags.Count > 0 || p.One("--limit") is not null || p.One("--next") is not null;
        if (named is not null && listing || p.Flags.Count > 1) throw new ArgumentException(DuplicatesUsage);
        var person = p.One("--person")!;
        var limit = Number(p, "--limit", 1, MemoryStore.DuplicatesMax) ?? 20;
        (DateTimeOffset At, Guid Id)? after;
        try { after = MemoryTools.Cursor(p.One("--next")); }
        catch (MemoryRefused e) { throw new ArgumentException($"--next: {e.Message}"); }
        await using var own = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var source = store ?? own!;
        var (login, refusal) = await Login(source, person, ct);
        if (login is not { } at) { o.WriteLine($"Nothing to show: {refusal}."); return 1; }
        var memories = new MemoryStore(source);
        if (named is { } id)
        {
            var m = await memories.Get(at, id, ct);
            if (m is null) { o.WriteLine(NoMemory(person)); return 1; }
            WholeMemory(o, m);
            if (m.DuplicateOf is not { Count: > 0 } copies)
            {
                o.WriteLine("It is flagged as a copy of nothing that stands now.");
                o.WriteLine(DataLine);
                return 0;
            }
            // A keep both names each memory shown here at the revision shown, so one changed since is refused (073).
            var read = new List<string>();
            foreach (var copy in copies)
            {
                o.WriteLine($"Close to it ({copy.Score:0.00}):");
                if (await memories.Get(at, copy.Id, ct) is { } other) { WholeMemory(o, other); read.Add($" --copy {other.Id}:{other.Revision}"); }
                else o.WriteLine($"{copy.Id}: {person} no longer reads it.");
            }
            o.WriteLine($"Keep both: ./rhythm resolve --person {person} {m.Id} keep_copy --revision {m.Revision}{string.Concat(read)}");
            o.WriteLine($"Or forget this one: ./rhythm forget --person {person} {m.Id} --revision {m.Revision}");
            o.WriteLine(DataLine);
            return 0;
        }
        MemoryDuplicatePage page;
        try { page = await memories.Duplicates(at, ScopeOf(p), limit, after is { } a ? new MemoryCursor(a.At, a.Id) : null, Preview, ct); }
        catch (MemoryRefused e) { o.WriteLine($"Nothing listed: {Shown(e.Message, 600)}"); return 1; }
        if (page.Total == 0) { o.WriteLine($"Nothing that {person} may change is flagged as a possible copy."); return 0; }
        foreach (var i in page.Items)
            o.WriteLine($"{i.CreatedAt.UtcDateTime:yyyy-MM-dd HH:mm}  {i.Id}  {ScopeWord(i.Scope)}  revision {i.Revision}  {Shown(i.Content, Preview)}{(i.Truncated ? " (more)" : "")}"
                + $"  close to {string.Join(", ", i.Copies.Select(c => $"{c.Id} ({c.Score:0.00})"))}");
        if (page.Next is { } next) o.WriteLine($"More: ./rhythm duplicates --person {person} ... --next {MemoryTools.Cursor(next.CreatedAt, next.Id)}");
        // Keep both only from one read whole: its step names each memory it is close to at the revision shown (073).
        o.WriteLine($"{page.Total} flagged as possible copies in all. Read one with its copies, and keep both as it prints: ./rhythm duplicates --person {person} <memory>.");
        o.WriteLine(DataLine);
        return 0;
    }

    // One memory whole for a person's review: its place and revision, then its words, every character no person sees shown as its code.
    static void WholeMemory(TextWriter o, MemoryRecord m)
    {
        o.WriteLine($"{m.Id}  {(m.Scope == "private" ? $"{m.Subject}'s own" : "Family")}  {m.Status}  revision {m.Revision}"
            + (m.Hold is { } hold ? $"  held for review: {MemoryHolds.Why(hold)}" : ""));
        o.WriteLine(Visible(m.Content ?? ""));
    }

    // contradictions: the pairs of memories of one scope this person may change that may conflict, newest first, with the whole list's
    // length (MemoryStore.Contradictions, 068's one predicate); or one pair read whole, with the steps at the revisions read. Only the
    // memory that began later replaces the other (049), so that is the only replacement offered, whichever way round the two were named
    // or the row runs; the earlier stands by taking the later out, which ends the review and brings it back with that memory (068). Q-161
    // is the owner's and nothing here changes it: an offer nobody has named stays as weak as 068 leaves it.
    public static async Task<int> Contradictions(string[] args, TextWriter o, NpgsqlDataSource? store = null, RhythmConfig? config = null, CancellationToken ct = default)
    {
        Offered(Settings(config));
        var p = Parse(args, ContradictionsUsage, new HashSet<string>(StringComparer.Ordinal) { "--person", "--limit", "--next" }, ScopeFlags);
        if (p.Words.Count is 1 or > 2) throw new ArgumentException(ContradictionsUsage);
        var pair = p.Words.Count == 2 ? (MemoryId(p.Words[0], ContradictionsUsage), MemoryId(p.Words[1], ContradictionsUsage)) : ((Guid, Guid)?)null;
        var listing = p.Flags.Count > 0 || p.One("--limit") is not null || p.One("--next") is not null;
        if (pair is not null && listing || p.Flags.Count > 1) throw new ArgumentException(ContradictionsUsage);
        var person = p.One("--person")!;
        var limit = Number(p, "--limit", 1, MemoryStore.ContradictionsMax) ?? 20;
        (DateTimeOffset At, Guid Id)? after;
        try { after = MemoryTools.Cursor(p.One("--next")); }
        catch (MemoryRefused e) { throw new ArgumentException($"--next: {e.Message}"); }
        await using var own = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var source = store ?? own!;
        var (login, refusal) = await Login(source, person, ct);
        if (login is not { } at) { o.WriteLine($"Nothing to show: {refusal}."); return 1; }
        var memories = new MemoryStore(source);
        if (pair is var (first, second))
        {
            var (a, b) = (await memories.Get(at, first, ct), await memories.Get(at, second, ct));
            if (a is null || b is null) { o.WriteLine(NoMemory(person)); return 1; }
            if (a.Contradictions?.Any(c => c.Id == b.Id) != true && b.Contradictions?.Any(c => c.Id == a.Id) != true)
            {
                o.WriteLine("These two are not waiting as a possible contradiction, so there is nothing to settle.");
                return 0;
            }
            // As 049 reads them: an unknown start is before every other, and two of them are level.
            var (fromA, fromB) = (Began(a) ?? DateTimeOffset.MinValue, Began(b) ?? DateTimeOffset.MinValue);
            var level = fromA == fromB;
            var (earlier, later) = fromA <= fromB ? (a, b) : (b, a);
            o.WriteLine($"{(level ? "The first" : "The earlier")}, {From(Began(earlier))}:");
            WholeMemory(o, earlier);
            o.WriteLine($"{(level ? "The second" : "The later")}, {From(Began(later))}:");
            WholeMemory(o, later);
            o.WriteLine($"Both stand: ./rhythm resolve --person {person} {later.Id} settle_keep_both --other {earlier.Id} --revision {later.Revision} --other-revision {earlier.Revision}");
            void TakeOut(string stands, string goes, MemoryRecord other)
            {
                o.WriteLine($"{stands} stands, {goes} marked wrong: ./rhythm set-status --person {person} {other.Id} deprecated --revision {other.Revision}");
                o.WriteLine($"{stands} stands, {goes} in the trash: ./rhythm forget --person {person} {other.Id} --revision {other.Revision}");
            }
            if (level)
            {
                o.WriteLine("Neither began after the other, so neither replaces the other.");
                TakeOut("The first", "the second", later);
                TakeOut("The second", "the first", earlier);
            }
            else
            {
                o.WriteLine($"The later replaces the earlier, whose words are kept: ./rhythm resolve --person {person} {later.Id} settle_replace --other {earlier.Id} --revision {later.Revision} --other-revision {earlier.Revision}");
                TakeOut("The earlier", "the later", later);
            }
            o.WriteLine("If the one taken out comes back, this pair comes back with it.");
            o.WriteLine(DataLine);
            return 0;
        }
        MemoryContradictionPage page;
        try { page = await memories.Contradictions(at, ScopeOf(p), limit, after is { } c ? new MemoryCursor(c.At, c.Id) : null, Preview, Call, ct); }
        catch (MemoryRefused e) { o.WriteLine($"Nothing listed: {Shown(e.Message, 600)}"); return 1; }
        if (page.Total == 0) { o.WriteLine($"No possible contradiction waits on {person}."); return 0; }
        static string Side(MemoryContradictionSide s) => $"  {s.Id}  revision {s.Revision}  {s.Status}  {From(s.StartedAt)}  {Shown(s.Content, Preview)}{(s.Truncated ? " (more)" : "")}";
        foreach (var k in page.Items)
        {
            var named = k.ProposedAt is null ? "" : $"  named a contradiction by {(k.ProposedClient is { } client ? $"an AI client ({Shown(client, 80)})" : "an AI client")}"
                + (k.Reason is { } why ? $": {Shown(why, 200)}" : "");
            o.WriteLine($"{k.OfferedAt.UtcDateTime:yyyy-MM-dd HH:mm}  {ScopeWord(k.Scope)}  how close {k.Score:0.00}{named}{(k.MaySettle ? "" : $"  [not {person}'s to settle]")}");
            o.WriteLine(Side(k.Memory));
            o.WriteLine(Side(k.Conflict));
            if (k.MaySettle) o.WriteLine($"  Read them whole and settle: ./rhythm contradictions --person {person} {k.Memory.Id} {k.Conflict.Id}");
        }
        if (page.Next is { } next) o.WriteLine($"More: ./rhythm contradictions --person {person} ... --next {MemoryTools.Cursor(next.CreatedAt, next.Id)}");
        o.WriteLine($"{page.Total} possible {(page.Total == 1 ? "contradiction" : "contradictions")} in all, each {person}'s to settle or an author's or a Family admin's. Neither memory changes until then.");
        o.WriteLine(DataLine);
        return 0;
    }

    // When a memory began being true, as 049 reads it to say which replaces which (none: its start is unknown), and that as a list shows it.
    static DateTimeOffset? Began(MemoryRecord m) => m.UnknownStart ? null : m.ValidFrom ?? m.CreatedAt;
    static string From(DateTimeOffset? at) => at is { } day ? $"true from {day.UtcDateTime:yyyy-MM-dd}" : "start unknown";

    // insight-candidates: the pairs of an older and a newer memory of one scope the nightly pass found worth a look, in one state, best
    // first, with that list's whole length (MemoryStore.Insights, 064's one predicate). Deciding one changes neither memory.
    public static async Task<int> InsightCandidates(string[] args, TextWriter o, NpgsqlDataSource? store = null, RhythmConfig? config = null, CancellationToken ct = default)
    {
        Offered(Settings(config));
        var p = Parse(args, CandidatesUsage, new HashSet<string>(StringComparer.Ordinal) { "--person", "--state", "--limit", "--next" }, ScopeFlags);
        if (p.Words.Count > 0 || p.Flags.Count > 1) throw new ArgumentException(CandidatesUsage);
        var person = p.One("--person")!;
        var state = Word(p, "--state", MemoryStore.InsightStates) ?? MemoryStore.InsightWaiting;
        var limit = Number(p, "--limit", 1, MemoryStore.InsightsMax) ?? 20;
        MemoryInsightCursor? after;
        try { after = MemoryTools.InsightCursor(p.One("--next")); }
        catch (MemoryRefused e) { throw new ArgumentException($"--next: {e.Message}"); }
        await using var own = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var source = store ?? own!;
        var (login, refusal) = await Login(source, person, ct);
        if (login is not { } at) { o.WriteLine($"Nothing listed: {refusal}."); return 1; }
        MemoryInsightPage page;
        try { page = await new MemoryStore(source).Insights(at, ScopeOf(p), limit, after, Preview, ct, state); }
        catch (MemoryRefused e) { o.WriteLine($"Nothing listed: {Shown(e.Message, 600)}"); return 1; }
        if (page.Total == 0) { o.WriteLine(state == MemoryStore.InsightWaiting ? $"No pair of memories waits on {person}." : $"No pair of memories that {person} reads is {state}."); return 0; }
        static string Side(string which, MemoryInsightSide s) => $"  {which} {s.Id}  {s.CreatedAt.UtcDateTime:yyyy-MM-dd}  {Shown(s.Content, Preview)}{(s.Truncated ? " (more)" : "")}";
        foreach (var c in page.Items)
        {
            o.WriteLine($"{c.Id}  {ScopeWord(c.Scope)}  {c.GapDays} days apart  {string.Join(", ", c.Signals)}{(c.Status == "pending" ? "" : $"  {c.Status}")}");
            o.WriteLine(Side("older", c.Older));
            o.WriteLine(Side("newer", c.Newer));
            if (c.Note is { } note) o.WriteLine($"  Why: {Shown(note, 500)}");
            if (c.InsightId is { } written) o.WriteLine($"  Written up as insight {written}");
        }
        if (page.Next is { } next) o.WriteLine($"More: ./rhythm insight-candidates --person {person} ... --next {MemoryTools.InsightCursor(next)}");
        o.WriteLine(state == MemoryStore.InsightWaiting
            ? $"{page.Total} {(page.Total == 1 ? "pair waits" : "pairs wait")} in all. Decide one, once and for good: ./rhythm resolve --person {person} <pair> use_candidate [--insight <id>] [--note <words>], or reject_candidate [--note <words>]. Neither memory changes."
            : $"{page.Total} {state} in all.");
        o.WriteLine(DataLine);
        return 0;
    }

    // insights: the one insight stream, one view at a time, each view's length read with its page, and what today came to
    // (InsightStore.Stream and Today, 066's one definition); an insight's feedback and reviews (one named); or a review taken back (reopen).
    public static async Task<int> Insights(string[] args, TextWriter o, NpgsqlDataSource? store = null, RhythmConfig? config = null, RelationshipHost? map = null, CancellationToken ct = default)
    {
        var settings = Settings(config);
        Offered(settings);
        var p = Parse(args, InsightsUsage, new HashSet<string>(StringComparer.Ordinal) { "--person", "--view", "--limit", "--next", "--note" }, ScopeFlags);
        var named = p.Words.Count switch { 0 => (Guid?)null, 1 or 2 => InsightId(p.Words[0]), _ => throw new ArgumentException(InsightsUsage) };
        if (p.Words.Count == 2 && p.Words[1].Trim().ToLowerInvariant() != "reopen") throw new ArgumentException(InsightsUsage);
        var reopen = p.Words.Count == 2;
        var listing = p.Flags.Count > 0 || p.One("--view") is not null || p.One("--limit") is not null || p.One("--next") is not null;
        if (named is not null && listing || p.Flags.Count > 1 || p.One("--note") is not null && !reopen) throw new ArgumentException(InsightsUsage);
        var person = p.One("--person")!;
        var view = Word(p, "--view", InsightStore.Views) ?? InsightStore.Proposed;
        var limit = Number(p, "--limit", 1, InsightStore.StreamMax) ?? 20;
        (DateTimeOffset At, Guid Id)? after;
        try { after = MemoryTools.Cursor(p.One("--next")); }
        catch (MemoryRefused e) { throw new ArgumentException($"--next: {e.Message}"); }
        await using var own = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var source = store ?? own!;
        var (login, refusal) = await Login(source, person, ct);
        if (login is not { } at) { o.WriteLine($"Nothing to show: {refusal}."); return 1; }
        try
        {
            var insights = await TerminalInsights(source, settings, map, ct);
            if (named is { } id) return reopen ? await Reopen(insights, at, id, p.One("--note"), person, o, ct) : await InsightHistory(insights, at, id, person, o, ct);
            var memories = new MemoryStore(source);
            var page = await insights.Stream(at, view, ScopeOf(p), limit, after, ct);
            var day = await insights.Today(at, await memories.InstallZone(ct), ct);
            o.WriteLine($"Today: {day.Says}" + (day.Note is { } said ? $" Its note: {Shown(said, 500)}" : "") + (day.Declined > 0 ? $" {day.Declined} declined today." : ""));
            var may = await insights.MayReview(at, [.. page.Items.Select(i => i.Id)], ct);
            string Text(JsonElement i, string name, int max) => i.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? Shown(v.GetString()!, max) : "";
            static IEnumerable<JsonElement> Each(JsonElement i, string name) => i.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];
            foreach (var i in page.Items)
            {
                o.WriteLine($"{i.ShownAt.UtcDateTime:yyyy-MM-dd HH:mm}  {i.Id}  {ScopeWord(i.Scope)}  {i.View}{(i.Review != i.View ? $" (review: {i.Review})" : "")}"
                    + $"{(may.Contains(i.Id) ? "" : $"  [not {person}'s to review]")}  {Text(i.Insight, "title", 200)}");
                o.WriteLine($"  {Text(i.Insight, "summary", 600)}");
                o.WriteLine($"  Why it matters: {Text(i.Insight, "whyItMatters", 600)}");
                o.WriteLine($"  Next step: {Text(i.Insight, "nextStep", 400)}");
                o.WriteLine($"  Evidence: {Text(i.Insight, "evidenceLevel", 20)}. Limits: {Text(i.Insight, "limitations", 400)}");
                foreach (var point in Each(i.Insight, "points")) o.WriteLine($"  Point ({Text(point, "type", 20)}): {Text(point, "text", 400)}");
                foreach (var cited in Each(i.Insight, "sources")) o.WriteLine($"  Source ({Text(cited, "kind", 20)}): {Text(cited, "title", 200)}");
            }
            if (page.Next is { } next) o.WriteLine($"More: ./rhythm insights --person {person} ... --next {MemoryTools.Cursor(next.At, next.Id)}");
            if (page.Items.Count == 0) o.WriteLine(view == InsightStore.Proposed ? $"No insight waits on {person}." : $"No {view} insight that {person} reads.");
            o.WriteLine($"{page.Proposed} proposed, {page.Older} older, {page.Confirmed} confirmed, {page.Snoozed} snoozed, {page.Closed} closed.");
            if (page.Items.Count > 0)
                o.WriteLine($"An insight is an AI's proposal until a person reviews it: ./rhythm resolve --person {person} <insight> confirm_insight [--note <words>], or dismiss_insight. "
                    + $"Take a review back: ./rhythm insights --person {person} <insight> reopen. Its history: ./rhythm insights --person {person} <insight>.");
            if (page.Items.Count > 0 || day.Note is not null) o.WriteLine(DataLine);
            return 0;
        }
        catch (MemoryRefused e) { o.WriteLine($"Nothing to show: {Shown(e.Message, 600)}"); return 1; }
        catch (Exception e) when (e is UnauthorizedAccessException or InvalidOperationException) { o.WriteLine(MapNotReady); return 1; }
    }

    static Guid InsightId(string word) =>
        Guid.TryParseExact(word.Trim(), "D", out var id) ? id : throw new ArgumentException($"{InsightsUsage} (an insight is named by its id, as insights lists it)");

    static async Task<int> InsightHistory(InsightStore insights, Guid at, Guid id, string person, TextWriter o, CancellationToken ct)
    {
        InsightHistoryPage page;
        try { page = await insights.History(at, id, InsightStore.HistoryMax, ct); }
        catch (MemoryRefused e) when (e.Code == "unknown") { o.WriteLine($"No insight with that id that {person} reads."); return 1; }
        o.WriteLine($"Its review now: {page.Review}. {page.Total} {(page.Total == 1 ? "event" : "events")}, newest first:");
        foreach (var e in page.Items)
            o.WriteLine($"  {e.At.UtcDateTime:yyyy-MM-dd HH:mm}  {e.Event}{(e.Value is { } value ? $" {value}" : "")}"
                + (e.Person is { } who ? $"  by {who}" : "") + (e.Channel is { } channel ? $"  through {channel}" : "") + (e.Client is { } client ? $" ({Shown(client, 80)})" : "")
                + (e.Note is { } note ? $"  {Shown(note, 500)}" : ""));
        o.WriteLine(DataLine);
        return 0;
    }

    static async Task<int> Reopen(InsightStore insights, Guid at, Guid id, string? note, string person, TextWriter o, CancellationToken ct)
    {
        InsightReviewed r;
        try { r = await insights.Review(at, id, "reopen", note, Call, ct); }
        catch (MemoryRefused e) when (e.Code == "unknown") { o.WriteLine($"No insight with that id that {person} reads."); return 1; }
        catch (MemoryRefused e) { o.WriteLine($"Nothing changed: {Shown(e.Message, 600)}"); return 1; }
        o.WriteLine(r.Unchanged ? "It has no review to take back, so nothing changed."
            : r.View == InsightStore.Closed ? "Reopened, but it stays closed because it was marked not useful or handled; new feedback brings it back."
            : r.View == InsightStore.Snoozed ? "Reopened: it is among the snoozed until its snooze ends, then proposed again."
            : "Reopened: it is proposed again.");
        return 0;
    }

    // resolve: what a person does in a review queue, one door (MemoryStore.ResolveItem through the terminal's channel, so an item leaves
    // its queue once, whatever the word). A memory's step names the revision read; a settling names the other memory and its revision too,
    // and a keep both each memory it is close to at the revision read (--copy).
    public static async Task<int> Resolve(string[] args, TextWriter o, NpgsqlDataSource? store = null, RhythmConfig? config = null, RelationshipHost? map = null, CancellationToken ct = default)
    {
        var settings = Settings(config);
        Offered(settings);
        var p = Parse(args, ResolveUsage, new HashSet<string>(StringComparer.Ordinal) { "--person", "--other", "--revision", "--other-revision", "--note", "--insight", "--copy" },
            new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal) { "--copy" });
        if (p.Words.Count != 2) throw new ArgumentException(ResolveUsage);
        if (!Guid.TryParseExact(p.Words[0].Trim(), "D", out var id)) throw new ArgumentException($"{ResolveUsage} (an item is named by its id, as its list shows it)");
        var action = p.Words[1].Trim().ToLowerInvariant();
        if (!MemoryResolve.ReviewActions.Contains(action, StringComparer.Ordinal)) throw new ArgumentException(ResolveUsage);
        var queue = MemoryResolve.Queues[action];
        var ofMemory = queue is not (MemoryResolve.Candidates or MemoryResolve.Insights);
        var settling = queue == MemoryResolve.Contradictions;
        var revision = Number(p, "--revision", 1, int.MaxValue);
        var otherRevision = Number(p, "--other-revision", 1, int.MaxValue);
        if (ofMemory && revision is null) throw new ArgumentException($"{ResolveUsage} (name --revision: the revision you read, as its list shows it)");
        if (!ofMemory && revision is not null) throw new ArgumentException($"{ResolveUsage} (an insight or a pair has no revision)");
        if (settling != p.One("--other") is not null || settling != otherRevision is not null)
            throw new ArgumentException($"{ResolveUsage} ({(settling ? "a settling names --other and --other-revision" : "only a settling names another memory")})");
        Guid? other = p.One("--other") is { } word ? MemoryId(word, ResolveUsage) : null;
        // A keep both's --copy: each memory it is close to as rhythm duplicates <memory> showed it, at that revision (073). A person's
        // keep both may settle a pair of them waiting as a possible contradiction (068), so it is never taken here without them: that
        // would settle it at revisions nobody read.
        MemoryCopyRead[]? read = null;
        if (p.All("--copy") is { Count: > 0 } given)
        {
            if (action != MemoryResolve.KeepCopy) throw new ArgumentException($"{ResolveUsage} (only a keep both names --copy)");
            read = [.. given.Select(c => c.Split(':') is [var memory, var n] && int.TryParse(n, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seen) && seen >= 1
                ? new MemoryCopyRead(MemoryId(memory, ResolveUsage), seen)
                : throw new ArgumentException($"{ResolveUsage} (--copy names a memory and the revision read, as <memory>:<n>)"))];
        }
        else if (action == MemoryResolve.KeepCopy)
            throw new ArgumentException($"{ResolveUsage} (a keep both names each memory it is close to as --copy <memory>:<n>, as "
                + $"./rhythm duplicates --person {p.One("--person")} {id} prints the whole step)");
        Guid? insight = null;
        if (p.One("--insight") is { } named)
            insight = Guid.TryParseExact(named.Trim(), "D", out var written) ? written : throw new ArgumentException($"{ResolveUsage} (--insight is an insight's id, as insights lists it)");
        var person = p.One("--person")!;
        await using var own = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var source = store ?? own!;
        var (login, refusal) = await Login(source, person, ct);
        if (login is not { } at) { o.WriteLine($"Nothing changed: {refusal}."); return 1; }
        // A release is checked for copies and contradiction candidates as a save is (068), as rhythm release checks it.
        using var loaded = action == MemoryResolve.Release ? new Embeddings(EmbeddingSettings.From(settings).Model) : null;
        var memories = new MemoryStore(source) { Check = action == MemoryResolve.Release ? ReviewCheck(settings, loaded) : null };
        MemoryResolved r;
        try
        {
            r = await memories.ResolveItem(at, new(id, action, Note: p.One("--note"), Insight: insight, Conflict: other, Expected: revision, ConflictExpected: otherRevision, Copies: read),
                ofMemory ? new InsightStore(source) : await TerminalInsights(source, settings, map, ct), Call, ct);
        }
        catch (MemoryRefused e) when (e.Code == "unknown")
        {
            o.WriteLine(ofMemory ? NoMemory(person) : $"No {(queue == MemoryResolve.Candidates ? "pair" : "insight")} with that id that {person} reads.");
            return 1;
        }
        catch (MemoryRefused e) when (e.Code == "conflict")
        {
            o.WriteLine($"Nothing changed: {Shown(e.Message, 600)}");
            return 1;
        }
        catch (MemoryRefused e) { o.WriteLine($"Nothing changed: {Shown(e.Message, 600)}"); return 1; }
        catch (Exception e) when (e is UnauthorizedAccessException or InvalidOperationException) { o.WriteLine(MapNotReady); return 1; }
        o.WriteLine(Said(r));
        Cascade(o, r.Write?.Cascade);
        return 0;
    }

    // What a resolve did, in words; nothing of a memory's own.
    static string Said(MemoryResolved r)
    {
        if (!r.Waiting)
            return r.Queue switch
            {
                MemoryResolve.Held => $"It was not held, so nothing changed: {r.Id}.",
                MemoryResolve.Duplicates => $"It is not flagged as a possible copy now, so nothing changed: {r.Id}.",
                MemoryResolve.Stale => $"It is not on the list of what may be out of date now, so nothing changed: {r.Id}.",
                MemoryResolve.Contradictions => $"This pair was settled already ({Settling(r.Settled!.Decision)}), so nothing changed.",
                MemoryResolve.Candidates => $"It was {r.Candidate!.Status} already, and a decision on a pair stays as it was made. Neither memory changed.",
                _ => r.Review is { Review: InsightStore.Proposed }
                    ? "It no longer waits on a person, since a person marked it not useful or handled; new feedback brings it back. Nothing changed."
                    : $"It was {r.Review!.Review} already, so nothing changed. Take a review back with rhythm insights <insight> reopen.",
            };
        return r.Action switch
        {
            MemoryResolve.Release => $"Released: {r.Id} is no longer held (revision {r.Write!.Revision}). Search, the map and capsules read it from now on.",
            MemoryResolve.KeepCopy => $"Kept both: {r.Id} is no longer flagged as a copy of what this review showed (revision {r.Write!.Revision}), and neither memory changed."
                + (r.StillCounted ? " Another flag of it stands, so it stays on the list." : ""),
            MemoryResolve.SettleKeepBoth => $"Settled: both stand, {r.Id} and {r.Settled!.Conflict}. An AI client neither ends nor replaces one of them while the other stands.",
            MemoryResolve.SettleReplace => $"Settled: {r.Id} stands and replaces {r.Settled!.Conflict}, whose words are kept as they were. Searches, capsules and AI clients get the one that stands.",
            MemoryResolve.StillTrue => $"Still true: {r.Id} (revision {r.Write!.Revision}) is checked as of now; nothing in it changed.",
            MemoryResolve.UseCandidate => "Marked used: the pass never offers this pair again, and neither memory changed.",
            MemoryResolve.RejectCandidate => "Rejected: the pass never offers this pair again, and neither memory changed.",
            MemoryResolve.ConfirmInsight => "Confirmed: a person said it holds. It stays an insight; no memory changes.",
            _ => "Dismissed: it leaves what waits on a person.",
        };
    }

    static string Settling(string decision) => decision == MemoryContradictions.KeepBoth ? "both stand" : "one replaced the other";
}
