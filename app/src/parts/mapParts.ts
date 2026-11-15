import { useEffect, useSyncExternalStore } from "react";

// The map parts on screen (docs/dev/screens.md 10.9). While one shows, the
// explorer's own sky behind it rests, as Home's sky rests under the explorer,
// so no more than a dashboard's own two maps move at once.
const shown = new Set<symbol>(),
  watchers = new Set<() => void>();
const changed = () => watchers.forEach((w) => w());

// Counts a map part as shown while it is mounted and `on`.
export function useMapPartShowing(on: boolean) {
  useEffect(() => {
    if (!on) return;
    const token = Symbol("map part");
    shown.add(token);
    changed();
    return () => {
      shown.delete(token);
      changed();
    };
  }, [on]);
}

// Whether any map part shows now.
export const useMapPartShown = () =>
  useSyncExternalStore(
    (watch) => {
      watchers.add(watch);
      return () => void watchers.delete(watch);
    },
    () => shown.size > 0,
    () => false,
  );
