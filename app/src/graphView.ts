import type { KnowledgeNode, KnowledgeEdge, SourceId } from "./model.js";
import { identityKey, linkType, readable } from "./knowledge.js";
import type { GraphNode, GraphPage } from "./knowledge.js";
import { catalogProvider } from "./instance.js";

// Display names of the providers the app knows. A provider the table lacks reads as a known
// provider's name and its suffix ("carelink-live" is "CareLink live", "carelink-csv" is
// "CareLink CSV"), else as its own id; the engine's catalog provider id, when an install kept an
// older engine's, reads as the Rhythm engine.
export const sourceNames: Record<string, string> = {
  rhythm: "Rhythm engine",
  notes: "Notes",
  csv: "CSV import",
  documents: "Documents",
  drive: "Google Drive",
  "second-brain": "Second Brain",
  carelink: "CareLink",
  garmin: "Garmin",
  nightscout: "Nightscout",
  calendar: "Calendar",
  demo: "Demo",
};
export function sourceName(id: SourceId) {
  if (Object.hasOwn(sourceNames, id)) return sourceNames[id];
  if (id === catalogProvider()) return sourceNames.rhythm;
  const [, base, suffix] = /^([a-z][a-z0-9-]*)-([a-z0-9]+)$/.exec(id) ?? [];
  return base && Object.hasOwn(sourceNames, base)
    ? `${sourceNames[base]} ${suffix === "csv" ? "CSV" : suffix}`
    : id;
}
const palette = ["#74dbbe", "#a8cff3", "#b7a7de", "#93c6c4", "#d8c4a1"];
export function hash(value: string) {
  let h = 2166136261;
  for (const c of value) h = Math.imul(h ^ c.charCodeAt(0), 16777619);
  return h >>> 0;
}
export const clusterColor = (key = "") => palette[hash(key) % palette.length];

// Second Brain lists a memory's scope (Personal or Family) first, then its
// projects sorted by id. A scope holds dozens of unrelated memories, so it is
// too broad to group the sky by: a memory groups by its first project, and a
// memory with none stays beside what it cites. Other sources' facets (the
// fictional sample's topics) are used as they come.
const scopeGroup = /^second-brain:(personal|household)$/;
export const primaryGroup = (n: Pick<KnowledgeNode, "groups">) =>
  n.groups?.find((g) => !scopeGroup.test(g.id));

// Visual groups are navigational summaries, never evidence of a relationship.
// Prefer source facets; otherwise inherit a linked project/theme, then a connected component.
export function clusterGraph(nodes: KnowledgeNode[], edges: KnowledgeEdge[]) {
  const byId = new Map(nodes.map((n) => [n.id, n]));
  const labels = new Map<string, string>();
  const groups = new Map<string, string>();
  const adjacency = new Map(nodes.map((n) => [n.id, [] as string[]]));
  for (const e of edges) {
    if ("verification" in e && e.verification !== "verified") continue;
    if (byId.has(e.source) && byId.has(e.target)) {
      adjacency.get(e.source)!.push(e.target);
      adjacency.get(e.target)!.push(e.source);
    }
  }
  for (const n of nodes) {
    const facet = primaryGroup(n);
    if (facet) {
      groups.set(n.id, facet.id);
      labels.set(facet.id, facet.label);
    } else if (n.type === "project" || n.type === "theme") {
      groups.set(n.id, n.id);
      labels.set(n.id, n.title);
    }
  }
  const queue = [...groups.keys()].sort();
  for (let i = 0; i < queue.length; i++)
    for (const next of adjacency.get(queue[i]) ?? []) {
      if (!groups.has(next)) {
        groups.set(next, groups.get(queue[i])!);
        queue.push(next);
      }
    }
  for (const n of [...nodes].sort((a, b) => a.id.localeCompare(b.id))) {
    if (groups.has(n.id)) continue;
    const key = n.id;
    labels.set(key, n.title);
    groups.set(key, key);
    const pending = [key];
    for (let i = 0; i < pending.length; i++)
      for (const next of adjacency.get(pending[i]) ?? []) {
        if (!groups.has(next)) {
          groups.set(next, key);
          pending.push(next);
        }
      }
  }
  return { groups, labels };
}

export function rankNodes(
  nodes: GraphNode[],
  edges: KnowledgeEdge[],
  pins: string[] = [],
) {
  const degree = new Map<string, number>();
  // A memory a newer one replaced (SB-P4-11) cites nothing for the ranking: what it points to is not lifted for it.
  const types = new Map(
    nodes.map((n) => [
      n.id,
      n.type === "memory" && replacedBy(n) ? "replaced" : n.type,
    ]),
  );
  // Records a current memory points to (or is pointed at by) through a verified link.
  const cited = new Set<string>();
  for (const e of edges) {
    if ("verification" in e && e.verification !== "verified") continue;
    degree.set(e.source, (degree.get(e.source) ?? 0) + 1);
    degree.set(e.target, (degree.get(e.target) ?? 0) + 1);
    if (types.get(e.source) === "memory") cited.add(e.target);
    if (types.get(e.target) === "memory") cited.add(e.source);
  }
  const newest = Math.max(
    0,
    ...nodes.map((n) => Date.parse(n.resource.sourceModifiedAt ?? "") || 0),
  );
  const pinned = new Set(pins);
  // Projects and memories lead, then what memories cite; media files trail.
  // Link counts only order records within those roles, so folders with many
  // files cannot crowd the memories out.
  // A fact a newer memory replaced (SB-P4-11) follows every current memory and
  // what memories cite, so it never takes their place in a bounded view.
  const role = (n: GraphNode) =>
    n.type === "project"
      ? 12
      : n.type === "memory"
        ? replacedBy(n)
          ? 4
          : 10
        : cited.has(n.id)
          ? 6
          : n.type === "record" && n.provenance.sourceId === "drive"
            ? -3
            : 0;
  const score = (n: GraphNode) =>
    (pinned.has(n.id) ? 100 : 0) +
    role(n) +
    Math.log2(1 + (degree.get(n.id) ?? 0)) * 2 +
    4 /
      (1 +
        Math.max(
          0,
          newest - (Date.parse(n.resource.sourceModifiedAt ?? "") || 0),
        ) /
          86400000);
  return nodes
    .filter(readable)
    .sort((a, b) => score(b) - score(a) || hash(a.id) - hash(b.id));
}

export function canonicalView(nodes: GraphNode[]) {
  const originals = new Map(
    nodes
      .filter(
        (n) =>
          readable(n) &&
          (!n.resource.representation ||
            n.resource.representation === "original"),
      )
      .map((n) => [identityKey(n.resource.identity), n.id]),
  );
  const copies = new Map<string, GraphNode[]>();
  const representative = new Map<string, string>();
  for (const n of nodes) {
    const original =
      n.resource.canonicalIdentity &&
      originals.get(identityKey(n.resource.canonicalIdentity));
    if (
      original &&
      original !== n.id &&
      n.resource.representation !== "original"
    ) {
      copies.set(original, [...(copies.get(original) ?? []), n]);
      representative.set(n.id, original);
    }
  }
  return {
    nodes: nodes.filter((n) => !representative.has(n.id)),
    copies,
    representative,
  };
}

// A data star's link to a record that names its topic ("Mentions glucose").
export const topicLinked = (e: KnowledgeEdge) =>
  "evidence" in e &&
  Array.isArray(e.evidence) &&
  e.evidence.some((x: { locator?: string }) => x.locator === "catalog:topic");
// A plugin's own link between its records (SB-P4-13, the engine's
// graph.pluginLinks on): its evidence names the plugin (ingest:<plugin>). The
// plugin's name, or null for any other link; an engine with the setting off
// sends none.
export const pluginLinked = (e: KnowledgeEdge): string | null => {
  if (!("evidence" in e) || !Array.isArray(e.evidence)) return null;
  for (const x of e.evidence as { locator?: string }[]) {
    const plugin = /^ingest:([a-z][a-z0-9-]{0,63})$/.exec(x.locator ?? "")?.[1];
    if (plugin) return plugin;
  }
  return null;
};
// Links made automatically ("inferred") by Second Brain, by a data star's
// topic or by a plugin are shown everywhere, dashed, and never treated as
// confirmed.
export const autoLinked = (e: KnowledgeEdge) =>
  "verification" in e &&
  e.verification !== "verified" &&
  "type" in e &&
  e.type !== "similar_to" &&
  "evidence" in e &&
  Array.isArray(e.evidence) &&
  (topicLinked(e) ||
    pluginLinked(e) !== null ||
    e.evidence.some((x: { locator?: string }) =>
      x.locator?.startsWith("second-brain:"),
    ));
// Two stars the engine found alike in meaning; nobody confirmed the link.
export const similarLinked = (e: KnowledgeEdge) =>
  "type" in e &&
  e.type === "similar_to" &&
  "verification" in e &&
  e.verification !== "verified";
// What the sky draws and a star's panel lists: every link nobody disputed,
// the unconfirmed ones dashed. Trace follows confirmed links only.
export const shownLink = (e: KnowledgeEdge) =>
  !("verification" in e) || e.verification !== "disputed";
// A link the sky draws dashed: any nobody confirmed (similar, topic and plugin
// links, Second Brain's automatic ones, suggested relates_to and follows).
// The one rule for dashing and for "Automatic links on the sky", so a line the
// switch hides is always a dashed one and a dashed one is always hidden.
export const dashedLink = (e: KnowledgeEdge) =>
  "verification" in e && e.verification !== "verified";
// The links a sky draws of those it is given (the owner, 2026-11-09, on the
// sky looking "like a crazy spider web"). With `automatic` off ("Automatic
// links on the sky" in Sources & settings) no dashed link is drawn; on a calm
// sky (Home's, a map part's and the explorer's Focus) no folder or project
// line is drawn at rest. The star in focus always draws all of its own links
// of every kind, so a chosen folder or project draws its members' lines.
// Display only: the map, the layout and a star's panel still read every link.
// With nothing to leave out it answers the same array.
export function drawnLinks<E extends KnowledgeEdge>(
  edges: E[],
  automatic: boolean,
  focus: string | null = null,
  calm = true,
): E[] {
  if (automatic && !calm) return edges;
  return edges.filter(
    (e) =>
      (focus !== null && (e.source === focus || e.target === focus)) ||
      ((automatic || !dashedLink(e)) &&
        !(calm && "type" in e && structural(e.type))),
  );
}
// How a sky draws a line (the owner, 2026-11-09, "quiet lines, bright focus").
// At rest a calm sky's lines are soft threads, thinner and fainter than they
// were; a chosen or hovered star's own lines are lit at least as brightly as
// they were, and every other line dims further. Trace and Discover keep the
// look they had (`calm` false). The colours are the sky's own.
export const lineLook = {
  // As every sky drew them before, and Trace and Discover still do.
  before: {
    width: (strength: number) => 0.65 + strength * 0.65,
    rest: 0.4,
    // A folder or project line, fainter than the links between ideas.
    member: 0.22,
    lit: { width: 2.2, opacity: 0.85 },
    dim: 0.09,
  },
  calm: {
    width: (strength: number) => 0.4 + strength * 0.4,
    rest: 0.24,
    // Drawn only for a chosen star, where it is lit.
    member: 0.24,
    lit: { width: 2.2, opacity: 0.9 },
    dim: 0.05,
  },
} as const;
// A line's width and opacity: lit (the focus's own), dimmed (another line
// while something is in focus) or at rest; `member` for a folder or project
// line.
export function lineStyle(
  strength: number,
  how: { calm: boolean; lit: boolean; dim: boolean; member: boolean },
) {
  const look = how.calm ? lineLook.calm : lineLook.before;
  if (how.lit) return look.lit;
  return {
    width: look.width(strength),
    opacity: how.dim ? look.dim : how.member ? look.member : look.rest,
  };
}
// Second Brain and notes-folder projects arrive as project records: drawn as small stars that
// their memories hang from, as Drive folders hold their files.
export const isProjectStar = (n: Pick<GraphNode, "type" | "provenance">) =>
  n.type === "project" &&
  (n.provenance.sourceId === "second-brain" ||
    n.provenance.sourceId === "notes");
// Membership links (a file in its folder, a memory in its project) are
// structure: drawn faintly.
export const structural = (type: unknown) =>
  type === "in_folder" || type === "in_project";

// Plain words for each link type the engine names (linkTypes; D-030 keeps
// upstream's names at the boundary and the app's own words on screen): what the
// other end is, read from the link's source and from its target. A link with
// no direction reads the same from both. Rhythm's own types read as the
// explorer names them today.
export const linkWords: Readonly<
  Record<string, { source: string; target: string }>
> = {
  relates_to: { source: "related to", target: "related to" },
  supersedes: { source: "replaces", target: "replaced by" },
  caused_by: { source: "caused by", target: "led to" },
  decided: { source: "decided", target: "decided by" },
  about_person: { source: "about", target: "about them" },
  part_of_project: { source: "part of", target: "includes" },
  follows: { source: "follows", target: "followed by" },
  drawn_from: { source: "drawn from", target: "source of" },
  references: { source: "references", target: "referenced by" },
  in_project: { source: "project", target: "in this project" },
  in_folder: { source: "folder", target: "in this folder" },
  about: { source: "about", target: "about" },
  similar_to: { source: "similar in meaning", target: "similar in meaning" },
  copy_of: { source: "copy of", target: "copy of" },
  contains: { source: "contains", target: "part of" },
};
// A type the table lacks is its source's own word, its underscores as spaces.
export function linkName(type: string, fromSource = true) {
  const words = Object.hasOwn(linkWords, type) ? linkWords[type] : undefined;
  return words
    ? fromSource
      ? words.source
      : words.target
    : type.replaceAll("_", " ");
}
// What a link reads as on screen, from one end (SB-P4-11, D-191): upstream's
// eight types in their plain words, from the side the reader is on; Rhythm's
// own types and a source's own word as the explorer has always named them, its
// type with spaces for underscores (the explorer's membership links keep their
// own words beside this).
export const linkLabel = (type: string, fromSource = true) =>
  linkType(type)?.upstream
    ? linkName(type, fromSource)
    : type.replaceAll("_", " ");

// Replaced facts (SB-P4-11; contracts/README.md, "Replaced facts on the map"):
// while the engine's memory.tools is on, a native memory's star that a newer
// memory of its scope replaced carries truth, naming the replacing star. Such a
// star is drawn faded, never hidden, and never as the current fact. Anything
// else, and every star from an engine that sends none, reads as no truth.
export function replacedBy(n: KnowledgeNode | GraphNode): string | null {
  const truth = (n as Partial<GraphNode>).truth as unknown;
  if (!truth || typeof truth !== "object") return null;
  const { state, replacedBy: by } = truth as Record<string, unknown>;
  return state === "replaced" &&
    typeof by === "string" &&
    by.length > 0 &&
    by.length <= 1000 &&
    by !== n.id
    ? by
    : null;
}
// The stars each star replaced, by the replacing star's id, in the order given.
export function replacements(nodes: GraphNode[]) {
  const replaced = new Map<string, GraphNode[]>();
  for (const n of nodes) {
    const by = replacedBy(n);
    if (by) replaced.set(by, [...(replaced.get(by) ?? []), n]);
  }
  return replaced;
}

// Drive folders arrive as theme records; they are drawn as small folder
// stars joined to their files, not as topic themes.
export const isFolder = (n: Pick<GraphNode, "type" | "provenance">) =>
  n.type === "theme" && n.provenance.sourceId === "drive";

// The engine sends at most 300 records per page; every one of them is shown.
export const viewLimit = 300;

// The ambient sky shows every record and every link; suggestions are dashed.
export function ambientWindow(
  page: Pick<GraphPage, "nodes" | "edges">,
  pins: string[] = [],
) {
  return graphWindow(page, viewLimit, pins);
}

export function graphWindow(
  page: Pick<GraphPage, "nodes" | "edges">,
  limit = 48,
  pins: string[] = [],
  required: string[] = [],
) {
  const canonical = canonicalView(page.nodes);
  const mandatory = new Set(required);
  // A specifically opened archive remains inspectable; its provenance is never rewritten.
  const candidates = [
    ...canonical.nodes,
    ...page.nodes.filter(
      (n) => canonical.representative.has(n.id) && mandatory.has(n.id),
    ),
  ];
  const ranked = rankNodes(candidates, page.edges, pins);
  const byId = new Map(ranked.map((n) => [n.id, n]));
  const chosen: GraphNode[] = [],
    ids = new Set<string>();
  const add = (id: string) => {
    const n = byId.get(id);
    if (n && !ids.has(id) && chosen.length < limit) {
      chosen.push(n);
      ids.add(id);
    }
  };
  required.forEach(add);
  pins.forEach(add);
  if (!required.length) {
    const groups = clusterGraph(ranked, page.edges).groups;
    const represented = new Set<string>();
    for (const n of ranked) {
      const key = `${groups.get(n.id)}:${n.provenance.sourceId}`;
      if (!represented.has(key)) {
        add(n.id);
        represented.add(key);
      }
      if (represented.size >= Math.min(8, limit)) break;
    }
    [...ranked]
      .sort((a, b) =>
        (b.resource.sourceModifiedAt ?? "").localeCompare(
          a.resource.sourceModifiedAt ?? "",
        ),
      )
      .slice(0, 4)
      .forEach((n) => add(n.id));
  }
  // Keep context around seeds instead of choosing unrelated high-scoring stars.
  const adjacency = new Map<string, string[]>();
  for (const e of page.edges)
    if (e.verification === "verified") {
      const a = adjacency.get(e.source) ?? [],
        b = adjacency.get(e.target) ?? [];
      a.push(e.target);
      b.push(e.source);
      adjacency.set(e.source, a);
      adjacency.set(e.target, b);
    }
  const seeds = [...ids];
  for (const id of seeds) (adjacency.get(id) ?? []).slice(0, 4).forEach(add);
  ranked.forEach((n) => add(n.id));
  return {
    nodes: chosen,
    edges: page.edges.filter((e) => ids.has(e.source) && ids.has(e.target)),
    total: canonical.nodes.length,
    copies: canonical.copies,
  };
}
