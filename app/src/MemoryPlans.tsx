import { useEffect, useRef, useState } from "react";
import type {
  DecisionRow,
  DueRow,
  MemoryCalibration,
  LoopDirection,
  MemoryOutcome,
  MemoryReads,
  MemoryWritten,
  OpenLoop,
} from "./memories";
import {
  counted,
  dayOf,
  installDay,
  rowGroup,
  scopeName,
  when,
} from "./memoryScreen";
import type { MemoryTools } from "./memoryScreen";

// The brief's lists (SB-P6-01; upstream's tasks, commitments and decisions views): what a person owes and is owed, what is due,
// and decisions with how they turned out. Each is the list the brief's count is the length of: the engine's page
// says its count from the statement that read it, and what is shown is what it says. A person's word on one goes through the app's
// channel (app_memories, resolve_loop, resolve_due, record_outcome), at the revision shown, so a tap on a row that changed since is
// refused and the list read again; each is a change like any other, so its notice offers Undo. No popup: a question asked of a row
// opens in that row, and what a list filters by is kept by MemoryView, so it survives opening a memory and coming back.

// How many rows a read asks for: the page, or as many as are shown, so a step on a long list leaves the person where they were.
const PAGE = 20;
const MOST = 50;
const asked = (shown: number) => Math.min(MOST, Math.max(PAGE, shown));

// A list's filter, one tap each, its count beside it.
function Strip<T extends string>({
  label,
  value,
  options,
  onChange,
}: {
  label: string;
  value: T;
  options: { id: T; label: string; count: number | null }[];
  onChange: (next: T) => void;
}) {
  return (
    <div
      className="memory-tabs memory-plan-strip"
      role="group"
      aria-label={label}
    >
      {options.map((o) => (
        <button
          key={o.id}
          aria-pressed={value === o.id}
          onClick={() => onChange(o.id)}
        >
          {o.label}
          {o.count === null ? "" : ` ${o.count}`}
        </button>
      ))}
    </div>
  );
}

const words = (row: { preview: string; truncated: boolean }) =>
  `${row.preview}${row.truncated ? "…" : ""}`;

// One step on a list: sent once at a time, what it did said with its Undo, and the list read again either way (a refusal says why
// on screen, and a row that changed since then shows as it is). Say: what it did, in words, for a step that changed something.
function useSteps(tools: MemoryTools, reload: () => Promise<void>) {
  const [acting, setActing] = useState<string | null>(null);
  const going = useRef(false);
  // A notice's Undo may be tapped after the list's filter changed: it reads the list as it is by then.
  const latest = useRef(reload);
  useEffect(() => {
    latest.current = reload;
  });

  async function undo(id: string, revision: number) {
    // A notice's Undo stays on screen until this answers, so a second tap on it sends nothing.
    if (going.current) return;
    going.current = true;
    setActing(id);
    try {
      const reply = await tools.call<{ written: MemoryWritten }>({
        op: "undo",
        id,
        expected: revision,
      });
      if (reply) tools.say({ text: "Undone: it is back as it was." });
      await latest.current();
    } finally {
      going.current = false;
      setActing(null);
    }
  }

  async function step(
    id: string,
    request: Record<string, unknown>,
    say: (w: MemoryWritten) => string,
    nothing: string,
  ) {
    if (going.current) return;
    going.current = true;
    setActing(id);
    try {
      const reply = await tools.call<{ written: MemoryWritten }>(request);
      if (reply) {
        const w = reply.written;
        const revision = w.revision;
        tools.say(
          w.unchanged
            ? { text: nothing }
            : {
                text: say(w),
                action: revision
                  ? { label: "Undo", run: () => void undo(id, revision) }
                  : undefined,
              },
        );
      }
      await latest.current();
    } finally {
      going.current = false;
      setActing(null);
    }
  }
  return { acting, step };
}

// What a person owes or is owed, in words: the other person's name is the engine's, made from what a client gave.
const owing = (row: Pick<OpenLoop, "direction" | "counterparty">) =>
  row.direction === "in"
    ? row.counterparty
      ? `${row.counterparty} owes you`
      : "Owed to you"
    : row.counterparty
      ? `You owe ${row.counterparty}`
      : "You owe it";

type LoopRead = MemoryReads["loops"] & { direction: LoopDirection };

export function MemoryTasks({
  tools,
  direction,
  setDirection,
}: {
  tools: MemoryTools;
  direction: LoopDirection;
  setDirection: (next: LoopDirection) => void;
}) {
  const [read, setRead] = useState<LoopRead | null>(null);
  const stamp = useRef(0);

  async function load(more = false) {
    const mine = ++stamp.current;
    const same = read?.direction === direction ? read : null;
    const reply = await tools.call<MemoryReads["loops"]>({
      op: "loops",
      direction,
      limit: more ? PAGE : asked(same?.loops.length ?? 0),
      ...(more && same?.next ? { cursor: same.next } : {}),
    });
    if (!reply || mine !== stamp.current) return;
    setRead({
      direction,
      loops: more && same ? [...same.loops, ...reply.loops] : reply.loops,
      next: reply.next,
      count: reply.count,
      youOwe: reply.youOwe,
      owedToYou: reply.owedToYou,
    });
  }
  const { acting, step } = useSteps(tools, () => load());

  useEffect(() => {
    void load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [direction]);

  const shown = read?.direction === direction ? read : null;
  const resolve = (row: OpenLoop, action: string, say: string) =>
    step(
      row.id,
      { op: "resolve_loop", id: row.id, action, expected: row.revision },
      () => say,
      "Nothing changed: it was resolved already.",
    );
  return (
    <>
      <Strip
        label="Which tasks"
        value={direction}
        options={[
          { id: "out", label: "You owe", count: read?.youOwe ?? null },
          { id: "in", label: "Owed to you", count: read?.owedToYou ?? null },
        ]}
        onChange={setDirection}
      />
      <p className="memory-hint">
        {shown
          ? direction === "out"
            ? `${counted(shown.count, "thing", "things")} you owe.`
            : `${counted(shown.count, "thing", "things")} owed to you.`
          : "What you said you would do, and what others promised you."}{" "}
        A memory tagged task is one you owe; a task's history keeps every step,
        and Undo puts it back.
      </p>
      <ul
        className="memory-list"
        aria-label={direction === "out" ? "You owe" : "Owed to you"}
      >
        {shown?.loops.map((row) => (
          <li key={row.id} className="memory-held-item">
            <span className="memory-preview">{words(row)}</span>
            <span className="memory-meta">
              {owing(row)} · {scopeName(row)}
              {row.project ? ` · ${row.project.slug}` : ""}
            </span>
            <span className="memory-actions" {...rowGroup(row.preview)}>
              <button
                disabled={acting !== null}
                onClick={() =>
                  void (row.direction === "in"
                    ? resolve(
                        row,
                        "received",
                        "Marked received: it leaves this list.",
                      )
                    : resolve(row, "done", "Marked done: it leaves this list."))
                }
              >
                {row.direction === "in" ? "Received" : "Done"}
              </button>
              <button
                disabled={acting !== null}
                onClick={() =>
                  void resolve(
                    row,
                    "not_a_task",
                    "No longer a task: the memory stays, out of this list.",
                  )
                }
              >
                Not a task
              </button>
              <button onClick={() => tools.open(row.id)}>Open</button>
            </span>
          </li>
        ))}
      </ul>
      {shown && !shown.loops.length && (
        <p className="memory-empty">
          {direction === "out"
            ? "You owe nothing here."
            : "Nothing is owed to you here."}
        </p>
      )}
      {shown?.next && (
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

const kindWords: Record<DueRow["kind"], string> = {
  due: "Due",
  event: "Happening",
  wake: "Reminder",
};

export type DueFilter = "now" | "later";

export function MemoryDueList({
  tools,
  filter,
  setFilter,
}: {
  tools: MemoryTools;
  filter: DueFilter;
  setFilter: (next: DueFilter) => void;
}) {
  const [read, setRead] = useState<
    (MemoryReads["due"] & { filter: DueFilter }) | null
  >(null);
  const [snoozing, setSnoozing] = useState<string | null>(null);
  // The day a person picked; until they do, a week on.
  const [picked, setPicked] = useState<string | null>(null);
  const stamp = useRef(0);

  // Due now reads the whole list, soonest first, whose head is the brief's two windows; later reads its own.
  async function load(more = false) {
    const mine = ++stamp.current;
    const same = read?.filter === filter ? read : null;
    const reply = await tools.call<MemoryReads["due"]>({
      op: "due",
      ...(filter === "later" ? { window: "later" } : {}),
      limit: more ? PAGE : asked(same?.due.length ?? 0),
      ...(more && same?.next ? { cursor: same.next } : {}),
    });
    if (!reply || mine !== stamp.current) return;
    setRead({
      ...reply,
      filter,
      due: more && same ? [...same.due, ...reply.due] : reply.due,
    });
  }
  const { acting, step } = useSteps(tools, () => load());

  useEffect(() => {
    void load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filter]);
  useEffect(() => {
    if (!snoozing) return;
    const { layer } = tools;
    layer.current = () => {
      setSnoozing(null);
      return true;
    };
    return () => {
      layer.current = null;
    };
  }, [snoozing, tools]);

  // The engine reads a snooze's day on its own clocks, which its page names; the app's config may name another zone. Only before
  // the first read, with no row to snooze yet, is the app's the best there is.
  const dayFrom = (days: number) => installDay(days, read?.zone);
  const day = picked ?? dayFrom(7);

  const shown = read?.filter === filter ? read : null;
  const soon = shown?.due.filter((r) => r.window !== "later") ?? [];
  const now = shown ? shown.overdue + shown.upcoming : 0;
  // Each part's count is its window's whole length, whatever a page has shown of it so far.
  const parts: { label: string; rows: DueRow[]; count: number }[] = (
    !shown
      ? []
      : filter === "later"
        ? [{ label: "Later", rows: shown.due, count: shown.later }]
        : [
            {
              label: "Overdue",
              rows: soon.filter((r) => r.window === "overdue"),
              count: shown.overdue,
            },
            {
              label: "Within 48 hours",
              rows: soon.filter((r) => r.window === "upcoming"),
              count: shown.upcoming,
            },
          ]
  ).filter((p) => p.rows.length > 0);
  // The whole list holds later dates too: once it reaches one, or has shown what the card counts, the rest is Later's.
  const more = shown?.next
    ? filter === "later" ||
      (soon.length < now && soon.length === shown.due.length)
    : false;

  async function snooze(row: DueRow, to: string) {
    setSnoozing(null);
    await step(
      row.id,
      {
        op: "resolve_due",
        id: row.id,
        action: "snooze",
        until: `${to}T09:00`,
        expected: row.revision,
      },
      (w) => {
        const at = w.due ? new Date(w.due.at).getTime() : NaN;
        // Within 48 hours is what the brief counts: a date already in it stays, and a later one comes in, the one snooze that raises it.
        const within = at - Date.now() <= 48 * 3_600_000;
        return `Put off until ${w.due?.local ?? to}.${
          !within
            ? " It comes back then."
            : row.window === "later"
              ? " That is within 48 hours, so it moves to Due now and the brief counts it."
              : " That is within 48 hours, so it stays here."
        }`;
      },
      "Nothing changed: it was put off until then already.",
    );
  }
  return (
    <>
      <Strip
        label="Which dates"
        value={filter}
        options={[
          {
            id: "now",
            label: "Due now",
            count: read ? read.overdue + read.upcoming : null,
          },
          { id: "later", label: "Later", count: read?.later ?? null },
        ]}
        // A question asked of a row goes with the list it was on, so the next Escape is the view's.
        onChange={(next) => {
          setSnoozing(null);
          setFilter(next);
        }}
      />
      <p className="memory-hint">
        {shown
          ? filter === "now"
            ? `${now} due: ${shown.overdue} overdue, ${shown.upcoming} within 48 hours.`
            : `${shown.later} with a later date.`
          : "What should come back to you, soonest first."}{" "}
        Snooze brings one back on another day; clearing takes its date off and
        keeps the memory. Dates read on {shown?.zone ?? "this install"}'s
        clocks.
      </p>
      {parts.map((part) => (
        <section key={part.label}>
          {filter === "now" && (
            <h3 className="memory-plan-part">
              {part.label} <span>{part.count}</span>
            </h3>
          )}
          <ul className="memory-list" aria-label={part.label}>
            {part.rows.map((row) => (
              <li key={row.id} className="memory-held-item">
                <span className="memory-preview">{words(row)}</span>
                <span className="memory-meta">
                  <span
                    className={
                      row.window === "overdue" ? "memory-plan-late" : undefined
                    }
                  >
                    {row.window === "overdue" ? "Overdue" : kindWords[row.kind]}
                  </span>{" "}
                  · {row.local} · {scopeName(row)}
                  {row.loop === "out" ? " · You owe it" : ""}
                  {row.loop === "in" ? " · Owed to you" : ""}
                </span>
                {snoozing === row.id ? (
                  <span
                    className="memory-confirm"
                    role="group"
                    aria-label="Snooze until"
                  >
                    <span>When should it come back?</span>
                    <button
                      disabled={acting !== null}
                      onClick={() => void snooze(row, dayFrom(1))}
                    >
                      Tomorrow
                    </button>
                    <button
                      disabled={acting !== null}
                      onClick={() => void snooze(row, dayFrom(7))}
                    >
                      In a week
                    </button>
                    <label className="memory-status-pick">
                      <span>On</span>
                      <input
                        type="date"
                        className="memory-input"
                        value={day}
                        min={dayFrom(1)}
                        onChange={(e) => setPicked(e.target.value)}
                      />
                    </label>
                    <button
                      disabled={!day || acting !== null}
                      onClick={() => void snooze(row, day)}
                    >
                      Put off
                    </button>
                    <button onClick={() => setSnoozing(null)}>Keep it</button>
                  </span>
                ) : (
                  <span className="memory-actions" {...rowGroup(row.preview)}>
                    <button
                      disabled={acting !== null}
                      onClick={() => setSnoozing(row.id)}
                    >
                      Snooze…
                    </button>
                    <button
                      disabled={acting !== null}
                      onClick={() =>
                        void step(
                          row.id,
                          {
                            op: "resolve_due",
                            id: row.id,
                            action: "clear_date",
                            expected: row.revision,
                          },
                          () => "Its date came off: the memory stays.",
                          "Nothing changed: it had no date.",
                        )
                      }
                    >
                      Clear date
                    </button>
                    <button onClick={() => tools.open(row.id)}>Open</button>
                  </span>
                )}
              </li>
            ))}
          </ul>
        </section>
      ))}
      {shown && !parts.length && (
        <p className="memory-empty">
          {filter === "now"
            ? "Nothing is due in the next 48 hours."
            : "Nothing has a later date."}
        </p>
      )}
      {more && (
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

const outcomeWords: Record<MemoryOutcome, string> = {
  right: "It turned out right",
  wrong: "It turned out wrong",
  mixed: "It turned out mixed",
  unknown: "Too early to tell",
};
const outcomeChoices: { result: MemoryOutcome; label: string }[] = [
  { result: "right", label: "Right" },
  { result: "wrong", label: "Wrong" },
  { result: "mixed", label: "Mixed" },
  { result: "unknown", label: "Too early" },
];

export type DecisionFilter = "review" | "open" | "resolved" | "scored";
// The engine's lists a filter reads: to review is the two the card counts, each with its own count and its own next page.
type Listed = "due" | "upcoming" | "open" | "resolved" | "scored";
const listsOf: Record<DecisionFilter, Listed[]> = {
  review: ["due", "upcoming"],
  open: ["open"],
  resolved: ["resolved"],
  scored: ["scored"],
};
const listTitle: Record<Listed, string> = {
  due: "Its review date has come",
  upcoming: "Within 48 hours",
  open: "No outcome yet",
  resolved: "With an outcome",
  scored: "What calibration rests on",
};
type DecisionPage = { rows: DecisionRow[]; next: string | null; count: number };

function decisionMeta(row: DecisionRow) {
  return [
    row.outcome ? outcomeWords[row.outcome] : null,
    row.confidence === null
      ? "No confidence given"
      : `You were ${Math.round(row.confidence * 100)}% sure${
          row.confidenceSource === "inferred" ? " (read from your words)" : ""
        }`,
    row.reviewAt
      ? `${row.window === "overdue" ? "Review was" : "Review"} ${when(row.reviewAt)}`
      : null,
    row.edited ? "Its words changed since" : null,
    row.current ? null : "Replaced since",
    scopeName(row),
  ]
    .filter(Boolean)
    .join(" · ");
}

// How sure the person was against how things turned out: the engine's own line, each band with how many decisions it rests on and
// no figure below its minimum, so a few decisions never read as a rate.
function Calibration({
  read,
  showScored,
}: {
  read: MemoryCalibration;
  showScored: () => void;
}) {
  return (
    <section className="memory-plan-calibration" aria-label="Calibration">
      <h3 className="memory-plan-part">Calibration</h3>
      <p className="memory-hint">{read.line}</p>
      {read.topicLine && <p className="memory-hint">{read.topicLine}</p>}
      {read.n > 0 && (
        <ul className="memory-plan-bands" aria-label="By how sure you were">
          {read.bands
            .filter((b) => b.n > 0)
            .map((b) => (
              <li key={b.band}>
                {b.band}% sure: {counted(b.n, "decision", "decisions")}
                {b.shown && b.hitRate !== null
                  ? `, right ${Math.round(b.hitRate * 100)}% of the time`
                  : ", too few to say"}
              </li>
            ))}
        </ul>
      )}
      {read.n > 0 && (
        <button className="secondary-action memory-more" onClick={showScored}>
          Show the {counted(read.n, "decision", "decisions")} it rests on
        </button>
      )}
    </section>
  );
}

export function MemoryDecisions({
  tools,
  filter,
  setFilter,
}: {
  tools: MemoryTools;
  filter: DecisionFilter;
  setFilter: (next: DecisionFilter) => void;
}) {
  const [pages, setPages] = useState<Partial<Record<Listed, DecisionPage>>>({});
  const [counts, setCounts] = useState<Pick<
    MemoryReads["decisions"],
    "open" | "dueNow" | "upcoming" | "resolved" | "scored"
  > | null>(null);
  const [calibration, setCalibration] = useState<MemoryCalibration | null>(
    null,
  );
  const stamp = useRef(0);

  async function readList(list: Listed, more: boolean) {
    const had = pages[list];
    const reply = await tools.call<MemoryReads["decisions"]>({
      op: "decisions",
      state: list,
      limit: more ? PAGE : asked(had?.rows.length ?? 0),
      ...(more && had?.next ? { cursor: had.next } : {}),
    });
    return { list, reply, had: more ? had : undefined };
  }
  async function load(more?: Listed) {
    const mine = ++stamp.current;
    const answers = await Promise.all(
      (more ? [more] : listsOf[filter]).map((list) =>
        readList(list, Boolean(more)),
      ),
    );
    if (mine !== stamp.current) return;
    for (const { list, reply, had } of answers) {
      if (!reply) continue;
      setCounts({
        open: reply.open,
        dueNow: reply.dueNow,
        upcoming: reply.upcoming,
        resolved: reply.resolved,
        scored: reply.scored,
      });
      setPages((p) => ({
        ...p,
        [list]: {
          rows: had ? [...had.rows, ...reply.decisions] : reply.decisions,
          next: reply.next,
          count: reply.count,
        },
      }));
    }
    if (more) return;
    // A read the list can go on without: an older engine's says nothing, and nothing shows.
    const figures = await tools.quiet<MemoryCalibration>({ op: "calibration" });
    if (mine === stamp.current) setCalibration(figures);
  }
  const { acting, step } = useSteps(tools, () => load());

  useEffect(() => {
    void load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filter]);

  const record = (row: DecisionRow, result: MemoryOutcome) =>
    step(
      row.id,
      { op: "record_outcome", id: row.id, result, expected: row.revision },
      (w) =>
        result === "unknown"
          ? w.reviewsDone || !w.reviewAt
            ? "Too early to tell, and it won't ask again."
            : `Too early to tell: it asks again on ${dayOf(w.reviewAt)}.`
          : `${outcomeWords[result]}.${
              row.confidence === null
                ? " It has no confidence, so calibration leaves it out."
                : ""
            }`,
      "Nothing changed: it had that outcome already.",
    );

  const lists = listsOf[filter];
  const toReview = counts ? counts.dueNow + counts.upcoming : null;
  const noneText: Record<DecisionFilter, string> = {
    review: "No decision is due for a look.",
    open: "No decision is waiting for an outcome.",
    resolved: "No decision has an outcome yet.",
    scored: "No decision has both an outcome and a confidence yet.",
  };
  const answered = lists.every((l) => pages[l]);
  const empty = answered && lists.every((l) => pages[l]!.count === 0);
  return (
    <>
      <Strip
        label="Which decisions"
        value={filter}
        options={[
          { id: "review", label: "To review", count: toReview },
          { id: "open", label: "No outcome yet", count: counts?.open ?? null },
          {
            id: "resolved",
            label: "With an outcome",
            count: counts?.resolved ?? null,
          },
          // Only once calibration's own list is asked for, so a person can see where they are.
          ...(filter === "scored"
            ? [
                {
                  id: "scored" as const,
                  label: "Calibration rests on",
                  count: counts?.scored ?? null,
                },
              ]
            : []),
        ]}
        onChange={setFilter}
      />
      {filter === "scored" && (
        <p className="memory-hint">
          The decisions with both an outcome and a confidence, any replaced
          since among them: what calibration rests on.
        </p>
      )}
      {filter === "review" && toReview !== null && (
        <p className="memory-hint">
          {counted(toReview, "decision", "decisions")} to look at again.
        </p>
      )}
      {lists.map((list) => {
        const page = pages[list];
        if (!page || (lists.length > 1 && page.count === 0)) return null;
        return (
          <section key={list} aria-label={listTitle[list]}>
            <h3 className="memory-plan-part">
              {listTitle[list]} <span>{page.count}</span>
            </h3>
            <ul className="memory-list" aria-label={listTitle[list]}>
              {page.rows.map((row) => (
                <li key={row.id} className="memory-held-item">
                  <span className="memory-preview">{words(row)}</span>
                  <span className="memory-meta">{decisionMeta(row)}</span>
                  <span
                    className="memory-actions"
                    role="group"
                    aria-label={`${
                      row.outcome
                        ? "Change how it turned out"
                        : "How it turned out"
                    }: ${row.preview}`.slice(0, 120)}
                  >
                    {outcomeChoices.map((c) => (
                      <button
                        key={c.result}
                        aria-pressed={row.outcome === c.result}
                        // Saying it again changes nothing, but for too early to tell, which asks again.
                        disabled={
                          acting !== null ||
                          (row.outcome === c.result && c.result !== "unknown")
                        }
                        onClick={() => void record(row, c.result)}
                      >
                        {c.label}
                      </button>
                    ))}
                    <button onClick={() => tools.open(row.id)}>Open</button>
                  </span>
                </li>
              ))}
            </ul>
            {page.next && (
              <button
                className="secondary-action memory-more"
                onClick={() => void load(list)}
              >
                Show more
              </button>
            )}
          </section>
        );
      })}
      {empty && <p className="memory-empty">{noneText[filter]}</p>}
      {calibration && (
        <Calibration
          read={calibration}
          showScored={() => setFilter("scored")}
        />
      )}
    </>
  );
}
