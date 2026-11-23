import { Activity } from "lucide-react";
import { hasMeasurement, healthStatus } from "./health";
import type { ObservatoryData } from "./model";
import { launcherGlucoseHistory } from "./launcherGlucoseHistory";
import { usePreviewFit } from "./usePreviewFit";

// A compact surface of the built-in tile, using its actual health/provenance
// contract. It deliberately does not squeeze the full health panel into a card.
export function LauncherGlucose({
  data,
  now,
  blocked,
}: {
  data: ObservatoryData;
  now: number;
  blocked: boolean;
}) {
  const fitted = usePreviewFit<HTMLElement>(["chart", "coverage", "age", "tir"]);
  const history = launcherGlucoseHistory(
    blocked ? null : data.glucoseHistory,
    now,
    data.current.unit,
  );
  const reading = data.current;
  const measuredStatus = blocked ? "unavailable" : healthStatus(reading, now);
  const status =
    reading.value === null && measuredStatus === "available"
      ? "missing"
      : measuredStatus;
  const available =
    !blocked && hasMeasurement(reading, now) && reading.value !== null;
  const observed = Date.parse(reading.provenance.observedAt ?? "");
  const minutes = Math.max(0, Math.floor((now - observed) / 60_000));
  const age = !Number.isFinite(observed)
    ? "Reading time unavailable"
    : minutes < 1
      ? "Less than a minute ago"
      : minutes < 60
        ? `${minutes} min ago`
        : minutes < 1440
          ? `${Math.floor(minutes / 60)} hr ${minutes % 60} min ago`
          : `${Math.floor(minutes / 1440)} days ago`;
  const label =
    status === "stale"
      ? "Stale reading"
      : status === "missing"
        ? "No reading"
        : status === "unavailable"
          ? "Source unavailable"
          : reading.provenance.kind === "archive"
            ? "Archived reading"
            : "Latest reading";
  return (
    <article
      ref={fitted}
      className={`launcher-summary launcher-glucose ${status}`}
      data-preview-ready="true"
      data-builtin="glucose"
      data-reading-status={status}
    >
      <header>
        <Activity size={14} aria-hidden="true" />
        <h1>Glucose</h1>
      </header>
      <span className="preview-label" role="status">
        {label}
      </span>
      <div className="glucose-preview-reading">
        <strong>
          {available
            ? new Intl.NumberFormat(undefined, {
                maximumFractionDigits: 2,
              }).format(reading.value!)
            : "—"}
        </strong>
        {available && <span>{reading.unit}</span>}
      </div>
      <time
        className="glucose-preview-age"
        dateTime={
          available ? (reading.provenance.observedAt ?? undefined) : undefined
        }
        title={
          available ? (reading.provenance.observedAt ?? undefined) : undefined
        }
      >
        {available ? age : "Reading unavailable"}
      </time>
      <GlucoseChart history={history} />
      <p
        className="glucose-preview-tir"
        title="Share of classified cadence slots within range, among observed slots in the trailing 24 hours. Missing slots are unknown, not in range. At least 70% coverage required."
      >
        <strong>
          {history.within === null ? "—" : `${Math.round(history.within)}%`}
        </strong>
        <span>24h in range</span>
      </p>
      <footer
        title={
          history.reason ??
          "Coverage is classified cadence slots / expected slots over the trailing 24 hours."
        }
      >
        {/* With no history at all its reason says so alone ("History unavailable"), not after
            "No history"; a coverage figure comes before its reason or range. */}
        {history.expectedSlots
          ? `${Math.floor(history.coverage)}% coverage · `
          : ""}
        <span className="glucose-preview-why">
          {history.reason
            ? history.reason === "Needs 70% coverage"
              ? "need 70%"
              : history.reason
            : `${history.archive ? "Archive · " : ""}${history.range.map((v) => new Intl.NumberFormat(undefined, { maximumFractionDigits: history.unit === "mmol/L" ? 2 : 0 }).format(v)).join("–")}`}
        </span>
      </footer>
    </article>
  );
}

function GlucoseChart({
  history,
}: {
  history: ReturnType<typeof launcherGlucoseHistory>;
}) {
  if (!history.points.length)
    return (
      <div className="glucose-preview-chart empty" role="status">
        Graph unavailable
      </div>
    );
  const values = history.points.map((p) => p.value);
  const lo = Math.max(0, Math.min(...values, history.range[0]) * 0.85);
  const hi = Math.max(...values, history.range[1]) * 1.08;
  const x = (t: number) =>
    ((t - history.start) / (history.end - history.start)) * 136 + 2;
  const y = (v: number) => 32 - ((v - lo) / (hi - lo)) * 28;
  return (
    <svg
      className="glucose-preview-chart"
      viewBox="0 0 140 36"
      preserveAspectRatio="none"
      role="img"
      aria-label={`Trailing 24 hours of measured glucose in ${history.unit}; gaps are blank. ${Math.floor(history.coverage)}% cadence coverage. Shaded range ${history.range.map((v) => v.toFixed(history.unit === "mmol/L" ? 2 : 0)).join(" to ")}.`}
    >
      <rect
        className="glucose-range-band"
        x="2"
        y={y(history.range[1])}
        width="136"
        height={y(history.range[0]) - y(history.range[1])}
      />
      {history.segments.map((points, i) => (
        <g key={i}>
          {points.length > 1 && (
            <polyline
              points={points.map((p) => `${x(p.time)},${y(p.value)}`).join(" ")}
            />
          )}
          {points.map((p) => (
            <circle key={p.time} cx={x(p.time)} cy={y(p.value)} r="0.8" />
          ))}
        </g>
      ))}
    </svg>
  );
}
