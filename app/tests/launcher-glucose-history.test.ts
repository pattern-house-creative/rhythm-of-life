import { describe, expect, it } from "vitest";
import type { GlucoseHistory } from "../src/model";
import {
  launcherGlucoseHistory,
  mgDlPerMmol,
} from "../src/launcherGlucoseHistory";
const now = Date.parse("2026-11-14T02:00:00Z"),
  minute = 60_000;
function fixture(value = 120): GlucoseHistory {
  return {
    start: new Date(now - 14 * 86400000).toISOString(),
    end: new Date(now).toISOString(),
    unit: "mg/dL",
    intervalMinutes: 5,
    timestampBasis: "verified",
    complete: true,
    sourceKind: "live",
    status: "available",
    provenance: {
      sourceId: "fixture",
      recordId: null,
      observedAt: new Date(now - minute).toISOString(),
      demo: false,
    },
    samples: Array.from({ length: 288 }, (_, i) => ({
      value,
      timestamp: new Date(now - 86400000 + i * 5 * minute).toISOString(),
    })),
  };
}
describe("launcher trailing 24-hour cadence estimate", () => {
  it.each([
    [69.9, 0],
    [70, 100],
    [180, 100],
    [180.1, 0],
  ])("classifies inclusive boundaries %s", (value, within) => {
    expect(launcherGlucoseHistory(fixture(value), now)).toMatchObject({
      within,
      coverage: 100,
      knownSlots: 288,
    });
  });
  it.each([70, 180, 69, 181])(
    "converts mmol/L with the engine factor before classifying %s",
    (value) => {
      const h = fixture(value / mgDlPerMmol);
      h.unit = "mmol/L";
      const a = launcherGlucoseHistory(h, now, "mmol/L"),
        b = launcherGlucoseHistory(fixture(value), now);
      expect(a.within).toBe(b.within);
      expect(a.range[0]).toBeCloseTo(70 / mgDlPerMmol, 10);
      expect(a.points[0].value).toBeCloseTo(value / mgDlPerMmol, 10);
    },
  );
  it("uses exactly trailing 24 hours, not local calendar days or newest sample time", () => {
    const h = fixture();
    h.samples.unshift({
      value: 300,
      timestamp: new Date(now - 86400000 - 1).toISOString(),
    });
    h.samples.push(
      { value: 300, timestamp: new Date(now).toISOString() },
      { value: 300, timestamp: new Date(now + minute).toISOString() },
    );
    expect(launcherGlucoseHistory(h, now).within).toBe(100);
    const aged = launcherGlucoseHistory(h, now + 2 * 3600000);
    expect(aged.coverage).toBeLessThan(100);
    expect(aged.end - aged.start).toBe(86400000);
  });
  it("deduplicates samples and weighs slots once despite bursts and irregular cadence", () => {
    const h = fixture();
    h.samples[0].value = 200;
    for (let i = 1; i < 100; i++)
      h.samples.push({
        value: 200,
        timestamp: new Date(now - 86400000 + i * 1000).toISOString(),
      });
    h.samples.push(h.samples[1]);
    const r = launcherGlucoseHistory(h, now);
    expect(r.knownSlots).toBe(288);
    expect(r.within).toBeCloseTo((287 / 288) * 100);
  });
  it("conflicting duplicates or bands leave an unknown slot, not a guessed mean", () => {
    const h = fixture();
    h.samples.push(
      { ...h.samples[0], value: 200 },
      {
        value: 200,
        timestamp: new Date(now - 86400000 + 6 * minute).toISOString(),
      },
    );
    const r = launcherGlucoseHistory(h, now);
    expect(r.knownSlots).toBe(286);
    expect(r.within).toBe(100);
  });
  it("keeps gaps blank and excludes missing slots from the percentage denominator", () => {
    const h = fixture();
    h.samples.splice(30, 60);
    const r = launcherGlucoseHistory(h, now);
    expect(r.coverage).toBeCloseTo((228 / 288) * 100);
    expect(r.within).toBe(100);
    expect(r.segments).toHaveLength(2);
    expect(r.points).toHaveLength(228);
  });
  it("withholds TIR below 70% coverage but still plots actual measurements", () => {
    const h = fixture();
    h.samples = h.samples.slice(0, 201);
    let r = launcherGlucoseHistory(h, now);
    expect(r.within).toBeNull();
    expect(r.reason).toBe("Needs 70% coverage");
    expect(r.points).toHaveLength(201);
    h.samples = fixture().samples.slice(0, 202);
    r = launcherGlucoseHistory(h, now);
    expect(r.within).toBe(100);
  });
  it("treats censored bounds conservatively and never plots them as exact values", () => {
    const h = fixture();
    h.samples[0] = { ...h.samples[0], value: 70, comparison: "<" };
    h.samples[1] = { ...h.samples[1], value: 180, comparison: ">" };
    h.samples[2] = { ...h.samples[2], value: 200, comparison: "<" };
    const r = launcherGlucoseHistory(h, now);
    expect(r.knownSlots).toBe(287);
    expect(r.points).toHaveLength(285);
    expect(r.within).toBeCloseTo((285 / 287) * 100);
  });
  it("does not reuse archive aggregates for 24h or infer from incomplete/unverified history", () => {
    const h = fixture();
    h.complete = false;
    expect(launcherGlucoseHistory(h, now).within).toBeNull();
    h.complete = true;
    h.timestampBasis = "unverified";
    expect(launcherGlucoseHistory(h, now).points).toHaveLength(0);
    h.timestampBasis = "verified";
    h.provenance.demo = true;
    expect(launcherGlucoseHistory(h, now).within).toBeNull();
    expect(launcherGlucoseHistory(null, now).points).toHaveLength(0);
  });
  it("rejects unsupported cadence/units and clips to the declared source interval", () => {
    const h = fixture();
    h.intervalMinutes = 0;
    expect(launcherGlucoseHistory(h, now).within).toBeNull();
    h.intervalMinutes = 5;
    h.unit = "unknown";
    expect(launcherGlucoseHistory(h, now).points).toHaveLength(0);
    h.unit = "mg/dL";
    h.end = new Date(now - 3600000).toISOString();
    expect(launcherGlucoseHistory(h, now).knownSlots).toBe(276);
  });
  it("is independent of local midnight and DST", () => {
    for (const end of [
      Date.parse("2026-11-01T06:30:00Z"),
      Date.parse("2026-03-08T07:30:00Z"),
    ]) {
      const h = fixture();
      const shift = end - now;
      h.start = new Date(Date.parse(h.start) + shift).toISOString();
      h.end = new Date(end).toISOString();
      h.samples = h.samples.map((s) => ({
        ...s,
        timestamp: new Date(Date.parse(s.timestamp) + shift).toISOString(),
      }));
      expect(launcherGlucoseHistory(h, end).coverage).toBe(100);
    }
  });
});
