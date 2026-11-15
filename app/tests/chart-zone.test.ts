import { expect, it } from "vitest";
import { hourLabel, hourTicks, overnightRuns } from "../src/chartModel";
import { localTime } from "../src/glucose";
import { demoInstance, setInstance } from "../src/instance";
import { localDay } from "../src/panels/garmin";

// The tests' device runs in UTC (vitest.config.ts); the install here is in
// Europe/London, an hour ahead in July.
const at = (iso: string) => Date.parse(iso);
const london = () =>
  setInstance({ ...demoInstance, timezone: "Europe/London", mode: "live" });

it("puts the chart's hour ticks and labels on the install's hours, not the device's", () => {
  london();
  // 01:30 to 13:30 in London: a tick every two London hours, from 02:00.
  expect(
    hourTicks(at("2026-07-01T00:30:00Z"), at("2026-07-01T12:30:00Z")),
  ).toEqual(
    ["01", "03", "05", "07", "09", "11"].map((h) =>
      at(`2026-07-01T${h}:00:00Z`),
    ),
  );
  // 06:00 UTC reads as London's 7 AM.
  expect(hourLabel(at("2026-07-01T06:00:00Z"))).toBe(
    hourLabel(at("2026-07-01T07:00:00Z"), "UTC"),
  );
});

it("ends the overnight shading under the label that says 7 AM", () => {
  london();
  // Readings every half hour, each with its hour in the install's zone, as the
  // server counts them.
  const samples = Array.from({ length: 25 }, (_, i) => {
    const time = at("2026-07-01T00:00:00Z") + i * 1_800_000;
    return {
      hour: Math.floor(localTime(time).minute / 60),
      timestamp: new Date(time).toISOString(),
    };
  });
  const [[, end]] = overnightRuns(samples);
  expect(
    hourTicks(at("2026-07-01T04:00:00Z"), at("2026-07-01T08:00:00Z")),
  ).toContain(end);
  expect(hourLabel(end)).toBe(hourLabel(at("2026-07-01T07:00:00Z"), "UTC"));
});

it("counts today in the install's time zone", () => {
  // 23:30 UTC on 1 July is already 2 July in London.
  const late = new Date("2026-07-01T23:30:00Z");
  expect(localDay(late)).toBe("2026-07-01");
  london();
  expect(localDay(late)).toBe("2026-07-02");
  expect(localDay(late, "UTC")).toBe("2026-07-01");
});
