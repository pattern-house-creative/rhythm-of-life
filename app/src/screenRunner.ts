// Studio follows the tab already holding the screen transport lock. No second tab competes to validate drafts.
const runners = new Map<string, object>();
const changed = (screen: string, active: boolean) =>
  window.dispatchEvent(
    new CustomEvent("rhythm:screen-runner", { detail: { screen, active } }),
  );
export const screenRunning = (screen: string) => runners.has(screen);
export function acquireScreenRunner(screen: string) {
  const token = {};
  runners.set(screen, token);
  changed(screen, true);
  return () => {
    if (runners.get(screen) === token) {
      runners.delete(screen);
      changed(screen, false);
    }
  };
}
