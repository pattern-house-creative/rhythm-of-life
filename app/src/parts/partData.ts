import { notConnected } from "../health";
import { newestFirst, runLine } from "../insights";
import type { DailyInsight, InsightRun } from "../insights";
import type { ObservatoryData } from "../model";
import { glucoseSlot } from "../../server/healthState";

// What Home's tiles and a dashboard's native parts both read from the app's
// own data (docs/dev/screens.md 10.3), worked out once for both.

// A source's tiles show only once it is connected: a household may never use
// glucose or Garmin, so the home screen never says they are missing. A source
// that failed still shows, as unavailable. Garmin waits until it is known.
export const glucoseOn = (data: ObservatoryData) =>
  !notConnected(data, glucoseSlot);
export const garminOn = (data: ObservatoryData) =>
  !notConnected(data, "garmin") &&
  !data.sources.find((s) => s.id === "garmin")?.checking;

// The day's insight when one is current; with none, the newest earlier one,
// which keeps the history in reach, and the engine's last run in a line. The
// demo's preview of a missing source shows none of them.
export function insightsShown(
  insights: {
    current: DailyInsight | null;
    insights: DailyInsight[];
    runs: InsightRun[];
  },
  blocked: boolean,
) {
  const current = blocked ? null : insights.current;
  return {
    current,
    latest:
      blocked || current ? null : (newestFirst(insights.insights)[0] ?? null),
    lastRun: blocked || current ? null : runLine(insights.runs),
  };
}
