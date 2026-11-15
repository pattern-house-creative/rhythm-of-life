import {
  mergePage,
  readable,
  retrieveGraph,
  identityKey,
} from "./knowledge.js";
import type {
  GraphAdapter,
  GraphNode,
  GraphPage,
  GraphSeries,
} from "./knowledge.js";
import { rankNodes } from "./graphView.js";
import type { DocumentOriginal, UploadAnswer } from "./documents.js";
import type { DailyInsight, FeedbackValue, InsightRun } from "./insights.js";
import type { RecallAnswer } from "./recall.js";
import type { MemoryClient } from "./memories.js";
import type {
  Dashboard,
  DashboardData,
  DashboardSummary,
  Corner,
  DashboardCards,
  DashboardMap,
  DashboardWriteAnswer,
  LayoutAnswer,
} from "./dashboards.js";
import type { Layout } from "./parts.js";
import type {
  ListenAnswer,
  ListenTaken,
  ScreenSettings,
  ScreenSummary,
  SendAnswer,
  Showing,
} from "./screens.js";

export const emptyGraph = (): GraphPage => ({
  nodes: [],
  edges: [],
  nextCursor: null,
  completeness: "partial",
  issues: [],
});

// Revalidate loaded IDs separately from discovery. Absence from a bounded search
// is never treated as deletion. Only authoritative get/null or explicit tombstones remove.
export async function refreshGraph(
  adapters: GraphAdapter[],
  current: GraphPage,
  signal?: AbortSignal,
): Promise<GraphPage> {
  const checked = await Promise.allSettled(
    adapters.map(async (a) => {
      const known = current.nodes.filter(
        (n) =>
          n.resource.identity.provider === a.provider &&
          (!a.key || a.key === `${a.provider}:${n.resource.identity.account}`),
      );
      if (a.refresh) return a.refresh(known, signal);
      const result = emptyGraph();
      result.completeness = "partial";
      // Bounded concurrency for sources without a batch revalidation API.
      for (let i = 0; i < known.length; i += 8) {
        const batch = await Promise.allSettled(
          known.slice(i, i + 8).map((n) => a.get(n.id, signal)),
        );
        batch.forEach((r, j) => {
          if (r.status === "fulfilled") {
            if (r.value && readable(r.value)) result.nodes.push(r.value);
            else (result.removedNodeIds ??= []).push(known[i + j].id);
          } else {
            const previous = known[i + j];
            result.nodes.push({
              ...previous,
              resource: { ...previous.resource, availability: "stale" },
            });
            result.issues.push({
              provider: a.provider,
              reason: "Some loaded records could not be revalidated.",
            });
          }
        });
      }
      return result;
    }),
  );
  if (signal?.aborted) throw new DOMException("Aborted", "AbortError");
  let merged = {
    ...current,
    issues: [] as GraphPage["issues"],
    sourceStates: {} as NonNullable<GraphPage["sourceStates"]>,
  };
  const tombstones = new Set<string>();
  checked.forEach((r, i) => {
    const a = adapters[i],
      key = a.key ?? a.provider;
    if (r.status === "fulfilled") {
      r.value.removedNodeIds?.forEach((id) => tombstones.add(id));
      merged = mergePage(merged, r.value) as typeof merged;
    } else {
      merged.issues.push({
        provider: a.provider,
        reason: "Refresh unavailable; last retrieved records may be stale.",
      });
      merged.nodes = merged.nodes.map((n) =>
        n.resource.identity.provider === a.provider &&
        (!a.key || a.key === `${a.provider}:${n.resource.identity.account}`)
          ? {
              ...n,
              resource: { ...n.resource, availability: "stale" as const },
            }
          : n,
      );
    }
    merged.sourceStates[key] = {
      completeness: r.status === "fulfilled" ? r.value.completeness : "partial",
    };
  });
  const discovery = await retrieveGraph(adapters, "discover", "", {}, signal);
  // Revocation wins within a refresh, even if a discovery response raced it.
  const result = mergePage(merged, {
    ...discovery,
    removedNodeIds: [...tombstones, ...(discovery.removedNodeIds ?? [])],
  });
  const cursors = { ...discovery.cursors };
  const discoveryStates = { ...discovery.sourceStates };
  const knownIds = new Set(current.nodes.map((n) => n.id));
  for (const a of adapters) {
    const key = a.key ?? a.provider;
    const prior = current.discoveryCursors?.[key];
    const newRecords = discovery.nodes.some(
      (n) =>
        !knownIds.has(n.id) &&
        n.resource.identity.provider === a.provider &&
        (!a.key || a.key === `${a.provider}:${n.resource.identity.account}`),
    );
    // Preserve progress on an unchanged head. A changed head may contain more
    // additions than one page, so follow its continuation before claiming coverage.
    if (prior !== undefined && !newRecords) {
      cursors[key] = prior;
      discoveryStates[key] = current.discoveryStates?.[key] ?? {
        completeness: "partial",
      };
    }
  }
  const states = { ...merged.sourceStates, ...discoveryStates };
  for (const [key, state] of Object.entries(merged.sourceStates)) {
    if (state.completeness === "partial") states[key] = state;
  }
  return {
    ...result,
    discoveryCursors: cursors,
    discoveryStates,
    sourceStates: states,
    completeness:
      result.issues.length ||
      Object.values(states).some((s) => s.completeness === "partial")
        ? "partial"
        : "complete",
  };
}

// Memory is a working set, not another knowledge database. Eviction is not deletion.
export function boundGraph(
  page: GraphPage,
  pins: string[] = [],
  limit = 1500,
): GraphPage {
  const nodes = rankNodes(page.nodes, page.edges, pins).slice(0, limit);
  const ids = new Set(nodes.map((n) => n.id));
  return {
    ...page,
    nodes,
    edges: page.edges
      .filter((e) => ids.has(e.source) || ids.has(e.target))
      .slice(0, limit * 8),
    completeness: page.nodes.length > limit ? "partial" : page.completeness,
  };
}
export type OriginalFile =
  | { ok: true; bytes: Uint8Array }
  | { ok: false; reason: "missing" | "unavailable" }
  | { ok: false; reason: "too_large"; maxBytes: number };
export interface GraphSession {
  initial: GraphPage;
  retrieve: (
    operation: "discover" | "search" | "neighbors",
    value: string,
    cursors?: Record<string, string | null>,
    signal?: AbortSignal,
    states?: GraphPage["sourceStates"],
  ) => ReturnType<typeof retrieveGraph>;
  refresh: (current: GraphPage, signal?: AbortSignal) => Promise<GraphPage>;
  // One memory's current full text, read by the source when it is opened. Never merged into the graph.
  // A document someone added as a file also names its archived original.
  text?: (
    id: string,
    signal?: AbortSignal,
  ) => Promise<{
    text: string;
    modifiedAt: string | null;
    original?: DocumentOriginal | null;
  } | null>;
  // A document's original file, read when the reader shows it and never kept: its bytes, or why
  // not (gone, too large for this front end, or unavailable).
  original?: (id: string, signal?: AbortSignal) => Promise<OriginalFile>;
  // A data star's recent readings, read when it is opened and never kept.
  data?: (id: string, signal?: AbortSignal) => Promise<GraphSeries[] | null>;
  // The explorer's combined search (SB-P2-10, recall.ts): the stars combined retrieval finds for
  // one question, with why, while the engine serves it. A live session only; never kept.
  recall?: (question: string, signal?: AbortSignal) => Promise<RecallAnswer>;
  demo?: { advance: () => void; setUnavailable: (value: boolean) => void };
  // The daily insights and their latest runs, newest
  // first, and feedback on one (with an optional note); feedback
  // answers with the updated insight.
  insights?: (
    signal?: AbortSignal,
  ) => Promise<{ insights: DailyInsight[]; runs: InsightRun[] }>;
  insightFeedback?: (
    id: string,
    value: FeedbackValue,
    until?: string,
    note?: string,
    signal?: AbortSignal,
  ) => Promise<DailyInsight>;
  // Saved dashboards (each one's latest version, without its page), one
  // version with its page, and what a version's feeds hold now, read when the
  // dashboard is open and never kept.
  dashboards?: (signal?: AbortSignal) => Promise<DashboardSummary[]>;
  dashboard?: (
    id: string,
    version?: number,
    signal?: AbortSignal,
  ) => Promise<Dashboard | null>;
  dashboardData?: (
    id: string,
    version?: number,
    signal?: AbortSignal,
  ) => Promise<DashboardData | null>;
  // A page's declared write, once edits are allowed, and its undo; both
  // answer what changed, or why nothing did.
  dashboardWrite?: (
    id: string,
    version: number,
    name: string,
    change: unknown,
    signal?: AbortSignal,
  ) => Promise<DashboardWriteAnswer>;
  dashboardUndo?: (
    write: string,
    signal?: AbortSignal,
  ) => Promise<DashboardWriteAnswer>;
  // How a dashboard's newest version is arranged (docs/dev/screens.md 10.7):
  // saving a layout from the revision the change started from, which answers
  // the saved layout and its revision, or why not (with the layout as it
  // stands when it changed since); never tried twice. And a map part's
  // focused sky, a page at a time (10.4).
  dashboardLayout?: (
    id: string,
    version: number,
    layout: Layout,
    base: number,
    note: string,
    signal?: AbortSignal,
  ) => Promise<LayoutAnswer>;
  dashboardMap?: (
    id: string,
    version: number | undefined,
    part: string,
    cursor?: string | null,
    signal?: AbortSignal,
  ) => Promise<DashboardMap | null>;
  // Turns edits on or off for exactly that version's writes; answers whether
  // they are on.
  dashboardAllowEdits?: (
    id: string,
    version: number,
    allowed: boolean,
    signal?: AbortSignal,
  ) => Promise<boolean>;
  // The ambient corners showing a dashboard's card, and choosing one (null puts
  // the stat card back).
  dashboardCards?: (signal?: AbortSignal) => Promise<DashboardCards>;
  setDashboardCard?: (
    corner: Corner,
    dashboard: string | null,
    signal?: AbortSignal,
  ) => Promise<DashboardCards>;
  // The stars pinned to the sky, kept on the engine so every screen shows them;
  // pinning one answers them all.
  pins?: (signal?: AbortSignal) => Promise<string[]>;
  setPin?: (
    star: string,
    pinned: boolean,
    signal?: AbortSignal,
  ) => Promise<string[]>;
  // Asks the engine to rebuild the map now; answers whether it accepted the
  // request or one is already waiting.
  requestRebuild?: (
    signal?: AbortSignal,
  ) => Promise<{ accepted: boolean; pending: boolean }>;
  // Documents a person adds: whether the engine takes them now, and one file
  // handed over, answering the star it becomes or why it was not added.
  uploads?: (
    signal?: AbortSignal,
  ) => Promise<{ available: boolean; maxBytes: number }>;
  upload?: (file: File, signal?: AbortSignal) => Promise<UploadAnswer>;
  // Native memories in the Memories view (SB-P3-13): whether the engine serves them (only with its
  // memory.tools on), and one op as the app. The demo has none.
  memories?: MemoryClient;
  // Live screens (docs/dev/screens.md): this screen's one waiting listen, answering the command
  // it claimed (or none after the wait), or null when screens are off here, or that this
  // screen's id listens with another app login (only while viewer sessions are on); the outcome of a
  // command it applied; a command for another screen; the screens seen today; a new name. The
  // demo plays the engine's part between this browser's tabs, and stops when the session does.
  screenListen?: (
    listen: {
      screen: string;
      name: string;
      hold?: number;
      showing?: Showing;
      settings?: ScreenSettings;
    },
    signal?: AbortSignal,
  ) => Promise<ListenAnswer | ListenTaken | null>;
  screenDone?: (
    done: {
      screen: string;
      command: string;
      result: "shown" | "failed";
      detail?: Record<string, unknown>;
      showing?: Showing;
    },
    signal?: AbortSignal,
  ) => Promise<void>;
  screenSend?: (
    send: {
      screen: string;
      to: string;
      action: string;
      args: Record<string, unknown>;
    },
    signal?: AbortSignal,
  ) => Promise<SendAnswer>;
  screenList?: (
    screen: string,
    signal?: AbortSignal,
  ) => Promise<ScreenSummary[]>;
  screenRename?: (
    screen: string,
    name: string,
    signal?: AbortSignal,
  ) => Promise<void>;
  screenStop?: () => void;
}

export function normalizeRecords(
  nodes: GraphNode[],
  edges: GraphPage["edges"],
) {
  const mapping = new Map(
    nodes.map((n) => [
      n.id,
      [
        n.resource.identity.provider,
        n.resource.identity.account,
        n.resource.identity.externalId,
      ]
        .map(encodeURIComponent)
        .join(":"),
    ]),
  );
  return {
    nodes: nodes.map((n) => ({ ...n, id: mapping.get(n.id)! })),
    edges: edges.map((e) => ({
      ...e,
      id: identityKey(e.origin),
      source: mapping.get(e.source) ?? e.source,
      target: mapping.get(e.target) ?? e.target,
      evidence: e.evidence.map((v) => ({
        ...v,
        resourceId: mapping.get(v.resourceId) ?? v.resourceId,
      })),
    })),
    mapping,
  };
}
