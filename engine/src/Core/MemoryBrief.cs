using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// The brief for native memories (SB-P5-08; story 12; docs/dev/memory.md sections 5 and 7.1; migration 069; adapted from Second
// Brain's agent brief, src/brief/compute.ts at the pinned commit, MIT; NOTICE): what needs the person now, each section the
// head of a list a tool walks and a count that is that list's length. So it writes nothing and defines nothing: each section
// is the page its own queue's statement reads, in which the count and the items come from one snapshot under that queue's one
// predicate, and what a section leaves out (a held or trashed memory, another person's, another scope's) its list leaves out.
//
// Due: list_due's overdue and upcoming windows (061's memory_due and memory_due_window), which are also what a decision's
// review date is read by since 069, so one due definition serves both (D-214). Decisions: list_decisions' due and upcoming
// (060's memory_decision_listed). You owe, owed to you: list_loops out and in (058). Stale: list_stale (053). Insights:
// list_insight_stream's proposed view (066). Contradictions and duplicates: list_contradictions and list_duplicates (068, 063).
// Held: how many memories wait in the review queue for a person's release, by the queue's own predicates (ReviewCount), with no
// words: only a person releases one, so it needs them now even though no AI client may read it.
// Standing: for a named project only, those that fire inside it and nowhere else and may fire now (065), list_standing's with
// that project; the project narrows no other section, as recall's narrows nothing but what fires. Calibration: its line once
// it is ready, and the scored list's length (decision_calibration's n). Changes: the groups of an AI client's changes in the
// last 48 hours (MemoryUndo.cs), none of a memory held now, of the one scope when one is asked, with the key undo takes; a
// change left out ends its group, so a key undoes what its group counts.
//
// Only the memory tools' brief and the app's op brief (its Home card) call it, with memory.tools on, off by default. With it off
// nothing is read and neither exists.

// A section: the length of its whole list, and the head of that list, in the list's order.
public sealed record MemoryBriefList<T>(int Total, IReadOnlyList<T> Items);

// A section of dated things: the lengths of its two windows (Overdue: its moment has come; Upcoming: within 48 hours), Total
// the two together, and the head of each, the moment that has come first.
public sealed record MemoryBriefDated<T>(int Total, int Overdue, int Upcoming, IReadOnlyList<T> Items);

// The AI changes: Total groups, Changes in all, the newest groups (at most MemoryStore.BriefGroups), and Truncated when more
// changes were made than one read takes (MemoryStore.ChangesRead), so the oldest are left out of every count and each is at
// least what it says.
public sealed record MemoryBriefChanges(int Total, int Changes, bool Truncated, DateTimeOffset Since, IReadOnlyList<MemoryChangeGroup> Groups);

// One brief. Project: the project as the call named it, which only the standing section reads; UnknownProject: the caller
// reads no project by that name, so that section is empty.
public sealed record MemoryBrief(string Zone, string? Project, bool UnknownProject, MemoryBriefDated<MemoryDueItem> Due, MemoryBriefDated<MemoryDecision> Decisions,
    MemoryBriefList<MemoryLoop> YouOwe, MemoryBriefList<MemoryLoop> OwedToYou, MemoryBriefList<MemoryStale> Stale, MemoryBriefList<InsightStreamItem> Insights,
    MemoryBriefList<MemoryContradiction> Contradictions, MemoryBriefList<MemoryDuplicate> Duplicates, int Held, MemoryBriefList<MemoryStandingItem> Standing,
    DecisionCalibration Calibration, MemoryBriefChanges Changes)
{
    // In a line, for a client or a card: what needs attention, each by its count, or that nothing does. The counts alone: no words.
    public string Says
    {
        get
        {
            var said = new StringBuilder();
            void Add(int n, string one, string many)
            {
                if (n == 0) return;
                if (said.Length > 0) said.Append(", ");
                said.Append(n).Append(' ').Append(n == 1 ? one : many);
            }
            Add(Due.Total, "due", "due");
            Add(Decisions.Total, "decision to review", "decisions to review");
            Add(YouOwe.Total, "you owe", "you owe");
            Add(OwedToYou.Total, "owed to you", "owed to you");
            Add(Stale.Total, "may be out of date", "may be out of date");
            Add(Insights.Total, "pending insight", "pending insights");
            Add(Contradictions.Total, "possible contradiction", "possible contradictions");
            Add(Duplicates.Total, "possible copy", "possible copies");
            Add(Held, "held for your review", "held for your review");
            Add(Standing.Total, "standing instruction for this project", "standing instructions for this project");
            // Cut at one read's changes, the count is only how many at least.
            if (Changes.Truncated) Add(Changes.Total, "or more groups of AI changes", "or more groups of AI changes");
            else Add(Changes.Total, "group of AI changes", "groups of AI changes");
            return said.Length == 0 ? "Nothing needs attention." : said.Append('.').ToString();
        }
    }
}

public sealed partial class MemoryStore
{
    // How many of each section a brief shows, upstream's where upstream has one (due 5, decisions 3, loops 5, stale 2, insights 1,
    // standing 3); the review's 3 and the groups' 5 are Rhythm's. Each list's tool walks the rest. A preview is shorter than a list's.
    public const int BriefDue = 5, BriefDecisions = 3, BriefLoops = 5, BriefStale = 2, BriefInsights = 1, BriefReview = 3, BriefStanding = 3, BriefGroups = 5,
        BriefPreview = 120, BriefProjectMax = 128;

    // A reader's brief, optionally of one scope; project names a project for its standing instructions. Each section's count
    // comes from the statement that read its items, so the two agree (decisions has two windows, so two statements: Reviews); the
    // sections are read one after another. Call: the channel it reads through (a contradiction's MaySettle).
    public async Task<MemoryBrief> Brief(Guid principal, string? scope, string? project, InsightStore insights, MemoryCall call, CancellationToken ct)
    {
        var name = MemoryInput.Project(project);
        if (name is { Length: > BriefProjectMax }) throw new MemoryRefused("invalid", $"project is a slug or alias of at most {BriefProjectMax} characters");
        var unknown = false;
        if (name is not null)
        {
            await using var c = await source.OpenConnectionAsync(ct);
            await using var known = Database.Command(c, null, $"SELECT EXISTS(SELECT 1 FROM memory_projects p WHERE {ProjectNamed} AND {ProjectListed()})",
                ("principal", principal), ("name", name));
            unknown = await known.ExecuteScalarAsync(ct) is not true;
        }

        // The list soonest first holds both windows ahead of the later ones.
        var due = await Due(principal, scope, null, BriefDue, null, BriefPreview, ct);
        var dates = new MemoryBriefDated<MemoryDueItem>(due.Overdue + due.Upcoming, due.Overdue, due.Upcoming,
            [.. due.Items.Where(i => i.Window != DueDates.Later)]);

        var come = await Decisions(principal, scope, MemoryDecisions.Due, BriefDecisions, null, BriefPreview, ct);
        var soon = come.Items.Count < BriefDecisions && come.Upcoming > 0
            ? await Decisions(principal, scope, MemoryDecisions.Upcoming, BriefDecisions - come.Items.Count, null, BriefPreview, ct)
            : null;
        var decisions = Reviews(come, soon);

        var owe = await Loops(principal, scope, MemoryLoops.Out, BriefLoops, null, BriefPreview, ct);
        var owed = await Loops(principal, scope, MemoryLoops.In, BriefLoops, null, BriefPreview, ct);
        var stale = await Stale(principal, scope, BriefStale, null, BriefPreview, ct);
        var proposed = await insights.Stream(principal, InsightStore.Proposed, scope, BriefInsights, null, ct);
        var contradictions = await Contradictions(principal, scope, BriefReview, null, BriefPreview, call, ct);
        var duplicates = await Duplicates(principal, scope, BriefReview, null, BriefPreview, ct);
        var held = await ReviewCount(principal, new(scope), ct);
        var standing = name is null || unknown ? new MemoryBriefList<MemoryStandingItem>(0, []) : await StandingFor(principal, scope, name, ct);
        var calibration = await Calibration(principal, scope, null, ct);
        var changes = await Changes(principal, null, Mcp.MemoryTools.Channel, ct, notHeld: true, scope: scope);

        return new(due.Zone, name, unknown, dates, decisions, new(owe.Total, owe.Items), new(owed.Total, owed.Items), new(stale.Total, stale.Items),
            new(proposed.Total, proposed.Items), new(contradictions.Total, contradictions.Items), new(duplicates.Total, duplicates.Items), held, standing, calibration,
            new(changes.Groups.Count, changes.Groups.Sum(g => g.Changes), changes.Truncated, changes.Since, [.. changes.Groups.Take(BriefGroups)]));
    }

    // The decisions section from list_decisions' due page and, when that page leaves room, its upcoming page. Each window's count
    // is the page's whose items are shown, so a decision that comes within 48 hours between the two reads never leaves more items
    // than the count says, which the app refuses as no answer of the engine's.
    internal static MemoryBriefDated<MemoryDecision> Reviews(MemoryDecisionPage come, MemoryDecisionPage? soon)
    {
        var upcoming = soon?.Total ?? come.Upcoming;
        return new(come.Due + upcoming, come.Due, upcoming, [.. come.Items, .. soon?.Items ?? []]);
    }

    async Task<MemoryBriefList<MemoryStandingItem>> StandingFor(Guid principal, string? scope, string project, CancellationToken ct)
    {
        var page = await Standing(principal, scope, BriefStanding, null, ct, project, firingOnly: true);
        return new(page.Total, page.Items);
    }
}
