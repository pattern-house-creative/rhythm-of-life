using System.Globalization;
using System.Text.Json;
using Npgsql;

namespace Knowledge.Cli;

// `rhythm import-second-brain` (SB-P8-01; cli/README.md, Importing a Second Brain export; docs/dev/memory.md section 9):
// the operator's step that reads one Second Brain export file (SecondBrainImport), for one person, with the owner's
// reviewed scope map when there is one, and prints what it would do. It writes only with --write; a dry run, the
// default, does everything and rolls it back, so the counts are those a real import gives. Only with
// memory.secondBrainImport on: off, absent or empty, it answers as a command the engine does not know, before it reads
// its arguments, a file or the database, and help is the list it was. It reads the file only: never the Second Brain
// service, never Drive. It prints counts, the reasons and the ids of what to review, never a record's words. With
// --household (D-247), only while memory scopes are off, every memory comes in as the household's, each keeping where it
// lived in Second Brain; with scopes on it is refused before anything is written.
public static class SecondBrainImportCommand
{
    public const string Name = "import-second-brain";
    public const string Usage = "import-second-brain <file> --person <id> [--scope-map <file>] [--household] [--write]";

    // Whether this install offers it: rhythm.yaml (RHYTHM_CONFIG) says memory.secondBrainImport on.
    public static bool Offered(RhythmConfig config) => MemorySettings.SecondBrainImportOn(config);

    public static async Task<int> Run(DeployFolder deploy, string[] args, TextWriter o, NpgsqlDataSource? store = null, RhythmConfig? config = null,
        CancellationToken ct = default)
    {
        if (!Offered(MemoryCommands.Settings(config))) throw new ArgumentException(MemoryCommands.UnknownCommand);
        string? person = null, file = null, scopeMap = null;
        bool write = false, dry = false, household = false;
        for (var i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--person" when i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) && person is null: person = args[++i]; break;
                case "--scope-map" when i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) && scopeMap is null: scopeMap = args[++i]; break;
                case "--household" when !household: household = true; break;
                case "--write" when !write && !dry: write = true; break;
                // The default, accepted so a runbook's line reads as it does for rhythm import.
                case "--dry-run" when !write && !dry: dry = true; break;
                case var a when !a.StartsWith("--", StringComparison.Ordinal) && file is null: file = a; break;
                default: throw new ArgumentException(Usage);
            }
        if (file is null || person is null) throw new ArgumentException(Usage + " (name the file and the person it is imported for)");
        var path = Path.GetFullPath(file);
        if (!File.Exists(path)) { o.WriteLine($"Nothing imported: there is no file {deploy.Shown(path)}."); return 1; }
        var map = SecondBrainScopeMap.None;
        if (scopeMap is not null)
        {
            var mapPath = Path.GetFullPath(scopeMap);
            if (!File.Exists(mapPath)) { o.WriteLine($"Nothing imported: there is no scope map {deploy.Shown(mapPath)}."); return 1; }
            try
            {
                using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(mapPath, ct));
                map = SecondBrainScopeMap.Read(json);
            }
            catch (Exception e) when (e is JsonException or SecondBrainExportInvalid)
            {
                o.WriteLine($"Nothing imported: {deploy.Shown(mapPath)} is not a scope map to use: {(e is JsonException ? "not a whole JSON file" : e.Message)}.");
                return 1;
            }
        }
        SbFile export;
        try { export = SbFile.Read(await File.ReadAllBytesAsync(path, ct)); }
        catch (SecondBrainExportInvalid e) { o.WriteLine($"Nothing imported: {deploy.Shown(path)} is not one to import: {e.Message}."); return 1; }
        await using var own = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var source = store ?? own!;
        if (await ArchiveCommands.Refusal(source, person, ct) is { } refused) { o.WriteLine($"Nothing imported: {refused}."); return 1; }
        SecondBrainImportReport report;
        try { report = await new SecondBrainImport(source).Import(export, person, map, write, household, ct); }
        catch (SecondBrainImportRefused e) { o.WriteLine($"Nothing imported: {e.Message}."); return 1; }
        foreach (var line in Lines(report, deploy.Shown(path), scopeMap is null ? null : deploy.Shown(Path.GetFullPath(scopeMap)))) o.WriteLine(line);
        // With memory.tools on, what it added is checked for copies and contradiction candidates (068).
        if (write) await MemoryCommands.ReviewImported(source, [.. report.Memories.Values.Distinct()], "rhythm import-second-brain", config, o, ct);
        return 0;
    }

    // The report as the terminal prints it: counts, reasons, and the ids of what the owner should review.
    public static IEnumerable<string> Lines(SecondBrainImportReport r, string shown, string? map)
    {
        var dry = !r.Wrote;
        var taken = r.ExportedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var version = r.Version == 0 ? "with no version (an older one)" : "version " + r.Version.ToString(CultureInfo.InvariantCulture);
        yield return (dry ? "Dry run: nothing was written. Importing " : "Imported ") + $"{shown} (a Second Brain export, {version}, taken {taken} UTC; "
            + $"SHA-256 {r.Sha256}) for {r.Person}, " + (r.Household ? "as household memories (--household, D-247)" + (map is null ? ":" : $", with the scope map {map}:")
                : map is null ? "with no scope map, so everything lands in their private scope:" : $"with the scope map {map}:");
        var scopes = r.Outcomes.Values.SelectMany(s => s.Keys).Distinct(StringComparer.Ordinal)
            .OrderBy(s => s == SecondBrainImportReport.FamilyScope ? 1 : s == SecondBrainImport.NoScope ? 2 : 0).ThenBy(s => s, StringComparer.Ordinal).ToList();
        foreach (var scope in scopes)
        {
            yield return "  " + (scope == SecondBrainImportReport.FamilyScope ? "Family" : scope == SecondBrainImport.NoScope ? "Neither end in the file"
                : $"{scope}'s private scope") + ":";
            foreach (var (kind, plural) in new[] { ("memory", "memories"), ("project", "projects"), ("link", "links") })
            {
                if (!r.Outcomes.TryGetValue(kind, out var byScope) || !byScope.TryGetValue(scope, out var outcomes)) continue;
                var held = kind == "memory" && r.Held.TryGetValue(scope, out var h) && h.Count > 0
                    ? $" ({h.Values.Sum()} of them held for review: {string.Join(", ", h.Select(x => $"{x.Key} {x.Value}"))})" : "";
                yield return $"    {plural}: " + string.Join("; ", outcomes.OrderBy(x => Rank(x.Key)).ThenBy(x => x.Key, StringComparer.Ordinal)
                    .Select(x => $"{x.Value} {Said(x.Key, dry)}" + (x.Key == "added" ? held : "")));
            }
        }
        // The household import's memories by where they lived in Second Brain, as each memory's origin keeps it (074).
        if (r.Workspaces is { Count: > 0 } lived)
        {
            yield return "  Where the memories lived in Second Brain, kept with each one's origin:";
            foreach (var (workspace, label) in new[] { (SecondBrainImport.Personal, "the login's personal workspace"), (SecondBrainImport.Team, "a team workspace"),
                         (SecondBrainImport.Unknown, "not known here (the Second Brain connector does not list it, and the scope map does not name it)") })
            {
                if (!lived.TryGetValue(workspace, out var outcomes)) continue;
                var held = outcomes.TryGetValue("held", out var h) && h > 0 ? $" ({h} of them held for review)" : "";
                yield return $"    {label}: " + string.Join("; ", outcomes.Where(x => x.Key != "held").OrderBy(x => Rank(x.Key)).ThenBy(x => x.Key, StringComparer.Ordinal)
                    .Select(x => $"{x.Value} {Said(x.Key, dry)}" + (x.Key == "added" ? held : "")));
            }
        }
        if (r.HeldFrom.Count > 0)
            yield return "  Held for review, by why: " + string.Join(", ", r.HeldFrom.Select(x =>
            {
                var (from, to) = (x.Key[..x.Key.IndexOf(':')], x.Key[(x.Key.IndexOf(':') + 1)..]);
                return from == "screening" ? $"{x.Value} by Rhythm's own screening ({to}) though Second Brain served it"
                    : from == to ? $"{x.Value} held in Second Brain as {from}" : $"{x.Value} held in Second Brain as {from}, here as {to}";
            })) + ". A person releases one in the app's Memories view or with ./rhythm release, with memory.tools on; until then it stays held.";
        foreach (var (name, n) in r.Notes) yield return "  " + Note(name, n);
        if (r.Review.Count > 0)
        {
            yield return "  For the owner to review before writing:";
            foreach (var line in r.Review) yield return "    - " + line;
        }
        yield return "  Not in the export, and what the import does instead:";
        foreach (var (field, instead) in r.Missing) yield return $"    - {field}: {instead}";
        yield return "  In the export, and what becomes of it:";
        foreach (var (field, instead) in r.NotKept) yield return $"    - {field}: {instead}";
        if (r.Household)
            yield return "  With --household every memory is the household's, its author the importing person (D-048 set aside for this install, D-247), "
                + "and where each lived in Second Brain is kept with it, so per-person scopes can tell a person's own from a team's later.";
        else if (!r.ScopesOn)
            yield return "  With memory scopes off every memory is the household's, and no private one is imported (D-048): only what the scope map names for Family comes in.";
        yield return "Nothing here was overwritten, moved or deleted, and importing the same file again adds nothing"
            + (dry ? ". Add --write to import it." : ".");
    }

    static int Rank(string outcome) => outcome switch
    {
        "added" => 0, "joined" => 1, "present" => 2, "imported_before" => 3, "imported_before_elsewhere" => 3,
        _ when outcome.StartsWith("skipped:", StringComparison.Ordinal) => 4, _ => 5,
    };

    static string Said(string outcome, bool dry) => outcome switch
    {
        "added" => dry ? "would be added" : "added",
        "joined" => dry ? "would join a project here of the same slug" : "joined a project here of the same slug",
        "present" => "already here (left as it is)",
        "imported_before" => "imported before (not again)",
        "imported_before_elsewhere" => "imported before into another scope (not again; listed for review below)",
        "skipped:outside_file" => "skipped: an end the file does not hold, such as another person's private memory (D-122)",
        "skipped:scopes" or "refused:scopes" => "skipped: their ends landed in two scopes, which never link",
        "skipped:end_not_imported" => "skipped: an end that did not come in",
        "skipped:end_elsewhere" => "skipped: an end was imported before into another scope (listed for review below)",
        "skipped:type" => "skipped: not one of Second Brain's eight link types",
        "skipped:duplicate" => "skipped: twice in the file",
        "skipped:invalid" or "refused:invalid" => "skipped: not a record Rhythm takes",
        "skipped:too_large" => "skipped: over 131 072 bytes of text",
        "skipped:window" => "skipped: a validity window that ends before it starts",
        "refused:private_off" => "refused: private, and memory scopes are off (D-048)",
        "refused:forbidden" => "refused: not this person's to write (Family for someone not in it)",
        "refused:not_named" => "refused: in a scope this import does not write (another person's private scope)",
        "refused:ends" => "refused: an end is not a memory here, or is in the trash",
        "refused:kinds" => "refused: decided and follows join two episodic memories only",
        "refused:project" => "refused: their project is not of their scope here",
        "refused:held_project" => "refused: a project here of that name is held for review, and nothing joins one until a person releases it",
        "refused:slug_taken" => "refused: another project of this file took its name here",
        "refused:unknown_author" => "refused: the person is not one of this install's",
        _ => outcome,
    };

    static string Note(string name, int n) => name switch
    {
        "ids_kept_as_their_sha256" => $"{n} id(s) outside the origin map's alphabet are kept there as their SHA-256 (D-124).",
        "ids_over_upstreams_40_bytes" => $"{n} id(s) over upstream's 40-byte bound: each gets a new id here, like every record (D-124).",
        "project_aliases_left_out" => $"{n} project alias(es) left out: another project of the same scope here answers to each already.",
        "project_aliases_not_taken" => $"{n} project alias(es) not taken: the project's own slug here, in a namespace Rhythm's engine sets itself, over 128 characters, or past 16.",
        "projects_held" => $"{n} project(s) came in held for review, their names reading like instructions to an AI client (D-037): listed to no one, and their memories held with them, until a person releases one.",
        "commitments_owed_to_the_person" => $"{n} memor(ies) tagged owed-to-me came in as owed to the person (058), with the other person from their counterparty: tag.",
        "commitments_the_person_owes" => $"{n} memor(ies) with a counterparty: tag came in as owed by the person to that one (058).",
        "membership_refused_project" => $"{n} memor(ies) name a project that was refused: in no project, the project: tag kept beside them.",
        "project_slugs_changed" => $"{n} project slug(s) changed: an underscore as a hyphen, or -2 and on for a slug another project of the same scope in this file took first (D-123).",
        "membership_ambiguous" => $"{n} memor(ies) name a project slug two projects of their scope hold: in no project, the project: tag kept beside them.",
        "membership_elsewhere" => $"{n} memor(ies) name a project that landed in another scope: in no project, the project: tag kept beside them.",
        "membership_unknown" => $"{n} memor(ies) name a project neither the file nor this install has: in no project, the project: tag kept beside them.",
        "capsule_slots_not_taken" => $"{n} capsule tag(s) not taken as a slot (the memory is held, or the slot is not one of Rhythm's): kept beside the memory.",
        "memories_with_upstream_tags" => $"{n} memor(ies) keep their tags in a namespace Rhythm's engine sets itself (standing instructions, decisions, confidences, commitments, holds and the like) beside them, as data nothing reads yet (D-121).",
        "sources_renamed" => $"{n} capture source(s) taken as second-brain: Second Brain's own jobs' (system), or none Rhythm takes.",
        "tags_left_out" => $"{n} tag(s) left out: over 128 characters, with control characters, or past 64 on one memory.",
        "links_added_as_suggested" => $"{n} automatic link(s) came in as suggested: Rhythm draws none again yet, so an edit of either end makes one stale and the link pruning pass removes it 7 days on (D-200, D-203).",
        "imported_before_with_other_text_here" => $"{n} memor(ies) imported before have other text here than in this file: left as they are (an import never overwrites).",
        "status_absent" => $"{n} memor(ies) with no status tag came in as drafts.",
        "family_memories_authored_by_the_person" => $"{n} Family memor(ies) came in with the importing person as their author, since the export names none: "
            + "with memory scopes on, another member who wrote one upstream changes it here only as a Family admin (Q-160).",
        "holds_of_a_reason_upstream_no_longer_writes" => $"{n} memor(ies) tagged quarantine: with a reason upstream no longer writes (review, from before 4.0) are held as instruction (D-120).",
        "created_at_missing" => $"{n} memor(ies) with no creation time are dated when the file was taken.",
        "scope_map_memories_not_in_file" => $"{n} memor(ies) the scope map names are not in this file.",
        "scope_map_projects_not_in_file" => $"{n} project(s) the scope map names are not in this file.",
        _ => $"{n} {name.Replace('_', ' ')}.",
    };
}
