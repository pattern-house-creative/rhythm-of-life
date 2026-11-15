import { keepUpToDate } from "./appUpdates";
import { takePageCapture } from "./bookmarklet";
import type { PageDraft } from "./bookmarklet";

type Register = Parameters<typeof keepUpToDate>[0];

// What the app's start does with the address it opened at (main.tsx, SB-P7-03): a page the Memories view's bookmark
// sent (bookmarklet.ts) is read from it and taken out of it first, and its tab is not reloaded into a new build, which
// would lose what came, until plain() says the tab draws the app after all (Start.tsx: the engine doesn't serve the
// Memories view); any other address keeps the app up to date as before, at once.
export function startPage(
  register: Register,
  where: Location = location,
  past: History = history,
  keep: typeof keepUpToDate = keepUpToDate,
): { captured: PageDraft | "unreadable" | null; plain: () => void } {
  const captured = takePageCapture(where, past);
  let kept = false;
  const plain = () => {
    if (kept) return;
    kept = true;
    keep(register);
  };
  if (!captured) plain();
  return { captured, plain };
}
