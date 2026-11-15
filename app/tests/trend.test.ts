import { describe, expect, it } from "vitest";
import {
  arrowCount,
  currentTrend,
  glucoseTrend,
  trendChange,
  trendWords,
} from "../src/trend";
import { sampleHealth } from "./sampleHealth";
import { previewHealth } from "../src/health";

// Fictional five-minute readings ending at `end`, newest last.
const end = Date.parse("2026-10-18T22:00:00Z");
function readings(values: number[], at = end) {
  return values.map((value, i) => ({
    value,
    timestamp: new Date(at - (values.length - 1 - i) * 300_000).toISOString(),
  }));
}
const latest = (value: number, at = end) => ({
  value,
  timestamp: new Date(at).toISOString(),
});

describe("Medtronic trend arrows", () => {
  it("buckets the 20-minute rate at 1, 2 and 3 mg/dL per minute, either way", () => {
    expect(arrowCount(0)).toBe(0);
    expect(arrowCount(0.99)).toBe(0);
    expect(arrowCount(1)).toBe(1);
    expect(arrowCount(1.99)).toBe(1);
    expect(arrowCount(2)).toBe(2);
    expect(arrowCount(2.99)).toBe(2);
    expect(arrowCount(3)).toBe(3);
    expect(arrowCount(7)).toBe(3);
    expect(arrowCount(-0.5)).toBe(0);
    expect(arrowCount(-1.5)).toBe(1);
    expect(arrowCount(-2.5)).toBe(2);
    expect(arrowCount(-3)).toBe(3);
    // 40 mg/dL over 20 minutes is exactly two, not 1.999….
    expect(arrowCount(40 / 20)).toBe(2);
    expect(arrowCount(60.000000001 / 20)).toBe(3);
  });

  it("compares the latest reading with the one 20 minutes earlier", () => {
    const samples = readings([160, 150, 138, 125, 112]);
    const trend = glucoseTrend(latest(112), samples, { now: end + 60_000 });
    expect(trend).toMatchObject({
      arrows: 2,
      direction: "down",
      delta: -48,
      minutes: 20,
    });
    expect(trend!.rate).toBeCloseTo(-2.4);
    expect(trendWords(trend)).toBe("Falling quickly");
    expect(trendChange(trend!)).toBe("−48 mg/dL in 20 min");
  });

  it("shows up to three arrows rising, and none while steady", () => {
    const up = glucoseTrend(latest(180), readings([110, 130, 150, 165, 180]), {
      now: end,
    });
    expect(up).toMatchObject({ arrows: 3, direction: "up" });
    expect(trendWords(up)).toBe("Rising very quickly");
    const one = glucoseTrend(latest(130), readings([105, 110, 118, 124, 130]), {
      now: end,
    });
    expect(one).toMatchObject({ arrows: 1, direction: "up" });
    expect(trendWords(one)).toBe("Rising");
    const steady = glucoseTrend(
      latest(118),
      readings([110, 112, 115, 117, 118]),
      {
        now: end,
      },
    );
    expect(steady).toMatchObject({ arrows: 0, direction: null, delta: 8 });
    expect(trendWords(steady)).toBe("Steady");
    expect(trendChange(steady!)).toBe("+8 mg/dL in 20 min");
  });

  it("allows one five-minute slot either side of 20 minutes, preferring the closest", () => {
    // No reading at 20 minutes; the one at 25 minutes is used.
    const samples = readings([170, 150, 140, 130, 120, 110]).filter(
      (_, i) => i !== 1,
    );
    const trend = glucoseTrend(latest(110), samples, { now: end });
    expect(trend?.minutes).toBe(25);
    expect(trend?.delta).toBe(-60);
    expect(trend?.arrows).toBe(2);
    // Off-grid CareLink seconds still find the nearest reading.
    const skewed = readings([150, 140, 130, 120, 110]).map((s, i) =>
      i === 0
        ? {
            ...s,
            timestamp: new Date(Date.parse(s.timestamp) + 37_000).toISOString(),
          }
        : s,
    );
    expect(
      glucoseTrend(latest(110), skewed, { now: end })?.minutes,
    ).toBeCloseTo(20 - 37 / 60);
  });

  it("shows no arrow without a reading 15 to 25 minutes earlier", () => {
    // Only 10 and 30 minutes back.
    const samples = [
      { value: 150, timestamp: new Date(end - 30 * 60_000).toISOString() },
      { value: 120, timestamp: new Date(end - 10 * 60_000).toISOString() },
    ];
    expect(glucoseTrend(latest(110), samples, { now: end })).toBeNull();
    expect(glucoseTrend(latest(110), [], { now: end })).toBeNull();
  });

  it("shows no arrow when the latest reading is not recent or not exact", () => {
    const samples = readings([160, 150, 138, 125, 112]);
    expect(
      glucoseTrend(latest(112), samples, { now: end + 21 * 60_000 }),
    ).toBeNull();
    expect(
      glucoseTrend(latest(112), samples, { now: end + 20 * 60_000 }),
    ).not.toBeNull();
    // A reading from the future is not trusted either.
    expect(
      glucoseTrend(latest(112), samples, { now: end - 5 * 60_000 }),
    ).toBeNull();
    // Fixed fictional days skip the age check on request.
    expect(
      glucoseTrend(latest(112), samples, {
        now: end + 86_400_000,
        ignoreAge: true,
      })?.arrows,
    ).toBe(2);
    expect(
      glucoseTrend({ ...latest(40), comparison: "<" }, samples, { now: end }),
    ).toBeNull();
    expect(glucoseTrend(null, samples, { now: end })).toBeNull();
  });

  it("ignores beyond-range and invalid earlier readings", () => {
    const at20 = new Date(end - 20 * 60_000).toISOString();
    const samples = [
      { value: 400, timestamp: at20, comparison: ">" as const },
      { value: Number.NaN, timestamp: at20 },
    ];
    expect(glucoseTrend(latest(200), samples, { now: end })).toBeNull();
  });

  it("reads the demo's current reading against its five-minute history", () => {
    // The demo's 24-hour series is hourly, so the history supplies the
    // reading 20 minutes earlier; the fictional evening is steady.
    const trend = currentTrend(sampleHealth, Date.parse("2027-01-01T00:00:00Z"));
    expect(trend).not.toBeNull();
    expect(trend!.minutes).toBe(20);
    expect(trend!.arrows).toBe(0);
    // A stale or missing reading gets no arrow.
    expect(currentTrend(previewHealth(sampleHealth, "stale"))).toBeNull();
    expect(currentTrend(previewHealth(sampleHealth, "empty"))).toBeNull();
  });

  it("does not age-exempt live readings", () => {
    const live = {
      ...sampleHealth,
      current: {
        ...sampleHealth.current,
        provenance: { ...sampleHealth.current.provenance, demo: false },
      },
    };
    const observed = Date.parse(sampleHealth.current.provenance.observedAt!);
    expect(currentTrend(live, observed + 5 * 60_000)).not.toBeNull();
    expect(currentTrend(live, observed + 45 * 60_000)).toBeNull();
  });
});
