import { describe, expect, it } from "vitest";
import type { GlucoseHistory } from "../src/model";
import {
  bestDay,
  glycemiaRisk,
  gmi,
  gmiFromDays,
  localTime,
  lowEpisodes,
  minutesPerDay,
  readingStats,
  summarizeAggregate,
  summarizeGlucose,
  typicalDay,
} from "../src/glucose";
import { perDay, rangeWindow } from "../src/glucoseView";
import { sampleHealth } from "./sampleHealth";

// Fictional, synthetic CGM readings every five minutes from a midnight in the install's
// time zone (UTC with the demo settings the tests run under).
const start = Date.parse("2026-10-01T00:00:00Z");
function history(days: number, value: (i: number) => number): GlucoseHistory {
  return {
    start: new Date(start).toISOString(),
    end: new Date(start + days * 86_400_000).toISOString(),
    intervalMinutes: 5,
    unit: "mg/dL",
    timestampBasis: "verified",
    complete: true,
    sourceKind: "demo",
    provenance: sampleHealth.glucoseHistory!.provenance,
    samples: Array.from({ length: days * 288 }, (_, i) => ({
      timestamp: new Date(start + i * 300_000).toISOString(),
      value: value(i),
    })),
  };
}
const run = (values: number[], at = start) =>
  values.map((value, i) => ({
    value,
    timestamp: new Date(at + i * 300_000).toISOString(),
  }));

describe("five-band time in ranges", () => {
  it("splits recorded time into the consensus bands and tight range", () => {
    // One each of 50, 60, 100, 150, 200 and 300, repeated.
    const values = [50, 60, 100, 150, 200, 300];
    const result = summarizeGlucose(history(14, (i) => values[i % 6]));
    const sixth = 100 / 6;
    expect(result.bands!.veryLow).toBeCloseTo(sixth);
    expect(result.bands!.low).toBeCloseTo(sixth);
    expect(result.bands!.inRange).toBeCloseTo(2 * sixth);
    expect(result.bands!.high).toBeCloseTo(sixth);
    expect(result.bands!.veryHigh).toBeCloseTo(sixth);
    expect(result.tight).toBeCloseTo(sixth);
    expect(Object.values(result.bands!).reduce((a, b) => a + b, 0)).toBeCloseTo(
      100,
    );
  });

  it("puts the band edges where the consensus does", () => {
    const values = [53, 54, 69, 70, 140, 141, 180, 181, 250, 251, 100, 160];
    const result = summarizeGlucose(history(14, (i) => values[i % 12]));
    const twelfth = 100 / 12;
    expect(result.bands!.veryLow).toBeCloseTo(twelfth);
    expect(result.bands!.low).toBeCloseTo(2 * twelfth);
    expect(result.bands!.inRange).toBeCloseTo(6 * twelfth);
    expect(result.bands!.high).toBeCloseTo(2 * twelfth);
    expect(result.bands!.veryHigh).toBeCloseTo(twelfth);
    // 70, 100 and 140.
    expect(result.tight).toBeCloseTo(3 * twelfth);
  });

  it("counts beyond-range readings in the outer bands only", () => {
    const data = history(14, () => 120);
    data.samples[0] = { ...data.samples[0], value: 40, comparison: "<" };
    data.samples[1] = { ...data.samples[1], value: 400, comparison: ">" };
    const result = summarizeGlucose(data);
    expect(result.bands!.veryLow).toBeCloseTo((1 / 4032) * 100, 6);
    expect(result.bands!.veryHigh).toBeCloseTo((1 / 4032) * 100, 6);
    expect(result.mean).toBe(120);
  });

  it("reports variability as the coefficient of variation", () => {
    const steady = summarizeGlucose(history(14, () => 140));
    expect(steady.cv).toBe(0);
    const swings = summarizeGlucose(history(14, (i) => (i % 2 ? 200 : 100)));
    expect(swings.mean).toBe(150);
    // Sample SD of an even 100/200 split is a shade over 50.
    expect(swings.cv).toBeCloseTo((50 * Math.sqrt(4032 / 4031) * 100) / 150, 6);
  });

  it("scores the glycemia risk index with its zones and cap", () => {
    expect(
      glycemiaRisk({ veryLow: 0, low: 0, inRange: 100, high: 0, veryHigh: 0 }),
    ).toEqual({ value: 0, zone: "A" });
    const b = glycemiaRisk({
      veryLow: 1,
      low: 3,
      inRange: 70,
      high: 20,
      veryHigh: 6,
    });
    // 3×1 + 2.4×3 + 1.6×6 + 0.8×20
    expect(b.value).toBeCloseTo(35.8);
    expect(b.zone).toBe("B");
    expect(
      glycemiaRisk({ veryLow: 0, low: 0, inRange: 75, high: 25, veryHigh: 0 })
        .zone,
    ).toBe("A");
    expect(
      glycemiaRisk({ veryLow: 40, low: 20, inRange: 40, high: 0, veryHigh: 0 }),
    ).toEqual({ value: 100, zone: "E" });
  });

  it("derives archive bands and risk from the CSV totals", () => {
    const result = summarizeAggregate({
      startDate: "2026-10-13",
      endDate: "2026-10-26",
      sourceKind: "carelink-csv",
      timeBasis: "device-local-unverified",
      lastImportedAt: null,
      intervalMinutes: 5,
      readings: 4032,
      exactReadings: 4032,
      exactSum: 4032 * 150,
      bands: { veryLow: 0, low: 0, inRange: 4032, high: 0, veryHigh: 0 },
      provenance: sampleHealth.glucoseHistory!.provenance,
    });
    expect(result.gri).toEqual({ value: 0, zone: "A" });
  });

  it("says lows as time per day", () => {
    expect(minutesPerDay(1)).toBeCloseTo(14.4);
    expect(perDay(1.75)).toBe("about 25 min a day");
    expect(perDay(0)).toBeNull();
    expect(perDay(0.03)).toBe("under a minute a day");
    expect(perDay(10)).toBe("about 2.5 h a day");
  });
});

describe("the ring's window", () => {
  it("prefers 14 days of history, then the CSV archive, then a shorter history", () => {
    const long = rangeWindow(sampleHealth.glucoseHistory, null);
    expect(long.window?.source).toBe("history");
    expect(long.window?.tight).not.toBeNull();
    const day = history(1, () => 120);
    const archive = {
      startDate: "2026-10-13",
      endDate: "2026-10-26",
      sourceKind: "carelink-csv" as const,
      timeBasis: "device-local-unverified" as const,
      lastImportedAt: null,
      intervalMinutes: 5,
      readings: 4032,
      exactReadings: 4032,
      exactSum: 4032 * 150,
      bands: { veryLow: 0, low: 32, inRange: 3600, high: 400, veryHigh: 0 },
      provenance: sampleHealth.glucoseHistory!.provenance,
      status: "available" as const,
    };
    const archived = rangeWindow(day, archive);
    expect(archived.window).toMatchObject({
      source: "archive",
      title: "Last 14 days",
      tight: null,
      cv: null,
    });
    const short = rangeWindow(day, null);
    expect(short.window).toMatchObject({
      source: "history",
      title: "Last 24 hours",
    });
    expect(rangeWindow(null, null)).toMatchObject({
      window: null,
      reason: "History unavailable · no estimate inferred",
    });
  });
});

describe("hypoglycemia episodes (2023 consensus)", () => {
  it("needs 15 minutes below 70 to start an episode", () => {
    expect(lowEpisodes(run([100, 65, 64, 100, 100, 100, 100]))).toHaveLength(0);
    const one = lowEpisodes(run([100, 65, 64, 63, 100, 100, 100]));
    expect(one).toHaveLength(1);
    expect(one[0].nadir).toBe(63);
    expect(one[0].start).toBe(new Date(start + 300_000).toISOString());
  });

  it("keeps one episode through a short recovery and ends it after 15 minutes at or above 70", () => {
    // Back above for only 10 minutes, then low again: still one episode.
    const merged = lowEpisodes(
      run([65, 64, 63, 75, 76, 62, 61, 60, 80, 80, 80, 80]),
    );
    expect(merged).toHaveLength(1);
    expect(merged[0].nadir).toBe(60);
    // It ends when glucose first returned at or above 70 for good.
    expect(merged[0].end).toBe(new Date(start + 8 * 300_000).toISOString());
    expect(merged[0].minutes).toBe(40);
    // A full 15 minutes above separates two episodes.
    const two = lowEpisodes(
      run([65, 64, 63, 75, 76, 77, 62, 61, 60, 80, 80, 80]),
    );
    expect(two).toHaveLength(2);
  });

  it("counts level 2 below 54, and treats a long gap as a break", () => {
    const values = [60, 52, 51, 50, 60, 80, 80, 80];
    expect(lowEpisodes(run(values), { threshold: 54 })).toHaveLength(1);
    expect(lowEpisodes(run(values))).toHaveLength(1);
    const gap = [...run([65, 64]), ...run([63, 62], start + 60 * 60_000)];
    expect(lowEpisodes(gap)).toHaveLength(0);
    // Readings below the sensor's range count as low.
    const bounded = run([65, 64, 63]).map((s, i) =>
      i === 1 ? { ...s, value: 40, comparison: "<" as const } : s,
    );
    expect(lowEpisodes(bounded)).toHaveLength(1);
  });

  it("marks lows that start between midnight and 6 AM as overnight", () => {
    const night = lowEpisodes(run([65, 64, 63, 80, 80, 80]));
    expect(night[0].night).toBe(true);
    const day = lowEpisodes(
      run([65, 64, 63, 80, 80, 80], start + 9 * 3_600_000),
    );
    expect(day[0].night).toBe(false);
    expect(localTime(start + 9 * 3_600_000)).toEqual({
      date: "2026-10-01",
      minute: 540,
    });
  });

  it("finds the demo's one fictional overnight low", () => {
    const lows = lowEpisodes(sampleHealth.glucoseHistory!.samples);
    expect(lows).toHaveLength(1);
    expect(lows[0].night).toBe(true);
  });
});

describe("GMI from the glucose data star", () => {
  const days = Array.from({ length: 90 }, (_, i) => ({
    date: new Date(Date.parse("2026-08-01T00:00:00Z") + i * 86_400_000)
      .toISOString()
      .slice(0, 10),
    min: 60,
    // The last 14 days run higher.
    mean: i >= 76 ? 180 : 140,
    max: 250,
    count: 288,
  }));

  it("weights each day's mean by its readings over the latest N days", () => {
    const d14 = gmiFromDays(days, 14)!;
    expect(d14.mean).toBe(180);
    expect(d14.gmi).toBeCloseTo(gmi(180));
    expect(d14).toMatchObject({
      start: "2026-10-16",
      end: "2026-10-29",
      daysCovered: 14,
      coverage: 100,
      partial: false,
    });
    const d90 = gmiFromDays(days, 90)!;
    expect(d90.mean).toBeCloseTo((76 * 140 + 14 * 180) / 90);
    // Unequal counts pull the mean toward the fuller day.
    const uneven = gmiFromDays(
      [
        { date: "2026-10-01", mean: 100, count: 288 },
        { date: "2026-10-02", mean: 200, count: 96 },
      ],
      2,
    )!;
    expect(uneven.mean).toBeCloseTo((100 * 288 + 200 * 96) / 384);
  });

  it("reports coverage and marks a window under 70% as partial", () => {
    const sparse = days.filter((_, i) => i % 2 === 0);
    const result = gmiFromDays(sparse, 30)!;
    expect(result.daysCovered).toBe(15);
    expect(result.coverage).toBeCloseTo(50);
    expect(result.partial).toBe(true);
    expect(gmiFromDays([], 90)).toBeNull();
    expect(
      gmiFromDays([{ date: "2026-10-01", mean: Number.NaN, count: 5 }], 90),
    ).toBeNull();
  });
});

describe("typical day and best day", () => {
  it("builds percentile bands by time of day from five days or more", () => {
    expect(typicalDay(history(4, () => 120))).toBeNull();
    const agp = typicalDay(history(14, (i) => 100 + (i % 288 < 144 ? 0 : 60)))!;
    expect(agp.days).toBe(14);
    expect(agp.points).toHaveLength(96);
    const morning = agp.points[20]!,
      evening = agp.points[80]!;
    expect(morning.p50).toBe(100);
    expect(evening.p50).toBe(160);
    expect(morning.p5).toBeLessThanOrEqual(morning.p95);
  });

  it("picks the full day with the most time in range", () => {
    const data = history(5, (i) =>
      Math.floor(i / 288) === 2 ? 120 : i % 2 ? 200 : 120,
    );
    const best = bestDay(data)!;
    expect(best.date).toBe("2026-10-03");
    expect(best.within).toBe(100);
    expect(best.days).toBe(5);
    expect(bestDay(history(2, () => 120))).toBeNull();
  });

  it("summarizes a stretch of the 24-hour chart", () => {
    const stats = readingStats([
      { value: 60 },
      { value: 100 },
      { value: 150 },
      { value: 200 },
    ])!;
    expect(stats).toMatchObject({
      readings: 4,
      within: 50,
      tight: 25,
      below: 25,
      above: 25,
      mean: 127.5,
      min: 60,
      max: 200,
    });
    expect(readingStats([])).toBeNull();
  });
});
