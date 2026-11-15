import { describe, expect, it, vi } from "vitest";
import {
  arrangeCommand,
  arrangeable,
  arrangementOnly,
  bandOf,
  changeHome,
  changeLayout,
  cleanFocus,
  cleanHomeGrid,
  cleanLayout,
  cornerSwap,
  designedTiles,
  drawsAsToday,
  dropMove,
  firstHomeGrid,
  foldOf,
  foldedHeight,
  foldedParts,
  focusTypes,
  homeGridProblem,
  homePlaces,
  homePresets,
  homeShowingParts,
  homeSmallest,
  kindSizes,
  layoutOf,
  layoutProblem,
  measureHome,
  measuredGrid,
  movePart,
  normalFocus,
  partLabel,
  partName,
  presetGrid,
  readingOrder,
  resizePart,
  showingParts,
  storedSize,
  swapCornerRects,
} from "../src/parts";
import type { HomeGrid, Layout, Part, Rect } from "../src/parts";
import {
  cleanDashboardData,
  cleanLayoutAnswer,
  cleanSummary,
  cornerNames,
  unarranged,
} from "../src/dashboards";
import {
  cleanSettings,
  cleanShowing,
  commandArgs,
  defaultTiles,
  layoutNames,
  savedGrid,
  saveGrid,
  settingsValid,
  watchSettings,
} from "../src/screens";
import type { Layout as Preset, Tiles } from "../src/screens";
import { createDemoDashboards } from "../src/demoDashboards";

// Rectangles as "x,y,w×h", by id, so a grid reads at a glance.
const rects = (grid: { parts: (Rect & { id: string })[] }) =>
  Object.fromEntries(
    grid.parts.map((p) => [p.id, `${p.x},${p.y},${p.w}×${p.h}`]),
  );
const part = (
  id: string,
  kind: Part["kind"],
  [x, y, w, h]: number[],
  more: Partial<Part> = {},
): Part => ({ id, kind, x, y, w, h, ...more });
const ok = <G>(r: { ok: true; grid: G } | { ok: false; reason: string }) => {
  if (!r.ok) throw new Error(r.reason);
  return r;
};
const reason = (r: { ok: boolean; reason?: string }) =>
  r.ok ? "changed" : r.reason;

// The contract's worked example (10.7): the map on the left, the page at the top right and
// glucose below it.
const plan = "rhythm:notes:training-plan";
const training: Layout = {
  parts: [
    part("map", "map", [0, 0, 7, 12], {
      focus: { near: { star: plan, depth: 1 } },
    }),
    part("page", "page", [7, 0, 5, 8]),
    part("glucose", "glucose", [7, 8, 5, 4]),
  ],
};

describe("the worked example", () => {
  it("moves the map right past its band, keeping 7 × 12", () => {
    expect(bandOf(training.parts, "map", "right")).toMatchObject({
      parts: [{ id: "page" }, { id: "glucose" }],
      from: 7,
      to: 12,
    });
    const moved = ok(movePart(training, "map", { place: "right" }));
    expect(rects(moved.grid)).toEqual({
      map: "5,0,7×12",
      page: "0,0,5×8",
      glucose: "0,8,5×4",
    });
    expect(moved.summary).toBe("Moved the map right");
    expect(moved.announcement).toBe("Map moved right, beside the page");
    // Everything else stays exactly as it was: the focus, the ids, the kinds.
    expect(moved.grid.parts[0].focus).toEqual(training.parts[0].focus);
    expect(layoutProblem(moved.grid)).toBeNull();
    expect(arrangementOnly(training, moved.grid)).toBeNull();
    // As a command, and back again.
    const command = arrangeCommand("move", { part: "map", place: "right" })!;
    expect(ok(changeLayout(training, command)).grid).toEqual(moved.grid);
    const back = ok(movePart(moved.grid, "map", { place: "left" }));
    expect(back.grid).toEqual(training);
    // The input is never changed.
    expect(rects(training).map).toBe("0,0,7×12");
  });
});

describe("moves", () => {
  it("trades sides with a band, its free cells and the gap between kept", () => {
    const loose = {
      parts: [
        part("map", "map", [0, 0, 6, 12]),
        part("page", "page", [6, 0, 6, 8]),
        part("glucose", "glucose", [8, 8, 2, 4]),
      ],
    };
    expect(rects(ok(movePart(loose, "map", { place: "right" })).grid)).toEqual({
      map: "6,0,6×12",
      page: "0,0,6×8",
      glucose: "2,8,2×4",
    });
    const apart = {
      parts: [
        part("map", "map", [0, 0, 5, 12]),
        part("page", "page", [7, 0, 5, 12]),
      ],
    };
    expect(rects(ok(movePart(apart, "map", { place: "right" })).grid)).toEqual({
      map: "7,0,5×12",
      page: "0,0,5×12",
    });
    // Up and down work on columns the same way.
    const stacked = {
      parts: [
        part("page", "page", [0, 0, 12, 6]),
        part("map", "map", [0, 6, 6, 6]),
        part("glucose", "glucose", [6, 6, 6, 6]),
      ],
    };
    const down = ok(movePart(stacked, "page", { place: "down" }));
    expect(rects(down.grid)).toEqual({
      page: "0,6,12×6",
      map: "0,0,6×6",
      glucose: "6,0,6×6",
    });
    expect(down.announcement).toBe("Page moved down, beside the map");
  });

  it("trades rectangles when there is no band, and refuses a part that doesn't fit", () => {
    const tall = {
      parts: [
        part("map", "map", [0, 0, 4, 6]),
        part("page", "page", [4, 0, 8, 12]),
        part("glucose", "glucose", [0, 6, 4, 6]),
      ],
    };
    // The page reaches outside the map's rows, so there is no band.
    expect(bandOf(tall.parts, "map", "right")).toBeNull();
    const traded = ok(movePart(tall, "map", { place: "right" }));
    expect(rects(traded.grid)).toMatchObject({
      map: "4,0,8×12",
      page: "0,0,4×6",
    });
    expect(traded.summary).toBe(
      "The map traded places and sizes with the page",
    );
    const small = {
      parts: [
        part("glucose", "glucose", [0, 0, 2, 4]),
        part("page", "page", [2, 0, 10, 12]),
      ],
    };
    expect(reason(movePart(small, "glucose", { place: "right" }))).toBe(
      "No room: the page doesn't fit where the glucose tile is",
    );
  });

  it("slides to an edge over free cells, and says when it is there already", () => {
    const top = {
      parts: [
        part("page", "page", [0, 4, 12, 8]),
        part("map", "map", [2, 0, 4, 4]),
      ],
    };
    const slid = ok(movePart(top, "map", { place: "left" }));
    expect(rects(slid.grid).map).toBe("0,0,4×4");
    expect(slid.announcement).toBe("Map moved left, to the left edge");
    expect(reason(movePart(slid.grid, "map", { place: "left" }))).toBe(
      "The map is already at the left edge",
    );
  });

  it("trades order with a part it lines up with, else rectangles", () => {
    const row = {
      parts: [
        part("page", "page", [0, 4, 12, 8]),
        part("glucose", "glucose", [0, 0, 2, 4]),
        part("recovery", "recovery", [4, 0, 3, 4]),
      ],
    };
    const swapped = ok(movePart(row, "glucose", { with: "recovery" }));
    // Each keeps its size and the two free columns stay between them.
    expect(rects(swapped.grid)).toMatchObject({
      recovery: "0,0,3×4",
      glucose: "5,0,2×4",
    });
    expect(swapped.summary).toBe(
      "Swapped the glucose tile and the recovery tile",
    );
    expect(swapped.announcement).toBe(
      "Glucose moved right, past the recovery tile",
    );
    // The same columns line up too.
    expect(
      rects(ok(movePart(training, "page", { with: "glucose" })).grid),
    ).toMatchObject({
      glucose: "7,0,5×4",
      page: "7,4,5×8",
    });
    const traded = ok(movePart(training, "map", { with: "glucose" }));
    expect(rects(traded.grid)).toMatchObject({
      map: "7,8,5×4",
      glucose: "0,0,7×12",
    });
    expect(reason(movePart(training, "map", { with: "map" }))).toBe(
      "The map can't trade places with itself",
    );
    expect(reason(movePart(training, "map", { with: "insights" }))).toBe(
      "There's no part “insights” here",
    );
  });

  it("goes to a corner, or trades with what covers it", () => {
    const top = {
      parts: [
        part("page", "page", [0, 4, 12, 8]),
        part("map", "map", [2, 0, 4, 4]),
      ],
    };
    const cornered = ok(movePart(top, "map", { place: "top_right" }));
    expect(rects(cornered.grid).map).toBe("8,0,4×4");
    expect(cornered.summary).toBe("Moved the map to the top right");
    expect(reason(movePart(cornered.grid, "map", { place: "top_right" }))).toBe(
      "The map is already in the top right corner",
    );
    expect(
      rects(ok(movePart(training, "glucose", { place: "top_left" })).grid),
    ).toMatchObject({
      glucose: "0,0,7×12",
      map: "7,8,5×4",
    });
    const crowded = {
      parts: [
        part("page", "page", [0, 0, 8, 12]),
        part("map", "map", [8, 0, 4, 4]),
        part("glucose", "glucose", [10, 9, 2, 2]),
      ],
    };
    expect(reason(movePart(crowded, "map", { place: "bottom_right" }))).toBe(
      "No room there",
    );
  });

  it("goes to a cell at its own size, pulled back inside the grid", () => {
    const top = {
      parts: [
        part("page", "page", [0, 4, 12, 8]),
        part("map", "map", [2, 0, 4, 4]),
      ],
    };
    const there = ok(movePart(top, "map", { x: 11, y: 0 }));
    expect(rects(there.grid).map).toBe("8,0,4×4");
    expect(there.announcement).toBe("Map moved to column 9, row 1");
    expect(reason(movePart(there.grid, "map", { x: 8, y: 0 }))).toBe(
      "The map is already there",
    );
    // Taken cells: the part covering that cell is the other part, as for `with`.
    expect(
      rects(ok(movePart(training, "glucose", { x: 0, y: 0 })).grid),
    ).toMatchObject({
      glucose: "0,0,7×12",
    });
    expect(reason(movePart(top, "nothing", { x: 0, y: 0 }))).toBe(
      "There's no part “nothing” here",
    );
  });
});

describe("drags", () => {
  it("drops onto free cells, a band or another part", () => {
    // Onto the page, in the map's band on the right: a move right.
    expect(
      dropMove(training.parts, "map", { x: 8, y: 2 }, { x: 5, y: 0 }),
    ).toEqual({
      place: "right",
    });
    // Onto the map, in no band of glucose's: `with` the map.
    expect(
      dropMove(training.parts, "glucose", { x: 2, y: 2 }, { x: 0, y: 0 }),
    ).toEqual({
      with: "map",
    });
    const top = [
      part("page", "page", [0, 4, 12, 8]),
      part("map", "map", [2, 0, 4, 4]),
    ];
    expect(dropMove(top, "map", { x: 9, y: 1 }, { x: 8, y: 0 })).toEqual({
      x: 8,
      y: 0,
    });
    expect(dropMove(top, "map", { x: 3, y: 1 }, { x: 2, y: 0 })).toBeNull();
  });
});

describe("resizes", () => {
  it("grows by taking cells from the parts in the way, as far as all can give", () => {
    const wider = ok(resizePart(training, "map", { size: "wider" }));
    expect(rects(wider.grid)).toEqual({
      map: "0,0,9×12",
      page: "9,0,3×8",
      glucose: "9,8,3×4",
    });
    expect(wider.announcement).toBe("Map wider, 9 of 12 columns");
    expect(reason(resizePart(wider.grid, "map", { size: "wider" }))).toBe(
      "No room: the page is at its smallest there",
    );
    // The page can give one column only, so the map grows by one.
    const narrowPage = {
      parts: [
        part("map", "map", [0, 0, 7, 12]),
        part("page", "page", [7, 0, 4, 8]),
        part("glucose", "glucose", [7, 8, 5, 4]),
      ],
    };
    expect(
      rects(ok(resizePart(narrowPage, "map", { size: "wider" })).grid),
    ).toEqual({
      map: "0,0,8×12",
      page: "8,0,3×8",
      glucose: "8,8,4×4",
    });
  });

  it("moves the edge away from the grid edge the part touches", () => {
    const right = ok(movePart(training, "map", { place: "right" })).grid;
    expect(rects(ok(resizePart(right, "map", { size: "wider" })).grid)).toEqual(
      {
        map: "3,0,9×12",
        page: "0,0,3×8",
        glucose: "0,8,3×4",
      },
    );
    const alone = layoutOf(null);
    expect(reason(resizePart(alone, "page", { size: "wider" }))).toBe(
      "The page already fills the grid's width",
    );
    expect(reason(resizePart(alone, "page", { size: "bigger" }))).toBe(
      "The page already fills the grid",
    );
  });

  it("shrinks to the smallest size, the part lining up taking the freed cells", () => {
    const pair = {
      parts: [
        part("map", "map", [0, 0, 6, 12]),
        part("page", "page", [6, 0, 6, 12]),
      ],
    };
    const narrower = ok(resizePart(pair, "map", { size: "narrower" }));
    expect(rects(narrower.grid)).toEqual({ map: "0,0,4×12", page: "4,0,8×12" });
    expect(reason(resizePart(narrower.grid, "map", { size: "narrower" }))).toBe(
      "The map is already at its smallest width",
    );
    // With nothing lining up, the freed cells stay empty.
    expect(
      rects(ok(resizePart(training, "map", { size: "narrower" })).grid),
    ).toEqual({
      map: "0,0,5×12",
      page: "7,0,5×8",
      glucose: "7,8,5×4",
    });
  });

  it("takes a size from the top left, the others giving way, all or nothing", () => {
    expect(
      rects(ok(resizePart(training, "map", { w: 9, h: 12 })).grid),
    ).toEqual({
      map: "0,0,9×12",
      page: "9,0,3×8",
      glucose: "9,8,3×4",
    });
    expect(reason(resizePart(training, "map", { w: 11, h: 12 }))).toBe(
      "No room: the page is at its smallest there",
    );
    expect(reason(resizePart(training, "map", { w: 3, h: 12 }))).toBe(
      "The map is at least 4 × 4",
    );
    expect(reason(resizePart(training, "map", { w: 7, h: 12 }))).toBe(
      "The map is already 7 × 12",
    );
    // Moved left to stay inside the grid.
    const right = ok(movePart(training, "map", { place: "right" })).grid;
    expect(
      rects(ok(resizePart(right, "map", { w: 9, h: 12 })).grid),
    ).toMatchObject({
      map: "3,0,9×12",
      page: "0,0,3×8",
    });
  });

  it("says at Home which layout gives the map room", () => {
    const corners = presetGrid("corners", defaultTiles);
    expect(reason(resizePart(corners, "map", { size: "bigger" }))).toBe(
      `The map fills the space between the tiles; the ${layoutNames.band} layout gives it the full width`,
    );
    const hero = presetGrid("hero", defaultTiles);
    const taller = ok(resizePart(hero, "map", { size: "taller" }));
    expect(rects(taller.grid)).toMatchObject({
      map: "3,0,9×9",
      recovery: "3,9,3×3",
    });
    expect(reason(resizePart(taller.grid, "map", { size: "taller" }))).toBe(
      `The map fills the space between the tiles; the ${layoutNames.corners} layout gives it the full height`,
    );
    // A corner may take cells from the map; a corner in the way is named as itself.
    expect(
      rects(ok(resizePart(corners, "main", { size: "wider" })).grid),
    ).toMatchObject({
      main: "0,0,4×8",
      map: "4,0,6×12",
    });
    const wide = ok(resizePart(hero, "training", { size: "wider" })).grid;
    expect(rects(wide)).toMatchObject({
      training: "6,8,4×4",
      changed: "10,8,2×4",
    });
    expect(reason(resizePart(wide, "training", { size: "wider" }))).toBe(
      "No room: the Focus corner is at its smallest there",
    );
    // A Home corner keeps Home's smallest size, 2 × 3, and the corner below takes the rows.
    const smaller = ok(resizePart(corners, "main", { size: "smaller" }));
    expect(rects(smaller.grid)).toMatchObject({
      main: "0,0,2×6",
      training: "0,6,2×6",
    });
  });
});

describe("focus", () => {
  it("changes a map's focus, and refuses what isn't one", () => {
    const garmin = normalFocus({ providers: ["garmin", "garmin"] })!;
    expect(garmin).toEqual({ providers: ["garmin"] });
    const focused = ok(
      changeLayout(training, { action: "focus_map", focus: garmin }),
    );
    expect(focused.grid.parts[0]).toEqual({
      ...training.parts[0],
      focus: garmin,
    });
    expect(focused.summary).toBe("Focused the map: providers garmin");
    expect(arrangementOnly(training, focused.grid)).toBeNull();
    const cleared = ok(
      changeLayout(focused.grid, { action: "focus_map", focus: null }),
    );
    expect(cleared.grid.parts[0]).not.toHaveProperty("focus");
    expect(cleared.summary).toBe("The map shows everything again");
    expect(
      reason(changeLayout(cleared.grid, { action: "focus_map", focus: null })),
    ).toBe("The map already shows everything");
    expect(
      reason(
        changeLayout(focused.grid, { action: "focus_map", focus: garmin }),
      ),
    ).toBe("The map already shows that");
    expect(
      reason(
        changeLayout(training, {
          action: "focus_map",
          part: "page",
          focus: garmin,
        }),
      ),
    ).toBe("The page isn't a map");
    expect(
      reason(
        changeLayout(layoutOf(null), { action: "focus_map", focus: garmin }),
      ),
    ).toBe("This dashboard has no map");
    const two = {
      parts: [
        part("map", "map", [0, 0, 6, 6]),
        part("sky", "map", [6, 0, 6, 6]),
        part("page", "page", [0, 6, 12, 6]),
      ],
    };
    expect(
      reason(changeLayout(two, { action: "focus_map", focus: garmin })),
    ).toBe("This dashboard has more than one map; name the one to focus");
    expect(partName(two, "sky")).toBe("the second map");
    expect(partLabel(two, "map")).toBe("First map");
  });

  it("keeps a focus as stored, and reads one it doesn't know as not whole", () => {
    expect(
      normalFocus({ query: "  Training  ", near: { star: plan } }),
    ).toEqual({
      query: "Training",
      near: { star: plan, depth: 1 },
    });
    for (const broken of [
      {},
      { query: "" },
      { query: "x".repeat(101) },
      { query: "a\u0007b" },
      { near: { star: plan, depth: 3 } },
      { near: { star: plan, hops: 1 } },
      { types: ["theme", "tag"] },
      { types: [] },
      { providers: ["Garmin"] },
      { groups: ["x".repeat(301)] },
      { owners: Array.from({ length: 11 }, (_, i) => `p${i}`) },
      { links: ["disputed"] },
      { tags: ["run"] },
      {
        groups: Array.from({ length: 20 }, (_, i) => `${i}`.padStart(80, "g")),
      },
      [],
    ])
      expect(normalFocus(broken)).toBeUndefined();
    expect(normalFocus(null)).toBeNull();
    expect(cleanFocus({ query: "run" })).toEqual({
      focus: { query: "run" },
      whole: true,
    });
    // A later piece's member leaves the map focused, but not arranged.
    expect(cleanFocus({ tags: ["run"] })).toEqual({ focus: {}, whole: false });
    expect(cleanFocus({ query: "run", tags: ["run"] })).toEqual({
      focus: { query: "run" },
      whole: false,
    });
    // Not as stored (a depth left out) reads, but isn't whole.
    expect(cleanFocus({ near: { star: plan } })).toEqual({
      focus: { near: { star: plan, depth: 1 } },
      whole: false,
    });
  });

  it("checks a focus as the engine's MapFocus does", () => {
    // Lists are counted as given and stored trimmed without repeats; values and the star are
    // trimmed; a query has no control character even before it is trimmed.
    expect(
      normalFocus({
        types: ["note", " note "],
        providers: [" garmin"],
        near: { star: ` ${plan} `, depth: 2 },
      }),
    ).toEqual({
      near: { star: plan, depth: 2 },
      types: ["note"],
      providers: ["garmin"],
    });
    expect(normalFocus({ types: [...focusTypes, "note"] })).toBeUndefined();
    expect(normalFocus({ query: "\tTraining" })).toBeUndefined();
    // A star and a group id have no control characters either, and are counted in
    // characters once trimmed.
    expect(
      normalFocus({ near: { star: "notes:notes:Plan\u0007.md" } }),
    ).toBeUndefined();
    expect(
      normalFocus({ groups: ["notes:alex:Run\u0001ning"] }),
    ).toBeUndefined();
    expect(
      normalFocus({
        near: { star: " notes:notes:Plan.md\t" },
        groups: ["🌊".repeat(300), " notes:alex:Running "],
      }),
    ).toEqual({
      near: { star: "notes:notes:Plan.md", depth: 1 },
      groups: ["🌊".repeat(300), "notes:alex:Running"],
    });
    // At most 1,500 characters as the database stores it: a space after each colon and comma.
    const groups = (n: number, width: number) => ({
      groups: Array.from({ length: n }, (_, i) => `${i}`.padStart(width, "g")),
    });
    const fits = groups(20, 71);
    expect(JSON.stringify(fits).length).toBeLessThanOrEqual(1500);
    expect(storedSize(fits)).toBeGreaterThan(1500);
    expect(normalFocus(fits)).toBeUndefined();
    expect(cleanFocus(fits).whole).toBe(false);
    expect(normalFocus(groups(20, 70))).toEqual(groups(20, 70));
  });
});

describe("Home", () => {
  it("applies a corner-only swap as move_tile, with no grid where there was none", () => {
    const hero = presetGrid("hero", defaultTiles);
    const swap = changeHome(null, hero, defaultTiles, {
      action: "move",
      part: "main",
      with: "recovery",
    });
    expect(swap).toEqual({
      ok: true,
      grid: null,
      swap: { tile: "main", to: "recovery" },
      summary: "Moved Main to the Recovery place",
      announcement: "Main moved to the Recovery place",
    });
    // With a grid, the grid is kept with the two corners' rectangles swapped, as move_tile
    // swaps them.
    const withGrid = changeHome(hero, hero, defaultTiles, {
      action: "move",
      part: "main",
      with: "recovery",
    });
    expect(withGrid).toMatchObject({
      ok: true,
      swap: { tile: "main", to: "recovery" },
    });
    expect(withGrid.ok && withGrid.grid).toEqual(
      swapCornerRects(hero, defaultTiles, "main", "recovery"),
    );
    // Lined up corners of the same size trade order: that is a swap too.
    const band = presetGrid("band", defaultTiles);
    expect(
      changeHome(null, band, defaultTiles, {
        action: "move",
        part: "recovery",
        place: "right",
      }),
    ).toMatchObject({
      ok: true,
      grid: null,
      swap: { tile: "recovery", to: "training" },
    });
    // Places follow the tiles.
    const tiles: Tiles = {
      ...defaultTiles,
      main: "recovery",
      recovery: "main",
    };
    const placed = presetGrid("hero", tiles);
    expect(rects(placed)).toMatchObject({
      main: "3,8,3×4",
      recovery: "0,0,3×12",
    });
    const moved = ok(movePart(placed, "main", { with: "training" }));
    expect(cornerSwap(placed, moved.grid, tiles, "main")).toEqual({
      tile: "main",
      to: "training",
    });
  });

  it("knows a grid that is the designed Home, and lets corners trade places when dropped on each other", () => {
    // The designed Home's places, as a grid drawing it with its tiles has them.
    const tiles: Tiles = { ...defaultTiles, main: "changed", changed: "main" };
    const hero = presetGrid("hero", tiles);
    const places = homePlaces(hero, tiles);
    expect(places).toEqual(homePresets.hero);
    expect(designedTiles(hero, places)).toEqual(tiles);
    // Two corners swapped are the designed Home with their places swapped.
    const swapped = swapCornerRects(hero, tiles, "main", "recovery");
    expect(designedTiles(swapped, places)).toEqual({
      ...tiles,
      main: "recovery",
      recovery: "changed",
    });
    // The map anywhere else, or a corner of another size, is only a grid's.
    const lower = ok(movePart(hero, "map", { place: "down" }));
    expect(designedTiles(lower.grid, places)).toBeNull();
    const taller = ok(resizePart(hero, "recovery", { size: "taller" }));
    expect(designedTiles(taller.grid, places)).toBeNull();
    // A measured Home has its own places.
    const measured: HomeGrid = {
      parts: hero.parts.map((p) =>
        p.id === "map" ? { ...p, h: 9 } : p.y === 8 ? { ...p, y: 9, h: 3 } : p,
      ),
    };
    expect(designedTiles(measured, places)).toBeNull();
    expect(designedTiles(measured, homePlaces(measured, tiles))).toEqual(tiles);
    // At Home a corner dropped on another corner takes its place, even one in its band, and
    // lined up corners of other sizes trade places too, as move_tile does.
    const designed = presetGrid("hero", defaultTiles);
    expect(
      dropMove(designed.parts, "main", { x: 6, y: 9 }, { x: 6, y: 0 }),
    ).toEqual({ with: "training" });
    // On the map, where it doesn't fit there, the band moves as on any grid.
    expect(
      dropMove(designed.parts, "main", { x: 11, y: 0 }, { x: 9, y: 0 }),
    ).toEqual({ place: "right" });
    const corners = presetGrid("corners", defaultTiles);
    expect(
      rects(ok(movePart(corners, "main", { with: "training" })).grid),
    ).toMatchObject({ main: "0,8,2×4", training: "0,0,2×8" });
    // Not on a dashboard: lined up parts keep trading order there.
    const board: Layout = {
      parts: [
        { id: "a", kind: "recovery", x: 0, y: 0, w: 6, h: 8 },
        { id: "b", kind: "training", x: 0, y: 8, w: 6, h: 4 },
        { id: "p", kind: "page", x: 6, y: 0, w: 6, h: 12 },
      ],
    };
    expect(rects(ok(movePart(board, "a", { with: "b" })).grid)).toMatchObject({
      a: "0,4,6×8",
      b: "0,0,6×4",
    });
  });

  it("keeps any other change as a grid, and refuses a focus", () => {
    const hero = presetGrid("hero", defaultTiles);
    const taller = changeHome(null, hero, defaultTiles, {
      action: "resize",
      part: "map",
      size: "taller",
    });
    expect(taller).toMatchObject({
      ok: true,
      swap: null,
      summary: "Made the map taller",
    });
    expect(taller.ok && taller.grid && homeGridProblem(taller.grid)).toBeNull();
    expect(
      changeHome(null, hero, defaultTiles, {
        action: "focus_map",
        focus: { query: "run" },
      }),
    ).toEqual({ ok: false, reason: "Home's map takes no focus" });
    expect(
      reason(
        changeHome(null, hero, defaultTiles, {
          action: "move",
          part: "page",
          place: "up",
        }),
      ),
    ).toBe("There's no part “page” here");
  });

  it("names its parts as Sources & settings does", () => {
    const hero = presetGrid("hero", defaultTiles);
    for (const corner of ["main", "recovery", "training", "changed"] as const) {
      expect(partLabel(hero, corner)).toBe(cornerNames[corner]);
      expect(partName(hero, corner)).toBe(`the ${cornerNames[corner]} corner`);
    }
    expect(homeShowingParts(hero).map((p) => `${p.id}:${p.kind}`)).toEqual([
      "main:corner",
      "map:map",
      "recovery:corner",
      "training:corner",
      "changed:corner",
    ]);
  });

  it("keeps a grid only when it keeps Home's rules", () => {
    for (const preset of ["corners", "hero", "band"] as const) {
      const grid = presetGrid(preset, defaultTiles);
      expect(homeGridProblem(grid)).toBeNull();
      expect(cleanHomeGrid(grid)).toEqual(grid);
      expect(Object.keys(homePresets[preset]).sort()).toEqual([
        "changed",
        "main",
        "map",
        "recovery",
        "training",
      ]);
    }
    const grid = presetGrid("corners", defaultTiles);
    const at = (id: string, change: Partial<Rect>) => ({
      parts: grid.parts.map((p) => (p.id === id ? { ...p, ...change } : p)),
    });
    expect(homeSmallest.main).toEqual({ w: 2, h: 3 });
    for (const broken of [
      null,
      { parts: grid.parts.slice(1) },
      { parts: [...grid.parts, grid.parts[0]] },
      at("training", { y: 9, h: 2 }),
      at("map", { w: 3 }),
      at("main", { h: 9 }),
      at("main", { x: 11 }),
      { parts: grid.parts.map((p) => ({ ...p, kind: "map" })) },
      { parts: grid.parts, extra: true },
    ])
      expect(cleanHomeGrid(broken)).toBeNull();
    // A grid kept in another order comes back in Home's order.
    expect(cleanHomeGrid({ parts: [...grid.parts].reverse() })).toEqual(grid);
  });
});

// Home's CSS grid as the app draws it in demo mode, measured in a browser at each size: each
// layout's resolved tracks and gaps, with every corner drawn (getComputedStyle on
// `.observatory`). The measured first grid snaps each part's area to the 12 × 12 grid.
const templates: Record<Preset, string> = {
  corners: `"main sky recovery" "training sky context"`,
  hero: `"main sky sky sky" "main recovery training context"`,
  band: `"sky sky sky sky" "main recovery training context"`,
};
type Drawn = [columns: number[], rows: number[], column: number, row: number];
const drawn: Record<string, Record<Preset, Drawn>> = {
  "1100x501": {
    corners: [[228, 540.625, 228], [205.469, 180], 26.4, 16],
    hero: [[290, 220.125, 220.141, 220.141], [211.469, 180], 33, 10],
    band: [[330.562, 206.609, 206.609, 206.625], [211.469, 180], 33, 10],
  },
  "1280x720": {
    corners: [[228, 703.688, 228], [415.719, 180], 30.72, 16],
    hero: [[290, 271.984, 271.984, 271.984], [421.719, 180], 38.4, 10],
    band: [[384.672, 240.422, 240.422, 240.438], [421.719, 180], 38.4, 10],
  },
  "1366x768": {
    corners: [[228, 781.625, 228], [454.922, 186.875], 32.784, 16],
    hero: [[290, 296.75, 296.766, 296.75], [455.109, 192.688], 40.98, 10],
    band: [[410.531, 256.578, 256.578, 256.594], [455.109, 192.688], 40.98, 10],
  },
  "1920x1080": {
    corners: [[297.594, 1144.34, 297.594], [669.516, 263.812], 46.08, 16],
    hero: [[364.797, 431.359, 431.375, 431.359], [746.641, 192.688], 57.6, 10],
    band: [[577, 360.641, 360.625, 360.641], [746.641, 192.688], 57.6, 10],
  },
};
const measureOf = (
  [columns, rows, column, row]: Drawn,
  areas: string,
  tiles: Tiles = defaultTiles,
) => measureHome({ columns, rows, gap: { column, row }, areas }, tiles);
// map, main, recovery, training, changed, each "x,y,w×h".
const measured: Record<string, Record<Preset, string>> = {
  "1100x501": {
    corners: "3,0,6×12 0,0,3×6 9,0,3×6 0,6,3×6 9,6,3×6",
    hero: "4,0,8×6 0,0,4×12 4,6,2×6 6,6,3×6 9,6,3×6",
    band: "0,0,12×6 0,6,4×6 4,6,3×6 7,6,2×6 9,6,3×6",
  },
  "1280x720": {
    corners: "2,0,8×12 0,0,2×8 10,0,2×8 0,8,2×4 10,8,2×4",
    hero: "3,0,9×8 0,0,3×12 3,8,3×4 6,8,3×4 9,8,3×4",
    band: "0,0,12×8 0,8,4×4 4,8,3×4 7,8,2×4 9,8,3×4",
  },
  "1366x768": {
    corners: "2,0,8×12 0,0,2×8 10,0,2×8 0,8,2×4 10,8,2×4",
    hero: "3,0,9×8 0,0,3×12 3,8,3×4 6,8,3×4 9,8,3×4",
    band: "0,0,12×8 0,8,4×4 4,8,3×4 7,8,2×4 9,8,3×4",
  },
  "1920x1080": {
    corners: "2,0,8×12 0,0,2×9 10,0,2×9 0,9,2×3 10,9,2×3",
    hero: "3,0,9×9 0,0,3×12 3,9,3×3 6,9,3×3 9,9,3×3",
    band: "0,0,12×9 0,9,4×3 4,9,3×3 7,9,2×3 9,9,3×3",
  },
};

describe("Home's first grid", () => {
  for (const [size, presets] of Object.entries(drawn))
    for (const preset of ["corners", "hero", "band"] as const)
      it(`measures ${preset} at ${size} within a cell of what is drawn`, () => {
        const m = measureOf(presets[preset], templates[preset]);
        const grid = measuredGrid(m)!;
        expect(Object.values(rects(grid)).join(" ")).toBe(
          measured[size][preset],
        );
        expect(homeGridProblem(grid)).toBeNull();
        expect(firstHomeGrid(preset, defaultTiles, m)).toEqual(grid);
        // Every edge lands on the grid line nearest the drawn edge (the middle of a gap),
        // so nothing moves by more than half a cell.
        const across = (m.width + m.gap.column) / 12,
          down = (m.height + m.gap.row) / 12;
        const line = (i: number, pitch: number, gap: number, end: number) =>
          i === 0 ? 0 : i === 12 ? end : i * pitch - gap / 2;
        for (const p of grid.parts) {
          const a = m.areas[p.id]!;
          const edges: [number, number, number, number, number][] = [
            [
              p.x,
              a.x === 0 ? 0 : a.x - m.gap.column / 2,
              across,
              m.gap.column,
              m.width,
            ],
            [
              p.x + p.w,
              a.x + a.width >= m.width - 0.01
                ? m.width
                : a.x + a.width + m.gap.column / 2,
              across,
              m.gap.column,
              m.width,
            ],
            [
              p.y,
              a.y === 0 ? 0 : a.y - m.gap.row / 2,
              down,
              m.gap.row,
              m.height,
            ],
            [
              p.y + p.h,
              a.y + a.height >= m.height - 0.01
                ? m.height
                : a.y + a.height + m.gap.row / 2,
              down,
              m.gap.row,
              m.height,
            ],
          ];
          for (const [i, at, pitch, gap, end] of edges)
            expect(Math.abs(line(i, pitch, gap, end) - at)).toBeLessThanOrEqual(
              pitch / 2 + 0.01,
            );
        }
      });

  it("puts each corner in the area of the place its tile has", () => {
    const tiles: Tiles = { ...defaultTiles, main: "changed", changed: "main" };
    const m = measureOf(drawn["1280x720"].hero, templates.hero, tiles);
    expect(m.areas.main).toEqual(
      measureOf(drawn["1280x720"].hero, templates.hero).areas.changed,
    );
    expect(rects(measuredGrid(m)!)).toMatchObject({
      main: "9,8,3×4",
      changed: "0,0,3×12",
    });
  });

  it("falls back to the table", () => {
    // A corner that gives its room away has no area.
    const empty = measureOf(
      drawn["1280x720"].hero,
      `"main sky sky sky" "main recovery recovery context"`,
    );
    expect(empty.areas.training).toBeNull();
    expect(measuredGrid(empty)).toBeNull();
    expect(firstHomeGrid("hero", defaultTiles, empty)).toEqual(
      presetGrid("hero", defaultTiles),
    );
    // A snapped corner below its smallest size.
    const flat = measureOf(
      [[228, 703.688, 228], [560, 40], 30.72, 16],
      templates.corners,
    );
    expect(measuredGrid(flat)).toBeNull();
    expect(firstHomeGrid("corners", defaultTiles, flat)).toEqual(
      presetGrid("corners", defaultTiles),
    );
    // A screen not drawing layouts measures nothing.
    expect(rects(firstHomeGrid("band", defaultTiles))).toEqual({
      map: "0,0,12×8",
      main: "0,8,4×4",
      recovery: "4,8,3×4",
      training: "7,8,3×4",
      changed: "10,8,2×4",
    });
    // Each corner takes the rectangle of the place its tile has.
    const swapped: Tiles = {
      ...defaultTiles,
      training: "changed",
      changed: "training",
    };
    expect(rects(presetGrid("corners", swapped))).toMatchObject({
      training: "10,8,2×4",
      changed: "0,8,2×4",
    });
  });
});

describe("layouts", () => {
  it("reads NULL as the page alone", () => {
    const alone = layoutOf(null);
    expect(alone).toEqual({
      parts: [{ id: "page", kind: "page", x: 0, y: 0, w: 12, h: 12 }],
    });
    expect(drawsAsToday(null)).toBe(true);
    expect(drawsAsToday(training)).toBe(false);
    expect(showingParts(null)).toEqual([
      { id: "page", kind: "page", x: 0, y: 0, w: 12, h: 12 },
    ]);
    expect(foldOf(alone, { width: 320, height: 200 }, 12)).toBe("saved");
    expect(arrangementOnly(null, alone)).toBeNull();
    expect(arrangeable(alone, true)).toBe(false);
    expect(cleanLayout(alone)).toEqual({ layout: alone, whole: true });
  });

  it("reads a layout whole, exactly as stored", () => {
    const card = part("sleep", "card", [10, 0, 2, 4], {
      dashboard: "00000000-0000-4000-8000-00000000d0a6",
    });
    const stored = {
      parts: [
        ...training.parts.map((p) => (p.id === "page" ? { ...p, w: 3 } : p)),
        card,
      ],
    };
    expect(cleanLayout(stored)).toEqual({ layout: stored, whole: true });
    expect(arrangeable(stored, true)).toBe(true);
    expect(arrangeable(stored, false)).toBe(false);
    // A card never names its own dashboard.
    const self = cleanLayout(stored, card.dashboard)!;
    expect(self.whole).toBe(false);
    expect(self.layout.parts.map((p) => p.id)).toEqual([
      "map",
      "page",
      "glucose",
    ]);
  });

  it("draws what it knows of a later piece's layout, and arranges none of it", () => {
    const later = {
      parts: [
        ...training.parts,
        { id: "weather", kind: "weather", x: 0, y: 0, w: 1, h: 1, city: "x" },
      ],
    };
    // Not drawn over anything: the later part needs a free cell, which the map has here.
    expect(cleanLayout(later)).toBeNull();
    const placed = {
      parts: [
        part("map", "map", [0, 0, 6, 12]),
        part("page", "page", [7, 0, 5, 8]),
        part("glucose", "glucose", [7, 8, 5, 4]),
        { id: "weather", kind: "weather", x: 6, y: 0, w: 1, h: 12, city: "x" },
      ],
    };
    expect(cleanLayout(placed)).toEqual({
      layout: { parts: placed.parts.slice(0, 3) },
      whole: false,
    });
    const focused = {
      parts: training.parts.map((p) =>
        p.kind === "map" ? { ...p, focus: { query: "run", tags: ["5k"] } } : p,
      ),
    };
    expect(cleanLayout(focused)).toMatchObject({
      layout: { parts: [{ id: "map", focus: { query: "run" } }, {}, {}] },
      whole: false,
    });
    expect(cleanLayout({ ...training, theme: "dark" })?.whole).toBe(false);
    expect(
      cleanLayout({
        parts: training.parts.map((p) =>
          p.kind === "page" ? { ...p, note: 1 } : p,
        ),
      })?.whole,
    ).toBe(false);
  });

  it("can't draw a layout that breaks the grid", () => {
    for (const broken of [
      null,
      { parts: [] },
      { parts: "page" },
      {
        parts: [
          part("page", "page", [0, 0, 12, 12]),
          part("page", "glucose", [0, 0, 2, 2]),
        ],
      },
      {
        parts: [
          part("page", "page", [0, 0, 8, 12]),
          part("map", "map", [6, 0, 6, 12]),
        ],
      },
      { parts: [part("map", "map", [0, 0, 12, 12])] },
      { parts: [part("Page", "page", [0, 0, 12, 12])] },
      { parts: [part("page", "page", [6, 0, 7, 12])] },
      {
        parts: [
          part("page", "page", [0, 0, 12, 12]),
          part("p2", "page", [0, 0, 0, 0]),
        ],
      },
    ])
      expect(cleanLayout(broken)).toBeNull();
  });

  it("checks every rule as the engine does", () => {
    expect(layoutProblem(training)).toBeNull();
    const cases: [unknown, string][] = [
      [{ parts: [] }, "A layout has 1 to 12 parts"],
      [
        { parts: [part("page", "page", [0, 0, 2, 12])] },
        "The part “page” is smaller than a page's smallest size, 3 × 3",
      ],
      [
        { parts: [part("page", "page", [0, 0, 12, 12], { focus: null })] },
        "The part “page” takes no “focus”",
      ],
      [
        {
          parts: [
            part("page", "page", [0, 0, 6, 12]),
            part("map", "map", [6, 0, 6, 12], { focus: { tags: [] } as never }),
          ],
        },
        "The focus of “map” isn't one",
      ],
      [
        {
          parts: [
            part("page", "page", [0, 0, 6, 6]),
            part("m1", "map", [6, 0, 6, 4]),
            part("m2", "map", [6, 4, 6, 4]),
            part("m3", "map", [6, 8, 6, 4]),
          ],
        },
        "A layout has at most 2 map parts",
      ],
      [
        {
          parts: [
            part("page", "page", [0, 0, 6, 6]),
            part("g1", "glucose", [6, 0, 6, 4]),
            part("g2", "glucose", [6, 4, 6, 4]),
          ],
        },
        "A layout has at most 1 glucose part",
      ],
      [
        { parts: [part("map", "map", [0, 0, 6, 6])] },
        "A layout has exactly one page part",
      ],
      [
        {
          parts: [
            part("page", "page", [0, 0, 6, 6]),
            part("c", "card", [6, 0, 6, 6], { dashboard: "nope" }),
          ],
        },
        "The card “c” names no other dashboard",
      ],
      [{ ...training, more: 1 }, "A layout is {parts: [...]}"],
    ];
    for (const [layout, problem] of cases)
      expect(layoutProblem(layout)).toBe(problem);
    // Sizes count as the database stores JSON, a space after each colon and comma.
    expect(storedSize({ a: [1, 2], b: "é" })).toBe(
      '{"a": [1, 2], "b": "é"}'.length,
    );
  });

  it("allows only an arrangement of the same parts", () => {
    const moved = ok(movePart(training, "map", { place: "right" })).grid;
    expect(arrangementOnly(training, moved)).toBeNull();
    const cases: [unknown, string][] = [
      [{ parts: moved.parts.slice(1) }, "A part was added or removed"],
      [
        { parts: [...moved.parts, part("insights", "insights", [5, 0, 3, 2])] },
        "A part was added or removed",
      ],
      [
        {
          parts: moved.parts.map((p) =>
            p.id === "glucose" ? { ...p, kind: "recovery" } : p,
          ),
        },
        "The part “glucose” changed its kind",
      ],
      [
        {
          parts: moved.parts.map((p) =>
            p.id === "page" ? { ...p, note: "x" } : p,
          ),
        },
        "Only the place, size or focus of “page” may change",
      ],
      [
        {
          parts: moved.parts.map((p) =>
            p.id === "page" ? { ...p, focus: { query: "x" } } : p,
          ),
        },
        "Only the place, size or focus of “page” may change",
      ],
      [
        { ...moved, theme: "dark" },
        "Only how the parts are arranged may change",
      ],
      [null, "Only how the parts are arranged may change"],
    ];
    for (const [after, problem] of cases)
      expect(arrangementOnly(training, after)).toBe(problem);
    const card = part("sleep", "card", [10, 0, 2, 4], {
      dashboard: "00000000-0000-4000-8000-00000000d0a6",
    });
    const withCard = { parts: [part("page", "page", [0, 0, 10, 12]), card] };
    expect(
      arrangementOnly(withCard, {
        parts: [
          withCard.parts[0],
          { ...card, dashboard: "00000000-0000-4000-8000-00000000d0a5" },
        ],
      }),
    ).toBe("The card “sleep” names another dashboard");
  });

  it("orders parts by row, then column, then id", () => {
    expect(
      readingOrder([
        { id: "b", x: 0, y: 4 },
        { id: "c", x: 6, y: 0 },
        { id: "a", x: 6, y: 0 },
        { id: "d", x: 0, y: 0 },
      ]).map((p) => p.id),
    ).toEqual(["d", "a", "c", "b"]);
    expect(
      showingParts(training, (star) =>
        star === plan ? "Training plan" : undefined,
      ),
    ).toEqual([
      {
        id: "map",
        kind: "map",
        x: 0,
        y: 0,
        w: 7,
        h: 12,
        focus: "near Training plan",
      },
      { id: "page", kind: "page", x: 7, y: 0, w: 5, h: 8 },
      { id: "glucose", kind: "glucose", x: 7, y: 8, w: 5, h: 4 },
    ]);
  });
});

describe("folding", () => {
  it("pins each kind's smallest size", () => {
    expect(
      Object.fromEntries(
        Object.entries(kindSizes).map(([kind, s]) => [
          kind,
          `${s.cells.w}×${s.cells.h} ${s.px.width}×${s.px.height}`,
        ]),
      ),
    ).toEqual({
      page: "3×3 280×180",
      map: "4×4 280×220",
      glucose: "2×2 200×110",
      recovery: "2×2 180×110",
      training: "2×2 180×110",
      changed: "2×2 200×110",
      insights: "3×2 260×140",
      card: "2×2 200×140",
    });
  });

  it("draws as saved while every part has its smallest pixels, then folds", () => {
    expect(foldOf(training, { width: 1200, height: 700 }, 16)).toBe("saved");
    // The page's 5 columns are under 280 px here.
    expect(foldOf(training, { width: 600, height: 700 }, 16)).toBe("two");
    expect(foldOf(training, { width: 559, height: 700 }, 16)).toBe("one");
    // Rows count too: glucose's 4 rows are under 110 px here.
    expect(
      foldOf(training, { width: 1200, height: 340 }, { column: 16, row: 16 }),
    ).toBe("two");
    const box = { width: 600, height: 400 };
    expect(
      foldedParts(training, "two", box).map(
        ({ part: p, columns, height }) => `${p.id} ${columns} ${height}`,
      ),
    ).toEqual(["map 2 220", "page 1 280", "glucose 1 null"]);
    expect(
      foldedParts(training, "one", box).every((f) => f.columns === 1),
    ).toBe(true);
    expect(foldedHeight("map", { width: 600, height: 1000 })).toBe(500);
    expect(foldedHeight("card", box)).toBe(180);
    expect(foldedHeight("insights", box)).toBeNull();
  });
});

describe("the screen's side", () => {
  const board = "00000000-0000-4000-8000-00000000D0A5";
  it("takes the piece 3 commands exactly", () => {
    expect(commandArgs("move", { part: "map", place: "right" })).toEqual({
      part: "map",
      place: "right",
    });
    expect(
      commandArgs("move", { part: "map", with: "page", dashboard: board }),
    ).toEqual({
      part: "map",
      with: "page",
      dashboard: board.toLowerCase(),
    });
    expect(
      commandArgs("move", { part: "main", x: 0, y: 11, dashboard: null }),
    ).toEqual({
      part: "main",
      x: 0,
      y: 11,
      dashboard: null,
    });
    expect(commandArgs("resize", { part: "map", size: "bigger" })).toEqual({
      part: "map",
      size: "bigger",
    });
    expect(commandArgs("resize", { part: "map", w: 12, h: 1 })).toEqual({
      part: "map",
      w: 12,
      h: 1,
    });
    expect(commandArgs("focus_map", {})).toEqual({ focus: null });
    expect(
      commandArgs("focus_map", {
        focus: { query: " run " },
        part: "map",
        dashboard: board,
      }),
    ).toEqual({
      focus: { query: "run" },
      part: "map",
      dashboard: board.toLowerCase(),
    });
    for (const [action, args] of [
      ["move", { part: "map" }],
      ["move", { part: "map", place: "right", with: "page" }],
      ["move", { part: "map", with: "map" }],
      ["move", { part: "map", x: 0 }],
      ["move", { part: "map", x: 12, y: 0 }],
      ["move", { part: "Map", place: "right" }],
      ["move", { part: "map", place: "middle" }],
      ["move", { part: "map", place: "right", dashboard: "home" }],
      ["resize", { part: "map", size: "huge" }],
      ["resize", { part: "map", w: 0, h: 4 }],
      ["resize", { part: "map", size: "bigger", w: 4, h: 4 }],
      ["focus_map", { focus: { tags: ["x"] } }],
      ["focus_map", { focus: {} }],
      ["focus_map", { part: "map", focus: null, extra: 1 }],
    ] as const)
      expect(commandArgs(action, args as Record<string, unknown>)).toBeNull();
    expect(
      arrangeCommand("move", commandArgs("move", { part: "map", x: 3, y: 4 })!),
    ).toEqual({ action: "move", part: "map", x: 3, y: 4 });
    expect(
      arrangeCommand("focus_map", commandArgs("focus_map", { part: "map" })!),
    ).toEqual({ action: "focus_map", part: "map", focus: null });
    expect(arrangeCommand("layout", { layout: "band" })).toBeNull();
  });

  it("keeps a Home grid in the settings, never null", () => {
    const grid = presetGrid("band", defaultTiles);
    const settings = { layout: "band", tiles: defaultTiles, home: null };
    expect(settingsValid(settings)).toBe(true);
    expect(settingsValid({ ...settings, grid })).toBe(true);
    expect(settingsValid({ layout: "band", tiles: defaultTiles, grid })).toBe(
      true,
    );
    expect(settingsValid({ ...settings, grid: null })).toBe(false);
    expect(settingsValid({ ...settings, grid: { parts: [] } })).toBe(false);
    expect(cleanSettings({ ...settings, grid })).toEqual({ ...settings, grid });
    expect(cleanSettings({ ...settings, grid: null })).toEqual(settings);
  });

  it("remembers the grid on this browser and follows other tabs", () => {
    const grid: HomeGrid = presetGrid("hero", defaultTiles);
    expect(savedGrid()).toBeNull();
    saveGrid(grid);
    expect(localStorage.getItem("rhythm-of-life.grid")).toBe(
      JSON.stringify(grid),
    );
    expect(savedGrid()).toEqual(grid);
    localStorage.setItem("rhythm-of-life.grid", "{}");
    expect(savedGrid()).toBeNull();
    saveGrid(null);
    expect(localStorage.getItem("rhythm-of-life.grid")).toBeNull();
    const changed = vi.fn();
    const stop = watchSettings(changed);
    window.dispatchEvent(
      new StorageEvent("storage", { key: "rhythm-of-life.grid" }),
    );
    expect(changed).toHaveBeenCalledTimes(1);
    stop();
  });

  it("reports parts in what a screen shows", () => {
    const showing = {
      view: "dashboard",
      layout: "hero",
      tiles: defaultTiles,
      narrow: false,
      parts: showingParts(training, () => "Training plan"),
    };
    expect(cleanShowing(showing)?.parts).toEqual(showing.parts);
    expect(
      cleanShowing({
        ...showing,
        parts: [{ ...showing.parts[0], focus: "x".repeat(150) }],
      })?.parts?.[0].focus,
    ).toHaveLength(100);
    expect(cleanShowing({ ...showing, parts: [] })?.parts).toEqual([]);
    for (const broken of [
      [{ ...showing.parts[0], x: 9 }],
      [{ ...showing.parts[0], kind: "Map" }],
      Array.from({ length: 13 }, () => showing.parts[1]),
      "map",
    ])
      expect(cleanShowing({ ...showing, parts: broken })).not.toHaveProperty(
        "parts",
      );
  });
});

describe("the dashboards' cleaners", () => {
  const [summary] = createDemoDashboards().list();
  const id = summary.id;
  it("carry a version's layout and its revision", () => {
    expect(cleanSummary(summary)).toMatchObject(unarranged);
    const {
      layout: _l,
      layoutRevision: _r,
      layoutNote: _n,
      layoutSavedAt: _s,
      ...older
    } = summary;
    expect(cleanSummary(older)).toMatchObject(unarranged);
    const arranged = {
      ...summary,
      layout: training,
      layoutRevision: 3,
      layoutNote: "Moved the map right",
      layoutSavedAt: "2026-10-31T12:00:00Z",
    };
    expect(cleanSummary(arranged)).toMatchObject({
      layout: training,
      layoutWhole: true,
      layoutRevision: 3,
      layoutNote: "Moved the map right",
      layoutSavedAt: "2026-10-31T12:00:00Z",
    });
    // Revision 0 is the layout the version was saved with: no note.
    expect(cleanSummary({ ...arranged, layoutRevision: 0 })).toMatchObject({
      layoutRevision: 0,
      layoutNote: null,
      layoutSavedAt: null,
    });
    // A layout that can't be drawn is the page alone, and isn't arranged; the dashboard stays.
    for (const broken of [
      { layout: { parts: [] } },
      { layout: "grid" },
      { layoutRevision: -1 },
      { layoutRevision: 1.5 },
    ])
      expect(cleanSummary({ ...arranged, ...broken })).toMatchObject({
        id,
        layout: null,
        layoutWhole: false,
      });
  });

  it("carry the arrangement in the feed read and a save's answer", () => {
    const data = {
      asOf: "2026-10-31T12:00:00Z",
      feeds: {},
      dashboard: { id, version: 2 },
    };
    expect(cleanDashboardData(data)?.dashboard).toEqual({ id, version: 2 });
    expect(
      cleanDashboardData({
        ...data,
        dashboard: {
          id,
          version: 2,
          layout: training,
          layoutRevision: 1,
          layoutNote: "Moved the map",
          layoutSavedAt: "2026-10-31T12:00:00Z",
        },
      })?.dashboard,
    ).toEqual({
      id,
      version: 2,
      layout: training,
      layoutWhole: true,
      layoutRevision: 1,
      layoutNote: "Moved the map",
      layoutSavedAt: "2026-10-31T12:00:00Z",
    });
    expect(
      cleanLayoutAnswer({ ok: true, layout: training, layoutRevision: 4 }, id),
    ).toEqual({
      ok: true,
      layout: training,
      layoutWhole: true,
      layoutRevision: 4,
    });
    expect(
      cleanLayoutAnswer({ ok: false, reason: "Not the newest version" }, id),
    ).toEqual({
      ok: false,
      reason: "Not the newest version",
    });
    expect(
      cleanLayoutAnswer(
        { ok: false, reason: "Changed since", layout: null, layoutRevision: 0 },
        id,
      ),
    ).toEqual({
      ok: false,
      reason: "Changed since",
      layout: null,
      layoutWhole: true,
      layoutRevision: 0,
    });
    for (const broken of [
      null,
      { ok: true },
      { ok: false },
      { ok: "yes", reason: "x" },
    ])
      expect(cleanLayoutAnswer(broken, id)).toBeNull();
  });
});
