import { afterEach, beforeAll, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import App from "../src/App";
import { demoData } from "../src/demo";
import { demoInsights, demoRuns } from "../src/demoInsights";
import type { DashboardSummary } from "../src/dashboards";
import { ambientWindow } from "../src/graphView";
import type { GraphSession } from "../src/liveGraph";
import type { Fold, Part, PartKind } from "../src/parts";
import { nativeParts } from "../src/parts/nativeParts";
import type { PartsContext } from "../src/parts/nativeParts";
import { sampleHealth, withSampleHealth } from "./sampleHealth";
import { population } from "./synthetic";

// The native parts of a dashboard's grid (docs/dev/screens.md 10.3): Home's
// own working pieces, drawn in a cell, each saying quietly what isn't there.

beforeAll(async () => {
  await import("../src/DashboardCard");
}, 30_000);
afterEach(() => localStorage.clear());

const map = population(12);
function context(over: Partial<PartsContext> = {}): PartsContext {
  return {
    data: sampleHealth,
    sources: sampleHealth.sources,
    state: "ready",
    blocked: false,
    demo: true,
    session: null,
    sky: {
      page: map,
      overview: ambientWindow(map),
      pins: [],
      demo: true,
      loading: false,
      readAt: null,
    },
    insights: { current: null, insights: [], runs: [] },
    dashboards: { list: [], complete: true },
    paused: false,
    openPanel: vi.fn(),
    openInsight: vi.fn(),
    openSources: vi.fn(),
    openStar: vi.fn(),
    openDashboard: vi.fn(),
    explore: vi.fn(),
    ...over,
  };
}
const part = (kind: PartKind, more: Partial<Part> = {}): Part => ({
  id: kind,
  kind,
  x: 0,
  y: 0,
  w: 4,
  h: 4,
  ...more,
});
function draw(p: Part, c = context(), fold?: Fold) {
  const node: ReactNode = nativeParts(c)(p, fold);
  return render(<div data-testid="cell">{node}</div>);
}

it("draws Home's glucose tile in a part, and opens its panel from the part", async () => {
  const user = userEvent.setup();
  const c = context();
  const { container } = draw(part("glucose"), c);
  const tile = screen.getByRole("button", { name: /GLUCOSE 112/ });
  expect(tile.closest(".native-part")).toHaveClass("part-glucose");
  expect(screen.getByTestId("dashboard-glucose")).toHaveAttribute(
    "data-demo",
    "true",
  );
  await user.click(tile);
  // The panel grows from the part itself: its own layout id, one per part.
  const [view, from] = vi.mocked(c.openPanel).mock.calls[0];
  expect(view).toBe("glucose");
  expect(from).toMatch(/^part-/);
  expect(container.querySelectorAll(".native-part")).toHaveLength(1);
});

it("draws the same glucose, recovery and training tiles Home draws", async () => {
  // One implementation: Home’s corners and a dashboard’s parts are the same
  // component, so for the same data they draw the same thing. (The glucose
  // summary reads its archive through the session, which a part here lacks.)
  const text = (tile: Element | null | undefined) => {
    const copy = tile?.cloneNode(true) as Element | undefined;
    copy?.querySelector(".glucose-history")?.remove();
    return copy?.textContent;
  };
  withSampleHealth();
  const home = render(<App />);
  await screen.findByRole("button", { name: /GLUCOSE 112/ });
  const atHome = ["glucose", "recovery", "training"].map((k) =>
    text(home.container.querySelector(`.${k}-zone .tile-button`)),
  );
  home.unmount();
  const drawn = ["glucose", "recovery", "training"].map((k) => {
    const { container, unmount } = draw(part(k as PartKind));
    const shown = text(container.querySelector(".tile-button"));
    unmount();
    return shown;
  });
  expect(atHome.every(Boolean)).toBe(true);
  expect(drawn).toEqual(atHome);
});

it("keeps its place and says so quietly when the data isn't there", async () => {
  const user = userEvent.setup();
  // The demo connects no glucose source and no Garmin.
  const c = context({ data: demoData, sources: demoData.sources });
  draw(part("glucose"), c);
  expect(screen.getByText(/A dashboard can live here/)).toBeInTheDocument();
  await user.click(screen.getByRole("button", { name: "Sources & settings" }));
  expect(c.openSources).toHaveBeenCalled();
  for (const kind of ["recovery", "training"] as const) {
    const { container, unmount } = draw(part(kind), c);
    const cell = container.querySelector(`.part-${kind}`)!;
    expect(cell).toHaveTextContent(kind.toUpperCase());
    expect(cell).toHaveTextContent(
      "Appears once a training source is connected",
    );
    // A quiet tile isn't a way into a panel.
    expect(within(cell as HTMLElement).queryByRole("button")).toBeNull();
    unmount();
  }
});

it("says Nothing yet for changes and insights, with the last run", () => {
  const runs = demoRuns();
  const c = context({
    data: demoData,
    sky: { ...context().sky, overview: { nodes: [], edges: [] } },
    insights: { current: null, insights: [], runs },
  });
  const changed = draw(part("changed"), c);
  const cell = changed.container.querySelector(".part-changed")!;
  expect(cell).toHaveTextContent("Nothing yet");
  // Home's corner welcomes the first records; a part says it quietly.
  expect(cell).not.toHaveTextContent("Knowledge is taking shape");
  changed.unmount();
  const insights = draw(part("insights", { w: 3, h: 2 }), c);
  const own = insights.container.querySelector(".part-insights")!;
  expect(own).toHaveTextContent("DAILY INSIGHTS");
  expect(own).toHaveTextContent("Nothing yet");
  expect(own.querySelector(".insight-run")).not.toBeNull();
});

it("shows the current insight and the way to the earlier ones", async () => {
  const user = userEvent.setup();
  const all = demoInsights();
  const [current, ...earlier] = all;
  const c = context({
    insights: { current, insights: all, runs: [] },
  });
  draw(part("insights", { w: 3, h: 2 }), c);
  await user.click(
    screen.getByRole("button", {
      name: new RegExp(`Open insight .*${current.title}`),
    }),
  );
  expect(c.openInsight).toHaveBeenCalledWith(current.id, expect.any(String));
  if (earlier.length) {
    await user.click(screen.getByRole("button", { name: /Earlier insights/ }));
    expect(c.openInsight).toHaveBeenLastCalledWith(
      expect.not.stringMatching(`^${current.id}$`),
      expect.any(String),
    );
  }
});

it("draws nothing for a page, a kind it doesn't know, or while access is lost", () => {
  const c = context();
  expect(nativeParts(c)(part("page"))).toBeNull();
  // A later piece's kind: its cell stays empty.
  const later = { ...part("glucose"), kind: "weather" } as unknown as Part;
  expect(draw(later, c).getByTestId("cell")).toBeEmptyDOMElement();
  const lost = draw(part("glucose"), context({ data: null }));
  expect(lost.getAllByTestId("cell").at(-1)).toBeEmptyDOMElement();
});

it("marks a folded part, which takes its content's height", () => {
  const { container } = draw(part("recovery"), context(), "one");
  expect(container.querySelector(".native-part")).toHaveAttribute(
    "data-fold",
    "one",
  );
  const saved = draw(part("recovery"), context());
  expect(saved.container.querySelector(".native-part")).not.toHaveAttribute(
    "data-fold",
  );
});

it("draws the demo's preview of a missing source without readings", () => {
  draw(part("glucose"), context({ blocked: true }));
  expect(screen.getByTestId("dashboard-glucose")).toHaveTextContent("—");
  expect(screen.getByText("Trend unavailable")).toBeInTheDocument();
});

// A saved dashboard as the list summarises it.
const summary = (id: string, title: string) =>
  ({ id, title, version: 1, latest: 1 }) as DashboardSummary;
function cardSession(known: Record<string, string>) {
  return {
    dashboard: vi.fn(async (id: string) =>
      known[id]
        ? {
            ...summary(id, known[id]),
            html: "<main>Fictional card</main>",
          }
        : null,
    ),
    dashboardData: vi.fn(async () => null),
  } as unknown as GraphSession;
}

it("draws another dashboard's card, and opens that dashboard", async () => {
  const user = userEvent.setup();
  const id = "11111111-1111-4111-8111-111111111111";
  const c = context({
    session: cardSession({ [id]: "Fictional week" }),
    dashboards: { list: [summary(id, "Fictional week")], complete: true },
  });
  draw(part("card", { dashboard: id, w: 3, h: 3 }), c);
  expect(await screen.findByTitle("Fictional week card")).toHaveAttribute(
    "sandbox",
    "allow-scripts",
  );
  await user.click(
    screen.getByRole("button", { name: "Open the Fictional week dashboard" }),
  );
  expect(c.openDashboard).toHaveBeenCalledWith(id);
});

it("says a card's dashboard is unavailable, with no title, when it was deleted or can't be read", async () => {
  const gone = "22222222-2222-4222-8222-222222222222";
  // Missing from the whole list: deleted, or not this viewer's to read; the list
  // doesn't tell the two apart, so neither names anything.
  const deleted = draw(
    part("card", { dashboard: gone }),
    context({
      session: cardSession({}),
      dashboards: { list: [], complete: true },
    }),
  );
  expect(
    deleted.getByText("That dashboard is unavailable"),
  ).toBeInTheDocument();
  expect(deleted.queryByText(/deleted/)).toBeNull();
  expect(deleted.queryByRole("button", { name: /^Open the/ })).toBeNull();
  deleted.unmount();
  // Not among the dashboards read so far, and this viewer can't read it.
  draw(
    part("card", { dashboard: gone }),
    context({
      session: cardSession({}),
      dashboards: { list: [], complete: false },
    }),
  );
  expect(
    await screen.findByText("That dashboard is unavailable"),
  ).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: /^Open the/ })).toBeNull();
  await waitFor(() => expect(screen.queryByTitle(/ card$/)).toBeNull());
});

it("shows a card part's dashboard when it can be read, though not yet listed", async () => {
  const id = "33333333-3333-4333-8333-333333333333";
  draw(
    part("card", { dashboard: id }),
    context({
      session: cardSession({ [id]: "Fictional plan" }),
      dashboards: { list: [], complete: false },
    }),
  );
  expect(await screen.findByTitle("Fictional plan card")).toBeInTheDocument();
});

it("keeps a part that breaks to its own cell", () => {
  const quiet = vi.spyOn(console, "error").mockImplementation(() => {});
  const broken = {
    ...sampleHealth,
    current: undefined,
  } as unknown as typeof sampleHealth;
  draw(part("glucose"), context({ data: broken }));
  expect(
    screen.getByText("This part can’t be drawn right now."),
  ).toBeInTheDocument();
  quiet.mockRestore();
});
