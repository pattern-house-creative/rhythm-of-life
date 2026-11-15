using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Knowledge.Mcp;

// app_memories (SB-P3-12; contracts/README.md 2.2; docs/dev/memory.md sections 5 to 7.2): the app's own reads and writes
// of native memories, for its Memories view (SB-P3-13) and Home's brief and resurfaced cards (SB-P5-08, SB-P5-17). One tool with an op, as app_screens: list, trash, get, history,
// projects, review, dismissals, connections, stale, brief, resurface, tags, changes, capsule_status, settings, loops, due, decisions and calibration read; capture, append, update, status, release, release_all,
// capsule, forget, restore, undo, delete, dismiss, undismiss, link, unlink, still_true, clear_flags, resurface_dismiss, undo_group, forget_all, restore_all, delete_all, project_create, project_update, project_delete, resolve_loop, resolve_due and record_outcome write. Each calls the native memory store's checked writers and reads (Core/Memories.cs, MemoryHistory,
// MemoryProjects, MemoryTrash, MemoryUndo, MemoryReview, Capsules), which decide who may do what under the one read
// predicate and the author lock (memory.md section 5).
//
// Only with memory.tools on (MemorySettings.ToolsOn), the setting that makes the MCP memory tools: off, absent or empty,
// the tool is neither listed nor taken, answered as one this role does not have (AppEndpoint), so the app's role answers
// exactly as before. The same switch, because a person needs these screens once an AI client can write memories: only a
// person releases a hold or deletes a memory for good (D-039, D-036), and this is where a person does.
//
// The channel is fixed here: every op reads and writes as the app, `app` (Channel), never from an argument, a header or
// the client's name (memory.md section 5, "the channel is the caller's word"); migration 033's memory_channel takes app
// only from the app's role. So the app's login is a person's channel: it may release a hold, or several at the revisions
// its person reviewed (review and release_all, SB-P3-22), undo its way to a release, put a memory into a capsule slot
// or take it out (capsule, SB-P3-22; 039 refuses an AI client's channel) and delete for good with the trash row's nonce
// and a confirmation equal to the memory's id, which the app asks its person for. Its reads are the household view's
// (the app token speaks for no person), or with app.viewers on, which this version refuses (D-046), a viewer session's,
// bound to its person. An AI client's token never reaches any of it: the door sends a harness token to the MCP tools,
// which have no op for a capsule slot, the review queue or a release (Perimeter, MemoryTools).
//
// share (SB-P3-10, API-19): with memory.share on beside memory.tools (MemorySettings.ShareOn), a person moves a memory
// between its owner's private scope and Family from the view, through the store's share (MemoryShare.cs, migration 048)
// as the app: its owner shares a private memory into Family, its author or a Family admin takes it back to the author's
// private scope. Off, absent or empty, the op is neither listed nor taken (outside the schema, as before), so the
// listing and every answer are exactly as they were. It is listed only while memory scopes are on as well (AppEndpoint),
// since with them off every memory is the household's and a share is refused (scopes_off) before anything is read. The
// household view (the app token, no person) reads no private memory and wrote no Family memory, so it shares nothing:
// the app's channel that does is a viewer session bound to its person (app.viewers, which this version refuses, D-046).
//
// A held memory is served whole to the app, its words with its hold, for a person to review before releasing it (D-160:
// only the app and the terminal serve them). A list and the trash serve it without its words, as the store does, and
// history and a version read leave a held state's words out (034). Every answer is the store's data in the contract's
// shape; a refusal is {ok: false, code, reason}, the reason the engine's own words, fit to show. Arguments outside the
// schema are refused before anything is read (Allowed).
//
// Truth over time (SB-P4-11, the app half of D-192): get and a list name each memory's state (current, replaced, ended
// or retracted), what replaced it and the retracted sources that flagged it, from migration 049's one definition, as the
// MCP get and list_recent do; a write, a forget and a restore whose retraction cascade did something name what it did
// (cascade). A person's own flag clear is clear_flags (below); a supersede is not offered here yet.
//
// Kept dismissals (SB-P4-05, MapDismissals, migration 057): dismissals reads the suggestions dismissed between two stars on
// this caller's map, newest first, whoever dismissed them (an AI client's unlink included), each with whether this caller
// may take it back; dismiss is Discover's Dismiss of one suggestion by its id on the map, kept through every rebuild (a
// link a person or an AI client made is unlinked instead, as unlink of its one type does); undismiss takes one back. They
// are a person's steps, so this tool, the app's channel, is where they are; 057 refuses them through an AI client's.
//
// A person's own link steps (run 17; API-21, API-29; SB-P4-02, SB-P4-08 and SB-P4-09's person halves): connections reads
// a memory's links and the map's suggestions between it and another memory (MemoryStore.Connections), link makes a
// person's link from it to another memory of its scope, verified (052, as 057 left it: a person's link takes back a
// dismissal of its type between the two), unlink removes the links between two memories or dismisses the map's
// suggestion between them (052), stale reads the memories this caller may change that may be out of date (053's one
// queue, MemoryStore.Stale), still_true confirms one without changing it (053's confirm_memory) and clear_flags takes a
// memory's retraction flags off once its person has checked it (049's clear_memory_flags). Each goes through the same
// checked functions as the MCP tools link, unlink, connections, list_stale, still_true and update's clear
// retracted_sources, so the author lock, the one scope and the holds decide as they do there; only the channel differs,
// the app's (a person's), never an AI client's. They are among the ops memory.tools makes, so off they are listed and
// taken nowhere, and the app's server refuses them as before (app/server/memories.ts).
//
// Duplicates (SB-P5-02, migration 063): capture checks a new memory's words against its own scope's, as remember does, and
// a near copy is saved and flagged for review, its answer naming the copies (duplicates); get and a list name what a
// memory was flagged as a copy of (duplicateOf); duplicates reads the review this caller may act on, newest first, with
// its count from the same read (063's one predicate), and keep_copy takes a memory's duplicate flags off once its person
// keeps both, through the same checked function as the MCP update's clear duplicate_of. Nothing is refused or merged.
//
// Contradictions (SB-P5-03, migration 068): every write that saves words is checked, as the memory tools' are, its answer
// naming the close memories of its scope that may conflict (contradictionCandidates, as get and a list do), contradictions
// reads the review this caller may act on, newest first, with its count from the same read (068's one predicate), and
// settle_contradiction is a person's decision on one, only through this channel and the terminal's: both stand, or the one
// named replaces the other. keep_copy, through this channel a person's word, settles the pair too.
//
// The full memory UI's list (SB-P6-01, AppMemoriesManage.cs): tags and their counts beside a list's filters, an AI client's
// groups of changes and a group's undo, several memories forgotten, brought back, deleted for good or shared at once, the
// project registry's writes, a capsule's status and the install's memory settings to read. Each is among the ops memory.tools
// makes, so off they are listed and taken nowhere; share_all is share's, taken only with memory.share on as well.
//
// A person's review queues (SB-P6-01, AppMemoriesQueues.cs): how many wait in each, the insight candidates and the insight stream to
// read, an insight's history, a review, and one resolve for what a person does in any of them. Among the ops memory.tools makes too.
//
// The brief's lists (SB-P6-01, AppMemoriesPlans.cs): loops, due and decisions read the lists the Home card heads, calibration what
// its line comes from, and resolve_loop, resolve_due and record_outcome are a person's own word on one. Among the ops memory.tools
// makes, so off they are listed and taken nowhere.
//
// The memory UI's last pieces (SB-P6-01, run 22, AppMemoriesStanding.cs): list and tags take query, the words a memory holds, through
// the predicates every list shares, so a count stays its list's length; standing lists the standing instructions as list_standing
// does and stop_standing stops one through the same checked writer; resolve_all takes one of a review queue's one-word steps for
// several items, each through resolve's one door. Among the ops memory.tools makes, so off they are listed and taken nowhere.
public static partial class AppMemories
{
    // The tool's name, as AppEndpoint lists it (AppEndpoint.Memories).
    public const string Name = "app_memories";

    // The one channel the app speaks through (migration 033's memory_channel).
    public const string Channel = "app";

    // A call may carry a memory's whole text (131 072 bytes, MemoryInput.MaxBytes) with its JSON escapes, so its body may
    // be larger than the app's other calls' 32 KiB (AppEndpoint.MaxBody), within the engine's own request limit.
    public const int MaxBody = 288 * 1024;

    public const int ListPreview = 300, ListDefault = 20, TrashDefault = 20, ReviewDefault = 20, StaleDefault = 20, DuplicatesDefault = 20, ContradictionsDefault = 20;

    public static readonly string[] Reads = ["list", "trash", "get", "history", "projects", "review", "dismissals", "connections", "stale", "duplicates", "contradictions", "brief", "resurface",
        "tags", "changes", "capsule_status", "settings", "queues", "insight_candidates", "insight_stream", "insight_history", "loops", "due", "decisions", "calibration",
        "standing"];
    // The share ops (SB-P3-10, SB-P6-01): not among Writes, since memory.tools alone does not make them; memory.share does, beside it.
    public const string ShareOp = "share", ShareAllOp = "share_all";
    public static readonly string[] ShareOps = [ShareOp, ShareAllOp];
    static readonly (string[] Required, string[] Optional) ShareShape = (["id"], ["scope", "expected"]), ShareAllShape = (["memories"], ["scope"]);
    public static readonly string[] Writes = ["capture", "append", "update", "status", "release", "release_all", "capsule", "forget", "restore", "undo", "delete",
        "dismiss", "undismiss", "link", "unlink", "still_true", "clear_flags", "keep_copy", "settle_contradiction", "resurface_dismiss",
        "undo_group", "forget_all", "restore_all", "delete_all", "project_create", "project_update", "project_delete", "review_insight", "resolve",
        "resolve_loop", "resolve_due", "record_outcome", "stop_standing", "resolve_all"];
    static readonly string[] Ops = [.. Reads, .. Writes];

    // A capsule op names core or project with one of its slots (CapsuleSlots, 025's checks), or none to take the memory
    // out of its slot.
    const string None = "none";
    static readonly string[] Kinds = ["episodic", "semantic"], Statuses = ["canonical", "draft", "deprecated"],
        Volatilities = ["durable", "state", "volatile"], Scopes = ["private", "shared"],
        Reasons = [MemoryHolds.Instruction, MemoryHolds.Hidden, MemoryHolds.TooLong, MemoryHolds.Burst, MemoryHolds.HeldProject],
        Capsules = [CapsuleSlots.Core, CapsuleSlots.Project, None],
        Decisions = [MemoryContradictions.KeepBoth, MemoryContradictions.Replace],
        Slots = [.. CapsuleSlots.CoreSlots, .. CapsuleSlots.ProjectSlots];

    // Each op's arguments beside op: the required, then the optional.
    static readonly IReadOnlyDictionary<string, (string[] Required, string[] Optional)> Shapes = new Dictionary<string, (string[], string[])>
    {
        ["list"] = ([], ["scope", "tag", "kind", "status", "project", "query", "cursor", "limit"]),
        ["trash"] = ([], ["cursor", "limit"]),
        ["get"] = (["id"], ["version"]),
        ["history"] = (["id"], ["before", "limit"]),
        ["projects"] = ([], ["scope", "archived"]),
        ["review"] = ([], ["scope", "reason", "cursor", "limit"]),
        ["capture"] = (["content"], ["scope", "tags", "project", "kind", "importance", "volatility", "key"]),
        ["append"] = (["id", "text"], ["expected", "key"]),
        ["update"] = (["id"], ["content", "tags", "kind", "importance", "volatility", "project", "clear", "expected", "key"]),
        ["status"] = (["id", "status"], ["expected", "key"]),
        ["release"] = (["id"], ["expected"]),
        ["release_all"] = (["memories"], []),
        ["capsule"] = (["id", "capsule"], ["slot", "expected"]),
        ["forget"] = (["id"], ["expected", "key"]),
        ["restore"] = (["id"], ["nonce"]),
        ["undo"] = (["id"], ["to", "expected"]),
        ["delete"] = (["id", "nonce", "confirm"], []),
        ["dismissals"] = ([], ["limit"]),
        ["dismiss"] = (["link"], []),
        ["undismiss"] = (["dismissal"], []),
        ["connections"] = (["id"], ["type", "limit"]),
        ["stale"] = ([], ["scope", "cursor", "limit"]),
        ["link"] = (["id", "target"], ["type"]),
        ["unlink"] = (["id", "target"], ["type"]),
        ["still_true"] = (["id"], ["expected"]),
        ["clear_flags"] = (["id"], ["expected"]),
        ["duplicates"] = ([], ["scope", "cursor", "limit"]),
        ["keep_copy"] = (["id"], ["expected"]),
        ["contradictions"] = ([], ["scope", "cursor", "limit"]),
        ["settle_contradiction"] = (["id", "target", "decision"], ["expected", "targetExpected"]),
        ["brief"] = ([], ["scope", "project"]),
        ["resurface"] = ([], []),
        ["resurface_dismiss"] = (["id"], []),
        ["tags"] = ([], ["scope", "tag", "kind", "status", "project", "query"]),
        ["changes"] = ([], ["since", "scope", "notHeld"]),
        ["capsule_status"] = (["capsule"], ["project", "scope"]),
        ["settings"] = ([], []),
        ["undo_group"] = (["group"], []),
        ["forget_all"] = (["memories"], []),
        ["restore_all"] = (["memories"], []),
        ["delete_all"] = (["memories"], []),
        ["project_create"] = (["name"], ["slug", "description", "aliases", "scope"]),
        ["project_update"] = (["id"], ["name", "description", "aliases", "archived", "expected"]),
        ["project_delete"] = (["id"], ["expected"]),
        ["queues"] = ([], ["scope"]),
        ["insight_candidates"] = ([], ["scope", "state", "cursor", "limit"]),
        ["insight_stream"] = ([], ["view", "scope", "cursor", "limit"]),
        ["insight_history"] = (["id"], ["limit"]),
        ["review_insight"] = (["id", "action"], ["note"]),
        ["resolve"] = (["id", "action"], ["target", "expected", "targetExpected", "note", "insight", "copies"]),
        ["loops"] = ([], ["direction", "scope", "cursor", "limit"]),
        ["due"] = ([], ["window", "scope", "cursor", "limit"]),
        ["decisions"] = ([], ["state", "scope", "cursor", "limit"]),
        ["calibration"] = ([], ["source", "scope"]),
        ["resolve_loop"] = (["id", "action"], ["expected"]),
        ["resolve_due"] = (["id", "action"], ["until", "expected"]),
        ["record_outcome"] = (["id", "result"], ["expected"]),
        ["standing"] = ([], ["scope", "project", "cursor", "limit"]),
        ["stop_standing"] = (["id"], ["expected"]),
        ["resolve_all"] = (["action", "items"], []),
    };

    [GeneratedRegex("^[A-Za-z0-9._:-]{8,200}$", RegexOptions.CultureInvariant)] private static partial Regex KeyPattern();
    // A link's type as the store names one (a lower-case word, 052's check): which types a link takes is the store's to say.
    [GeneratedRegex("^[a-z][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)] private static partial Regex TypePattern();
    // A group's key as an answer gave it (MemoryStore.Key), which the store decodes.
    [GeneratedRegex("^[A-Za-z0-9_-]{1,200}$", RegexOptions.CultureInvariant)] private static partial Regex GroupPattern();

    // The listing, with memory.tools on. `share`: memory.share is on beside it and memory scopes are on, so the share op
    // is listed (AppEndpoint); without it the listing is exactly as before.
    public static object Listing(bool share = false)
    {
        object Text(int min, int max) => new { type = "string", minLength = min, maxLength = max };
        object Id() => new { type = "string", format = "uuid" };
        object Revision() => new { type = "integer", minimum = 1 };
        return new
        {
            name = Name,
            description = "Native memories for the app's Memories view, as the app (channel app), which a person uses. op list (scope, tag, kind, status, project, query: words its text holds, case aside, 1-200 characters, cursor, limit 1-50): {memories, next}, newest first, each with a 300-character preview; a held memory without its words. op trash (cursor, limit): {trash, next}, what this caller may restore, with days left, the trash nonce and mayDeleteForever. op get (id, version): {memory}, whole, a held one with its words and its hold for a person to review, or {version}, one revision. op history (id, before, limit 1-100): {changes, ...}, newest first. op projects (scope, archived): {projects}. op review (scope, reason, cursor, limit 1-50): {review, next, count}, the held memories this caller reads, newest held first, why and how each came, never their words. op capture (content, scope, tags, project, kind, importance, volatility, key): a new draft. op append (id, text), update (id, content, tags, kind, importance, volatility, project, clear), status (id, status), release (id: a person lifts its hold), release_all (memories: 1-50 of {id, expected}, each at the revision its person reviewed): {results}, capsule (id, capsule core, project or none, slot: a person puts it into a capsule slot or takes it out), forget (id: to the trash), restore (id, nonce), undo (id, to: a revision, else the newest change) and delete (id, nonce, confirm: the memory's id, once the person confirmed: for good), each with expected (the revision read) where it takes one and key (an idempotency key) on capture, append, update, status and forget. A write answers {ok: true, op, id, revision, ...}; a refusal {ok: false, code, reason}. op dismissals (limit 1-50): {dismissals, truncated}, the suggestions dismissed between two stars on this caller's map, newest first, by a person or by an AI client's unlink, each with mayRestore. op dismiss (link: a suggestion's id on the map): Discover's Dismiss, kept through every rebuild; a link a person or an AI client made is unlinked instead, removed for good unless it was marked suggested: {ok: true, op, link, dismissal, removed, unchanged}. op undismiss (dismissal: an id from dismissals): taken back, the suggestion on the map again from the next read: {ok: true, op, dismissal, unchanged}."
                + " op connections (id, type, limit 1-50): {id, connections, truncated}, the links people and AI clients made from or to the memory, newest first, then the map's suggestions between it and another memory, each with mayUnlink. op link (id, target: another memory of its scope, type: relates_to by default, caused_by, decided, about_person, part_of_project, follows or drawn_from): a person's link, confirmed: {ok: true, op, link, id, target, type, verification, created, unchanged, ...}. op unlink (id, target, type: one type, else every type but a replacement's): the links between the two removed and the map's suggestions between them dismissed: {ok: true, op, removed, kept, dismissed, unchanged, ...}. op stale (scope, cursor, limit 1-50): {stale, next, count}, the memories this caller may change that may be out of date or were built on one marked wrong, longest unchecked first. op still_true (id, expected): a person's word that it still holds, changing nothing in it: {ok: true, op, id, revision, checkedAt, was, retracted, unchanged, ...}. op clear_flags (id, expected): its flags for a check taken off once its person has checked it: a write's answer. op duplicates (scope, cursor, limit 1-50): {duplicates, next, count}, the memories this caller may change that were flagged when saved as possible copies of another of their scope, newest first, each with duplicateOf (the copies that stand, closest first). op keep_copy (id, expected): its flags as a possible copy taken off once its person keeps both: a write's answer. A capture whose words are close to a memory of its scope is saved and flagged, its answer naming them in duplicates."
                + " op brief (scope, project): the Home card's brief, each section {count, items}, count the whole length of the list its MCP tool walks, its items that list's head, no held or trashed memory, says the counts in a line." + " op contradictions (scope, cursor, limit 1-50): {contradictions, next, count}, the pairs of memories of one scope this caller may change that may conflict, a person's to settle, newest first, each with both previews, how close and who named it a contradiction, count the whole list's length. op settle_contradiction (id, target, decision: keep_both or replace, expected, targetExpected): a person's decision on one: both stand, or id replaces target. A write's answer with target and decision, or unchanged when it was settled already."
                + " op resurface: {day, pick}, the Home card's old memory of the install's day, the same one all day: {id, revision, scope, owner, preview, truncated, createdAt}, or null when none is eligible; never held, trashed, the engine's own, what a Second Brain job wrote or one this reader dismissed. op resurface_dismiss (id): {op, id, unchanged}, this reader retires that memory from the card for good, in the trash or held too; one by the household view, or with scopes off by anyone, holds for every reader."
                + " op loops (direction out or in, scope, cursor, limit 1-50): {loops, next, count, youOwe, owedToYou}, the open loops this caller owns and may change, newest first, count the asked list's whole length and the other two both directions'. op due (window overdue, upcoming or later, scope, cursor, limit 1-50): {due, next, count, overdue, upcoming, later, zone}, its memories with a due date, soonest first. op decisions (state open, due, upcoming, resolved, scored or all, scope, cursor, limit 1-50): {decisions, next, count, open, dueNow, upcoming, resolved, scored}, its decisions, newest first, open by default. op calibration (source stated or inferred, scope): how sure the person was against how things turned out, each figure with how many decisions it rests on, none below its minimum. op resolve_loop (id, action done, received or not_a_task, expected), resolve_due (id, action snooze with until, or clear_date, expected) and record_outcome (id, result right, wrong, mixed or unknown, expected): a person's word on one, a write's answer with what its step said; undo takes each back."
                + " op tags (scope, tag, kind, status, project, query): {tags, truncated, count}, each tag on the memories a list with these filters holds with how many carry it, most carried first, and that list's whole length; a held memory's tags are none of them. op changes (since, scope, notHeld: true leaves out what is held now, as the brief counts them): {groups, truncated, since, count}, the AI clients' changes this caller reads, newest group first (48 hours when since is not given), count how many groups, each group the changes of one kind by one login and client, each within ten minutes of the one before, with its key and its newest memories, a held one without its words. op undo_group (group: a key as changes or an earlier undo_group gave it): {op, group, total, remaining, done, capped, results, redo}, up to 5 of its memories back as they were before it, the rest for the next call with the same key, redo the key that undoes these undos. op forget_all (memories: 1-50 of {id, expected}), restore_all (memories: 1-50 of {id, nonce}) and delete_all (memories: 1-50 of {id, nonce, confirm: the memory's id, once the person confirmed}): {op, results}, each memory answered on its own: a person's steps. op project_create (name, slug, description, aliases, scope), project_update (id, name, description, aliases, archived, expected): {op, project, scopes, changed, unchanged}; project_delete (id, expected): {op, id, slug, owner, memories, scopes}, its memories kept, out of it. op capsule_status (capsule core or project, project, scope): a capsule slot by slot as its AI clients are served it, with the memories in its slots that wait, and no words; served says whether AI clients are offered capsules. op settings: the install's memory settings, to read; they change in rhythm.yaml."
                + " op queues (scope): {held, duplicates, contradictions, stale, candidates, insights}, how many wait in each of a person's review queues, each the length of its own list. op insight_candidates (scope, state: waiting, the default, used or rejected, cursor, limit 1-50): {candidates, next, count}, the pairs of an older and a newer memory of one scope the nightly pass found worth a look, best first, with both previews, count the whole list's length. op insight_stream (view: proposed, the default, older, confirmed, snoozed, closed or all, scope, cursor, limit 1-50): {view, count, proposed, older, confirmed, snoozed, closed, candidates, day, insights, next}, the one insight stream, one view at a time, each view's length read with the page, day what today came to, each insight with mayReview. op insight_history (id, limit 1-100): {id, review, count, events}, an insight's feedback and reviews, newest first. op review_insight (id, action: confirm, dismiss or reopen, note): {op, id, review, view, reviewedAt, unchanged}, a person's review, reopen taking one back. op resolve (id, action, target, expected, targetExpected, note, insight, copies): a person's review queues through one door, an item leaving its queue once whatever the word: release (a held memory), keep_copy (a possible copy; copies, each memory it is close to at the revision read: refused, nothing changed, when one the review shows changed since or is not named), settle_keep_both or settle_replace (a possible contradiction: id stands, target the other memory), still_true (may be out of date), use_candidate or reject_candidate (a pair; note, insight), confirm_insight or dismiss_insight (an insight; note): {op, queue, action, id, unchanged, waiting, stillCounted, ...}."
                + " op resolve_all (action: still_true, reject_candidate, confirm_insight or dismiss_insight; items: 1-50 of {id, expected}, expected the revision read of each memory for still_true and none for an insight or a pair): {op, action, results}, the same step for each, each through resolve's one door and answered on its own: a person's step."
                + " op standing (scope, project, cursor, limit 1-50): {standing, next, count, firing, held, overLimit, maxInScope, maxFires, served}, the standing instructions this caller reads, oldest first, as list_standing lists them, each with whether it fires and why not, a held one without its words; served says whether any fires at all now. op stop_standing (id, expected): it stops firing and stays a memory, a write's answer; undo starts it again."
                + (share ? " op share (id, scope, expected): scope shared, the default, shares one of its owner's private memories into Family; scope private takes a Family memory back to its author's private scope, its author's or a Family admin's step. A write's answer with projectLeft, or unchanged where it is already. op share_all (memories: 1-50 of {id, expected}, scope): {op, results}, each answered on its own." : ""),
            inputSchema = new
            {
                type = "object", additionalProperties = false, required = new[] { "op" },
                properties = new
                {
                    op = new { type = "string", @enum = share ? [.. Ops, .. ShareOps] : Ops },
                    id = Id(), nonce = Id(), confirm = Id(),
                    version = Revision(), before = Revision(), expected = Revision(), to = Revision(),
                    // A list and the trash take at most 50, history at most 100 (Fits): the listing gives the bound every op
                    // keeps to, so a call within it is never refused for its limit.
                    limit = new { type = "integer", minimum = 1, maximum = MemoryStore.TrashMax, description = "1 to 50; history takes up to 100" },
                    cursor = Text(1, 200),
                    scope = new { type = "string", @enum = Scopes },
                    tag = Text(1, MemoryInput.MaxTagLength),
                    kind = new { type = "string", @enum = Kinds },
                    status = new { type = "string", @enum = Statuses },
                    volatility = new { type = "string", @enum = Volatilities },
                    importance = new { type = "integer", minimum = 1, maximum = 5 },
                    project = Text(1, 64),
                    archived = new { type = "boolean" },
                    reason = new { type = "string", @enum = Reasons },
                    capsule = new { type = "string", @enum = Capsules },
                    slot = new { type = "string", @enum = Slots },
                    memories = new
                    {
                        type = "array", minItems = 1, maxItems = MemoryStore.ReleaseAllMax,
                        items = new
                        {
                            type = "object", additionalProperties = false, required = new[] { "id" },
                            properties = new { id = Id(), expected = Revision(), nonce = Id(), confirm = Id() },
                        },
                    },
                    content = Text(1, MemoryInput.MaxBytes),
                    text = Text(1, MemoryInput.MaxBytes),
                    tags = new { type = "array", maxItems = MemoryInput.MaxTags, items = Text(1, MemoryInput.MaxTagLength) },
                    clear = new { type = "array", maxItems = 4, items = new { type = "string", @enum = MemoryChange.Clearable.Order().ToArray() } },
                    key = new { type = "string", pattern = "^[A-Za-z0-9._:-]{8,200}$" },
                    link = new { type = "string", minLength = 1, maxLength = 100 },
                    dismissal = new { type = "string", minLength = 1, maxLength = 160 },
                    target = Id(),
                    type = new { type = "string", pattern = "^[a-z][a-z0-9_-]{0,63}$" },
                    decision = new { type = "string", @enum = Decisions },
                    targetExpected = Revision(),
                    // resolve's keep_copy: each memory the copy is close to at the revision read (073).
                    copies = new
                    {
                        type = "array", minItems = 1, maxItems = MemoryResolve.CopiesMax,
                        items = new { type = "object", additionalProperties = false, required = new[] { "id", "expected" }, properties = new { id = Id(), expected = Revision() } },
                    },
                    since = new { type = "string", maxLength = 40 },
                    notHeld = new { type = "boolean" },
                    direction = new { type = "string", @enum = Directions },
                    window = new { type = "string", @enum = DueDates.Windows },
                    state = new { type = "string", @enum = (string[])[.. MemoryDecisions.Lists, .. MemoryStore.InsightStates] },
                    source = new { type = "string", @enum = CalibrationSources },
                    action = new { type = "string", @enum = (string[])[.. MemoryLoops.Actions, .. DueDates.Actions, .. MemoryResolve.ReviewActions, .. ReviewWords] },
                    result = new { type = "string", @enum = MemoryDecisions.Results },
                    until = new { type = "string", minLength = 1, maxLength = 40 },
                    group = new { type = "string", pattern = "^[A-Za-z0-9_-]{1,200}$" },
                    name = Text(1, MemoryProjectInput.MaxName),
                    slug = new { type = "string", pattern = "^[a-z0-9][a-z0-9-]{0,63}$" },
                    description = new { type = "string", maxLength = MemoryProjectInput.MaxDescription },
                    aliases = new { type = "array", maxItems = MemoryProjectInput.MaxAliases, items = Text(1, MemoryProjectInput.MaxAlias) },
                    view = new { type = "string", @enum = InsightStore.Views },
                    note = Text(1, 500),
                    insight = Id(),
                    query = Text(1, MemoryStore.QueryMax),
                    items = new
                    {
                        type = "array", minItems = 1, maxItems = MemoryStore.ReleaseAllMax,
                        items = new
                        {
                            type = "object", additionalProperties = false, required = new[] { "id" },
                            properties = new { id = Id(), expected = Revision() },
                        },
                    },
                },
            },
            annotations = new { readOnlyHint = false, destructiveHint = true, openWorldHint = false },
        };
    }

    // One op with exactly its arguments, each of its type and range; nothing else beside them, no member twice. An update
    // names at least one thing to change. `share`: memory.share is on beside memory.tools, so the share op is taken too.
    public static bool Allowed(JsonElement args, bool share = false)
    {
        if (args.ValueKind != JsonValueKind.Object) return false;
        var names = args.EnumerateObject().Select(x => x.Name).ToArray();
        if (names.Length != names.Distinct(StringComparer.Ordinal).Count()) return false;
        if (!args.TryGetProperty("op", out var o) || o.ValueKind != JsonValueKind.String || o.GetString() is not { } op) return false;
        (string[] Required, string[] Optional) shape;
        if (share && op == ShareOp) shape = ShareShape;
        else if (share && op == ShareAllOp) shape = ShareAllShape;
        else if (!Shapes.TryGetValue(op, out shape)) return false;
        if (shape.Required.Any(r => !args.TryGetProperty(r, out _))) return false;
        foreach (var member in args.EnumerateObject())
        {
            if (member.Name == "op") continue;
            if (!shape.Required.Contains(member.Name) && !shape.Optional.Contains(member.Name)) return false;
            if (!Fits(op, member.Name, member.Value)) return false;
        }
        // A capsule op names a slot with core or project, and none without one.
        if (op == "capsule" && (args.GetProperty("capsule").GetString() == None) == args.TryGetProperty("slot", out _)) return false;
        // A capsule's status names core, or project with its project.
        if (op == "capsule_status" && (args.GetProperty("capsule").GetString() is not { } kind || kind == None
            || (kind == CapsuleSlots.Project) != args.TryGetProperty("project", out _))) return false;
        // A snooze names when it comes back, and a clear names no date.
        if (op == "resolve_due" && (args.GetProperty("action").GetString() == DueDates.Snooze) != args.TryGetProperty("until", out _)) return false;
        // A project's change names at least one thing to change.
        if (op == "project_update" && !names.Any(n => n is "name" or "description" or "aliases" or "archived")) return false;
        // The other memory's revision goes with the other memory.
        if (op == "resolve" && args.TryGetProperty("targetExpected", out _) && !args.TryGetProperty("target", out _)) return false;
        // The memories read go with a keep both.
        if (op == "resolve" && args.TryGetProperty("copies", out _) && args.GetProperty("action").GetString() != MemoryResolve.KeepCopy) return false;
        return op != "update" || names.Any(n => n is "content" or "tags" or "kind" or "importance" or "volatility" or "project" or "clear");
    }

    static bool Fits(string op, string name, JsonElement v)
    {
        bool Text(int max) => v.ValueKind == JsonValueKind.String && v.GetString()!.Length is var n && n >= 1 && n <= max;
        bool Word(string[] words) => v.ValueKind == JsonValueKind.String && words.Contains(v.GetString());
        bool Number(int min, int max) => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n >= min && n <= max;
        bool Uuid() => v.ValueKind == JsonValueKind.String && Guid.TryParseExact(v.GetString(), "D", out _);
        bool Words(int max, Func<JsonElement, bool> each) => v.ValueKind == JsonValueKind.Array && v.GetArrayLength() <= max && v.EnumerateArray().All(each);
        return name switch
        {
            "id" or "nonce" or "confirm" => Uuid(),
            "version" or "before" or "expected" or "to" or "targetExpected" => Number(1, int.MaxValue),
            "limit" => Number(1, op is "history" or "insight_history" ? MemoryStore.HistoryMax : op == "review" ? MemoryStore.ReviewMax
                : op == "dismissals" ? MapDismissals.Max : MemoryStore.TrashMax),
            "cursor" => Text(200),
            "scope" => Word(Scopes),
            "tag" => Text(MemoryInput.MaxTagLength),
            "kind" => Word(Kinds),
            "status" => Word(Statuses),
            "volatility" => Word(Volatilities),
            "importance" => Number(1, 5),
            "project" => Text(64),
            "archived" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "reason" => Word(Reasons),
            "capsule" => Word(Capsules),
            "slot" => Word(Slots),
            "copies" => v.ValueKind == JsonValueKind.Array && v.GetArrayLength() is >= 1 and <= MemoryResolve.CopiesMax && v.EnumerateArray().All(Reviewed)
                && v.EnumerateArray().Select(m => Guid.Parse(m.GetProperty("id").GetString()!)).Distinct().Count() == v.GetArrayLength(),
            "memories" => v.ValueKind == JsonValueKind.Array && v.GetArrayLength() is >= 1 and <= MemoryStore.ReleaseAllMax && v.EnumerateArray().All(op switch
            {
                "restore_all" => InTrash,
                "delete_all" => ConfirmedGone,
                _ => Reviewed,
            }),
            "content" or "text" => Text(MemoryInput.MaxBytes),
            "tags" => Words(MemoryInput.MaxTags, t => t.ValueKind == JsonValueKind.String && t.GetString()!.Length is >= 1 and <= MemoryInput.MaxTagLength),
            "clear" => Words(4, t => t.ValueKind == JsonValueKind.String && MemoryChange.Clearable.Contains(t.GetString()!)),
            "key" => v.ValueKind == JsonValueKind.String && KeyPattern().IsMatch(v.GetString()!),
            "link" => v.ValueKind == JsonValueKind.String && MapDismissals.IsLink(v.GetString()),
            "dismissal" => v.ValueKind == JsonValueKind.String && MapDismissals.IsId(v.GetString()),
            "target" => Uuid(),
            "decision" => Word(Decisions),
            "type" => v.ValueKind == JsonValueKind.String && TypePattern().IsMatch(v.GetString()!),
            "since" => v.ValueKind == JsonValueKind.String && IsInstant(v.GetString()),
            "group" => v.ValueKind == JsonValueKind.String && GroupPattern().IsMatch(v.GetString()!),
            "name" => Text(MemoryProjectInput.MaxName),
            "slug" => v.ValueKind == JsonValueKind.String && MemoryProjectInput.IsSlug(v.GetString()),
            "description" => v.ValueKind == JsonValueKind.String && v.GetString()!.Length <= MemoryProjectInput.MaxDescription,
            "aliases" => Words(MemoryProjectInput.MaxAliases, t => t.ValueKind == JsonValueKind.String && t.GetString()!.Length is >= 1 and <= MemoryProjectInput.MaxAlias),
            "state" => Word(op == "decisions" ? [.. MemoryDecisions.Lists] : MemoryStore.InsightStates),
            "view" => Word(InsightStore.Views),
            "action" => Word(op switch
            {
                "resolve" => MemoryResolve.ReviewActions,
                "resolve_all" => MemoryResolve.BulkActions,
                "resolve_loop" => [.. MemoryLoops.Actions],
                "resolve_due" => [.. DueDates.Actions],
                _ => ReviewWords,
            }),
            "note" => Text(500),
            "insight" => Uuid(),
            "notHeld" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "direction" => Word(Directions),
            "window" => Word([.. DueDates.Windows]),
            "source" => Word(CalibrationSources),
            "result" => Word([.. MemoryDecisions.Results]),
            "until" => Text(40),
            // Words a list looks for: at most QueryMax characters as sent, as the listing's maxLength and every other text here count
            // them, and something besides spaces (the store trims them before it looks).
            "query" => Text(MemoryStore.QueryMax) && !string.IsNullOrWhiteSpace(v.GetString()),
            "items" => v.ValueKind == JsonValueKind.Array && v.GetArrayLength() is >= 1 and <= MemoryStore.ReleaseAllMax && v.EnumerateArray().All(Item),
            _ => false,
        };
    }

    // One item of a resolve of several: its id, and the revision its person read where a memory has one.
    static bool Item(JsonElement m) =>
        m.ValueKind == JsonValueKind.Object && m.EnumerateObject().Select(x => x.Name).ToArray() is var names
        && names.Contains("id") && names.All(n => n is "id" or "expected") && names.Length == names.Distinct(StringComparer.Ordinal).Count()
        && IsUuid(m.GetProperty("id"))
        && (!m.TryGetProperty("expected", out var e) || e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) && n >= 1);

    // One memory of a release_all: exactly its id and the revision its person reviewed.
    static bool Reviewed(JsonElement m) =>
        m.ValueKind == JsonValueKind.Object && m.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).SequenceEqual(["expected", "id"])
        && m.GetProperty("id") is { ValueKind: JsonValueKind.String } i && Guid.TryParseExact(i.GetString(), "D", out _)
        && m.GetProperty("expected") is { ValueKind: JsonValueKind.Number } e && e.TryGetInt32(out var n) && n >= 1;

    // One memory of a restore of several: exactly its id and the trash row its person saw.
    static bool InTrash(JsonElement m) =>
        m.ValueKind == JsonValueKind.Object && m.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).SequenceEqual(["id", "nonce"])
        && IsUuid(m.GetProperty("id")) && IsUuid(m.GetProperty("nonce"));

    // One memory of a delete of several: its id, the trash row's nonce and the person's confirmation, its own id.
    static bool ConfirmedGone(JsonElement m) =>
        m.ValueKind == JsonValueKind.Object && m.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).SequenceEqual(["confirm", "id", "nonce"])
        && IsUuid(m.GetProperty("id")) && IsUuid(m.GetProperty("nonce")) && m.GetProperty("confirm").ValueKind == JsonValueKind.String
        && m.GetProperty("confirm").GetString() == m.GetProperty("id").GetString();

    static bool IsUuid(JsonElement v) => v.ValueKind == JsonValueKind.String && Guid.TryParseExact(v.GetString(), "D", out _);

    // One call, its arguments checked by Allowed already. A refusal of the store's is {ok: false, code, reason}, status 200,
    // as an upload's refusal is; the database not answering is the endpoint's 503.
    public static async Task<(int Status, object? Result)> Call(IServiceProvider services, Guid principal, JsonElement args, CancellationToken ct)
    {
        var store = MemoryTools.Store(services, "app");
        var op = args.GetProperty("op").GetString()!;
        string? Text(string name) => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? Number(string name) => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        Guid? Uuid(string name) => Text(name) is { } t ? Guid.Parse(t) : null;
        string[]? Many(string name) => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Select(x => x.GetString()!)] : null;
        MemoryCall Way(bool keyed = false) => new(Channel, null, keyed ? Text("key") : null);
        var id = Uuid("id") ?? Guid.Empty;
        (Guid Id, int Revision)[] Revisions() => [.. args.GetProperty("memories").EnumerateArray()
            .Select(m => (Guid.Parse(m.GetProperty("id").GetString()!), m.GetProperty("expected").GetInt32()))];
        try
        {
            JsonObject answer = op switch
            {
                "list" => await List(store, principal, Text("scope"), Text("tag"), Text("kind"), Text("status"), Text("project"), Text("cursor"),
                    Number("limit") ?? ListDefault, ct, Text("query")),
                "trash" => await Trash(store, principal, Text("cursor"), Number("limit") ?? TrashDefault, ct),
                "get" => await Get(store, principal, id, Number("version"), ct),
                "history" => await History(store, principal, id, Number("before"), Number("limit") ?? MemoryStore.HistoryDefault, ct),
                "projects" => Projects(await store.Projects(principal, new(Text("scope"),
                    args.TryGetProperty("archived", out var archived) && archived.ValueKind == JsonValueKind.True), ct)),
                "review" => await Review(store, principal, Text("scope"), Text("reason"), Text("cursor"), Number("limit") ?? ReviewDefault, ct),
                "capture" => Written(op, await store.Create(principal, new MemoryDraft(Text("content")!,
                    Many("tags"), Text("scope"), Text("kind"), Number("importance"), Text("volatility"), Text("project")), Way(true), ct)),
                "append" => Written(op, await store.Append(principal, id, Text("text")!, Number("expected"), Way(true), ct)),
                "update" => Written(op, await store.Update(principal, id, new MemoryChange(Text("content"), Many("tags"), Text("kind"), Number("importance"),
                    Text("volatility"), Text("project"), Many("clear") is { } clear ? new HashSet<string>(clear, StringComparer.Ordinal) : null),
                    Number("expected"), Way(true), ct)),
                "status" => Written(op, await store.SetStatus(principal, id, Text("status")!, Number("expected"), Way(true), ct)),
                "release" => Written(op, await store.Release(principal, id, Number("expected"), Way(), ct)),
                "release_all" => ReleasedAll(await store.ReleaseAll(principal, Revisions(), Way(), ct)),
                "capsule" => Slotted(Slot(Text("capsule")), Text("slot"),
                    await store.SetCapsule(principal, id, Slot(Text("capsule")), Text("slot"), Number("expected"), Way(), ct)),
                "undo" => Written(op, await store.Undo(principal, id, Number("to"), Number("expected"), Way(), ct)),
                ShareOp => await Share(services, store, principal, id, Text("scope"), Number("expected"), Way(), ct),
                "dismissals" => Dismissals(await MapReady(() => MapDismissals.List(Host(services), store, principal, Number("limit") ?? MapDismissals.Default, ct))),
                "dismiss" => Dismissed(await MapReady(() => MapDismissals.Dismiss(Host(services), store, principal, Text("link")!, Way(), ct))),
                "undismiss" => Undismissed(await MapReady(() => MapDismissals.Restore(Host(services), store, principal, Text("dismissal")!, Way(), ct))),
                "connections" => Connections(await store.Connections(principal, id, Text("type"), Number("limit") ?? MemoryStore.ConnectionsDefault, ct)
                    ?? throw Unknown()),
                "stale" => await Stale(store, principal, Text("scope"), Text("cursor"), Number("limit") ?? StaleDefault, ct),
                "link" => Linked(await store.Link(principal, id, Uuid("target")!.Value, Text("type") ?? "relates_to", null, Way(), ct)),
                "unlink" => Unlinked(await store.Unlink(principal, id, Uuid("target")!.Value, Text("type"), Way(), ct)),
                "still_true" => Confirmed(await store.StillTrue(principal, id, Number("expected"), Way(), ct)),
                "clear_flags" => Written(op, await store.ClearFlags(principal, id, Number("expected"), Way(), ct)),
                "duplicates" => await Duplicates(store, principal, Text("scope"), Text("cursor"), Number("limit") ?? DuplicatesDefault, ct),
                "keep_copy" => Written(op, await store.KeepCopy(principal, id, Number("expected"), Way(), ct)),
                "contradictions" => await Contradictions(store, principal, Text("scope"), Text("cursor"), Number("limit") ?? ContradictionsDefault, Way(), ct),
                "brief" => Brief(await store.Brief(principal, Text("scope"), Text("project"), MemoryTools.Insights(services), Way(), ct)),
                "resurface" => Resurfaced(await store.Resurface(principal, ct)),
                "resurface_dismiss" => ResurfaceDismissed(await store.DismissResurfaced(principal, id, Way(), ct)),
                "settle_contradiction" => Settled(await store.Settle(principal, id, Uuid("target")!.Value, Text("decision")!, Number("expected"), Number("targetExpected"), Way(), ct)),
                "tags" => await Tagged(store, principal, new MemoryFilter(Text("scope"), Text("tag") is { } tag ? [tag] : null, Text("kind"), Text("status"), Text("project"),
                    Query: Text("query")), ct),
                "changes" => await Changes(store, principal, Instant(Text("since")), Text("scope"),
                    args.TryGetProperty("notHeld", out var notHeld) && notHeld.ValueKind == JsonValueKind.True, ct),
                "capsule_status" => await CapsuleStatus(services, principal, Text("capsule")!, Text("project"), Text("scope"), ct),
                "settings" => await Settings(services, principal, ct),
                "undo_group" => Regrouped(await store.UndoGroup(principal, Text("group")!, Way(), ct)),
                "forget_all" => ForgottenAll(await store.ForgetAll(principal, Revisions(), Way(), ct)),
                "restore_all" => RestoredAll(await store.RestoreAll(principal, [.. args.GetProperty("memories").EnumerateArray()
                    .Select(m => (Guid.Parse(m.GetProperty("id").GetString()!), Guid.Parse(m.GetProperty("nonce").GetString()!)))], Way(), ct)),
                "delete_all" => DeletedAll(await store.DeleteAll(principal, [.. args.GetProperty("memories").EnumerateArray()
                    .Select(m => (Guid.Parse(m.GetProperty("id").GetString()!), Guid.Parse(m.GetProperty("nonce").GetString()!), m.GetProperty("confirm").GetString()!))], Way(), ct)),
                ShareAllOp => await SharedAll(services, store, principal, Revisions(), Text("scope"), Way(), ct),
                "project_create" => ProjectWritten(op, await store.CreateProject(principal, new MemoryProjectDraft(Text("name")!, Text("slug"), Text("description"), Many("aliases"), Text("scope")), Way(), ct)),
                "project_update" => ProjectWritten(op, await store.UpdateProject(principal, id, new MemoryProjectChange(Text("name"), Text("description"), Many("aliases"),
                    args.TryGetProperty("archived", out var archive) ? archive.ValueKind == JsonValueKind.True ? "archived" : "active" : null), Number("expected"), Way(), ct)),
                "project_delete" => ProjectDeleted(await store.DeleteProject(principal, id, Number("expected"), Way(), ct)),
                "queues" => await QueueCounts(services, store, principal, Text("scope"), Way(), ct),
                "insight_candidates" => await Pairs(store, principal, Text("scope"), Text("state"), Text("cursor"), Number("limit") ?? PairsDefault, ct),
                "insight_stream" => await Stream(services, store, principal, Text("view"), Text("scope"), Text("cursor"), Number("limit") ?? StreamDefault, ct),
                "insight_history" => await InsightEvents(services, principal, id, Number("limit") ?? InsightHistoryDefault, ct),
                "review_insight" => ReviewedInsight(await MemoryTools.Insights(services).Review(principal, id, Text("action")!, Text("note"), Way(), ct)),
                "resolve" => ResolvedItem(await store.ResolveItem(principal, new(id, Text("action")!, Note: Text("note"), Insight: Uuid("insight"), Conflict: Uuid("target"),
                    Expected: Number("expected"), ConflictExpected: Number("targetExpected"),
                    Copies: args.TryGetProperty("copies", out var copies) ? [.. copies.EnumerateArray()
                        .Select(m => new MemoryCopyRead(Guid.Parse(m.GetProperty("id").GetString()!), m.GetProperty("expected").GetInt32()))] : null),
                    MemoryTools.Insights(services), Way(), ct)),
                "loops" => await LoopList(store, principal, Text("scope"), Text("direction"), Text("cursor"), Number("limit") ?? PlansDefault, ct),
                "due" => await DueList(store, principal, Text("scope"), Text("window"), Text("cursor"), Number("limit") ?? PlansDefault, ct),
                "decisions" => await DecisionList(store, principal, Text("scope"), Text("state"), Text("cursor"), Number("limit") ?? PlansDefault, ct),
                "calibration" => Calibrated(await store.Calibration(principal, Text("scope"), Text("source"), ct), Text("source")),
                "resolve_loop" => LoopResolved(await store.Resolve(principal, id, Text("action")!, Number("expected"), Way(), ct)),
                "resolve_due" => DueResolved(await store.ResolveDue(principal, id, Text("action")!, Text("until"), Number("expected"), Way(), ct)),
                "record_outcome" => OutcomeRecorded(await store.RecordOutcome(principal, id, Text("result")!, Number("expected"), Way(), ct)),
                "standing" => await StandingList(services, store, principal, Text("scope"), Text("project"), Text("cursor"), Number("limit") ?? StandingDefault, ct),
                "stop_standing" => Written(op, await store.StopStanding(principal, id, Number("expected"), Way(), ct)),
                "resolve_all" => ResolvedAll(Text("action")!, await store.ResolveAll(principal, Text("action")!, [.. args.GetProperty("items").EnumerateArray()
                    .Select(i => (Guid.Parse(i.GetProperty("id").GetString()!), i.TryGetProperty("expected", out var e) ? e.GetInt32() : (int?)null))],
                    MemoryTools.Insights(services), Way(), ct)),
                "forget" => Forgotten(await store.Forget(principal, id, Number("expected"), Way(true), ct)),
                "restore" => Restored(await store.Restore(principal, id, Uuid("nonce"), Way(), ct)),
                _ => Deleted(await store.DeleteForever(principal, id, Uuid("nonce")!.Value, Text("confirm")!, Way(), ct)),
            };
            answer["ok"] = true;
            return (200, JsonRpc.Result(Ordered(answer), Summary(op, answer)));
        }
        catch (MemoryRefused refused)
        {
            var body = new JsonObject { ["ok"] = false, ["code"] = refused.Code, ["reason"] = Reason(refused) };
            if (refused.Revision is { } revision) body["revision"] = revision;
            if (refused.Known is { } known) body["known"] = new JsonArray([.. known.Select(k => (JsonNode?)k)]);
            return (200, JsonRpc.Result(body, body["reason"]!.GetValue<string>()));
        }
        catch (ArgumentException e)
        {
            var body = new JsonObject { ["ok"] = false, ["code"] = "invalid", ["reason"] = e.Message };
            return (200, JsonRpc.Result(body, e.Message));
        }
    }

    // The dismissal ops read the map (RelationshipHost.ShownView): while it is not ready yet (the host's
    // UnauthorizedAccessException) or a pass is past its budget (InvalidOperationException), they answer unavailable, as
    // the store does when it does not answer, never forbidden, which the app's server takes to mean memory.tools went off.
    static async Task<T> MapReady<T>(Func<Task<T>> read)
    {
        try { return await read(); }
        catch (Exception e) when (e is UnauthorizedAccessException or InvalidOperationException)
        { throw new MemoryRefused("unavailable", "The map isn't ready just now, so dismissals can't be read or kept: try again in a moment."); }
    }

    // The store's words, but for its "Unknown memory", which a person reads better whole.
    static string Reason(MemoryRefused refused) => refused.Code == "unknown" && refused.Message == "Unknown memory"
        ? "No memory with that id that you can read: it may be in the trash, in a scope you do not read, or deleted for good."
        : refused.Message;

    static string Summary(string op, JsonObject answer) => op switch
    {
        "list" => $"{answer["memories"]!.AsArray().Count} memories; they are in structuredContent.",
        "trash" => $"{answer["trash"]!.AsArray().Count} memories in the trash; they are in structuredContent.",
        "get" => "The memory is in structuredContent.",
        "history" => $"{answer["changes"]!.AsArray().Count} changes; they are in structuredContent.",
        "projects" => $"{answer["projects"]!.AsArray().Count} projects; they are in structuredContent.",
        "review" => $"{answer["review"]!.AsArray().Count} memories held for review; they are in structuredContent.",
        "dismissals" => $"{answer["dismissals"]!.AsArray().Count} dismissed suggestions; they are in structuredContent.",
        "connections" => $"{answer["connections"]!.AsArray().Count} connections; they are in structuredContent.",
        "stale" => $"{answer["stale"]!.AsArray().Count} memories that may be out of date; they are in structuredContent.",
        "duplicates" => $"{answer["duplicates"]!.AsArray().Count} memories flagged as possible copies; they are in structuredContent.",
        "contradictions" => $"{answer["contradictions"]!.AsArray().Count} possible contradictions to review; they are in structuredContent.",
        "brief" => answer["says"]!.GetValue<string>() + " It is in structuredContent.",
        "resurface" => answer["pick"] is null ? "No old memory to read again today." : "One old memory to read again; it is in structuredContent.",
        "tags" => $"{answer["tags"]!.AsArray().Count} tags on {answer["count"]!.GetValue<int>()} memories; they are in structuredContent.",
        "changes" => $"{answer["groups"]!.AsArray().Count} groups of AI changes; they are in structuredContent.",
        "capsule_status" => "The capsule's status is in structuredContent.",
        "settings" => "The memory settings are in structuredContent.",
        "queues" => "What waits in each review queue is in structuredContent.",
        "insight_candidates" => $"{answer["candidates"]!.AsArray().Count} pairs of memories worth a look; they are in structuredContent.",
        "insight_stream" => $"{answer["insights"]!.AsArray().Count} insights; they are in structuredContent.",
        "insight_history" => $"{answer["events"]!.AsArray().Count} events; they are in structuredContent.",
        "loops" => $"{answer["loops"]!.AsArray().Count} open loops of {answer["count"]!.GetValue<int>()}; they are in structuredContent.",
        "due" => $"{answer["due"]!.AsArray().Count} memories with a due date of {answer["count"]!.GetValue<int>()}; they are in structuredContent.",
        "decisions" => $"{answer["decisions"]!.AsArray().Count} decisions of {answer["count"]!.GetValue<int>()}; they are in structuredContent.",
        "calibration" => "Calibration is in structuredContent.",
        "standing" => $"{answer["standing"]!.AsArray().Count} standing instructions of {answer["count"]!.GetValue<int>()}; they are in structuredContent.",
        _ =>$"The {op} is done; what it did is in structuredContent.",
    };

    // ok first, as every answer of the contract reads.
    static JsonObject Ordered(JsonObject answer)
    {
        var ordered = new JsonObject { ["ok"] = answer["ok"]?.DeepClone() };
        foreach (var (name, value) in answer.ToList()) if (name != "ok") ordered[name] = value?.DeepClone();
        return ordered;
    }

    static async Task<JsonObject> List(MemoryStore store, Guid principal, string? scope, string? tag, string? kind, string? status, string? project,
        string? cursor, int limit, CancellationToken ct, string? query = null)
    {
        var at = MemoryTools.Cursor(cursor);
        var page = await store.List(principal, new MemoryFilter(scope, tag is null ? null : [tag], kind, status, project, null, null, null, null, limit,
            at is { } a ? new MemoryCursor(a.At, a.Id) : null, ListPreview, query), ct);
        return new()
        {
            ["memories"] = new JsonArray([.. page.Items.Select(m => (JsonNode)Memory(m))]),
            ["next"] = page.Next is { } next ? MemoryTools.Cursor(next.CreatedAt, next.Id) : null,
        };
    }

    static async Task<JsonObject> Trash(MemoryStore store, Guid principal, string? cursor, int limit, CancellationToken ct)
    {
        var at = MemoryTools.Cursor(cursor);
        var page = await store.Trash(principal, limit, at is { } t ? new MemoryTrashCursor(t.At, t.Id) : null, ListPreview, new(Channel), ct);
        return new()
        {
            ["trash"] = new JsonArray([.. page.Items.Select(t => (JsonNode)new JsonObject
            {
                ["id"] = t.Id.ToString(), ["scope"] = t.Scope, ["owner"] = t.Subject, ["author"] = t.Author, ["source"] = t.Source, ["status"] = t.Status,
                ["preview"] = t.Preview, ["truncated"] = t.Truncated, ["held"] = Held(t.Hold), ["reason"] = t.Reason, ["channel"] = t.Channel,
                ["trashedBy"] = t.TrashedBy, ["trashedAt"] = t.TrashedAt, ["purgeAfter"] = t.PurgeAfter, ["daysLeft"] = t.DaysLeft,
                ["nonce"] = t.Nonce.ToString(), ["mayDeleteForever"] = t.MayDeleteForever, ["revision"] = t.Revision,
            })]),
            ["next"] = page.Next is { } next ? MemoryTools.Cursor(next.TrashedAt, next.Id) : null,
        };
    }

    static async Task<JsonObject> Get(MemoryStore store, Guid principal, Guid id, int? version, CancellationToken ct)
    {
        if (version is not { } revision)
            return new() { ["memory"] = Memory(await store.Get(principal, id, ct) ?? throw Unknown()) };
        var read = await store.GetVersion(principal, id, revision, ct) ?? throw Unknown();
        if (read.Version is not { } v)
            throw read.Missing switch
            {
                "not_visible" => new MemoryRefused("not_visible", $"No revision {revision} of this memory is visible to you."),
                "pruned" => new MemoryRefused("pruned", $"Revision {revision} is no longer kept; the oldest kept is {read.OldestKept}.", read.OldestKept),
                _ => new MemoryRefused("no_version", $"This memory has no revision {revision}."),
            };
        return new()
        {
            ["version"] = new JsonObject
            {
                ["id"] = v.Id.ToString(), ["revision"] = v.Revision, ["current"] = v.Current, ["scope"] = v.Scope, ["owner"] = v.Subject, ["status"] = v.Status,
                // A state that was held comes without its words, tags or project, as history serves it (034), unless a
                // later state released the same text (the store's Held is false then).
                ["kind"] = v.Kind, ["importance"] = v.Importance, ["volatility"] = v.Volatility, ["tags"] = Strings(v.Held ? [] : v.Tags),
                ["project"] = v.Held ? null : Project(v.Project), ["content"] = v.Held ? null : v.Content, ["held"] = v.Held ? Held(v.Hold ?? "") : null, ["validFrom"] = v.ValidFrom, ["unknownStart"] = v.UnknownStart,
                ["validUntil"] = v.ValidUntil, ["writtenAt"] = v.WrittenAt, ["retiredAt"] = v.RetiredAt, ["person"] = v.Person, ["channel"] = v.Channel,
                ["client"] = v.Client,
            },
        };
    }

    static async Task<JsonObject> History(MemoryStore store, Guid principal, Guid id, int? before, int limit, CancellationToken ct)
    {
        var h = await store.History(principal, id, before, limit, ct) ?? throw Unknown();
        return new()
        {
            ["id"] = h.Id.ToString(), ["revision"] = h.Revision,
            ["changes"] = new JsonArray([.. h.Items.Select(i => (JsonNode)new JsonObject
            {
                ["revision"] = i.Revision, ["event"] = i.Event, ["at"] = i.At, ["person"] = i.Person, ["channel"] = i.Channel, ["client"] = i.Client,
                ["fields"] = Strings(i.Fields), ["scope"] = i.Scope, ["owner"] = i.Subject, ["kept"] = i.Kept, ["status"] = i.Status,
                ["preview"] = i.Held ? null : i.Preview, ["held"] = i.Held, ["to"] = i.To,
            })]),
            ["next"] = h.Next, ["cut"] = h.Cut, ["earlierBy"] = h.EarlierBy, ["kept"] = h.Kept, ["oldestKept"] = h.OldestKept, ["pruned"] = h.Pruned,
        };
    }

    // A share or a take-back (SB-P3-10, MemoryStore.Share) as the app: the store decides who may and where it lands. A
    // write's answer with projectLeft (it had a project that did not come with it). After a move the map is asked for a
    // rebuild within the share tool's bound (RefreshRequests.QueueBounded, at most one every three minutes), which lists
    // the memory where it is now, since the move dropped its star rows. AppEndpoint takes the op only with memory.share
    // on; a direct call with it off changes nothing.
    static async Task<JsonObject> Share(IServiceProvider services, MemoryStore store, Guid principal, Guid id, string? scope, int? expected,
        MemoryCall call, CancellationToken ct)
    {
        if (!MemoryTools.ShareOn(services)) throw new ArgumentException("Sharing is off in this install (memory.share)");
        var moved = await store.Share(principal, id, scope, expected, call, ct);
        if (!moved.Unchanged)
            try { await new RefreshRequests(services.GetRequiredService<Database>().Source).QueueBounded(ct); }
            catch (Exception e) when (e is not OperationCanceledException) { services.GetService<RequestLog>()?.Failed("app", Name, 503, "rebuild", e); }
        var answer = Written(ShareOp, moved);
        answer["projectLeft"] = moved.ProjectLeft;
        return answer;
    }

    // The review queue (MemoryStore.Review, SB-P5-01): what a person looks at before releasing, never the words, tags or
    // project, which get serves whole; mayRelease is for this channel (the app's, a person's), the author lock deciding.
    // count is the length of the whole queue by the same predicates (ReviewCount), the cursor and the limit aside.
    static async Task<JsonObject> Review(MemoryStore store, Guid principal, string? scope, string? reason, string? cursor, int limit, CancellationToken ct)
    {
        var at = MemoryTools.Cursor(cursor);
        var filter = new MemoryReviewFilter(scope, reason, limit, at is { } a ? new MemoryReviewCursor(a.At, a.Id) : null);
        var page = await store.Review(principal, filter, new(Channel), ct);
        return new()
        {
            ["review"] = new JsonArray([.. page.Items.Select(i => (JsonNode)new JsonObject
            {
                ["id"] = i.Id.ToString(), ["scope"] = i.Scope, ["owner"] = i.Subject, ["author"] = i.Author, ["source"] = i.Source, ["status"] = i.Status,
                ["reason"] = i.Reason, ["score"] = i.Score, ["signals"] = Strings(i.Signals), ["heldAt"] = i.HeldAt, ["heldBy"] = i.HeldBy,
                ["channel"] = i.Channel, ["client"] = i.Client, ["mayRelease"] = i.MayRelease, ["revision"] = i.Revision,
            })]),
            ["next"] = page.Next is { } next ? MemoryTools.Cursor(next.HeldAt, next.Id) : null,
            ["count"] = await store.ReviewCount(principal, filter with { After = null }, ct),
        };
    }

    // Several releases, each answered on its own: released, unchanged (it was not held), unknown, forbidden or conflict (it
    // changed since the revision its person reviewed, and stays held; revision is the one it is at).
    static JsonObject ReleasedAll(IReadOnlyList<MemoryReleaseResult> results) => new()
    {
        ["op"] = "release_all",
        ["results"] = new JsonArray([.. results.Select(r => (JsonNode)new JsonObject
        {
            ["id"] = r.Id.ToString(), ["result"] = r.Result, ["revision"] = r.Revision,
        })]),
    };

    // The capsule a capsule op names: core or project, or null for none (out of its slot).
    static string? Slot(string? capsule) => capsule == None ? null : capsule;

    // A capsule op's answer: the write's, and the slot the memory is in now (none when taken out), which is the one asked
    // for whether this write moved it there or it was there already (unchanged).
    static JsonObject Slotted(string? capsule, string? slot, MemoryWrite w)
    {
        var answer = Written("capsule", w);
        answer["capsule"] = capsule is null ? null : new JsonObject { ["kind"] = capsule, ["slot"] = slot };
        return answer;
    }

    static JsonObject Projects(IReadOnlyList<MemoryProject> projects) => new()
    {
        ["projects"] = new JsonArray([.. projects.Select(p => (JsonNode)ProjectRow(p))]),
    };

    // A memory as the app is served it: whole from get, a held one with its words and its hold, for a person to review; a
    // preview in a list, a held one without its words (the store's Select). capsule: the slot a person put it in, which a
    // capsule serves only while it is canonical and not held (Core/Capsules.cs); none for a held one in a list. state,
    // replacedBy and retractedSources (migration 049, SB-P4-11): what it is now, what replaced it (only while replaced) and
    // the retracted memories of its scope that flagged it, as the MCP get serves them; they hold no words, so a held one in
    // a list carries them too. duplicateOf (migration 063): the memories of its scope it was flagged as a copy of on save
    // that stand now, closest first, only when it has any (never for a held one).
    static JsonObject Memory(MemoryRecord m) => Standing(Copies(new()
    {
        ["id"] = m.Id.ToString(), ["scope"] = m.Scope, ["owner"] = m.Subject, ["author"] = m.Author, ["source"] = m.Source, ["kind"] = m.Kind,
        ["importance"] = m.Importance, ["volatility"] = m.Volatility, ["status"] = m.Status, ["tags"] = Strings(m.Tags), ["project"] = Project(m.Project),
        ["content"] = m.Content, ["truncated"] = m.Truncated, ["held"] = Held(m.Hold), ["validFrom"] = m.ValidFrom, ["unknownStart"] = m.UnknownStart,
        ["validUntil"] = m.ValidUntil, ["revision"] = m.Revision, ["createdAt"] = m.CreatedAt, ["updatedAt"] = m.UpdatedAt,
        ["capsule"] = m.Capsule is { } kind ? new JsonObject { ["kind"] = kind, ["slot"] = m.Slot } : null,
        ["state"] = m.State, ["replacedBy"] = m.State == "replaced" ? m.ReplacedBy?.ToString() : null,
        ["retractedSources"] = Strings((m.RetractedSources ?? []).Select(s => s.ToString())),
    }, "duplicateOf", m.Hold is null ? m.DuplicateOf : null), "contradictions", m.Hold is null ? m.Contradictions : null);

    // The copies a memory names (SB-P5-02), under `name`, only when it names any, so every other answer is as before.
    static JsonObject Copies(JsonObject answer, string name, IReadOnlyList<MemoryCopy>? copies)
    {
        if (copies is { Count: > 0 }) answer[name] = CopyList(copies);
        return answer;
    }

    static JsonArray CopyList(IReadOnlyList<MemoryCopy> copies) =>
        new([.. copies.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id.ToString(), ["score"] = c.Score })]);

    // A write's answer: what it did, never the memory's words; with what its retraction cascade did, when it did something,
    // the copies a write was flagged for (duplicates), when it was, and what may contradict it (contradictionCandidates).
    static JsonObject Written(string op, MemoryWrite w) => Standing(Copies(Cascaded(new()
    {
        ["op"] = op, ["id"] = w.Id.ToString(), ["revision"] = w.Revision, ["to"] = w.To, ["scope"] = w.Scope, ["owner"] = w.Subject, ["status"] = w.Status,
        ["scopes"] = w.Scopes,
        ["project"] = w.Project is { } p ? new JsonObject { ["id"] = p.Id.ToString(), ["slug"] = p.Slug, ["created"] = p.Created } : null,
        ["changed"] = Strings(w.Changed), ["unchanged"] = w.Unchanged, ["replayed"] = w.Replayed, ["droppedTags"] = Strings(w.DroppedTags),
        ["held"] = w.Hold is { } h ? Held(h.Reason, h.Signals) : null,
    }, w.Cascade), "duplicates", w.Hold is null ? w.Duplicates : null), "contradictionCandidates", w.Hold is null ? w.Candidates : null);

    // What stands against a memory as a possible contradiction (SB-P5-03), under `name`, only when anything does, so every
    // other answer is as before.
    static JsonObject Standing(JsonObject answer, string name, IReadOnlyList<MemoryCandidate>? candidates)
    {
        if (candidates is { Count: > 0 })
            answer[name] = new JsonArray([.. candidates.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id.ToString(), ["score"] = c.Score, ["proposed"] = c.Proposed })]);
        return answer;
    }

    static JsonObject Forgotten(MemoryTrashed t) => Cascaded(new()
    {
        ["op"] = "forget", ["id"] = t.Id.ToString(), ["revision"] = t.Revision, ["scope"] = t.Scope, ["owner"] = t.Subject, ["status"] = t.Status,
        ["scopes"] = t.Scopes, ["replayed"] = t.Replayed,
        ["trash"] = new JsonObject
        {
            ["nonce"] = t.Trash.Nonce.ToString(), ["trashedAt"] = t.Trash.TrashedAt, ["purgeAfter"] = t.Trash.PurgeAfter,
            ["days"] = (int)Math.Ceiling((t.Trash.PurgeAfter - t.Trash.TrashedAt).TotalDays), ["links"] = t.Trash.Links,
        },
    }, t.Cascade);

    static JsonObject Restored(MemoryRestored r) => Cascaded(new()
    {
        ["op"] = "restore", ["id"] = r.Id.ToString(), ["revision"] = r.Revision, ["scope"] = r.Scope, ["owner"] = r.Subject, ["status"] = r.Status,
        ["scopes"] = r.Scopes, ["replayed"] = r.Replayed, ["held"] = r.Hold is { } h ? Held(h.Reason) : null, ["links"] = r.Links,
        ["linksGone"] = r.LinksGone, ["trashedAt"] = r.TrashedAt,
    }, r.Cascade);

    // What a change's retraction cascade did (migration 049, SB-P4-08), as the MCP tools say it in words
    // (MemoryTools.Cascade) and the terminal prints it: the memories it had replaced that are current again or replaced by
    // it again, and those built on it flagged for a check or no longer. Added only when it did something, so every other
    // answer is as before.
    static JsonObject Cascaded(JsonObject answer, MemoryCascade? c)
    {
        if (c is null || c.Reopened.Count + c.Reclosed.Count + c.Flagged.Count + c.Unflagged.Count == 0) return answer;
        JsonArray Ids(IReadOnlyList<Guid> ids) => Strings(ids.Select(i => i.ToString()));
        answer["cascade"] = new JsonObject
        {
            ["reopened"] = Ids(c.Reopened), ["reclosed"] = Ids(c.Reclosed), ["flagged"] = Ids(c.Flagged), ["unflagged"] = Ids(c.Unflagged),
        };
        return answer;
    }

    static JsonObject Deleted(MemoryDeleted d) => new()
    {
        ["op"] = "delete", ["id"] = d.Id.ToString(), ["owner"] = d.Subject, ["links"] = d.Links, ["versions"] = d.Versions, ["scopes"] = d.Scopes,
    };

    // A hold as the app is told it: why (instruction, hidden, too_long, burst or held_project, migrations 036, 045 and
    // 046), and the signals this write's screening found. The reason is passed as the store gives it.
    static JsonObject? Held(string? reason, IReadOnlyList<string>? signals = null) =>
        reason is null ? null : new JsonObject { ["reason"] = reason, ["signals"] = Strings(signals ?? []) };

    static JsonObject? Project(MemoryProjectRef? p) =>
        p is null ? null : new JsonObject { ["id"] = p.Id.ToString(), ["slug"] = p.Slug, ["name"] = p.Name };

    static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode?)v)]);

    static MemoryRefused Unknown() => new("unknown", "Unknown memory");

    // The map, for the dismissal ops: the engine's one host (Program.cs), as the harness's map tools take it.
    static Relationships.RelationshipHost Host(IServiceProvider services) => services.GetRequiredService<Relationships.RelationshipHost>();

    // The dismissals on this caller's map (MapDismissals.List): each by its id, with its two stars as the map names them,
    // the link type, when, by whom (person: a person's channel, the app's login or the terminal's; ai_client: an AI
    // client's unlink), whether this caller's own login or person made it, and whether this caller may take it back.
    static JsonObject Dismissals((IReadOnlyList<MapDismissal> Items, bool Truncated) page) => new()
    {
        ["dismissals"] = new JsonArray([.. page.Items.Select(d => (JsonNode)new JsonObject
        {
            ["id"] = d.Id, ["kind"] = d.Kind, ["type"] = d.Type,
            ["stars"] = new JsonArray(new JsonObject { ["id"] = d.First.Id, ["title"] = d.First.Title },
                new JsonObject { ["id"] = d.Second.Id, ["title"] = d.Second.Title }),
            ["dismissedAt"] = d.At, ["by"] = d.ByPerson ? "person" : "ai_client", ["mine"] = d.Mine, ["mayRestore"] = d.MayRestore,
        })]),
        ["truncated"] = page.Truncated,
    };

    // Discover's Dismiss (MapDismissals.Dismiss): the dismissal now kept, by its id (null when a stated made link was
    // removed and nothing is kept), and whether a made link was removed.
    static JsonObject Dismissed(MapDismissed d) => new()
    {
        ["op"] = "dismiss", ["link"] = d.Link, ["dismissal"] = d.Dismissal, ["removed"] = d.Removed, ["unchanged"] = d.Unchanged, ["scopes"] = d.Scopes,
    };

    static JsonObject Undismissed(MapRestored r) => new()
    {
        ["op"] = "undismiss", ["dismissal"] = r.Dismissal, ["unchanged"] = r.Unchanged, ["back"] = r.Back, ["scopes"] = r.Scopes,
    };

    // A memory's connections (MemoryStore.Connections), as the MCP connections tool serves them, in the app's words: each
    // a made link (linkId, made) or the map's suggestion between the two memories' stars (no linkId), its type and its
    // direction from this memory, who made it (verification; mine: this login or its person; imported: an import brought
    // it, with no maker here), whether this caller may
    // unlink it (a suggestion: dismiss it), when, its confidence, and the other memory as its reader is served it, a held
    // one without its words.
    static JsonObject Connections(MemoryConnections found) => new()
    {
        ["id"] = found.Id.ToString(),
        ["connections"] = new JsonArray([.. found.Items.Select(c => (JsonNode)new JsonObject
        {
            ["linkId"] = c.LinkId?.ToString(), ["made"] = c.Made, ["type"] = c.Type, ["direction"] = c.Direction, ["verification"] = c.Verification,
            ["mine"] = c.Mine, ["imported"] = c.Imported, ["mayUnlink"] = c.MayUnlink, ["at"] = c.At, ["confidence"] = c.Confidence,
            ["memory"] = new JsonObject
            {
                ["id"] = c.Memory.Id.ToString(), ["scope"] = c.Memory.Scope, ["owner"] = c.Memory.Subject, ["status"] = c.Memory.Status,
                ["state"] = c.Memory.State, ["kind"] = c.Memory.Kind, ["preview"] = c.Memory.Preview, ["truncated"] = c.Memory.Truncated,
                ["held"] = Held(c.Memory.Hold),
            },
        })]),
        ["truncated"] = found.Truncated,
    };

    // The stale queue this caller acts on (MemoryStore.Stale, 053's one predicate): the memories it may change that the
    // nightly pass found out of date (not_confirmed) or that a retracted source flagged (retracted_source), longest
    // unchecked first; count is the whole queue's length from the same read, so it is the length of the list.
    static async Task<JsonObject> Stale(MemoryStore store, Guid principal, string? scope, string? cursor, int limit, CancellationToken ct)
    {
        var at = MemoryTools.Cursor(cursor);
        var page = await store.Stale(principal, scope, limit, at is { } a ? new MemoryStaleCursor(a.At, a.Id) : null, ListPreview, ct);
        return new()
        {
            ["stale"] = new JsonArray([.. page.Items.Select(i => (JsonNode)new JsonObject
            {
                ["id"] = i.Id.ToString(), ["scope"] = i.Scope, ["owner"] = i.Subject, ["author"] = i.Author, ["status"] = i.Status,
                ["volatility"] = i.Volatility, ["reason"] = i.Reason, ["since"] = i.Since, ["foundAt"] = i.FoundAt, ["confirmedAt"] = i.ConfirmedAt,
                ["preview"] = i.Content, ["truncated"] = i.Truncated, ["retractedSources"] = Strings(i.RetractedSources.Select(s => s.ToString())),
                ["revision"] = i.Revision,
            })]),
            ["next"] = page.Next is { } next ? MemoryTools.Cursor(next.Since, next.Id) : null,
            ["count"] = page.Total,
        };
    }

    // The duplicate review this caller acts on (MemoryStore.Duplicates, 063's one predicate): the memories it may change that
    // were flagged on save as close to another of their scope, newest first; count is the whole list's length from the same
    // read, so it is the length of the list.
    static async Task<JsonObject> Duplicates(MemoryStore store, Guid principal, string? scope, string? cursor, int limit, CancellationToken ct)
    {
        var at = MemoryTools.Cursor(cursor);
        var page = await store.Duplicates(principal, scope, limit, at is { } a ? new MemoryCursor(a.At, a.Id) : null, ListPreview, ct);
        return new()
        {
            ["duplicates"] = new JsonArray([.. page.Items.Select(i => (JsonNode)new JsonObject
            {
                ["id"] = i.Id.ToString(), ["scope"] = i.Scope, ["owner"] = i.Subject, ["author"] = i.Author, ["status"] = i.Status,
                ["createdAt"] = i.CreatedAt, ["preview"] = i.Content, ["truncated"] = i.Truncated, ["duplicateOf"] = CopyList(i.Copies),
                ["revision"] = i.Revision,
            })]),
            ["next"] = page.Next is { } next ? MemoryTools.Cursor(next.CreatedAt, next.Id) : null,
            ["count"] = page.Total,
        };
    }

    // The contradiction review this caller acts on (MemoryStore.Contradictions, 068's one predicate), newest first; count is
    // the whole list's length from the same read, so it is the length of the list.
    static async Task<JsonObject> Contradictions(MemoryStore store, Guid principal, string? scope, string? cursor, int limit, MemoryCall call, CancellationToken ct)
    {
        var at = MemoryTools.Cursor(cursor);
        var page = await store.Contradictions(principal, scope, limit, at is { } a ? new MemoryCursor(a.At, a.Id) : null, ListPreview, call, ct);
        static JsonObject Side(MemoryContradictionSide s) => new()
        {
            ["id"] = s.Id.ToString(), ["revision"] = s.Revision, ["author"] = s.Author, ["status"] = s.Status, ["preview"] = s.Content, ["truncated"] = s.Truncated, ["startedAt"] = s.StartedAt,
        };
        return new()
        {
            ["contradictions"] = new JsonArray([.. page.Items.Select(k => (JsonNode)new JsonObject
            {
                ["scope"] = k.Scope, ["owner"] = k.Subject, ["score"] = k.Score, ["offeredAt"] = k.OfferedAt, ["memory"] = Side(k.Memory), ["conflict"] = Side(k.Conflict),
                ["proposed"] = k.ProposedAt is { } when ? new JsonObject { ["at"] = when, ["person"] = k.ProposedBy, ["client"] = k.ProposedClient, ["reason"] = k.Reason } : null,
                ["maySettle"] = k.MaySettle,
            })]),
            ["next"] = page.Next is { } next ? MemoryTools.Cursor(next.CreatedAt, next.Id) : null,
            ["count"] = page.Total,
        };
    }

    // The day's old memory for the app's Home card (SB-P5-17, MemoryResurface.cs), or none: what a card needs, and never a held
    // memory, so it has its words.
    static JsonObject Resurfaced(MemoryResurfacePick p) => new()
    {
        ["day"] = p.Day,
        ["pick"] = p.Pick is not { } m ? null : new JsonObject
        {
            ["id"] = m.Id.ToString(), ["revision"] = m.Revision, ["scope"] = m.Scope, ["owner"] = m.Subject, ["preview"] = m.Content, ["truncated"] = m.Truncated,
            ["createdAt"] = m.CreatedAt,
        },
    };

    // A person's dismissal of it: this reader's for good, and unchanged when it was so already.
    static JsonObject ResurfaceDismissed(MemoryResurfaceDismissed d) => new()
    {
        ["op"] = "resurface_dismiss", ["id"] = d.Id.ToString(), ["unchanged"] = d.Unchanged,
    };

    // The brief for the app's Home card (SB-P5-08, MemoryBrief.cs): each section's count, the whole length of the list its tool
    // walks, and its head, item for item what the MCP brief shows. An item is what a card needs: its id, its revision, a preview
    // and the words that place it. No section holds a held or trashed memory, so every item has its words.
    static JsonObject Brief(MemoryBrief b)
    {
        static JsonObject Item(Guid id, int revision, string scope, string owner, string preview, bool truncated) => new()
        {
            ["id"] = id.ToString(), ["revision"] = revision, ["scope"] = scope, ["owner"] = owner, ["preview"] = preview, ["truncated"] = truncated,
        };
        static JsonObject Section<T>(MemoryBriefList<T> list, Func<T, JsonObject> item) => new()
        {
            ["count"] = list.Total, ["items"] = new JsonArray([.. list.Items.Select(i => (JsonNode)item(i))]),
        };
        static JsonObject Dated<T>(MemoryBriefDated<T> list, Func<T, JsonObject> item) => new()
        {
            ["count"] = list.Total, ["overdue"] = list.Overdue, ["upcoming"] = list.Upcoming, ["items"] = new JsonArray([.. list.Items.Select(i => (JsonNode)item(i))]),
        };
        static JsonObject Loop(MemoryLoop i)
        {
            var o = Item(i.Id, i.Revision, i.Scope, i.Subject, i.Content, i.Truncated);
            o["counterparty"] = MemoryLoops.Name(i.Counterparty);
            return o;
        }
        return new()
        {
            ["project"] = b.Project,
            ["unknownProject"] = b.UnknownProject,
            ["zone"] = b.Zone,
            ["says"] = b.Says,
            ["due"] = Dated(b.Due, i =>
            {
                var o = Item(i.Id, i.Revision, i.Scope, i.Subject, i.Content, i.Truncated);
                o["window"] = i.Window;
                o["at"] = i.Due.At;
                o["local"] = i.Due.Local;
                return o;
            }),
            ["decisions"] = Dated(b.Decisions, i =>
            {
                var o = Item(i.Id, i.Revision, i.Scope, i.Subject, i.Content, i.Truncated);
                o["window"] = i.Window;
                o["at"] = i.ReviewAt;
                return o;
            }),
            ["youOwe"] = Section(b.YouOwe, Loop),
            ["owedToYou"] = Section(b.OwedToYou, Loop),
            ["stale"] = Section(b.Stale, i =>
            {
                var o = Item(i.Id, i.Revision, i.Scope, i.Subject, i.Content, i.Truncated);
                o["reason"] = i.Reason;
                return o;
            }),
            ["insights"] = Section(b.Insights, i => new JsonObject
            {
                ["id"] = i.Id.ToString(), ["scope"] = i.Scope, ["owner"] = i.Owner,
                ["title"] = i.Insight.GetProperty("title").GetString(),
            }),
            ["contradictions"] = Section(b.Contradictions, k =>
            {
                var o = Item(k.Memory.Id, k.Memory.Revision, k.Scope, k.Subject, k.Memory.Content, k.Memory.Truncated);
                o["conflict"] = new JsonObject { ["id"] = k.Conflict.Id.ToString(), ["preview"] = k.Conflict.Content, ["truncated"] = k.Conflict.Truncated };
                return o;
            }),
            ["duplicates"] = Section(b.Duplicates, i => Item(i.Id, i.Revision, i.Scope, i.Subject, i.Content, i.Truncated)),
            ["held"] = b.Held,
            // A standing instruction comes whole, as the MCP tool's list serves it: a card's preview is as short as the others'.
            ["standing"] = Section(b.Standing, i =>
            {
                var runes = i.Content!.EnumerateRunes().ToList();
                return Item(i.Id, i.Revision, i.Scope, i.Subject, string.Concat(runes.Take(MemoryStore.BriefPreview)), runes.Count > MemoryStore.BriefPreview);
            }),
            ["calibration"] = new JsonObject { ["ready"] = b.Calibration.Ready, ["n"] = b.Calibration.N, ["line"] = b.Calibration.Ready ? b.Calibration.Line : null },
            ["changes"] = new JsonObject
            {
                ["count"] = b.Changes.Total, ["changes"] = b.Changes.Changes, ["truncated"] = b.Changes.Truncated,
                ["groups"] = new JsonArray([.. b.Changes.Groups.Select(g => (JsonNode)new JsonObject
                {
                    ["group"] = g.Group, ["family"] = g.Family, ["client"] = g.Client, ["changes"] = g.Changes, ["memories"] = g.Memories, ["until"] = g.Until,
                })]),
            },
        };
    }

    // A person's decision on a contradiction: a write's answer, the other memory and what was decided.
    static JsonObject Settled(MemorySettled s)
    {
        var answer = Written("settle_contradiction", s.Write);
        answer["target"] = s.Conflict.ToString();
        answer["decision"] = s.Decision;
        return answer;
    }

    // A person's link (MemoryStore.Link through the app's channel, so verified): its id, the two memories as named (id
    // the source, target) and as stored (from, to: relates_to has no direction and is stored smaller id first), whether
    // this call made it (created) or it was there already as it is (unchanged), and the source end's scope.
    static JsonObject Linked(MemoryLinked l) => new()
    {
        ["op"] = "link", ["link"] = l.Id.ToString(), ["id"] = l.Source.ToString(), ["target"] = l.Target.ToString(), ["from"] = l.From.ToString(),
        ["to"] = l.To.ToString(), ["type"] = l.Type, ["directed"] = l.Directed, ["verification"] = l.Verification, ["created"] = l.Created,
        ["unchanged"] = l.Unchanged, ["scope"] = l.Scope, ["owner"] = l.Subject, ["scopes"] = l.Scopes,
    };

    // An unlink (MemoryStore.Unlink): the made links it removed, those removed and kept as dismissals (automatic ones), the
    // types of the map's suggestions between the two it dismissed, how many of those were dismissed before, and whether it
    // did nothing.
    static JsonObject Unlinked(MemoryUnlinked u)
    {
        JsonArray Refs(IReadOnlyList<MemoryLinkRef> refs) =>
            new([.. refs.Select(r => (JsonNode)new JsonObject { ["id"] = r.Id.ToString(), ["type"] = r.Type })]);
        return new()
        {
            ["op"] = "unlink", ["id"] = u.Source.ToString(), ["target"] = u.Target.ToString(), ["removed"] = Refs(u.Removed), ["kept"] = Refs(u.Kept),
            ["dismissed"] = Strings(u.Dismissed), ["already"] = u.Already, ["unchanged"] = u.Unchanged, ["scope"] = u.Scope, ["owner"] = u.Subject,
            ["scopes"] = u.Scopes,
        };
    }

    // Still true (MemoryStore.StillTrue): the memory as a write answers it, its revision unchanged, its new checked time,
    // why it was in the queue (was: not_confirmed, retracted_source or null) and the retracted sources whose flags came off.
    static JsonObject Confirmed(MemoryConfirmed c) => new()
    {
        ["op"] = "still_true", ["id"] = c.Id.ToString(), ["revision"] = c.Revision, ["scope"] = c.Scope, ["owner"] = c.Subject, ["status"] = c.Status,
        ["scopes"] = c.Scopes, ["unchanged"] = c.Unchanged, ["checkedAt"] = c.CheckedAt, ["was"] = c.Was,
        ["retracted"] = Strings(c.Retracted.Select(r => r.ToString())),
    };
}
