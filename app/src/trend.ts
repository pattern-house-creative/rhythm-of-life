import { hasMeasurement, healthStatus } from "./health";
import type { ObservatoryData } from "./model";

// Medtronic's trend arrows (its pump and Guardian Connect user guides): the rate
// is the sensor glucose change over the last 20 minutes, in mg/dL per minute.
// Only up or down, never diagonal or flat: under 1 shows no arrow, 1–2 one,
// 2–3 two, and 3 or more three. Dexcom's arrows follow a different rule.
export const trendMinutes = 20;
// Readings arrive every five minutes, so the earlier reading may sit up to one
// slot either side of 20 minutes back.
export const trendToleranceMinutes = 5;
// The latest reading must be this fresh to draw an arrow (the app's stale age).
export const trendMaxAgeMinutes = 20;

export type ArrowCount = 0 | 1 | 2 | 3;
export interface GlucoseTrend {
  arrows: ArrowCount;
  direction: "up" | "down" | null; // null while steady (no arrow)
  rate: number; // mg/dL per minute, signed
  delta: number; // mg/dL between the two readings, signed
  minutes: number; // between the two readings
  at: string;
  from: string;
}
type Reading = { timestamp: string; value: number; comparison?: "<" | ">" };

export function arrowCount(rate: number): ArrowCount {
  // Rounded so 40 mg/dL over exactly 20 minutes lands on two, not 1.9999.
  const r = Math.round(Math.abs(rate) * 1000) / 1000;
  return r >= 3 ? 3 : r >= 2 ? 2 : r >= 1 ? 1 : 0;
}

// null means unknown: no fresh latest reading, or no reading about 20 minutes
// before it. A trend with zero arrows means steady.
export function glucoseTrend(
  latest: Reading | null | undefined,
  samples: readonly Reading[],
  {
    now = Date.now(),
    maxAgeMinutes = trendMaxAgeMinutes,
    ignoreAge = false,
  }: { now?: number; maxAgeMinutes?: number; ignoreAge?: boolean } = {},
): GlucoseTrend | null {
  if (!latest || latest.comparison) return null;
  const at = Date.parse(latest.timestamp);
  if (!Number.isFinite(at) || !Number.isFinite(latest.value)) return null;
  if (latest.value <= 0) return null;
  if (!ignoreAge && (now - at > maxAgeMinutes * 60_000 || at - now > 60_000))
    return null;
  const target = at - trendMinutes * 60_000,
    tolerance = trendToleranceMinutes * 60_000;
  let best: { time: number; reading: Reading } | null = null;
  for (const reading of samples) {
    const time = Date.parse(reading.timestamp);
    if (
      reading.comparison ||
      !Number.isFinite(time) ||
      !Number.isFinite(reading.value) ||
      reading.value <= 0 ||
      Math.abs(time - target) > tolerance
    )
      continue;
    if (!best || Math.abs(time - target) < Math.abs(best.time - target))
      best = { time, reading };
  }
  if (!best) return null;
  const minutes = (at - best.time) / 60_000;
  const delta = latest.value - best.reading.value;
  const rate = delta / minutes;
  const arrows = arrowCount(rate);
  return {
    arrows,
    direction: arrows === 0 ? null : rate > 0 ? "up" : "down",
    rate,
    delta,
    minutes,
    at: latest.timestamp,
    from: best.reading.timestamp,
  };
}

// The arrow beside the current reading: that reading against the CGM sample
// nearest 20 minutes before it, from the 24-hour series or the history (the
// same sensor stream). Stale or missing readings get no arrow. The demo's day
// is fixed, so its age is not checked.
export function currentTrend(data: ObservatoryData, now = Date.now()) {
  const current = data.current;
  if (
    current.unit !== "mg/dL" ||
    current.value === null ||
    !current.provenance.observedAt ||
    healthStatus(current, now) !== "available"
  )
    return null;
  const samples = [
    ...(hasMeasurement(data.glucose, now) ? data.glucose.samples : []),
    ...(data.glucoseHistory && hasMeasurement(data.glucoseHistory, now)
      ? data.glucoseHistory.samples
      : []),
  ];
  return glucoseTrend(
    { timestamp: current.provenance.observedAt, value: current.value },
    samples,
    { now, ignoreAge: current.provenance.demo },
  );
}

const speed = ["Steady", "", "quickly", "very quickly"] as const;
// "Falling quickly", "Rising", "Steady"; null when unknown.
export function trendWords(trend: GlucoseTrend | null) {
  if (!trend) return null;
  if (!trend.direction) return speed[0];
  const verb = trend.direction === "up" ? "Rising" : "Falling";
  return trend.arrows === 1 ? verb : `${verb} ${speed[trend.arrows]}`;
}

// "−6 mg/dL in 20 min" for the line under a reading.
export function trendChange(trend: GlucoseTrend, unit = "mg/dL") {
  const sign = trend.delta > 0 ? "+" : trend.delta < 0 ? "−" : "±";
  return `${sign}${Math.abs(Math.round(trend.delta))} ${unit} in ${Math.round(trend.minutes)} min`;
}
