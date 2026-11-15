import { readFileSync } from "node:fs";
import { act, fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { PartsGrid, touchedGripsMs } from "../src/PartsGrid";
import type { GridChange, GridPart } from "../src/PartsGrid";

// One grid of parts as docs/dev/screens.md 10.2 and 10.9 draw it: placed with CSS alone, each
// part mounted once, a group with its grip first, arranged by keys and by the pointer only on
// the app's own grips, and nothing at all around a page that is alone. Fictional parts only.
afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});
const map: GridPart = { id: "map", kind: "map", x: 0, y: 0, w: 7, h: 12 };
const page: GridPart = { id: "page", kind: "page", x: 7, y: 0, w: 5, h: 8 };
const glucose: GridPart = {
  id: "glucose",
  kind: "glucose",
  x: 7,
  y: 8,
  w: 5,
  h: 4,
};
const parts = [glucose, page, map];
const labels: Record<string, string> = {
  map: "Map, near Training",
  page: "Training",
  glucose: "Glucose",
};
const label = (p: GridPart) => labels[p.id] ?? "Part";
const content = (p: GridPart) => <p>{`${p.id} content`}</p>;
const ok = () =>
  vi.fn(async (_change: GridChange<GridPart>) => ({ ok: true as const }));

it("places each part in its cells with CSS alone and never moves one in the DOM", () => {
  const { container, rerender } = render(
    <PartsGrid parts={parts} label={label} render={content} />,
  );
  const drawn = () => [
    ...container.querySelectorAll<HTMLElement>(".parts-part"),
  ];
  // Reading order as the grid first drew: by row, then column.
  expect(drawn().map((e) => e.dataset.part)).toEqual([
    "map",
    "page",
    "glucose",
  ]);
  const [mapEl, pageEl] = drawn();
  expect(mapEl.style.gridColumn).toBe("1 / span 7");
  expect(mapEl.style.gridRow).toBe("1 / span 12");
  expect(pageEl.style.gridColumn).toBe("8 / span 5");
  // The map moves right: the same elements, in the same order, placed anew.
  rerender(
    <PartsGrid
      parts={[
        { ...map, x: 5 },
        { ...page, x: 0 },
        { ...glucose, x: 0 },
      ]}
      label={label}
      render={content}
    />,
  );
  expect(drawn()).toEqual([mapEl, pageEl, drawn()[2]]);
  expect(drawn().map((e) => e.dataset.part)).toEqual([
    "map",
    "page",
    "glucose",
  ]);
  expect(mapEl.style.gridColumn).toBe("6 / span 7");
  expect(pageEl.style.gridColumn).toBe("1 / span 5");
  // Each part is a group named for what it shows; without arranging, no grips.
  expect(
    screen.getByRole("group", { name: "Map, near Training" }),
  ).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: /^Arrange/ })).toBeNull();
});

it("draws a page that is alone exactly as before parts: no group, no grip, no live region", () => {
  const { container } = render(
    <PartsGrid
      parts={[{ id: "page", kind: "page", x: 0, y: 0, w: 12, h: 12 }]}
      plain
      label={label}
      render={content}
      arrange={ok()}
    />,
  );
  expect(container.querySelector(".parts")).toHaveClass("plain");
  expect(screen.queryByRole("group")).toBeNull();
  expect(screen.queryByRole("button")).toBeNull();
  expect(screen.queryByRole("status")).toBeNull();
  expect(screen.getByText("page content")).toBeInTheDocument();
  expect(
    container.querySelector(".parts-part")!.getAttribute("style"),
  ).toBeNull();
});

it("keeps a later piece's part empty and shows no grip for it", () => {
  const draw = vi.fn(content);
  render(
    <PartsGrid
      parts={[
        page,
        map,
        { id: "later", kind: "hologram", x: 7, y: 8, w: 5, h: 4 },
      ]}
      label={label}
      render={draw}
      arrange={ok()}
    />,
  );
  expect([...new Set(draw.mock.calls.map(([p]) => p.id))].sort()).toEqual([
    "map",
    "page",
  ]);
  const later = document.querySelector('[data-part="later"]')!;
  expect(later.querySelector(".parts-body")!.childElementCount).toBe(0);
  expect(within(later as HTMLElement).queryByRole("button")).toBeNull();
});

it("arranges from the keyboard: the grip first, Enter picks up, arrows move, Shift resizes, Enter drops one change", async () => {
  const arrange = ok();
  render(
    <PartsGrid
      parts={parts}
      label={label}
      render={content}
      arrange={arrange}
    />,
  );
  const group = screen.getByRole("group", { name: "Map, near Training" });
  const grip = within(group).getByRole("button", { name: "Arrange map" });
  // The grip is the first thing in its part.
  expect(group.firstElementChild).toBe(grip);
  fireEvent.keyDown(grip, { key: "Enter" });
  expect(document.documentElement).toHaveAttribute("data-arranging");
  expect(screen.getByRole("status")).toHaveTextContent(
    /Map, near Training picked up/,
  );
  // Right: the page and glucose are its band, so they trade sides with it.
  fireEvent.keyDown(grip, { key: "ArrowRight" });
  const mapEl = document.querySelector<HTMLElement>('[data-part="map"]')!;
  expect(mapEl.style.gridColumn).toBe("6 / span 7");
  expect(screen.getByRole("status").textContent).toMatch(/Map/);
  // Shift and an arrow resizes it.
  fireEvent.keyDown(grip, { key: "ArrowLeft", shiftKey: true });
  expect(arrange).not.toHaveBeenCalled();
  await act(async () => fireEvent.keyDown(grip, { key: "Enter" }));
  expect(document.documentElement).not.toHaveAttribute("data-arranging");
  expect(arrange).toHaveBeenCalledTimes(1);
  const change = arrange.mock.calls[0][0];
  expect(change.part).toBe("map");
  expect(change.commands).toEqual([
    { action: "move", part: "map", place: "right" },
    { action: "resize", part: "map", size: "narrower" },
  ]);
  expect(change.before).toEqual(parts);
  // Narrower moves the edge away from the grid edge it touches: its left one.
  expect(change.after.find((p) => p.id === "map")).toMatchObject({
    x: 7,
    w: 5,
  });
  expect(change.summary).toBe("Moved and resized the map");
});

it("puts a held part back with Escape and saves nothing", async () => {
  const arrange = ok();
  const outside = vi.fn();
  window.addEventListener("keydown", outside);
  render(
    <PartsGrid
      parts={parts}
      label={label}
      render={content}
      arrange={arrange}
    />,
  );
  const grip = screen.getByRole("button", { name: "Arrange glucose tile" });
  fireEvent.keyDown(grip, { key: " " });
  fireEvent.keyDown(grip, { key: "ArrowUp" });
  fireEvent.keyDown(grip, { key: "Escape" });
  window.removeEventListener("keydown", outside);
  // The app's own Escape handlers never hear it.
  expect(outside.mock.calls.some(([e]) => e.key === "Escape")).toBe(false);
  expect(document.documentElement).not.toHaveAttribute("data-arranging");
  expect(
    document.querySelector<HTMLElement>('[data-part="glucose"]')!.style.gridRow,
  ).toBe("9 / span 4");
  expect(screen.getByRole("status")).toHaveTextContent("Glucose put back");
  expect(arrange).not.toHaveBeenCalled();
});

it("drags a grip with the pointer over a clear layer and drops it as a move, never through drag and drop", async () => {
  const arrange = ok();
  const { container } = render(
    <PartsGrid
      parts={parts}
      label={label}
      render={content}
      arrange={arrange}
    />,
  );
  const root = container.querySelector<HTMLElement>(".parts")!;
  vi.spyOn(root, "getBoundingClientRect").mockReturnValue(
    DOMRect.fromRect({ x: 0, y: 0, width: 1200, height: 1200 }),
  );
  // Nothing in the grid can be dragged the browser's way.
  expect(container.querySelector("[draggable]")).toBeNull();
  fireEvent.dragStart(root);
  fireEvent.drop(root);
  const grip = screen.getByRole("button", { name: "Arrange glucose tile" });
  fireEvent.pointerDown(grip, {
    pointerId: 1,
    pointerType: "mouse",
    button: 0,
    clientX: 1150,
    clientY: 810,
  });
  // While held, a layer covers every frame.
  expect(container.querySelector(".parts-shield")).not.toBeNull();
  // Onto the free cell below the map? None is free: onto the map, as `with`.
  fireEvent.pointerMove(grip, { pointerId: 1, clientX: 350, clientY: 810 });
  expect(container.querySelector(".parts-ghost")).not.toBeNull();
  await act(async () =>
    fireEvent.pointerUp(grip, { pointerId: 1, clientX: 350, clientY: 810 }),
  );
  expect(container.querySelector(".parts-shield")).toBeNull();
  expect(arrange).toHaveBeenCalledTimes(1);
  expect(arrange.mock.calls[0][0].commands).toEqual([
    { action: "move", part: "glucose", with: "map" },
  ]);
});

it("hands a drop it refused to the host, to show why, as well as saying it to screen readers", async () => {
  const arrange = ok(),
    refused = vi.fn();
  const { container } = render(
    <PartsGrid
      parts={parts}
      label={label}
      render={content}
      arrange={arrange}
      refused={refused}
    />,
  );
  const root = container.querySelector<HTMLElement>(".parts")!;
  vi.spyOn(root, "getBoundingClientRect").mockReturnValue(
    DOMRect.fromRect({ x: 0, y: 0, width: 1200, height: 1200 }),
  );
  // The glucose tile's corner dragged up past its smallest size.
  const corner = container.querySelector(
    '[data-part="glucose"] > .parts-corner',
  )!;
  fireEvent.pointerDown(corner, {
    pointerId: 2,
    pointerType: "mouse",
    button: 0,
    clientX: 1190,
    clientY: 1190,
  });
  fireEvent.pointerMove(corner, { pointerId: 2, clientX: 1190, clientY: 860 });
  await act(async () =>
    fireEvent.pointerUp(corner, { pointerId: 2, clientX: 1190, clientY: 860 }),
  );
  expect(arrange).not.toHaveBeenCalled();
  expect(refused).toHaveBeenCalledTimes(1);
  const [reason] = refused.mock.calls[0] as [string];
  expect(screen.getByRole("status")).toHaveTextContent(reason);
});

it("shows no grips where nothing is arranged by hand, and none on a part of a kind it doesn't know", () => {
  render(
    <PartsGrid parts={parts} label={label} render={content} arrange={null} />,
  );
  expect(screen.queryByRole("button")).toBeNull();
  expect(screen.queryByRole("status")).toBeNull();
});

it("shows Home's grips for a few seconds after a touch on Home", () => {
  vi.useFakeTimers();
  const home: GridPart[] = [
    { id: "map", x: 3, y: 0, w: 9, h: 8 },
    { id: "main", x: 0, y: 0, w: 3, h: 12 },
    { id: "recovery", x: 3, y: 8, w: 3, h: 4 },
    { id: "training", x: 6, y: 8, w: 3, h: 4 },
    { id: "changed", x: 9, y: 8, w: 3, h: 4 },
  ];
  const { container } = render(
    <PartsGrid
      parts={home}
      homeMode
      label={label}
      render={content}
      arrange={ok()}
    />,
  );
  const root = container.querySelector(".parts")!;
  expect(root).toHaveClass("home");
  expect(
    screen
      .getAllByRole("button", { name: /^Arrange/ })
      .map((b) => b.getAttribute("aria-label")),
  ).toEqual([
    "Arrange Main corner",
    "Arrange map",
    "Arrange Recovery corner",
    "Arrange Training corner",
    "Arrange Focus corner",
  ]);
  expect(root).not.toHaveAttribute("data-touched");
  fireEvent.pointerDown(screen.getByText("map content"), {
    pointerType: "touch",
  });
  expect(root).toHaveAttribute("data-touched");
  act(() => vi.advanceTimersByTime(touchedGripsMs));
  expect(root).not.toHaveAttribute("data-touched");
  // A mouse is not a touch.
  fireEvent.pointerDown(screen.getByText("map content"), {
    pointerType: "mouse",
  });
  expect(root).not.toHaveAttribute("data-touched");
});

it("lets a hidden grip over a Home drawn as today take no click or tap from what is beneath", () => {
  vi.useFakeTimers();
  // PartsGrid.css as a fine pointer reads it, then its touch-screen rules as a coarse one does.
  const sheet = document.createElement("style");
  sheet.textContent = readFileSync("src/PartsGrid.css", "utf8");
  document.head.append(sheet);
  const coarse = document.createElement("style");
  try {
    const home: GridPart[] = [
      { id: "map", x: 2, y: 0, w: 8, h: 12 },
      { id: "main", x: 0, y: 0, w: 2, h: 8 },
      { id: "recovery", x: 10, y: 0, w: 2, h: 8 },
      { id: "training", x: 0, y: 8, w: 2, h: 4 },
      { id: "changed", x: 10, y: 8, w: 2, h: 4 },
    ];
    const { container } = render(
      <PartsGrid parts={home} homeMode overlay label={label} arrange={ok()} />,
    );
    const root = container.querySelector<HTMLElement>(".parts")!;
    vi.spyOn(root, "getBoundingClientRect").mockReturnValue(
      DOMRect.fromRect({ x: 0, y: 0, width: 1200, height: 1200 }),
    );
    const takes = (name: string) =>
      getComputedStyle(screen.getByRole("button", { name })).pointerEvents;
    const corner = () =>
      getComputedStyle(
        container.querySelector('[data-part="main"] > .parts-corner')!,
      ).pointerEvents;
    expect(takes("Arrange Main corner")).toBe("none");
    expect(corner()).toBe("none");
    // A mouse over the main corner's area shows its grip, which then takes the pointer.
    fireEvent.pointerMove(container, {
      pointerType: "mouse",
      clientX: 100,
      clientY: 100,
    });
    expect(takes("Arrange Main corner")).toBe("auto");
    expect(corner()).toBe("auto");
    expect(takes("Arrange map")).toBe("none");
    fireEvent.pointerLeave(container);
    expect(takes("Arrange Main corner")).toBe("none");
    // On a touch screen, for a few seconds after a touch on Home.
    for (const rule of sheet.sheet!.cssRules)
      if (
        rule instanceof CSSMediaRule &&
        rule.media.mediaText.includes("coarse")
      )
        coarse.textContent += [...rule.cssRules]
          .map((r) => r.cssText)
          .join("\n");
    document.head.append(coarse);
    expect(takes("Arrange map")).toBe("none");
    fireEvent.pointerDown(container, { pointerType: "touch" });
    expect(takes("Arrange map")).toBe("auto");
    act(() => vi.advanceTimersByTime(touchedGripsMs));
    expect(takes("Arrange map")).toBe("none");
    // The keys reach a grip whatever the pointer does.
    act(() => screen.getByRole("button", { name: "Arrange map" }).focus());
    expect(takes("Arrange map")).toBe("auto");
  } finally {
    sheet.remove();
    coarse.remove();
  }
});
