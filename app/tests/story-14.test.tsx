// Story 14 (SB-P6-06): graph stars, existing editable dashboards, health views and their links to the original source
// keep working, at a phone's size and at a desktop's. The engine's side, on the synthetic parity corpus with memory
// scopes off and on, is engine/tests/Knowledge.Tests/StoryFourteenTests.cs; scripts/story-14.mjs runs both with the
// existing tests the suite keeps. Fictional data only.
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import App from "../src/App";
import Explorer from "../src/Explorer";
import DashboardView from "../src/DashboardView";
import { emptyGraph } from "../src/liveGraph";
import { catalogProvider } from "../src/instance";
import { createDemoDashboards } from "../src/demoDashboards";
import type { Dashboard, DashboardData, DashboardWriteAnswer } from "../src/dashboards";
import type { KnowledgeController } from "../src/useKnowledge";
import type { GraphNode, GraphPage } from "../src/knowledge";
import { record, relation } from "./synthetic";
import { withSampleHealth } from "./sampleHealth";

// A household with a fictional glucose source connected and the glucose tile in the main slot, as
// tests/glucose-panel.test.tsx has it: the demo itself connects none.
vi.mock("../src/demoReadings", async (original) => {
  const real = await original<typeof import("../src/demoReadings")>();
  const { sampleShapes } = await import("./sampleShapes");
  return {
    ...real,
    demoReadings: (id: string, today?: Date) =>
      real.readingsFrom(sampleShapes, id, today) ?? real.demoReadings(id, today),
  };
});
vi.mock("../src/demoDashboards", async (original) => {
  const real = await original<typeof import("../src/demoDashboards")>();
  return {
    ...real,
    createDemoDashboards: () => {
      const boards = real.createDemoDashboards();
      boards.setCard("main", null);
      return boards;
    },
  };
});

// The two sizes the app lays itself out for (App.tsx, Explorer.tsx): a phone, where the reader is a sheet that covers
// the sky, and a wide desktop screen.
const sizes = {
  phone: (query: string) => query.includes("max-width: 760px"),
  desktop: (query: string) => query.includes("min-width: 1100px"),
} as const;
let standard: ((query: string) => MediaQueryList) | undefined;
function at(size: keyof typeof sizes) {
  standard ??= vi.mocked(window.matchMedia).getMockImplementation() as (query: string) => MediaQueryList;
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

const controller = (page: GraphPage): KnowledgeController => ({
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
  },
});
const real = (n: GraphNode) => {
  n.provenance.demo = false;
  return n;
};
// A fictional Drive document a memory cites, and a person's data star (the catalog's, from a CSV import).
function sky() {
  const doc = real(record("fictional-quote", "drive"));
  doc.type = "document";
  doc.title = "Fictional heating quote";
  doc.resource.canonicalUrl = "https://drive.example.invalid/file/d/fictional-quote/view";
  doc.resource.access.canOpen = true;
  const memory = real(record("fictional-install"));
  memory.type = "memory";
  memory.title = "Fictional install day";
  const data = real(record("alex:sleep", "csv"));
  data.type = "record";
  data.title = "Fictional sleep";
  data.resource.identity = { provider: catalogProvider(), account: "data:csv", externalId: "alex:sleep" };
  return { page: { ...emptyGraph(), nodes: [doc, memory, data], edges: [relation(memory, doc)] }, doc, memory, data };
}
function explore(page: GraphPage, selected: string) {
  return render(
    <Explorer
      knowledge={controller(page)}
      selected={selected}
      onSelect={vi.fn()}
      close={vi.fn()}
      paused
      state="ready"
    />,
  );
}
const details = () => screen.getByRole("complementary", { name: "Selected knowledge details" });

// The page's side of a dashboard's message channel (as tests/dashboard-writes.test.tsx has it): the app makes one when
// the frame loads and hands the frame its second port.
const channels: MessageChannel[] = [];
const Channel = globalThis.MessageChannel;
function capture() {
  vi.stubGlobal(
    "MessageChannel",
    class extends Channel {
      constructor() {
        super();
        channels.push(this);
      }
    },
  );
  return channels.length;
}
async function framePage(before: number) {
  await waitFor(() => expect(channels.length).toBeGreaterThan(before));
  const port = channels.at(-1)!.port2;
  const heard: { type: string; [k: string]: unknown }[] = [];
  port.onmessage = (e) => heard.push(e.data);
  return {
    say: (m: unknown) => port.postMessage(m),
    answer: async (n: number) => {
      await waitFor(() => expect(heard.some((h) => h.type === "written" && h.id === n)).toBe(true));
      return heard.find((h) => h.type === "written" && h.id === n)!.answer as DashboardWriteAnswer & { summary?: string };
    },
  };
}

describe.each(["phone", "desktop"] as const)("story 14 on a %s", (size) => {
  beforeAll(async () => {
    await import("../src/Details");
  }, 30_000);
  beforeEach(() => at(size));
  afterEach(() => {
    if (standard) vi.mocked(window.matchMedia).mockImplementation(standard);
  });

  it("opens a Drive star's original from its reader, and a data star's readings", async () => {
    const s = sky();
    const { unmount } = explore(s.page, s.doc.id);
    expect(within(details()).getByRole("heading", { name: "Fictional heating quote" })).toBeInTheDocument();
    expect(within(details()).getByRole("link", { name: /Open in Google Drive/ })).toHaveAttribute(
      "href",
      s.doc.resource.canonicalUrl,
    );
    // The memory that cites it leads to it.
    expect(within(details()).getByRole("list", { name: "Connected items" })).toHaveTextContent("Fictional install day");
    // Its reader opens at both sizes: beside the details on a desktop, which stay in use, and over them on a phone,
    // which sets them aside.
    const panel = details();
    expect(screen.queryByRole("region", { name: "Source reader" })).toBeNull();
    await userEvent.setup().click(panel.querySelector<HTMLButtonElement>(".item-actions .primary-action")!);
    expect(await screen.findByRole("region", { name: "Source reader" })).toBeInTheDocument();
    await waitFor(() => expect(panel.hasAttribute("inert")).toBe(size === "phone"));
    if (size === "desktop") expect(panel).toBeVisible();
    unmount();
    const data = explore(s.page, s.data.id);
    expect(within(details()).getByRole("button", { name: /Show readings/ })).toBeInTheDocument();
    data.unmount();
    // A star that may not be opened offers no link to its original.
    s.doc.resource.access.canOpen = false;
    explore(s.page, s.doc.id);
    expect(within(details()).queryByRole("link", { name: /Open in Google Drive/ })).toBeNull();
  });

  it("lets someone allow edits on an existing dashboard, apply its declared write and undo it", async () => {
    const boards = createDemoDashboards();
    const id = boards.list()[0].id;
    const dashboard: Dashboard = { ...boards.get(id)!, editsAllowed: false };
    const data: DashboardData = boards.data(id)!;
    const write = {
      id: "22222222-2222-4222-8222-222222222222",
      dashboard: id,
      version: dashboard.version,
      name: "effort",
      kind: "log",
      summary: "Training log: effort 6 of 10 for Oct 29",
      writtenAt: "2026-10-29T01:00:00.000Z",
      undoneAt: null,
    };
    const session = {
      dashboard: vi.fn(async () => dashboard),
      dashboardData: vi.fn(async () => data),
      dashboardAllowEdits: vi.fn(async (_i: string, _v: number, on: boolean) => on),
      dashboardWrite: vi.fn(async (): Promise<DashboardWriteAnswer> => ({ ok: true, write })),
      dashboardUndo: vi.fn(async (): Promise<DashboardWriteAnswer> => ({
        ok: true,
        write: { ...write, undoneAt: "2026-10-29T01:01:00.000Z" },
      })),
    };
    const before = capture();
    render(<DashboardView id={id} session={session} onOpenStar={() => {}} />);
    await screen.findByTitle(`${dashboard.title} dashboard`);
    const p = await framePage(before);
    const allow = screen.getByRole("switch", { name: "Allow edits" });
    await act(async () => fireEvent.click(allow));
    await waitFor(() => expect(allow).toHaveAttribute("aria-checked", "true"));
    expect(session.dashboardAllowEdits).toHaveBeenCalledWith(id, dashboard.version, true);
    await act(async () => p.say({ type: "write", id: 1, name: "effort", change: { metric: "rpe", value: 6, unit: "score" } }));
    expect(await p.answer(1)).toEqual({ ok: true, summary: write.summary });
    expect(await screen.findByRole("status")).toHaveTextContent(write.summary);
    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Undo" })));
    expect(session.dashboardUndo).toHaveBeenCalledWith(write.id);
    expect(await screen.findByText(`Undone: ${write.summary}`)).toBeInTheDocument();
  });

  it("opens the glucose panel from its tile, with the reading and the record it came from", async () => {
    withSampleHealth();
    const user = userEvent.setup();
    render(<App />);
    await screen.findByRole("region", { name: "Knowledge constellation" });
    await user.click(await screen.findByRole("button", { name: /Glucose 112/ }));
    const dialog = await screen.findByRole("dialog", { name: "glucose" }, { timeout: 10000 });
    expect(within(dialog).getByRole("region", { name: "Current glucose" })).toHaveTextContent("112");
    expect(dialog).toHaveTextContent(/Record demo:/);
  }, 20_000);
});
