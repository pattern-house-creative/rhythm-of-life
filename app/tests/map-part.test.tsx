import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { expect, it, vi } from "vitest";
import { act, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ambientWindow } from "../src/graphView";
import { catalogProvider, demoInstance, setInstance } from "../src/instance";
import type { GraphPage } from "../src/knowledge";
import type { FocusedPage } from "../src/mapFocus";
import type { MapFocus } from "../src/parts";
import { MapPart } from "../src/parts/MapPart";
import type { MapSky } from "../src/parts/MapPart";
import { useMapPartShown } from "../src/parts/mapParts";
import { population, record, relation } from "./synthetic";

// A dashboard's map part (docs/dev/screens.md 10.3, 10.4, 10.9): the real sky
// in a view of its own, narrowed only by its own focus.

const map = population(12);
const sky = (over: Partial<MapSky> = {}): MapSky => ({
  page: map,
  overview: ambientWindow(map),
  pins: [],
  demo: true,
  loading: false,
  readAt: null,
  ...over,
});
const stars = (root: ParentNode) =>
  [...root.querySelectorAll("g.graph-node")].map(
    (g) => (g as SVGGElement).dataset.id,
  );
const page = (nodes: GraphPage["nodes"], matched: number): FocusedPage => ({
  nodes,
  edges: [],
  nextCursor: matched > nodes.length ? `o:${nodes.length}` : null,
  completeness: "complete",
  issues: [],
  matched,
});
function drawMap(
  props: Partial<Parameters<typeof MapPart>[0]> & { focus?: MapFocus | null },
) {
  const onStar = vi.fn(),
    onExplore = vi.fn();
  const view = render(
    <section className="part-map">
      <MapPart
        sky={sky()}
        blocked={false}
        paused={false}
        onStar={onStar}
        onExplore={onExplore}
        {...props}
      />
    </section>,
  );
  return { ...view, onStar, onExplore };
}

it("draws what Home's sky draws when it has no focus", async () => {
  const user = userEvent.setup();
  const { container, onExplore } = drawMap({});
  expect(screen.getByText(/^KNOWLEDGE/)).toHaveTextContent("KNOWLEDGE · DEMO");
  expect(screen.getByText(/12 stars ·/)).toBeInTheDocument();
  expect(stars(container)).toHaveLength(12);
  await user.click(screen.getByRole("button", { name: /Open Explorer/ }));
  expect(onExplore).toHaveBeenCalledWith(null);
});

it("counts as a map part while it shows, so the explorer's sky rests", () => {
  // What the probe saw each time it drew.
  const seen: boolean[] = [];
  function Probe() {
    seen.push(useMapPartShown());
    return null;
  }
  const shown = () => seen.at(-1);
  render(<Probe />);
  expect(shown()).toBe(false);
  // Home’s own sky is not a part.
  const home = drawMap({ own: false });
  expect(shown()).toBe(false);
  home.unmount();
  const part = drawMap({});
  expect(shown()).toBe(true);
  part.unmount();
  expect(shown()).toBe(false);
});

it("filters the demo's map in the browser, which holds all of it", () => {
  const { container } = drawMap({ focus: { query: "idea 1" } });
  // "Fictional idea 1", "… 10" and "… 11".
  expect(stars(container)).toHaveLength(3);
  expect(screen.getByText(/matching “idea 1”/)).toHaveTextContent(
    "matching “idea 1” · 3 stars",
  );
  // A focused part's caption has its own way in; the sky's entry is Home's.
  expect(screen.queryByRole("button", { name: /Open Explorer/ })).toBe(null);
});

it("says when nothing on the map matches its focus, naming the focus", () => {
  const { container } = drawMap({ focus: { query: "zebra" } });
  expect(stars(container)).toHaveLength(0);
  expect(
    screen.getByRole("heading", {
      name: "Nothing on the map matches this focus",
    }),
  ).toBeInTheDocument();
  expect(screen.getAllByText("matching “zebra”").length).toBeGreaterThan(0);
  expect(screen.queryByText(/Knowledge graph unavailable/)).toBeNull();
});

it("asks the engine for a live focused part, and counts every match", async () => {
  let answer!: (p: FocusedPage) => void;
  const read = vi.fn(
    (_cursor: string | null, _signal: AbortSignal) =>
      new Promise<FocusedPage>((r) => (answer = r)),
  );
  const { container } = drawMap({
    sky: sky({ demo: false }),
    focus: { providers: ["drive"] },
    read,
  });
  expect(read).toHaveBeenCalledWith(null, expect.any(AbortSignal));
  // Waiting is never shown as unavailable.
  expect(screen.getByText("Loading the sky")).toBeInTheDocument();
  expect(screen.queryByText(/Knowledge graph unavailable/)).toBeNull();
  await act(async () => answer(page(map.nodes.slice(0, 2), 1240)));
  expect(stars(container)).toHaveLength(2);
  expect(screen.getByText(/providers drive/)).toHaveTextContent(
    "providers drive · 2 of 1,240 stars",
  );
});

it("reads its focus again with the map, on coming back, and when it changes", async () => {
  const read = vi.fn(async () => page(map.nodes.slice(0, 1), 1));
  const props = {
    focus: { providers: ["drive"] } as MapFocus,
    read,
  };
  const { rerender } = drawMap({ ...props, sky: sky({ demo: false }) });
  await act(async () => {});
  expect(read).toHaveBeenCalledTimes(1);
  const again = (over: Partial<Parameters<typeof MapPart>[0]>) =>
    rerender(
      <section className="part-map">
        <MapPart
          sky={sky({ demo: false, readAt: "2026-10-18T12:15:00Z" })}
          blocked={false}
          paused={false}
          onStar={() => {}}
          onExplore={() => {}}
          {...props}
          {...over}
        />
      </section>,
    );
  // The main map was read again.
  await act(async () => again({}));
  expect(read).toHaveBeenCalledTimes(2);
  // The same focus written anew is the same focus.
  await act(async () => again({ focus: { providers: ["drive"] } }));
  expect(read).toHaveBeenCalledTimes(2);
  await act(async () => {
    document.dispatchEvent(new Event("visibilitychange"));
  });
  expect(read).toHaveBeenCalledTimes(3);
  await act(async () => again({ focus: { providers: ["garmin"] } }));
  expect(read).toHaveBeenCalledTimes(4);
  // A read in flight is let go when a newer one starts.
  const signals = read.mock.calls.map(
    (c) => (c as unknown as [string | null, AbortSignal])[1],
  );
  expect(signals.slice(0, -1).every((s) => s.aborted)).toBe(true);
});

it("says the map can't be read when the engine can't answer", async () => {
  const failing = drawMap({
    sky: sky({ demo: false }),
    focus: { providers: ["drive"] },
    read: vi.fn(async () => {
      throw new Error("unavailable");
    }),
  });
  expect(
    await screen.findByText("Sky unavailable · health sources are independent"),
  ).toBeInTheDocument();
  failing.unmount();
  // An engine older than piece 3 has no focused read.
  drawMap({ sky: sky({ demo: false }), focus: { providers: ["drive"] } });
  expect(
    await screen.findByText("Sky unavailable · health sources are independent"),
  ).toBeInTheDocument();
});

// An empty sky says what this part knows (issue: an empty Home told people to search and touch
// stars it didn't have): the demo's preview of an empty, loading or failed source, a first read
// that failed, and a read that found no stars, each with no gesture hint.
it("tells a preview, a failed read and an empty read apart, with nothing to select", () => {
  const preview = drawMap({ own: false, blocked: true });
  expect(screen.getByText("0 stars shown")).toBeInTheDocument();
  expect(screen.getByText("No stars in this preview")).toBeInTheDocument();
  expect(
    screen.getByText("Sample stars return when the demo is restored."),
  ).toBeInTheDocument();
  expect(screen.queryByText("Select a star to explore")).toBeNull();
  expect(screen.queryByText(/groups?$/)).toBeNull();
  expect(screen.queryByText(/search|filters/)).toBeNull();
  preview.unmount();

  const nothing = { ...map, nodes: [], edges: [] };
  const live = (readAt: string | null) =>
    sky({ demo: false, page: nothing, overview: nothing, readAt });
  const failed = drawMap({ own: false, sky: live(null) });
  expect(screen.getByText("Sky unavailable")).toBeInTheDocument();
  expect(
    screen.getByText("Rhythm couldn’t load these records."),
  ).toBeInTheDocument();
  expect(
    screen.getByText("Sky unavailable · health sources are independent"),
  ).toBeInTheDocument();
  failed.unmount();

  drawMap({ own: false, sky: live("2026-11-08T09:00:00Z") });
  expect(screen.getByText("No stars to show")).toBeInTheDocument();
  expect(
    screen.getByText("Rhythm found no records you can view."),
  ).toBeInTheDocument();
  expect(screen.queryByText(/unavailable/)).toBeNull();
  expect(screen.queryByText("Select a star to explore")).toBeNull();
});

it("says when a source couldn't be read, and draws the rest", async () => {
  const { container } = drawMap({
    sky: sky({ demo: false }),
    focus: { providers: ["drive"] },
    read: async () => ({
      ...page(map.nodes.slice(0, 3), 3),
      completeness: "partial",
      issues: [
        {
          provider: "drive",
          code: "unavailable",
          message: "Fictional",
        } as unknown as GraphPage["issues"][number],
      ],
    }),
  });
  expect(
    await screen.findByText("A source couldn’t be read · the rest is shown"),
  ).toBeInTheDocument();
  expect(stars(container)).toHaveLength(3);
});

it("says nothing of the engine's own note on what it read", async () => {
  // The engine's focused read, as the contract fixture has it: one star of
  // two, and its note on the snapshot under the catalog provider, which is no
  // source that failed.
  const answer = JSON.parse(
    readFileSync(
      resolve(
        process.cwd(),
        "../contracts/fixtures/app_dashboards.map.example.json",
      ),
      "utf8",
    ),
  ).map as FocusedPage & { part: string; focus: MapFocus };
  expect(answer.issues).toEqual([
    expect.objectContaining({ provider: catalogProvider() }),
  ]);
  const { container } = drawMap({
    sky: sky({ demo: false }),
    focus: answer.focus,
    read: async () => answer,
  });
  expect(await screen.findByText(/1 of 2 stars/)).toBeInTheDocument();
  expect(stars(container)).toEqual([answer.nodes[0].id]);
  expect(screen.queryByRole("status")).toBe(null);
});

it("tells the engine's note from a source's under an install's own catalog provider", async () => {
  // An install that kept an older engine's star ids notes under that id.
  setInstance({ ...demoInstance, catalogProvider: "truenas" });
  try {
    const note = {
      provider: "truenas",
      reason:
        "Scheduled-refresh snapshot, each record confirmed within the last 35 minutes.",
    };
    const quiet = drawMap({
      sky: sky({ demo: false }),
      focus: { providers: ["drive"] },
      read: async () => ({ ...page(map.nodes.slice(0, 3), 3), issues: [note] }),
    });
    expect(await screen.findByText(/3 stars/)).toBeInTheDocument();
    expect(screen.queryByRole("status")).toBe(null);
    quiet.unmount();
    // Beside a source that couldn't be read, it still says so.
    drawMap({
      sky: sky({ demo: false }),
      focus: { providers: ["drive"] },
      read: async () => ({
        ...page(map.nodes.slice(0, 3), 3),
        issues: [note, { provider: "drive", reason: "Fictional" }],
      }),
    });
    expect(
      await screen.findByText("A source couldn’t be read · the rest is shown"),
    ).toBeInTheDocument();
  } finally {
    setInstance(demoInstance);
  }
});

it("adds the dashboard stars linked to a shown star", () => {
  const board = record("fictional-board", "rhythm", "dashboard");
  const shown = map.nodes.find((n) => n.title === "Fictional idea 3")!;
  const withBoard: GraphPage = {
    ...map,
    nodes: [...map.nodes, board],
    edges: [...map.edges, relation(board, shown)],
  };
  const { container } = drawMap({
    sky: sky({ page: withBoard, overview: ambientWindow(withBoard) }),
    focus: { query: "idea 3" },
  });
  expect(stars(container)).toEqual(
    expect.arrayContaining([shown.id, board.id]),
  );
  // The count is of the stars that match, not the dashboards beside them.
  expect(screen.getByText(/matching “idea 3”/)).toHaveTextContent("1 star");
});

it("explores its stars, or a query's search, and shows everything again", async () => {
  const user = userEvent.setup();
  const showEverything = vi.fn();
  const query = drawMap({
    focus: { query: "idea 1" },
    onShowEverything: showEverything,
  });
  await user.click(screen.getByRole("button", { name: /^Explore$/ }));
  expect(query.onExplore).toHaveBeenCalledWith({ query: "idea 1" });
  await user.click(screen.getByRole("button", { name: "Show everything" }));
  expect(showEverything).toHaveBeenCalled();
  query.unmount();
  const types = drawMap({ focus: { query: "idea 1", types: ["memory"] } });
  await user.click(screen.getByRole("button", { name: /^Explore$/ }));
  const lit = types.onExplore.mock.calls[0][0] as {
    title: string;
    page: GraphPage;
  };
  expect(lit.title).toBe("matching “idea 1”; types memory");
  expect(lit.page.nodes.every((n) => n.type === "memory")).toBe(true);
  // Where nothing is arranged, the focus stays.
  expect(screen.queryByRole("button", { name: "Show everything" })).toBeNull();
});

it("holds still in a fold: a swipe scrolls the column and a tap opens a star", async () => {
  const user = userEvent.setup();
  const { container, onStar } = drawMap({ still: true });
  const constellation = container.querySelector(".constellation")!;
  expect(constellation).toHaveClass("is-fixed", "is-still");
  const svg = container.querySelector("svg.graph-svg");
  expect(svg).toHaveAccessibleName(
    "Knowledge constellation. Select a star to explore its connections.",
  );
  const star = container.querySelector<SVGGElement>("g.graph-node")!;
  await user.click(star);
  expect(onStar).toHaveBeenCalledWith(star.dataset.id);
});

it("lets a vertical swipe on a folded map scroll, whatever order the styles come in", () => {
  // The browser takes the most restrictive touch-action along the way up, so the folded
  // sky's own pan-y must outweigh App.css's none for a sky that isn't interactive. The
  // styles go in with App.css last, the order that would lose a tie.
  const styles = [
    "src/parts/parts.css",
    "src/PartsGrid.css",
    "src/App.css",
  ].map((file) => {
    const style = document.createElement("style");
    style.textContent = readFileSync(file, "utf8");
    return document.head.appendChild(style);
  });
  try {
    const folded = drawMap({ still: true });
    const touch = (root: ParentNode) =>
      getComputedStyle(root.querySelector("svg.graph-svg")!).touchAction;
    expect(touch(folded.container)).toBe("pan-y");
    folded.unmount();
    // Drawn as saved, the sky still takes the pointer itself, as Home's does.
    expect(touch(drawMap({}).container)).toBe("none");
  } finally {
    for (const style of styles) style.remove();
  }
});
