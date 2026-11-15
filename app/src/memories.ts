import { cleanInsight } from "./insights.js";
import type { DailyInsight } from "./insights.js";
import { object } from "./validation.js";

// Native memories in the app's Memories view (SB-P3-12, SB-P3-13; app_memories, contracts/README.md 2.2), shared by the
// browser and the app's server: the requests each op takes, and every answer rebuilt from the contract's fields alone.
// The engine reads and writes them as the app (channel app), a person's channel: only here (and at the terminal) does a
// person release a hold, several at once from the review queue, put a memory into a capsule slot or delete a memory for
// good (SB-P3-22), and with the engine's memory.share on too, share a memory into Family or take it back (run 14). The
// full memory UI's list (SB-P6-01): a list's tags and count, an AI client's groups of changes and a group's undo, several
// memories at once, a project's writes, a capsule's status and the settings to read. A person's review queues (SB-P6-01): how
// many wait in each, possible copies and contradictions, insight candidates and the insight stream, and one resolve for what a
// person does in any of them. The memory UI's last pieces (run 22): the words a list and its tags look for, the standing instructions
// and a person's stop of one, and a review queue's one-word step for several items. The tool exists only while the engine's
// memory.tools is on; with it off, and in demo mode, the facade answers {available: false} and the app shows nothing of it.

export type MemoryStatus = "canonical" | "draft" | "deprecated";
export type MemoryScope = "private" | "shared";
// burst: a write burst from one AI client (SB-P5-01, migration 045); held_project: a write through a channel scored as an
// AI client's into a project that a held memory made, which no person has let in yet (SB-P3-21, migrations 045 and 046).
export type HoldReason =
  "instruction" | "hidden" | "too_long" | "burst" | "held_project";
export interface MemoryHold {
  reason: HoldReason;
  signals: string[];
}
export interface MemoryProjectRef {
  id: string;
  slug: string;
  name: string | null;
}
// A capsule slot a person put a memory in (SB-P3-22): core with one of its four, or project (its project's capsule)
// with one of its three. A capsule serves it only while it is canonical and not held.
export type CapsuleKind = "core" | "project";
export const capsuleSlots: Record<CapsuleKind, readonly string[]> = {
  core: ["identity", "preferences", "constraints", "principles"],
  project: ["current-state", "decisions", "open-questions"],
};
export interface MemoryCapsule {
  kind: CapsuleKind;
  slot: string;
}
export interface Memory {
  id: string;
  scope: MemoryScope;
  owner: string;
  author: string | null;
  source: string;
  kind: string | null;
  importance: number | null;
  volatility: string | null;
  status: MemoryStatus;
  tags: string[];
  project: MemoryProjectRef | null;
  // Whole from get (a held memory's included, for a person to review); a preview in a list, none for a held one.
  content: string | null;
  truncated: boolean;
  held: MemoryHold | null;
  validFrom: string | null;
  validUntil: string | null;
  revision: number;
  createdAt: string;
  updatedAt: string;
  // Its capsule slot, from get and a list (none in a list for a held one); null from an engine that sends none.
  capsule: MemoryCapsule | null;
  // What it is now (SB-P4-11, migration 049's one definition): current, replaced (by replacedBy, a memory of its own
  // scope), ended, or retracted (marked wrong); and the retracted memories it was built on, which flagged it for a check.
  // An engine from before the truth states sends none: current, replaced by nothing, no flags.
  state: MemoryState;
  replacedBy: string | null;
  retractedSources: string[];
}
export type MemoryState = "current" | "replaced" | "ended" | "retracted";
// What a change's retraction cascade did (SB-P4-08): memories of its scope it had replaced that are current again
// (reopened) or replaced by it again (reclosed), and memories built on it flagged for a check or no longer.
export interface MemoryCascade {
  reopened: string[];
  reclosed: string[];
  flagged: string[];
  unflagged: string[];
}
export interface MemoryVersion {
  id: string;
  revision: number;
  current: boolean;
  status: MemoryStatus;
  tags: string[];
  content: string | null;
  held: MemoryHold | null;
  writtenAt: string;
  person: string | null;
  channel: string | null;
}
export interface MemoryChange {
  revision: number;
  event: string;
  at: string;
  person: string | null;
  channel: string;
  client: string | null;
  fields: string[];
  status: MemoryStatus | null;
  preview: string | null;
  held: boolean;
  kept: boolean;
  to: number | null;
}
export interface MemoryHistory {
  id: string;
  revision: number;
  changes: MemoryChange[];
  // The revision to read before for the next, older page.
  next: number | null;
  cut: boolean;
  // Earlier states no longer kept (their entries stay), and the oldest revision still kept; an engine that says nothing:
  // none, and 0.
  pruned: boolean;
  oldestKept: number;
}
export interface TrashedMemory {
  id: string;
  scope: MemoryScope;
  owner: string;
  status: MemoryStatus;
  preview: string | null;
  truncated: boolean;
  held: MemoryHold | null;
  trashedAt: string;
  purgeAfter: string;
  daysLeft: number;
  nonce: string;
  mayDeleteForever: boolean;
  revision: number;
}
// A project as the registry holds it (SB-P3-07, SB-P6-01). What an engine from before the project writes sends none of: no
// description, no author and no revision, so no change names a revision it read.
export interface MemoryProject {
  id: string;
  slug: string;
  name: string;
  scope: MemoryScope;
  owner: string;
  aliases: string[];
  status: string;
  description: string;
  author: string | null;
  revision: number | null;
  createdAt: string | null;
  updatedAt: string | null;
}
// A held memory in the review queue (SB-P3-22): why and how it came, never its words, tags or project.
export interface ReviewItem {
  id: string;
  scope: MemoryScope;
  owner: string;
  author: string | null;
  source: string;
  status: MemoryStatus;
  reason: HoldReason;
  score: number;
  signals: string[];
  heldAt: string;
  heldBy: string | null;
  channel: string | null;
  client: string | null;
  mayRelease: boolean;
  revision: number;
}
// One memory of a release of several, answered on its own.
export interface MemoryReleaseResult {
  id: string;
  result: "released" | "unchanged" | "unknown" | "forbidden" | "conflict";
  revision: number | null;
}
// What a write did: never the memory's words.
export interface MemoryWritten {
  op:
    | "capture"
    | "append"
    | "update"
    | "status"
    | "release"
    | "capsule"
    | "undo"
    | "forget"
    | "restore"
    | "delete"
    | "share"
    | "clear_flags"
    | "resolve_loop"
    | "resolve_due"
    | "record_outcome"
    | "stop_standing";
  id: string;
  revision: number | null;
  to: number | null;
  scope: MemoryScope | null;
  owner: string;
  status: MemoryStatus | null;
  scopes: "off" | "on";
  project: { slug: string; created: boolean } | null;
  unchanged: boolean;
  replayed: boolean;
  droppedTags: string[];
  held: MemoryHold | null;
  // forget: the trash row and its days.
  trash: { nonce: string; days: number } | null;
  // capsule: the slot it is in now, or none.
  capsule: MemoryCapsule | null;
  // share: its project did not come with it, so it is in no project where it landed.
  projectLeft: boolean;
  // What its retraction cascade did, when it did something (a status, an undo, a forget or a restore); else null.
  cascade: MemoryCascade | null;
  // resolve_loop and resolve_due: what was asked. resolve_loop: which way the loop runs and the other person's name.
  action?: string;
  direction?: "out" | "in";
  counterparty?: string | null;
  // resolve_due: the date after it (none after a clear) and before it.
  due?: MemoryDueDate | null;
  before?: MemoryDueDate | null;
  // record_outcome: what was asked, the outcome before it, how often unknown asked again, the next review date (none once nothing
  // is left to ask), and whether an unknown stopped the asking.
  result?: MemoryOutcome;
  previous?: MemoryOutcome | null;
  rearms?: number;
  reviewAt?: string | null;
  reviewsDone?: boolean;
}
export interface MemoryRefused {
  ok: false;
  code: string;
  reason: string;
  revision: number | null;
  known: string[];
}

// A suggestion of the map's that a person or an AI client dismissed (SB-P4-05, migration 057), as the engine lists them
// for the reader's own map: its id (memories or stars, the two ids smaller first, and the link type), the two stars on
// the map with their titles, the link type, when, by whom (person: the app's login or the terminal's; ai_client: an AI
// client's unlink), whether this login or its person made it, and whether this caller may take it back.
export interface MapDismissal {
  id: string;
  kind: "memories" | "stars";
  type: string;
  stars: [{ id: string; title: string }, { id: string; title: string }];
  dismissedAt: string;
  by: "person" | "ai_client";
  mine: boolean;
  mayRestore: boolean;
}
// A person's own link steps (run 17; API-21): a memory's connections, one step away, as the engine serves them to this
// reader: a link a person or an AI client made (made, linkId) or the map's suggestion between the two memories (no
// linkId), its type and direction from the memory asked about, who made it, whether this caller may unlink it (a
// suggestion: dismiss it), when, its confidence, and the other memory, a held one without its words.
export interface MemoryNeighbour {
  id: string;
  scope: MemoryScope;
  owner: string;
  status: MemoryStatus;
  state: MemoryState;
  kind: string | null;
  preview: string | null;
  truncated: boolean;
  held: MemoryHold | null;
}
export interface MemoryConnection {
  linkId: string | null;
  made: boolean;
  type: string;
  direction: "outgoing" | "incoming" | "none";
  verification: "verified" | "inferred" | "suggested";
  mine: boolean;
  // An import brought it (a Second Brain export's, or another install's Rhythm export): no maker here, its verification
  // as the file gave it, or verified once a person here confirms it. False from an engine that does not say.
  imported: boolean;
  mayUnlink: boolean;
  at: string;
  confidence: number | null;
  memory: MemoryNeighbour;
}
// One memory that may be out of date (053's stale queue): found by the nightly pass (not_confirmed) or built on a memory
// marked wrong since (retracted_source), with the revision a person reads it at.
export interface StaleMemory {
  id: string;
  scope: MemoryScope;
  owner: string;
  author: string | null;
  status: MemoryStatus;
  volatility: string | null;
  reason: "not_confirmed" | "retracted_source";
  since: string;
  foundAt: string | null;
  confirmedAt: string | null;
  preview: string;
  truncated: boolean;
  retractedSources: string[];
  revision: number;
}
// The brief (SB-P5-08; app_memories op brief; Home no longer draws it, D-257): each section's count, the whole length of the list its MCP
// tool walks, and its head, item for item what the MCP brief shows. No section holds a held or trashed memory.
export interface BriefItem {
  id: string;
  revision: number;
  scope: MemoryScope;
  owner: string;
  preview: string;
  truncated: boolean;
}
export interface BriefDated extends BriefItem {
  window: "overdue" | "upcoming";
  at: string;
  // A due date as it reads on the install's clocks; a review date comes with its moment alone.
  local: string | null;
}
export interface BriefLoop extends BriefItem {
  counterparty: string | null;
}
export interface BriefStale extends BriefItem {
  reason: string;
}
export interface BriefInsight {
  id: string;
  scope: MemoryScope;
  owner: string;
  title: string;
}
export interface BriefContradiction extends BriefItem {
  conflict: { id: string; preview: string; truncated: boolean };
}
export interface BriefGroup {
  group: string;
  family: string;
  client: string | null;
  changes: number;
  memories: number;
  until: string;
}
export interface BriefList<T> {
  count: number;
  items: T[];
}
export interface BriefWindows<T> extends BriefList<T> {
  overdue: number;
  upcoming: number;
}
export interface MemoryBrief {
  project: string | null;
  unknownProject: boolean;
  zone: string;
  says: string;
  due: BriefWindows<BriefDated>;
  decisions: BriefWindows<BriefDated>;
  youOwe: BriefList<BriefLoop>;
  owedToYou: BriefList<BriefLoop>;
  stale: BriefList<BriefStale>;
  insights: BriefList<BriefInsight>;
  contradictions: BriefList<BriefContradiction>;
  duplicates: BriefList<BriefItem>;
  // How many memories wait in the review queue: a count with no words.
  held: number;
  standing: BriefList<BriefItem>;
  calibration: { ready: boolean; n: number; line: string | null };
  changes: {
    count: number;
    changes: number;
    truncated: boolean;
    groups: BriefGroup[];
  };
}
// The day's old memory for the Home card (SB-P5-17; app_memories op resurface), the same one all day, or none: never a held,
// trashed or dismissed one, so it always has its words.
export interface ResurfacedMemory {
  id: string;
  revision: number;
  scope: MemoryScope;
  owner: string;
  preview: string;
  truncated: boolean;
  createdAt: string;
}
// This reader has retired it from the card for good; unchanged when it already had.
export type ResurfaceDismissed = {
  op: "resurface_dismiss";
  id: string;
  unchanged: boolean;
};
// A person's link (link), the links between two removed (unlink), and still true (still_true).
export type MemoryLinked = {
  op: "link";
  link: string;
  id: string;
  target: string;
  type: string;
  directed: boolean;
  verification: "verified" | "inferred" | "suggested";
  created: boolean;
  unchanged: boolean;
};
export type MemoryUnlinked = {
  op: "unlink";
  id: string;
  target: string;
  removed: { id: string; type: string }[];
  kept: { id: string; type: string }[];
  dismissed: string[];
  already: number;
  unchanged: boolean;
};
export type MemoryConfirmed = {
  op: "still_true";
  id: string;
  revision: number;
  unchanged: boolean;
  checkedAt: string;
  was: "not_confirmed" | "retracted_source" | null;
  retracted: string[];
};
// A tag on the memories a list holds, with how many carry it, most carried first (SB-P6-01).
export interface TagCount {
  tag: string;
  count: number;
}
// An AI client's group of changes as this reader reads the audit trail (SB-P6-01): the changes of one kind by one login and
// client, each within ten minutes of the one before. Its key is what undo_group takes; items are its newest memories, one row
// each, a held one (or one this reader no longer reads) without its words.
export interface ChangeRow {
  id: string;
  revision: number;
  event: string;
  at: string;
  changes: number;
  preview: string | null;
  truncated: boolean;
  held: boolean;
}
export interface ChangeGroup {
  group: string;
  family: string;
  person: string | null;
  client: string | null;
  changes: number;
  memories: number;
  from: string;
  until: string;
  capped: boolean;
  items: ChangeRow[];
}
// A capsule slot by slot, as its AI clients are served it, and what waits in each (SB-P6-01). No words of any memory.
export interface CapsuleSlotStatus {
  slot: string;
  state: "filled" | "empty" | "duplicate" | "omitted";
  filledBy: string | null;
  duplicates: string[];
  invalid: { id: string; reason: "content-too-large" | "empty-content" }[];
  waiting: {
    id: string;
    status: MemoryStatus;
    held: boolean;
    expired: boolean;
  }[];
}
export interface MemoryCapsuleStatus {
  kind: CapsuleKind;
  project: string | null;
  scope: MemoryScope;
  complete: boolean;
  populated: boolean;
  hash: string;
  chars: number;
  maxChars: number;
  served: boolean;
  slots: CapsuleSlotStatus[];
}
// The install's memory settings, to read; they change in rhythm.yaml (SB-P6-01).
export interface MemorySettingsRead {
  scopes: "off" | "on";
  person: string | null;
  share: boolean;
  capsules: boolean;
  capture: boolean;
  secondBrainImport: boolean;
  recall: boolean;
  recallLog: boolean;
  nightSummary: boolean;
  trashDays: number;
  versionsKept: number;
  maxBytes: number;
}
// A person's review queues (SB-P6-01): how many wait in each, each the length of that queue's own list.
export interface MemoryQueues {
  held: number;
  duplicates: number;
  contradictions: number;
  stale: number;
  candidates: number;
  insights: number;
}
// A memory flagged when saved as a possible copy of another of its scope, with the copies that stand now, closest first.
export interface MemoryCopyRef {
  id: string;
  score: number;
}
export interface DuplicateMemory {
  id: string;
  scope: MemoryScope;
  owner: string;
  author: string | null;
  status: MemoryStatus;
  createdAt: string;
  preview: string;
  truncated: boolean;
  duplicateOf: MemoryCopyRef[];
  revision: number;
}
// A pair of memories of one scope that may conflict: a person's to settle (maySettle, for this channel). proposed: an AI client
// named it a contradiction, in its own words, which are stored data. startedAt: when a side began being true (null: unknown, which counts
// as before every other); only the later can replace the other, and memory is only the one whose words were checked, which after an
// edit can be the earlier.
export interface ContradictionSide {
  id: string;
  revision: number;
  author: string | null;
  status: MemoryStatus;
  preview: string;
  truncated: boolean;
  startedAt: string | null;
}
export interface Contradiction {
  scope: MemoryScope;
  owner: string;
  score: number;
  offeredAt: string;
  memory: ContradictionSide;
  conflict: ContradictionSide;
  proposed: {
    at: string;
    person: string | null;
    client: string | null;
    reason: string | null;
  } | null;
  maySettle: boolean;
}
// A pair of an older and a newer memory the nightly pass found worth a look, in the state asked for.
export interface PairSide {
  id: string;
  revision: number;
  createdAt: string;
  importance: number | null;
  preview: string;
  truncated: boolean;
}
export interface InsightPair {
  id: string;
  scope: MemoryScope;
  owner: string;
  score: number;
  signals: string[];
  similarity: number;
  gapDays: number;
  foundAt: string;
  older: PairSide;
  newer: PairSide;
  status: "pending" | "used" | "rejected";
  decidedAt: string | null;
  note: string | null;
  insight: string | null;
}
// One insight of the stream: which view it is in, its review (an AI's until a person confirms it), whether this login may review
// it, and the insight as every reader receives it, all of its text untrusted.
export type InsightView =
  "proposed" | "older" | "confirmed" | "snoozed" | "closed";
export type InsightReview = "proposed" | "confirmed" | "dismissed";
export interface StreamInsight {
  id: string;
  view: InsightView;
  review: InsightReview;
  reviewedAt: string | null;
  snoozeUntil: string | null;
  snoozeEnded: boolean;
  shownAt: string;
  scope: MemoryScope;
  owner: string;
  candidates: string[];
  mayReview: boolean;
  insight: DailyInsight;
}
export interface InsightDay {
  date: string;
  zone: string;
  outcome: "saved" | "quiet" | "blocked" | "none";
  note: string | null;
  insight: string | null;
  declined: number;
  says: string;
}
export interface InsightEvent {
  event: string;
  value: string | null;
  until: string | null;
  note: string | null;
  person: string | null;
  channel: string | null;
  client: string | null;
  at: string;
}
// The brief's lists (SB-P6-01; app_memories ops loops, due, decisions and calibration): each page's count the asked list's whole
// length, from the statement that read it, so a count in the brief is the length of its list.
export type LoopDirection = "out" | "in";
export interface OpenLoop {
  id: string;
  revision: number;
  scope: MemoryScope;
  owner: string;
  author: string | null;
  status: MemoryStatus;
  direction: LoopDirection;
  // The other person's name in words, or none.
  counterparty: string | null;
  tags: string[];
  project: MemoryProjectRef | null;
  createdAt: string;
  preview: string;
  truncated: boolean;
}
export type DueWindow = "overdue" | "upcoming" | "later";
export type DueKind = "due" | "event" | "wake";
export interface MemoryDueDate {
  at: string;
  // How it reads on the install's clocks.
  local: string;
  zone: string;
  kind: DueKind;
  source: "explicit" | "text";
}
export interface DueRow {
  id: string;
  revision: number;
  scope: MemoryScope;
  owner: string;
  author: string | null;
  status: MemoryStatus;
  window: DueWindow;
  at: string;
  local: string;
  kind: DueKind;
  source: "explicit" | "text";
  // The open loop's direction when it is one.
  loop: LoopDirection | null;
  tags: string[];
  project: MemoryProjectRef | null;
  createdAt: string;
  preview: string;
  truncated: boolean;
}
export type MemoryOutcome = "right" | "wrong" | "mixed" | "unknown";
export type DecisionList =
  "open" | "due" | "upcoming" | "resolved" | "scored" | "all";
export interface DecisionRow {
  id: string;
  revision: number;
  scope: MemoryScope;
  owner: string;
  author: string | null;
  status: MemoryStatus;
  // How sure the person was (0.05 to 0.95), set once, and whether they said so or a client read it from their words.
  confidence: number | null;
  confidenceSource: "stated" | "inferred" | null;
  outcome: MemoryOutcome | null;
  // How often "too early to tell" asked again.
  rearms: number;
  // When to look at it again; none once nothing is left to ask.
  reviewAt: string | null;
  window: DueWindow | null;
  // Its window is open: a scored list keeps decisions replaced since.
  current: boolean;
  // Its words differ from those it was recorded with, so the confidence was given for other words.
  edited: boolean;
  tags: string[];
  project: MemoryProjectRef | null;
  createdAt: string;
  preview: string;
  truncated: boolean;
}
export interface CalibrationBand {
  band: string;
  n: number;
  nStated: number;
  nInferred: number;
  // Enough in it for its figures, which are null otherwise.
  shown: boolean;
  meanConfidence: number | null;
  hitRate: number | null;
  low: number | null;
  high: number | null;
}
export interface MemoryCalibration {
  source: "all" | "stated" | "inferred";
  ready: boolean;
  // The scored list's length.
  n: number;
  nStated: number;
  nInferred: number;
  minDecisions: number;
  minBand: number;
  minTopic: number;
  brier: number | null;
  gap: number | null;
  direction: "over" | "under" | "in_line" | null;
  bands: CalibrationBand[];
  headline: string | null;
  topic: {
    name: string;
    n: number;
    gap: number;
    direction: "over" | "under";
  } | null;
  kind: "not_ready" | "rate" | "in_line" | "no_range";
  line: string;
  topicLine: string | null;
  stated: number | null;
  hit: number | null;
}
export type MemoryReads = {
  queues: MemoryQueues;
  duplicates: {
    duplicates: DuplicateMemory[];
    next: string | null;
    count: number;
  };
  contradictions: {
    contradictions: Contradiction[];
    next: string | null;
    count: number;
  };
  insight_candidates: {
    candidates: InsightPair[];
    next: string | null;
    count: number;
  };
  insight_stream: {
    view: InsightView | "all";
    count: number;
    proposed: number;
    older: number;
    confirmed: number;
    snoozed: number;
    closed: number;
    candidates: number;
    day: InsightDay;
    insights: StreamInsight[];
    next: string | null;
  };
  insight_history: {
    id: string;
    review: InsightReview;
    count: number;
    events: InsightEvent[];
  };
  tags: { tags: TagCount[]; truncated: boolean; count: number };
  // count: how many groups the read found, of which the newest are shown; an engine from before it doesn't say.
  changes: {
    groups: ChangeGroup[];
    truncated: boolean;
    since: string;
    count?: number;
  };
  loops: {
    loops: OpenLoop[];
    next: string | null;
    count: number;
    youOwe: number;
    owedToYou: number;
  };
  due: {
    due: DueRow[];
    next: string | null;
    count: number;
    overdue: number;
    upcoming: number;
    later: number;
    zone: string;
  };
  decisions: {
    decisions: DecisionRow[];
    next: string | null;
    count: number;
    open: number;
    dueNow: number;
    upcoming: number;
    resolved: number;
    scored: number;
  };
  calibration: MemoryCalibration;
  capsule_status: MemoryCapsuleStatus;
  settings: MemorySettingsRead;
  list: { memories: Memory[]; next: string | null };
  trash: { trash: TrashedMemory[]; next: string | null };
  get: { memory: Memory | null; version: MemoryVersion | null };
  history: { history: MemoryHistory };
  projects: { projects: MemoryProject[] };
  review: { review: ReviewItem[]; next: string | null; count: number };
  dismissals: { dismissals: MapDismissal[]; truncated: boolean };
  connections: {
    id: string;
    connections: MemoryConnection[];
    truncated: boolean;
  };
  stale: { stale: StaleMemory[]; next: string | null; count: number };
  brief: MemoryBrief;
  resurface: { day: string; pick: ResurfacedMemory | null };
  // The standing instructions this reader reads (run 22), as list_standing lists them: the whole list's length and how many may fire,
  // are held and are over the limit; served, whether any fires at all now.
  standing: {
    standing: StandingInstruction[];
    next: string | null;
    count: number;
    firing: number;
    held: number;
    overLimit: number;
    maxInScope: number;
    maxFires: number;
    served: boolean;
  };
};
// One standing instruction (SB-P5-12, run 22's list): where it came from, its whole words (none, nor its project, for a held one) and
// whether it fires, and why not.
export type StandingReason = "held" | "project_held" | "over_limit";
export interface StandingInstruction {
  id: string;
  revision: number;
  scope: MemoryScope;
  owner: string;
  author: string | null;
  source: string;
  status: MemoryStatus;
  project: { id: string; slug: string } | null;
  createdAt: string;
  content: string | null;
  held: MemoryHold | null;
  fires: boolean;
  reason: StandingReason | null;
}
// A review queue's one-word step for several items (run 22), each answered on its own: resolved, unchanged (it was not waiting), or why
// not; a memory's revision, an insight's review now, a pair's decision now, and no one's words.
export type BulkResolveAction =
  "still_true" | "reject_candidate" | "confirm_insight" | "dismiss_insight";
export type MemoryResolvedAll = {
  op: "resolve_all";
  action: BulkResolveAction;
  results: {
    id: string;
    result:
      | "resolved"
      | "unchanged"
      | "unknown"
      | "forbidden"
      | "conflict"
      | "held"
      | "retracted"
      | "invalid";
    revision: number | null;
    review: InsightReview | null;
    view: InsightView | null;
    candidate: "used" | "rejected" | null;
  }[];
};
export type MemoryReleasedAll = {
  op: "release_all";
  results: MemoryReleaseResult[];
};
// Several memories at once (SB-P6-01), a person's steps, each answered on its own.
export type MemoryForgottenAll = {
  op: "forget_all";
  results: {
    id: string;
    result: "forgotten" | "unknown" | "forbidden" | "conflict";
    revision: number | null;
    nonce: string | null;
    days: number | null;
    cascade: MemoryCascade | null;
  }[];
};
export type MemoryRestoredAll = {
  op: "restore_all";
  results: {
    id: string;
    result: "restored" | "unknown" | "forbidden" | "conflict";
    revision: number | null;
    held: MemoryHold | null;
    cascade: MemoryCascade | null;
  }[];
};
export type MemoryDeletedAll = {
  op: "delete_all";
  results: { id: string; result: "deleted" | "unknown" | "forbidden" }[];
};
export type MemorySharedAll = {
  op: "share_all";
  results: {
    id: string;
    result:
      | "moved"
      | "unchanged"
      | "unknown"
      | "forbidden"
      | "conflict"
      | "held"
      | "no_author"
      | "scopes_off"
      | "invalid";
    revision: number | null;
    projectLeft: boolean;
  }[];
};
// One call's work on a group of an AI client's changes: each memory it reached, what is left for the next call with the same
// key, and the key that undoes these undos (none when this call undid nothing).
export type MemoryGroupUndone = {
  op: "undo_group";
  group: string;
  total: number;
  remaining: number;
  done: boolean;
  capped: boolean;
  results: { id: string; result: string }[];
  redo: string | null;
};
// A project's writes (SB-P6-01): what the registry holds now, or what a delete left.
export type MemoryProjectWritten = {
  op: "project_create" | "project_update";
  project: MemoryProject;
  scopes: "off" | "on";
  changed: string[];
  unchanged: boolean;
};
export type MemoryProjectDeleted = {
  op: "project_delete";
  id: string;
  slug: string;
  owner: string;
  memories: number;
  scopes: "off" | "on";
};
// Discover's Dismiss (dismiss): the dismissal now kept (null when a link someone stated was removed and nothing is
// kept), and whether a made link was removed; and taking one back (undismiss).
export type MapDismissed = {
  op: "dismiss";
  link: string;
  dismissal: string | null;
  removed: boolean;
  unchanged: boolean;
};
export type MapUndismissed = {
  op: "undismiss";
  dismissal: string;
  unchanged: boolean;
  // Whether the map finds a suggestion of its type between the two stars now, so it is back from
  // the next read; false when the dismissal came from removing a suggested link, which is gone.
  back: boolean;
};
// What a person's step in a review queue did (op resolve, an item leaving its queue once): one step's fields filled, the others
// null. No memory's words.
export type MemoryResolveAction =
  | "release"
  | "keep_copy"
  | "settle_keep_both"
  | "settle_replace"
  | "still_true"
  | "use_candidate"
  | "reject_candidate"
  | "confirm_insight"
  | "dismiss_insight";
export type MemoryResolved = {
  op: "resolve";
  queue: string;
  action: MemoryResolveAction;
  id: string;
  unchanged: boolean;
  waiting: boolean;
  stillCounted: boolean;
  revision: number | null;
  scope: MemoryScope | null;
  owner: string | null;
  status: MemoryStatus | null;
  changed: string[];
  held: MemoryHold | null;
  cascade: MemoryCascade | null;
  target: string | null;
  decision: "keep_both" | "replaced" | null;
  review: InsightReview | null;
  view: InsightView | null;
  candidate: "used" | "rejected" | null;
  note: string | null;
  insight: string | null;
  was: "not_confirmed" | "retracted_source" | null;
};
// A person's review of an insight, or a review taken back (reopen).
export type InsightReviewed = {
  op: "review_insight";
  id: string;
  review: InsightReview;
  view: InsightView;
  reviewedAt: string | null;
  unchanged: boolean;
};
export type MemoryOp =
  | keyof MemoryReads
  | MemoryResolved["op"]
  | InsightReviewed["op"]
  | MemoryWritten["op"]
  | MemoryReleasedAll["op"]
  | MapDismissed["op"]
  | MapUndismissed["op"]
  | MemoryLinked["op"]
  | MemoryUnlinked["op"]
  | MemoryConfirmed["op"]
  | ResurfaceDismissed["op"]
  | MemoryForgottenAll["op"]
  | MemoryRestoredAll["op"]
  | MemoryDeletedAll["op"]
  | MemorySharedAll["op"]
  | MemoryGroupUndone["op"]
  | MemoryProjectWritten["op"]
  | MemoryProjectDeleted["op"]
  | MemoryResolvedAll["op"];
export type MemoryReply<T> =
  | { available: false }
  | ({ available: true; ok: true } & T)
  | ({ available: true } & MemoryRefused);

// Each op's members beside op: the required, then the optional, and each member's check. The engine checks the same
// (Mcp/AppMemories.cs); the facade refuses anything else before calling it.
const shapes: Record<MemoryOp, [string[], string[]]> = {
  list: [
    [],
    ["scope", "tag", "kind", "status", "project", "query", "cursor", "limit"],
  ],
  trash: [[], ["cursor", "limit"]],
  get: [["id"], ["version"]],
  history: [["id"], ["before", "limit"]],
  projects: [[], ["scope", "archived"]],
  review: [[], ["scope", "reason", "cursor", "limit"]],
  capture: [
    ["content"],
    ["scope", "tags", "project", "kind", "importance", "volatility", "key"],
  ],
  append: [
    ["id", "text"],
    ["expected", "key"],
  ],
  update: [
    ["id"],
    [
      "content",
      "tags",
      "kind",
      "importance",
      "volatility",
      "project",
      "clear",
      "expected",
      "key",
    ],
  ],
  status: [
    ["id", "status"],
    ["expected", "key"],
  ],
  release: [["id"], ["expected"]],
  release_all: [["memories"], []],
  capsule: [
    ["id", "capsule"],
    ["slot", "expected"],
  ],
  forget: [["id"], ["expected", "key"]],
  restore: [["id"], ["nonce"]],
  undo: [["id"], ["to", "expected"]],
  delete: [["id", "nonce", "confirm"], []],
  // A person's share (run 14), which the engine takes only with memory.share on (the facade checks its listing).
  share: [["id"], ["scope", "expected"]],
  // The map's kept dismissals (SB-P4-05): the list, Discover's Dismiss of one suggestion by its id on the map, and
  // taking one back by its id.
  dismissals: [[], ["limit"]],
  dismiss: [["link"], []],
  undismiss: [["dismissal"], []],
  // A person's own link steps (run 17): a memory's connections, the stale queue, a person's link and unlink, still
  // true and a flag clear.
  connections: [["id"], ["type", "limit"]],
  stale: [[], ["scope", "cursor", "limit"]],
  link: [["id", "target"], ["type"]],
  unlink: [["id", "target"], ["type"]],
  still_true: [["id"], ["expected"]],
  clear_flags: [["id"], ["expected"]],
  // The brief (SB-P5-08).
  brief: [[], ["scope", "project"]],
  // The old memory for the Home card (SB-P5-17).
  resurface: [[], []],
  resurface_dismiss: [["id"], []],
  // The full memory UI's list (SB-P6-01): a list's tags and count, an AI client's changes and a group's undo, several at once,
  // a project's writes, a capsule's status and the settings to read.
  tags: [[], ["scope", "tag", "kind", "status", "project", "query"]],
  changes: [[], ["since", "scope", "notHeld"]],
  capsule_status: [["capsule"], ["project", "scope"]],
  settings: [[], []],
  undo_group: [["group"], []],
  forget_all: [["memories"], []],
  restore_all: [["memories"], []],
  delete_all: [["memories"], []],
  // A share of several, which the engine takes only with memory.share on, as one (the facade checks its listing).
  share_all: [["memories"], ["scope"]],
  project_create: [["name"], ["slug", "description", "aliases", "scope"]],
  project_update: [
    ["id"],
    ["name", "description", "aliases", "archived", "expected"],
  ],
  project_delete: [["id"], ["expected"]],
  // A person's review queues (SB-P6-01): how many wait, the lists, an insight's history, a review, and one resolve.
  queues: [[], ["scope"]],
  duplicates: [[], ["scope", "cursor", "limit"]],
  contradictions: [[], ["scope", "cursor", "limit"]],
  insight_candidates: [[], ["scope", "state", "cursor", "limit"]],
  insight_stream: [[], ["view", "scope", "cursor", "limit"]],
  insight_history: [["id"], ["limit"]],
  review_insight: [["id", "action"], ["note"]],
  resolve: [
    ["id", "action"],
    ["target", "expected", "targetExpected", "note", "insight", "copies"],
  ],
  // The brief's lists (SB-P6-01), and a person's word on one.
  loops: [[], ["direction", "scope", "cursor", "limit"]],
  due: [[], ["window", "scope", "cursor", "limit"]],
  decisions: [[], ["state", "scope", "cursor", "limit"]],
  calibration: [[], ["source", "scope"]],
  resolve_loop: [["id", "action"], ["expected"]],
  resolve_due: [
    ["id", "action"],
    ["until", "expected"],
  ],
  record_outcome: [["id", "result"], ["expected"]],
  // The memory UI's last pieces (SB-P6-01, run 22): the standing instructions, a person's stop of one, and a review queue's one-word step
  // for several items.
  standing: [[], ["scope", "project", "cursor", "limit"]],
  stop_standing: [["id"], ["expected"]],
  resolve_all: [["action", "items"], []],
};
// The ops of a person's link steps (run 17), the brief (SB-P5-08) and the old memory (SB-P5-17), which the app's server passes on
// only while the engine serves the Memories view at all (memory.tools on); otherwise it refuses them as requests it does not know,
// as before.
export const personLinkOps: readonly string[] = [
  "connections",
  "stale",
  "link",
  "unlink",
  "still_true",
  "clear_flags",
  "brief",
  "resurface",
  "resurface_dismiss",
  "tags",
  "changes",
  "capsule_status",
  "settings",
  "undo_group",
  "forget_all",
  "restore_all",
  "delete_all",
  "project_create",
  "project_update",
  "project_delete",
  "queues",
  "duplicates",
  "contradictions",
  "insight_candidates",
  "insight_stream",
  "insight_history",
  "review_insight",
  "resolve",
  "loops",
  "due",
  "decisions",
  "calibration",
  "resolve_loop",
  "resolve_due",
  "record_outcome",
  "standing",
  "stop_standing",
  "resolve_all",
];
// The ops the facade passes on only while the engine's listing names a person's share (memory.share on, memory scopes on).
export const personShareOps: readonly string[] = ["share", "share_all"];
export const memoryReads: readonly string[] = [
  "queues",
  "duplicates",
  "contradictions",
  "insight_candidates",
  "insight_stream",
  "insight_history",
  "tags",
  "changes",
  "capsule_status",
  "settings",
  "list",
  "trash",
  "get",
  "history",
  "projects",
  "review",
  "dismissals",
  "connections",
  "stale",
  "brief",
  "resurface",
  "loops",
  "due",
  "decisions",
  "calibration",
  "standing",
];
// The most memories one release of several names (MemoryStore.ReleaseAllMax).
export const releaseAllMax = 50;
export const maxMemoryBytes = 131_072;
// The largest request /api/memories takes: a memory's whole text, escaped, and its other members.
export const maxMemoryBody = 288 * 1024;
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
// A link's id on the map (app_graph's edges): the map's own suggestion, or a link a person or an AI client made.
export const mapLinkId =
  /^(relationship:[0-9a-f]{64}|made-link:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/;
// A dismissal's id, as the engine lists it: memories or stars, the two ids smaller first, and the link type.
export const dismissalId =
  /^(memories|stars):[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}:[a-z][a-z0-9_-]{0,63}$/;
// A link's type as the engine names one: a lower-case word (which types a link takes is the engine's to say).
const linkTypeWord = /^[a-z][a-z0-9_-]{0,63}$/;
// A group of an AI client's changes, by the key the engine gave it (opaque here).
const groupKey = /^[A-Za-z0-9_-]{1,200}$/;
const text = (v: unknown, max: number): v is string =>
  typeof v === "string" && v.length >= 1 && v.length <= max;
// What the engine cut with Postgres' left() or a migration checked with length(), each of which counts code points: an emoji is
// one there and two here.
const points = (v: string) => [...v].length;
const preview = (v: unknown, max: number): v is string =>
  typeof v === "string" && v.length >= 1 && points(v) <= max;
const count = (v: unknown, least: number, most: number): v is number =>
  typeof v === "number" && Number.isInteger(v) && v >= least && v <= most;
const one = (v: unknown, words: readonly string[]) =>
  typeof v === "string" && words.includes(v);
const statuses: readonly MemoryStatus[] = ["canonical", "draft", "deprecated"];
const resolveActions: readonly MemoryResolveAction[] = [
  "release",
  "keep_copy",
  "settle_keep_both",
  "settle_replace",
  "still_true",
  "use_candidate",
  "reject_candidate",
  "confirm_insight",
  "dismiss_insight",
];
// The steps a review queue takes for several items at once (MemoryResolve.BulkActions): the one-word ones.
export const bulkResolveActions: readonly BulkResolveAction[] = [
  "still_true",
  "reject_candidate",
  "confirm_insight",
  "dismiss_insight",
];
// The longest words a list looks for (MemoryStore.QueryMax).
export const maxQuery = 200;
const insightViews: readonly InsightView[] = [
  "proposed",
  "older",
  "confirmed",
  "snoozed",
  "closed",
];
const insightReviews: readonly InsightReview[] = [
  "proposed",
  "confirmed",
  "dismissed",
];
const reasons: readonly HoldReason[] = [
  "instruction",
  "hidden",
  "too_long",
  "burst",
  "held_project",
];
const outcomes: readonly MemoryOutcome[] = [
  "right",
  "wrong",
  "mixed",
  "unknown",
];
const checks: Record<string, (v: unknown, op: string) => boolean> = {
  id: (v) => typeof v === "string" && uuid.test(v),
  nonce: (v) => typeof v === "string" && uuid.test(v),
  confirm: (v) => typeof v === "string" && uuid.test(v),
  version: (v) => count(v, 1, 2_147_483_647),
  before: (v) => count(v, 1, 2_147_483_647),
  expected: (v) => count(v, 1, 2_147_483_647),
  to: (v) => count(v, 1, 2_147_483_647),
  limit: (v, op) =>
    count(v, 1, op === "history" || op === "insight_history" ? 100 : 50),
  reason: (v) => one(v, reasons),
  capsule: (v) => one(v, ["core", "project", "none"]),
  slot: (v) => one(v, [...capsuleSlots.core, ...capsuleSlots.project]),
  // Each memory a step of several names, exactly as its one step takes it: a release, a forget or a share its id and the
  // revision its person read; a restore its id and trash row; a delete those and its person's confirmation, its own id.
  memories: (v, op) =>
    Array.isArray(v) &&
    v.length >= 1 &&
    v.length <= releaseAllMax &&
    v.every((m) => {
      if (!object(m) || typeof m.id !== "string" || !uuid.test(m.id))
        return false;
      const names = Object.keys(m).sort().join();
      if (op === "restore_all")
        return (
          names === "id,nonce" &&
          typeof m.nonce === "string" &&
          uuid.test(m.nonce)
        );
      if (op === "delete_all")
        return (
          names === "confirm,id,nonce" &&
          typeof m.nonce === "string" &&
          uuid.test(m.nonce) &&
          m.confirm === m.id
        );
      return names === "expected,id" && count(m.expected, 1, 2_147_483_647);
    }),
  since: (v) => typeof v === "string" && v.length <= 40 && instant(v),
  notHeld: (v) => typeof v === "boolean",
  direction: (v) => one(v, ["out", "in"]),
  window: (v) => one(v, ["overdue", "upcoming", "later"]),
  // One word per op: a decision's list, or a pair's state.
  state: (v, op) =>
    one(
      v,
      op === "decisions"
        ? ["open", "due", "upcoming", "resolved", "scored", "all"]
        : ["waiting", "used", "rejected"],
    ),
  source: (v) => one(v, ["stated", "inferred"]),
  // One word per op: a review queue's, a loop's, a due date's, or an insight's review.
  action: (v, op) =>
    one(
      v,
      op === "resolve"
        ? resolveActions
        : op === "resolve_all"
          ? bulkResolveActions
          : op === "resolve_loop"
            ? ["done", "received", "not_a_task"]
            : op === "resolve_due"
              ? ["snooze", "clear_date"]
              : ["confirm", "dismiss", "reopen"],
    ),
  result: (v) => one(v, outcomes),
  until: (v) => text(v, 40),
  group: (v) => typeof v === "string" && groupKey.test(v),
  name: (v) => text(v, 120),
  slug: (v) => slug(v),
  description: (v) => typeof v === "string" && v.length <= 1000,
  aliases: (v) =>
    Array.isArray(v) && v.length <= 16 && v.every((a) => text(a, 128)),
  cursor: (v) => text(v, 200),
  scope: (v) => one(v, ["private", "shared"]),
  tag: (v) => text(v, 128),
  kind: (v) => one(v, ["episodic", "semantic"]),
  status: (v) => one(v, statuses),
  volatility: (v) => one(v, ["durable", "state", "volatile"]),
  importance: (v) => count(v, 1, 5),
  project: (v) => text(v, 64),
  archived: (v) => typeof v === "boolean",
  content: (v) => text(v, maxMemoryBytes),
  text: (v) => text(v, maxMemoryBytes),
  tags: (v) =>
    Array.isArray(v) && v.length <= 64 && v.every((t) => text(t, 128)),
  clear: (v) =>
    Array.isArray(v) &&
    v.length <= 4 &&
    v.every((c) => one(c, ["importance", "kind", "project", "volatility"])),
  key: (v) => typeof v === "string" && /^[A-Za-z0-9._:-]{8,200}$/.test(v),
  link: (v) => typeof v === "string" && mapLinkId.test(v),
  dismissal: (v) => typeof v === "string" && dismissalId.test(v),
  target: (v) => typeof v === "string" && uuid.test(v),
  type: (v) => typeof v === "string" && linkTypeWord.test(v),
  targetExpected: (v) => count(v, 1, 2_147_483_647),
  // A keep both's memories read (073): 1 to 3, each once, as {id, expected}.
  copies: (v) =>
    Array.isArray(v) &&
    v.length >= 1 &&
    v.length <= 3 &&
    v.every(
      (m) =>
        object(m) &&
        Object.keys(m).sort().join() === "expected,id" &&
        typeof m.id === "string" &&
        uuid.test(m.id) &&
        count(m.expected, 1, 2_147_483_647),
    ) &&
    new Set(v.map((m) => String((m as { id: string }).id).toLowerCase()))
      .size === v.length,
  insight: (v) => typeof v === "string" && uuid.test(v),
  note: (v) => text(v, 500),
  view: (v) =>
    one(v, ["proposed", "older", "confirmed", "snoozed", "closed", "all"]),
  // Words a list looks for: at most maxQuery characters as sent, as the engine's door counts them, and something besides spaces.
  query: (v) => text(v, maxQuery) && v.trim().length > 0,
  // Each item of a step for several: its id, and the revision its person read where a memory has one.
  items: (v) =>
    Array.isArray(v) &&
    v.length >= 1 &&
    v.length <= releaseAllMax &&
    v.every(
      (i) =>
        object(i) &&
        typeof i.id === "string" &&
        uuid.test(i.id) &&
        Object.keys(i).every((k) => k === "id" || k === "expected") &&
        (!Object.hasOwn(i, "expected") || count(i.expected, 1, 2_147_483_647)),
    ),
};

// A request as the facade passes it on: exactly one op with exactly its members, each in range; null otherwise. An
// update names at least one thing to change; a capsule names a slot with core or project, and none without one. Never
// a channel or a client: the engine fixes both.
export function memoryRequest(body: unknown): Record<string, unknown> | null {
  if (
    !object(body) ||
    typeof body.op !== "string" ||
    !Object.hasOwn(shapes, body.op)
  )
    return null;
  const op = body.op as MemoryOp;
  const [required, optional] = shapes[op];
  if (required.some((r) => !Object.hasOwn(body, r))) return null;
  for (const [name, value] of Object.entries(body)) {
    if (name === "op") continue;
    if (!required.includes(name) && !optional.includes(name)) return null;
    if (!checks[name]?.(value, op)) return null;
  }
  if (
    op === "update" &&
    ![
      "content",
      "tags",
      "kind",
      "importance",
      "volatility",
      "project",
      "clear",
    ].some((n) => Object.hasOwn(body, n))
  )
    return null;
  if (
    op === "capsule" &&
    (body.capsule === "none") === Object.hasOwn(body, "slot")
  )
    return null;
  // A capsule's status names core, or project with its project.
  if (
    op === "capsule_status" &&
    (body.capsule === "none" ||
      (body.capsule === "project") !== Object.hasOwn(body, "project"))
  )
    return null;
  // The other memory's revision goes with the other memory.
  if (
    op === "resolve" &&
    Object.hasOwn(body, "targetExpected") &&
    !Object.hasOwn(body, "target")
  )
    return null;
  // The memories read go with a keep both.
  if (
    op === "resolve" &&
    Object.hasOwn(body, "copies") &&
    body.action !== "keep_copy"
  )
    return null;
  // A snooze names when it comes back, and a clear names no date.
  if (
    op === "resolve_due" &&
    (body.action === "snooze") !== Object.hasOwn(body, "until")
  )
    return null;
  // A project's change names at least one thing to change.
  if (
    op === "project_update" &&
    !["name", "description", "aliases", "archived"].some((n) =>
      Object.hasOwn(body, n),
    )
  )
    return null;
  return { ...body };
}

// ---------------------------------------------------------------------------------------------------------------------
// Answers, rebuilt from the contract's fields alone; null when one doesn't check out.

const instant = (v: unknown): v is string =>
  typeof v === "string" &&
  v.length <= 40 &&
  /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d{1,9})?(Z|[+-]\d\d:\d\d)$/.test(v) &&
  Number.isFinite(Date.parse(v));
const maybe = (v: unknown): v is string | null => v === null || instant(v);
const id = (v: unknown): v is string => typeof v === "string" && uuid.test(v);
const words = (v: unknown, max = 64): v is string[] =>
  Array.isArray(v) &&
  v.length <= max &&
  v.every((w) => typeof w === "string" && points(w) <= 200);
const owner = (v: unknown): v is string =>
  typeof v === "string" && /^[a-z][a-z0-9-]{0,31}$/.test(v);
const scope = (v: unknown): v is MemoryScope =>
  v === "private" || v === "shared";
const status = (v: unknown): v is MemoryStatus =>
  statuses.includes(v as MemoryStatus);
const nameOrNull = (v: unknown, max = 200): v is string | null =>
  v === null || (typeof v === "string" && v.length <= max);
const slug = (v: unknown): v is string =>
  typeof v === "string" && /^[a-z0-9][a-z0-9-]{0,63}$/.test(v);

export function cleanHold(v: unknown): MemoryHold | null | undefined {
  if (v === null) return null;
  if (
    !object(v) ||
    !one(v.reason, [
      "instruction",
      "hidden",
      "too_long",
      "burst",
      "held_project",
    ]) ||
    !words(v.signals, 20)
  )
    return undefined;
  return {
    reason: v.reason as HoldReason,
    signals: [...(v.signals as string[])],
  };
}
// A capsule slot, one of its own kind's slots; null for none, undefined when it doesn't check out. An engine from before
// SB-P3-22 sends no slot at all: none.
export function cleanCapsule(v: unknown): MemoryCapsule | null | undefined {
  if (v === null || v === undefined) return null;
  if (
    !object(v) ||
    (v.kind !== "core" && v.kind !== "project") ||
    typeof v.slot !== "string" ||
    !capsuleSlots[v.kind].includes(v.slot)
  )
    return undefined;
  return { kind: v.kind, slot: v.slot };
}
function projectRef(v: unknown): MemoryProjectRef | null | undefined {
  if (v === null) return null;
  if (!object(v) || !id(v.id) || !slug(v.slug) || !nameOrNull(v.name))
    return undefined;
  return { id: v.id, slug: v.slug, name: v.name };
}

const states: readonly MemoryState[] = [
  "current",
  "replaced",
  "ended",
  "retracted",
];
// A memory's truth (SB-P4-11): its state, what replaced it (only while replaced) and its retracted sources, as the
// contract has them; an engine from before them sends none of the three, which reads as current with nothing. Undefined
// when what came doesn't check out.
function cleanTruth(
  v: Record<string, unknown>,
): Pick<Memory, "state" | "replacedBy" | "retractedSources"> | undefined {
  const state = v.state === undefined ? "current" : v.state;
  const by = v.replacedBy === undefined ? null : v.replacedBy;
  const sources = v.retractedSources === undefined ? [] : v.retractedSources;
  if (
    !states.includes(state as MemoryState) ||
    (state === "replaced" ? !id(by) : by !== null) ||
    !Array.isArray(sources) ||
    sources.length > 100 ||
    !sources.every(id)
  )
    return undefined;
  return {
    state: state as MemoryState,
    replacedBy: by as string | null,
    retractedSources: [...(sources as string[])],
  };
}
// What a write's retraction cascade did, each list memory ids (at most 25); null when it sent none, or one that doesn't
// check out (it says what else changed, never whether this write happened).
function cleanCascade(v: unknown): MemoryCascade | null {
  const ids = (x: unknown): x is string[] =>
    Array.isArray(x) && x.length <= 25 && x.every(id);
  if (
    !object(v) ||
    !ids(v.reopened) ||
    !ids(v.reclosed) ||
    !ids(v.flagged) ||
    !ids(v.unflagged)
  )
    return null;
  return {
    reopened: [...v.reopened],
    reclosed: [...v.reclosed],
    flagged: [...v.flagged],
    unflagged: [...v.unflagged],
  };
}

export function cleanMemory(v: unknown): Memory | null {
  if (!object(v)) return null;
  const held = cleanHold(v.held),
    project = projectRef(v.project),
    capsule = cleanCapsule(v.capsule),
    truth = cleanTruth(v);
  if (
    truth === undefined ||
    capsule === undefined ||
    !id(v.id) ||
    !scope(v.scope) ||
    !owner(v.owner) ||
    !nameOrNull(v.author, 32) ||
    typeof v.source !== "string" ||
    !nameOrNull(v.kind, 20) ||
    !(v.importance === null || count(v.importance, 1, 5)) ||
    !nameOrNull(v.volatility, 20) ||
    !status(v.status) ||
    !words(v.tags) ||
    project === undefined ||
    !(
      v.content === null ||
      (typeof v.content === "string" && v.content.length <= maxMemoryBytes)
    ) ||
    typeof v.truncated !== "boolean" ||
    held === undefined ||
    !maybe(v.validFrom) ||
    !maybe(v.validUntil) ||
    !count(v.revision, 1, 2_147_483_647) ||
    !instant(v.createdAt) ||
    !instant(v.updatedAt)
  )
    return null;
  return {
    id: v.id,
    scope: v.scope,
    owner: v.owner,
    author: v.author,
    source: v.source,
    kind: v.kind,
    importance: v.importance as number | null,
    volatility: v.volatility,
    status: v.status,
    tags: [...v.tags],
    project,
    content: v.content as string | null,
    truncated: v.truncated,
    held,
    validFrom: v.validFrom,
    validUntil: v.validUntil,
    revision: v.revision,
    createdAt: v.createdAt,
    updatedAt: v.updatedAt,
    capsule,
    ...truth,
  };
}

function cleanProject(v: unknown): MemoryProject | null {
  if (
    !object(v) ||
    !id(v.id) ||
    !slug(v.slug) ||
    !preview(v.name, 120) ||
    !scope(v.scope) ||
    !owner(v.owner) ||
    !words(v.aliases, 16) ||
    !one(v.status, ["active", "archived"])
  )
    return null;
  // What an engine from before the project writes sends none of.
  const description = v.description === undefined ? "" : v.description;
  const author = v.author === undefined ? null : v.author;
  const revision = v.revision === undefined ? null : v.revision;
  const createdAt = v.createdAt === undefined ? null : v.createdAt;
  const updatedAt = v.updatedAt === undefined ? null : v.updatedAt;
  if (
    typeof description !== "string" ||
    points(description) > 1000 ||
    !(author === null || owner(author)) ||
    !(revision === null || count(revision, 1, 2_147_483_647)) ||
    !maybe(createdAt) ||
    !maybe(updatedAt)
  )
    return null;
  return {
    id: v.id,
    slug: v.slug,
    name: v.name,
    scope: v.scope,
    owner: v.owner,
    aliases: [...v.aliases],
    status: v.status as string,
    description,
    author,
    revision,
    createdAt,
    updatedAt,
  };
}

function cleanChangeRow(v: unknown): ChangeRow | null {
  if (
    !object(v) ||
    !id(v.id) ||
    !count(v.revision, 1, 2_147_483_647) ||
    typeof v.event !== "string" ||
    !/^[a-z_]{1,40}$/.test(v.event) ||
    !instant(v.at) ||
    !count(v.changes, 1, 2_147_483_647) ||
    !(v.preview === null || preview(v.preview, 120)) ||
    typeof v.truncated !== "boolean" ||
    typeof v.held !== "boolean"
  )
    return null;
  return {
    id: v.id,
    revision: v.revision,
    event: v.event,
    at: v.at,
    changes: v.changes,
    // A held memory has no words here, whatever came.
    preview: v.held ? null : v.preview,
    truncated: v.truncated,
    held: v.held,
  };
}

function cleanChangeGroup(v: unknown): ChangeGroup | null {
  if (
    !object(v) ||
    typeof v.group !== "string" ||
    !groupKey.test(v.group) ||
    typeof v.family !== "string" ||
    !/^[a-z_]{1,40}$/.test(v.family) ||
    !(v.person === null || owner(v.person)) ||
    !nameOrNull(v.client, 200) ||
    !count(v.changes, 1, 2_147_483_647) ||
    !count(v.memories, 1, 2_147_483_647) ||
    !instant(v.from) ||
    !instant(v.until) ||
    typeof v.capped !== "boolean" ||
    !Array.isArray(v.items) ||
    v.items.length > 20
  )
    return null;
  const items = v.items.map(cleanChangeRow);
  return items.every((i) => i !== null)
    ? {
        group: v.group,
        family: v.family,
        person: v.person as string | null,
        client: v.client,
        changes: v.changes,
        memories: v.memories,
        from: v.from,
        until: v.until,
        capped: v.capped,
        items: items as ChangeRow[],
      }
    : null;
}

function cleanCapsuleSlot(v: unknown): CapsuleSlotStatus | null {
  if (
    !object(v) ||
    !one(v.slot, [...capsuleSlots.core, ...capsuleSlots.project]) ||
    !one(v.state, ["filled", "empty", "duplicate", "omitted"]) ||
    !(v.filledBy === null || id(v.filledBy)) ||
    !Array.isArray(v.duplicates) ||
    v.duplicates.length > 200 ||
    !v.duplicates.every(id) ||
    !Array.isArray(v.invalid) ||
    v.invalid.length > 200 ||
    !Array.isArray(v.waiting) ||
    v.waiting.length > 200
  )
    return null;
  const invalid = v.invalid.map((i: unknown) =>
    object(i) &&
    id(i.id) &&
    one(i.reason, ["content-too-large", "empty-content"])
      ? {
          id: i.id,
          reason: i.reason as CapsuleSlotStatus["invalid"][0]["reason"],
        }
      : null,
  );
  const waiting = v.waiting.map((w: unknown) =>
    object(w) &&
    id(w.id) &&
    status(w.status) &&
    typeof w.held === "boolean" &&
    typeof w.expired === "boolean"
      ? { id: w.id, status: w.status, held: w.held, expired: w.expired }
      : null,
  );
  if (invalid.some((i) => i === null) || waiting.some((w) => w === null))
    return null;
  return {
    slot: v.slot as string,
    state: v.state as CapsuleSlotStatus["state"],
    filledBy: v.filledBy as string | null,
    duplicates: [...(v.duplicates as string[])],
    invalid: invalid as CapsuleSlotStatus["invalid"],
    waiting: waiting as CapsuleSlotStatus["waiting"],
  };
}

function cleanCapsuleStatus(
  v: Record<string, unknown>,
): MemoryCapsuleStatus | null {
  if (
    (v.kind !== "core" && v.kind !== "project") ||
    !(v.project === null || text(v.project, 128)) ||
    !scope(v.scope) ||
    typeof v.complete !== "boolean" ||
    typeof v.populated !== "boolean" ||
    typeof v.hash !== "string" ||
    !/^sha256:[0-9a-f]{64}$/.test(v.hash) ||
    !count(v.chars, 0, 2_147_483_647) ||
    !count(v.maxChars, 1, 2_147_483_647) ||
    typeof v.served !== "boolean" ||
    !Array.isArray(v.slots) ||
    v.slots.length !== capsuleSlots[v.kind].length
  )
    return null;
  const slots = v.slots.map(cleanCapsuleSlot);
  const order = capsuleSlots[v.kind];
  if (slots.some((x, i) => x === null || x.slot !== order[i])) return null;
  return {
    kind: v.kind,
    project: v.project as string | null,
    scope: v.scope,
    complete: v.complete,
    populated: v.populated,
    hash: v.hash,
    chars: v.chars,
    maxChars: v.maxChars,
    served: v.served,
    slots: slots as CapsuleSlotStatus[],
  };
}

function cleanSettings(v: Record<string, unknown>): MemorySettingsRead | null {
  const flags = [
    "share",
    "capsules",
    "capture",
    "secondBrainImport",
    "recall",
    "recallLog",
    "nightSummary",
  ] as const;
  if (
    !one(v.scopes, ["off", "on"]) ||
    !(v.person === null || owner(v.person)) ||
    flags.some((f) => typeof v[f] !== "boolean") ||
    !count(v.trashDays, 1, 3650) ||
    !count(v.versionsKept, 1, 100_000) ||
    !count(v.maxBytes, 1, 2_147_483_647)
  )
    return null;
  return {
    scopes: v.scopes as "off" | "on",
    person: v.person as string | null,
    share: v.share as boolean,
    capsules: v.capsules as boolean,
    capture: v.capture as boolean,
    secondBrainImport: v.secondBrainImport as boolean,
    recall: v.recall as boolean,
    recallLog: v.recallLog as boolean,
    nightSummary: v.nightSummary as boolean,
    trashDays: v.trashDays,
    versionsKept: v.versionsKept,
    maxBytes: v.maxBytes,
  };
}

function cleanReview(v: unknown): ReviewItem | null {
  if (
    !object(v) ||
    !id(v.id) ||
    !scope(v.scope) ||
    !owner(v.owner) ||
    !nameOrNull(v.author, 32) ||
    !text(v.source, 64) ||
    !status(v.status) ||
    !one(v.reason, reasons) ||
    typeof v.score !== "number" ||
    !Number.isFinite(v.score) ||
    v.score < 0 ||
    !words(v.signals, 20) ||
    !instant(v.heldAt) ||
    !nameOrNull(v.heldBy, 32) ||
    !(
      v.channel === null ||
      one(v.channel, ["app", "mcp", "cli", "system", "capture"])
    ) ||
    !nameOrNull(v.client, 80) ||
    typeof v.mayRelease !== "boolean" ||
    !count(v.revision, 1, 2_147_483_647)
  )
    return null;
  return {
    id: v.id,
    scope: v.scope,
    owner: v.owner,
    author: v.author,
    source: v.source,
    status: v.status,
    reason: v.reason as HoldReason,
    score: v.score,
    signals: [...v.signals],
    heldAt: v.heldAt,
    heldBy: v.heldBy,
    channel: v.channel as string | null,
    client: v.client,
    mayRelease: v.mayRelease,
    revision: v.revision,
  };
}

const releaseResults = [
  "released",
  "unchanged",
  "unknown",
  "forbidden",
  "conflict",
] as const;
function cleanReleased(v: unknown): MemoryReleaseResult | null {
  if (
    !object(v) ||
    !id(v.id) ||
    !one(v.result, releaseResults) ||
    !(v.revision === null || count(v.revision, 1, 2_147_483_647))
  )
    return null;
  return {
    id: v.id,
    result: v.result as MemoryReleaseResult["result"],
    revision: v.revision as number | null,
  };
}

function cleanVersion(v: unknown): MemoryVersion | null {
  if (!object(v)) return null;
  const held = cleanHold(v.held);
  if (
    !id(v.id) ||
    !count(v.revision, 1, 2_147_483_647) ||
    typeof v.current !== "boolean" ||
    !status(v.status) ||
    !words(v.tags) ||
    !(
      v.content === null ||
      (typeof v.content === "string" && v.content.length <= maxMemoryBytes)
    ) ||
    held === undefined ||
    !instant(v.writtenAt) ||
    !nameOrNull(v.person, 32) ||
    !nameOrNull(v.channel, 10)
  )
    return null;
  return {
    id: v.id,
    revision: v.revision,
    current: v.current,
    status: v.status,
    tags: [...v.tags],
    content: v.content as string | null,
    held,
    writtenAt: v.writtenAt,
    person: v.person,
    channel: v.channel,
  };
}

function cleanChange(v: unknown): MemoryChange | null {
  if (
    !object(v) ||
    !count(v.revision, 1, 2_147_483_647) ||
    typeof v.event !== "string" ||
    !/^[a-z_]{1,40}$/.test(v.event) ||
    !instant(v.at) ||
    !nameOrNull(v.person, 32) ||
    !one(v.channel, ["app", "mcp", "cli", "system", "capture"]) ||
    !nameOrNull(v.client, 80) ||
    !words(v.fields, 20) ||
    !(v.status === null || status(v.status)) ||
    !(
      v.preview === null ||
      (typeof v.preview === "string" && points(v.preview) <= 160)
    ) ||
    typeof v.held !== "boolean" ||
    typeof v.kept !== "boolean" ||
    !(v.to === null || count(v.to, 1, 2_147_483_647))
  )
    return null;
  return {
    revision: v.revision,
    event: v.event,
    at: v.at,
    person: v.person,
    channel: v.channel as string,
    client: v.client,
    fields: [...v.fields],
    status: v.status as MemoryStatus | null,
    preview: v.held ? null : v.preview,
    held: v.held,
    kept: v.kept,
    to: v.to as number | null,
  };
}

function cleanTrashed(v: unknown): TrashedMemory | null {
  if (!object(v)) return null;
  const held = cleanHold(v.held);
  if (
    !id(v.id) ||
    !scope(v.scope) ||
    !owner(v.owner) ||
    !status(v.status) ||
    !nameOrNull(v.preview, 4000) ||
    typeof v.truncated !== "boolean" ||
    held === undefined ||
    !instant(v.trashedAt) ||
    !instant(v.purgeAfter) ||
    !count(v.daysLeft, 0, 400) ||
    !id(v.nonce) ||
    typeof v.mayDeleteForever !== "boolean" ||
    !count(v.revision, 1, 2_147_483_647)
  )
    return null;
  return {
    id: v.id,
    scope: v.scope,
    owner: v.owner,
    status: v.status,
    preview: held ? null : v.preview,
    truncated: v.truncated,
    held,
    trashedAt: v.trashedAt,
    purgeAfter: v.purgeAfter,
    daysLeft: v.daysLeft,
    nonce: v.nonce,
    mayDeleteForever: v.mayDeleteForever,
    revision: v.revision,
  };
}

// One dismissal as the engine lists it (SB-P4-05): an id that names its kind and type, two stars, who and when.
function cleanDismissal(v: unknown): MapDismissal | null {
  if (
    !object(v) ||
    typeof v.id !== "string" ||
    !dismissalId.test(v.id) ||
    !one(v.kind, ["memories", "stars"]) ||
    typeof v.type !== "string" ||
    !v.id.startsWith(`${String(v.kind)}:`) ||
    !v.id.endsWith(`:${v.type}`) ||
    !Array.isArray(v.stars) ||
    v.stars.length !== 2 ||
    !instant(v.dismissedAt) ||
    !one(v.by, ["person", "ai_client"]) ||
    typeof v.mine !== "boolean" ||
    typeof v.mayRestore !== "boolean"
  )
    return null;
  const stars = v.stars.map((star: unknown) =>
    object(star) &&
    text(star.id, 1000) &&
    typeof star.title === "string" &&
    star.title.length <= 1000
      ? { id: star.id, title: star.title }
      : null,
  );
  if (stars.some((star) => star === null)) return null;
  return {
    id: v.id,
    kind: v.kind as MapDismissal["kind"],
    type: v.type,
    stars: stars as MapDismissal["stars"],
    dismissedAt: v.dismissedAt,
    by: v.by as MapDismissal["by"],
    mine: v.mine,
    mayRestore: v.mayRestore,
  };
}

// One connection as the engine serves it (run 17), or null.
function cleanConnection(v: unknown): MemoryConnection | null {
  if (!object(v) || !object(v.memory)) return null;
  const m = v.memory;
  const held = cleanHold(m.held);
  const imported = v.imported === undefined ? false : v.imported;
  if (
    !(v.linkId === null || id(v.linkId)) ||
    typeof v.made !== "boolean" ||
    v.made !== (v.linkId !== null) ||
    typeof v.type !== "string" ||
    !linkTypeWord.test(v.type) ||
    !one(v.direction, ["outgoing", "incoming", "none"]) ||
    !one(v.verification, ["verified", "inferred", "suggested"]) ||
    typeof v.mine !== "boolean" ||
    typeof imported !== "boolean" ||
    (imported && !v.made) ||
    typeof v.mayUnlink !== "boolean" ||
    !instant(v.at) ||
    !(
      v.confidence === null ||
      (typeof v.confidence === "number" &&
        v.confidence >= 0 &&
        v.confidence <= 1)
    ) ||
    !id(m.id) ||
    !scope(m.scope) ||
    !owner(m.owner) ||
    !status(m.status) ||
    !states.includes(m.state as MemoryState) ||
    !nameOrNull(m.kind, 20) ||
    !(m.preview === null || preview(m.preview, 120)) ||
    typeof m.truncated !== "boolean" ||
    held === undefined ||
    (held !== null && m.preview !== null)
  )
    return null;
  return {
    linkId: v.linkId as string | null,
    made: v.made,
    type: v.type,
    direction: v.direction as MemoryConnection["direction"],
    verification: v.verification as MemoryConnection["verification"],
    mine: v.mine,
    imported,
    mayUnlink: v.mayUnlink,
    at: v.at,
    confidence: v.confidence as number | null,
    memory: {
      id: m.id,
      scope: m.scope,
      owner: m.owner,
      status: m.status,
      state: m.state as MemoryState,
      kind: m.kind as string | null,
      preview: m.preview as string | null,
      truncated: m.truncated,
      held,
    },
  };
}

// One memory of the stale queue as the engine serves it (run 17), or null.
function cleanStale(v: unknown): StaleMemory | null {
  if (
    !object(v) ||
    !id(v.id) ||
    !scope(v.scope) ||
    !owner(v.owner) ||
    !nameOrNull(v.author, 32) ||
    !status(v.status) ||
    !nameOrNull(v.volatility, 20) ||
    !one(v.reason, ["not_confirmed", "retracted_source"]) ||
    !instant(v.since) ||
    !maybe(v.foundAt) ||
    !maybe(v.confirmedAt) ||
    typeof v.preview !== "string" ||
    points(v.preview) > 300 ||
    typeof v.truncated !== "boolean" ||
    !Array.isArray(v.retractedSources) ||
    v.retractedSources.length > 100 ||
    !v.retractedSources.every(id) ||
    !count(v.revision, 1, 2_147_483_647)
  )
    return null;
  return {
    id: v.id,
    scope: v.scope,
    owner: v.owner,
    author: v.author,
    status: v.status,
    volatility: v.volatility,
    reason: v.reason as StaleMemory["reason"],
    since: v.since,
    foundAt: v.foundAt,
    confirmedAt: v.confirmedAt,
    preview: v.preview,
    truncated: v.truncated,
    retractedSources: [...(v.retractedSources as string[])],
    revision: v.revision,
  };
}

// What every row of the brief's lists holds (SB-P6-01): no list holds a held memory, so each has its words, at most a list's preview.
function cleanPlanRow(v: Record<string, unknown>) {
  const project = projectRef(v.project);
  return id(v.id) &&
    count(v.revision, 1, 2_147_483_647) &&
    scope(v.scope) &&
    owner(v.owner) &&
    nameOrNull(v.author, 32) &&
    status(v.status) &&
    words(v.tags) &&
    project !== undefined &&
    instant(v.createdAt) &&
    typeof v.preview === "string" &&
    points(v.preview) <= 300 &&
    typeof v.truncated === "boolean"
    ? {
        id: v.id,
        revision: v.revision,
        scope: v.scope,
        owner: v.owner,
        author: v.author,
        status: v.status,
        tags: [...v.tags],
        project,
        createdAt: v.createdAt,
        preview: v.preview,
        truncated: v.truncated,
      }
    : null;
}
function cleanLoop(v: unknown): OpenLoop | null {
  if (!object(v)) return null;
  const row = cleanPlanRow(v);
  return row && one(v.direction, ["out", "in"]) && nameOrNull(v.counterparty)
    ? {
        ...row,
        direction: v.direction as LoopDirection,
        counterparty: v.counterparty,
      }
    : null;
}
const dueKinds: readonly DueKind[] = ["due", "event", "wake"];
const dueSources = ["explicit", "text"];
const dueWindows: readonly DueWindow[] = ["overdue", "upcoming", "later"];
// A due date as an answer gives it: an object, or null for none; undefined when it doesn't check out.
function cleanDueDate(v: unknown): MemoryDueDate | null | undefined {
  if (v === null) return null;
  return object(v) &&
    instant(v.at) &&
    text(v.local, 40) &&
    text(v.zone, 64) &&
    one(v.kind, dueKinds) &&
    one(v.source, dueSources)
    ? {
        at: v.at,
        local: v.local,
        zone: v.zone,
        kind: v.kind as DueKind,
        source: v.source as MemoryDueDate["source"],
      }
    : undefined;
}
function cleanDueRow(v: unknown): DueRow | null {
  if (!object(v)) return null;
  const row = cleanPlanRow(v);
  return row &&
    one(v.window, dueWindows) &&
    instant(v.at) &&
    text(v.local, 40) &&
    one(v.kind, dueKinds) &&
    one(v.source, dueSources) &&
    (v.loop === null || one(v.loop, ["out", "in"]))
    ? {
        ...row,
        window: v.window as DueWindow,
        at: v.at,
        local: v.local,
        kind: v.kind as DueKind,
        source: v.source as DueRow["source"],
        loop: v.loop as LoopDirection | null,
      }
    : null;
}
const unit = (v: unknown): v is number =>
  typeof v === "number" && Number.isFinite(v) && v >= 0 && v <= 1;
const unitOrNull = (v: unknown): v is number | null => v === null || unit(v);
function cleanDecisionRow(v: unknown): DecisionRow | null {
  if (!object(v)) return null;
  const row = cleanPlanRow(v);
  return row &&
    (v.confidence === null || unit(v.confidence)) &&
    (v.confidenceSource === null ||
      one(v.confidenceSource, ["stated", "inferred"])) &&
    (v.outcome === null || one(v.outcome, outcomes)) &&
    count(v.rearms, 0, 2) &&
    maybe(v.reviewAt) &&
    (v.window === null || one(v.window, dueWindows)) &&
    typeof v.current === "boolean" &&
    typeof v.edited === "boolean"
    ? {
        ...row,
        confidence: v.confidence,
        confidenceSource: v.confidenceSource as DecisionRow["confidenceSource"],
        outcome: v.outcome as DecisionRow["outcome"],
        rearms: v.rearms,
        reviewAt: v.reviewAt,
        window: v.window as DecisionRow["window"],
        current: v.current,
        edited: v.edited,
      }
    : null;
}
const calibrationBands = ["50-59", "60-69", "70-79", "80-89", "90-95"];
function cleanCalibrationBand(v: unknown): CalibrationBand | null {
  return object(v) &&
    one(v.band, calibrationBands) &&
    count(v.n, 0, 2_147_483_647) &&
    count(v.nStated, 0, 2_147_483_647) &&
    count(v.nInferred, 0, 2_147_483_647) &&
    typeof v.shown === "boolean" &&
    unitOrNull(v.meanConfidence) &&
    unitOrNull(v.hitRate) &&
    unitOrNull(v.low) &&
    unitOrNull(v.high)
    ? {
        band: v.band as string,
        n: v.n,
        nStated: v.nStated,
        nInferred: v.nInferred,
        shown: v.shown,
        meanConfidence: v.meanConfidence,
        hitRate: v.hitRate,
        low: v.low,
        high: v.high,
      }
    : null;
}
const figureOrNull = (v: unknown): v is number | null =>
  v === null || (typeof v === "number" && Number.isFinite(v));
function cleanCalibration(v: unknown): MemoryCalibration | null {
  if (!object(v) || !Array.isArray(v.bands) || v.bands.length !== 5)
    return null;
  const bands = v.bands.map(cleanCalibrationBand);
  if (
    !bands.every((b) => b !== null) ||
    !one(v.source, ["all", "stated", "inferred"]) ||
    typeof v.ready !== "boolean" ||
    !count(v.n, 0, 2_147_483_647) ||
    !count(v.nStated, 0, 2_147_483_647) ||
    !count(v.nInferred, 0, 2_147_483_647) ||
    !count(v.minDecisions, 0, 1000) ||
    !count(v.minBand, 0, 1000) ||
    !count(v.minTopic, 0, 1000) ||
    !figureOrNull(v.brier) ||
    !figureOrNull(v.gap) ||
    !(v.direction === null || one(v.direction, ["over", "under", "in_line"])) ||
    !(v.headline === null || one(v.headline, calibrationBands)) ||
    !one(v.kind, ["not_ready", "rate", "in_line", "no_range"]) ||
    !text(v.line, 400) ||
    !nameOrNull(v.topicLine, 400) ||
    !(v.stated === null || count(v.stated, 0, 100)) ||
    !(v.hit === null || count(v.hit, 0, 100))
  )
    return null;
  const topic = v.topic;
  if (
    topic !== null &&
    !(
      object(topic) &&
      text(topic.name, 128) &&
      count(topic.n, 0, 2_147_483_647) &&
      typeof topic.gap === "number" &&
      one(topic.direction, ["over", "under"])
    )
  )
    return null;
  return {
    source: v.source as MemoryCalibration["source"],
    ready: v.ready,
    n: v.n,
    nStated: v.nStated,
    nInferred: v.nInferred,
    minDecisions: v.minDecisions,
    minBand: v.minBand,
    minTopic: v.minTopic,
    brier: v.brier,
    gap: v.gap,
    direction: v.direction as MemoryCalibration["direction"],
    bands: bands as CalibrationBand[],
    headline: v.headline as string | null,
    topic:
      topic === null
        ? null
        : {
            name: topic.name as string,
            n: topic.n as number,
            gap: topic.gap as number,
            direction: topic.direction as "over" | "under",
          },
    kind: v.kind as MemoryCalibration["kind"],
    line: v.line,
    topicLine: v.topicLine,
    stated: v.stated,
    hit: v.hit,
  };
}

// The longest preview the old memory carries (MemoryStore.ResurfacePreview).
const resurfacePreview = 280;
function cleanResurfaced(v: unknown): ResurfacedMemory | null {
  return object(v) &&
    id(v.id) &&
    count(v.revision, 1, 2_147_483_647) &&
    scope(v.scope) &&
    owner(v.owner) &&
    preview(v.preview, resurfacePreview) &&
    typeof v.truncated === "boolean" &&
    instant(v.createdAt)
    ? {
        id: v.id,
        revision: v.revision,
        scope: v.scope,
        owner: v.owner,
        preview: v.preview,
        truncated: v.truncated,
        createdAt: v.createdAt,
      }
    : null;
}

// The longest preview a brief's item carries (MemoryStore.BriefPreview), and how many of each section it shows.
const briefPreview = 120;
function cleanBriefItem(v: unknown): BriefItem | null {
  if (
    !object(v) ||
    !id(v.id) ||
    !count(v.revision, 1, 2_147_483_647) ||
    !scope(v.scope) ||
    !owner(v.owner) ||
    typeof v.preview !== "string" ||
    points(v.preview) > briefPreview ||
    typeof v.truncated !== "boolean"
  )
    return null;
  return {
    id: v.id,
    revision: v.revision,
    scope: v.scope,
    owner: v.owner,
    preview: v.preview,
    truncated: v.truncated,
  };
}
function cleanBriefDated(v: unknown): BriefDated | null {
  const item = cleanBriefItem(v);
  if (
    !item ||
    !object(v) ||
    !one(v.window, ["overdue", "upcoming"]) ||
    !instant(v.at)
  )
    return null;
  // A due date says how it reads on the install's clocks; a review date doesn't.
  const local = v.local === undefined ? null : v.local;
  if (!nameOrNull(local, 40)) return null;
  return {
    ...item,
    window: v.window as BriefDated["window"],
    at: v.at,
    local,
  };
}
function cleanBriefLoop(v: unknown): BriefLoop | null {
  const item = cleanBriefItem(v);
  return item && object(v) && nameOrNull(v.counterparty, 200)
    ? { ...item, counterparty: v.counterparty }
    : null;
}
function cleanBriefStale(v: unknown): BriefStale | null {
  const item = cleanBriefItem(v);
  return item &&
    object(v) &&
    typeof v.reason === "string" &&
    /^[a-z_]{1,40}$/.test(v.reason)
    ? { ...item, reason: v.reason }
    : null;
}
function cleanBriefInsight(v: unknown): BriefInsight | null {
  return object(v) &&
    id(v.id) &&
    scope(v.scope) &&
    owner(v.owner) &&
    text(v.title, 400)
    ? { id: v.id, scope: v.scope, owner: v.owner, title: v.title }
    : null;
}
function cleanBriefContradiction(v: unknown): BriefContradiction | null {
  const item = cleanBriefItem(v);
  if (!item || !object(v) || !object(v.conflict)) return null;
  const { conflict } = v;
  return id(conflict.id) &&
    typeof conflict.preview === "string" &&
    points(conflict.preview) <= briefPreview &&
    typeof conflict.truncated === "boolean"
    ? {
        ...item,
        conflict: {
          id: conflict.id,
          preview: conflict.preview,
          truncated: conflict.truncated,
        },
      }
    : null;
}
function cleanBriefGroup(v: unknown): BriefGroup | null {
  return object(v) &&
    typeof v.group === "string" &&
    /^[A-Za-z0-9_-]{1,200}$/.test(v.group) &&
    typeof v.family === "string" &&
    /^[a-z_]{1,40}$/.test(v.family) &&
    nameOrNull(v.client, 200) &&
    count(v.changes, 1, 2_147_483_647) &&
    count(v.memories, 1, 2_147_483_647) &&
    instant(v.until)
    ? {
        group: v.group,
        family: v.family,
        client: v.client,
        changes: v.changes,
        memories: v.memories,
        until: v.until,
      }
    : null;
}
// A section whose count says fewer than it shows is no answer of the engine's: its count is its whole list's length.
function cleanBriefList<T>(
  v: unknown,
  clean: (x: unknown) => T | null,
  max: number,
): BriefList<T> | null {
  if (
    !object(v) ||
    !count(v.count, 0, 2_147_483_647) ||
    !Array.isArray(v.items) ||
    v.items.length > max ||
    v.items.length > v.count
  )
    return null;
  const items = v.items.map(clean);
  return items.every((x) => x !== null)
    ? { count: v.count, items: items as T[] }
    : null;
}
function cleanBriefWindows<T>(
  v: unknown,
  clean: (x: unknown) => T | null,
  max: number,
): BriefWindows<T> | null {
  const list = cleanBriefList(v, clean, max);
  return list &&
    object(v) &&
    count(v.overdue, 0, 2_147_483_647) &&
    count(v.upcoming, 0, 2_147_483_647) &&
    v.overdue + v.upcoming === list.count
    ? { ...list, overdue: v.overdue, upcoming: v.upcoming }
    : null;
}
export function cleanBrief(v: unknown): MemoryBrief | null {
  if (
    !object(v) ||
    !(v.project === null || text(v.project, 64)) ||
    typeof v.unknownProject !== "boolean" ||
    !text(v.zone, 64) ||
    !text(v.says, 400) ||
    !count(v.held, 0, 2_147_483_647) ||
    !object(v.calibration) ||
    typeof v.calibration.ready !== "boolean" ||
    !count(v.calibration.n, 0, 2_147_483_647) ||
    !nameOrNull(v.calibration.line, 400) ||
    !object(v.changes) ||
    !count(v.changes.count, 0, 2_147_483_647) ||
    !count(v.changes.changes, 0, 2_147_483_647) ||
    typeof v.changes.truncated !== "boolean" ||
    !Array.isArray(v.changes.groups) ||
    v.changes.groups.length > 5 ||
    v.changes.groups.length > v.changes.count
  )
    return null;
  const due = cleanBriefWindows(v.due, cleanBriefDated, 5);
  const decisions = cleanBriefWindows(v.decisions, cleanBriefDated, 3);
  const youOwe = cleanBriefList(v.youOwe, cleanBriefLoop, 5);
  const owedToYou = cleanBriefList(v.owedToYou, cleanBriefLoop, 5);
  const stale = cleanBriefList(v.stale, cleanBriefStale, 2);
  const insights = cleanBriefList(v.insights, cleanBriefInsight, 1);
  const contradictions = cleanBriefList(
    v.contradictions,
    cleanBriefContradiction,
    3,
  );
  const duplicates = cleanBriefList(v.duplicates, cleanBriefItem, 3);
  const standing = cleanBriefList(v.standing, cleanBriefItem, 3);
  const groups = v.changes.groups.map(cleanBriefGroup);
  if (
    !due ||
    !decisions ||
    !youOwe ||
    !owedToYou ||
    !stale ||
    !insights ||
    !contradictions ||
    !duplicates ||
    !standing ||
    groups.some((g) => g === null)
  )
    return null;
  return {
    project: v.project,
    unknownProject: v.unknownProject,
    zone: v.zone,
    says: v.says,
    held: v.held,
    due,
    decisions,
    youOwe,
    owedToYou,
    stale,
    insights,
    contradictions,
    duplicates,
    standing,
    calibration: {
      ready: v.calibration.ready,
      n: v.calibration.n,
      line: v.calibration.line,
    },
    changes: {
      count: v.changes.count,
      changes: v.changes.changes,
      truncated: v.changes.truncated,
      groups: groups as BriefGroup[],
    },
  };
}

const linkRefs = (v: unknown): v is { id: string; type: string }[] =>
  Array.isArray(v) &&
  v.length <= 20 &&
  v.every(
    (r) =>
      object(r) &&
      id(r.id) &&
      typeof r.type === "string" &&
      linkTypeWord.test(r.type),
  );

function cleanWritten(
  v: Record<string, unknown>,
  op: string,
): MemoryWritten | null {
  const held = v.held === undefined ? null : cleanHold(v.held);
  const project =
    v.project === undefined || v.project === null
      ? null
      : object(v.project) &&
          slug(v.project.slug) &&
          typeof v.project.created === "boolean"
        ? { slug: v.project.slug, created: v.project.created }
        : undefined;
  const trash =
    op !== "forget"
      ? null
      : object(v.trash) && id(v.trash.nonce) && count(v.trash.days, 0, 400)
        ? { nonce: v.trash.nonce, days: v.trash.days }
        : undefined;
  const capsule = op === "capsule" ? cleanCapsule(v.capsule) : null;
  const said = cleanSaid(v, op);
  if (
    said === undefined ||
    capsule === undefined ||
    v.op !== op ||
    !id(v.id) ||
    !owner(v.owner) ||
    !one(v.scopes, ["off", "on"]) ||
    held === undefined ||
    project === undefined ||
    trash === undefined ||
    (op !== "delete" &&
      (!count(v.revision, 1, 2_147_483_647) ||
        !scope(v.scope) ||
        !status(v.status)))
  )
    return null;
  return {
    op: op as MemoryWritten["op"],
    id: v.id,
    revision: op === "delete" ? null : (v.revision as number),
    to: count(v.to, 1, 2_147_483_647) ? v.to : null,
    scope: op === "delete" ? null : (v.scope as MemoryScope),
    owner: v.owner,
    status: op === "delete" ? null : (v.status as MemoryStatus),
    scopes: v.scopes as "off" | "on",
    project,
    unchanged: v.unchanged === true,
    replayed: v.replayed === true,
    droppedTags: words(v.droppedTags) ? [...v.droppedTags] : [],
    held,
    trash,
    capsule,
    projectLeft: op === "share" && v.projectLeft === true,
    cascade: cleanCascade(v.cascade),
    ...said,
  };
}

// What a person's word on one of the brief's lists said beside the write, only for those ops, so every other write reads as
// before; undefined when it doesn't check out.
function cleanSaid(
  v: Record<string, unknown>,
  op: string,
): Partial<MemoryWritten> | undefined {
  if (op === "resolve_loop")
    return one(v.action, ["done", "received", "not_a_task"]) &&
      one(v.direction, ["out", "in"]) &&
      nameOrNull(v.counterparty)
      ? {
          action: v.action as string,
          direction: v.direction as LoopDirection,
          counterparty: v.counterparty,
        }
      : undefined;
  if (op === "resolve_due") {
    const due = cleanDueDate(v.due),
      before = cleanDueDate(v.before);
    return one(v.action, ["snooze", "clear_date"]) &&
      due !== undefined &&
      before !== undefined
      ? { action: v.action as string, due, before }
      : undefined;
  }
  if (op === "record_outcome")
    return one(v.result, outcomes) &&
      (v.previous === null || one(v.previous, outcomes)) &&
      count(v.rearms, 0, 2) &&
      maybe(v.reviewAt) &&
      typeof v.reviewsDone === "boolean"
      ? {
          result: v.result as MemoryOutcome,
          previous: v.previous as MemoryOutcome | null,
          rearms: v.rearms,
          reviewAt: v.reviewAt,
          reviewsDone: v.reviewsDone,
        }
      : undefined;
  return {};
}

const codes = [
  "unknown",
  "forbidden",
  "invalid",
  "too_large",
  "conflict",
  "key_reused",
  "unknown_project",
  "taken",
  "nothing_to_undo",
  "no_version",
  "not_visible",
  "pruned",
  "moved",
  "unavailable",
  // A share's (run 14): memory scopes off, a memory held for review, and one no current person wrote to take back.
  "scopes_off",
  "held",
  "no_author",
  // Still true's (run 17): a memory marked wrong is not confirmed as true.
  "retracted",
  // A capsule's status (SB-P6-01): over 200 canonical memories in its slots.
  "too_many_candidates",
  // A person's review queues (SB-P6-01): only a person does this, the pair is not queued or no longer stands, it was settled, a
  // replacement its rules refuse; and what the store says of a standing instruction past its limit or a digest before its time.
  "needs_person",
  "not_offered",
  "settled",
  "protected",
  "limit",
  "not_due",
];

// A person's review queues' answers (SB-P6-01), each in the contract's shape; null when one doesn't check out.
const score = (v: unknown): v is number =>
  typeof v === "number" && Number.isFinite(v);
const maybeOwner = (v: unknown): v is string | null => v === null || owner(v);
function cleanDuplicate(v: unknown): DuplicateMemory | null {
  if (
    !object(v) ||
    !id(v.id) ||
    !scope(v.scope) ||
    !owner(v.owner) ||
    !maybeOwner(v.author) ||
    !status(v.status) ||
    !instant(v.createdAt) ||
    typeof v.preview !== "string" ||
    points(v.preview) > 300 ||
    typeof v.truncated !== "boolean" ||
    !Array.isArray(v.duplicateOf) ||
    v.duplicateOf.length > 3 ||
    !count(v.revision, 1, 2_147_483_647)
  )
    return null;
  const copies = v.duplicateOf.map((c: unknown) =>
    object(c) && id(c.id) && score(c.score)
      ? { id: c.id, score: c.score }
      : null,
  );
  if (copies.some((c) => c === null)) return null;
  return {
    id: v.id,
    scope: v.scope,
    owner: v.owner,
    author: v.author,
    status: v.status,
    createdAt: v.createdAt,
    preview: v.preview,
    truncated: v.truncated,
    duplicateOf: copies as MemoryCopyRef[],
    revision: v.revision,
  };
}
function cleanSide(v: unknown): ContradictionSide | null {
  return object(v) &&
    id(v.id) &&
    count(v.revision, 1, 2_147_483_647) &&
    maybeOwner(v.author) &&
    status(v.status) &&
    typeof v.preview === "string" &&
    points(v.preview) <= 300 &&
    typeof v.truncated === "boolean" &&
    maybe(v.startedAt)
    ? {
        id: v.id,
        revision: v.revision,
        author: v.author,
        status: v.status,
        preview: v.preview,
        truncated: v.truncated,
        startedAt: v.startedAt,
      }
    : null;
}
function cleanContradiction(v: unknown): Contradiction | null {
  if (
    !object(v) ||
    !scope(v.scope) ||
    !owner(v.owner) ||
    !score(v.score) ||
    !instant(v.offeredAt) ||
    typeof v.maySettle !== "boolean"
  )
    return null;
  const memory = cleanSide(v.memory);
  const conflict = cleanSide(v.conflict);
  const p = v.proposed;
  if (
    !memory ||
    !conflict ||
    !(
      p === null ||
      (object(p) &&
        instant(p.at) &&
        maybeOwner(p.person) &&
        nameOrNull(p.client, 80) &&
        (p.reason === null ||
          (typeof p.reason === "string" && points(p.reason) <= 200)))
    )
  )
    return null;
  return {
    scope: v.scope,
    owner: v.owner,
    score: v.score,
    offeredAt: v.offeredAt,
    memory,
    conflict,
    proposed: p
      ? {
          at: p.at as string,
          person: p.person as string | null,
          client: p.client as string | null,
          reason: p.reason as string | null,
        }
      : null,
    maySettle: v.maySettle,
  };
}
function cleanPairSide(v: unknown): PairSide | null {
  return object(v) &&
    id(v.id) &&
    count(v.revision, 1, 2_147_483_647) &&
    instant(v.createdAt) &&
    (v.importance === null || count(v.importance, 1, 5)) &&
    typeof v.preview === "string" &&
    points(v.preview) <= 300 &&
    typeof v.truncated === "boolean"
    ? {
        id: v.id,
        revision: v.revision,
        createdAt: v.createdAt,
        importance: v.importance as number | null,
        preview: v.preview,
        truncated: v.truncated,
      }
    : null;
}
function cleanPair(v: unknown): InsightPair | null {
  if (
    !object(v) ||
    !id(v.id) ||
    !scope(v.scope) ||
    !owner(v.owner) ||
    !score(v.score) ||
    !words(v.signals, 8) ||
    !score(v.similarity) ||
    !count(v.gapDays, 0, 2_147_483_647) ||
    !instant(v.foundAt) ||
    !one(v.status, ["pending", "used", "rejected"]) ||
    !maybe(v.decidedAt) ||
    !(
      v.note === null ||
      (typeof v.note === "string" && points(v.note) <= 500)
    ) ||
    !(v.insight === null || id(v.insight))
  )
    return null;
  const older = cleanPairSide(v.older);
  const newer = cleanPairSide(v.newer);
  if (!older || !newer) return null;
  return {
    id: v.id,
    scope: v.scope,
    owner: v.owner,
    score: v.score,
    signals: [...v.signals],
    similarity: v.similarity,
    gapDays: v.gapDays,
    foundAt: v.foundAt,
    older,
    newer,
    status: v.status as InsightPair["status"],
    decidedAt: v.decidedAt,
    note: v.note as string | null,
    insight: v.insight as string | null,
  };
}
function cleanStreamInsight(v: unknown): StreamInsight | null {
  if (
    !object(v) ||
    !id(v.id) ||
    !one(v.view, insightViews) ||
    !one(v.review, insightReviews) ||
    !maybe(v.reviewedAt) ||
    !maybe(v.snoozeUntil) ||
    typeof v.snoozeEnded !== "boolean" ||
    !instant(v.shownAt) ||
    !scope(v.scope) ||
    !owner(v.owner) ||
    !Array.isArray(v.candidates) ||
    v.candidates.length > 100 ||
    !v.candidates.every(id) ||
    typeof v.mayReview !== "boolean"
  )
    return null;
  const insight = cleanInsight(v.insight);
  if (!insight || insight.id !== v.id) return null;
  return {
    id: v.id,
    view: v.view as InsightView,
    review: v.review as InsightReview,
    reviewedAt: v.reviewedAt,
    snoozeUntil: v.snoozeUntil,
    snoozeEnded: v.snoozeEnded,
    shownAt: v.shownAt,
    scope: v.scope,
    owner: v.owner,
    candidates: [...(v.candidates as string[])],
    mayReview: v.mayReview,
    insight,
  };
}
function cleanDay(v: unknown): InsightDay | null {
  return object(v) &&
    typeof v.date === "string" &&
    /^\d{4}-\d\d-\d\d$/.test(v.date) &&
    text(v.zone, 64) &&
    one(v.outcome, ["saved", "quiet", "blocked", "none"]) &&
    (v.note === null ||
      (typeof v.note === "string" && points(v.note) <= 500)) &&
    (v.insight === null || id(v.insight)) &&
    count(v.declined, 0, 2_147_483_647) &&
    text(v.says, 200)
    ? {
        date: v.date,
        zone: v.zone,
        outcome: v.outcome as InsightDay["outcome"],
        note: v.note as string | null,
        insight: v.insight as string | null,
        declined: v.declined,
        says: v.says,
      }
    : null;
}
function cleanInsightEvent(v: unknown): InsightEvent | null {
  return object(v) &&
    one(v.event, [
      "feedback",
      "confirmed",
      "dismissed",
      "reopened",
      "replaced",
    ]) &&
    (v.value === null ||
      one(v.value, [
        "interesting",
        "explore",
        "not_useful",
        "later",
        "handled",
      ])) &&
    maybe(v.until) &&
    (v.note === null ||
      (typeof v.note === "string" && points(v.note) <= 500)) &&
    maybeOwner(v.person) &&
    (v.channel === null || one(v.channel, ["app", "mcp", "cli"])) &&
    nameOrNull(v.client, 80) &&
    instant(v.at)
    ? {
        event: v.event as string,
        value: v.value as string | null,
        until: v.until,
        note: v.note as string | null,
        person: v.person,
        channel: v.channel as string | null,
        client: v.client,
        at: v.at,
      }
    : null;
}
function cleanResolved(v: Record<string, unknown>): MemoryResolved | null {
  const held = cleanHold(v.held);
  if (
    v.op !== "resolve" ||
    !one(v.queue, [
      "held",
      "duplicates",
      "contradictions",
      "stale",
      "insight_candidates",
      "insights",
    ]) ||
    !one(v.action, resolveActions) ||
    !id(v.id) ||
    typeof v.unchanged !== "boolean" ||
    typeof v.waiting !== "boolean" ||
    typeof v.stillCounted !== "boolean" ||
    !(v.revision === null || count(v.revision, 1, 2_147_483_647)) ||
    !(v.scope === null || scope(v.scope)) ||
    !maybeOwner(v.owner) ||
    !(v.status === null || status(v.status)) ||
    !words(v.changed, 16) ||
    held === undefined ||
    !(v.target === null || id(v.target)) ||
    !(v.decision === null || one(v.decision, ["keep_both", "replaced"])) ||
    !(v.review === null || one(v.review, insightReviews)) ||
    !(v.view === null || one(v.view, insightViews)) ||
    !(v.candidate === null || one(v.candidate, ["used", "rejected"])) ||
    !(
      v.note === null ||
      (typeof v.note === "string" && points(v.note) <= 500)
    ) ||
    !(v.insight === null || id(v.insight)) ||
    !(v.was === null || one(v.was, ["not_confirmed", "retracted_source"]))
  )
    return null;
  return {
    op: "resolve",
    queue: v.queue as string,
    action: v.action as MemoryResolveAction,
    id: v.id,
    unchanged: v.unchanged,
    waiting: v.waiting,
    stillCounted: v.stillCounted,
    revision: v.revision as number | null,
    scope: v.scope as MemoryScope | null,
    owner: v.owner,
    status: v.status as MemoryStatus | null,
    changed: [...v.changed],
    held,
    cascade: cleanCascade(v.cascade),
    target: v.target as string | null,
    decision: v.decision as MemoryResolved["decision"],
    review: v.review as InsightReview | null,
    view: v.view as InsightView | null,
    candidate: v.candidate as MemoryResolved["candidate"],
    note: v.note as string | null,
    insight: v.insight as string | null,
    was: v.was as MemoryResolved["was"],
  };
}

// One standing instruction as the engine lists it (run 22): a held one with no words and no project, one that fires with no reason why
// not, and one that doesn't with one; null when it doesn't check out.
const standingReasons: readonly StandingReason[] = [
  "held",
  "project_held",
  "over_limit",
];
function cleanStanding(v: unknown): StandingInstruction | null {
  if (!object(v)) return null;
  const held = cleanHold(v.held);
  const project =
    v.project === null
      ? null
      : object(v.project) && id(v.project.id) && slug(v.project.slug)
        ? { id: v.project.id, slug: v.project.slug }
        : undefined;
  if (
    held === undefined ||
    project === undefined ||
    !id(v.id) ||
    !count(v.revision, 1, 2_147_483_647) ||
    !scope(v.scope) ||
    !owner(v.owner) ||
    !(v.author === null || typeof v.author === "string") ||
    !text(v.source, 64) ||
    !status(v.status) ||
    !instant(v.createdAt) ||
    typeof v.fires !== "boolean" ||
    !(v.fires ? v.reason === null : one(v.reason, standingReasons)) ||
    !(held ? v.content === null && project === null : preview(v.content, 500))
  )
    return null;
  return {
    id: v.id,
    revision: v.revision,
    scope: v.scope,
    owner: v.owner,
    author: v.author as string | null,
    source: v.source,
    status: v.status,
    project,
    createdAt: v.createdAt,
    content: held ? null : (v.content as string),
    held,
    fires: v.fires,
    reason: v.reason as StandingReason | null,
  };
}

// A refusal, its reason the engine's own words (fit to show), or null.
export function cleanRefusal(v: unknown): MemoryRefused | null {
  if (
    !object(v) ||
    v.ok !== false ||
    !one(v.code, codes) ||
    !text(v.reason, 500)
  )
    return null;
  return {
    ok: false,
    code: v.code as string,
    reason: v.reason,
    revision: count(v.revision, 0, 2_147_483_647) ? v.revision : null,
    known: Array.isArray(v.known) ? v.known.filter(slug).slice(0, 10) : [],
  };
}

// One op's answer as the engine gives it, rebuilt; null when it doesn't check out. The facade passes this on, and
// the browser checks it again with cleanPassedAnswer.
export function cleanMemoryAnswer(
  op: string,
  v: unknown,
): Record<string, unknown> | null {
  if (!object(v)) return null;
  if (v.ok === false) return cleanRefusal(v) as Record<string, unknown> | null;
  if (v.ok !== true) return null;
  const all = <T>(
    list: unknown,
    clean: (x: unknown) => T | null,
    max: number,
  ): T[] | null => {
    if (!Array.isArray(list) || list.length > max) return null;
    const out = list.map(clean);
    return out.every((x) => x !== null) ? (out as T[]) : null;
  };
  const next = (n: unknown) => (n === null || text(n, 200) ? n : undefined);
  switch (op) {
    case "list": {
      const memories = all(v.memories, cleanMemory, 50);
      return memories && next(v.next) !== undefined
        ? { ok: true, memories: memories.map(listed), next: v.next }
        : null;
    }
    case "trash": {
      const trash = all(v.trash, cleanTrashed, 50);
      return trash && next(v.next) !== undefined
        ? { ok: true, trash, next: v.next }
        : null;
    }
    case "get": {
      if (Object.hasOwn(v, "version")) {
        const version = cleanVersion(v.version);
        return version ? { ok: true, memory: null, version } : null;
      }
      const memory = cleanMemory(v.memory);
      return memory ? { ok: true, memory, version: null } : null;
    }
    case "history": {
      const changes = all(v.changes, cleanChange, 100);
      if (
        !changes ||
        !id(v.id) ||
        !count(v.revision, 1, 2_147_483_647) ||
        !(v.next === null || count(v.next, 1, 2_147_483_647)) ||
        typeof v.cut !== "boolean"
      )
        return null;
      return {
        ok: true,
        history: {
          id: v.id,
          revision: v.revision,
          changes,
          next: v.next as number | null,
          cut: v.cut,
          pruned: v.pruned === true,
          oldestKept: count(v.oldestKept, 0, 2_147_483_647) ? v.oldestKept : 0,
        },
      };
    }
    case "projects": {
      const projects = all(v.projects, cleanProject, 500);
      return projects ? { ok: true, projects } : null;
    }
    // The full memory UI's list (SB-P6-01) keeps the engine's shape too.
    case "tags": {
      const tags = all(
        v.tags,
        (t) =>
          object(t) && preview(t.tag, 128) && count(t.count, 1, 2_147_483_647)
            ? { tag: t.tag, count: t.count }
            : null,
        200,
      );
      return tags &&
        typeof v.truncated === "boolean" &&
        count(v.count, 0, 2_147_483_647)
        ? { ok: true, tags, truncated: v.truncated, count: v.count }
        : null;
    }
    case "changes": {
      const groups = all(v.groups, cleanChangeGroup, 50);
      return groups && typeof v.truncated === "boolean" && instant(v.since)
        ? {
            ok: true,
            groups,
            truncated: v.truncated,
            since: v.since,
            ...(count(v.count, 0, 2_147_483_647) ? { count: v.count } : {}),
          }
        : null;
    }
    // The brief's lists (SB-P6-01) keep the engine's shape too. A page never holds more than its count says.
    case "loops": {
      const loops = all(v.loops, cleanLoop, 50);
      return loops &&
        next(v.next) !== undefined &&
        count(v.count, loops.length, 2_147_483_647) &&
        count(v.youOwe, 0, 2_147_483_647) &&
        count(v.owedToYou, 0, 2_147_483_647)
        ? {
            ok: true,
            loops,
            next: v.next,
            count: v.count,
            youOwe: v.youOwe,
            owedToYou: v.owedToYou,
          }
        : null;
    }
    case "due": {
      const due = all(v.due, cleanDueRow, 50);
      return due &&
        next(v.next) !== undefined &&
        count(v.count, due.length, 2_147_483_647) &&
        count(v.overdue, 0, 2_147_483_647) &&
        count(v.upcoming, 0, 2_147_483_647) &&
        count(v.later, 0, 2_147_483_647) &&
        text(v.zone, 64)
        ? {
            ok: true,
            due,
            next: v.next,
            count: v.count,
            overdue: v.overdue,
            upcoming: v.upcoming,
            later: v.later,
            zone: v.zone,
          }
        : null;
    }
    case "decisions": {
      const decisions = all(v.decisions, cleanDecisionRow, 50);
      return decisions &&
        next(v.next) !== undefined &&
        count(v.count, decisions.length, 2_147_483_647) &&
        count(v.open, 0, 2_147_483_647) &&
        count(v.dueNow, 0, 2_147_483_647) &&
        count(v.upcoming, 0, 2_147_483_647) &&
        count(v.resolved, 0, 2_147_483_647) &&
        count(v.scored, 0, 2_147_483_647)
        ? {
            ok: true,
            decisions,
            next: v.next,
            count: v.count,
            open: v.open,
            dueNow: v.dueNow,
            upcoming: v.upcoming,
            resolved: v.resolved,
            scored: v.scored,
          }
        : null;
    }
    case "calibration": {
      const answer = cleanCalibration(v);
      return answer ? { ok: true, ...answer } : null;
    }
    case "capsule_status": {
      const answer = cleanCapsuleStatus(v);
      return answer ? { ok: true, ...answer } : null;
    }
    case "settings": {
      const answer = cleanSettings(v);
      return answer ? { ok: true, ...answer } : null;
    }
    case "undo_group": {
      const results = all(
        v.results,
        (r) =>
          object(r) &&
          id(r.id) &&
          typeof r.result === "string" &&
          /^[a-z_]{1,40}$/.test(r.result)
            ? { id: r.id, result: r.result }
            : null,
        50,
      );
      return results &&
        v.op === "undo_group" &&
        typeof v.group === "string" &&
        groupKey.test(v.group) &&
        count(v.total, 0, 2_147_483_647) &&
        count(v.remaining, 0, 2_147_483_647) &&
        typeof v.done === "boolean" &&
        typeof v.capped === "boolean" &&
        (v.redo === null ||
          (typeof v.redo === "string" && groupKey.test(v.redo)))
        ? {
            ok: true,
            op: "undo_group",
            group: v.group,
            total: v.total,
            remaining: v.remaining,
            done: v.done,
            capped: v.capped,
            results,
            redo: v.redo,
          }
        : null;
    }
    case "forget_all": {
      const results = all(
        v.results,
        (r) =>
          object(r) &&
          id(r.id) &&
          one(r.result, ["forgotten", "unknown", "forbidden", "conflict"]) &&
          (r.revision === null || count(r.revision, 1, 2_147_483_647)) &&
          (r.result === "forgotten"
            ? id(r.nonce) && count(r.days, 0, 400)
            : r.nonce === null && r.days === null)
            ? {
                id: r.id,
                result: r.result,
                revision: r.revision,
                nonce: r.nonce,
                days: r.days,
                cascade: cleanCascade(r.cascade),
              }
            : null,
        releaseAllMax,
      );
      return results && results.length > 0 && v.op === "forget_all"
        ? { ok: true, op: "forget_all", results }
        : null;
    }
    case "restore_all": {
      const results = all(
        v.results,
        (r) => {
          if (!object(r)) return null;
          const held = cleanHold(r.held);
          return id(r.id) &&
            one(r.result, ["restored", "unknown", "forbidden", "conflict"]) &&
            (r.revision === null || count(r.revision, 1, 2_147_483_647)) &&
            held !== undefined
            ? {
                id: r.id,
                result: r.result,
                revision: r.revision,
                held,
                cascade: cleanCascade(r.cascade),
              }
            : null;
        },
        releaseAllMax,
      );
      return results && results.length > 0 && v.op === "restore_all"
        ? { ok: true, op: "restore_all", results }
        : null;
    }
    case "delete_all": {
      const results = all(
        v.results,
        (r) =>
          object(r) &&
          id(r.id) &&
          one(r.result, ["deleted", "unknown", "forbidden"])
            ? { id: r.id, result: r.result }
            : null,
        releaseAllMax,
      );
      return results && results.length > 0 && v.op === "delete_all"
        ? { ok: true, op: "delete_all", results }
        : null;
    }
    case "share_all": {
      const results = all(
        v.results,
        (r) =>
          object(r) &&
          id(r.id) &&
          one(r.result, [
            "moved",
            "unchanged",
            "unknown",
            "forbidden",
            "conflict",
            "held",
            "no_author",
            "scopes_off",
            "invalid",
          ]) &&
          (r.revision === null || count(r.revision, 1, 2_147_483_647)) &&
          typeof r.projectLeft === "boolean"
            ? {
                id: r.id,
                result: r.result,
                revision: r.revision,
                projectLeft: r.projectLeft,
              }
            : null,
        releaseAllMax,
      );
      return results && results.length > 0 && v.op === "share_all"
        ? { ok: true, op: "share_all", results }
        : null;
    }
    // A person's review queues (SB-P6-01) keep the engine's shape too.
    case "queues":
      return [
        "held",
        "duplicates",
        "contradictions",
        "stale",
        "candidates",
        "insights",
      ].every((q) => count(v[q], 0, 2_147_483_647))
        ? {
            ok: true,
            held: v.held,
            duplicates: v.duplicates,
            contradictions: v.contradictions,
            stale: v.stale,
            candidates: v.candidates,
            insights: v.insights,
          }
        : null;
    case "duplicates": {
      const duplicates = all(v.duplicates, cleanDuplicate, 50);
      return duplicates &&
        next(v.next) !== undefined &&
        count(v.count, 0, 2_147_483_647)
        ? { ok: true, duplicates, next: v.next, count: v.count }
        : null;
    }
    case "contradictions": {
      const contradictions = all(v.contradictions, cleanContradiction, 50);
      return contradictions &&
        next(v.next) !== undefined &&
        count(v.count, 0, 2_147_483_647)
        ? { ok: true, contradictions, next: v.next, count: v.count }
        : null;
    }
    case "insight_candidates": {
      const candidates = all(v.candidates, cleanPair, 50);
      return candidates &&
        next(v.next) !== undefined &&
        count(v.count, 0, 2_147_483_647)
        ? { ok: true, candidates, next: v.next, count: v.count }
        : null;
    }
    case "insight_stream": {
      const insights = all(v.insights, cleanStreamInsight, 50);
      const day = cleanDay(v.day);
      return insights &&
        day &&
        one(v.view, [...insightViews, "all"]) &&
        [
          "count",
          "proposed",
          "older",
          "confirmed",
          "snoozed",
          "closed",
          "candidates",
        ].every((n) => count(v[n], 0, 2_147_483_647)) &&
        next(v.next) !== undefined
        ? {
            ok: true,
            view: v.view,
            count: v.count,
            proposed: v.proposed,
            older: v.older,
            confirmed: v.confirmed,
            snoozed: v.snoozed,
            closed: v.closed,
            candidates: v.candidates,
            day,
            insights,
            next: v.next,
          }
        : null;
    }
    case "insight_history": {
      const events = all(v.events, cleanInsightEvent, 100);
      return events &&
        id(v.id) &&
        one(v.review, insightReviews) &&
        count(v.count, 0, 2_147_483_647)
        ? { ok: true, id: v.id, review: v.review, count: v.count, events }
        : null;
    }
    case "review_insight":
      return v.op === "review_insight" &&
        id(v.id) &&
        one(v.review, insightReviews) &&
        one(v.view, insightViews) &&
        maybe(v.reviewedAt) &&
        typeof v.unchanged === "boolean"
        ? {
            ok: true,
            op: "review_insight",
            id: v.id,
            review: v.review,
            view: v.view,
            reviewedAt: v.reviewedAt,
            unchanged: v.unchanged,
          }
        : null;
    case "resolve": {
      const resolved = cleanResolved(v);
      return resolved ? { ok: true, ...resolved } : null;
    }
    // The memory UI's last pieces (run 22) keep the engine's shape too. A page never holds more than its count says.
    case "standing": {
      const standing = all(v.standing, cleanStanding, 50);
      return standing &&
        next(v.next) !== undefined &&
        count(v.count, standing.length, 2_147_483_647) &&
        count(v.firing, 0, v.count as number) &&
        count(v.held, 0, v.count as number) &&
        count(v.overLimit, 0, v.count as number) &&
        count(v.maxInScope, 1, 2_147_483_647) &&
        count(v.maxFires, 1, 2_147_483_647) &&
        typeof v.served === "boolean"
        ? {
            ok: true,
            standing,
            next: v.next,
            count: v.count,
            firing: v.firing,
            held: v.held,
            overLimit: v.overLimit,
            maxInScope: v.maxInScope,
            maxFires: v.maxFires,
            served: v.served,
          }
        : null;
    }
    case "resolve_all": {
      const results = all(
        v.results,
        (r) =>
          object(r) &&
          id(r.id) &&
          one(r.result, [
            "resolved",
            "unchanged",
            "unknown",
            "forbidden",
            "conflict",
            "held",
            "retracted",
            "invalid",
          ]) &&
          (r.revision === null || count(r.revision, 1, 2_147_483_647)) &&
          (r.review === null || one(r.review, insightReviews)) &&
          (r.view === null || one(r.view, insightViews)) &&
          (r.candidate === null || one(r.candidate, ["used", "rejected"]))
            ? {
                id: r.id,
                result: r.result,
                revision: r.revision,
                review: r.review,
                view: r.view,
                candidate: r.candidate,
              }
            : null,
        releaseAllMax,
      );
      return results &&
        results.length > 0 &&
        v.op === "resolve_all" &&
        one(v.action, bulkResolveActions)
        ? { ok: true, op: "resolve_all", action: v.action, results }
        : null;
    }
    case "project_create":
    case "project_update": {
      const project = cleanProject(v.project);
      return project &&
        v.op === op &&
        one(v.scopes, ["off", "on"]) &&
        words(v.changed, 8) &&
        typeof v.unchanged === "boolean"
        ? {
            ok: true,
            op,
            project,
            scopes: v.scopes,
            changed: [...v.changed],
            unchanged: v.unchanged,
          }
        : null;
    }
    case "project_delete":
      return v.op === "project_delete" &&
        id(v.id) &&
        slug(v.slug) &&
        owner(v.owner) &&
        count(v.memories, 0, 2_147_483_647) &&
        one(v.scopes, ["off", "on"])
        ? {
            ok: true,
            op: "project_delete",
            id: v.id,
            slug: v.slug,
            owner: v.owner,
            memories: v.memories,
            scopes: v.scopes,
          }
        : null;
    // The review queue and a release of several keep the engine's shape, so passing one on and checking it again in the
    // browser (cleanPassedAnswer) reads the same.
    case "review": {
      const review = all(v.review, cleanReview, 50);
      return review &&
        next(v.next) !== undefined &&
        count(v.count, 0, 2_147_483_647)
        ? { ok: true, review, next: v.next, count: v.count }
        : null;
    }
    case "release_all": {
      const results = all(v.results, cleanReleased, releaseAllMax);
      return results && results.length > 0 && v.op === "release_all"
        ? { ok: true, op: "release_all", results }
        : null;
    }
    // The map's kept dismissals keep the engine's shape too (SB-P4-05).
    case "dismissals": {
      const dismissals = all(v.dismissals, cleanDismissal, 50);
      return dismissals && typeof v.truncated === "boolean"
        ? { ok: true, dismissals, truncated: v.truncated }
        : null;
    }
    case "dismiss":
      return v.op === "dismiss" &&
        typeof v.link === "string" &&
        mapLinkId.test(v.link) &&
        (v.dismissal === null ||
          (typeof v.dismissal === "string" && dismissalId.test(v.dismissal))) &&
        typeof v.removed === "boolean" &&
        typeof v.unchanged === "boolean"
        ? {
            ok: true,
            op: "dismiss",
            link: v.link,
            dismissal: v.dismissal,
            removed: v.removed,
            unchanged: v.unchanged,
          }
        : null;
    case "undismiss":
      return v.op === "undismiss" &&
        typeof v.dismissal === "string" &&
        dismissalId.test(v.dismissal) &&
        typeof v.unchanged === "boolean" &&
        typeof v.back === "boolean"
        ? {
            ok: true,
            op: "undismiss",
            dismissal: v.dismissal,
            unchanged: v.unchanged,
            back: v.back,
          }
        : null;
    // A person's own link steps (run 17) keep the engine's shape too.
    case "connections": {
      const connections = all(v.connections, cleanConnection, 50);
      return connections && id(v.id) && typeof v.truncated === "boolean"
        ? { ok: true, id: v.id, connections, truncated: v.truncated }
        : null;
    }
    case "brief": {
      const brief = cleanBrief(v);
      return brief ? { ok: true, ...brief } : null;
    }
    case "stale": {
      const stale = all(v.stale, cleanStale, 50);
      return stale &&
        next(v.next) !== undefined &&
        count(v.count, 0, 2_147_483_647)
        ? { ok: true, stale, next: v.next, count: v.count }
        : null;
    }
    case "link":
      return v.op === "link" &&
        id(v.link) &&
        id(v.id) &&
        id(v.target) &&
        typeof v.type === "string" &&
        linkTypeWord.test(v.type) &&
        typeof v.directed === "boolean" &&
        one(v.verification, ["verified", "inferred", "suggested"]) &&
        typeof v.created === "boolean" &&
        typeof v.unchanged === "boolean"
        ? {
            ok: true,
            op: "link",
            link: v.link,
            id: v.id,
            target: v.target,
            type: v.type,
            directed: v.directed,
            verification: v.verification,
            created: v.created,
            unchanged: v.unchanged,
          }
        : null;
    case "unlink":
      return v.op === "unlink" &&
        id(v.id) &&
        id(v.target) &&
        linkRefs(v.removed) &&
        linkRefs(v.kept) &&
        Array.isArray(v.dismissed) &&
        v.dismissed.length <= 20 &&
        v.dismissed.every(
          (t) => typeof t === "string" && linkTypeWord.test(t),
        ) &&
        count(v.already, 0, 1000) &&
        typeof v.unchanged === "boolean"
        ? {
            ok: true,
            op: "unlink",
            id: v.id,
            target: v.target,
            removed: v.removed.map((r) => ({ id: r.id, type: r.type })),
            kept: v.kept.map((r) => ({ id: r.id, type: r.type })),
            dismissed: [...(v.dismissed as string[])],
            already: v.already,
            unchanged: v.unchanged,
          }
        : null;
    case "resurface": {
      if (typeof v.day !== "string" || !/^\d{4}-\d\d-\d\d$/.test(v.day))
        return null;
      const pick = v.pick === null ? null : cleanResurfaced(v.pick);
      return pick === null && v.pick !== null
        ? null
        : { ok: true, day: v.day, pick };
    }
    case "resurface_dismiss":
      return v.op === "resurface_dismiss" &&
        id(v.id) &&
        typeof v.unchanged === "boolean"
        ? {
            ok: true,
            op: "resurface_dismiss",
            id: v.id,
            unchanged: v.unchanged,
          }
        : null;
    case "still_true":
      return v.op === "still_true" &&
        id(v.id) &&
        count(v.revision, 1, 2_147_483_647) &&
        typeof v.unchanged === "boolean" &&
        instant(v.checkedAt) &&
        (v.was === null || one(v.was, ["not_confirmed", "retracted_source"])) &&
        Array.isArray(v.retracted) &&
        v.retracted.length <= 100 &&
        v.retracted.every(id)
        ? {
            ok: true,
            op: "still_true",
            id: v.id,
            revision: v.revision,
            unchanged: v.unchanged,
            checkedAt: v.checkedAt,
            was: v.was,
            retracted: [...(v.retracted as string[])],
          }
        : null;
    default: {
      const written = cleanWritten(v, op);
      return written ? { ok: true, written } : null;
    }
  }
}

// The facade's answer, which cleanMemoryAnswer rebuilt ({memory} or {version} for get, {history}, {written} for a
// write), put back in the engine's shape and checked again, so the browser trusts nothing the facade did not check and
// gets what the view reads. A refusal, a list, the trash and the projects keep the engine's shape and are checked as
// they are.
export function cleanPassedAnswer(
  op: string,
  v: unknown,
): Record<string, unknown> | null {
  if (!object(v) || v.ok !== true) return cleanMemoryAnswer(op, v);
  switch (op) {
    case "list":
    case "trash":
    case "projects":
    case "review":
    case "release_all":
    case "dismissals":
    case "dismiss":
    case "undismiss":
    case "connections":
    case "stale":
    case "brief":
    case "resurface":
    case "resurface_dismiss":
    case "link":
    case "unlink":
    case "still_true":
    case "tags":
    case "changes":
    case "capsule_status":
    case "settings":
    case "undo_group":
    case "forget_all":
    case "restore_all":
    case "delete_all":
    case "share_all":
    case "project_create":
    case "project_update":
    case "project_delete":
    case "queues":
    case "duplicates":
    case "contradictions":
    case "insight_candidates":
    case "insight_stream":
    case "insight_history":
    case "review_insight":
    case "resolve":
    case "loops":
    case "due":
    case "decisions":
    case "calibration":
    case "standing":
    case "resolve_all":
      return cleanMemoryAnswer(op, v);
    case "get":
      return cleanMemoryAnswer(
        op,
        v.version === null || v.version === undefined
          ? { ok: true, memory: v.memory }
          : { ok: true, version: v.version },
      );
    case "history":
      return object(v.history)
        ? cleanMemoryAnswer(op, { ...v.history, ok: true })
        : null;
    default:
      return object(v.written)
        ? cleanMemoryAnswer(op, { ...v.written, ok: true })
        : null;
  }
}

// What a request's text takes as UTF-8, which the limits count (a memory's text at most maxMemoryBytes).
export const utf8Bytes = (text: string) =>
  new TextEncoder().encode(text).length;

// A list serves a held memory without its words, tags, project or capsule slot, whatever came.
const listed = (m: Memory): Memory =>
  m.held
    ? {
        ...m,
        content: null,
        tags: [],
        project: null,
        truncated: false,
        capsule: null,
      }
    : m;

// ---------------------------------------------------------------------------------------------------------------------
// For the view: what a person reads.

export const statusNames: Record<MemoryStatus, string> = {
  canonical: "Confirmed",
  draft: "Draft",
  deprecated: "Marked wrong",
};

// What a memory is now (SB-P4-11), in a person's words where it is not simply current; a retracted one's status already
// reads "Marked wrong".
export const stateNames: Record<MemoryState, string | null> = {
  current: null,
  replaced: "Replaced",
  ended: "Ended",
  retracted: null,
};

// What a change's retraction cascade did, in a person's words; "" when it did nothing.
export function cascadeWords(c: MemoryCascade | null): string {
  if (!c) return "";
  const said = (n: number, one: string, many: string) =>
    n === 0 ? "" : n === 1 ? `1 ${one}` : `${n} ${many}`;
  return [
    said(
      c.reopened.length,
      "memory it had replaced is current again.",
      "memories it had replaced are current again.",
    ),
    said(
      c.reclosed.length,
      "memory it had replaced is replaced by it again.",
      "memories it had replaced are replaced by it again.",
    ),
    said(
      c.flagged.length,
      "memory built on it is flagged for a check.",
      "memories built on it are flagged for a check.",
    ),
    said(
      c.unflagged.length,
      "memory built on it is no longer flagged.",
      "memories built on it are no longer flagged.",
    ),
  ]
    .filter(Boolean)
    .join(" ");
}

// A project's slug as the engine derives it from a name when none is given (MemoryProjectInput.Slug), for the view's preview;
// "" when nothing valid survives, so a person gives one.
export function projectSlug(name: string): string {
  const slugged = name
    .trim()
    .toLowerCase()
    .replace(/_/g, "-")
    .replace(/\s+/g, "-")
    .replace(/[^a-z0-9-]/g, "")
    .replace(/-{2,}/g, "-")
    .replace(/^-+/, "")
    .slice(0, 64)
    .replace(/-+$/, "");
  return slug(slugged) ? slugged : "";
}

// Why a memory is held, in a person's words.
export const holdWhy: Record<HoldReason, string> = {
  instruction: "it reads like instructions to an AI assistant",
  hidden:
    "it holds hidden characters, which can carry instructions no person sees",
  too_long: "it is longer than 32 KiB, more than can be checked whole",
  burst:
    "it came in a burst of writes from one AI client, faster than a person works",
  held_project:
    "it is in a project that a memory held for review made, which no person has let in yet",
};

// A capsule slot in a person's words.
export const slotNames: Record<string, string> = {
  identity: "Identity",
  preferences: "Preferences",
  constraints: "Constraints",
  principles: "Principles",
  "current-state": "Current state",
  decisions: "Decisions",
  "open-questions": "Open questions",
};

// The characters a person can't see, shown as a code each (U+200B), so reviewing a held memory shows what it holds: tag
// characters, zero-width characters, direction controls and the other invisible format characters.
export function visibleText(text: string): string {
  return text.replace(
    /[\u00ad\u061c\u180e\u200b-\u200f\u202a-\u202e\u2060-\u2064\u2066-\u206f\ufeff\ufff9-\ufffb]|\udb40[\udc00-\udc7f]/g,
    (c) =>
      `[U+${(c.codePointAt(0) ?? 0).toString(16).toUpperCase().padStart(4, "0")}]`,
  );
}

// A new idempotency key for one capture or change, kept until it is answered, so sending again after a lost answer
// stores once.
export const newKey = () =>
  `app:${typeof crypto !== "undefined" && "randomUUID" in crypto ? crypto.randomUUID() : `${Date.now()}-${Math.random().toString(36).slice(2, 12)}`}`;

// The browser's side, given to the explorer by its session (liveGraph.ts): whether the engine serves the Memories, and
// one call.
export interface MemoryClient {
  available: (signal?: AbortSignal) => Promise<boolean>;
  // Whether the engine takes a person's share too (memory.share on, and memory scopes on): false, or absent, otherwise.
  sharing?: (signal?: AbortSignal) => Promise<boolean>;
  call: <T = Record<string, unknown>>(
    request: Record<string, unknown>,
    signal?: AbortSignal,
  ) => Promise<MemoryReply<T>>;
}
