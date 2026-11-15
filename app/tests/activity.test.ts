import { expect, it } from "vitest";
import {
  activityName,
  distanceBySport,
  formatDistance,
  sportOf,
  usesMiles,
  workoutDistance,
} from "../src/activity";
import { weeklyWorkouts } from "../src/model";
import type { Workout } from "../src/model";

const workout = (id: string, extra: Partial<Workout>): Workout => ({
  id,
  title: "Recorded activity",
  distanceKm: null,
  durationMinutes: 30,
  startedAt: "2026-10-26T14:00:00Z",
  status: "available",
  provenance: {
    sourceId: "garmin",
    recordId: id,
    observedAt: "2026-10-26T14:00:00Z",
    calendarDate: "2026-10-26",
    timeZone: "Europe/London",
    demo: false,
  },
  ...extra,
});

it("names activities from their source type and keeps titles when no type is known", () => {
  expect(activityName({ activityType: "lap_swimming", title: "x" })).toBe(
    "Pool swim",
  );
  expect(activityName({ activityType: "strength_training", title: "x" })).toBe(
    "Strength",
  );
  expect(activityName({ activityType: "transition_v2", title: "x" })).toBe(
    "Transition",
  );
  expect(
    activityName({ activityType: "stand_up_paddleboarding", title: "x" }),
  ).toBe("Stand up paddleboarding");
  expect(activityName({ activityType: null, title: "Sunday long run" })).toBe(
    "Sunday long run",
  );
  expect(sportOf("indoor_cycling")).toBe("bike");
  expect(sportOf("indoor_running")).toBe("run");
  expect(sportOf("open_water_swimming")).toBe("swim");
  expect(sportOf("strength_training")).toBe("other");
});

it("shows swims in the pool's unit, other distances in miles or km, and never a zero distance", () => {
  expect(formatDistance(914.4, "swim", "yard")).toBe("1,000 yd");
  expect(formatDistance(1000, "swim", "meter")).toBe("1,000 m");
  expect(formatDistance(5000, "run", null, true)).toBe("3.11 mi");
  expect(formatDistance(5000, "run", null, false)).toBe("5.00 km");
  expect(formatDistance(40234, "bike", null, true)).toBe("25.0 mi");
  expect(formatDistance(0, "other", null, true)).toBeNull();
  expect(formatDistance(null, "run", null, true)).toBeNull();
  expect(usesMiles("en-US")).toBe(true);
  expect(usesMiles("en-GB")).toBe(false);
  expect(usesMiles("de-DE")).toBe(false);
});

it("counts a multisport event once: legs carry time and distance, the parent counts the session", () => {
  const records = [
    workout("parent", {
      multisport: "parent",
      activityType: "multi_sport",
      durationMinutes: 75,
      distanceMeters: 21000,
    }),
    workout("swim", {
      multisport: "leg",
      activityType: "lap_swimming",
      poolUnit: "meter",
      durationMinutes: 15,
      distanceMeters: 750,
    }),
    workout("t1", {
      multisport: "leg",
      activityType: "transition_v2",
      durationMinutes: 2,
      distanceMeters: 0,
    }),
    workout("bike", {
      multisport: "leg",
      activityType: "indoor_cycling",
      durationMinutes: 38,
      distanceMeters: 0,
    }),
    workout("run", {
      multisport: "leg",
      activityType: "indoor_running",
      durationMinutes: 20,
      distanceMeters: 5000,
    }),
    workout("lift", {
      activityType: "strength_training",
      durationMinutes: 40,
      distanceMeters: 0,
    }),
  ];
  const week = weeklyWorkouts(records, "2026-10-27");
  expect(week.sessions).toBe(2);
  expect(week.minutes).toBe(15 + 2 + 38 + 20 + 40);
  expect(workoutDistance(records[0], true)).toBeNull();
  expect(workoutDistance(records[4], true)).toBe("3.11 mi");
  expect(distanceBySport(week.counted, true)).toEqual([
    "Swim 750 m",
    "Run 3.11 mi",
  ]);
});
