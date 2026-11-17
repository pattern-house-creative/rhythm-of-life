import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import Explorer from "../src/Explorer";
import {
  cleanSummaries,
  dashboardStarId,
  drawsOn,
  withDashboards,
} from "../src/dashboards";
import type { DashboardSummary } from "../src/dashboards";
import { createDemoDashboards } from "../src/demoDashboards";
import { createGraph } from "../src/graph";
import { ambientWindow, viewLimit } from "../src/graphView";
import { emptyGraph } from "../src/liveGraph";
import type { GraphEdge, GraphNode, GraphPage } from "../src/knowledge";
import type { KnowledgeController } from "../src/useKnowledge";
import { record, relation } from "./synthetic";

// A dashboard that names no star (it reads memories by filter: a memories
// feed, docs/dev/screens.md 10.12) has a place on the sky: beside the project
// stars its feeds name, as a dashboard beside the star it reads, or, naming no
// project on the map, a star of its own on the ring the sky keeps for stars
// with no links. The explorer's search finds it by its title, and a dashboard
// that names stars is placed as before. Fictional data only.
beforeEach(() => {
  vi.useFakeTimers({ toFake: ["Date"] });
  vi.setSystemTime(new Date("2026-10-20T08:00:00Z"));
});
afterEach(() => vi.useRealTimers());

const [demo] = createDemoDashboards().list();
const real = (n: GraphNode) => {
  n.provenance.demo = false;
  return n;
};
// A native project's star as the engine sends it (MemoryStars): provider
// memory, account project, the project's id.
const porch = real(
  record("6d3c2a10-0000-4000-8000-0000000f0001", "memory", "project"),
);
porch.type = "project";
porch.title = "DEMO Porch";
porch.groups = [];
const hinges = real(record("6d3c2a10-0000-4000-8000-0000000f0002", "memory"));
hinges.title = "DEMO: fit new hinges on the porch door.";
const walks = real(record("fictional-walks", "notes"));
walks.title = "Fictional walks";
const paths = real(record("fictional-paths", "notes"));
paths.title = "Fictional paths";
const inProject: GraphEdge = {
  ...relation(hinges, porch),
  id: `in_project:${hinges.id}`,
  type: "in_project",
};
const page: GraphPage = {
  ...emptyGraph(),
  nodes: [porch, hinges, walks, paths],
  edges: [inProject, relation(walks, paths)],
};

const board = (
  id: string,
  title: string,
  more: Partial<DashboardSummary>,
): DashboardSummary => ({ ...demo, id, title, corners: [], ...more });
// Names a star, as every dashboard did before memories feeds.
const walked = board(
  "6d3c2a10-0000-4000-8000-0000000d0011",
  "Fictional walks board",
  { stars: [{ graphId: walks.id, title: "Fictional walks" }] },
);
// Reads the porch project's memories: beside the porch's star.
const porchBoard = board(
  "6d3c2a10-0000-4000-8000-0000000d0012",
  "DEMO Porch board",
  { stars: [], projects: [{ graphId: porch.id, title: "DEMO Porch" }] },
);
// Reads every project's open work: a star of its own.
const everyBoard = board(
  "6d3c2a10-0000-4000-8000-0000000d0013",
  "DEMO Every project",
  { stars: [], projects: [] },
);
// Reads a project whose star isn't on this map: a star of its own too.
const shedBoard = board(
  "6d3c2a10-0000-4000-8000-0000000d0014",
  "DEMO Shed board",
  {
    stars: [],
    projects: [
      { graphId: "memory:project:fictional-shed", title: "DEMO Shed" },
    ],
  },
);
const starOf = (d: DashboardSummary) => dashboardStarId(d.id);

it("keeps a star-less version's project stars, and never a starred one's", () => {
  const listed = cleanSummaries({
    dashboards: [walked, porchBoard, everyBoard],
  });
  expect(listed.map((d) => [d.title, d.projects])).toEqual([
    ["Fictional walks board", undefined],
    ["DEMO Porch board", [{ graphId: porch.id, title: "DEMO Porch" }]],
    ["DEMO Every project", []],
  ]);
  // An engine from before sends none: as it came, with no project to sit
  // beside, so it is a star of its own.
  const { projects: _, ...older } = porchBoard;
  expect(cleanSummaries({ dashboards: [older] })).toEqual([older]);
  const alone = withDashboards(page, [older]);
  expect(alone.nodes.map((n) => n.id)).toContain(starOf(porchBoard));
  expect(alone.edges.some((e) => e.source === starOf(porchBoard))).toBe(false);
  // Failing closed: projects on a version that names stars, too many, or a
  // malformed one leave the dashboard out, as a malformed star does.
  for (const broken of [
    { ...walked, projects: [{ graphId: porch.id, title: "DEMO Porch" }] },
    {
      ...porchBoard,
      projects: Array.from({ length: 13 }, (_, i) => ({
        graphId: `memory:project:${i}`,
        title: "x",
      })),
    },
    { ...porchBoard, projects: [{ graphId: "", title: "x" }] },
    { ...porchBoard, projects: "porch" },
  ])
    expect(cleanSummaries({ dashboards: [broken, everyBoard] })).toEqual([
      everyBoard,
    ]);
});

it("sets a dashboard that filters on a project beside that project's star", () => {
  const sky = withDashboards(page, [porchBoard]);
  const star = sky.nodes.find((n) => n.id === starOf(porchBoard))!;
  expect(star.title).toBe("DEMO Porch board");
  expect(sky.edges.filter((e) => e.source === star.id)).toEqual([
    expect.objectContaining({
      source: star.id,
      target: porch.id,
      type: "draws_on",
      verification: "verified",
      relationship:
        "The dashboard “DEMO Porch board” reads this project's memories.",
    }),
  ]);
  expect(sky.edges.filter(drawsOn)).toHaveLength(1);
  // It joins the project's cluster, so the sky draws it there, not on the
  // outer ring.
  const graph = createGraph(sky.nodes, sky.edges, 600);
  expect(graph.loose.has(star.id)).toBe(false);
  expect(graph.clusters.groups.get(star.id)).toBe(
    graph.clusters.groups.get(porch.id),
  );
});

it("gives a dashboard that names no project on the map a star of its own on the outer ring", () => {
  const sky = withDashboards(page, [everyBoard, shedBoard]);
  for (const d of [everyBoard, shedBoard]) {
    const star = sky.nodes.find((n) => n.id === starOf(d))!;
    expect(star.title).toBe(d.title);
    expect(star.resource.identity.account).toBe("dashboard");
    // No links: the shed's star isn't on this map, so nothing is drawn to it.
    expect(sky.edges.some((e) => e.source === star.id)).toBe(false);
  }
  const graph = createGraph(sky.nodes, sky.edges, 600);
  for (const d of [everyBoard, shedBoard]) {
    expect(graph.loose.has(starOf(d))).toBe(true);
    // Held on the ring at its anchor, as every star with no links is.
    const point = graph.byId.get(starOf(d))!;
    expect([point.anchorX, point.anchorY]).not.toEqual([410, 300]);
  }
  // It waits only for the map, so it never shows alone while the sky loads;
  // one a screen was asked to open joins all the same.
  expect(withDashboards(emptyGraph(), [everyBoard]).nodes).toHaveLength(0);
  expect(
    withDashboards(emptyGraph(), [everyBoard], false, everyBoard.id).nodes.map(
      (n) => n.id,
    ),
  ).toEqual([starOf(everyBoard)]);
  // Once on the sky it is not added twice.
  expect(withDashboards(sky, [everyBoard]).nodes).toHaveLength(
    sky.nodes.length,
  );
});

it("places a dashboard that names stars exactly as before", () => {
  const before = withDashboards(page, [walked]);
  const after = withDashboards(page, [walked, porchBoard, everyBoard]);
  const lone = new Set([starOf(porchBoard), starOf(everyBoard)]);
  expect(after.nodes.filter((n) => !lone.has(n.id))).toEqual(before.nodes);
  expect(after.edges.filter((e) => !lone.has(e.source))).toEqual(before.edges);
  expect(
    before.edges.find((e) => e.source === starOf(walked))?.relationship,
  ).toBe("The dashboard “Fictional walks board” reads this star.");
  // Still not before any star it names is on the map.
  expect(withDashboards(emptyGraph(), [walked]).nodes).toHaveLength(0);
});

it("keeps a dashboard with no links on a full sky, as a pinned star is kept", () => {
  // A sky of more stars than it draws, each newer than the dashboard, so the
  // dashboard ranks below every one of them.
  const many = Array.from({ length: viewLimit + 20 }, (_, i) => {
    const n = real(record(`fictional-full-${i}`, "notes"));
    n.resource.sourceModifiedAt = "2026-10-19T12:00:00Z";
    return n;
  });
  const old = { ...everyBoard, savedAt: "2026-01-01T00:00:00Z" };
  const sky = withDashboards({ ...emptyGraph(), nodes: many, edges: [] }, [
    old,
  ]);
  const drawn = ambientWindow(sky);
  expect(drawn.nodes).toHaveLength(viewLimit);
  expect(drawn.nodes.map((n) => n.id)).toContain(starOf(everyBoard));
});

const controller = (sky: GraphPage): KnowledgeController => ({
  page: sky,
  accept: vi.fn(),
  pins: [],
  togglePin: vi.fn(),
  busy: false,
  error: null,
  refresh: vi.fn(async () => {}),
  discoverMore: vi.fn(async () => {}),
  lastRefresh: null,
  epoch: 0,
  session: {
    initial: sky,
    refresh: vi.fn(async () => sky),
    retrieve: vi.fn(async () => ({ ...sky, cursors: {} })),
  },
});

it("finds both by title in the explorer's search, drawn as dashboards", async () => {
  vi.useRealTimers();
  const sky = withDashboards(page, [walked, porchBoard, everyBoard]);
  const user = userEvent.setup();
  const { container } = render(
    <Explorer
      knowledge={controller(sky)}
      selected={null}
      onSelect={vi.fn()}
      close={vi.fn()}
      paused
      state="ready"
    />,
  );
  const star = (d: DashboardSummary) =>
    container.querySelector<SVGGElement>(
      `g.graph-node[data-id="${CSS.escape(starOf(d))}"]`,
    )!;
  // Both are on the explorer's sky with a dashboard's look; the lone one sits
  // on the outer ring without taking the quiet colour of other lone stars.
  for (const d of [porchBoard, everyBoard])
    expect(star(d)).toHaveClass("dashboard-star");
  expect(star(everyBoard)).toHaveClass("loose-node");
  expect(star(everyBoard).style.color).not.toBe("rgb(111, 135, 150)");
  expect(star(porchBoard)).not.toHaveClass("loose-node");
  const search = screen.getByRole("textbox", { name: "Search knowledge" });
  const panel = () =>
    screen.getByRole("complementary", { name: "Selected knowledge details" });
  const matches = () =>
    within(within(panel()).getByRole("list", { name: "Matching items" }))
      .getAllByRole("listitem")
      .map((li) => li.querySelector(".row-title")?.textContent);
  await user.type(search, "Porch board");
  expect(matches()).toEqual(["DEMO Porch board"]);
  await user.clear(search);
  await user.type(search, "Every project");
  expect(matches()).toEqual(["DEMO Every project"]);
  // Lit on the sky as any match is.
  expect(star(everyBoard)).toHaveClass("related-node");
});
