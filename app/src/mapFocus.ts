import { readable, starSource } from "./knowledge.js";
import type { GraphEdge, GraphNode, GraphPage } from "./knowledge.js";
import { shownLink, viewLimit } from "./graphView.js";
import type { FocusLink, MapFocus } from "./parts.js";

// A map part's focus (docs/dev/screens.md 10.4), worked out in the browser as the engine's
// DashboardMap works it out: for the demo, whose map the browser holds whole, and in plain
// words for what a screen reports. A focused part on a live install asks the engine instead,
// which filters the whole readable map.

// The provider a star's records came from (10.4's table): a data star's measurements'
// provider, the one after "data:" in its account (`garmin`); a record saved in Rhythm, its
// account (`drive`); any other star, its own provider (`drive`, `notes`, `second-brain`). The
// explorer's source filter reads stars the same way.
export { starSource };

// The stars a `near` member shows: that star, and those within `depth` links of it in either
// direction, following only the links the sky draws (every verification but disputed).
export function nearStars(
  star: string,
  edges: readonly GraphEdge[],
  depth: 1 | 2,
): Set<string> {
  const reached = new Set([star]);
  let frontier = new Set([star]);
  for (let hop = 0; hop < depth; hop++) {
    const next = new Set<string>();
    for (const e of edges) {
      if (!shownLink(e)) continue;
      for (const [from, to] of [
        [e.source, e.target],
        [e.target, e.source],
      ])
        if (frontier.has(from) && !reached.has(to)) {
          reached.add(to);
          next.add(to);
        }
    }
    frontier = next;
  }
  return reached;
}

// Whether a star meets every star member of a focus (all but `links`), and for a list any one
// of its values. `near` is the set nearStars gives for the focus's `near`, when it has one.
export function starMatches(
  n: GraphNode,
  focus: MapFocus,
  near: ReadonlySet<string> | null,
): boolean {
  const query = focus.query?.toLowerCase();
  return (
    (!query ||
      [n.title, n.overview, ...(n.groups ?? []).map((g) => g.label)].some(
        (text) =>
          typeof text === "string" && text.toLowerCase().includes(query),
      )) &&
    (!focus.near || !!near?.has(n.id)) &&
    (!focus.types || focus.types.includes(n.type)) &&
    (!focus.providers || focus.providers.includes(starSource(n))) &&
    (!focus.groups ||
      (n.groups ?? []).some((g) => focus.groups!.includes(g.id))) &&
    (!focus.owners || focus.owners.includes(n.resource.owner))
  );
}

// A focused map part's read, as the engine answers it: one page of the matching stars and the
// links between them, `matched` counting every match.
export interface FocusedPage extends GraphPage {
  matched: number;
}
const ordinal = (a: string, b: string) => (a < b ? -1 : a > b ? 1 : 0);
// A focus applied to a map held whole (the demo's), with the engine's rules (10.4): first only
// what the map shows (readable stars whose provider it shows, and the links between two of
// them that aren't disputed); then the stars meeting the focus, `near` walked over those
// links; ranked as the overview ranks stars (memories and what they cite first, then
// documents, then themes, then the rest; within each the most linked, then the newest, then
// by graph id), `limit` a page with an `o:N` cursor; and the links between two stars of the
// page, only those `links` names when it is given.
export function focusedPage(
  map: Pick<GraphPage, "nodes" | "edges">,
  focus: MapFocus,
  {
    cursor = null,
    limit = viewLimit,
    shows = () => true,
  }: {
    cursor?: string | null;
    limit?: number;
    shows?: (provider: string) => boolean;
  } = {},
): FocusedPage {
  const nodes = map.nodes.filter(
    (n) => readable(n) && shows(n.resource.identity.provider),
  );
  const kept = new Set(nodes.map((n) => n.id));
  const links = map.edges.filter(
    (e) => shownLink(e) && kept.has(e.source) && kept.has(e.target),
  );
  const near = focus.near
    ? nearStars(focus.near.star, links, focus.near.depth)
    : null;
  const degree = new Map<string, number>();
  for (const e of links)
    for (const id of [e.source, e.target])
      degree.set(id, (degree.get(id) ?? 0) + 1);
  const linked = (id: string) => degree.get(id) ?? 0;
  const memories = new Set(
    nodes
      .filter((n) => n.type === "memory" || n.type === "note")
      .map((n) => n.id),
  );
  const cited = new Set(
    links
      .filter((e) => memories.has(e.source) && e.verification === "verified")
      .map((e) => e.target),
  );
  const tier = (n: GraphNode) =>
    memories.has(n.id) || cited.has(n.id)
      ? 0
      : n.type === "document"
        ? 1
        : n.type === "theme"
          ? 2
          : 3;
  const newest = (n: GraphNode) =>
    Date.parse(n.resource.sourceModifiedAt ?? n.provenance.observedAt) || 0;
  const ranked = nodes
    .filter((n) => starMatches(n, focus, near))
    .sort(
      (a, b) =>
        tier(a) - tier(b) ||
        linked(b.id) - linked(a.id) ||
        newest(b) - newest(a) ||
        ordinal(a.id, b.id),
    );
  const offset = Number(/^o:(\d{1,9})$/.exec(cursor ?? "")?.[1] ?? 0);
  const shown = ranked.slice(offset, offset + limit);
  const ids = new Set(shown.map((n) => n.id));
  const rank = (e: GraphEdge) =>
    e.verification === "verified" ? 0 : e.verification === "inferred" ? 1 : 2;
  const edges = links
    .filter(
      (e) =>
        ids.has(e.source) &&
        ids.has(e.target) &&
        (!focus.links || focus.links.includes(e.verification as FocusLink)),
    )
    .sort(
      (a, b) =>
        rank(a) - rank(b) ||
        linked(b.source) +
          linked(b.target) -
          linked(a.source) -
          linked(a.target) ||
        ordinal(a.id, b.id),
    )
    .slice(0, limit * 2);
  const nextCursor =
    offset + limit < ranked.length ? `o:${offset + limit}` : null;
  return {
    nodes: shown,
    edges,
    nextCursor,
    completeness: nextCursor ? "partial" : "complete",
    issues: [],
    matched: ranked.length,
  };
}

// A focus in plain words, as `showing.parts` reports it and a part's label and caption name
// it: "near Training plan; providers garmin", cut to 100 characters. `title` names a star by
// its graph id when the screen knows it.
export function focusSummary(
  focus: MapFocus | null | undefined,
  title?: (star: string) => string | undefined,
): string {
  if (!focus) return "everything";
  const star = focus.near && (title?.(focus.near.star) || focus.near.star);
  const words = [
    focus.query !== undefined ? `matching “${focus.query}”` : null,
    focus.near
      ? focus.near.depth === 2
        ? `within 2 links of ${star}`
        : `near ${star}`
      : null,
    focus.types ? `types ${focus.types.join(", ")}` : null,
    focus.providers ? `providers ${focus.providers.join(", ")}` : null,
    focus.groups ? `groups ${focus.groups.join(", ")}` : null,
    focus.owners ? `owners ${focus.owners.join(", ")}` : null,
    focus.links ? `links ${focus.links.join(", ")}` : null,
  ].filter((w) => w !== null);
  const text = words.length
    ? words.join("; ")
    : "a focus this screen can't read";
  return Array.from(text.replace(/\p{Cc}/gu, " "))
    .slice(0, 100)
    .join("");
}
