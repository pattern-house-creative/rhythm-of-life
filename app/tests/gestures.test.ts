import { expect, it } from "vitest";
import {
  doubleTapMs,
  flickMin,
  flickVelocity,
  glide,
  isDoubleTap,
  nearestStar,
  starReach,
} from "../src/gestures";

const stars = [
  { id: "a", x: 100, y: 100 },
  { id: "b", x: 130, y: 100 },
  { id: "c", x: 300, y: 300 },
];

it("takes a tap to the nearest star within a fingertip's reach", () => {
  // Between two stars, the closer one wins, whichever is drawn on top.
  expect(nearestStar(stars, 112, 104)?.id).toBe("a");
  expect(nearestStar(stars, 120, 98)?.id).toBe("b");
  // Just off a star still reaches it; open sky does not.
  expect(nearestStar(stars, 300 + starReach - 2, 300)?.id).toBe("c");
  expect(nearestStar(stars, 200, 200)).toBeNull();
});

it("counts two quick taps in one place as a double tap", () => {
  const first = { x: 50, y: 50, t: 1000 };
  expect(isDoubleTap(null, first)).toBe(false);
  expect(isDoubleTap(first, { x: 60, y: 55, t: 1200 })).toBe(true);
  expect(isDoubleTap(first, { x: 60, y: 55, t: 1000 + doubleTapMs + 1 })).toBe(
    false,
  );
  expect(isDoubleTap(first, { x: 150, y: 50, t: 1200 })).toBe(false);
});

it("reads a flick's speed from the end of the finger's path", () => {
  // A finger moving right at 1px per ms as it lifts.
  const path = [0, 20, 40, 60, 80, 100].map((t) => ({ x: t, y: 0, t }));
  const fast = flickVelocity(path, 105);
  expect(fast.x).toBeCloseTo(1, 5);
  expect(fast.x).toBeGreaterThan(flickMin);
  // A finger that stopped before lifting does not glide.
  expect(flickVelocity(path, 400)).toEqual({ x: 0, y: 0 });
  // One sample is no direction at all.
  expect(flickVelocity([{ x: 0, y: 0, t: 0 }], 10)).toEqual({ x: 0, y: 0 });
});

it("slows a glide frame by frame, whatever the frame rate", () => {
  expect(glide(1, 16)).toBeCloseTo(0.95, 5);
  // Two short frames slow it as much as one long one.
  expect(glide(glide(1, 8), 8)).toBeCloseTo(glide(1, 16), 10);
});
