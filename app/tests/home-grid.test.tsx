import { afterEach, beforeAll, expect, it, vi } from "vitest";
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
import { presetGrid } from "../src/parts";
import { defaultTiles } from "../src/screens";

// Home as a layout of the same grid (docs/dev/screens.md 10.5, 10.9 and 10.10): an unchanged
// Home draws as today with five Arrange buttons where layouts show; the first move or resize
// turns it into a grid, kept on this screen with the runner's note and Undo; choosing a layout
// puts the designed Home back; narrow screens keep today's Home. Fictional data only (the demo).
beforeAll(async () => {
  await import("../src/Details");
}, 30_000);
afterEach(() => {
  localStorage.clear();
  sessionStorage.clear();
  vi.restoreAllMocks();
});
const wide = () => {
  const narrow = vi.mocked(window.matchMedia).getMockImplementation()!;
  vi.mocked(window.matchMedia).mockImplementation(
    (query: string) =>
      ({
        matches: query.includes("min-width: 1100px"),
        media: query,
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
      }) as unknown as MediaQueryList,
  );
  return () => vi.mocked(window.matchMedia).mockImplementation(narrow);
};
const home = () => document.querySelector<HTMLElement>(".observatory")!;
const saved = () =>
  JSON.parse(localStorage.getItem("rhythm-of-life.grid") ?? "null") as {
    parts: { id: string; x: number; y: number; w: number; h: number }[];
  } | null;
const arrange = () =>
  screen
    .queryAllByRole("button", { name: /^Arrange / })
    .map((b) => b.getAttribute("aria-label"));

it("draws an unchanged Home as today, with five Arrange buttons where layouts show", async () => {
  const restore = wide();
  const { unmount } = render(<App />);
  await screen.findByRole("region", { name: "Knowledge constellation" });
  // Today's Home: the zones in Home's own CSS grid, no grid of parts.
  expect(home()).not.toHaveAttribute("data-grid");
  expect(home().querySelector(".graph-zone")).toBeInTheDocument();
  expect(home().querySelector(".parts:not(.overlay)")).toBeNull();
  // The one addition: a grip for each part, in its tab order, over Home and nothing inside it.
  expect(arrange()).toEqual([
    "Arrange Main corner",
    "Arrange map",
    "Arrange Recovery corner",
    "Arrange Training corner",
    "Arrange Focus corner",
  ]);
  const overlay = home().querySelector(".parts.overlay")!;
  expect(overlay).toHaveClass("home");
  expect(overlay.querySelector(".parts-body")).toBeNull();
  // Where the first move would put each part: the layout's table (no CSS grid is drawn here).
  const map = presetGrid("hero", defaultTiles).parts.find(
    (p) => p.id === "map",
  )!;
  expect(
    overlay.querySelector<HTMLElement>('[data-part="map"]')!.style.gridColumn,
  ).toBe(`${map.x + 1} / span ${map.w}`);
  unmount();
  restore();
  // Narrow screens keep today's fixed Home, with no grips.
  render(<App />);
  await screen.findByRole("region", { name: "Knowledge constellation" });
  expect(arrange()).toEqual([]);
});

it("turns Home into a grid at the first move, with the note and Undo", async () => {
  const restore = wide();
  render(<App />);
  await screen.findByRole("region", { name: "Knowledge constellation" });
  const grip = screen.getByRole("button", { name: "Arrange map" });
  grip.focus();
  fireEvent.keyDown(grip, { key: "Enter" });
  expect(document.documentElement).toHaveAttribute("data-arranging");
  fireEvent.keyDown(grip, { key: "ArrowDown" });
  // Nothing is kept until the part is dropped.
  expect(saved()).toBeNull();
  await act(async () => fireEvent.keyDown(grip, { key: "Enter" }));
  await waitFor(() => expect(home()).toHaveAttribute("data-grid"));
  expect(document.documentElement).not.toHaveAttribute("data-arranging");
  // The hero table's map traded sides with the corners below it.
  expect(saved()!.parts).toContainEqual({ id: "map", x: 3, y: 4, w: 9, h: 8 });
  expect(saved()!.parts).toContainEqual({
    id: "recovery",
    x: 3,
    y: 0,
    w: 3,
    h: 4,
  });
  // Home is one grid of its five parts now, each labelled, the sky among them.
  const parts = home().querySelector(".parts.home:not(.overlay)")!;
  expect(
    within(parts as HTMLElement)
      .getAllByRole("group")
      .map((g) => g.getAttribute("aria-label")),
  ).toEqual([
    "Main corner",
    "Recovery corner",
    "Training corner",
    "Focus corner",
    "Map",
  ]);
  expect(
    parts.querySelector<HTMLElement>('[data-part="map"]')!.style.gridRow,
  ).toBe("5 / span 8");
  expect(
    within(parts as HTMLElement).getByRole("region", {
      name: "Knowledge constellation",
    }),
  ).toBeInTheDocument();
  // A corner with neither its source nor a card keeps its cells and says so quietly.
  expect(
    within(
      within(parts as HTMLElement).getByRole("group", {
        name: "Training corner",
      }),
    ).getByText("Appears once a training source is connected"),
  ).toBeInTheDocument();
  // The keyboard keeps its place: the map's grip on the grid has focus.
  await waitFor(() =>
    expect(document.activeElement).toBe(
      screen.getByRole("button", { name: "Arrange map" }),
    ),
  );
  // The same note as a command's, naming no sender, and its Undo puts today's Home back.
  const note = screen.getByText("Moved the map down").closest("p")!;
  fireEvent.click(within(note).getByRole("button", { name: /Undo/ }));
  await waitFor(() => expect(home()).not.toHaveAttribute("data-grid"));
  expect(saved()).toBeNull();
  expect(arrange()).toHaveLength(5);
  restore();
});

it("puts the designed Home back when a layout is chosen, and says Home is arranged by hand", async () => {
  const user = userEvent.setup();
  const restore = wide();
  const grid = presetGrid("band", defaultTiles);
  localStorage.setItem("rhythm-of-life.layout", "band");
  localStorage.setItem("rhythm-of-life.grid", JSON.stringify(grid));
  render(<App />);
  await screen.findByRole("region", { name: "Knowledge constellation" });
  expect(home()).toHaveAttribute("data-grid");
  await user.click(
    screen.getByRole("button", { name: "Sources and settings" }),
  );
  const layouts = await screen.findByRole("group", {
    name: "Layout of the home screen",
  });
  expect(
    screen.getByText(
      "This screen's Home is arranged by hand. Choosing a layout puts that layout's design back.",
    ),
  ).toBeInTheDocument();
  // No layout is what Home shows now, so the one it came from can be chosen to go back.
  for (const button of within(layouts).getAllByRole("button"))
    expect(button).toHaveAttribute("aria-pressed", "false");
  await user.click(
    within(layouts).getByRole("button", { name: "Rhythm band" }),
  );
  expect(saved()).toBeNull();
  expect(
    within(layouts).getByRole("button", { name: "Rhythm band" }),
  ).toHaveAttribute("aria-pressed", "true");
  expect(screen.queryByText(/arranged by hand/)).toBeNull();
  // The same note as the layout command's, and its Undo brings the grid back.
  const note = screen
    .getByText("Switched to the Rhythm band layout")
    .closest("p")!;
  fireEvent.click(within(note).getByRole("button", { name: /Undo/ }));
  await waitFor(() => expect(saved()).toEqual(grid));
  restore();
});

it("keeps today's Home on a narrow screen while its grid waits there", async () => {
  localStorage.setItem(
    "rhythm-of-life.grid",
    JSON.stringify(presetGrid("corners", defaultTiles)),
  );
  render(<App />);
  await screen.findByRole("region", { name: "Knowledge constellation" });
  expect(home()).not.toHaveAttribute("data-grid");
  expect(home().querySelector(".parts")).toBeNull();
  expect(saved()).toEqual(presetGrid("corners", defaultTiles));
});
