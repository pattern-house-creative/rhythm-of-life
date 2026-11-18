import { useEffect, useLayoutEffect, useRef, useState } from "react";
import type {
  CSSProperties,
  KeyboardEvent as ReactKeyboardEvent,
  PointerEvent as ReactPointerEvent,
  ReactNode,
} from "react";
import { GripHorizontal } from "lucide-react";
import { holdMs, tapSlop } from "./gestures";
import {
  dropMove,
  foldOf,
  foldedParts,
  gridCells,
  isPartKind,
  movePart,
  partLabel,
  partName,
  readingOrder,
  resizePart,
} from "./parts";
import type {
  ArrangeCommand,
  Arranged,
  Fold,
  Part,
  Side,
  SizeStep,
} from "./parts";
import "./PartsGrid.css";

// One grid of parts (docs/dev/screens.md 10.2 and 10.9): a dashboard's layout in each of its
// hosts, or Home with a grid. Parts are placed with CSS alone: each part's element is keyed by
// its id and mounted once, in reading order as the grid first draws, and moving, resizing or
// folding never moves it in the DOM, so a page's frame never loads twice and a map never settles
// again because a neighbour moved. A dashboard folds into two columns or one when its box is too
// small for every part's smallest size; Home never folds.
//
// Arranging is in place, with no mode: a quiet grip at each part's top right and a resize corner
// at its bottom right, drawn by the app over the cell and never inside a frame. They answer
// pointer events (captured) and keys only; the grid registers no drag and drop handlers, so a
// drag a page starts in its frame can never move a part. While a part is held, a clear layer
// covers every frame so the drag isn't lost into one, and the document's root element carries
// data-arranging so the app's own Escape handlers stand aside. A change is worked out here as a
// preview and handed to the host when it is dropped, never while dragging.

// A part as a grid draws it: a dashboard's (with its kind) or Home's (without).
export interface GridPart {
  id: string;
  kind?: string;
  x: number;
  y: number;
  w: number;
  h: number;
}
// A change made by hand, as dropped: the moves and resizes in turn, the parts before and after,
// and the change in plain words.
export interface GridChange<P extends GridPart> {
  part: string;
  commands: Exclude<ArrangeCommand, { action: "focus_map" }>[];
  before: P[];
  after: P[];
  summary: string;
  announcement: string;
}
export type GridOutcome = { ok: true } | { ok: false; reason: string };

// Home's grips show for this long after a touch on Home outside a frame (10.9).
export const touchedGripsMs = 4000;
const sides: Record<string, Side> = {
  ArrowLeft: "left",
  ArrowRight: "right",
  ArrowUp: "up",
  ArrowDown: "down",
};
const steps: Record<string, SizeStep> = {
  ArrowLeft: "narrower",
  ArrowRight: "wider",
  ArrowUp: "shorter",
  ArrowDown: "taller",
};
// A pointer starts a drag once it has moved this far, in pixels.
const dragPx = 4;

type Held<P extends GridPart> =
  | {
      by: "pointer";
      id: string;
      resize: boolean;
      pointer: number;
      from: { x: number; y: number };
      // Whether the pointer has picked the part up yet (a touch holds first), and has moved.
      up: boolean;
      moved: boolean;
      start: P[];
      command: GridChange<P>["commands"][number] | null;
      preview: Arranged<{ parts: P[] }> | null;
    }
  | {
      by: "keys";
      id: string;
      start: P[];
      working: P[];
      commands: GridChange<P>["commands"];
      last: Arranged<{ parts: P[] }> | null;
    };

const place = (r: { x: number; y: number; w: number; h: number }) => ({
  gridColumn: `${r.x + 1} / span ${r.w}`,
  gridRow: `${r.y + 1} / span ${r.h}`,
});
const same = (a: readonly GridPart[], b: readonly GridPart[]) =>
  a.length === b.length &&
  a.every((p) => {
    const q = b.find((r) => r.id === p.id);
    return q && q.x === p.x && q.y === p.y && q.w === p.w && q.h === p.h;
  });

export function PartsGrid<P extends GridPart>({
  parts,
  label,
  render,
  arrange,
  refused,
  homeMode = false,
  overlay = false,
  plain = false,
  onFold,
  className = "",
  style,
}: {
  parts: readonly P[];
  // Each part's group label: "Map, near Training plan", "Glucose", or the dashboard's title.
  label: (part: P) => string;
  // A part's content (the page's frame, or a native part); nothing for a kind this app doesn't
  // know, whose cell stays empty.
  render?: (part: P, fold: Fold) => ReactNode;
  // Saves a change made by hand, and shows why where it isn't made; or null where nothing is
  // arranged by hand.
  arrange?: ((change: GridChange<P>) => Promise<GridOutcome>) | null;
  // Shows why a part dropped by the pointer couldn't go there (no room), where the host shows a
  // change's note; the grid's live region says it to screen readers either way.
  refused?: (reason: string) => void;
  // Home: never folds, and on touch screens its grips show for a few seconds after a touch.
  homeMode?: boolean;
  // Only the grips and corners, over a Home drawn as today.
  overlay?: boolean;
  // The page alone covering the grid: drawn exactly as before parts, with no grid at all.
  plain?: boolean;
  onFold?: (fold: Fold) => void;
  className?: string;
  style?: CSSProperties;
}) {
  const root = useRef<HTMLDivElement>(null);
  const [box, setBox] = useState<{
    width: number;
    height: number;
    gap: { column: number; row: number };
  } | null>(null);
  // The part held now, kept in a ref too for the pointer's and keys' handlers.
  const [held, setHeld] = useState<Held<P> | null>(null);
  const heldRef = useRef<Held<P> | null>(null);
  const hold = (next: Held<P> | null) => {
    heldRef.current = next;
    setHeld(next);
  };
  // A change dropped and on its way: drawn until the host draws what was saved, or put back.
  const [settling, setSettling] = useState<P[] | null>(null);
  const [said, setSaid] = useState("");
  const [touched, setTouched] = useState(false);
  const holdTimer = useRef(0),
    touchTimer = useRef(0);

  // The box the grid is drawn in, and its gaps, as the browser lays them out.
  useLayoutEffect(() => {
    const el = root.current;
    if (!el || plain || overlay || typeof ResizeObserver === "undefined")
      return;
    const measure = () => {
      const style = getComputedStyle(el);
      setBox({
        width: el.clientWidth,
        height: Math.min(el.clientHeight, window.innerHeight),
        gap: {
          column: Number.parseFloat(style.columnGap) || 0,
          row: Number.parseFloat(style.rowGap) || 0,
        },
      });
    };
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(el);
    return () => observer.disconnect();
  }, [plain, overlay]);
  // A dashboard folds when its box can't give every part its smallest size; a box not measured
  // yet (or a test's) draws as saved.
  const kinds = parts.every((p) => isPartKind(p.kind));
  const fold: Fold =
    homeMode || overlay || plain || !kinds || !box || !box.width || !box.height
      ? "saved"
      : foldOf({ parts: parts as unknown as Part[] }, box, box.gap);
  useEffect(() => onFold?.(fold), [fold, onFold]);

  // Each part's element in the order the grid first drew them; a part added later comes last.
  const [order, setOrder] = useState(() =>
    readingOrder(parts).map((p) => p.id),
  );
  const ids = new Set(parts.map((p) => p.id));
  const nextOrder = [
    ...order.filter((id) => ids.has(id)),
    ...readingOrder(parts)
      .map((p) => p.id)
      .filter((id) => !order.includes(id)),
  ];

  if (
    nextOrder.length !== order.length ||
    nextOrder.some((id, i) => order[i] !== id)
  )
    setOrder(nextOrder);

  const canArrange = !!arrange && fold === "saved" && !plain;
  // What is drawn: a part held by the keys moves as it goes; a dropped change, until saved.
  const drawn =
    held?.by === "keys" ? held.working : (settling ?? (parts as P[]));
  // eslint-disable-next-line react/set-state-in-effect -- Drop the optimistic arrangement once the host supplies the saved parts.
  useEffect(() => setSettling(null), [parts]);

  useEffect(
    () => () => {
      clearTimeout(holdTimer.current);
      clearTimeout(touchTimer.current);
    },
    [],
  );

  const grid = (list: readonly P[]) => ({ parts: list as P[] });
  const nameOf = (list: readonly P[], id: string) =>
    partName(grid(list) as never, id);
  const say = (text: string) =>
    // The same words twice are still read out.
    setSaid((now) => (now === text ? `${text} ` : text));
  function putBack() {
    const now = heldRef.current;
    clearTimeout(holdTimer.current);
    hold(null);
    if (now?.by === "keys" || (now?.by === "pointer" && now.moved))
      say(`${partLabel(grid(now.start) as never, now.id)} put back`);
  }
  // While a part is held the document's root says so, and Escape puts it back wherever focus is.
  const holding = held !== null;
  useEffect(() => {
    if (!holding) return;
    const rootEl = document.documentElement;
    rootEl.setAttribute("data-arranging", "");
    const key = (e: KeyboardEvent) => {
      if (e.key !== "Escape" || heldRef.current?.by !== "pointer") return;
      e.preventDefault();
      e.stopImmediatePropagation();
      putBack();
    };
    window.addEventListener("keydown", key, true);
    return () => {
      rootEl.removeAttribute("data-arranging");
      window.removeEventListener("keydown", key, true);
    };
    // putBack reads only refs and setters, so the one it closes over always works.
  }, [holding]);
  // Hands a change to the host; drawn as dropped until the host draws what it saved.
  const drop = (change: GridChange<P>) => {
    if (!arrange) return;
    setSettling(change.after);
    void arrange(change).then(
      (outcome) => {
        setSettling(null);
        say(outcome.ok ? change.announcement : outcome.reason);
      },
      () => setSettling(null),
    );
  };
  const summed = (
    start: P[],
    id: string,
    commands: GridChange<P>["commands"],
    last: Arranged<{ parts: P[] }> & { ok: true },
  ) => {
    if (commands.length === 1) return last.summary;
    const moves = commands.filter((c) => c.action === "move").length,
      name = nameOf(start, id);
    return moves === commands.length
      ? `Moved ${name}`
      : moves
        ? `Moved and resized ${name}`
        : `Resized ${name}`;
  };

  // ----- Pointer: a grip moves the part, a corner resizes it; a touch holds first.
  const cell = (clientX: number, clientY: number) => {
    const r = root.current!.getBoundingClientRect(),
      style = getComputedStyle(root.current!);
    const gx = Number.parseFloat(style.columnGap) || 0,
      gy = Number.parseFloat(style.rowGap) || 0;
    const across = (r.width + gx) / gridCells,
      down = (r.height + gy) / gridCells;
    const clamp = (v: number) => Math.max(0, Math.min(gridCells - 1, v));
    return {
      x: clamp(Math.floor((clientX - r.left + gx / 2) / across)),
      y: clamp(Math.floor((clientY - r.top + gy / 2) / down)),
      across,
      down,
    };
  };
  const press = (e: ReactPointerEvent, part: P, resize: boolean) => {
    if (!canArrange || held || e.button !== 0) return;
    e.preventDefault();
    e.stopPropagation();
    e.currentTarget.setPointerCapture?.(e.pointerId);
    const touch = e.pointerType !== "mouse";
    const next: Held<P> = {
      by: "pointer",
      id: part.id,
      resize,
      pointer: e.pointerId,
      from: { x: e.clientX, y: e.clientY },
      up: !touch,
      moved: false,
      start: [...(parts as P[])],
      command: null,
      preview: null,
    };
    hold(next);
    // A press and hold picks the part up on a touch screen.
    if (touch)
      holdTimer.current = window.setTimeout(() => {
        const h = heldRef.current;
        if (h?.by === "pointer") hold({ ...h, up: true });
        navigator.vibrate?.(10);
      }, holdMs);
  };
  const move = (e: ReactPointerEvent) => {
    const h = heldRef.current;
    if (h?.by !== "pointer" || h.pointer !== e.pointerId) return;
    const dx = e.clientX - h.from.x,
      dy = e.clientY - h.from.y;
    if (!h.up) {
      // A finger that wanders before the hold isn't picking the part up.
      if (Math.hypot(dx, dy) > tapSlop) putBack();
      return;
    }
    if (!h.moved && Math.hypot(dx, dy) < dragPx) return;
    const at = cell(e.clientX, e.clientY);
    const p = h.start.find((q) => q.id === h.id)!;
    let command: GridChange<P>["commands"][number] | null = null;
    if (h.resize) {
      const w = Math.max(
          1,
          Math.min(gridCells - p.x, p.w + Math.round(dx / at.across)),
        ),
        hh = Math.max(
          1,
          Math.min(gridCells - p.y, p.h + Math.round(dy / at.down)),
        );
      if (w !== p.w || hh !== p.h)
        command = { action: "resize", part: h.id, w, h: hh };
    } else {
      const to = {
        x: Math.max(
          0,
          Math.min(gridCells - p.w, p.x + Math.round(dx / at.across)),
        ),
        y: Math.max(
          0,
          Math.min(gridCells - p.h, p.y + Math.round(dy / at.down)),
        ),
      };
      const where = dropMove(h.start, h.id, at, to);
      if (where) command = { action: "move", part: h.id, ...where };
    }
    const preview = command
      ? command.action === "move"
        ? movePart(grid(h.start), h.id, command)
        : resizePart(grid(h.start), h.id, command)
      : null;
    hold({ ...h, moved: true, command, preview });
  };
  const release = (e: ReactPointerEvent) => {
    const h = heldRef.current;
    if (h?.by !== "pointer" || h.pointer !== e.pointerId) return;
    clearTimeout(holdTimer.current);
    hold(null);
    if (h.preview?.ok && h.command && !same(h.start, h.preview.grid.parts))
      drop({
        part: h.id,
        commands: [h.command],
        before: h.start,
        after: h.preview.grid.parts,
        summary: h.preview.summary,
        announcement: h.preview.announcement,
      });
    else if (h.preview && !h.preview.ok) {
      say(h.preview.reason);
      refused?.(h.preview.reason);
    }
  };

  // ----- Keys: Enter or Space picks the part up and drops it, the arrows move it a step, Shift
  // with the arrows resizes it, and Escape puts it back.
  const key = (e: ReactKeyboardEvent, part: P) => {
    if (!canArrange) return;
    const h = heldRef.current;
    const mine = h?.by === "keys" && h.id === part.id ? h : null;
    if (e.key === "Enter" || e.key === " ") {
      e.preventDefault();
      if (!mine) {
        if (h) return;
        hold({
          by: "keys",
          id: part.id,
          start: [...(parts as P[])],
          working: [...(parts as P[])],
          commands: [],
          last: null,
        });
        say(
          `${label(part)} picked up. The arrow keys move it, Shift and the arrow keys resize it, Enter drops it and Escape puts it back.`,
        );
        return;
      }
      hold(null);
      if (mine.last?.ok && !same(mine.start, mine.working))
        drop({
          part: part.id,
          commands: mine.commands,
          before: mine.start,
          after: mine.working,
          summary: summed(mine.start, part.id, mine.commands, mine.last),
          announcement: mine.last.announcement,
        });
      else say(`${partLabel(grid(mine.start) as never, part.id)} put back`);
      return;
    }
    if (!mine) return;
    if (e.key === "Escape") {
      e.preventDefault();
      e.stopPropagation();
      putBack();
      return;
    }
    if (!(e.key in sides)) return;
    e.preventDefault();
    e.stopPropagation();
    const command: GridChange<P>["commands"][number] = e.shiftKey
      ? { action: "resize", part: part.id, size: steps[e.key] }
      : { action: "move", part: part.id, place: sides[e.key] };
    const result =
      command.action === "move"
        ? movePart(grid(mine.working), part.id, command)
        : resizePart(grid(mine.working), part.id, command);
    if (!result.ok) return say(result.reason);
    hold({
      ...mine,
      working: result.grid.parts,
      commands: [...mine.commands, command],
      last: result,
    });
    say(result.announcement);
  };

  // On a touch screen Home's grips show for a few seconds after a touch on Home outside a
  // frame (a touch in a frame never reaches the app). Over a Home drawn as today the parts
  // beneath take the pointer, so the overlay hears it on the element it covers, and a mouse
  // over a part's area shows that part's grip as a hover would.
  const [hovered, setHovered] = useState<string | null>(null);
  const partsNow = useRef(parts);
  useLayoutEffect(() => {
    partsNow.current = parts;
  }, [parts]);
  useEffect(() => {
    const el = root.current;
    const host = overlay ? el?.parentElement : el;
    if (!el || !host || (!homeMode && !overlay)) return;
    const down = (e: PointerEvent) => {
      if (!homeMode || e.pointerType !== "touch") return;
      setTouched(true);
      clearTimeout(touchTimer.current);
      touchTimer.current = window.setTimeout(
        () => setTouched(false),
        touchedGripsMs,
      );
    };
    const over = (e: PointerEvent) => {
      if (e.pointerType !== "mouse") return;
      const r = el.getBoundingClientRect();
      const inside =
        e.clientX >= r.left &&
        e.clientX < r.right &&
        e.clientY >= r.top &&
        e.clientY < r.bottom;
      const at = inside ? cell(e.clientX, e.clientY) : null;
      const part = at
        ? partsNow.current.find(
            (p) =>
              at.x >= p.x &&
              at.x < p.x + p.w &&
              at.y >= p.y &&
              at.y < p.y + p.h,
          )
        : undefined;
      setHovered(part?.id ?? null);
    };
    const left = () => setHovered(null);
    host.addEventListener("pointerdown", down, true);
    if (overlay) {
      host.addEventListener("pointermove", over, true);
      host.addEventListener("pointerleave", left);
    }
    return () => {
      host.removeEventListener("pointerdown", down, true);
      host.removeEventListener("pointermove", over, true);
      host.removeEventListener("pointerleave", left);
    };
    // cell reads the grid's box as it is when the pointer moves.
  }, [homeMode, overlay]);

  const folded =
    fold === "saved"
      ? null
      : foldedParts({ parts: drawn as unknown as Part[] }, fold, box!);
  const reading = new Map(readingOrder(drawn).map((p, i) => [p.id, i]));
  const ghost =
    held?.by === "pointer" && held.moved && held.preview
      ? held.preview.ok
        ? held.preview.grid.parts.find((p) => p.id === held.id)
        : null
      : null;
  return (
    <div
      ref={root}
      className={`parts ${plain ? "plain" : fold}${homeMode ? " home" : ""}${overlay ? " overlay" : ""}${canArrange ? " arranging" : ""}${className ? ` ${className}` : ""}`}
      style={style}
      data-held={held ? "" : undefined}
      data-touched={touched ? "" : undefined}
    >
      {order.map((id) => {
        const part = drawn.find((p) => p.id === id);
        if (!part) return null;
        const known = part.kind === undefined || isPartKind(part.kind);
        const shown = folded?.find((f) => f.part.id === id);
        const name = nameOf(drawn, id).replace(/^the /, "");
        const placed: CSSProperties | undefined = plain
          ? undefined
          : shown
            ? {
                order: reading.get(id),
                gridColumn: shown.columns === 2 ? "1 / -1" : undefined,
                height: shown.height ?? undefined,
              }
            : place(part);
        return (
          <div
            key={id}
            className={`parts-part${held?.id === id ? " held" : ""}`}
            data-part={id}
            data-hover={overlay && hovered === id ? "" : undefined}
            data-kind={part.kind}
            role={plain ? undefined : "group"}
            aria-label={plain ? undefined : label(part)}
            style={placed}
          >
            {canArrange && known && (
              <button
                type="button"
                className="parts-grip"
                aria-label={`Arrange ${name}`}
                aria-pressed={held?.id === id ? true : undefined}
                onPointerDown={(e) => press(e, part, false)}
                onPointerMove={move}
                onPointerUp={release}
                onPointerCancel={putBack}
                onKeyDown={(e) => key(e, part)}
                onBlur={() => {
                  if (heldRef.current?.by === "keys") putBack();
                }}
              >
                <GripHorizontal size={14} aria-hidden="true" />
              </button>
            )}
            {!overlay && (
              <div className="parts-body">
                {known ? render?.(part, fold) : null}
              </div>
            )}
            {canArrange && known && (
              <span
                className="parts-corner"
                aria-hidden="true"
                onPointerDown={(e) => press(e, part, true)}
                onPointerMove={move}
                onPointerUp={release}
                onPointerCancel={putBack}
              />
            )}
          </div>
        );
      })}
      {ghost && (
        <div className="parts-ghost" aria-hidden="true" style={place(ghost)} />
      )}
      {held && <div className="parts-shield" aria-hidden="true" />}
      {(canArrange || said) && (
        <p className="parts-live" role="status" aria-live="polite">
          {said}
        </p>
      )}
    </div>
  );
}
