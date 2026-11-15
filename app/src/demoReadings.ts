import type { GraphSeries } from "./knowledge";

// Fictional data star readings for the demo, shaped like the engine's `data`
// operation: per metric, daily low/average/high/count and a latest reading.
// Deterministic for a given day, so the demo looks the same on every open. The
// demo's stars are everyday wellbeing: mood, sleep, steps and heart rate.
export type ReadingShape = {
  unit: string;
  days: number;
  // Typical daily value, day-to-day wobble, readings per day and the spread
  // between a day's low and high around its average.
  mean: number;
  wobble: number;
  count: number;
  spread?: number;
  // Some days have no reading (rest days, a sensor change).
  gaps?: number;
};
const days = 30;
const groups: Record<string, Record<string, ReadingShape>> = {
  "demo/mood": {
    mood: { unit: "score", days, mean: 3.6, wobble: 0.8, count: 1 },
  },
  "demo/sleep": {
    sleep_minutes: { unit: "min", days, mean: 435, wobble: 35, count: 1 },
  },
  "demo/activity": {
    steps: { unit: "count", days, mean: 8200, wobble: 2600, count: 1 },
  },
  "demo/heart": {
    resting_heart_rate: { unit: "bpm", days, mean: 58, wobble: 3, count: 1 },
  },
};

// The engine's own label rule: words of the metric name, first one capitalized.
const label = (metric: string) => {
  const text = metric
    .split("_")
    .map((w) => (w === "hrv" ? "HRV" : w === "spo2" ? "SpO2" : w))
    .join(" ");
  return text[0].toUpperCase() + text.slice(1);
};
function random(seed: string) {
  let h = 2166136261;
  for (const c of seed) h = Math.imul(h ^ c.charCodeAt(0), 16777619);
  return () => {
    h = Math.imul(h ^ (h >>> 15), 2246822507);
    h = Math.imul(h ^ (h >>> 13), 3266489909);
    return ((h ^= h >>> 16) >>> 0) / 4294967296;
  };
}
const round = (v: number, unit: string) =>
  unit === "breaths/min" || v < 10 ? Math.round(v * 100) / 100 : Math.round(v);

export const demoReadings = (id: string, today = new Date()) =>
  readingsFrom(groups, id, today);

// One data star's readings from a table of shapes, keyed "<provider>/<group>".
export function readingsFrom(
  shapes: Record<string, Record<string, ReadingShape>>,
  id: string,
  today = new Date(),
): GraphSeries[] | null {
  const [, account, star] = id.split(":").map(decodeURIComponent);
  const group =
    shapes[`${account?.replace(/^data:/, "")}/${star?.split(":")[1]}`];
  if (!group) return null;
  const day = (back: number) => {
    const d = new Date(today);
    d.setDate(d.getDate() - back);
    return d.toLocaleDateString("en-CA");
  };
  return Object.entries(group).map(([metric, s]) => {
    const next = random(`${metric}:${day(0)}`);
    const days = [];
    for (let back = s.days - 1; back >= 0; back--) {
      if (s.gaps && next() < s.gaps) continue;
      const mean = Math.max(0, s.mean + (next() - 0.5) * 2 * s.wobble);
      const spread = (s.spread ?? 0) * (0.6 + next() * 0.8);
      days.push({
        date: day(back),
        min: round(Math.max(0, mean - spread * 0.45), s.unit),
        mean: round(mean, s.unit),
        max: round(mean + spread * 0.55, s.unit),
        count: s.count,
      });
    }
    const last = days.at(-1);
    return {
      metric,
      label: label(metric),
      unit: s.unit,
      latest: last
        ? { value: last.mean, observedAt: null, date: last.date }
        : null,
      days,
    };
  });
}
