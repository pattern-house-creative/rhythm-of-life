import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import type { DashboardData } from "./dashboards";
import type { DashboardParts } from "./DashboardView";
import { PageFrame } from "./PageFrame";
import { PartsGrid } from "./PartsGrid";
import { layoutOf, partLabel } from "./parts";
import {
  draftDashboard,
  draftData,
  requestId,
  studioCall,
  studioSession,
  studioShown,
  StudioAccessError,
  StudioGoneError,
} from "./studioProtocol";
import type { StudioSession, StudioDraft } from "./studioProtocol";
import { screenRunning } from "./screenRunner";
import "./Studio.css";

// A session open here is read every second and a half. With none, an agent's open_studio command wakes Studio at once
// (LiveScreen.tsx), so it only looks again once a minute. A read that fails waits twice as long each time, up to a minute.
const studioActiveMs = 1500,
  studioIdleMs = 60_000;
const studioDelay = (active: boolean, failures: number) =>
  Math.min(
    (active ? studioActiveMs : studioIdleMs) * 2 ** failures,
    studioIdleMs,
  );

interface Preview {
  revision: number;
  draft: StudioDraft;
  data: DashboardData;
  ready: boolean;
}
export function Studio({
  screen,
  enabled,
  parts,
  saved,
  requireRunner = false,
}: {
  screen: string;
  enabled: boolean;
  parts: DashboardParts;
  saved: (id: string) => void;
  requireRunner?: boolean;
}) {
  const [session, setSession] = useState<StudioSession | null>(null),
    [previews, setPreviews] = useState<Preview[]>([]),
    [watch, setWatch] = useState(false),
    [error, setError] = useState(""),
    [connected, setConnected] = useState(true),
    [busy, setBusy] = useState(false),
    [feedback, setFeedback] = useState("");
  const latest = useRef(session),
    loaded = useRef(""),
    closed = useRef(""),
    saving = useRef(false),
    fence = useRef(false);
  const [readingFeeds, setReadingFeeds] = useState(false),
    [dataAttempt, setDataAttempt] = useState(0);
  const [runner, setRunner] = useState(() => screenRunning(screen));
  useEffect(() => {
    setRunner(screenRunning(screen));
    const changed = (event: Event) => {
      const detail = (event as CustomEvent).detail;
      if (detail?.screen === screen) setRunner(detail.active === true);
    };
    window.addEventListener("rhythm:screen-runner", changed);
    return () => window.removeEventListener("rhythm:screen-runner", changed);
  }, [screen]);
  const dialog = useRef<HTMLDialogElement>(null);
  const clearDraft = useCallback(() => {
    latest.current = null;
    setSession(null);
    setPreviews([]);
    loaded.current = "";
    fence.current = false;
  }, []);
  useLayoutEffect(() => {
    clearDraft();
    closed.current = "";
  }, [screen, enabled, clearDraft]);
  useEffect(() => {
    window.addEventListener("observatory-access-lost", clearDraft);
    return () =>
      window.removeEventListener("observatory-access-lost", clearDraft);
  }, [clearDraft]);
  const savedNow = useRef(saved);
  savedNow.current = saved;
  useEffect(() => {
    if (!session) return;
    const before = document.activeElement as HTMLElement | null;
    const el = dialog.current;
    el?.showModal?.();
    studioShown(session.id);
    return () => {
      studioShown(null);
      el?.close?.();
      if (before?.isConnected) before.focus({ preventScroll: true });
    };
  }, [session?.id]);
  latest.current = session;
  // Studio lets go of a session that ended, here or anywhere: the dialog closes and the screen shows what it did before.
  const end = useCallback(
    (id: string) => {
      closed.current = id;
      clearDraft();
    },
    [clearDraft],
  );
  const adopt = useCallback(
    (next: StudioSession | null) => {
      if (!next || next.id === closed.current) return;
      // Saved, canceled or discarded (an expired session reads as canceled). A saved one opens the version it saved.
      if (next.status !== "active") {
        end(next.id);
        if (next.status === "saved" && next.result?.id)
          savedNow.current(next.result.id);
        return;
      }
      const current = latest.current;
      if (current?.id === next.id && current.revision > next.revision) return;
      if (current?.id !== next.id) {
        setPreviews([]);
        loaded.current = "";
        fence.current = false;
        setError("");
      }
      latest.current = next;
      setSession(next);
    },
    [end],
  );
  useEffect(() => {
    if (!enabled || (requireRunner && !runner)) return;
    let gone = false,
      fetching = false,
      again = false,
      failures = 0,
      timer: ReturnType<typeof setTimeout>;
    const abort = new AbortController();
    const poll = async () => {
      if (gone) return;
      clearTimeout(timer);
      if (fetching) {
        again = true;
        return;
      }
      fetching = true;
      const current = latest.current;
      try {
        const value = await studioCall(
          current
            ? { op: "read", screen, sessionId: current.id }
            : { op: "active", screen },
          abort.signal,
        );
        if (!gone) {
          adopt(studioSession(value));
          setConnected(true);
          failures = 0;
        }
      } catch (error) {
        if (!gone) {
          if (error instanceof StudioGoneError && current) {
            // Nothing left to read again: back to looking for a new session, now and then.
            if (latest.current?.id === current.id) end(current.id);
            setConnected(true);
            failures = 0;
          } else {
            if (error instanceof StudioAccessError) clearDraft();
            setConnected(false);
            failures += 1;
          }
        }
      }
      fetching = false;
      if (!gone)
        timer = setTimeout(
          poll,
          again ? 0 : studioDelay(!!latest.current, failures),
        );
      again = false;
    };
    const wake = () => {
      void poll();
    };
    window.addEventListener("rhythm:studio", wake);
    window.addEventListener("online", wake);
    void poll();
    return () => {
      gone = true;
      clearTimeout(timer);
      abort.abort();
      window.removeEventListener("rhythm:studio", wake);
      window.removeEventListener("online", wake);
    };
  }, [screen, enabled, adopt, end, runner, requireRunner, clearDraft]);
  useEffect(() => {
    if (!session?.candidate || session.status !== "active" || fence.current)
      return;
    const mark = `${session.id}:${session.revision}`;
    if (loaded.current === mark) return;
    loaded.current = mark;
    let gone = false;
    let retry: ReturnType<typeof setTimeout>;
    const abort = new AbortController();
    setReadingFeeds(true);
    const revisions =
      session.preview && session.valid_revision !== session.revision
        ? [session.valid_revision, session.revision]
        : [session.revision];
    Promise.all(
      revisions.map(async (revision) => ({
        revision,
        data: await studioCall(
          { op: "data", screen, sessionId: session.id, revision },
          abort.signal,
        ),
      })),
    )
      .then((reads) => {
        if (
          gone ||
          fence.current ||
          latest.current?.id !== session.id ||
          latest.current.revision !== session.revision
        )
          return;
        const loadedPreviews = reads.map((read) => ({
          revision: read.revision,
          draft:
            read.revision === session.revision
              ? session.candidate!
              : session.preview!,
          data: draftData({ ...session, revision: read.revision }, read.data),
          ready: false,
        }));
        setPreviews((before) => [
          ...before
            .filter((p) => p.ready && !revisions.includes(p.revision))
            .slice(-1),
          ...loadedPreviews,
        ]);
        setError("");
        setReadingFeeds(false);
      })
      .catch((e) => {
        if (!gone) {
          if (e instanceof StudioAccessError) {
            clearDraft();
            return;
          }
          if (e instanceof StudioGoneError) {
            end(session.id);
            return;
          }
          loaded.current = "";
          setError(String(e));
          setReadingFeeds(false);
          retry = setTimeout(() => setDataAttempt((n) => n + 1), 5000);
        }
      });
    return () => {
      gone = true;
      abort.abort();
      clearTimeout(retry);
    };
  }, [
    session?.id,
    session?.revision,
    session?.status,
    screen,
    dataAttempt,
    clearDraft,
    end,
  ]);
  const change = async (op: string, payload: Record<string, unknown> = {}) => {
    const current = latest.current;
    if (!current) return;
    return studioCall({
      op,
      screen,
      sessionId: current.id,
      revision: current.revision,
      requestId: requestId(),
      payload,
    });
  };
  const validate = async (revision: number, ok: boolean, message?: string) => {
    if (
      revision === latest.current?.valid_revision &&
      revision !== latest.current?.revision
    ) {
      if (ok)
        setPreviews((before) =>
          before.map((p) =>
            p.revision === revision ? { ...p, ready: true } : p,
          ),
        );
      return;
    }
    if (
      fence.current ||
      latest.current?.revision !== revision ||
      latest.current.status !== "active"
    )
      return;
    if (ok)
      setPreviews((before) =>
        before
          .map((p) => (p.revision === revision ? { ...p, ready: true } : p))
          .filter((p) => p.revision === revision),
      );
    else {
      setError(
        message ?? "Candidate did not draw. Keeping the last valid preview.",
      );
      setPreviews((before) => before.filter((p) => p.revision !== revision));
    }
    try {
      adopt(
        studioSession(
          await change("validate", { ok, ...(message ? { message } : {}) }),
        ),
      );
    } catch (e) {
      setError(String(e));
    }
  };
  const finish = async (op: "save" | "cancel" | "discard") => {
    if (saving.current) return;
    saving.current = true;
    setBusy(true);
    setError("");
    // Stop accepting local candidates immediately; the server fence is authoritative and must be confirmed.
    if (op !== "save") fence.current = true;
    try {
      const result = await change(op);
      if (op === "save") {
        const out = result as { id: string; version: number };
        if (!out?.id)
          throw new Error(
            "Save result unavailable. Read Studio before trying again.",
          );
        closed.current = session!.id;
        setSession(null);
        latest.current = null;
        setPreviews([]);
        saved(out.id);
      } else adopt(studioSession(result));
    } catch (e) {
      setError(String(e));
    } finally {
      saving.current = false;
      setBusy(false);
    }
  };
  if (!enabled || !session) return null;
  // Only an active session is held here (adopt).
  const canSave =
    connected &&
    !busy &&
    !fence.current &&
    session.revision > 0 &&
    session.valid_revision === session.revision &&
    previews.some((p) => p.ready && p.revision === session.revision);
  const shown = previews.filter((p) => p.ready).at(-1)?.revision;
  const feedRead = previews.find((p) => p.revision === shown)?.data;
  const unavailable = Object.entries(feedRead?.feeds ?? {}).filter(
    ([, f]) => f.status === "unavailable",
  );
  return (
    <dialog
      ref={dialog}
      className="studio-shell"
      aria-label="Rhythm Studio"
      onCancel={(e) => e.preventDefault()}
    >
      <header>
        <div>
          <strong>Studio</strong>
          <span>
            {session.candidate?.title ?? "Waiting for your assistant"}
          </span>
        </div>
        <nav aria-label="Studio controls">
          <button aria-pressed={watch} onClick={() => setWatch(!watch)}>
            Watch code
          </button>
          <button disabled={!canSave} onClick={() => void finish("save")}>
            Save
          </button>
          <button disabled={busy} onClick={() => void finish("discard")}>
            Discard
          </button>
          <button disabled={busy} onClick={() => void finish("cancel")}>
            Cancel
          </button>
        </nav>
      </header>
      <div className="studio-progress" role="status">
        {!connected
          ? "Disconnected — retaining the preview. Reconnecting…"
          : fence.current
            ? "Stopping this session; awaiting confirmation…"
            : readingFeeds
              ? "Reading draft feeds…"
              : session.valid_revision === session.revision &&
                  session.revision > 0
                ? `Preview ${session.revision} validated. Ready to save.`
                : `${session.events.at(-1)?.message ?? (session.candidate ? `Validating received revision ${session.revision}` : "Waiting for a renderable update")}`}
      </div>
      {error && (
        <p role="alert" className="studio-error">
          {error}
        </p>
      )}
      <main>
        <div className="studio-preview">
          {!previews.length && (
            <div className="studio-empty">
              Your dashboard will appear as validated updates arrive.
            </div>
          )}
          {previews.map((p) => (
            <div
              key={`${session.id}:${p.revision}`}
              className={`studio-layer ${p.ready ? "visible" : "validating"}`}
              aria-hidden={!p.ready}
              inert={!p.ready}
            >
              <DraftPreview
                session={session}
                preview={p}
                parts={parts}
                drawn={() => {
                  if (!p.ready) void validate(p.revision, true);
                }}
                failed={(reason) => void validate(p.revision, false, reason)}
              />
            </div>
          ))}
        </div>
        {watch && (
          <aside className="studio-code" aria-label="Received code">
            <h2>Received code · revision {session.revision}</h2>
            <p>Completed updates received by Rhythm. No simulated typing.</p>
            <pre>
              <code>{session.candidate?.html ?? "No code received yet."}</code>
            </pre>
            <ol>
              {session.events.map((e) => (
                <li key={e.seq}>
                  <time>{new Date(e.at).toLocaleTimeString()}</time> · {e.op} ·
                  revision {e.revision}
                  {e.message ? ` · ${e.message}` : ""}
                </li>
              ))}
            </ol>
          </aside>
        )}
      </main>
      <footer>
        {feedRead && (
          <span>
            Feeds read {new Date(feedRead.asOf).toLocaleTimeString()}
            {unavailable.length
              ? ` · ${unavailable.map(([name, f]) => `${name}: ${f.reason ?? "unavailable"}`).join("; ")}`
              : ""}
          </span>
        )}
        <span>
          {shown
            ? `Showing validated preview ${shown}`
            : "No validated preview yet"}
        </span>
        <form
          onSubmit={async (e) => {
            e.preventDefault();
            const text = feedback.trim();
            if (!text) return;
            try {
              adopt(studioSession(await change("feedback", { text })));
              setFeedback("");
            } catch (err) {
              setError(String(err));
            }
          }}
        >
          <label htmlFor="studio-feedback">Steer your assistant</label>
          <input
            id="studio-feedback"
            maxLength={1000}
            value={feedback}
            onChange={(e) => setFeedback(e.target.value)}
            placeholder="Read at the assistant’s next checkpoint"
          />
          <button disabled={!connected || !feedback.trim()}>
            Send feedback
          </button>
        </form>
      </footer>
    </dialog>
  );
}

function DraftPreview({
  session,
  preview,
  parts,
  drawn,
  failed,
}: {
  session: StudioSession;
  preview: Preview;
  parts: DashboardParts;
  drawn: () => void;
  failed: (reason: string) => void;
}) {
  const dashboard = draftDashboard(session, preview.draft, preview.revision),
    layout = layoutOf(dashboard.layout);
  const controls =
    preview.revision === session.revision
      ? session.controls
      : session.preview_controls;
  const manifest = JSON.stringify(controls);
  const selection = useMemo(
    () => ({
      revision: 0,
      values: Object.fromEntries(
        (JSON.parse(manifest) as typeof controls).map((c) => [c.id, c.initial]),
      ),
    }),
    [manifest],
  );
  return (
    <PartsGrid
      parts={layout.parts}
      label={(p) => partLabel(layout, p.id)}
      render={(part, fold) =>
        part.kind === "page" ? (
          <PageFrame
            dashboard={dashboard}
            data={preview.data}
            edits={false}
            drawn={preview.ready}
            hidden={false}
            selection={selection}
            onDrawn={drawn}
            onFail={failed}
            onStopped={failed}
            onOpenStar={() => {}}
            onWrite={(_name, _change, reply) =>
              reply({
                ok: false,
                reason: "Writes are disabled in Studio previews.",
              })
            }
          />
        ) : (
          parts.renderPart(part, { dashboard, fold })
        )
      }
    />
  );
}
