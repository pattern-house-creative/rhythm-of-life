import { describe, expect, it } from "vitest";
import type { GlucoseHistory } from "../src/model";
import { summarizeAggregate, summarizeGlucose } from "../src/glucose";
import { sampleHealth } from "./sampleHealth";

function history(): GlucoseHistory {
  const start = Date.parse("2026-10-01T00:00:00Z");
  return {
    start: new Date(start).toISOString(),
    end: new Date(start + 14 * 86400000).toISOString(),
    intervalMinutes: 5,
    unit: "mg/dL",
    timestampBasis: "verified",
    complete: true,
    sourceKind: "demo",
    provenance: sampleHealth.glucoseHistory.provenance,
    samples: Array.from({ length: 4032 }, (_, i) => ({
      timestamp: new Date(start + i * 300000).toISOString(),
      value: 150,
    })),
  };
}
describe("historical CGM summary", () => {
  it("uses the published GMI equation and counts full fixed-cadence coverage", () => {
    const result = summarizeGlucose(history());
    expect(result.mean).toBe(150);
    expect(result.gmi).toBeCloseTo(6.898, 3);
    expect(result.coverage).toBe(100);
    expect(result.range).toEqual({ below: 0, within: 100, above: 0 });
  });
  it("includes 70 and 180 in range and calculates below/above from recorded time", () => {
    const data = history();
    data.samples.forEach((s, i) => (s.value = [69, 70, 180, 181][i % 4]));
    expect(summarizeGlucose(data).range).toEqual({
      below: 25,
      within: 50,
      above: 25,
    });
  });
  it("does not fill gaps, inflate duplicate coverage, or count conflicting records", () => {
    const data = history();
    data.samples = data.samples.filter((_, i) => i % 4 !== 0);
    const base = summarizeGlucose(data);
    expect(base.coverage).toBe(75);
    expect(base.range?.within).toBe(100);
    data.samples.push({ ...data.samples[0] });
    expect(summarizeGlucose(data)).toEqual(base);
    data.samples.push({ ...data.samples[0], value: 250 });
    const conflicting = summarizeGlucose(data);
    expect(conflicting.readings).toBe(base.readings - 1);
    expect(conflicting.mean).toBe(150);
  });
  it("withholds estimates for sparse, incomplete or unresolved histories", () => {
    const data = history();
    for (const input of [
      { ...data, samples: [] },
      { ...data, samples: data.samples.slice(0, 2000) },
      { ...data, complete: false },
      { ...data, timestampBasis: "unverified" as const },
      { ...data, unit: "mmol/L" },
      { ...data, intervalMinutes: 60 },
    ]) {
      const result = summarizeGlucose(input);
      expect(result.gmi).toBeNull();
      expect(result.range).toBeNull();
      expect(result.reason).toBeTruthy();
    }
  });
  it("keeps bounded readings out of the mean, drops invalid and out-of-window ones, and counts off-grid timestamps", () => {
    const data = history();
    data.samples[0].comparison = ">";
    data.samples[0].value = 400;
    data.samples[1].value = NaN;
    data.samples[2].value = -1;
    data.samples[3].timestamp = new Date(
      Date.parse(data.start) + 900001,
    ).toISOString();
    data.samples.push({ timestamp: data.end, value: 400 });
    const result = summarizeGlucose(data);
    expect(result.readings).toBe(4029);
    expect(result.bounded).toBe(1);
    expect(result.mean).toBe(150);
    expect(result.range!.above).toBeCloseTo((1 / 4030) * 100, 6);
  });
  it("counts real sensor timestamps that are not aligned to the window start", () => {
    const data = history();
    // CareLink readings arrive at arbitrary seconds; the window starts at "now - 24h".
    data.samples = data.samples.map((s) => ({
      ...s,
      timestamp: new Date(Date.parse(s.timestamp) + 37_000).toISOString(),
    }));
    const result = summarizeGlucose(data);
    expect(result.coverage).toBe(100);
    expect(result.gmi).toBeCloseTo(6.898, 3);
  });
  it("reports a well-covered day's time in range and mean, but never a one-day GMI", () => {
    const data = history();
    const day = {
      ...data,
      end: new Date(Date.parse(data.start) + 86400000).toISOString(),
      samples: data.samples.slice(0, 288).map((s, i) => ({
        ...s,
        value: i % 2 ? 200 : 100,
      })),
    };
    const result = summarizeGlucose(day);
    expect(result.coverage).toBe(100);
    expect(result.mean).toBe(150);
    expect(result.range).toEqual({ below: 0, within: 50, above: 50 });
    expect(result.gmi).toBeNull();
    expect(result.reason).toMatch(/14 days/);
  });
  it("derives 14-day GMI and bands from CSV archive totals only at 70% coverage", () => {
    const base = {
      startDate: "2026-10-13",
      endDate: "2026-10-26",
      sourceKind: "carelink-csv" as const,
      timeBasis: "device-local-unverified" as const,
      lastImportedAt: "2026-10-27T12:17:03Z",
      intervalMinutes: 5,
      provenance: sampleHealth.glucoseHistory.provenance,
    };
    const full = summarizeAggregate({
      ...base,
      readings: 4032,
      exactReadings: 4000,
      exactSum: 4000 * 150,
      bands: { veryLow: 40, low: 80, inRange: 3000, high: 800, veryHigh: 112 },
    });
    expect(full.coverage).toBe(100);
    expect(full.gmi).toBeCloseTo(6.898, 3);
    expect(full.range!.within).toBeCloseTo((3000 / 4032) * 100, 6);
    expect(full.range!.below).toBeCloseTo((120 / 4032) * 100, 6);
    expect(full.days).toBe(14);
    const partial = summarizeAggregate({
      ...base,
      readings: 1200,
      exactReadings: 1200,
      exactSum: 1200 * 150,
      bands: { veryLow: 0, low: 0, inRange: 1200, high: 0, veryHigh: 0 },
    });
    expect(partial.gmi).toBeNull();
    expect(partial.range).toBeNull();
    expect(partial.coverage).toBeCloseTo((1200 / 4032) * 100, 6);
    expect(partial.reason).toMatch(/70%/);
  });
  it("keeps the fictional historical window consistent with the final displayed reading", () => {
    const result = summarizeGlucose(sampleHealth.glucoseHistory);
    expect(result.days).toBe(14);
    expect(result.coverage).toBeGreaterThan(70);
    expect(result.coverage).toBeLessThan(100);
    expect(sampleHealth.glucoseHistory.samples.at(-1)?.value).toBe(
      sampleHealth.current.value,
    );
    expect(Date.parse(sampleHealth.glucoseHistory.samples.at(-1)!.timestamp)).toBe(
      Date.parse(sampleHealth.current.provenance.observedAt),
    );
    expect(
      result.range!.below + result.range!.within + result.range!.above,
    ).toBeCloseTo(100);
  });
});
