using System.Globalization;
using Npgsql;

namespace Knowledge.Cli;

// `rhythm night` (SB-P5-14, INT-18; cli/README.md, What the daily jobs did): the night summary the daily jobs keep with
// engine.nightSummary on (NightlyRunner, migration 050's night_runs), the latest night kept or the one named, a line a
// job: its outcome, a failure's kind, its time against its budget and what it did, in counts. Kinds and counts only,
// never a record's words. With engine.nightSummary off, absent or empty it answers exactly as a command the engine does
// not know, before it reads its arguments or the database, and help is the list it was.
public static class NightCommand
{
    public const string Usage = "night [--night <YYYY-MM-DD>]";

    // Whether this install offers it: rhythm.yaml (RHYTHM_CONFIG) says engine.nightSummary on.
    public static bool Offered(RhythmConfig config) => NightlySettings.From(config).Summary;

    public static async Task<int> Run(string[] args, TextWriter o, NpgsqlDataSource? store = null, RhythmConfig? config = null, CancellationToken ct = default)
    {
        if (!Offered(MemoryCommands.Settings(config))) throw new ArgumentException(MemoryCommands.UnknownCommand);
        DateOnly? night = args switch
        {
            [] => null,
            ["--night", var day] when DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) => d,
            _ => throw new ArgumentException(Usage + " (a night is the install's date, as 2026-11-03)"),
        };
        await using var own = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var rows = await NightSummary.Read(store ?? own!, night, ct);
        if (rows.Count == 0 && night is { } named)
        {
            o.WriteLine($"No night summary is kept for {named.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}: the daily jobs keep the last {NightlyRunner.KeptNights} nights.");
            return 1;
        }
        foreach (var line in NightSummary.Lines(rows)) o.WriteLine(line);
        return 0;
    }
}
