using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace Knowledge;

// The two audit trails (SB-P3-04; docs/dev/memory.md sections 4 and 5). The memory audit trail (memory_events, migration
// 025) has one row per change to a memory or a project, written by the checked writers with the principal, the channel
// and the client (MemoryCall). The admin audit (admin_events, migration 037) has one row per setup change that decides
// who reads what: Family's members and admins, source shares, the tokens people are given, a person's status and the
// memory.scopes switch. Triggers on those tables write it, so every writer is audited; a writer only names itself for
// its transaction (As). Both trails are insert-only, for the owner too, and the engine's login only reads them.
//
// The setup trail is served nowhere yet (an admin view is SB-P6-01's); history serves memory_events, as a memory's own
// history (MemoryHistory). Naming a writer changes no answer.

// Who a setup change is recorded as made by: the operator on the host (Host, channel cli), the app's server for one of
// its logins (App), or the engine itself with its job (Start, the start's people and switch; AppToken, the app token's
// file taken back every minute; Lifetime, viewer sessions ended at the end of their 30 days).
public sealed record AdminActor(string Channel, Guid? Principal = null, string? Job = null)
{
    public static readonly AdminActor Host = new("cli");
    public static readonly AdminActor Start = new("system", Job: "start");
    public static readonly AdminActor AppToken = new("system", Job: "app-token");
    public static readonly AdminActor Lifetime = new("system", Job: "viewer-lifetime");
    public static AdminActor App(Guid login) => new("app", login);
}

public static partial class AdminAudit
{
    // Names the writer of every setup change the rest of this transaction makes: the settings migration 037's triggers
    // read, set for the transaction only (outside one they would last one statement, so it takes one). Plain settings,
    // so a database not migrated to 037 yet takes them and ignores them.
    public static async Task As(NpgsqlConnection c, NpgsqlTransaction tx, AdminActor actor, CancellationToken ct)
    {
        if (actor.Channel is not ("cli" or "app" or "mcp" or "system") || (actor.Channel is "app" or "mcp") != (actor.Principal is not null)
            || actor.Job is not null && (actor.Channel != "system" || !JobPattern().IsMatch(actor.Job)))
            throw new ArgumentException("An audit names a channel (cli, app, mcp or system), a login for app and mcp only, and a job for system only");
        await using var cmd = Database.Command(c, tx, """
            SELECT set_config('rhythm.audit_channel',@channel,true),set_config('rhythm.audit_actor',@actor,true),
              set_config('rhythm.audit_job',@job,true)
            """, ("channel", actor.Channel), ("actor", actor.Principal?.ToString() ?? ""), ("job", actor.Job ?? ""));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    [GeneratedRegex("^[a-z][a-z0-9_.-]{0,39}$")] private static partial Regex JobPattern();

    // An AI client's name as the memory audit trail records it, from what the client says of itself (the MCP
    // initialize's clientInfo name, which the tools of SB-P3-09 pass): control and formatting characters (direction
    // controls and zero-width characters included) and angle brackets removed, white space folded to one space, at most
    // MaxClient characters, and none when nothing is left. A name is a label, never a reason to refuse a write, so it is
    // cleaned rather than refused (as Second Brain's client label is).
    public const int MaxClient = 80;

    public static string? Client(string? name)
    {
        if (name is null) return null;
        var text = new StringBuilder(name.Length);
        var space = false;
        foreach (var rune in name.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsWhiteSpace(rune) || category is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                space = text.Length > 0;
                continue;
            }
            if (Rune.IsControl(rune) || category is UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
                || rune.Value is '<' or '>') continue;
            if (space) { text.Append(' '); space = false; }
            text.Append(rune.ToString());
        }
        var clean = text.ToString();
        if (clean.Length == 0) return null;
        // Cut at MaxClient characters as the database counts them (code points), never inside one.
        var runes = clean.EnumerateRunes().Take(MaxClient + 1).ToList();
        return runes.Count <= MaxClient ? clean : string.Concat(runes.Take(MaxClient).Select(r => r.ToString())).TrimEnd();
    }
}
