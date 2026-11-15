import { instanceTimeZone } from "../instance";
import { lazy, Suspense, useMemo, useState } from "react";
import { Activity, ArrowUpRight } from "lucide-react";
import { hasMeasurement, healthCaption, healthStatus } from "../health";
import type { DemoState, GlucoseHistory, ObservatoryData } from "../model";
import { RangeRing } from "../GlucoseSummary";
import {
  bandLimits,
  bandNames,
  bandOrder,
  glucoseProvider,
  pct,
  perDay,
  rangeWindow,
  ringLabel,
  useGlucoseDays,
} from "../glucoseView";
import type { RangeWindow } from "../glucoseView";
import {
  bestDay,
  dayRange,
  gmiFromDays,
  lowEpisodes,
  readingStats,
  typicalDay,
} from "../glucose";
import { chartModel } from "../chartModel";
import { currentTrend, trendChange, trendWords } from "../trend";
import { TrendArrows } from "../TrendArrows";
import { Origin } from "./Origin";
import { sourceName } from "../graphView";
import type { ReadData } from "./readings";
import "./GlucosePanel.css";
const HealthChart = lazy(() =>
  import("../HealthChart").then((module) => ({ default: module.HealthChart })),
);

const fullDay = (date: string) =>
  new Date(`${date}T12:00:00Z`).toLocaleDateString("en-US", {
    weekday: "short",
    month: "short",
    day: "numeric",
    timeZone: "UTC",
  });

export function GlucosePanel({
  data,
  state,
  selectNode,
  readData,
}: {
  data: ObservatoryData;
  state: DemoState;
  selectNode: (id: string) => void;
  readData?: ReadData;
}) {
  const [segment, setSegment] = useState("all");
  const demo = data.current.provenance.demo;
  const currentReady =
    hasMeasurement(data.current) && data.current.value !== null;
  const trend = useMemo(() => currentTrend(data), [data]);
  const { window, reason, gmi14 } = useMemo(
    () =>
      rangeWindow(data.glucoseHistory, data.glucoseSummary, state === "stale"),
    [data.glucoseHistory, data.glucoseSummary, state],
  );
  const history =
    data.glucoseHistory && hasMeasurement(data.glucoseHistory)
      ? data.glucoseHistory
      : null;
  const day = useMemo(
    () => readingStats(chartModel(data.glucose, segment).samples),
    [data.glucose, segment],
  );
  const days = useGlucoseDays(readData, glucoseProvider(data));
  return (
    <div className="glucose-panel">
      <div className="detail-title">
        <div>
          <p className="eyebrow teal">HEALTH / GLUCOSE</p>
          <h2>Glucose</h2>
          <p>
            {demo
              ? "Illustrative glucose measurements"
              : "Glucose measurements"}{" "}
            · {healthCaption(data.current)}
          </p>
        </div>
        <Activity size={32} strokeWidth={1} />
      </div>

      <section className="glucose-now" aria-label="Current glucose">
        <div className="now-reading">
          <strong>{currentReady ? data.current.value : "—"}</strong>
          <TrendArrows trend={trend} size={38} />
          <span>{data.current.unit}</span>
        </div>
        <div className="now-words">
          <b>
            {currentReady
              ? (trendWords(trend) ?? "Trend unavailable")
              : "No current reading"}
          </b>
          <span>
            {trend
              ? trendChange(trend, data.current.unit)
              : !currentReady
                ? healthCaption(data.current)
                : healthStatus(data.current) === "available"
                  ? "No reading about 20 minutes before this one"
                  : "A stale reading gets no arrow"}
          </span>
          <small>Arrows show the 20-minute rate of change</small>
        </div>
      </section>

      <section className="glucose-day" aria-label="Glucose over the day">
        <div className="chart-toolbar">
          <span className="eyebrow">
            {demo ? "DEMO DAY" : "LAST 24 HOURS"} · {data.glucose.unit}
          </span>
          <div
            className="segmented"
            role="group"
            aria-label="Chart time segment"
          >
            {[
              ["all", "24 hours"],
              ["night", "Overnight"],
              ["day", "Daytime"],
            ].map(([key, label]) => (
              <button
                key={key}
                aria-pressed={segment === key}
                onClick={() => setSegment(key)}
              >
                {label}
              </button>
            ))}
          </div>
        </div>
        <Suspense
          fallback={
            <p role="status" className="chart-empty">
              Loading chart…
            </p>
          }
        >
          <HealthChart series={data.glucose} segment={segment}>
            {day && (
              <dl className="day-stats" aria-label="This stretch of the chart">
                <div>
                  <dt>
                    <i className="inRange" />
                    In range
                  </dt>
                  <dd>{pct(day.within)}</dd>
                </div>
                <div>
                  <dt>
                    <i className="tight" />
                    Tight
                  </dt>
                  <dd>{pct(day.tight)}</dd>
                </div>
                <div>
                  <dt>Average</dt>
                  <dd>
                    {Math.round(day.mean)}
                    <small>mg/dL</small>
                  </dd>
                </div>
                <div>
                  <dt>Low to high</dt>
                  <dd>
                    {day.min}–{day.max}
                  </dd>
                </div>
              </dl>
            )}
            <p className="chart-note">
              {demo
                ? "The selected demo day, one reading an hour."
                : "Readings from the last 24 hours; gaps stay empty."}{" "}
              Overnight is midnight to 7 AM, {instanceTimeZone()} time.
            </p>
          </HealthChart>
        </Suspense>
      </section>

      {window ? (
        <Ranges window={window} />
      ) : (
        <section className="glucose-ranges" aria-label="Time in ranges">
          <header className="glucose-head">
            <p className="eyebrow">TIME IN RANGES</p>
          </header>
          <p className="history-coverage">{reason}</p>
        </section>
      )}

      <Gmi
        days={days}
        reading={!!readData && days === undefined}
        fallback={gmi14}
      />

      {history && <TypicalDay history={history} />}

      {history && <Lows history={history} />}

      <details className="history-method">
        <summary>How these are calculated</summary>
        <p>
          <b>Trend arrows</b> show the change from the
          reading about 20 minutes earlier, per minute. Under 1 mg/dL a minute
          shows no arrow, 1–2 one, 2–3 two and 3 or more three, up or down.
          There is no arrow when the latest reading is over 20 minutes old or
          nothing was recorded 15–25 minutes before it.
        </p>
        <p>
          <b>Time in ranges</b> uses the consensus bands: very low below 54, low
          54–69, in range 70–180, high 181–250 and very high above 250 mg/dL.
          Tight range is 70–140. Shares are of recorded readings; missing time
          is left out, not filled in. Readings beyond the sensor’s range count
          in the outer bands but not in the mean. The usual goals are over 70%
          in range, under 4% below 70, under 1% below 54, under 25% above 180,
          under 5% above 250 and a CV of 36% or less.
        </p>
        <p>
          <b>GMI</b> = 3.31 + 0.02392 × mean sensor glucose (mg/dL). The 14-,
          30- and 90-day values weight each day’s mean by its readings, Σ(mean ×
          count) ÷ Σcount, from the glucose data star. Coverage compares the
          readings with one every five minutes; under 70% it is marked partial.
          GMI can differ from a lab A1C.
        </p>
        <p>
          <b>Variability</b> is the coefficient of variation, standard deviation
          ÷ mean. <b>GRI</b>, the glycemia risk index, is 3.0 × very low + 2.4 ×
          low + 1.6 × very high + 0.8 × high (in %), capped at 100: zone A is
          0–20 and E is 81–100.
        </p>
        <p>
          <b>Lows</b> follow the 2023 consensus: an episode starts after 15
          minutes in a row below 70 (or 54) and ends after 15 minutes at or
          above it. Overnight lows start between midnight and 6 AM.{" "}
          <b>Typical day</b> pools every day’s readings by quarter hour: the
          line is the median, the darker band the middle half (25–75%) and the
          lighter band 5–95%.
        </p>
        <p>
          <a
            href="https://pmc.ncbi.nlm.nih.gov/articles/PMC6196826/"
            target="_blank"
            rel="noreferrer"
          >
            GMI method
          </a>{" "}
          ·{" "}
          <a
            href="https://doi.org/10.2337/dc26-s006"
            target="_blank"
            rel="noreferrer"
          >
            ADA CGM reporting guidance
          </a>{" "}
          ·{" "}
          <a
            href="https://pubmed.ncbi.nlm.nih.gov/36493795/"
            target="_blank"
            rel="noreferrer"
          >
            Hypoglycemia consensus
          </a>
        </p>
        {data.glucoseHistory && (
          <p>
            {data.glucoseHistory.provenance.demo ? "Fictional " : ""}
            {sourceName(data.glucoseHistory.provenance.sourceId)} history ·
            Record {data.glucoseHistory.provenance.recordId}. Source streams are
            kept separate.
          </p>
        )}
      </details>

      {demo && (
        <button className="linked-record" onClick={() => selectNode("run")}>
          <span>
            <b>Linked sample activity</b>
            <small>Sunday long run · 09:00 UTC · 8.4 km</small>
          </span>
          <ArrowUpRight size={18} />
        </button>
      )}
      {demo && (
        <p className="chart-note">
          An explicit demo-day link, not evidence of medical causation.
        </p>
      )}
      {healthStatus(data.current) === "stale" && (
        <p className="caution">
          STALE OBSERVATION · This is the last recorded value, not a current
          reading.
        </p>
      )}
      <p className="chart-note">
        For your information only, not a medical device. Use your device’s own
        display and your care team for treatment decisions.
      </p>
      <Origin value={data.current.provenance} />
    </div>
  );
}

function Ranges({ window }: { window: RangeWindow }) {
  const cvSteady = window.cv !== null && window.cv <= 36;
  return (
    <section className="glucose-ranges" aria-label="Time in ranges">
      <header className="glucose-head">
        <p className="eyebrow">TIME IN RANGES</p>
        <span>
          {window.title} · {window.tag}
          {window.stale ? " · STALE" : ""} · {window.dates}
        </span>
      </header>
      <div className="ranges-body">
        <RangeRing
          className="ranges-ring"
          bands={window.bands}
          tight={window.tight}
          label={ringLabel(window.bands, window.tight, window.title)}
        >
          <strong>{pct(window.within)}</strong>
          <span>in range</span>
          {window.tight !== null && <small>{pct(window.tight)} tight</small>}
        </RangeRing>
        <ul className="ranges-legend">
          {bandOrder.map((key) => (
            <li key={key} className={key}>
              <i />
              <span className="band-name">
                {bandNames[key]}
                <small>{bandLimits[key]}</small>
              </span>
              <b>{pct(window.bands[key])}</b>
              {(key === "low" || key === "veryLow") && (
                <small className="per-day">
                  {perDay(window.bands[key]) ?? "none"}
                </small>
              )}
            </li>
          ))}
          {window.tight !== null && (
            <li className="tight">
              <i />
              <span className="band-name">
                Tight range
                <small>70–140</small>
              </span>
              <b>{pct(window.tight)}</b>
            </li>
          )}
        </ul>
      </div>
      <dl className="ranges-facts">
        <div>
          <dt>Average</dt>
          <dd>
            {window.mean !== null ? Math.round(window.mean) : "—"}
            <small>mg/dL</small>
          </dd>
          <small>mean glucose</small>
        </div>
        {window.cv !== null && (
          <div>
            <dt>Variability</dt>
            <dd>{Math.round(window.cv)}%</dd>
            <small>CV · {cvSteady ? "steady" : "above 36%"}</small>
          </div>
        )}
        <div>
          <dt>Risk index</dt>
          <dd>
            <span className={`gri-badge zone-${window.gri.zone}`}>
              {window.gri.zone}
            </span>
          </dd>
          <small>GRI {Math.round(window.gri.value)} of 100</small>
        </div>
      </dl>
      <p className="history-coverage">
        {Math.round(window.coverage)}% of expected readings recorded
        {window.bounded ? ` · ${window.bounded} beyond sensor range` : ""}
        {window.source === "archive" ? " · source archive, device days" : ""}
      </p>
    </section>
  );
}

const windows = [14, 30, 90] as const;
function Gmi({
  days,
  reading,
  fallback,
}: {
  days: ReturnType<typeof useGlucoseDays>;
  reading: boolean;
  fallback: number | null;
}) {
  const [span, setSpan] = useState<(typeof windows)[number]>(90);
  const result = useMemo(
    () => (days ? gmiFromDays(days, span) : null),
    [days, span],
  );
  const shown = result?.gmi ?? (days || reading ? null : fallback);
  const inWindow = useMemo(
    () =>
      result && days
        ? days.filter((d) => d.date >= result.start && d.date <= result.end)
        : [],
    [days, result],
  );
  return (
    <section className="glucose-gmi" aria-label="Glucose management indicator">
      <header className="glucose-head">
        <p className="eyebrow">GMI</p>
        {days && (
          <div className="segmented" role="group" aria-label="GMI window">
            {windows.map((n) => (
              <button
                key={n}
                aria-pressed={span === n}
                onClick={() => setSpan(n)}
              >
                {n} days
              </button>
            ))}
          </div>
        )}
      </header>
      <div className="gmi-body">
        <strong className="gmi-value">
          {shown !== null ? shown.toFixed(1) : "—"}
          {shown !== null && <small>%</small>}
        </strong>
        <div className="gmi-words">
          <b>
            {result
              ? `${span}-day estimate`
              : shown !== null
                ? "14-day estimate"
                : reading
                  ? "Reading 90 days of glucose…"
                  : "Estimate unavailable"}
            {result ? ` · mean ${Math.round(result.mean)} mg/dL` : ""}
          </b>
          {result ? (
            <span>
              {dayRange(result.start, result.end)} · {result.daysCovered} of{" "}
              {span} days · {Math.round(result.coverage)}% of readings
            </span>
          ) : (
            shown !== null && <span>From the recent history</span>
          )}
          {result?.partial && (
            <span className="partial">
              Partial · under 70% of readings, so read it loosely
            </span>
          )}
        </div>
      </div>
      {inWindow.length > 1 && result && (
        <DailyMeans days={inWindow} start={result.start} span={span} />
      )}
      <p className="chart-note">
        GMI is an A1C-like estimate from sensor glucose; GMI is not lab A1C.
      </p>
    </section>
  );
}

// Each day of the GMI window: its average as a line over a soft band from
// the day's low to its high. Missing days leave a gap.
function DailyMeans({
  days,
  start,
  span,
}: {
  days: { date: string; min: number; mean: number; max: number }[];
  start: string;
  span: number;
}) {
  const lo = 40,
    hi = Math.max(250, ...days.map((d) => d.max));
  const y = (v: number) =>
    48 - ((Math.min(hi, Math.max(lo, v)) - lo) / (hi - lo)) * 44;
  const index = (date: string) =>
    (Date.parse(`${date}T00:00:00Z`) - Date.parse(`${start}T00:00:00Z`)) /
    86_400_000;
  const x = (date: string) => ((index(date) + 0.5) / span) * 300;
  const runs: (typeof days)[] = [];
  for (const d of [...days].sort((a, b) => a.date.localeCompare(b.date))) {
    const run = runs.at(-1);
    if (run && index(d.date) - index(run.at(-1)!.date) === 1) run.push(d);
    else runs.push([d]);
  }
  const line = (run: typeof days, key: "min" | "mean" | "max") =>
    run.map((d) => `${x(d.date)},${y(d[key])}`).join(" L");
  const means = days.map((d) => d.mean);
  return (
    <div
      className="daily-means"
      role="img"
      aria-label={`Daily average glucose over ${days.length} days, from ${Math.round(Math.min(...means))} to ${Math.round(Math.max(...means))} mg/dL.`}
    >
      <svg viewBox="0 0 300 50" preserveAspectRatio="none" aria-hidden="true">
        <rect
          className="dm-range"
          x="0"
          width="300"
          y={y(180)}
          height={y(70) - y(180)}
        />
        {runs.map((run) =>
          run.length > 1 ? (
            <g key={run[0].date}>
              <path
                className="dm-span"
                d={`M${line(run, "max")} L${line([...run].reverse(), "min")} Z`}
              />
              <path className="dm-mean" d={`M${line(run, "mean")}`} />
            </g>
          ) : (
            <line
              key={run[0].date}
              className="dm-mean"
              x1={x(run[0].date) - 0.8}
              x2={x(run[0].date) + 0.8}
              y1={y(run[0].mean)}
              y2={y(run[0].mean)}
            />
          ),
        )}
      </svg>
      <span>Daily average, over each day’s low-to-high spread</span>
    </div>
  );
}

// AGP-style typical day: median line with 25–75% and 5–95% bands across the
// local day, night shaded.
function TypicalDay({ history }: { history: GlucoseHistory }) {
  const agp = useMemo(() => typicalDay(history), [history]);
  if (!agp) return null;
  const valid = agp.points.filter((p) => p !== null);
  const lo = 40,
    hi = Math.min(
      400,
      Math.max(
        250,
        Math.ceil((Math.max(...valid.map((p) => p.p95)) + 10) / 50) * 50,
      ),
    );
  const y = (v: number) =>
    100 - ((Math.min(hi, Math.max(lo, v)) - lo) / (hi - lo)) * 100;
  const x = (minute: number) => (minute / 1440) * 600;
  // Wrap midnight so the bands reach both edges.
  const first = agp.points[0],
    last = agp.points.at(-1);
  type Point = NonNullable<(typeof agp.points)[number]>;
  const edge =
    first && last
      ? (Object.fromEntries(
          (["p5", "p25", "p50", "p75", "p95"] as const).map((k) => [
            k,
            (first[k] + last[k]) / 2,
          ]),
        ) as Omit<Point, "minute">)
      : null;
  const points: (Point | null)[] = [
    edge ? { minute: 0, ...edge } : null,
    ...agp.points,
    edge ? { minute: 1440, ...edge } : null,
  ];
  const runs: Point[][] = [];
  let run: Point[] = [];
  for (const p of points) {
    if (p) run.push(p);
    else if (run.length) {
      runs.push(run);
      run = [];
    }
  }
  if (run.length) runs.push(run);
  const band = (a: keyof Point, b: keyof Point) =>
    runs
      .filter((r) => r.length > 1)
      .map(
        (r) =>
          `M${r.map((p) => `${x(p.minute)},${y(p[a])}`).join(" L")} L${[...r]
            .reverse()
            .map((p) => `${x(p.minute)},${y(p[b])}`)
            .join(" L")} Z`,
      )
      .join(" ");
  const median = runs
    .filter((r) => r.length > 1)
    .map((r) => `M${r.map((p) => `${x(p.minute)},${y(p.p50)}`).join(" L")}`)
    .join(" ");
  const medians = valid.map((p) => p.p50);
  return (
    <section className="glucose-typical" aria-label="Typical day">
      <header className="glucose-head">
        <p className="eyebrow">TYPICAL DAY</p>
        <span>
          {agp.days} days · median {Math.round(Math.min(...medians))}–
          {Math.round(Math.max(...medians))} mg/dL
        </span>
      </header>
      <div
        className="agp"
        role="img"
        aria-label={`Typical day over ${agp.days} days: median glucose by time of day ranges from ${Math.round(Math.min(...medians))} to ${Math.round(Math.max(...medians))} mg/dL, with the middle half and 5 to 95 percent bands.`}
      >
        <svg
          viewBox="0 0 600 100"
          preserveAspectRatio="none"
          aria-hidden="true"
        >
          <rect className="agp-night" x="0" y="0" width={x(360)} height="100" />
          <rect
            className="agp-range"
            x="0"
            width="600"
            y={y(180)}
            height={y(70) - y(180)}
          />
          <path className="agp-outer" d={band("p95", "p5")} />
          <path className="agp-inner" d={band("p75", "p25")} />
          <line
            className="agp-line high"
            x1="0"
            x2="600"
            y1={y(180)}
            y2={y(180)}
          />
          <line
            className="agp-line low"
            x1="0"
            x2="600"
            y1={y(70)}
            y2={y(70)}
          />
          <path className="agp-median" d={median} />
        </svg>
        <span className="agp-night-label">night</span>
        {[70, 180].map((v) => (
          <span key={v} className="agp-y" style={{ top: `${y(v)}%` }}>
            {v}
          </span>
        ))}
      </div>
      <div className="agp-x" aria-hidden="true">
        <span>12 AM</span>
        <span>6 AM</span>
        <span>12 PM</span>
        <span>6 PM</span>
        <span>12 AM</span>
      </div>
      <div className="agp-key" aria-hidden="true">
        <span>
          <i className="median" />
          median
        </span>
        <span>
          <i className="inner" />
          middle half
        </span>
        <span>
          <i className="outer" />
          5–95%
        </span>
      </div>
    </section>
  );
}

function Lows({ history }: { history: GlucoseHistory }) {
  const result = useMemo(() => {
    const options = { intervalMinutes: history.intervalMinutes };
    const level1 = lowEpisodes(history.samples, { ...options, threshold: 70 });
    const level2 = lowEpisodes(history.samples, { ...options, threshold: 54 });
    return { level1, level2, best: bestDay(history) };
  }, [history]);
  const span =
    (Date.parse(history.end) - Date.parse(history.start)) / 86_400_000;
  const when =
    span >= 2 ? `${Math.round(span)} days` : `${Math.round(span * 24)} hours`;
  const night = result.level1.filter((e) => e.night).length;
  const { best } = result;
  return (
    <section className="glucose-pair" aria-label="Lows and best day">
      <div className={`pair-card lows ${result.level1.length ? "" : "none"}`}>
        <p className="eyebrow">LOWS · {when.toUpperCase()}</p>
        <strong>{result.level1.length || "None"}</strong>
        <b>
          {result.level1.length === 0
            ? "no lows below 70"
            : result.level1.length === 1
              ? "episode below 70"
              : "episodes below 70"}
        </b>
        <span>
          {result.level1.length
            ? `${result.level2.length} below 54 · ${night} overnight`
            : "15 minutes or more below 70 counts as one"}
        </span>
      </div>
      {best && (
        <div className="pair-card best">
          <p className="eyebrow">BEST DAY</p>
          <strong>{fullDay(best.date)}</strong>
          <b>{pct(best.within)} in range</b>
          <span>
            {best.mean !== null
              ? `average ${Math.round(best.mean)} mg/dL · `
              : ""}
            best of {best.days} full days
          </span>
        </div>
      )}
    </section>
  );
}
