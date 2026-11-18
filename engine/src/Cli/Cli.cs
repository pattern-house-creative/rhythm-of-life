namespace Knowledge.Cli;

// The install commands of `rhythm` that run inside the engine image (the cli compose service, which works in the
// deploy folder mounted at /work). They read rhythm.yaml themselves, so they run before it exists or while it is
// broken. The host wrappers (rhythm, rhythm.ps1 at the repository root) add up, down, logs, backup, restore, upgrade.
public static class CliCommands
{
    public static readonly string[] Names = ["init", "connect", "disconnect", "doctor", "preflight", "ready", "token", "person", "family", "share", "scopes", "viewer", "export", "import", "remember", "recall", "list", "release", "capsule", "delete-forever", "forget", "set-status", "link", "unlink", "still-true", "clear-flags", "undismiss", "queues", "duplicates", "contradictions", "insight-candidates", "insights", "resolve", "share-memory", "night", "import-second-brain", "help", "capture"];

    public static async Task<int> Run(string command, string[] args, CancellationToken ct)
    {
        var deploy = DeployFolder.Current();
        switch (command)
        {
            case "init": return InitCommand.Run(deploy, args);
            case "connect": return await ConnectCommand.Run(deploy, args, ct: ct);
            case "disconnect": return ConnectCommand.Disconnect(deploy, args, Console.Out);
            case "doctor": return await DoctorCommand.Run(deploy, Console.Out, StackCommands.Http, ct: ct, read: StackCommands.Body, mounts: DoctorCommand.EngineMounts.Current());
            case "preflight": return StackCommands.Preflight(deploy, Console.Out);
            case "ready": return await StackCommands.Ready(deploy, Console.Out, StackCommands.Http, ct: ct);
            case "token": return await TokenCommand.Token(deploy, args, Console.Out, ct: ct);
            // Offboarding a person (SB-P7-13): the operator's step, which asks before it changes anything.
            case "person" when args is ["offboard", ..]: return await OffboardCommand.Run(args[1..], Console.In, Console.Out, ct: ct);
            case "person": return await TokenCommand.Person(args, Console.Out, ct: ct);
            case "family": return await ScopeCommands.Family(args, Console.Out, ct: ct);
            case "share": return await ScopeCommands.Share(args, Console.Out, ct: ct);
            case "scopes": return await ScopeCommands.Scopes(deploy, args, Console.In, Console.Out, ct: ct);
            case "viewer": return await ViewerCommand.Run(deploy, args, Console.Out, ct: ct);
            case "export": return await ArchiveCommands.Export(deploy, args, Console.Out, ct: ct);
            case "import": return await ArchiveCommands.Import(deploy, args, Console.Out, ct: ct);
            case "remember": return await MemoryCommands.Remember(args, Console.In, Console.Out, ct: ct);
            case "recall": return await MemoryCommands.Recall(args, Console.Out, ct: ct);
            case "list": return await MemoryCommands.List(args, Console.Out, ct: ct);
            // A person's own steps (SB-P3-22), only with memory.tools on; off, each answers as a command the engine does
            // not know, as before.
            case "release": return await MemoryCommands.Release(args, Console.Out, ct: ct);
            case "capsule": return await MemoryCommands.Capsule(args, Console.Out, ct: ct);
            case "delete-forever": return await MemoryCommands.DeleteForever(args, Console.In, Console.Out, ct: ct);
            case "forget": return await MemoryCommands.Forget(args, Console.Out, ct: ct);
            case "set-status": return await MemoryCommands.SetStatus(args, Console.Out, ct: ct);
            // A person's own link steps (run 17), only with memory.tools on too; off, as commands the engine does not know.
            case "link": return await MemoryCommands.Link(args, Console.Out, ct: ct);
            case "unlink": return await MemoryCommands.Unlink(args, Console.Out, ct: ct);
            case "still-true": return await MemoryCommands.StillTrue(args, Console.Out, ct: ct);
            case "clear-flags": return await MemoryCommands.ClearFlags(args, Console.Out, ct: ct);
            case "undismiss": return await MemoryCommands.Undismiss(args, Console.Out, ct: ct);
            // A person's review queues (SB-P6-01), only with memory.tools on too; off, as commands the engine does not know.
            case "queues": return await MemoryCommands.Queues(args, Console.Out, ct: ct);
            case "duplicates": return await MemoryCommands.Duplicates(args, Console.Out, ct: ct);
            case "contradictions": return await MemoryCommands.Contradictions(args, Console.Out, ct: ct);
            case "insight-candidates": return await MemoryCommands.InsightCandidates(args, Console.Out, ct: ct);
            case "insights": return await MemoryCommands.Insights(args, Console.Out, ct: ct);
            case "resolve": return await MemoryCommands.Resolve(args, Console.Out, ct: ct);
            // A person's share (run 14), only with memory.share on beside memory.tools; off, as a command the engine does not know.
            case "share-memory": return await MemoryCommands.ShareMemory(args, Console.Out, ct: ct);
            // The night summary (SB-P5-14), only with engine.nightSummary on; off, it answers as a command the engine does not
            // know, as before.
            case "night": return await NightCommand.Run(args, Console.Out, ct: ct);
            // A capture connection (SB-P7-01), only with memory.capture on beside memory.tools; off, as a command the engine
            // does not know, as before.
            case CaptureCommand.Name: return await CaptureCommand.Run(deploy, args, Console.Out, ct: ct);
            // The Second Brain export importer (SB-P8-01), only with memory.secondBrainImport on; off, it answers as a command
            // the engine does not know, as before.
            case SecondBrainImportCommand.Name: return await SecondBrainImportCommand.Run(deploy, args, Console.Out, ct: ct);
            default:
                var (steps, share) = PersonStepsOffered();
                Console.WriteLine(Help(steps, share, NightOffered(), ImportOffered(), CaptureOffered()));
                return 0;
        }
    }

    // Whether help lists a person's own steps: only when rhythm.yaml loads and says memory.tools on, so with it off,
    // absent, or no settings yet, help is the list it was; and the share among them only with memory.share on too.
    static (bool Steps, bool Share) PersonStepsOffered()
    {
        try
        {
            var settings = MemoryCommands.Settings();
            return (MemorySettings.ToolsOn(settings), MemorySettings.ShareOn(settings));
        }
        catch (Exception e) when (e is ConfigException or IOException or UnauthorizedAccessException or ArgumentException) { return (false, false); }
    }

    // Whether help lists the night summary: only when rhythm.yaml loads and says engine.nightSummary on.
    static bool NightOffered()
    {
        try { return NightCommand.Offered(MemoryCommands.Settings()); }
        catch (Exception e) when (e is ConfigException or IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    // Whether help lists the Second Brain importer: only when rhythm.yaml loads and says memory.secondBrainImport on.
    static bool ImportOffered()
    {
        try { return SecondBrainImportCommand.Offered(MemoryCommands.Settings()); }
        catch (Exception e) when (e is ConfigException or IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    // Whether help lists the capture connections: only when rhythm.yaml loads and says memory.capture on beside memory.tools.
    static bool CaptureOffered()
    {
        try { return CaptureCommand.Offered(MemoryCommands.Settings()); }
        catch (Exception e) when (e is ConfigException or IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    // The list. With the person's steps, their lines come before the engine's own commands (the share's last, with
    // memory.share on beside memory.tools), and so does the night summary's line when it is offered, and then the
    // Second Brain importer's, and then the capture connections'; without any of them, it is the list as before, character
    // for character.
    public static string Help(bool personSteps, bool share = false, bool night = false, bool import = false, bool capture = false) =>
        Head + "\n" + (personSteps ? Steps + "\n" + (share ? ShareStep + "\n" : "") : "")
        + (night ? Night + "\n" : "") + (import ? Import + "\n" : "") + (capture ? Capture + "\n" : "") + "  check-config | refresh | migrate | serve";

    static readonly string Night = "  " + NightCommand.Usage.PadRight(54) + "what the daily jobs did, a line a job (the latest night kept)";

    const string Import = """
          import-second-brain <file> --person <id> [--scope-map <file>] [--household] [--write]
                                                                a Second Brain export: what it would add, or with --write its import
        """;

    const string Capture = """
          capture connect <name> [--person <id>] [--out <file>] | capture list | capture disconnect <name>
                                                                a token for POST /capture; disconnecting moves its mirrors to the trash
        """;

    const string Steps = """
          release --person <id> [<memory> [--revision <n>]] [--family|--private] [--reason ..] [--limit <1-50>]
               [--next <cursor>]                               held for review: the queue, one read whole, its release
          capsule --person <id> <memory> core <slot> | project <slot> | none [--revision <n>]
                                                               a memory into a capsule slot, or out of it
          capsule --person <id> [--family] [--project <slug>]  a capsule as AI clients are served it, and what waits
          delete-forever --person <id> [<memory>] [--limit <1-50>] [--next <cursor>]
                                                               the trash, or one memory deleted for good (type its id)
          forget --person <id> <memory> [--revision <n>]       a memory into the trash
          set-status --person <id> <memory> canonical|draft|deprecated [--revision <n>]
                                                               confirm a memory, make it a draft, or mark it wrong
          link --person <id> <memory> [<other> [--type <type>]] [--limit <1-50>]
                                                               a memory's links, or a person's link to another
          unlink --person <id> <memory> <other> [--type <type>]
                                                               their links removed, the map's suggestion dismissed
          still-true --person <id> [<memory> [--revision <n>]] [--family|--private] [--limit <1-50>]
               [--next <cursor>]                               what may be out of date, or one checked as still true
          clear-flags --person <id> <memory> [--revision <n>]  its flags for a check off, once checked
          queues --person <id> [--family|--private]            what waits on them, each the length of its list
          duplicates --person <id> [<memory>] [--family|--private] [--limit <1-50>] [--next <cursor>]
                                                               flagged as possible copies, or one with its copies
          contradictions --person <id> [<memory> <other>] [--family|--private] [--limit <1-50>] [--next <cursor>]
                                                               possible contradictions, or one pair whole
          insight-candidates --person <id> [--state waiting|used|rejected] [--family|--private] [--limit <1-50>]
               [--next <cursor>]                               pairs of memories worth a look
          insights --person <id> [<insight> [reopen [--note <words>]]] [--view <view>] [--family|--private]
               [--limit <1-50>] [--next <cursor>]              the insight stream, one's history, a review taken back
          resolve --person <id> <item> <action> [--other <memory>] [--revision <n>] [--other-revision <n>]
               [--note <words>] [--insight <id>]               a person's step in a review queue, once
          undismiss --person <id> [<dismissal>] [--limit <1-50>]
                                                               the map's dismissals, or one taken back
        """;

    const string ShareStep = """
          share-memory --person <id> <memory> [--private] [--revision <n>]
                                                               into Family, or back to its author's private scope
        """;

    const string Head = """
                    Commands inside the engine image (./rhythm <command> on the host):
                      init [--no-input] [--force] [--name ..] [--people ..] [--timezone ..] [--data ..] [--exposure ..] [--host ..] [--mcp-host ..]
                           [--demo yes|no] [--notes ..] [--app-port ..] [--mcp-port ..] [--project-name ..]
                                                                           settings, .env and every secret
                      connect [<adapter>] [--set key=value] [--no-input]   list adapters, or turn one on
                      disconnect <adapter>                                  turn one off
                      doctor                                                check settings, secrets and the running stack
                      token create <name> [--person <id>] [--out <file>] [--role harness|ingest] [--plugin <name>] [--expires-at <UTC timestamp>]
                                                                           a token file; --person: it speaks for that person
                      token list | token revoke <name> | token revoke --all every enabled token and whom it speaks for; disable
                                                                           one, or every token at once after a leak
                      person list | person suspend <id> | person unsuspend <id>
                                                                           people, Family and tokens; suspending disables their tokens
                      person offboard <id>                                  someone leaves: access ended, their private memories to the trash
                      family add <id> | family remove <id> | family admin <id> | family unadmin <id>
                                                                           who reads Family's records with memory scopes on, and its admins
                      share list | share add <person> <provider> | share revoke <person> <provider>
                                                                           one person's source that Family reads with memory scopes on
                      scopes off                                            switch memory scopes off on purpose, after a preview
                      viewer invite <person> [--minutes <1-60>] | viewer list | viewer revoke <name> | viewer revoke --person <id>
                                                                           viewer sessions in the app (off unless app.viewers is on)
                      export [--person <id>] [--out <file>]                native memories, as that person (or the household view) reads them
                      import <file> [--person <id>] [--family] [--dry-run] an export back in: only the scopes it names, never overwriting
                      remember --person <id> [--family] [--tag <tag>]... [--project <slug>] [--kind ..] [--importance 1-5]
                               [--volatility ..] [--key ..] <text...> | -
                                                                           a memory, as that person (theirs unless --family)
                      recall --person <id> [--top <1-20>] [--json] <question...>
                                                                           what that person reads about it, words and meaning
                      list --person <id> [--family|--private] [--tag ..] [--project ..] [--kind ..] [--status ..] [--limit <1-50>]
                           [--next <cursor>]                               that person's memories, newest first
                    """;
}
