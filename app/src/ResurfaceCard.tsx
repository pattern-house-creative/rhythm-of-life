import { useEffect, useId, useRef, useState } from "react";
import { ChevronDown, X } from "lucide-react";
import { homeServed } from "./homeServed";
import { focusAfter } from "./memoryFocus";
import type {
  MemoryClient,
  MemoryReads,
  ResurfacedMemory,
  ResurfaceDismissed,
} from "./memories";
import "./ResurfaceCard.css";

// A resurfaced old memory on Home (SB-P5-17; app_memories ops resurface and resurface_dismiss, Core/MemoryResurface.cs): one memory
// a day worth reading again, a slim line under the brief in Home's own flow, its words opening in place, which a person may dismiss
// for good. Only while the engine serves the memories (its memory.tools on): with it off, in the demo, while a read fails, before
// one answers or with none to show, it draws nothing at all, so Home is as it was. The engine chooses it, so a reload shows the same
// one; read again on coming back to Home or to the page, and every 15 minutes, as the brief is.
const everyMs = 900_000;

// When it was written, as the reader's own calendar reads it: the month beside its line, the day when it is open.
const written = (at: string, open: boolean) =>
  new Date(at).toLocaleDateString(undefined, {
    year: "numeric",
    month: "short",
    day: open ? "numeric" : undefined,
  });

export function ResurfaceCard({
  client,
  resting,
}: {
  // The session's memories; absent in the demo, so nothing is asked.
  client?: MemoryClient;
  // Home is behind a panel, the explorer or a full screen: read again on coming back.
  resting: boolean;
}) {
  const [pick, setPick] = useState<ResurfacedMemory | null>(null);
  // Which pick is open, and whose dismissal failed: tomorrow's starts closed and with nothing to say.
  const [openFor, setOpenFor] = useState<string | null>(null);
  const [failedFor, setFailedFor] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const opened = useId();
  const card = useRef<HTMLElement>(null);
  // What this page dismissed, or is dismissing: an answer that was on its way when it did never brings it back.
  const dismissed = useRef(new Set<string>());
  useEffect(() => {
    if (!client || resting) return;
    const controller = new AbortController();
    const { signal } = controller;
    const read = async () => {
      // Off, or a read that failed, draws nothing: never an old pick as if it were today's.
      const answer = (await homeServed(client))
        ? await client.call<MemoryReads["resurface"]>(
            { op: "resurface" },
            signal,
          )
        : null;
      if (signal.aborted) return;
      const next = answer?.available && answer.ok ? answer.pick : null;
      setPick(next && !dismissed.current.has(next.id) ? next : null);
    };
    const attempt = () => {
      if (!document.hidden) read().catch(() => {});
    };
    attempt();
    const interval = setInterval(attempt, everyMs);
    document.addEventListener("visibilitychange", attempt);
    window.addEventListener("pageshow", attempt);
    return () => {
      controller.abort();
      clearInterval(interval);
      document.removeEventListener("visibilitychange", attempt);
      window.removeEventListener("pageshow", attempt);
    };
  }, [client, resting]);
  if (!client || !pick) return null;
  const open = openFor === pick.id;
  const dismiss = async () => {
    // Pressed from the keyboard, focus goes on to what is next on Home when the card goes (SB-P6-02).
    const focused = card.current?.contains(document.activeElement);
    setBusy(true);
    setFailedFor(null);
    dismissed.current.add(pick.id);
    // Not tied to the card's reads: leaving Home while it is sent still dismisses it.
    const answer = await client
      .call<ResurfaceDismissed>({ op: "resurface_dismiss", id: pick.id })
      .catch(() => null);
    setBusy(false);
    // Deleted for good since, no longer this reader's, or the engine stopped serving it: there is nothing left to dismiss. One in
    // the trash or held since is dismissed as any other, so a restore or a release does not bring it back.
    if (
      answer &&
      (!answer.available || answer.ok || answer.code === "unknown")
    ) {
      setPick((shown) => (shown?.id === pick.id ? null : shown));
      if (focused && card.current) focusAfter(card.current);
      return;
    }
    // Still here: a read that came meanwhile may have taken it off.
    dismissed.current.delete(pick.id);
    setPick((shown) => shown ?? pick);
    setFailedFor(pick.id);
  };
  const words = `${pick.preview}${pick.truncated ? "…" : ""}`;
  return (
    <section ref={card} className="resurface-card" aria-label="An old memory">
      <div className="resurface-line">
        <button
          className="resurface-says"
          aria-expanded={open}
          aria-controls={opened}
          onClick={() => setOpenFor(open ? null : pick.id)}
        >
          <small>
            {open && "From "}
            <time dateTime={pick.createdAt}>
              {written(pick.createdAt, open)}
            </time>
          </small>
          {!open && <span>{words}</span>}
          <ChevronDown
            size={14}
            aria-hidden="true"
            data-open={open || undefined}
          />
        </button>
        <button
          className="resurface-dismiss"
          aria-label="Dismiss"
          title="Don't show this one again"
          disabled={busy}
          onClick={dismiss}
        >
          <X size={13} aria-hidden="true" />
        </button>
      </div>
      <div id={opened} className="resurface-open" hidden={!open}>
        {open && (
          <p tabIndex={0} role="group" aria-label="Its words">
            {words}
          </p>
        )}
      </div>
      <div className="resurface-said" aria-live="polite">
        {failedFor === pick.id && (
          <small role="status" className="resurface-failed">
            That didn't go through. Try again.
          </small>
        )}
      </div>
    </section>
  );
}
