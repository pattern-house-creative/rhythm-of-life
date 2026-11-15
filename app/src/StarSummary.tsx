import type { ReactNode } from "react";
import type { GraphSeries } from "./knowledge";
import {
  dailySummary,
  dayName,
  duration,
  glucoseSummary,
  headline,
  insulinSummary,
  longDate,
  meaningOf,
  ordered,
  say,
  show,
  shortDate,
  sleepSummary,
  starKind,
  trainingSummary,
  weekday,
  when,
} from "./readingsModel";
import type { InsulinDay, Shown } from "./readingsModel";
import { slotAt, useScrub, useWidth } from "./scrub";

type Focus = { focus: string | null; setFocus: (date: string | null) => void };

// A big thin number with its unit set small beside it.
export function Figure({
  shown,
  size,
}: {
  shown: Shown | null;
  size?: "large";
}) {
  return (
    <span className={`figure${size ? ` ${size}` : ""}`}>
      <b>{shown ? shown.value : "—"}</b>
      {shown?.unit && <small>{shown.unit}</small>}
    </span>
  );
}

// The star at a glance, above its measurements, so no screen opens onto a
// wall of cards. Which summary a star gets depends on what it measures.
export function StarSummary({
  series,
  end,
  ...focus
}: { series: GraphSeries[]; end: string } & Focus) {
  switch (starKind(series)) {
    case "insulin":
      return <InsulinSummary series={series} end={end} {...focus} />;
    case "glucose":
      return <GlucoseSummary series={series} end={end} {...focus} />;
    case "sleep":
      return <SleepSummary series={series} end={end} />;
    case "training":
      return <TrainingSummary series={series} end={end} {...focus} />;
    case "daily":
      return <DailySummary series={series} end={end} {...focus} />;
    default:
      return <OtherSummary series={series} end={end} />;
  }
}

function Panel({
  eyebrow,
  children,
  className = "",
}: {
  eyebrow: string;
  children: ReactNode;
  className?: string;
}) {
  return (
    <section className={`star-summary ${className}`} aria-label="At a glance">
      <p className="eyebrow star-eyebrow">{eyebrow}</p>
      {children}
    </section>
  );
}
function Stat({
  shown,
  label,
  note,
  tone,
}: {
  shown: Shown | null;
  label: string;
  note?: ReactNode;
  tone?: string;
}) {
  return (
    <div className={`star-stat${tone ? ` tone-${tone}` : ""}`}>
      <Figure shown={shown} />
      <span className="star-label">{label}</span>
      {note && <span className="star-note">{note}</span>}
    </div>
  );
}
const of = (value: number | null | undefined, unit: string) =>
  value == null ? null : show(value, unit);
const percent = (share: number) => `${Math.round(share * 100)}%`;

// --- Insulin and carbs -------------------------------------------------------

function InsulinSummary({
  series,
  end,
  focus,
  setFocus,
}: { series: GraphSeries[]; end: string } & Focus) {
  const s = insulinSummary(series, end);
  const day = focus ? s.days.find((d) => d.date === focus) : undefined;
  const bolus = day ? day.bolus : s.bolusPerDay;
  const auto = day ? day.auto : s.autoPerDay;
  const insulin =
    bolus == null && auto == null ? null : (bolus ?? 0) + (auto ?? 0);
  const carbs = day ? day.carbs : s.carbsPerDay;
  const change =
    !day && s.insulinPerDay != null && s.previous != null
      ? s.insulinPerDay - s.previous
      : null;
  return (
    <Panel
      eyebrow={day ? longDate(day.date).toUpperCase() : "LAST 14 DAYS"}
      className="insulin-summary"
    >
      <div className="star-row">
        <div className="star-ring-block">
          <Ring
            segments={[
              { value: bolus ?? 0, tone: "mint" },
              { value: auto ?? 0, tone: "lavender" },
            ]}
          >
            <Figure
              shown={
                insulin == null
                  ? null
                  : { value: show(insulin, "U").value, unit: "" }
              }
              size="large"
            />
            <span className="ring-caption">
              {day ? "U that day" : "U a day"}
            </span>
          </Ring>
          <div className="star-legend">
            <span className="star-label">
              {day ? "Insulin that day" : "Insulin per day"}
            </span>
            <span className="legend-row tone-mint">
              <i /> Bolus <b>{bolus == null ? "—" : say(bolus, "U")}</b>
              {insulin ? (
                <small>{percent((bolus ?? 0) / insulin)}</small>
              ) : null}
            </span>
            <span className="legend-row tone-lavender">
              <i /> Auto-correction <b>{auto == null ? "—" : say(auto, "U")}</b>
              {insulin ? <small>{percent((auto ?? 0) / insulin)}</small> : null}
            </span>
            {s.basal != null && (
              <span className="legend-row tone-teal">
                <i /> Plus basal, about <b>{say(s.basal, "U/h")}</b>
              </span>
            )}
            {change != null && Math.abs(change) >= 0.05 && (
              <span className="star-note">
                {change > 0 ? "+" : "−"}
                {say(Math.abs(change), "U")} a day against the 14 days before
              </span>
            )}
          </div>
        </div>
        <Stat
          shown={of(carbs, "g")}
          label={day ? "Carbs entered that day" : "Carbs entered per day"}
          tone="warm"
          note={
            !day && s.carbsPerUnit != null
              ? `About ${Math.round(s.carbsPerUnit)} g for each unit of bolus`
              : undefined
          }
        />
      </div>
      <InsulinChart days={s.days} focus={focus} setFocus={setFocus} />
      <div className="star-key" aria-hidden="true">
        <span className="tone-mint">
          <i /> Bolus
        </span>
        <span className="tone-lavender">
          <i /> Auto-correction
        </span>
        <span className="tone-warm">
          <i className="bubble" /> Carbs, sized by grams
        </span>
      </div>
      {s.autoShare != null && (
        <p className="star-line">
          Auto-correction was {percent(s.autoShare)} of all bolus insulin
          {s.autoShare > 0.3
            ? ". Above about 30% often means a meal bolus came late or was missed."
            : ", under the 30% that often signals a late or missed meal bolus."}
        </p>
      )}
    </Panel>
  );
}

// Fourteen days of insulin as soft stacked columns (bolus under
// auto-correction) with each day's carbs as a bubble above.
function InsulinChart({
  days,
  focus,
  setFocus,
}: { days: InsulinDay[] } & Focus) {
  const [ref, width] = useWidth<SVGSVGElement>(640);
  const height = 190,
    left = 4,
    right = 4,
    top = 40,
    bottom = 24;
  const n = days.length;
  const slot = (width - left - right) / n;
  const x = (i: number) => left + (i + 0.5) * slot;
  const insulin = (d: InsulinDay) => (d.bolus ?? 0) + (d.auto ?? 0);
  const most = Math.max(1, ...days.map(insulin));
  const mostCarbs = Math.max(1, ...days.map((d) => d.carbs ?? 0));
  const floor = height - bottom;
  const h = (v: number) => (v / most) * (floor - top - 16);
  const bar = Math.max(4, Math.min(16, slot * 0.4));
  const bubble = Math.max(3, Math.min(12, slot * 0.34));
  const dates = days.map((d) => d.date);
  const readable = (date: string) => {
    const d = days.find((r) => r.date === date);
    return !!d && (d.bolus != null || d.auto != null || d.carbs != null);
  };
  const scrub = useScrub({
    dates,
    readable,
    focus,
    setFocus,
    pick: (clientX) => {
      const i = slotAt(ref, clientX, left, right, n);
      return i < 0 ? null : dates[i];
    },
  });
  const at = focus ? dates.indexOf(focus) : -1;
  const focused = at >= 0 ? days[at] : null;
  return (
    <svg
      ref={ref}
      className={`insulin-chart${at >= 0 ? " focused" : ""}`}
      width="100%"
      height={height}
      viewBox={`0 0 ${width} ${height}`}
      role="slider"
      tabIndex={0}
      aria-label="Insulin and carbs by day, last 14 days"
      aria-valuemin={0}
      aria-valuemax={n - 1}
      aria-valuenow={at >= 0 ? at : n - 1}
      aria-valuetext={
        focused
          ? `${longDate(focused.date)}: ${[
              focused.bolus != null && `bolus ${say(focused.bolus, "U")}`,
              focused.auto != null &&
                `auto-correction ${say(focused.auto, "U")}`,
              focused.carbs != null && `carbs ${say(focused.carbs, "g")}`,
            ]
              .filter(Boolean)
              .join(", ")}`
          : "Use the arrow keys to read each day"
      }
      {...scrub}
    >
      <line
        className="chart-floor"
        x1={left}
        x2={width - right}
        y1={floor}
        y2={floor}
      />
      {days.map((d, i) => {
        const b = h(d.bolus ?? 0),
          a = h(d.auto ?? 0);
        const r =
          d.carbs != null && d.carbs > 0
            ? Math.max(2, bubble * Math.sqrt(d.carbs / mostCarbs))
            : 0;
        const dim = at >= 0 && at !== i;
        return (
          <g key={d.date} className={dim ? "dim" : ""}>
            {d.bolus != null && b > 0 && (
              <rect
                className="bar-bolus"
                x={x(i) - bar / 2}
                width={bar}
                y={floor - b}
                height={b}
                rx={Math.min(bar / 2, b / 2)}
              />
            )}
            {d.auto != null && a > 0 && (
              <rect
                className="bar-auto"
                x={x(i) - bar / 2}
                width={bar}
                y={floor - b - a - (b > 0 ? 2 : 0)}
                height={a}
                rx={Math.min(bar / 2, a / 2)}
              />
            )}
            {r > 0 && (
              <circle
                className="carb-bubble"
                cx={x(i)}
                cy={top / 2 + 2}
                r={r}
              />
            )}
            <text
              className="chart-day"
              x={x(i)}
              y={height - 6}
              textAnchor="middle"
            >
              {weekday(d.date)}
            </text>
          </g>
        );
      })}
      {focused && (
        <g className="chart-cursor">
          <line x1={x(at)} x2={x(at)} y1={4} y2={floor} />
          {insulin(focused) > 0 && (
            <text
              className="bar-total"
              x={x(at)}
              y={floor - h(insulin(focused)) - 8}
              textAnchor="middle"
            >
              {say(insulin(focused), "U")}
            </text>
          )}
        </g>
      )}
      <rect className="chart-hit" width={width} height={height} />
    </svg>
  );
}

// --- Glucose -----------------------------------------------------------------

function GlucoseSummary({
  series,
  end,
  focus,
  setFocus,
}: { series: GraphSeries[]; end: string } & Focus) {
  const g = glucoseSummary(series, end);
  const sensor = series.find(
    (s) => s.metric === "sensor_glucose" || s.metric === "glucose",
  )!;
  const day = focus ? sensor.days.find((d) => d.date === focus) : undefined;
  const inWindow = !!focus && g.coverage.some((c) => c.date === focus);
  return (
    <Panel eyebrow="AT A GLANCE" className="glucose-summary">
      <div className="star-row">
        <Stat
          shown={of(g.average, "mg/dL")}
          label="Average · last 14 days"
          tone="mint"
          note={
            g.average90 != null
              ? `${g.span}-day average ${say(g.average90, "mg/dL")}`
              : undefined
          }
        />
        <Stat
          shown={of(g.latest?.value, "mg/dL")}
          label={
            g.latest
              ? `Latest · ${g.latest.observedAt ? when(g.latest.observedAt) : dayName(g.latest.date)}`
              : "Latest"
          }
        />
        <Stat
          shown={{ value: `${g.covered}`, unit: `of ${g.span}` }}
          label="Days with sensor data"
          note={
            inWindow
              ? day
                ? `${longDate(focus!)} · ${day.count} readings · average ${say(day.mean, "mg/dL")}`
                : `${longDate(focus!)} · no sensor data`
              : g.covered < g.span
                ? `${g.span - g.covered} ${g.span - g.covered === 1 ? "day" : "days"} without, like a sensor change`
                : "Every day covered"
          }
        />
      </div>
      <Strip
        dates={g.coverage.map((c) => c.date)}
        size={(date) =>
          g.coverage.find((c) => c.date === date)?.covered ? 1 : null
        }
        label={`Days with sensor data, last ${g.span} days`}
        describe={(date) =>
          g.coverage.find((c) => c.date === date)?.covered
            ? `${longDate(date)}: sensor data`
            : `${longDate(date)}: no sensor data`
        }
        focus={focus}
        setFocus={setFocus}
        tone="mint"
      />
    </Panel>
  );
}

// --- Sleep -------------------------------------------------------------------

const stageTone: Record<string, string> = {
  Deep: "deep",
  Light: "light",
  REM: "rem",
  Awake: "awake",
};
function SleepSummary({ series, end }: { series: GraphSeries[]; end: string }) {
  const s = sleepSummary(series, end);
  const lastNight = !!s.night && dayName(s.night, true) === "last night";
  return (
    <Panel
      eyebrow={
        s.night
          ? `${lastNight ? "Last night" : "Latest night"} · ${shortDate(s.night)}`.toUpperCase()
          : "SLEEP"
      }
      className="sleep-summary"
    >
      <div className="star-row">
        <div className="star-ring-block">
          <Ring
            segments={
              s.stages.length
                ? s.stages.map((st) => ({
                    value: st.seconds,
                    tone: stageTone[st.name],
                  }))
                : [{ value: 1, tone: "lavender" }]
            }
          >
            <Figure
              shown={
                s.last == null ? null : { value: duration(s.last), unit: "" }
              }
              size="large"
            />
            <span className="ring-caption">asleep</span>
          </Ring>
          {s.stages.length > 0 && (
            <div className="star-legend">
              <span className="star-label">Stages</span>
              {s.stages.map((st) => (
                <span
                  key={st.name}
                  className={`legend-row tone-${stageTone[st.name]}`}
                >
                  <i /> {st.name} <b>{duration(st.seconds)}</b>
                </span>
              ))}
            </div>
          )}
        </div>
        <div className="star-stack">
          <Stat
            shown={
              s.average == null
                ? null
                : { value: duration(s.average), unit: "" }
            }
            label={`Average night · last ${s.nights}`}
            tone="lavender"
            note={
              s.last != null && s.average != null && s.nights > 1
                ? `${lastNight ? "Last night" : "That night"} was ${duration(Math.abs(s.last - s.average))} ${s.last >= s.average ? "longer" : "shorter"}`
                : undefined
            }
          />
          {s.score != null && (
            <Stat
              shown={{
                value: String(Math.round(s.score)),
                unit: s.scoreWord ?? "",
              }}
              label="Sleep score"
              tone="lavender"
            />
          )}
        </div>
      </div>
    </Panel>
  );
}

// --- Training ----------------------------------------------------------------

function TrainingSummary({
  series,
  end,
  focus,
  setFocus,
}: { series: GraphSeries[]; end: string } & Focus) {
  const t = trainingSummary(series, end);
  const sessions = (n: number) => `${n} ${n === 1 ? "workout" : "workouts"}`;
  const focused = focus ? t.strip.find((d) => d.date === focus) : undefined;
  return (
    <Panel eyebrow="LAST 7 DAYS" className="training-summary">
      <div className="star-row">
        {t.time != null && (
          <Stat
            shown={{ value: duration(t.time), unit: "" }}
            label={`Training · ${sessions(t.sessions)}`}
            tone="mint"
            note={
              t.timeBefore != null
                ? `${duration(t.timeBefore)} and ${sessions(t.sessionsBefore)} the week before`
                : undefined
            }
          />
        )}
        {t.distance != null && (
          <Stat shown={of(t.distance, "m")} label="Distance" />
        )}
        {t.load != null && (
          <Stat
            shown={of(t.load, "score")}
            label="Training load, added up"
            tone="lavender"
          />
        )}
      </div>
      <Strip
        dates={t.strip.map((d) => d.date)}
        size={(date) => t.strip.find((d) => d.date === date)?.seconds ?? null}
        label="Training time by day, last 14 days"
        describe={(date) => {
          const d = t.strip.find((r) => r.date === date);
          return d?.seconds
            ? `${longDate(date)}: ${duration(d.seconds)} of training`
            : `${longDate(date)}: rest day`;
        }}
        focus={focus}
        setFocus={setFocus}
        tone="mint"
        days
      />
      <p className="star-line">
        {focused
          ? focused.seconds
            ? `${longDate(focused.date)} · ${duration(focused.seconds)} of training${focused.load ? ` · load ${Math.round(focused.load)}` : ""}`
            : `${longDate(focused.date)} · rest day`
          : "Each dot is a day, sized by training time."}
      </p>
    </Panel>
  );
}

// --- Daily activity ----------------------------------------------------------

function DailySummary({
  series,
  end,
  focus,
  setFocus,
}: { series: GraphSeries[]; end: string } & Focus) {
  const d = dailySummary(series, end);
  const focused = focus ? d.strip.find((r) => r.date === focus) : undefined;
  return (
    <Panel eyebrow="LAST 7 DAYS" className="daily-summary">
      <div className="star-row">
        {d.intensity != null && (
          <div className="star-ring-block">
            <Ring
              segments={[
                { value: Math.min(d.intensity, 150), tone: "mint" },
                { value: Math.max(0, 150 - d.intensity), tone: "track" },
              ]}
            >
              <Figure
                shown={{ value: String(Math.round(d.intensity)), unit: "" }}
                size="large"
              />
              <span className="ring-caption">of 150 min</span>
            </Ring>
            <div className="star-legend">
              <span className="star-label">Intensity minutes</span>
              <span className="star-note">
                {d.intensity >= 150
                  ? "Weekly goal reached."
                  : `${Math.round(150 - d.intensity)} to the weekly 150.`}{" "}
                Vigorous minutes count double.
              </span>
            </div>
          </div>
        )}
        <div className="star-stack">
          {d.steps != null && (
            <Stat
              shown={of(d.steps, "count")}
              label="Steps"
              tone="mint"
              note={
                d.stepsPerDay != null
                  ? `${Math.round(d.stepsPerDay).toLocaleString("en-US")} a day`
                  : undefined
              }
            />
          )}
          {d.activeCalories != null && (
            <Stat
              shown={of(d.activeCalories, "kcal")}
              label="Active calories a day"
              tone="warm"
            />
          )}
          {d.distance != null && (
            <Stat shown={of(d.distance, "m")} label="Distance" />
          )}
        </div>
      </div>
      {d.steps != null && (
        <>
          <Strip
            dates={d.strip.map((r) => r.date)}
            size={(date) => d.strip.find((r) => r.date === date)?.steps ?? null}
            label="Steps by day, last 14 days"
            describe={(date) => {
              const r = d.strip.find((x) => x.date === date);
              return r?.steps != null
                ? `${longDate(date)}: ${Math.round(r.steps).toLocaleString("en-US")} steps`
                : `${longDate(date)}: no reading`;
            }}
            focus={focus}
            setFocus={setFocus}
            tone="mint"
            days
          />
          <p className="star-line">
            {focused
              ? focused.steps != null
                ? `${longDate(focused.date)} · ${Math.round(focused.steps).toLocaleString("en-US")} steps`
                : `${longDate(focused.date)} · no reading`
              : "Each dot is a day of steps."}
          </p>
        </>
      )}
    </Panel>
  );
}

// --- Anything else -----------------------------------------------------------

function OtherSummary({ series, end }: { series: GraphSeries[]; end: string }) {
  const first = ordered(series).slice(0, 3);
  const days = new Set(series.flatMap((s) => s.days.map((d) => d.date)));
  return (
    <Panel
      eyebrow={`${series.length} ${series.length === 1 ? "MEASUREMENT" : "MEASUREMENTS"} · ${days.size} ${days.size === 1 ? "DAY" : "DAYS"}`}
    >
      <div className="star-row">
        {first.map((s) => {
          const m = meaningOf(s);
          const h = headline(s, m, end);
          return (
            <Stat
              key={s.metric}
              shown={h.value == null ? null : show(h.value, s.unit, m)}
              label={`${m.name} · ${h.caption}`}
              tone={m.tone}
            />
          );
        })}
      </div>
    </Panel>
  );
}

// --- Shapes ------------------------------------------------------------------

// A ring split into arcs, with its center left for a number.
function Ring({
  segments,
  children,
}: {
  segments: { value: number; tone: string }[];
  children: ReactNode;
}) {
  const size = 124,
    stroke = 8,
    r = (size - stroke) / 2,
    c = 2 * Math.PI * r;
  const sum = segments.reduce((n, s) => n + Math.max(0, s.value), 0);
  const parts = segments.filter((s) => s.value > 0);
  const gap = parts.length > 1 ? 5 : 0;
  let offset = 0;
  return (
    <div className="star-ring">
      <svg
        width={size}
        height={size}
        viewBox={`0 0 ${size} ${size}`}
        aria-hidden="true"
      >
        <circle className="ring-track" cx={size / 2} cy={size / 2} r={r} />
        {sum > 0 &&
          parts.map((s, i) => {
            const length = (s.value / sum) * c;
            const arc = (
              <circle
                key={i}
                className={`ring-arc tone-${s.tone}`}
                cx={size / 2}
                cy={size / 2}
                r={r}
                strokeDasharray={`${Math.max(0.01, length - gap)} ${c}`}
                strokeDashoffset={-offset}
                transform={`rotate(-90 ${size / 2} ${size / 2})`}
              />
            );
            offset += length;
            return arc;
          })}
      </svg>
      <div className="ring-center">{children}</div>
    </div>
  );
}

// A row of day dots sized by a value, read like any chart.
function Strip({
  dates,
  size,
  label,
  describe,
  focus,
  setFocus,
  tone,
  days = false,
}: {
  dates: string[];
  size: (date: string) => number | null;
  label: string;
  describe: (date: string) => string;
  tone: string;
  days?: boolean;
} & Focus) {
  const [ref, width] = useWidth<SVGSVGElement>(600);
  const n = dates.length;
  const height = days ? 46 : 22;
  const slot = width / n;
  const most = Math.max(1, ...dates.map((d) => size(d) ?? 0));
  const radius = Math.max(1.6, Math.min(days ? 9 : 3, slot * 0.36));
  const scrub = useScrub({
    dates,
    readable: () => true,
    focus,
    setFocus,
    pick: (clientX) => {
      const i = slotAt(ref, clientX, 0, 0, n);
      return i < 0 ? null : dates[i];
    },
  });
  const at = focus ? dates.indexOf(focus) : -1;
  const cy = days ? 16 : height / 2;
  return (
    <svg
      ref={ref}
      className={`day-strip tone-${tone}${at >= 0 ? " focused" : ""}`}
      width="100%"
      height={height}
      viewBox={`0 0 ${width} ${height}`}
      role="slider"
      tabIndex={0}
      aria-label={label}
      aria-valuemin={0}
      aria-valuemax={n - 1}
      aria-valuenow={at >= 0 ? at : n - 1}
      aria-valuetext={at >= 0 ? describe(dates[at]) : describe(dates[n - 1])}
      {...scrub}
    >
      {at >= 0 && (
        <rect
          className="strip-cursor"
          x={at * slot + 1}
          width={slot - 2}
          y={0}
          height={days ? 32 : height}
          rx={Math.min(8, slot / 2)}
        />
      )}
      {dates.map((date, i) => {
        const v = size(date);
        const r = v ? Math.max(1.8, radius * Math.sqrt(v / most)) : 1.4;
        return (
          <g key={date}>
            <circle
              className={v ? "strip-dot" : "strip-empty"}
              cx={(i + 0.5) * slot}
              cy={cy}
              r={r}
            />
            {days && (
              <text
                className="chart-day"
                x={(i + 0.5) * slot}
                y={height - 3}
                textAnchor="middle"
              >
                {weekday(date)}
              </text>
            )}
          </g>
        );
      })}
      <rect className="chart-hit" width={width} height={height} />
    </svg>
  );
}
