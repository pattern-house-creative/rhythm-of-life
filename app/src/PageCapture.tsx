import { useEffect, useRef, useState } from "react";
import { memoryClient } from "./apiClient";
import { instance } from "./instance";
import { holdWhy, maxMemoryBytes, newKey, utf8Bytes } from "./memories";
import type { MemoryClient, MemoryWritten } from "./memories";
import { captureText, pageLimits, pageTags } from "./bookmarklet";
import type { PageDraft } from "./bookmarklet";
import "./PageCapture.css";

// The app's own page for saving the page a person is on (SB-P7-03), opened by the bookmark in the Memories view
// (PageBookmark.tsx) in a tab of its own (main.tsx shows it instead of the sky while the address carried a page). It
// shows what the bookmark brought in the Memories view's capture box, for the person to read and change, and saves
// nothing until they press Save: then one capture through the app's own server as the app (app_memories; the browser
// holds no token), as a draft in Family tagged reading, with a key so a lost answer is resent once under the same key.
// Only while the engine serves the Memories view (memory.tools on); otherwise the start draws the app instead (Start.tsx,
// unserved), or, drawn alone or when the engine stops serving it before Save, it says so and sends nothing. Nothing is
// kept in the browser: closing or reloading the tab drops what the bookmark brought, which the bookmark brings again.
//
// Anyone can open this page with words of their choosing, so a gesture begun on another page must never count as Save
// here: the second click of a double click, or Ctrl+Enter held down, landing in the tab that page just opened. Save
// counts only once the form has been in view for half a second and the person has then done something of their own in
// this page: moved the pointer, touched it, or pressed a key (a key held down repeats, which never counts). Leaving the
// tab and coming back starts that again.
const armAfterMs = 500;

// unserved: what the start does when the engine's first answer says it doesn't serve the Memories view (Start.tsx draws
// the app instead, as with memory.tools off before this page existed); without it, the page says so itself.
export default function PageCapture({
  page,
  client = memoryClient,
  unserved,
}: {
  page: PageDraft | "unreadable";
  client?: MemoryClient;
  unserved?: () => void;
}) {
  const name = instance().name;
  const [served, setServed] = useState<boolean | null>(null);
  const [draft, setDraft] = useState(() =>
    page === "unreadable" ? "" : captureText(page),
  );
  const [key, setKey] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<{
    text: string;
    failed?: boolean;
  } | null>(null);
  const [saved, setSaved] = useState<MemoryWritten | null>(null);
  const box = useRef<HTMLTextAreaElement>(null);
  const done = useRef<HTMLHeadingElement>(null);
  // In view long enough (ready), and a step of the person's own in this page since (armed).
  const ready = useRef(false);
  const armed = useRef(false);
  const showing = Boolean(served && !saved);
  const off = useRef(unserved);
  useEffect(() => {
    off.current = unserved;
  }, [unserved]);

  useEffect(() => {
    const controller = new AbortController();
    const answered = (on: boolean) => {
      if (controller.signal.aborted) return;
      if (!on && off.current) off.current();
      else setServed(on);
    };
    client.available(controller.signal).then(answered, () => answered(false));
    return () => controller.abort();
  }, [client]);
  useEffect(() => {
    if (served) box.current?.focus({ preventScroll: true });
  }, [served]);
  useEffect(() => {
    if (saved) done.current?.focus({ preventScroll: true });
  }, [saved]);
  useEffect(() => {
    if (!showing) return;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const wait = () => {
      clearTimeout(timer);
      ready.current = false;
      armed.current = false;
      if (document.visibilityState === "visible")
        timer = setTimeout(() => (ready.current = true), armAfterMs);
    };
    const own = (e: Event) => {
      if (ready.current && !(e instanceof KeyboardEvent && e.repeat))
        armed.current = true;
    };
    const steps = ["pointermove", "pointerdown", "keydown"] as const;
    wait();
    document.addEventListener("visibilitychange", wait);
    for (const step of steps) window.addEventListener(step, own, true);
    return () => {
      clearTimeout(timer);
      document.removeEventListener("visibilitychange", wait);
      for (const step of steps) window.removeEventListener(step, own, true);
    };
  }, [showing]);

  async function save() {
    const content = draft.trim();
    if (!content || busy || !armed.current) return;
    if (utf8Bytes(content) > maxMemoryBytes) {
      setNotice({
        text: "This is too long to save: a memory holds at most 128 KiB of text. Nothing was sent.",
        failed: true,
      });
      return;
    }
    const attempt = key ?? newKey();
    setKey(attempt);
    setBusy(true);
    setNotice({ text: "Saving…" });
    try {
      const reply = await client.call<{ written: MemoryWritten }>({
        op: "capture",
        content,
        scope: "shared",
        tags: [...pageTags],
        key: attempt,
      });
      if (!reply.available) {
        setNotice(null);
        setServed(false);
      } else if (!reply.ok) setNotice({ text: reply.reason, failed: true });
      else {
        setNotice(null);
        setSaved(reply.written);
      }
    } finally {
      setBusy(false);
    }
  }

  // A tab the bookmark opened may close itself; one opened another way can't, and says so.
  const close = () => {
    window.close();
    setTimeout(() => {
      if (!window.closed)
        setNotice({
          text: "This tab can't close itself: close it as you would any other.",
        });
    }, 400);
  };
  const open = (
    <a className="page-capture-open" href="/">
      Open {name}
    </a>
  );
  const from = page !== "unreadable" && page.url ? hostOf(page.url) : null;

  return (
    <main className="page-capture" aria-labelledby="page-capture-title">
      <h1 id="page-capture-title" tabIndex={-1} ref={done}>
        {saved ? "Saved" : "Save this page"}
      </h1>
      {served === null && (
        <p role="status" className="page-capture-hint">
          Checking that {name} saves memories…
        </p>
      )}
      {served === false && (
        <>
          <p className="page-capture-note">
            {name} isn't saving memories right now: its Memories view is off on
            this install, or it couldn't be reached. Nothing was saved.
          </p>
          <div className="page-capture-row">{open}</div>
        </>
      )}
      {served && saved && (
        <>
          <p role="status" className="page-capture-note">
            {said(saved)}
          </p>
          <div className="page-capture-row">
            <button type="button" className="page-capture-save" onClick={close}>
              Close this tab
            </button>
            {open}
          </div>
        </>
      )}
      {served && !saved && (
        <form
          className="page-capture-form"
          onSubmit={(e) => {
            e.preventDefault();
            void save();
          }}
        >
          {page === "unreadable" ? (
            <p className="page-capture-note">
              This address didn't bring a page this app can read, so nothing
              came across. Write the memory yourself, or click the bookmark
              again on the page.
            </p>
          ) : (
            <p className="page-capture-hint">
              {from ? `From ${from}. ` : ""}Read it and change what you like.
              {page.cut
                ? ` Only the first ${pageLimits.text.toLocaleString("en-GB")} characters of what you selected came across.`
                : ""}
            </p>
          )}
          <label htmlFor="page-capture-text">Memory</label>
          <textarea
            id="page-capture-text"
            ref={box}
            value={draft}
            rows={8}
            maxLength={131_072}
            onChange={(e) => {
              setDraft(e.target.value);
              // A changed text is a new request: only the same text resends under the same key.
              setKey(null);
            }}
            onKeyDown={(e) => {
              if (e.key === "Enter" && (e.metaKey || e.ctrlKey) && !e.repeat) {
                e.preventDefault();
                void save();
              }
            }}
          />
          <div className="page-capture-row">
            <span className="page-capture-hint">
              Nothing is saved until you press Save. It is saved as a draft in
              Family, tagged reading, and checked like any memory.
            </span>
            <button
              type="submit"
              className="page-capture-save"
              disabled={busy || !draft.trim()}
            >
              Save
            </button>
          </div>
        </form>
      )}
      <div className="page-capture-said" aria-live="polite">
        {notice && (
          <p
            role={notice.failed ? "alert" : "status"}
            className="page-capture-note"
            data-failed={notice.failed || undefined}
          >
            {notice.text}
          </p>
        )}
      </div>
    </main>
  );
}

const hostOf = (url: string) => {
  try {
    return new URL(url).host || null;
  } catch {
    return null;
  }
};

// What the save did, in words: where it landed, and a hold with its reason.
function said(w: MemoryWritten) {
  const where =
    w.scopes === "off"
      ? "for the household"
      : w.scope === "shared"
        ? "in Family"
        : "in your own memories";
  return w.held
    ? `Saved ${where}, and held for review: ${holdWhy[w.held.reason]}. Open the Memories view to look at it and release it.`
    : `${w.replayed ? "Saved already" : "Saved"} as a draft ${where}, tagged reading. Confirm it in the Memories view when you're sure of it.`;
}
