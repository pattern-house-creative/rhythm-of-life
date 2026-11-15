namespace Knowledge;

// The memory section of rhythm.yaml and the defaults native memories take (docs/dev/memory.md section 6; SB-P1-09,
// D-021 and D-048). Five settings so far. memory.scopes: off, the default and what every existing install
// has (a rhythm.yaml without the key), or on. memory.capsules (SB-P3-08): off, the same default, or on, which lists the
// get_prompt_capsule tool and the capsule resources to AI clients (CapsulesOn). memory.tools (SB-P3-09): off, the same
// default, or on, which lists the memory tools to AI clients and answers them (ToolsOn, Mcp/MemoryTools.cs), and lists
// app_memories to the app for its Memories view (Mcp/AppMemories.cs, SB-P3-12). memory.share (SB-P3-10, Q-157): off, the
// same default, or on, which with memory.tools on as well lists the share tool beside the memory tools (ShareOn); it
// moves a memory between its owner's private scope and Family only while memory.scopes is on, and real use waits for the
// owner's per-person scopes (D-013). memory.secondBrainImport (SB-P8-01): off, the same default, or on, which offers the
// operator the Second Brain export importer at the terminal (SecondBrainImportOn). memory.capture (SB-P7-01): off, the
// same default, or on, which with memory.tools on as well opens the /capture route and rhythm capture (CaptureOn). Any
// other value, or any other key under memory, fails at start naming it.
//
// On is accepted since phase 1's exit (section 6.4, D-131): every read path filters by scope and the
// authorisation tests pass with scopes on (SB-P1-10; its write-side cases go with phase 3's write functions, D-130).
// Off stays the default and what every existing install has: nothing turns scopes on but an operator's rhythm.yaml, and
// for a real household that is the owner's decision (D-013). Before the exit this version refused on at the settings
// load and at the switch's first start (Principals.Install); tests still reach that refusal by passing accepted: false.
//
// The other defaults are rules rather than settings, kept here so the write paths of phase 3 take them from one place:
// a memory lands private by default when scopes are on (Lands), an AI client's memory is a draft or held for review
// (NewStatus, and the holds of D-050), and no capture lane is on: none is built yet, and each one phase 7 adds is an
// adapter, off until its person turns it on (D-016; ConfigTests pins the adapters on by default).
public sealed record MemorySettings(bool Scopes)
{
    // True since phase 1's exit, that exit's own reviewed change; never a setting.
    public const bool ScopesAccepted = true;

    // A new memory's status: a draft until a person confirms it; never canonical because an AI wrote it (D-021, D-015).
    // Migration 025 gives memories.status the same default. A held memory (memories.hold, SB-P3-18) stays out of
    // search, capsules and the map until a person releases it (D-039).
    public const string NewStatus = "draft";

    public static readonly MemorySettings Off = new(false);
    static readonly string[] Keys = ["scopes", "capsules", "tools", "share", "secondBrainImport", "capture"];

    public static MemorySettings From(RhythmConfig config)
    {
        if (config.Memory is null) return Off;
        if (config.Memory is not IDictionary<object, object?> section)
            throw new ConfigException("memory is a section of settings (memory: { scopes: off })");
        foreach (var key in section.Keys.Select(k => k?.ToString() ?? ""))
            if (!Keys.Contains(key)) throw new ConfigException($"memory.{key} is not a setting; memory takes {string.Join(", ", Keys)}");
        if (section.TryGetValue("capsules", out var capsules) && capsules is not (null or "off" or "on"))
            throw new ConfigException("memory.capsules is off or on (docs/configuration.md)");
        if (section.TryGetValue("tools", out var tools) && tools is not (null or "off" or "on"))
            throw new ConfigException("memory.tools is off or on (docs/configuration.md)");
        if (section.TryGetValue("share", out var share) && share is not (null or "off" or "on"))
            throw new ConfigException("memory.share is off or on (docs/configuration.md)");
        if (section.TryGetValue("secondBrainImport", out var import) && import is not (null or "off" or "on"))
            throw new ConfigException("memory.secondBrainImport is off or on (docs/configuration.md)");
        if (section.TryGetValue("capture", out var capture) && capture is not (null or "off" or "on"))
            throw new ConfigException("memory.capture is off or on (docs/configuration.md)");
        var scopes = section.TryGetValue("scopes", out var value) ? value : null;
        return scopes switch
        {
            null => Off,
            "off" => Off,
            "on" => new(true),
            _ => throw new ConfigException("memory.scopes is off or on (docs/configuration.md)"),
        };
    }

    // memory.capsules: on lists get_prompt_capsule and the capsule resources (rhythm://capsules/...) to AI clients and
    // answers them; off, absent (every rhythm.yaml written before it) or an empty section, neither is listed nor answered,
    // so every existing install's tool and resource lists are as they were. Its value is checked by From at start.
    public static bool CapsulesOn(RhythmConfig config) =>
        config.Memory is IDictionary<object, object?> section && section.TryGetValue("capsules", out var value) && value is "on";

    // memory.tools: on lists the 41 memory tools (MemoryTools.Names, from remember to resolve) to AI clients and answers
    // them, and app_memories to the app (AppMemories), where a person takes the steps no AI client may; off, absent (every
    // rhythm.yaml written before it) or an empty section, none of them is made at all, so tools/list and every call answer
    // exactly as before (a call naming one is an unknown tool, the app's a tool its role does not have).
    // get_prompt_capsule stays behind memory.capsules.
    public static bool ToolsOn(RhythmConfig config) =>
        config.Memory is IDictionary<object, object?> section && section.TryGetValue("tools", out var value) && value is "on";

    // memory.share: on, with memory.tools on too, makes and lists the share tool (MemoryTools.ShareTool), which moves a
    // memory between its owner's private scope and Family and takes it back (migration 048); off, absent (every
    // rhythm.yaml written before it), empty, or with memory.tools off, it is not made at all, so a call naming it is an
    // unknown tool and every other answer is as before. With memory.scopes off the tool refuses every call (every memory
    // is the household's already). Its value is checked by From at start.
    public static bool ShareOn(RhythmConfig config) =>
        ToolsOn(config) && config.Memory is IDictionary<object, object?> section && section.TryGetValue("share", out var value) && value is "on";

    // memory.capture (SB-P7-01): on, with memory.tools on too (what it saves waits in the review queue those serve), opens
    // the engine's /capture route to capture tokens (Mcp/CaptureEndpoint.cs) and offers the operator rhythm capture; off,
    // absent (every rhythm.yaml written before it), empty, or with memory.tools off, the route answers as an unknown route
    // does, the command as one the engine does not know, and every other answer is as before. A capture token already made
    // keeps its row and reaches nothing. Its value is checked by From at start.
    public static bool CaptureOn(RhythmConfig config) =>
        ToolsOn(config) && config.Memory is IDictionary<object, object?> section && section.TryGetValue("capture", out var value) && value is "on";

    // memory.secondBrainImport (SB-P8-01): on offers the operator rhythm import-second-brain, which reads a Second Brain
    // export file and, only when asked (--write), adds its memories, projects and links through migration 059's checked
    // functions; off, absent (every rhythm.yaml written before it) or empty, the command answers as one the engine does not
    // know and help is the list it was. Nothing else reads it: no tool, route or job imports anything. Its value is checked
    // by From at start.
    public static bool SecondBrainImportOn(RhythmConfig config) =>
        config.Memory is IDictionary<object, object?> section && section.TryGetValue("secondBrainImport", out var value) && value is "on";

    // Why this version will not run with these settings, or null. Checked when the settings load (so check-config,
    // doctor and every start say it before anything runs) and again by the start-up step that would act on it.
    public string? Refusal(bool accepted = ScopesAccepted) => Scopes && !accepted
        ? "memory.scopes: on is refused by this version of the engine: per-person scopes are still being built, and the engine "
          + "accepts the switch only once every read path filters by scope (docs/dev/memory.md section 6.4). Set memory.scopes: off"
        : null;

    // Where a new native memory lands (section 5, Where writes land): the scope its call names, else the writer's own.
    // `named` is private or shared, or upstream's personal and company, or Family (D-044); null when the call names none.
    // With scopes off every memory is the household's, and a call that asks for a private one is refused (D-048). With
    // them on a memory is private to its writer's person by default (D-021), and a writer with no person (the household
    // view) has no private scope, so it writes a memory only into Family, and only when the call says so.
    // Migration 033's memory_landing decides the same in the database, under the recorded mode locked FOR SHARE, for every
    // memory written (MemoryStore); a test keeps the two equal.
    public static Landing Lands(bool scopesOn, string? writerPerson, string? named)
    {
        var wanted = Named(named);
        if (!scopesOn)
            return wanted == "private"
                ? Landing.Refused("private memories wait until per-person scopes are on; with them off every memory is visible to everyone in the household")
                : new(RhythmConfig.Household, "shared", null);
        if (wanted == "shared") return new(RhythmConfig.Household, "shared", null);
        return writerPerson is null
            ? Landing.Refused("this client has no person, so it has no private scope: name the shared scope (Family) to save a household memory")
            : new(writerPerson, "private", null);
    }

    // A scope as a call names it, in Rhythm's words or upstream's (D-044): "private" or "shared", null for none.
    public static string? Named(string? named) => named?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "private" or "personal" => "private",
        "shared" or "family" or "company" => "shared",
        _ => throw new ArgumentException("scope is private or shared (personal and company are accepted too)"),
    };

    // A subject and stored scope, or why the write is refused.
    public sealed record Landing(string? Subject, string? Scope, string? Refusal)
    {
        public static Landing Refused(string why) => new(null, null, why);
    }
}
