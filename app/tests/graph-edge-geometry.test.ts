import { expect, it } from "vitest";
import { edgeClearance, graphEdgeGeometry, placeLabel } from "../src/graph";
import type { LabelBox } from "../src/graph";

// Screen pixels per graph unit: viewport width over the 820-unit viewBox, times zoom.
const phone = 320 / 820,
  phoneZoomedOut = phone * 0.55,
  desktop = 1440 / 820,
  desktopZoomedIn = desktop * 3.2;
const square = (m: { x: number; y: number }, half: number): LabelBox => ({
  x: m.x - half,
  y: m.y - half,
  w: half * 2,
  h: half * 2,
});
// Chebyshev distance from a link midpoint to the nearest edge of a label box.
const gap = (m: { x: number; y: number }, b: LabelBox) =>
  Math.max(b.x - m.x, m.x - (b.x + b.w), b.y - m.y, m.y - (b.y + b.h), 0);

it.each([
  ["phone", phone],
  ["phone zoomed out", phoneZoomedOut],
  ["desktop", desktop],
  ["desktop zoomed in", desktopZoomedIn],
])(
  "reserves 24 screen pixels, the tap strip's width, around link midpoints on %s",
  (_, unit) => {
    expect(edgeClearance(unit) * 2 * unit).toBeCloseTo(24, 9);
  },
);

it("keeps phone labels off a short link's midpoint where fixed graph units did not", () => {
  const a = { x: 300, y: 300, theme: false },
    b = { x: 400, y: 300 };
  const { midpoint } = graphEdgeGeometry(a, b);
  // A short label at the phone font size (33 graph units for 15px text).
  const size = { width: 66, height: 33 * 1.2, font: 33 },
    bounds = { left: 0, right: 820, top: 0, bottom: 820 };
  const fixed = placeLabel(a, size, [square(midpoint, 14)], bounds);
  expect(fixed.choice).toBe(0);
  expect(gap(midpoint, fixed.box) * phone).toBeLessThan(16);
  const scaled = placeLabel(
    a,
    size,
    [square(midpoint, edgeClearance(phone))],
    bounds,
  );
  expect(scaled.choice).toBeGreaterThan(0);
  expect(gap(midpoint, scaled.box) * phone).toBeGreaterThanOrEqual(16);
});

it("hides a label rather than covering a link when no position fits", () => {
  const point = { x: 400, y: 400, theme: false };
  const crowded = [square(point, 400)];
  const placed = placeLabel(
    point,
    { width: 80, height: 18, font: 15 },
    crowded,
    {
      left: 0,
      right: 820,
      top: 0,
      bottom: 820,
    },
  );
  expect(placed.choice).toBe(-1);
});

it.each([
  [
    { x: 100, y: 100 },
    { x: 300, y: 100 },
    { x: 205, y: 94 },
  ],
  [
    { x: 100, y: 100 },
    { x: 100, y: 300 },
    { x: 105, y: 194 },
  ],
  [
    { x: 100, y: 100 },
    { x: 300, y: 300 },
    { x: 205, y: 194 },
  ],
])(
  "keeps link-center clearance aligned to its quadratic path",
  (a, b, midpoint) => {
    const geometry = graphEdgeGeometry(a, b);
    expect(geometry.midpoint).toEqual(midpoint);
    expect(geometry.path).toBe(
      `M${a.x},${a.y} Q${(a.x + b.x) / 2 + 10},${(a.y + b.y) / 2 - 12} ${b.x},${b.y}`,
    );
    expect(graphEdgeGeometry(b, a).midpoint).toEqual(midpoint);
    expect(
      graphEdgeGeometry(
        { x: a.x + 17, y: a.y - 9 },
        { x: b.x + 17, y: b.y - 9 },
      ).midpoint,
    ).toEqual({ x: midpoint.x + 17, y: midpoint.y - 9 });
  },
);
