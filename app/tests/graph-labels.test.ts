import { expect, it } from "vitest";
import {
  displayTitle,
  labelCandidates,
  labelFloor,
  labelFontSize,
  labelScale,
  labelText,
  labelWeight,
  namedAtRest,
  namedInShortSky,
  nodeHitRadius,
  overSky,
  placeLabels,
  quietSky,
  shortSkyHeight,
  skyFloor,
  skyRect,
  visibleBounds,
} from "../src/graph";
import type {
  LabelBox,
  LabelRequest,
  LabelTraits,
  ScreenRect,
} from "../src/graph";

// A fictional memory title shaped like a long Second Brain content prefix.
const excerpt =
  "Fictional planning note about weekend river walks and the shared reading list, see https://example.test/fictional/path?id=42 for the draft outline and the notes that follow it in the archive for later review";

it("keeps short titles whole on one line", () => {
  expect(labelText("Fictional sleep notes")).toBe("Fictional sleep notes");
  expect(labelText("A fictional thirty-char title.")).toBe(
    "A fictional thirty-char title.",
  );
  expect(labelText("Fictional morning glucose study")).toBe(
    "Fictional morning glucose…",
  );
});

it("shortens long excerpts to one line, cut at a word, with no raw links", () => {
  const label = labelText(excerpt);
  expect(label.length).toBeLessThanOrEqual(30);
  expect(label).toBe("Fictional planning note about…");
  expect(label).not.toMatch(/https?:|example\.test/);
  // Trailing punctuation is dropped before the ellipsis.
  expect(labelText("Fictional household — receipts, shared index")).toBe(
    "Fictional household…",
  );
});

it("cuts a single overlong word instead of overflowing", () => {
  const label = labelText("Fictional" + "x".repeat(60));
  expect(label).toHaveLength(30);
  expect(label.endsWith("…")).toBe(true);
});

it("removes links from display titles but keeps a title that is only a link", () => {
  expect(
    displayTitle("  Fictional   trip  https://example.test/a  plan "),
  ).toBe("Fictional trip plan");
  expect(displayTitle("https://example.test/only")).toBe(
    "https://example.test/only",
  );
  expect(
    labelText("https://example.test/" + "y".repeat(40)).length,
  ).toBeLessThanOrEqual(30);
});

// Screen pixels per graph unit: viewport width over the 820-unit viewBox, times zoom.
it.each([
  ["phone", 320 / 820],
  ["phone zoomed out", (320 / 820) * 0.55],
  ["desktop", 1440 / 820],
  ["large desktop", 2147 / 820],
  ["large desktop zoomed in", (2147 / 820) * 3.2],
])("sizes node tap targets in screen pixels on %s", (_, unit) => {
  for (const [theme, across, star] of [
    [false, 44, 9],
    [true, 56, 20],
  ] as const) {
    const r = nodeHitRadius(theme, unit);
    // At least the target size on screen and never smaller than the drawn star;
    // only the star itself makes it larger.
    expect(r * unit * 2).toBeGreaterThanOrEqual(across - 1e-9);
    expect(r).toBeGreaterThanOrEqual(star);
    expect(r * unit).toBeCloseTo(Math.max(across / 2, star * unit), 9);
  }
  // Graph-unit radii made large-display targets about 130px across.
  expect(nodeHitRadius(false, 2147 / 820) * (2147 / 820) * 2).toBeLessThan(50);
});

it.each([
  ["phone", 320 / 820],
  ["phone zoomed out", (320 / 820) * 0.55],
  ["desktop", 1440 / 820],
  ["large desktop", 2147 / 820],
  ["large desktop zoomed in", (2147 / 820) * 3.2],
])(
  "keeps label text one on-screen size and clear of the star on %s",
  (_, unit) => {
    for (const [theme, px, star] of [
      [false, 13, 8],
      [true, 16, 19],
    ] as const) {
      const font = labelFontSize(theme, unit);
      expect(font * unit).toBeCloseTo(px, 9);
      const height = 2 * font * 1.2;
      const [below, above, right, left] = labelCandidates(theme, font, height);
      // Box edges, as placeLabel computes them, relative to the node centre.
      const belowTop = below.y - font,
        aboveBottom = above.y - font + height;
      // Each side clears the drawn star by a gap of 30% of the on-screen text.
      expect((belowTop - star) * unit).toBeCloseTo(px * 0.3, 9);
      expect((-aboveBottom - star) * unit).toBeCloseTo(px * 0.3, 9);
      expect(right.x).toBeGreaterThan(star);
      expect(left.x).toBeLessThan(-star);
    }
    // A graph-unit floor made large-display labels about 36px tall.
    expect(labelFontSize(false, 2147 / 820) * (2147 / 820)).toBeCloseTo(13, 9);
  },
);

it("places every forced name, even when names must overlap", () => {
  // Five fictional stars on one spot: only one name fits without overlapping.
  const requests = [1, 2, 3, 4, 5].map((i) => ({
    id: `star-${i}`,
    point: { id: `star-${i}`, x: 400, y: 300, theme: false },
    size: { width: 90, height: 14, font: 12 },
    weight: i,
  }));
  const bounds = { left: 0, right: 820, top: 0, bottom: 600 };
  const free = placeLabels(requests, [], bounds, 1, new Set(), new Map());
  expect(free.size).toBeLessThan(5);
  const forced = placeLabels(
    requests.map((r) => ({ ...r, forced: true })),
    [],
    bounds,
    1,
    new Set(),
    new Map(),
  );
  expect(forced.size).toBe(5);
});

// The design refresh (work item 3419da9d): names read from across a room on a
// wall display, and a phone's sky keeps to its landmarks and the selection.

it("draws names larger on a wall-sized sky and never smaller anywhere", () => {
  for (const width of [200, 320, 390, 760, 1000])
    expect(labelScale(width)).toBe(1);
  expect(labelScale(1500)).toBeCloseTo(1.2, 9);
  expect(labelScale(1800)).toBeGreaterThan(labelScale(1500));
  // Capped at the tiles' own largest zoom (App.css --z).
  expect(labelScale(2100)).toBeCloseTo(1.44, 9);
  expect(labelScale(5000)).toBe(1.45);
  // The scale multiplies the on-screen size at every zoom.
  for (const unit of [0.4, 1.3, 2.6 * 3.2]) {
    expect(labelFontSize(false, unit, false, 1.4) * unit).toBeCloseTo(
      13 * 1.4,
      9,
    );
    expect(labelFontSize(true, unit, false, 1.4) * unit).toBeCloseTo(
      16 * 1.4,
      9,
    );
  }
});

it("ranks names: selection, hover or focus, pins, the focus's light, landmarks, dashboards, hubs, memories", () => {
  const ladder: LabelTraits[] = [
    { selected: true },
    { emphasis: true },
    { pinned: true },
    { lit: true, loose: true },
    { theme: true },
    { folder: true },
    { dashboard: true },
    { priority: "primary" },
    { priority: "secondary" },
    { memoryHub: true, memory: true },
    { memory: true },
    {},
    { loose: true },
  ];
  const weights = ladder.map(labelWeight);
  for (let i = 1; i < weights.length; i++)
    expect(weights[i - 1]).toBeGreaterThan(weights[i]);
  // A landmark outranks every dashboard, whatever else the dashboard is.
  expect(labelWeight({ theme: true, loose: true })).toBeGreaterThan(
    labelWeight({ dashboard: true, priority: "primary", memoryHub: true }),
  );
  // Nothing but a selection outranks a hovered or focused star.
  expect(labelWeight({ emphasis: true, loose: true })).toBeGreaterThan(
    labelWeight({
      pinned: true,
      lit: true,
      theme: true,
      folder: true,
      dashboard: true,
      priority: "primary",
      memoryHub: true,
      memory: true,
    }),
  );
});

it("names each cluster's hub at rest on a wide sky but not on a phone", () => {
  for (const compact of [true, false]) {
    for (const t of [
      { theme: true },
      { folder: true },
      { dashboard: true },
      { pinned: true },
      { memoryHub: true },
    ] satisfies LabelTraits[])
      expect(namedAtRest(t, compact)).toBe(true);
    expect(namedAtRest({ memory: true }, compact)).toBe(false);
    expect(namedAtRest({ loose: true }, compact)).toBe(false);
  }
  expect(namedAtRest({ priority: "primary" }, false)).toBe(true);
  expect(namedAtRest({ priority: "secondary" }, false)).toBe(true);
  expect(namedAtRest({ priority: "primary" }, true)).toBe(false);
  expect(namedAtRest({ priority: "secondary" }, true)).toBe(false);
});

const touching = (a: LabelBox, b: LabelBox) =>
  a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h;
function expectApart(placed: ReturnType<typeof placeLabels>) {
  const boxes = [...placed.values()].map((p) => p.box);
  for (let i = 0; i < boxes.length; i++)
    for (let j = i + 1; j < boxes.length; j++)
      expect(touching(boxes[i], boxes[j])).toBe(false);
}

// Three fictional dashboards bunched together with a topic just below them, as
// the demo's sky draws them on a 320px phone (about 0.39px per graph unit).
function bunched(selected?: string) {
  const unit = 320 / 820;
  const font = labelFontSize(false, unit, true);
  const dashboard = (id: string, x: number, y: number, title: string) => ({
    id,
    point: { x, y, theme: false },
    // A name line and the DASHBOARD tag under it.
    size: { width: title.length * font * 0.56, height: 2.3 * font * 1.2, font },
    traits: { dashboard: true, selected: id === selected } as LabelTraits,
  });
  const stars = [
    dashboard("glance", 380, 120, "Week at a glance"),
    dashboard("map", 420, 108, "Training map"),
    dashboard("week", 455, 116, "Sample training week"),
    {
      id: "training",
      point: { x: 420, y: 190, theme: true },
      size: {
        width: 8 * labelFontSize(true, unit, true) * 0.6,
        height: labelFontSize(true, unit, true) * 1.2,
        font: labelFontSize(true, unit, true),
      },
      traits: { theme: true } as LabelTraits,
    },
  ];
  const requests: LabelRequest[] = stars.map((s) => ({
    id: s.id,
    point: s.point,
    size: s.size,
    weight: labelWeight(s.traits),
    forced: s.id === selected,
  }));
  return placeLabels(
    requests,
    [],
    { left: 0, right: 820, top: 0, bottom: 400 },
    unit,
    new Set(),
    new Map(),
  );
}

it("quiets dashboard names that collide on a phone and keeps the landmark", () => {
  const placed = bunched();
  expect(placed.has("training")).toBe(true);
  // Before, all three dashboards were forced and sat on each other.
  expect(placed.size).toBeLessThan(4);
  expectApart(placed);
});

it("brings the selected dashboard's name forward and hides what it would cover", () => {
  for (const chosen of ["glance", "map", "week"]) {
    const placed = bunched(chosen);
    expect(placed.has(chosen)).toBe(true);
    expect(placed.get(chosen)!.choice).toBeGreaterThanOrEqual(0);
    expectApart(placed);
  }
});

it.each([
  ["a 320px phone", 320 / 820, true],
  ["a 390px phone", 390 / 820, true],
  ["a desktop", 1040 / 820, false],
  ["a 2560px wall display", (1850 / 820) * 1, false],
])(
  "never lets two shown names overlap on %s, even with long names",
  (_, unit, compact) => {
    // Forty fictional stars in a deterministic scatter, a few with long names.
    const scale = labelScale(unit * 820);
    const requests: LabelRequest[] = Array.from({ length: 40 }, (_, i) => {
      const theme = i % 9 === 0;
      const font = labelFontSize(theme, unit, compact, scale);
      const title = labelText(
        i % 5 === 0
          ? "Fictional notes on the long walk along the river and the reading list"
          : `Fictional record ${i}`,
        compact ? 20 : 30,
      );
      return {
        id: `star-${i}`,
        point: {
          x: 60 + ((i * 137) % 700),
          y: 50 + ((i * 89) % 500),
          theme,
        },
        size: { width: title.length * font * 0.56, height: font * 1.2, font },
        weight: labelWeight({
          theme,
          dashboard: i % 4 === 1,
          priority: i % 7 === 2 ? "primary" : undefined,
        }),
        forced: i === 3,
      };
    });
    const placed = placeLabels(
      requests,
      [],
      { left: 0, right: 820, top: 0, bottom: 600 },
      unit,
      new Set(),
      new Map(),
    );
    expect(placed.has("star-3")).toBe(true);
    expect(placed.size).toBeGreaterThan(3);
    expectApart(placed);
  },
);

it("keeps a smaller band free of names on the ambient sky than in the explorer", () => {
  for (const width of [320, 390, 1040, 1850]) {
    const px = (units: number) => (units * width) / 820;
    // The explorer keeps its controls and legend clear: 64px, or 60 units.
    expect(px(labelFloor(true, width))).toBeCloseTo(Math.max(64, px(60)), 9);
    // The ambient sky keeps only its one-line hint clear.
    expect(px(labelFloor(false, width))).toBeCloseTo(Math.max(28, px(26)), 9);
    expect(labelFloor(false, width)).toBeLessThan(labelFloor(true, width));
  }
});

it("treats a narrow or a short sky as a phone's", () => {
  // 320 and 390px phones upright, and phones on their side.
  expect(quietSky(320, 470)).toBe(true);
  expect(quietSky(390, 223)).toBe(true);
  expect(quietSky(560, 330)).toBe(true);
  // Desktop, iPad either way up, and a wall display.
  expect(quietSky(1040, 560)).toBe(false);
  expect(quietSky(900, 700)).toBe(false);
  expect(quietSky(1850, 1100)).toBe(false);
});

it("sets the selected name outside its star's ring", () => {
  const font = 13,
    size = { width: 90, height: font * 1.2, font };
  const request = (clear?: number): LabelRequest => ({
    id: "chosen",
    point: { x: 400, y: 300, theme: false },
    size,
    weight: labelWeight({ selected: true }),
    forced: true,
    clear,
  });
  const bounds = { left: 0, right: 820, top: 0, bottom: 600 };
  const plain = placeLabels([request()], [], bounds, 1, new Set(), new Map());
  const ringed = placeLabels(
    [request(27)],
    [],
    bounds,
    1,
    new Set(),
    new Map(),
  );
  // Below the star either way; past the 25-unit ring only with the clearance.
  expect(plain.get("chosen")!.box.y - 300).toBeLessThan(25);
  expect(ringed.get("chosen")!.box.y - 300).toBeGreaterThan(25);
});

// A 320 x 568 phone with two previews: the 820 x 220 sky is drawn into an svg 320 px wide and
// 40 px tall, letterboxed (0.18 px a unit, 86 px in from the left). Measured in the demo.
const phone = { a: 0.181, d: 0.181, e: 85.8, f: 292.6 };
const toScreen = (b: LabelBox, m: typeof phone) => ({
  left: b.x * m.a + m.e,
  top: b.y * m.d + m.f,
  right: (b.x + b.w) * m.a + m.e,
  bottom: (b.y + b.h) * m.d + m.f,
});

it("reads a rectangle on screen in the sky's own units", () => {
  const r = skyRect(
    { left: 85.8, top: 292.6, right: 234.22, bottom: 332.42 },
    phone,
  );
  expect(r.left).toBeCloseTo(0, 9);
  expect(r.top).toBeCloseTo(0, 9);
  expect(r.right).toBeCloseTo(820, 0);
  expect(r.bottom).toBeCloseTo(220, 0);
});

it("fits a name to the part of the sky that shows, 4 px clear of its bottom", () => {
  // Half a pixel a unit; only the top 100 px of the svg show, the rest under a preview.
  const m = { a: 0.5, d: 0.5, e: 10, f: 100 };
  const drawn = { left: 8, right: 812, top: 8, bottom: 380 };
  const visible: ScreenRect = { left: 10, top: 100, right: 420, bottom: 200 };
  const bounds = visibleBounds(drawn, visible, m);
  expect(bounds).toEqual({ left: 8, right: 812, top: 8, bottom: 192 });
  // A landmark low in what shows: its name below the star would be cut by
  // the edge, so it sits above the star instead (or, with no room, not at all).
  const font = labelFontSize(true, m.a);
  const request: LabelRequest = {
    id: "health",
    point: { x: 400, y: 140, theme: true },
    size: { width: 6 * font * 0.6, height: font * 1.2, font },
    weight: labelWeight({ theme: true }),
  };
  const free = placeLabels([request], [], drawn, m.a, new Set(), new Map());
  expect(toScreen(free.get("health")!.box, m).bottom).toBeGreaterThan(200);
  const fitted = placeLabels(
    [request],
    [],
    bounds,
    m.a,
    new Set(),
    new Map(),
  ).get("health")!;
  expect(fitted.choice).toBe(1);
  const box = toScreen(fitted.box, m);
  expect(box.top).toBeGreaterThanOrEqual(visible.top);
  expect(box.bottom).toBeLessThanOrEqual(visible.bottom - 4);
});

it("paints only landmarks, pins, the selection and the star in hand in a sky under 88 px", () => {
  expect(shortSkyHeight).toBe(88);
  for (const kept of [
    { theme: true },
    { folder: true },
    { pinned: true },
    { selected: true },
    { emphasis: true },
  ] as LabelTraits[])
    expect(namedInShortSky(kept)).toBe(true);
  for (const quiet of [
    { dashboard: true },
    { memoryHub: true, memory: true },
    { priority: "primary" },
    { loose: true },
  ] as LabelTraits[])
    expect(namedInShortSky(quiet)).toBe(false);
});

it("keeps names 4 px clear of the explorer's zoom controls, the selection still first", () => {
  // A 390 px explorer at 1:1 (one unit a pixel): the controls stand at the right edge.
  const unit = { a: 1, d: 1, e: 0, f: 0 };
  const toolbar: ScreenRect = { left: 330, top: 340, right: 382, bottom: 530 };
  const reserved = overSky(toolbar, unit);
  expect(reserved).toEqual({ x: 326, y: 336, w: 60, h: 198 });
  const font = labelFontSize(false, 1, true);
  const named = (id: string, selected = false): LabelRequest => ({
    id,
    point: { x: 320, y: 380, theme: false },
    size: { width: 130, height: font * 1.2, font },
    weight: labelWeight({ selected }),
    forced: selected,
  });
  const bounds = { left: 0, right: 390, top: 0, bottom: 600 };
  const placed = placeLabels(
    [named("Resting heart rate")],
    [reserved],
    bounds,
    1,
    new Set(),
    new Map(),
  ).get("Resting heart rate")!;
  // Off the controls: a side or a place above or below that leaves them 4 px.
  const b = placed.box;
  expect(
    b.x + b.w <= toolbar.left - 4 ||
      b.y + b.h <= toolbar.top - 4 ||
      b.y >= toolbar.bottom + 4,
  ).toBe(true);
  // The selected name is still always drawn.
  expect(
    placeLabels(
      [named("chosen", true)],
      [overSky({ left: 0, top: 0, right: 390, bottom: 600 }, unit)],
      bounds,
      1,
      new Set(),
      new Map(),
    ).has("chosen"),
  ).toBe(true);
});

// A still sky (reduced motion) keeps its first placement, so its names must clear the hint from
// the start: measured at 1024 x 1366, the sky is 675 to 1114 px and its hint 1095 to 1112.
it("keeps names above the hint across the sky's lower edge, not beside it", () => {
  const sky: ScreenRect = { left: 0, top: 675, right: 1024, bottom: 1114 };
  const hint: ScreenRect = { left: 430, top: 1095, right: 594, bottom: 1112 };
  expect(skyFloor(sky, [hint])).toBe(1095);
  // Names then end 4 px above it, wherever they are across the sky.
  const m = { a: 1, d: 1, e: 0, f: 675 };
  const bounds = visibleBounds(
    { left: 8, right: 812, top: 8, bottom: 2000 },
    { ...sky, bottom: skyFloor(sky, [hint]) },
    m,
  );
  expect(bounds.bottom + m.f).toBe(1091);
  // A line in the sky's upper half, or off to its side, sets no floor.
  expect(
    skyFloor(sky, [{ left: 430, top: 700, right: 594, bottom: 717 }]),
  ).toBe(1114);
  expect(
    skyFloor(sky, [{ left: 1100, top: 1095, right: 1200, bottom: 1112 }]),
  ).toBe(1114);
  expect(skyFloor(sky, [])).toBe(1114);
});
