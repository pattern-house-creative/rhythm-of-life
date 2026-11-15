import type { KnowledgeNode, KnowledgeEdge, SourceId } from "./model.js";
import type { DocumentOriginal } from "./documents.js";
import { catalogProvider, healthSubject } from "./instance.js";

export type Verification =
  "verified" | "suggested" | "inferred" | "disputed" | "unverified";
// The link vocabulary (D-030): Second Brain's eight link types beside Rhythm's
// own, as the engine names them (engine/src/Core/LinkTypes.cs, migration 051's
// link_types()): whether each has a direction, and the memory kinds a type may
// join (the episodic rule). A type not listed is its source's own word, with
// the direction the engine sends.
export type MemoryKind = "episodic" | "semantic";
export interface LinkType {
  directed: boolean;
  kinds?: readonly MemoryKind[];
  upstream?: true;
}
export const linkTypes: Readonly<Record<string, LinkType>> = {
  relates_to: { directed: false, upstream: true },
  supersedes: { directed: true, upstream: true },
  caused_by: { directed: true, upstream: true },
  decided: { directed: true, kinds: ["episodic"], upstream: true },
  about_person: { directed: true, upstream: true },
  part_of_project: { directed: true, upstream: true },
  follows: { directed: true, kinds: ["episodic"], upstream: true },
  drawn_from: { directed: true, upstream: true },
  references: { directed: true },
  in_project: { directed: true },
  in_folder: { directed: true },
  about: { directed: false },
  similar_to: { directed: false },
  copy_of: { directed: false },
  contains: { directed: true },
};
export const linkType = (type: string): LinkType | undefined =>
  Object.hasOwn(linkTypes, type) ? linkTypes[type] : undefined;
// A type with kinds joins only memories of those kinds at both ends; an end of
// no kind never qualifies. Any other type joins anything.
export function linkKindsAllow(
  type: string,
  source?: MemoryKind | null,
  target?: MemoryKind | null,
) {
  const kinds = linkType(type)?.kinds;
  return (
    !kinds ||
    (!!source && !!target && kinds.includes(source) && kinds.includes(target))
  );
}
export type Availability = "available" | "stale" | "missing" | "restricted";
export interface ResourceIdentity {
  provider: SourceId;
  account: string;
  externalId: string;
}
export interface ResourceMetadata {
  identity: ResourceIdentity;
  canonicalUrl: string | null;
  sourceUrl: string | null;
  sourceModifiedAt: string | null;
  retrievedAt: string;
  revision: string | null;
  owner: string;
  access: { scope: string; canRead: boolean; canOpen: boolean };
  availability: Availability;
  content: string | null;
  representation?: "original" | "archive" | "index";
  canonicalIdentity?: ResourceIdentity;
  // A document added as a file: its archived original (content reads only; see cleanOriginal).
  original?: DocumentOriginal;
}
export interface Evidence {
  resourceId: string;
  excerpt: string;
  locator: string;
  retrievedAt: string;
}
export interface GraphNode extends KnowledgeNode {
  resource: ResourceMetadata;
  measurements?: GraphSeries[];
  // A native memory's star that a newer memory of its scope replaced (SB-P4-11), sent only while the engine's
  // memory.tools is on: drawn faded, never hidden, and never as the current fact (graphView.ts, replacedBy).
  truth?: GraphTruth;
}
// A replaced fact on the map: its state, and the star of the memory that replaced it.
export interface GraphTruth {
  state: "replaced";
  replacedBy: string;
}
// One measurement of a data star: its latest reading and daily summaries.
export interface GraphSeries {
  metric: string;
  label: string;
  unit: string;
  latest: { value: number; observedAt: string | null; date: string } | null;
  days: {
    date: string;
    min: number;
    mean: number;
    max: number;
    count: number;
  }[];
}
export interface GraphEdge extends KnowledgeEdge {
  id: string;
  // Preserve the provider's vocabulary, not a lossy application enum.
  type: string;
  origin: { provider: SourceId; account: string; externalId: string };
  directed: boolean;
  verification: Verification;
  evidence: Evidence[];
  sourceModifiedAt: string | null;
  retrievedAt: string;
}
export interface GraphPage {
  nodes: GraphNode[];
  edges: GraphEdge[];
  nextCursor: string | null;
  completeness: "complete" | "partial";
  issues: { provider: SourceId; reason: string }[];
  removedNodeIds?: string[];
  removedEdgeIds?: string[];
  sourceStates?: Record<
    string,
    { completeness: "complete" | "partial"; reason?: string }
  >;
  discoveryCursors?: Record<string, string | null>;
  discoveryStates?: GraphPage["sourceStates"];
}
export interface AdapterCapabilities {
  discovery: "recent" | "relevant" | "none";
  changes: "cursor" | "revalidate" | "none";
  pagination: "cursor" | "bounded";
  relationships: "both" | "outgoing" | "none";
}
export interface GraphAdapter {
  provider: SourceId;
  // Distinct adapter key for each account/workspace; defaults to provider for legacy fixtures.
  key?: string;
  capabilities?: AdapterCapabilities;
  discover?(cursor?: string, signal?: AbortSignal): Promise<GraphPage>;
  refresh?(known: GraphNode[], signal?: AbortSignal): Promise<GraphPage>;
  search(
    query: string,
    cursor?: string,
    signal?: AbortSignal,
  ): Promise<GraphPage>;
  neighbors(
    id: string,
    cursor?: string,
    signal?: AbortSignal,
  ): Promise<GraphPage>;
  get(id: string, signal?: AbortSignal): Promise<GraphNode | null>;
}
export const identityKey = (identity: ResourceIdentity) =>
  [identity.provider, identity.account, identity.externalId]
    .map(encodeURIComponent)
    .join(":");
export const readable = (n: GraphNode) =>
  n.resource.access.canRead &&
  n.resource.availability !== "restricted" &&
  n.resource.availability !== "missing";
export function neighborhood(
  id: string,
  edges: GraphEdge[],
  depth: number,
  follows: (e: GraphEdge) => boolean = (e) => e.verification === "verified",
) {
  const distances = new Map([[id, 0]]);
  for (let hop = 1; hop <= depth; hop++) {
    for (const edge of edges.filter(follows)) {
      if (distances.get(edge.source) === hop - 1 && !distances.has(edge.target))
        distances.set(edge.target, hop);
      if (distances.get(edge.target) === hop - 1 && !distances.has(edge.source))
        distances.set(edge.source, hop);
    }
  }
  return distances;
}
// Connectivity paths may traverse either end; edge direction remains explicit in evidence.
// This explores only the loaded, authorized subgraph, never claims global exhaustiveness.
export function tracePath(
  start: string,
  end: string,
  edges: GraphEdge[],
  nodes: GraphNode[],
) {
  const allowed = new Set(nodes.filter(readable).map((n) => n.id));
  if (!allowed.has(start) || !allowed.has(end)) return null;
  const adjacency = new Map<string, string[]>();
  for (const e of edges) {
    if (
      e.verification !== "verified" ||
      !allowed.has(e.source) ||
      !allowed.has(e.target)
    )
      continue;
    adjacency.set(e.source, [...(adjacency.get(e.source) ?? []), e.target]);
    adjacency.set(e.target, [...(adjacency.get(e.target) ?? []), e.source]);
  }
  const queue = [start],
    parents = new Map<string, string | null>([[start, null]]);
  for (let i = 0; i < queue.length; i++) {
    const current = queue[i];
    if (current === end) {
      const path: string[] = [];
      for (
        let id: string | null = end;
        id !== null;
        id = parents.get(id) ?? null
      )
        path.push(id);
      return path.reverse();
    }
    for (const next of adjacency.get(current) ?? [])
      if (!parents.has(next)) {
        parents.set(next, current);
        queue.push(next);
      }
  }
  return null;
}
export function mergePage(current: GraphPage, next: GraphPage): GraphPage {
  const removed = new Set(next.removedNodeIds ?? []);
  const nodes = [
    ...new Map(
      [...current.nodes, ...next.nodes].map((n) => [n.id, n]),
    ).values(),
  ];
  for (const n of nodes) if (!readable(n)) removed.add(n.id);
  const removedEdges = new Set(next.removedEdgeIds ?? []);
  const sourceStates = { ...current.sourceStates, ...next.sourceStates };
  return {
    ...next,
    discoveryCursors: next.discoveryCursors ?? current.discoveryCursors,
    discoveryStates: next.discoveryStates ?? current.discoveryStates,
    sourceStates,
    completeness:
      current.completeness === "partial" || next.completeness === "partial"
        ? "partial"
        : "complete",
    issues: [
      ...new Map(
        [...current.issues, ...next.issues].map((i) => [
          i.provider + i.reason,
          i,
        ]),
      ).values(),
    ],
    nodes: nodes.filter((n) => readable(n) && !removed.has(n.id)),
    edges: [
      ...new Map(
        [...current.edges, ...next.edges].map((e) => [
          identityKey(e.origin),
          e,
        ]),
      ).values(),
    ].filter(
      (e) =>
        !removedEdges.has(e.id) &&
        !removed.has(e.source) &&
        !removed.has(e.target),
    ),
  };
}
export function safeSourceUrl(node: GraphNode): string | null {
  if (!readable(node) || !node.resource.access.canOpen) return null;
  const raw = node.resource.canonicalUrl;
  if (!raw) return null;
  try {
    return new URL(raw).protocol === "https:" ? raw : null;
  } catch {
    return null;
  }
}
// Google's own embeddable viewer for a Drive original. Google checks access with the
// viewer's Google sign-in; no content passes through this app. Folders and unfamiliar
// link forms keep the plain "Open original" link.
export function drivePreviewUrl(node: GraphNode): string | null {
  const raw = safeSourceUrl(node);
  if (!raw || node.resource.identity.provider !== "drive") return null;
  const url = new URL(raw);
  const editor =
    /^\/(document|spreadsheets|presentation)\/d\/([\w-]+)(?:\/|$)/.exec(
      url.pathname,
    );
  if (url.host === "docs.google.com" && editor)
    return `https://docs.google.com/${editor[1]}/d/${editor[2]}/preview`;
  const file = /^\/file\/d\/([\w-]+)(?:\/|$)/.exec(url.pathname);
  if (url.host === "drive.google.com" && file)
    return `https://drive.google.com/file/d/${file[1]}/preview`;
  return null;
}

// Stars for records the engine keeps itself, under its catalog provider id ("rhythm" unless the
// install kept an older engine's): a data star stands for a group of measurements ("data:<source>"
// account), a saved record for one stored document.
export const isDataStar = (n: GraphNode) =>
  n.resource.identity.provider === catalogProvider() &&
  n.resource.identity.account.startsWith("data:");
// A memory or note from a memory source (a notes folder, Second Brain), whose text is read
// from that source when it is opened.
export const memoryText = (n: GraphNode) =>
  (n.type === "memory" || n.type === "note") &&
  n.resource.identity.provider !== catalogProvider() &&
  !n.provenance.demo;
// Where a note's links lead on the map (docs/adapters/notes.md): the star the engine linked for
// that very link, else the note with that path or file name, found as Obsidian finds it (the
// same folder first, then the shortest path), else the one note with that title. Null when the
// note is not on the map.
export function noteLinks(
  from: GraphNode,
  nodes: GraphNode[],
  edges: GraphEdge[],
) {
  const ids = new Set(nodes.map((n) => n.id));
  const linked = new Map<string, string>();
  for (const e of edges)
    if (e.source === from.id && ids.has(e.target))
      for (const x of e.evidence) linked.set(x.excerpt, e.target);
  const { provider, account } = from.resource.identity;
  const notes = nodes.filter(
    (n) =>
      n.type === "note" &&
      n.resource.identity.provider === provider &&
      n.resource.identity.account === account,
  );
  const path = (n: GraphNode) => n.resource.identity.externalId.toLowerCase();
  const folder = (p: string) => p.slice(0, Math.max(0, p.lastIndexOf("/")));
  const here = folder(path(from));
  const at = (p: string | null) =>
    p === null ? null : (notes.find((n) => path(n) === p)?.id ?? null);
  const one = (found: GraphNode[]) => (found.length === 1 ? found[0].id : null);
  // A path from a folder, with "." and ".." applied; null when it climbs out of the folder.
  const join = (dir: string, relative: string) => {
    const parts = relative.startsWith("/") || !dir ? [] : dir.split("/");
    for (const part of relative.split("/"))
      if (part === "..") {
        if (!parts.length) return null;
        parts.pop();
      } else if (part && part !== ".") parts.push(part);
    return parts.join("/");
  };
  return (target: string, raw: string, wiki: boolean): string | null => {
    const found = linked.get(raw);
    if (found) return found;
    const t = target.replace(/\\/g, "/").trim().toLowerCase();
    if (!wiki) return at(join(here, t)) ?? at(join(here, `${t}.md`));
    const bare = t.replace(/\.md$/, "");
    if (bare.includes("/"))
      return (
        at(join(here, `${bare}.md`)) ??
        at(join("", `${bare}.md`)) ??
        one(notes.filter((n) => path(n).endsWith(`/${bare}.md`)))
      );
    const named = notes
      .filter(
        (n) =>
          path(n)
            .replace(/^.*\//, "")
            .replace(/\.(md|markdown)$/, "") === bare,
      )
      .sort(
        (a, b) =>
          path(a).split("/").length - path(b).split("/").length ||
          path(a).length - path(b).length,
      );
    return (
      (named.find((n) => folder(path(n)) === here) ?? named[0])?.id ??
      one(notes.filter((n) => n.title.toLowerCase() === bare))
    );
  };
}
export const isSavedRecord = (n: GraphNode) =>
  n.resource.identity.provider === catalogProvider() &&
  !isDataStar(n) &&
  n.type === "document";
// The provider a star's records came from: a data star's measurements' provider (its account
// is "data:<provider>"), a saved document's provider (its account), else the star's own.
export const starSource = (n: GraphNode) =>
  isDataStar(n)
    ? n.resource.identity.account.slice("data:".length)
    : isSavedRecord(n)
      ? n.resource.identity.account
      : n.provenance.sourceId;
// The health panels show one person's readings (the first person in the settings), so only
// that person's matching data stars open one; every other data star shows its readings in
// the reader.
// Any provider's glucose star opens the glucose panel; Garmin's two groups open theirs.
const panels: Record<string, "glucose" | "training" | "recovery"> = {
  "data:garmin/training": "training",
  "data:garmin/recovery": "recovery",
};
export function healthPanel(n: GraphNode) {
  if (!isDataStar(n)) return null;
  const [subject, group] = n.resource.identity.externalId.split(":");
  if (subject !== healthSubject()) return null;
  return group === "glucose"
    ? "glucose"
    : (panels[`${n.resource.identity.account}/${group}`] ?? null);
}

// In-memory fixtures only: no network, persistent storage, credentials, or provider SDK.
export function createMockAdapters(
  nodes: GraphNode[],
  edges: GraphEdge[],
  options: { unavailable?: SourceId; pageSize?: number } = {},
): GraphAdapter[] {
  const size = Math.max(1, options.pageSize ?? 4);
  const check = (signal?: AbortSignal) => {
    if (signal?.aborted) throw new DOMException("Aborted", "AbortError");
  };
  return (
    ["drive", "second-brain", "rhythm", "demo", "garmin"] as SourceId[]
  ).map((provider) => {
    const page = (
      matches: GraphNode[],
      cursor?: string,
      root?: string,
    ): GraphPage => {
      if (options.unavailable === provider)
        throw new Error(`${provider} unavailable`);
      const offset = cursor ? Number(cursor) : 0;
      if (!Number.isSafeInteger(offset) || offset < 0)
        throw new Error("Invalid cursor");
      const authorized = matches.filter(readable),
        batch = authorized.slice(offset, offset + size);
      const ids = new Set(batch.map((n) => n.id));
      if (root && nodes.some((n) => n.id === root && readable(n)))
        ids.add(root);
      const allowed = new Set(nodes.filter(readable).map((n) => n.id));
      const linked = edges.filter(
        (e) =>
          ids.has(e.source) &&
          ids.has(e.target) &&
          allowed.has(e.source) &&
          allowed.has(e.target),
      );
      const nextCursor =
        offset + size < authorized.length ? String(offset + size) : null;
      return {
        nodes: nodes.filter((n) => ids.has(n.id) && readable(n)),
        edges: linked,
        nextCursor,
        completeness: nextCursor ? "partial" : "complete",
        issues: [],
      };
    };
    return {
      provider,
      async search(query, cursor, signal) {
        check(signal);
        return page(
          nodes.filter(
            (n) =>
              n.provenance.sourceId === provider &&
              `${n.title} ${n.overview}`
                .toLowerCase()
                .includes(query.toLowerCase()),
          ),
          cursor,
        );
      },
      async neighbors(id, cursor, signal) {
        check(signal);
        if (!nodes.some((n) => n.id === id && readable(n)))
          return page([], cursor);
        const ids = new Set(
          edges
            .filter((e) => e.source === id || e.target === id)
            .flatMap((e) => [e.source, e.target]),
        );
        return page(
          nodes.filter(
            (n) =>
              n.provenance.sourceId === provider &&
              ids.has(n.id) &&
              n.id !== id,
          ),
          cursor,
          id,
        );
      },
      async get(id, signal) {
        check(signal);
        if (options.unavailable === provider)
          throw new Error(`${provider} unavailable`);
        return (
          nodes.find(
            (n) =>
              n.id === id && n.provenance.sourceId === provider && readable(n),
          ) ?? null
        );
      },
    };
  });
}
export async function retrieveGraph(
  adapters: GraphAdapter[],
  operation: "neighbors" | "search" | "discover",
  value: string,
  cursors: Record<string, string | null> = {},
  signal?: AbortSignal,
  previousStates: GraphPage["sourceStates"] = {},
): Promise<GraphPage & { cursors: Record<string, string | null> }> {
  const results = await Promise.allSettled(
    adapters.map((a) =>
      cursors[a.key ?? a.provider] === null
        ? Promise.resolve(null)
        : operation === "discover"
          ? (a.discover?.(cursors[a.key ?? a.provider] ?? undefined, signal) ??
            Promise.resolve(null))
          : a[operation](
              value,
              cursors[a.key ?? a.provider] ?? undefined,
              signal,
            ),
    ),
  );
  if (signal?.aborted) throw new DOMException("Aborted", "AbortError");
  let result: GraphPage = {
    nodes: [],
    edges: [],
    nextCursor: null,
    completeness: "complete",
    issues: [],
    sourceStates: { ...previousStates },
  };
  const next = { ...cursors },
    issues: GraphPage["issues"] = [];
  results.forEach((r, i) => {
    const provider = adapters[i].provider,
      key = adapters[i].key ?? provider;
    if (r.status === "fulfilled" && r.value) {
      result = mergePage(result, r.value);
      next[key] = r.value.nextCursor;
      result.sourceStates = {
        ...result.sourceStates,
        [key]: { completeness: r.value.completeness },
      };
    }
    if (operation === "discover" && !adapters[i].discover) {
      result.sourceStates = {
        ...result.sourceStates,
        [key]: {
          completeness: "partial",
          reason:
            "Source does not expose recent discovery; use relevant search.",
        },
      };
      result.completeness = "partial";
    }
    if (r.status === "fulfilled" && !r.value && cursors[key] === null) {
      // This call did not re-query the completed/bounded source; it cannot certify
      // that source's coverage. The caller retains the earlier source state.
      result.sourceStates![key] ??= { completeness: "partial" };
    }
    if (r.status === "rejected") {
      issues.push({
        provider,
        reason: "Source unavailable; other results remain usable.",
      });
      delete next[key];
      result.sourceStates![key] = { completeness: "partial" };
    }
  });
  return {
    ...result,
    cursors: next,
    issues: [...result.issues, ...issues],
    completeness:
      Object.values(result.sourceStates ?? {}).some(
        (s) => s.completeness === "partial",
      ) ||
      result.issues.length ||
      issues.length ||
      Object.values(next).some(Boolean)
        ? "partial"
        : "complete",
  };
}
export const retrieveNeighbors = (
  adapters: GraphAdapter[],
  id: string,
  cursors: Record<string, string | null> = {},
  signal?: AbortSignal,
) => retrieveGraph(adapters, "neighbors", id, cursors, signal);
