import { useEffect, useRef, useState } from "react";
import { personName } from "./instance";
import { releaseAllMax } from "./memories";
import type {
  InsightEvent,
  InsightPair,
  InsightReviewed,
  InsightView,
  MemoryReads,
  MemoryResolved,
  MemoryResolvedAll,
  PairSide,
  StreamInsight,
} from "./memories";
import { BulkBar, several } from "./MemoryBulk";
import { counted, dayOf, rowGroup, scopeName, when } from "./memoryScreen";
import type { MemoryTools } from "./memoryScreen";
import { useLayer, useQueue } from "./MemoryQueues";

// Two more of a person's review queues (SB-P6-01; D-220, D-224): the one insight stream, where what an AI proposed is kept apart from what
// a person confirmed, and the pairs of memories the nightly pass found worth a look. This is D-224's person's way: an install whose one
// client saves every insight has it reviewed here, or at the terminal, and never by that client. Each step goes through the engine as
// the app (op resolve, once; a review taken back by op review_insight's reopen). Each count is the length of its list, read with it.
// All of an insight's and a pair's words are stored data written by people and AI clients. Since run 22 a person reviews several insights,
// or declines several pairs, at once (op resolve_all, upstream's bulk resolve; a row with words of its own through op resolve, with
// them), each answered on its own, and names the insight a pair
// became when marking it used: one that cites both its memories, which the engine requires.

// What became of the rest of a step for several, once its own words have said what went.
function restWords(results: MemoryResolvedAll["results"], failed: boolean) {
  const already = results.filter((r) => r.result === "unchanged").length;
  const refused = results.filter(
    (r) => r.result !== "resolved" && r.result !== "unchanged",
  ).length;
  return [
    already
      ? `${already === 1 ? "1 was" : `${already} were`} decided already, so nothing changed there.`
      : "",
    refused
      ? `${refused === 1 ? "1 was" : `${refused} were`} not yours to decide, or no longer there.`
      : "",
    failed ? "The rest didn't go: nothing more was sent." : "",
  ]
    .filter(Boolean)
    .join(" ");
}

// A step for several takes no one's words (op resolve_all names each item's id alone), so a chosen row whose box holds a person's words
// goes after the rest, on its own through op resolve with them, as its own button sends it, and is answered as the step for several
// answers each. A call that does not come stops what is left, as a page of several that does not come does.
async function resolveChosen(
  tools: MemoryTools,
  action: MemoryResolvedAll["action"],
  ids: string[],
  notes: Record<string, string>,
): Promise<{ results: MemoryResolvedAll["results"]; failed: boolean }> {
  const said = (id: string) => notes[id]?.trim() ?? "";
  const together = await several<MemoryResolvedAll["results"][0]>(
    tools,
    "resolve_all",
    ids.filter((id) => !said(id)).map((id) => ({ id })),
    { action },
    "items",
  );
  const results = [...together.results];
  if (together.failed) return { results, failed: true };
  for (const id of ids.filter(said)) {
    const reply = await tools.call<MemoryResolved>({
      op: "resolve",
      id,
      action,
      note: said(id),
    });
    if (!reply) return { results, failed: true };
    results.push({
      id,
      result: reply.waiting && !reply.unchanged ? "resolved" : "unchanged",
      revision: reply.revision,
      review: reply.review,
      view: reply.view,
      candidate: reply.candidate,
    });
  }
  return { results, failed: false };
}

// The notes left once a step has answered: those of the rows it answered go, whatever the answer, as one row's own step clears its own.
const notesLeft = (
  notes: Record<string, string>,
  results: MemoryResolvedAll["results"],
) =>
  Object.fromEntries(
    Object.entries(notes).filter(([id]) => !results.some((r) => r.id === id)),
  );

// Whether any chosen row has words of its own, which a step for several then sends with it.
const anyNoted = (chosen: { id: string }[], notes: Record<string, string>) =>
  chosen.some((c) => notes[c.id]?.trim());

// Whether an insight could be the one a pair became: it cites both the pair's memories (a source not on the web naming each), and a private
// pair's insight is its own person's (066's rule, which the engine checks again).
export const citesBoth = (i: StreamInsight, pair: InsightPair) => {
  const named = new Set(
    i.insight.sources
      .filter((s) => s.kind !== "web" && s.locator)
      .map((s) => s.locator!.toLowerCase()),
  );
  return (
    named.has(pair.older.id.toLowerCase()) &&
    named.has(pair.newer.id.toLowerCase()) &&
    (pair.owner === "household" || i.owner === pair.owner)
  );
};

const views: { id: InsightView; label: string }[] = [
  { id: "proposed", label: "Waiting" },
  { id: "older", label: "Older" },
  { id: "confirmed", label: "Confirmed" },
  { id: "snoozed", label: "Snoozed" },
  { id: "closed", label: "Closed" },
];

const reviewWords: Record<string, string> = {
  proposed: "Proposed by an AI client: no fact until a person says so",
  confirmed: "A person said it holds",
  dismissed: "A person said it does not hold",
};

const eventWords: Record<string, string> = {
  confirmed: "Confirmed",
  dismissed: "Dismissed",
  reopened: "Review taken back",
  replaced: "Replaced by a later insight",
  feedback: "Feedback",
};

const feedbackWords: Record<string, string> = {
  interesting: "interesting",
  explore: "worth exploring",
  not_useful: "not useful",
  later: "later",
  handled: "handled",
};

// The way it came: a person's channels by name, an AI client's as one, its own name for itself shown as its own.
const through = (e: InsightEvent) =>
  e.channel === "app"
    ? " in the app"
    : e.channel === "cli"
      ? " at the terminal"
      : e.channel === "mcp"
        ? e.client
          ? ` by an AI client (${e.client})`
          : " by an AI client"
        : "";

export function MemoryInsightStream({ tools }: { tools: MemoryTools }) {
  const [view, setView] = useState<InsightView>("proposed");
  const [read, setRead] = useState<MemoryReads["insight_stream"] | null>(null);
  const [notes, setNotes] = useState<Record<string, string>>({});
  const [history, setHistory] = useState<
    Record<string, MemoryReads["insight_history"]>
  >({});
  const [busy, setBusy] = useState(false);
  // The insights chosen to review together (run 22): only those waiting that this login may review.
  const [chosen, setChosen] = useState<StreamInsight[]>([]);
  const stamp = useRef(0);
  // The view on screen: a notice's Take it back outlives the view it was made in, and what it reads again goes under today's buttons.
  const showing = useRef(view);
  // A take-back of several on its way: the notice's button stays on screen until it answers, so a second tap sends nothing.
  const reopening = useRef(false);

  async function load(more = false) {
    const mine = ++stamp.current;
    const reply = await tools.call<MemoryReads["insight_stream"]>({
      op: "insight_stream",
      view: showing.current,
      limit: 20,
      ...(more && read?.next ? { cursor: read.next } : {}),
    });
    if (!reply || mine !== stamp.current) return;
    setRead((was) => ({
      ...reply,
      insights:
        more && was ? [...was.insights, ...reply.insights] : reply.insights,
    }));
    // What is chosen stays while it is shown and still waits; the rest leaves the choice. A next page leaves the choice as it was.
    if (!more)
      setChosen((c) =>
        c.filter((x) =>
          reply.insights.some(
            (i) =>
              i.id === x.id && i.review === "proposed" && i.view !== "closed",
          ),
        ),
      );
  }
  useEffect(() => {
    showing.current = view;
    setRead(null);
    setChosen([]);
    void load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [view]);

  async function readHistory(id: string) {
    const reply = await tools.call<MemoryReads["insight_history"]>({
      op: "insight_history",
      id,
    });
    if (reply) setHistory((h) => ({ ...h, [id]: reply }));
  }

  // Taken back from a notice, or from a reviewed insight: it is an AI's proposal again.
  async function reopen(id: string) {
    setBusy(true);
    const reply = await tools.call<InsightReviewed>({
      op: "review_insight",
      id,
      action: "reopen",
    });
    setBusy(false);
    if (reply)
      tools.say({
        text: reply.unchanged
          ? "Nothing changed: it has no review to take back."
          : reply.view === "closed"
            ? "Taken back, but it stays closed because it was marked not useful or handled; new feedback brings it back."
            : reply.view === "snoozed"
              ? "Taken back: it is among the snoozed until its snooze ends, then proposed again."
              : "Taken back: it is proposed again.",
      });
    await load();
    tools.waiting();
    setHistory((h) => {
      const left = { ...h };
      delete left[id];
      return left;
    });
  }

  // Several reviews taken back from a notice, one at a time, each as its own reopen.
  async function reopenAll(ids: string[]) {
    if (reopening.current) return;
    reopening.current = true;
    setBusy(true);
    let back = 0;
    try {
      for (const id of ids) {
        const reply = await tools.call<InsightReviewed>({
          op: "review_insight",
          id,
          action: "reopen",
        });
        if (!reply) break;
        if (!reply.unchanged) back += 1;
      }
    } finally {
      reopening.current = false;
      setBusy(false);
    }
    tools.say({
      text:
        back === ids.length
          ? `Taken back: ${counted(back, "insight is", "insights are")} proposed again, unless one was closed as not useful or handled.`
          : `Took back ${back} of ${ids.length}; the rest stay as they were.`,
      failed: back < ids.length,
    });
    await load();
    tools.waiting();
  }

  // Several at once (run 22): the same word on each, each through the one door and answered on its own, a row's own words kept with it.
  async function reviewChosen(action: "confirm" | "dismiss") {
    const { results, failed } = await resolveChosen(
      tools,
      action === "confirm" ? "confirm_insight" : "dismiss_insight",
      chosen.map((i) => i.id),
      notes,
    );
    if (results.length) {
      setChosen([]);
      setNotes((n) => notesLeft(n, results));
      const done = results.filter((r) => r.result === "resolved");
      tools.say({
        text: [
          done.length
            ? action === "confirm"
              ? `Confirmed ${counted(done.length, "insight", "insights")}: a person said ${done.length === 1 ? "it holds. It stays an insight" : "they hold. They stay insights"}; no memory changes.`
              : `Dismissed ${counted(done.length, "insight", "insights")}: ${done.length === 1 ? "it leaves" : "they leave"} what waits on a person.`
            : "Nothing was reviewed.",
          restWords(results, failed),
        ]
          .filter(Boolean)
          .join(" "),
        failed: failed || done.length < results.length,
        action: done.length
          ? {
              label: "Take them back",
              run: () => void reopenAll(done.map((r) => r.id)),
            }
          : undefined,
      });
    }
    await load();
    tools.waiting();
  }

  // A person's word on it, with their own words if any, kept in its history; it leaves what waits once.
  async function review(i: StreamInsight, action: "confirm" | "dismiss") {
    setBusy(true);
    const note = notes[i.id]?.trim();
    const reply = await tools.call<MemoryResolved>({
      op: "resolve",
      id: i.id,
      action: action === "confirm" ? "confirm_insight" : "dismiss_insight",
      ...(note ? { note } : {}),
    });
    setBusy(false);
    if (reply) {
      setNotes((n) => {
        const left = { ...n };
        delete left[i.id];
        return left;
      });
      tools.say({
        text: !reply.waiting
          ? reply.review === "proposed"
            ? "Nothing changed: a person marked it not useful or handled, so it no longer waits."
            : `Nothing changed: it was ${reply.review} already.`
          : action === "confirm"
            ? "Confirmed: a person said it holds. It stays an insight; no memory changes."
            : "Dismissed: it leaves what waits on a person.",
        action: reply.waiting
          ? { label: "Take it back", run: () => void reopen(i.id) }
          : undefined,
      });
    }
    await load();
    tools.waiting();
  }

  return (
    <>
      {read && (
        <p className="memory-hint">
          {read.day.says}
          {read.day.declined
            ? ` ${read.day.declined === 1 ? "1 pair was" : `${read.day.declined} pairs were`} declined today.`
            : ""}
        </p>
      )}
      {read?.day.note && (
        <p className="memory-hint">Its note: {read.day.note}</p>
      )}
      <p className="memory-hint">
        An insight is an AI client's proposal until a person reviews it, and a
        client never reviews what it saved. Confirming writes no memory.
      </p>
      <div className="memory-filters" role="group" aria-label="Which insights">
        {views.map((v) => (
          <button
            key={v.id}
            className="memory-filter-clear"
            aria-pressed={view === v.id}
            onClick={() => setView(v.id)}
          >
            {v.label}
            {read ? ` (${read[v.id]})` : ""}
          </button>
        ))}
      </div>
      <ul className="memory-list" aria-label="Insights">
        {read?.insights.map((i) => {
          const about = i.insight;
          const open = i.review === "proposed" && i.view !== "closed";
          return (
            <li key={i.id} className="memory-held-item">
              <strong className="memory-preview">{about.title}</strong>
              <span className="memory-meta">
                {reviewWords[i.review]} · {scopeName(i)} · {when(i.shownAt)}
                {i.snoozeUntil && !i.snoozeEnded
                  ? ` · snoozed until ${when(i.snoozeUntil)}`
                  : ""}
              </span>
              {about.summary && <p className="memory-hint">{about.summary}</p>}
              {about.whyItMatters && (
                <p className="memory-hint">Why: {about.whyItMatters}</p>
              )}
              {about.nextStep && (
                <p className="memory-hint">Next: {about.nextStep}</p>
              )}
              <span className="memory-meta">
                {about.evidenceLevel} evidence
                {about.limitations ? ` · ${about.limitations}` : ""}
              </span>
              {about.sources.length > 0 && (
                <span className="memory-meta">
                  From {about.sources.map((s) => s.title).join(", ")}
                </span>
              )}
              {open && i.mayReview && (
                <label className="memory-note">
                  <span className="memory-meta">
                    What you would say, if anything (kept with it)
                  </span>
                  <input
                    value={notes[i.id] ?? ""}
                    maxLength={500}
                    onChange={(e) =>
                      setNotes((n) => ({ ...n, [i.id]: e.target.value }))
                    }
                  />
                </label>
              )}
              <span className="memory-actions" {...rowGroup(about.title)}>
                {open && i.mayReview && (
                  <>
                    <button
                      disabled={busy}
                      onClick={() => void review(i, "confirm")}
                    >
                      It holds
                    </button>
                    <button
                      disabled={busy}
                      onClick={() => void review(i, "dismiss")}
                    >
                      It does not hold
                    </button>
                  </>
                )}
                {!open && i.mayReview && i.review !== "proposed" && (
                  <button disabled={busy} onClick={() => void reopen(i.id)}>
                    Take the review back
                  </button>
                )}
                {!history[i.id] && (
                  <button onClick={() => void readHistory(i.id)}>
                    Its history
                  </button>
                )}
                {open && i.mayReview && (
                  <label className="memory-choose">
                    <input
                      type="checkbox"
                      checked={chosen.some((x) => x.id === i.id)}
                      disabled={
                        !chosen.some((x) => x.id === i.id) &&
                        chosen.length >= releaseAllMax
                      }
                      onChange={(e) =>
                        setChosen((c) =>
                          e.target.checked
                            ? [...c, i]
                            : c.filter((x) => x.id !== i.id),
                        )
                      }
                    />{" "}
                    Choose
                  </label>
                )}
              </span>
              {!i.mayReview && (
                <span className="memory-hint">
                  Its owner, the person who saved it or a Family admin reviews
                  it, at the terminal.
                </span>
              )}
              {history[i.id] && (
                <ul
                  className="memory-copies"
                  aria-label="Its feedback and reviews"
                >
                  {history[i.id].events.map((e, n) => (
                    <li key={n} className="memory-meta">
                      {eventWords[e.event] ?? e.event}
                      {e.value ? `: ${feedbackWords[e.value] ?? e.value}` : ""}
                      {e.person ? ` by ${personName(e.person)}` : ""}
                      {through(e)} · {when(e.at)}
                      {e.note ? ` · ${e.note}` : ""}
                    </li>
                  ))}
                  {history[i.id].events.length === 0 && (
                    <li className="memory-meta">Nothing yet.</li>
                  )}
                </ul>
              )}
            </li>
          );
        })}
      </ul>
      {read && !read.insights.length && (
        <p className="memory-empty">
          {view === "proposed" ? "No insight waits on a person." : "None here."}
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
      <BulkBar
        count={chosen.length}
        shown={
          read?.insights.filter(
            (i) =>
              i.mayReview && i.review === "proposed" && i.view !== "closed",
          ).length ?? 0
        }
        max={releaseAllMax}
        noun={["insight", "insights"]}
        actions={[
          {
            label: "They hold…",
            asks: `Say ${chosen.length === 1 ? "this insight holds" : `these ${chosen.length} insights hold`}? ${chosen.length === 1 ? "It stays an insight" : "They stay insights"}, no memory changes, and you can take ${chosen.length === 1 ? "it" : "them"} back.${anyNoted(chosen, notes) ? " What you wrote beside any is kept with it." : ""}`,
            go: "They hold",
            keep: "Not yet",
            run: () => reviewChosen("confirm"),
          },
          {
            label: "They do not hold…",
            asks: `Say ${chosen.length === 1 ? "this insight does not hold" : `these ${chosen.length} insights do not hold`}? ${chosen.length === 1 ? "It leaves" : "They leave"} what waits on a person, and you can take ${chosen.length === 1 ? "it" : "them"} back.${anyNoted(chosen, notes) ? " What you wrote beside any is kept with it." : ""}`,
            go: "They do not hold",
            keep: "Not yet",
            run: () => reviewChosen("dismiss"),
          },
        ]}
        onAll={() =>
          setChosen((c) =>
            [
              ...c,
              ...(read?.insights ?? []).filter(
                (i) =>
                  i.mayReview &&
                  i.review === "proposed" &&
                  i.view !== "closed" &&
                  !c.some((x) => x.id === i.id),
              ),
            ].slice(0, releaseAllMax),
          )
        }
        onClear={() => setChosen([])}
        tools={tools}
      />
    </>
  );
}

const states = [
  { id: "waiting", label: "Waiting" },
  { id: "used", label: "Used" },
  { id: "rejected", label: "Declined" },
] as const;

const signalWords: Record<string, string> = {
  similar: "their words are close",
  weighty: "both are marked important",
};

export function MemoryPairs({ tools }: { tools: MemoryTools }) {
  const [state, setState] = useState<(typeof states)[number]["id"]>("waiting");
  const { read, load } = useQueue<
    MemoryReads["insight_candidates"],
    InsightPair
  >(tools, { op: "insight_candidates", state }, (a) => a.candidates);
  const [notes, setNotes] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  // A decision being asked about, since nothing undoes one (064).
  const [deciding, setDeciding] = useState<{
    id: string;
    action: "use" | "reject";
  } | null>(null);
  useLayer(deciding !== null, () => setDeciding(null), tools);
  // The pairs chosen to decline together (run 22), and for a pair being marked used the insights that could be the one it became (null
  // while they are read) and the one a person picked ("" for none).
  const [chosen, setChosen] = useState<InsightPair[]>([]);
  const [became, setBecame] = useState<{
    pair: string;
    insights: StreamInsight[] | null;
    picked: string;
  } | null>(null);
  useEffect(() => setChosen([]), [state]);
  useEffect(() => {
    if (!read) return;
    // What is chosen stays while it is listed and waits.
    setChosen((c) => c.filter((p) => read.items.some((x) => x.id === p.id)));
  }, [read]);

  // The insights this reader reads that cite both of a pair's memories, newest first, from the whole stream, a few pages at most: what a
  // person can name as the one it became. Read quietly: if it doesn't come, the pair is marked used naming none, as before.
  async function findBecame(pair: InsightPair) {
    setBecame({ pair: pair.id, insights: null, picked: "" });
    const found: StreamInsight[] = [];
    let cursor: string | null = null;
    for (let n = 0; n < 4; n++) {
      const reply: MemoryReads["insight_stream"] | null = await tools.quiet<
        MemoryReads["insight_stream"]
      >({
        op: "insight_stream",
        view: "all",
        limit: 50,
        ...(cursor ? { cursor } : {}),
      });
      if (!reply) break;
      found.push(...reply.insights.filter((i) => citesBoth(i, pair)));
      cursor = reply.next;
      if (!cursor) break;
    }
    setBecame((b) => (b?.pair === pair.id ? { ...b, insights: found } : b));
  }

  // Several declined at once (run 22), a row's own words kept with it, as one row's decline keeps them.
  async function declineChosen() {
    const { results, failed } = await resolveChosen(
      tools,
      "reject_candidate",
      chosen.map((p) => p.id),
      notes,
    );
    if (results.length) {
      setChosen([]);
      setNotes((n) => notesLeft(n, results));
      const done = results.filter((r) => r.result === "resolved").length;
      tools.say({
        text: [
          done
            ? `Declined ${counted(done, "pair", "pairs")}: ${done === 1 ? "it" : "they"} won't be offered again. Neither memory of any changed.`
            : "Nothing was declined.",
          restWords(results, failed),
        ]
          .filter(Boolean)
          .join(" "),
        failed: failed || done < results.length,
      });
    }
    await load();
    tools.waiting();
  }

  // A decision on the pair, once and for good: neither memory changes, and the pass never offers it again. Used says an insight came of
  // it; this names none, and the daily insight run reads only what waits, so a pair that should become one is left waiting.
  async function decide(pair: InsightPair, action: "use" | "reject") {
    setDeciding(null);
    setBusy(true);
    const note = notes[pair.id]?.trim();
    // The insight a person named as the one it became, when they named one.
    const insight =
      action === "use" && became?.pair === pair.id && became.picked
        ? became.picked
        : null;
    setBecame(null);
    const reply = await tools.call<MemoryResolved>({
      op: "resolve",
      id: pair.id,
      action: action === "use" ? "use_candidate" : "reject_candidate",
      ...(note ? { note } : {}),
      ...(insight ? { insight } : {}),
    });
    setBusy(false);
    if (reply)
      tools.say({
        text: !reply.waiting
          ? `Nothing changed: it was ${reply.candidate === "used" ? "used" : "declined"} already, and a decision on a pair stays as it was made.`
          : action === "use"
            ? reply.insight
              ? "Marked used, naming the insight it became: it won't be offered again. Neither memory changed."
              : "Marked used: it won't be offered again. Neither memory changed."
            : "Declined: it won't be offered again. Neither memory changed.",
      });
    await load();
    tools.waiting();
  }

  const side = (name: string, s: PairSide) => (
    <div className="memory-side" role="group" aria-label={name}>
      <span className="memory-preview">
        {s.preview}
        {s.truncated ? "…" : ""}
      </span>
      <span className="memory-meta">
        {dayOf(s.createdAt)}
        {s.importance ? ` · importance ${s.importance}` : ""}
      </span>
      <span className="memory-actions">
        <button onClick={() => tools.open(s.id)}>Open</button>
      </span>
    </div>
  );

  return (
    <>
      <p className="memory-hint">
        {read
          ? read.count === 1
            ? `1 pair ${state === "waiting" ? "waits" : "here"}.`
            : `${read.count} pairs ${state === "waiting" ? "wait" : "here"}.`
          : "Pairs of an older and a newer memory wait here."}{" "}
        The nightly pass found each worth a look together, and the daily insight
        run can write an insight from one that waits. Mark one used once an
        insight has come of it, or decline it if there is nothing in it. Either
        is for good, and neither memory changes.
      </p>
      <div className="memory-filters" role="group" aria-label="Which pairs">
        {states.map((s) => (
          <button
            key={s.id}
            className="memory-filter-clear"
            aria-pressed={state === s.id}
            onClick={() => setState(s.id)}
          >
            {s.label}
          </button>
        ))}
      </div>
      <ul className="memory-list" aria-label="Pairs">
        {read?.items.map((pair) => (
          <li key={pair.id} className="memory-held-item">
            <span className="memory-meta">
              {scopeName(pair)} · {pair.gapDays} days apart ·{" "}
              {pair.signals.map((s) => signalWords[s] ?? s).join(", ")}
            </span>
            <div className="memory-pair">
              {side("The older memory", pair.older)}
              {side("The newer memory", pair.newer)}
            </div>
            {pair.status === "pending" ? (
              <>
                <label className="memory-note">
                  <span className="memory-meta">Why, if you like</span>
                  <input
                    value={notes[pair.id] ?? ""}
                    maxLength={500}
                    onChange={(e) =>
                      setNotes((n) => ({ ...n, [pair.id]: e.target.value }))
                    }
                  />
                </label>
                {deciding?.id === pair.id ? (
                  <span
                    className="memory-confirm"
                    role="group"
                    aria-label="Decide this pair?"
                  >
                    <span>
                      {deciding.action === "use"
                        ? "Mark it used? That says an insight has come of it. It leaves what the daily insight run reads, so none will be written from it after this, and this can't be undone. To have one written, leave it waiting."
                        : "Decline it? It won't be offered again, and this can't be undone."}
                    </span>
                    {deciding.action === "use" &&
                      became?.pair === pair.id &&
                      (became.insights === null ? (
                        <span className="memory-hint">
                          Looking for an insight that cites both memories…
                        </span>
                      ) : became.insights.length === 0 ? (
                        <span className="memory-hint">
                          No insight you read cites both memories, so it is
                          marked used naming none.
                        </span>
                      ) : (
                        <label className="memory-note">
                          <span className="memory-meta">
                            The insight it became, if one did
                          </span>
                          <select
                            value={became.picked}
                            onChange={(e) =>
                              setBecame({ ...became, picked: e.target.value })
                            }
                          >
                            <option value="">None named</option>
                            {became.insights.map((i) => (
                              <option key={i.id} value={i.id}>
                                {i.insight.title.slice(0, 120)}
                              </option>
                            ))}
                          </select>
                        </label>
                      ))}
                    <button
                      className="memory-danger"
                      disabled={busy}
                      onClick={() => void decide(pair, deciding.action)}
                    >
                      {deciding.action === "use"
                        ? "Mark it used"
                        : "Decline it"}
                    </button>
                    <button onClick={() => setDeciding(null)}>Not yet</button>
                  </span>
                ) : (
                  <span
                    className="memory-actions"
                    {...rowGroup(`The pair: ${pair.newer.preview}`)}
                  >
                    <button
                      disabled={busy}
                      onClick={() => {
                        setDeciding({ id: pair.id, action: "use" });
                        void findBecame(pair);
                      }}
                    >
                      Mark used…
                    </button>
                    <button
                      disabled={busy}
                      onClick={() =>
                        setDeciding({ id: pair.id, action: "reject" })
                      }
                    >
                      Nothing in it…
                    </button>
                    <label className="memory-choose">
                      <input
                        type="checkbox"
                        checked={chosen.some((x) => x.id === pair.id)}
                        disabled={
                          !chosen.some((x) => x.id === pair.id) &&
                          chosen.length >= releaseAllMax
                        }
                        onChange={(e) =>
                          setChosen((c) =>
                            e.target.checked
                              ? [...c, pair]
                              : c.filter((x) => x.id !== pair.id),
                          )
                        }
                      />{" "}
                      Choose
                    </label>
                  </span>
                )}
              </>
            ) : (
              <span className="memory-meta">
                {pair.status === "used" ? "Used" : "Declined"}
                {pair.decidedAt ? ` ${when(pair.decidedAt)}` : ""}
                {pair.note ? ` · ${pair.note}` : ""}
              </span>
            )}
          </li>
        ))}
      </ul>
      {read && !read.items.length && (
        <p className="memory-empty">
          {state === "waiting" ? "No pair waits." : "None here."}
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
      <BulkBar
        count={chosen.length}
        shown={read?.items.filter((p) => p.status === "pending").length ?? 0}
        max={releaseAllMax}
        noun={["pair", "pairs"]}
        actions={[
          {
            label: "Nothing in them…",
            asks: `Decline ${chosen.length === 1 ? "this pair" : `these ${chosen.length} pairs`}? ${chosen.length === 1 ? "It" : "None of them"} will be offered again, and this can't be undone. Neither memory of any changes.${anyNoted(chosen, notes) ? " What you wrote beside any is kept with it." : ""}`,
            go: "Decline them",
            keep: "Not yet",
            run: declineChosen,
          },
        ]}
        onAll={() =>
          setChosen((c) =>
            [
              ...c,
              ...(read?.items ?? []).filter(
                (p) => p.status === "pending" && !c.some((x) => x.id === p.id),
              ),
            ].slice(0, releaseAllMax),
          )
        }
        onClear={() => setChosen([])}
        tools={tools}
      />
    </>
  );
}
