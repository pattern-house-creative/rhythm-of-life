// Touch gestures on the sky, kept apart from the DOM so they can be tested.
// Distances are screen pixels; times are milliseconds.

// Press and hold this long, without wandering, to pick up a star.
export const holdMs = 450;
// A finger may wander this far and still tap or hold.
export const tapSlop = 10;
// A two-finger tap lifts within this time. One finger taps until it has been
// down long enough to hold.
export const tapMs = 350;
// A second tap this soon and this close zooms in.
export const doubleTapMs = 320;
export const doubleTapPx = 40;
// A tap means the nearest star within a fingertip's reach.
export const starReach = 30;

export interface ScreenStar {
  id: string;
  x: number;
  y: number;
}
export function nearestStar(
  stars: ScreenStar[],
  x: number,
  y: number,
  reach = starReach,
) {
  let best: ScreenStar | null = null,
    distance = reach;
  for (const star of stars) {
    const d = Math.hypot(star.x - x, star.y - y);
    if (d <= distance) {
      best = star;
      distance = d;
    }
  }
  return best;
}

export interface Tap {
  x: number;
  y: number;
  t: number;
}
export const isDoubleTap = (first: Tap | null, second: Tap) =>
  !!first &&
  second.t - first.t <= doubleTapMs &&
  Math.hypot(second.x - first.x, second.y - first.y) <= doubleTapPx;

// A finger's speed as it lifted, in pixels per millisecond, from the last
// tenth of a second of its path. A finger that stopped before lifting has
// no speed left.
export function flickVelocity(
  samples: { t: number; x: number; y: number }[],
  now: number,
) {
  const recent = samples.filter((s) => now - s.t <= 100);
  const first = recent[0],
    last = recent.at(-1);
  if (!first || !last || last === first || now - last.t > 60)
    return { x: 0, y: 0 };
  const dt = Math.max(1, last.t - first.t);
  return { x: (last.x - first.x) / dt, y: (last.y - first.y) / dt };
}
// A flick keeps the sky gliding when the finger was moving faster than this,
// slowing by about a twentieth every frame until it is barely moving.
export const flickMin = 0.3;
export const glideStop = 0.02;
export const glide = (speed: number, dt: number) =>
  speed * Math.pow(0.95, dt / 16);
