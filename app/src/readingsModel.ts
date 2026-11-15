import type { GraphSeries } from "./knowledge";
import { usesMiles } from "./activity";

// What a data star's measurements mean, in plain words, and how each one
// reads as a day: additive measurements (boluses, carbs, steps) add up to a
// daily total (a day's average × its count); rates and levels (basal, glucose,
// heart rate) read as the day's average with its low and high; once-a-day
// values (a night's sleep, a score) read as themselves.

export type Day = GraphSeries["days"][number];
export type Tone = "mint" | "lavender" | "warm" | "teal";

export interface Meaning {
  name: string;
  about: string;
  // How one day reads: its total, or its average.
  daily: "total" | "average";
  // The card's big number: the latest day, the average day over the last 14,
  // or the last 7 days added up (for things that don't happen every day).
  headline: "latest" | "average" | "week";
  section: string;
  tone: Tone;
  // A night's value, dated by the morning it ends.
  night?: boolean;
  // Several readings a day: show each day's low to high.
  band?: boolean;
  // A day's readings, counted ("9 boluses").
  each?: [string, string];
  // A unit to show in place of the engine's ("steps" for a count).
  unit?: string;
  // A shaded target range on the chart.
  target?: [number, number];
  // One reading per workout: drawn as separate days, never joined up.
  apart?: boolean;
}

const insulin = "Insulin",
  food = "Food",
  glucose = "Glucose",
  sleep = "Sleep",
  heart = "Heart",
  energy = "Energy and stress",
  breathing = "Breathing",
  readiness = "Readiness",
  workouts = "Workouts",
  movement = "Movement";

const total = (
  name: string,
  about: string,
  section: string,
  tone: Tone,
  extra: Partial<Meaning> = {},
): Meaning => ({
  name,
  about,
  daily: "total",
  headline: "average",
  section,
  tone,
  ...extra,
});
const level = (
  name: string,
  about: string,
  section: string,
  tone: Tone,
  extra: Partial<Meaning> = {},
): Meaning => ({
  name,
  about,
  daily: "average",
  headline: "average",
  section,
  tone,
  ...extra,
});
const nightly = (
  name: string,
  about: string,
  section: string,
  tone: Tone,
  extra: Partial<Meaning> = {},
): Meaning => ({
  name,
  about,
  daily: "average",
  headline: "latest",
  section,
  tone,
  ...extra,
});
const session = (
  name: string,
  about: string,
  extra: Partial<Meaning> = {},
): Meaning => ({
  name,
  about,
  daily: "total",
  headline: "week",
  section: workouts,
  tone: "mint",
  each: ["workout", "workouts"],
  apart: true,
  ...extra,
});

// Known measurements, in the order they read best. New ones the engine adds
// fall back to a meaning guessed from their unit.
export const catalogue: Record<string, Meaning> = {
  sensor_glucose: level(
    "Sensor glucose",
    "Your CGM's reading every five minutes. Each day shows its low, average and high.",
    glucose,
    "mint",
    { band: true, target: [70, 180], each: ["reading", "readings"] },
  ),
  // The ingest contract's name for any CGM's readings.
  glucose: level(
    "Glucose",
    "Your CGM's readings. Each day shows its low, average and high.",
    glucose,
    "mint",
    { band: true, target: [70, 180], each: ["reading", "readings"] },
  ),
  blood_glucose: level(
    "Fingerstick checks",
    "Meter readings entered on the pump, to calibrate or confirm the sensor.",
    glucose,
    "teal",
    { target: [70, 180], each: ["check", "checks"] },
  ),
  bolus_delivered: total(
    "Bolus insulin",
    "Meal and correction boluses you gave, added up for each day.",
    insulin,
    "mint",
    { each: ["bolus", "boluses"] },
  ),
  auto_insulin_aggregate: total(
    "Auto-correction insulin",
    "Extra insulin SmartGuard gave on its own to bring highs down.",
    insulin,
    "lavender",
  ),
  basal_rate: level(
    "Basal rate",
    "The steady background insulin the pump runs, in units per hour.",
    insulin,
    "teal",
    { band: true, each: ["rate change", "rate changes"] },
  ),
  carbohydrate_input: total(
    "Carbs entered",
    "Carbs you told the pump about when bolusing, added up for each day.",
    food,
    "warm",
    { each: ["entry", "entries"] },
  ),
  sleep_score: nightly(
    "Sleep score",
    "Garmin's 0–100 for the night: 90+ excellent, 80s good, 60–79 fair.",
    sleep,
    "lavender",
    { night: true },
  ),
  sleep_duration: nightly(
    "Sleep",
    "Time asleep, from falling asleep to waking up.",
    sleep,
    "lavender",
    { night: true },
  ),
  sleep_deep: nightly(
    "Deep sleep",
    "The most restorative stage, when the body repairs itself.",
    sleep,
    "lavender",
    { night: true },
  ),
  sleep_rem: nightly(
    "REM sleep",
    "Dreaming sleep, tied to memory and mood.",
    sleep,
    "lavender",
    { night: true },
  ),
  sleep_light: nightly(
    "Light sleep",
    "Most of the night: the in-between stage.",
    sleep,
    "lavender",
    { night: true },
  ),
  sleep_awake: nightly(
    "Awake time",
    "Time awake after first falling asleep.",
    sleep,
    "lavender",
    { night: true },
  ),
  sleep_unclassified: nightly(
    "Unclassified sleep",
    "Sleep the watch couldn't place in a stage.",
    sleep,
    "lavender",
    { night: true },
  ),
  sleep_stress: nightly(
    "Stress while asleep",
    "Average stress through the night. Lower is calmer.",
    sleep,
    "lavender",
    { night: true },
  ),
  nap_duration: total("Naps", "Daytime sleep, added up.", sleep, "lavender", {
    headline: "latest",
  }),
  resting_heart_rate: nightly(
    "Resting heart rate",
    "Your lowest steady heart rate of the day. Lower than usual means well rested.",
    heart,
    "teal",
  ),
  hrv_overnight: nightly(
    "Overnight HRV",
    "Heart rate variability while asleep. Higher than your usual means recovered.",
    heart,
    "lavender",
    { night: true },
  ),
  hrv_weekly: nightly(
    "7-day HRV",
    "Your rolling weekly HRV average, which Garmin compares with your baseline.",
    heart,
    "lavender",
  ),
  hrv_baseline_low: nightly(
    "HRV baseline, low end",
    "The bottom of your usual HRV range.",
    heart,
    "lavender",
  ),
  hrv_baseline_high: nightly(
    "HRV baseline, high end",
    "The top of your usual HRV range.",
    heart,
    "lavender",
  ),
  hrv_five_min_high: nightly(
    "Highest 5-minute HRV",
    "The best five minutes of HRV in the night.",
    heart,
    "lavender",
    { night: true },
  ),
  hrv: level(
    "HRV",
    "Heart rate variability through the day.",
    heart,
    "lavender",
    { band: true },
  ),
  training_readiness: nightly(
    "Training readiness",
    "How ready you are for hard training today: 75+ high, 50–74 moderate.",
    readiness,
    "mint",
  ),
  training_status: nightly(
    "Training status",
    "Garmin's read of how your training is going.",
    readiness,
    "mint",
  ),
  training_load_acute: nightly(
    "Acute training load",
    "Training load from the last 7 days, as Garmin weighs it.",
    readiness,
    "mint",
  ),
  vo2_max: nightly(
    "VO2 max",
    "Garmin's estimate of your aerobic fitness.",
    readiness,
    "mint",
  ),
  body_battery: level(
    "Body Battery",
    "Garmin's 0–100 energy estimate through the day.",
    energy,
    "mint",
    { band: true },
  ),
  body_battery_charged: nightly(
    "Body Battery charged",
    "Energy regained, mostly while asleep.",
    energy,
    "mint",
  ),
  body_battery_drained: nightly(
    "Body Battery drained",
    "Energy spent through the day.",
    energy,
    "warm",
  ),
  body_battery_max: nightly(
    "Body Battery high",
    "The day's highest energy level, usually on waking.",
    energy,
    "mint",
  ),
  body_battery_min: nightly(
    "Body Battery low",
    "The day's lowest energy level, usually at bedtime.",
    energy,
    "warm",
  ),
  stress_mean: nightly(
    "Average stress",
    "Garmin's 0–100 stress through the day. Under 25 is rest.",
    energy,
    "warm",
  ),
  stress_max: nightly(
    "Peak stress",
    "The day's highest stress level.",
    energy,
    "warm",
  ),
  respiration_awake: nightly(
    "Breathing rate",
    "Breaths per minute while awake.",
    breathing,
    "teal",
  ),
  spo2_mean: nightly(
    "Blood oxygen",
    "Average SpO2 from the wrist sensor.",
    breathing,
    "teal",
  ),
  activity_duration: session(
    "Training time",
    "Time in recorded workouts, added up.",
  ),
  activity_distance: session(
    "Workout distance",
    "Distance covered in workouts, added up.",
  ),
  activity_training_load: session(
    "Training load",
    "How much each workout asked of your body, added up.",
    { tone: "lavender" },
  ),
  activity_calories: session(
    "Workout calories",
    "Energy burned in workouts, added up.",
    { tone: "warm" },
  ),
  activity_heart_rate: level(
    "Workout heart rate",
    "Average heart rate during a workout.",
    workouts,
    "teal",
    { each: ["workout", "workouts"], apart: true },
  ),
  activity_heart_rate_max: level(
    "Peak workout heart rate",
    "The highest heart rate reached in a workout.",
    workouts,
    "teal",
    { each: ["workout", "workouts"], apart: true },
  ),
  training_effect_aerobic: level(
    "Aerobic effect",
    "0–5 per workout: 2 maintains, 3 improves, 4 highly improves endurance.",
    workouts,
    "mint",
    { each: ["workout", "workouts"], apart: true },
  ),
  training_effect_anaerobic: level(
    "Anaerobic effect",
    "0–5 per workout, for speed and power.",
    workouts,
    "lavender",
    { each: ["workout", "workouts"], apart: true },
  ),
  strength_sets: session("Strength sets", "Sets in strength workouts.", {
    unit: "sets",
  }),
  strength_repetitions: session(
    "Strength reps",
    "Repetitions in strength workouts.",
    { unit: "reps" },
  ),
  steps: total("Steps", "Every step across the day.", movement, "mint", {
    unit: "steps",
  }),
  daily_distance: total(
    "Distance",
    "Everything you walked, ran and rode in a day.",
    movement,
    "mint",
  ),
  intensity_moderate: total(
    "Moderate minutes",
    "Brisk activity. Garmin aims for 150 a week, with vigorous minutes counting double.",
    movement,
    "teal",
  ),
  intensity_vigorous: total(
    "Vigorous minutes",
    "Hard efforts. Each counts double toward the weekly 150.",
    movement,
    "mint",
  ),
  calories_active: total(
    "Active calories",
    "Energy burned by moving, above resting.",
    movement,
    "warm",
  ),
  calories_total: total(
    "Total calories",
    "All energy burned in a day, resting included.",
    movement,
    "warm",
  ),
  heart_rate: level(
    "Heart rate",
    "All-day heart rate. Each day shows its low, average and high.",
    heart,
    "teal",
    { band: true },
  ),
  heart_rate_min: nightly(
    "Lowest heart rate",
    "The day's lowest heart rate.",
    heart,
    "teal",
  ),
  heart_rate_max: nightly(
    "Highest heart rate",
    "The day's highest heart rate.",
    heart,
    "teal",
  ),
};
const order = Object.keys(catalogue);
export const more = "More readings";

// Units that add up over a day; everything else is a rate or a level.
const additive = new Set([
  "U",
  "g",
  "kcal",
  "count",
  "m",
  "km",
  "min",
  "steps",
]);
const median = (values: number[]) => {
  const sorted = [...values].sort((a, b) => a - b);
  return sorted.length ? sorted[Math.floor(sorted.length / 2)] : 0;
};
// A new measurement gets a sensible reading from its unit and how often it
// is recorded, under its engine label.
export function meaningOf(s: GraphSeries): Meaning {
  const known = catalogue[s.metric];
  if (known) return known;
  const perDay = median(s.days.map((d) => d.count));
  const adds = additive.has(s.unit) || (s.unit === "s" && perDay > 1);
  return {
    name: s.label || s.metric,
    about: adds
      ? "Added up for each day."
      : perDay > 1
        ? "Each day shows its low, average and high."
        : "One reading a day.",
    daily: adds ? "total" : "average",
    headline: perDay <= 1 && !adds ? "latest" : "average",
    section: more,
    tone: "teal",
    band: !adds && perDay > 1,
  };
}

// Known measurements first, in the catalogue's order; new ones after.
export function ordered(series: GraphSeries[]) {
  const rank = (s: GraphSeries) => {
    const i = order.indexOf(s.metric);
    return i < 0 ? order.length : i;
  };
  return series
    .map((s, i) => ({ s, i }))
    .sort((a, b) => rank(a.s) - rank(b.s) || a.i - b.i)
    .map(({ s }) => s);
}

export const dailyValue = (day: Day, m: Pick<Meaning, "daily">) =>
  m.daily === "total" ? day.mean * day.count : day.mean;

// --- Numbers in words -----------------------------------------------------

export interface Shown {
  value: string;
  unit: string;
}
const fixed = (value: number, digits: number) =>
  Number(value.toFixed(digits)).toLocaleString("en-US", {
    minimumFractionDigits: digits,
    maximumFractionDigits: digits,
  });
const auto = (value: number) => {
  const size = Math.abs(value);
  return Number(
    value.toFixed(size >= 100 ? 0 : size >= 10 ? 1 : 2),
  ).toLocaleString("en-US");
};
// Durations in seconds read as hours and minutes (as the sleep tile does).
export function duration(seconds: number) {
  const minutes = Math.round(seconds / 60);
  return minutes >= 60
    ? `${Math.floor(minutes / 60)}h ${String(minutes % 60).padStart(2, "0")}m`
    : `${minutes} min`;
}
export function show(
  value: number,
  unit: string,
  m?: Pick<Meaning, "unit">,
  miles = usesMiles(),
): Shown {
  switch (unit) {
    case "s":
      return { value: duration(value), unit: "" };
    case "m":
      return miles
        ? {
            value: fixed(value / 1609.344, value < 16093 ? 2 : 1),
            unit: "mi",
          }
        : { value: fixed(value / 1000, value < 10000 ? 2 : 1), unit: "km" };
    case "count":
      return {
        value: Math.round(value).toLocaleString("en-US"),
        unit: m?.unit ?? "",
      };
    case "score":
      return {
        value: Math.abs(value) < 10 ? fixed(value, 1) : fixed(value, 0),
        unit: "",
      };
    case "U":
      return { value: fixed(value, 1), unit: "U" };
    case "U/h":
      return { value: fixed(value, 2), unit: "U/h" };
    case "g":
    case "mg/dL":
    case "ms":
    case "kcal":
    case "min":
      return { value: fixed(value, 0), unit };
    case "%":
      return { value: fixed(value, 0), unit: "%" };
    case "beats/min":
      return { value: fixed(value, 0), unit: "bpm" };
    case "breaths/min":
      return { value: fixed(value, 1), unit: "brpm" };
    default:
      return { value: auto(value), unit: m?.unit ?? unit };
  }
}
export const text = ({ value, unit }: Shown) =>
  unit === "%" ? `${value}%` : unit ? `${value} ${unit}` : value;
export const say = (value: number, unit: string, m?: Pick<Meaning, "unit">) =>
  text(show(value, unit, m));

// --- Calendar days ---------------------------------------------------------

const parse = (date: string) => {
  const [y, mo, d] = date.split("-").map(Number);
  return new Date(y, mo - 1, d);
};
const iso = (d: Date) =>
  `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
export const shift = (date: string, days: number) => {
  const d = parse(date);
  d.setDate(d.getDate() + days);
  return iso(d);
};
// The n calendar days ending at (and including) the given day.
export const calendar = (end: string, n: number) =>
  Array.from({ length: n }, (_, i) => shift(end, i - n + 1));
export const lastDate = (series: GraphSeries[]) =>
  series
    .flatMap((s) => s.days.map((d) => d.date))
    .sort()
    .at(-1) ?? null;
export const firstDate = (series: GraphSeries[]) =>
  series
    .flatMap((s) => s.days.map((d) => d.date))
    .sort()
    .at(0) ?? null;
export const spanDays = (from: string, to: string) =>
  Math.round((parse(to).getTime() - parse(from).getTime()) / 86_400_000) + 1;
export const longDate = (date: string) =>
  parse(date).toLocaleDateString("en-US", {
    weekday: "short",
    month: "short",
    day: "numeric",
  });
export const shortDate = (date: string) =>
  parse(date).toLocaleDateString("en-US", { month: "short", day: "numeric" });
export const weekday = (date: string) =>
  parse(date).toLocaleDateString("en-US", { weekday: "narrow" });
// "today", "yesterday" or the date. A night is dated by the morning it
// ends, so only today's is "last night".
export function dayName(date: string, night = false, today = iso(new Date())) {
  if (date === today) return night ? "last night" : "today";
  if (date === shift(today, -1) && !night) return "yesterday";
  return shortDate(date);
}

// --- One measurement over a window of days --------------------------------

export const byDate = (s: GraphSeries | null | undefined) =>
  new Map((s?.days ?? []).map((d) => [d.date, d]));
const avg = (values: number[]) =>
  values.length ? values.reduce((a, b) => a + b, 0) / values.length : null;
const sum = (values: number[]) =>
  values.length ? values.reduce((a, b) => a + b, 0) : null;

// A day's value of one measurement, or null when it has no reading that day.
export function on(
  s: GraphSeries | null | undefined,
  date: string,
  m?: Pick<Meaning, "daily">,
) {
  const day = s?.days.find((d) => d.date === date);
  return day ? dailyValue(day, m ?? meaningOf(s!)) : null;
}
export function values(
  s: GraphSeries | null | undefined,
  dates: string[],
  m?: Pick<Meaning, "daily">,
) {
  if (!s) return [];
  const days = byDate(s),
    meaning = m ?? meaningOf(s);
  return dates
    .map((date) => days.get(date))
    .filter((d): d is Day => !!d)
    .map((d) => dailyValue(d, meaning));
}
export const averageOver = (
  s: GraphSeries | null | undefined,
  dates: string[],
  m?: Pick<Meaning, "daily">,
) => avg(values(s, dates, m));
export const totalOver = (
  s: GraphSeries | null | undefined,
  dates: string[],
  m?: Pick<Meaning, "daily">,
) => sum(values(s, dates, m));

export interface Headline {
  value: number | null;
  // What the number is, in a word or two: "per day · 14-day average".
  caption: string;
  // A second, smaller line: the other way of reading it.
  aside: string;
}
export function headline(
  s: GraphSeries,
  m: Meaning,
  end: string,
  today?: string,
): Headline {
  const days = [...s.days]
    .filter((d) => d.date <= end)
    .sort((a, b) => a.date.localeCompare(b.date));
  const last = days.at(-1);
  if (!last) return { value: null, caption: "no recent reading", aside: "" };
  const fortnight = calendar(end, 14);
  const lastValue = dailyValue(last, m);
  const lastName = dayName(last.date, m.night, today);
  const twoWeeks = averageOver(s, fortnight, m);
  if (m.headline === "week") {
    const week = calendar(end, 7);
    const before = calendar(shift(end, -7), 7);
    const count = days
      .filter((d) => week.includes(d.date))
      .reduce((n, d) => n + d.count, 0);
    const previous = totalOver(s, before, m);
    return {
      value: totalOver(s, week, m) ?? 0,
      caption: "last 7 days",
      aside: [
        m.each && `${count} ${count === 1 ? m.each[0] : m.each[1]}`,
        previous != null && `${say(previous, s.unit, m)} the week before`,
      ]
        .filter(Boolean)
        .join(" · "),
    };
  }
  if (m.headline === "average" && twoWeeks != null) {
    const covered = values(s, fortnight, m).length;
    return {
      value: twoWeeks,
      caption:
        m.daily === "total"
          ? `per day · ${covered}-day average`
          : `average · ${covered} ${covered === 1 ? "day" : "days"}`,
      aside:
        m.daily === "total"
          ? `${capital(lastName)} ${say(lastValue, s.unit, m)}`
          : s.latest
            ? `Latest ${say(s.latest.value, s.unit, m)} · ${s.latest.observedAt ? when(s.latest.observedAt) : dayName(s.latest.date, false, today)}`
            : "",
    };
  }
  const covered = values(s, fortnight, m).length;
  return {
    value: lastValue,
    caption: lastName,
    aside:
      twoWeeks != null && covered > 1
        ? `${covered}-${m.night ? "night" : "day"} average ${say(twoWeeks, s.unit, m)}`
        : "",
  };
}
export const capital = (t: string) => t.charAt(0).toUpperCase() + t.slice(1);
export const when = (isoTime: string) =>
  new Date(isoTime).toLocaleString("en-US", {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit",
  });

// One day under the cursor: its value and the detail behind it.
export function readout(s: GraphSeries, m: Meaning, date: string) {
  const day = s.days.find((d) => d.date === date);
  if (!day)
    return {
      value: null,
      caption: longDate(date),
      aside: "No reading this day",
    };
  const parts: string[] = [];
  if (m.band || (m.daily === "average" && day.count > 1 && day.min !== day.max))
    parts.push(
      `low ${say(day.min, s.unit, m)}`,
      `high ${say(day.max, s.unit, m)}`,
    );
  if (m.each && day.count > 1) parts.unshift(`${day.count} ${m.each[1]}`);
  else if (m.each && day.count === 1 && m.daily === "total")
    parts.unshift(`1 ${m.each[0]}`);
  return {
    value: dailyValue(day, m),
    caption:
      m.daily === "total" ? `${longDate(date)} · day total` : longDate(date),
    aside: parts.join(" · "),
  };
}

// --- A whole star at a glance ----------------------------------------------

export const find = (series: GraphSeries[], name: string) =>
  series.find((s) => s.metric === name) ?? null;

export interface InsulinDay {
  date: string;
  bolus: number | null;
  auto: number | null;
  carbs: number | null;
}
// Insulin and carbs: bolus and auto-correction insulin per day, carbs per
// day, the basal rate underneath, and how the last 14 days compare with the
// 14 before.
export function insulinSummary(series: GraphSeries[], end: string) {
  const bolus = find(series, "bolus_delivered"),
    autoCorrection = find(series, "auto_insulin_aggregate"),
    carbs = find(series, "carbohydrate_input"),
    basal = find(series, "basal_rate");
  const t = { daily: "total" as const };
  const rows = (dates: string[]): InsulinDay[] =>
    dates.map((date) => ({
      date,
      bolus: on(bolus, date, t),
      auto: on(autoCorrection, date, t),
      carbs: on(carbs, date, t),
    }));
  const insulinOf = (d: InsulinDay) =>
    d.bolus == null && d.auto == null ? null : (d.bolus ?? 0) + (d.auto ?? 0);
  const recent = rows(calendar(end, 14)),
    before = rows(calendar(shift(end, -14), 14));
  const mean = (list: (number | null)[]) =>
    avg(list.filter((v): v is number => v != null));
  const insulinPerDay = mean(recent.map(insulinOf));
  const bolusPerDay = mean(recent.map((d) => d.bolus));
  const autoPerDay = mean(recent.map((d) => d.auto));
  const carbsPerDay = mean(recent.map((d) => d.carbs));
  const share =
    bolusPerDay != null && autoPerDay != null && bolusPerDay + autoPerDay > 0
      ? autoPerDay / (bolusPerDay + autoPerDay)
      : null;
  return {
    days: recent,
    insulinPerDay,
    bolusPerDay,
    autoPerDay,
    carbsPerDay,
    // Grams of carbs covered by each unit of bolus, as CareLink shows it.
    carbsPerUnit:
      carbsPerDay != null && bolusPerDay ? carbsPerDay / bolusPerDay : null,
    autoShare: share,
    basal: averageOver(basal, calendar(end, 14), { daily: "average" }),
    previous: mean(before.map(insulinOf)),
    covered: recent.filter((d) => insulinOf(d) != null).length,
  };
}

// Glucose: the average, the latest reading and how many days have data.
export function glucoseSummary(series: GraphSeries[], end: string) {
  const sensor = (find(series, "sensor_glucose") ?? find(series, "glucose"))!;
  const weighted = (dates: string[]) => {
    const days = dates
      .map((date) => byDate(sensor).get(date))
      .filter((d): d is Day => !!d);
    const count = days.reduce((n, d) => n + d.count, 0);
    return count
      ? days.reduce((n, d) => n + d.mean * d.count, 0) / count
      : null;
  };
  const first = firstDate([sensor]) ?? end;
  const span = Math.min(90, spanDays(first, end));
  const window = calendar(end, span);
  const present = new Set(sensor.days.map((d) => d.date));
  return {
    average: weighted(calendar(end, 14)),
    average90: span > 14 ? weighted(window) : null,
    latest: sensor.latest,
    span,
    coverage: window.map((date) => ({ date, covered: present.has(date) })),
    covered: window.filter((date) => present.has(date)).length,
  };
}

const sleepScoreWord = (score: number) =>
  score >= 90
    ? "Excellent"
    : score >= 80
      ? "Good"
      : score >= 60
        ? "Fair"
        : "Poor";
// Sleep: last night against the usual night, with last night's stages.
export function sleepSummary(series: GraphSeries[], end: string) {
  const asleep = find(series, "sleep_duration")!;
  const last = [...asleep.days]
    .filter((d) => d.date <= end)
    .sort((a, b) => a.date.localeCompare(b.date))
    .at(-1);
  const nights = calendar(end, 14);
  const score = last ? on(find(series, "sleep_score"), last.date) : null;
  const stages = (
    [
      ["sleep_deep", "Deep"],
      ["sleep_light", "Light"],
      ["sleep_rem", "REM"],
      ["sleep_awake", "Awake"],
    ] as const
  )
    .map(([metric, name]) => ({
      metric,
      name,
      seconds: last ? on(find(series, metric), last.date) : null,
    }))
    .filter((s): s is typeof s & { seconds: number } => s.seconds != null);
  return {
    night: last?.date ?? null,
    last: last?.mean ?? null,
    average: averageOver(asleep, nights),
    nights: values(asleep, nights).length,
    score,
    scoreWord: score != null ? sleepScoreWord(score) : null,
    stages,
  };
}

// Training: the last 7 days added up, against the 7 before, and a strip of
// the last 14 days.
export function trainingSummary(series: GraphSeries[], end: string) {
  const time = find(series, "activity_duration"),
    distance = find(series, "activity_distance"),
    load = find(series, "activity_training_load");
  const t = { daily: "total" as const };
  const week = calendar(end, 7),
    before = calendar(shift(end, -7), 7);
  const sessions = (dates: string[]) => {
    const anchor = time ?? distance ?? load;
    return (anchor?.days ?? [])
      .filter((d) => dates.includes(d.date))
      .reduce((n, d) => n + d.count, 0);
  };
  const strip = calendar(end, 14).map((date) => ({
    date,
    seconds: on(time, date, t),
    load: on(load, date, t),
  }));
  return {
    time: time ? (totalOver(time, week, t) ?? 0) : null,
    timeBefore: time ? (totalOver(time, before, t) ?? 0) : null,
    distance: distance ? (totalOver(distance, week, t) ?? 0) : null,
    load: load ? (totalOver(load, week, t) ?? 0) : null,
    sessions: sessions(week),
    sessionsBefore: sessions(before),
    strip,
  };
}

// Daily activity: steps and intensity minutes for the last 7 days.
export function dailySummary(series: GraphSeries[], end: string) {
  const t = { daily: "total" as const };
  const week = calendar(end, 7);
  const steps = find(series, "steps");
  const moderate = totalOver(find(series, "intensity_moderate"), week, t);
  const vigorous = totalOver(find(series, "intensity_vigorous"), week, t);
  return {
    steps: steps ? (totalOver(steps, week, t) ?? 0) : null,
    stepsPerDay: averageOver(steps, week, t),
    // Garmin's weekly goal counts vigorous minutes double.
    intensity:
      moderate == null && vigorous == null
        ? null
        : (moderate ?? 0) + 2 * (vigorous ?? 0),
    activeCalories: averageOver(find(series, "calories_active"), week, t),
    distance: totalOver(find(series, "daily_distance"), week, t),
    strip: calendar(end, 14).map((date) => ({
      date,
      steps: on(steps, date, t),
    })),
  };
}

export type StarKind =
  "insulin" | "glucose" | "sleep" | "training" | "daily" | "other";
// Which summary a star gets, from the measurements it has (not its name),
// so a star the engine adds later still gets one.
export function starKind(series: GraphSeries[]): StarKind {
  const has = (...names: string[]) => names.some((n) => find(series, n));
  if (has("bolus_delivered", "auto_insulin_aggregate", "carbohydrate_input"))
    return "insulin";
  if (has("sensor_glucose", "glucose")) return "glucose";
  if (has("sleep_duration")) return "sleep";
  if (has("activity_duration", "activity_distance", "activity_training_load"))
    return "training";
  if (has("steps", "intensity_moderate", "intensity_vigorous")) return "daily";
  return "other";
}

// Three to five round values across a range, for an opened chart's axis;
// durations step in whole quarter hours and hours.
export function niceTicks(lo: number, hi: number, count: number, unit = "") {
  if (!(hi > lo)) return [lo];
  const raw = (hi - lo) / (count - 1);
  const power = 10 ** Math.floor(Math.log10(raw));
  const steps =
    unit === "s"
      ? [300, 900, 1800, 3600, 7200, 10800, 14400, 21600]
      : [1, 2, 2.5, 5, 10].map((f) => f * power);
  const step = steps.find((s) => s >= raw) ?? steps.at(-1)!;
  const out: number[] = [];
  for (let v = Math.ceil(lo / step) * step; v <= hi + 1e-9; v += step)
    out.push(Number(v.toFixed(6)));
  return out;
}
