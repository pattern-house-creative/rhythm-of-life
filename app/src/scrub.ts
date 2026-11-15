import { useLayoutEffect, useRef, useState } from "react";
import type { KeyboardEvent, PointerEvent, RefObject } from "react";

// The width a chart actually has, so it draws at real pixels (round dots,
// crisp lines) instead of stretching a fixed drawing.
export function useWidth<T extends Element>(fallback: number) {
  const ref = useRef<T>(null);
  const [width, setWidth] = useState(fallback);
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const measure = () => {
      const w = el.getBoundingClientRect().width;
      if (w > 0) setWidth(Math.round(w));
    };
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(el);
    return () => observer.disconnect();
  }, []);
  return [ref, width] as const;
}

// Reading a chart day by day: a mouse reads wherever it hovers, a finger or
// pen by dragging sideways (an upright drag still scrolls the page), and the
// keyboard with the arrow keys. A click or tap that didn't drag activates.
export function useScrub({
  dates,
  readable,
  focus,
  setFocus,
  pick,
  onActivate,
}: {
  // The chart's days, oldest first, and the ones with a reading.
  dates: string[];
  readable: (date: string) => boolean;
  focus: string | null;
  setFocus: (date: string | null) => void;
  // The day under a pointer's horizontal position.
  pick: (clientX: number) => string | null;
  onActivate?: () => void;
}) {
  const drag = useRef<{ id: number; x: number; moved: boolean } | null>(null);
  const dragged = useRef(false);
  const step = (from: number, by: number) => {
    for (let i = from + by; i >= 0 && i < dates.length; i += by)
      if (readable(dates[i])) return dates[i];
    return null;
  };
  return {
    onPointerDown(e: PointerEvent<Element>) {
      drag.current = { id: e.pointerId, x: e.clientX, moved: false };
      dragged.current = false;
      if (e.pointerType === "mouse") setFocus(pick(e.clientX));
    },
    onPointerMove(e: PointerEvent<Element>) {
      const d = drag.current;
      if (d && d.id === e.pointerId) {
        if (!d.moved && Math.abs(e.clientX - d.x) > 6) {
          d.moved = true;
          dragged.current = true;
          try {
            e.currentTarget.setPointerCapture(e.pointerId);
          } catch {
            // The pointer may already be gone.
          }
        }
        if (d.moved || e.pointerType === "mouse") setFocus(pick(e.clientX));
      } else if (e.pointerType === "mouse") setFocus(pick(e.clientX));
    },
    onPointerUp() {
      drag.current = null;
    },
    onPointerCancel() {
      drag.current = null;
    },
    onPointerLeave(e: PointerEvent<Element>) {
      if (e.pointerType === "mouse" && !drag.current) setFocus(null);
    },
    // A drag that ends over the chart still fires a click; it isn't one.
    onClickCapture(e: { stopPropagation: () => void }) {
      if (dragged.current) {
        dragged.current = false;
        e.stopPropagation();
      }
    },
    onKeyDown(e: KeyboardEvent<Element>) {
      const at = focus ? dates.indexOf(focus) : dates.length;
      let next: string | null | undefined;
      if (e.key === "ArrowLeft" || e.key === "ArrowDown") next = step(at, -1);
      else if (e.key === "ArrowRight" || e.key === "ArrowUp")
        next = focus ? step(at, 1) : step(dates.length, -1);
      else if (e.key === "Home") next = step(-1, 1);
      else if (e.key === "End") next = step(dates.length, -1);
      else if (e.key === "Escape" && focus) {
        e.preventDefault();
        e.stopPropagation();
        setFocus(null);
        return;
      } else if ((e.key === "Enter" || e.key === " ") && onActivate) {
        e.preventDefault();
        onActivate();
        return;
      } else return;
      e.preventDefault();
      if (next) setFocus(next);
    },
    onBlur() {
      setFocus(null);
    },
  };
}

// The day slot under a horizontal position, for a chart drawn as `count`
// equal slots between `left` and `right` (in the element's own pixels).
export function slotAt(
  el: RefObject<Element | null>,
  clientX: number,
  left: number,
  right: number,
  count: number,
) {
  const box = el.current?.getBoundingClientRect();
  if (!box || count === 0) return -1;
  const inner = box.width - left - right;
  const i = Math.floor(((clientX - box.left - left) / inner) * count);
  return Math.max(0, Math.min(count - 1, i));
}
