import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import Explorer from "../src/Explorer";
import { emptyGraph } from "../src/liveGraph";
import { record, relation } from "./synthetic";
import type { KnowledgeController } from "../src/useKnowledge";
import type { GraphEdge, GraphNode, GraphPage } from "../src/knowledge";
import type { MemoryClient } from "../src/memories";

const controller = (
  page: GraphPage,
  memories?: MemoryClient,
): KnowledgeController => ({
  page,
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
    initial: page,
    refresh: vi.fn(async () => page),
    retrieve: vi.fn(async () => ({ ...page, cursors: {} })),
    ...(memories ? { memories } : {}),
  },
});
const real = (n: GraphNode) => {
  n.provenance.demo = false;
  return n;
};
// A fictional Drive folder with two files, a document cited by a memory, and
// a memory that links to nothing.
function sky() {
  const folder = real(record("fictional-folder", "drive"));
  folder.type = "theme";
  folder.title = "Fictional folder";
  const doc = real(record("fictional-doc", "drive"));
  doc.type = "document";
  doc.title = "Fictional plan";
  doc.resource.canonicalUrl =
    "https://docs.google.com/document/d/fictional-doc/edit";
  doc.resource.access.canOpen = true;
  const sheet = real(record("fictional-sheet", "drive"));
  sheet.title = "Fictional budget";
  const memory = real(record("fictional-memory"));
  memory.type = "memory";
  memory.title = "Fictional memory about the plan";
  const alone = real(record("fictional-alone"));
  alone.type = "memory";
  alone.title = "Fictional unlinked memory";
  const inFolder = (file: GraphNode): GraphEdge => ({
    ...relation(file, folder),
    id: `in_folder:${file.id}`,
    type: "in_folder",
  });
  const edges = [inFolder(doc), inFolder(sheet), relation(memory, doc)];
  const page = {
    ...emptyGraph(),
    nodes: [folder, doc, sheet, memory, alone],
    edges,
  };
  return { page, folder, doc, sheet, memory, alone };
}
function show(
  page: GraphPage,
  selected: string | null,
  memories?: MemoryClient,
) {
  const onSelect = vi.fn();
  const view = render(
    <Explorer
      knowledge={controller(page, memories)}
      selected={selected}
      onSelect={onSelect}
      close={vi.fn()}
      paused
      state="ready"
    />,
  );
  return { ...view, onSelect };
}
const panel = () =>
  screen.getByRole("complementary", { name: "Selected knowledge details" });

it("names a folder plainly and lists its files as one-line rows", () => {
  const s = sky();
  show(s.page, s.folder.id);
  const details = panel();
  expect(details).toHaveTextContent("Folder · Google Drive");
  expect(details).not.toHaveTextContent("theme /");
  const rows = within(
    within(details).getByRole("list", { name: "Connected items" }),
  ).getAllByRole("listitem");
  expect(rows).toHaveLength(2);
  // Each row is the item's name, with a separate control that explains it.
  expect(
    within(rows[0]).getByRole("button", { name: /^Fictional (plan|budget)$/ }),
  ).toBeInTheDocument();
  const name = rows[0].querySelector(".row-title")!.textContent;
  // Each explanation control names its own record, so rows are distinguishable.
  expect(
    within(rows[0]).getByRole("button", {
      name: `Why connected: ${name} (in this folder)`,
    }),
  ).toBeInTheDocument();
});

it("calls a sample topic a topic and keeps a way to read its sample", () => {
  const topic = record("fictional-sample-topic", "drive");
  topic.type = "theme";
  topic.title = "Fictional sample topic";
  const page = { ...emptyGraph(), nodes: [topic], edges: [] };
  show(page, topic.id);
  expect(panel()).toHaveTextContent("Topic · Google Drive");
  expect(
    within(panel()).getByRole("button", { name: "Read sample source" }),
  ).toBeInTheDocument();
});

it("puts the way to open a document right under its name", () => {
  const s = sky();
  show(s.page, s.doc.id);
  const details = panel();
  const heading = within(details).getByRole("heading", {
    name: "Fictional plan",
  });
  const open = within(details).getByRole("button", {
    name: "Preview original",
  });
  // The main button follows the title, before the summary and connections.
  expect(
    heading.compareDocumentPosition(open) & Node.DOCUMENT_POSITION_FOLLOWING,
  ).toBeTruthy();
  const connections = within(details).getByRole("list", {
    name: "Connected items",
  });
  expect(
    open.compareDocumentPosition(connections) &
      Node.DOCUMENT_POSITION_FOLLOWING,
  ).toBeTruthy();
  expect(
    within(details).getByRole("link", { name: /Open in Google Drive/ }),
  ).toHaveAttribute("href", s.doc.resource.canonicalUrl);
});

it("opens documents in a reader beside the panel, and Escape closes it first", async () => {
  const s = sky();
  const user = userEvent.setup();
  const { onSelect } = show(s.page, s.doc.id);
  await user.click(screen.getByRole("button", { name: "Preview original" }));
  const reader = screen.getByRole("region", { name: "Source reader" });
  expect(
    within(reader).getByTitle("Fictional plan in Google Drive"),
  ).toHaveAttribute(
    "src",
    "https://docs.google.com/document/d/fictional-doc/preview",
  );
  // The panel keeps the document's details and connections while reading.
  expect(panel()).toHaveTextContent("Fictional plan");
  await user.keyboard("{Escape}");
  expect(
    screen.queryByRole("region", { name: "Source reader" }),
  ).not.toBeInTheDocument();
  expect(onSelect).not.toHaveBeenCalled();
  expect(panel()).toHaveTextContent("Fictional plan");
});

it("says when something has no links yet", () => {
  const s = sky();
  show(s.page, s.alone.id);
  expect(panel()).toHaveTextContent("No linked records yet");
});

it("searches in place: every star stays and the matches light up", async () => {
  const s = sky();
  const user = userEvent.setup();
  const { container } = show(s.page, null);
  const stars = () => container.querySelectorAll("svg.graph-svg g.graph-node");
  const before = stars().length;
  await user.type(
    screen.getByRole("textbox", { name: "Search knowledge" }),
    "plan",
  );
  expect(stars()).toHaveLength(before);
  const lit = [...container.querySelectorAll("g.graph-node.related-node")].map(
    (g) => g.getAttribute("data-id"),
  );
  expect(lit.sort()).toEqual([s.doc.id, s.memory.id].sort());
  const results = within(panel()).getByRole("list", { name: "Matching items" });
  expect(within(results).getAllByRole("listitem")).toHaveLength(2);
  // Escape clears the search before anything else.
  await user.keyboard("{Escape}");
  expect(container.querySelectorAll("g.graph-node.related-node")).toHaveLength(
    0,
  );
  expect(screen.getByRole("textbox", { name: "Search knowledge" })).toHaveValue(
    "",
  );
});

it("clears a search before the selection, and keeps the selection", async () => {
  const s = sky();
  const user = userEvent.setup();
  const { onSelect } = show(s.page, s.doc.id);
  await user.type(
    screen.getByRole("textbox", { name: "Search knowledge" }),
    "budget",
  );
  expect(
    within(panel()).getByRole("list", { name: "Matching items" }),
  ).toBeInTheDocument();
  await user.keyboard("{Escape}");
  expect(onSelect).not.toHaveBeenCalled();
  expect(
    within(panel()).getByRole("heading", { name: "Fictional plan" }),
  ).toBeInTheDocument();
});

it("lights search matches, not the traced path, while searching in Trace", async () => {
  const s = sky();
  const user = userEvent.setup();
  const { container } = show(s.page, s.memory.id);
  await user.click(screen.getByRole("button", { name: "Trace", exact: true }));
  await user.selectOptions(screen.getByLabelText("Trace to"), s.folder.id);
  const lit = () =>
    [...container.querySelectorAll("g.graph-node.related-node")]
      .map((g) => g.getAttribute("data-id"))
      .sort();
  expect(lit()).toEqual([s.memory.id, s.doc.id, s.folder.id].sort());
  await user.type(
    screen.getByRole("textbox", { name: "Search knowledge" }),
    "budget",
  );
  expect(lit()).toEqual([s.sheet.id]);
});

// A fictional Second Brain project with two memories: one pair linked by
// Second Brain on its own, plus a similarity suggestion.
function projectSky() {
  const ideas = real(record("fictional-project-ideas"));
  ideas.type = "project";
  ideas.title = "Fictional ideas";
  const a = real(record("fictional-idea-a"));
  a.type = "memory";
  a.title = "Fictional idea A";
  const b = real(record("fictional-idea-b"));
  b.type = "memory";
  b.title = "Fictional idea B";
  const c = real(record("fictional-idea-c"));
  c.type = "memory";
  c.title = "Fictional idea C";
  const member = (m: GraphNode): GraphEdge => ({
    ...relation(m, ideas),
    id: `in_project:${m.id}`,
    type: "in_project",
  });
  const auto: GraphEdge = {
    ...relation(a, b),
    id: "relates_to:auto",
    type: "relates_to",
    verification: "suggested",
    evidence: [
      {
        resourceId: a.id,
        excerpt: "Fictional",
        locator: "second-brain:relates_to:inferred",
        retrievedAt: "2026-10-18T12:01:00Z",
      },
    ],
  };
  // Shaped like Second Brain's evidence, so only its type marks it as similar.
  const similar: GraphEdge = {
    ...relation(a, c),
    id: "similar_to:a-c",
    type: "similar_to",
    verification: "suggested",
    evidence: auto.evidence,
  };
  const page = {
    ...emptyGraph(),
    nodes: [ideas, a, b, c],
    edges: [member(a), member(b), auto, similar],
  };
  return { page, ideas, a, b, c };
}

it("draws a Second Brain project as a star its memories hang from", () => {
  const s = projectSky();
  const { container } = show(s.page, s.ideas.id);
  const star = container.querySelector(
    `svg.graph-svg g.graph-node[data-id="${s.ideas.id}"]`,
  )!;
  expect(star).toHaveClass("project-star");
  expect(star.querySelector(".project-glyph")).not.toBeNull();
  // Memories are small brains.
  expect(
    container.querySelector(
      `svg.graph-svg g.graph-node[data-id="${s.a.id}"] .memory-glyph`,
    ),
  ).not.toBeNull();
  const details = panel();
  expect(details).toHaveTextContent("Project · Second Brain");
  // A project has no source of its own to open; it lists its memories.
  expect(
    within(details).queryByRole("button", {
      name: /Preview original|Read full memory|View source record|Read sample source/,
    }),
  ).toBeNull();
  expect(
    within(details).getAllByRole("button", { name: /\(in this project\)$/ }),
  ).toHaveLength(2);
});

it("shows automatic links and similarity suggestions, dashed, outside Discover", () => {
  // A star found alike in meaning once showed "Connected 0" outside Discover.
  const s = projectSky();
  const { container } = show(s.page, s.a.id);
  const details = panel();
  expect(details).toHaveTextContent("Connected 3");
  const rows = within(
    within(details).getByRole("list", { name: "Connected items" }),
  );
  // Its project, the auto-linked memory and the similar one. With every
  // setting off Second Brain's relates_to reads as it always has.
  expect(
    rows.getByRole("button", {
      name: "Why connected: Fictional idea B (relates to · auto)",
    }),
  ).toBeInTheDocument();
  expect(
    rows.getByRole("button", {
      name: "Why connected: Fictional idea C (similar in meaning)",
    }),
  ).toBeInTheDocument();
  for (const other of [s.b, s.c])
    expect(
      container.querySelector(
        `path.graph-edge[data-source="${s.a.id}"][data-target="${other.id}"]`,
      ),
    ).toHaveAttribute("stroke-dasharray");
});

it("names upstream's link types in plain words only while the engine serves the memories", async () => {
  // SB-P4-11: the plain words (D-191's linkWords) come with the memory tools
  // on; off, the link and its explanation read as they did before.
  const off = projectSky();
  const offView = show(off.page, off.a.id);
  const offRows = within(
    within(panel()).getByRole("list", { name: "Connected items" }),
  );
  expect(
    offRows.getByRole("button", {
      name: "Why connected: Fictional idea B (relates to · auto)",
    }),
  ).toBeInTheDocument();
  await userEvent.click(
    offRows.getByRole("button", {
      name: "Why connected: Fictional idea B (relates to · auto)",
    }),
  );
  expect(
    within(panel()).getByText(/^relates to · suggested · /),
  ).toBeInTheDocument();
  offView.unmount();

  const on = projectSky();
  const served: MemoryClient = {
    available: vi.fn(async () => true),
    call: vi.fn(),
  };
  show(on.page, on.a.id, served);
  const onRows = within(
    within(panel()).getByRole("list", { name: "Connected items" }),
  );
  const related = await onRows.findByRole("button", {
    name: "Why connected: Fictional idea B (related to · auto)",
  });
  expect(served.available).toHaveBeenCalled();
  await userEvent.click(related);
  expect(
    within(panel()).getByText(/^related to · suggested · /),
  ).toBeInTheDocument();
});

it("hides an auto link from the sky once it is dismissed in Discover", async () => {
  const s = projectSky();
  const user = userEvent.setup();
  const { container } = show(s.page, s.a.id);
  await user.click(
    screen.getByRole("button", { name: "Discover", exact: true }),
  );
  const autoEdge = () =>
    container.querySelector(
      `path.graph-edge[data-source="${s.a.id}"][data-target="${s.b.id}"]`,
    );
  expect(autoEdge()).not.toBeNull();
  const card = screen
    .getAllByRole("article")
    .find((x) => x.textContent?.includes("Fictional idea B"))!;
  expect(card).toHaveTextContent("Linked automatically by Second Brain");
  await user.click(within(card).getByRole("button", { name: "Dismiss" }));
  expect(autoEdge()).toBeNull();
});

it("follows a data star's topic links outside Discover, dashed", () => {
  // Data stars link to the records that name their topic; the explorer once
  // dropped those links, so a data star showed "Connected 0".
  const star = real(record("alex:training", "rhythm", "data:garmin"));
  star.type = "record";
  star.title = "Training";
  const plan = real(record("fictional-race-plan"));
  plan.type = "memory";
  plan.title = "Fictional race plan";
  const topic: GraphEdge = {
    ...relation(star, plan),
    id: "about:training-plan",
    type: "about",
    directed: false,
    verification: "inferred",
    evidence: [
      {
        resourceId: star.id,
        excerpt: "Mentions training",
        locator: "catalog:topic",
        retrievedAt: "2026-10-28T12:00:00Z",
      },
    ],
  };
  const { container } = show(
    { ...emptyGraph(), nodes: [star, plan], edges: [topic] },
    star.id,
  );
  const rows = within(
    within(panel()).getByRole("list", { name: "Connected items" }),
  );
  expect(
    rows.getByRole("button", { name: "Fictional race plan" }),
  ).toBeInTheDocument();
  expect(
    container.querySelector(
      `path.graph-edge[data-source="${star.id}"][data-target="${plan.id}"]`,
    ),
  ).toHaveAttribute("stroke-dasharray");
});

it("keeps a live install's sky after the app went offline and came back", () => {
  // Offline, the app marks its state "error" while it is cleared; the network
  // coming back brings the data back but not "ready". Only the demo previews it.
  const s = sky();
  const knowledge = controller(s.page);
  const view = render(
    <Explorer
      knowledge={knowledge}
      selected={null}
      onSelect={vi.fn()}
      close={vi.fn()}
      paused
      state="error"
    />,
  );
  expect(screen.queryByText("Sample unavailable")).not.toBeInTheDocument();
  expect(screen.getByText(/5 items · tap a star/)).toBeInTheDocument();
  view.unmount();
  // The demo's own "Source error" preview still empties its sky.
  render(
    <Explorer
      knowledge={{
        ...knowledge,
        session: {
          ...knowledge.session!,
          demo: { advance: vi.fn(), setUnavailable: vi.fn() },
        },
      }}
      selected={null}
      onSelect={vi.fn()}
      close={vi.fn()}
      paused
      state="error"
    />,
  );
  expect(screen.getByText("Sample unavailable")).toBeInTheDocument();
});
