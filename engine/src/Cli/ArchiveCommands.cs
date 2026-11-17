using System.Text;
using System.Text.Json;
using Npgsql;

namespace Knowledge.Cli;

// `rhythm export` and `rhythm import` (cli/README.md, Export and import; docs/dev/memory.md section 9; SB-P3-14): native
// memories in Rhythm's own format (MemoryArchive), on the host, straight against the database with the engine's login,
// as `rhythm family` does. Like `token` and `family` an export reads no rhythm.yaml, and an import only for memory.tools,
// which makes it check what it added for copies and contradiction candidates (068). Each prints counts, never a record's
// title or text; the export file holds the words, so it is written private to the deploy folder's owner.
//
// An export is read as one reader: a person (--person, as a client bound to them reads with memory.scopes on: their own
// private records and, as a member, Family's) or, without --person, the household view (Family only with scopes on).
// With scopes off everything is the household's, and so is every memory. An import names whom it writes for and where:
// --person <id> writes that person's private scope, --family Family (as that person, or as the household view without
// --person); it adds only what that reader may write there, and never overwrites, moves or deletes anything here.
public static class ArchiveCommands
{
    public const string ExportUsage = "export [--person <id>] [--out <file>]";
    public const string ImportUsage = "import <file> [--person <id>] [--family] [--dry-run]";

    // Whom a reader names: a current person, else why not.
    internal static async Task<string?> Refusal(NpgsqlDataSource source, string person, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("SELECT kind,status FROM subjects WHERE id=@p");
        cmd.Parameters.AddWithValue("p", person);
        await using var row = await cmd.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct) || row.GetString(0) != "person") return $"{person} is not a person of this install (people in rhythm.yaml)";
        return row.GetString(1) switch
        {
            "current" => null,
            "suspended" => $"{person} is suspended, so they read and write nothing (./rhythm person unsuspend {person})",
            _ => $"{person} is no longer in people (rhythm.yaml), so they read and write nothing",
        };
    }

    public static async Task<int> Export(DeployFolder deploy, string[] args, TextWriter o, NpgsqlDataSource? store = null, CancellationToken ct = default)
    {
        string? person = null, output = null;
        for (var i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--person" when i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) && person is null: person = args[++i]; break;
                case "--out" when i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) && output is null: output = args[++i]; break;
                default: throw new ArgumentException(ExportUsage);
            }
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var path = Path.GetFullPath(output ?? Path.Combine(deploy.Root, "data", "exports", $"rhythm-memories-{person ?? "household"}-{stamp}.json"));
        // The command runs in a container that sees only the deploy folder (mounted at /work, removed when it ends), so
        // a file anywhere else would be written there and lost with it: refused before anything is read.
        if (!Inside(deploy, path))
        {
            o.WriteLine($"Nothing exported: {output} is outside the deploy folder, and a file there would be lost with the command's "
                + "container. Name one inside it, such as data/exports/alex.json (deploy/compose/data/exports/alex.json).");
            return 1;
        }
        await using var own = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var source = store ?? own!;
        if (person is not null && await Refusal(source, person, ct) is { } refused) { o.WriteLine($"Nothing exported: {refused}."); return 1; }
        if (File.Exists(path)) { o.WriteLine($"Nothing exported: {deploy.Shown(path)} exists already, and an export never overwrites a file."); return 1; }
        // A folder it makes is private to the deploy folder's owner, as the file is.
        if (Path.GetDirectoryName(path) is { } parent && !Directory.Exists(parent))
        {
            Directory.CreateDirectory(parent);
            Owners.Apply(parent, Owners.Person(deploy, Owners.PrivateFolder));
        }
        // Written beside its place first, under a name of its own, created private, and moved there once whole (never over
        // a file that is there by then), so a failed export leaves no file that looks like one. What a failure removes is
        // only the file this run made: never one that was there, such as another export's unfinished file.
        var partial = $"{path}.{Guid.NewGuid():N}.partial";
        var made = false;
        ExportSummary summary;
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = Owners.Private;
            var file = new FileStream(partial, options);
            made = true;
            await using (file)
            await using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
                summary = await new MemoryArchive(source).Export(person, writer, ct);
            File.Move(partial, path);
            Owners.Apply(path, Owners.Person(deploy, Owners.Private));
        }
        catch
        {
            if (made && File.Exists(partial)) File.Delete(partial);
            throw;
        }
        var who = person ?? "the household view";
        o.WriteLine($"Exported what {who} reads to {deploy.Shown(path)}: {summary.Memories} memories ({summary.Trashed} in the trash, "
            + $"{summary.Held} held for review), {summary.Projects} projects and {summary.Links} links.");
        if (summary.PerScope.Count > 0)
            o.WriteLine("  memories by scope: " + string.Join(", ", summary.PerScope.OrderBy(s => s.Key == "Family").ThenBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key} {s.Value}")));
        o.WriteLine(summary.Scopes
            ? person is null ? "With memory scopes on the household view reads Family's records only: no one's private records are in the file."
                : $"With memory scopes on that is {person}'s private records and, as a member, Family's: never another person's private records."
            : "With memory scopes off every memory is the household's, so the file holds every memory, whoever exports it.");
        if (summary.LinksLeftOut > 0)
            o.WriteLine($"  {summary.LinksLeftOut} link(s) to records the file does not hold (another scope's, or a record that is not a memory) were left out.");
        o.WriteLine("The file holds the memories' words: keep it as private as a backup, and delete it once it is imported.");
        return 0;
    }

    public static async Task<int> Import(DeployFolder deploy, string[] args, TextWriter o, NpgsqlDataSource? store = null, CancellationToken ct = default,
        RhythmConfig? config = null)
    {
        string? person = null, file = null;
        bool family = false, dry = false;
        for (var i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--person" when i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) && person is null: person = args[++i]; break;
                case "--family" when !family: family = true; break;
                case "--dry-run" when !dry: dry = true; break;
                case var a when !a.StartsWith("--", StringComparison.Ordinal) && file is null: file = a; break;
                default: throw new ArgumentException(ImportUsage);
            }
        if (file is null || person is null && !family) throw new ArgumentException(ImportUsage + " (name --person, --family or both: the scopes it writes)");
        var path = Path.GetFullPath(file);
        if (!File.Exists(path)) { o.WriteLine($"Nothing imported: there is no file {deploy.Shown(path)}."); return 1; }
        await using var own = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var source = store ?? own!;
        if (person is not null && await Refusal(source, person, ct) is { } refused) { o.WriteLine($"Nothing imported: {refused}."); return 1; }
        ImportReport report;
        try
        {
            await using var stream = File.OpenRead(path);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            report = await new MemoryArchive(source).Import(json, person, person is not null, family, dry, ct);
        }
        catch (Exception e) when (e is JsonException or ArchiveInvalid)
        {
            o.WriteLine($"Nothing imported: {deploy.Shown(path)} is {(e is ArchiveInvalid ? "not one to import: " + e.Message : "not a whole JSON file")}.");
            return 1;
        }
        var scopes = string.Join(" and ", new[] { person is null ? null : $"{person}'s private scope", family ? "Family" : null }.OfType<string>());
        o.WriteLine($"{(dry ? "Dry run: nothing was written. Importing" : "Imported")} {deploy.Shown(path)} as {person ?? "the household view"}, into {scopes}"
            + (report.SameInstall ? " (an export of this install)" : "") + ":");
        foreach (var (kind, plural) in new[] { ("memory", "memories"), ("project", "projects"), ("link", "links") })
        {
            if (!report.Outcomes.TryGetValue(kind, out var outcomes)) { o.WriteLine($"  {plural}: none in the file"); continue; }
            o.WriteLine($"  {plural}: " + string.Join(", ", outcomes.OrderBy(x => Rank(x.Key)).ThenBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => $"{x.Value} {Said(x.Key, dry)}")));
        }
        // Commitments (058) only when the file holds one, so every other import reports as before.
        if (report.Outcomes.TryGetValue("commitment", out var commitments))
            o.WriteLine("  commitments: " + string.Join(", ", commitments.OrderBy(x => Rank(x.Key)).ThenBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => $"{x.Value} {Said(x.Key, dry)}")));
        // Decisions (060) likewise, only when the file holds one.
        if (report.Outcomes.TryGetValue("decision", out var decisions))
            o.WriteLine("  decisions: " + string.Join(", ", decisions.OrderBy(x => Rank(x.Key)).ThenBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => $"{x.Value} {Said(x.Key, dry)}")));
        // Due dates (061) likewise, only when the file holds one.
        if (report.Outcomes.TryGetValue("due", out var dues))
            o.WriteLine("  due dates: " + string.Join(", ", dues.OrderBy(x => Rank(x.Key)).ThenBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => $"{x.Value} {Said(x.Key, dry)}")));
        // Standing instructions (065) likewise.
        if (report.Outcomes.TryGetValue("standing", out var standing))
            o.WriteLine("  standing instructions: " + string.Join(", ", standing.OrderBy(x => Rank(x.Key)).ThenBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => $"{x.Value} {Said(x.Key, dry)}")));
        // Work items (079) likewise.
        if (report.Outcomes.TryGetValue("work", out var work))
            o.WriteLine("  work items: " + string.Join(", ", work.OrderBy(x => Rank(x.Key)).ThenBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => $"{x.Value} {Said(x.Key, dry)}")));
        if (report.VersionsKept + report.VersionsLeftOut > 0)
            o.WriteLine($"  earlier versions: {report.VersionsKept} {(dry ? "would be kept" : "kept")}"
                + (report.VersionsLeftOut > 0 ? $", {report.VersionsLeftOut} left out (from before a scope its reader does not read)" : ""));
        if (report.AliasesLeftOut > 0) o.WriteLine($"  {report.AliasesLeftOut} project alias(es) left out: another project here answers to them.");
        if (report.HeldHere > 0)
            o.WriteLine($"  {report.HeldHere} memor{(report.HeldHere == 1 ? "y" : "ies")} {(dry ? "would be" : report.HeldHere == 1 ? "is" : "are")} held for review here: words that read like "
                + "instructions to an AI client. A person releases one in the app's Memories view or with ./rhythm release, with memory.tools on; until then it stays held.");
        if (!report.Scopes && !family)
            o.WriteLine("  With memory scopes off every memory is the household's, and no private one is imported (D-048): name --family to import them.");
        // With memory.tools on, what it added is checked for copies and contradiction candidates (068); rhythm.yaml is read for
        // that switch alone, and off or unreadable this prints as before.
        if (!dry) await MemoryCommands.ReviewImported(source, report.Added ?? [], "rhythm import", config, o, ct);
        o.WriteLine("Nothing here was overwritten, moved or deleted, and importing the same file again adds nothing.");
        return 0;
    }

    // Whether a path is a file inside the deploy folder (not the folder itself, nor anything above or beside it).
    internal static bool Inside(DeployFolder deploy, string path)
    {
        var relative = Path.GetRelativePath(deploy.Root, Path.GetFullPath(path));
        return relative != "." && !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith("../", StringComparison.Ordinal);
    }

    static int Rank(string outcome) => outcome switch
    {
        "added" => 0, "name_left_out" => 1, "joined" => 1, "present" => 2, "imported_before" => 3, "deleted_here" => 4, "expired" => 5, "not_named" => 6,
        _ => 7,
    };

    // How an outcome reads in the report.
    static string Said(string outcome, bool dry) => outcome switch
    {
        "added" => dry ? "would be added" : "added",
        "joined" => dry ? "would join a project here of the same name" : "joined a project here of the same name",
        "present" => "already here (left as it is)",
        "imported_before" => "imported before and gone since (not brought back)",
        "deleted_here" => "deleted here (not brought back)",
        "expired" => "past their time in the trash (left out)",
        "not_named" => "in scopes this import does not name (left out)",
        "refused:forbidden" => "refused: not this reader's to write (another person's Family record, or Family for someone not in it)",
        "refused:private_off" => "refused: private, and memory scopes are off (D-048)",
        "refused:unknown_author" => "refused: written by someone who is not a person here",
        "refused:project" => "refused: their project did not come in",
        "refused:ends" => "refused: their ends are not memories here in one scope",
        "refused:id_taken" => "refused: another record here has their id",
        "refused:invalid" => "refused: not valid records",
        "name_left_out" => "of them without the other person's name, which read like instructions to an AI client",
        _ => outcome,
    };
}
