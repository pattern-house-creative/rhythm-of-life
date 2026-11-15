import { useId } from "react";
import type { GraphSeries } from "./knowledge";
import type { Day, Meaning } from "./readingsModel";
import {
  byDate,
  dailyValue,
  niceTicks,
  show,
  shortDate,
  text,
} from "./readingsModel";
import { slotAt, useScrub, useWidth } from "./scrub";

// One measurement's days as a small chart you can read by touch: a band
// from each day's low to high under its average for several readings a day,
// stems for things that happen some days, and a soft line otherwise. Opened
// up, it gains axes.
export function DayChart({
  series,
  meaning: m,
  dates,
  focus,
  setFocus,
  large = false,
  label,
  valueText,
  onActivate,
}: {
  series: GraphSeries;
  meaning: Meaning;
  dates: string[];
  focus: string | null;
  setFocus: (date: string | null) => void;
  large?: boolean;
  label: string;
  valueText: string;
  onActivate?: () => void;
}) {
  const [ref, width] = useWidth<SVGSVGElement>(300);
  const fade = `fade-${useId().replace(/[^\w-]/g, "")}`;
  const height = large ? 210 : 64;
  const pad = large
    ? { left: 50, right: 10, top: 14, bottom: 26 }
    : { left: 2, right: 2, top: 8, bottom: 6 };
  const days = byDate(series);
  const slots = dates.map((date) => ({ date, day: days.get(date) ?? null }));
  const present = slots.filter(
    (s): s is { date: string; day: Day } => s.day !== null,
  );
  const n = dates.length;
  const inner = Math.max(1, width - pad.left - pad.right);
  const x = (i: number) => pad.left + ((i + 0.5) * inner) / n;
  const band =
    !!m.band && present.some((s) => s.day.count > 1 && s.day.max > s.day.min);
  // Workouts and other some-days readings stand apart instead of joining up.
  const sparse = !band && (!!m.apart || present.length < n * 0.6);
  const value = (d: Day) => dailyValue(d, m);
  const all = present.flatMap((s) =>
    band ? [s.day.min, s.day.max] : [value(s.day)],
  );
  const zero = m.daily === "total" || series.unit === "s";
  // Totals and durations stand on zero; levels use their own range (and the
  // target range, when there is one, so it is always in view).
  const target = m.target && !zero ? m.target : [];
  let lo = all.length ? (zero ? 0 : Math.min(...all, ...target)) : 0;
  let hi = all.length ? Math.max(...all, ...target) : 1;
  if (hi === lo) {
    hi += Math.abs(hi) * 0.1 || 1;
    if (!zero) lo -= Math.abs(lo) * 0.1 || 1;
  }
  const room = (hi - lo) * 0.08;
  if (!zero) lo -= room;
  hi += room;
  const y = (v: number) =>
    pad.top + (height - pad.top - pad.bottom) * (1 - (v - lo) / (hi - lo));
  const baseline = y(lo);
  // Runs of consecutive days with readings, so a gap reads as a gap.
  const runs: { i: number; day: Day }[][] = [];
  slots.forEach((s, i) => {
    if (!s.day) return;
    const run = runs.at(-1);
    if (run && run.at(-1)!.i === i - 1) run.push({ i, day: s.day });
    else runs.push([{ i, day: s.day }]);
  });
  const scrub = useScrub({
    dates,
    readable: (date) => days.has(date),
    focus,
    setFocus,
    pick: (clientX) => {
      const i = slotAt(ref, clientX, pad.left, pad.right, n);
      return i < 0 ? null : dates[i];
    },
    onActivate,
  });
  const at = focus ? dates.indexOf(focus) : -1;
  const focused = at >= 0 ? slots[at].day : null;
  const dots = n <= 45;
  const ticks = large
    ? niceTicks(lo + (zero ? 0 : room), hi - room, 4, series.unit)
    : [];
  const xTicks = large ? dateTicks(n, width < 420 ? 3 : 5) : [];
  // Axis values drop needless decimals ("20 U", not "20.0 U") and long
  // units, which the card already names.
  const tick = (v: number) => {
    const shown = show(v, series.unit, m);
    return text(shown.unit.length > 3 ? { ...shown, unit: "" } : shown).replace(
      /(\d)\.0+(?!\d)/,
      "$1",
    );
  };
  return (
    <svg
      ref={ref}
      className={`day-chart${large ? " large" : ""}${at >= 0 ? " focused" : ""}`}
      width="100%"
      height={height}
      viewBox={`0 0 ${width} ${height}`}
      role="slider"
      tabIndex={0}
      aria-label={label}
      aria-valuemin={0}
      aria-valuemax={Math.max(0, n - 1)}
      aria-valuenow={at >= 0 ? at : n - 1}
      aria-valuetext={valueText}
      {...scrub}
    >
      <defs>
        <linearGradient id={fade} x1="0" x2="0" y1="0" y2="1">
          <stop offset="0" className="fade-top" />
          <stop offset="1" className="fade-bottom" />
        </linearGradient>
      </defs>
      {m.target && !zero && (
        <rect
          className="chart-target"
          x={pad.left}
          width={inner}
          y={y(m.target[1])}
          height={Math.max(0, y(m.target[0]) - y(m.target[1]))}
          rx={3}
        />
      )}
      {ticks.map((t) => (
        <g key={t} className="chart-tick">
          <line x1={pad.left} x2={width - pad.right} y1={y(t)} y2={y(t)} />
          <text x={pad.left - 8} y={y(t)} dy="0.32em" textAnchor="end">
            {tick(t)}
          </text>
        </g>
      ))}
      {xTicks.map((i) => (
        <text
          key={i}
          className="chart-date"
          x={x(i)}
          y={height - 6}
          textAnchor={i === 0 ? "start" : i === n - 1 ? "end" : "middle"}
          dx={i === 0 ? -4 : i === n - 1 ? 4 : 0}
        >
          {shortDate(dates[i])}
        </text>
      ))}
      {band &&
        runs.map((run) =>
          run.length > 1 ? (
            <polygon
              key={`b${run[0].i}`}
              className="chart-band"
              points={[
                ...run.map((p) => `${x(p.i)},${y(p.day.max)}`),
                ...run.map((p) => `${x(p.i)},${y(p.day.min)}`).reverse(),
              ].join(" ")}
            />
          ) : (
            <line
              key={`b${run[0].i}`}
              className="chart-band-stroke"
              x1={x(run[0].i)}
              x2={x(run[0].i)}
              y1={y(run[0].day.max)}
              y2={y(run[0].day.min)}
            />
          ),
        )}
      {!band &&
        !sparse &&
        runs.map(
          (run) =>
            run.length > 1 && (
              <polygon
                key={`a${run[0].i}`}
                className="chart-area"
                fill={`url(#${fade})`}
                points={[
                  `${x(run[0].i)},${baseline}`,
                  ...run.map((p) => `${x(p.i)},${y(value(p.day))}`),
                  `${x(run.at(-1)!.i)},${baseline}`,
                ].join(" ")}
              />
            ),
        )}
      {sparse &&
        zero &&
        present.map((s) => {
          const i = dates.indexOf(s.date);
          return (
            <line
              key={`s${s.date}`}
              className="chart-stem"
              x1={x(i)}
              x2={x(i)}
              y1={baseline}
              y2={y(value(s.day))}
            />
          );
        })}
      {!sparse &&
        runs.map((run) =>
          run.length > 1 ? (
            <polyline
              key={`l${run[0].i}`}
              className="chart-line"
              points={run.map((p) => `${x(p.i)},${y(value(p.day))}`).join(" ")}
            />
          ) : null,
        )}
      {(dots || sparse) &&
        present.map((s) => {
          const i = dates.indexOf(s.date);
          return (
            <circle
              key={`d${s.date}`}
              className="chart-dot"
              cx={x(i)}
              cy={y(value(s.day))}
              r={sparse ? (large ? 3.5 : 2.6) : large ? 2.4 : 1.6}
            />
          );
        })}
      {at >= 0 && (
        <g className="chart-cursor">
          <line
            x1={x(at)}
            x2={x(at)}
            y1={pad.top - 4}
            y2={height - pad.bottom}
          />
          {focused && band && (
            <>
              <line
                className="chart-cursor-range"
                x1={x(at)}
                x2={x(at)}
                y1={y(focused.max)}
                y2={y(focused.min)}
              />
              <circle cx={x(at)} cy={y(focused.max)} r={2} className="end" />
              <circle cx={x(at)} cy={y(focused.min)} r={2} className="end" />
            </>
          )}
          {focused && (
            <circle cx={x(at)} cy={y(value(focused))} r={large ? 5 : 4} />
          )}
        </g>
      )}
      {/* The whole drawing is the target, so a finger anywhere reads a day. */}
      <rect className="chart-hit" width={width} height={height} />
    </svg>
  );
}

// Evenly spread date labels: always the first and last day.
function dateTicks(n: number, count: number) {
  if (n <= 1) return [0];
  const out = new Set<number>();
  for (let k = 0; k < count; k++)
    out.add(Math.round((k * (n - 1)) / (count - 1)));
  return [...out];
}
