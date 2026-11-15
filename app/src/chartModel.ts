import { homeTimeZone, localTime } from "./glucose";
import { hasMeasurement, shortTime } from "./health";
import type { MeasurementSeries } from "./model";
// The glucose tile's line covers the last three hours of readings.
export const recentHours = 3;
function recent<T extends { timestamp: string }>(samples: T[]) {
  const end = Date.parse(samples.at(-1)?.timestamp ?? "");
  if (!Number.isFinite(end)) return samples.slice(-recentHours * 12);
  return samples.filter(
    (s) => Date.parse(s.timestamp) >= end - recentHours * 3_600_000,
  );
}
export function chartModel(
  series: MeasurementSeries,
  segment = "all",
  small = false,
) {
  const valid = (hasMeasurement(series) ? series.samples : []).filter(
    (s) => Number.isFinite(s.value) && s.value > 0 && Number.isFinite(s.hour),
  );
  const samples = small
    ? recent(valid)
    : valid.filter((s) =>
        segment === "night"
          ? s.hour < 7
          : segment === "day"
            ? s.hour >= 7
            : true,
      );
  const values = samples.map((s) => s.value);
  const min = values.length ? Math.min(...values) : null,
    max = values.length ? Math.max(...values) : null;
  const reference: [number, number] =
    series.unit === "mmol/L" ? [3.9, 10] : [70, 180];
  const step = series.unit === "mmol/L" ? 1 : 20;
  const lower =
    Math.floor(
      Math.max(0, Math.min(min ?? reference[0], reference[0]) - step) / step,
    ) * step;
  const upper =
    Math.ceil((Math.max(max ?? reference[1], reference[1]) + step) / step) *
    step;
  const domain: [number, number] = [lower, upper];
  const label = samples.length
    ? `${series.provenance.demo ? "Fictional " : "Recorded "}${small ? "last three hours of" : segment === "night" ? "overnight" : segment === "day" ? "daytime" : "24-hour"} glucose readings. ${samples.length} samples, from ${min} to ${max} ${series.unit}. Latest ${samples.at(-1)!.value} ${series.unit} at ${shortTime(samples.at(-1)!.timestamp)}.`
    : "No glucose readings available for this interval.";
  return { samples, min, max, domain, reference, label };
}

// Whole hours in the install's time zone to label along a stretch of time: every
// six hours across a day, closer together for a shorter stretch. The zone is the
// one the server counts each sample's hour in, so the labels line up with the
// overnight shading whatever zone the device is set to.
export function hourTicks(start: number, end: number, zone = homeTimeZone()) {
  if (!Number.isFinite(start) || !Number.isFinite(end) || end <= start)
    return [];
  const hours = (end - start) / 3_600_000;
  const step = hours > 12 ? 6 : hours > 5 ? 2 : 1;
  const ticks: number[] = [];
  // A zone's whole hours fall on a quarter hour of UTC.
  const quarter = 900_000;
  for (let t = Math.ceil(start / quarter) * quarter; t <= end; t += quarter) {
    const { minute } = localTime(t, zone);
    if (minute % 60 === 0 && (minute / 60) % step === 0) ticks.push(t);
  }
  return ticks;
}
// An hour label on the chart ("7 AM"), in the install's time zone.
export const hourLabel = (time: number, zone = homeTimeZone()) =>
  new Date(time)
    .toLocaleTimeString([], { hour: "numeric", timeZone: zone })
    .replace(":00", "");

// Stretches of the chart that fall overnight (before 07:00, as the Overnight
// switch counts it), as [first, last] instants for shading.
export function overnightRuns(samples: { hour: number; timestamp: string }[]) {
  const runs: [number, number][] = [];
  let run: [number, number] | null = null;
  for (const s of samples) {
    const time = Date.parse(s.timestamp);
    if (s.hour < 7) {
      if (run) run[1] = time;
      else run = [time, time];
    } else if (run) {
      // Shade up to the first daytime reading, but never across a long gap.
      runs.push([run[0], Math.min(time, run[1] + 3_600_000)]);
      run = null;
    }
  }
  if (run) runs.push(run);
  return runs.filter(([a, b]) => b > a);
}
