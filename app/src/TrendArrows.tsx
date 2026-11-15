import type { GlucoseTrend } from "./trend";
import { trendWords } from "./trend";
import "./GlucoseSummary.css";

// Medtronic-style trend: one to three slim chevrons stacked in the direction of
// change. Nothing is drawn while glucose is steady or the trend is unknown.
export function TrendArrows({
  trend,
  size,
  className = "",
}: {
  trend: GlucoseTrend | null;
  // Height of the stack of three, in pixels (else set by CSS); fewer chevrons
  // sit centered in it.
  size?: number;
  className?: string;
}) {
  if (!trend?.direction) return null;
  const n = trend.arrows;
  const up = trend.direction === "up";
  // Each chevron is 10 wide and 4.5 tall, 5.5 apart, in a 12 × 18 box.
  const step = 5.5,
    tall = 4.5,
    top = (18 - (tall + step * (n - 1))) / 2;
  const chevrons = Array.from({ length: n }, (_, i) => {
    const y = top + i * step;
    return up
      ? `1,${y + tall} 6,${y} 11,${y + tall}`
      : `1,${y} 6,${y + tall} 11,${y}`;
  });
  return (
    <span
      className={`trend-arrows ${up ? "up" : "down"} n${n} ${className}`}
      role="img"
      aria-label={`${trendWords(trend)}: ${n} ${up ? "up" : "down"} arrow${n > 1 ? "s" : ""}, ${Math.abs(trend.rate).toFixed(1)} mg/dL per minute`}
      style={size ? { height: size, width: (size * 12) / 18 } : undefined}
    >
      <svg viewBox="0 0 12 18" width="100%" height="100%" aria-hidden="true">
        {(up ? [...chevrons].reverse() : chevrons).map((points, i) => (
          <polyline
            key={points}
            points={points}
            style={{ animationDelay: `${i * 90}ms` }}
          />
        ))}
      </svg>
    </span>
  );
}
