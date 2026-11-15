using Npgsql;

namespace Knowledge.Cli;

// `rhythm person offboard <id>` (SB-P7-13; migration 072, Core/Offboarding.cs; docs/dev/memory.md section 4): the
// operator's step on the host when someone leaves the household for good, straight against the database with the engine's
// login, as `person suspend` is. It first says what it does, what it leaves and what it does not end (tokens bound to no
// person, the app, adapters and plugins reading their accounts), as counts and names, never a title, and changes
// nothing unless the operator types the person's id. Going on with an offboarding stopped part way asks nothing: the
// decision was taken. Once the person was brought back since (072's returned_at), it shows what it does and asks again,
// whatever their status now: a suspension since is not that decision. Like `person` and `token` it does not read
// rhythm.yaml, so it works while that is broken.
public static class OffboardCommand
{
    public const string Usage = "person offboard <id>";

    public static async Task<int> Run(string[] args, TextReader input, TextWriter o, NpgsqlDataSource? store = null,
        int batch = Offboardings.Batch, int? batches = null, CancellationToken ct = default)
    {
        if (args is not [var person] || person.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException(Usage);
        await using var own = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var offboardings = new Offboardings(store ?? own!);
        var plan = await offboardings.Plan(person, ct);
        if (plan.Status != "current" && plan.Offboarding is { FinishedAt: { } done, ReturnedAt: null } && plan.Private + plan.InTrash + plan.Projects == 0)
        {
            o.WriteLine($"{person} was offboarded on {done:yyyy-MM-dd}: none of their private memories or memory projects is left, and nothing changed now.");
            return 0;
        }
        if (plan.Offboarding is { FinishedAt: null, ReturnedAt: null } going && plan.Status != "current")
            o.WriteLine($"Going on with {person}'s offboarding, started {going.StartedAt:yyyy-MM-dd}, from where it stopped.");
        else
        {
            foreach (var line in Preview(plan)) o.WriteLine(line);
            o.Write($"Type {person} to offboard them; anything else changes nothing: ");
            if (input.ReadLine()?.Trim() != person)
            {
                o.WriteLine();
                o.WriteLine($"Nothing changed: {person} is as they were.");
                return 1;
            }
        }

        var started = await Start(offboardings, person, ct);
        o.WriteLine(Ended(person, started));
        if (plan.Listed)
            o.WriteLine($"{person} is still listed in people (rhythm.yaml): take them out and run ./rhythm up, or they stay listed, suspended."
                // The start refuses an adapter whose owner is not in people (RhythmConfig), and this command does not read
                // rhythm.yaml, so it says so whenever records of theirs from an adapter or plugin are here.
                + (plan.Own.Count > 0 ? $" Give any adapter whose owner is {person} another owner first, or ./rhythm up stops on it." : ""));
        if (started.Family == "admin" && !await offboardings.Admins(ct))
            o.WriteLine("Family has no admin now: a Family memory can then be changed only by its author (docs/dev/memory.md section 4).");
        var moved = await Move(offboardings, person, batch, batches, ct);
        if (moved.Trashed > 0)
            o.WriteLine($"Moved {moved.Trashed} private memories to the trash: nothing is deleted yet, and the trash keeps them until {moved.PurgeAfter:yyyy-MM-dd}.");
        if (moved.Left > 0 && !moved.Paused)
        {
            o.WriteLine($"Stopped with {moved.Left} private memories still out of the trash. Their access has ended already; "
                + $"./rhythm person offboard {person} goes on from here.");
            return 1;
        }
        var closed = moved.Paused ? null : await offboardings.Close(person, false, ct);
        if (closed is null or { Paused: true })
        {
            o.WriteLine($"{person} was brought back meanwhile, so their offboarding waits: ./rhythm person offboard {person} goes on with it, "
                + "after showing what it does and asking again.");
            return 1;
        }
        if (closed.Projects > 0) o.WriteLine($"Deleted {closed.Projects} private project(s) with no memory left in them.");
        o.WriteLine(closed.Finished
            ? $"Finished: none of {person}'s private memories or memory projects is left."
            : $"The rest finishes on its own: the {closed.Waiting} memories of theirs in the trash wait there until {closed.PurgeAfter:yyyy-MM-dd}, "
              + "when the daily purge deletes them for good" + (closed.ProjectsLeft > 0 ? $", and then the daily jobs delete their {closed.ProjectsLeft} private project(s)" : "")
              + " and record the offboarding finished.");
        return 0;
    }

    // The first step, as the operator (072's offboard_person): the access bound to the person ends at once.
    internal static async Task<OffboardStarted> Start(Offboardings store, string person, CancellationToken ct)
    {
        using var doc = await store.Call("SELECT offboard_person(@p)::text", AdminActor.Host, ct, ("p", person));
        var o = doc.RootElement;
        return new(o.GetProperty("was").GetString()!, o.GetProperty("status").GetString()!, Offboardings.Int(o, "tokens"),
            Offboardings.Int(o, "codes"), o.GetProperty("family").ValueKind == System.Text.Json.JsonValueKind.String ? o.GetProperty("family").GetString() : null,
            Offboardings.Int(o, "shares"), o.GetProperty("started").GetBoolean(), o.GetProperty("started_at").GetDateTimeOffset(),
            o.GetProperty("scopes").GetString()!);
    }

    // One batch of at most `limit` of the person's private memories into the trash, as the operator (offboard_trash).
    internal static async Task<OffboardBatch> Trash(Offboardings store, string person, int limit, CancellationToken ct)
    {
        using var doc = await store.Call("SELECT offboard_trash(@p,@limit)::text", AdminActor.Host, ct, ("p", person), ("limit", limit));
        var o = doc.RootElement;
        if (o.GetProperty("paused").GetBoolean()) return new(0, 0, null, true);
        return new(Offboardings.Int(o, "trashed"), o.GetProperty("left").GetInt64(), Offboardings.When(o, "purge_after"), false);
    }

    // The batches one after another until none of the person's private memories is left out of the trash, at most `batches`
    // of them when given (the command's own bound, or a test's stop), each its own transaction, so a run stopped anywhere
    // has moved whole batches only and the next run goes on from there. A batch that moved nothing with some left (one
    // deleted for good meanwhile) ends the run too.
    internal static async Task<OffboardMoved> Move(Offboardings store, string person, int batch, int? batches, CancellationToken ct)
    {
        int moved = 0, runs = 0;
        DateTimeOffset? until = null;
        long left = 0;
        while (batches is not { } most || runs < most)
        {
            var one = await Trash(store, person, batch, ct);
            runs++;
            if (one.Paused) return new(moved, left, until, true, false);
            moved += one.Trashed;
            until = one.PurgeAfter ?? until;
            left = one.Left;
            if (left == 0 || one.Trashed == 0) return new(moved, left, until, false, left > 0);
        }
        return new(moved, left, until, false, left > 0);
    }

    // What it does and what it leaves, before the operator decides.
    static IEnumerable<string> Preview(OffboardPlan p)
    {
        var who = p.Person;
        var again = p.Offboarding is not null && (p.Status == "current" || p.Returned) ? $" (offboarded since {p.Offboarding.StartedAt:yyyy-MM-dd}, and brought back)" : "";
        yield return $"Offboarding {who}{again} ends the access bound to them at once and keeps what the household shares; the last lines say what it does not end.";
        if (!p.On)
            yield return p.Private == 0
                ? $"Memory scopes are off: the household shares everything, so nothing is private to {who} and nothing goes to the trash "
                  + "(every memory is the household's, docs/dev/memory.md section 5)."
                : "Memory scopes are off: the household shares everything.";
        yield return "What it does now:";
        var status = p.Status switch
        {
            "current" => "suspended",
            "suspended" => "already suspended",
            _ => "former already (no longer in people)",
        };
        yield return $"  nothing bound to {who} reads or writes from now on: {status}, {p.Tokens} token(s) and session(s) bound to them disabled for good, "
            + (p.Family is null ? "not in Family" : p.Family == "admin" ? "out of Family with their admin role" : "out of Family")
            + $", {p.Shares} source share(s) ended{(p.On ? "" : $" (with memory scopes off, Family and shares change what anyone reads only once they are on, and the first start with them on leaves {who} out of Family, brought back or not, until ./rhythm family add {who})")}.";
        // Brought back, the person is not put back into Family, nor are their source shares (072 ended both, and neither
        // unsuspend nor the first switch-on of scopes gives them back), so the way back names the steps that do.
        if (p.On || p.Private > 0)
            yield return $"  Their {p.Private} private memories go to the trash, as a forget puts one there: nothing is deleted now. The trash keeps them "
                + $"14 days, then the daily purge deletes them for good{(p.Projects > 0 ? $", and their {p.Projects} private project(s) with them" : "")}. "
                + $"Brought back in that time ({BringBack(p)}), {who} restores what they want with a new token, and stays out of Family until "
                + $"./rhythm family add {who}{(p.Shares > 0 ? $", their source shares ended until ./rhythm share add {who} <provider>" : "")}.";
        yield return "What it leaves:";
        yield return p.On
            ? $"  the {p.FamilyWritten} Family memories {who} wrote, in Family with {who} as their author;"
            : $"  the {p.FamilyWritten} memories {who} wrote, the household's, read by everyone as today with {who} as their author;";
        var records = p.Own.Count == 0 ? "none now" : string.Join(", ", p.Own.Select(r => $"{MemoryCommands.Shown(r.Provider, 64)} {r.Records}"));
        yield return p.On
            ? $"  their own records from adapters and plugins ({records}), kept as their source keeps them: nobody reads them with memory "
              + $"scopes on while {who} is suspended or former;"
            : $"  their own records from adapters and plugins ({records}), kept as their source keeps them and read by everyone as today;";
        if (p.Saved.Count > 0)
        {
            var saved = string.Join(", ", p.Saved.Select(r => $"{MemoryCommands.Shown(r.Provider, 64)} {r.Records}"));
            yield return p.On
                ? $"  the records of theirs saved in Rhythm itself ({saved}: save_document copies, uploads and dashboard log entries), kept "
                  + $"as they are: nobody reads them with memory scopes on while {who} is suspended or former;"
                : $"  the records of theirs saved in Rhythm itself ({saved}: save_document copies, uploads and dashboard log entries), kept "
                  + "as they are and read by everyone as today;";
        }
        if (p.Insights + p.Dashboards > 0)
            yield return $"  their {p.Insights} private insight(s) and {p.Dashboards} private dashboard(s), which nobody reads with memory scopes on either;";
        if (p.Recalls > 0)
            yield return $"  the recall log's {p.Recalls} row(s) of their questions as they asked them (engine.recallLog), kept in their own scope "
                + "until the daily prune deletes each 30 days after it was asked;";
        yield return "  both audit trails, which hold no words.";
        // What ends only by the operator's own step: nothing ties these to the person, so the offboarding cannot end them.
        yield return "What it does not end:";
        if (p.Unbound.Count > 0)
            yield return $"  {p.Unbound.Count} token(s) bound to no person, which stay enabled ({string.Join(", ", p.Unbound.Take(10).Select(n => MemoryCommands.Shown(n, 64)))}"
                + (p.Unbound.Count > 10 ? $" and {p.Unbound.Count - 10} more: ./rhythm token list" : "")
                + $"): if {who} used one, ./rhythm token revoke <name>, and make whoever else used it a new one;";
        yield return $"  the app, which has no login of its own: whoever reaches it reads what the household reads, so keep {who} off the network it listens on;";
        if (p.Own.Count > 0)
            yield return $"  an adapter or plugin reading {who}'s own account ({string.Join(", ", p.Own.Select(r => MemoryCommands.Shown(r.Provider, 64)))}) keeps "
                + "adding records until you disconnect it: ./rhythm disconnect <adapter>, or for a plugin its own settings or ./rhythm token revoke "
                + $"plugin:<name>, which stops it for everyone; and give an adapter whose owner is {who} another owner before you take {who} out of "
                + "people, or ./rhythm up stops on it.";
    }

    // How the person is brought back within the grace: unsuspended while the engine's last start found them in people; a
    // former person listed in people again (the start makes them current); a suspended person taken out of people listed
    // again first, since lifting the suspension of someone not listed makes them former (Principals.Unsuspend).
    static string BringBack(OffboardPlan p) => p.Status == "former"
        ? "listed in people again and ./rhythm up"
        : p.Listed ? $"./rhythm person unsuspend {p.Person}" : $"listed in people again, ./rhythm up, then ./rhythm person unsuspend {p.Person}";

    // What the first step did.
    static string Ended(string person, OffboardStarted s)
    {
        var parts = new List<string>
        {
            s.Was == "current" ? $"{person} is suspended" : $"{person} stays {s.Was}",
            $"{s.Tokens} token(s) and session(s) disabled",
            s.Family is null ? "not in Family" : s.Family == "admin" ? "out of Family (an admin before)" : "out of Family",
            $"{s.Shares} source share(s) ended",
        };
        if (s.Codes > 0) parts.Add($"{s.Codes} one-time viewer code(s) removed");
        return $"Done at once: {string.Join("; ", parts)}.";
    }
}
