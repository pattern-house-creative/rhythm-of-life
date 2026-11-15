import { useId } from "react";
import type { MeasurementSeries } from "./model";
import { chartModel } from "./chartModel";

// The tile's last three hours: a soft line with faint 70 and 180 guides and a
// dot on the latest reading.
export function Sparkline({ series }: { series: MeasurementSeries }) {
  const id = useId().replace(/:/g, "");
  const { samples, domain, reference, label } = chartModel(series, "all", true);
  const y = (v: number) =>
    92 - ((v - domain[0]) / (domain[1] - domain[0])) * 84;
  const points = samples
    .map(
      (s, i) =>
        `${samples.length === 1 ? 150 : (i / (samples.length - 1)) * 300},${y(s.value)}`,
    )
    .join(" ");
  const last = samples.at(-1);
  return (
    <div className="sparkline" role="img" aria-label={label}>
      <svg
        viewBox="0 0 300 100"
        width="100%"
        height="100%"
        preserveAspectRatio="none"
        aria-hidden="true"
      >
        <defs>
          <linearGradient id={id} x1="0" y1="0" x2="0" y2="1">
            <stop stopColor="#75d8b9" stopOpacity=".22" />
            <stop offset="1" stopColor="#75d8b9" stopOpacity="0" />
          </linearGradient>
        </defs>
        {reference.map((v) => (
          <line
            key={v}
            className="spark-guide"
            x1="0"
            x2="300"
            y1={y(v)}
            y2={y(v)}
            vectorEffect="non-scaling-stroke"
          />
        ))}
        {samples.length > 0 && (
          <>
            <polygon points={`0,100 ${points} 300,100`} fill={`url(#${id})`} />
            <polyline
              points={points}
              fill="none"
              stroke="#7bdcbb"
              strokeWidth="1.8"
              strokeLinejoin="round"
              strokeLinecap="round"
              vectorEffect="non-scaling-stroke"
            />
          </>
        )}
      </svg>
      {last && (
        <i
          className="spark-dot"
          style={{
            top: `${y(last.value)}%`,
            left: samples.length === 1 ? "50%" : "100%",
          }}
        />
      )}
    </div>
  );
}
