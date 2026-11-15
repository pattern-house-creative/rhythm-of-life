import { useMemo, useState } from "react";
import { motion } from "motion/react";
import {
  Activity,
  ArrowUpRight,
  Bike,
  Dumbbell,
  Flame,
  Footprints,
  HeartPulse,
  Waves,
} from "lucide-react";
import type { LucideIcon } from "lucide-react";
import { hasMeasurement, isDemo } from "../health";
import { weeklyWorkouts } from "../model";
import type { ObservatoryData, Workout } from "../model";
import {
  activityName,
  formatDistance,
  sportOf,
  workoutDistance,
} from "../activity";
import type { Sport } from "../activity";
import { Origin } from "./Origin";
import { dataStar, metric, useReadings } from "./readings";
import type { ReadData } from "./readings";
import { Arc, GlucoseLine } from "./bodyViz";
import {
  addDays,
  byDay,
  dayName,
  effectWord,
  hm,
  intensityGoal,
  intensityWeek,
  localDay,
  raceLeg,
  sportWeek,
  streak,
  trainingWeeks,
  weekday,
  workoutGlucose,
} from "./garmin";
import type { IntensityDay, SportWeek, TrainingDay } from "./garmin";
import "./TrainingPanel.css";

const sportIcon: Record<Sport, LucideIcon> = {
  swim: Waves,
  bike: Bike,
  run: Footprints,
  other: Dumbbell,
};
const sportName: Record<Sport, string> = {
  swim: "Swim",
  bike: "Bike",
  run: "Run",
  other: "Other",
};
const latestDate = (...stars: Parameters<typeof metric>[0][]) =>
  stars
    .flatMap((s) => s ?? [])
    .map((s) => s.latest?.date ?? s.days.at(-1)?.date ?? "")
    .sort()
    .at(-1) ?? "";

export function TrainingPanel({
  data,
  selectNode,
  readData,
}: {
  data: ObservatoryData;
  selectNode: (id: string) => void;
  readData?: ReadData;
}) {
  const demo = isDemo(data);
  const trainingReady = !["unavailable", "missing"].includes(
    data.workoutsStatus ?? "available",
  );
  const workouts = useMemo(
    () => (trainingReady ? data.workouts.filter((w) => hasMeasurement(w)) : []),
    [data.workouts, trainingReady],
  );
  // The recorded workouts' own week (the demo's is its sample day's).
  const weekAnchor = demo
    ? data.current.provenance.observedAt
    : (data.workoutsCoverage?.calendarDate ??
      data.retrievedAt ??
      data.workouts[0]?.startedAt ??
      null);
  const week = weeklyWorkouts(workouts, weekAnchor);
  const training = useReadings(readData, dataStar("garmin", "training"));
  const daily = useReadings(readData, dataStar("garmin", "daily"));
  const reading = training === undefined || daily === undefined;

  // Garmin's own days run to today, or to its latest reading if that is later.
  const today = [localDay(), latestDate(training, daily)].sort().at(-1)!;
  const intensity = intensityWeek(daily, today);
  const days = trainingWeeks(training, workouts, today);
  const run = streak(days, today);
  const thisWeek = days.filter((d) => d.date >= intensity.start);
  const totals = {
    sessions: thisWeek.reduce((t, d) => t + d.sessions, 0),
    minutes: thisWeek.reduce((t, d) => t + (d.minutes ?? 0), 0),
    load: thisWeek.reduce((t, d) => t + (d.load ?? 0), 0),
  };
  const sports = sportWeek(week.counted);
  const anchorDay = weekAnchor?.slice(0, 10);
  const sportWeekStart =
    anchorDay && Number.isFinite(Date.parse(anchorDay))
      ? addDays(anchorDay, -weekday(anchorDay))
      : null;
  const sportsLabel =
    !sportWeekStart || sportWeekStart === intensity.start
      ? "THIS WEEK BY SPORT"
      : `WEEK OF ${dayName(sportWeekStart, { month: "short", day: "numeric" }).toUpperCase()} BY SPORT`;

  const [showAll, setShowAll] = useState(false);
  const sorted = workouts
    .slice()
    .sort((a, b) => Date.parse(b.startedAt) - Date.parse(a.startedAt));
  const shown = showAll ? sorted : sorted.slice(0, 4);
  const glucose = hasMeasurement(data.glucose) ? data.glucose.samples : [];

  const steps = byDay(daily, "steps", true);
  const stepDays = [...steps.entries()].filter(
    ([d]) => d > addDays(today, -14),
  );
  const stepAverage = stepDays.length
    ? stepDays.reduce((t, [, v]) => t + v, 0) / stepDays.length
    : null;

  return (
    <div className="training-panel">
      <div className="detail-title">
        <div>
          <p className="eyebrow teal">HEALTH / TRAINING</p>
          <h2>Training</h2>
          <p>
            {trainingReady
              ? `Swim, bike, run · ${totals.sessions || week.sessions} ${
                  (totals.sessions || week.sessions) === 1
                    ? "session"
                    : "sessions"
                } this week`
              : "Activity source unavailable"}
          </p>
        </div>
        <span className="tri-mark" aria-hidden="true">
          <Waves size={20} strokeWidth={1.3} />
          <Bike size={20} strokeWidth={1.3} />
          <Footprints size={20} strokeWidth={1.3} />
        </span>
      </div>

      <section className="training-hero" aria-label="This week">
        <IntensityRing
          moderate={intensity.moderate}
          vigorous={intensity.vigorous}
          known={intensity.known}
          reading={daily === undefined}
        />
        <div className="training-week">
          <p className="eyebrow">THIS WEEK · SINCE MONDAY</p>
          <div className="week-numbers">
            <div>
              <strong>{totals.sessions}</strong>
              <span>{totals.sessions === 1 ? "session" : "sessions"}</span>
            </div>
            <div>
              <strong>{totals.minutes ? hm(totals.minutes * 60) : "—"}</strong>
              <span>moving time</span>
            </div>
            <div>
              <strong>{totals.load ? Math.round(totals.load) : "—"}</strong>
              <span>training load</span>
            </div>
          </div>
          <WeekDots days={intensity.days} today={today} />
          {run > 1 && (
            <p className="streak">
              <Flame size={15} />
              <b>{run}-day streak</b>
              <span>· trained {run} days in a row</span>
            </p>
          )}
        </div>
      </section>

      <section className="body-section">
        <div className="body-heading">
          <p className="eyebrow">TWO WEEKS OF TRAINING LOAD</p>
          <span>
            {reading
              ? "reading Garmin…"
              : `${days.filter((d) => d.active).length} active days`}
          </span>
        </div>
        <LoadCalendar days={days} today={today} streak={run} />
      </section>

      {trainingReady && (
        <section className="body-section">
          <div className="body-heading">
            <p className="eyebrow">{sportsLabel}</p>
            <span>IRONMAN 70.3 distances for scale</span>
          </div>
          <div className="sport-lanes">
            {sports
              .filter((s) => s.sport !== "other" || s.sessions > 0)
              .map((s) => (
                <SportLane key={s.sport} week={s} />
              ))}
          </div>
          {data.workoutsCoverage && !data.workoutsCoverage.complete && (
            <p className="quiet-note" role="status">
              Some days haven't synced yet, so this week may read low.
            </p>
          )}
        </section>
      )}

      {trainingReady && sorted.length > 0 && (
        <section className="body-section">
          <div className="body-heading">
            <p className="eyebrow">RECENT WORKOUTS</p>
            <span>
              {sorted.length} recorded
              {demo ? " · fictional" : ""}
            </span>
          </div>
          <div className="workout-cards">
            {shown.map((w, i) => (
              <WorkoutCard key={w.id} workout={w} glucose={glucose} index={i} />
            ))}
          </div>
          {sorted.length > 4 && (
            <button
              className="show-more"
              aria-expanded={showAll}
              onClick={() => setShowAll(!showAll)}
            >
              {showAll ? "Show fewer" : `Show all ${sorted.length}`}
            </button>
          )}
        </section>
      )}

      <div className="quiet-stats">
        <div>
          <b>
            {hasMeasurement(data.steps) && data.steps.value != null
              ? data.steps.value.toLocaleString()
              : "—"}
          </b>
          <span>
            steps
            {data.steps.provenance.calendarDate
              ? ` · ${dayName(data.steps.provenance.calendarDate)}`
              : " · latest day"}
          </span>
        </div>
        {stepAverage != null && (
          <div>
            <b>{Math.round(stepAverage).toLocaleString()}</b>
            <span>steps a day · two weeks</span>
          </div>
        )}
        {intensity.known && (
          <div>
            <b>{intensity.vigorous}</b>
            <span>vigorous min this week</span>
          </div>
        )}
      </div>

      {demo && (
        <button className="linked-record" onClick={() => selectNode("base")}>
          <span>
            <b>Building a base</b>
            <small>Explore the sample training project.</small>
          </span>
          <ArrowUpRight size={18} />
        </button>
      )}
      <Origin value={data.workouts[0]?.provenance ?? data.steps.provenance} />
    </div>
  );
}

// Garmin's weekly intensity minutes, vigorous counted double, against 150.
function IntensityRing({
  moderate,
  vigorous,
  known,
  reading,
}: {
  moderate: number;
  vigorous: number;
  known: boolean;
  reading: boolean;
}) {
  const total = moderate + 2 * vigorous;
  const size = 220,
    c = size / 2,
    r = 96;
  const deg = (minutes: number) => (minutes / intensityGoal) * 360;
  const modEnd = Math.min(360, deg(moderate)),
    allEnd = Math.min(360, deg(total));
  const extra = Math.max(0, total - intensityGoal);
  return (
    <div
      className="intensity-ring"
      role="img"
      aria-label={
        known
          ? `${total} of ${intensityGoal} intensity minutes this week: ${moderate} moderate and ${vigorous} vigorous, counted double.`
          : "Intensity minutes unavailable."
      }
    >
      <svg viewBox={`0 0 ${size} ${size}`} aria-hidden="true">
        <defs>
          <linearGradient id="intensity-vigorous" x1="0" y1="0" x2="1" y2="1">
            <stop offset="0" stopColor="#7bdcbb" />
            <stop offset="1" stopColor="#c8f2e2" />
          </linearGradient>
        </defs>
        <circle className="intensity-track" cx={c} cy={c} r={r} />
        {[0.25, 0.5, 0.75].map((q) => (
          <circle
            key={q}
            className="intensity-tick"
            cx={c + r * Math.sin(q * 2 * Math.PI)}
            cy={c - r * Math.cos(q * 2 * Math.PI)}
            r={1.6}
          />
        ))}
        {known && (
          <>
            <Arc
              cx={c}
              cy={c}
              r={r}
              from={0}
              to={modEnd}
              className="intensity-moderate"
              width={14}
            />
            <Arc
              cx={c}
              cy={c}
              r={r}
              from={modEnd}
              to={allEnd}
              stroke="url(#intensity-vigorous)"
              width={14}
              delay={0.45}
            />
            {extra >= 8 && (
              <Arc
                cx={c}
                cy={c}
                r={r - 18}
                from={0}
                to={Math.min(360, deg(extra))}
                className="intensity-extra"
                width={4}
                delay={0.9}
              />
            )}
          </>
        )}
      </svg>
      <div className="intensity-centre">
        <strong>{known ? total : "—"}</strong>
        <span>
          of {intensityGoal} min
          {total >= intensityGoal && known ? " · goal met" : ""}
        </span>
        <small>intensity minutes</small>
        {known && (
          <em>
            {moderate} moderate · {vigorous} vigorous ×2
          </em>
        )}
        {!known && reading && <em>reading Garmin…</em>}
      </div>
    </div>
  );
}

function WeekDots({ days, today }: { days: IntensityDay[]; today: string }) {
  const max = Math.max(30, ...days.map((d) => d.minutes ?? 0));
  return (
    <ol className="week-dots" aria-label="Intensity minutes each day this week">
      {days.map((d) => (
        <li
          key={d.date}
          className={d.future ? "future" : d.date === today ? "today" : ""}
          aria-label={`${dayName(d.date, { weekday: "long" })}: ${d.future ? "to come" : d.minutes == null ? "not recorded" : `${Math.round(d.minutes)} intensity minutes`}`}
        >
          <span
            style={{
              ["--fill" as string]: d.minutes ? Math.sqrt(d.minutes / max) : 0,
            }}
          />
          <small>{dayName(d.date, { weekday: "narrow" })}</small>
        </li>
      ))}
    </ol>
  );
}

// Last week and this week as a calendar of dots: size is the day's training
// load, colour its main sport, and a soft chain joins days trained in a row.
function LoadCalendar({
  days,
  today,
  streak: run,
}: {
  days: TrainingDay[];
  today: string;
  streak: number;
}) {
  const max = Math.max(1, ...days.map((d) => d.load ?? 0));
  const streakFrom =
    run > 0
      ? addDays(
          days.filter((d) => d.date <= today).at(-1)!.active
            ? today
            : addDays(today, -1),
          -(run - 1),
        )
      : null;
  const inStreak = (d: TrainingDay) =>
    !!streakFrom && d.active && d.date >= streakFrom && d.date <= today;
  return (
    <div className="load-calendar">
      <div className="load-head" aria-hidden="true">
        <span />
        {days.slice(0, 7).map((d) => (
          <small key={d.date}>{dayName(d.date, { weekday: "narrow" })}</small>
        ))}
      </div>
      {[0, 1].map((row) => (
        <div className="load-row" key={row}>
          <span className="load-label">{row ? "This week" : "Last week"}</span>
          <ol aria-label={row ? "This week" : "Last week"}>
            {days.slice(row * 7, row * 7 + 7).map((d, i, week) => {
              const sport = d.sports[0] ?? "unknown";
              const size = d.active
                ? d.load
                  ? 0.42 + 0.58 * Math.sqrt(d.load / max)
                  : 0.5
                : 0;
              const joined = d.active && i > 0 && week[i - 1].active;
              return (
                <li
                  key={d.date}
                  className={[
                    "load-day",
                    d.active ? "active" : "",
                    d.future ? "future" : "",
                    d.date === today ? "today" : "",
                    joined ? "joined" : "",
                    joined && inStreak(d) ? "streaking" : "",
                    `sport-${sport}`,
                  ].join(" ")}
                  title={describeDay(d)}
                  aria-label={describeDay(d)}
                >
                  <motion.span
                    className="load-dot"
                    initial={{ scale: 0 }}
                    animate={{ scale: 1 }}
                    transition={{ delay: 0.05 * (row * 7 + i), duration: 0.4 }}
                    style={{ ["--size" as string]: size }}
                  >
                    {d.load ? <b>{Math.round(d.load)}</b> : null}
                  </motion.span>
                  <small>{Number(d.date.slice(8))}</small>
                </li>
              );
            })}
          </ol>
        </div>
      ))}
      <div className="load-legend" aria-hidden="true">
        {(["swim", "bike", "run", "other"] as const)
          .filter((s) => days.some((d) => d.sports.includes(s)))
          .map((s) => (
            <span key={s} className={`sport-${s}`}>
              <i />
              {sportName[s]}
            </span>
          ))}
        <span className="legend-size">bigger dot · more load</span>
      </div>
    </div>
  );
}
function describeDay(d: TrainingDay) {
  const when = dayName(d.date);
  if (d.future) return `${when}: still to come`;
  if (!d.active) return `${when}: rest`;
  return `${when}: ${[
    d.sports.length
      ? d.sports.map((s) => sportName[s]).join(" + ")
      : "training",
    d.minutes ? hm(d.minutes * 60) : null,
    d.load ? `load ${Math.round(d.load)}` : null,
  ]
    .filter(Boolean)
    .join(", ")}`;
}

function SportLane({ week: s }: { week: SportWeek }) {
  const Icon = s.sport === "other" ? Activity : sportIcon[s.sport];
  const leg = s.sport === "other" ? null : raceLeg[s.sport];
  const ratio = leg && s.meters > 0 ? s.meters / leg : null;
  const distance = formatDistance(
    s.meters,
    s.sport,
    s.yards ? "yard" : "meter",
  );
  return (
    <div className={`sport-lane sport-${s.sport}${s.sessions ? "" : " empty"}`}>
      <span className="sport-icon">
        <Icon size={18} strokeWidth={1.5} />
      </span>
      <div className="sport-main">
        <div className="sport-line">
          <b>{sportName[s.sport]}</b>
          <span>
            {s.sessions
              ? `${s.sessions} ${s.sessions === 1 ? "session" : "sessions"} · ${hm(s.minutes * 60)}`
              : s.sport === "swim"
                ? "no swims yet"
                : s.sport === "bike"
                  ? "no rides yet"
                  : "no runs yet"}
          </span>
          <strong>{distance ?? (s.sessions ? "—" : "")}</strong>
        </div>
        {leg && (
          <div className="sport-track" aria-hidden="true">
            <motion.span
              initial={{ width: 0 }}
              animate={{ width: `${Math.min(1, ratio ?? 0) * 100}%` }}
              transition={{ duration: 0.9, ease: [0.2, 0.7, 0.2, 1] }}
            />
          </div>
        )}
        {leg && (
          <small>
            {ratio == null
              ? `race ${formatDistance(leg, s.sport, s.yards ? "yard" : "meter")}`
              : ratio >= 1
                ? `${ratio.toFixed(1)}× the 70.3 ${s.sport === "bike" ? "ride" : s.sport}`
                : `${Math.round(ratio * 100)}% of the 70.3 ${s.sport === "bike" ? "ride" : s.sport} · ${formatDistance(leg, s.sport, s.yards ? "yard" : "meter")}`}
          </small>
        )}
      </div>
    </div>
  );
}

function Effect({ label, value }: { label: string; value: number }) {
  const v = Math.max(0, Math.min(5, value));
  return (
    <div className="effect">
      <span className="effect-label">{label}</span>
      <span className="effect-bars" aria-hidden="true">
        {[0, 1, 2, 3, 4].map((i) => (
          <i key={i}>
            <motion.b
              initial={{ width: 0 }}
              animate={{ width: `${Math.max(0, Math.min(1, v - i)) * 100}%` }}
              transition={{ delay: 0.2 + i * 0.08, duration: 0.35 }}
            />
          </i>
        ))}
      </span>
      <b>{v.toFixed(1)}</b>
      <small>{effectWord(v)}</small>
    </div>
  );
}

function WorkoutCard({
  workout: w,
  glucose,
  index,
}: {
  workout: Workout;
  glucose: { timestamp: string; value: number }[];
  index: number;
}) {
  const sport = sportOf(w.activityType);
  const Icon =
    sport === "other" && w.activityType !== "strength_training"
      ? Activity
      : sportIcon[sport];
  const distance = workoutDistance(w);
  const [amount, unit] = distance ? distance.split(" ") : [];
  const during = workoutGlucose(w, glucose);
  const when = Number.isFinite(Date.parse(w.startedAt))
    ? `${dayName(w.provenance.calendarDate ?? w.startedAt.slice(0, 10))} · ${new Date(w.startedAt).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })}`
    : "time not recorded";
  const hasEffects = w.aerobicEffect != null || w.anaerobicEffect != null;
  return (
    <motion.article
      className={`workout-card sport-${sport}`}
      initial={{ opacity: 0, y: 8 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ delay: 0.05 * index, duration: 0.35 }}
    >
      <span className="sport-icon">
        <Icon size={19} strokeWidth={1.5} />
      </span>
      <div className="workout-main">
        <header>
          <b>
            {activityName(w)}
            {w.multisport === "leg" && <em> · multisport leg</em>}
          </b>
          <small>{when}</small>
        </header>
        <div className="workout-stats">
          {amount && (
            <span>
              <strong>{amount}</strong> {unit}
            </span>
          )}
          {w.durationMinutes != null && (
            <span>
              <strong>{hm(w.durationMinutes * 60)}</strong>
            </span>
          )}
          {w.averageHeartRate != null && (
            <span>
              <HeartPulse size={13} />
              <strong>{w.averageHeartRate}</strong> avg bpm
            </span>
          )}
          {w.trainingLoad != null && (
            <span>
              <strong>{w.trainingLoad}</strong> load
            </span>
          )}
        </div>
        {hasEffects && (
          <div className="effects">
            {w.aerobicEffect != null && (
              <Effect label="Aerobic" value={w.aerobicEffect} />
            )}
            {w.anaerobicEffect != null && (
              <Effect label="Anaerobic" value={w.anaerobicEffect} />
            )}
          </div>
        )}
        {during && (
          <div className="workout-glucose">
            <GlucoseLine
              points={during.points}
              start={during.start - 15 * 60000}
              end={during.end + 30 * 60000}
              span={[during.start, during.end]}
              height={40}
              label={`Glucose during the workout: ${during.from} to ${during.to} mg/dL, lowest ${during.lowest}.`}
            />
            <p>
              glucose <b>{during.from}</b> → <b>{during.to}</b> mg/dL during
              {during.lowest < 70 && (
                <span className="low-words"> · dipped to {during.lowest}</span>
              )}
            </p>
          </div>
        )}
      </div>
    </motion.article>
  );
}
