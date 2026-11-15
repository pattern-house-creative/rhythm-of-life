import { useEffect, useRef, useState } from "react";
import { holdWhy, statusNames } from "./memories";
import type {
  MemoryReads,
  MemoryWritten,
  StandingInstruction,
  StandingReason,
} from "./memories";
import { rowGroup, scopeName, when } from "./memoryScreen";
import type { MemoryTools } from "./memoryScreen";
import { useLayer } from "./MemoryQueues";

// Standing instructions in the Memories view (SB-P6-01, run 22; D-236's proposal; SB-P5-12, migration 065): the notes a person asked to
// have applied whenever their topic comes up, oldest first, as an AI client's list_standing reads them, each with whether it fires and why
// not, and how many of the list may fire, read with it. A person opens one as any memory, or stops one: it fires no more and stays a memory,
// through the same checked writer as the MCP tool (op stop_standing, at the revision shown), and Undo starts it again. A held one comes
// without its words until a person releases it in Review. Its words are stored data, written by a person or an AI client.

const whyNot: Record<StandingReason, string> = {
  held: "held for review, so it never fires until a person releases it",
  project_held:
    "in a project held for review, so no question can name it and it never fires",
  over_limit: "past the oldest 50 of its scope, so it waits for a place",
};

export function MemoryStandingList({ tools }: { tools: MemoryTools }) {
  // The list as far as it is read, and the counts beside it, which come with each page from the statement that read it.
  const [read, setRead] = useState<MemoryReads["standing"] | null>(null);
  const stamp = useRef(0);
  // A stop being asked about: it fires no more once a person says so.
  const [stopping, setStopping] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  useLayer(stopping !== null, () => setStopping(null), tools);

  async function load(more = false) {
    const asked = ++stamp.current;
    const reply = await tools.call<MemoryReads["standing"]>({
      op: "standing",
      limit: 20,
      ...(more && read?.next ? { cursor: read.next } : {}),
    });
    if (!reply || asked !== stamp.current) return;
    setRead((was) => ({
      ...reply,
      standing:
        more && was ? [...was.standing, ...reply.standing] : reply.standing,
    }));
  }
  useEffect(() => {
    void load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  const again = () => load();

  async function undo(id: string, revision: number) {
    const reply = await tools.call<{ written: MemoryWritten }>({
      op: "undo",
      id,
      expected: revision,
    });
    if (reply)
      tools.say({
        text: "Started again: it fires once more when its topic comes up.",
      });
    await again();
  }

  async function stop(s: StandingInstruction) {
    setStopping(null);
    setBusy(true);
    const reply = await tools.call<{ written: MemoryWritten }>({
      op: "stop_standing",
      id: s.id,
      expected: s.revision,
    });
    setBusy(false);
    if (reply) {
      const w = reply.written;
      tools.say({
        text: w.unchanged
          ? "Nothing changed: it was stopped already."
          : "Stopped: it fires no more and stays a memory. What it was is kept in its history.",
        action:
          w.unchanged || w.revision === null
            ? undefined
            : { label: "Undo", run: () => void undo(w.id, w.revision!) },
      });
    }
    await again();
  }

  const count = read?.count;
  return (
    <>
      <p className="memory-hint">
        {count === undefined
          ? "Standing instructions wait here."
          : count === 0
            ? "No standing instructions yet."
            : `${count === 1 ? "1 standing instruction" : `${count} standing instructions`}; ${
                read!.firing === count
                  ? count === 1
                    ? "it may fire"
                    : "each may fire"
                  : `${read!.firing} may fire`
              }.`}{" "}
        Each is a note a person asked to have applied when its topic comes up:
        an AI client reads it, with where it came from, above what its search
        found, at most {read?.maxFires ?? 2} at once. Stop one and it fires no
        more but stays a memory.
      </p>
      {read && !read.served && read.count > 0 && (
        <p className="memory-hint">
          None fires just now: recall or the local model is off in this install.
        </p>
      )}
      <ul className="memory-list" aria-label="Standing instructions">
        {read?.standing.map((s) => {
          const words = s.held ? "Held for review" : (s.content ?? "");
          return (
            <li key={s.id} className="memory-held-item">
              <span className="memory-preview">{words}</span>
              <span className="memory-meta">
                {statusNames[s.status]} · {scopeName(s)} ·{" "}
                {s.fires ? "may fire" : `doesn't fire: ${whyNot[s.reason!]}`}
                {s.project ? ` · only in ${s.project.slug}` : ""} · since{" "}
                {when(s.createdAt)}
              </span>
              {s.held && (
                <span className="memory-hint">
                  {holdWhy[s.held.reason]}. Read it and release it in Review.
                </span>
              )}
              {stopping === s.id ? (
                <span
                  className="memory-confirm"
                  role="group"
                  aria-label="Stop this instruction?"
                >
                  <span>
                    Stop it? It fires no more and stays a memory, and you can
                    start it again with Undo.
                  </span>
                  <button
                    className="memory-danger"
                    disabled={busy}
                    onClick={() => void stop(s)}
                  >
                    Stop it
                  </button>
                  <button onClick={() => setStopping(null)}>Keep it</button>
                </span>
              ) : (
                <span className="memory-actions" {...rowGroup(words)}>
                  <button onClick={() => tools.open(s.id)}>Open</button>
                  {!s.held && (
                    <button disabled={busy} onClick={() => setStopping(s.id)}>
                      Stop it…
                    </button>
                  )}
                </span>
              )}
            </li>
          );
        })}
      </ul>
      {read && !read.standing.length && (
        <p className="memory-empty">
          No standing instructions. An AI client saves one when a person asks it
          to remember something for whenever a topic comes up.
        </p>
      )}
      {read?.next && (
        <button
          className="secondary-action memory-more"
          onClick={() => void load(true)}
        >
          Show more
        </button>
      )}
    </>
  );
}
