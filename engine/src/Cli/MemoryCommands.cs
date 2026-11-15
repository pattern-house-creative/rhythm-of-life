using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Adapters;
using Knowledge.Mcp;
using Npgsql;

namespace Knowledge.Cli;

// `rhythm remember`, `rhythm recall` and `rhythm list` (SB-P3-19, LAN-16; cli/README.md, Memories from the terminal;
// docs/dev/memory.md sections 3 and 5): capture and recall from a terminal as one person, through the same engine
// functions as the MCP memory tools and the recall tool (MemoryStore.Create and List, Recall), so the one read predicate,
// the author lock, where a write lands and the holds' screening decide as they do for that person's AI client. And,
// with memory.tools on, a person's own steps no AI client takes (SB-P3-22): `rhythm release` (the review queue, a held
// memory read whole, its release at the revision read), `rhythm capsule` (a capsule slot) and `rhythm delete-forever`
// (from the trash, once the person types the memory's id).
//
// Each speaks through the person's terminal login (TerminalLogin): a principal of role harness bound to that person and
// marked terminal (migration 044), which the operator's command makes here on first use, with no token anyone holds and
// refused at the door. Only such a login writes through the terminal's channel, `cli`, and every AI client's token is
// refused that channel (044's memory_channel), so what only a person may do (D-149) is told by the login, never by what a
// caller says. A write records the channel cli and the client `rhythm`, and its source is cli.
//
// Like `family` and `import`, remember and list do not read rhythm.yaml. Recall does, for what combined retrieval reads
// (the adapters' provider policies, the passages the settings list, the model): it loads the local model only from the
// folder its environment variable names (RHYTHM_MODEL_DIR for MiniLM), never downloading, and without it answers keyword
// results with recall's notice that semantic search was skipped. It asks no live source whether a record is current, so
// a live source's records (Drive's) are left out here, and says so. Nothing is logged (engine.recallLog keeps AI
// clients' recalls only). What the commands print of stored text is cleaned of control and formatting characters, so a
// memory's words never drive the terminal.
public static partial class MemoryCommands
{
    public const string RememberUsage = "remember --person <id> [--family] [--tag <tag>]... [--project <slug>] [--kind episodic|semantic] "
        + "[--importance 1-5] [--volatility durable|state|volatile] [--key <idempotency key>] <text...> | -";
    public const string RecallUsage = "recall --person <id> [--top <1-20>] [--json] <question...>";
    public const string ListUsage = "list --person <id> [--family|--private] [--tag <tag>] [--project <slug>] [--kind episodic|semantic] "
        + "[--status canonical|draft|deprecated] [--limit <1-50>] [--next <cursor>]";

    // The terminal's channel and the client name its writes record (migration 044's memory_channel takes cli from a
    // terminal login only).
    public const string Channel = "cli", Client = "rhythm";
    static readonly MemoryCall Call = new(Channel, Client);

    // The command line: options with a value, flags, and the words left, in order. A repeated option keeps every value
    // (--tag); a value never starts with "--".
    sealed class Parsed
    {
        public readonly Dictionary<string, List<string>> Options = new(StringComparer.Ordinal);
        public readonly HashSet<string> Flags = new(StringComparer.Ordinal);
        public readonly List<string> Words = [];
        public string? One(string name) => Options.TryGetValue(name, out var v) ? v[^1] : null;
        public IReadOnlyList<string> All(string name) => Options.TryGetValue(name, out var v) ? v : [];
    }

    static Parsed Parse(string[] args, string usage, IReadOnlySet<string> options, IReadOnlySet<string> flags, IReadOnlySet<string>? repeated = null)
    {
        var parsed = new Parsed();
        var rest = false;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (rest || !a.StartsWith("--", StringComparison.Ordinal) || a == "-") { parsed.Words.Add(a); continue; }
            if (a == "--") { rest = true; continue; }
            if (flags.Contains(a)) { if (!parsed.Flags.Add(a)) throw new ArgumentException(usage); continue; }
            if (!options.Contains(a) || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException(usage);
            if (parsed.Options.ContainsKey(a) && repeated?.Contains(a) != true) throw new ArgumentException(usage);
            (parsed.Options.TryGetValue(a, out var list) ? list : parsed.Options[a] = []).Add(args[++i]);
        }
        if (parsed.One("--person") is not { Length: > 0 }) throw new ArgumentException(usage + " (name --person: the terminal speaks for one person)");
        return parsed;
    }

    static int? Number(Parsed p, string name, int min, int max)
    {
        if (p.One(name) is not { } text) return null;
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max ? n
            : throw new ArgumentException($"{name} is a whole number from {min} to {max}");
    }

    static string? Word(Parsed p, string name, params string[] allowed)
    {
        if (p.One(name) is not { } text) return null;
        var word = text.Trim().ToLowerInvariant();
        return allowed.Contains(word, StringComparer.Ordinal) ? word : throw new ArgumentException($"{name} is {string.Join(", ", allowed)}");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // remember

    public static async Task<int> Remember(string[] args, TextReader input, TextWriter o, NpgsqlDataSource? store = null, CancellationToken ct = default)
    {
        var p = Parse(args, RememberUsage,
            new HashSet<string>(StringComparer.Ordinal) { "--person", "--tag", "--project", "--kind", "--importance", "--volatility", "--key" },
            new HashSet<string>(StringComparer.Ordinal) { "--family" }, new HashSet<string>(StringComparer.Ordinal) { "--tag" });
        // The text: the words after the options, or with `-` alone what is piped in.
        var text = p.Words is ["-"] ? await input.ReadToEndAsync(ct) : string.Join(' ', p.Words);
        if (p.Words.Count == 0 || string.IsNullOrWhiteSpace(text)) throw new ArgumentException(RememberUsage + " (the text to remember)");
        var person = p.One("--person")!;
        var draft = new MemoryDraft(text, p.All("--tag"), p.Flags.Contains("--family") ? "shared" : null, Word(p, "--kind", "episodic", "semantic"),
            Number(p, "--importance", 1, 5), Word(p, "--volatility", "durable", "state", "volatile"), p.One("--project"));
        await using var own = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var source = store ?? own!;
        if (await TerminalLogin.Refusal(source, person, ct) is { } refused) { o.WriteLine($"Nothing remembered: {refused}."); return 1; }
        MemoryWrite w;
        try
        {
            var login = await TerminalLogin.For(source, person, ct);
            w = await new MemoryStore(source).Create(login, draft, Call with { Key = p.One("--key") }, ct);
        }
        catch (MemoryRefused e) { o.WriteLine($"Nothing remembered: {Shown(e.Message, 600)}"); return 1; }
        var where = w.Scopes == "off" ? "a household memory, which everyone in the household reads (memory scopes are off)"
            : w.Scope == "private" ? $"in {person}'s private scope" : "in Family, which every member reads";
        o.WriteLine(w.Replayed ? $"Answered already: an earlier remember with this key stored it, {where}: {w.Id} (revision {w.Revision})."
            : $"Remembered as a draft, {where}: {w.Id} (revision {w.Revision}).");
        if (!w.Replayed && w.Project is { Created: true } made) o.WriteLine($"  Made the project {Shown(made.Slug, 80)}.");
        if (w.DroppedTags.Count > 0) o.WriteLine($"  Left off tags the engine sets itself: {Shown(string.Join(", ", w.DroppedTags), 400)}.");
        if (w.Hold is { } hold) o.WriteLine("  " + MemoryHolds.TerminalNotice(hold));
        return 0;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // list

    public static async Task<int> List(string[] args, TextWriter o, NpgsqlDataSource? store = null, CancellationToken ct = default)
    {
        var p = Parse(args, ListUsage,
            new HashSet<string>(StringComparer.Ordinal) { "--person", "--tag", "--project", "--kind", "--status", "--limit", "--next" },
            new HashSet<string>(StringComparer.Ordinal) { "--family", "--private" });
        if (p.Words.Count > 0 || p.Flags.Contains("--family") && p.Flags.Contains("--private")) throw new ArgumentException(ListUsage);
        var person = p.One("--person")!;
        (DateTimeOffset At, Guid Id)? at;
        try { at = MemoryTools.Cursor(p.One("--next")); }
        catch (MemoryRefused e) { throw new ArgumentException($"--next: {e.Message}"); }
        var filter = new MemoryFilter(p.Flags.Contains("--family") ? "shared" : p.Flags.Contains("--private") ? "private" : null,
            p.One("--tag") is { } tag ? [tag] : null, Word(p, "--kind", "episodic", "semantic"), Word(p, "--status", "canonical", "draft", "deprecated"),
            p.One("--project"), null, null, null, null, Number(p, "--limit", 1, 50) ?? 10, at is { } a ? new MemoryCursor(a.At, a.Id) : null, Preview);
        await using var own = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var source = store ?? own!;
        if (await TerminalLogin.Refusal(source, person, ct) is { } refused) { o.WriteLine($"Nothing listed: {refused}."); return 1; }
        MemoryPage page;
        try { page = await new MemoryStore(source).List(await TerminalLogin.For(source, person, ct), filter, ct); }
        catch (MemoryRefused e) { o.WriteLine($"Nothing listed: {Shown(e.Message, 600)}"); return 1; }
        if (page.Items.Count == 0) { o.WriteLine($"No memories that {person} reads match."); return 0; }
        foreach (var m in page.Items)
        {
            var scope = m.Scope == "private" ? "private" : "Family";
            var head = $"{m.CreatedAt.UtcDateTime:yyyy-MM-dd HH:mm}  {m.Id}  {scope}  {m.Status}";
            o.WriteLine(m.Hold is { } reason
                ? $"{head}  [held for review: {MemoryHolds.Why(reason)}; its words wait for a person]"
                : $"{head}  {Shown(m.Content ?? "", Preview)}{(m.Truncated ? " (more)" : "")}{(m.Tags.Count > 0 ? "  #" + Shown(string.Join(" #", m.Tags), 200) : "")}");
        }
        if (page.Next is { } next)
            o.WriteLine($"More: ./rhythm list --person {person} ... --next {MemoryTools.Cursor(next.CreatedAt, next.Id)}");
        return 0;
    }

    // How much of each memory a list shows, on one line.
    const int Preview = 160;

    // ---------------------------------------------------------------------------------------------------------------
    // recall

    // What recall needs beyond the database: the settings, and the model when the caller has one (tests).
    public sealed record RecallSetup(RhythmConfig Config, IEmbedder? Model = null, string Status = "off");

    public static async Task<int> Recall(string[] args, TextWriter o, NpgsqlDataSource? store = null, RecallSetup? setup = null, CancellationToken ct = default)
    {
        var p = Parse(args, RecallUsage, new HashSet<string>(StringComparer.Ordinal) { "--person", "--top" },
            new HashSet<string>(StringComparer.Ordinal) { "--json" });
        var question = string.Join(' ', p.Words);
        if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException(RecallUsage + " (the question)");
        var person = p.One("--person")!;
        var top = Number(p, "--top", 1, RecallOptions.Default.MaxTopK) ?? 5;
        var config = setup?.Config ?? RhythmConfig.Load(Environment.GetEnvironmentVariable("RHYTHM_CONFIG") ?? "/etc/rhythm/rhythm.yaml");
        if (!RhythmConfig.Loaded) RhythmConfig.Current = config;
        await using var own = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var source = store ?? own!;
        if (await TerminalLogin.Refusal(source, person, ct) is { } refused) { o.WriteLine($"Nothing recalled: {refused}."); return 1; }
        var registry = AdapterRegistry.For(config);
        var policies = registry.Policies(config);
        var db = new Database(source, policies);
        var settings = EmbeddingSettings.From(config);
        using var loaded = setup is null ? new Embeddings(settings.Model) : null;
        var (model, status) = setup is { } given ? (given.Model, given.Status) : Load(loaded!, settings);
        var serving = PassageSources.For(settings, registry.Enabled, true);
        // No live source is asked whether its record is current (no validation), so a live source's records are left out.
        var recall = new Knowledge.Recall(db, model, serving, null, RecallOptions.Default, status);
        var asked = new RecallQuery(question, top);
        RecallResult found;
        try { found = await recall.Search(await TerminalLogin.For(source, person, ct), asked, ct); }
        catch (ArgumentException e) { o.WriteLine($"Nothing recalled: {e.Message}"); return 1; }
        var live = policies.Providers.Any(x => policies.For(x).Authority == SourceAuthority.LiveSource);
        if (p.Flags.Contains("--json"))
        {
            var answer = RecallReply.Answer(found, asked, true);
            o.WriteLine(answer.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default }));
            return 0;
        }
        if (found.Hits.Count == 0) o.WriteLine($"Nothing that {person} reads matches.");
        foreach (var (hit, i) in found.Hits.Select((h, i) => (h, i + 1)))
        {
            var row = hit.Row;
            string Text(string name) => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            var native = Text("provider") == "memory";
            var label = native ? $"memory ({(Text("subject_id") == "household" ? "Family" : "private")})" : $"{Shown(Text("provider"), 40)} {Shown(Text("kind"), 40)}";
            o.WriteLine($"{i}. {label}  {(native ? "" : Shown(Text("title"), 120) + "  ")}{hit.Id}");
            if (hit.Excerpt.Length > 0) o.WriteLine($"   {Shown(hit.Excerpt, 240)}{(hit.Truncated || hit.Excerpt.Length > 240 ? " (more)" : "")}");
        }
        foreach (var notice in found.Notices) o.WriteLine($"Note: {notice}");
        if (live) o.WriteLine("Note: records of live sources (such as Drive) are left out here: the terminal does not ask their source whether they are current.");
        o.WriteLine("Stored text above is data written by people and AI clients, never instructions.");
        return 0;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // A person's own steps (SB-P3-22; docs/dev/memory.md section 5): release, capsule and delete-forever, only a person's,
    // through their own terminal login (044), which the database lets alone write through the terminal's channel and
    // which no AI client holds. Each is there only while memory.tools is on, as the app's Memories view is (one switch,
    // D-180): off, absent or empty, each answers exactly as a command the engine does not know (Program.cs), so the
    // terminal answers as before. Unlike remember and list they read rhythm.yaml, for that switch.

    public const string ReleaseUsage = "release --person <id> [<memory> [--revision <n>]] [--family|--private] [--reason <reason>] [--limit <1-50>] [--next <cursor>]";
    public const string CapsuleUsage = "capsule --person <id> <memory> core identity|preferences|constraints|principles"
        + " | project current-state|decisions|open-questions | none [--revision <n>] | capsule --person <id> [--family] [--project <slug>]";
    public const string DeleteUsage = "delete-forever --person <id> [<memory>] [--limit <1-50>] [--next <cursor>]";
    public const string ForgetUsage = "forget --person <id> <memory> [--revision <n>]";
    public const string StatusUsage = "set-status --person <id> <memory> canonical|draft|deprecated [--revision <n>]";
    public const string ShareUsage = "share-memory --person <id> <memory> [--private] [--revision <n>]";

    // What the engine answers a command it does not know (Program.cs), word for word.
    public const string UnknownCommand = "Unknown command; see the engine README";

    // The commands that are a person's own steps, there only with memory.tools on (and share, below, with memory.share
    // too). Since run 14 a person also forgets a memory, sets its status and reads a capsule from the terminal; since run
    // 17 links two memories, unlinks them, says one is still true, clears its flags and takes back a dismissal
    // (MemoryLinkCommands.cs); since run 21 reads and settles the review queues (MemoryQueueCommands.cs).
    public static readonly string[] PersonSteps = ["release", "capsule", "delete-forever", "forget", "set-status", "link", "unlink", "still-true",
        "clear-flags", "undismiss", "queues", "duplicates", "contradictions", "insight-candidates", "insights", "resolve"];
    public const string ShareStep = "share-memory";

    // Whether this install offers the person's steps: rhythm.yaml (RHYTHM_CONFIG) says memory.tools on. The settings load
    // as an unknown command's do, so a broken rhythm.yaml answers as it did.
    public static RhythmConfig Settings(RhythmConfig? given = null) =>
        given ?? RhythmConfig.Load(Environment.GetEnvironmentVariable("RHYTHM_CONFIG") ?? "/etc/rhythm/rhythm.yaml");

    static void Offered(RhythmConfig config)
    {
        if (!MemorySettings.ToolsOn(config)) throw new ArgumentException(UnknownCommand);
    }

    // The person's terminal login, or the refusal the terminal prints (null login).
    static async Task<(Guid? Login, string? Refused)> Login(NpgsqlDataSource source, string person, CancellationToken ct) =>
        await TerminalLogin.Refusal(source, person, ct) is { } refused ? (null, refused) : (await TerminalLogin.For(source, person, ct), null);

    static Guid MemoryId(string word, string usage) =>
        Guid.TryParseExact(word.Trim(), "D", out var id) ? id : throw new ArgumentException($"{usage} (a memory is named by its id, as list shows it)");

    static string Whose(string scope, string subject) => scope == "private" ? $"{subject}'s own" : "Family's";

    static string NoMemory(string person) =>
        $"No memory with that id that {person} reads: it may be in the trash, in a scope {person} does not read, or deleted for good.";

    // A held memory's words for a person to review at the terminal: every character no person sees (control, formatting
    // and private-use characters, direction controls, tag characters, a lone surrogate) shown as its code ([U+200B]), so
    // what it holds is visible and nothing in it drives the terminal; each line of it on a line of its own, indented.
    internal static string Visible(string text, string indent = "  ")
    {
        var b = new StringBuilder(indent);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n') { b.Append('\n').Append(indent); continue; }
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                var code = char.ConvertToUtf32(c, text[i + 1]);
                if (CharUnicodeInfo.GetUnicodeCategory(code) is UnicodeCategory.Format or UnicodeCategory.PrivateUse) b.Append($"[U+{code:X4}]");
                else b.Append(c).Append(text[i + 1]);
                i++;
                continue;
            }
            var hidden = char.IsControl(c) || char.IsSurrogate(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse;
            if (hidden) b.Append($"[U+{(int)c:X4}]"); else b.Append(c);
        }
        return b.ToString();
    }

    // release: the review queue a person reads (no memory named), one held memory whole for review (a memory named), or
    // its release at the revision the person read (--revision): MemoryStore.Review, Get and Release, through the
    // terminal's channel. A release always names the revision read, so what changed since is never let in unread, and
    // the queue names no revision: it is shown only beside the memory whole, so the command it gives follows a reading.
    public static async Task<int> Release(string[] args, TextWriter o, NpgsqlDataSource? store = null, RhythmConfig? config = null, CancellationToken ct = default)
    {
        Offered(Settings(config));
        var p = Parse(args, ReleaseUsage, new HashSet<string>(StringComparer.Ordinal) { "--person", "--revision", "--reason", "--limit", "--next" },
            new HashSet<string>(StringComparer.Ordinal) { "--family", "--private" });
        var named = p.Words.Count switch { 0 => (Guid?)null, 1 => MemoryId(p.Words[0], ReleaseUsage), _ => throw new ArgumentException(ReleaseUsage) };
        var listing = p.Flags.Count > 0 || p.One("--reason") is not null || p.One("--limit") is not null || p.One("--next") is not null;
        if (named is null && p.One("--revision") is not null || named is not null && listing || p.Flags.Count > 1) throw new ArgumentException(ReleaseUsage);
        var person = p.One("--person")!;
        var revision = Number(p, "--revision", 1, int.MaxValue);
        var limit = Number(p, "--limit", 1, MemoryStore.ReviewMax) ?? 20;
        (DateTimeOffset At, Guid Id)? after;
        try { after = MemoryTools.Cursor(p.One("--next")); }
        catch (MemoryRefused e) { throw new ArgumentException($"--next: {e.Message}"); }
        await using var own = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var source = store ?? own!;
        var (login, refusal) = await Login(source, person, ct);
        if (login is not { } at) { o.WriteLine($"Nothing released: {refusal}."); return 1; }
        // A release is checked for copies and contradiction candidates as a save is (068), with the model when its files are here.
        using var loaded = revision is null ? null : new Embeddings(EmbeddingSettings.From(Settings(config)).Model);
        var memories = new MemoryStore(source) { Check = revision is null ? null : ReviewCheck(Settings(config), loaded) };
        if (named is not { } id)
        {
            var filter = new MemoryReviewFilter(p.Flags.Contains("--family") ? "shared" : p.Flags.Contains("--private") ? "private" : null,
                p.One("--reason"), limit, after is { } a ? new MemoryReviewCursor(a.At, a.Id) : null);
            MemoryReviewPage page;
            int count;
            try { page = await memories.Review(at, filter, Call, ct); count = await memories.ReviewCount(at, filter with { After = null }, ct); }
            catch (MemoryRefused e) { o.WriteLine($"Nothing listed: {Shown(e.Message, 600)}"); return 1; }
            if (page.Items.Count == 0) { o.WriteLine($"Nothing that {person} reads is held for review."); return 0; }
            foreach (var i in page.Items)
            {
                var how = i.Channel switch { "mcp" => $"an AI client{(i.Client is { } c ? $" ({Shown(c, 80)})" : "")}", "app" => "the app", "cli" => "the terminal",
                    "capture" => $"a capture connection{(i.Client is { } n ? $" ({Shown(n, 80)})" : "")}", _ => "Rhythm" };
                o.WriteLine($"{i.HeldAt.UtcDateTime:yyyy-MM-dd HH:mm}  {i.Id}  {(i.Scope == "private" ? "private" : "Family")}  "
                    + $"held: {MemoryHolds.Why(i.Reason)}{(i.Signals.Count > 0 ? $" ({string.Join(", ", i.Signals)})" : "")}; written by {how}"
                    + (i.HeldBy is { } by ? $" for {by}" : "") + (i.MayRelease ? "" : $"  [not {person}'s to release]"));
            }
            if (page.Next is { } next) o.WriteLine($"More: ./rhythm release --person {person} ... --next {MemoryTools.Cursor(next.HeldAt, next.Id)}");
            o.WriteLine($"{count} held for review in all. Nothing here shows their words: read one with ./rhythm release --person {person} <memory>.");
            return 0;
        }
        if (revision is not { } read)
        {
            var m = await memories.Get(at, id, ct);
            if (m is null) { o.WriteLine(NoMemory(person)); return 1; }
            if (m.Hold is not { } reason) { o.WriteLine($"It is not held for review, so there is nothing to release: {m.Id} (revision {m.Revision})."); return 0; }
            o.WriteLine($"Held for review: {MemoryHolds.Why(reason)}.");
            o.WriteLine($"{m.Id}  {(m.Scope == "private" ? $"{m.Subject}'s own" : "Family")}  {m.Status}  revision {m.Revision}  written through {Shown(m.Source, 40)}"
                + (m.Author is { } author ? $" for {author}" : ""));
            if (m.Project is { } project) o.WriteLine($"Project: {Visible(project.Slug, "")}");
            if (m.Tags.Count > 0) o.WriteLine($"Tags: {Visible(string.Join(" ", m.Tags.Select(t => "#" + t)), "")}");
            o.WriteLine("Its words as stored, every character no person sees shown as its code:");
            o.WriteLine(Visible(m.Content ?? ""));
            o.WriteLine("Release it only if you trust these words: search, the map, capsules and AI clients will read them once it is released.");
            o.WriteLine($"To release it as you read it here: ./rhythm release --person {person} {m.Id} --revision {m.Revision}");
            return 0;
        }
        MemoryWrite w;
        try { w = await memories.Release(at, id, read, Call, ct); }
        catch (MemoryRefused e) when (e.Code == "unknown") { o.WriteLine(NoMemory(person)); return 1; }
        catch (MemoryRefused e) when (e.Code == "conflict")
        {
            o.WriteLine($"Not released: it changed since revision {read}{(e.Revision is { } now ? $" and is at revision {now} now" : "")}. "
                + $"Read it again: ./rhythm release --person {person} {id}");
            return 1;
        }
        catch (MemoryRefused e) { o.WriteLine($"Not released: {Shown(e.Message, 600)}"); return 1; }
        o.WriteLine(w.Unchanged ? $"It was not held, so nothing changed: {w.Id} (revision {w.Revision})."
            : $"Released: {w.Id} is no longer held (revision {w.Revision}). Search, the map and capsules read it from now on.");
        return 0;
    }

    // capsule: put a memory into one of its scope's capsule slots, or take it out (none): MemoryStore.SetCapsule through
    // the terminal's channel, which migration 039 refuses an AI client's. The memory's own scope's capsule (its person's
    // own, or Family's); a project slot is in its project's capsule. A capsule serves only a canonical memory that is not
    // held, so the answer says when it waits for either.
    public static async Task<int> Capsule(string[] args, TextWriter o, NpgsqlDataSource? store = null, RhythmConfig? config = null, CancellationToken ct = default)
    {
        var settings = Settings(config);
        Offered(settings);
        var p = Parse(args, CapsuleUsage, new HashSet<string>(StringComparer.Ordinal) { "--person", "--revision", "--project" },
            new HashSet<string>(StringComparer.Ordinal) { "--family" });
        // No memory named: the capsule itself, as its AI clients are served it (the view).
        if (p.Words.Count == 0)
        {
            if (p.One("--revision") is not null) throw new ArgumentException(CapsuleUsage);
            return await CapsuleView(p, settings, o, store, ct);
        }
        if (p.Flags.Count > 0 || p.One("--project") is not null) throw new ArgumentException(CapsuleUsage);
        var (capsule, slot) = p.Words.Count switch
        {
            2 when p.Words[1].Trim().ToLowerInvariant() == "none" => ((string?)null, (string?)null),
            3 when p.Words[1].Trim().ToLowerInvariant() is CapsuleSlots.Core or CapsuleSlots.Project => (p.Words[1].Trim().ToLowerInvariant(), p.Words[2].Trim().ToLowerInvariant()),
            _ => throw new ArgumentException(CapsuleUsage),
        };
        if (capsule is not null && !CapsuleSlots.For(capsule).Contains(slot!)) throw new ArgumentException(CapsuleUsage);
        var id = MemoryId(p.Words[0], CapsuleUsage);
        var person = p.One("--person")!;
        var revision = Number(p, "--revision", 1, int.MaxValue);
        await using var own = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var source = store ?? own!;
        var (login, refusal) = await Login(source, person, ct);
        if (login is not { } at) { o.WriteLine($"Nothing changed: {refusal}."); return 1; }
        MemoryWrite w;
        try { w = await new MemoryStore(source).SetCapsule(at, id, capsule, slot, revision, Call, ct); }
        catch (MemoryRefused e) when (e.Code == "unknown") { o.WriteLine(NoMemory(person)); return 1; }
        catch (MemoryRefused e) when (e.Code == "conflict")
        {
            o.WriteLine($"Nothing changed: it changed since revision {revision}{(e.Revision is { } now ? $" and is at revision {now} now" : "")}.");
            return 1;
        }
        catch (MemoryRefused e) { o.WriteLine($"Nothing changed: {Shown(e.Message, 600)}"); return 1; }
        var where = capsule == CapsuleSlots.Project
            ? $"the {Shown(w.Project?.Slug ?? "", 80)} project's capsule ({Whose(w.Scope, w.Subject)})" : $"{Whose(w.Scope, w.Subject)} core capsule";
        o.WriteLine(capsule is null
            ? w.Unchanged ? $"It was in no capsule slot, so nothing changed: {w.Id} (revision {w.Revision})." : $"Taken out of its capsule slot: {w.Id} (revision {w.Revision})."
            : w.Unchanged ? $"It is in {where} as {slot} already, so nothing changed: {w.Id} (revision {w.Revision})."
            : $"In {where} as {slot}: {w.Id} (revision {w.Revision}).");
        if (capsule is null) return 0;
        if (w.Hold is not null)
            o.WriteLine($"  It is held for review, so the capsule leaves it out until a person releases it (./rhythm release --person {person} {w.Id}).");
        else if (w.Status == "draft")
            o.WriteLine("  It is a draft: a capsule serves confirmed memories only, so it fills the slot once someone confirms it (canonical), "
                + "in the app's Memories view for one.");
        else if (w.Status == "deprecated") o.WriteLine("  It is marked wrong (deprecated), so the capsule leaves it out.");
        o.WriteLine("  Every AI client that reads this capsule reads it first; a slot two memories fill is left out until one of them leaves it.");
        return 0;
    }

    // The capsule view (capsule with no memory named, run 14): one capsule as an AI client that reads it is served it
    // (PromptCapsules.Build, the same read and selection as get_prompt_capsule), each slot with the memory that fills it
    // and its words as served, every character no person sees shown as its code; then what the capsule leaves out and
    // why, and the memories in its slots that wait (PromptCapsules.Waiting: a draft, one marked wrong, one held for
    // review, whose words are not shown here, or one past its validity), each with the step that lets it in. The
    // person's own core capsule unless --family names Family's, and with --project that project's capsule instead. It
    // writes nothing. With memory.capsules off AI clients are served no capsule, which the view says.
    static async Task<int> CapsuleView(Parsed p, RhythmConfig settings, TextWriter o, NpgsqlDataSource? store, CancellationToken ct)
    {
        var person = p.One("--person")!;
        var project = p.One("--project");
        var request = new CapsuleRequest(project is null ? CapsuleSlots.Core : CapsuleSlots.Project, project, p.Flags.Contains("--family") ? "shared" : null);
        await using var own = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var source = store ?? own!;
        var (login, refusal) = await Login(source, person, ct);
        if (login is not { } at) { o.WriteLine($"Nothing to show: {refusal}."); return 1; }
        var capsules = new PromptCapsules(source);
        BuiltCapsule built;
        IReadOnlyList<CapsuleWaiting> waiting;
        try { built = await capsules.Build(at, request, ct); waiting = await capsules.Waiting(at, request, ct); }
        catch (MemoryRefused e)
        {
            o.WriteLine($"Nothing to show: {Shown(e.Message, 600)}");
            return 1;
        }
        var payload = built.Payload;
        var scope = (string)payload["scope"]!;
        var whose = scope == "shared" ? "Family's" : $"{person}'s own";
        var name = project is null ? $"{whose} core capsule" : $"the {Shown((string?)payload["project_id"] ?? project, 80)} project's capsule ({whose})";
        o.WriteLine($"{char.ToUpperInvariant(name[0])}{name[1..]}, as an AI client that reads it is served it: "
            + $"{((bool)payload["complete"]! ? "complete" : "not complete")}, {(int)payload["char_count"]!} of {(int)payload["max_chars"]!} characters.");
        // The served text names each section's slot and words; the sections name the memory that fills each.
        var served = JsonNode.Parse((string)payload["text"]!)!["sections"]!.AsArray()
            .ToDictionary(s => (string)s!["slot"]!, s => (string)s!["content"]!, StringComparer.Ordinal);
        var filled = payload["sections"]!.AsArray().ToDictionary(s => (string)s!["slot"]!, s => (string)s!["source_entry_id"]!, StringComparer.Ordinal);
        var omitted = payload["omitted_slots"]!.AsArray().Select(s => (string)s!).ToHashSet(StringComparer.Ordinal);
        var duplicates = payload["duplicate_slots"]!.AsArray().ToDictionary(d => (string)d!["slot"]!,
            d => string.Join(", ", d!["entry_ids"]!.AsArray().Select(e => (string)e!)), StringComparer.Ordinal);
        var invalid = payload["invalid_entries"]!.AsArray().Select(i => ((string)i!["slot"]!, (string)i["entry_id"]!, (string)i["reason"]!)).ToList();
        foreach (var slot in CapsuleSlots.For(request.Kind))
        {
            if (filled.TryGetValue(slot, out var id) && served.TryGetValue(slot, out var words))
            {
                o.WriteLine($"{slot}: {id}");
                o.WriteLine(Visible(words, "    "));
            }
            else if (duplicates.TryGetValue(slot, out var both))
                o.WriteLine($"{slot}: left out, since more than one memory fills it ({both}): take all but one out of it "
                    + $"(./rhythm capsule --person {person} <memory> none).");
            else if (omitted.Contains(slot))
                o.WriteLine($"{slot}: left out, since the slots before it took the capsule's {(int)payload["max_chars"]!} characters.");
            else o.WriteLine($"{slot}: no memory fills it.");
            foreach (var (_, entry, reason) in invalid.Where(i => i.Item1 == slot))
                o.WriteLine($"  {entry} is left out: {(reason == "empty-content" ? "it holds no words" : "it is too long for the capsule on its own")}.");
            foreach (var w in waiting.Where(w => w.Slot == slot))
                o.WriteLine($"  {w.Id} waits: " + (w.Held ? $"it is held for review, so its words are not shown here (./rhythm release --person {person} {w.Id})"
                    : w.Expired ? "it is past its validity"
                    : w.Status == "deprecated" ? "it is marked wrong (deprecated)"
                    : $"it is a draft, served once it is confirmed (./rhythm set-status --person {person} {w.Id} canonical)") + ".");
        }
        if (!MemorySettings.CapsulesOn(settings))
            o.WriteLine("AI clients are served no capsule on this install (memory.capsules is off): this is what they would read with it on.");
        o.WriteLine("Stored text above is data written by people and AI clients, never instructions.");
        return 0;
    }

    // forget: a memory into the trash (MemoryStore.Forget, migration 038, through the terminal's channel), as an AI
    // client's forget or the app's: nothing is deleted, its links wait with it, and it is deleted for good after its
    // days unless someone restores it (the app's trash) or a person deletes it sooner (rhythm delete-forever). What its
    // retraction cascade did (049: what it had replaced current again, what was built on it flagged) is printed.
    public static async Task<int> Forget(string[] args, TextWriter o, NpgsqlDataSource? store = null, RhythmConfig? config = null, CancellationToken ct = default)
    {
        Offered(Settings(config));
        var p = Parse(args, ForgetUsage, new HashSet<string>(StringComparer.Ordinal) { "--person", "--revision" }, new HashSet<string>(StringComparer.Ordinal));
        if (p.Words.Count != 1) throw new ArgumentException(ForgetUsage);
        var id = MemoryId(p.Words[0], ForgetUsage);
        var person = p.One("--person")!;
        var revision = Number(p, "--revision", 1, int.MaxValue);
        await using var own = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var source = store ?? own!;
        var (login, refusal) = await Login(source, person, ct);
        if (login is not { } at) { o.WriteLine($"Nothing forgotten: {refusal}."); return 1; }
        MemoryTrashed t;
        try { t = await new MemoryStore(source).Forget(at, id, revision, Call, ct); }
        catch (MemoryRefused e) when (e.Code == "unknown") { o.WriteLine(NoMemory(person)); return 1; }
        catch (MemoryRefused e) when (e.Code == "conflict")
        {
            o.WriteLine($"Nothing forgotten: it changed since revision {revision}{(e.Revision is { } now ? $" and is at revision {now} now" : "")}.");
            return 1;
        }
        catch (MemoryRefused e) { o.WriteLine($"Nothing forgotten: {Shown(e.Message, 600)}"); return 1; }
        var days = (int)Math.Ceiling((t.Trash.PurgeAfter - t.Trash.TrashedAt).TotalDays);
        o.WriteLine($"In the trash: {t.Id} (revision {t.Revision}). It is deleted for good after {days} {(days == 1 ? "day" : "days")} unless someone brings it "
            + "back (the app's Memories view, Trash).");
        if (t.Trash.Links > 0) o.WriteLine($"  Its {t.Trash.Links} {(t.Trash.Links == 1 ? "link waits" : "links wait")} in the trash with it.");
        Cascade(o, t.Cascade);
        o.WriteLine($"  To delete it for good sooner: ./rhythm delete-forever --person {person} {t.Id}");
        return 0;
    }

    // What a change's retraction cascade did (migration 049), as an AI client's answer names it (MemoryTools.Cascade): the
    // memories this one had replaced that are current again or replaced by it again, and those built on it flagged for a
    // check or no longer. Every id is named (at most 25 of each); nothing when the cascade did nothing.
    static void Cascade(TextWriter o, MemoryCascade? c)
    {
        if (c is null) return;
        static string Ids(IReadOnlyList<Guid> ids) => string.Join(", ", ids);
        if (c.Reopened.Count > 0)
            o.WriteLine($"  Current again, since this one had replaced {(c.Reopened.Count == 1 ? "it" : "them")}: {Ids(c.Reopened)}.");
        if (c.Reclosed.Count > 0) o.WriteLine($"  Replaced by this one again: {Ids(c.Reclosed)}.");
        if (c.Flagged.Count > 0)
            o.WriteLine($"  Flagged for a check, since {(c.Flagged.Count == 1 ? "it was" : "they were")} built on this one: {Ids(c.Flagged)}. "
                + "A flag comes off when this one comes back.");
        if (c.Unflagged.Count > 0) o.WriteLine($"  No longer flagged for this one: {Ids(c.Unflagged)}.");
    }

    // set-status: confirm a memory (canonical), mark it a draft again, or mark it wrong (deprecated): MemoryStore.SetStatus
    // through the terminal's channel. Search ranks a confirmed memory first and a capsule serves only confirmed ones.
    // Marking one wrong, or bringing it back, prints what its retraction cascade did (049), as forget does.
    public static async Task<int> SetStatus(string[] args, TextWriter o, NpgsqlDataSource? store = null, RhythmConfig? config = null, CancellationToken ct = default)
    {
        Offered(Settings(config));
        var p = Parse(args, StatusUsage, new HashSet<string>(StringComparer.Ordinal) { "--person", "--revision" }, new HashSet<string>(StringComparer.Ordinal));
        if (p.Words.Count != 2) throw new ArgumentException(StatusUsage);
        var id = MemoryId(p.Words[0], StatusUsage);
        var status = p.Words[1].Trim().ToLowerInvariant();
        if (status is not ("canonical" or "draft" or "deprecated")) throw new ArgumentException(StatusUsage);
        var person = p.One("--person")!;
        var revision = Number(p, "--revision", 1, int.MaxValue);
        await using var own = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var source = store ?? own!;
        var (login, refusal) = await Login(source, person, ct);
        if (login is not { } at) { o.WriteLine($"Nothing changed: {refusal}."); return 1; }
        MemoryWrite w;
        try { w = await new MemoryStore(source).SetStatus(at, id, status, revision, Call, ct); }
        catch (MemoryRefused e) when (e.Code == "unknown") { o.WriteLine(NoMemory(person)); return 1; }
        catch (MemoryRefused e) when (e.Code == "conflict")
        {
            o.WriteLine($"Nothing changed: it changed since revision {revision}{(e.Revision is { } now ? $" and is at revision {now} now" : "")}.");
            return 1;
        }
        catch (MemoryRefused e) { o.WriteLine($"Nothing changed: {Shown(e.Message, 600)}"); return 1; }
        var named = status switch { "canonical" => "confirmed (canonical)", "deprecated" => "marked wrong (deprecated)", _ => "a draft" };
        o.WriteLine(w.Unchanged ? $"It is {named} already, so nothing changed: {w.Id} (revision {w.Revision})."
            : $"Now {named}: {w.Id} (revision {w.Revision}).");
        if (w.Hold is { } hold) o.WriteLine("  " + MemoryHolds.TerminalNotice(hold));
        Cascade(o, w.Cascade);
        return 0;
    }

    // share-memory: move a memory between its owner's private scope and Family (MemoryStore.Share, migration 048, through
    // the terminal's channel): Family unless --private, which takes a Family memory back to its author's private scope,
    // its author's or a Family admin's step. Only with memory.share on beside memory.tools (MemorySettings.ShareOn), and
    // with memory scopes on, else refused before anything is read (scopes_off). After a move the map is asked for a
    // rebuild within the share tool's bound (RefreshRequests.QueueBounded), as the share tool asks for one.
    public static async Task<int> ShareMemory(string[] args, TextWriter o, NpgsqlDataSource? store = null, RhythmConfig? config = null, CancellationToken ct = default)
    {
        if (!MemorySettings.ShareOn(Settings(config))) throw new ArgumentException(UnknownCommand);
        var p = Parse(args, ShareUsage, new HashSet<string>(StringComparer.Ordinal) { "--person", "--revision" },
            new HashSet<string>(StringComparer.Ordinal) { "--private" });
        if (p.Words.Count != 1) throw new ArgumentException(ShareUsage);
        var id = MemoryId(p.Words[0], ShareUsage);
        var person = p.One("--person")!;
        var revision = Number(p, "--revision", 1, int.MaxValue);
        var back = p.Flags.Contains("--private");
        await using var own = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var source = store ?? own!;
        var (login, refusal) = await Login(source, person, ct);
        if (login is not { } at) { o.WriteLine($"Nothing moved: {refusal}."); return 1; }
        MemoryWrite w;
        try { w = await new MemoryStore(source).Share(at, id, back ? "private" : "shared", revision, Call, ct); }
        catch (MemoryRefused e) when (e.Code == "unknown") { o.WriteLine(NoMemory(person)); return 1; }
        catch (MemoryRefused e) when (e.Code == "conflict")
        {
            o.WriteLine($"Nothing moved: it changed since revision {revision}{(e.Revision is { } now ? $" and is at revision {now} now" : "")}.");
            return 1;
        }
        catch (MemoryRefused e) { o.WriteLine($"Nothing moved: {Shown(e.Message, 600)}"); return 1; }
        if (w.Unchanged)
        {
            o.WriteLine($"It is {(back ? "private" : "in Family")} already, so nothing moved: {w.Id} (revision {w.Revision}).");
            return 0;
        }
        try { await new RefreshRequests(source).QueueBounded(ct); }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException) { /* the scheduled rebuild lists it where it is now */ }
        o.WriteLine(back
            ? $"Taken back to {Whose(w.Scope, w.Subject)} private scope: {w.Id} (revision {w.Revision}). Family's members no longer read it."
            : $"Shared into Family: {w.Id} (revision {w.Revision}). Every member reads it now, with its author, from this change on; its history "
              + "before the share stays its author's.");
        if (w.Project is { } project) o.WriteLine($"  Its project there: {Shown(project.Slug, 80)}{(project.Created ? " (made for it)" : "")}.");
        if (w.ProjectLeft) o.WriteLine("  Its project did not come with it: it is in no project now.");
        if (w.Changed.Contains("capsule")) o.WriteLine("  It left its capsule slot: slot it again where it is now if it belongs there (./rhythm capsule).");
        if (!back) o.WriteLine($"  Its author or a Family admin can take it back: ./rhythm share-memory --person {person} {w.Id} --private");
        o.WriteLine("  The map shows it where it is now after its next rebuild.");
        return 0;
    }

    // delete-forever: what this person may delete for good from the trash (no memory named), or one memory deleted for
    // good once the person types its id (MemoryStore.TrashEntry and DeleteForever, through the terminal's channel, with
    // the nonce of the trip into the trash shown): never one outside the trash, and never undone.
    public static async Task<int> DeleteForever(string[] args, TextReader input, TextWriter o, NpgsqlDataSource? store = null, RhythmConfig? config = null,
        CancellationToken ct = default)
    {
        Offered(Settings(config));
        var p = Parse(args, DeleteUsage, new HashSet<string>(StringComparer.Ordinal) { "--person", "--limit", "--next" }, new HashSet<string>(StringComparer.Ordinal));
        var named = p.Words.Count switch { 0 => (Guid?)null, 1 => MemoryId(p.Words[0], DeleteUsage), _ => throw new ArgumentException(DeleteUsage) };
        if (named is not null && (p.One("--limit") is not null || p.One("--next") is not null)) throw new ArgumentException(DeleteUsage);
        var person = p.One("--person")!;
        var limit = Number(p, "--limit", 1, MemoryStore.TrashMax) ?? 20;
        (DateTimeOffset At, Guid Id)? after;
        try { after = MemoryTools.Cursor(p.One("--next")); }
        catch (MemoryRefused e) { throw new ArgumentException($"--next: {e.Message}"); }
        await using var own = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var source = store ?? own!;
        var (login, refusal) = await Login(source, person, ct);
        if (login is not { } at) { o.WriteLine($"Nothing deleted: {refusal}."); return 1; }
        var memories = new MemoryStore(source);
        string Line(MemoryTrashItem t) => $"{t.TrashedAt.UtcDateTime:yyyy-MM-dd HH:mm}  {t.Id}  {(t.Scope == "private" ? "private" : "Family")}  "
            + $"{(t.DaysLeft == 1 ? "1 day" : $"{t.DaysLeft} days")} left  "
            + (t.Hold is { } held ? $"[held for review: {MemoryHolds.Why(held)}]" : $"{Shown(t.Preview ?? "", Preview)}{(t.Truncated ? " (more)" : "")}");
        if (named is not { } id)
        {
            MemoryTrashPage page;
            try { page = await memories.Trash(at, limit, after is { } a ? new MemoryTrashCursor(a.At, a.Id) : null, Preview, Call, ct); }
            catch (MemoryRefused e) { o.WriteLine($"Nothing listed: {Shown(e.Message, 600)}"); return 1; }
            if (page.Items.Count == 0) { o.WriteLine($"Nothing in the trash that {person} may restore or delete."); return 0; }
            foreach (var t in page.Items) o.WriteLine(Line(t) + (t.MayDeleteForever ? "" : $"  [not {person}'s to delete for good]"));
            if (page.Next is { } next) o.WriteLine($"More: ./rhythm delete-forever --person {person} --next {MemoryTools.Cursor(next.TrashedAt, next.Id)}");
            o.WriteLine($"Delete one for good: ./rhythm delete-forever --person {person} <memory> (it asks you to type the memory's id). "
                + "The trash deletes each for good by itself once its days are over.");
            return 0;
        }
        MemoryTrashItem? entry;
        try { entry = await memories.TrashEntry(at, id, Preview, Call, ct); }
        catch (MemoryRefused e) { o.WriteLine($"Nothing deleted: {Shown(e.Message, 600)}"); return 1; }
        if (entry is null)
        {
            o.WriteLine($"No memory with that id in the trash that {person} may restore: only a memory in the trash is deleted for good, so it is forgotten "
                + "first (in the app's Memories view, or by an AI client's forget).");
            return 1;
        }
        o.WriteLine(Line(entry));
        if (!entry.MayDeleteForever) { o.WriteLine($"Nothing deleted: only its author or a Family admin deletes it for good, not {person}."); return 1; }
        o.WriteLine("Delete it for good? Its text, its earlier versions and its links go, and it cannot be brought back; only a line in its audit trail stays.");
        o.Write("Type the memory's id to delete it for good, anything else to keep it: ");
        o.Flush();
        var typed = (await input.ReadLineAsync(ct))?.Trim();
        o.WriteLine();
        if (typed != entry.Id.ToString()) { o.WriteLine("Nothing deleted: it stays in the trash."); return 1; }
        MemoryDeleted d;
        try { d = await memories.DeleteForever(at, entry.Id, entry.Nonce, typed, Call, ct); }
        catch (MemoryRefused e) when (e.Code == "unknown")
        {
            o.WriteLine("Nothing deleted: it left the trash, or went back into it, since it was shown here. List the trash again.");
            return 1;
        }
        catch (MemoryRefused e) { o.WriteLine($"Nothing deleted: {Shown(e.Message, 600)}"); return 1; }
        o.WriteLine($"Deleted for good: {d.Id}, with {d.Versions} earlier {(d.Versions == 1 ? "version" : "versions")} and {d.Links} {(d.Links == 1 ? "link" : "links")}.");
        return 0;
    }

    // The settings' model from the folder its environment variable names, never downloaded (D-041): with it, the meaning
    // arm runs as the engine's does; without, keyword results and recall's notice.
    // The check a person's release or an import takes (SB-P5-02, SB-P5-03): the settings' thresholds, and the model when the
    // settings have it on and its files are here, as recall loads it; else the same words only. A model that does not load
    // never stops the step.
    internal static DuplicateCheck ReviewCheck(RhythmConfig config, Embeddings? embeddings)
    {
        if (embeddings is not null) Load(embeddings, EmbeddingSettings.From(config));
        return DuplicateCheck.For(config, embeddings?.Model is null ? null : embeddings);
    }

    // The settings when they can be read, else none: an import and the two like it read rhythm.yaml only for this check, so a
    // missing or broken one reports as before.
    internal static RhythmConfig? SettingsIfAny(RhythmConfig? given)
    {
        if (given is not null) return given;
        try { return Settings(); }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
    }

    // What rhythm import and rhythm import-second-brain do after they have committed (SB-P5-02, SB-P5-03): with memory.tools on,
    // the memories they added are checked as a save is, with at most this long for all of them; with it off nothing is read
    // and nothing printed. A check that fails says so and leaves the import as it is.
    static readonly TimeSpan ImportCheckBudget = TimeSpan.FromMinutes(10);

    internal static async Task ReviewImported(NpgsqlDataSource source, IReadOnlyCollection<Guid> added, string client, RhythmConfig? config, TextWriter o, CancellationToken ct)
    {
        if (added.Count == 0 || SettingsIfAny(config) is not { } settings || !MemorySettings.ToolsOn(settings)) return;
        using var loaded = new Embeddings(EmbeddingSettings.From(settings).Model);
        try
        {
            var r = await new MemoryStore(source).ReviewImported(added, ReviewCheck(settings, loaded), client, ImportCheckBudget, ct);
            if (ReviewLine(r) is { } line) o.WriteLine(line);
            if (r.Left > 0) o.WriteLine($"  {r.Left} not checked for copies within {ImportCheckBudget.TotalMinutes:0} minutes: they are checked once their words change.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            o.WriteLine($"  Not checked for copies: the check failed ({e.GetType().Name}), and what was imported stands.");
        }
    }

    // What an import's check put before a person, each as its own list counts it: the memories flagged as possible copies (the
    // copies list's), and the pairs the contradiction review lists (`rhythm contradictions`, the app's Conflicts), not counting a
    // pair offered that waits among the copies until it is named. None when the check put nothing there.
    internal static string? ReviewLine(MemoryImportReview r) => r.Flagged + r.Queued > 0
        ? $"  For a person's review: {r.Flagged} flagged as possible copies, {r.Queued} queued as possible contradictions of memories here."
        : null;

    static (IEmbedder? Model, string Status) Load(Embeddings embeddings, EmbeddingSettings settings)
    {
        if (!settings.Enabled) return (null, "off");
        return EmbeddingModels.Given(settings.Model) is { } folder && embeddings.TryLoad(folder) ? (embeddings.Model, "on")
            : (null, "not loaded in the terminal (only from the folder RHYTHM_MODEL_DIR names, never downloaded)");
    }

    // Stored text for one terminal line: control, formatting and separator characters (escape sequences, bidirectional
    // overrides, zero-width marks) become spaces, white space is folded, and it is cut at `max` characters, never inside
    // a surrogate pair.
    internal static string Shown(string text, int max)
    {
        var line = new StringBuilder(Math.Min(text.Length, max + 1));
        var space = false;
        foreach (var c in text)
        {
            var blank = char.IsWhiteSpace(c) || char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse;
            if (blank) { space = line.Length > 0; continue; }
            if (space) { line.Append(' '); space = false; }
            line.Append(c);
            if (line.Length >= max) break;
        }
        var shown = line.ToString();
        if (shown.Length > max) shown = shown[..max];
        if (shown.Length > 0 && char.IsHighSurrogate(shown[^1])) shown = shown[..^1];
        return shown.TrimEnd();
    }
}
