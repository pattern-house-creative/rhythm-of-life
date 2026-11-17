import { useEffect, useRef, useState } from "react";
import type { MutableRefObject } from "react";
import { ArrowLeft, History, Link2, RotateCcw, Trash2, X } from "lucide-react";
import { personName } from "./instance";
import {
  capsuleSlots,
  cascadeWords,
  holdWhy,
  maxMemoryBytes,
  newKey,
  releaseAllMax,
  slotNames,
  statusNames,
  utf8Bytes,
  visibleText,
  workWords,
} from "./memories";
import type {
  Memory,
  MemoryCapsule,
  MemoryClient,
  MemoryConfirmed,
  MemoryConnection,
  MemoryDeletedAll,
  MemoryLinked,
  MemoryProject,
  MemoryRestoredAll,
  MemorySettingsRead,
  MemoryUnlinked,
  StaleMemory,
  MemoryHistory,
  MemoryProjectRef,
  MemoryQueues,
  MemoryReads,
  MemoryReleasedAll,
  MemoryResolvedAll,
  MemoryStatus,
  MemoryVersion,
  MemoryWritten,
  ReviewItem,
  TrashedMemory,
} from "./memories";
import type { GraphNode, MemoryKind } from "./knowledge";
import { linkKindsAllow } from "./knowledge";
import { linkName } from "./graphView";
import PageBookmark from "./PageBookmark";
import { BulkBar, several, severalWords } from "./MemoryBulk";
import { MemoryChanges } from "./MemoryChanges";
import { MemoryDecisions, MemoryDueList, MemoryTasks } from "./MemoryPlans";
import type { DecisionFilter, DueFilter } from "./MemoryPlans";
import { filterArgs, MemoryList } from "./MemoryList";
import type { MemoryFilters } from "./MemoryList";
import { MemoryInsightStream, MemoryPairs } from "./MemoryInsightReview";
import { MemoryProjects } from "./MemoryProjects";
import { MemoryConflicts, MemoryCopies } from "./MemoryQueues";
import { MemorySettingsView } from "./MemorySettingsView";
import { MemoryStandingList } from "./MemoryStanding";
import { MemoryTimeline } from "./MemoryTimeline";
import { leaveStrip, useMemoryFocus, walkStrip } from "./memoryFocus";
import type { Place } from "./memoryFocus";
import {
  cascades,
  counted,
  kindNames,
  kindWords,
  rowGroup,
  scopeName,
  volatilityNames,
  volatilityWords,
  when,
  whose,
  workWho,
} from "./memoryScreen";
import type {
  BriefTarget,
  MemoryAnswer,
  MemoryTools,
  Notice,
} from "./memoryScreen";
import "./MemoryView.css";

// The Memories view (SB-P3-13): native memories as a person keeps them, beside the explorer's panel where the reader
// opens (on a phone it covers the sky, as the reader does). Quick capture in the document-upload pattern: one box, one
// Save, and a status line that says how it went. The recent list and the trash; a memory opened whole, with edit, an
// added update, its status, its history (read a revision, put it back) and undo; forget into the trash, restore and
// delete for good behind a confirmation. A memory held for review shows why, with its words and any hidden characters
// made visible, and only here does a person release it (D-039). A person's steps beyond one memory at a time
// (SB-P3-22): the review queue, where each held memory is read before it can be chosen and several are released at
// the revisions read; and a memory's capsule slot, which every AI client of its scope reads first. With the engine's
// memory.share on (and memory scopes on), a person shares a memory into Family or takes it back (run 14). A person's own
// link steps (run 17): a memory's links and the map's suggestions between it and another memory, a person's link to
// another memory of its scope (confirmed, an AI client's confirmed by making it again), unlinking one or dismissing a
// suggestion; the memories that may be out of date, each said still true without changing it; and a flag for a check
// taken off once its person has checked it. The full memory UI (SB-P6-01): the list's filters and several memories at
// once (MemoryList.tsx, MemoryBulk.tsx), projects and a capsule's status (MemoryProjects.tsx, MemoryCapsuleCard.tsx), AI
// changes and a group's undo (MemoryChanges.tsx), a memory's history as a timeline (MemoryTimeline.tsx) and the memory settings
// to read (MemorySettingsView.tsx), a person's review queues (MemoryQueues.tsx, MemoryInsightReview.tsx: possible copies,
// possible contradictions, the insight stream and the pairs worth a look, with how many wait in each on the strip) and the brief's
// lists (MemoryPlans.tsx): tasks and commitments, what is due and decisions with their outcomes, each the list a count in the brief
// is the length of. Each section one entry of `sections` below and a component of its own, given the same tools (memoryScreen.ts). Everything
// goes through the engine as the app (app_memories, a person's channel); nothing is kept in the browser beyond what is on screen. Since
// run 22: the list's words and kind (MemoryList.tsx), a memory's kind, importance and how long it holds changed from its reader, the
// standing instructions (MemoryStanding.tsx), and several in a review queue at once (here for what may be out of date,
// MemoryInsightReview.tsx for insights and pairs, each through op resolve_all).

// The memory a native memory's star stands for (provider and account memory, its id the memory's; MemoryStars.Key in
// the engine), or null for any other star.
export const nativeMemoryId = (n?: GraphNode | null) =>
  n?.resource.identity.provider === "memory" &&
  n.resource.identity.account === "memory" &&
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(
    n.resource.identity.externalId,
  )
    ? n.resource.identity.externalId
    : null;

// The project a native project's star stands for (MemoryStars.ProjectKey: account project), or null for any other star.
export const nativeProjectId = (n?: GraphNode | null) =>
  n?.resource.identity.provider === "memory" &&
  n.resource.identity.account === "project" &&
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(
    n.resource.identity.externalId,
  )
    ? n.resource.identity.externalId
    : null;

// Whether the engine serves the Memories view now: asked once per session, false meanwhile and on any failure.
export function useMemoriesServed(client?: MemoryClient) {
  const [served, setServed] = useState(false);
  useEffect(() => {
    if (!client) return;
    const controller = new AbortController();
    client.available(controller.signal).then(
      (on) => !controller.signal.aborted && setServed(on),
      () => {},
    );
    return () => controller.abort();
  }, [client]);
  return Boolean(client) && served;
}

type Step =
  | "edit"
  | "append"
  | "forget"
  | "release"
  | "share"
  | "organise"
  | "classify"
  | null;
// The view's sections, in the order they are offered: its label on the strip and its title once chosen. A later section is one
// more entry here, one more id and a component of its own file below.
const sections = [
  { id: "recent", label: "Recent", title: "Memories" },
  { id: "plans", label: "Tasks", title: "Tasks and commitments" },
  { id: "due", label: "Due", title: "Due" },
  { id: "decisions", label: "Decisions", title: "Decisions" },
  { id: "standing", label: "Standing", title: "Standing instructions" },
  { id: "projects", label: "Projects", title: "Projects" },
  { id: "changes", label: "AI changes", title: "AI changes" },
  { id: "review", label: "Review", title: "Held for review" },
  { id: "copies", label: "Copies", title: "Possible copies" },
  { id: "conflicts", label: "Conflicts", title: "Possible contradictions" },
  { id: "stale", label: "To check", title: "May be out of date" },
  { id: "insights", label: "Insights", title: "Insights to review" },
  { id: "pairs", label: "Pairs", title: "Pairs worth a look" },
  { id: "trash", label: "Trash", title: "Trash" },
  { id: "settings", label: "Settings", title: "Memory settings" },
] as const;
type Section = (typeof sections)[number]["id"];
// Which of a review queue's counts each section's button says.
const queueOf: Partial<Record<Section, keyof MemoryQueues>> = {
  review: "held",
  copies: "duplicates",
  conflicts: "contradictions",
  stale: "stale",
  insights: "insights",
  pairs: "candidates",
};
// The section each of the brief's lists is (memoryScreen.ts).
const briefSection: Record<BriefTarget, Section> = {
  due: "due",
  decisions: "decisions",
  youOwe: "plans",
  owedToYou: "plans",
  stale: "stale",
  held: "review",
  changes: "changes",
  contradictions: "conflicts",
  duplicates: "copies",
  insights: "insights",
};
// The link types a person makes between two memories (052: Second Brain's, but supersedes, which a replacement makes).
const linkable = [
  "relates_to",
  "caused_by",
  "decided",
  "about_person",
  "part_of_project",
  "follows",
  "drawn_from",
] as const;

const capsuleName = (m: Memory, c: MemoryCapsule) =>
  c.kind === "core"
    ? `${whose(m)} core capsule`
    : `the ${m.project?.slug ?? "project"} project's capsule`;
// How the write that held a memory came, in words. An AI client's name is the one it gave itself, so it is shown as
// an AI client's, never as a channel of its own (a client calling itself "the terminal" stays an AI client here).
const heldHow = (i: ReviewItem) =>
  i.channel === "mcp"
    ? i.client
      ? `an AI client (${i.client})`
      : "an AI client"
    : i.channel === "app"
      ? "the app"
      : i.channel === "cli"
        ? "the terminal"
        : i.channel === "capture"
          ? i.client
            ? `a capture connection (${i.client})`
            : "a capture connection"
          : "Rhythm";
// A memory's truth (SB-P4-11, the app half of D-192): no longer true because a newer memory replaced it (with the way
// to it) or because it ended, marked wrong, and flagged for a check because a memory it was built on was marked wrong
// since or moved to the trash (with the way to each; one in the trash or gone for good cannot be opened, which
// openSource says). Nothing for a current memory with no flags, so it reads as before.
function Truth({
  memory: m,
  open,
  openSource,
  checked,
}: {
  memory: Memory;
  open: (id: string) => void;
  openSource: (id: string) => void;
  // A person's flag clear (run 17): it was checked and still stands.
  checked?: () => void;
}) {
  const sources = m.retractedSources.slice(0, 5);
  return (
    <>
      {m.state === "replaced" && (
        <div className="memory-truth" role="note">
          <p>
            <strong>No longer true:</strong> a newer memory replaced it
            {m.validUntil ? ` on ${when(m.validUntil)}` : ""}. Searches,
            capsules and AI clients get the newer one; the map shows this one
            faded. Its words are kept as they were.
          </p>
          {m.replacedBy && (
            <button onClick={() => open(m.replacedBy!)}>
              Open what replaced it
            </button>
          )}
        </div>
      )}
      {m.state === "ended" && (
        <div className="memory-truth" role="note">
          <p>
            <strong>No longer true:</strong> it ended
            {m.validUntil ? ` on ${when(m.validUntil)}` : ""}, and nothing
            replaced it. Searches, the map and capsules leave it out.
          </p>
        </div>
      )}
      {m.state === "retracted" && (
        <div className="memory-truth" role="note">
          <p>
            <strong>Marked wrong:</strong> searches, the map and capsules leave
            it out.
          </p>
        </div>
      )}
      {sources.length > 0 && (
        <div className="memory-truth memory-flagged" role="note">
          <p>
            <strong>Flagged for a check:</strong> it was built on{" "}
            {m.retractedSources.length === 1
              ? "a memory"
              : `${m.retractedSources.length} memories`}{" "}
            marked wrong or moved to the trash since. Check that it still holds;
            once you have, take the flag off here.
          </p>
          {sources.map((id, i) => (
            <button key={id} onClick={() => openSource(id)}>
              {sources.length === 1
                ? "Open the memory it was built on"
                : `Open the memory it was built on, ${i + 1}`}
            </button>
          ))}
          {checked && (
            <button onClick={checked}>It still holds: take the flag off</button>
          )}
        </div>
      )}
    </>
  );
}

export default function MemoryView({
  client,
  start = null,
  startProject = null,
  startBrief = null,
  onClose,
  onSaved,
  back,
}: {
  client: MemoryClient;
  // A memory to open straight away: a native memory's star the explorer had chosen.
  start?: string | null;
  // A project to open straight away: a native project's star the explorer had chosen.
  startProject?: string | null;
  // One of the brief's lists to open at: the list its count is the length of.
  startBrief?: BriefTarget | null;
  onClose: () => void;
  // A memory was saved: the map shows it after its next rebuild.
  onSaved?: () => void;
  // Escape steps back a layer here first: set to a function that answers whether it did.
  back?: MutableRefObject<(() => boolean) | null>;
}) {
  const [tab, setTab] = useState<Section>(
    startBrief
      ? briefSection[startBrief]
      : startProject
        ? "projects"
        : "recent",
  );
  // What each of the brief's lists filters by; kept here, so each survives opening a memory and coming back.
  const [owing, setOwing] = useState<"out" | "in">(
    startBrief === "owedToYou" ? "in" : "out",
  );
  const [dueFilter, setDueFilter] = useState<DueFilter>("now");
  const [decisionFilter, setDecisionFilter] =
    useState<DecisionFilter>("review");
  // What the list filters by, the projects and tags it offers, and the memories chosen in it; kept here, so each survives
  // opening a memory and coming back.
  const [filters, setFiltersNow] = useState<MemoryFilters>({});
  const [tags, setTags] = useState<MemoryReads["tags"] | null>(null);
  const [projects, setProjects] = useState<MemoryProject[] | null>(null);
  const [chosenRows, setChosenRows] = useState<Memory[]>([]);
  const [chosenTrash, setChosenTrash] = useState<TrashedMemory[]>([]);
  const [projectOpen, setProjectOpen] = useState<string | null>(startProject);
  // The memory settings, read once, quietly; null until then, and from an engine that doesn't say.
  const [settings, setSettings] = useState<MemorySettingsRead | null>(null);
  const [settingsRead, setSettingsRead] = useState(false);
  // A memory's project and tags being changed (the reader's Organise).
  const [organising, setOrganising] = useState<{
    project: string;
    tags: string;
  } | null>(null);
  // Its kind, importance and how long it holds being changed (run 22), each "" for not said.
  const [classifying, setClassifying] = useState<{
    kind: string;
    importance: string;
    volatility: string;
  } | null>(null);
  // What may be out of date, chosen to say still true of several at once (run 22), each at the revision shown.
  const [chosenStale, setChosenStale] = useState<StaleMemory[]>([]);
  const [review, setReview] = useState<{
    items: ReviewItem[];
    next: string | null;
    count: number;
  } | null>(null);
  // The held memories read here, each at the revision whose words were shown: only those may be chosen to release.
  // Its tags and project too, which a release lets into every list with its words (memory.md 7.1).
  const [reviewed, setReviewed] = useState<
    Record<
      string,
      {
        revision: number;
        content: string;
        tags: string[];
        project: MemoryProjectRef | null;
      }
    >
  >({});
  const [chosen, setChosen] = useState<string[]>([]);
  const [releasing, setReleasing] = useState(false);
  const [list, setList] = useState<{
    items: Memory[];
    next: string | null;
  } | null>(null);
  const [trash, setTrash] = useState<{
    items: TrashedMemory[];
    next: string | null;
  } | null>(null);
  // The memories that may be out of date (run 17): the stale queue this person may act on, and its length.
  const [stale, setStale] = useState<{
    items: StaleMemory[];
    next: string | null;
    count: number;
  } | null>(null);
  // The open memory's links and the map's suggestions (run 17), a link being taken away (its key), and a new link being
  // chosen: the other memories of its scope, the one chosen and the type.
  const [links, setLinks] = useState<MemoryReads["connections"] | null>(null);
  const [unlinking, setUnlinking] = useState<string | null>(null);
  const [linking, setLinking] = useState<{
    candidates: Memory[];
    target: string;
    type: string;
  } | null>(null);
  const [open, setOpen] = useState<string | null>(start);
  const [memory, setMemory] = useState<Memory | null>(null);
  const [step, setStep] = useState<Step>(null);
  const [text, setText] = useState("");
  const [history, setHistory] = useState<MemoryHistory | null>(null);
  const [version, setVersion] = useState<MemoryVersion | null>(null);
  const [deleting, setDeleting] = useState<string | null>(null);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [draft, setDraft] = useState("");
  const [key, setKey] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [off, setOff] = useState(false);
  // Whether the engine takes a person's share (memory.share on, and memory scopes on): asked once, false meanwhile.
  const [sharing, setSharing] = useState(false);
  // A flagged memory's source being opened from it (Truth), and the memory it was opened from.
  const source = useRef<{ id: string; from: string } | null>(null);
  const heading = useRef<HTMLHeadingElement>(null);
  const viewRoot = useRef<HTMLElement>(null);
  const strip = useRef<HTMLDivElement>(null);
  const captureBox = useRef<HTMLTextAreaElement>(null);
  // Save pressed from the keyboard: it is disabled once the text has gone, so the box takes focus back for the next one.
  const keyedSave = useRef(false);
  const focus = useMemoryFocus(viewRoot, heading);
  // The memory open now as the effect below last saw it, and where in the list it was opened from.
  const shownOpen = useRef(start);
  const openedFrom = useRef<Place | null>(null);
  const stamp = useRef(0);
  const tagStamp = useRef(0);
  // A section's own layer, which Escape takes back first (memoryScreen.ts).
  const layer = useRef<(() => boolean) | null>(null);
  // How many wait in each review queue, read quietly; null until then, and from an engine that doesn't say.
  const [queues, setQueues] = useState<MemoryQueues | null>(null);
  const queueStamp = useRef(0);

  // One op as the app; a refusal shows its reason, an install where the view went away says so. Text longer than a
  // memory holds (counted as UTF-8, as the engine counts it) is not sent at all.
  async function call<T>(
    request: Record<string, unknown>,
  ): Promise<MemoryAnswer<T> | null> {
    if (
      ["content", "text"].some(
        (k) =>
          typeof request[k] === "string" &&
          utf8Bytes(request[k] as string) > maxMemoryBytes,
      )
    ) {
      setNotice({
        text: "This is too long to save: a memory holds at most 128 KiB of text. Nothing was sent.",
        failed: true,
      });
      return null;
    }
    const reply = await client.call<T>(request);
    if (!reply.available) {
      setOff(true);
      return null;
    }
    if (!reply.ok) {
      setNotice({ text: reply.reason, failed: true });
      if (reply.code === "conflict" && open)
        void read(
          open,
          "This memory changed since you opened it; here it is as it is now.",
        );
      return null;
    }
    return reply as MemoryAnswer<T>;
  }

  // A read a section can go on without: if it doesn't come, or is an op an older engine doesn't take, nothing shows.
  async function quiet<T>(
    request: Record<string, unknown>,
  ): Promise<MemoryAnswer<T> | null> {
    try {
      const reply = await client.call<T>(request);
      return reply.available && reply.ok ? (reply as MemoryAnswer<T>) : null;
    } catch {
      return null;
    }
  }

  // How many wait in each queue: each the length of its list, read again whenever something may have left one.
  async function readQueues() {
    const asked = ++queueStamp.current;
    const reply = await quiet<MemoryQueues>({ op: "queues" });
    if (asked === queueStamp.current) setQueues(reply);
  }

  const setFilters = (next: MemoryFilters) => {
    // The choice was of the rows shown, so it goes with them.
    setChosenRows([]);
    setFiltersNow(next);
  };

  const tools: MemoryTools = {
    call,
    quiet,
    say: setNotice,
    open: setOpen,
    saved: () => onSaved?.(),
    waiting: () => void readQueues(),
    sharing,
    settings,
    layer,
  };

  // The tags and the count beside the list, for what it filters by now.
  async function readTags() {
    const asked = ++tagStamp.current;
    const reply = await quiet<MemoryReads["tags"]>({
      op: "tags",
      ...filterArgs(filters),
    });
    if (asked === tagStamp.current)
      setTags(
        reply
          ? { tags: reply.tags, truncated: reply.truncated, count: reply.count }
          : null,
      );
  }

  async function load(more = false) {
    const asked = ++stamp.current;
    if (tab === "recent") {
      const reply = await call<MemoryReads["list"]>({
        op: "list",
        limit: 20,
        ...filterArgs(filters),
        ...(more && list?.next ? { cursor: list.next } : {}),
      });
      if (reply && asked === stamp.current) {
        setList({
          items:
            more && list ? [...list.items, ...reply.memories] : reply.memories,
          next: reply.next,
        });
        // What is chosen stays at the revision now shown; one no longer shown leaves the choice.
        setChosenRows((c) =>
          c.flatMap((m) => {
            const now = reply.memories.find((x) => x.id === m.id);
            return now ? [now] : more ? [m] : [];
          }),
        );
      }
      if (!more) void readTags();
    } else if (tab === "review") {
      const reply = await call<MemoryReads["review"]>({
        op: "review",
        limit: 20,
        ...(more && review?.next ? { cursor: review.next } : {}),
      });
      if (reply && asked === stamp.current) {
        setReview({
          items:
            more && review ? [...review.items, ...reply.review] : reply.review,
          next: reply.next,
          count: reply.count,
        });
        // What is still held stays chosen; the rest leaves the choice with the queue.
        if (!more) {
          setChosen((c) =>
            c.filter((id) => reply.review.some((i) => i.id === id)),
          );
          setReleasing(false);
        }
      }
    } else if (tab === "stale") {
      const reply = await call<MemoryReads["stale"]>({
        op: "stale",
        limit: 20,
        ...(more && stale?.next ? { cursor: stale.next } : {}),
      });
      if (reply && asked === stamp.current) {
        setStale({
          items: more && stale ? [...stale.items, ...reply.stale] : reply.stale,
          next: reply.next,
          count: reply.count,
        });
        // What is chosen stays at the revision now shown; one no longer listed leaves the choice.
        setChosenStale((c) =>
          c.flatMap((s) => {
            const now = reply.stale.find((x) => x.id === s.id);
            return now ? [now] : more ? [s] : [];
          }),
        );
      }
    } else if (tab === "trash") {
      const reply = await call<MemoryReads["trash"]>({
        op: "trash",
        limit: 20,
        ...(more && trash?.next ? { cursor: trash.next } : {}),
      });
      if (reply && asked === stamp.current) {
        setTrash({
          items: more && trash ? [...trash.items, ...reply.trash] : reply.trash,
          next: reply.next,
        });
        setChosenTrash((c) =>
          c.flatMap((t) => {
            const now = reply.trash.find((x) => x.id === t.id);
            return now ? [now] : more ? [t] : [];
          }),
        );
      }
    }
  }

  // The open memory, read again. Refused (forgotten or deleted elsewhere since the list was read, say), the reader
  // closes back to the list, the refusal's notice staying.
  // A flagged memory's source that answers so (in the trash, or deleted for good, since it flagged it) is said to be
  // gone, and the flagged memory opens again.
  async function read(id: string, said?: string) {
    const reply = await call<MemoryReads["get"]>({ op: "get", id });
    const from = source.current?.id === id ? source.current.from : null;
    source.current = null;
    if (!reply?.memory) {
      setMemory(null);
      if (from) {
        setNotice({
          text: "That memory is in the trash or deleted for good, so it cannot be opened here. The flag stays until this memory is checked.",
          failed: true,
        });
        setOpen((o) => (o === id ? from : o));
        return;
      }
      setOpen((o) => (o === id ? null : o));
      return;
    }
    setMemory(reply.memory);
    if (said) setNotice({ text: said });
    if (history) await readHistory(id);
  }

  async function readHistory(id: string) {
    const reply = await call<MemoryReads["history"]>({
      op: "history",
      id,
      limit: 50,
    });
    if (reply) setHistory(reply.history);
  }

  // The next, older page of the timeline, under what is shown.
  async function readEarlier(id: string) {
    if (history?.next === null || history?.next === undefined) return;
    const reply = await call<MemoryReads["history"]>({
      op: "history",
      id,
      before: history.next,
      limit: 50,
    });
    if (reply)
      setHistory({
        ...reply.history,
        changes: [...history.changes, ...reply.history.changes],
      });
  }

  useEffect(() => {
    if (!client.sharing) return;
    const controller = new AbortController();
    client.sharing(controller.signal).then(
      (on) => !controller.signal.aborted && setSharing(on),
      () => {},
    );
    return () => controller.abort();
  }, [client]);
  useEffect(() => {
    let live = true;
    void quiet<MemorySettingsRead>({ op: "settings" }).then((reply) => {
      if (!live) return;
      if (reply) setSettings(reply);
      setSettingsRead(true);
    });
    return () => {
      live = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  useEffect(() => {
    if (open) return;
    // eslint-disable-next-line react-hooks/exhaustive-deps
    void load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tab, open, filters]);
  // The strip's counts, read with whichever list is opened, and again when a release or a still true may have left one shorter.
  useEffect(() => {
    if (open) return;
    void readQueues();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tab, open, review, stale]);
  // The projects the list offers to filter by.
  useEffect(() => {
    if (open || tab !== "recent") return;
    let live = true;
    void quiet<MemoryReads["projects"]>({
      op: "projects",
      archived: true,
    }).then((reply) => {
      if (live && reply) setProjects(reply.projects);
    });
    return () => {
      live = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tab, open]);
  useEffect(() => {
    setMemory(null);
    setHistory(null);
    setVersion(null);
    setStep(null);
    setLinks(null);
    setUnlinking(null);
    setLinking(null);
    setOrganising(null);
    setClassifying(null);
    if (open) void read(open);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);
  // Focus goes to the heading of what is now shown; back from a memory, on to the row it was opened from, once that is there.
  useEffect(() => {
    const was = shownOpen.current;
    shownOpen.current = open;
    if (!was && open) openedFrom.current = focus.place();
    heading.current?.focus({ preventScroll: true });
    if (was && !open && openedFrom.current) focus.returnTo(openedFrom.current);
    if (!open) openedFrom.current = null;
    // On a phone the strip scrolls across: the section shown is kept in sight, also when a count on Home opened one far along it.
    strip.current
      ?.querySelector('[aria-pressed="true"]')
      ?.scrollIntoView?.({ block: "nearest", inline: "nearest" });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, tab]);
  useEffect(() => {
    if (!notice || notice.busy || notice.failed || notice.action) return;
    const timer = setTimeout(() => setNotice(null), 12_000);
    return () => clearTimeout(timer);
  }, [notice]);
  // Escape: a confirmation or an open editor first, then the open memory, then the view itself (the explorer's).
  useEffect(() => {
    if (!back) return;
    back.current = () => {
      if (layer.current?.()) return true;
      if (step || deleting || version || releasing || unlinking || linking) {
        setStep(null);
        setDeleting(null);
        setVersion(null);
        setReleasing(false);
        setUnlinking(null);
        setLinking(null);
        return true;
      }
      if (open) {
        setOpen(null);
        return true;
      }
      return false;
    };
    return () => {
      back.current = null;
    };
  }, [back, step, deleting, version, open, releasing, unlinking, linking]);

  // A write done: what it did, in words, and the memory read again.
  async function wrote(reply: { written: MemoryWritten } | null, said: string) {
    if (!reply) return;
    const w = reply.written;
    setStep(null);
    setText("");
    setVersion(null);
    // What its retraction cascade did (SB-P4-11), when it did something: other memories current again or flagged.
    const cascade = cascadeWords(w.cascade);
    setNotice({
      text: [
        w.held
          ? `${said} It is held for review: ${holdWhy[w.held.reason]}.`
          : w.unchanged
            ? "Nothing changed: the memory already holds that."
            : said,
        w.droppedTags.length
          ? `Left off ${w.droppedTags.join(", ")}: Rhythm sets ${w.droppedTags.length === 1 ? "that" : "those"} itself.`
          : "",
        cascade,
      ]
        .filter(Boolean)
        .join(" "),
    });
    if (open) await read(open);
  }

  async function capture() {
    const content = draft.trim();
    if (!content || busy) return;
    const attempt = key ?? newKey();
    setKey(attempt);
    setBusy(true);
    setNotice({ text: "Saving…", busy: true });
    try {
      const reply = await call<{ written: MemoryWritten }>({
        op: "capture",
        content,
        scope: "shared",
        key: attempt,
      });
      if (!reply) return;
      const w = reply.written;
      setDraft("");
      setKey(null);
      if (keyedSave.current) captureBox.current?.focus({ preventScroll: true });
      const where =
        w.scopes === "off"
          ? "for the household"
          : w.scope === "shared"
            ? "in Family"
            : "in your own memories";
      setNotice({
        text: w.held
          ? `Saved ${where}, and held for review: ${holdWhy[w.held.reason]}. Open it to look and release it.`
          : `${w.replayed ? "Saved already" : "Saved"} as a draft ${where}.${w.project?.created ? ` Made the project ${w.project.slug}.` : ""}`,
        action: {
          label: "Open",
          run: () => {
            setNotice(null);
            setOpen(w.id);
          },
        },
      });
      onSaved?.();
      if (tab === "recent" && !open) await load();
    } finally {
      keyedSave.current = false;
      setBusy(false);
    }
  }

  async function forget(m: Memory) {
    const reply = await call<{ written: MemoryWritten }>({
      op: "forget",
      id: m.id,
      expected: m.revision,
    });
    if (!reply) return;
    const nonce = reply.written.trash?.nonce;
    setStep(null);
    setOpen(null);
    setNotice({
      text: [
        `Moved to the trash. It is deleted for good after ${reply.written.trash?.days ?? 14} days unless you bring it back.`,
        cascadeWords(reply.written.cascade),
      ]
        .filter(Boolean)
        .join(" "),
      action: nonce
        ? { label: "Undo", run: () => void restore(m.id, nonce) }
        : undefined,
    });
  }

  async function restore(id: string, nonce: string) {
    const reply = await call<{ written: MemoryWritten }>({
      op: "restore",
      id,
      nonce,
    });
    if (!reply) return;
    setNotice({
      text: [
        reply.written.held
          ? `Brought back, still held for review: ${holdWhy[reply.written.held.reason]}.`
          : "Brought back from the trash.",
        cascadeWords(reply.written.cascade),
      ]
        .filter(Boolean)
        .join(" "),
      action: {
        label: "Open",
        run: () => {
          setNotice(null);
          setTab("recent");
          setOpen(id);
        },
      },
    });
    await load();
  }

  async function deleteForever(t: TrashedMemory) {
    // The person confirmed: the trash row's nonce, and the memory's id as the confirmation (D-036).
    const reply = await call<{ written: MemoryWritten }>({
      op: "delete",
      id: t.id,
      nonce: t.nonce,
      confirm: t.id,
    });
    setDeleting(null);
    if (!reply) return;
    setNotice({ text: "Deleted for good." });
    await load();
  }

  // Several from the trash, each from the trash row shown: a memory trashed again since stays there.
  async function restoreChosen() {
    const { results, failed } = await several<MemoryRestoredAll["results"][0]>(
      tools,
      "restore_all",
      chosenTrash.map((t) => ({ id: t.id, nonce: t.nonce })),
    );
    if (!results.length) {
      await load();
      return;
    }
    setChosenTrash([]);
    const back = results.filter((r) => r.result === "restored");
    const held = back.filter((r) => r.held).length;
    setNotice({
      text: [
        `Brought back ${counted(back.length, "memory", "memories")}.`,
        held
          ? `${held === 1 ? "1 is" : `${held} are`} still held for review.`
          : "",
        severalWords(results, ["restored"], failed),
        cascadeWords(cascades(results.map((r) => r.cascade))),
      ]
        .filter(Boolean)
        .join(" "),
      failed: failed || back.length < results.length,
    });
    await load();
  }

  // Several for good, those this person may delete, each with its nonce and their confirmation, its own id (D-036).
  async function deleteChosen() {
    const { results, failed } = await several<MemoryDeletedAll["results"][0]>(
      tools,
      "delete_all",
      chosenTrash
        .filter((t) => t.mayDeleteForever)
        .map((t) => ({ id: t.id, nonce: t.nonce, confirm: t.id })),
    );
    if (!results.length) {
      await load();
      return;
    }
    setChosenTrash([]);
    const gone = results.filter((r) => r.result === "deleted").length;
    setNotice({
      text: [
        `Deleted ${counted(gone, "memory", "memories")} for good.`,
        severalWords(results, ["deleted"], failed),
      ]
        .filter(Boolean)
        .join(" "),
      failed: failed || gone < results.length,
    });
    await load();
  }

  // A held memory's words, read in the review queue: what the person sees is what they may then release, at that
  // revision. One no longer held (released elsewhere meanwhile) leaves the queue.
  async function readHeld(item: ReviewItem) {
    const reply = await call<MemoryReads["get"]>({ op: "get", id: item.id });
    const m = reply?.memory;
    if (!m) return;
    if (!m.held) {
      setNotice({ text: "It is no longer held for review." });
      await load();
      return;
    }
    setReviewed((r) => ({
      ...r,
      [m.id]: {
        revision: m.revision,
        content: m.content ?? "",
        tags: m.tags,
        project: m.project,
      },
    }));
  }

  // Several released at once, each at the revision its words were read; each is answered on its own, and one that
  // changed since stays held (D-039).
  async function releaseChosen() {
    const memories = chosen
      .filter((id) => reviewed[id])
      .slice(0, releaseAllMax)
      .map((id) => ({ id, expected: reviewed[id].revision }));
    setReleasing(false);
    if (!memories.length) return;
    const reply = await call<MemoryReleasedAll>({
      op: "release_all",
      memories,
    });
    if (!reply) return;
    const count = (r: string) =>
      reply.results.filter((x) => x.result === r).length;
    const released = count("released") + count("unchanged");
    const changed = count("conflict");
    const refused = reply.results.length - released - changed;
    setNotice({
      text: [
        released === 1
          ? "Released 1 memory."
          : `Released ${released} memories.`,
        changed
          ? `${changed === 1 ? "1 changed" : `${changed} changed`} since you read ${changed === 1 ? "it" : "them"} and ${changed === 1 ? "stays" : "stay"} held: read ${changed === 1 ? "it" : "them"} again.`
          : "",
        refused
          ? `${refused === 1 ? "1 was" : `${refused} were`} not yours to release, or no longer there.`
          : "",
      ]
        .filter(Boolean)
        .join(" "),
      failed: changed + refused > 0,
    });
    setReviewed((r) => {
      const left = { ...r };
      for (const x of reply.results) delete left[x.id];
      return left;
    });
    setChosen([]);
    await load();
  }

  // A capsule slot (SB-P3-22): core or the memory's project's, or none. The capsule every AI client of the scope reads
  // first serves it only while it is confirmed and not held, which the notice says.
  async function slot(m: Memory, value: string) {
    const [kind, name] = value.split(":");
    const reply = await call<{ written: MemoryWritten }>(
      kind === "none"
        ? { op: "capsule", id: m.id, capsule: "none", expected: m.revision }
        : {
            op: "capsule",
            id: m.id,
            capsule: kind,
            slot: name,
            expected: m.revision,
          },
    );
    if (!reply) return;
    const c = reply.written.capsule;
    const said = !c
      ? "Taken out of its capsule slot."
      : `In ${capsuleName(m, c)} as ${slotNames[c.slot].toLowerCase()}.${
          reply.written.held
            ? " It is held for review, so the capsule leaves it out until it is released."
            : reply.written.status === "draft"
              ? " It is a draft: the capsule serves it once you confirm it."
              : reply.written.status === "deprecated"
                ? " It is marked wrong, so the capsule leaves it out."
                : " Every AI client that reads this capsule reads it first."
        }`;
    setStep(null);
    setNotice({
      text: reply.written.unchanged
        ? "Nothing changed: it is in that slot already."
        : said,
    });
    if (open) await read(open);
  }

  // A person's share (run 14): into Family, or back to its author's own memories, at the revision read; the engine
  // decides who may (its owner shares; its author or a Family admin takes it back) and says why not. A move takes a
  // memory out of its capsule slot. After a take-back the reader may no longer read it (a Family admin's), and then
  // the reader closes, the notice staying.
  async function move(m: Memory, scope: "shared" | "private") {
    const reply = await call<{ written: MemoryWritten }>({
      op: "share",
      id: m.id,
      scope,
      expected: m.revision,
    });
    setStep(null);
    if (!reply) return;
    const w = reply.written;
    setNotice({
      text: w.unchanged
        ? scope === "shared"
          ? "Nothing changed: it is in Family already."
          : "Nothing changed: it is private already."
        : [
            scope === "shared"
              ? "Shared with Family: every member reads it now, with its author."
              : `Taken back to ${personName(w.owner)}'s own memories: Family's members no longer read it.`,
            w.projectLeft
              ? "Its project stayed behind, so it is in no project now."
              : "",
            m.capsule ? "It left its capsule slot." : "",
          ]
            .filter(Boolean)
            .join(" "),
    });
    if (!w.unchanged) onSaved?.();
    const again = await client.call<MemoryReads["get"]>({
      op: "get",
      id: m.id,
    });
    if (!again.available || !again.ok || !again.memory) {
      setMemory(null);
      setOpen(null);
      return;
    }
    setMemory(again.memory);
    if (history) await readHistory(m.id);
  }

  // Undo its newest change. A restore from the trash moves no revision and is not undone this way (forgetting it again
  // is), so right after one this says so rather than undoing the change before the trip.
  async function undoLast(m: Memory) {
    const newest = await call<MemoryReads["history"]>({
      op: "history",
      id: m.id,
      limit: 1,
    });
    const change = newest?.history.changes[0];
    if (!newest) return;
    if (change?.event === "restored" && change.revision === m.revision)
      return setNotice({
        text: "It was just brought back from the trash; to undo that, forget it again. History puts back an earlier revision.",
      });
    await wrote(
      await call({ op: "undo", id: m.id, expected: m.revision }),
      "Undone: it is back as it was before its last change.",
    );
  }

  // A person's own link steps (run 17). The open memory's links: those people and AI clients made, then the map's
  // suggestions between it and another memory, each with whether this person may take it away.
  async function readLinks(id: string) {
    const reply = await call<MemoryReads["connections"]>({
      op: "connections",
      id,
      limit: 50,
    });
    if (reply)
      setLinks({
        id: reply.id,
        connections: reply.connections,
        truncated: reply.truncated,
      });
  }

  // A new link: the other memories of its scope that a person can read whole (a held one waits for its release).
  async function startLink(m: Memory) {
    const reply = await call<MemoryReads["list"]>({
      op: "list",
      scope: m.scope,
      limit: 50,
    });
    if (!reply) return;
    const candidates = reply.memories.filter((x) => x.id !== m.id && !x.held);
    setUnlinking(null);
    setLinking({
      candidates,
      target: candidates[0]?.id ?? "",
      type: "relates_to",
    });
  }

  // A person's link, from this memory to the one chosen (or, to confirm an AI client's, the way it was made): the engine
  // decides who may (the author lock, one scope, no held memory) and says why not.
  async function linkTo(m: Memory, from: string, to: string, type: string) {
    const reply = await call<MemoryLinked>({
      op: "link",
      id: from,
      target: to,
      type,
    });
    if (!reply) return;
    setLinking(null);
    setNotice({
      text: reply.unchanged
        ? "Nothing changed: they are linked that way already."
        : reply.created
          ? `Linked (${linkName(reply.type)}). The map and searches follow it for whoever reads both memories.`
          : "Confirmed: the link is yours now, not only a guess.",
    });
    onSaved?.();
    await readLinks(m.id);
  }

  // A link taken away, or the map's suggestion dismissed: of its one type, between the two memories.
  async function unlinkFrom(m: Memory, c: MemoryConnection) {
    const reply = await call<MemoryUnlinked>({
      op: "unlink",
      id: m.id,
      target: c.memory.id,
      type: c.type,
    });
    setUnlinking(null);
    if (!reply) return;
    setNotice({
      text: reply.unchanged
        ? "Nothing changed: it was gone already."
        : reply.removed.length || reply.kept.length
          ? `Link removed.${reply.kept.length ? " It won't be suggested again." : ""}`
          : "Suggestion dismissed: the map won't suggest it again. Discover lists it, if you want it back.",
    });
    onSaved?.();
    await readLinks(m.id);
  }

  // Still true (run 17): the person's word that it holds as it is, at the revision shown; nothing in it changes, and it
  // leaves the list until it is due again. Any flag for a check comes off with it.
  async function stillTrue(item: StaleMemory) {
    const reply = await call<MemoryConfirmed>({
      op: "still_true",
      id: item.id,
      expected: item.revision,
    });
    if (!reply) return;
    setNotice({
      text: reply.unchanged
        ? "Nothing changed: it was checked today already."
        : `Marked still true: nothing in it changed.${reply.retracted.length ? " Its flag for a check came off." : ""}`,
    });
    await load();
  }

  // Still true for several at once (run 22), each at the revision shown and through the one door (op resolve_all): one changed since
  // stays in the list to read again, and nothing in any of them changes.
  async function stillTrueChosen() {
    const { results, failed } = await several<MemoryResolvedAll["results"][0]>(
      tools,
      "resolve_all",
      chosenStale.map((s) => ({ id: s.id, expected: s.revision })),
      { action: "still_true" },
      "items",
    );
    if (!results.length) {
      await load();
      return;
    }
    setChosenStale([]);
    const done = results.filter((r) => r.result === "resolved").length;
    const already = results.filter((r) => r.result === "unchanged").length;
    setNotice({
      text: [
        done
          ? `Marked ${counted(done, "memory", "memories")} still true: nothing in ${done === 1 ? "it" : "them"} changed.`
          : "Nothing was marked still true.",
        already
          ? `${already === 1 ? "1 was" : `${already} were`} checked already.`
          : "",
        severalWords(results, ["resolved", "unchanged"], failed),
      ]
        .filter(Boolean)
        .join(" "),
      failed: failed || done + already < results.length,
    });
    await load();
  }

  // A flag for a check taken off once its person has checked it still holds (run 17), at the revision read.
  const flagsChecked = async (m: Memory) =>
    wrote(
      await call({ op: "clear_flags", id: m.id, expected: m.revision }),
      "Flag taken off: it stands as you checked it.",
    );

  // A work item's claim let go by its person (migration 081), whoever holds it: the session that held it no longer moves it, and
  // another may claim it. The memory itself does not change.
  async function claimReleased(m: Memory) {
    const reply = await call<{ written: MemoryWritten }>({
      op: "release_claim",
      id: m.id,
    });
    if (!reply) return;
    setNotice({
      text: reply.written.unchanged
        ? "Nothing changed: its claim had ended already."
        : "Claim released: another session may claim it now.",
    });
    if (open) await read(open);
  }

  // A memory's project and tags, what a person changes beside its words: the projects of its scope to choose from.
  async function startOrganising(m: Memory) {
    setOrganising({
      project: m.project?.slug ?? "",
      tags: m.tags.join(", "),
    });
    setStep("organise");
    const reply = await quiet<MemoryReads["projects"]>({
      op: "projects",
      archived: true,
    });
    if (reply) setProjects(reply.projects);
  }

  async function saveOrganised(m: Memory) {
    if (!organising) return;
    const asked = organising.tags
      .split(",")
      .map((t) => t.trim().replace(/^#/, "").toLowerCase())
      .filter(Boolean);
    const tagsChanged = asked.join("\n") !== m.tags.join("\n");
    const projectChanged = organising.project !== (m.project?.slug ?? "");
    if (!tagsChanged && !projectChanged) {
      setStep(null);
      return;
    }
    await wrote(
      await call({
        op: "update",
        id: m.id,
        ...(tagsChanged ? { tags: asked } : {}),
        ...(projectChanged
          ? organising.project
            ? { project: organising.project }
            : { clear: ["project"] }
          : {}),
        expected: m.revision,
      }),
      "Saved; what it was before is kept in its history.",
    );
    onSaved?.();
  }

  // A memory's kind, importance and how long it holds (run 22), what a person says of it beside its words: each one changed is sent,
  // each one taken back to not said is cleared, at the revision read; nothing changed sends nothing.
  function startClassifying(m: Memory) {
    setClassifying({
      kind: m.kind ?? "",
      importance: m.importance ? String(m.importance) : "",
      volatility: m.volatility ?? "",
    });
    setStep("classify");
  }

  async function saveClassified(m: Memory) {
    if (!classifying) return;
    const was = {
      kind: m.kind ?? "",
      importance: m.importance ? String(m.importance) : "",
      volatility: m.volatility ?? "",
    };
    const names = ["kind", "importance", "volatility"] as const;
    const changed = names.filter((n) => classifying[n] !== was[n]);
    if (!changed.length) {
      setStep(null);
      return;
    }
    const set = changed.filter((n) => classifying[n]);
    const cleared = changed.filter((n) => !classifying[n]);
    const reply = await call<{ written: MemoryWritten }>({
      op: "update",
      id: m.id,
      ...Object.fromEntries(
        set.map((n) => [
          n,
          n === "importance" ? Number(classifying[n]) : classifying[n],
        ]),
      ),
      ...(cleared.length ? { clear: cleared } : {}),
      expected: m.revision,
    });
    // Refused, the form stays with what was chosen, the reason on screen (a revision read since reads the memory again).
    if (!reply) return;
    await wrote(reply, "Saved; what it was before is kept in its history.");
    setClassifying(null);
  }

  const status = async (m: Memory, next: MemoryStatus) =>
    wrote(
      await call({
        op: "status",
        id: m.id,
        status: next,
        expected: m.revision,
      }),
      `Marked ${statusNames[next].toLowerCase()}.`,
    );

  if (off)
    return (
      <section
        ref={viewRoot}
        className="explorer-reader memory-view"
        aria-label="Memories"
      >
        <Bar
          title="Memories"
          onBack={onClose}
          label="Close memories"
          heading={heading}
        />
        <p className="memory-empty">Memories are off on this install.</p>
      </section>
    );

  const m = memory;
  return (
    <section
      ref={viewRoot}
      className="explorer-reader memory-view"
      aria-label="Memories"
    >
      <Bar
        title={open ? "Memory" : sections.find((x) => x.id === tab)!.title}
        onBack={open ? () => setOpen(null) : onClose}
        label={open ? "Back to the list" : "Close memories"}
        heading={heading}
      >
        {!open && (
          // One tab stop walked by the arrow keys, Home and End (walkStrip): a toolbar, so a screen reader says the arrow keys move
          // along it, and the strip is the only way to most sections.
          <div
            ref={strip}
            className="memory-tabs"
            role="toolbar"
            aria-label="Show"
            onKeyDown={walkStrip}
            onBlur={leaveStrip}
          >
            {sections.map((section) => {
              const queue = queueOf[section.id];
              const waits = queue && queues ? queues[queue] : 0;
              return (
                <button
                  key={section.id}
                  aria-pressed={tab === section.id}
                  tabIndex={tab === section.id ? 0 : -1}
                  aria-label={
                    waits ? `${section.label}, ${waits} waiting` : undefined
                  }
                  onClick={() => {
                    setNotice(null);
                    setTab(section.id);
                  }}
                >
                  {section.id === "trash" && (
                    <Trash2 size={13} aria-hidden="true" />
                  )}{" "}
                  {section.label}
                  {waits > 0 && (
                    <span className="memory-waits" aria-hidden="true">
                      {waits}
                    </span>
                  )}
                </button>
              );
            })}
          </div>
        )}
      </Bar>
      <div className="reader-text memory-body">
        {!open && tab === "recent" && (
          <>
            <form
              className="memory-capture"
              onSubmit={(e) => {
                e.preventDefault();
                void capture();
              }}
            >
              <label htmlFor="memory-new">Save a memory</label>
              <textarea
                id="memory-new"
                ref={captureBox}
                aria-describedby="memory-new-hint"
                value={draft}
                placeholder="Something worth finding again, written to make sense on its own. #tags become tags."
                rows={3}
                maxLength={131_072}
                onChange={(e) => {
                  setDraft(e.target.value);
                  // A changed text is a new request: only the same text resends under the same key.
                  setKey(null);
                }}
                onKeyDown={(e) => {
                  if (e.key === "Enter" && (e.metaKey || e.ctrlKey)) {
                    e.preventDefault();
                    void capture();
                  }
                }}
              />
              <div className="memory-row">
                <span id="memory-new-hint" className="memory-hint">
                  Saved as a draft in Family; you can confirm it later.
                </span>
                <button
                  type="submit"
                  className="primary-action"
                  disabled={busy || !draft.trim()}
                  onClick={(e) => (keyedSave.current = e.detail === 0)}
                >
                  Save
                </button>
              </div>
            </form>
            <PageBookmark />
            <MemoryList
              list={list}
              filters={filters}
              setFilters={setFilters}
              tags={tags}
              projects={projects}
              chosen={chosenRows}
              setChosen={setChosenRows}
              tools={tools}
              more={() => void load(true)}
              reload={() => load()}
            />
          </>
        )}
        {!open && tab === "plans" && (
          <MemoryTasks
            tools={tools}
            direction={owing}
            setDirection={setOwing}
          />
        )}
        {!open && tab === "due" && (
          <MemoryDueList
            tools={tools}
            filter={dueFilter}
            setFilter={setDueFilter}
          />
        )}
        {!open && tab === "decisions" && (
          <MemoryDecisions
            tools={tools}
            filter={decisionFilter}
            setFilter={setDecisionFilter}
          />
        )}
        {!open && tab === "standing" && <MemoryStandingList tools={tools} />}
        {!open && tab === "projects" && (
          <MemoryProjects
            opened={projectOpen}
            setOpened={setProjectOpen}
            showMemories={(p) => {
              setFilters({ project: { slug: p.slug, scope: p.scope } });
              setTab("recent");
            }}
            deleted={(p) => {
              // The list filtering by it would be refused: it filters by nothing now.
              if (
                filters.project?.slug === p.slug &&
                filters.project.scope === p.scope
              )
                setFilters({ ...filters, project: undefined });
            }}
            tools={tools}
          />
        )}
        {!open && tab === "changes" && (
          <MemoryChanges tools={tools} asBrief={startBrief === "changes"} />
        )}
        {!open && tab === "copies" && <MemoryCopies tools={tools} />}
        {!open && tab === "conflicts" && <MemoryConflicts tools={tools} />}
        {!open && tab === "insights" && <MemoryInsightStream tools={tools} />}
        {!open && tab === "pairs" && <MemoryPairs tools={tools} />}
        {!open && tab === "settings" && settingsRead && (
          <MemorySettingsView settings={settings} tools={tools} />
        )}
        {!open && tab === "review" && (
          <>
            <p className="memory-hint">
              {review
                ? review.count === 1
                  ? "1 memory is held for review."
                  : `${review.count} memories are held for review.`
                : "Held memories wait here for a person."}{" "}
              Search, the map, capsules and AI clients leave each out until it
              is released. Read one to choose it.
            </p>
            <ul className="memory-list" aria-label="Held for review">
              {review?.items.map((item) => {
                const read = reviewed[item.id];
                return (
                  <li key={item.id} className="memory-held-item">
                    <span className="memory-preview">
                      {holdWhy[item.reason]}
                      {item.signals.length
                        ? ` (${item.signals.join(", ")})`
                        : ""}
                    </span>
                    <span className="memory-meta">
                      {scopeName(item)} · from {heldHow(item)}
                      {item.heldBy
                        ? ` for ${personName(item.heldBy)}`
                        : ""} · {when(item.heldAt)}
                    </span>
                    {read && (
                      <p
                        className="source-content memory-text"
                        tabIndex={0}
                        role="group"
                        aria-label="Its words"
                      >
                        {visibleText(read.content)}
                      </p>
                    )}
                    {read && (read.project || read.tags.length > 0) && (
                      <p className="memory-meta">
                        {read.project
                          ? `Project ${visibleText(read.project.slug)}`
                          : ""}
                        {read.project && read.tags.length ? " · " : ""}
                        {read.tags.map((t) => `#${visibleText(t)}`).join(" ")}
                      </p>
                    )}
                    {read && read.project && item.reason === "held_project" && (
                      <p className="memory-hint">
                        Releasing it lets the project{" "}
                        {visibleText(read.project.slug)} into every list too.
                      </p>
                    )}
                    <span
                      className="memory-actions"
                      {...rowGroup(`Held: ${holdWhy[item.reason]}`)}
                    >
                      {!read && (
                        <button onClick={() => void readHeld(item)}>
                          Read it
                        </button>
                      )}
                      {read && item.mayRelease && (
                        <label className="memory-choose">
                          <input
                            type="checkbox"
                            checked={chosen.includes(item.id)}
                            disabled={
                              !chosen.includes(item.id) &&
                              chosen.length >= releaseAllMax
                            }
                            onChange={(e) =>
                              setChosen((c) =>
                                e.target.checked
                                  ? [...c, item.id]
                                  : c.filter((x) => x !== item.id),
                              )
                            }
                          />{" "}
                          Release this one
                        </label>
                      )}
                      {!item.mayRelease && (
                        <span className="memory-hint">
                          Its author or a Family admin releases it.
                        </span>
                      )}
                      <button onClick={() => setOpen(item.id)}>Open</button>
                    </span>
                  </li>
                );
              })}
            </ul>
            {review && !review.items.length && (
              <p className="memory-empty">Nothing is held for review.</p>
            )}
            {review?.next && (
              <button
                className="secondary-action memory-more"
                onClick={() => void load(true)}
              >
                Show more
              </button>
            )}
            {chosen.length >= releaseAllMax && (
              <p className="memory-hint">
                {releaseAllMax} are chosen, as many as one release takes:
                release these, then choose more.
              </p>
            )}
            {chosen.length > 0 &&
              (releasing ? (
                <span
                  className="memory-confirm"
                  role="group"
                  aria-label="Release them?"
                >
                  <span>
                    Release{" "}
                    {chosen.length === 1
                      ? "this memory"
                      : `these ${chosen.length} memories`}
                    ? Search, the map, capsules and AI clients will read the
                    words you read.
                  </span>
                  <button
                    className="memory-danger"
                    onClick={() => void releaseChosen()}
                  >
                    Release
                  </button>
                  <button onClick={() => setReleasing(false)}>
                    Keep them held
                  </button>
                </span>
              ) : (
                <button
                  className="primary-action memory-more"
                  onClick={() => setReleasing(true)}
                >
                  Release {chosen.length === 1 ? "1" : chosen.length} you read…
                </button>
              ))}
          </>
        )}
        {!open && tab === "stale" && (
          <>
            <p className="memory-hint">
              {stale
                ? stale.count === 1
                  ? "1 memory may be out of date."
                  : `${stale.count} memories may be out of date.`
                : "Memories that may be out of date wait here."}{" "}
              One saved as a fact that changes often is due for a check 14 days
              after it last changed, a state after 90 days, and one built on a
              memory marked wrong since straight away. Still true says it holds
              as it is, changing nothing in it; edit it if it changed.
            </p>
            <ul className="memory-list" aria-label="May be out of date">
              {stale?.items.map((item) => (
                <li key={item.id} className="memory-held-item">
                  <span className="memory-preview">
                    {item.preview}
                    {item.truncated ? "…" : ""}
                  </span>
                  <span className="memory-meta">
                    {item.reason === "retracted_source"
                      ? "Built on a memory marked wrong since"
                      : `Unchanged since ${when(item.since)}`}{" "}
                    · {scopeName(item)} · revision {item.revision}
                  </span>
                  <span className="memory-actions" {...rowGroup(item.preview)}>
                    <button onClick={() => void stillTrue(item)}>
                      Still true
                    </button>
                    <button onClick={() => setOpen(item.id)}>Open</button>
                    <label className="memory-choose">
                      <input
                        type="checkbox"
                        checked={chosenStale.some((x) => x.id === item.id)}
                        disabled={
                          !chosenStale.some((x) => x.id === item.id) &&
                          chosenStale.length >= releaseAllMax
                        }
                        onChange={(e) =>
                          setChosenStale((c) =>
                            e.target.checked
                              ? [...c, item]
                              : c.filter((x) => x.id !== item.id),
                          )
                        }
                      />{" "}
                      Choose
                    </label>
                  </span>
                </li>
              ))}
            </ul>
            {stale && !stale.items.length && (
              <p className="memory-empty">Nothing looks out of date.</p>
            )}
            {stale?.next && (
              <button
                className="secondary-action memory-more"
                onClick={() => void load(true)}
              >
                Show more
              </button>
            )}
            <BulkBar
              count={chosenStale.length}
              shown={stale?.items.length ?? 0}
              max={releaseAllMax}
              actions={[{ label: "Still true", run: stillTrueChosen }]}
              onAll={() =>
                setChosenStale((c) =>
                  [
                    ...c,
                    ...(stale?.items ?? []).filter(
                      (s) => !c.some((x) => x.id === s.id),
                    ),
                  ].slice(0, releaseAllMax),
                )
              }
              onClear={() => setChosenStale([])}
              tools={tools}
            />
          </>
        )}
        {!open && tab === "trash" && (
          <>
            <p className="memory-hint">
              Forgotten memories stay here for {settings?.trashDays ?? 14} days,
              then are deleted for good.
            </p>
            <ul className="memory-list" aria-label="Trash">
              {trash?.items.map((t) => (
                <li key={t.id} className="memory-trashed">
                  <span className="memory-preview">
                    {t.held ? "Held for review" : (t.preview ?? "")}
                    {t.truncated ? "…" : ""}
                  </span>
                  <span className="memory-meta">
                    {t.daysLeft === 1
                      ? "1 day left"
                      : `${t.daysLeft} days left`}{" "}
                    · {scopeName(t)}
                  </span>
                  {deleting === t.id ? (
                    <span
                      className="memory-confirm"
                      role="group"
                      aria-label="Delete for good?"
                    >
                      <span>Delete it for good? This can't be undone.</span>
                      <button
                        className="memory-danger"
                        onClick={() => void deleteForever(t)}
                      >
                        Delete for good
                      </button>
                      <button onClick={() => setDeleting(null)}>Keep it</button>
                    </span>
                  ) : (
                    <span
                      className="memory-actions"
                      {...rowGroup(
                        t.held ? "Held for review" : (t.preview ?? ""),
                      )}
                    >
                      <button onClick={() => void restore(t.id, t.nonce)}>
                        <RotateCcw size={13} aria-hidden="true" /> Restore
                      </button>
                      {t.mayDeleteForever && (
                        <button onClick={() => setDeleting(t.id)}>
                          Delete for good…
                        </button>
                      )}
                      <label className="memory-choose">
                        <input
                          type="checkbox"
                          checked={chosenTrash.some((x) => x.id === t.id)}
                          disabled={
                            !chosenTrash.some((x) => x.id === t.id) &&
                            chosenTrash.length >= releaseAllMax
                          }
                          onChange={(e) =>
                            setChosenTrash((c) =>
                              e.target.checked
                                ? [...c, t]
                                : c.filter((x) => x.id !== t.id),
                            )
                          }
                        />{" "}
                        Choose
                      </label>
                    </span>
                  )}
                </li>
              ))}
            </ul>
            {trash && !trash.items.length && (
              <p className="memory-empty">The trash is empty.</p>
            )}
            {trash?.next && (
              <button
                className="secondary-action memory-more"
                onClick={() => void load(true)}
              >
                Show more
              </button>
            )}
            <BulkBar
              count={chosenTrash.length}
              shown={trash?.items.length ?? 0}
              max={releaseAllMax}
              actions={[
                { label: "Restore", run: restoreChosen },
                ...(chosenTrash.some((t) => t.mayDeleteForever)
                  ? [
                      {
                        label: "Delete for good…",
                        asks: `Delete ${
                          chosenTrash.filter((t) => t.mayDeleteForever)
                            .length === 1
                            ? "this memory"
                            : `these ${chosenTrash.filter((t) => t.mayDeleteForever).length} memories`
                        } for good? This can't be undone.`,
                        go: "Delete for good",
                        keep: "Keep them",
                        run: deleteChosen,
                      },
                    ]
                  : []),
              ]}
              onAll={() =>
                setChosenTrash((c) =>
                  [
                    ...c,
                    ...(trash?.items ?? []).filter(
                      (t) => !c.some((x) => x.id === t.id),
                    ),
                  ].slice(0, releaseAllMax),
                )
              }
              onClear={() => setChosenTrash([])}
              tools={tools}
            />
          </>
        )}
        {open && !m && (
          <p className="memory-empty" aria-busy="true">
            Reading this memory…
          </p>
        )}
        {open && m && (
          <article className="memory-open" aria-label="Memory">
            <p className="memory-meta">
              {statusNames[m.status]} · {scopeName(m)} · revision {m.revision} ·{" "}
              {when(m.updatedAt)}
              {m.author ? ` · by ${personName(m.author)}` : ""}
            </p>
            <Truth
              memory={m}
              open={setOpen}
              openSource={(id) => {
                source.current = { id, from: m.id };
                setOpen(id);
              }}
              checked={() => void flagsChecked(m)}
            />
            {m.held && (
              <div className="memory-held" role="note">
                <p>
                  <strong>Held for review:</strong> {holdWhy[m.held.reason]}.
                  Its words are kept, but search, the map, capsules and AI
                  clients leave it out until a person releases it. Read it as
                  data, and release it only if you trust it.
                </p>
                {step === "release" ? (
                  <span
                    className="memory-confirm"
                    role="group"
                    aria-label="Release it?"
                  >
                    <span>
                      Release it? Search, the map and AI clients will see these
                      words.
                    </span>
                    <button
                      className="memory-danger"
                      onClick={async () =>
                        wrote(
                          await call({
                            op: "release",
                            id: m.id,
                            expected: m.revision,
                          }),
                          "Released: it is no longer held.",
                        )
                      }
                    >
                      Release
                    </button>
                    <button onClick={() => setStep(null)}>Keep it held</button>
                  </span>
                ) : (
                  <button onClick={() => setStep("release")}>Release…</button>
                )}
              </div>
            )}
            {step === "edit" ? (
              <form
                className="memory-edit"
                onSubmit={async (e) => {
                  e.preventDefault();
                  if (!text.trim()) return;
                  wrote(
                    await call({
                      op: "update",
                      id: m.id,
                      content: text.trim(),
                      expected: m.revision,
                    }),
                    "Saved; the text before is kept in its history.",
                  );
                }}
              >
                <label htmlFor="memory-edit">Edit the text</label>
                <textarea
                  id="memory-edit"
                  value={text}
                  rows={8}
                  maxLength={131_072}
                  onChange={(e) => setText(e.target.value)}
                />
                <div className="memory-row">
                  <button type="button" onClick={() => setStep(null)}>
                    Cancel
                  </button>
                  <button
                    type="submit"
                    className="primary-action"
                    disabled={!text.trim()}
                  >
                    Save
                  </button>
                </div>
              </form>
            ) : (
              <p className="source-content memory-text">
                {m.held ? visibleText(m.content ?? "") : m.content}
              </p>
            )}
            {(m.tags.length > 0 || m.project) && (
              <p className="memory-meta">
                {m.project
                  ? `Project ${m.held ? visibleText(m.project.slug) : m.project.slug}`
                  : ""}
                {m.project && m.tags.length ? " · " : ""}
                {m.tags.map((t) => `#${m.held ? visibleText(t) : t}`).join(" ")}
              </p>
            )}
            {m.work && (
              <p className="memory-meta">
                Work item: {workWords(m.work)}
                {m.work.assignee && ` · ${workWho({ ...m.work, claim: null })}`}
              </p>
            )}
            {m.work?.claim && (
              <div className="memory-truth memory-claim" role="note">
                <p>
                  Claimed by {m.work.claim.label} until{" "}
                  {when(m.work.claim.expiresAt)}: only that session moves its
                  state until then, unless the claim is released.
                </p>
                <button type="button" onClick={() => void claimReleased(m)}>
                  Release claim
                </button>
              </div>
            )}
            {(m.kind || m.importance || m.volatility) && (
              <p className="memory-meta">
                {[
                  m.kind ? (kindWords[m.kind] ?? m.kind) : "",
                  m.importance ? "importance " + m.importance + " of 5" : "",
                  m.volatility
                    ? (volatilityWords[m.volatility] ?? m.volatility)
                    : "",
                ]
                  .filter(Boolean)
                  .join(" · ")
                  .replace(/^./, (c) => c.toUpperCase())}
              </p>
            )}
            {m.capsule && (
              <p className="memory-meta">
                In {capsuleName(m, m.capsule)} as{" "}
                {slotNames[m.capsule.slot].toLowerCase()}
                {m.status !== "canonical" || m.held
                  ? " (served once it is confirmed and not held)"
                  : ""}
              </p>
            )}
            {step === "append" && (
              <form
                className="memory-edit"
                onSubmit={async (e) => {
                  e.preventDefault();
                  if (!text.trim()) return;
                  wrote(
                    await call({
                      op: "append",
                      id: m.id,
                      text: text.trim(),
                      expected: m.revision,
                    }),
                    "Update added, dated today.",
                  );
                }}
              >
                <label htmlFor="memory-append">What changed</label>
                <textarea
                  id="memory-append"
                  value={text}
                  rows={3}
                  maxLength={131_072}
                  onChange={(e) => setText(e.target.value)}
                />
                <div className="memory-row">
                  <button type="button" onClick={() => setStep(null)}>
                    Cancel
                  </button>
                  <button
                    type="submit"
                    className="primary-action"
                    disabled={!text.trim()}
                  >
                    Add update
                  </button>
                </div>
              </form>
            )}
            {step === "organise" && organising && (
              <form
                className="memory-edit"
                onSubmit={(e) => {
                  e.preventDefault();
                  void saveOrganised(m);
                }}
              >
                <label htmlFor="memory-project">Project</label>
                <select
                  id="memory-project"
                  className="memory-input"
                  value={organising.project}
                  onChange={(e) =>
                    setOrganising({ ...organising, project: e.target.value })
                  }
                >
                  <option value="">In no project</option>
                  {m.project &&
                    !projects?.some(
                      (p) => p.slug === m.project!.slug && p.scope === m.scope,
                    ) && (
                      <option value={m.project.slug}>{m.project.slug}</option>
                    )}
                  {(projects ?? [])
                    .filter(
                      (p) =>
                        p.scope === m.scope &&
                        (p.status === "active" || p.slug === m.project?.slug),
                    )
                    .map((p) => (
                      <option key={p.id} value={p.slug}>
                        {p.name}
                      </option>
                    ))}
                </select>
                <label htmlFor="memory-tags">Tags, separated by commas</label>
                <input
                  id="memory-tags"
                  className="memory-input"
                  value={organising.tags}
                  onChange={(e) =>
                    setOrganising({ ...organising, tags: e.target.value })
                  }
                />
                <div className="memory-row">
                  <button type="button" onClick={() => setStep(null)}>
                    Cancel
                  </button>
                  <button type="submit" className="primary-action">
                    Save
                  </button>
                </div>
              </form>
            )}
            {step === "classify" && classifying && (
              <form
                className="memory-edit"
                aria-label="What it is"
                onSubmit={(e) => {
                  e.preventDefault();
                  void saveClassified(m);
                }}
              >
                <label htmlFor="memory-kind">Kind</label>
                <select
                  id="memory-kind"
                  className="memory-input"
                  value={classifying.kind}
                  onChange={(e) =>
                    setClassifying({ ...classifying, kind: e.target.value })
                  }
                >
                  <option value="">Not said</option>
                  {(["episodic", "semantic"] as const).map((k) => (
                    <option key={k} value={k}>
                      {kindNames[k]}
                    </option>
                  ))}
                </select>
                <label htmlFor="memory-importance">
                  Importance, 1 the least and 5 the most
                </label>
                <select
                  id="memory-importance"
                  className="memory-input"
                  value={classifying.importance}
                  onChange={(e) =>
                    setClassifying({
                      ...classifying,
                      importance: e.target.value,
                    })
                  }
                >
                  <option value="">Not said</option>
                  {[1, 2, 3, 4, 5].map((n) => (
                    <option key={n} value={String(n)}>
                      {n}
                    </option>
                  ))}
                </select>
                <label htmlFor="memory-volatility">How long it holds</label>
                <select
                  id="memory-volatility"
                  className="memory-input"
                  value={classifying.volatility}
                  onChange={(e) =>
                    setClassifying({
                      ...classifying,
                      volatility: e.target.value,
                    })
                  }
                >
                  <option value="">Not said</option>
                  {(["durable", "state", "volatile"] as const).map((v) => (
                    <option key={v} value={v}>
                      {volatilityNames[v]}
                    </option>
                  ))}
                </select>
                <div className="memory-row">
                  <button type="button" onClick={() => setStep(null)}>
                    Cancel
                  </button>
                  <button type="submit" className="primary-action">
                    Save
                  </button>
                </div>
              </form>
            )}
            {step === "forget" && (
              <span
                className="memory-confirm"
                role="group"
                aria-label="Forget it?"
              >
                <span>
                  Move it to the trash? It stays there 14 days, and you can
                  bring it back.
                </span>
                <button
                  className="memory-danger"
                  onClick={() => void forget(m)}
                >
                  Move to the trash
                </button>
                <button onClick={() => setStep(null)}>Keep it</button>
              </span>
            )}
            {step === "share" && (
              <span
                className="memory-confirm"
                role="group"
                aria-label={
                  m.scope === "private"
                    ? "Share it with Family?"
                    : "Take it back from Family?"
                }
              >
                <span>
                  {m.scope === "private"
                    ? "Share it with Family? Every member reads it from now on, with its author; its earlier history stays its author's. Its author or a Family admin can take it back."
                    : `Take it back to ${personName(m.author ?? m.owner)}'s own memories? Family's members stop reading it. Only its author or a Family admin can.`}
                </span>
                <button
                  className="memory-danger"
                  onClick={() =>
                    void move(m, m.scope === "private" ? "shared" : "private")
                  }
                >
                  {m.scope === "private" ? "Share with Family" : "Take it back"}
                </button>
                <button onClick={() => setStep(null)}>
                  {m.scope === "private"
                    ? "Keep it private"
                    : "Keep it in Family"}
                </button>
              </span>
            )}
            {!step && (
              <div
                className="memory-actions"
                role="group"
                aria-label="This memory"
              >
                <button
                  onClick={() => {
                    setText(m.content ?? "");
                    setStep("edit");
                  }}
                >
                  Edit
                </button>
                <button
                  onClick={() => {
                    setText("");
                    setStep("append");
                  }}
                >
                  Add an update
                </button>
                {!m.held && (
                  <button onClick={() => void startOrganising(m)}>
                    Project and tags…
                  </button>
                )}
                {!m.held && (
                  <button onClick={() => startClassifying(m)}>
                    Kind and importance…
                  </button>
                )}
                <label className="memory-status-pick">
                  <span>Status</span>
                  <select
                    value={m.status}
                    onChange={(e) =>
                      void status(m, e.target.value as MemoryStatus)
                    }
                  >
                    {(["draft", "canonical", "deprecated"] as const).map(
                      (s) => (
                        <option key={s} value={s}>
                          {statusNames[s]}
                        </option>
                      ),
                    )}
                  </select>
                </label>
                <label className="memory-status-pick">
                  <span>Capsule slot</span>
                  <select
                    value={
                      m.capsule ? `${m.capsule.kind}:${m.capsule.slot}` : "none"
                    }
                    onChange={(e) => void slot(m, e.target.value)}
                  >
                    <option value="none">In no capsule</option>
                    <optgroup label={`${whose(m)} core capsule`}>
                      {capsuleSlots.core.map((s) => (
                        <option key={s} value={`core:${s}`}>
                          {slotNames[s]}
                        </option>
                      ))}
                    </optgroup>
                    {m.project && (
                      <optgroup label={`${m.project.slug} project's capsule`}>
                        {capsuleSlots.project.map((s) => (
                          <option key={s} value={`project:${s}`}>
                            {slotNames[s]}
                          </option>
                        ))}
                      </optgroup>
                    )}
                  </select>
                </label>
                {sharing && m.scope === "private" && !m.held && (
                  <button onClick={() => setStep("share")}>
                    Share with Family…
                  </button>
                )}
                {sharing && m.scope === "shared" && m.author && (
                  <button onClick={() => setStep("share")}>
                    Make private…
                  </button>
                )}
                <button onClick={() => void undoLast(m)}>
                  <RotateCcw size={13} aria-hidden="true" /> Undo last change
                </button>
                <button
                  aria-expanded={Boolean(history)}
                  onClick={() =>
                    history ? setHistory(null) : void readHistory(m.id)
                  }
                >
                  <History size={13} aria-hidden="true" /> History
                </button>
                <button
                  aria-expanded={Boolean(links)}
                  onClick={() => {
                    setUnlinking(null);
                    setLinking(null);
                    if (links) setLinks(null);
                    else void readLinks(m.id);
                  }}
                >
                  <Link2 size={13} aria-hidden="true" /> Links
                </button>
                <button onClick={() => setStep("forget")}>
                  <Trash2 size={13} aria-hidden="true" /> Forget…
                </button>
              </div>
            )}
            {links && links.id === m.id && (
              <section className="memory-version" aria-label="Links">
                {!links.connections.length && (
                  <p className="memory-hint">
                    No links yet, and nothing the map suggests.
                  </p>
                )}
                <ul className="memory-history" aria-label="Its links">
                  {links.connections.map((c) => {
                    const key = `${c.linkId ?? "map"}:${c.type}:${c.memory.id}`;
                    const incoming = c.direction === "incoming";
                    // An unlink takes every made link of its type between the two, either way (052), so it is offered
                    // only when each of them is this person's to take away, and its prompt says so.
                    const together = links.connections.filter(
                      (x) =>
                        x.made &&
                        x.type === c.type &&
                        x.memory.id === c.memory.id,
                    );
                    const mayUnlink = c.made
                      ? together.every((x) => x.mayUnlink)
                      : c.mayUnlink;
                    return (
                      <li key={key}>
                        <span className="memory-meta">
                          {linkName(c.type, !incoming)} ·{" "}
                          {!c.made
                            ? "suggested by the map"
                            : // An import's link has no maker here: the file does not say a person's from an AI's.
                              c.imported
                              ? c.verification === "verified"
                                ? "imported, confirmed"
                                : "imported, not yet confirmed"
                              : c.verification === "verified"
                                ? c.mine
                                  ? "made by you"
                                  : "made by a person"
                                : c.verification === "inferred"
                                  ? c.mine
                                    ? "made by your AI client, not yet confirmed"
                                    : "an AI client's, not yet confirmed"
                                  : "suggested automatically, not yet confirmed"}{" "}
                          · {scopeName(c.memory)}
                        </span>
                        <span className="memory-preview">
                          {c.memory.held
                            ? "Held for review"
                            : (c.memory.preview ?? "")}
                          {c.memory.truncated ? "…" : ""}
                        </span>
                        {unlinking === key ? (
                          <span
                            className="memory-confirm"
                            role="group"
                            aria-label={
                              c.made
                                ? "Remove the links of this type between them?"
                                : "Dismiss this suggestion?"
                            }
                          >
                            <span>
                              {c.made
                                ? `Remove every "${linkName(c.type)}" link between these two memories${together.length > 1 ? ", both ways" : ""}? Linking them again makes a new one.`
                                : "Dismiss this suggestion? The map won't suggest it again until it is shown again in Discover."}
                            </span>
                            <button
                              className="memory-danger"
                              onClick={() => void unlinkFrom(m, c)}
                            >
                              {c.made ? "Remove the link" : "Dismiss"}
                            </button>
                            <button onClick={() => setUnlinking(null)}>
                              Keep it
                            </button>
                          </span>
                        ) : (
                          <span
                            className="memory-actions"
                            {...rowGroup(
                              c.memory.held
                                ? "Held for review"
                                : (c.memory.preview ?? "A linked memory"),
                            )}
                          >
                            <button onClick={() => setOpen(c.memory.id)}>
                              Open
                            </button>
                            {c.made && c.verification !== "verified" && (
                              <button
                                onClick={() =>
                                  void linkTo(
                                    m,
                                    incoming ? c.memory.id : m.id,
                                    incoming ? m.id : c.memory.id,
                                    c.type,
                                  )
                                }
                              >
                                Confirm
                              </button>
                            )}
                            {mayUnlink && (
                              <button
                                onClick={() => {
                                  setLinking(null);
                                  setUnlinking(key);
                                }}
                              >
                                {c.made ? "Unlink…" : "Dismiss…"}
                              </button>
                            )}
                          </span>
                        )}
                      </li>
                    );
                  })}
                </ul>
                {links.truncated && (
                  <p className="memory-hint">
                    More links than these {links.connections.length}: the newest
                    are shown. ./rhythm link lists more at the terminal.
                  </p>
                )}
                {linking ? (
                  <form
                    className="memory-edit"
                    onSubmit={(e) => {
                      e.preventDefault();
                      if (linking.target)
                        void linkTo(m, m.id, linking.target, linking.type);
                    }}
                  >
                    {linking.candidates.length ? (
                      <>
                        <label htmlFor="memory-link-target">Link it to</label>
                        <select
                          id="memory-link-target"
                          value={linking.target}
                          onChange={(e) => {
                            const kind = linking.candidates.find(
                              (x) => x.id === e.target.value,
                            )?.kind as MemoryKind | null;
                            // A type the two memories' kinds don't allow (the episodic rule) goes back to related to.
                            setLinking({
                              ...linking,
                              target: e.target.value,
                              type: linkKindsAllow(
                                linking.type,
                                m.kind as MemoryKind | null,
                                kind,
                              )
                                ? linking.type
                                : "relates_to",
                            });
                          }}
                        >
                          {linking.candidates.map((x) => (
                            <option key={x.id} value={x.id}>
                              {(x.content ?? "").slice(0, 80) || x.id}
                            </option>
                          ))}
                        </select>
                        <label htmlFor="memory-link-type">This memory is</label>
                        <select
                          id="memory-link-type"
                          value={linking.type}
                          onChange={(e) =>
                            setLinking({ ...linking, type: e.target.value })
                          }
                        >
                          {linkable.map((t) => (
                            <option
                              key={t}
                              value={t}
                              disabled={
                                !linkKindsAllow(
                                  t,
                                  m.kind as MemoryKind | null,
                                  linking.candidates.find(
                                    (x) => x.id === linking.target,
                                  )?.kind as MemoryKind | null,
                                )
                              }
                            >
                              {linkName(t)}
                            </option>
                          ))}
                        </select>
                      </>
                    ) : (
                      <p className="memory-hint">
                        No other memory of its scope to link it to yet.
                      </p>
                    )}
                    <div className="memory-row">
                      <button type="button" onClick={() => setLinking(null)}>
                        Cancel
                      </button>
                      <button
                        type="submit"
                        className="primary-action"
                        disabled={!linking.target}
                      >
                        Link
                      </button>
                    </div>
                  </form>
                ) : (
                  <button onClick={() => void startLink(m)}>
                    Link it to another memory…
                  </button>
                )}
              </section>
            )}
            {version && (
              <div
                key={version.revision}
                className="memory-version"
                role="region"
                aria-label={`Revision ${version.revision}`}
              >
                <p className="memory-meta">
                  Revision {version.revision} · {statusNames[version.status]} ·{" "}
                  {when(version.writtenAt)}
                  <button
                    aria-label="Close this revision"
                    onClick={() => setVersion(null)}
                  >
                    <X size={13} />
                  </button>
                </p>
                <p className="source-content memory-text">
                  {version.content ??
                    (version.held
                      ? "This revision was held for review, so its words are not shown."
                      : "")}
                </p>
              </div>
            )}
            {history && (
              <MemoryTimeline
                memory={m}
                history={history}
                onRead={async (revision) => {
                  const reply = await call<MemoryReads["get"]>({
                    op: "get",
                    id: m.id,
                    version: revision,
                  });
                  if (reply?.version) setVersion(reply.version);
                }}
                onPutBack={async (revision) =>
                  wrote(
                    await call({
                      op: "undo",
                      id: m.id,
                      to: revision,
                      expected: m.revision,
                    }),
                    `Put back as at revision ${revision}.`,
                  )
                }
                onEarlier={() => void readEarlier(m.id)}
              />
            )}
          </article>
        )}
      </div>
      {/* On screen before anything is said in it, as a live region must be for a screen reader to say what comes. */}
      <div className="memory-notices" aria-live="polite">
        {notice && (
          <p
            className={`upload-status memory-notice${notice.failed ? " failed" : ""}`}
            role="status"
          >
            <span>{notice.text}</span>
            {notice.action && (
              <button onClick={notice.action.run}>{notice.action.label}</button>
            )}
            {!notice.busy && (
              <button aria-label="Dismiss" onClick={() => setNotice(null)}>
                <X size={13} />
              </button>
            )}
          </p>
        )}
      </div>
    </section>
  );
}

function Bar({
  title,
  onBack,
  label,
  heading,
  children,
}: {
  title: string;
  onBack: () => void;
  label: string;
  heading: React.RefObject<HTMLHeadingElement | null>;
  children?: React.ReactNode;
}) {
  return (
    <header className="reader-bar">
      <button className="back-link" onClick={onBack} aria-label={label}>
        <ArrowLeft size={15} /> <span className="reader-label">Back</span>
      </button>
      <div className="reader-title">
        <span className="eyebrow">MEMORIES</span>
        <h2 ref={heading} tabIndex={-1}>
          {title}
        </h2>
      </div>
      {children}
    </header>
  );
}
