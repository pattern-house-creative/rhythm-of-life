import { expect, it } from "vitest";
import {
  ambientWindow,
  clusterGraph,
  isFolder,
  primaryGroup,
  rankNodes,
  shownLink,
} from "../src/graphView";
import { createGraph, graphBounds, orderClusters } from "../src/graph";
import { tracePath } from "../src/knowledge";
import type { GraphEdge, GraphNode } from "../src/knowledge";
import { record, relation } from "./synthetic";

const folder = (id: string) => {
  const n = record(`fictional-folder-${id}`, "drive");
  n.type = "theme";
  n.title = `Fictional ${id} folder`;
  delete n.groups;
  return n;
};
const file = (id: string) => {
  const n = record(`fictional-${id}`, "drive");
  delete n.groups;
  return n;
};
const memory = (id: string) => {
  const n = record(`fictional-${id}`);
  delete n.groups;
  return n;
};
const link = (a: GraphNode, b: GraphNode, type: string): GraphEdge => ({
  ...relation(a, b),
  id: `${type}:${a.id}->${b.id}`,
  type,
});

// A fictional Drive tree: root > A > A1 and root > B, with files at each level.
function tree() {
  const root = folder("root"),
    a = folder("a"),
    a1 = folder("a1"),
    b = folder("b");
  const d1 = file("d1"),
    d2 = file("d2"),
    d3 = file("d3"),
    d4 = file("d4");
  const m = memory("cites-folder"),
    m2 = memory("cites-file");
  const edges = [
    link(a, root, "in_folder"),
    link(a1, a, "in_folder"),
    link(b, root, "in_folder"),
    link(d1, a1, "in_folder"),
    link(d2, a, "in_folder"),
    link(d3, b, "in_folder"),
    link(d4, root, "in_folder"),
    link(m, a1, "references"),
    link(m2, d3, "references"),
    link(d1, b, "references"),
  ];
  const page = { nodes: [root, a, a1, b, d1, d2, d3, d4, m, m2], edges };
  return { page, root, a, a1, b, d1, d2, d3, d4, m, m2 };
}

it("recognises Drive folders, not topic themes, as folders", () => {
  const t = tree();
  expect(isFolder(t.root)).toBe(true);
  const topic = memory("topic");
  topic.type = "theme";
  expect(isFolder(topic)).toBe(false);
  expect(isFolder(t.d1)).toBe(false);
});

it("ranks memories and what they cite above busier folders and files", () => {
  const busy = folder("busy");
  const files = Array.from({ length: 10 }, (_, i) => file(`busy-${i}`));
  const m = memory("idea"),
    cited = file("cited"),
    other = file("other"),
    image = file("image");
  image.type = "record";
  const project = memory("project");
  project.type = "project";
  const edges = [
    ...files.map((f) => link(f, busy, "in_folder")),
    link(m, cited, "references"),
    link(other, files[0], "references"),
    link(other, files[1], "references"),
    link(other, files[2], "references"),
  ];
  const order = rankNodes(
    [busy, ...files, m, cited, other, image, project],
    edges,
  ).map((n) => n.id);
  const at = (n: GraphNode) => order.indexOf(n.id);
  // Projects and memories lead (links break ties), then what memories cite.
  expect([at(project), at(m)].sort()).toEqual([0, 1]);
  expect(at(cited)).toBe(2);
  expect(at(busy)).toBeGreaterThan(at(cited));
  expect(at(cited)).toBeLessThan(at(other));
  expect(at(image)).toBe(order.length - 1);
});

it("shows every record and link in the ambient view, folders and similar links included", () => {
  const t = tree();
  const memories = Array.from({ length: 40 }, (_, i) => memory(`many-${i}`));
  const similar = link(memories[0], memories[1], "similar_to");
  similar.verification = "suggested";
  const view = ambientWindow({
    nodes: [...t.page.nodes, ...memories],
    edges: [...t.page.edges, similar],
  });
  // Nothing is left out: all 10 tree records and all 40 memories.
  expect(view.nodes).toHaveLength(50);
  expect(view.nodes.filter((n) => n.type === "theme")).toHaveLength(4);
  expect(view.edges.filter((e) => e.type === "in_folder")).toHaveLength(7);
  expect(view.edges).toContain(similar);
});

it("shows every link but disputed ones, while Trace follows confirmed links only", () => {
  const [a, b, c] = ["x", "y", "z"].map(memory);
  const similar = { ...link(a, b, "similar_to"), verification: "suggested" };
  const related = { ...link(b, c, "related_to"), verification: "suggested" };
  const disputed = { ...link(a, c, "references"), verification: "disputed" };
  expect(shownLink(similar as GraphEdge)).toBe(true);
  expect(shownLink(related as GraphEdge)).toBe(true);
  expect(shownLink(link(a, b, "references"))).toBe(true);
  expect(shownLink(disputed as GraphEdge)).toBe(false);
  expect(
    tracePath(a.id, c.id, [similar, related] as GraphEdge[], [a, b, c]),
  ).toBeNull();
  expect(tracePath(a.id, b.id, [link(a, b, "references")], [a, b, c])).toEqual([
    a.id,
    b.id,
  ]);
});

it("orders clusters around the ring so the most linked ones are neighbours", () => {
  const sizes = new Map([
    ["x", 5],
    ["y", 3],
    ["z", 2],
    ["w", 1],
  ]);
  const links: [string, string][] = [
    ["x", "z"],
    ["x", "z"],
    ["x", "z"],
    ["z", "y"],
    ["z", "y"],
    ["y", "w"],
  ];
  expect(orderClusters(["w", "x", "y", "z"], sizes, links)).toEqual([
    "x",
    "z",
    "y",
    "w",
  ]);
});

it("puts the most cross-linked cluster in the middle so its links radiate", () => {
  // A fictional index file linked to files in five other folders, plus
  // memories that link to nothing, as on a real Drive.
  const index = file("index");
  index.groups = [{ id: "index-folder", label: "Fictional index folder" }];
  const clusters = [1, 2, 3, 4, 5].map((i) =>
    [1, 2].map((j) => {
      const n = file(`f${i}-${j}`);
      n.groups = [{ id: `folder-${i}`, label: `Fictional folder ${i}` }];
      return n;
    }),
  );
  const loose = [1, 2, 3, 4].map((i) => memory(`loose-${i}`));
  const nodes = [index, ...clusters.flat(), ...loose];
  const edges = clusters.map((c) => link(index, c[0], "references"));
  const height = 820;
  const graph = createGraph(nodes, edges, height);
  const area = graphBounds(height);
  const middle = (area.top + area.bottom) / 2;
  const fromCentre = (n: GraphNode) => {
    const p = graph.byId.get(n.id)!;
    return Math.hypot(p.anchorX - 410, p.anchorY - middle);
  };
  for (const c of clusters)
    expect(fromCentre(index)).toBeLessThan(fromCentre(c[0]));
  graph.simulation.stop();
});

it("draws a folder as the small hub its files fan out around", () => {
  const t = tree();
  const loose = [1, 2, 3, 4].map((i) => memory(`loose-${i}`));
  const nodes = [...t.page.nodes, ...loose];
  const graph = createGraph(nodes, t.page.edges, 820);
  // Folders keep a record's size, unlike topic themes.
  expect(graph.byId.get(t.a.id)!.theme).toBe(false);
  // Each file linked only to its folder sits nearer it than any other folder.
  // (d1 also cites folder B, so it settles between the two.)
  const p = (n: GraphNode) => graph.byId.get(n.id)!;
  const d = (a: GraphNode, b: GraphNode) =>
    Math.hypot(p(a).anchorX - p(b).anchorX, p(a).anchorY - p(b).anchorY);
  for (const [file, own] of [
    [t.d2, t.a],
    [t.d3, t.b],
    [t.d4, t.root],
  ] as const)
    for (const other of [t.root, t.a, t.a1, t.b].filter((f) => f !== own))
      expect(d(file, own)).toBeLessThan(d(file, other));
  graph.simulation.stop();
});

it("groups memories by their first project, never by Personal or Family alone", () => {
  const personal = { id: "second-brain:personal", label: "Personal" };
  const family = { id: "second-brain:household", label: "Family" };
  const garden = {
    id: "second-brain:project:personal:fictional-garden",
    label: "Fictional garden",
  };
  const zoo = {
    id: "second-brain:project:personal:fictional-zoo",
    label: "Fictional zoo",
  };
  const a = memory("in-garden"),
    b = memory("in-garden-and-zoo"),
    c = memory("personal-cites-file"),
    d = memory("family-only"),
    e = memory("family-too");
  const cited = file("cited");
  a.groups = [personal, garden];
  b.groups = [personal, garden, zoo];
  c.groups = [personal];
  d.groups = [family];
  e.groups = [family];
  cited.groups = [{ id: "fictional-folder", label: "Fictional folder" }];
  expect(primaryGroup(b)).toEqual(garden);
  expect(primaryGroup(c)).toBeUndefined();
  expect(primaryGroup(cited)?.id).toBe("fictional-folder");
  const { groups, labels } = clusterGraph(
    [a, b, c, d, e, cited],
    [link(c, cited, "references")],
  );
  expect(groups.get(a.id)).toBe(garden.id);
  expect(groups.get(b.id)).toBe(garden.id);
  expect(labels.get(garden.id)).toBe("Fictional garden");
  // A memory without a project sits with what it cites, or on its own.
  expect(groups.get(c.id)).toBe("fictional-folder");
  expect(groups.get(d.id)).toBe(d.id);
  expect(groups.get(e.id)).toBe(e.id);
});
