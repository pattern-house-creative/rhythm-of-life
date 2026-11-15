import { personName } from "./instance";
import { statusNames } from "./memories";
import type { Memory, MemoryChange, MemoryHistory } from "./memories";
import { counted, dayOf, rowGroup, when } from "./memoryScreen";

// A memory's history as a timeline (SB-P6-01): its changes newest first under the day each came, who or what made each (an AI
// client's named as one, never as a person, even a client that speaks for a person), what it did and how its words read then;
// a revision still kept can be read and put back. Earlier pages come on asking, and it says what is no longer kept or can't be
// seen.

// Who made a change: a person at the app or the terminal, or an AI client or a capture connection under the name it was given,
// stored data, so never shown bare, where a client calling itself a person or the terminal would read as one.
const named = (what: string, client: string | null) =>
  client ? `${what} (${client})` : what;
export const madeBy = (c: MemoryChange) =>
  c.channel === "mcp"
    ? c.person
      ? `${named("an AI client", c.client)} for ${personName(c.person)}`
      : named("an AI client", c.client)
    : c.channel === "capture"
      ? named("a capture connection", c.client)
      : c.person
        ? personName(c.person)
        : c.channel === "app"
          ? "the app"
          : c.channel === "cli"
            ? "the terminal"
            : "Rhythm";

export const happened = (c: MemoryChange) => {
  switch (c.event) {
    case "created":
      return "Saved";
    case "appended":
      return "Update added";
    case "updated":
      return `Edited${c.fields.length ? ` (${c.fields.join(", ")})` : ""}`;
    case "status_changed":
      return `Marked ${c.status ? statusNames[c.status].toLowerCase() : "again"}`;
    case "released":
      return "Released from review";
    case "reverted":
      return c.to ? `Put back as at revision ${c.to}` : "Undone";
    case "trashed":
      return "Moved to the trash";
    case "restored":
      return "Brought back from the trash";
    case "capsule_changed":
      return "Capsule slot changed";
    case "imported":
      return "Imported";
    // A move between its author's own memories and Family (run 14; migration 048).
    case "shared":
      return "Shared with Family";
    case "unshared":
      return "Taken back from Family";
    // When it was true, one memory replacing it, and the retraction cascade (run 14; migration 049).
    case "validity_changed":
      return "When it was true changed";
    case "superseded":
      return "Replaced by a newer memory";
    case "reopened":
      return "Current again: what replaced it was marked wrong";
    case "reclosed":
      return "Replaced again: what replaced it is back";
    case "flagged":
      return "Flagged for a check: a memory it was built on was marked wrong";
    case "unflagged":
      return "No longer flagged";
    case "flags_cleared":
      return "Checked: its flags taken off";
    // Still true (run 15's engine, a person's step since run 17; migration 053).
    case "confirmed":
      return "Checked: still true";
    // A person's word on the brief's lists, and an AI client's (migrations 058, 060 and 061; a person's step since SB-P6-01).
    case "resolved":
      return "Its task was resolved";
    case "outcome":
      return "Its outcome was recorded";
    case "due_changed":
      return "Its date moved or came off";
    default:
      return c.event.replace(/_/g, " ");
  }
};

export function MemoryTimeline({
  memory,
  history,
  onRead,
  onPutBack,
  onEarlier,
}: {
  memory: Memory;
  history: MemoryHistory;
  onRead: (revision: number) => void;
  onPutBack: (revision: number) => void;
  onEarlier: () => void;
}) {
  return (
    <>
      <ol className="memory-history" aria-label="History">
        {history.changes.map((c, i) => {
          const day = dayOf(c.at);
          const first = i === 0 || dayOf(history.changes[i - 1].at) !== day;
          return (
            <li
              key={`${c.revision}-${c.event}-${c.at}`}
              className={c.channel === "mcp" ? "memory-by-ai" : undefined}
            >
              {first && <span className="memory-day">{day}</span>}
              <span className="memory-meta">
                Revision {c.revision} · {happened(c)} · {madeBy(c)} ·{" "}
                {when(c.at)}
              </span>
              {c.preview && <span className="memory-preview">{c.preview}</span>}
              {c.kept && c.revision !== memory.revision && (
                <span
                  className="memory-actions"
                  {...rowGroup(`Revision ${c.revision}`)}
                >
                  <button onClick={() => onRead(c.revision)}>Read</button>
                  <button onClick={() => onPutBack(c.revision)}>
                    Put back
                  </button>
                </span>
              )}
            </li>
          );
        })}
        {history.cut && (
          <li className="memory-hint">
            Earlier changes were made where you can't see them.
          </li>
        )}
      </ol>
      {history.next !== null && (
        <button className="secondary-action memory-more" onClick={onEarlier}>
          Show earlier changes
        </button>
      )}
      {history.pruned && (
        <p className="memory-hint">
          Each entry stays, but only{" "}
          {history.oldestKept > 1
            ? `revisions from ${history.oldestKept} on`
            : counted(history.oldestKept, "revision", "revisions")}{" "}
          can still be read or put back.
        </p>
      )}
    </>
  );
}
