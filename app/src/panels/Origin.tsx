import type { HealthProvenance } from "../model";
import { observationLabel, stamp } from "../health";
import { sourceName } from "../graphView";

// Where a reading came from and when: shown at the foot of each panel.
export function Origin({ value }: { value: HealthProvenance }) {
  return (
    <div className="origin">
      <span className="tag">
        {value.demo
          ? "FICTIONAL SAMPLE"
          : !value.recordId
            ? "NO SOURCE RECORD"
            : value.kind === "live"
              ? "ROLLING SOURCE"
              : value.kind === "archive"
                ? "HISTORICAL ARCHIVE"
                : "SOURCE RECORD"}
      </span>
      <p>
        {sourceName(value.sourceId)} · {observationLabel(value)}
      </p>
      <small>
        {value.lastSyncedAt
          ? `Last successful sync ${stamp(value.lastSyncedAt)}. `
          : "Last successful sync not reported. "}
        {value.sourceUpdatedAt
          ? `Source modified ${stamp(value.sourceUpdatedAt)}. `
          : ""}
        {value.recordId
          ? `Record ${value.recordId}.`
          : "No source record available."}{" "}
        {value.demo
          ? "Fictional measurements."
          : value.retrievedAt
            ? `Retrieved ${stamp(value.retrievedAt)}.`
            : "Retrieval time unavailable."}
      </small>
    </div>
  );
}
