import { useEffect, useRef, useState } from "react";
import { RotateCcw } from "lucide-react";
import { personName } from "./instance";
import type { ChangeGroup, MemoryGroupUndone, MemoryReads } from "./memories";
import { counted, rowGroup, when } from "./memoryScreen";
import type { MemoryTools } from "./memoryScreen";

// AI changes (SB-P6-01; upstream's "Changed by AI tools"): what an AI client changed lately, group by group as the audit trail
// has it, and one tap that takes a group back, as many calls as the group takes. The undo is a change of the app's, a person's,
// so its own group undoes it again. What an AI client saved new is no change here: forgetting it is the trash's, and one held
// waits in Review.

const WINDOW_DAYS = 2;
const LONGER_DAYS = 7;
// The most calls one tap makes: a group undoes at most 50 memories, 5 a call.
const CALLS = 12;

// What a group's changes did, by their kind (MemoryStore.Family).
const did: Record<string, string> = {
  edit: "edited",
  status: "changed the status of",
  release: "released",
  revert: "undid changes to",
  resolve: "resolved",
  outcome: "recorded an outcome on",
  due: "moved the due date of",
  standing: "stopped a standing instruction in",
};

// The client's own name for itself is stored data, shown as an AI client's and never as a channel of its own.
const whoDid = (g: ChangeGroup) => {
  const client = g.client ? `An AI client (${g.client})` : "An AI client";
  return g.person ? `${client} for ${personName(g.person)}` : client;
};

export function MemoryChanges({
  tools,
  asBrief = false,
}: {
  tools: MemoryTools;
  // Opened from the brief's count on Home: the groups it counts, which leave out what is held now, so the two agree.
  asBrief?: boolean;
}) {
  const [days, setDays] = useState(WINDOW_DAYS);
  const [briefly, setBriefly] = useState(asBrief);
  const [read, setRead] = useState<MemoryReads["changes"] | null>(null);
  const [undoing, setUndoing] = useState<string | null>(null);
  const going = useRef(false);
  // The groups undone since this list was opened. The trail keeps their changes, so they stay listed; a second tap would pass
  // over what is undone, so it says so instead.
  const [taken, setTaken] = useState<string[]>([]);

  async function load(window = days, brief = briefly) {
    const reply = await tools.call<MemoryReads["changes"]>(
      brief
        ? { op: "changes", notHeld: true }
        : {
            op: "changes",
            since: new Date(Date.now() - window * 86_400_000).toISOString(),
          },
    );
    if (reply)
      setRead({
        groups: reply.groups,
        truncated: reply.truncated,
        since: reply.since,
        count: reply.count,
      });
  }

  useEffect(() => {
    void load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // One tap: each call takes up to 5 of its memories back, until none is left. A memory left as it is is named again by each
  // call that passes it, so the newest word on each is the one that counts.
  async function undoGroup(group: string, redoes?: string) {
    // A notice's Redo stays on screen until this answers, so a second tap on it sends nothing.
    if (going.current) return;
    going.current = true;
    setUndoing(group);
    const results = new Map<string, string>();
    let redo: string | null = null;
    let done = false;
    let capped = false;
    try {
      for (let call = 0; call < CALLS && !done; call++) {
        const reply = await tools.call<MemoryGroupUndone>({
          op: "undo_group",
          group,
        });
        if (!reply) break;
        for (const r of reply.results) results.set(r.id, r.result);
        redo = reply.redo ?? redo;
        done = reply.done;
        capped = reply.capped;
      }
    } finally {
      going.current = false;
      setUndoing(null);
    }
    // A refusal is on screen already, when nothing came of it.
    if (!done && !results.size) return;
    const all = [...results.values()];
    const undone = all.filter((r) => r === "undone").length;
    const since = all.filter((r) => r === "changed_since").length;
    const refused =
      all.length - undone - since - all.filter((r) => r === "unchanged").length;
    const again = redo;
    tools.say({
      text: [
        undone
          ? `Undone: ${counted(undone, "memory is", "memories are")} back as ${undone === 1 ? "it was" : "they were"} before.`
          : "Nothing was undone.",
        since
          ? `${since === 1 ? "1 changed" : `${since} changed`} after these and ${since === 1 ? "stays as it is" : "stay as they are"}.`
          : "",
        refused
          ? `${refused === 1 ? "1 was" : `${refused} were`} not yours to change, or no longer kept.`
          : "",
        capped ? "It held more than one undo takes: the oldest 50 went." : "",
        !done ? "The rest didn't go: nothing more was sent." : "",
      ]
        .filter(Boolean)
        .join(" "),
      failed: undone === 0 || !done,
      action:
        again && !redoes
          ? { label: "Redo", run: () => void undoGroup(again, group) }
          : undefined,
    });
    if (undone) {
      // Undone only once all of it went: one that stopped partway keeps its button, and the same key takes the rest. What a
      // redo put back can be undone again, whether or not all of it went.
      if (redoes) setTaken((t) => t.filter((g) => g !== redoes));
      else if (done) setTaken((t) => [...t, group]);
      tools.saved();
    }
    await load();
  }

  return (
    <>
      <p className="memory-hint">
        {briefly && read?.count !== undefined
          ? `${counted(read.count, "group", "groups")} of changes, as the brief counts them, leaving out what is held for review. `
          : ""}
        What AI clients changed in the last{" "}
        {days === 1 ? "day" : `${days} days`}, each burst as one group. Undo
        puts a group's memories back as they were; a memory changed since stays
        as it is. What a client saved new, and what it had held for review, is
        in Recent and Review.
      </p>
      <ul className="memory-list" aria-label="AI changes">
        {read?.groups.map((g) => (
          <li key={g.group} className="memory-change-group">
            <span className="memory-preview memory-change-what">
              {whoDid(g)} {did[g.family] ?? g.family.replace(/_/g, " ")}{" "}
              {counted(g.memories, "memory", "memories")}
              {g.changes > g.memories
                ? ` (${counted(g.changes, "change", "changes")})`
                : ""}
            </span>
            <span className="memory-meta">
              {when(g.from) === when(g.until)
                ? when(g.until)
                : `${when(g.from)} to ${when(g.until)}`}
              {g.capped ? " · more than one undo takes" : ""}
            </span>
            <ul className="memory-change-items" aria-label="Its memories">
              {g.items.map((item) => (
                <li key={item.id}>
                  <button onClick={() => tools.open(item.id)}>
                    <span className="memory-preview">
                      {item.held
                        ? "Held for review"
                        : item.preview === null
                          ? "A memory you no longer read"
                          : item.preview}
                      {item.truncated ? "…" : ""}
                    </span>
                    {item.changes > 1 && (
                      <span className="memory-meta">
                        {counted(item.changes, "change", "changes")}
                      </span>
                    )}
                  </button>
                </li>
              ))}
            </ul>
            {g.memories > g.items.length && (
              <span className="memory-hint">
                And {g.memories - g.items.length} more.
              </span>
            )}
            <span
              className="memory-actions"
              {...rowGroup(`${whoDid(g)}, ${when(g.until)}`)}
            >
              <button
                disabled={undoing !== null || taken.includes(g.group)}
                aria-busy={undoing === g.group}
                onClick={() => void undoGroup(g.group)}
              >
                <RotateCcw size={13} aria-hidden="true" />{" "}
                {undoing === g.group
                  ? "Undoing…"
                  : taken.includes(g.group)
                    ? "Undone"
                    : "Undo these"}
              </button>
            </span>
          </li>
        ))}
      </ul>
      {read && !read.groups.length && (
        <p className="memory-empty">
          No AI client has changed anything in this time.
        </p>
      )}
      {read?.truncated && (
        <p className="memory-hint">
          More changed than one read takes: the newest are shown.
        </p>
      )}
      {read && days === WINDOW_DAYS && (
        <button
          className="secondary-action memory-more"
          onClick={() => {
            setDays(LONGER_DAYS);
            setBriefly(false);
            void load(LONGER_DAYS, false);
          }}
        >
          Show the last {LONGER_DAYS} days
        </button>
      )}
    </>
  );
}
