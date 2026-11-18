import type { ReactNode } from "react";
import { ArrowUpRight, ChevronRight, Heart, Moon } from "lucide-react";
import {
  hasMeasurement,
  healthCaption,
  healthStatus,
  observationLabel,
} from "../health";
import { sleepDuration } from "../model";
import type { ObservatoryData } from "../model";
import { garminOn } from "./partData";

// The recovery tile (docs/dev/screens.md 10.3): last night's sleep, resting
// heart rate and overnight HRV. Home's recovery corner draws it, and so does a
// dashboard's recovery part. The whole tile opens the recovery panel.
export function RecoveryPart({
  data,
  blocked,
  onOpen,
}: {
  data: ObservatoryData;
  // The demo's preview of a missing or failed source shows no readings.
  blocked: boolean;
  onOpen: () => void;
}) {
  if (!garminOn(data))
    return <QuietTile icon={<Moon size={14} />} label="RECOVERY" />;
  const signals = data.recovery.metrics;
  const sleepReady = hasMeasurement(signals?.sleep ?? data.recovery);
  const heartReady = hasMeasurement(signals?.restingHeartRate ?? data.recovery);
  const hrvReady = hasMeasurement(signals?.overnightHrv ?? data.recovery);
  return (
    <button className="tile-button" onClick={onOpen}>
      <div className="section-label">
        <span>
          <Moon size={14} />
          RECOVERY
        </span>
        <ArrowUpRight size={16} />
      </div>
      <>
        <div className="sleep-value">
          {blocked || !sleepReady
            ? "—"
            : sleepDuration(data.recovery.sleepMinutes)}
          <span className="sleep-observation">
            sleep
            {!blocked &&
              sleepReady &&
              ` · ${observationLabel((signals?.sleep ?? data.recovery).provenance)}`}
            {signals?.sleep.status === "stale" ? " · stale" : ""}
          </span>
        </div>
        <div className="recovery-pair">
          <span>
            <Heart size={13} />
            Resting HR{" "}
            <b>
              {blocked || !heartReady
                ? "—"
                : (data.recovery.restingHeartRate ?? "—")}{" "}
              <small>bpm</small>
            </b>
            {!blocked && heartReady && (
              <small>
                {observationLabel(
                  (signals?.restingHeartRate ?? data.recovery).provenance,
                )}
              </small>
            )}
            {signals && <small>{healthStatus(signals.restingHeartRate)}</small>}
          </span>
          <span>
            Overnight HRV{" "}
            <b>
              {blocked || !hrvReady
                ? "—"
                : (data.recovery.overnightHrvMs ?? "—")}{" "}
              <small>ms</small>
            </b>
            {!blocked && hrvReady && (
              <small>
                {observationLabel(
                  (signals?.overnightHrv ?? data.recovery).provenance,
                )}
              </small>
            )}
            {signals && <small>{healthStatus(signals.overnightHrv)}</small>}
          </span>
        </div>
        <p className="quiet-copy">
          Sleep and overnight signals. No readiness score inferred.
        </p>
        <div className="tile-footer">
          <span>
            {signals
              ? "Garmin · individual dates and freshness in details"
              : healthCaption(data.recovery)}
          </span>
          <ChevronRight size={13} />
        </div>
      </>
    </button>
  );
}

// A training source's tile while none is on: it keeps its place and says so
// quietly. Home without a grid gives that room away instead.
export function QuietTile({ icon, label }: { icon: ReactNode; label: string }) {
  return (
    <div className="tile-button quiet-tile">
      <div className="section-label">
        <span>
          {icon}
          {label}
        </span>
      </div>
      <p className="quiet-copy">Appears once a training source is connected</p>
    </div>
  );
}
