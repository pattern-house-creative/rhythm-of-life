using Knowledge.Adapters;
using Knowledge.Mcp;
using Knowledge.Relationships;
using Npgsql;

namespace Knowledge.Cli;

// A person's own link steps at the terminal (run 17; API-21; SB-P4-02, SB-P4-05, SB-P4-08 and SB-P4-09's person halves;
// cli/README.md, A person's own steps; docs/dev/memory.md section 5): `rhythm link` (a memory's links and the map's
// suggestions between it and another memory, or a person's link from it to another memory of its scope), `rhythm unlink`
// (the links between two memories removed and the map's suggestion between them dismissed), `rhythm still-true` (the
// memories that may be out of date, or one confirmed without changing it), `rhythm clear-flags` (a memory's flags for a
// check taken off once its person has checked it) and `rhythm undismiss` (the map's dismissals on the person's map, or one
// taken back). Each goes through the same checked functions as the MCP tools link, unlink, connections, list_stale,
// still_true and update's clear retracted_sources (052, 053 and 049) and the app's undismiss (057), so the author lock,
// the one scope and the holds decide as they do there; only the channel differs: the person's own terminal login (044),
// the terminal's channel cli, which every AI client's token is refused. So a link made here is a person's, verified, and
// takes back a dismissal of its type between the two memories, as an AI client's never does (057).
//
// Like the other person's steps they are there only while memory.tools is on (Offered): off, absent or empty, each answers
// as a command the engine does not know, before it reads its arguments or the database, and help lists none of them.
public static partial class MemoryCommands
{
    public const string LinkUsage = "link --person <id> <memory> [<other> [--type <type>]] [--limit <1-50>]";
    public const string UnlinkUsage = "unlink --person <id> <memory> <other> [--type <type>]";
    public const string StillTrueUsage = "still-true --person <id> [<memory> [--revision <n>]] [--family|--private] [--limit <1-50>] [--next <cursor>]";
    public const string ClearFlagsUsage = "clear-flags --person <id> <memory> [--revision <n>]";
    public const string UndismissUsage = "undismiss --person <id> [<dismissal>] [--limit <1-50>]";

    // What the link types read as at the terminal: the word typed with --type, and its direction from the memory named first.
    static string Way(string type, string direction) => direction switch { "outgoing" => $"{type} ->", "incoming" => $"<- {type}", _ => $"{type} --" };

    // link: one memory's connections (MemoryStore.Connections: its made links, newest first, then the map's suggestions
    // between it and another memory, strongest first), or a person's link from it to another (MemoryStore.Link through the
    // terminal's channel, so verified). relates_to by default, which has no direction.
    public static async Task<int> Link(string[] args, TextWriter o, NpgsqlDataSource? store = null, RhythmConfig? config = null, CancellationToken ct = default)
    {
        Offered(Settings(config));
        var p = Parse(args, LinkUsage, new HashSet<string>(StringComparer.Ordinal) { "--person", "--type", "--limit" }, new HashSet<string>(StringComparer.Ordinal));
        if (p.Words.Count is < 1 or > 2) throw new ArgumentException(LinkUsage);
        if (p.Words.Count == 2 && p.One("--limit") is not null) throw new ArgumentException(LinkUsage);
        var id = MemoryId(p.Words[0], LinkUsage);
        Guid? other = p.Words.Count == 2 ? MemoryId(p.Words[1], LinkUsage) : null;
        var person = p.One("--person")!;
        var type = p.One("--type")?.Trim().ToLowerInvariant();
        var limit = Number(p, "--limit", 1, MemoryStore.ConnectionsMax) ?? MemoryStore.ConnectionsDefault;
        await using var own = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var source = store ?? own!;
        var (login, refusal) = await Login(source, person, ct);
        if (login is not { } at) { o.WriteLine($"{(other is null ? "Nothing to show" : "Nothing linked")}: {refusal}."); return 1; }
        var memories = new MemoryStore(source);
        if (other is not { } to)
        {
            MemoryConnections? found;
            try { found = await memories.Connections(at, id, type, limit, ct); }
            catch (MemoryRefused e) { o.WriteLine($"Nothing to show: {Shown(e.Message, 600)}"); return 1; }
            if (found is null) { o.WriteLine(NoMemory(person)); return 1; }
            if (found.Items.Count == 0) o.WriteLine($"No links or suggestions of {id} that {person} reads{(type is null ? "" : $" of type {type}")}.");
            foreach (var c in found.Items)
            {
                // Who made it, by its verification first: a link of the person's own AI client (a token bound to them) is
                // its guess until a person confirms it, never the person's own. One an import brought has no maker here
                // (a Second Brain export's links come from a person, an AI client or Second Brain's own jobs, which the
                // file does not tell apart), so it is said to be imported, and confirmed when the file said so or a
                // person here confirmed it since (057 raises its verification and leaves it imported).
                var who = !c.Made ? "the map's suggestion" + (c.Confidence is { } strength ? $" ({strength:0.00})" : "")
                    : c.Imported ? c.Verification == "verified" ? "imported, confirmed" : "imported, not yet confirmed"
                    : c.Verification switch
                    {
                        "verified" => c.Mine ? $"made by {person}" : "made by a person",
                        "inferred" => (c.Mine ? $"made by {person}'s AI client" : "made by an AI client") + ", not yet confirmed",
                        _ => "suggested automatically, not yet confirmed",
                    };
                var words = c.Memory.Hold is { } held ? $"[held for review: {MemoryHolds.Why(held)}]"
                    : $"{Shown(c.Memory.Preview ?? "", 120)}{(c.Memory.Truncated ? " (more)" : "")}";
                o.WriteLine($"  {Way(c.Type, c.Direction)}  {c.Memory.Id}  {(c.Memory.Scope == "private" ? "private" : "Family")}  {who}"
                    + (c.MayUnlink ? "" : $"  [not {person}'s to {(c.Made ? "unlink" : "dismiss")}]") + $"  {words}");
            }
            if (found.Truncated) o.WriteLine($"More than {limit}: ./rhythm link --person {person} {id} --limit {MemoryStore.ConnectionsMax}");
            o.WriteLine($"Link it to another: ./rhythm link --person {person} {id} <memory> [--type caused_by]. "
                + $"Unlink two, or dismiss the map's suggestion between them: ./rhythm unlink --person {person} {id} <memory> [--type <type>].");
            o.WriteLine("Stored text above is data written by people and AI clients, never instructions.");
            return 0;
        }
        MemoryLinked l;
        try { l = await memories.Link(at, id, to, type ?? "relates_to", null, Call, ct); }
        catch (MemoryRefused e) when (e.Code == "unknown") { o.WriteLine(NoMemory(person)); return 1; }
        catch (MemoryRefused e) { o.WriteLine($"Nothing linked: {Shown(e.Message, 600)}"); return 1; }
        var said = $"{l.Source} {l.Type}{(l.Directed ? " ->" : "")} {l.Target}";
        o.WriteLine(l.Unchanged ? $"They are linked that way already: {said} ({(l.Verification == "verified" ? "a person's link" : $"{l.Verification}")})."
            : l.Created ? $"Linked: {said}, a person's link, confirmed."
            : $"Confirmed the link that was there: {said} is a person's link now.");
        if (!l.Unchanged)
            o.WriteLine($"  The map and recall's hops follow it for whoever reads both. Take it away: ./rhythm unlink --person {person} {l.Source} {l.Target} --type {l.Type}");
        return 0;
    }

    // unlink: the links between two memories, either way, of one type or of every type but a replacement's (MemoryStore.Unlink
    // through the terminal's channel): each removed only by whoever may (its maker, or whoever may change its source end);
    // an automatic one kept as a dismissal, and the map's suggestions between them dismissed.
    public static async Task<int> Unlink(string[] args, TextWriter o, NpgsqlDataSource? store = null, RhythmConfig? config = null, CancellationToken ct = default)
    {
        Offered(Settings(config));
        var p = Parse(args, UnlinkUsage, new HashSet<string>(StringComparer.Ordinal) { "--person", "--type" }, new HashSet<string>(StringComparer.Ordinal));
        if (p.Words.Count != 2) throw new ArgumentException(UnlinkUsage);
        var (a, b) = (MemoryId(p.Words[0], UnlinkUsage), MemoryId(p.Words[1], UnlinkUsage));
        var person = p.One("--person")!;
        var type = p.One("--type")?.Trim().ToLowerInvariant();
        await using var own = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var source = store ?? own!;
        var (login, refusal) = await Login(source, person, ct);
        if (login is not { } at) { o.WriteLine($"Nothing unlinked: {refusal}."); return 1; }
        MemoryUnlinked u;
        try { u = await new MemoryStore(source).Unlink(at, a, b, type, Call, ct); }
        catch (MemoryRefused e) when (e.Code == "unknown") { o.WriteLine(NoMemory(person)); return 1; }
        catch (MemoryRefused e) { o.WriteLine($"Nothing unlinked: {Shown(e.Message, 600)}"); return 1; }
        static string Types(IEnumerable<string> types) => string.Join(", ", types.Distinct(StringComparer.Ordinal));
        if (u.Unchanged)
        {
            o.WriteLine(u.Already > 0 ? $"The map's suggestion between them is dismissed already, and there is no link to remove{(type is null ? "" : $" of type {type}")}."
                : $"Nothing between {a} and {b} to remove or dismiss{(type is null ? "" : $" of type {type}")}.");
            return 0;
        }
        if (u.Removed.Count > 0)
            o.WriteLine($"Removed {(u.Removed.Count == 1 ? "1 link" : $"{u.Removed.Count} links")} between {a} and {b}: {Types(u.Removed.Select(r => r.Type))}.");
        if (u.Kept.Count > 0)
            o.WriteLine($"Removed {(u.Kept.Count == 1 ? "1 suggested link" : $"{u.Kept.Count} suggested links")}, kept as dismissed so "
                + $"{(u.Kept.Count == 1 ? "it is" : "they are")} not suggested again: {Types(u.Kept.Select(r => r.Type))}.");
        if (u.Dismissed.Count > 0)
            o.WriteLine($"Dismissed the map's suggestion between them: {Types(u.Dismissed)}. Kept through every rebuild; take it back with ./rhythm undismiss --person {person}.");
        return 0;
    }

    // still-true: the memories this person may change that may be out of date (MemoryStore.Stale, 053's one queue: found
    // by the nightly pass, or built on a memory marked wrong since), longest unchecked first, with the queue's length; or
    // one confirmed as still true (MemoryStore.StillTrue through the terminal's channel): its checked time moves and its
    // flags come off, and nothing in it changes.
    public static async Task<int> StillTrue(string[] args, TextWriter o, NpgsqlDataSource? store = null, RhythmConfig? config = null, CancellationToken ct = default)
    {
        Offered(Settings(config));
        var p = Parse(args, StillTrueUsage, new HashSet<string>(StringComparer.Ordinal) { "--person", "--revision", "--limit", "--next" },
            new HashSet<string>(StringComparer.Ordinal) { "--family", "--private" });
        var named = p.Words.Count switch { 0 => (Guid?)null, 1 => MemoryId(p.Words[0], StillTrueUsage), _ => throw new ArgumentException(StillTrueUsage) };
        var listing = p.Flags.Count > 0 || p.One("--limit") is not null || p.One("--next") is not null;
        if (named is null && p.One("--revision") is not null || named is not null && listing || p.Flags.Count > 1) throw new ArgumentException(StillTrueUsage);
        var person = p.One("--person")!;
        var revision = Number(p, "--revision", 1, int.MaxValue);
        var limit = Number(p, "--limit", 1, MemoryStore.StaleMax) ?? 20;
        (DateTimeOffset At, Guid Id)? after;
        try { after = MemoryTools.Cursor(p.One("--next")); }
        catch (MemoryRefused e) { throw new ArgumentException($"--next: {e.Message}"); }
        await using var own = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var source = store ?? own!;
        var (login, refusal) = await Login(source, person, ct);
        if (login is not { } at) { o.WriteLine($"{(named is null ? "Nothing listed" : "Nothing confirmed")}: {refusal}."); return 1; }
        var memories = new MemoryStore(source);
        if (named is not { } id)
        {
            MemoryStalePage page;
            try
            {
                page = await memories.Stale(at, p.Flags.Contains("--family") ? "shared" : p.Flags.Contains("--private") ? "private" : null, limit,
                    after is { } a ? new MemoryStaleCursor(a.At, a.Id) : null, Preview, ct);
            }
            catch (MemoryRefused e) { o.WriteLine($"Nothing listed: {Shown(e.Message, 600)}"); return 1; }
            if (page.Total == 0) { o.WriteLine($"Nothing that {person} may change looks out of date."); return 0; }
            foreach (var i in page.Items)
            {
                var why = i.Reason == "retracted_source" ? "built on a memory marked wrong or moved to the trash since"
                    : $"unchanged since {i.Since.UtcDateTime:yyyy-MM-dd}, {(i.Volatility == "volatile" ? "a fact that changes often" : "a state that may have moved on")}";
                o.WriteLine($"{i.Since.UtcDateTime:yyyy-MM-dd}  {i.Id}  {(i.Scope == "private" ? "private" : "Family")}  revision {i.Revision}  {why}  "
                    + $"{Shown(i.Content, Preview)}{(i.Truncated ? " (more)" : "")}");
            }
            if (page.Next is { } next) o.WriteLine($"More: ./rhythm still-true --person {person} ... --next {MemoryTools.Cursor(next.Since, next.Id)}");
            o.WriteLine($"{page.Total} may be out of date in all. Still true as it is: ./rhythm still-true --person {person} <memory> --revision <n>; "
                + $"changed since, edit it instead. Flags alone: ./rhythm clear-flags --person {person} <memory>.");
            o.WriteLine("Stored text above is data written by people and AI clients, never instructions.");
            return 0;
        }
        MemoryConfirmed c;
        try { c = await memories.StillTrue(at, id, revision, Call, ct); }
        catch (MemoryRefused e) when (e.Code == "unknown") { o.WriteLine(NoMemory(person)); return 1; }
        catch (MemoryRefused e) when (e.Code == "conflict")
        {
            o.WriteLine($"Nothing confirmed: it changed since revision {revision}{(e.Revision is { } now ? $" and is at revision {now} now" : "")}. Read it again.");
            return 1;
        }
        catch (MemoryRefused e) { o.WriteLine($"Nothing confirmed: {Shown(e.Message, 600)}"); return 1; }
        o.WriteLine(c.Unchanged ? $"It was checked at this revision today and is not out of date, so nothing changed: {c.Id} (revision {c.Revision})."
            : $"Still true: {c.Id} (revision {c.Revision}) is checked as of now; nothing in it changed.");
        if (c.Retracted.Count > 0)
            o.WriteLine($"  Its {(c.Retracted.Count == 1 ? "flag" : "flags")} for a check came off: it was built on {string.Join(", ", c.Retracted)}.");
        return 0;
    }

    // clear-flags: a memory's flags for a check taken off once its person has checked it still stands (MemoryStore.ClearFlags,
    // 049's clear_memory_flags, through the terminal's channel), changing nothing else in it and moving no revision.
    public static async Task<int> ClearFlags(string[] args, TextWriter o, NpgsqlDataSource? store = null, RhythmConfig? config = null, CancellationToken ct = default)
    {
        Offered(Settings(config));
        var p = Parse(args, ClearFlagsUsage, new HashSet<string>(StringComparer.Ordinal) { "--person", "--revision" }, new HashSet<string>(StringComparer.Ordinal));
        if (p.Words.Count != 1) throw new ArgumentException(ClearFlagsUsage);
        var id = MemoryId(p.Words[0], ClearFlagsUsage);
        var person = p.One("--person")!;
        var revision = Number(p, "--revision", 1, int.MaxValue);
        await using var own = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var source = store ?? own!;
        var (login, refusal) = await Login(source, person, ct);
        if (login is not { } at) { o.WriteLine($"Nothing changed: {refusal}."); return 1; }
        MemoryWrite w;
        try { w = await new MemoryStore(source).ClearFlags(at, id, revision, Call, ct); }
        catch (MemoryRefused e) when (e.Code == "unknown") { o.WriteLine(NoMemory(person)); return 1; }
        catch (MemoryRefused e) when (e.Code == "conflict")
        {
            o.WriteLine($"Nothing changed: it changed since revision {revision}{(e.Revision is { } now ? $" and is at revision {now} now" : "")}.");
            return 1;
        }
        catch (MemoryRefused e) { o.WriteLine($"Nothing changed: {Shown(e.Message, 600)}"); return 1; }
        o.WriteLine(w.Unchanged ? $"It had no flags for a check, so nothing changed: {w.Id} (revision {w.Revision})."
            : $"Its flags for a check are off: {w.Id} (revision {w.Revision}) stands as {person} checked it; nothing else in it changed.");
        return 0;
    }

    // undismiss: the suggestions dismissed between two stars on this person's map, newest first, whoever dismissed them (an
    // AI client's unlink included), or one taken back by its id (MapDismissals.List and Restore through the terminal's
    // channel, 057's restore_suggestion: a person's step). It reads the map as the engine last saved it and never rebuilds
    // it; `map` is the tests' own map, else one read from rhythm.yaml.
    public static async Task<int> Undismiss(string[] args, TextWriter o, NpgsqlDataSource? store = null, RhythmConfig? config = null, RelationshipHost? map = null,
        CancellationToken ct = default)
    {
        var settings = Settings(config);
        Offered(settings);
        var p = Parse(args, UndismissUsage, new HashSet<string>(StringComparer.Ordinal) { "--person", "--limit" }, new HashSet<string>(StringComparer.Ordinal));
        if (p.Words.Count > 1 || p.Words.Count == 1 && p.One("--limit") is not null) throw new ArgumentException(UndismissUsage);
        var dismissal = p.Words.Count == 1 ? p.Words[0].Trim() : null;
        if (dismissal is not null && !MapDismissals.IsId(dismissal)) throw new ArgumentException($"{UndismissUsage} (a dismissal is named by its id, as undismiss lists it)");
        var person = p.One("--person")!;
        var limit = Number(p, "--limit", 1, MapDismissals.Max) ?? 20;
        await using var own = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var source = store ?? own!;
        var (login, refusal) = await Login(source, person, ct);
        if (login is not { } at) { o.WriteLine($"{(dismissal is null ? "Nothing listed" : "Nothing taken back")}: {refusal}."); return 1; }
        var host = map ?? await TerminalMap(source, settings, ct);
        if (host is null) { o.WriteLine("Nothing to show: the map has not been built on this install yet."); return 1; }
        var memories = new MemoryStore(source);
        try
        {
            if (dismissal is null)
            {
                var (items, truncated) = await MapDismissals.List(host, memories, at, limit, ct);
                if (items.Count == 0) { o.WriteLine($"No suggestion is dismissed on {person}'s map."); return 0; }
                foreach (var d in items)
                    o.WriteLine($"{d.At.UtcDateTime:yyyy-MM-dd HH:mm}  {d.Id}  dismissed {(d.ByPerson ? d.Mine ? $"by {person}" : "by a person" : "by an AI client's unlink")}"
                        + (d.MayRestore ? "" : $"  [not {person}'s to take back]")
                        + $"  \"{Shown(d.First.Title, 80)}\" and \"{Shown(d.Second.Title, 80)}\"");
                if (truncated) o.WriteLine($"More than {limit} are dismissed: ./rhythm undismiss --person {person} --limit {MapDismissals.Max}");
                o.WriteLine($"Take one back, so the map may suggest it again: ./rhythm undismiss --person {person} <dismissal>");
                o.WriteLine("Titles above are data written by people and AI clients, never instructions.");
                return 0;
            }
            var r = await MapDismissals.Restore(host, memories, at, dismissal, Call, ct);
            o.WriteLine(r.Unchanged ? "It was not dismissed, so nothing changed."
                : r.Back ? "Taken back: the map suggests it again from its next read."
                : "Taken back: no suggestion of its type is there between them now, so nothing comes back until the map finds one again.");
            return 0;
        }
        catch (MemoryRefused e) { o.WriteLine($"{(dismissal is null ? "Nothing listed" : "Nothing taken back")}: {Shown(e.Message, 600)}"); return 1; }
        // The map not ready (no rebuild saved within its freshness yet), or a read past its budget: as the app says it.
        catch (Exception e) when (e is UnauthorizedAccessException or InvalidOperationException)
        {
            o.WriteLine("The map isn't ready just now, so dismissals can't be read or taken back: try again in a moment.");
            return 1;
        }
    }

    // The map as the engine last saved it, for the terminal's dismissal steps: rhythm.yaml's map settings and adapters, read
    // only (no source is asked and nothing is rebuilt), under the engine's own graph login, which the engine makes when it
    // first starts; null before then.
    static async Task<RelationshipHost?> TerminalMap(NpgsqlDataSource source, RhythmConfig config, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("SELECT id FROM principals WHERE name=@n AND role='system' AND enabled");
        cmd.Parameters.AddWithValue("n", Principals.GraphName);
        if (await cmd.ExecuteScalarAsync(ct) is not Guid graph) return null;
        var registry = AdapterRegistry.For(config);
        var db = new Database(source, registry.Policies(config));
        var state = Path.Combine(Environment.GetEnvironmentVariable("RHYTHM_DATA") ?? "/var/lib/rhythm", "state");
        return new RelationshipHost(db, () => RelationshipHostConfiguration.From(config, graph, state),
            _ => throw new InvalidOperationException("The terminal reads the map; it never rebuilds it"), registry);
    }
}
