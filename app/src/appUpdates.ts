import type { RegisterSWOptions } from "virtual:pwa-register";

// Keeps the installed app on the newest version. The service worker takes a
// new version over as soon as it's found: on opening, on coming back into
// view, and hourly, so a screen left open updates too. The open copy still
// runs the old code, so it reloads at a quiet moment: at once if nothing was
// touched since it opened or it's out of view, otherwise after a minute with
// no tap or key and no text field or embedded frame (a dashboard being
// edited) focused, or after a quarter of an hour with no tap or key at all.
// A part the old copy hadn't loaded yet is gone once a new version is
// deployed, so failing to load one moves to that version at once. Out of view
// while a file picker is open (a phone leaves the page for it) doesn't count:
// reloading then would lose the file being added.
export const quietMs = 60_000;
export const focusGraceMs = 15 * 60_000;
export const checkEveryMs = 60 * 60_000;
export const openingMs = 20_000;
const recheckMs = 15_000;
const checkGapMs = 60_000;
const urgentMs = 2 * 60_000;

type Register = (options: RegisterSWOptions) => unknown;

export function keepUpToDate(register: Register, win: Window = window) {
  const doc = win.document;
  const openedAt = Date.now();
  let registration: ServiceWorkerRegistration | undefined;
  let lastCheck = openedAt;
  let lastInput = openedAt;
  let touched = false;
  let ready = false;
  let urgentUntil = 0;
  let timer: ReturnType<typeof setTimeout> | undefined;

  const check = () => {
    if (!registration || !win.navigator.onLine) return;
    lastCheck = Date.now();
    registration.update().catch(() => {});
  };
  const focused = () => {
    const el = doc.activeElement as HTMLElement | null;
    return Boolean(
      el &&
        (el.isContentEditable ||
          ["INPUT", "TEXTAREA", "SELECT", "IFRAME"].includes(el.tagName)),
    );
  };
  const settle = () => {
    clearTimeout(timer);
    if (!ready) return;
    const now = Date.now();
    const idle = now - lastInput;
    if (
      now < urgentUntil ||
      (doc.visibilityState === "hidden" &&
        !doc.documentElement?.hasAttribute("data-picking-file")) ||
      (!touched && now - openedAt < openingMs) ||
      (idle >= quietMs && (!focused() || idle >= focusGraceMs))
    ) {
      win.location.reload();
      return;
    }
    timer = setTimeout(settle, Math.max(quietMs - idle, recheckMs));
  };

  for (const type of ["pointerdown", "keydown", "wheel"]) {
    win.addEventListener(
      type,
      () => {
        touched = true;
        lastInput = Date.now();
      },
      { capture: true, passive: true },
    );
  }
  doc.addEventListener("visibilitychange", () => {
    if (
      doc.visibilityState === "visible" &&
      Date.now() - lastCheck >= checkGapMs
    ) {
      check();
    }
    settle();
  });
  win.addEventListener("vite:preloadError", () => {
    if (!win.navigator.onLine) return;
    urgentUntil = Date.now() + urgentMs;
    if (ready) settle();
    else check();
  });

  register({
    immediate: true,
    onNeedReload() {
      ready = true;
      settle();
    },
    onRegisteredSW(_url, r) {
      registration = r;
      win.setInterval(check, checkEveryMs);
    },
  });
}

// "Try again" after access or a source was lost. The app has no sign-in of its
// own: the front door before it (an access proxy, or a hosted platform's login)
// signs the household in. The service worker answers every page load from its
// cache, so a plain reload never reaches a front door whose session has ended,
// and the installed app could never get back past it. Online, the worker is
// first let go, so this one load (and the front door's sign-in, and its way
// back) comes from the network; the page registers the worker again as it
// opens, and its cached files are kept. Offline, the cached copy opens as before.
export async function reloadFromNetwork(win: Window = window) {
  if (win.navigator.onLine && win.navigator.serviceWorker) {
    try {
      const registrations =
        await win.navigator.serviceWorker.getRegistrations();
      await Promise.all(registrations.map((r) => r.unregister()));
    } catch {
      // Refused (a private window, say): a plain reload is what's left.
    }
  }
  win.location.reload();
}
