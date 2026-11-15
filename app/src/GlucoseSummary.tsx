import { useMemo } from "react";
import type { ReactNode } from "react";
import type { GlucoseAggregate, GlucoseHistory } from "./model";
import type { Bands } from "./glucose";
import { gmiFromDays } from "./glucose";
import { pct, rangeWindow, useGlucoseDays } from "./glucoseView";
import type { ReadData } from "./panels/readings";
import "./GlucoseSummary.css";

// A ring of the five bands, lows rising up the left side from the bottom,
// in range across the top and highs coming down the right. An inner arc shows
// time in tight range (70–140), starting where in range starts.
export function RangeRing({
  bands,
  tight = null,
  label,
  className = "",
  children,
}: {
  bands: Bands;
  tight?: number | null;
  // What the ring says, for screen readers; without one it is decorative.
  label?: string;
  className?: string;
  children?: ReactNode;
}) {
  const r = 43,
    c = 2 * Math.PI * r,
    inner = 33,
    ci = 2 * Math.PI * inner;
  const order = ["veryLow", "low", "inRange", "high", "veryHigh"] as const;
  const shown = order.filter((k) => bands[k] > 0);
  const gap = shown.length > 1 ? 1.1 : 0;
  let at = 0;
  const segments = order.map((key) => {
    const share = Math.max(0, bands[key]) / 100;
    // Tiny bands stay visible as a sliver; the legend carries the number.
    const length = share > 0 ? Math.max(1.4, share * c - gap) : 0;
    const segment = { key, start: at, length };
    at += share * c;
    return segment;
  });
  const tightStart =
    ((Math.max(0, bands.veryLow) + Math.max(0, bands.low)) / 100) * ci;
  return (
    <div
      className={`range-ring ${className}`}
      {...(label
        ? { role: "img", "aria-label": label }
        : { "aria-hidden": true })}
    >
      <svg viewBox="0 0 100 100" aria-hidden="true">
        <g transform="rotate(90 50 50)">
          <circle className="ring-track" cx="50" cy="50" r={r} />
          {segments.map(
            (s) =>
              s.length > 0 && (
                <circle
                  key={s.key}
                  className={`ring-band ${s.key}`}
                  cx="50"
                  cy="50"
                  r={r}
                  strokeDasharray={`${s.length} ${c - s.length}`}
                  strokeDashoffset={-(s.start + gap / 2)}
                />
              ),
          )}
          {tight !== null && (
            <>
              <circle className="ring-tight-track" cx="50" cy="50" r={inner} />
              {tight > 0 && (
                <circle
                  className="ring-tight"
                  cx="50"
                  cy="50"
                  r={inner}
                  strokeDasharray={`${(tight / 100) * ci} ${ci}`}
                  strokeDashoffset={-tightStart}
                />
              )}
            </>
          )}
        </g>
      </svg>
      {children && <div className="range-ring-center">{children}</div>}
    </div>
  );
}

// The ambient tile's glance: the ring, time in range and the 90-day GMI.
export function GlucoseSummary({
  history,
  summary,
  provider,
  readData,
  unavailable = false,
  stale = false,
}: {
  history: GlucoseHistory | null;
  // Source totals for 14 days when the history itself is shorter.
  summary?: GlucoseAggregate | null;
  // Whose glucose data star holds the longer record (glucoseProvider).
  provider?: string | null;
  readData?: ReadData;
  unavailable?: boolean;
  stale?: boolean;
}) {
  const { window, gmi14 } = useMemo(
    () => rangeWindow(history, summary, stale),
    [history, summary, stale],
  );
  const days = useGlucoseDays(unavailable ? undefined : readData, provider);
  const long = useMemo(() => (days ? gmiFromDays(days, 90) : null), [days]);
  const reading = !unavailable && !!readData && days === undefined;
  const gmi = unavailable
    ? null
    : long
      ? {
          value: long.gmi,
          note: long.partial ? "90 days · partial" : "90-day estimate",
        }
      : !reading && gmi14 !== null
        ? { value: gmi14, note: "14-day estimate" }
        : null;
  const shown = !unavailable && window;
  return (
    <div className="glucose-history compact" aria-label="Glucose summary">
      <p className="history-period">
        {shown
          ? `${window.title} · ${window.tag}${window.stale ? " · STALE" : ""}`
          : "Glucose history"}
      </p>
      <div className="glance">
        {shown && (
          // The tile's text already says the time in range; the ring is a picture of it.
          <RangeRing
            className="glance-ring"
            bands={window.bands}
            tight={window.tight}
          >
            <strong>{pct(window.within)}</strong>
            <span>in range</span>
          </RangeRing>
        )}
        <div className="history-metrics">
          <div>
            <span>Time in range</span>
            <strong>{shown ? pct(window.within) : "—"}</strong>
            <small>70–180 mg/dL</small>
          </div>
          <div>
            <span>GMI</span>
            <strong>{gmi ? `${gmi.value.toFixed(1)}%` : "—"}</strong>
            <small>
              {gmi?.note ?? (reading ? "Reading 90 days…" : "Unavailable")}
            </small>
          </div>
        </div>
      </div>
    </div>
  );
}
