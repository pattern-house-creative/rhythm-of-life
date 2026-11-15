import { Suspense, lazy, useEffect, useRef, useState } from "react";
import { Minimize2 } from "lucide-react";
import type { GraphSession } from "./liveGraph";
import type { DashboardParts } from "./DashboardView";

const DashboardView = lazy(() => import("./DashboardView"));
// The exit control shows for this long after the last sign of someone there, and for a minute
// while the page itself has focus.
export const awakeMs = 3000,
  framedMs = 60_000;

// A dashboard filling the whole display (docs/dev/screens.md section 8): no sky, tiles or panels,
// only its page in the same sealed frame as the reader's, read as often, and a small exit
// control that shows when someone moves the pointer, touches the screen, scrolls or presses a
// key. Escape exits too (App). The page's frame is sealed, so the app never hears what happens
// over it: while the control is hidden, a clear layer over the page takes the first movement or
// touch and shows the control, and then steps aside so the page can be used. A tap into the page
// moves focus there, which also counts, and while focus stays there (someone is using the page,
// unheard) the control stays and the layer keeps away, so it doesn't take their next taps. A
// dashboard drawn as a grid of parts (10.9) fills the display the same way; its clear layers
// cover only its frames (the page and its cards, App.css), since the app hears its own parts.
export function FullScreen({
  id,
  title,
  session,
  exit,
  onOpenStar,
  parts,
}: {
  id: string;
  title: string;
  session: GraphSession;
  exit: () => void;
  onOpenStar: (graphId: string) => void;
  // Its native parts: the map, the tiles and the cards (App.tsx).
  parts?: DashboardParts;
}) {
  const [awake, setAwake] = useState(false);
  const ref = useRef<HTMLElement>(null);
  useEffect(() => {
    let timer = 0,
      last = 0;
    const framed = () => {
      const focused = document.activeElement;
      return (
        focused instanceof HTMLIFrameElement && !!ref.current?.contains(focused)
      );
    };
    const sleep = () => {
      const left = (framed() ? framedMs : awakeMs) - (Date.now() - last);
      if (left > 0) timer = window.setTimeout(sleep, left);
      else setAwake(false);
    };
    const wake = () => {
      last = Date.now();
      setAwake(true);
      clearTimeout(timer);
      timer = window.setTimeout(sleep, awakeMs);
    };
    const events = [
      "pointermove",
      "pointerdown",
      "touchstart",
      "wheel",
      "keydown",
    ];
    const options = { capture: true, passive: true };
    for (const e of events) window.addEventListener(e, wake, options);
    // Focus going into the page, and coming back to the app.
    window.addEventListener("blur", wake);
    window.addEventListener("focus", wake);
    return () => {
      clearTimeout(timer);
      for (const e of events) window.removeEventListener(e, wake, options);
      window.removeEventListener("blur", wake);
      window.removeEventListener("focus", wake);
    };
  }, []);
  // Focus comes here, out of the content now covered, and goes back once it is shown again.
  useEffect(() => {
    const before = document.activeElement as HTMLElement | null;
    ref.current?.focus({ preventScroll: true });
    return () => {
      if (before?.isConnected) before.focus({ preventScroll: true });
    };
  }, []);
  return (
    <section
      ref={ref}
      tabIndex={-1}
      className={`full-screen${awake ? " awake" : ""}`}
      aria-label={`${title}, full screen`}
    >
      <Suspense
        fallback={
          <p className="dashboard-status" aria-busy="true">
            Opening this dashboard…
          </p>
        }
      >
        <DashboardView
          key={id}
          id={id}
          session={session}
          onOpenStar={onOpenStar}
          bare
          {...parts}
        />
      </Suspense>
      {!awake && <div className="full-screen-wake" aria-hidden="true" />}
      <div className="full-screen-bar">
        <span>{title}</span>
        <button onClick={exit}>
          <Minimize2 size={15} aria-hidden="true" /> Exit full screen
        </button>
      </div>
    </section>
  );
}
