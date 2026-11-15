import { expect, it } from "vitest";
import {
  containGraph,
  createGraph,
  graphBounds,
  insideSky,
  graphLabelPriorities,
  labelLines,
  skyBounds,
  placeCaptions,
  placeLabels,
} from "../src/graph";
import type { LabelRequest } from "../src/graph";
import { record, relation } from "./synthetic";

// Shaped like the live graph: no facets, a hub linked to four documents, a
// linked pair, and memories that link to nothing.
function shaped() {
  const hub = record("fictional-hub"),
    docs = [1, 2, 3, 4].map((i) => record(`fictional-doc-${i}`, "drive")),
    pair = [record("fictional-pair-a"), record("fictional-pair-b", "drive")],
    loose = [1, 2, 3, 4, 5, 6].map((i) => record(`fictional-loose-${i}`));
  const nodes = [hub, ...docs, ...pair, ...loose];
  for (const n of nodes) delete n.groups;
  const edges = [
    ...docs.map((d) => relation(hub, d)),
    relation(pair[0], pair[1]),
  ];
  return { nodes, edges, hub, docs, pair, loose };
}

it("names the hub of each linked cluster at rest and leaves loose records unnamed", () => {
  const { nodes, edges, hub, docs, pair, loose } = shaped();
  const priorities = graphLabelPriorities(nodes, edges);
  expect(priorities.get(hub.id)).toBe("primary");
  // Clusters of four or more also name a runner-up; pairs name one record.
  expect(docs.filter((d) => priorities.get(d.id) === "secondary")).toHaveLength(
    1,
  );
  expect(pair.filter((n) => priorities.has(n.id))).toHaveLength(1);
  expect(loose.some((n) => priorities.has(n.id))).toBe(false);
  expect(priorities.size).toBe(3);
});

it("places unlinked records on an outer ring around the linked clusters", () => {
  const { nodes, edges, loose } = shaped();
  const height = 820;
  const graph = createGraph(nodes, edges, height);
  expect([...graph.loose].sort()).toEqual(loose.map((n) => n.id).sort());
  // Distance from the centre as a fraction of the outer ring's ellipse.
  const area = graphBounds(height);
  const ring = (x: number, y: number) =>
    Math.hypot(
      (x - 410) / ((area.right - area.left) / 2 - 40),
      (y - (area.top + area.bottom) / 2) / ((area.bottom - area.top) / 2 - 10),
    );
  const anchors = graph.points.map((p) => ({
    loose: graph.loose.has(p.id),
    r: ring(p.anchorX, p.anchorY),
  }));
  const inner = Math.max(...anchors.filter((a) => !a.loose).map((a) => a.r));
  for (const a of anchors.filter((a) => a.loose)) {
    // Settled anchors stay on the ring; repulsion moves them only slightly.
    expect(a.r).toBeCloseTo(1, 1);
    expect(a.r).toBeGreaterThan(inner + 0.1);
  }
  const mean = (loose: boolean) => {
    const rs = graph.points
      .filter((p) => graph.loose.has(p.id) === loose)
      .map((p) => ring(p.x, p.y));
    return rs.reduce((s, r) => s + r, 0) / rs.length;
  };
  expect(mean(true)).toBeGreaterThan(mean(false) + 0.2);
  graph.simulation.stop();
  // With nothing to separate them from, records keep the original layout.
  for (const subset of [loose, nodes.filter((n) => !loose.includes(n))]) {
    const plain = createGraph(subset, edges, height);
    expect(plain.loose.size).toBe(0);
    plain.simulation.stop();
  }
});

// One-line labels below their stars, with bounds that rule out every other
// candidate position, so only the below slot can be used.
const font = 13,
  size = { width: 100, height: font * 1.2, font };
const bounds = { left: 0, right: 820, top: 405, bottom: 430 };
const label = (id: string, x: number, weight = 0): LabelRequest => ({
  id,
  point: { x, y: 400, theme: false },
  size,
  weight,
});

it("keeps a label that is showing when an equal neighbour competes for its place", () => {
  const preferred = new Map<string, number>();
  const first = placeLabels(
    [label("a", 400), label("b", 400)],
    [],
    bounds,
    1,
    new Set(),
    preferred,
  );
  expect([...first.keys()]).toEqual(["a"]);
  // Drift reorders the records; the label already showing keeps its place.
  const second = placeLabels(
    [label("b", 401), label("a", 400)],
    [],
    bounds,
    1,
    new Set(first.keys()),
    preferred,
  );
  expect([...second.keys()]).toEqual(["a"]);
});

it("lets labels already showing sit closer than a newcomer may", () => {
  // Boxes 8px apart: within the 5px a shown label tolerates, short of the
  // 11px a new label needs.
  const pair = [label("a", 400), label("b", 508)];
  const both = placeLabels(pair, [], bounds, 1, new Set(["a", "b"]), new Map());
  expect(both.size).toBe(2);
  const one = placeLabels(pair, [], bounds, 1, new Set(["a"]), new Map());
  expect([...one.keys()]).toEqual(["a"]);
});

it("gives hidden labels no space and always shows the selected record", () => {
  // b overlaps a and is hidden; c overlaps only where b would have been.
  const placed = placeLabels(
    [label("a", 400, 2), label("b", 460, 1), label("c", 560)],
    [],
    bounds,
    1,
    new Set(),
    new Map(),
  );
  expect([...placed.keys()].sort()).toEqual(["a", "c"]);
  const selected = placeLabels(
    [label("a", 400, 2), { ...label("s", 400, 1000), forced: true }],
    [],
    bounds,
    1,
    new Set(["a"]),
    new Map(),
  );
  expect([...selected.keys()]).toEqual(["s"]);
});

it("draws a linked cluster as a star around its best-connected record", () => {
  const { nodes, edges, hub, docs } = shaped();
  const graph = createGraph(nodes, edges, 820);
  const centre = graph.byId.get(hub.id)!;
  const spokes = docs.map((d) => {
    const p = graph.byId.get(d.id)!;
    return Math.hypot(p.anchorX - centre.anchorX, p.anchorY - centre.anchorY);
  });
  for (const length of spokes)
    expect(Math.abs(length - spokes[0])).toBeLessThan(2);
  graph.simulation.stop();
});

it.each([300, 332, 600, 1250])(
  "keeps margins proportional on a %s-unit-tall canvas",
  (height) => {
    const area = graphBounds(height);
    // Short explorer canvases keep most of their height for records.
    expect(area.bottom - area.top).toBeGreaterThanOrEqual(height * 0.69);
    expect(area.top).toBeLessThanOrEqual(80);
    expect(height - area.bottom).toBeLessThanOrEqual(100);
  },
);

it("names a folder group just above its highest record and hides clashing names", () => {
  const wide = { left: 0, right: 820, top: 0, bottom: 820 };
  const placed = placeCaptions(
    [
      {
        id: "small",
        members: [
          { x: 300, y: 300 },
          { x: 340, y: 320 },
        ],
        width: 60,
        font: 11,
      },
      {
        id: "big",
        members: [
          { x: 300, y: 310 },
          { x: 320, y: 330 },
          { x: 340, y: 350 },
        ],
        width: 60,
        font: 11,
      },
      { id: "edge", members: [{ x: 5, y: 400 }], width: 80, font: 11 },
    ],
    wide,
    20,
  );
  // Larger groups place first; a caption touching it is hidden, as is one
  // that would leave the view.
  expect([...placed.keys()]).toEqual(["big"]);
  const big = placed.get("big")!;
  expect(big.x).toBe(320);
  expect(big.y).toBe(310 - 20);
});

it("keeps labels off other records' stars but not their own", () => {
  const star = (id: string, x: number) => ({
    x: x - 7,
    y: 393,
    w: 14,
    h: 14,
    reserved: true,
    owner: id,
  });
  // Its own star area may be large enough to reach the label slot.
  const own = placeLabels(
    [label("a", 400)],
    [{ ...star("a", 400), y: 380, h: 40 }],
    bounds,
    1,
    new Set(),
    new Map(),
  );
  expect(own.has("a")).toBe(true);
  // Another record's star right where the label would go blocks it.
  const blocked = placeLabels(
    [label("a", 400)],
    [{ ...star("b", 400), y: 410, owner: "b" }],
    bounds,
    1,
    new Set(),
    new Map(),
  );
  expect(blocked.has("a")).toBe(false);
  // A label already showing tolerates 3px of drift into that area.
  const drifted = placeLabels(
    [label("a", 400)],
    [{ ...star("b", 400), y: 427.5 - 2, owner: "b" }],
    bounds,
    1,
    new Set(["a"]),
    new Map(),
  );
  expect(drifted.has("a")).toBe(true);
  const fresh = placeLabels(
    [label("a", 400)],
    [{ ...star("b", 400), y: 427.5 - 2, owner: "b" }],
    bounds,
    1,
    new Set(),
    new Map(),
  );
  expect(fresh.has("a")).toBe(false);
});

it("leaves a group unnamed when its records are spread across the sky", () => {
  const wide = { left: 0, right: 820, top: 0, bottom: 820 };
  const request = (id: string, xs: number[]) => ({
    id,
    members: xs.map((x) => ({ x, y: 400 })),
    width: 60,
    font: 11,
  });
  const placed = placeCaptions(
    [request("together", [300, 360]), request("spread", [100, 700])],
    wide,
    20,
    200,
  );
  expect([...placed.keys()]).toEqual(["together"]);
});

it("lets records reach nearly the whole sky, with rounded corners", () => {
  const height = 600;
  const area = skyBounds(height);
  const cx = (area.left + area.right) / 2,
    cy = (area.top + area.bottom) / 2;
  // Inside stays put, even well outside the oval the sky settles into.
  const settled = graphBounds(height);
  expect(insideSky(settled.right + 10, cy, height)).toEqual({
    x: settled.right + 10,
    y: cy,
  });
  // Straight out to a side stops at that side of the sky.
  const side = insideSky(area.left - 200, cy, height);
  expect(side.x).toBeCloseTo(area.left, 6);
  expect(side.y).toBeCloseTo(cy, 6);
  const top = insideSky(cx, area.top - 200, height);
  expect(top.y).toBeCloseTo(area.top, 6);
  // Far past a corner stops at the rounded corner, inside the box's corner
  // but much further out than an oval allows.
  const corner = insideSky(area.right + 300, area.bottom + 300, height);
  expect(corner.x).toBeLessThan(area.right - 20);
  expect(corner.y).toBeLessThan(area.bottom - 20);
  const rx = (area.right - area.left) / 2,
    ry = (area.bottom - area.top) / 2;
  expect(
    Math.hypot((corner.x - cx) / rx, (corner.y - cy) / ry),
  ).toBeGreaterThan(1.1);
});

it("gives a large sky's records room instead of pressing them against its edge", () => {
  const { nodes, edges } = shaped();
  const height = 300;
  const graph = createGraph(nodes, edges, height);
  graph.simulation.tick(240);
  containGraph(graph.points, height);
  const area = graphBounds(height);
  const cx = (area.left + area.right) / 2,
    cy = (area.top + area.bottom) / 2,
    rx = (area.right - area.left) / 2,
    ry = (area.bottom - area.top) / 2;
  const onRim = graph.points.filter(
    (p) => Math.hypot((p.x - cx) / rx, (p.y - cy) / ry) > 0.995,
  ).length;
  // A wide, short sky used to push its outer records into straight rows.
  expect(onRim / graph.points.length).toBeLessThan(0.25);
  graph.simulation.stop();
});

it("wraps a memory title onto two balanced lines instead of cutting it", () => {
  expect(labelLines("Fictional garden plan")).toEqual([
    "Fictional garden plan",
  ]);
  // 36 characters: split where the two lines are closest in length.
  expect(labelLines("Fictional plan for the spring garden")).toEqual([
    "Fictional plan for",
    "the spring garden",
  ]);
  // Every line fits; nothing is cut while two lines hold the title.
  const title = "Fictional weekly check-in on sleep and training load";
  const lines = labelLines(title);
  expect(lines).toHaveLength(2);
  expect(lines.join(" ")).toBe(title);
  for (const line of lines) expect(line.length).toBeLessThanOrEqual(26);
  // Longer than two lines: the first is full, the second ends with "…".
  const long = labelLines(
    "Fictional notes about the reading list, river walks and a shared outline",
  );
  expect(long).toHaveLength(2);
  expect(long[0].length).toBeLessThanOrEqual(26);
  expect(long[1].length).toBeLessThanOrEqual(26);
  expect(long[1].endsWith("…")).toBe(true);
  // Phones get shorter lines.
  for (const line of labelLines(title, 20))
    expect(line.length).toBeLessThanOrEqual(20);
});
