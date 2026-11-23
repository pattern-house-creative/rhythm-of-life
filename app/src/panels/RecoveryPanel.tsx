import { useMemo, useState } from "react";
import { motion } from "motion/react";
import { ArrowUpRight, Moon } from "lucide-react";
import { hasMeasurement } from "../health";
import type { ObservatoryData } from "../model";
import { Origin } from "./Origin";
import { dataStar, metric, useReadings } from "./readings";
import type { ReadData } from "./readings";
import { polar } from "./arcs";
import { Arc, GlucoseLine, Trend } from "./bodyViz";
import {
  addDays,
  batteryBand,
  byDay,
  dayName,
  hm,
  hrvStatus,
  localDay,
  nights,
  onOrBefore,
  overnightGlucose,
  readinessBand,
  sleepBand,
  stageName,
  sleepPercent,
  stageShares,
  stages,
} from "./garmin";
import type { Night } from "./garmin";
import "./RecoveryPanel.css";

const round = (v: number, digits = 0) =>
  Number(v.toFixed(digits)).toLocaleString(undefined, {
    maximumFractionDigits: digits,
  });

export function RecoveryPanel({
  data,
  selectNode,
  readData,
}: {
  data: ObservatoryData;
  selectNode: (id: string) => void;
  readData?: ReadData;
}) {
  const series = useReadings(readData, dataStar("garmin", "recovery"));
  const reading = series === undefined;
  const list = useMemo(() => nights(series), [series]);
  const [picked, setPicked] = useState<string | null>(null);

  // Without nightly readings, last night still comes from the summary.
  const signals = data.recovery.metrics;
  const summaryNight: Night | null =
    hasMeasurement(signals?.sleep ?? data.recovery) &&
    data.recovery.sleepMinutes != null
      ? {
          date:
            signals?.sleep.provenance.calendarDate ??
            data.recovery.provenance.calendarDate ??
            data.recovery.provenance.observedAt?.slice(0, 10) ??
            "",
          asleep: data.recovery.sleepMinutes * 60,
          stages: { deep: null, light: null, rem: null, awake: null },
          percent: { deep: null, light: null, rem: null },
          score: null,
        }
      : null;
  const latest = list.at(-1) ?? summaryNight;
  const night = list.find((n) => n.date === picked) ?? latest;
  const focus = night?.date || null;
  const isLatest = !!night && night.date === latest?.date;
  const today = localDay();
  const nightLabel = !night
    ? "No recent night"
    : isLatest && (night.date === today || night.date === addDays(today, 1))
      ? "Last night"
      : night.date
        ? dayName(night.date)
        : "Latest night";

  // Two weeks of nights, ending at the latest one; missing nights stay empty.
  const fortnight = latest?.date
    ? Array.from({ length: 14 }, (_, i) => addDays(latest.date, i - 13))
    : [];

  const day = (name: string) => byDay(series, name);
  const at = (name: string) => (focus ? onOrBefore(day(name), focus) : null);
  const hrv = day("hrv_overnight"),
    weekly = day("hrv_weekly"),
    low = day("hrv_baseline_low"),
    high = day("hrv_baseline_high"),
    resting = day("resting_heart_rate");
  // Garmin's daily Body Battery low and high, or the intraday readings' range.
  const battery = metric(series, "body_battery");
  const intraday = (pick: "min" | "max") =>
    focus
      ? onOrBefore(
          new Map((battery?.days ?? []).map((d) => [d.date, d[pick]])),
          focus,
        )
      : null;
  const batteryLow = at("body_battery_min") ?? intraday("min"),
    batteryHigh = at("body_battery_max") ?? intraday("max");
  const chargedDays = day("body_battery_charged"),
    wakeDays = day("body_battery_wake");
  const charged = at("body_battery_charged"),
    readiness = at("training_readiness");
  // Body Battery at wake and recharged during sleep, for this night only.
  const wake = focus ? (wakeDays.get(focus) ?? null) : null,
    recharged = focus
      ? (day("body_battery_sleep_charge").get(focus) ?? null)
      : null;
  const wakeSpan = fortnight
    .map((d) => wakeDays.get(d))
    .filter((v): v is number => v != null);
  const wakeAverage = wakeSpan.length
    ? wakeSpan.reduce((a, b) => a + b, 0) / wakeSpan.length
    : null;
  const batteryTrend = wakeAverage != null ? wakeDays : chargedDays;
  const hrvNight = focus ? onOrBefore(hrv, focus) : null;
  const hrvWeek = focus ? onOrBefore(weekly, focus) : null;
  const baseLow = focus ? onOrBefore(low, focus) : null,
    baseHigh = focus ? onOrBefore(high, focus) : null;
  const hrvWord = hrvStatus(hrvWeek?.value, baseLow?.value, baseHigh?.value);
  const restingNow = focus ? onOrBefore(resting, focus) : null;
  const restingSpan = fortnight
    .map((d) => resting.get(d))
    .filter((v): v is number => v != null);
  const restingAverage = restingSpan.length
    ? restingSpan.reduce((a, b) => a + b, 0) / restingSpan.length
    : null;
  // Without readings, the summary's own resting HR and HRV.
  const summaryResting =
    !restingNow &&
    hasMeasurement(signals?.restingHeartRate ?? data.recovery) &&
    data.recovery.restingHeartRate != null
      ? data.recovery.restingHeartRate
      : null;
  const summaryHrv =
    !hrvNight &&
    hasMeasurement(signals?.overnightHrv ?? data.recovery) &&
    data.recovery.overnightHrvMs != null
      ? data.recovery.overnightHrvMs
      : null;

  // Overnight glucose belongs beside the latest night only.
  const glucose =
    hasMeasurement(data.glucose) && isLatest
      ? overnightGlucose(data.glucose.samples)
      : null;
  // The words say what overnightGlucose counts: the readings between midnight
  // and 6 AM on one date, however few, never matched to when sleep began or
  // ended. Time below 70 is estimated from the readings' spacing.
  const glucoseDay = glucose ? dayName(glucose.date) : "";
  const glucoseLabel =
    glucose &&
    `OVERNIGHT GLUCOSE · MIDNIGHT–6 AM · ${glucoseDay.toUpperCase()}`;
  const glucoseShare = glucose ? Math.round(glucose.inRange * 100) : 0,
    glucoseCount = glucose?.points.length ?? 0,
    // Whole minutes; any reading below 70 counts at least one interval.
    glucoseBelow = glucose ? Math.round(glucose.minutesBelow70) : 0,
    glucoseLow = !!glucose && glucose.minutesBelow70 > 0;
  const glucoseReadings = `${glucoseCount} ${glucoseCount === 1 ? "reading" : "readings"}`;

  const quiet = [
    { name: "stress_mean", label: "avg stress", unit: "" },
    { name: "sleep_stress", label: "sleep stress", unit: "" },
    {
      name: "respiration_awake",
      label: "breaths/min awake",
      unit: "",
      digits: 1,
    },
    { name: "spo2_mean", label: "avg SpO2", unit: "%" },
  ]
    .map((q) => ({ ...q, reading: at(q.name) }))
    .filter((q) => q.reading);

  return (
    <div className="recovery-panel">
      <div className="detail-title">
        <div>
          <p className="eyebrow lavender">HEALTH / RECOVERY</p>
          <h2>Sleep & recovery</h2>
          <p>
            {night
              ? `${nightLabel}${night.date && nightLabel === "Last night" ? ` · ${dayName(night.date)}` : ""}`
              : reading
                ? "Reading Garmin…"
                : "No recent sleep from Garmin"}
          </p>
        </div>
        <Moon size={32} strokeWidth={1} />
      </div>

      <section className="sleep-hero" aria-label={`${nightLabel}'s sleep`}>
        <SleepRing night={night} />
        <div className="sleep-side">
          {night?.score != null && (
            <div className="sleep-duration">
              <strong>{hm(night.asleep)}</strong>
              <span>asleep · {nightLabel.toLowerCase()}</span>
            </div>
          )}
          <StageList night={night} reading={reading} />
          {(wake != null || recharged != null) && (
            <div className="sleep-morning">
              {recharged != null && (
                <div>
                  <strong>+{round(recharged)}</strong>
                  <span>Body Battery recharged overnight</span>
                </div>
              )}
              {wake != null && (
                <div>
                  <strong>{round(wake)}</strong>
                  <span>
                    at wake <em className="word mint">{batteryBand(wake)}</em>
                  </span>
                </div>
              )}
            </div>
          )}
          {glucose && (
            <div className="night-glucose">
              <p className="eyebrow">{glucoseLabel}</p>
              <GlucoseLine
                points={glucose.points}
                start={glucose.start}
                end={glucose.end}
                label={`Overnight glucose, midnight to 6 AM, ${glucoseDay}: ${glucoseShare}% of ${glucoseReadings} in range, lowest reading ${glucose.lowest} mg/dL, ${glucoseLow ? `about ${glucoseBelow} min below 70 mg/dL` : "no readings below 70 mg/dL"}.`}
              />
              <div className="night-glucose-axis" aria-hidden="true">
                <span>12 AM</span>
                <span>3 AM</span>
                <span>6 AM</span>
              </div>
              <p className="night-glucose-words">
                <span>
                  <b>{glucoseShare}%</b> of {glucoseReadings} in range
                </span>
                <i />
                <span>
                  Lowest reading <b>{glucose.lowest}</b> mg/dL
                </span>
                <i />
                {glucoseLow ? (
                  <span className="low-words">
                    About <b>{glucoseBelow}</b> min below 70 mg/dL
                  </span>
                ) : (
                  <span>No readings below 70 mg/dL</span>
                )}
              </p>
            </div>
          )}
        </div>
      </section>

      {list.length > 1 && (
        <section className="body-section">
          <div className="body-heading">
            <p className="eyebrow">TWO WEEKS OF SLEEP</p>
            <span>
              {hm(
                list
                  .slice(-14)
                  .filter((n) => n.asleep != null)
                  .reduce((t, n, _, all) => t + n.asleep! / all.length, 0),
              )}{" "}
              average asleep
            </span>
          </div>
          <NightChart
            dates={fortnight}
            nights={list}
            focus={focus}
            onPick={(date) => setPicked(date === latest?.date ? null : date)}
          />
          {!isLatest && latest && (
            <button className="back-to-latest" onClick={() => setPicked(null)}>
              Back to last night
            </button>
          )}
        </section>
      )}

      <section className="body-grid">
        <div className="body-card">
          <p className="eyebrow">OVERNIGHT HRV</p>
          <div className="body-number">
            <strong>
              {hrvNight
                ? round(hrvNight.value)
                : summaryHrv != null
                  ? summaryHrv
                  : "—"}{" "}
              <small>ms</small>
            </strong>
            <span>
              {hrvWord ? (
                <em className={hrvWord === "Balanced" ? "word" : "word warm"}>
                  {hrvWord}
                </em>
              ) : null}
              {hrvWeek
                ? `7-day average ${round(hrvWeek.value)} ms`
                : hrvNight
                  ? "last night"
                  : summaryHrv != null
                    ? "latest night"
                    : reading
                      ? "reading…"
                      : "not recorded"}
            </span>
          </div>
          <Trend
            dates={fortnight}
            values={hrv}
            focus={focus}
            band={low.size && high.size ? { low, high } : null}
            average={weekly.size ? weekly : null}
            minSpan={24}
            label={`Overnight HRV over two weeks${baseLow && baseHigh ? `, with the ${round(baseLow.value)}–${round(baseHigh.value)} ms baseline band` : ""}.`}
          />
          {baseLow && baseHigh ? (
            <small>
              baseline {round(baseLow.value)}–{round(baseHigh.value)} ms ·
              shaded
            </small>
          ) : weekly.size ? (
            <small>line: 7-day average</small>
          ) : null}
        </div>
        <div className="body-card">
          <p className="eyebrow">RESTING HEART RATE</p>
          <div className="body-number">
            <strong>
              {restingNow
                ? round(restingNow.value)
                : summaryResting != null
                  ? summaryResting
                  : "—"}{" "}
              <small>bpm</small>
            </strong>
            <span>
              {restingNow && restingAverage != null && restingSpan.length > 3
                ? Math.round(restingNow.value - restingAverage) === 0
                  ? "right on your two-week average"
                  : `${Math.abs(Math.round(restingNow.value - restingAverage))} ${restingNow.value < restingAverage ? "below" : "above"} your two-week average`
                : restingNow || summaryResting != null
                  ? "latest day"
                  : reading
                    ? "reading…"
                    : "not recorded"}
            </span>
          </div>
          <Trend
            dates={fortnight}
            values={resting}
            focus={focus}
            tone="rose"
            minSpan={12}
            label="Resting heart rate over two weeks."
          />
          {restingSpan.length > 1 && (
            <small>
              two-week range {round(Math.min(...restingSpan))}–
              {round(Math.max(...restingSpan))} bpm
            </small>
          )}
        </div>
        {readiness && (
          <div className="body-card">
            <p className="eyebrow">TRAINING READINESS</p>
            <ReadinessGauge value={readiness.value} />
            <small>
              {readiness.date === focus
                ? nightLabel === "Last night"
                  ? "this morning"
                  : dayName(readiness.date)
                : `from ${dayName(readiness.date)}`}
            </small>
          </div>
        )}
        {(wakeAverage != null || charged || batteryHigh) && (
          <div className="body-card battery-card">
            <p className="eyebrow">BODY BATTERY</p>
            {wakeAverage != null ? (
              <div className="body-number">
                <strong>{round(wakeAverage)}</strong>
                <span>average at wake · two weeks</span>
              </div>
            ) : charged ? (
              <div className="body-number">
                <strong>+{round(charged.value)}</strong>
                <span>
                  charged
                  {charged.date === focus
                    ? ` · ${nightLabel.toLowerCase()}`
                    : ` · ${dayName(charged.date)}`}
                </span>
              </div>
            ) : null}
            {batteryTrend.size > 1 && (
              <div className="battery-nights">
                <Trend
                  dates={fortnight}
                  values={batteryTrend}
                  focus={focus}
                  tone="mint"
                  minSpan={40}
                  wide={!readiness}
                  label={`Body Battery ${wakeAverage != null ? "at wake" : "charged"} each day over two weeks.`}
                />
                <small>
                  {wakeAverage != null ? "at wake" : "charged"} each day · two
                  weeks
                </small>
              </div>
            )}
            {batteryHigh && batteryLow && (
              <BatteryRange
                low={batteryLow.value}
                high={batteryHigh.value}
                when={
                  batteryHigh.date === today
                    ? "today"
                    : dayName(batteryHigh.date)
                }
              />
            )}
          </div>
        )}
      </section>

      {quiet.length > 0 && (
        <div className="quiet-stats">
          {quiet.map((q) => (
            <div key={q.name}>
              <b>
                {round(q.reading!.value, q.digits)}
                {q.unit}
              </b>
              <span>{q.label}</span>
            </div>
          ))}
        </div>
      )}

      {data.recovery.provenance.demo && (
        <button className="linked-record" onClick={() => selectNode("sleep")}>
          <span>
            <b>Explore the sleep journal</b>
            <small>See the fictional routine and its related ideas.</small>
          </span>
          <ArrowUpRight size={18} />
        </button>
      )}
      <Origin value={signals?.sleep.provenance ?? data.recovery.provenance} />
    </div>
  );
}

// Last night as two rings: the sleep score around the outside and the stages
// within. Without a score, the centre holds the time asleep.
function SleepRing({ night }: { night: Night | null }) {
  const shares = night ? stageShares(night) : null;
  const size = 220,
    c = size / 2;
  let angle = 0;
  const gap = 3;
  return (
    <div className="sleep-ring">
      <svg viewBox={`0 0 ${size} ${size}`} aria-hidden="true">
        <defs>
          <linearGradient id="sleep-score" x1="0" y1="0" x2="1" y2="1">
            <stop offset="0" stopColor="#d0b6e7" />
            <stop offset="1" stopColor="#8fd3c0" />
          </linearGradient>
        </defs>
        {night?.score != null && (
          <circle className="ring-track" cx={c} cy={c} r={100} />
        )}
        <circle className="ring-track inner" cx={c} cy={c} r={80} />
        {night?.score != null && (
          <>
            <Arc
              key={`score-${night.date}`}
              cx={c}
              cy={c}
              r={100}
              from={0}
              to={(Math.min(100, night.score) / 100) * 360}
              stroke="url(#sleep-score)"
              width={5}
            />
            {(() => {
              const [x, y] = polar(
                c,
                c,
                100,
                (Math.min(100, night.score) / 100) * 360,
              );
              return (
                <motion.circle
                  key={`tip-${night.date}`}
                  className="score-tip"
                  cx={x}
                  cy={y}
                  r={5}
                  initial={{ opacity: 0 }}
                  animate={{ opacity: 1 }}
                  transition={{ delay: 0.8 }}
                />
              );
            })()}
          </>
        )}
        {shares &&
          stages.map((s, i) => {
            const sweep = shares[s] * 360;
            const from = angle + gap / 2,
              to = angle + sweep - gap / 2;
            angle += sweep;
            return (
              <Arc
                key={`${night!.date}-${s}`}
                cx={c}
                cy={c}
                r={80}
                from={from}
                to={to}
                className={`stage-arc ${s}`}
                width={14}
                delay={0.1 + i * 0.12}
              />
            );
          })}
      </svg>
      <div className="sleep-ring-centre">
        {night?.score != null ? (
          <>
            <strong>{Math.round(night.score)}</strong>
            <span className="band-word">{sleepBand(night.score)}</span>
            <small>sleep score</small>
          </>
        ) : night?.asleep != null ? (
          <>
            <strong className="duration">{hm(night.asleep)}</strong>
            <small>asleep</small>
          </>
        ) : (
          <>
            <strong>—</strong>
            <small>no sleep recorded</small>
          </>
        )}
      </div>
    </div>
  );
}

function StageList({
  night,
  reading,
}: {
  night: Night | null;
  reading: boolean;
}) {
  const shares = night ? stageShares(night) : null;
  if (!night || !shares)
    return (
      <p className="stage-empty">
        {reading
          ? "Reading the night's stages…"
          : "Stages weren't recorded for this night."}
      </p>
    );
  return (
    <ul className="stage-list" aria-label="Sleep stages">
      {stages.map((s) => {
        // Deep, light and REM as Garmin shows them, a share of time asleep;
        // awake as a share of the whole night.
        const percent = s === "awake" ? null : sleepPercent(night, s);
        const bar = percent != null ? percent / 100 : shares[s];
        return (
          <li key={s} className={s}>
            <i />
            <span>{stageName[s]}</span>
            <b>{hm(night.stages[s])}</b>
            <small>{percent != null ? `${Math.round(percent)}%` : ""}</small>
            <span className="stage-share" aria-hidden="true">
              <motion.span
                key={`${night.date}-${s}`}
                initial={{ width: 0 }}
                animate={{ width: `${Math.min(1, bar) * 100}%` }}
                transition={{ duration: 0.7, ease: [0.2, 0.7, 0.2, 1] }}
              />
            </span>
          </li>
        );
      })}
    </ul>
  );
}

// Fourteen nights as soft stacked columns; tapping one brings it into focus.
function NightChart({
  dates,
  nights: list,
  focus,
  onPick,
}: {
  dates: string[];
  nights: Night[];
  focus: string | null;
  onPick: (date: string) => void;
}) {
  const byDate = new Map(list.map((n) => [n.date, n]));
  const total = (n: Night) =>
    stages.reduce((t, s) => t + (n.stages[s] ?? 0), 0) || (n.asleep ?? 0);
  const max = Math.max(8 * 3600, ...list.map(total));
  return (
    <div
      className="night-chart"
      role="group"
      aria-label="Sleep, last 14 nights"
    >
      <div className="night-plot" aria-hidden="true">
        <span
          className="night-guide"
          style={{ bottom: `${(8 * 3600 * 100) / max}%` }}
        >
          <small>8h</small>
        </span>
      </div>
      {dates.map((date) => {
        const n = byDate.get(date);
        const shares = n ? stageShares(n) : null;
        return (
          <button
            key={date}
            className="night"
            aria-pressed={date === focus}
            disabled={!n}
            onClick={() => onPick(date)}
            aria-label={
              n
                ? `${dayName(date)}: ${hm(n.asleep)} asleep${n.score != null ? `, sleep score ${Math.round(n.score)}` : ""}`
                : `${dayName(date)}: no sleep recorded`
            }
          >
            <span className="night-score">
              {n?.score != null ? Math.round(n.score) : ""}
            </span>
            <span className="night-track">
              {n && (
                <motion.span
                  className="night-bar"
                  initial={{ height: 0 }}
                  animate={{ height: `${(total(n) / max) * 100}%` }}
                  transition={{ duration: 0.7, ease: [0.2, 0.7, 0.2, 1] }}
                >
                  {shares ? (
                    stages
                      .slice()
                      .reverse()
                      .map((s) => (
                        <span
                          key={s}
                          className={s}
                          style={{ flexGrow: shares[s] }}
                        />
                      ))
                  ) : (
                    <span className="light" style={{ flexGrow: 1 }} />
                  )}
                </motion.span>
              )}
            </span>
            <small>{dayName(date, { weekday: "narrow" })}</small>
          </button>
        );
      })}
    </div>
  );
}

// Garmin's readiness bands around a 240° gauge, with the score at its tip.
function ReadinessGauge({ value }: { value: number }) {
  const v = Math.max(0, Math.min(100, value));
  const from = -120,
    sweep = 240;
  const to = from + (sweep * v) / 100;
  const [x, y] = polar(80, 80, 62, to);
  return (
    <div className="readiness">
      <svg viewBox="0 0 160 136" aria-hidden="true">
        <defs>
          <linearGradient id="readiness-fill" x1="0" y1="1" x2="1" y2="0">
            <stop offset="0" stopColor="#c1a66d" />
            <stop offset="0.55" stopColor="#b6a6cc" />
            <stop offset="1" stopColor="#7bdcbb" />
          </linearGradient>
        </defs>
        <Arc
          cx={80}
          cy={80}
          r={62}
          from={from}
          to={from + sweep}
          className="gauge-track"
          width={9}
        />
        {[25, 50, 75, 95].map((b) => {
          const [a, bb] = polar(80, 80, 52, from + (sweep * b) / 100);
          const [c2, d] = polar(80, 80, 56, from + (sweep * b) / 100);
          return (
            <line
              key={b}
              className="gauge-tick"
              x1={a}
              y1={bb}
              x2={c2}
              y2={d}
            />
          );
        })}
        <Arc
          cx={80}
          cy={80}
          r={62}
          from={from}
          to={to}
          stroke="url(#readiness-fill)"
          width={9}
          delay={0.15}
        />
        <motion.circle
          className="gauge-tip"
          cx={x}
          cy={y}
          r={6.5}
          initial={{ opacity: 0, scale: 0.4 }}
          animate={{ opacity: 1, scale: 1 }}
          transition={{ delay: 0.9 }}
        />
      </svg>
      <div className="readiness-centre">
        <strong>{Math.round(value)}</strong>
        <span className="band-word">{readinessBand(value)}</span>
      </div>
    </div>
  );
}

function BatteryRange({
  low,
  high,
  when,
}: {
  low: number;
  high: number;
  when: string;
}) {
  const lo = Math.max(0, Math.min(100, low)),
    hi = Math.max(lo, Math.min(100, high));
  return (
    <div className="battery">
      <small className="battery-when">
        {when === "today" ? "today's range" : `range · ${when}`}
      </small>
      <div
        className="battery-track"
        role="img"
        aria-label={`Body Battery ${when} from ${Math.round(lo)} to ${Math.round(hi)} of 100.`}
      >
        <motion.span
          className="battery-range"
          initial={{ left: `${lo}%`, width: 0 }}
          animate={{ left: `${lo}%`, width: `${hi - lo}%` }}
          transition={{ duration: 0.8, delay: 0.2 }}
        />
        <span className="battery-mark" style={{ left: `${lo}%` }} />
        <span className="battery-mark high" style={{ left: `${hi}%` }} />
      </div>
      <div className="battery-words">
        <span>
          low <b>{Math.round(lo)}</b>
        </span>
        <span>
          peak <b>{Math.round(hi)}</b>
          <em className="word mint">{batteryBand(hi)}</em>
        </span>
      </div>
    </div>
  );
}
