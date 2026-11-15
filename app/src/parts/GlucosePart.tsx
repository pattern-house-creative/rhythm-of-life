import {
  Activity,
  ArrowUpRight,
  ChevronRight,
  LayoutDashboard,
} from "lucide-react";
import {
  hasMeasurement,
  healthCaption,
  healthStatus,
  shortTime,
} from "../health";
import type { DemoState, ObservatoryData } from "../model";
import { chartModel } from "../chartModel";
import { glucoseProvider } from "../glucoseView";
import { GlucoseSummary } from "../GlucoseSummary";
import type { GraphSession } from "../liveGraph";
import { Sparkline } from "../Sparkline";
import { TrendArrows } from "../TrendArrows";
import { currentTrend, trendChange, trendWords } from "../trend";
import { LauncherGlucose } from "../LauncherGlucose";

// The glucose tile (docs/dev/screens.md 10.3): the latest reading with its
// trend, the three-hour line and time in range. Home's main corner draws it,
// and so does a dashboard's glucose part. The whole tile opens the glucose
// panel.
export function GlucosePart({
  data,
  state,
  blocked,
  readData,
  spark = true,
  onOpen,
  launcherNow,
}: {
  data: ObservatoryData;
  state: DemoState;
  // The demo's preview of a missing or failed source shows no readings.
  blocked: boolean;
  readData?: GraphSession["data"];
  // The three-hour line, left out while the explorer covers Home.
  spark?: boolean;
  onOpen: () => void;
  launcherNow?: number;
}) {
  if (launcherNow !== undefined)
    return <LauncherGlucose data={data} now={launcherNow} blocked={blocked} />;
  const currentReady =
    hasMeasurement(data.current) && data.current.value !== null;
  // Medtronic's rule: the change over the last 20 minutes, in one to three arrows.
  const trend = blocked ? null : currentTrend(data);
  return (
    <button className="tile-button" onClick={onOpen}>
      <div className="section-label">
        <span>
          <Activity size={14} />
          GLUCOSE
        </span>
        <ArrowUpRight size={16} />
      </div>
      <>
        <div
          className="glucose-value"
          data-testid="dashboard-glucose"
          data-observed-at={data.current.provenance.observedAt ?? ""}
          data-retrieved-at={data.current.provenance.retrievedAt ?? ""}
          data-freshness={healthStatus(data.current)}
          data-source={data.current.provenance.sourceId}
          data-demo={data.current.provenance.demo}
        >
          {blocked || !currentReady ? "—" : data.current.value}
          <span>
            <TrendArrows trend={trend} />
            <small>{data.current.unit}</small>
          </span>
        </div>
        <div
          className={`metric-caption ${healthStatus(data.current) === "stale" ? "caution" : ""}`}
        >
          <i />
          {healthStatus(data.current) === "unavailable"
            ? "Source unavailable"
            : !currentReady
              ? "No measurement"
              : healthStatus(data.current) === "stale"
                ? "Reading is stale"
                : data.current.provenance.demo
                  ? "Demo reading"
                  : data.current.provenance.kind === "archive"
                    ? "Archived reading"
                    : "Recorded reading"}
        </div>
        {!blocked && hasMeasurement(data.glucose) && spark && (
          <Sparkline series={data.glucose} />
        )}
        <div className="spark-axis">
          <span>
            {shortTime(
              chartModel(data.glucose, "all", true).samples[0]?.timestamp,
            )}
          </span>
          <span>{shortTime(data.glucose.samples.at(-1)?.timestamp)}</span>
        </div>
        <p className="quiet-copy">
          {trend
            ? `${trendWords(trend)} · ${trendChange(trend, data.current.unit)}`
            : !blocked && healthStatus(data.current) === "available"
              ? "No reading 20 minutes earlier, so no arrow"
              : "Trend unavailable"}
        </p>
        <GlucoseSummary
          stale={state === "stale"}
          history={data.glucoseHistory}
          summary={data.glucoseSummary}
          provider={glucoseProvider(data)}
          readData={readData}
          unavailable={blocked}
        />
        <div className="tile-footer">
          <span>{healthCaption(data.current)}</span>
          <span>
            View day <ChevronRight size={12} />
          </span>
        </div>
      </>
    </button>
  );
}

// The main corner with neither a dashboard nor a glucose source: one quiet
// line saying a dashboard can live here, never a tile that looks broken. A
// glucose part with no glucose source says the same.
export function MainHint({
  dashboards,
  onSources,
}: {
  // Whether any dashboard is saved yet.
  dashboards: boolean;
  onSources: () => void;
}) {
  return (
    <>
      <LayoutDashboard size={18} aria-hidden="true" />
      <p>
        {dashboards
          ? "A dashboard can live here: choose one for this spot in "
          : "A dashboard can live here. Ask your AI to build one, then choose it for this spot in "}
        <button onClick={onSources}>Sources & settings</button>.
      </p>
    </>
  );
}
