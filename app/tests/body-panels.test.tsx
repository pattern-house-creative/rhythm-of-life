import { describe, expect, it, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { sampleHealth } from "./sampleHealth";
import { sampleReadings } from "./sampleReadings";
import type { GraphSeries } from "../src/knowledge";
import type { ObservatoryData, Workout } from "../src/model";
import { RecoveryPanel } from "../src/panels/RecoveryPanel";
import { TrainingPanel } from "../src/panels/TrainingPanel";
import { dataStar } from "../src/panels/readings";
import {
  addDays,
  batteryBand,
  effectWord,
  hm,
  hrvStatus,
  intensityWeek,
  localDay,
  lowEpisodes,
  nights,
  overnightGlucose,
  readinessBand,
  sleepBand,
  sleepPercent,
  sportWeek,
  stageShares,
  streak,
  trainingWeeks,
  workoutGlucose,
} from "../src/panels/garmin";

// Synthetic, fictional readings only.
function series(
  metric: string,
  unit: string,
  values: [string, number, number?][],
): GraphSeries {
  const days = values.map(([date, mean, count = 1]) => ({
    date,
    min: mean,
    mean,
    max: mean,
    count,
  }));
  const last = days.at(-1);
  return {
    metric,
    label: metric,
    unit,
    latest: last
      ? { value: last.mean, observedAt: null, date: last.date }
      : null,
    days,
  };
}
const daysBack = (today: string, n: number) =>
  Array.from({ length: n }, (_, i) => addDays(today, i - n + 1));
const workout = (over: Partial<Workout> & { id: string }): Workout => ({
  title: "Recorded activity",
  distanceKm: null,
  durationMinutes: 45,
  startedAt: "2026-10-26T12:00:00Z",
  provenance: {
    sourceId: "garmin",
    recordId: `test:${over.id}`,
    observedAt: "2026-10-26T13:00:00Z",
    calendarDate: "2026-10-26",
    demo: true,
  },
  ...over,
});
// A local time in the install's time zone: UTC with the demo settings the tests run under.
const local = (date: string, hour: number, minute = 0) =>
  new Date(
    Date.UTC(
      Number(date.slice(0, 4)),
      Number(date.slice(5, 7)) - 1,
      Number(date.slice(8, 10)),
      hour,
      minute,
    ),
  ).toISOString();

describe("Garmin words and numbers", () => {
  it("uses Garmin's own bands", () => {
    expect([95, 90, 89, 80, 79, 60, 59].map(sleepBand)).toEqual([
      "Excellent",
      "Excellent",
      "Good",
      "Good",
      "Fair",
      "Fair",
      "Poor",
    ]);
    expect([100, 95, 94, 75, 74, 50, 49, 25, 24, 1].map(readinessBand)).toEqual(
      [
        "Prime",
        "Prime",
        "High",
        "High",
        "Moderate",
        "Moderate",
        "Low",
        "Low",
        "Poor",
        "Poor",
      ],
    );
    expect([100, 76, 75, 51, 50, 26, 25, 5].map(batteryBand)).toEqual([
      "High",
      "High",
      "Medium",
      "Medium",
      "Low",
      "Low",
      "Very low",
      "Very low",
    ]);
    // Training effect words follow the one-decimal value shown.
    expect([0.4, 1.0, 2.0, 2.96, 3.6, 4.0, 5.0].map(effectWord)).toEqual([
      "No benefit",
      "Minor benefit",
      "Maintaining",
      "Improving",
      "Improving",
      "Highly improving",
      "Overreaching",
    ]);
    expect(hm(26400)).toBe("7h 20m");
    expect(hm(1500)).toBe("25m");
    expect(hm(null)).toBe("—");
  });

  it("calls a 7-day HRV average inside the baseline balanced", () => {
    expect(hrvStatus(57, 49, 66)).toBe("Balanced");
    expect(hrvStatus(45, 49, 66)).toBe("Below baseline");
    expect(hrvStatus(70, 49, 66)).toBe("Above baseline");
    expect(hrvStatus(57, null, 66)).toBeNull();
  });
});

describe("nights", () => {
  it("builds each night from its stages, score and Garmin's percentages", () => {
    const list = nights([
      series("sleep_duration", "s", [["2026-10-27", 25200]]),
      series("sleep_deep", "s", [
        ["2026-10-27", 5400],
        ["2026-10-28", 3600],
      ]),
      series("sleep_light", "s", [
        ["2026-10-27", 14400],
        ["2026-10-28", 14400],
      ]),
      series("sleep_rem", "s", [
        ["2026-10-27", 5400],
        ["2026-10-28", 3600],
      ]),
      series("sleep_awake", "s", [["2026-10-27", 1800]]),
      series("sleep_score", "score", [["2026-10-27", 84]]),
      series("sleep_deep_percentage", "%", [["2026-10-28", 17]]),
    ]);
    expect(list.map((n) => n.date)).toEqual(["2026-10-27", "2026-10-28"]);
    expect(list[0]).toMatchObject({ asleep: 25200, score: 84 });
    // Without a reported duration, time asleep is the sleeping stages.
    expect(list[1]).toMatchObject({ asleep: 21600, score: null });
    // The ring's shares include awake time; the percentages are of time asleep.
    expect(stageShares(list[0])!.awake).toBeCloseTo(1800 / 27000);
    expect(sleepPercent(list[0], "deep")).toBeCloseTo((5400 / 25200) * 100);
    expect(sleepPercent(list[1], "deep")).toBe(17);
    expect(nights(null)).toEqual([]);
  });
});

describe("overnight glucose", () => {
  it("reads midnight to six on the latest night, counting lows of 15 minutes or more", () => {
    const samples = [
      // The night before: ignored.
      { timestamp: local("2026-10-27", 2), value: 40 },
      // Last night every five minutes, with 20 minutes below 70 from 3:00.
      ...Array.from({ length: 72 }, (_, i) => ({
        timestamp: local("2026-10-28", Math.floor(i / 12), (i % 12) * 5),
        value: i >= 36 && i < 40 ? 62 : 120,
      })),
      // After six: not overnight.
      { timestamp: local("2026-10-28", 7), value: 250 },
    ];
    const night = overnightGlucose(samples)!;
    expect(night.date).toBe("2026-10-28");
    expect(night.points).toHaveLength(72);
    expect(night.interval).toBe(5);
    expect(night.minutesBelow70).toBe(20);
    expect(night.lows).toBe(1);
    expect(night.lowest).toBe(62);
    expect(night.highest).toBe(120);
    expect(night.inRange).toBeCloseTo(68 / 72);
    expect(night.end - night.start).toBe(6 * 3600000);
    expect(new Date(night.start).toISOString()).toBe(local("2026-10-28", 0));
    expect(overnightGlucose(samples.slice(0, 3))).toBeNull();
  });

  it("does not count a short dip as a low", () => {
    const at = (m: number, value: number) => ({ at: m * 60000, value });
    expect(lowEpisodes([at(0, 65), at(5, 66), at(10, 90)], 5)).toBe(0);
    expect(lowEpisodes([at(0, 65), at(5, 66), at(10, 64), at(15, 63)], 5)).toBe(
      1,
    );
    // A gap in readings starts a new run.
    expect(lowEpisodes([at(0, 65), at(5, 66), at(60, 64)], 5)).toBe(0);
  });

  it("finds the readings through a workout", () => {
    const start = "2026-10-28T12:00:00Z";
    const samples = Array.from({ length: 30 }, (_, i) => ({
      timestamp: new Date(Date.parse(start) + (i - 6) * 300000).toISOString(),
      value: 160 - i * 2,
    }));
    const during = workoutGlucose(
      { startedAt: start, durationMinutes: 30 },
      samples,
    )!;
    expect(during.from).toBe(148);
    expect(during.to).toBe(136);
    expect(during.points[0].value).toBe(154);
    expect(
      workoutGlucose({ startedAt: start, durationMinutes: 5 }, samples),
    ).toBeNull();
    expect(
      workoutGlucose({ startedAt: start, durationMinutes: null }, samples),
    ).toBeNull();
  });
});

describe("training", () => {
  const today = "2026-10-28"; // A Wednesday.

  it("counts the week's intensity minutes from Monday, vigorous double", () => {
    const week = intensityWeek(
      [
        series("intensity_moderate", "min", [
          ["2026-10-25", 90],
          ["2026-10-26", 20],
          ["2026-10-28", 10],
        ]),
        series("intensity_vigorous", "min", [
          ["2026-10-27", 15],
          ["2026-10-28", 5],
        ]),
      ],
      today,
    );
    expect(week.start).toBe("2026-10-26");
    expect(week.moderate).toBe(30);
    expect(week.vigorous).toBe(20);
    expect(week.total).toBe(70);
    expect(week.days.map((d) => d.minutes)).toEqual([
      20,
      30,
      20,
      null,
      null,
      null,
      null,
    ]);
    expect(week.days.map((d) => d.future)).toEqual([
      false,
      false,
      false,
      true,
      true,
      true,
      true,
    ]);
    expect(intensityWeek(null, today).known).toBe(false);
  });

  it("lays out two weeks of load with each day's sport and a streak", () => {
    const training = [
      series("activity_duration", "s", [
        ["2026-10-24", 1800],
        ["2026-10-26", 2700],
        ["2026-10-27", 1800, 2],
      ]),
      series("activity_training_load", "score", [
        ["2026-10-26", 90],
        ["2026-10-27", 40, 2],
      ]),
    ];
    const days = trainingWeeks(
      training,
      [
        workout({ id: "swim", activityType: "lap_swimming" }),
        workout({
          id: "run",
          activityType: "running",
          durationMinutes: 20,
          startedAt: "2026-10-28T11:00:00Z",
          trainingLoad: 55,
          provenance: {
            sourceId: "garmin",
            recordId: "test:run",
            observedAt: null,
            calendarDate: "2026-10-28",
            demo: true,
          },
        }),
      ],
      today,
    );
    expect(days).toHaveLength(14);
    expect(days[0].date).toBe("2026-10-19");
    expect(days.at(-1)!.date).toBe("2026-11-01");
    const byDate = new Map(days.map((d) => [d.date, d]));
    expect(byDate.get("2026-10-27")).toMatchObject({
      load: 80,
      minutes: 60,
      sessions: 2,
      active: true,
    });
    expect(byDate.get("2026-10-26")!.sports).toEqual(["swim"]);
    // A day the readings haven't reached yet still counts its recorded workout.
    expect(byDate.get("2026-10-28")).toMatchObject({
      load: 55,
      minutes: 20,
      sports: ["run"],
      active: true,
    });
    expect(byDate.get("2026-10-29")).toMatchObject({
      future: true,
      active: false,
    });
    expect(streak(days, today)).toBe(3);
    // Today without training yet keeps yesterday's streak alive.
    expect(streak(trainingWeeks(training, [], today), today)).toBe(2);
  });

  it("totals the week by sport, counting multisport through its legs", () => {
    const week = sportWeek([
      workout({
        id: "swim",
        activityType: "lap_swimming",
        poolUnit: "yard",
        distanceMeters: 1829,
      }),
      workout({
        id: "race",
        activityType: "multi_sport",
        multisport: "parent",
      }),
      workout({
        id: "bike",
        activityType: "road_biking",
        multisport: "leg",
        distanceMeters: 40000,
        durationMinutes: 80,
      }),
      workout({ id: "lift", activityType: "strength_training" }),
    ]);
    expect(week.map((s) => [s.sport, s.sessions])).toEqual([
      ["swim", 1],
      ["bike", 1],
      ["run", 0],
      ["other", 1],
    ]);
    expect(week[0]).toMatchObject({ meters: 1829, yards: true });
    expect(week[1]).toMatchObject({ meters: 40000, minutes: 80 });
  });
});

// The panels read their data stars through readData; these return synthetic
// readings for the last two weeks, ending today.
function recoveryReadings(today: string, full = true) {
  const dates = daysBack(today, 14);
  const each = (v: (i: number) => number) =>
    dates.map((d, i) => [d, v(i)] as [string, number]);
  return [
    series(
      "sleep_duration",
      "s",
      each(() => 27000),
    ),
    series(
      "sleep_deep",
      "s",
      each(() => 5100),
    ),
    series(
      "sleep_light",
      "s",
      each(() => 15000),
    ),
    series(
      "sleep_rem",
      "s",
      each(() => 5400),
    ),
    series(
      "sleep_awake",
      "s",
      each(() => 1500),
    ),
    series(
      "resting_heart_rate",
      "beats/min",
      each((i) => 50 + (i % 3)),
    ),
    series(
      "hrv_overnight",
      "ms",
      each((i) => 55 + (i % 5)),
    ),
    series(
      "hrv_weekly",
      "ms",
      each(() => 57),
    ),
    series(
      "body_battery_charged",
      "score",
      each(() => 60),
    ),
    series(
      "body_battery_min",
      "score",
      each(() => 22),
    ),
    series(
      "body_battery_max",
      "score",
      each(() => 88),
    ),
    series(
      "stress_mean",
      "score",
      each(() => 30),
    ),
    series(
      "spo2_mean",
      "%",
      each(() => 96),
    ),
    ...(full
      ? [
          series(
            "sleep_score",
            "score",
            each((i) => (i === 13 ? 82 : 70)),
          ),
          series(
            "hrv_baseline_low",
            "ms",
            each(() => 49),
          ),
          series(
            "hrv_baseline_high",
            "ms",
            each(() => 66),
          ),
          series(
            "training_readiness",
            "score",
            each(() => 64),
          ),
          series(
            "body_battery_wake",
            "score",
            each(() => 83),
          ),
          series(
            "body_battery_sleep_charge",
            "score",
            each(() => 54),
          ),
        ]
      : []),
  ];
}
const reader = (stars: Record<string, GraphSeries[] | null>) =>
  vi.fn(async (id: string) => stars[id] ?? null);

describe("Sleep & recovery panel", () => {
  it("shows last night's score, stages, morning and heart signals", async () => {
    const today = localDay();
    const readData = reader({
      [dataStar("garmin", "recovery")]: recoveryReadings(today),
    });
    render(
      <RecoveryPanel
        data={sampleHealth}
        selectNode={vi.fn()}
        readData={readData}
      />,
    );
    const hero = await screen.findByRole("region", {
      name: "Last night's sleep",
    });
    expect(await within(hero).findByText("82")).toBeInTheDocument();
    expect(within(hero).getByText("Good")).toBeInTheDocument();
    expect(within(hero).getByText("sleep score")).toBeInTheDocument();
    expect(screen.getByText(/^Last night/)).toBeInTheDocument();
    const stagesList = screen.getByRole("list", { name: "Sleep stages" });
    expect(within(stagesList).getByText("Deep")).toBeInTheDocument();
    expect(within(stagesList).getByText("1h 25m")).toBeInTheDocument();
    expect(within(stagesList).getByText("4h 10m")).toBeInTheDocument();
    expect(screen.getByText("+54")).toBeInTheDocument();
    expect(
      screen.getByText("Body Battery recharged overnight"),
    ).toBeInTheDocument();
    expect(screen.getByText("Balanced")).toBeInTheDocument();
    expect(screen.getByText("TRAINING READINESS")).toBeInTheDocument();
    expect(screen.getByText("Moderate")).toBeInTheDocument();
    expect(screen.getByText("baseline 49–66 ms · shaded")).toBeInTheDocument();
    expect(screen.getByText("avg SpO2")).toBeInTheDocument();
    // No hedging copy, and a single origin line.
    expect(screen.queryByText(/No sleep score/)).not.toBeInTheDocument();
    expect(document.querySelectorAll(".origin")).toHaveLength(1);
  });

  it("brings a tapped night into focus and back", async () => {
    const today = localDay();
    const readData = reader({
      [dataStar("garmin", "recovery")]: recoveryReadings(today),
    });
    const user = userEvent.setup();
    render(
      <RecoveryPanel
        data={sampleHealth}
        selectNode={vi.fn()}
        readData={readData}
      />,
    );
    const chart = await screen.findByRole("group", {
      name: "Sleep, last 14 nights",
    });
    const nightsShown = within(chart).getAllByRole("button");
    expect(nightsShown).toHaveLength(14);
    expect(nightsShown[13]).toHaveAttribute("aria-pressed", "true");
    await user.click(nightsShown[10]);
    expect(nightsShown[10]).toHaveAttribute("aria-pressed", "true");
    expect(screen.queryByText(/^Last night/)).not.toBeInTheDocument();
    expect(screen.getAllByText("70").length).toBeGreaterThan(0);
    await user.click(
      screen.getByRole("button", { name: "Back to last night" }),
    );
    expect(nightsShown[13]).toHaveAttribute("aria-pressed", "true");
  });

  it("leaves out what Garmin doesn't send, with no placeholders", async () => {
    const today = localDay();
    const readData = reader({
      [dataStar("garmin", "recovery")]: recoveryReadings(today, false),
    });
    render(
      <RecoveryPanel
        data={sampleHealth}
        selectNode={vi.fn()}
        readData={readData}
      />,
    );
    // Without a score, the ring holds the time asleep.
    expect(await screen.findByText("7h 30m")).toBeInTheDocument();
    expect(screen.queryByText("sleep score")).not.toBeInTheDocument();
    expect(screen.queryByText("TRAINING READINESS")).not.toBeInTheDocument();
    expect(screen.queryByText(/recharged overnight/)).not.toBeInTheDocument();
    expect(screen.queryByText("Balanced")).not.toBeInTheDocument();
    expect(screen.getByText("7-day average 57 ms")).toBeInTheDocument();
    expect(screen.getByText("+60")).toBeInTheDocument();
  });

  it("matches production in the demo: no readiness gauge", async () => {
    render(
      <RecoveryPanel
        data={sampleHealth}
        selectNode={vi.fn()}
        readData={async (id) => sampleReadings(id)}
      />,
    );
    expect(
      await screen.findByText("Body Battery recharged overnight"),
    ).toBeInTheDocument();
    expect(screen.queryByText("TRAINING READINESS")).not.toBeInTheDocument();
  });

  it("falls back to the summary when the nightly readings are unavailable", async () => {
    const readData = reader({});
    render(
      <RecoveryPanel
        data={sampleHealth}
        selectNode={vi.fn()}
        readData={readData}
      />,
    );
    expect(
      await screen.findByText("Stages weren't recorded for this night."),
    ).toBeInTheDocument();
    expect(screen.getByText("7h 42m")).toBeInTheDocument();
    expect(screen.getByText("54")).toBeInTheDocument();
    expect(screen.getByText("48")).toBeInTheDocument();
  });
});

describe("Training panel", () => {
  it("shows the week's ring, load calendar, sports and workout cards", async () => {
    const today = localDay();
    const week = intensityWeek(null, today);
    const dates = daysBack(today, 14);
    const readData = reader({
      [dataStar("garmin", "daily")]: [
        series("intensity_moderate", "min", [[week.start, 40]]),
        series("intensity_vigorous", "min", [[week.start, 30]]),
        series(
          "steps",
          "count",
          dates.map((d) => [d, 10000] as [string, number]),
        ),
      ],
      [dataStar("garmin", "training")]: [
        series(
          "activity_duration",
          "s",
          dates.slice(-3).map((d) => [d, 3000] as [string, number]),
        ),
        series(
          "activity_training_load",
          "score",
          dates.slice(-3).map((d) => [d, 100] as [string, number]),
        ),
      ],
    });
    // The demo's workouts, made fuller, in its own sample week.
    const start = "2026-10-18T14:00:00Z";
    const data: ObservatoryData = {
      ...sampleHealth,
      glucose: {
        ...sampleHealth.glucose,
        samples: Array.from({ length: 24 }, (_, i) => ({
          hour: 9,
          value: 150 - i * 3,
          timestamp: new Date(Date.parse(start) + i * 300000).toISOString(),
        })),
      },
      workouts: [
        workout({
          id: "run",
          activityType: "running",
          distanceMeters: 10000,
          durationMinutes: 60,
          startedAt: start,
          trainingLoad: 142,
          aerobicEffect: 3.6,
          anaerobicEffect: 1.1,
          averageHeartRate: 148,
          provenance: {
            ...sampleHealth.workouts[0].provenance,
            calendarDate: "2026-10-18",
          },
        }),
        workout({
          id: "swim",
          activityType: "lap_swimming",
          poolUnit: "yard",
          distanceMeters: 2286,
          durationMinutes: 40,
          startedAt: "2026-10-16T12:00:00Z",
          provenance: {
            ...sampleHealth.workouts[0].provenance,
            calendarDate: "2026-10-16",
          },
        }),
      ],
    };
    render(
      <TrainingPanel data={data} selectNode={vi.fn()} readData={readData} />,
    );
    expect(
      await screen.findByRole("img", {
        name: "100 of 150 intensity minutes this week: 40 moderate and 30 vigorous, counted double.",
      }),
    ).toBeInTheDocument();
    expect(screen.getByText("3-day streak")).toBeInTheDocument();
    expect(screen.getByText("Improving")).toBeInTheDocument();
    expect(screen.getByText("Minor benefit")).toBeInTheDocument();
    expect(screen.getByText("2,500 yd")).toBeInTheDocument();
    expect(screen.getByText("1.2× the 70.3 swim")).toBeInTheDocument();
    expect(screen.getByText(/no rides yet/)).toBeInTheDocument();
    expect(
      screen.getByRole("img", { name: /^Glucose during the workout: 150 to/ }),
    ).toBeInTheDocument();
    expect(screen.getByText("steps a day · two weeks")).toBeInTheDocument();
    expect(screen.getByText(/^10,000$/)).toBeInTheDocument();
    expect(document.querySelectorAll(".origin")).toHaveLength(1);
  });

  it("gives the demo a fictional swim, ride and run with Garmin's statistics", async () => {
    render(
      <TrainingPanel
        data={sampleHealth}
        selectNode={vi.fn()}
        readData={async (id) => sampleReadings(id)}
      />,
    );
    expect(await screen.findByText("2,000 yd")).toBeInTheDocument();
    const lanes = document.querySelector<HTMLElement>(".sport-lanes")!;
    for (const sport of ["Swim", "Bike", "Run"])
      expect(within(lanes).getByText(sport)).toBeInTheDocument();
    expect(within(lanes).queryByText("Other")).not.toBeInTheDocument();
    expect(within(lanes).getAllByText(/^1 session/)).toHaveLength(3);
    expect(screen.getByText("Pool swim")).toBeInTheDocument();
    expect(screen.getByText("Road ride")).toBeInTheDocument();
    expect(screen.getByText("Improving")).toBeInTheDocument();
    expect(screen.getAllByText("Maintaining")).toHaveLength(2);
    // The swim's training load. The week's vigorous minutes, counted from today, can come to the same figure, so
    // more than one element may hold it.
    expect(screen.getAllByText("142").length).toBeGreaterThan(0);
  });

  it("stays calm when the activity source is unavailable", async () => {
    render(
      <TrainingPanel
        data={{ ...sampleHealth, workouts: [], workoutsStatus: "unavailable" }}
        selectNode={vi.fn()}
        readData={reader({})}
      />,
    );
    expect(
      await screen.findByText("Activity source unavailable"),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("img", { name: "Intensity minutes unavailable." }),
    ).toBeInTheDocument();
    expect(screen.queryByText("RECENT WORKOUTS")).not.toBeInTheDocument();
  });
});
