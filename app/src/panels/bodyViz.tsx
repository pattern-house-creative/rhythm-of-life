import { motion } from "motion/react";
import type { GlucosePoint } from "./garmin";
import { arcPath } from "./arcs";
import "./bodyViz.css";

// Small hand-drawn pieces shared by the sleep, recovery and training panels.

// An arc that draws itself in.
export function Arc({
  cx,
  cy,
  r,
  from,
  to,
  className,
  stroke,
  width,
  delay = 0,
}: {
  cx: number;
  cy: number;
  r: number;
  from: number;
  to: number;
  className?: string;
  stroke?: string;
  width: number;
  delay?: number;
}) {
  if (!(to - from > 0.2)) return null;
  return (
    <motion.path
      d={arcPath(cx, cy, r, from, to)}
      className={className}
      stroke={stroke}
      strokeWidth={width}
      strokeLinecap="round"
      fill="none"
      initial={{ pathLength: 0, opacity: 0 }}
      animate={{ pathLength: 1, opacity: 1 }}
      transition={{ duration: 0.9, delay, ease: [0.2, 0.7, 0.2, 1] }}
    />
  );
}

// Glucose through a stretch of time: the 70–180 band, the line coloured by
// range, and an optional highlighted span (a workout).
export function GlucoseLine({
  points,
  start,
  end,
  span,
  label,
  height = 56,
}: {
  points: GlucosePoint[];
  start: number;
  end: number;
  span?: [number, number];
  label: string;
  height?: number;
}) {
  const width = 320,
    pad = 4;
  // The 70–180 band always shows, with room for the readings around it.
  const top = Math.max(200, ...points.map((p) => p.value + 12)),
    bottom = Math.min(55, ...points.map((p) => p.value - 8));
  const x = (t: number) =>
    pad + ((t - start) / Math.max(1, end - start)) * (width - pad * 2);
  const y = (v: number) =>
    pad + (1 - (v - bottom) / (top - bottom)) * (height - pad * 2);
  const range = (v: number) => (v < 70 ? "low" : v > 180 ? "high" : "in");
  // Split the line where it changes range, or where readings are missing.
  const gap = 20 * 60000;
  const runs: { kind: string; pts: GlucosePoint[] }[] = [];
  for (const [i, p] of points.entries()) {
    const prev = points[i - 1];
    const kind = range(p.value);
    const last = runs.at(-1);
    if (last && prev && p.at - prev.at <= gap) {
      if (last.kind === kind) last.pts.push(p);
      else runs.push({ kind, pts: [prev, p] });
    } else runs.push({ kind, pts: [p] });
  }
  return (
    <svg
      className="glucose-line"
      viewBox={`0 0 ${width} ${height}`}
      role="img"
      aria-label={label}
    >
      <rect
        className="glucose-band"
        x={pad}
        width={width - pad * 2}
        y={y(180)}
        height={y(70) - y(180)}
        rx={4}
      />
      {span && (
        <rect
          className="glucose-span"
          x={x(span[0])}
          width={Math.max(2, x(span[1]) - x(span[0]))}
          y={0}
          height={height}
          rx={3}
        />
      )}
      {runs.map((run, i) =>
        run.pts.length > 1 ? (
          <polyline
            key={i}
            className={`glucose-run ${run.kind}`}
            points={run.pts
              .map((p) => `${x(p.at).toFixed(1)},${y(p.value).toFixed(1)}`)
              .join(" ")}
          />
        ) : (
          <circle
            key={i}
            className={`glucose-dot ${run.kind}`}
            cx={x(run.pts[0].at)}
            cy={y(run.pts[0].value)}
            r={1.8}
          />
        ),
      )}
    </svg>
  );
}

// Fourteen days of one reading: dots joined by a soft line, an optional band
// (a baseline) and an optional average line, with one day in focus.
export function Trend({
  dates,
  values,
  focus,
  band,
  average,
  label,
  tone = "lavender",
  minSpan = 4,
  wide = false,
}: {
  dates: string[];
  values: Map<string, number>;
  focus: string | null;
  band?: { low: Map<string, number>; high: Map<string, number> } | null;
  average?: Map<string, number> | null;
  label: string;
  tone?: "lavender" | "rose" | "mint";
  // The smallest range the chart spans, so a quiet fortnight looks quiet.
  minSpan?: number;
  // A full-width card draws a longer chart at the same height.
  wide?: boolean;
}) {
  const width = wide ? 620 : 300,
    height = 74,
    padX = 8,
    padY = 10;
  const all = [
    ...dates.map((d) => values.get(d)),
    ...dates.flatMap((d) => [
      band?.low.get(d),
      band?.high.get(d),
      average?.get(d),
    ]),
  ].filter((v): v is number => v != null && Number.isFinite(v));
  if (!all.length) return null;
  const lo = Math.min(...all),
    hi = Math.max(...all);
  const span = Math.max(minSpan, hi - lo);
  const middle = (lo + hi) / 2;
  const bottom = middle - span * 0.68,
    top = middle + span * 0.68;
  const x = (i: number) =>
    padX +
    (dates.length > 1 ? i / (dates.length - 1) : 0.5) * (width - padX * 2);
  const y = (v: number) =>
    padY + (1 - (v - bottom) / (top - bottom)) * (height - padY * 2);
  const series = (map: Map<string, number>) =>
    dates
      .map((d, i) => (map.has(d) ? [x(i), y(map.get(d)!)] : null))
      .filter((p): p is number[] => p !== null);
  const line = series(values);
  const bandTop = band ? series(band.high) : [],
    bandBottom = band ? series(band.low) : [];
  return (
    <svg
      className={`trend ${tone}`}
      viewBox={`0 0 ${width} ${height}`}
      role="img"
      aria-label={label}
    >
      {bandTop.length > 1 && bandBottom.length > 1 && (
        <polygon
          className="trend-band"
          points={[...bandTop, ...bandBottom.slice().reverse()]
            .map((p) => p.join(","))
            .join(" ")}
        />
      )}
      {average && series(average).length > 1 && (
        <polyline
          className="trend-average"
          points={series(average)
            .map((p) => p.join(","))
            .join(" ")}
        />
      )}
      {line.length > 1 && (
        <polyline
          className="trend-line"
          points={line.map((p) => p.join(",")).join(" ")}
        />
      )}
      {dates.map((d, i) =>
        values.has(d) ? (
          <circle
            key={d}
            className={d === focus ? "trend-focus" : "trend-dot"}
            cx={x(i)}
            cy={y(values.get(d)!)}
            r={d === focus ? 5 : 2.4}
          />
        ) : null,
      )}
    </svg>
  );
}
