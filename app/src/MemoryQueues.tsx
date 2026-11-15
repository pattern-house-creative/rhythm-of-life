import { useEffect, useRef, useState } from "react";
import { personName } from "./instance";
import { holdWhy, visibleText } from "./memories";
import type {
  Contradiction,
  ContradictionSide,
  DuplicateMemory,
  MemoryHold,
  MemoryReads,
  MemoryResolved,
  MemoryWritten,
} from "./memories";
import { dayOf, rowGroup, scopeName, when } from "./memoryScreen";
import type { MemoryTools } from "./memoryScreen";

// Two of a person's review queues (SB-P6-01; D-218, D-222): possible copies with their list and keep both, and possible contradictions
// with a person's settling. Both go through the engine as the app (app_memories, a person's channel), each step through one door (op
// resolve, MemoryStore.ResolveItem) at the revision shown, so an item leaves its queue once; no AI client's token reaches them. Each
// count on screen is the length of the list under it, read with it. A question is asked in the same flow, never in a popup, and Escape
// takes it back first (tools.layer). Only the side that began later can replace the other (049), so a pair's sides are named by when each
// began and not by which was checked; the earlier stands by taking the later out, which ends the review until that memory comes back (068).

// Escape takes a question back first, while one is open.
export function useLayer(open: boolean, close: () => void, tools: MemoryTools) {
  useEffect(() => {
    if (!open) return;
    const { layer } = tools;
    layer.current = () => {
      close();
      return true;
    };
    return () => {
      layer.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, tools.layer]);
}

// One list of a queue, page by page: its items, the next page's place and the whole list's length, which comes with each page.
export function useQueue<A extends { next: string | null; count: number }, T>(
  tools: MemoryTools,
  request: Record<string, unknown>,
  items: (answer: A) => T[],
) {
  const [read, setRead] = useState<{
    items: T[];
    next: string | null;
    count: number;
  } | null>(null);
  const stamp = useRef(0);
  const key = JSON.stringify(request);
  async function load(more = false) {
    const asked = ++stamp.current;
    const reply = await tools.call<A>({
      ...request,
      limit: 20,
      ...(more && read?.next ? { cursor: read.next } : {}),
    });
    if (!reply || asked !== stamp.current) return;
    setRead((was) => ({
      items: more && was ? [...was.items, ...items(reply)] : items(reply),
      next: reply.next,
      count: reply.count,
    }));
  }
  useEffect(() => {
    setRead(null);
    void load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);
  return { read, load };
}

// A memory's whole words, read for a person where they are reviewing it, with the revision they read, and its hold: a memory a list
// showed may have been held for review since (get serves it whole with its hold, for a person to review, D-160), and then nothing is
// settled at it until it is released, since the engine refuses every step on a held memory.
type Whole = { content: string; revision: number; held: MemoryHold | null };
function useWhole(tools: MemoryTools) {
  const [whole, setWhole] = useState<Record<string, Whole>>({});
  async function readWhole(id: string) {
    const reply = await tools.call<MemoryReads["get"]>({ op: "get", id });
    const m = reply?.memory;
    if (m)
      setWhole((w) => ({
        ...w,
        [id]: { content: m.content ?? "", revision: m.revision, held: m.held },
      }));
  }
  return { whole, readWhole, forgetWhole: () => setWhole({}) };
}

// A memory read whole and found held: why, in place of its words, which are reviewed where it opens, with its release.
function HeldInstead({ held }: { held: MemoryHold }) {
  return (
    <p className="memory-hint" role="group" aria-label="Held for review">
      Held for review: {holdWhy[held.reason]}. Open it to review it; nothing is
      settled at it until it is released.
    </p>
  );
}

const alike = (score: number) =>
  score >= 1 ? "the same words" : `${Math.round(score * 100)}% alike`;

const statusWord = (status: string) =>
  status === "canonical"
    ? "confirmed"
    : status === "deprecated"
      ? "marked wrong"
      : "a draft";

export function MemoryCopies({ tools }: { tools: MemoryTools }) {
  const { read, load } = useQueue<MemoryReads["duplicates"], DuplicateMemory>(
    tools,
    { op: "duplicates" },
    (a) => a.duplicates,
  );
  const { whole, readWhole, forgetWhole } = useWhole(tools);
  // The question open on a row, if any: keep both, or forget this copy.
  const [asking, setAsking] = useState<{
    id: string;
    what: "keep" | "forget";
  } | null>(null);
  const [busy, setBusy] = useState(false);
  useLayer(asking !== null, () => setAsking(null), tools);
  // Every memory this one is close to read whole, and none of them held.
  const readAll = (item: DuplicateMemory) =>
    item.duplicateOf.every((copy) => whole[copy.id] && !whole[copy.id].held);

  async function done() {
    forgetWhole();
    await load();
    tools.waiting();
  }

  // Keep both, at the revision shown, once a person has read what it is close to and said so: the flags that review showed come off,
  // neither memory changes, and a pair of them that also waits as a possible contradiction is settled as both standing (068), which an
  // AI client then can't get round by ending or replacing either. Nothing takes it back, so it is asked first, and it names each
  // memory it is close to at the revision read whole: one changed since, or one the list shows now and was not read, is refused
  // with nothing changed (073), and the list is read again, so it is read again before keep both is offered.
  async function keep(item: DuplicateMemory) {
    setAsking(null);
    setBusy(true);
    const copies = item.duplicateOf
      .filter((copy) => whole[copy.id])
      .map((copy) => ({ id: copy.id, expected: whole[copy.id].revision }));
    const reply = await tools.call<MemoryResolved>({
      op: "resolve",
      id: item.id,
      action: "keep_copy",
      expected: item.revision,
      ...(copies.length ? { copies } : {}),
    });
    setBusy(false);
    if (reply)
      tools.say({
        text: reply.unchanged
          ? "Nothing changed: it was no longer flagged."
          : reply.stillCounted
            ? "Kept both. Another memory it was flagged against still stands, so it stays on this list."
            : "Kept both: neither memory changed, and they won't be flagged against each other again. Where they also waited as a possible contradiction, that is settled too: an AI client neither ends nor replaces one of them while the other stands.",
      });
    // A keep both may draw the link the flag held back, so the map is told as a settling's and a forget's are.
    if (reply && !reply.unchanged) tools.saved();
    await done();
  }

  // Forget this copy, at the revision shown: into the trash, which brings it back.
  async function forget(item: DuplicateMemory) {
    setAsking(null);
    setBusy(true);
    const reply = await tools.call<{ written: MemoryWritten }>({
      op: "forget",
      id: item.id,
      expected: item.revision,
    });
    setBusy(false);
    if (reply) {
      tools.say({
        text: `Moved to the trash. It is deleted for good after ${reply.written.trash?.days ?? 14} days unless you bring it back.`,
      });
      tools.saved();
    }
    await done();
  }

  return (
    <>
      <p className="memory-hint">
        {read
          ? read.count === 1
            ? "1 memory was flagged as a possible copy."
            : `${read.count} memories were flagged as possible copies.`
          : "Memories flagged as possible copies wait here."}{" "}
        Each is saved as it came, and searches find it as usual. Read what it is
        close to, then keep both, or forget the one you don't want.
      </p>
      <ul className="memory-list" aria-label="Possible copies">
        {read?.items.map((item) => (
          <li key={item.id} className="memory-held-item">
            <span className="memory-preview">
              {item.preview}
              {item.truncated ? "…" : ""}
            </span>
            <span className="memory-meta">
              {scopeName(item)} · saved {when(item.createdAt)} ·{" "}
              {statusWord(item.status)} · revision {item.revision}
            </span>
            <ul className="memory-copies" aria-label="What it is close to">
              {item.duplicateOf.map((copy) => (
                <li key={copy.id} className="memory-copy">
                  <span className="memory-meta">
                    Close to another memory: {alike(copy.score)}
                  </span>
                  {whole[copy.id]?.held ? (
                    <HeldInstead held={whole[copy.id].held!} />
                  ) : whole[copy.id] ? (
                    <p
                      className="source-content memory-text"
                      tabIndex={0}
                      role="group"
                      aria-label="Its words"
                    >
                      {visibleText(whole[copy.id].content)}
                    </p>
                  ) : (
                    <span className="memory-actions">
                      <button onClick={() => void readWhole(copy.id)}>
                        Read it
                      </button>
                    </span>
                  )}
                  <span className="memory-actions">
                    <button onClick={() => tools.open(copy.id)}>Open it</button>
                  </span>
                </li>
              ))}
            </ul>
            {asking?.id === item.id && asking.what === "forget" ? (
              <span
                className="memory-confirm"
                role="group"
                aria-label="Forget this copy?"
              >
                <span>
                  Forget this one? It goes to the trash; the other stays.
                </span>
                <button
                  className="memory-danger"
                  disabled={busy}
                  onClick={() => void forget(item)}
                >
                  Forget it
                </button>
                <button onClick={() => setAsking(null)}>Keep it</button>
              </span>
            ) : asking?.id === item.id && readAll(item) ? (
              <span
                className="memory-confirm"
                role="group"
                aria-label="Keep both?"
              >
                <span>
                  Keep both, each as it is? They won't be flagged against each
                  other again. Where they also wait as a possible contradiction,
                  that is settled too: an AI client neither ends nor replaces
                  one of them while the other stands. Nothing takes this back.
                </span>
                <button
                  className="memory-danger"
                  disabled={busy}
                  onClick={() => void keep(item)}
                >
                  Keep both
                </button>
                <button onClick={() => setAsking(null)}>Not yet</button>
              </span>
            ) : (
              <span className="memory-actions" {...rowGroup(item.preview)}>
                {/* Offered once every memory it is close to is read whole and none is held. When its row goes, focus goes on to
                    the next row's Read it (memoryFocus), never to a step on a pair nobody has read. */}
                {readAll(item) && (
                  <button
                    disabled={busy}
                    data-then="Read it"
                    onClick={() => setAsking({ id: item.id, what: "keep" })}
                  >
                    Keep both…
                  </button>
                )}
                <button
                  disabled={busy}
                  onClick={() => setAsking({ id: item.id, what: "forget" })}
                >
                  Forget this one…
                </button>
                <button onClick={() => tools.open(item.id)}>Open</button>
              </span>
            )}
          </li>
        ))}
      </ul>
      {read && !read.items.length && (
        <p className="memory-empty">Nothing is flagged as a possible copy.</p>
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

// Who named a pair a contradiction. A client's name and reason are its own words: stored data, never a channel of their own.
const namedBy = (k: Contradiction) => {
  if (!k.proposed) return null;
  const client = k.proposed.client
    ? `An AI client (${k.proposed.client})`
    : "An AI client";
  const who = k.proposed.person
    ? `${client} for ${personName(k.proposed.person)}`
    : client;
  return `${who} named it a contradiction${k.proposed.reason ? `: ${k.proposed.reason}` : "."}`;
};

const pairKey = (k: Contradiction) => `${k.memory.id}:${k.conflict.id}`;

// A question being asked of one pair: both stand, or one side stands.
type Settling = { key: string; stands: ContradictionSide | null };

// When a side began being true, to the millisecond a browser reads, so a side counted later here is later for the engine too; an unknown
// start is before every other, as the engine has it.
const began = (s: ContradictionSide) =>
  s.startedAt === null ? -Infinity : Date.parse(s.startedAt);
const otherSide = (k: Contradiction, s: ContradictionSide) =>
  s.id === k.memory.id ? k.conflict : k.memory;

export function MemoryConflicts({ tools }: { tools: MemoryTools }) {
  const { read, load } = useQueue<MemoryReads["contradictions"], Contradiction>(
    tools,
    { op: "contradictions" },
    (a) => a.contradictions,
  );
  const { whole, readWhole, forgetWhole } = useWhole(tools);
  const [settling, setSettling] = useState<Settling | null>(null);
  const [busy, setBusy] = useState(false);
  useLayer(settling !== null, () => setSettling(null), tools);

  // The revision a side was read at: what its words were read whole at, else what the list shows.
  const revisionOf = (side: ContradictionSide) =>
    whole[side.id]?.revision ?? side.revision;

  // A person's settling at the revisions read: both stand, or the side named, which began later, replaces the other. One that changed
  // since, or one the engine refuses (why is on screen), reads the list again, so what is shown is what is settled next.
  async function settle(k: Contradiction, stands: ContradictionSide | null) {
    setSettling(null);
    setBusy(true);
    const first = stands ?? k.memory;
    const other = otherSide(k, first);
    const reply = await tools.call<MemoryResolved>({
      op: "resolve",
      id: first.id,
      action: stands ? "settle_replace" : "settle_keep_both",
      target: other.id,
      expected: revisionOf(first),
      targetExpected: revisionOf(other),
    });
    setBusy(false);
    if (reply) {
      tools.say({
        text: reply.unchanged
          ? `Nothing changed: this was settled already (${reply.decision === "replaced" ? "one replaced the other" : "both stand"}).`
          : reply.decision === "replaced"
            ? "Settled: the one you chose stands, and the other is kept as replaced. Searches, capsules and AI clients get the one that stands; open it to undo."
            : "Settled: both stand. An AI client neither ends nor replaces one of them while the other stands.",
      });
      if (!reply.unchanged) tools.saved();
    }
    forgetWhole();
    await load();
    tools.waiting();
  }

  // A side that did not begin later stands by taking the other out, at the revision read: marked wrong, or into the trash. Either ends the
  // review, and it comes back with that memory.
  async function takeOut(other: ContradictionSide, how: "wrong" | "forget") {
    setSettling(null);
    setBusy(true);
    const expected = revisionOf(other);
    const reply = await tools.call<{ written: MemoryWritten }>(
      how === "wrong"
        ? { op: "status", id: other.id, status: "deprecated", expected }
        : { op: "forget", id: other.id, expected },
    );
    setBusy(false);
    if (reply) {
      tools.say({
        text:
          how === "wrong"
            ? "This one stands: the other is marked wrong, so searches, capsules and AI clients don't get it. If it stops being marked wrong, this pair comes back."
            : `This one stands: the other is in the trash, deleted for good after ${reply.written.trash?.days ?? 14} days unless you bring it back, and then this pair comes back with it.`,
      });
      tools.saved();
    }
    forgetWhole();
    await load();
    tools.waiting();
  }

  // A side read whole and found held since the list was read.
  const heldSide = (k: Contradiction) =>
    [k.memory, k.conflict].some((s) => whole[s.id]?.held);

  const side = (k: Contradiction, s: ContradictionSide, name: string) => (
    <div className="memory-side" role="group" aria-label={name}>
      {whole[s.id]?.held ? (
        <HeldInstead held={whole[s.id].held!} />
      ) : whole[s.id] ? (
        <p
          className="source-content memory-text"
          tabIndex={0}
          role="group"
          aria-label="Its words"
        >
          {visibleText(whole[s.id].content)}
        </p>
      ) : (
        <span className="memory-preview">
          {s.preview}
          {s.truncated ? "…" : ""}
        </span>
      )}
      <span className="memory-meta">
        {s.author ? `${personName(s.author)}'s · ` : ""}
        {statusWord(s.status)} · revision {revisionOf(s)} ·{" "}
        {s.startedAt ? `true from ${dayOf(s.startedAt)}` : "start unknown"}
      </span>
      <span className="memory-actions">
        {s.truncated && !whole[s.id] && (
          <button onClick={() => void readWhole(s.id)}>Read all of it</button>
        )}
        {k.maySettle && !heldSide(k) && (
          <button
            disabled={busy}
            onClick={() => setSettling({ key: pairKey(k), stands: s })}
          >
            This one stands…
          </button>
        )}
        <button onClick={() => tools.open(s.id)}>Open</button>
      </span>
    </div>
  );

  return (
    <>
      <p className="memory-hint">
        {read
          ? read.count === 1
            ? "1 pair of memories may conflict."
            : `${read.count} pairs of memories may conflict.`
          : "Pairs of memories that may conflict wait here."}{" "}
        They are close enough that one may contradict the other. Neither changes
        until a person settles it, and an AI client never does.
      </p>
      <ul className="memory-list" aria-label="Possible contradictions">
        {read?.items.map((k) => {
          const asked = settling?.key === pairKey(k) ? settling : null;
          const named = namedBy(k);
          const level = began(k.memory) === began(k.conflict);
          const [earlier, later] =
            began(k.memory) < began(k.conflict)
              ? [k.memory, k.conflict]
              : [k.conflict, k.memory];
          // The side a question names, and whether it can replace the other: only if it began later.
          const stands = asked?.stands ?? null;
          const replaces = stands !== null && !level && stands.id === later.id;
          return (
            <li key={pairKey(k)} className="memory-held-item">
              <span className="memory-meta">
                {scopeName(k)} · {Math.round(k.score * 100)}% alike ·{" "}
                {when(k.offeredAt)}
              </span>
              {named && <p className="memory-hint">{named}</p>}
              <div className="memory-pair">
                {side(k, earlier, level ? "One memory" : "The earlier memory")}
                {side(
                  k,
                  later,
                  level ? "The other memory" : "The later memory",
                )}
              </div>
              {heldSide(k) ? (
                <span className="memory-hint">
                  One of them is held for review: open it and release it before
                  settling the pair.
                </span>
              ) : asked ? (
                <span
                  key={stands?.id ?? "both"}
                  className="memory-confirm"
                  role="group"
                  aria-label="Settle this pair?"
                >
                  <span>
                    {!stands
                      ? "Both stand, each as it is? An AI client neither ends nor replaces one of them while the other stands."
                      : replaces
                        ? "This one stands and replaces the other, whose words are kept as they were? Searches, capsules and AI clients will get this one."
                        : `${level ? "Neither began after the other, so neither can replace the other." : "This one began first, and only the one that began later can replace the other."} For this one to stand, take the other out: mark it wrong, or forget it into the trash, which brings it back. If it comes back, so does this pair.`}
                  </span>
                  {stands && !replaces ? (
                    <>
                      <button
                        className="memory-danger"
                        disabled={busy}
                        onClick={() =>
                          void takeOut(otherSide(k, stands), "wrong")
                        }
                      >
                        Mark the other wrong
                      </button>
                      <button
                        className="memory-danger"
                        disabled={busy}
                        onClick={() =>
                          void takeOut(otherSide(k, stands), "forget")
                        }
                      >
                        Forget the other
                      </button>
                    </>
                  ) : (
                    <button
                      className="memory-danger"
                      disabled={busy}
                      onClick={() => void settle(k, stands)}
                    >
                      {stands ? "Replace the other" : "Keep both"}
                    </button>
                  )}
                  <button onClick={() => setSettling(null)}>Not yet</button>
                </span>
              ) : k.maySettle ? (
                <span className="memory-actions">
                  <button
                    disabled={busy}
                    onClick={() =>
                      setSettling({ key: pairKey(k), stands: null })
                    }
                  >
                    Both stand…
                  </button>
                </span>
              ) : (
                <span className="memory-hint">
                  An author of one of them or a Family admin settles it.
                </span>
              )}
            </li>
          );
        })}
      </ul>
      {read && !read.items.length && (
        <p className="memory-empty">No possible contradiction waits.</p>
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
