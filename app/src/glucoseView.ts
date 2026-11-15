import type {
  GlucoseAggregate,
  GlucoseHistory,
  ObservatoryData,
} from "./model";
import { glucoseSlot } from "../server/healthState";
import type { Bands } from "./glucose";
import { hasMeasurement, healthStatus } from "./health";
import {
  dayRange,
  historyDates,
  minutesPerDay,
  summarizeAggregate,
  summarizeGlucose,
} from "./glucose";
import { dataStar, metric, useReadings } from "./panels/readings";
import type { ReadData } from "./panels/readings";

// Shared wording and windows for the glucose tile and panel.
export const pct = (n: number) =>
  `${n.toFixed(n >= 10 || n === 0 || n >= 99.95 ? 0 : 1)}%`;

// The five consensus bands, highest first as the AGP report lists them.
export const bandOrder = [
  "veryHigh",
  "high",
  "inRange",
  "low",
  "veryLow",
] as const;
export const bandNames: Record<keyof Bands, string> = {
  veryHigh: "Very high",
  high: "High",
  inRange: "In range",
  low: "Low",
  veryLow: "Very low",
};
export const bandLimits: Record<keyof Bands, string> = {
  veryHigh: "above 250",
  high: "181–250",
  inRange: "70–180",
  low: "54–69",
  veryLow: "below 54",
};

export const ringLabel = (bands: Bands, tight: number | null, when: string) =>
  `${when}: ${bandOrder.map((k) => `${bandNames[k].toLowerCase()} ${bandLimits[k]} ${bands[k].toFixed(1)}%`).join(", ")}${tight !== null ? `; tight range 70–140 ${tight.toFixed(1)}%` : ""}.`;

export type RangeWindow = NonNullable<ReturnType<typeof rangeWindow>["window"]>;
// The best window for time in ranges: a history of 14 days or more, else the
// glucose archive's 14 days, else whatever well-covered history there is.
export function rangeWindow(
  history: GlucoseHistory | null,
  summary: GlucoseAggregate | null | undefined,
  stale = false,
) {
  const recent =
    history && hasMeasurement(history) ? summarizeGlucose(history) : null;
  // Archive totals carry an explicit status; their days are not instants to age.
  const archive =
    summary && (summary.status === "available" || summary.status === "stale")
      ? summary
      : null;
  const long = archive ? summarizeAggregate(archive) : null;
  const archiveDays = archive
    ? dayRange(archive.startDate, archive.endDate)
    : null;
  const gmi14 = recent?.gmi ?? long?.gmi ?? null;
  const fromHistory = () =>
    history && recent?.bands && recent.range
      ? {
          source: "history" as const,
          title:
            recent.days >= 2
              ? `Last ${Math.round(recent.days)} days`
              : `Last ${Math.round(recent.days * 24)} hours`,
          tag: history.provenance.demo
            ? "DEMO"
            : history.sourceKind === "archive"
              ? "CSV ARCHIVE"
              : "CGM",
          dates: historyDates(history),
          stale: stale || healthStatus(history) === "stale",
          days: recent.days,
          coverage: recent.coverage,
          readings: recent.readings + recent.bounded,
          bounded: recent.bounded,
          bands: recent.bands,
          within: recent.range.within,
          tight: recent.tight,
          mean: recent.mean,
          cv: recent.cv,
          gri: recent.gri,
        }
      : null;
  const fromArchive = () =>
    archive && long?.bands && long.range && long.gri
      ? {
          source: "archive" as const,
          title: "Last 14 days",
          tag: "CSV ARCHIVE",
          dates: archiveDays ?? "",
          stale: archive.status === "stale",
          days: long.days,
          coverage: long.coverage,
          readings: long.readings,
          bounded: archive.readings - archive.exactReadings,
          bands: long.bands,
          within: long.range.within,
          tight: null,
          mean: long.mean,
          cv: null,
          gri: long.gri,
        }
      : null;
  const window =
    recent && recent.days >= 14
      ? fromHistory()
      : (fromArchive() ?? fromHistory());
  const reason = window
    ? null
    : !history || !recent
      ? "History unavailable · no estimate inferred"
      : (recent.reason ?? "History unavailable · no estimate inferred");
  return { window, reason, gmi14, archiveDays };
}

// The provider whose glucose data star holds the longest record: the history's archive, else
// the history's own provider, else the current reading's. Null before any reading names one.
export function glucoseProvider(
  data: Pick<ObservatoryData, "glucoseHistory" | "current">,
) {
  const history = data.glucoseHistory;
  const provider =
    history?.sources?.find((s) => s.sourceKind === "archive")?.provider ??
    history?.provenance.sourceId ??
    data.current.provenance.sourceId;
  return provider === glucoseSlot ? null : provider;
}

// That provider's glucose data star's daily summaries (metric glucose, or the older
// sensor_glucose): undefined while reading, null when unavailable.
export function useGlucoseDays(readData?: ReadData, provider?: string | null) {
  const series = useReadings(
    provider ? readData : undefined,
    dataStar(provider ?? glucoseSlot, "glucose"),
  );
  if (series === undefined) return undefined;
  const sg = metric(series, "glucose") ?? metric(series, "sensor_glucose");
  return sg && sg.unit === "mg/dL" && sg.days.length ? sg.days : null;
}

// Lows as time per day, "about 25 min a day".
export function perDay(percent: number) {
  const minutes = minutesPerDay(percent);
  if (minutes < 1) return percent > 0 ? "under a minute a day" : null;
  if (minutes < 90) return `about ${Math.round(minutes)} min a day`;
  const hours = Math.round(minutes / 30) / 2;
  return `about ${hours} h a day`;
}
