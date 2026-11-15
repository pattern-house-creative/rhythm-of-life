import { instanceTimeZone } from "../instance";
import type { GraphSeries } from "../knowledge";
import type { Workout } from "../model";
import { sportOf, workoutMeters } from "../activity";
import type { Sport } from "../activity";
import { metric } from "./readings";

// Garmin readings shaped for the sleep, recovery and training panels: nightly
// sleep, Garmin's own band words, overnight glucose and weekly training.

// ----- Calendar days (YYYY-MM-DD), counted at noon UTC so no zone shifts them.
export const addDays = (date: string, days: number) => {
  const d = new Date(`${date}T12:00:00Z`);
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
};
// Monday is 0, as in the weekly workout totals.
export const weekday = (date: string) =>
  (new Date(`${date}T12:00:00Z`).getUTCDay() + 6) % 7;
// Today in the install's time zone, as the glucose panel counts its days.
export const localDay = (now = new Date(), zone = instanceTimeZone()) =>
  now.toLocaleDateString("en-CA", { timeZone: zone });
export const dayName = (
  date: string,
  style: Intl.DateTimeFormatOptions = {
    weekday: "short",
    month: "short",
    day: "numeric",
  },
) =>
  new Date(`${date}T12:00:00Z`).toLocaleDateString(undefined, {
    ...style,
    timeZone: "UTC",
  });

// "7h 24m", or "45m" under an hour.
export function hm(seconds: number | null | undefined) {
  if (seconds == null || !Number.isFinite(seconds)) return "—";
  const minutes = Math.round(seconds / 60);
  const h = Math.floor(minutes / 60);
  return h ? `${h}h ${minutes % 60}m` : `${minutes}m`;
}

// ----- Series by day. Once-a-day metrics keep the day's value; additive ones
// (durations, loads, minutes) total mean × count.
export function byDay(
  series: GraphSeries[] | null | undefined,
  name: string,
  total = false,
) {
  const days = new Map<string, number>();
  for (const d of metric(series, name)?.days ?? [])
    if (Number.isFinite(d.mean) && d.count > 0)
      days.set(d.date, total ? d.mean * d.count : d.mean);
  return days;
}
// The value on a day, or the latest one before it.
export function onOrBefore(days: Map<string, number>, date: string) {
  let best: [string, number] | null = null;
  for (const entry of days)
    if (entry[0] <= date && (!best || entry[0] > best[0])) best = entry;
  return best ? { date: best[0], value: best[1] } : null;
}

// ----- Garmin's band words.
export const sleepBand = (score: number) =>
  score >= 90
    ? "Excellent"
    : score >= 80
      ? "Good"
      : score >= 60
        ? "Fair"
        : "Poor";
export const readinessBand = (score: number) =>
  score >= 95
    ? "Prime"
    : score >= 75
      ? "High"
      : score >= 50
        ? "Moderate"
        : score >= 25
          ? "Low"
          : "Poor";
export const batteryBand = (level: number) =>
  level >= 76
    ? "High"
    : level >= 51
      ? "Medium"
      : level >= 26
        ? "Low"
        : "Very low";
// Training effect, 0–5, in Garmin's current words, on the one-decimal value it shows.
export function effectWord(effect: number) {
  const te = Math.round(effect * 10) / 10;
  return te >= 5
    ? "Overreaching"
    : te >= 4
      ? "Highly improving"
      : te >= 3
        ? "Improving"
        : te >= 2
          ? "Maintaining"
          : te >= 1
            ? "Minor benefit"
            : "No benefit";
}
// Garmin calls a 7-day HRV average inside the baseline band "Balanced".
export function hrvStatus(
  weekly: number | null | undefined,
  low: number | null | undefined,
  high: number | null | undefined,
) {
  if (weekly == null || low == null || high == null || !(high > low))
    return null;
  return weekly < low
    ? "Below baseline"
    : weekly > high
      ? "Above baseline"
      : "Balanced";
}

// ----- Sleep.
export const stages = ["deep", "light", "rem", "awake"] as const;
export type Stage = (typeof stages)[number];
export const stageName: Record<Stage, string> = {
  deep: "Deep",
  light: "Light",
  rem: "REM",
  awake: "Awake",
};
export interface Night {
  date: string;
  // Seconds asleep, as Garmin reports it, and each stage in seconds.
  asleep: number | null;
  stages: Record<Stage, number | null>;
  // Garmin's own share of sleep time per stage, when it reports one.
  percent: Record<Exclude<Stage, "awake">, number | null>;
  score: number | null;
}
export function nights(series: GraphSeries[] | null | undefined): Night[] {
  const duration = byDay(series, "sleep_duration"),
    score = byDay(series, "sleep_score");
  const percent = {
    deep: byDay(series, "sleep_deep_percentage"),
    light: byDay(series, "sleep_light_percentage"),
    rem: byDay(series, "sleep_rem_percentage"),
  };
  const parts = Object.fromEntries(
    stages.map((s) => [s, byDay(series, `sleep_${s}`)]),
  ) as Record<Stage, Map<string, number>>;
  const dates = new Set([
    ...duration.keys(),
    ...stages.flatMap((s) => [...parts[s].keys()]),
    ...score.keys(),
  ]);
  return [...dates].sort().map((date) => {
    const night = Object.fromEntries(
      stages.map((s) => [s, parts[s].get(date) ?? null]),
    ) as Record<Stage, number | null>;
    const slept = [night.deep, night.light, night.rem];
    return {
      date,
      asleep:
        duration.get(date) ??
        (slept.every((v) => v != null)
          ? slept.reduce((a, b) => a! + b!, 0)
          : null),
      stages: night,
      percent: {
        deep: percent.deep.get(date) ?? null,
        light: percent.light.get(date) ?? null,
        rem: percent.rem.get(date) ?? null,
      },
      score: score.get(date) ?? null,
    };
  });
}
// Each stage's share of the night (awake included), when any stage is known.
export function stageShares(night: Night) {
  const total = stages.reduce((sum, s) => sum + (night.stages[s] ?? 0), 0);
  if (!(total > 0)) return null;
  return Object.fromEntries(
    stages.map((s) => [s, (night.stages[s] ?? 0) / total]),
  ) as Record<Stage, number>;
}

// Each sleep stage's percentage of time asleep, as Garmin shows it: its own
// figure when reported, otherwise from the stage times. Awake has none.
export function sleepPercent(night: Night, stage: Exclude<Stage, "awake">) {
  const reported = night.percent[stage];
  if (reported != null) return reported;
  const slept = ["deep", "light", "rem"] as const;
  const total = slept.reduce((t, s) => t + (night.stages[s] ?? 0), 0);
  const value = night.stages[stage];
  return total > 0 && value != null ? (value / total) * 100 : null;
}

// ----- Overnight glucose, 00:00–06:00 in the CGM's own zone.
export interface GlucosePoint {
  at: number;
  value: number;
}
export interface Overnight {
  date: string;
  points: GlucosePoint[];
  start: number;
  end: number;
  inRange: number; // Share of readings 70–180.
  minutesBelow70: number;
  minutesBelow54: number;
  lows: number; // Episodes of 15 minutes or more below 70.
  lowest: number;
  highest: number;
  interval: number;
}
const zoneParts = (zone: string) =>
  new Intl.DateTimeFormat("en-CA", {
    timeZone: zone,
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "numeric",
    minute: "numeric",
    second: "numeric",
    hourCycle: "h23",
  });
// Typical spacing between readings in minutes (5 for a Medtronic sensor).
export function readingInterval(points: GlucosePoint[]) {
  const gaps = points
    .slice(1)
    .map((p, i) => (p.at - points[i].at) / 60000)
    .filter((g) => g > 0)
    .sort((a, b) => a - b);
  const median = gaps[Math.floor(gaps.length / 2)];
  return median ? Math.min(15, Math.max(1, median)) : 5;
}
// Runs of readings below a threshold that last at least 15 minutes.
export function lowEpisodes(
  points: GlucosePoint[],
  interval: number,
  below = 70,
) {
  let episodes = 0,
    run = 0;
  for (const [i, p] of points.entries()) {
    const joined = i > 0 && p.at - points[i - 1].at <= interval * 60000 * 2.5;
    run = p.value < below ? (joined ? run : 0) + 1 : 0;
    if (run * interval >= 15 && (run - 1) * interval < 15) episodes++;
  }
  return episodes;
}
export function overnightGlucose(
  samples: { timestamp: string; value: number }[],
  zone = instanceTimeZone(),
): Overnight | null {
  const format = zoneParts(zone);
  const local = samples
    .map((s) => ({ at: Date.parse(s.timestamp), value: s.value }))
    .filter(
      (p) => Number.isFinite(p.at) && Number.isFinite(p.value) && p.value > 0,
    )
    .map((p) => {
      const parts = Object.fromEntries(
        format.formatToParts(p.at).map((x) => [x.type, x.value]),
      );
      return {
        ...p,
        date: `${parts.year}-${parts.month}-${parts.day}`,
        hour: Number(parts.hour),
        // Time since local midnight.
        since:
          ((Number(parts.hour) * 60 + Number(parts.minute)) * 60 +
            Number(parts.second)) *
            1000 +
          (p.at % 1000),
      };
    })
    .filter((p) => p.hour < 6);
  const date = local
    .map((p) => p.date)
    .sort()
    .at(-1);
  const points = local
    .filter((p) => p.date === date)
    .map(({ at, value }) => ({ at, value }))
    .sort((a, b) => a.at - b.at);
  if (!date || points.length < 3) return null;
  const interval = readingInterval(points);
  const values = points.map((p) => p.value);
  // Midnight to six in the CGM's zone, from the first reading's own offset.
  const first = local
    .filter((p) => p.date === date)
    .sort((a, b) => a.at - b.at)[0];
  const start = first.at - first.since;
  return {
    date,
    points,
    start,
    end: start + 6 * 3600000,
    inRange: values.filter((v) => v >= 70 && v <= 180).length / values.length,
    minutesBelow70: values.filter((v) => v < 70).length * interval,
    minutesBelow54: values.filter((v) => v < 54).length * interval,
    lows: lowEpisodes(points, interval),
    lowest: Math.min(...values),
    highest: Math.max(...values),
    interval,
  };
}
// Readings around one workout: from 15 minutes before to 30 after.
export function workoutGlucose(
  w: Pick<Workout, "startedAt" | "durationMinutes">,
  samples: { timestamp: string; value: number }[],
) {
  const start = Date.parse(w.startedAt);
  if (!Number.isFinite(start) || !w.durationMinutes) return null;
  const end = start + w.durationMinutes * 60000;
  const points = samples
    .map((s) => ({ at: Date.parse(s.timestamp), value: s.value }))
    .filter(
      (p) =>
        Number.isFinite(p.at) &&
        p.value > 0 &&
        p.at >= start - 15 * 60000 &&
        p.at <= end + 30 * 60000,
    )
    .sort((a, b) => a.at - b.at);
  const during = points.filter((p) => p.at >= start && p.at <= end);
  if (during.length < 3) return null;
  return {
    points,
    start,
    end,
    from: during[0].value,
    to: during.at(-1)!.value,
    lowest: Math.min(...during.map((p) => p.value)),
  };
}

// ----- Training.
export const intensityGoal = 150;
export interface IntensityDay {
  date: string;
  moderate: number | null;
  vigorous: number | null;
  // Garmin's weekly count: vigorous minutes count double.
  minutes: number | null;
  future: boolean;
}
export function intensityWeek(
  daily: GraphSeries[] | null | undefined,
  today: string,
) {
  const moderate = byDay(daily, "intensity_moderate", true),
    vigorous = byDay(daily, "intensity_vigorous", true);
  const start = addDays(today, -weekday(today));
  const days: IntensityDay[] = Array.from({ length: 7 }, (_, i) => {
    const date = addDays(start, i);
    const m = moderate.get(date) ?? null,
      v = vigorous.get(date) ?? null;
    return {
      date,
      moderate: m,
      vigorous: v,
      minutes: m == null && v == null ? null : (m ?? 0) + 2 * (v ?? 0),
      future: date > today,
    };
  });
  const sum = (key: "moderate" | "vigorous") =>
    Math.round(days.reduce((total, d) => total + (d[key] ?? 0), 0));
  return {
    start,
    days,
    moderate: sum("moderate"),
    vigorous: sum("vigorous"),
    total: sum("moderate") + 2 * sum("vigorous"),
    known: days.some((d) => d.minutes != null),
  };
}

const dayOf = (w: Workout) =>
  w.provenance.calendarDate ?? w.startedAt.slice(0, 10);
export interface TrainingDay {
  date: string;
  load: number | null;
  minutes: number | null;
  sessions: number;
  sports: Sport[];
  active: boolean;
  future: boolean;
}
// Last week and this week, Monday to Sunday, from the day's activity readings
// and the recorded workouts (which add each session's sport).
export function trainingWeeks(
  training: GraphSeries[] | null | undefined,
  workouts: Workout[],
  today: string,
): TrainingDay[] {
  const load = byDay(training, "activity_training_load", true),
    seconds = byDay(training, "activity_duration", true);
  const sessions = new Map(
    (metric(training, "activity_duration")?.days ?? []).map((d) => [
      d.date,
      d.count,
    ]),
  );
  const start = addDays(today, -weekday(today) - 7);
  return Array.from({ length: 14 }, (_, i) => {
    const date = addDays(start, i);
    const recorded = workouts.filter(
      (w) => w.multisport !== "parent" && dayOf(w) === date,
    );
    const minutes = recorded.reduce((t, w) => t + (w.durationMinutes ?? 0), 0);
    const workoutLoad = recorded.reduce((t, w) => t + (w.trainingLoad ?? 0), 0);
    const byMinutes = new Map<Sport, number>();
    for (const w of recorded) {
      const sport = sportOf(w.activityType);
      byMinutes.set(
        sport,
        (byMinutes.get(sport) ?? 0) + (w.durationMinutes ?? 1),
      );
    }
    const sports = [...byMinutes].sort((a, b) => b[1] - a[1]).map(([s]) => s);
    const day = {
      date,
      load: load.get(date) ?? (workoutLoad > 0 ? workoutLoad : null),
      minutes: seconds.has(date)
        ? seconds.get(date)! / 60
        : recorded.length
          ? minutes
          : null,
      sessions:
        sessions.get(date) ??
        recorded.filter((w) => w.multisport !== "leg").length,
      sports,
      future: date > today,
    };
    return {
      ...day,
      active:
        !day.future &&
        (day.sessions > 0 || (day.minutes ?? 0) > 0 || (day.load ?? 0) > 0),
    };
  });
}
// Days in a row with training, ending today, or yesterday while today is young.
export function streak(days: TrainingDay[], today: string) {
  const past = days.filter((d) => d.date <= today);
  let i = past.length - 1;
  if (past[i] && !past[i].active) i--;
  let count = 0;
  while (i >= 0 && past[i].active) {
    count++;
    i--;
  }
  return count;
}

// This week's workouts by sport. Multisport parents are counted through their legs.
export interface SportWeek {
  sport: Sport;
  sessions: number;
  minutes: number;
  meters: number;
  yards: boolean;
}
export function sportWeek(counted: Workout[]): SportWeek[] {
  const order: Sport[] = ["swim", "bike", "run", "other"];
  return order.map((sport) => {
    const items = counted.filter(
      (w) => w.multisport !== "parent" && sportOf(w.activityType) === sport,
    );
    return {
      sport,
      sessions: items.length,
      minutes: items.reduce((t, w) => t + (w.durationMinutes ?? 0), 0),
      meters: items.reduce((t, w) => t + Math.max(0, workoutMeters(w) ?? 0), 0),
      yards: items.length > 0 && items.every((w) => w.poolUnit === "yard"),
    };
  });
}
// IRONMAN 70.3 legs, for a playful sense of scale.
export const raceLeg: Record<Exclude<Sport, "other">, number> = {
  swim: 1900,
  bike: 90000,
  run: 21097,
};
