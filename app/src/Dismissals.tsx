import { useState } from "react";
import type { MapDismissal } from "./memories";
import type { KeptDismissals } from "./keptDismissals";
import { displayTitle } from "./graph";

// The dismissals on this person's map, newest first, each with a way to show it again where they
// may. `words` names a link type as the explorer does.
export function DismissedList({
  kept,
  words,
  onShown,
}: {
  kept: KeptDismissals;
  words: (type: string) => string;
  onShown: (ids: string[]) => void;
}) {
  const [busy, setBusy] = useState<string | null>(null);
  if (!kept.list.length) return null;
  const by = (d: MapDismissal) =>
    d.mine
      ? "dismissed by you"
      : d.by === "person"
        ? "dismissed by a person"
        : "dismissed by an AI client";
  return (
    <section className="kept-dismissals" aria-label="Dismissed suggestions">
      <h3>Dismissed</h3>
      <p className="quiet-note">
        Kept off the map through every rebuild until someone shows them again.
      </p>
      <ul className="item-rows">
        {kept.list.map((d) => {
          const [a, b] = d.stars.map((s) => displayTitle(s.title));
          return (
            <li key={d.id}>
              <span className="row-main">
                <span className="row-title">
                  {a} · {b}
                </span>
                <small className="row-relation">
                  {words(d.type)} · {by(d)}
                </small>
              </span>
              {d.mayRestore && (
                <button
                  className="secondary-action"
                  disabled={busy === d.id}
                  aria-label={`Show again: ${a} and ${b}`}
                  onClick={() => {
                    setBusy(d.id);
                    void kept.restore(d).then((ids) => {
                      setBusy(null);
                      onShown(ids);
                    });
                  }}
                >
                  Show again
                </button>
              )}
            </li>
          );
        })}
      </ul>
      {kept.truncated && (
        <p className="quiet-note">Only the newest are listed.</p>
      )}
    </section>
  );
}
