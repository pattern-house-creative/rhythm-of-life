import {
  forceCollide,
  forceLink,
  forceManyBody,
  forceSimulation,
  forceX,
  forceY,
} from "d3-force";
import type { SimulationNodeDatum } from "d3-force";
import type { KnowledgeEdge, KnowledgeNode } from "./model";
import { clusterGraph, hash, isFolder } from "./graphView";
export interface GraphPoint extends SimulationNodeDatum {
  id: string;
  x: number;
  y: number;
  anchorX: number;
  anchorY: number;
  theme: boolean;
}
// Restore the imperative D3 simulation's private copies, never React/source records.
export function restorePositions(
  points: GraphPoint[],
  previous: Map<string, { x: number; y: number; height: number }>,
  height: number,
) {
  for (const point of points) {
    const old = previous.get(point.id);
    if (old?.height === height) {
      point.x = old.x;
      point.y = old.y;
    }
  }
}
// Name the hub of each connected cluster at rest, and its runner-up in clusters
// of four or more. Unconnected records stay unnamed until explored or selected.
export function graphLabelPriorities(
  nodes: KnowledgeNode[],
  edges: KnowledgeEdge[],
) {
  const degree = new Map<string, number>();
  for (const e of edges) {
    degree.set(e.source, (degree.get(e.source) ?? 0) + 1);
    degree.set(e.target, (degree.get(e.target) ?? 0) + 1);
  }
  const result = new Map<string, "primary" | "secondary">();
  const { groups } = clusterGraph(nodes, edges);
  const sizes = new Map<string, number>();
  for (const cluster of groups.values())
    sizes.set(cluster, (sizes.get(cluster) ?? 0) + 1);
  for (const cluster of new Set(groups.values())) {
    const ranked = nodes
      .filter(
        (n) =>
          groups.get(n.id) === cluster &&
          n.type !== "theme" &&
          (degree.get(n.id) ?? 0) > 0,
      )
      .sort(
        (a, b) =>
          (degree.get(b.id) ?? 0) - (degree.get(a.id) ?? 0) ||
          Number(b.type === "project") - Number(a.type === "project") ||
          a.id.localeCompare(b.id),
      );
    ranked
      .slice(0, (sizes.get(cluster) ?? 0) >= 4 ? 2 : 1)
      .forEach((n, i) => result.set(n.id, i === 0 ? "primary" : "secondary"));
  }
  return result;
}
// Where the ambient sky placed each record, in its own graph units.
export interface SkySeed {
  height: number;
  positions: Map<string, { x: number; y: number }>;
}
// Forces operate on copies, never on source records. A seed (the ambient
// sky) places every record it shares, so the explorer opens on the same sky.
export function createGraph(
  nodes: KnowledgeNode[],
  edges: KnowledgeEdge[],
  height: number,
  seed?: SkySeed,
) {
  const clusters = clusterGraph(nodes, edges);
  // Records with no link and no cluster mates sit on a quieter outer ring, so
  // connected clusters read as the structure instead of competing with them.
  const present = new Set(nodes.map((n) => n.id));
  const linked = new Set(
    edges
      .filter((e) => present.has(e.source) && present.has(e.target))
      .flatMap((e) => [e.source, e.target]),
  );
  const sizes = new Map<string, number>();
  for (const cluster of clusters.groups.values())
    sizes.set(cluster, (sizes.get(cluster) ?? 0) + 1);
  const loose = new Set(
    nodes
      .filter(
        (n) =>
          n.type !== "theme" &&
          !linked.has(n.id) &&
          sizes.get(clusters.groups.get(n.id)!) === 1,
      )
      .map((n) => n.id),
  );
  // Only separate them when there is structure to separate them from.
  if (!loose.size || loose.size === nodes.length) loose.clear();
  const outer = new Map(
    [...loose]
      .sort((a, b) => hash(a) - hash(b) || a.localeCompare(b))
      .map((id, i) => [id, i]),
  );
  const clusterLinks: [string, string][] = edges
    .filter(
      (e) =>
        present.has(e.source) &&
        present.has(e.target) &&
        !loose.has(e.source) &&
        !loose.has(e.target),
    )
    .map((e) => [
      clusters.groups.get(e.source)!,
      clusters.groups.get(e.target)!,
    ]);
  const ringed = [
    ...new Set(
      nodes
        .filter((n) => !loose.has(n.id))
        .map((n) => clusters.groups.get(n.id)!),
    ),
  ].sort();
  // With a ring, the cluster with the most links to other clusters (an index
  // document, say) sits in the middle so its links radiate outwards instead
  // of crossing the whole graph from one side.
  const reachOut = new Map<string, number>();
  for (const [a, b] of clusterLinks)
    if (a !== b)
      for (const k of [a, b]) reachOut.set(k, (reachOut.get(k) ?? 0) + 1);
  const centre =
    loose.size && ringed.length >= 4
      ? ringed.reduce((best, k) =>
          (reachOut.get(k) ?? 0) > (reachOut.get(best) ?? 0) ? k : best,
        )
      : undefined;
  const keys = orderClusters(
    ringed.filter((k) => k !== centre),
    sizes,
    clusterLinks,
  );
  const area = graphBounds(height);
  const middle = (area.top + area.bottom) / 2,
    across = (area.right - area.left) / 2,
    reach = (area.bottom - area.top) / 2 - 10;
  // Space records by the room there is: about 70% of the oval they live in,
  // shared out. A fixed spacing overfilled every real screen and pressed the
  // outer records into straight rows along the edges.
  const room =
    Math.PI * ((area.right - area.left) / 2) * ((area.bottom - area.top) / 2);
  const spacing = Math.sqrt(
    (room * 0.7) / (Math.PI * Math.max(1, nodes.length)),
  );
  // Links and fans lengthen with that spacing, so a roomy sky spreads out.
  const stretch = Math.min(1.3, Math.max(1, spacing / 17.5));
  // With a ring, each cluster is drawn as a small star: its best-connected
  // record at the anchor and the rest fanned out away from the centre.
  const degree = new Map<string, number>();
  for (const e of edges)
    if (present.has(e.source) && present.has(e.target))
      for (const id of [e.source, e.target])
        degree.set(id, (degree.get(id) ?? 0) + 1);
  const fan = new Map<string, { slot: number; of: number }>();
  if (loose.size)
    for (const key of centre === undefined ? keys : [...keys, centre]) {
      const members = nodes
        .filter((n) => !loose.has(n.id) && clusters.groups.get(n.id) === key)
        .sort(
          // A folder (or theme) is its cluster's hub; its files fan out round it.
          (a, b) =>
            Number(b.type === "theme") - Number(a.type === "theme") ||
            (degree.get(b.id) ?? 0) - (degree.get(a.id) ?? 0) ||
            a.id.localeCompare(b.id),
        );
      members.forEach((n, slot) => fan.set(n.id, { slot, of: members.length }));
    }
  const points: GraphPoint[] = nodes.map((n) => {
    const ring = outer.get(n.id);
    if (ring !== undefined) {
      const angle = -Math.PI / 2 + ((ring + 0.5) * 2 * Math.PI) / outer.size;
      const x = 410 + Math.cos(angle) * (across - 40),
        y = middle + Math.sin(angle) * reach;
      return { id: n.id, theme: false, x, y, anchorX: x, anchorY: y };
    }
    const key = clusters.groups.get(n.id)!,
      index = keys.indexOf(key);
    const angle =
      -Math.PI / 2 + (index * 2 * Math.PI) / Math.max(1, keys.length);
    const place = fan.get(n.id);
    let x: number, y: number;
    if (place) {
      const middleCluster = key === centre;
      const anchorX =
          410 +
          (keys.length > 1 && !middleCluster
            ? Math.cos(angle) * across * 0.47
            : 0),
        anchorY =
          middle +
          (keys.length > 1 && !middleCluster
            ? Math.sin(angle) * reach * 0.5
            : 0);
      // Leaves spread over at most 200 degrees, centred on the outward
      // direction; the middle cluster's leaves go all the way round.
      const leaves = place.of - 1,
        step = middleCluster
          ? (Math.PI * 2) / Math.max(1, leaves)
          : leaves > 1
            ? Math.min(Math.PI / 3, (Math.PI * 1.1) / (leaves - 1))
            : 0,
        turn = angle + (place.slot - 1 - (leaves - 1) / 2) * step,
        radius = place.slot ? (middleCluster ? 46 : 52) * stretch : 0;
      x = anchorX + Math.cos(turn) * radius;
      y = anchorY + Math.sin(turn) * radius * Math.min(1, reach / 160);
    } else {
      const spread = (hash(n.id) / 4294967295) * Math.PI * 2;
      const radius = n.type === "theme" ? 0 : 45 + (hash(n.id + "radius") % 65);
      x =
        410 +
        (keys.length > 1 ? Math.cos(angle) * across * 0.55 : 0) +
        Math.cos(spread) * radius;
      y =
        height / 2 +
        (keys.length > 1 ? (Math.sin(angle) * (height - 270)) / 2 : 0) +
        Math.sin(spread) * radius * Math.min(1, height / 820);
    }
    return {
      id: n.id,
      // Folders are hubs, but small: they keep a record's spacing and labels.
      theme: n.type === "theme" && !isFolder(n),
      x,
      y,
      anchorX: x,
      anchorY: y,
    };
  });
  const byId = new Map(points.map((n) => [n.id, n]));
  const links = edges
    .filter((e) => byId.has(e.source) && byId.has(e.target))
    .map((e) => ({ ...e }));
  // Ring records hold their place firmly, so repulsion cannot flatten the
  // ring against the edges into rows.
  const hold = (n: GraphPoint) => (loose.has(n.id) ? 0.35 : 0.075);
  const x = forceX<GraphPoint>((n) => n.anchorX).strength(hold),
    y = forceY<GraphPoint>((n) => n.anchorY).strength(hold);
  const simulation = forceSimulation(points)
    .stop()
    .velocityDecay(0.72)
    .force("x", x)
    .force("y", y)
    .force(
      "charge",
      forceManyBody<GraphPoint>().strength(
        -75 * Math.min(1, (spacing / 24) ** 2),
      ),
    )
    .force(
      "collision",
      forceCollide<GraphPoint>((n) =>
        n.theme
          ? Math.min(60, height / 9, spacing * 1.4)
          : Math.min(44, height / 11, spacing),
      )
        .strength(0.8)
        .iterations(2),
    )
    .force(
      "links",
      forceLink<GraphPoint, (typeof links)[number]>(links)
        .id((n) => n.id)
        .distance((e) => {
          const a = e.source as unknown as GraphPoint,
            b = e.target as unknown as GraphPoint;
          return Math.hypot(a.anchorX - b.anchorX, a.anchorY - b.anchorY);
        })
        .strength((e) => e.strength * 0.08),
    );
  if (loose.size) {
    // Settle the linked records first: verified links pull their ends
    // together while each cluster keeps a gentle pull towards its slot, so
    // hubs end up among the records they cite. The settled positions become
    // the anchors the ambient drift moves around.
    const settle = forceLink<GraphPoint, (typeof links)[number]>(links)
      .id((n) => n.id)
      .distance((e) =>
        "verification" in e && e.verification === "verified"
          ? 46 * stretch
          : 90,
      )
      .strength((e) =>
        "verification" in e && e.verification === "verified" ? 0.35 : 0.04,
      );
    const gentle = (n: GraphPoint) => (loose.has(n.id) ? 0.35 : 0.03);
    x.strength(gentle);
    y.strength(gentle);
    simulation
      .force("links", settle)
      .force("edge", softEdge(height, loose))
      .alpha(1);
    for (let i = 0; i < 240; i++) {
      simulation.tick();
      containGraph(points, height);
    }
    for (const p of points) {
      p.anchorX = p.x;
      p.anchorY = p.y;
    }
    // The settled layout is final: from here each record only eases towards
    // its anchor. Repulsion and link forces kept pushing against the anchors
    // and left records wandering (and names flipping) indefinitely.
    simulation
      .force("charge", null)
      .force("collision", null)
      .force("links", null)
      .force("edge", null);
    x.strength(0.2);
    y.strength(0.2);
  } else simulation.tick(45);
  // Records the seed knows keep their places in the sky and only ease towards
  // them from here. A taller frame (a phone's explorer) stretches the sky
  // downwards so its records keep their spacing; a shorter one shrinks it
  // evenly. Every record keeps its share of the sky's width and height.
  const shared = seed ? points.filter((p) => seed.positions.has(p.id)) : [];
  if (seed && shared.length) {
    const ratio = height / seed.height,
      across = Math.min(1, ratio);
    for (const p of shared) {
      const at = seed.positions.get(p.id)!;
      p.x = p.anchorX = 410 + (at.x - 410) * across;
      p.y = p.anchorY = height / 2 + (at.y - seed.height / 2) * ratio;
    }
    simulation
      .force("charge", null)
      .force("collision", null)
      .force("links", null)
      .force("edge", null);
    x.strength(0.2);
    y.strength(0.2);
  }
  containGraph(points, height);
  return { points, byId, simulation, x, y, clusters, loose };
}
// Order clusters around the ring so the most linked ones are neighbours:
// start from the largest, then repeatedly take the cluster with the most
// links to the last one placed (then to all placed, then the largest). Links
// then mostly run between neighbours instead of across the whole graph.
export function orderClusters(
  keys: string[],
  sizes: ReadonlyMap<string, number>,
  links: [string, string][],
) {
  const weight = new Map<string, number>();
  const pair = (a: string, b: string) => (a < b ? `${a}\n${b}` : `${b}\n${a}`);
  for (const [a, b] of links)
    if (a !== b) weight.set(pair(a, b), (weight.get(pair(a, b)) ?? 0) + 1);
  const w = (a: string, b: string) => weight.get(pair(a, b)) ?? 0;
  const order: string[] = [];
  const rest = [...keys];
  while (rest.length) {
    const last = order.at(-1);
    let best = 0,
      bestScore = -Infinity;
    rest.forEach((k, i) => {
      const score =
        last === undefined
          ? (sizes.get(k) ?? 0)
          : w(last, k) * 1e6 +
            order.reduce((t, p) => t + w(p, k), 0) * 1e3 +
            (sizes.get(k) ?? 0);
      if (score > bestScore) {
        best = i;
        bestScore = score;
      }
    });
    order.push(rest.splice(best, 1)[0]);
  }
  return order;
}

// The oval the sky settles into, in graph units: its records spread through
// it and are eased off its rim. A short canvas (the explorer on a wide screen)
// keeps proportionally smaller top and bottom margins, instead of squeezing
// every record into a thin band of rows.
export function graphBounds(height: number) {
  return {
    left: 30,
    right: 790,
    top: Math.min(48, height * 0.08),
    bottom: height - Math.min(60, height * 0.1),
  };
}
// Where a record may be, settled or dragged: nearly the whole sky, in a box
// with well-rounded corners. One pushed past it moves back towards the middle.
export function skyBounds(height: number) {
  return {
    left: 12,
    right: 808,
    top: Math.min(20, height * 0.04),
    bottom: height - Math.min(26, height * 0.05),
  };
}
export function insideSky(x: number, y: number, height: number) {
  const area = skyBounds(height);
  const cx = (area.left + area.right) / 2,
    cy = (area.top + area.bottom) / 2,
    rx = (area.right - area.left) / 2,
    ry = (area.bottom - area.top) / 2;
  // A superellipse: round at the corners, close to the sky's edges elsewhere.
  const d =
    (Math.abs((x - cx) / rx) ** 4 + Math.abs((y - cy) / ry) ** 4) ** 0.25;
  return d > 1 ? { x: cx + (x - cx) / d, y: cy + (y - cy) / d } : { x, y };
}
// While the sky settles, records drifting towards the oval's rim are eased
// back in, so the outermost ones spread through the sky instead of lining its
// edge. Unlinked records keep their own outer ring.
function softEdge(height: number, ring: Set<string>) {
  const area = graphBounds(height);
  const cx = (area.left + area.right) / 2,
    cy = (area.top + area.bottom) / 2,
    rx = (area.right - area.left) / 2,
    ry = (area.bottom - area.top) / 2;
  let points: GraphPoint[] = [];
  const force = (alpha: number) => {
    for (const n of points) {
      if (ring.has(n.id)) continue;
      const dx = (n.x - cx) / rx,
        dy = (n.y - cy) / ry,
        d = Math.hypot(dx, dy);
      if (d <= 0.8) continue;
      const k = (d - 0.8) * alpha * 1.2;
      n.vx = (n.vx ?? 0) - (dx / d) * rx * k;
      n.vy = (n.vy ?? 0) - (dy / d) * ry * k;
    }
  };
  force.initialize = (nodes: GraphPoint[]) => {
    points = nodes;
  };
  return force;
}
export function containGraph(points: GraphPoint[], height: number) {
  for (const n of points) Object.assign(n, insideSky(n.x, n.y, height));
}

// Reserve the middle of the rendered quadratic edge when placing clickable
// labels. Keep this calculation next to its path so the two cannot drift.
export function graphEdgeGeometry(
  a: Pick<GraphPoint, "x" | "y">,
  b: Pick<GraphPoint, "x" | "y">,
) {
  const cx = (a.x + b.x) / 2 + 10;
  const cy = (a.y + b.y) / 2 - 12;
  return {
    path: `M${a.x},${a.y} Q${cx},${cy} ${b.x},${b.y}`,
    midpoint: { x: (a.x + 2 * cx + b.x) / 4, y: (a.y + 2 * cy + b.y) / 4 },
  };
}

// Half-size, in graph units, of the label-free square kept around each link
// midpoint: 24px across on screen, matching the link's 24px tap strip, so a
// link stays tappable at its middle while labels may sit close by.
export const edgeClearance = (pixelsPerUnit: number) =>
  12 / Math.max(pixelsPerUnit, 0.05);

// Radius, in graph units, of a node's tap target: 44px across on screen (56px
// for themes) at every width and zoom, and never smaller than the drawn star.
export const nodeHitRadius = (theme: boolean, pixelsPerUnit: number) =>
  Math.max(theme ? 20 : 9, (theme ? 28 : 22) / Math.max(pixelsPerUnit, 0.05));

// Label text size, in graph units, for a fixed on-screen size at every width
// and zoom: 13px for records and 16px for themes, 1px less on phones, times
// the sky's labelScale.
export const labelFontSize = (
  theme: boolean,
  pixelsPerUnit: number,
  compact = false,
  scale = 1,
) =>
  (((theme ? 16 : 13) - Number(compact)) * scale) /
  Math.max(pixelsPerUnit, 0.05);

// The band, in graph units, kept free of names at the bottom of a sky
// `skyWidth` screen pixels wide: 64px on screen in the explorer, over its
// controls and legend; 28px on an ambient sky, over its one-line hint, so a
// phone's landmarks low in the sky keep their names.
export function labelFloor(interactive: boolean, skyWidth: number) {
  const px = interactive ? 64 : 28;
  return Math.max(interactive ? 60 : 26, (px * 820) / Math.max(1, skyWidth));
}

// Whether a sky names only its landmarks at rest, as a phone's does: one
// narrower than 520px, or shorter than 420px (a phone on its side, a small
// map part).
export function quietSky(skyWidth: number, skyHeight: number) {
  return skyWidth < 520 || skyHeight < 420;
}

// A rectangle on screen, in CSS pixels.
export interface ScreenRect {
  left: number;
  top: number;
  right: number;
  bottom: number;
}
// How the sky's own units land on screen: the layer's screen matrix, whose
// a and d scale and e and f move (the sky is never rotated or skewed).
export interface SkyMatrix {
  a: number;
  d: number;
  e: number;
  f: number;
}
// A rectangle on screen in the sky's own units.
export function skyRect(rect: ScreenRect, m: SkyMatrix): ScreenRect {
  return {
    left: (rect.left - m.e) / m.a,
    right: (rect.right - m.e) / m.a,
    top: (rect.top - m.f) / m.d,
    bottom: (rect.bottom - m.f) / m.d,
  };
}
// Names fit the part of the sky that shows, not only the part it draws: the
// drawn bounds (graph units) cut to the sky's visible rectangle on screen,
// kept `inset` px clear of its bottom edge, so no name is painted half off a
// short sky.
export function visibleBounds(
  bounds: { left: number; right: number; top: number; bottom: number },
  visible: ScreenRect,
  m: SkyMatrix,
  inset = 4,
) {
  const v = skyRect({ ...visible, bottom: visible.bottom - inset }, m);
  return {
    left: Math.max(bounds.left, v.left),
    right: Math.min(bounds.right, v.right),
    top: Math.max(bounds.top, v.top),
    bottom: Math.min(bounds.bottom, v.bottom),
  };
}
// The lowest a name may reach on screen: the sky's visible bottom, or the
// top of the first line drawn across its lower half (Home's hint, a part's
// status), so a name sits above that band, never beside the hint in it. A
// still sky (reduced motion) keeps its names where they are first put, so
// this holds from the first placement.
export function skyFloor(visible: ScreenRect, under: ScreenRect[]) {
  const middle = (visible.top + visible.bottom) / 2;
  return Math.min(
    visible.bottom,
    ...under
      .filter(
        (u) =>
          u.right > visible.left &&
          u.left < visible.right &&
          u.top >= middle &&
          u.top < visible.bottom,
      )
      .map((u) => u.top),
  );
}
// Something drawn over the sky (the explorer's zoom controls, the heading
// above Home's sky) as a box names keep `clearance` px clear of.
export function overSky(
  rect: ScreenRect,
  m: SkyMatrix,
  clearance = 4,
): LabelBox {
  const r = skyRect(
    {
      left: rect.left - clearance,
      top: rect.top - clearance,
      right: rect.right + clearance,
      bottom: rect.bottom + clearance,
    },
    m,
  );
  return { x: r.left, y: r.top, w: r.right - r.left, h: r.bottom - r.top };
}
// A sky with less than this much height showing (CSS px): a short phone's,
// between two previews. It paints only names that matter most there.
export const shortSkyHeight = 88;
// The names a short sky paints, each only where it fits whole: its landmarks
// (topics, folders and projects), a pinned star's, the selection's, and the
// star a pointer or the keyboard rests on.
export function namedInShortSky(t: LabelTraits) {
  return !!(t.selected || t.emphasis || t.pinned || t.theme || t.folder);
}

// How much larger names are drawn on a large sky, so a wall display reads from
// across the room: unchanged up to a sky 1000px wide, then growing to 1.45
// times (the tiles' own largest zoom) on a sky about 2100px wide.
export function labelScale(skyWidth: number) {
  return Math.min(1.45, Math.max(1, 1 + (skyWidth - 1000) / 2500));
}

// What a sky knows about a star when it decides whose name to show.
export interface LabelTraits {
  selected?: boolean;
  // A pointer or the keyboard rests on it.
  emphasis?: boolean;
  pinned?: boolean;
  // In the explorer, lit by the focus: a selection's links, a search's
  // matches, a traced path.
  lit?: boolean;
  theme?: boolean;
  // A folder or a project star.
  folder?: boolean;
  dashboard?: boolean;
  priority?: "primary" | "secondary";
  memory?: boolean;
  // A memory linked to two or more records.
  memoryHub?: boolean;
  loose?: boolean;
}
// Who keeps a name when two would collide; higher places first. The selection,
// then the star a pointer or keyboard rests on, pinned stars, what the
// explorer's focus lights, then landmarks (topics, folders and projects),
// dashboards, each cluster's hub and runner-up, memories, linked records and
// last unlinked ones.
export function labelWeight(t: LabelTraits) {
  return (
    Number(!!t.selected) * 10000 +
    Number(!!t.emphasis) * 5000 +
    Number(!!t.pinned) * 2000 +
    Number(!!t.lit) * 1000 +
    Number(!!t.theme) * 400 +
    Number(!!t.folder) * 300 +
    Number(!!t.dashboard) * 200 +
    (t.priority === "primary" ? 100 : t.priority === "secondary" ? 60 : 0) +
    Number(!!t.memoryHub) * 30 +
    Number(!!t.memory) * 2 +
    Number(!t.loose)
  );
}
// Whether the ambient sky names a star at rest, before collisions decide.
// Every sky names its landmarks, dashboards, pinned stars and memories that
// tie records together; a sky larger than a phone's (quietSky) also names each
// cluster's hub and runner-up, so a wall display says what it shows. The rest
// are named on selection, hover, focus, zoom or the label control.
export function namedAtRest(t: LabelTraits, quiet: boolean) {
  return (
    !!t.theme ||
    !!t.folder ||
    !!t.dashboard ||
    !!t.pinned ||
    !!t.memoryHub ||
    (!quiet && t.priority !== undefined)
  );
}

// Record titles can be long excerpts with raw links. Short surfaces show the
// title without links; the full title stays in the tooltip and details panel.
export function displayTitle(title: string) {
  const plain = title
    .replace(/\bhttps?:\/\/\S+/gi, " ")
    .replace(/\s+/g, " ")
    .trim();
  return plain || title.replace(/\s+/g, " ").trim();
}

// One label line of at most `max` characters. A longer title is cut at a word
// (or mid-word when the first words are very long) and ends with an ellipsis.
export function labelText(title: string, max = 30) {
  const text = displayTitle(title);
  if (text.length <= max) return text;
  const space = text.lastIndexOf(" ", max - 1);
  const cut = space > max / 2 ? space : max - 1;
  return `${text.slice(0, cut).replace(/[\s.,;:!?–—&/-]+$/, "")}…`;
}

// A memory's title on up to two lines of at most `max` characters, split at
// the word that best balances them. A title too long for two lines fills the
// first and ends the second with an ellipsis.
export function labelLines(title: string, max = 26) {
  const text = displayTitle(title);
  if (text.length <= max) return [text];
  const words = text.split(" ");
  let best: string[] | null = null;
  for (let i = 1; i < words.length; i++) {
    const first = words.slice(0, i).join(" "),
      second = words.slice(i).join(" ");
    if (first.length > max) break;
    if (
      second.length <= max &&
      (!best ||
        Math.max(first.length, second.length) <
          Math.max(best[0].length, best[1].length))
    )
      best = [first, second];
  }
  if (best) return best;
  let first = "",
    i = 0;
  while (
    i < words.length &&
    (first ? first.length + 1 : 0) + words[i].length <= max
  )
    first = first ? `${first} ${words[i++]}` : words[i++];
  // A first word longer than a line: one shortened line.
  if (!first) return [labelText(text, max)];
  return [first, labelText(words.slice(i).join(" "), max)];
}

export interface LabelBox {
  x: number;
  y: number;
  w: number;
  h: number;
  // A link's reserved tap area already includes its margin; labels may touch it.
  reserved?: boolean;
  // The record whose star this box covers; its own label ignores it.
  owner?: string;
}
// Below, above, right, left, then further below. Side labels are vertically
// centred so narrow viewports can keep them above the controls. Offsets clear
// the drawn star (graph units), or `clear` units round its centre (a
// selection's ring), by a gap that scales with the on-screen text.
export function labelCandidates(
  theme: boolean,
  font: number,
  height: number,
  clear?: number,
) {
  const star = clear ?? (theme ? 19 : 8),
    gap = font * 0.3;
  return [
    { x: 0, y: star + gap + font, a: "middle" },
    { x: 0, y: -star - gap - height + font, a: "middle" },
    { x: star + font * 0.5, y: font - height / 2, a: "start" },
    { x: -star - font * 0.5, y: font - height / 2, a: "end" },
    { x: 0, y: star + gap + font * 2.6, a: "middle" },
  ] as const;
}
// First candidate inside the visible bounds that clears every occupied box by
// `spacing` (graph units). Choice -1 means none fits; the caller hides that label.
export function placeLabel(
  point: Pick<GraphPoint, "x" | "y" | "theme">,
  size: { width: number; height: number; font: number },
  occupied: LabelBox[],
  bounds: { left: number; right: number; top: number; bottom: number },
  preferred = 0,
  spacing: { x: number; y: number; reserved?: number } = { x: 7, y: 5 },
  clear?: number,
) {
  const candidates = labelCandidates(
    point.theme,
    size.font,
    size.height,
    clear,
  );
  const box = (c: (typeof candidates)[number]): LabelBox => ({
    x:
      point.x +
      c.x -
      (c.a === "middle" ? size.width / 2 : c.a === "end" ? size.width : 0),
    y: point.y + c.y - size.font,
    w: size.width,
    h: size.height,
  });
  for (const i of [preferred, ...candidates.keys()]) {
    const b = box(candidates[i]);
    if (
      b.x < bounds.left ||
      b.x + b.w > bounds.right ||
      b.y < bounds.top ||
      b.y + b.h > bounds.bottom
    )
      continue;
    if (
      occupied.every((r) => {
        const gx = r.reserved ? (spacing.reserved ?? 0) : spacing.x,
          gy = r.reserved ? (spacing.reserved ?? 0) : spacing.y;
        return (
          b.x + b.w + gx <= r.x ||
          r.x + r.w + gx <= b.x ||
          b.y + b.h + gy <= r.y ||
          r.y + r.h + gy <= b.y
        );
      })
    )
      return { choice: i, candidate: candidates[i], box: b };
  }
  return { choice: -1, candidate: candidates[0], box: box(candidates[0]) };
}

export interface CaptionRequest {
  id: string;
  members: Pick<GraphPoint, "x" | "y">[];
  width: number;
  font: number;
}
// A group's name sits centred just above its highest member. Larger groups
// place first; a caption is hidden when its group is spread over more than
// `spread` (its records do not sit together, so the name would point at the
// wrong place), when it would leave the view, or when it would touch another
// caption. Shown captions return their baseline position and box, which
// labels then keep clear of.
export function placeCaptions(
  requests: CaptionRequest[],
  bounds: { left: number; right: number; top: number; bottom: number },
  gap: number,
  spread = Infinity,
) {
  const placed = new Map<string, { x: number; y: number; box: LabelBox }>();
  const boxes: LabelBox[] = [];
  const ordered = [...requests]
    .filter((r) => r.members.length)
    .sort(
      (a, b) => b.members.length - a.members.length || a.id.localeCompare(b.id),
    );
  for (const r of ordered) {
    const xs = r.members.map((m) => m.x),
      ys = r.members.map((m) => m.y);
    if (
      Math.hypot(
        Math.max(...xs) - Math.min(...xs),
        Math.max(...ys) - Math.min(...ys),
      ) > spread
    )
      continue;
    const x = r.members.reduce((s, m) => s + m.x, 0) / r.members.length;
    const y = Math.min(...r.members.map((m) => m.y)) - gap;
    const box = {
      x: x - r.width / 2,
      y: y - r.font,
      w: r.width,
      h: r.font * 1.2,
    };
    if (
      box.x < bounds.left ||
      box.x + box.w > bounds.right ||
      box.y < bounds.top ||
      box.y + box.h > bounds.bottom ||
      boxes.some(
        (o) =>
          box.x < o.x + o.w + r.font &&
          o.x < box.x + box.w + r.font &&
          box.y < o.y + o.h + r.font / 2 &&
          o.y < box.y + box.h + r.font / 2,
      )
    )
      continue;
    boxes.push(box);
    placed.set(r.id, { x, y, box });
  }
  return placed;
}

export interface LabelRequest {
  id: string;
  point: Pick<GraphPoint, "x" | "y" | "theme">;
  size: { width: number; height: number; font: number };
  // Higher places first (labelWeight): selection, landmarks, dashboards, hubs.
  weight: number;
  // Shown even when nothing fits: the selected record, or every record while
  // the label control asks for all names.
  forced?: boolean;
  // Graph units the name keeps from the star's centre, when more than the
  // drawn star: the selected star's ring.
  clear?: number;
}
// One placement pass. Labels shown last pass go first within their weight and
// tolerate a tighter gutter than newcomers, so stars drifting by a few pixels
// do not make neighbouring labels trade places and blink. Hidden labels claim
// no space. Returns the placement for each shown label; absent means hidden.
export function placeLabels(
  requests: LabelRequest[],
  occupied: LabelBox[],
  bounds: { left: number; right: number; top: number; bottom: number },
  pixelsPerUnit: number,
  shown: ReadonlySet<string>,
  preferred: Map<string, number>,
) {
  const unit = Math.max(pixelsPerUnit, 0.05);
  const score = (r: LabelRequest) => r.weight + Number(shown.has(r.id)) * 5;
  const placements = new Map<string, ReturnType<typeof placeLabel>>();
  for (const r of [...requests].sort((a, b) => score(b) - score(a))) {
    // An 8px by 4px gutter on screen, 3px less for a label already showing
    // and 3px more for a new one. A label already showing may also drift 3px
    // into a reserved star or link area before it gives way.
    const slack = shown.has(r.id) ? -3 : 3;
    const placed = placeLabel(
      r.point,
      r.size,
      occupied.filter((b) => b.owner !== r.id),
      bounds,
      preferred.get(r.id),
      {
        x: (8 + slack) / unit,
        y: (4 + slack) / unit,
        reserved: Math.min(0, slack) / unit,
      },
      r.clear,
    );
    if (placed.choice < 0 && !r.forced) continue;
    preferred.set(r.id, Math.max(0, placed.choice));
    occupied.push(placed.box);
    placements.set(r.id, placed);
  }
  return placements;
}
