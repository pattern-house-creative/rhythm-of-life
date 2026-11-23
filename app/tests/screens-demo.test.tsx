import { afterEach, beforeEach, expect, it, vi } from "vitest";
import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import App, { idleReturnMs } from "../src/App";
import { createDemoScreens } from "../src/demoScreens";
import { defaultScreenName } from "../src/screens";
import { handleGraph } from "../server/graph";
import type { ListenAnswer, SendAnswer } from "../src/screens";
import type { GraphSession } from "../src/liveGraph";
import type { DashboardSummary } from "../src/dashboards";

// An engine's refresh brings back only the overview, where the demo's keeps every star the
// screen holds: a test can have the demo's refresh leave one star out, as an engine's would.
// It can also stand in for the engine's list of dashboards, given the demo's own.
const overview = vi.hoisted(() => ({
  without: null as string | null,
  lists: null as
    | null
    | ((
        real: () => Promise<DashboardSummary[]>,
      ) => Promise<DashboardSummary[]>),
}));
vi.mock("../src/apiClient", async (original) => {
  const real = await original<typeof import("../src/apiClient")>();
  return {
    ...real,
    createApiSession: (demo?: boolean): GraphSession => {
      const session = real.createApiSession(demo);
      const dashboards = session.dashboards!;
      return {
        ...session,
        dashboards: (signal) =>
          overview.lists
            ? overview.lists(() => dashboards(signal))
            : dashboards(signal),
        refresh: async (current, signal) => {
          const page = await session.refresh(current, signal);
          const left = overview.without;
          return left
            ? {
                ...page,
                nodes: page.nodes.filter((n) => n.id !== left),
                edges: page.edges.filter(
                  (e) => e.source !== left && e.target !== left,
                ),
              }
            : page;
        },
      };
    },
  };
});

// Live screens in the demo (docs/dev/screens.md sections 6 and 7): this browser's tabs find
// each other over a BroadcastChannel. Here the app is one tab, and the test plays another,
// "Kitchen iPad", through a demo hub of its own. Fictional stars and dashboards only.
const RealChannel = globalThis.BroadcastChannel;
const hubs: NonNullable<ReturnType<typeof createDemoScreens>>[] = [];
const waits: AbortController[] = [];
beforeEach(() => {
  // tests/setup.ts turns screens off; these tests turn them back on.
  vi.stubGlobal("BroadcastChannel", RealChannel);
});
afterEach(() => {
  overview.without = null;
  overview.lists = null;
  vi.useRealTimers();
  for (const w of waits.splice(0)) w.abort();
  for (const h of hubs.splice(0)) h.screenStop();
  localStorage.clear();
  sessionStorage.clear();
});
const hub = () => {
  const made = createDemoScreens()!;
  hubs.push(made);
  return made;
};
const kitchenId = "kitchenScreen0000000001",
  hallId = "hallScreen00000000000002";
const run = "demo:fictional-household:demo%3Arun";
const glance = "00000000-0000-4000-8000-00000000d0a6";

// A demo star's id, as the app's sky has it.
async function starId(title: string) {
  const page = await (
    await handleGraph(
      new Request("http://localhost/api/graph", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ operation: "discover" }),
      }),
    )
  ).json();
  return page.nodes.find((n: { title: string }) => n.title === title).id;
}
// A tab's listen, answered when a command comes or the wait ends.
function listen(
  h: ReturnType<typeof hub>,
  id: string,
  name: string,
  hold = 25,
) {
  const wait = new AbortController();
  waits.push(wait);
  return h
    .screenListen({ screen: id, name, hold }, wait.signal)
    .catch(() => null) as Promise<ListenAnswer | null>;
}

it("passes a command between two demo screens with the engine's checks and outcome", async () => {
  const kitchen = hub(),
    hall = hub();
  const hallWaits = listen(hall, hallId, "Hall");
  void listen(kitchen, kitchenId, "Kitchen iPad");
  await waitFor(async () =>
    expect(
      (await kitchen.screenList(kitchenId)).map((s) => [
        s.name,
        s.open,
        s.self,
      ]),
    ).toEqual(
      expect.arrayContaining([
        ["Hall", true, false],
        ["Kitchen iPad", true, true],
      ]),
    ),
  );
  const sent = kitchen.screenSend({
    screen: kitchenId,
    to: hallId,
    action: "open_star",
    args: { star: run },
  });
  const claimed = (await hallWaits)!;
  expect(claimed.command).toMatchObject({
    action: "open_star",
    args: { star: run },
    sender: { kind: "screen", screen: kitchenId, name: "Kitchen iPad" },
    result: "delivered",
  });
  await hall.screenDone({
    screen: hallId,
    command: claimed.command!.id,
    result: "shown",
    detail: { summary: "Opened Sunday long run" },
  });
  const answer = (await sent) as Extract<SendAnswer, { ok: true }>;
  expect(answer.ok).toBe(true);
  expect(answer.command).toMatchObject({
    result: "shown",
    detail: { summary: "Opened Sunday long run" },
    ms: { deliver: expect.any(Number), show: expect.any(Number) },
  });
  // A dashboard's star goes as its dashboard, as the engine sends it.
  const next = listen(hall, hallId, "Hall");
  void kitchen.screenSend({
    screen: kitchenId,
    to: hallId,
    action: "open_star",
    args: { star: `rhythm:dashboard:${glance}` },
  });
  expect((await next)!.command).toMatchObject({
    action: "open_dashboard",
    args: { dashboard: glance },
  });
  // Arguments the action doesn't take, and a screen that isn't open, are refused.
  expect(
    await kitchen.screenSend({
      screen: kitchenId,
      to: hallId,
      action: "home",
      args: { star: run },
    }),
  ).toMatchObject({ ok: false });
  expect(
    await kitchen.screenSend({
      screen: kitchenId,
      to: "nobodyScreen000000000003",
      action: "home",
      args: {},
    }),
  ).toEqual({
    ok: false,
    reason:
      "It wasn't sent: that screen isn't open now, or it can't show this.",
  });
});

it("expires a command not claimed within 30 seconds, and never delivers it later", async () => {
  const kitchen = hub(),
    hall = hub();
  await listen(hall, hallId, "Hall", 0);
  void listen(kitchen, kitchenId, "Kitchen iPad");
  await waitFor(async () =>
    expect(
      (await kitchen.screenList(kitchenId)).some((s) => s.id === hallId),
    ).toBe(true),
  );
  const giveUp = new AbortController();
  const sent = kitchen.screenSend(
    { screen: kitchenId, to: hallId, action: "home", args: {} },
    giveUp.signal,
  );
  // The hall is open but not listening; the sender stops waiting before it hears.
  await new Promise((r) => setTimeout(r, 50));
  giveUp.abort();
  expect(await sent).toMatchObject({
    ok: true,
    command: { result: "pending" },
  });
  vi.useFakeTimers({ toFake: ["Date"] });
  vi.setSystemTime(Date.now() + 31_000);
  expect(await listen(hall, hallId, "Hall", 0)).toMatchObject({
    command: null,
  });
});

it("arranges this screen's Home by command, kept on this screen, with Undo", async () => {
  const { send, kitchen, app } = await beside();
  const grid = () =>
    JSON.parse(localStorage.getItem("rhythm-of-life.grid") ?? "null") as {
      parts: object[];
    } | null;
  // Home's map takes no focus.
  await expect(
    send("focus_map", { focus: { query: "run" }, dashboard: null }),
  ).rejects.toThrow("Home's map takes no focus.");
  // A narrow screen keeps today's Home: the grid is saved and the report says so.
  expect(
    await send("move", { part: "map", place: "down", dashboard: null }),
  ).toMatchObject({
    result: "shown",
    detail: {
      summary: "Moved the map down",
      note: "Saved; this screen is too narrow to show layouts.",
    },
  });
  expect(grid()!.parts).toContainEqual({ id: "map", x: 3, y: 4, w: 9, h: 8 });
  // What the screen reports is its grid now.
  await waitFor(async () =>
    expect(
      (await kitchen.screenList(kitchenId)).find((s) => s.id === app.id)!
        .showing.parts,
    ).toContainEqual({ id: "map", kind: "map", x: 3, y: 4, w: 9, h: 8 }),
  );
  expect(await send("undo")).toMatchObject({ result: "shown" });
  expect(grid()).toBeNull();
});

it("stays off where BroadcastChannel is missing, and never calls /api/screens", async () => {
  vi.stubGlobal("BroadcastChannel", undefined);
  expect(createDemoScreens()).toBeNull();
  render(<App />);
  await screen.findByRole("region", { name: "Knowledge constellation" });
  expect(
    vi
      .mocked(fetch)
      .mock.calls.some(([url]) => String(url).includes("screens")),
  ).toBe(false);
});

// The app as one demo tab, and the test's own hub as "Kitchen iPad" beside it.
async function beside() {
  const user = userEvent.setup();
  const view = render(<App />);
  await screen.findByRole("region", { name: "Knowledge constellation" });
  const kitchen = hub();
  let waiting = listen(kitchen, kitchenId, "Kitchen iPad");
  const app = await waitFor(async () => {
    const other = (await kitchen.screenList(kitchenId)).find((s) => !s.self);
    expect(other).toBeDefined();
    return other!;
  });
  const send = async (action: string, args: Record<string, unknown> = {}) => {
    const answer = await kitchen.screenSend({
      screen: kitchenId,
      to: app.id,
      action,
      args,
    });
    if (!answer.ok) throw new Error(answer.reason);
    return answer.command;
  };
  // What the kitchen claims next, as a command another tab sent it.
  const claim = async () => {
    const answer = await waiting;
    waiting = listen(kitchen, kitchenId, "Kitchen iPad");
    return answer?.command ?? null;
  };
  return { ...view, user, kitchen, app, send, claim };
}
const note = () =>
  screen.getByText(/^From the screen “Kitchen iPad”:/).closest("p")!;
const explorer = () =>
  screen.queryByRole("region", { name: "Knowledge explorer" });

it("opens a star from another screen in Focus, with a note and Undo, and reports it", async () => {
  const { app, send } = await beside();
  // This tab is its own demo screen: the browser's id and a suffix of its own.
  expect(app.id).toMatch(/^[A-Za-z0-9_-]{22}-[A-Za-z0-9_-]{8}$/);
  expect(app.name).toBe(defaultScreenName());
  const command = await send("open_star", { star: run });
  expect(command).toMatchObject({
    result: "shown",
    detail: { summary: "Opened Sunday long run" },
  });
  const opened = await screen.findByRole("region", {
    name: "Knowledge explorer",
  });
  expect(
    within(opened).getByRole("heading", { name: "Sunday long run" }),
  ).toBeInTheDocument();
  expect(note()).toHaveTextContent(
    "From the screen “Kitchen iPad”: Opened Sunday long run",
  );
  // The note sits outside the inert content, so its Undo is in reach.
  expect(note().closest("[inert]")).toBeNull();
  fireEvent.click(within(note()).getByRole("button", { name: /Undo/ }));
  await waitFor(() => expect(explorer()).not.toBeInTheDocument());
});

it("searches in Focus and reports the matches, then opens a star over the search", async () => {
  const { send } = await beside();
  const command = await send("search", { query: "  run " });
  expect(command.result).toBe("shown");
  expect(command.detail.matches).toBeGreaterThan(0);
  expect(command.detail.titles).toContain("Sunday long run");
  const opened = (await screen.findByRole("region", {
    name: "Knowledge explorer",
  }))!;
  expect(
    within(opened).getByRole("textbox", { name: "Search knowledge" }),
  ).toHaveValue("run");
  expect(within(opened).getByText("Search: run")).toBeInTheDocument();
  const lit = opened.querySelectorAll("g.graph-node.related-node");
  expect(lit).toHaveLength(command.detail.matches as number);
  // A star from a command clears the search, so its panel shows.
  await send("open_star", { star: run });
  await waitFor(() =>
    expect(
      within(opened).getByRole("textbox", { name: "Search knowledge" }),
    ).toHaveValue(""),
  );
  expect(
    within(opened).getByRole("heading", { name: "Sunday long run" }),
  ).toBeInTheDocument();
});

it("opens a dashboard in the reader, and goes Home", async () => {
  const { send } = await beside();
  expect(await send("open_dashboard", { dashboard: glance })).toMatchObject({
    result: "shown",
    detail: { summary: "Opened Week at a glance" },
  });
  const reader = await screen.findByRole("region", { name: "Source reader" });
  expect(
    within(reader).getByRole("heading", { name: "Week at a glance" }),
  ).toBeInTheDocument();
  expect(await send("home")).toMatchObject({
    result: "shown",
    detail: { summary: "Back to Home" },
  });
  await waitFor(() => expect(explorer()).not.toBeInTheDocument());
  // Something not on this screen's map is not opened, and says so.
  expect(
    await send("open_star", { star: "demo:fictional-household:demo%3Anone" }),
  ).toMatchObject({
    result: "failed",
    detail: { summary: "That star isn't on this screen's map." },
  });
});

it("opens a star beyond this screen's map, fetched from the whole map with its links", async () => {
  const { send } = await beside();
  // The demo's first page of each source is this screen's map; this one is on the next.
  const far = "drive:fictional-household:demo%3Areading";
  expect(await send("open_star", { star: far })).toMatchObject({
    result: "shown",
    detail: { summary: "Opened Reading garden" },
  });
  const opened = await screen.findByRole("region", {
    name: "Knowledge explorer",
  });
  expect(
    within(opened).getByRole("heading", { name: "Reading garden" }),
  ).toBeInTheDocument();
  // Its neighbours came with it, so Focus shows what it links to.
  expect(
    within(opened).getByRole("button", { name: "Home library", exact: true }),
  ).toBeInTheDocument();
  // A refresh that brings back only the overview leaves it out: it is fetched again.
  overview.without = far;
  const neighbours = () =>
    vi
      .mocked(fetch)
      .mock.calls.filter(
        ([url, init]) =>
          url === "/api/graph" &&
          JSON.parse(String(init?.body)).operation === "neighbors",
      ).length;
  expect(neighbours()).toBe(1);
  // Coming back online reads the map again (a second after the last read).
  const later = Date.now() + 5000;
  vi.spyOn(Date, "now").mockReturnValue(later);
  window.dispatchEvent(new Event("online"));
  vi.mocked(Date.now).mockRestore();
  await waitFor(() => expect(neighbours()).toBe(2));
  expect(
    await within(opened).findByRole("heading", { name: "Reading garden" }),
  ).toBeInTheDocument();
});

it("switches the layout and moves tiles, saved for this screen even where too narrow", async () => {
  const { container, send } = await beside();
  expect(await send("layout", { layout: "band" })).toMatchObject({
    result: "shown",
    detail: {
      summary: "Switched to the Rhythm band layout",
      note: "Saved; this screen is too narrow to show layouts.",
    },
  });
  expect(container.querySelector(".app")).toHaveAttribute(
    "data-layout",
    "band",
  );
  expect(localStorage.getItem("rhythm-of-life.layout")).toBe("band");
  expect(
    await send("move_tile", { tile: "recovery", to: "main" }),
  ).toMatchObject({
    result: "shown",
    detail: { summary: "Moved Recovery to the Main place" },
  });
  expect(JSON.parse(localStorage.getItem("rhythm-of-life.tiles")!)).toEqual({
    main: "recovery",
    recovery: "main",
    training: "training",
    changed: "changed",
  });
  // Too narrow for layouts: every tile keeps its own place on screen.
  expect(
    container.querySelector<HTMLElement>(".main-zone")!.style.gridArea,
  ).toBe("");
  // Undo puts back the newest change first.
  expect(await send("undo")).toMatchObject({
    result: "shown",
    detail: { summary: "Undone: Moved Recovery to the Main place" },
  });
  expect(JSON.parse(localStorage.getItem("rhythm-of-life.tiles")!)).toEqual({
    main: "main",
    recovery: "recovery",
    training: "training",
    changed: "changed",
  });
  await send("undo");
  expect(container.querySelector(".app")).toHaveAttribute(
    "data-layout",
    "hero",
  );
  expect(await send("undo")).toMatchObject({
    result: "failed",
    detail: { summary: "Nothing to undo" },
  });
});

it("sets a corner's card for the household, and undoes it", async () => {
  const { send } = await beside();
  expect(
    await send("corner_card", { corner: "training", dashboard: glance }),
  ).toMatchObject({
    result: "shown",
    detail: { summary: "Showing Week at a glance in the Training corner" },
  });
  await waitFor(() =>
    expect(
      document.querySelector(".training-zone [title='Week at a glance card']"),
    ).not.toBeNull(),
  );
  await send("undo");
  await waitFor(() =>
    expect(document.querySelector(".training-zone")).toBeNull(),
  );
});

it("keeps a view a command opened past the idle minutes, until someone uses the screen", async () => {
  const { send } = await beside();
  await send("open_star", { star: run });
  await screen.findByRole("region", { name: "Knowledge explorer" });
  vi.useFakeTimers();
  act(() => {
    vi.advanceTimersByTime(idleReturnMs * 2);
  });
  expect(explorer()).toBeInTheDocument();
  // Someone's own input turns the usual idle return back on.
  fireEvent.pointerDown(window);
  act(() => {
    vi.advanceTimersByTime(idleReturnMs + 50);
  });
  expect(explorer()).not.toBeInTheDocument();
});

it("shows a star on another open screen from its panel, and says how it went", async () => {
  const { user, claim, kitchen } = await beside();
  await user.click(
    await screen.findByRole("button", { name: "Open Explorer" }),
  );
  const opened = await screen.findByRole("region", {
    name: "Knowledge explorer",
  });
  await user.click(
    within(opened).getByRole("button", {
      name: "Rhythm of Life",
      exact: true,
    }),
  );
  await user.click(within(opened).getByRole("button", { name: "Show on…" }));
  const group = await within(opened).findByRole("group", {
    name: "Show on another screen",
  });
  await user.click(
    await within(group).findByRole("button", { name: "Kitchen iPad" }),
  );
  const command = (await claim())!;
  expect(command).toMatchObject({
    action: "open_star",
    args: { star: await starId("Rhythm of Life") },
    sender: { kind: "screen", name: defaultScreenName() },
  });
  await kitchen.screenDone({
    screen: kitchenId,
    command: command.id,
    result: "shown",
    detail: { summary: "Opened Rhythm of Life" },
  });
  expect(
    await within(group).findByText("Shown on Kitchen iPad"),
  ).toBeInTheDocument();
});

it("says so when no other screen is open, and renames this one in Details", async () => {
  const user = userEvent.setup();
  render(<App />);
  await user.click(
    await screen.findByRole("button", { name: "Open Explorer" }),
  );
  const opened = await screen.findByRole("region", {
    name: "Knowledge explorer",
  });
  await user.click(
    within(opened).getByRole("button", {
      name: "Rhythm of Life",
      exact: true,
    }),
  );
  await user.click(within(opened).getByRole("button", { name: "Show on…" }));
  expect(
    await within(opened).findByText("No other screen is open right now."),
  ).toBeInTheDocument();
  await user.keyboard("{Escape}{Escape}{Escape}");
  await user.click(
    await screen.findByRole("button", { name: "Sources and settings" }),
  );
  const field = await screen.findByRole("textbox", {
    name: "This screen's name",
  });
  expect(field).toHaveValue(defaultScreenName());
  await user.clear(field);
  await user.type(field, "Hall{Enter}");
  expect(await screen.findByText("Saved.")).toBeInTheDocument();
  // Kept for this tab, since each demo tab is a screen of its own.
  expect(
    JSON.parse(sessionStorage.getItem("rhythm-of-life.screen-tab")!).name,
  ).toBe("Hall");
});

const training = "00000000-0000-4000-8000-00000000d0a5";
const full = () => screen.queryByRole("region", { name: /, full screen$/ });

it("shows a dashboard full screen until someone exits, and undoes it", async () => {
  const { send, kitchen, app } = await beside();
  // Without a dashboard named, the one open; with none open, it says so.
  expect(await send("full_screen")).toMatchObject({
    result: "failed",
    detail: { summary: "No dashboard is open" },
  });
  expect(await send("full_screen", { dashboard: glance })).toMatchObject({
    result: "shown",
    detail: { summary: "Showing Week at a glance full screen" },
  });
  const shown = await screen.findByRole("region", {
    name: "Week at a glance, full screen",
  });
  expect(
    await within(shown).findByTitle("Week at a glance dashboard"),
  ).toBeInTheDocument();
  // Nothing else on the screen can be reached under it; the note still can.
  expect(document.querySelector(".main-content")).toHaveAttribute("inert");
  expect(note().closest("[inert]")).toBeNull();
  // What it shows, as an assistant reads it.
  const listed = () =>
    kitchen
      .screenList(kitchenId)
      .then((all) => all.find((s) => s.id === app.id)!.showing);
  await waitFor(async () =>
    expect(await listed()).toMatchObject({
      view: "full_screen",
      dashboard: { id: glance, title: "Week at a glance" },
      home: null,
    }),
  );
  // Undo goes back to the sky.
  expect(await send("undo")).toMatchObject({ result: "shown" });
  await waitFor(() => expect(full()).toBeNull());
  // Open in the reader, then full screen with no dashboard named: the one open.
  await send("open_dashboard", { dashboard: glance });
  await screen.findByRole("region", { name: "Source reader" });
  expect(await send("full_screen")).toMatchObject({ result: "shown" });
  await screen.findByRole("region", { name: "Week at a glance, full screen" });
  // Escape leaves it for the reader under it.
  fireEvent.keyDown(window, { key: "Escape" });
  await waitFor(() => expect(full()).toBeNull());
  expect(
    screen.getByRole("region", { name: "Source reader" }),
  ).toBeInTheDocument();
  // A dashboard this screen doesn't have isn't shown.
  expect(
    await send("full_screen", {
      dashboard: "0f8e7d6c-5b4a-4392-8170-6f5e4d3c2b1a",
    }),
  ).toMatchObject({
    result: "failed",
    detail: { summary: "That dashboard isn't among this screen's dashboards." },
  });
});

it("ends a full screen with the next command, and brings it back on Undo", async () => {
  const { send, container } = await beside();
  await send("full_screen", { dashboard: glance });
  await screen.findByRole("region", { name: "Week at a glance, full screen" });
  // A layout change shows on the sky, so the full screen gives way to it.
  await send("layout", { layout: "band" });
  await waitFor(() => expect(full()).toBeNull());
  expect(container.querySelector(".app")).toHaveAttribute(
    "data-layout",
    "band",
  );
  await send("undo");
  await screen.findByRole("region", { name: "Week at a glance, full screen" });
  expect(container.querySelector(".app")).toHaveAttribute(
    "data-layout",
    "hero",
  );
  // Opening a star takes its place.
  await send("open_star", { star: run });
  await screen.findByRole("region", { name: "Knowledge explorer" });
  expect(full()).toBeNull();
});

it("keeps a full screen Undo brought back, after the view it gave way to closes", async () => {
  const { send } = await beside();
  await send("full_screen", { dashboard: glance });
  await screen.findByRole("region", { name: "Week at a glance, full screen" });
  await send("open_star", { star: run });
  await screen.findByRole("region", { name: "Knowledge explorer" });
  await send("undo");
  await screen.findByRole("region", { name: "Week at a glance, full screen" });
  await waitFor(() => expect(explorer()).not.toBeInTheDocument());
  // Long after the explorer closed, what Undo put back is still there.
  await new Promise((resolve) => setTimeout(resolve, 600));
  expect(
    screen.getByRole("region", { name: "Week at a glance, full screen" }),
  ).toBeInTheDocument();
});

it("makes a dashboard this screen's Home, shown at Home, and restores the default on Undo", async () => {
  const { send, kitchen, app } = await beside();
  // At Home now, the new Home shows at once.
  expect(await send("set_home", { dashboard: training })).toMatchObject({
    result: "shown",
    detail: { summary: "Sample training week is this screen's Home now" },
  });
  await screen.findByRole("region", {
    name: "Sample training week, full screen",
  });
  expect(JSON.parse(localStorage.getItem("rhythm-of-life.home")!)).toEqual({
    dashboard: training,
  });
  await waitFor(async () =>
    expect(
      (await kitchen.screenList(kitchenId)).find((s) => s.id === app.id)!
        .showing,
    ).toMatchObject({
      view: "full_screen",
      dashboard: { id: training },
      home: { dashboard: { id: training, title: "Sample training week" } },
    }),
  );
  // Exiting shows the sky; going Home shows the Home dashboard again.
  fireEvent.keyDown(window, { key: "Escape" });
  await waitFor(() => expect(full()).toBeNull());
  await send("open_star", { star: run });
  await screen.findByRole("region", { name: "Knowledge explorer" });
  await send("home");
  await screen.findByRole("region", {
    name: "Sample training week, full screen",
  });
  // Undo goes back to the star, then to the sky, then to the default Home.
  await send("undo");
  await screen.findByRole("region", { name: "Knowledge explorer" });
  await send("undo");
  expect(await send("undo")).toMatchObject({
    result: "shown",
    detail: {
      summary: "Undone: Sample training week is this screen's Home now",
    },
  });
  await waitFor(() => expect(full()).toBeNull());
  expect(localStorage.getItem("rhythm-of-life.home")).toBeNull();
  // No dashboard restores the default Home; one this screen doesn't have is refused.
  expect(await send("set_home")).toMatchObject({
    result: "shown",
    detail: { summary: "This screen's Home is the sky and tiles again" },
  });
  expect(
    await send("set_home", {
      dashboard: "0f8e7d6c-5b4a-4392-8170-6f5e4d3c2b1a",
    }),
  ).toMatchObject({ result: "failed" });
});

it("opens at its Home dashboard, and goes back to it from the sky after the idle minutes", async () => {
  localStorage.setItem(
    "rhythm-of-life.home",
    JSON.stringify({ dashboard: glance }),
  );
  render(<App />);
  await screen.findByRole("region", { name: "Week at a glance, full screen" });
  vi.useFakeTimers();
  fireEvent.click(screen.getByRole("button", { name: "Exit full screen" }));
  expect(full()).toBeNull();
  act(() => {
    vi.advanceTimersByTime(idleReturnMs + 50);
  });
  vi.useRealTimers();
  expect(
    await screen.findByRole("region", {
      name: "Week at a glance, full screen",
    }),
  ).toBeInTheDocument();
});

it("falls back to the default Home when its dashboard was deleted, and says so", async () => {
  const gone = "0f8e7d6c-5b4a-4392-8170-6f5e4d3c2b1a";
  localStorage.setItem(
    "rhythm-of-life.home",
    JSON.stringify({ dashboard: gone }),
  );
  const { kitchen, app, user, send } = await beside();
  await waitFor(() => expect(full()).toBeNull());
  // What it shows reaches the others with its next listen.
  await send("home");
  await waitFor(async () =>
    expect(
      (await kitchen.screenList(kitchenId)).find((s) => s.id === app.id)!
        .showing,
    ).toMatchObject({
      view: "home",
      home: { dashboard: { id: gone }, missing: true },
    }),
  );
  await user.click(
    screen.getByRole("button", { name: "Sources and settings" }),
  );
  expect(
    await screen.findByText(
      "This screen's Home dashboard was deleted, so it shows the sky and tiles.",
    ),
  ).toBeInTheDocument();
  await user.click(
    screen.getByRole("button", { name: "Restore default Home" }),
  );
  expect(
    await screen.findByText(/^This screen's Home is the sky and tiles\./),
  ).toBeInTheDocument();
  expect(localStorage.getItem("rhythm-of-life.home")).toBeNull();
});

it("reports when someone last touched the screen, never a command", async () => {
  const { send, kitchen, app } = await beside();
  const shown = async () =>
    (await kitchen.screenList(kitchenId)).find((s) => s.id === app.id)!.showing;
  await send("search", { query: "run" });
  await waitFor(async () => expect((await shown()).view).toBe("search"));
  expect((await shown()).activeAt).toBeUndefined();
  const before = Date.now();
  fireEvent.pointerDown(window);
  // The next report carries it.
  await send("home");
  await waitFor(async () => expect((await shown()).view).toBe("home"));
  const at = (await shown()).activeAt as string;
  expect(Date.parse(at)).toBeGreaterThanOrEqual(before - 1000);
});

it("exits a full screen shown over the Home dashboard back to that dashboard", async () => {
  localStorage.setItem(
    "rhythm-of-life.home",
    JSON.stringify({ dashboard: glance }),
  );
  const { send } = await beside();
  await screen.findByRole("region", { name: "Week at a glance, full screen" });
  await send("full_screen", { dashboard: training });
  await screen.findByRole("region", {
    name: "Sample training week, full screen",
  });
  // Its exit, Escape and Back each show the Home dashboard it was over, not the sky.
  fireEvent.click(screen.getByRole("button", { name: "Exit full screen" }));
  await screen.findByRole("region", { name: "Week at a glance, full screen" });
  await send("full_screen", { dashboard: training });
  await screen.findByRole("region", {
    name: "Sample training week, full screen",
  });
  fireEvent.keyDown(window, { key: "Escape" });
  await screen.findByRole("region", { name: "Week at a glance, full screen" });
  await send("full_screen", { dashboard: training });
  await screen.findByRole("region", {
    name: "Sample training week, full screen",
  });
  act(() => {
    history.back();
  });
  await screen.findByRole("region", { name: "Week at a glance, full screen" });
  // The Home dashboard's own exit shows the sky.
  fireEvent.click(screen.getByRole("button", { name: "Exit full screen" }));
  await waitFor(() => expect(full()).toBeNull());
});

it("keeps its Home dashboard when the dashboards can't be read, or the list may leave it out", async () => {
  localStorage.setItem(
    "rhythm-of-life.home",
    JSON.stringify({ dashboard: glance }),
  );
  // The engine is down: the read fails, which says nothing about the Home.
  overview.lists = () => Promise.reject(new Error("unavailable"));
  const { kitchen, app, send, unmount } = await beside();
  await screen.findByRole("region", { name: /, full screen$/ });
  await send("home");
  const shown = async () =>
    (await kitchen.screenList(kitchenId)).find((s) => s.id === app.id)!.showing;
  await waitFor(async () =>
    expect(await shown()).toMatchObject({
      view: "full_screen",
      dashboard: { id: glance },
    }),
  );
  expect((await shown()).home).not.toHaveProperty("missing");
  expect(full()).not.toBeNull();
  unmount();
  for (const h of hubs.splice(0)) h.screenStop();
  // As many as the engine lists, the 100 changed most recently: an older Home may be left out.
  overview.lists = async (real) => {
    const [, other] = await real();
    return Array.from({ length: 100 }, (_, n) => ({
      ...other,
      id: `00000000-0000-4000-8000-${String(n).padStart(12, "0")}`,
    }));
  };
  const again = await beside();
  await screen.findByRole("region", { name: /, full screen$/ });
  await again.send("home");
  await waitFor(async () =>
    expect(
      (await again.kitchen.screenList(kitchenId)).find(
        (s) => s.id === again.app.id,
      )!.showing,
    ).toMatchObject({ view: "full_screen", dashboard: { id: glance } }),
  );
  expect(
    (await again.kitchen.screenList(kitchenId)).find(
      (s) => s.id === again.app.id,
    )!.showing.home,
  ).not.toHaveProperty("missing");
});

it("shows a dashboard full screen while the dashboards are still being read", async () => {
  // The first read is slow: a command for a dashboard waits for it, rather than failing.
  let release: () => void = () => {};
  const slow = new Promise<void>((resolve) => (release = resolve));
  overview.lists = (real) => slow.then(real);
  const { send } = await beside();
  const sent = send("full_screen", { dashboard: glance });
  await new Promise((resolve) => setTimeout(resolve, 600));
  release();
  expect(await sent).toMatchObject({
    result: "shown",
    detail: { summary: "Showing Week at a glance full screen" },
  });
  await screen.findByRole("region", { name: "Week at a glance, full screen" });
});

it("arranges the demo's dashboard with a layout by command, with the engine's checks and Undo", async () => {
  // The pages' sides of their channels, so this one can say it has drawn.
  const channels: MessageChannel[] = [];
  const Channel = globalThis.MessageChannel;
  vi.stubGlobal(
    "MessageChannel",
    class extends Channel {
      constructor() {
        super();
        channels.push(this);
      }
    },
  );
  const { send, kitchen, app } = await beside();
  const maps = "00000000-0000-4000-8000-00000000d0a7";
  expect(await send("open_dashboard", { dashboard: maps })).toMatchObject({
    result: "shown",
  });
  await screen.findByRole("group", { name: "Map, near Training" });
  // Nothing is arranged until its page has drawn (10.3).
  await waitFor(() => expect(channels.length).toBeGreaterThan(0));
  await act(async () => {
    for (const c of channels) c.port2.postMessage({ type: "ready" });
    await new Promise((resolve) => setTimeout(resolve, 50));
  });
  const at = (id: string) =>
    document.querySelector<HTMLElement>(`[data-part="${id}"]`)!.style
      .gridColumn;
  expect(at("map")).toBe("1 / span 7");
  // What the screen reports names its parts, which the demo checks commands against.
  await waitFor(async () =>
    expect(
      (await kitchen.screenList(kitchenId)).find((s) => s.id === app.id)!
        .showing.parts,
    ).toContainEqual(expect.objectContaining({ id: "map", kind: "map" })),
  );
  await expect(send("move", { part: "glow", place: "left" })).rejects.toThrow(
    "It wasn't sent: glow is not a part of that dashboard.",
  );
  await expect(
    send("focus_map", { part: "page", focus: null }),
  ).rejects.toThrow("It wasn't sent: page is not a map.");
  expect(await send("move", { part: "map", place: "right" })).toMatchObject({
    result: "shown",
  });
  await waitFor(() => expect(at("map")).toBe("6 / span 7"));
  expect(at("page")).toBe("1 / span 5");
  expect(
    await send("focus_map", { focus: { providers: ["garmin"] } }),
  ).toMatchObject({
    result: "shown",
    detail: { summary: "Focused the map: providers garmin" },
  });
  await screen.findByRole("group", { name: "Map, providers garmin" });
  // Undo puts each back in turn.
  expect(await send("undo")).toMatchObject({ result: "shown" });
  await screen.findByRole("group", { name: "Map, near Training" });
  expect(await send("undo")).toMatchObject({ result: "shown" });
  await waitFor(() => expect(at("map")).toBe("1 / span 7"));
});
