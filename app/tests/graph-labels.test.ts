import { expect, it } from "vitest";
import {
  displayTitle,
  labelCandidates,
  labelFontSize,
  labelText,
  nodeHitRadius,
  placeLabels,
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
