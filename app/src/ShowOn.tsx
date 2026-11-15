import { useEffect, useRef, useState } from "react";
import { Cast } from "lucide-react";
import type { GraphNode } from "./knowledge";
import { dashboardOf } from "./dashboards";
import { sentWords } from "./screens";
import type { ScreenSummary, SendAnswer } from "./screens";

// The other open screens, and showing a star on one of them (docs/dev/screens.md section 6).
export interface ShowOnScreens {
  list: (signal?: AbortSignal) => Promise<ScreenSummary[]>;
  send: (
    to: string,
    action: string,
    args: Record<string, unknown>,
    signal?: AbortSignal,
  ) => Promise<SendAnswer>;
}

// "Show on…" in a star's panel: the other open screens, listed right there (no popup).
// Choosing one shows the star there (a dashboard star as its dashboard) and says how it went.
export function ShowOn({
  star,
  screens,
}: {
  star: GraphNode;
  screens: ShowOnScreens;
}) {
  const [open, setOpen] = useState(false);
  const [others, setOthers] = useState<ScreenSummary[] | "looking" | "failed">(
    "looking",
  );
  const [sending, setSending] = useState(false);
  const [said, setSaid] = useState("");
  const flight = useRef<AbortController | null>(null);
  useEffect(() => () => flight.current?.abort(), []);
  const begin = () => {
    flight.current?.abort();
    const controller = new AbortController();
    flight.current = controller;
    return controller;
  };
  const look = async () => {
    const controller = begin();
    setOthers("looking");
    try {
      const all = await screens.list(controller.signal);
      if (!controller.signal.aborted)
        setOthers(all.filter((s) => s.open && !s.self));
    } catch {
      if (!controller.signal.aborted) setOthers("failed");
    }
  };
  const show = async (to: ScreenSummary) => {
    const controller = begin();
    const dashboard = dashboardOf(star);
    setSending(true);
    setSaid(`Sending to ${to.name}…`);
    const answer = await screens.send(
      to.id,
      dashboard ? "open_dashboard" : "open_star",
      dashboard ? { dashboard } : { star: star.id },
      controller.signal,
    );
    if (controller.signal.aborted) return;
    setSaid(answer.ok ? sentWords(answer.command, to.name) : answer.reason);
    setSending(false);
  };
  return (
    <>
      <button
        className="secondary-action show-on-toggle"
        aria-expanded={open}
        onClick={() => {
          if (open) {
            flight.current?.abort();
            setSending(false);
            return setOpen(false);
          }
          setOpen(true);
          setSaid("");
          void look();
        }}
      >
        <Cast size={14} aria-hidden="true" /> Show on…
      </button>
      {open && (
        <div
          className="show-on"
          role="group"
          aria-label="Show on another screen"
        >
          {others === "looking" ? (
            <p role="status">Looking for other screens…</p>
          ) : others === "failed" ? (
            <p role="status">Other screens can't be reached right now.</p>
          ) : others.length === 0 ? (
            <p role="status">No other screen is open right now.</p>
          ) : (
            <ul aria-label="Open screens">
              {others.map((s) => (
                <li key={s.id}>
                  <button disabled={sending} onClick={() => void show(s)}>
                    {s.name}
                  </button>
                </li>
              ))}
            </ul>
          )}
          {said && <p role="status">{said}</p>}
        </div>
      )}
    </>
  );
}
