import { useEffect, useState } from "react";
import { counted } from "./memoryScreen";
import type { MemoryTools } from "./memoryScreen";

// Several memories at once (SB-P6-01): the bar under a list whose rows have been chosen, each of its steps behind a
// confirmation in the same flow (never a popup), and the one way each is sent and answered. Each memory is answered on its
// own by the engine: one refused leaves the others done, and the words say which went how. No mode: a row is chosen by its own
// box, and the bar appears only while one is.

export interface BulkAction {
  // The button, and what it asks before anything is sent; a step that needs no asking runs at once.
  label: string;
  asks?: string;
  go?: string;
  keep?: string;
  run: () => void | Promise<void>;
}

// A step's memories go in pages, so a long one fits one call's time on the way (each page is one call, never sent twice).
const page = 20;

// One step's memories, page by page. Its results, and whether a page failed (its refusal is on screen already). `member`: what the op
// names its list (a review queue's step for several names its items, run 22).
// eslint-disable-next-line react/only-export-components -- Shared feature API; edits to this module may reload its consumers.
export async function several<R>(
  tools: MemoryTools,
  op: string,
  memories: Record<string, unknown>[],
  extra: Record<string, unknown> = {},
  member = "memories",
): Promise<{ results: R[]; failed: boolean }> {
  const results: R[] = [];
  for (let i = 0; i < memories.length; i += page) {
    const reply = await tools.call<{ results: R[] }>({
      op,
      [member]: memories.slice(i, i + page),
      ...extra,
    });
    if (!reply) return { results, failed: true };
    results.push(...reply.results);
  }
  return { results, failed: false };
}

// What became of the rest, once the step's own words have said what went: how many changed since their person read them, how
// many weren't theirs or are gone, each only when there are any. `stays` is what a memory left behind does.
// eslint-disable-next-line react/only-export-components -- Shared feature API; edits to this module may reload its consumers.
export function severalWords(
  results: { result: string }[],
  done: readonly string[],
  failed: boolean,
) {
  const changed = results.filter((r) => r.result === "conflict").length;
  const refused = results.filter(
    (r) => !done.includes(r.result) && r.result !== "conflict",
  ).length;
  return [
    changed
      ? `${changed === 1 ? "1 changed" : `${changed} changed`} since you read ${changed === 1 ? "it" : "them"} and ${changed === 1 ? "stays" : "stay"} as ${changed === 1 ? "it is" : "they are"}: read ${changed === 1 ? "it" : "them"} again.`
      : "",
    refused
      ? `${refused === 1 ? "1 was" : `${refused} were`} not yours to change, or no longer there.`
      : "",
    failed ? "The rest didn't go: nothing more was sent." : "",
  ]
    .filter(Boolean)
    .join(" ");
}

export function BulkBar({
  count,
  shown,
  max,
  actions,
  onAll,
  onClear,
  tools,
  noun = ["memory", "memories"],
}: {
  count: number;
  // How many rows could be chosen now, and the most one step takes.
  shown: number;
  max: number;
  actions: BulkAction[];
  onAll: () => void;
  onClear: () => void;
  tools: MemoryTools;
  // What a row is, one and several, where it is not a memory (run 22: an insight, a pair).
  noun?: [string, string];
}) {
  const [asking, setAsking] = useState<BulkAction | null>(null);
  // A step on its way: its buttons stay off until it has answered, so a second tap never sends it again.
  const [busy, setBusy] = useState(false);
  async function go(action: BulkAction) {
    setBusy(true);
    try {
      await action.run();
    } finally {
      setBusy(false);
    }
  }
  // Escape takes the question back first; a changed choice does too, since it asked about another set.
  useEffect(() => {
    if (!asking) return;
    const { layer } = tools;
    // eslint-disable-next-line react/immutability -- Register the confirmation in the shared mutable Escape-handler ref until cleanup.
    layer.current = () => {
      setAsking(null);
      return true;
    };
    return () => {
      layer.current = null;
    };
  }, [asking, tools]);
  // eslint-disable-next-line react/set-state-in-effect -- A confirmation applies only to the selection it was opened for.
  useEffect(() => setAsking(null), [count]);
  if (count === 0) return null;
  return (
    <div
      className="memory-bulk"
      role="group"
      aria-label={`The ${noun[1]} you chose`}
      aria-busy={busy}
    >
      {asking ? (
        <span className="memory-confirm" role="group" aria-label={asking.label}>
          <span>{asking.asks}</span>
          <button
            className="memory-danger"
            onClick={() => {
              setAsking(null);
              void go(asking);
            }}
          >
            {asking.go}
          </button>
          <button onClick={() => setAsking(null)}>{asking.keep}</button>
        </span>
      ) : (
        <>
          <span className="memory-bulk-count">
            {counted(count, noun[0], noun[1])} chosen
          </span>
          <span className="memory-actions">
            {actions.map((action) => (
              <button
                key={action.label}
                disabled={busy}
                onClick={() =>
                  action.asks ? setAsking(action) : void go(action)
                }
              >
                {action.label}
              </button>
            ))}
            {count < Math.min(shown, max) && (
              <button disabled={busy} onClick={onAll}>
                Choose all {Math.min(shown, max)} shown
              </button>
            )}
            <button disabled={busy} onClick={onClear}>
              Clear choice
            </button>
          </span>
          {count >= max && (
            <span className="memory-hint">
              {max} are chosen, as many as one step takes: finish these, then
              choose more.
            </span>
          )}
        </>
      )}
    </div>
  );
}
