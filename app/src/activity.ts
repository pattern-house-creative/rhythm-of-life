import type { Workout } from "./model.js";

// Garmin activity type keys to readable names. Unknown keys are humanized, never guessed.
const names: Record<string, string> = {
  running: "Run",
  indoor_running: "Treadmill run",
  treadmill_running: "Treadmill run",
  trail_running: "Trail run",
  track_running: "Track run",
  virtual_run: "Virtual run",
  lap_swimming: "Pool swim",
  open_water_swimming: "Open-water swim",
  swimming: "Swim",
  cycling: "Ride",
  road_biking: "Road ride",
  indoor_cycling: "Indoor ride",
  virtual_ride: "Virtual ride",
  mountain_biking: "Mountain bike",
  gravel_cycling: "Gravel ride",
  strength_training: "Strength",
  multi_sport: "Multisport",
  transition_v2: "Transition",
  transition: "Transition",
  walking: "Walk",
  hiking: "Hike",
  yoga: "Yoga",
  indoor_cardio: "Indoor cardio",
  hiit: "HIIT",
  indoor_rowing: "Indoor row",
};
export function activityName(w: Pick<Workout, "activityType" | "title">) {
  const type = w.activityType;
  if (!type) return w.title;
  return (
    names[type] ??
    type
      .replace(/_v\d+$/, "")
      .replace(/_/g, " ")
      .replace(/^./, (c) => c.toUpperCase())
  );
}

export type Sport = "swim" | "bike" | "run" | "other";
export function sportOf(type?: string | null): Sport {
  if (!type) return "other";
  if (/swim/.test(type)) return "swim";
  if (/cycling|biking|ride/.test(type)) return "bike";
  if (/running|(^|_)run$/.test(type)) return "run";
  return "other";
}

// Miles for US-English viewers, kilometres otherwise; swims always use pool units.
export const usesMiles = (
  locale = typeof navigator === "undefined" ? "en-US" : navigator.language,
) => /^en(-US)?$/i.test(locale);

export function formatDistance(
  meters: number | null | undefined,
  sport: Sport,
  poolUnit?: "meter" | "yard" | null,
  miles = usesMiles(),
) {
  // Zero distance (strength, indoor rides without a sensor) is not a distance.
  if (meters == null || !(meters > 0)) return null;
  if (sport === "swim") {
    const yards = poolUnit === "yard";
    return `${Math.round(yards ? meters / 0.9144 : meters).toLocaleString("en-US")} ${yards ? "yd" : "m"}`;
  }
  const n = miles ? meters / 1609.344 : meters / 1000;
  return `${n < 10 ? n.toFixed(2) : n.toFixed(1)} ${miles ? "mi" : "km"}`;
}

export const workoutMeters = (w: Workout) =>
  w.distanceMeters ?? (w.distanceKm != null ? w.distanceKm * 1000 : null);

// A multisport total mixes swim and run metres; its legs show their own distances.
export const workoutDistance = (w: Workout, miles?: boolean) =>
  w.multisport === "parent"
    ? null
    : formatDistance(
        workoutMeters(w),
        sportOf(w.activityType),
        w.poolUnit,
        miles,
      );

// Weekly distance per sport. Mixed pool units in one week are shown in metres.
export function distanceBySport(records: Workout[], miles?: boolean) {
  const totals = new Map<Sport, { meters: number; yardPools: boolean }>();
  for (const w of records) {
    const meters = workoutMeters(w);
    if (!meters || meters <= 0) continue;
    const sport = sportOf(w.activityType);
    const t = totals.get(sport) ?? { meters: 0, yardPools: true };
    t.meters += meters;
    t.yardPools &&= w.poolUnit === "yard";
    totals.set(sport, t);
  }
  const label: Record<Sport, string> = {
    swim: "Swim",
    bike: "Bike",
    run: "Run",
    other: "Other",
  };
  return (["swim", "bike", "run", "other"] as const)
    .filter((s) => totals.has(s))
    .map(
      (s) =>
        `${label[s]} ${formatDistance(totals.get(s)!.meters, s, totals.get(s)!.yardPools ? "yard" : "meter", miles)}`,
    );
}
