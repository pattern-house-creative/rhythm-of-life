import { useId } from "react";
import type { ReactNode } from "react";
import { stamp } from "./health";
import { chartModel, hourLabel, hourTicks, overnightRuns } from "./chartModel";
import {
  Area,
  AreaChart,
  CartesianGrid,
  ReferenceArea,
  ReferenceLine,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";
import type { MeasurementSeries } from "./model";

// The line takes the band color of where it is: amber above range, mint in
// range, rose below. Stops are placed within the line's own height.
function bandStops(min: number, max: number, reference: [number, number]) {
  const at = (v: number) => (max - v) / (max - min);
  const high = "#c6ad7c",
    mid = "#7bdcbb",
    low = "#e3a3ab";
  if (max - min < 1)
    return [
      {
        offset: 0,
        color: max > reference[1] ? high : max < reference[0] ? low : mid,
      },
    ];
  const stops: { offset: number; color: string }[] = [];
  const top = at(reference[1]),
    bottom = at(reference[0]);
  stops.push({
    offset: 0,
    color: max > reference[1] ? high : max < reference[0] ? low : mid,
  });
  if (top > 0 && top < 1)
    stops.push({ offset: top, color: high }, { offset: top, color: mid });
  if (bottom > 0 && bottom < 1)
    stops.push({ offset: bottom, color: mid }, { offset: bottom, color: low });
  stops.push({
    offset: 1,
    color: min < reference[0] ? low : min > reference[1] ? high : mid,
  });
  return stops;
}

export function HealthChart({
  series,
  small = false,
  segment = "all",
  children,
}: {
  series: MeasurementSeries;
  small?: boolean;
  segment?: string;
  // Shown between the chart and its table of values.
  children?: ReactNode;
}) {
  const { samples, domain, reference, label, min, max } = chartModel(
    series,
    segment,
    small,
  );
  const id = useId().replace(/:/g, "");
  const plot = samples.flatMap((sample, index) => {
    const time = Date.parse(sample.timestamp),
      previous = samples[index - 1];
    return !series.provenance.demo &&
      previous &&
      time - Date.parse(previous.timestamp) > 600000
      ? [
          { time: Date.parse(previous.timestamp) + 1, value: null },
          { ...sample, time },
        ]
      : [{ ...sample, time }];
  });
  if (!samples.length)
    return (
      <p role="status" className="chart-empty">
        No glucose readings available for this interval.
      </p>
    );
  const times = samples.map((s) => Date.parse(s.timestamp));
  const ticks = hourTicks(times[0], times.at(-1)!);
  const nights = segment === "all" ? overnightRuns(samples) : [];
  const stops = bandStops(min ?? 0, max ?? 0, reference);
  return (
    <>
      <div
        className={small ? "sparkline" : "health-chart"}
        role="img"
        aria-label={label}
      >
        <ResponsiveContainer width="100%" height="100%">
          <AreaChart
            data={plot}
            accessibilityLayer
            margin={
              small
                ? { top: 8, right: 0, bottom: 0, left: 0 }
                : { top: 14, right: 12, bottom: 0, left: -22 }
            }
          >
            <defs>
              <linearGradient id={`${id}-fill`} x1="0" y1="0" x2="0" y2="1">
                <stop offset="0%" stopColor="#75d8b9" stopOpacity=".2" />
                <stop offset="100%" stopColor="#75d8b9" stopOpacity="0" />
              </linearGradient>
              <linearGradient id={`${id}-line`} x1="0" y1="0" x2="0" y2="1">
                {stops.map((s, i) => (
                  <stop key={i} offset={s.offset} stopColor={s.color} />
                ))}
              </linearGradient>
            </defs>
            {!small && (
              <>
                {nights.map(([from, to]) => (
                  <ReferenceArea
                    key={from}
                    x1={from}
                    x2={to}
                    fill="#8d9ccf"
                    fillOpacity={0.035}
                    ifOverflow="hidden"
                  />
                ))}
                <CartesianGrid
                  vertical={false}
                  stroke="#26343c"
                  strokeDasharray="3 7"
                />
                <XAxis
                  dataKey="time"
                  type="number"
                  scale="time"
                  domain={["dataMin", "dataMax"]}
                  ticks={ticks}
                  tickFormatter={(t) => hourLabel(Number(t))}
                  tick={{ fill: "#8fa9b7", fontSize: 10 }}
                  tickLine={false}
                  axisLine={false}
                />
                <YAxis
                  domain={domain}
                  ticks={[
                    ...new Set([domain[0], ...reference, domain[1]]),
                  ].sort((a, b) => a - b)}
                  tick={{ fill: "#8fa9b7", fontSize: 10 }}
                  tickLine={false}
                  axisLine={false}
                />
                <ReferenceArea
                  y1={reference[0]}
                  y2={reference[1]}
                  fill="#79dbb5"
                  fillOpacity={0.035}
                />
                <ReferenceLine
                  y={reference[1]}
                  stroke="#c6ad7c"
                  strokeOpacity={0.55}
                  strokeDasharray="3 6"
                />
                <ReferenceLine
                  y={reference[0]}
                  stroke="#e3a3ab"
                  strokeOpacity={0.55}
                  strokeDasharray="3 6"
                />
                <Tooltip
                  cursor={{ stroke: "#93b4c355" }}
                  contentStyle={{
                    background: "#17232a",
                    border: "1px solid #33454e",
                    borderRadius: 12,
                    color: "#edf3f3",
                    fontSize: 12,
                  }}
                  labelFormatter={(t) =>
                    stamp(new Date(Number(t)).toISOString())
                  }
                  formatter={(v) => [`${v} ${series.unit}`, "Glucose"]}
                />
              </>
            )}
            <Area
              type="monotone"
              dataKey="value"
              stroke={`url(#${id}-line)`}
              strokeWidth={small ? 1.8 : 2.2}
              strokeLinecap="round"
              fill={`url(#${id}-fill)`}
              isAnimationActive={false}
              activeDot={{ r: 3.5, fill: "#d7e5e9", stroke: "none" }}
            />
          </AreaChart>
        </ResponsiveContainer>
      </div>
      {children}
      {!small && (
        <details className="chart-readings">
          <summary>Read chart values</summary>
          <p>{label}</p>
          <table>
            <caption>
              {series.provenance.demo ? "Fictional" : "Recorded"} glucose
              samples · {series.unit}
            </caption>
            <thead>
              <tr>
                <th scope="col">Recorded time</th>
                <th scope="col">Glucose</th>
              </tr>
            </thead>
            <tbody>
              {samples.map((s) => (
                <tr key={s.timestamp}>
                  <th scope="row">
                    <time dateTime={s.timestamp}>
                      {s.timestamp.slice(0, 10)} {s.timestamp.slice(11, 16)}{" "}
                      {s.timestamp.slice(19)}
                    </time>
                  </th>
                  <td>
                    {s.value} {series.unit}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </details>
      )}
    </>
  );
}
