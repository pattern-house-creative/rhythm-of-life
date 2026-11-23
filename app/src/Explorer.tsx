import { lazy, Suspense, useEffect, useMemo, useRef, useState } from "react";
import {
  Activity,
  ArrowLeft,
  ArrowRight,
  Brain,
  Crosshair,
  FileText,
  Folder,
  GitBranch,
  Hash,
  FilePlus,
  Image as ImageIcon,
  Info,
  Layers,
  Lightbulb,
  Pin,
  PinOff,
  Sparkle,
  Search,
  SlidersHorizontal,
  Sparkles,
  StickyNote,
  X,
  ExternalLink,
  LayoutDashboard,
  Download,
} from "lucide-react";
import type { LucideIcon } from "lucide-react";
import type { DemoState } from "./model";
import { filterNodes } from "./model";
import type { KnowledgeController } from "./useKnowledge";
import {
  graphWindow,
  rankNodes,
  clusterColor,
  sourceName,
  canonicalView,
  autoLinked,
  topicLinked,
  pluginLinked,
  similarLinked,
  shownLink,
  isFolder,
  isProjectStar,
  primaryGroup,
  viewLimit,
  linkLabel,
  replacedBy,
  replacements,
  workLabel,
  workOf,
} from "./graphView";
import { Constellation } from "./Constellation";
import { useMapPartShown } from "./parts/mapParts";
import { DataView } from "./DataView";
import { displayTitle } from "./graph";

import {
  drivePreviewUrl,
  healthPanel,
  identityKey,
  isDataStar,
  isSavedRecord,
  memoryText,
  neighborhood,
  noteLinks,
  safeSourceUrl,
  starSource,
  tracePath,
} from "./knowledge";
import type { GraphEdge, GraphNode } from "./knowledge";
import { drawsOn, isDashboardStar } from "./dashboards";
import { Markdown } from "./markdown";
import { originalPath, uploadAccept, uploadable } from "./documents";
import type { DocumentOriginal } from "./documents";
import DocumentView from "./DocumentView";
import { ShowOn } from "./ShowOn";
import type { ShowOnScreens } from "./ShowOn";
import { DashboardActions } from "./DashboardActions";
import MemoryView, {
  nativeMemoryId,
  nativeProjectId,
  useMemoriesServed,
} from "./MemoryView";
import type { BriefTarget } from "./memoryScreen";
import { DismissedList } from "./Dismissals";
import { useKeptDismissals } from "./keptDismissals";
import type { DashboardScreen } from "./DashboardActions";
import type { DashboardParts } from "./DashboardView";
import { recallReason } from "./recall";
import type { Recalled, RecallResult } from "./recall";

// A dashboard's page, its frame and the app's fonts load only when one opens.
const DashboardView = lazy(() => import("./DashboardView"));
import "./Explorer.css";

// What a record is, in plain words, and the icon that marks it in lists.
const kinds: Record<string, [string, LucideIcon]> = {
  document: ["Document", FileText],
  memory: ["Memory", Brain],
  project: ["Project", Layers],
  theme: ["Topic", Hash],
  decision: ["Decision", Lightbulb],
  note: ["Note", StickyNote],
  record: ["File", ImageIcon],
};
const folder = (n: GraphNode) => isFolder(n) && !n.provenance.demo;
const kindName = (n: GraphNode) =>
  isDashboardStar(n)
    ? "Dashboard"
    : folder(n)
      ? "Folder"
      : isDataStar(n)
        ? "Readings"
        : (kinds[n.type]?.[0] ?? n.type);
function KindIcon({ node }: { node: GraphNode }) {
  const Icon = isDashboardStar(node)
    ? LayoutDashboard
    : folder(node)
      ? Folder
      : isProjectStar(node)
        ? Sparkle
        : isDataStar(node)
          ? Activity
          : (kinds[node.type]?.[1] ?? FileText);
  return (
    <Icon
      size={15}
      aria-hidden="true"
      className="kind-icon"
      style={{ color: clusterColor(primaryGroup(node)?.id) }}
    />
  );
}
// The combined search's stars in the search panel, best first, each saying why it was found
// and what matched, then what was found that has no star here yet (counted, never named), or
// that the search did not answer this time (the title matches still show).
function RecallList({
  answer,
  stars,
  onChoose,
}: {
  answer: Recalled | "failed";
  stars: { result: RecallResult; star: GraphNode }[];
  onChoose: (id: string) => void;
}) {
  if (answer === "failed")
    return (
      <p className="recall-status" role="status">
        Search by words and meaning is unavailable right now; the title matches
        are below.
      </p>
    );
  return (
    <section className="recall-results" aria-label="Found by words and meaning">
      <span className="eyebrow">BY WORDS AND MEANING</span>
      {answer.notices.map((n) => (
        <p key={n} className="recall-status" role="status">
          {n}
        </p>
      ))}
      {stars.length ? (
        <ul className="item-rows" aria-label="Found by words and meaning">
          {stars.map(({ result, star }) => (
            <li key={star.id}>
              <button className="row-main" onClick={() => onChoose(star.id)}>
                <KindIcon node={star} />
                <span className="row-title">{displayTitle(star.title)}</span>
                <small className="row-relation">
                  {recallReason(result.why)}
                </small>
              </button>
              {result.excerpt && (
                <p className="recall-excerpt">
                  {result.excerpt}
                  {result.truncated ? "…" : ""}
                </p>
              )}
            </li>
          ))}
        </ul>
      ) : (
        <p className="recall-status">
          Nothing on the map was found by words or meaning.
        </p>
      )}
      {answer.offMap > 0 && (
        <p className="recall-status">
          {answer.offMap === 1
            ? "1 more found has no star on the map yet."
            : `${answer.offMap} more found have no star on the map yet.`}
        </p>
      )}
    </section>
  );
}
// A link's type on screen: its plain words only while the engine serves the
// memories (SB-P4-11), so an install with every setting off reads as before.
const linkWordsFor = (type: string, words: boolean, fromSource = true) =>
  words ? linkLabel(type, fromSource) : type.replaceAll("_", " ");
// A membership link reads from the side you are on: a file's folder or a
// folder's file, a memory's project or a project's memory. Links Second Brain
// made itself say so, as do stars found alike in meaning and other suggestions.
// While the engine serves the memories (its memory.tools on, `words`), upstream's
// link types read in their plain words from the side you are on ("replaces",
// "replaced by"; SB-P4-11, linkLabel); otherwise, and Rhythm's own always, as
// before: the type with spaces for underscores.
const relationName = (e: GraphEdge, from: GraphNode, words: boolean) =>
  drawsOn(e)
    ? e.source === from.id
      ? "reads"
      : "dashboard"
    : e.type === "in_folder"
      ? e.source === from.id
        ? "folder"
        : "in this folder"
      : e.type === "in_project"
        ? e.source === from.id
          ? "project"
          : "in this project"
        : similarLinked(e)
          ? "similar in meaning"
          : `${linkWordsFor(e.type, words, e.source === from.id)}${autoLinked(e) ? " · auto" : e.verification !== "verified" ? " · suggested" : ""}`;
// What the explorer follows and draws outside Discover: every link nobody
// disputed, the unconfirmed ones dashed. Trace follows confirmed links only.
const linkable = (e: GraphEdge) => shownLink(e);
// A plugin's own link (SB-P4-13) names the plugin as the source's name reads.
const autoBy = (e: GraphEdge) =>
  topicLinked(e)
    ? "Linked automatically by topic"
    : pluginLinked(e)
      ? `Suggested by the ${sourceName(pluginLinked(e)!)} plugin`
      : "Linked automatically by Second Brain";
// A memory's truth on its star (SB-P4-11): a fact a newer memory replaced says
// it is no longer true and leads to what replaced it (or says that one is not
// on the map yet); the newer memory names what it replaced. Nothing for any
// other star, so a map without replaced facts reads as before.
function Truth({
  node,
  nodes,
  choose,
}: {
  node: GraphNode;
  nodes: GraphNode[];
  choose: (id: string) => void;
}) {
  const by = replacedBy(node);
  const newer = by ? nodes.find((n) => n.id === by) : undefined;
  const replaced = replacements(nodes).get(node.id) ?? [];
  if (!by && !replaced.length) return null;
  return (
    <div className="star-truth" role="note">
      {by && (
        <p className="caution replaced-note">
          No longer true: a newer memory replaced it. It stays on the map,
          faded, so you can see what changed.
        </p>
      )}
      {by &&
        (newer ? (
          <button className="secondary-action" onClick={() => choose(newer.id)}>
            Replaced by: {newer.title}{" "}
            <ArrowRight size={14} aria-hidden="true" />
          </button>
        ) : (
          <p className="quiet-note">What replaced it is not on the map yet.</p>
        ))}
      {replaced.map((old) => (
        <button
          key={old.id}
          className="secondary-action"
          onClick={() => choose(old.id)}
        >
          Replaces: {old.title} <ArrowRight size={14} aria-hidden="true" />
        </button>
      ))}
    </div>
  );
}
// "low" is only for moving around the sky: the sheet drops to a strip so
// the sky has the screen, and comes back when a star is chosen.
type Sheet = "low" | "peek" | "half" | "full";
// Phone sheet heights, as shares of the screen.
const sheetHeights: Record<Sheet, number> = {
  low: 0.13,
  peek: 0.36,
  half: 0.6,
  full: 0.9,
};
const phoneSheet = () =>
  window.matchMedia?.("(max-width: 760px) and (min-height: 501px)").matches ??
  false;
const panelNames = {
  glucose: "Glucose",
  training: "Training",
  recovery: "Recovery",
} as const;
type HealthPanel = keyof typeof panelNames;
// Stars lit together until one is chosen: an insight's trail, or a map part's.
export interface Trail {
  title: string;
  stars: { id: string; title: string }[];
  map?: boolean;
}

export default function Explorer({
  knowledge,
  selected: requestedSelected,
  onSelect,
  close,
  paused,
  state,
  onOpenPanel,
  trail: initialTrail,
  reader,
  onRebuild,
  request,
  onState,
  showOn,
  dashboardScreen,
  dashboardParts,
  behind = false,
  nativeReader = false,
  onOpenOriginal,
  automaticLinks = true,
  workItems = true,
}: {
  knowledge: KnowledgeController;
  selected: string | null;
  onSelect: (id: string | null) => void;
  close: () => void;
  paused: boolean;
  state: DemoState;
  // Opens a health tile's panel; a matching data star leads there.
  onOpenPanel?: (panel: HealthPanel) => void;
  // An insight's trail: its stars, lit and framed together until one is
  // chosen; clearing the choice comes back to them. A map part's Explore
  // lights its stars the same way (`map`).
  trail?: Trail | null;
  // A star to open in the reader straight away, as a corner card's dashboard.
  reader?: string | null;
  // Reads the map again and asks the engine to rebuild it (after a document is added).
  onRebuild?: () => void;
  // A command for this screen (LiveScreen.tsx), new each time its nonce is: a star in Focus,
  // clearing any search so its panel shows; a dashboard in the reader; or a search, filled in.
  // A dashboard's map part asks the same way for its stars, lit as a trail. The Memories view may be asked
  // for at one of the brief's lists (memories), and leaving that list goes back to Home. (Home draws no brief
  // since 2026-11-09, D-257, so no screen asks this now.)
  request?: {
    nonce: number;
    star?: string;
    reader?: string;
    search?: string;
    trail?: Trail;
    memories?: BriefTarget;
  } | null;
  // What the explorer shows now: its star, its reader and the active search.
  onState?: (state: {
    selected: string | null;
    reader: string | null;
    query: string;
  }) => void;
  // The other open screens, and showing a star on one of them.
  showOn?: ShowOnScreens;
  // A dashboard in the reader full screen, or as this screen's Home.
  dashboardScreen?: DashboardScreen;
  // A dashboard's native parts in the reader: its map, tiles and cards (App.tsx).
  dashboardParts?: DashboardParts;
  // A full screen covers the explorer: its keys are the full screen's.
  behind?: boolean;
  // Whether the sky draws the dashed links ("Automatic links on the sky", Sources & settings).
  // Off, the star in focus still draws all of its own, Discover (which reviews them) draws them
  // all, and the panel lists them whatever it says.
  nativeReader?: boolean;
  onOpenOriginal?: (id: string) => void;
  automaticLinks?: boolean;
  // Whether the sky draws work items' stars ("Work items on the sky", Sources & settings).
  workItems?: boolean;
}) {
  const { page, accept, session } = knowledge;
  // The Memories view (SB-P3-13, MemoryView.tsx): only while the engine serves it (its memory.tools on). It opens where
  // the reader does, at the native memory chosen on the map if there is one; a reader opened meanwhile sits over it.
  // While it is served, links read in their plain words too (SB-P4-11, relationName).
  const memoryClient = session?.memories;
  const memoriesServed = useMemoriesServed(memoryClient);
  const selected = page.nodes.some((n) => n.id === requestedSelected)
    ? requestedSelected
    : null;
  const [browseCount, setBrowseCount] = useState(50);
  // A dashboard’s map part in the reader rests this sky behind it, as Home’s
  // sky rests under the explorer (parts/mapParts.ts).
  const partShown = useMapPartShown();
  const [mode, setMode] = useState<"focus" | "trace" | "discover">("focus");
  const [depth, setDepth] = useState(1),
    [query, setQuery] = useState(""),
    [source, setSource] = useState("all"),
    [type, setType] = useState("all");
  const [target, setTarget] = useState(""),
    [explainId, setExplainId] = useState<string | null>(null),
    [documentId, setDocumentId] = useState<string | null>(reader ?? null);
  // On phones the details sheet rests low over the sky and can be pulled up.
  const [sheet, setSheet] = useState<Sheet>("peek");
  const [searchOpen, setSearchOpen] = useState(false);
  const searchInput = useRef<HTMLInputElement>(null),
    searchToggle = useRef<HTMLButtonElement>(null),
    readerHeading = useRef<HTMLHeadingElement>(null);
  // A pull on the sheet's grip: where it started and whether it moved.
  const pull = useRef<{
    id: number;
    y: number;
    h: number;
    moved: boolean;
  } | null>(null);
  const pulled = useRef(false);
  const [memory, setMemory] = useState<{
    id: string;
    status: "loading" | "ready" | "unavailable";
    text?: string;
    modifiedAt?: string | null;
    // A document added as a file: its original, which the reader shows instead of the text.
    original?: DocumentOriginal | null;
  } | null>(null);
  const knownIds = useMemo(
    () => new Set(page.nodes.map((n) => n.id)),
    [page.nodes],
  );
  const explain = page.edges.find(
    (e) =>
      e.id === explainId && knownIds.has(e.source) && knownIds.has(e.target),
  );
  const setExplain = (edge: GraphEdge | null) => setExplainId(edge?.id ?? null);
  const [busy, setBusy] = useState(false),
    [notice, setNotice] = useState(""),
    [failure, setFailure] = useState(false);
  const [cursors, setCursors] = useState<
    Record<string, Record<string, string | null>>
  >({});
  const [dismissed, setDismissed] = useState<string[]>([]);
  // Discover's Dismiss kept on the engine while it serves the memories (SB-P4-05, keptDismissals.ts):
  // a dismissed suggestion then stays off the map after a reload and a rebuild, and Discover lists
  // what was dismissed with a way to show it again. Otherwise null, and Dismiss is this screen's.
  const kept = useKeptDismissals(
    memoriesServed ? memoryClient : undefined,
    mode === "discover",
    () => void knowledge.refresh(),
  );
  const [searchPages, setSearchPages] = useState<
    Record<string, Record<string, string | null>>
  >({});
  // The combined search (SB-P2-10, recall.ts): the last question's stars found by words and
  // meaning, with why, while the engine serves it. Once it says it doesn't, this session asks no
  // more, and the search is the title search it always was. An answer that could not say
  // (retry) shows nothing and asks again next time, unless this session has seen it on: only
  // then does a search it could not answer say so.
  const [recalled, setRecalled] = useState<{
    query: string;
    answer: Recalled | "failed";
  } | null>(null);
  const recallOff = useRef(false);
  const recallSeen = useRef(false);
  const abort = useRef<AbortController | null>(null),
    panel = useRef<HTMLElement>(null),
    heading = useRef<HTMLHeadingElement>(null);
  // The preview states are the demo's: a live install's sky always shows (the app
  // marks its own state "error" while it is cleared, offline or signed out).
  const blocked =
    !!session?.demo && ["empty", "error", "loading"].includes(state);
  const node = page.nodes.find((n) => n.id === selected);
  const path = useMemo(
    () =>
      selected && target
        ? tracePath(selected, target, page.edges, page.nodes)
        : null,
    [selected, target, page],
  );
  const distances = useMemo(
    () =>
      selected ? neighborhood(selected, page.edges, depth, linkable) : null,
    [selected, page.edges, depth],
  );
  const reach = useMemo(
    () => (selected ? neighborhood(selected, page.edges, 2, linkable) : null),
    [selected, page.edges],
  );
  // A source is the provider a star's records came from: CSV readings and added documents
  // filter as CSV and Documents, though the engine keeps their stars.
  const filtered = useMemo(
    () =>
      (filterNodes(page.nodes, query, "all", type) as GraphNode[]).filter(
        (n) => source === "all" || starSource(n) === source,
      ),
    [page.nodes, query, source, type],
  );
  const filteredView = Boolean(query || source !== "all" || type !== "all");
  // The combined search's answer for the search shown now: its stars still on the map, best
  // first, under the same source and type filters as the title matches.
  const shownRecall =
    recalled && filteredView && recalled.query === query.trim()
      ? recalled.answer
      : null;
  const recallStars = useMemo(
    () =>
      shownRecall && shownRecall !== "failed"
        ? shownRecall.results.flatMap((result) => {
            const star = page.nodes.find((n) => n.id === result.id);
            return star &&
              (source === "all" || starSource(star) === source) &&
              filterNodes([star], "", "all", type).length
              ? [{ result, star }]
              : [];
          })
        : [],
    [shownRecall, page.nodes, source, type],
  );
  // Every provider with a star on the map, by its display name; an unknown id reads as itself.
  const providers = useMemo(
    () =>
      [...new Set(page.nodes.map(starSource))]
        .map((id) => [id, sourceName(id)] as const)
        .sort((a, b) => a[1].localeCompare(b[1])),
    [page.nodes],
  );
  const [trail, setTrail] = useState(initialTrail ?? null);
  const trailIds = useMemo(
    () =>
      (trail?.stars ?? []).map((s) => s.id).filter((id) => knownIds.has(id)),
    [trail, knownIds],
  );
  const trailView = !!trail && !filteredView && mode === "focus" && !selected;
  // The whole sky stays in place while focusing or searching: a selection or
  // a search brightens what matches and the camera moves to it, so nothing is
  // re-laid out by a tap or a keystroke.
  const eligible = useMemo(
    () => (blocked ? [] : page.nodes),
    [blocked, page.nodes],
  );
  const sky = useMemo(
    () =>
      graphWindow(
        { nodes: eligible, edges: page.edges },
        viewLimit,
        knowledge.pins,
      ),
    [eligible, page.edges, knowledge.pins],
  );
  // What a search lights: the combined search's stars first, when it has any, then the title
  // matches, each once.
  const matched = useMemo(
    () =>
      recallStars.length
        ? [
            ...new Set([
              ...recallStars.map((s) => s.star.id),
              ...filtered.map((n) => n.id),
            ]),
          ]
        : filtered.map((n) => n.id),
    [recallStars, filtered],
  );
  const required = useMemo(
    () =>
      filteredView
        ? matched.slice(0, viewLimit)
        : mode === "trace" && path
          ? path
          : selected
            ? [selected]
            : trailView
              ? trailIds
              : [],
    [filteredView, matched, mode, path, selected, trailView, trailIds],
  );
  // Only a very large loaded graph can leave the subject out of the sky; then
  // it is windowed around the subject instead.
  const windowed = useMemo(
    () =>
      required.every((id) => sky.nodes.some((n) => n.id === id))
        ? sky
        : graphWindow(
            { nodes: eligible, edges: page.edges },
            viewLimit,
            knowledge.pins,
            required,
          ),
    [sky, required, eligible, page.edges, knowledge.pins],
  );
  const visible = windowed.nodes;
  // Automatic (dashed) links on this sky: as Sources & settings says, but always in Discover,
  // which is where they are reviewed.
  const automaticDrawn = automaticLinks || mode === "discover";
  const graphEdges = useMemo(
    () =>
      windowed.edges.filter(
        (e) =>
          !dismissed.includes(e.id) && (mode === "discover" || linkable(e)),
      ),
    [windowed.edges, mode, dismissed],
  );
  const highlighted = useMemo(
    () =>
      filteredView
        ? matched
        : mode === "trace" && path
          ? path
          : selected
            ? [...(distances?.keys() ?? [])]
            : trailView && trailIds.length
              ? trailIds
              : undefined,
    [
      filteredView,
      matched,
      mode,
      path,
      selected,
      distances,
      trailView,
      trailIds,
    ],
  );
  const matchKey = filteredView ? (highlighted ?? []).join("|") : "";
  const copies = useMemo(() => canonicalView(page.nodes).copies, [page.nodes]);
  const suggestions = page.edges.filter(
    (e) =>
      ["suggested", "inferred", "unverified"].includes(e.verification) &&
      !dismissed.includes(e.id) &&
      knownIds.has(e.source) &&
      knownIds.has(e.target),
  );
  // One row per neighbour: two notes that link each other are listed once, by a confirmed link
  // when there is one, with each way they are related.
  const connected = useMemo(() => {
    const rows = new Map<
      string,
      { edge: GraphEdge; next: GraphNode; relations: string[] }
    >();
    if (!node) return [];
    for (const e of page.edges) {
      if (!linkable(e) || (e.source !== node.id && e.target !== node.id))
        continue;
      const id = e.source === node.id ? e.target : e.source;
      const next = page.nodes.find((n) => n.id === id);
      if (!next) continue;
      const relation = relationName(e, node, memoriesServed);
      const row = rows.get(id);
      if (!row) rows.set(id, { edge: e, next, relations: [relation] });
      else {
        if (!row.relations.includes(relation)) row.relations.push(relation);
        if (
          row.edge.verification !== "verified" &&
          e.verification === "verified"
        )
          row.edge = e;
      }
    }
    return [...rows.values()];
  }, [node, page.edges, page.nodes, memoriesServed]);
  const sourceDocument = page.nodes.find((n) => n.id === documentId);
  const drivePreview = sourceDocument ? drivePreviewUrl(sourceDocument) : null;
  const preview = nativeReader ? null : drivePreview;
  const originalLink = sourceDocument ? safeSourceUrl(sourceDocument) : null;
  const externalDriveFile = nativeReader && !!drivePreview && !!originalLink;
  const canOpenInBrowser =
    externalDriveFile &&
    new URLSearchParams(window.location.search).get("openOriginal") === "1";
  // A memory or a record saved on the engine has no original to embed, so its current text is read
  // from the source while it is open, held only in this state and dropped when it closes.
  const readText = session?.text;
  // Only memories, notes and saved records have text to read; a project has none.
  const readable =
    sourceDocument &&
    (memoryText(sourceDocument) || isSavedRecord(sourceDocument));
  const reading =
    sourceDocument && readable && !sourceDocument.provenance.demo && readText
      ? sourceDocument.id
      : null;
  const fromEngine = !!sourceDocument && isSavedRecord(sourceDocument);
  // Where a memory's or note's text is read from, and what to call it.
  const origin =
    sourceDocument?.provenance.sourceId === "notes"
      ? "the notes folder"
      : sourceName(sourceDocument?.provenance.sourceId ?? "");
  const textKind = sourceDocument?.type === "note" ? "note" : "memory";
  // A dashboard opens in the reader too: its page, fed what its feeds hold now.
  const dashboardStar =
    sourceDocument && isDashboardStar(sourceDocument) ? sourceDocument : null;
  const dataStar =
    sourceDocument &&
    isDataStar(sourceDocument) &&
    !sourceDocument.provenance.demo
      ? sourceDocument
      : null;
  useEffect(() => {
    if (!reading || !readText) return;
    const controller = new AbortController();
    readText(reading, controller.signal).then(
      (read) =>
        setMemory(
          read
            ? { id: reading, status: "ready", ...read }
            : { id: reading, status: "unavailable" },
        ),
      () => {
        if (!controller.signal.aborted)
          setMemory({ id: reading, status: "unavailable" });
      },
    );
    return () => {
      controller.abort();
      setMemory(null);
    };
  }, [reading, readText]);
  const opened = !reading
    ? null
    : memory?.id === reading
      ? memory
      : { id: reading, status: "loading" as const };
  // A document added as a file opens as that file (its pages, rendered Markdown or text).
  const original =
    opened?.status === "ready" && opened.original && session?.original
      ? opened.original
      : null;
  // A note's text, once read (or the demo's own), reads as Markdown, its links leading to stars.
  const noteText =
    sourceDocument?.type !== "note"
      ? null
      : opened
        ? opened.status === "ready"
          ? (opened.text ?? null)
          : null
        : sourceDocument.resource.content;
  const noteLink = useMemo(
    () =>
      sourceDocument && noteText !== null
        ? noteLinks(sourceDocument, page.nodes, page.edges)
        : undefined,
    [sourceDocument, noteText, page.nodes, page.edges],
  );
  const choose = (id: string) => {
    setSheet("peek");
    setSearchOpen(false);
    abort.current?.abort();
    setBusy(false);
    onSelect(id);
    setExplain(null);
    setDocumentId(null);
    setNotice("");
    setQuery("");
    setSource("all");
    setType("all");
  };
  // A note's link opens the star it names, and a linked note in the reader.
  const follow = (id: string) => {
    if (!knownIds.has(id)) return;
    choose(id);
    if (page.nodes.find((n) => n.id === id)?.type === "note") setDocumentId(id);
  };
  useEffect(() => {
    if (panel.current) panel.current.scrollTop = 0;
    // The Memories view keeps the focus it holds: a count on Home opens it with its list's heading focused in this same first
    // render, which the panel's heading would otherwise take back (SB-P6-02). Without the view nothing here changes.
    if (document.activeElement?.closest(".memory-view")) return;
    heading.current?.focus({ preventScroll: true });
  }, [selected, mode, explain]);
  // Adding a document: whether the engine takes them, then one file at a time. Its star
  // joins the map at the rebuild the engine queues, and opens in the reader once it does.
  const [uploads, setUploads] = useState<{ maxBytes: number } | null>(null);
  const [upload, setUpload] = useState<{
    state: "sending" | "waiting" | "ready" | "failed";
    text: string;
    id?: string;
  } | null>(null);
  const fileInput = useRef<HTMLInputElement>(null);
  const [memories, setMemories] = useState<{
    start: string | null;
    // A native project's star opens the view at that project (SB-P6-01).
    project?: string | null;
    // Opened at one of the brief's lists, and leaving it goes back to Home.
    brief?: BriefTarget;
    nonce: number;
  } | null>(null);
  const leaveMemories = () => {
    const home = memories?.brief !== undefined;
    setMemories(null);
    if (home) close();
    else memoriesLeft.current = true;
  };
  const memoryBack = useRef<(() => boolean) | null>(null);
  // Closing the Memories view leaves focus where it came from: a star's Open in Memories while it is still there, else the
  // dock's button (SB-P6-02). The pressed button itself is kept, since a browser may give a tapped button no focus.
  const memoriesDock = useRef<HTMLButtonElement>(null);
  const memoriesFrom = useRef<HTMLElement | null>(null);
  const memoriesLeft = useRef(false);
  useEffect(() => {
    if (!session?.uploads) return;
    const controller = new AbortController();
    session.uploads(controller.signal).then(
      (u) => !controller.signal.aborted && setUploads(u.available ? u : null),
      () => {},
    );
    return () => controller.abort();
  }, [session]);
  const addDocument = async (file: File) => {
    if (!session?.upload || !uploads || upload?.state === "sending") return;
    if (!uploadable(file.name) || file.size === 0)
      return setUpload({
        state: "failed",
        text: "Only PDF, .txt and .md files can be added.",
      });
    if (file.size > uploads.maxBytes)
      return setUpload({
        state: "failed",
        text: `Files up to ${Math.floor(uploads.maxBytes / 1024 / 1024)} MiB can be added.`,
      });
    setUpload({ state: "sending", text: `Adding ${file.name}…` });
    try {
      const answer = await session.upload(file);
      if (!answer.ok)
        return setUpload({ state: "failed", text: answer.reason });
      const { graphId: id, title } = answer.document;
      setUpload({
        state: "waiting",
        id,
        text: `Added ${title}. It joins the map within a couple of minutes and opens here.`,
      });
      onRebuild?.();
    } catch {
      setUpload({
        state: "failed",
        text: "The document could not be added right now. Try again later.",
      });
    }
  };
  useEffect(() => {
    if (upload?.state !== "waiting" || !upload.id || !knownIds.has(upload.id))
      return;
    const title = page.nodes.find((n) => n.id === upload.id)?.title ?? "It";
    // eslint-disable-next-line react/set-state-in-effect
    setUpload({
      state: "ready",
      id: upload.id,
      text: `${title} is on the map.`,
    });
    // Opened straight away unless something else is being read.
    if (!documentId) {
      onSelect(upload.id);
      setDocumentId(upload.id);
    }
  }, [upload, knownIds, page.nodes, documentId, onSelect]);
  useEffect(() => {
    if (!upload || upload.state === "sending" || upload.state === "waiting")
      return;
    const timer = setTimeout(() => setUpload(null), 12_000);
    return () => clearTimeout(timer);
  }, [upload]);
  // A command's request, applied once per nonce, whether the explorer opened for it or was
  // open already: back to Focus, with nothing else in the way of what it asked for. Applied
  // while rendering, so the first view drawn (and reported) is already the one asked for.
  const nonce = request?.nonce;
  const [answered, setAnswered] = useState<number | undefined>(undefined);
  if (request && nonce !== answered) {
    setAnswered(nonce);
    setBusy(false);
    setNotice("");
    setMode("focus");
    setExplain(null);
    setTrail(request.trail ?? null);
    setSheet("peek");
    setSource("all");
    setType("all");
    setQuery(request.search ?? "");
    setSearchOpen(request.search !== undefined);
    setDocumentId(request.reader ?? null);
    if (request.memories)
      setMemories({
        start: null,
        brief: request.memories,
        nonce: (memories?.nonce ?? 0) + 1,
      });
  }
  // A search or neighbours on their way are for what was shown before.
  useEffect(() => {
    if (nonce !== undefined) abort.current?.abort();
  }, [nonce]);
  const shownQuery = filteredView ? query.trim() : "";
  useEffect(() => {
    onState?.({ selected, reader: documentId, query: shownQuery });
  }, [onState, selected, documentId, shownQuery]);
  const readerOpen = Boolean(documentId);
  const readerWasOpen = useRef(false);
  useEffect(() => {
    if (readerOpen) readerHeading.current?.focus({ preventScroll: true });
    else if (readerWasOpen.current)
      heading.current?.focus({ preventScroll: true });
    readerWasOpen.current = readerOpen;
  }, [readerOpen]);
  useEffect(() => () => abort.current?.abort(), []);
  useEffect(() => {
    abort.current?.abort();
    // Cancel an in-flight external query and its continuation when a refresh completes.
    // eslint-disable-next-line react/set-state-in-effect
    setBusy(false);
    setCursors({});
    setSearchPages({});
    setRecalled(null);
  }, [knowledge.epoch]);
  const load = async () => {
    if (!selected || !session) return;
    abort.current?.abort();
    const controller = new AbortController();
    abort.current = controller;
    const id = selected;
    setBusy(true);
    setNotice("Looking for source-linked neighbors…");
    try {
      const result = await session.retrieve(
        "neighbors",
        id,
        cursors[id],
        controller.signal,
      );
      accept(result);
      setCursors((c) => ({ ...c, [id]: result.cursors }));
      setNotice(
        result.issues.length
          ? "A source returned incomplete or unavailable results. Other source results are retained. Retry when ready."
          : result.completeness === "partial"
            ? Object.values(result.cursors).some(Boolean)
              ? "More connections are available. Load the next page to continue."
              : "Partial neighborhood; no continuation cursor. Refresh or search to explore further."
            : session.demo
              ? "All available neighbors for this item have been retrieved from the sample adapters."
              : "Available neighbors within the authorized source view have been retrieved.",
      );
    } catch (e) {
      if (!(e instanceof DOMException && e.name === "AbortError"))
        setNotice(
          "Could not load connections. Your current graph is still available.",
        );
    } finally {
      if (!controller.signal.aborted) setBusy(false);
    }
  };
  const reset = () => {
    setQuery("");
    setSource("all");
    setType("all");
  };
  const searchSources = async () => {
    if (!query.trim() || !session) return;
    abort.current?.abort();
    const controller = new AbortController();
    abort.current = controller;
    const q = query.trim();
    setBusy(true);
    // The combined search runs beside the title search, while the engine serves it.
    const recalling =
      session.recall && !recallOff.current
        ? session.recall(q, controller.signal).catch(() => null)
        : null;
    try {
      try {
        const result = await session.retrieve(
          "search",
          q,
          searchPages[q],
          controller.signal,
        );
        accept(result);
        setSearchPages((p) => ({ ...p, [q]: result.cursors }));
        onSelect(null);
        setNotice(
          result.issues.length
            ? session.demo
              ? "One sample source is unavailable; available results are shown."
              : "Results are limited to the authorized graph subset; source coverage is partial."
            : result.completeness === "partial"
              ? Object.values(result.cursors).some(Boolean)
                ? "More source matches are available. Search again for the next page."
                : "Bounded source results; no continuation cursor was provided."
              : `${result.nodes.length} matches retrieved. ${session?.demo ? "Search is bounded to these sample sources." : "Only the currently authorized graph subset was searched."}`,
        );
      } catch (e) {
        if (!(e instanceof DOMException && e.name === "AbortError"))
          setNotice("Source search failed. Loaded items are still available.");
      }
      // Its stars join the sky and are listed with why, after the title search's.
      const answer = recalling && (await recalling);
      if (answer && !controller.signal.aborted) {
        if (!answer.available) {
          if (!answer.retry) recallOff.current = true;
          else if (recallSeen.current)
            setRecalled({ query: q, answer: "failed" });
        } else if (answer.failed) {
          recallSeen.current = true;
          setRecalled({ query: q, answer: "failed" });
        } else {
          recallSeen.current = true;
          accept(
            answer.page,
            answer.recall.results.map((r) => r.id),
          );
          setRecalled({ query: q, answer: answer.recall });
        }
      }
    } finally {
      if (!controller.signal.aborted) setBusy(false);
    }
  };
  const edgeName = (e: GraphEdge) =>
    `${page.nodes.find((n) => n.id === e.source)?.title ?? "Unavailable item"} ${e.directed ? "→" : "↔"} ${page.nodes.find((n) => n.id === e.target)?.title ?? "Unavailable item"}`;
  const done =
    selected &&
    cursors[selected] &&
    Object.values(cursors[selected]).length > 0 &&
    Object.values(cursors[selected]).every((c) => c === null);
  // Steps back one layer: a source, an explanation, a search, the selection.
  const clear = () => {
    if (documentId) setDocumentId(null);
    else if (memories) {
      if (!memoryBack.current?.()) leaveMemories();
    } else if (explain) setExplain(null);
    else if (filteredView || searchOpen) {
      reset();
      setSearchOpen(false);
      if (document.activeElement === searchInput.current)
        setTimeout(() => {
          if (searchToggle.current?.offsetParent) searchToggle.current.focus();
        });
    } else if (selected) onSelect(null);
    else return false;
    return true;
  };
  const pinned = Boolean(node && knowledge.pins.includes(node.id));
  // On phones the reader covers everything; what it covers is set aside.
  const [phone, setPhone] = useState(phoneSheet);
  useEffect(() => {
    const query = window.matchMedia?.(
      "(max-width: 760px) and (min-height: 501px)",
    );
    if (!query) return;
    const update = () => setPhone(query.matches);
    query.addEventListener?.("change", update);
    return () => query.removeEventListener?.("change", update);
  }, []);
  // A request for one of the brief's lists comes from what knows the engine serves them, so its list shows at once.
  const memoriesShown = Boolean(
    memories &&
    memoryClient &&
    (memoriesServed || memories.brief) &&
    !sourceDocument,
  );
  const covered = phone && (readerOpen || memoriesShown);
  useEffect(() => {
    if (memoriesShown || !memoriesLeft.current) return;
    memoriesLeft.current = false;
    const from = memoriesFrom.current;
    memoriesFrom.current = null;
    if (from?.isConnected) from.focus({ preventScroll: true });
    if (!from || document.activeElement !== from)
      memoriesDock.current?.focus({ preventScroll: true });
  }, [memoriesShown]);
  const nodeView = !explain && !filteredView && mode === "focus" && !!node;
  const secondDegree = page.nodes.filter((n) => reach?.get(n.id) === 2);
  const opensAt = safeSourceUrl;
  // Dragging the grip follows the finger, then settles at the nearest height;
  // pulled below its lowest, the sheet returns to the ambient view.
  const settle = (cancelled: boolean) => {
    const start = pull.current,
      sheetEl = panel.current;
    pull.current = null;
    if (!start?.moved || !sheetEl) return;
    const h = sheetEl.getBoundingClientRect().height;
    sheetEl.classList.remove("dragging");
    sheetEl.style.height = "";
    pulled.current = true;
    setTimeout(() => (pulled.current = false));
    if (cancelled) return;
    const lowest = sheet === "low" ? "low" : "peek";
    // From the low strip, closing takes a pull almost off the screen.
    if (h < innerHeight * (lowest === "low" ? 0.05 : sheetHeights.peek * 0.7))
      return close();
    const heights: Sheet[] =
      lowest === "low"
        ? ["low", "peek", "half", "full"]
        : ["peek", "half", "full"];
    const nearest = heights.reduce((a, b) =>
      Math.abs(innerHeight * sheetHeights[b] - h) <
      Math.abs(innerHeight * sheetHeights[a] - h)
        ? b
        : a,
    );
    setSheet(nearest);
  };
  // The pull is followed on the window, so it keeps going when the finger or
  // pointer leaves the grip, and taps on the grip's buttons still land.
  const grip = {
    onClickCapture: (e: React.MouseEvent) => {
      if (!pulled.current) return;
      e.stopPropagation();
      e.preventDefault();
    },
    onPointerDown: (e: React.PointerEvent) => {
      if (!phoneSheet() || !panel.current || pull.current) return;
      pull.current = {
        id: e.pointerId,
        y: e.clientY,
        h: panel.current.getBoundingClientRect().height,
        moved: false,
      };
      const move = (event: PointerEvent) => {
        const begun = pull.current,
          sheetEl = panel.current;
        if (!begun || !sheetEl || event.pointerId !== begun.id) return;
        const dy = event.clientY - begun.y;
        if (!begun.moved && Math.abs(dy) > 8) {
          begun.moved = true;
          sheetEl.classList.add("dragging");
        }
        if (begun.moved)
          sheetEl.style.height = `${Math.max(60, Math.min(innerHeight * 0.94, begun.h - dy))}px`;
      };
      const end = (event: PointerEvent) => {
        if (event.pointerId !== pull.current?.id) return;
        window.removeEventListener("pointermove", move);
        window.removeEventListener("pointerup", end);
        window.removeEventListener("pointercancel", end);
        settle(event.type === "pointercancel");
      };
      window.addEventListener("pointermove", move);
      window.addEventListener("pointerup", end);
      window.addEventListener("pointercancel", end);
    },
  };
  const clearRef = useRef(clear),
    behindRef = useRef(behind);
  useEffect(() => {
    clearRef.current = clear;
    behindRef.current = behind;
  });
  // Escape steps back a layer at a time before leaving for the ambient sky,
  // wherever focus is (even on the page after a click on plain text), unless
  // a full screen covers it or a dashboard's part is held, which Escape puts
  // back (PartsGrid.tsx).
  useEffect(() => {
    const key = (e: KeyboardEvent) => {
      if (
        e.key === "Escape" &&
        !behindRef.current &&
        !document.documentElement.hasAttribute("data-arranging") &&
        clearRef.current()
      )
        e.stopPropagation();
    };
    window.addEventListener("keydown", key, true);
    return () => window.removeEventListener("keydown", key, true);
  }, []);
  return (
    <section
      className={`explorer${dashboardStar ? " dashboard-open" : ""}`}
      aria-label="Knowledge explorer"
      data-sheet={sheet}
    >
      <div className="explorer-sky" inert={covered}>
        <div className="exploration-context" aria-live="polite">
          <span>
            {filteredView
              ? query.trim()
                ? `Search: ${query.trim()}`
                : "Filtered sky"
              : mode === "trace"
                ? "Follow a thread"
                : mode === "discover"
                  ? "Possible connections"
                  : node
                    ? displayTitle(node.title)
                    : trailView
                      ? trail?.map
                        ? "Stars from a map"
                        : "Insight trail"
                      : "Choose a starting point"}
          </span>
          <small>
            {blocked
              ? "Sample unavailable"
              : filteredView
                ? `${filtered.length} ${filtered.length === 1 ? "match" : "matches"} lit · select one to explore`
                : mode === "trace"
                  ? "Verified links in the loaded graph · traversed in either direction"
                  : mode === "discover"
                    ? "Dashed lines are unverified suggestions"
                    : node
                      ? `${depth === 1 ? "Direct" : "Direct + second-degree"} connections · ${Math.max(0, (distances?.size ?? 1) - 1)} linked`
                      : trailView
                        ? `${trailIds.length} ${trailIds.length === 1 ? "star" : "stars"} lit · select one to explore`
                        : `${visible.length} ${visible.length === 1 ? "star" : "stars"} · select one or browse the list`}
          </small>
        </div>
        <Constellation
          pinned={knowledge.pins}
          // Nothing read: a read that never succeeded failed; one that did
          // found no stars this person can view.
          emptyMessage={
            !session?.demo && !page.nodes.length
              ? knowledge.lastRefresh
                ? "Rhythm found no records you can view."
                : "Rhythm couldn’t load these records."
              : undefined
          }
          emptyTitle={
            !session?.demo && !page.nodes.length && knowledge.lastRefresh
              ? "No stars to show"
              : undefined
          }
          loading={!session?.demo && !knowledge.lastRefresh && !knowledge.error}
          cameraKey={
            mode +
            ":" +
            (selected ?? "") +
            ":" +
            target +
            ":" +
            depth +
            ":" +
            matchKey +
            (trailView ? ":trail" : "")
          }
          nodes={visible}
          edges={graphEdges}
          automaticLinks={automaticDrawn}
          workItems={workItems}
          // Focus is calm, as Home's sky is. Trace keeps every line (a thread
          // can run through a folder) and Discover reviews them all.
          calm={mode === "focus"}
          selected={selected}
          onSelect={choose}
          // On phones, moving around the sky lowers the sheet out of the way;
          // the camera frames records above the sheet's resting height.
          onPanStart={phone ? () => setSheet("low") : undefined}
          insetBottom={
            phone ? innerHeight * (sheetHeights.peek - sheetHeights.low) : 0
          }
          interactive
          healthFocus={false}
          paused={paused || partShown}
          highlightedIds={highlighted}
          focusIds={highlighted}
          path={
            mode === "trace" && !filteredView ? (path ?? undefined) : undefined
          }
          onExplain={(e) => {
            setExplain(e as GraphEdge);
            setDocumentId(null);
          }}
          onBackground={() => {
            // Empty sky steps back: first the selection, then to the ambient view.
            if (!clear() && mode === "focus") close();
          }}
        />
        <div className="sky-legend">
          <span>
            <i />{" "}
            {session?.demo
              ? "Explicit sample link"
              : "Source-backed relationship"}
          </span>
          {mode === "discover" && (
            <span className="suggestion-legend">┄ Suggested association</span>
          )}
          {!automaticDrawn && (
            <span>Automatic links hidden · a chosen star shows its own</span>
          )}
          {!workItems && visible.some((n) => workOf(n)) && (
            <span>Work items hidden · a chosen one still shows</span>
          )}
          <span>
            {session?.demo ? "Synthetic sources" : "Authorized source subset"} ·{" "}
            {visible.length} shown / {page.nodes.length} loaded ·{" "}
            {page.completeness === "partial"
              ? "partial coverage"
              : session?.demo
                ? "current sample"
                : "loaded source view"}
          </span>
        </div>
      </div>
      <header
        inert={covered}
        className={`explorer-dock${searchOpen || filteredView ? " searching" : ""}`}
      >
        <button
          className="icon-button"
          onClick={close}
          aria-label="Back to Home"
          title="Back to Home (Esc)"
        >
          <X size={18} />
        </button>
        <div className="explorer-modes" aria-label="Graph exploration mode">
          {(
            [
              ["focus", Crosshair, "Focus"],
              ["trace", GitBranch, "Trace"],
              ["discover", Sparkles, "Discover"],
            ] as const
          ).map(([id, Icon, label]) => (
            <button
              key={id}
              aria-pressed={mode === id}
              onClick={() => {
                setMode(id);
                setExplain(null);
                setDocumentId(null);
              }}
            >
              <Icon size={14} />
              {label}
            </button>
          ))}
        </div>
        <div className="dock-search">
          <button
            ref={searchToggle}
            className="icon-button dock-search-toggle"
            aria-label="Search"
            aria-expanded={searchOpen || filteredView}
            onClick={() => {
              setSearchOpen((open) => !open);
              setTimeout(() => searchInput.current?.focus());
            }}
          >
            <Search size={17} />
          </button>
          <div className="search-field">
            <Search size={15} />
            <input
              ref={searchInput}
              aria-label="Search knowledge"
              placeholder="Search the sky…"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
            />
            {query && (
              <button aria-label="Clear search" onClick={() => setQuery("")}>
                <X size={15} />
              </button>
            )}
          </div>
          <details className="explorer-filters">
            <summary aria-label="Filters" title="Filter by source or type">
              <SlidersHorizontal size={15} />
            </summary>
            <div>
              <select
                aria-label="Filter by source"
                value={source}
                onChange={(e) => setSource(e.target.value)}
              >
                <option value="all">All sources</option>
                {providers.map(([id, name]) => (
                  <option key={id} value={id}>
                    {name}
                  </option>
                ))}
              </select>
              <select
                aria-label="Filter by type"
                value={type}
                onChange={(e) => setType(e.target.value)}
              >
                <option value="all">All types</option>
                {[
                  "theme",
                  "project",
                  "decision",
                  "document",
                  "memory",
                  "note",
                  "record",
                ].map((t) => (
                  <option key={t}>{t}</option>
                ))}
              </select>
            </div>
          </details>
          {memoriesServed && (
            <button
              ref={memoriesDock}
              className="icon-button dock-memories"
              aria-label="Memories"
              title="Memories: save one, read, edit and tidy"
              aria-pressed={memoriesShown}
              onClick={() => {
                const start = nativeMemoryId(sourceDocument ?? node);
                const project = nativeProjectId(sourceDocument ?? node);
                setDocumentId(null);
                memoriesFrom.current = null;
                setMemories((now) =>
                  now && memoriesShown
                    ? null
                    : { start, project, nonce: (now?.nonce ?? 0) + 1 },
                );
              }}
            >
              <Brain size={16} />
            </button>
          )}
          {uploads && (
            <>
              <button
                className="icon-button dock-upload"
                aria-label="Add a document"
                title="Add a document: PDF, text or Markdown"
                disabled={upload?.state === "sending"}
                onClick={() => {
                  // A phone leaves the page for its file picker; the app must not reload into a new version
                  // meanwhile (appUpdates.ts), or the chosen file is lost. Back in the page, picked or not, it may.
                  const root = document.documentElement;
                  const back = () => {
                    root.removeAttribute("data-picking-file");
                    window.removeEventListener("focus", back);
                  };
                  root.setAttribute("data-picking-file", "");
                  window.addEventListener("focus", back);
                  fileInput.current?.click();
                }}
              >
                <FilePlus size={16} />
              </button>
              <input
                ref={fileInput}
                type="file"
                accept={uploadAccept}
                hidden
                aria-hidden="true"
                tabIndex={-1}
                onChange={(e) => {
                  document.documentElement.removeAttribute("data-picking-file");
                  const file = e.target.files?.[0];
                  e.target.value = "";
                  if (file) void addDocument(file);
                }}
              />
            </>
          )}
        </div>
      </header>
      {upload && (
        <p
          className={`upload-status${upload.state === "failed" ? " failed" : ""}`}
          role="status"
          // The view stays open while the document is on its way to the map.
          data-keep-open={
            upload.state === "sending" ||
            upload.state === "waiting" ||
            undefined
          }
          inert={covered}
        >
          <span>{upload.text}</span>
          {upload.state === "ready" &&
            upload.id &&
            documentId !== upload.id && (
              <button
                onClick={() => {
                  choose(upload.id!);
                  setDocumentId(upload.id!);
                }}
              >
                Open
              </button>
            )}
          {upload.state !== "sending" && (
            <button aria-label="Dismiss" onClick={() => setUpload(null)}>
              <X size={13} />
            </button>
          )}
        </p>
      )}
      <aside
        // An open dashboard takes the full width; the drawer steps aside.
        inert={covered || Boolean(dashboardStar)}
        ref={panel}
        className="explorer-panel"
        aria-label="Selected knowledge details"
      >
        {/* On phones the sheet's top is its grip: drag it between heights,
            or below its lowest to return to the ambient view. */}
        <div className="sheet-grip" {...grip}>
          <button
            className="panel-handle"
            aria-label={sheet === "full" ? "Lower details" : "Raise details"}
            onClick={() => {
              if (pulled.current) return;
              setSheet((s) =>
                s === "low" || s === "full"
                  ? "peek"
                  : s === "peek"
                    ? "half"
                    : "full",
              );
            }}
          >
            <i />
          </button>
          {nodeView && node && (
            <div className="detail-top">
              <span className="eyebrow item-kind">
                <KindIcon node={node} /> {kindName(node)} ·{" "}
                {sourceName(node.provenance.sourceId)}
              </span>
              <span className="detail-actions">
                <button
                  className="icon-button"
                  aria-pressed={pinned}
                  aria-label={
                    pinned ? "Unpin from overview" : "Pin to overview"
                  }
                  title={pinned ? "Unpin from overview" : "Pin to overview"}
                  onClick={() => knowledge.togglePin(node.id)}
                >
                  {pinned ? <PinOff size={16} /> : <Pin size={16} />}
                </button>
                <button
                  className="icon-button"
                  aria-label="Clear focus"
                  title="Clear focus"
                  onClick={() => {
                    setDocumentId(null);
                    onSelect(null);
                  }}
                >
                  <X size={17} />
                </button>
              </span>
            </div>
          )}
        </div>
        {explain ? (
          <>
            <button className="back-link" onClick={() => setExplain(null)}>
              <ArrowLeft size={14} /> Back to connections
            </button>
            <span className={`verification ${explain.verification}`}>
              {explain.verification === "verified"
                ? session?.demo
                  ? "Explicit sample relationship"
                  : "Verified source relationship"
                : explain.verification === "disputed"
                  ? "Disputed relationship"
                  : autoLinked(explain)
                    ? autoBy(explain)
                    : "Unverified suggestion"}
            </span>
            <h2 ref={heading} tabIndex={-1} className="edge-heading">
              {edgeName(explain)}
            </h2>
            <span className="relation-type">
              {linkWordsFor(explain.type, memoriesServed)} ·{" "}
              {explain.verification} ·{" "}
              {explain.directed ? "directed" : "undirected"}
            </span>
            <p>{explain.relationship}</p>
            <h3>Evidence</h3>
            {explain.evidence.map((e, i) => (
              <blockquote key={i}>
                <p>{e.excerpt}</p>
                <cite>{e.locator}</cite>
                <button onClick={() => setDocumentId(e.resourceId)}>
                  {session?.demo ? "Read sample source" : "View source record"}{" "}
                  <ArrowRight size={13} aria-hidden="true" />
                </button>
              </blockquote>
            ))}
            <p className="source-meta">
              Retrieved {explain.retrievedAt.slice(0, 10)} ·{" "}
              {session?.demo ? "fictional evidence" : "source evidence"}.{" "}
              {topicLinked(explain)
                ? "Linked because the record names this data's topic; nobody confirmed the link."
                : pluginLinked(explain) && autoLinked(explain)
                  ? `The ${sourceName(pluginLinked(explain)!)} plugin linked these records on its own; nobody confirmed the link.`
                  : autoLinked(explain)
                    ? "Second Brain linked these on its own; nobody confirmed the link."
                    : explain.verification !== "verified"
                      ? "Thematic overlap is a reason to investigate, not proof of a relationship."
                      : session?.demo
                        ? "“Explicit” describes the synthetic relationship register, not a verified personal fact."
                        : "The relationship is backed by the cited source and its recorded verification."}
            </p>
          </>
        ) : filteredView ? (
          <>
            <span className="eyebrow">SEARCH</span>
            <h2 ref={heading} tabIndex={-1}>
              {filtered.length
                ? `${filtered.length} ${filtered.length === 1 ? "match" : "matches"}`
                : recallStars.length
                  ? `${recallStars.length} found by words and meaning`
                  : "No stars match your search"}
            </h2>
            {!filtered.length && !recallStars.length && (
              <p>
                Try another word, or clear the filters to see the whole sky.
              </p>
            )}
            <div className="result-actions">
              <button className="text-button" onClick={reset}>
                Reset filters
              </button>
            </div>
            {query.trim() && (
              <div className="source-search-action">
                <button disabled={busy} onClick={searchSources}>
                  {busy
                    ? "Searching…"
                    : session?.demo
                      ? "Search sample sources"
                      : "Search authorized graph"}
                </button>
                <span role="status">
                  {notice ||
                    (session?.demo
                      ? "Search beyond the loaded graph across each source."
                      : "Search the loaded map. Broader source discovery is not available.")}
                </span>
              </div>
            )}
            {shownRecall && (
              <RecallList
                answer={shownRecall}
                stars={recallStars}
                onChoose={choose}
              />
            )}
            {shownRecall && filtered.length > 0 && (
              <span className="eyebrow recall-eyebrow">BY TITLE</span>
            )}
            <ul className="item-rows" aria-label="Matching items">
              {filtered.slice(0, browseCount).map((n) => (
                <li key={n.id}>
                  <button className="row-main" onClick={() => choose(n.id)}>
                    <KindIcon node={n} />
                    <span className="row-title">{displayTitle(n.title)}</span>
                    <small className="row-relation">{kindName(n)}</small>
                  </button>
                </li>
              ))}
            </ul>
            {filtered.length > browseCount && (
              <button
                className="text-button"
                onClick={() => setBrowseCount((n) => n + 50)}
              >
                Show 50 more matches
              </button>
            )}
          </>
        ) : (
          <>
            {mode === "trace" && (
              <div className="trace-controls">
                <span className="eyebrow">TRACE A CONNECTION</span>
                <h2 ref={heading} tabIndex={-1}>
                  From one idea to another
                </h2>
                <label>
                  From
                  <select
                    aria-label="Trace from"
                    value={selected ?? ""}
                    onChange={(e) => choose(e.target.value)}
                  >
                    <option value="">Choose a starting item</option>
                    {page.nodes.map((n) => (
                      <option key={n.id} value={n.id}>
                        {n.title}
                      </option>
                    ))}
                  </select>
                </label>
                <label>
                  To
                  <select
                    aria-label="Trace to"
                    value={target}
                    onChange={(e) => setTarget(e.target.value)}
                  >
                    <option value="">Choose a destination</option>
                    {page.nodes.map((n) => (
                      <option key={n.id} value={n.id}>
                        {n.title}
                      </option>
                    ))}
                  </select>
                </label>
                {selected &&
                  target &&
                  (path ? (
                    <div className="trace-route" aria-label="Connection path">
                      <p>
                        {path.length - 1} {path.length === 2 ? "link" : "links"}{" "}
                        · shortest loaded path
                      </p>
                      {path.slice(0, browseCount).map((id, i) => (
                        <div key={id}>
                          <button onClick={() => choose(id)}>
                            {page.nodes.find((n) => n.id === id)?.title}
                          </button>
                          {i < path.length - 1 && (
                            <button
                              className="path-step"
                              onClick={() =>
                                setExplain(
                                  page.edges.find(
                                    (e) =>
                                      e.verification === "verified" &&
                                      ((e.source === id &&
                                        e.target === path[i + 1]) ||
                                        (e.target === id &&
                                          e.source === path[i + 1])),
                                  )!,
                                )
                              }
                            >
                              Explain this link <ArrowRight size={13} />
                            </button>
                          )}
                        </div>
                      ))}
                      {path.length > browseCount && (
                        <button onClick={() => setBrowseCount((n) => n + 50)}>
                          Show next path steps
                        </button>
                      )}
                    </div>
                  ) : (
                    <p role="status">
                      No verified path in the loaded graph. Load more
                      connections from either endpoint; this does not mean no
                      relationship exists.
                    </p>
                  ))}
              </div>
            )}
            {mode === "discover" && (
              <>
                <span className="eyebrow">DISCOVER</span>
                <h2 ref={heading} tabIndex={-1}>
                  Worth a closer look
                </h2>
                <p>
                  {session?.demo
                    ? "Shared ideas across sources. These sample suggestions are kept separate from explicit links and excluded from traces."
                    : "Stars alike in meaning, and data stars linked by topic. These suggestions are kept separate from explicit links and excluded from traces."}
                </p>
                {suggestions.slice(0, browseCount).map((e) => (
                  <article className="suggestion" key={e.id}>
                    <span className="verification suggested">
                      {autoLinked(e) ? autoBy(e) : "Unverified suggestion"}
                    </span>
                    <h3>{edgeName(e)}</h3>
                    <p>{e.relationship}</p>
                    <div>
                      <button onClick={() => setExplain(e)}>
                        Inspect evidence <ArrowRight size={14} />
                      </button>
                      <button
                        onClick={() => {
                          setDismissed([...dismissed, e.id]);
                          kept?.dismiss(e);
                        }}
                      >
                        Dismiss
                      </button>
                    </div>
                  </article>
                ))}
                {suggestions.length > browseCount && (
                  <button onClick={() => setBrowseCount((n) => n + 50)}>
                    Show more suggestions
                  </button>
                )}
                {!suggestions.length && (
                  <p>No suggestions left in the loaded graph.</p>
                )}
                {kept?.note && (
                  <p className="quiet-note" role="status">
                    {kept.note}
                  </p>
                )}
                {kept && (
                  <DismissedList
                    kept={kept}
                    words={(type) =>
                      type === "similar_to"
                        ? "similar in meaning"
                        : linkWordsFor(type, memoriesServed)
                    }
                    onShown={(ids) =>
                      setDismissed((d) => d.filter((id) => !ids.includes(id)))
                    }
                  />
                )}
              </>
            )}
            {mode === "focus" &&
              (node ? (
                <>
                  <h2 ref={heading} tabIndex={-1}>
                    {node.title}
                  </h2>
                  {/* A replaced fact (SB-P4-11) says so before anything else, and leads to what replaced it. */}
                  <Truth node={node} nodes={page.nodes} choose={choose} />
                  {/* A work item (migration 079) says what it is and where it stands. */}
                  {workOf(node) && (
                    <p className="quiet-note star-work" role="note">
                      Work item: {workLabel(workOf(node)!)}
                      {workOf(node)!.priority
                        ? `, priority ${workOf(node)!.priority}`
                        : ""}
                    </p>
                  )}
                  {/* One clear way to the thing itself, right under its name. */}
                  <div className="item-actions">
                    {isDashboardStar(node) ? (
                      <button
                        className="primary-action"
                        onClick={() => setDocumentId(node.id)}
                      >
                        Open dashboard{" "}
                        <ArrowRight size={15} aria-hidden="true" />
                      </button>
                    ) : isProjectStar(node) ? null : folder(node) &&
                      opensAt(node) ? (
                      <a
                        className="primary-action"
                        href={opensAt(node)!}
                        target="_blank"
                        rel="noopener noreferrer"
                      >
                        Open folder in {sourceName(node.provenance.sourceId)}{" "}
                        <ExternalLink size={15} />
                      </a>
                    ) : isDataStar(node) && !node.provenance.demo ? (
                      // A data star leads to its health panel when there is one, and
                      // always to its readings.
                      <>
                        {healthPanel(node) && onOpenPanel ? (
                          <button
                            className="primary-action"
                            onClick={() => onOpenPanel(healthPanel(node)!)}
                          >
                            Open {panelNames[healthPanel(node)!]} panel{" "}
                            <ArrowRight size={15} aria-hidden="true" />
                          </button>
                        ) : null}
                        <button
                          className={
                            healthPanel(node) && onOpenPanel
                              ? "secondary-action"
                              : "primary-action"
                          }
                          onClick={() => setDocumentId(node.id)}
                        >
                          Show readings{" "}
                          <ArrowRight size={15} aria-hidden="true" />
                        </button>
                      </>
                    ) : (
                      <>
                        <button
                          className="primary-action"
                          onClick={() => setDocumentId(node.id)}
                        >
                          {node.provenance.demo
                            ? "Read sample source"
                            : drivePreviewUrl(node)
                              ? nativeReader ? "View original file link" : "Preview original"
                              : memoryText(node) && session?.text
                                ? node.type === "note"
                                  ? "Read note"
                                  : "Read full memory"
                                : isSavedRecord(node) && session?.text
                                  ? node.resource.identity.account ===
                                    "documents"
                                    ? "Open document"
                                    : "Read saved text"
                                  : "View source record"}{" "}
                          <ArrowRight size={15} aria-hidden="true" />
                        </button>
                        {memoriesServed &&
                          (nativeMemoryId(node) || nativeProjectId(node)) && (
                            <button
                              className="secondary-action"
                              onClick={(e) => {
                                setDocumentId(null);
                                memoriesFrom.current = e.currentTarget;
                                setMemories((now) => ({
                                  start: nativeMemoryId(node),
                                  project: nativeProjectId(node),
                                  nonce: (now?.nonce ?? 0) + 1,
                                }));
                              }}
                            >
                              Open in Memories
                            </button>
                          )}
                        {opensAt(node) && !(nativeReader && drivePreviewUrl(node)) && (
                          <a
                            className="secondary-action"
                            href={opensAt(node)!}
                            target="_blank"
                            rel="noopener noreferrer"
                          >
                            Open in {sourceName(node.provenance.sourceId)}{" "}
                            <ExternalLink size={14} />
                          </a>
                        )}
                      </>
                    )}
                    {showOn && (
                      <ShowOn key={node.id} star={node} screens={showOn} />
                    )}
                  </div>
                  {/* The dashboards that read this star, one tap from it. */}
                  {page.edges
                    .filter((e) => drawsOn(e) && e.target === node.id)
                    .map((e) => page.nodes.find((n) => n.id === e.source))
                    .filter((d): d is GraphNode => Boolean(d))
                    .map((d) => (
                      <button
                        key={d.id}
                        className="secondary-action dashboard-action"
                        onClick={() => setDocumentId(d.id)}
                      >
                        <LayoutDashboard size={14} aria-hidden="true" />{" "}
                        {d.title} <ArrowRight size={14} aria-hidden="true" />
                      </button>
                    ))}
                  {node.overview && (
                    <p className="item-summary">{node.overview}</p>
                  )}
                  {copies.get(node.id)?.map((copy) => (
                    <button
                      className="secondary-action"
                      key={copy.id}
                      onClick={() => choose(copy.id)}
                    >
                      Saved copy · {sourceName(copy.provenance.sourceId)} ·{" "}
                      {copy.resource.sourceModifiedAt?.slice(0, 10)}
                    </button>
                  ))}
                  {/* A data star's readings are read when opened, never kept. */}
                  {node.resource.representation &&
                    node.resource.representation !== "original" &&
                    !isDataStar(node) && (
                      <p className="caution">
                        {node.resource.representation === "archive"
                          ? "Archived copy"
                          : "Indexed copy"}{" "}
                        · retained separately from its canonical source.
                      </p>
                    )}
                  <h3>
                    Connected <span>{connected.length}</span>
                  </h3>
                  {connected.length ? (
                    <ul className="item-rows" aria-label="Connected items">
                      {connected
                        .slice(0, browseCount)
                        .map(({ edge: e, next, relations }) => {
                          const relation = relations.join(" · ");
                          return (
                            <li key={next.id}>
                              <button
                                className="row-main"
                                onClick={() => choose(next.id)}
                              >
                                <KindIcon node={next} />
                                <span className="row-title">{next.title}</span>
                                <small
                                  className="row-relation"
                                  aria-hidden="true"
                                >
                                  {relation}
                                </small>
                              </button>
                              <button
                                className="row-why icon-button"
                                aria-label={`Why connected: ${next.title} (${relation})`}
                                title="Why connected?"
                                onClick={() => setExplain(e)}
                              >
                                <Info size={15} />
                              </button>
                            </li>
                          );
                        })}
                    </ul>
                  ) : (
                    <p className="quiet-note">
                      No linked records yet. Explore more connections to look in
                      the sources.
                    </p>
                  )}
                  {connected.length > browseCount && (
                    <button
                      className="text-button"
                      onClick={() => setBrowseCount((n) => n + 50)}
                    >
                      Show more connected items
                    </button>
                  )}
                  {(secondDegree.length > 0 || depth === 2) && (
                    <button
                      className="depth-switch"
                      aria-pressed={depth === 2}
                      onClick={() => setDepth(depth === 2 ? 1 : 2)}
                    >
                      <span aria-hidden="true" />
                      Include one step further ({secondDegree.length})
                    </button>
                  )}
                  {depth === 2 && (
                    <ul className="item-rows" aria-label="One step further">
                      {secondDegree.slice(0, browseCount).map((n) => (
                        <li key={n.id}>
                          <button
                            className="row-main"
                            onClick={() => choose(n.id)}
                          >
                            <KindIcon node={n} />
                            <span className="row-title">{n.title}</span>
                          </button>
                        </li>
                      ))}
                    </ul>
                  )}
                  {depth === 2 && secondDegree.length > browseCount && (
                    <button
                      className="text-button"
                      onClick={() => setBrowseCount((n) => n + 50)}
                    >
                      Show more second-degree items
                    </button>
                  )}
                </>
              ) : trailView && trail ? (
                <>
                  <span className="eyebrow">
                    {trail.map ? "FROM A MAP" : "INSIGHT TRAIL"}
                  </span>
                  <h2 ref={heading} tabIndex={-1}>
                    {trail.title}
                  </h2>
                  <p>
                    {trail.map
                      ? "The stars this map shows. Tap one to explore it."
                      : "The stars this insight draws on. Tap one to explore it."}
                  </p>
                  <ul
                    className="item-rows"
                    aria-label={
                      trail.map ? "Stars from a map" : "Insight trail"
                    }
                  >
                    {trail.stars.map((s) => {
                      const n = page.nodes.find((x) => x.id === s.id);
                      return (
                        <li key={s.id} className={n ? "" : "trail-missing"}>
                          <button
                            className="row-main"
                            disabled={!n}
                            onClick={() => n && choose(n.id)}
                          >
                            {n && <KindIcon node={n} />}
                            <span className="row-title">
                              {n ? displayTitle(n.title) : s.title}
                            </span>
                            <small className="row-relation">
                              {n ? kindName(n) : "not in the loaded sky"}
                            </small>
                          </button>
                        </li>
                      );
                    })}
                  </ul>
                  <button
                    className="text-button"
                    onClick={() => setTrail(null)}
                  >
                    See the whole sky
                  </button>
                </>
              ) : (
                <>
                  <span className="eyebrow">START EXPLORING</span>
                  <h2 ref={heading} tabIndex={-1}>
                    Find the thread
                  </h2>
                  <p>
                    Tap a star, or start from one of these. Search finds
                    anything in the sky.
                  </p>
                  <ul className="item-rows starting-points">
                    {rankNodes(page.nodes, page.edges, knowledge.pins)
                      .filter((n) => n.type !== "theme")
                      .slice(0, 4)
                      .map((n) => (
                        <li key={n.id}>
                          <button
                            className="row-main"
                            onClick={() => choose(n.id)}
                          >
                            <KindIcon node={n} />
                            <span className="row-title">{n.title}</span>
                          </button>
                        </li>
                      ))}
                  </ul>
                  <details className="knowledge-list">
                    <summary>Browse all {filtered.length} items</summary>
                    {filtered.length > browseCount && (
                      <button onClick={() => setBrowseCount((n) => n + 50)}>
                        Show 50 more items
                      </button>
                    )}
                    <div aria-label="Accessible knowledge list">
                      {filtered.slice(0, browseCount).map((n) => (
                        <button key={n.id} onClick={() => choose(n.id)}>
                          {n.title}
                          <small>
                            {sourceName(n.provenance.sourceId)} · {kindName(n)}
                          </small>
                        </button>
                      ))}
                    </div>
                  </details>
                  <div className="graph-refresh">
                    <button
                      disabled={knowledge.busy || busy}
                      onClick={() => void knowledge.refresh()}
                    >
                      {" "}
                      {knowledge.busy ? "Refreshing…" : "Refresh sources"}
                    </button>
                    {Object.values(page.discoveryCursors ?? {}).some(
                      Boolean,
                    ) && (
                      <button
                        disabled={knowledge.busy || busy}
                        onClick={() => void knowledge.discoverMore()}
                      >
                        Discover more
                      </button>
                    )}
                    {session?.demo && (
                      <button
                        disabled={knowledge.busy || busy}
                        onClick={() => {
                          session.demo!.advance();
                          void knowledge.refresh();
                        }}
                      >
                        Add sample records
                      </button>
                    )}
                    <span role="status">
                      {knowledge.error ||
                        (knowledge.lastRefresh
                          ? "Checked " +
                            new Date(knowledge.lastRefresh).toLocaleTimeString(
                              [],
                              {
                                hour: "2-digit",
                                minute: "2-digit",
                              },
                            ) +
                            (page.completeness === "partial"
                              ? " · partial source coverage"
                              : " · loaded sources checked")
                          : "Discovering source records…")}
                    </span>
                    <span className="source-key">
                      {[
                        ...new Set(visible.map((n) => n.provenance.sourceId)),
                      ].map((s) => (
                        <span key={s}>{sourceName(s)}</span>
                      ))}
                    </span>
                  </div>
                </>
              ))}
            {node && mode !== "discover" && !isDashboardStar(node) && (
              <div className="progressive-load">
                <button
                  className="load-connections"
                  onClick={load}
                  disabled={busy || Boolean(done)}
                >
                  {busy
                    ? "Exploring…"
                    : done
                      ? "Neighbors loaded"
                      : cursors[node.id]
                        ? "Load more connections"
                        : "Explore more connections"}
                </button>
                <p role="status">
                  {notice ||
                    (session?.demo
                      ? "Retrieve a bounded page from each synthetic source. No background indexing."
                      : "Explore relationships within the authorized source subset.")}
                </p>
                {session?.demo && (
                  <label className="failure-toggle">
                    <input
                      type="checkbox"
                      checked={failure}
                      onChange={(e) => {
                        setFailure(e.target.checked);
                        session?.demo?.setUnavailable(e.target.checked);
                        setCursors({});
                        setSearchPages({});
                        setNotice("");
                      }}
                    />{" "}
                    Simulate Drive unavailable
                  </label>
                )}
              </div>
            )}
            {nodeView && node && (
              <details className="source-details">
                <summary>Source details</summary>
                <div className="source-meta">
                  <span
                    className={
                      node.resource.availability === "stale" ? "caution" : ""
                    }
                  >
                    {node.resource.availability === "stale"
                      ? "Saved revision · may be stale"
                      : node.provenance.demo
                        ? "Synthetic source record"
                        : "Authorized source record"}
                  </span>
                  <br />
                  Modified{" "}
                  {node.resource.sourceModifiedAt?.slice(0, 10) ?? "unknown"} ·
                  retrieved {node.resource.retrievedAt.slice(0, 10)}
                  <p>
                    {identityKey(node.resource.identity)}
                    <br />
                    {node.resource.owner} · {node.resource.access.scope}
                    <br />
                    Revision {node.resource.revision}
                  </p>
                </div>
              </details>
            )}
          </>
        )}
      </aside>
      {/* Documents and memories open in a reader with room to read, beside
          the panel, instead of inside it. */}
      {sourceDocument && (
        <section
          className={`explorer-reader${preview || dashboardStar ? " framed" : ""}${!preview && !reading && !dataStar && !dashboardStar && !sourceDocument.resource.content ? " bare" : ""}`}
          aria-label="Source reader"
        >
          {/* One slim row, so the document below gets the height. */}
          <header className="reader-bar">
            <button
              className="back-link"
              onClick={() => setDocumentId(null)}
              aria-label="Close the reader"
            >
              <ArrowLeft size={15} /> <span className="reader-label">Back</span>
            </button>
            <div className="reader-title">
              <span className="eyebrow">
                {dashboardStar
                  ? sourceDocument.provenance.demo
                    ? "SAMPLE DASHBOARD"
                    : "DASHBOARD"
                  : `${
                      dataStar || fromEngine
                        ? (sourceDocument.groups?.[0]?.label ??
                          sourceName(sourceDocument.provenance.sourceId))
                        : sourceName(sourceDocument.provenance.sourceId)
                    } · ${
                      sourceDocument.provenance.demo
                        ? "SAMPLE SOURCE"
                        : dataStar
                          ? "READINGS"
                          : "SOURCE RECORD"
                    }`}
              </span>
              <h2
                ref={readerHeading}
                tabIndex={-1}
                title={sourceDocument.title}
              >
                {sourceDocument.title}
              </h2>
            </div>
            {originalLink && !externalDriveFile && (
              <a
                className="secondary-action reader-open"
                href={originalLink}
                target="_blank"
                rel="noopener noreferrer"
              >
                <span className="reader-label">Open original</span>{" "}
                <ExternalLink size={14} aria-hidden="true" />
              </a>
            )}
            {original && (
              <a
                className="secondary-action reader-open"
                href={originalPath(original.id, true)}
                download={original.name}
                title={`Download ${original.name}`}
              >
                <span className="reader-label">Download</span>{" "}
                <Download size={14} aria-hidden="true" />
              </a>
            )}
            {dashboardStar && dashboardScreen && (
              <div className="reader-actions">
                <DashboardActions
                  id={dashboardStar.resource.identity.externalId}
                  screen={dashboardScreen}
                  className="secondary-action reader-open"
                  label="reader-label"
                />
              </div>
            )}
          </header>
          {dashboardStar && session?.dashboard ? (
            <Suspense
              fallback={
                <p className="source-content" aria-busy="true">
                  Opening this dashboard…
                </p>
              }
            >
              <DashboardView
                id={dashboardStar.resource.identity.externalId}
                session={session}
                {...dashboardParts}
                onOpenStar={(id) => {
                  // A mark on the page leads back into the sky, to a star on the map.
                  if (knownIds.has(id)) choose(id);
                }}
              />
            </Suspense>
          ) : externalDriveFile ? (
            <div className="reader-text google-file-original">
              {canOpenInBrowser ? (
                <>
                  <a
                    className="secondary-action"
                    href={originalLink}
                    rel="noreferrer"
                    onClick={() => onOpenOriginal?.(sourceDocument.id)}
                  >
                    Open original <ExternalLink size={14} aria-hidden="true" />
                  </a>
                  <p>Use your Google account in the browser to access this file.</p>
                </>
              ) : <p>Google Drive previews require another site. Copy this file link into a browser signed in to Google.</p>}
              <label>
                Original file link
                <input type="url" readOnly value={originalLink} onFocus={(event) => event.currentTarget.select()} />
              </label>
            </div>
          ) : preview ? (
            <iframe
              className="source-preview"
              src={preview}
              title={`${sourceDocument.title} in Google Drive`}
              referrerPolicy="no-referrer"
              sandbox="allow-scripts allow-same-origin allow-popups allow-popups-to-escape-sandbox allow-forms allow-downloads"
            />
          ) : dataStar ? (
            <div className="reader-text">
              <DataView id={dataStar.id} read={session?.data} />
            </div>
          ) : original && session?.original ? (
            <DocumentView
              original={original}
              text={opened?.text ?? ""}
              read={session.original}
              title={sourceDocument.title}
            />
          ) : noteText !== null ? (
            <div className="reader-text">
              <Markdown
                className="source-content"
                text={noteText}
                link={noteLink}
                open={follow}
              />
            </div>
          ) : (
            <div className="reader-text">
              <p
                className="source-content"
                aria-busy={opened?.status === "loading"}
              >
                {opened?.status === "ready"
                  ? opened.text
                  : opened?.status === "loading"
                    ? fromEngine
                      ? "Reading this record from the Rhythm engine…"
                      : `Reading this ${textKind} from ${origin}…`
                    : opened
                      ? fromEngine
                        ? "This record's text is unavailable right now. Try again later."
                        : `This ${textKind}'s text is unavailable right now. Try again later.`
                      : (sourceDocument.resource.content ??
                        (sourceDocument.provenance.demo
                          ? "Source unavailable."
                          : "This graph retains citation metadata, not the document body."))}
              </p>
            </div>
          )}
          {!dashboardStar && !externalDriveFile && (
            <p className="source-meta">
              {sourceDocument.provenance.demo
                ? "Fictional records have no original file or source link."
                : preview
                  ? "Google Drive controls this preview. If it stays blank or asks for sign-in or access, use Open original."
                  : dataStar
                    ? "Read from the Rhythm engine just now: each measurement's latest reading and daily lows, averages and highs for the last 90 days. Not saved by the app."
                    : original
                      ? `The file as it was added, from the Rhythm engine's archive${opened?.modifiedAt ? ` · last changed ${opened.modifiedAt.slice(0, 10)}` : ""}. Not saved by the app.`
                      : opened?.status === "ready"
                        ? `Read from ${fromEngine ? "the Rhythm engine" : origin} just now${opened.modifiedAt ? ` · last ${fromEngine ? "changed" : "edited"} ${opened.modifiedAt.slice(0, 10)}` : ""}. Not saved by the app.`
                        : "The original source controls access to its contents."}
            </p>
          )}
        </section>
      )}
      {memoriesShown && memories && memoryClient && (
        <MemoryView
          key={memories.nonce}
          client={memoryClient}
          start={memories.start}
          startProject={memories.project}
          startBrief={memories.brief}
          back={memoryBack}
          onClose={leaveMemories}
          onSaved={onRebuild}
        />
      )}
    </section>
  );
}
