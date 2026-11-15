import type { GlucoseHistory } from "./model";

// The app's existing range is fixed, not a person-specific target. Match the
// engine's HealthSemantics conversion; rounded display labels do not set bounds.
export const glucoseRangeMgDl = [70, 180] as const;
export const mgDlPerMmol = 18.0182;
const day = 86_400_000;
export type GlucosePoint = { time: number; value: number; slot: number };
export function launcherGlucoseHistory(
  history: GlucoseHistory | null,
  now: number,
  displayUnit = "mg/dL",
) {
  const unit = displayUnit === "mmol/L" ? "mmol/L" : "mg/dL";
  const scale = unit === "mmol/L" ? mgDlPerMmol : 1;
  const range = glucoseRangeMgDl.map((v) => v / scale) as [number, number];
  const base = {
    start: now - day,
    end: now,
    unit,
    range,
    coverage: 0,
    knownSlots: 0,
    expectedSlots: 0,
    within: null as number | null,
    segments: [] as GlucosePoint[][],
    points: [] as GlucosePoint[],
    archive: history?.sourceKind === "archive",
  };
  if (
    !history ||
    history.provenance.demo ||
    !Number.isFinite(now) ||
    history.status === "unavailable" ||
    history.status === "missing"
  )
    return { ...base, reason: "History unavailable" };
  const interval = history.intervalMinutes * 60_000;
  const from = Date.parse(history.start),
    until = Date.parse(history.end);
  if (
    history.timestampBasis !== "verified" ||
    !["mg/dL", "mmol/L"].includes(history.unit) ||
    !Number.isFinite(from) ||
    !Number.isFinite(until) ||
    until <= from ||
    !Number.isFinite(interval) ||
    interval < 60_000 ||
    interval > 900_000 ||
    day % interval !== 0
  )
    return { ...base, reason: "History not verified" };
  type Slot = { band: number | null; point?: GlucosePoint };
  const slots = new Map<number, Slot>();
  const times = new Map<
    number,
    { value: number; comparison?: "<" | ">" } | null
  >();
  for (const s of history.samples) {
    const time = Date.parse(s.timestamp);
    if (
      !Number.isFinite(time) ||
      time < Math.max(base.start, from) ||
      time >= Math.min(now, until) ||
      !Number.isFinite(s.value) ||
      s.value <= 0
    )
      continue;
    const prior = times.get(time);
    if (prior === undefined) times.set(time, s);
    else if (
      prior &&
      (prior.value !== s.value || prior.comparison !== s.comparison)
    )
      times.set(time, null);
  }
  for (const [time, s] of [...times].sort(([a], [b]) => a - b)) {
    const index = Math.floor((time - base.start) / interval);
    const value = s
      ? s.value * (history.unit === "mmol/L" ? mgDlPerMmol : 1)
      : NaN;
    // Only unambiguous bounds count. E.g. <200 cannot establish a range band.
    const band = !s
      ? null
      : s.comparison === "<"
        ? value <= 70
          ? -1
          : null
        : s.comparison === ">"
          ? value >= 180
            ? 1
            : null
          : value < 70 - 1e-9
            ? -1
            : value > 180 + 1e-9
              ? 1
              : 0;
    const previous = slots.get(index);
    const point =
      s && !s.comparison
        ? { time, value: value / scale, slot: index }
        : undefined;
    // One vote per cadence slot. Conflicting bands/duplicate timestamps stay
    // unknown rather than weighting an oversampled interval or guessing time.
    slots.set(index, {
      band: previous && previous.band !== band ? null : band,
      point,
    });
  }
  const known = [...slots.values()].filter((s) => s.band !== null);
  const expected = day / interval;
  const coverage = (known.length / expected) * 100;
  const points = [...slots.values()].flatMap((s) =>
    s.band !== null && s.point ? [s.point] : [],
  );
  const segments: GlucosePoint[][] = [];
  for (const p of points) {
    const segment = segments.at(-1),
      last = segment?.at(-1);
    // Straight sample joins only, no smoothing, extrapolation or gap filling.
    if (
      last &&
      p.slot === last.slot + 1 &&
      p.time - last.time <= interval * 1.5
    )
      segment!.push(p);
    else segments.push([p]);
  }
  return {
    ...base,
    points,
    segments,
    coverage,
    knownSlots: known.length,
    expectedSlots: expected,
    within:
      history.complete && coverage >= 70 && known.length
        ? (known.filter((s) => s.band === 0).length / known.length) * 100
        : null,
    reason: !history.complete
      ? "Incomplete history"
      : coverage < 70
        ? "Needs 70% coverage"
        : null,
  };
}
