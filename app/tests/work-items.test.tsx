// Work items in the app (the engine's memory.work, migration 079): a project's backlog kept as memories. The Memories list is
// the backlog: its work filter keeps work items only, by state, type and priority, with the list's own count beside it, and a work
// item's row and its opened memory say what it is where a memory shows its kind and importance. On the sky a work item is a star
// drawn as a small box, and "Work items on the sky" in Sources & settings leaves those stars off this screen's skies: display
// only, kept on this browser, nothing deleted, a chosen or pinned one still drawn. There is no board, no popup and no new card on
// Home. An engine that sends no work, and one with memory.work off, reads exactly as before. At a phone's size and at a desktop's.
// Fictional data only.
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import App from "../src/App";
import Explorer from "../src/Explorer";
import { emptyGraph } from "../src/liveGraph";
import type { GraphSession } from "../src/liveGraph";
import {
  ambientWindow,
  withoutWork,
  workFinished,
  workLabel,
  workOf,
} from "../src/graphView";
import type { KnowledgeController } from "../src/useKnowledge";
import type { GraphEdge, GraphNode, GraphPage } from "../src/knowledge";
import {
  cleanMemory,
  cleanMemoryAnswer,
  cleanWork,
  memoryRequest,
  workWords,
} from "../src/memories";
import type { Memory, MemoryChange, MemoryClient } from "../src/memories";
import { happened } from "../src/MemoryTimeline";
import { workWho } from "../src/memoryScreen";
import { MapPart } from "../src/parts/MapPart";
import type { MapSky } from "../src/parts/MapPart";
import { saveWorkItems, savedWorkItems, watchSettings } from "../src/screens";
import {
  defaultSettings,
  fakeEngine,
  memory,
  project,
  uuid,
} from "./memoryFake";
import { record, relation } from "./synthetic";

const sizes = {
  phone: (query: string) => query.includes("max-width: 760px"),
  desktop: (query: string) => query.includes("min-width: 1100px"),
} as const;
let standard: ((query: string) => MediaQueryList) | undefined;
function at(size: keyof typeof sizes) {
  standard ??= vi.mocked(window.matchMedia).getMockImplementation() as (
    query: string,
  ) => MediaQueryList;
  vi.mocked(window.matchMedia).mockImplementation(
    (query: string) =>
      ({
        matches: sizes[size](query),
        media: query,
        addListener: vi.fn(),
        removeListener: vi.fn(),
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
      }) as unknown as MediaQueryList,
  );
}
afterEach(() => {
  if (standard) vi.mocked(window.matchMedia).mockImplementation(standard);
  localStorage.clear();
});

// The contract's own fictional examples; tests run from app/, beside contracts/.
const fixture = (name: string) =>
  JSON.parse(
    readFileSync(resolve(process.cwd(), "../contracts/fixtures", name), "utf8"),
  );
const controller = (
  page: GraphPage,
  extra: Partial<GraphSession> = {},
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
    ...extra,
  },
});
const plot = { id: uuid(50), slug: "wendle", name: "Wendle" };
// The Wendle plot's backlog beside one ordinary memory of the project.
const backlog = (): Memory[] => [
  memory(1, "DEMO: the Wendle plot gate latch sticks.", {
    project: plot,
    work: { type: "bug", state: "backlog", priority: 2 },
  }),
  memory(2, "DEMO: a rain gauge for the Wendle plot.", {
    project: plot,
    work: { type: "feature", state: "active", priority: 1 },
  }),
  memory(3, "DEMO: label the Wendle seed trays.", {
    project: plot,
    work: { type: "task", state: "done", priority: null },
  }),
  memory(4, "DEMO: the Wendle plot is number 14.", { project: plot }),
];
// The fake engine answers a list as it is asked; this one keeps the work filter the engine keeps (Memories.cs, Filtered).
function working(memories: Memory[], on = true) {
  const fake = fakeEngine({
    memories,
    projects: [project(50, "wendle", { name: "Wendle" })],
    settings: { ...defaultSettings, work: on },
  });
  const call = fake.client.call as unknown as ReturnType<typeof vi.fn>;
  const plain = call.getMockImplementation()!;
  const kept = (m: Memory, r: Record<string, unknown>) =>
    (r.work === undefined &&
      r.workType === undefined &&
      r.workPriority === undefined) ||
    (m.work !== undefined &&
      (r.work === undefined ||
        r.work === "all" ||
        (r.work === "open"
          ? !["done", "cancelled"].includes(m.work.state)
          : m.work.state === r.work)) &&
      (r.workType === undefined || m.work.type === r.workType) &&
      (r.workPriority === undefined ||
        (m.work.priority ?? 0) === r.workPriority));
  call.mockImplementation(async (request: Record<string, unknown>) => {
    const rest = Object.fromEntries(
      Object.entries(request).filter(([name]) => !name.startsWith("work")),
    );
    const answer = await plain(rest);
    // The request the view sent is the one recorded, its work filter included.
    fake.calls[fake.calls.length - 1] = request;
    if (request.op === "list" && answer.ok)
      return {
        ...answer,
        memories: (answer.memories as Memory[]).filter((m) => kept(m, request)),
      };
    if (request.op === "tags" && answer.ok)
      return {
        ...answer,
        count: memories.filter(
          (m) =>
            kept(m, request) &&
            (!request.project || m.project?.slug === request.project),
        ).length,
      };
    return answer;
  });
  return fake;
}
async function opened(client: MemoryClient, nodes: GraphNode[] = []) {
  const page = { ...emptyGraph(), nodes };
  render(
    <Explorer
      selected={null}
      onSelect={vi.fn()}
      close={vi.fn()}
      paused
      state="ready"
      knowledge={controller(page, { memories: client })}
    />,
  );
  await userEvent.click(
    await screen.findByRole("button", { name: "Memories" }),
  );
  return screen.findByRole("region", { name: "Memories" });
}

// A native memory's star, as the engine names one, and a work item's.
function star(n: number, title: string, work?: GraphNode["work"]): GraphNode {
  const s = record(uuid(n), "memory", "memory");
  s.title = title;
  s.overview = title;
  s.provenance.demo = false;
  return work ? { ...s, work } : s;
}
const link = (a: GraphNode, b: GraphNode): GraphEdge => ({
  ...relation(a, b),
  id: `relates_to:${a.id}:${b.id}`,
  type: "relates_to",
  verification: "verified",
});
function sky() {
  const note = star(10, "DEMO: the Wendle plot is number 14.");
  const open = star(11, "DEMO: the Wendle plot gate latch sticks.", {
    type: "bug",
    state: "active",
    priority: 2,
  });
  const done = star(12, "DEMO: label the Wendle seed trays.", {
    type: "task",
    state: "done",
    priority: null,
  });
  const dropped = star(13, "DEMO: a second Wendle water butt.", {
    type: "feature",
    state: "cancelled",
    priority: null,
  });
  const page: GraphPage = {
    ...emptyGraph(),
    nodes: [note, open, done, dropped],
    edges: [link(open, note), link(done, note)],
  };
  return { page, note, open, done, dropped };
}
const drawn = (container: HTMLElement, id: string) =>
  container.querySelector<SVGGElement>(
    `svg.graph-svg g.graph-node[data-id="${id}"]`,
  );
const lines = (container: HTMLElement) =>
  container.querySelectorAll(
    "svg.graph-svg path.graph-edge, svg.graph-svg line.graph-edge, svg.graph-svg .graph-edge",
  ).length;

it("takes a list's and its tags' work filter in a request, and nothing else's", () => {
  for (const op of ["list", "tags"]) {
    expect(
      memoryRequest({ op, work: "open", workType: "bug", workPriority: 2 }),
    ).toEqual({ op, work: "open", workType: "bug", workPriority: 2 });
    expect(memoryRequest({ op, work: "all", workPriority: 0 })).not.toBeNull();
    expect(memoryRequest({ op, work: "done" })).not.toBeNull();
    expect(memoryRequest({ op, work: "blocked" })).toBeNull();
    expect(memoryRequest({ op, work: true })).toBeNull();
    expect(memoryRequest({ op, workType: "epic" })).toBeNull();
    expect(memoryRequest({ op, workPriority: 5 })).toBeNull();
    expect(memoryRequest({ op, workPriority: "2" })).toBeNull();
  }
  expect(memoryRequest({ op: "trash", work: "open" })).toBeNull();
  expect(memoryRequest({ op: "review", workType: "bug" })).toBeNull();
});

it("reads a work item's properties from the contract's fields, and a memory without them as before", () => {
  const listed = fixture("app_memories.work.example.json");
  const read = cleanMemoryAnswer("list", listed) as { memories: Memory[] };
  expect(read.memories.map((m) => m.work)).toEqual([
    {
      type: "feature",
      state: "active",
      priority: 1,
      assignee: { kind: "agent", name: "Rain gauge - claude-code" },
      claim: {
        label: "Rain gauge - claude-code",
        claimedAt: "2026-11-02T09:30:00.123456+00:00",
        expiresAt: "2026-11-02T11:30:00.123456+00:00",
      },
    },
    {
      type: "improvement",
      state: "ready",
      priority: null,
      assignee: { kind: "person", name: "sam" },
    },
  ]);
  expect(workWords(read.memories[0].work!)).toBe(
    "Feature · active · priority 1",
  );
  expect(workWords(read.memories[1].work!)).toBe("Improvement · ready");
  // Whose it is and who holds it now (migration 081), in the install's time zone; nobody's and unclaimed says nothing.
  expect(workWho(read.memories[0].work!)).toBe(
    "for Rain gauge - claude-code · claimed by Rain gauge - claude-code until 11:30",
  );
  expect(workWho(read.memories[1].work!)).toBe("for Sam");
  expect(workWho({ type: "bug", state: "active", priority: null })).toBe("");
  // An assignee or a claim that isn't the contract's is no memory at all; one the engine leaves out, or null, is none.
  const first = listed.memories[0];
  for (const work of [
    { ...first.work, assignee: { kind: "robot", name: "DEMO" } },
    { ...first.work, assignee: { kind: "agent", name: "" } },
    { ...first.work, assignee: { kind: "agent", name: "a".repeat(121) } },
    { ...first.work, assignee: { kind: "agent", name: "DEMO\nlabel" } },
    { ...first.work, claim: { label: "DEMO", claimedAt: "soon" } },
    { ...first.work, claim: { ...first.work.claim, label: 7 } },
  ])
    expect(cleanMemory({ ...first, work })).toBeNull();
  expect(
    cleanMemory({
      ...first,
      work: { ...first.work, assignee: null, claim: null },
    })?.work,
  ).toEqual({ type: "feature", state: "active", priority: 1 });
  // A person's release of a claim is a write like any other, by the memory's id alone.
  expect(memoryRequest({ op: "release_claim", id: first.id })).toEqual({
    op: "release_claim",
    id: first.id,
  });
  expect(memoryRequest({ op: "release_claim" })).toBeNull();
  expect(
    memoryRequest({ op: "release_claim", id: first.id, claim: uuid(9) }),
  ).toBeNull();
  expect(
    cleanMemoryAnswer(
      "release_claim",
      fixture("app_memories.release_claim.example.json"),
    ),
  ).toMatchObject({
    ok: true,
    written: { op: "release_claim", revision: 2, unchanged: false },
  });
  // An ordinary memory, and every memory of an engine that sends no work, has none.
  const plain = fixture("app_memories.list.example.json").memories[0];
  expect(cleanMemory(plain)).not.toHaveProperty("work");
  expect(cleanMemory({ ...plain, work: null })).not.toHaveProperty("work");
  // Anything but the contract's words is no memory at all, as every other field's.
  for (const work of [
    { type: "epic", state: "active", priority: 1 },
    { type: "bug", state: "blocked", priority: 1 },
    { type: "bug", state: "active", priority: 5 },
    { type: "bug", state: "active" },
    "bug",
  ])
    expect(cleanMemory({ ...plain, work })).toBeNull();
  expect(cleanWork(undefined)).toBeNull();
  // The settings say whether the install has work items on; an older engine's, which say nothing, read as off.
  const settings = fixture("app_memories.settings.example.json");
  expect(cleanMemoryAnswer("settings", settings)).toMatchObject({
    work: false,
  });
  expect(
    cleanMemoryAnswer("settings", { ...settings, work: true }),
  ).toMatchObject({ work: true });
  const { work: _, ...older } = settings;
  expect(cleanMemoryAnswer("settings", older)).toMatchObject({ work: false });
  expect(cleanMemoryAnswer("settings", { ...settings, work: "on" })).toBeNull();
});

it("says a work item's changes in a memory's history in plain words", () => {
  const change = (event: string, fields: string[]): MemoryChange => ({
    revision: 2,
    event,
    at: "2026-11-02T09:30:00+00:00",
    person: null,
    channel: "mcp",
    client: "DEMO client",
    fields,
    status: "draft",
    preview: null,
    held: false,
    kept: true,
    to: null,
  });
  expect(happened(change("work_set", ["work"]))).toBe("Saved as a work item");
  expect(happened(change("work_changed", ["work"]))).toBe("Made a work item");
  expect(
    happened(change("work_changed", ["work_state", "work_priority"])),
  ).toBe("Its work changed (state, priority)");
});

it("reads a star's work from the contract's words alone", () => {
  const graph = fixture("app_graph.work.example.json");
  const [item, plain] = graph.nodes as GraphNode[];
  expect(workOf(item)).toEqual({
    type: "feature",
    state: "active",
    priority: 1,
  });
  expect(workLabel(workOf(item)!)).toBe("feature, active");
  expect(workOf(plain)).toBeNull();
  for (const n of fixture("app_graph.example.json").nodes)
    expect(workOf(n)).toBeNull();
  for (const work of [
    null,
    "bug",
    { type: "epic", state: "active", priority: 1 },
    { type: "bug", state: "blocked", priority: 1 },
    { type: "bug", state: "active", priority: 0 },
    { type: "bug", state: "active", priority: 2.5 },
  ])
    expect(workOf({ ...plain, work } as unknown as GraphNode)).toBeNull();
  // A priority left out reads as none; done and cancelled are finished work.
  expect(
    workOf({
      ...plain,
      work: { type: "bug", state: "review" },
    } as unknown as GraphNode),
  ).toEqual({ type: "bug", state: "review", priority: null });
  expect(workLabel({ type: "bug", state: "review", priority: null })).toBe(
    "bug, in review",
  );
  expect(workFinished({ type: "bug", state: "done", priority: null })).toBe(
    true,
  );
  expect(
    workFinished({ type: "bug", state: "cancelled", priority: null }),
  ).toBe(true);
  expect(workFinished({ type: "bug", state: "review", priority: null })).toBe(
    false,
  );
});

it("leaves work items off a sky only while asked, keeps the chosen and pinned ones, and changes nothing it is given", () => {
  const s = sky();
  const { nodes, edges } = s.page;
  // On, and with no work item to leave out, the same arrays: nothing is laid out again.
  expect(withoutWork(nodes, edges, true)).toEqual({ nodes, edges });
  expect(withoutWork(nodes, edges, true).nodes).toBe(nodes);
  expect(withoutWork([s.note], [], false).nodes).toHaveLength(1);
  const off = withoutWork(nodes, edges, false);
  expect(off.nodes.map((n) => n.id)).toEqual([s.note.id]);
  expect(off.edges).toEqual([]);
  const kept = withoutWork(nodes, edges, false, new Set([s.open.id]));
  expect(kept.nodes.map((n) => n.id)).toEqual([s.note.id, s.open.id]);
  expect(kept.edges.map((e) => e.id)).toEqual([link(s.open, s.note).id]);
  // What it was given is as it was.
  expect(nodes).toHaveLength(4);
  expect(edges).toHaveLength(2);
});

it("keeps the choice on this browser, and another tab's change is heard", () => {
  expect(savedWorkItems()).toBe(true);
  saveWorkItems(false);
  expect(localStorage.getItem("rhythm-of-life.work-items")).toBe("off");
  expect(savedWorkItems()).toBe(false);
  saveWorkItems(true);
  expect(localStorage.getItem("rhythm-of-life.work-items")).toBeNull();
  localStorage.setItem("rhythm-of-life.work-items", "nonsense");
  expect(savedWorkItems()).toBe(true);
  const changed = vi.fn();
  const stop = watchSettings(changed);
  window.dispatchEvent(
    new StorageEvent("storage", { key: "rhythm-of-life.work-items" }),
  );
  expect(changed).toHaveBeenCalledTimes(1);
  stop();
});

it("offers the switch in Sources & settings only while there is a work item to hide, or they are hidden", async () => {
  const user = userEvent.setup();
  // The demo's map has no work item: no switch, and Sources & settings is as it was.
  const first = render(<App />);
  await user.click(
    await screen.findByRole("button", { name: "Sources and settings" }),
  );
  expect(
    await screen.findByRole("button", {
      name: "Automatic links on the sky: on",
    }),
  ).toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: /Work items on the sky/ }),
  ).not.toBeInTheDocument();
  first.unmount();
  // Hidden on this screen: the switch is there to bring them back, says nothing is deleted, and stays off after a reload.
  saveWorkItems(false);
  const second = render(<App />);
  await user.click(
    await screen.findByRole("button", { name: "Sources and settings" }),
  );
  const off = await screen.findByRole("button", {
    name: "Work items on the sky: off",
  });
  expect(off).toHaveAttribute("aria-pressed", "false");
  expect(off).toHaveAccessibleDescription(
    "Work items are hidden on the sky. Nothing is deleted: the Memories view still lists them, and one you choose still shows.",
  );
  off.focus();
  await user.keyboard("{Enter}");
  const on = screen.getByRole("button", { name: "Work items on the sky: on" });
  expect(on).toHaveAttribute("aria-pressed", "true");
  expect(on).toHaveAccessibleDescription(
    "Work items are a project's tasks, bugs, features and improvements: stars drawn as a small box.",
  );
  expect(localStorage.getItem("rhythm-of-life.work-items")).toBeNull();
  await user.click(on);
  expect(localStorage.getItem("rhythm-of-life.work-items")).toBe("off");
  second.unmount();
});

describe.each(["phone", "desktop"] as const)("at a %s's size", (size) => {
  it("filters the list to a project's work by state, type and priority, with the count beside it", async () => {
    at(size);
    const fake = working(backlog());
    const region = await opened(fake.client);
    const list = await within(region).findByRole("list", {
      name: "Recent memories",
    });
    expect(await within(list).findAllByRole("listitem")).toHaveLength(4);
    // A work item's row says what it is, before what every row says; an ordinary memory's is as it was.
    const row = (words: RegExp) =>
      within(list).getByText(words).closest("li") as HTMLElement;
    expect(
      within(row(/gate latch sticks/)).getByText(
        /^Bug · backlog · priority 2 · Draft/,
      ),
    ).toBeInTheDocument();
    expect(
      within(row(/seed trays/)).getByText(/^Task · done · Draft/),
    ).toBeInTheDocument();
    expect(within(row(/number 14/)).getByText(/^Draft/)).toBeInTheDocument();

    const filters = within(region).getByRole("group", {
      name: "Filter these memories",
    });
    const work = await within(filters).findByLabelText("Work");
    expect(
      within(work)
        .getAllByRole("option")
        .map((o) => o.textContent),
    ).toEqual([
      "Any memory",
      "Work items",
      "Open work",
      "Backlog",
      "Ready",
      "Active",
      "In review",
      "Done",
      "Cancelled",
    ]);
    await userEvent.selectOptions(work, "open");
    await waitFor(() =>
      expect(fake.asked("list").at(-1)).toEqual({
        op: "list",
        limit: 20,
        work: "open",
      }),
    );
    expect(fake.asked("tags").at(-1)).toEqual({ op: "tags", work: "open" });
    await waitFor(async () =>
      expect(await within(list).findAllByRole("listitem")).toHaveLength(2),
    );
    expect(
      await within(region).findByText("2 memories match."),
    ).toBeInTheDocument();

    await userEvent.selectOptions(
      within(filters).getByLabelText("Work type"),
      "feature",
    );
    await userEvent.selectOptions(
      within(filters).getByLabelText("Priority"),
      "1",
    );
    await userEvent.selectOptions(
      within(filters).getByLabelText("Collection"),
      "shared:wendle",
    );
    await waitFor(() =>
      expect(fake.asked("list").at(-1)).toEqual({
        op: "list",
        limit: 20,
        work: "open",
        workType: "feature",
        workPriority: 1,
        project: "wendle",
        scope: "shared",
      }),
    );
    await waitFor(() =>
      expect(within(list).getAllByRole("listitem")).toHaveLength(1),
    );
    expect(within(list).getByText(/rain gauge/)).toBeInTheDocument();
    // Those nobody ranked, alone.
    await userEvent.selectOptions(
      within(filters).getByLabelText("Priority"),
      "0",
    );
    await userEvent.selectOptions(
      within(filters).getByLabelText("Work type"),
      "",
    );
    await userEvent.selectOptions(work, "all");
    await waitFor(() =>
      expect(fake.asked("list").at(-1)).toMatchObject({
        work: "all",
        workPriority: 0,
      }),
    );
    await waitFor(() =>
      expect(within(list).getAllByRole("listitem")).toHaveLength(1),
    );
    expect(within(list).getByText(/seed trays/)).toBeInTheDocument();
    // Clear filters takes the work filter off with the rest.
    await userEvent.click(
      within(filters).getByRole("button", { name: "Clear filters" }),
    );
    await waitFor(() =>
      expect(fake.asked("list").at(-1)).toEqual({ op: "list", limit: 20 }),
    );
    await waitFor(() =>
      expect(within(list).getAllByRole("listitem")).toHaveLength(4),
    );

    // Opened, a work item says what it is where a memory shows its kind and importance; nothing here changes it.
    await userEvent.click(within(list).getByText(/rain gauge/));
    expect(
      await within(region).findByText(
        "Work item: Feature · active · priority 1",
      ),
    ).toBeInTheDocument();
    expect(
      fake.calls.every((c) =>
        [
          "list",
          "tags",
          "settings",
          "projects",
          "get",
          "history",
          "connections",
          "queues",
          "changes",
          "review",
        ].includes(String(c.op)),
      ),
    ).toBe(true);
  });

  it("says whose a work item is and who holds it, and lets a person release the claim", async () => {
    at(size);
    const items = backlog();
    items[1] = {
      ...items[1],
      work: {
        ...items[1].work!,
        assignee: { kind: "agent", name: "Rain gauge - claude-code" },
        claim: {
          label: "Rain gauge - claude-code",
          claimedAt: "2026-11-02T09:30:00+00:00",
          expiresAt: "2026-11-02T11:30:00+00:00",
        },
      },
    };
    items[0] = {
      ...items[0],
      work: { ...items[0].work!, assignee: { kind: "person", name: "sam" } },
    };
    const fake = working(items);
    const region = await opened(fake.client);
    const list = await within(region).findByRole("list", {
      name: "Recent memories",
    });
    const row = (words: RegExp) =>
      within(list).getByText(words).closest("li") as HTMLElement;
    await within(list).findByText(/rain gauge/);
    expect(
      within(row(/gate latch sticks/)).getByText(
        /^Bug · backlog · priority 2 · for Sam · Draft/,
      ),
    ).toBeInTheDocument();
    expect(
      within(row(/rain gauge/)).getByText(
        /^Feature · active · priority 1 · for Rain gauge - claude-code · claimed by Rain gauge - claude-code until 11:30 · Draft/,
      ),
    ).toBeInTheDocument();

    // Opened, it says who holds it until when, and Release claim lets it go; the memory itself does not change.
    await userEvent.click(within(list).getByText(/rain gauge/));
    expect(
      await within(region).findByText(
        "Work item: Feature · active · priority 1 · for Rain gauge - claude-code",
      ),
    ).toBeInTheDocument();
    const note = within(region).getByText(
      /^Claimed by Rain gauge - claude-code until/,
    ).parentElement as HTMLElement;
    expect(note).toHaveAttribute("role", "note");
    await userEvent.click(
      within(note).getByRole("button", { name: "Release claim" }),
    );
    expect(fake.asked("release_claim")).toEqual([
      { op: "release_claim", id: items[1].id },
    ]);
    expect(
      await within(region).findByText(
        "Claim released: another session may claim it now.",
      ),
    ).toBeInTheDocument();
    await waitFor(() =>
      expect(
        within(region).queryByText(/^Claimed by Rain gauge/),
      ).not.toBeInTheDocument(),
    );
    expect(
      within(region).getByText(
        "Work item: Feature · active · priority 1 · for Rain gauge - claude-code",
      ),
    ).toBeInTheDocument();
    expect(fake.asked("update")).toHaveLength(0);
  });

  it("offers no work filter while the install has work items off, or says nothing of them", async () => {
    at(size);
    for (const fake of [
      working([memory(4, "DEMO: the Wendle plot is number 14.")], false),
      fakeEngine({
        memories: [memory(4, "DEMO: the Wendle plot is number 14.")],
      }),
    ]) {
      const view = await opened(fake.client);
      const filters = within(view).getByRole("group", {
        name: "Filter these memories",
      });
      await within(filters).findByLabelText("Status");
      await waitFor(() =>
        expect(fake.asked("settings").length).toBeGreaterThan(0),
      );
      for (const name of ["Work", "Work type", "Priority"])
        expect(within(filters).queryByLabelText(name)).not.toBeInTheDocument();
      document.body.innerHTML = "";
    }
  });

  it("draws a work item as a small box that says what it is, quieter once finished", () => {
    at(size);
    const s = sky();
    const { container } = render(
      <Explorer
        selected={null}
        onSelect={vi.fn()}
        close={vi.fn()}
        paused
        state="ready"
        knowledge={controller(s.page)}
      />,
    );
    const open = drawn(container, s.open.id)!;
    expect(open).toHaveClass("work-star", "work-active");
    expect(open).toHaveAccessibleName(
      "Explore DEMO: the Wendle plot gate latch sticks. (work item: bug, active)",
    );
    expect(open.querySelector("rect.work-glyph")).not.toBeNull();
    expect(open.querySelector(".memory-glyph")).toBeNull();
    expect(open.style.opacity).toBe("1");
    // Finished work is quieter, and reads apart by its mark: a tick when done, a bar when cancelled.
    const done = drawn(container, s.done.id)!;
    expect(done).toHaveClass("work-star", "work-done");
    expect(done.style.opacity).toBe("0.6");
    expect(done.querySelectorAll("path")).toHaveLength(1);
    expect(done.querySelector("circle[r='1.5']")).toBeNull();
    expect(open.querySelector("circle[r='1.5']")).not.toBeNull();
    expect(drawn(container, s.dropped.id)).toHaveClass("work-cancelled");
    // An ordinary memory's star is as it was.
    const note = drawn(container, s.note.id)!;
    expect(note).not.toHaveClass("work-star");
    expect(note.querySelector(".memory-glyph")).not.toBeNull();
    expect(note).toHaveAccessibleName(
      "Explore DEMO: the Wendle plot is number 14.",
    );
  });

  it("leaves work items off the explorer's sky while they are hidden, but the one someone chose", async () => {
    at(size);
    const s = sky();
    const shown = (selected: string | null, workItems: boolean) =>
      render(
        <Explorer
          selected={selected}
          onSelect={vi.fn()}
          close={vi.fn()}
          paused
          state="ready"
          knowledge={controller(s.page)}
          workItems={workItems}
        />,
      );
    const hidden = shown(null, false);
    expect(drawn(hidden.container, s.note.id)).not.toBeNull();
    for (const item of [s.open, s.done, s.dropped])
      expect(drawn(hidden.container, item.id)).toBeNull();
    expect(
      screen.getByText("Work items hidden · a chosen one still shows"),
    ).toBeInTheDocument();
    hidden.unmount();
    // Chosen, a hidden work item is on the sky with its own link, and its panel says what it is.
    const chosen = shown(s.open.id, false);
    expect(drawn(chosen.container, s.open.id)).not.toBeNull();
    expect(drawn(chosen.container, s.done.id)).toBeNull();
    expect(
      within(
        screen.getByRole("complementary", {
          name: "Selected knowledge details",
        }),
      ).getByText("Work item: bug, active, priority 2"),
    ).toBeInTheDocument();
    chosen.unmount();
    // On, every one is drawn and nothing says they are hidden.
    const all = shown(null, true);
    for (const item of [s.note, s.open, s.done, s.dropped])
      expect(drawn(all.container, item.id)).not.toBeNull();
    expect(
      screen.queryByText("Work items hidden · a chosen one still shows"),
    ).not.toBeInTheDocument();
  });

  it("leaves them off a map part's sky too, and Home's, by the same choice", () => {
    at(size);
    const s = sky();
    const part = (workItems?: boolean) => {
      const mapSky: MapSky = {
        page: s.page,
        overview: ambientWindow(s.page),
        pins: [],
        demo: true,
        loading: false,
        readAt: null,
        workItems,
      };
      return render(
        <MapPart
          sky={mapSky}
          blocked={false}
          paused
          own
          selected={null}
          onStar={vi.fn()}
          onExplore={vi.fn()}
        />,
      );
    };
    const on = part();
    expect(drawn(on.container, s.open.id)).not.toBeNull();
    on.unmount();
    const off = part(false);
    expect(drawn(off.container, s.note.id)).not.toBeNull();
    expect(drawn(off.container, s.open.id)).toBeNull();
    expect(drawn(off.container, s.done.id)).toBeNull();
    expect(lines(off.container)).toBe(0);
  });
});
