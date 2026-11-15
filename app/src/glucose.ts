import { instanceTimeZone } from "./instance";
import type { GlucoseAggregate, GlucoseHistory } from "./model";
const dayMs = 86400000;
// International consensus: report CGM metrics from >=70% of expected readings;
// GMI additionally needs at least 14 days of data.
const minCoverage = 70,
  gmiDays = 14;
export const gmi = (mean: number) => 3.31 + 0.02392 * mean;

// Fixed-cadence CGM only. A missing slot stays unknown; never carry a value across a gap.
// The adapter must resolve timezones and select ONE source stream before calling this.
// Readings land in the cadence slot containing them: real sensor timestamps are not
// aligned to an arbitrary window start. Bounded readings (beyond sensor range) count
// toward below/above range but never toward the mean.
export function summarizeGlucose(history: GlucoseHistory) {
  const start = Date.parse(history.start),
    end = Date.parse(history.end);
  const interval = history.intervalMinutes * 60000,
    duration = end - start;
  const empty = {
    coverage: 0,
    days: 0,
    readings: 0,
    bounded: 0,
    mean: null,
    gmi: null,
    range: null,
    bands: null,
    tight: null,
    sd: null,
    cv: null,
    gri: null,
  };
  if (
    history.unit !== "mg/dL" ||
    history.timestampBasis !== "verified" ||
    !history.complete ||
    !Number.isFinite(duration) ||
    duration <= 0 ||
    !Number.isFinite(interval) ||
    interval <= 0 ||
    interval > 15 * 60000 ||
    duration % interval !== 0 ||
    duration / interval > 106000
  ) {
    return { ...empty, reason: "Verified, complete CGM history required" };
  }
  type Reading = { value: number; comparison?: "<" | ">" };
  const byTime = new Map<number, Reading | null>();
  for (const sample of history.samples) {
    const time = Date.parse(sample.timestamp);
    if (!Number.isFinite(time) || time < start || time >= end) continue;
    if (!Number.isFinite(sample.value) || sample.value <= 0) continue;
    const reading = { value: sample.value, comparison: sample.comparison };
    const prior = byTime.get(time);
    if (prior === undefined) byTime.set(time, reading);
    else if (
      prior &&
      (prior.value !== reading.value || prior.comparison !== reading.comparison)
    )
      byTime.set(time, null); // Conflicting duplicates are unknown.
  }
  const slots = new Set<number>();
  const readings: Reading[] = [];
  for (const [time, reading] of byTime) {
    if (!reading) continue;
    slots.add(Math.floor((time - start) / interval));
    readings.push(reading);
  }
  const exact = readings.filter((r) => !r.comparison).map((r) => r.value);
  const coverage = Math.min(100, ((slots.size * interval) / duration) * 100);
  const days = duration / dayMs;
  const counts = {
    readings: exact.length,
    bounded: readings.length - exact.length,
  };
  if (coverage < minCoverage || !readings.length) {
    return {
      ...empty,
      ...counts,
      days,
      coverage,
      reason: `Needs at least ${minCoverage}% CGM coverage`,
    };
  }
  const mean = exact.length
    ? exact.reduce((sum, v) => sum + v, 0) / exact.length
    : null;
  const share = (test: (r: Reading) => boolean) =>
    (readings.filter(test).length / readings.length) * 100;
  const below = share(
    (r) => r.comparison === "<" || (!r.comparison && r.value < 70),
  );
  const above = share(
    (r) => r.comparison === ">" || (!r.comparison && r.value > 180),
  );
  const veryLow = share(
    (r) => r.comparison === "<" || (!r.comparison && r.value < 54),
  );
  const veryHigh = share(
    (r) => r.comparison === ">" || (!r.comparison && r.value > 250),
  );
  const bands = {
    veryLow,
    low: below - veryLow,
    inRange: 100 - below - above,
    high: above - veryHigh,
    veryHigh,
  };
  // Time in tight range, 70–140 mg/dL (AGP report v5).
  const tight = share((r) => !r.comparison && r.value >= 70 && r.value <= 140);
  const sd =
    mean !== null && exact.length > 1
      ? Math.sqrt(
          exact.reduce((sum, v) => sum + (v - mean) ** 2, 0) /
            (exact.length - 1),
        )
      : null;
  const longEnough = days >= gmiDays;
  return {
    ...counts,
    days,
    coverage,
    mean,
    gmi: longEnough && mean !== null ? gmi(mean) : null,
    range: { below, within: 100 - below - above, above },
    bands,
    tight,
    sd,
    cv: sd !== null && mean ? (sd / mean) * 100 : null,
    gri: glycemiaRisk(bands),
    reason: longEnough ? null : `GMI needs ${gmiDays} days of CGM data`,
  };
}

export type Bands = {
  veryLow: number;
  low: number;
  inRange: number;
  high: number;
  veryHigh: number;
};
// Glycemia Risk Index (Klonoff 2023): 0 best, 100 worst, zones A to E.
export function glycemiaRisk(bands: Bands) {
  const value = Math.min(
    100,
    3.0 * bands.veryLow +
      2.4 * bands.low +
      1.6 * bands.veryHigh +
      0.8 * bands.high,
  );
  const zone = (["A", "B", "C", "D", "E"] as const)[
    Math.min(4, Math.max(0, Math.ceil(value / 20) - 1))
  ];
  return { value, zone };
}

// About how long each day a share of recorded time comes to.
export const minutesPerDay = (percent: number) => (percent / 100) * 1440;

// Totals the source computed over a window of whole calendar days (normally 14).
export function summarizeAggregate(summary: GlucoseAggregate) {
  const days =
    (Date.parse(`${summary.endDate}T00:00:00Z`) -
      Date.parse(`${summary.startDate}T00:00:00Z`)) /
      dayMs +
    1;
  const expected = (days * dayMs) / (summary.intervalMinutes * 60000);
  const coverage =
    expected > 0 ? Math.min(100, (summary.readings / expected) * 100) : 0;
  const base = { days, coverage, readings: summary.readings };
  const total = Object.values(summary.bands).reduce((sum, n) => sum + n, 0);
  if (coverage < minCoverage || days < gmiDays || !total) {
    return {
      ...base,
      mean: null,
      gmi: null,
      range: null,
      bands: null,
      gri: null,
      reason: `GMI and ${gmiDays}-day time in range need ${gmiDays} days with at least ${minCoverage}% CGM coverage`,
    };
  }
  const pct = (n: number) => (n / total) * 100;
  const bands = {
    veryLow: pct(summary.bands.veryLow),
    low: pct(summary.bands.low),
    inRange: pct(summary.bands.inRange),
    high: pct(summary.bands.high),
    veryHigh: pct(summary.bands.veryHigh),
  };
  const mean = summary.exactReadings
    ? summary.exactSum / summary.exactReadings
    : null;
  return {
    ...base,
    mean,
    gmi: mean !== null ? gmi(mean) : null,
    range: {
      below: bands.veryLow + bands.low,
      within: bands.inRange,
      above: bands.high + bands.veryHigh,
    },
    bands,
    gri: glycemiaRisk(bands),
    reason: null,
  };
}

export function historyDates(history: { start: string; end: string }) {
  const format = (iso: string) =>
    new Date(iso).toLocaleDateString("en-US", {
      month: "short",
      day: "numeric",
      timeZone: instanceTimeZone(),
    });
  return `${format(history.start)}–${format(history.end)}, ${new Date(history.end).getUTCFullYear()}`;
}

// Calendar-day range ("Oct 13–Oct 26"), without shifting days through a time zone.
export function dayRange(startDate: string, endDate: string) {
  const format = (day: string) =>
    new Date(`${day}T12:00:00Z`).toLocaleDateString("en-US", {
      month: "short",
      day: "numeric",
      timeZone: "UTC",
    });
  return `${format(startDate)}–${format(endDate)}`;
}

// Overnight and daytime follow the install's time zone (rhythm.yaml), as the 24-hour chart does.
export const homeTimeZone = () => instanceTimeZone();
const partsFormat = new Map<string, Intl.DateTimeFormat>();
// Local calendar day and minute of the day for an instant.
export function localTime(time: number, timeZone = homeTimeZone()) {
  let format = partsFormat.get(timeZone);
  if (!format) {
    format = new Intl.DateTimeFormat("en-CA", {
      timeZone,
      year: "numeric",
      month: "2-digit",
      day: "2-digit",
      hour: "2-digit",
      minute: "2-digit",
      hourCycle: "h23",
    });
    partsFormat.set(timeZone, format);
  }
  const p = Object.fromEntries(
    format.formatToParts(new Date(time)).map((x) => [x.type, x.value]),
  );
  return {
    date: `${p.year}-${p.month}-${p.day}`,
    minute: Number(p.hour) * 60 + Number(p.minute),
  };
}

type Sample = { timestamp: string; value: number; comparison?: "<" | ">" };
// Valid readings in time order; a time with conflicting readings is dropped.
function ordered(samples: readonly Sample[]) {
  const byTime = new Map<number, Sample | null>();
  for (const s of samples) {
    const time = Date.parse(s.timestamp);
    if (!Number.isFinite(time) || !Number.isFinite(s.value) || s.value <= 0)
      continue;
    const prior = byTime.get(time);
    if (prior === undefined) byTime.set(time, s);
    else if (
      prior &&
      (prior.value !== s.value || prior.comparison !== s.comparison)
    )
      byTime.set(time, null);
  }
  return [...byTime]
    .filter((e): e is [number, Sample] => e[1] !== null)
    .sort((a, b) => a[0] - b[0])
    .map(([time, s]) => ({ time, ...s }));
}

export interface LowEpisode {
  start: string;
  end: string;
  minutes: number;
  nadir: number;
  // Started between 00:00 and 05:59 local time.
  night: boolean;
}
// Hypoglycemia episodes by the 2023 international consensus: one starts after
// at least 15 consecutive minutes below the threshold (70 for level 1, 54 for
// level 2) and ends after at least 15 consecutive minutes at or above it. A gap
// in the readings longer than 15 minutes closes a run.
export function lowEpisodes(
  samples: readonly Sample[],
  {
    threshold = 70,
    intervalMinutes = 5,
    timeZone = homeTimeZone(),
  }: { threshold?: number; intervalMinutes?: number; timeZone?: string } = {},
): LowEpisode[] {
  const span = 15 * 60_000,
    slot = intervalMinutes * 60_000;
  const episodes: LowEpisode[] = [];
  let runStart: number | null = null,
    episodeStart: number | null = null,
    recovery: number | null = null,
    lastBelow = 0,
    nadir = Infinity,
    previous: number | null = null;
  const close = (end: number) => {
    if (episodeStart === null) return;
    episodes.push({
      start: new Date(episodeStart).toISOString(),
      end: new Date(end).toISOString(),
      minutes: Math.round((end - episodeStart) / 60_000),
      nadir,
      night: localTime(episodeStart, timeZone).minute < 360,
    });
    episodeStart = null;
  };
  for (const r of ordered(samples)) {
    if (previous !== null && r.time - previous > span) {
      close(recovery ?? lastBelow + slot);
      runStart = recovery = null;
    }
    previous = r.time;
    const below =
      r.comparison === "<" || (!r.comparison && r.value < threshold);
    if (below) {
      recovery = null;
      lastBelow = r.time;
      if (episodeStart === null) {
        if (runStart === null) {
          runStart = r.time;
          nadir = Infinity;
        }
        nadir = Math.min(nadir, r.value);
        if (r.time - runStart + slot >= span) episodeStart = runStart;
      } else nadir = Math.min(nadir, r.value);
    } else if (episodeStart === null) runStart = null;
    else {
      recovery ??= r.time;
      if (r.time - recovery + slot >= span) {
        close(recovery);
        runStart = recovery = null;
      }
    }
  }
  close(recovery ?? lastBelow + slot);
  return episodes;
}

// An AGP-style typical day: for each quarter hour of the local day, the 5th,
// 25th, 50th, 75th and 95th percentiles of every day's readings, lightly
// smoothed. Needs a well-covered history of at least five days.
export const typicalDayMinDays = 5;
export function typicalDay(
  history: GlucoseHistory,
  { binMinutes = 15, timeZone = homeTimeZone() } = {},
) {
  const summary = summarizeGlucose(history);
  if (!summary.range || summary.days < typicalDayMinDays) return null;
  const count = Math.round(1440 / binMinutes);
  const bins: number[][] = Array.from({ length: count }, () => []);
  for (const r of ordered(history.samples)) {
    // Beyond-range readings keep their order: below 40 or above 400.
    const value =
      r.comparison === "<" ? 39 : r.comparison === ">" ? 401 : r.value;
    const bin = Math.floor(localTime(r.time, timeZone).minute / binMinutes);
    bins[Math.min(count - 1, bin)].push(value);
  }
  const quantile = (sorted: number[], q: number) => {
    const at = (sorted.length - 1) * q,
      low = Math.floor(at),
      high = Math.min(sorted.length - 1, low + 1);
    return sorted[low] + (sorted[high] - sorted[low]) * (at - low);
  };
  const qs = [0.05, 0.25, 0.5, 0.75, 0.95];
  const raw = bins.map((values) => {
    if (values.length < 5) return null;
    const sorted = [...values].sort((a, b) => a - b);
    return qs.map((q) => quantile(sorted, q));
  });
  // A circular three-bin average keeps the bands from looking jagged.
  const points = raw.map((p, i) => {
    if (!p) return null;
    const near = [raw[(i - 1 + count) % count], p, raw[(i + 1) % count]].filter(
      (x): x is number[] => x !== null,
    );
    const avg = qs.map(
      (_, k) => near.reduce((sum, x) => sum + x[k], 0) / near.length,
    );
    return {
      minute: i * binMinutes + binMinutes / 2,
      p5: avg[0],
      p25: avg[1],
      p50: avg[2],
      p75: avg[3],
      p95: avg[4],
    };
  });
  return { points, days: Math.round(summary.days), binMinutes };
}

// The local day with the most time in range, among days with at least 70% of
// their readings. Needs three such days to call one the best.
export function bestDay(
  history: GlucoseHistory,
  { timeZone = homeTimeZone() } = {},
) {
  if (history.unit !== "mg/dL" || history.intervalMinutes <= 0) return null;
  const perDay = 1440 / history.intervalMinutes;
  const days = new Map<
    string,
    { within: number; total: number; sum: number; exact: number }
  >();
  for (const r of ordered(history.samples)) {
    const date = localTime(r.time, timeZone).date;
    const day = days.get(date) ?? { within: 0, total: 0, sum: 0, exact: 0 };
    day.total++;
    if (!r.comparison) {
      day.sum += r.value;
      day.exact++;
      if (r.value >= 70 && r.value <= 180) day.within++;
    }
    days.set(date, day);
  }
  const full = [...days]
    .filter(([, d]) => d.total >= perDay * (minCoverage / 100))
    .map(([date, d]) => ({
      date,
      within: (d.within / d.total) * 100,
      mean: d.exact ? d.sum / d.exact : null,
    }));
  if (full.length < 3) return null;
  const best = full.reduce((a, b) =>
    b.within > a.within || (b.within === a.within && b.date > a.date) ? b : a,
  );
  return { ...best, days: full.length };
}

// GMI over the latest N days of the glucose data star's daily summaries: the
// readings-weighted mean, Σ(mean × count) / Σcount. Coverage compares the
// readings with one every five minutes across all N days.
export function gmiFromDays(
  days: readonly { date: string; mean: number; count: number }[],
  windowDays: number,
  readingsPerDay = 288,
) {
  const valid = days.filter(
    (d) =>
      /^\d{4}-\d{2}-\d{2}$/.test(d.date) &&
      Number.isFinite(d.mean) &&
      d.mean > 0 &&
      Number.isFinite(d.count) &&
      d.count > 0,
  );
  const end = valid
    .map((d) => d.date)
    .sort()
    .at(-1);
  if (!end || windowDays <= 0) return null;
  const startTime = Date.parse(`${end}T00:00:00Z`) - (windowDays - 1) * dayMs;
  const start = new Date(startTime).toISOString().slice(0, 10);
  const inWindow = valid.filter((d) => d.date >= start && d.date <= end);
  const readings = inWindow.reduce((sum, d) => sum + d.count, 0);
  const mean =
    inWindow.reduce((sum, d) => sum + d.mean * d.count, 0) / readings;
  const coverage = Math.min(
    100,
    (readings / (windowDays * readingsPerDay)) * 100,
  );
  return {
    gmi: gmi(mean),
    mean,
    start,
    end,
    windowDays,
    daysCovered: new Set(inWindow.map((d) => d.date)).size,
    readings,
    coverage,
    partial: coverage < minCoverage,
  };
}

// Plain shares of the readings shown in one stretch of the 24-hour chart.
export function readingStats(samples: readonly { value: number }[]) {
  const values = samples
    .map((s) => s.value)
    .filter((v) => Number.isFinite(v) && v > 0);
  if (!values.length) return null;
  const share = (test: (v: number) => boolean) =>
    (values.filter(test).length / values.length) * 100;
  return {
    readings: values.length,
    within: share((v) => v >= 70 && v <= 180),
    tight: share((v) => v >= 70 && v <= 140),
    below: share((v) => v < 70),
    above: share((v) => v > 180),
    mean: values.reduce((sum, v) => sum + v, 0) / values.length,
    min: Math.min(...values),
    max: Math.max(...values),
  };
}
