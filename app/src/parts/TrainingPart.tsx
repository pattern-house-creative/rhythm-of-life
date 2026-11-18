import { ArrowDownRight, ArrowUpRight, ChevronRight } from "lucide-react";
import { hasMeasurement, observationLabel } from "../health";
import { weeklyWorkouts } from "../model";
import type { ObservatoryData } from "../model";
import { activityName, workoutDistance } from "../activity";
import { garminOn } from "./partData";
import { QuietTile } from "./RecoveryPart";

// The training tile (docs/dev/screens.md 10.3): the latest activity and the
// week's sessions, day by day. Home's training corner draws it, and so does a
// dashboard's training part. The whole tile opens the training panel.
export function TrainingPart({
  data,
  blocked,
  demo,
  onOpen,
}: {
  data: ObservatoryData;
  // The demo's preview of a missing or failed source shows no readings.
  blocked: boolean;
  demo: boolean;
  onOpen: () => void;
}) {
  if (!garminOn(data))
    return <QuietTile icon={<ArrowDownRight size={15} />} label="TRAINING" />;
  const trainingReady = !["missing", "unavailable"].includes(
    data.workoutsStatus ?? "available",
  );
  const week = weeklyWorkouts(
    trainingReady ? data.workouts.filter((w) => hasMeasurement(w)) : [],
    demo
      ? data.current.provenance.observedAt
      : (data.workoutsCoverage?.calendarDate ??
          data.retrievedAt ??
          data.workouts[0]?.startedAt ??
          null),
  );
  // A multisport event shows as itself, not as its last transition leg.
  const latestWorkout = trainingReady
    ? data.workouts.find((w) => hasMeasurement(w) && w.multisport !== "leg")
    : undefined;
  const latestDistance = latestWorkout
    ? workoutDistance(latestWorkout)?.split(" ")
    : undefined;
  return (
    <button className="tile-button" onClick={onOpen}>
      <div className="section-label">
        <span>
          <ArrowDownRight size={15} />
          TRAINING
        </span>
        <ArrowUpRight size={16} />
      </div>
      <>
        <p className="workout-title">
          {blocked
            ? "Activity unavailable"
            : latestWorkout
              ? activityName(latestWorkout)
              : trainingReady
                ? "No recorded activity"
                : "Activity unavailable"}
        </p>
        <div className="run-value">
          {blocked || !latestWorkout
            ? "—"
            : (latestDistance?.[0] ??
              latestWorkout.durationMinutes ??
              "—")}{" "}
          <small>{latestDistance?.[1] ?? "min"}</small>
          <span className="workout-observation">
            {blocked || !latestWorkout
              ? "—"
              : [
                  latestDistance &&
                    `${latestWorkout.durationMinutes ?? "—"} min`,
                  latestWorkout.trainingLoad != null &&
                    `load ${latestWorkout.trainingLoad}`,
                ]
                  .filter(Boolean)
                  .join(" · ") || "recorded activity"}
            {!blocked &&
              latestWorkout &&
              ` · ${observationLabel(latestWorkout.provenance)}`}
          </span>
        </div>
        <div
          className="week-activity"
          aria-label={`${trainingReady ? week.sessions : "Unavailable"} ${demo ? "sample " : ""}workouts this week`}
        >
          {["M", "T", "W", "T", "F", "S", "S"].map((d, i) => (
            <div key={i}>
              <span
                className={!blocked && week.days[i] > 0 ? "active" : ""}
                style={{
                  // A share of the chart's own height, so the tallest
                  // day never rises over the text above it.
                  height: blocked
                    ? 7
                    : `max(7px, calc((100% - 18px) * ${
                        week.days[i] / Math.max(1, ...week.days)
                      }))`,
                }}
              />
              <small>{d}</small>
            </div>
          ))}
        </div>
        <p className="quiet-copy">
          {blocked
            ? "Steps unavailable"
            : `${hasMeasurement(data.steps) ? (data.steps.value?.toLocaleString() ?? "Unavailable") : "Unavailable"} ${data.steps.provenance.demo ? "sample " : ""}steps · ${observationLabel(data.steps.provenance)}`}
        </p>
        <div className="tile-footer">
          <span>
            {blocked
              ? "ACTIVITY UNAVAILABLE"
              : trainingReady
                ? `${week.sessions} RECORDED SESSIONS${data.workoutsCoverage && !data.workoutsCoverage.complete ? " · PARTIAL" : ""}`
                : "ACTIVITY UNAVAILABLE"}
          </span>
          <span>
            {blocked ||
            !trainingReady ||
            week.counted.some((w) => w.durationMinutes === null)
              ? "—"
              : `${week.minutes} min`}{" "}
            <ChevronRight size={12} />
          </span>
        </div>
      </>
    </button>
  );
}
