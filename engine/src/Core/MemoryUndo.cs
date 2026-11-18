using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// Undo for native memories (docs/dev/memory.md section 5, undo a change; SB-P3-05): one memory's newest change, the
// memory back as it was at an earlier revision, or a group of an AI client's changes at once. An undo is a change of its
// own (migration 041's undo_memory: a new revision, the replaced state kept as a version, one audit row), so an undo is
// undone the same way. Who may undo is section 5's rule: with scopes off every admitted login, with them on a private
// memory's owner, a Family memory's author or a Family admin, any reader of a Family memory no person wrote, and anyone
// their own newest changes. An undo never releases a hold for an AI client (D-039).
//
// A group (adapted from Second Brain's change groups, src/brief/changes.ts at the pinned commit): one login's changes of
// one kind (edits, status changes, releases, undos, loops resolved, outcomes recorded, due dates moved or work items changed) through one
// channel and client, each within ten minutes of the one before, as the reader sees them on the audit trail. Its key is
// opaque to a client and names only what the reader
// already reads: the memory and revision of its first and last change. UndoGroup finds the group's members again from
// that key at every call, as the caller reads the trail then, so a key never selects a change the caller does not see
// and a client cannot smuggle ids into it; each member is undone only while the group's changes are still its newest,
// GroupPage memories undone a call, the rest left for the next call. A member the caller may not undo, or whose undo
// writes nothing, takes none of a call's GroupPage, so it never keeps a call from reaching the members after it.
//
// The undo tool (SB-P3-09) and the app's Memories view (SB-P3-12, one memory's undo) call it, only with memory.tools on;
// the brief's AI changes (SB-P5-08, MemoryBrief.cs) read Changes with it on too, and the Memories view reads them and undoes a
// group (app_memories changes and undo_group, SB-P6-01), so with it off every existing answer is as it was.

// One change in a group: the memory, the revision the change wrote, its kind (appended, updated, status_changed, released,
// reverted, resolved, outcome, due_changed, standing_stopped, capsule_changed or work_changed) and when.
public sealed record MemoryChangeItem(Guid Memory, int Revision, string Event, DateTimeOffset At);

// A group as one reader sees it. Group: the key UndoGroup takes. Family: edit (appended, updated), status, release,
// revert, resolve, outcome, due, standing, capsule or work. Person: who made them, the person the login is bound to, or none (a login with no person). Changes and
// Memories: how many of each the reader sees; Capped: more memories than one group undo takes (GroupMax), so it undoes the
// oldest GroupMax. Items newest first. No words of any memory: a held memory's change is listed as any other's.
public sealed record MemoryChangeGroup(string Group, string Family, string? Person, string Channel, string? Client, int Changes,
    int Memories, DateTimeOffset From, DateTimeOffset Until, bool Capped, IReadOnlyList<MemoryChangeItem> Items);

// The groups of changes since a time, newest first. Truncated: more changes than one read takes (ChangesRead) were
// made since, so the oldest are left out.
public sealed record MemoryChanges(IReadOnlyList<MemoryChangeGroup> Groups, bool Truncated, DateTimeOffset Since);

// What a group undo did to one member: undone, unchanged (nothing to write), changed_since (it changed after the group,
// so it is left as it is), or the undo's refusal (forbidden, unknown, not_visible, pruned, moved, needs_person).
public sealed record MemoryGroupResult(Guid Memory, string Result);

// One call's work on a group. Total: its members (at most GroupMax memories); Remaining: members this call did not reach,
// for the next call with the same key; Done: none left. A member already undone is passed over without a result. Redo:
// the key of this group's undos by this caller, which undoes them and nothing else it undid; null when this call undid nothing.
public sealed record MemoryGroupUndo(string Group, int Total, int Remaining, bool Done, bool Capped, IReadOnlyList<MemoryGroupResult> Results,
    string? Redo);

public sealed partial class MemoryStore
{
    public static readonly TimeSpan GroupGap = TimeSpan.FromMinutes(10), ChangesWindow = TimeSpan.FromHours(48);
    public const int GroupMax = 50, GroupPage = 5, ChangesRead = 200;
    // Bound the audit rows read for a group independently of how many distinct wordings its memories retain.
    // A key spanning more names no group (list the changes again).
    public const int GroupRead = GroupMax * 20;

    // The changes an undo reverses, each a revision of its own. A memory's writing is no change to undo (forgetting it is
    // the trash's, SB-P3-06), and neither is a share (SB-P3-10). A loop's resolve (058, SB-P5-09) is one, and so is a
    // decision's outcome (060, SB-P5-11), and a snooze or clear of a due date (061, SB-P5-10); a due date written with its
    // memory (due_set) is part of its writing; and so is a standing instruction's mark (standing_set), whose stop (065,
    // SB-P5-12) is a change. A capsule slot set or cleared (039) is one too: through an AI client's channel there is one
    // only with memory.capsuleWrites on (078), and it is listed among that client's changes so a person sees it and can
    // undo it; an AI client's own undo of it stays refused (041). A work item's change (079, with memory.work on) is one:
    // its state, type or priority changed, or an existing memory made a work item; properties written with a new memory
    // (work_set) are part of its writing.
    public static readonly IReadOnlyList<string> Undoable =
        ["appended", "updated", "status_changed", "released", "reverted", "resolved", "outcome", "due_changed", "standing_stopped", "capsule_changed",
         "work_changed"];

    public static string Family(string change) => change switch
    {
        "appended" or "updated" => "edit",
        "status_changed" => "status",
        "released" => "release",
        "reverted" => "revert",
        "resolved" => "resolve",
        "outcome" => "outcome",
        "due_changed" => "due",
        "standing_stopped" => "standing",
        "capsule_changed" => "capsule",
        "work_changed" => "work",
        _ => change,
    };

    // The memory back as it was at revision `to`, or before its newest change when `to` is null. Expected: the revision
    // the caller read; a stale one is refused (conflict), which makes a retry safe, since an undo takes no idempotency key
    // (a second undo would undo the first). An undo that brings back other words, another kind or no hold draws its links
    // on save again (SB-P4-03), as a change does.
    public async Task<MemoryWrite> Undo(Guid principal, Guid memory, int? to, int? expected, MemoryCall call, CancellationToken ct)
    {
        if (call.Key is not null)
            throw new MemoryRefused("invalid", "An undo takes no idempotency key: name the revision you read (expected) to make a retry safe");
        var write = await Write("SELECT undo_memory(@principal,@id,@to,@expected,@channel,@client)::text", principal, call, [], ct,
            ("id", NpgsqlDbType.Uuid, memory), ("to", NpgsqlDbType.Integer, to), ("expected", NpgsqlDbType.Integer, expected));
        return await Linked(principal, write, Words(write), call, ct);
    }

    // The groups of changes made through a channel (mcp: an AI client's) since a time (48 hours ago when none), as the
    // caller reads the audit trail. NotHeld (the brief, SB-P5-08): leaves out the changes of a memory held now, so the brief
    // counts no held row; Scope (the brief's workspace): only the changes of memories of that scope. A change left out still
    // ends a group (Runs): UndoGroup reads all of a key's span, so a group running across it would undo what this answer never
    // counted. One read takes the newest ChangesRead changes, left out or not, and Truncated says more were made.
    public async Task<MemoryChanges> Changes(Guid principal, DateTimeOffset? since, string channel, CancellationToken ct, bool notHeld = false, string? scope = null)
    {
        string? named;
        try { named = MemorySettings.Named(scope); }
        catch (ArgumentException e) { throw new MemoryRefused("invalid", e.Message); }
        var from = (since ?? DateTimeOffset.UtcNow - ChangesWindow).ToUniversalTime();
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var shown = (notHeld ? "m.hold IS NULL" : "true") + (named is null ? "" : " AND m.scope=@scope");
        var rows = await ChangeRows(c, tx, principal, "e.channel=@channel AND e.at>=@since", shown, "DESC", ChangesRead + 1, ct,
            ("channel", channel), ("since", from), ("scope", named));
        await tx.CommitAsync(ct);
        var truncated = rows.Count > ChangesRead;
        var groups = Runs(rows.Take(ChangesRead)).OrderByDescending(r => r[^1].Id).Select(Describe).ToList();
        return new(groups, truncated, from);
    }

    // Undo a group's changes, GroupPage memories a call. Each member is checked again first: undone only while the
    // group's changes are its newest, back to the revision before the first of them; one changed since is left as it is.
    // Only an undo that writes counts towards GroupPage: a member refused (forbidden, pruned, not_visible, moved) or whose
    // undo changes nothing is reported and passed, so every call reaches further and the group is Done once each member
    // has been tried, at most GroupMax undos a call.
    public async Task<MemoryGroupUndo> UndoGroup(Guid principal, string group, MemoryCall call, CancellationToken ct)
    {
        if (call.Key is not null) throw new MemoryRefused("invalid", "An undo takes no idempotency key: undoing a group again passes over what is undone");
        var members = await Members(principal, group, ct) ?? throw new MemoryRefused("unknown", "Unknown group: list the changes again for a current one");
        var results = new List<MemoryGroupResult>();
        MemoryWrite? newest = null;
        int acted = 0, i = 0;
        for (; i < members.Items.Count && acted < GroupPage; i++)
        {
            var member = members.Items[i];
            if (member.Verdict == "done") continue;
            if (member.Verdict != "pending") { results.Add(new(member.Memory, member.Verdict)); continue; }
            try
            {
                var undone = await Undo(principal, member.Memory, member.To, member.Tip, call, ct);
                results.Add(new(member.Memory, undone.Unchanged ? "unchanged" : "undone"));
                if (!undone.Unchanged)
                {
                    newest = undone;
                    acted++;
                }
            }
            catch (MemoryRefused refused) { results.Add(new(member.Memory, refused.Code == "conflict" ? "changed_since" : refused.Code)); }
        }
        var remaining = members.Items.Count - i;
        var redo = newest is null ? null : await RedoKey(principal, call, newest, members.Items, ct);
        return new(group, members.Items.Count, remaining, remaining == 0, members.Capped, results, redo);
    }

    // A change as the trail records it, for grouping. Shown: the answer counts it; one it leaves out only ends a group (Runs).
    internal sealed record ChangeRow(long Id, Guid Memory, int Revision, string Event, DateTimeOffset At, Guid Principal, string? Person,
        string Channel, string? Client, bool Shown = true);

    // The changes a reader sees: of the kinds an undo reverses, made by a login (the operator's on the host have none), on
    // a memory it reads now (the read predicate on its own row; none in the trash), from the revision its history starts
    // at for this reader (034's floor). `where` narrows them; `order` is by the trail's own order.
    static Task<List<ChangeRow>> ChangeRows(NpgsqlConnection c, NpgsqlTransaction tx, Guid principal, string where, string order, int take,
        CancellationToken ct, params (string, object?)[] args) => ChangeRows(c, tx, principal, where, "true", order, take, ct, args);

    // The same, and `shown` says of each row whether the answer counts it.
    static async Task<List<ChangeRow>> ChangeRows(NpgsqlConnection c, NpgsqlTransaction tx, Guid principal, string where, string shown, string order, int take,
        CancellationToken ct, params (string, object?)[] args)
    {
        await using var cmd = Database.Command(c, tx, $"""
            SELECT e.id,e.memory_id,e.revision,e.event,e.at,e.principal_id,q.person_id,e.channel,e.client,({shown})
            FROM memory_events e
            JOIN memories m ON m.id=e.memory_id
            JOIN resources r ON r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL AND r.external_id=m.id::text
            LEFT JOIN principals q ON q.id=e.principal_id
            WHERE e.principal_id IS NOT NULL AND e.revision IS NOT NULL AND e.event=ANY(@events) AND {where}
              AND {Database.Visible("r")} AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id)
              AND e.revision>=memory_history_floor(@principal,m.id)
            ORDER BY e.id {order} LIMIT @take
            """, [("principal", principal), ("events", Undoable.ToArray()), ("take", take), .. args]);
        var rows = new List<ChangeRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new(r.GetInt64(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4), r.GetGuid(5),
                Text(r, 6), r.GetString(7), Text(r, 8), r.GetBoolean(9)));
        return rows;
    }

    // The groups among changes: each login's changes of one kind through one channel and client, in the trail's order, a
    // group ending where the next such change came more than GroupGap after the one before it, or where a change the answer
    // leaves out came, so a key's span holds only what its group counts.
    internal static List<List<ChangeRow>> Runs(IEnumerable<ChangeRow> rows)
    {
        var runs = new List<List<ChangeRow>>();
        var open = new Dictionary<(Guid, string, string, string), List<ChangeRow>>();
        foreach (var row in rows.OrderBy(r => r.Id))
        {
            var key = (row.Principal, row.Channel, row.Client ?? "", Family(row.Event));
            if (!row.Shown)
            {
                open.Remove(key);
                continue;
            }
            if (open.TryGetValue(key, out var run) && row.At - run[^1].At <= GroupGap) run.Add(row);
            else
            {
                run = [row];
                runs.Add(run);
                open[key] = run;
            }
        }
        return runs;
    }

    static MemoryChangeGroup Describe(List<ChangeRow> run)
    {
        var memories = run.Select(r => r.Memory).Distinct().Count();
        return new(Key(run[0], run[^1]), Family(run[0].Event), run[0].Person, run[0].Channel, run[0].Client, run.Count, memories,
            run[0].At, run[^1].At, memories > GroupMax,
            [.. Enumerable.Reverse(run).Select(r => new MemoryChangeItem(r.Memory, r.Revision, r.Event, r.At))]);
    }

    // A group's key: its first and last change, by memory and revision, which the reader read already; base64url, so a
    // client passes it on as it was given and does not build one.
    internal static string Key(ChangeRow first, ChangeRow last) =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes($"v1.{first.Memory:N}.{first.Revision}.{last.Memory:N}.{last.Revision}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [GeneratedRegex(@"^v1\.([0-9a-f]{32})\.([1-9][0-9]{0,8})\.([0-9a-f]{32})\.([1-9][0-9]{0,8})$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyShape();

    internal static ((Guid Memory, int Revision) First, (Guid Memory, int Revision) Last)? Decode(string? group)
    {
        if (string.IsNullOrWhiteSpace(group) || group.Length > 200 || group.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')))
            return null;
        string text;
        try
        {
            var b64 = group.Replace('-', '+').Replace('_', '/');
            text = Encoding.ASCII.GetString(Convert.FromBase64String(b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=')));
        }
        catch (FormatException) { return null; }
        if (KeyShape().Match(text) is not { Success: true } m) return null;
        return ((Guid.ParseExact(m.Groups[1].Value, "N"), int.Parse(m.Groups[2].Value)), (Guid.ParseExact(m.Groups[3].Value, "N"), int.Parse(m.Groups[4].Value)));
    }

    // A member of a group and what to do with it: pending (undo back to To while it is still at Tip), done (the group's
    // changes are undone already: its newest change is an undo back to To), or changed_since.
    sealed record GroupMember(Guid Memory, string Verdict, int To, int Tip);
    sealed record GroupMembers(IReadOnlyList<GroupMember> Items, bool Capped);

    // The group a key names, found again as the caller reads the trail now: its first and last change must be ones the
    // caller sees, by one login, of one kind, through one channel and client; its members are that login's such changes
    // between them that the caller sees, each within GroupGap of the one before. Null when the key names no such group.
    async Task<GroupMembers?> Members(Guid principal, string group, CancellationToken ct)
    {
        if (Decode(group) is not { } key) return null;
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        async Task<ChangeRow?> Anchor((Guid Memory, int Revision) at) =>
            (await ChangeRows(c, tx, principal, "e.memory_id=@m AND e.revision=@r", "ASC", 1, ct, ("m", at.Memory), ("r", at.Revision))).SingleOrDefault();
        if (await Anchor(key.First) is not { } first || await Anchor(key.Last) is not { } last) return null;
        if (first.Id > last.Id || first.Principal != last.Principal || first.Channel != last.Channel || first.Client != last.Client
            || Family(first.Event) != Family(last.Event)) return null;
        // That login's changes of the group's kind between the two, at most GroupRead of them: a key spanning more is no
        // group the listing gave (it reads ChangesRead rows), so it names none.
        string[] kind = [.. Undoable.Where(e => Family(e) == Family(first.Event))];
        var rows = await ChangeRows(c, tx, principal,
            "e.principal_id=@p AND e.channel=@ch AND e.client IS NOT DISTINCT FROM @cl::text AND e.id BETWEEN @a AND @b AND e.event=ANY(@kind)",
            "ASC", GroupRead + 1, ct, ("p", first.Principal), ("ch", first.Channel), ("cl", first.Client), ("a", first.Id), ("b", last.Id), ("kind", kind));
        if (rows.Count > GroupRead || rows.Zip(rows.Skip(1)).Any(p => p.Second.At - p.First.At > GroupGap)) return null;
        var order = rows.Select(r => r.Memory).Distinct().ToList();
        var capped = order.Count > GroupMax;
        order = [.. order.Take(GroupMax)];
        // Each member's current revision and its newest change, to tell one still at the group's changes from one undone
        // since (whoever undid it) or changed since.
        var now = new Dictionary<Guid, (int Revision, string? Event, int? To)>();
        await using (var cmd = Database.Command(c, tx, """
            SELECT m.id,m.revision,x.event,CASE WHEN jsonb_typeof(x.detail->'to')='number' THEN (x.detail->>'to')::int END
            FROM memories m LEFT JOIN LATERAL (SELECT e.event,e.detail FROM memory_events e
              WHERE e.memory_id=m.id AND e.revision=m.revision AND e.event=ANY(@events) ORDER BY e.id DESC LIMIT 1) x ON true
            WHERE m.id=ANY(@ids)
            """, ("ids", order.ToArray()), ("events", Undoable.ToArray())))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                now[r.GetGuid(0)] = (r.GetInt32(1), Text(r, 2), r.IsDBNull(3) ? null : r.GetInt32(3));
        await tx.CommitAsync(ct);
        var items = new List<GroupMember>();
        foreach (var memory in order)
        {
            var revisions = rows.Where(r => r.Memory == memory).Select(r => r.Revision).Order().ToList();
            int to = revisions[0] - 1, tip = revisions[^1];
            var contiguous = revisions.Zip(revisions.Skip(1)).All(p => p.Second == p.First + 1);
            var (revision, change, back) = now.TryGetValue(memory, out var n) ? n : (0, null, null);
            var verdict = !contiguous ? "changed_since"
                : revision == tip ? "pending"
                : revision == tip + 1 && change == "reverted" && back == to ? "done"
                : "changed_since";
            items.Add(new(memory, verdict, to, tip));
        }
        return new(items, capped);
    }

    // The key that takes back what a group's undo did: the caller's own undos through this channel and client, as the caller
    // reads them, each within GroupGap of the one before, ending at its newest and reaching back only over undos of the group's
    // members made since the group's own changes. A key selects every such undo between its two ends (Members), so an undo of
    // anything else ends the reach there: a redo never puts back what its person undid apart from this group, and whatever of
    // the group lies before such an undo stays undone, for each memory's own history to put back.
    async Task<string?> RedoKey(Guid principal, MemoryCall call, MemoryWrite newest, IReadOnlyList<GroupMember> members, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var rows = await ChangeRows(c, tx, principal,
            "e.principal_id=@principal AND e.channel=@ch AND e.client IS NOT DISTINCT FROM @cl::text AND e.event='reverted' AND e.at>=@since",
            "DESC", ChangesRead, ct, ("ch", call.Channel), ("cl", AdminAudit.Client(call.Client)), ("since", DateTimeOffset.UtcNow - ChangesWindow));
        await tx.CommitAsync(ct);
        var run = Runs(rows).FirstOrDefault(run => run.Any(r => r.Memory == newest.Id && r.Revision == newest.Revision));
        if (run is null) return null;
        var tips = members.ToDictionary(m => m.Memory, m => m.Tip);
        var last = run.FindIndex(r => r.Memory == newest.Id && r.Revision == newest.Revision);
        var first = last;
        while (first > 0 && tips.TryGetValue(run[first - 1].Memory, out var tip) && run[first - 1].Revision > tip) first--;
        return Key(run[first], run[last]);
    }
}
