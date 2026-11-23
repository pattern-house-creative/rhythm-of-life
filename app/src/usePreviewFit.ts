import { useLayoutEffect, useRef } from "react";

// A launcher tile is a fixed box, and Android's text size reaches its page, so a preview Rhythm
// draws may hold more words than its tile at enlarged text or on a narrow tile. While its content
// is taller or wider than the tile, it gives up the next of `drops` in turn (named in its
// `data-drop`, which launcherCard.css hides), so nothing overlaps or is cut; with room again, it
// takes them back. Measured before it is painted, again when the tile changes size, and once the
// fonts have loaded.
export function usePreviewFit<T extends HTMLElement>(drops: readonly string[]) {
  const ref = useRef<T>(null);
  const order = drops.join(" ");
  useLayoutEffect(() => {
    const tile = ref.current;
    if (!tile) return;
    const steps = order.split(" ");
    const over = () =>
      tile.scrollHeight > tile.clientHeight ||
      tile.scrollWidth > tile.clientWidth;
    const fit = () => {
      let given = 0;
      tile.dataset.drop = "";
      while (given < steps.length && over()) {
        given += 1;
        tile.dataset.drop = steps.slice(0, given).join(" ");
      }
    };
    fit();
    const resized =
      typeof ResizeObserver === "undefined" ? null : new ResizeObserver(fit);
    resized?.observe(tile);
    let gone = false;
    void document.fonts?.ready.then(() => {
      if (!gone) fit();
    });
    return () => {
      gone = true;
      resized?.disconnect();
    };
  });
  return ref;
}
