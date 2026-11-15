import { afterEach, beforeAll, expect, it, vi } from "vitest";
import { readFileSync } from "node:fs";
import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import App from "../src/App";
import { designedTiles, homePlaces, presetGrid } from "../src/parts";
import type { HomeGrid, HomeId } from "../src/parts";
import { defaultTiles } from "../src/screens";
import type { Layout, Tiles } from "../src/screens";

// Moves at Home and the moves back (docs/dev/screens.md 10.5): whatever a person drags where,
// dragging it back leaves Home exactly as it was, every corner in its place, with its place's
// size and look, and no grid when there was none; a corner moved to another place takes that
// place's look. Drags here go through Home's grips with a pointer, on a grid of 100 px cells.
// Fictional data only (the demo).
beforeAll(async () => {
  await import("../src/Details");
}, 30_000);
afterEach(() => {
  localStorage.clear();
  sessionStorage.clear();
  vi.restoreAllMocks();
});
const wide = () => {
  vi.mocked(window.matchMedia).mockImplementation(
    (query: string) =>
      ({
        matches: query.includes("min-width: 1100px"),
        media: query,
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
      }) as unknown as MediaQueryList,
  );
  // Home's grid (or the grips over it) is 1200 × 1200 px: a cell is 100 px each way.
  const box = Element.prototype.getBoundingClientRect;
  vi.spyOn(Element.prototype, "getBoundingClientRect").mockImplementation(
    function (this: Element) {
      return this.classList.contains("parts")
        ? DOMRect.fromRect({ x: 0, y: 0, width: 1200, height: 1200 })
        : box.call(this);
    },
  );
};
const home = () => document.querySelector<HTMLElement>(".observatory")!;
// The screen's note, with the change's summary and Undo.
const note = () => document.querySelector<HTMLElement>(".screen-note")!;
const savedGrid = () =>
  JSON.parse(
    localStorage.getItem("rhythm-of-life.grid") ?? "null",
  ) as HomeGrid | null;
const savedTiles = () =>
  JSON.parse(
    localStorage.getItem("rhythm-of-life.tiles") ?? "null",
  ) as Tiles | null;
const layoutNow = () =>
  (localStorage.getItem("rhythm-of-life.layout") ?? "hero") as Layout;
// Where Home's parts are now: its grid, or without one the layout's table with its tiles (no
// CSS grid is drawn here to measure).
const rects = () =>
  savedGrid() ?? presetGrid(layoutNow(), savedTiles() ?? defaultTiles);
const at = (id: HomeId) => rects().parts.find((p) => p.id === id)!;
// Each corner's zone in a Home drawn as today, by its tile (its class says which tile it is).
const zoneClass = {
  main: "main-zone",
  recovery: "recovery-zone",
  training: "training-zone",
  changed: "context-zone",
} as const;
type Corner = keyof typeof zoneClass;
const corners = Object.keys(zoneClass) as Corner[];
const zone = (corner: Corner) =>
  home().querySelector<HTMLElement>(`:scope > .${zoneClass[corner]}`);
// The whole of what Home shows of its layout: whether it is a grid, each zone's tile, place,
// look and grid area, and each grid part's cells; and what the screen keeps.
const shown = () => ({
  grid: home().hasAttribute("data-grid"),
  zones: [...home().querySelectorAll<HTMLElement>(":scope > .health-zone")].map(
    (z) => ({
      tile: z.className,
      place: z.dataset.place,
      look: z.dataset.look,
      area: z.style.gridArea,
    }),
  ),
  parts: [...home().querySelectorAll<HTMLElement>("[data-part]")].map((p) => [
    p.dataset.part,
    p.style.gridColumn,
    p.style.gridRow,
  ]),
  kept: { grid: savedGrid(), tiles: savedTiles() ?? defaultTiles },
});
const names: Record<HomeId, string> = {
  map: "Arrange map",
  main: "Arrange Main corner",
  recovery: "Arrange Recovery corner",
  training: "Arrange Training corner",
  changed: "Arrange Focus corner",
};
// Picks a part up by its grip at its top left cell and drops it with the pointer over `to`.
const drag = async (id: HomeId, to: { x: number; y: number }) => {
  const from = at(id);
  const grip = screen.getByRole("button", { name: names[id] });
  const point = (c: { x: number; y: number }) => ({
    pointerId: 1,
    pointerType: "mouse",
    button: 0,
    clientX: c.x * 100 + 50,
    clientY: c.y * 100 + 50,
  });
  fireEvent.pointerDown(grip, point(from));
  fireEvent.pointerMove(grip, point(to));
  await act(async () => {
    fireEvent.pointerUp(grip, point(to));
  });
};
// Home drawn with what its corners show: the demo's dashboard cards included.
const settled = async () => {
  await screen.findByRole("region", { name: "Knowledge constellation" });
  await screen.findByTitle("Week at a glance card");
};
// The look each layout gives a place where layouts show (App.tsx zoneOf).
const lookOf = (layout: Layout, place: Corner) =>
  layout === "corners"
    ? undefined
    : layout === "hero" && place === "main"
      ? "tall"
      : "row";

it("moves the Main card to the top right and back in the Tall column, coming back tall with no grid, after a reload too", async () => {
  wide();
  const { unmount } = render(<App />);
  await settled();
  const before = shown();
  expect(before.grid).toBe(false);
  expect(zone("main")).toHaveAttribute("data-place", "main");
  expect(zone("main")).toHaveAttribute("data-look", "tall");
  // Over the top-right corner, the map's: the Main corner and the parts on its right trade
  // sides, which only a grid can draw.
  await drag("main", { x: 11, y: 0 });
  await waitFor(() => expect(home()).toHaveAttribute("data-grid"));
  expect(at("main")).toMatchObject({ x: 9, y: 0, w: 3, h: 12 });
  // Dropped back over its own place: Home as its layout draws it again, with no grid.
  await drag("main", { x: 0, y: 0 });
  await waitFor(() => expect(home()).not.toHaveAttribute("data-grid"));
  expect(note()).toHaveTextContent("Moved the Main corner left");
  expect(shown()).toEqual(before);
  expect(savedGrid()).toBeNull();
  // A reload keeps it so.
  unmount();
  render(<App />);
  await settled();
  expect(shown()).toEqual(before);
  expect(zone("main")).toHaveAttribute("data-look", "tall");
});

it("comes back to the designed Home by the keys too, and the note's Undo puts back a swap", async () => {
  wide();
  render(<App />);
  await settled();
  const before = shown();
  const keys = async (id: HomeId, key: string) => {
    const grip = screen.getByRole("button", { name: names[id] });
    grip.focus();
    fireEvent.keyDown(grip, { key: "Enter" });
    fireEvent.keyDown(grip, { key });
    await act(async () => fireEvent.keyDown(grip, { key: "Enter" }));
  };
  await keys("main", "ArrowRight");
  await waitFor(() => expect(home()).toHaveAttribute("data-grid"));
  await keys("main", "ArrowLeft");
  await waitFor(() => expect(home()).not.toHaveAttribute("data-grid"));
  expect(shown()).toEqual(before);
  // The Focus corner dropped on the Main corner takes its place, and Undo puts both back.
  await drag("changed", at("main"));
  expect(note()).toHaveTextContent("Moved Focus to the Main place");
  expect(savedTiles()).toMatchObject({ main: "changed", changed: "main" });
  expect(home()).not.toHaveAttribute("data-grid");
  await act(async () =>
    fireEvent.click(within(note()).getByRole("button", { name: /Undo/ })),
  );
  await waitFor(() => expect(shown()).toEqual(before));
});

it("gives a moved corner its new place's look, and two swaps made back put Home back", async () => {
  wide();
  render(<App />);
  await settled();
  const before = shown();
  // Focus into the tall column: it takes the tall place's look, and Main the row's.
  await drag("changed", at("main"));
  expect(zone("changed")).toHaveAttribute("data-place", "main");
  expect(zone("changed")).toHaveAttribute("data-look", "tall");
  expect(zone("main")).toHaveAttribute("data-place", "changed");
  expect(zone("main")).toHaveAttribute("data-look", "row");
  expect(zone("main")!.style.gridArea).toBe("context");
  // Main on to the Recovery corner's place.
  await drag("main", at("recovery"));
  expect(zone("main")).toHaveAttribute("data-place", "recovery");
  expect(zone("recovery")).toHaveAttribute("data-place", "changed");
  expect(home()).not.toHaveAttribute("data-grid");
  // And back, in reverse.
  await drag("main", at("recovery"));
  await drag("changed", at("main"));
  expect(shown()).toEqual(before);
});

for (const layout of ["corners", "hero", "band"] as const)
  it(`moves every corner to every other place and back in the ${layout} layout, leaving Home exactly as it was`, async () => {
    localStorage.setItem("rhythm-of-life.layout", layout);
    wide();
    render(<App />);
    await settled();
    const before = shown();
    const places = homePlaces(presetGrid(layout, defaultTiles), defaultTiles);
    for (const corner of corners)
      for (const other of ["map", ...corners] as HomeId[]) {
        if (other === corner) continue;
        const from = at(corner),
          onto = at(other);
        await drag(corner, onto);
        if (other !== "map") {
          // Two corners trade places, each taking the other's look; no grid is made.
          const tiles = designedTiles(rects(), places);
          expect(tiles).toMatchObject({ [corner]: other, [other]: corner });
          expect(home()).not.toHaveAttribute("data-grid");
          for (const [moved, place] of [
            [corner, other],
            [other, corner],
          ] as [Corner, Corner][]) {
            const z = zone(moved);
            if (!z) continue; // an empty corner gives its room away
            expect(z.dataset.place).toBe(place);
            expect(z.dataset.look).toBe(lookOf(layout, place));
          }
        }
        await drag(corner, from);
        expect(shown()).toEqual(before);
      }
  }, 60_000);

it("keys Home's tall and row looks to the place a tile sits in, never to the tile alone", () => {
  const css = readFileSync("src/App.css", "utf8").replace(/\r/g, "");
  // Before, glucose kept the tall column's look wherever it went, and the others the row's.
  expect(css).not.toMatch(/data-layout="(hero|band)"\]\s+\.glucose-zone/);
  expect(css).not.toMatch(
    /data-layout="band"\], \[data-layout="hero"\]\)\s+:is\(\.recovery-zone/,
  );
  expect(css).toContain('.glucose-zone[data-look="tall"]');
  expect(css).toContain('.glucose-zone[data-look="row"]');
  expect(css).toContain('[data-place="recovery"] {');
});
