import { afterEach, beforeEach, expect, it, vi } from "vitest";
import type { RegisterSWOptions } from "virtual:pwa-register";
import {
  checkEveryMs,
  focusGraceMs,
  keepUpToDate,
  quietMs,
  reloadFromNetwork,
} from "../src/appUpdates";

beforeEach(() => {
  vi.useFakeTimers();
  vi.setSystemTime(new Date("2026-10-29T12:00:00Z"));
});
afterEach(() => vi.useRealTimers());

// A stand-in window: its own events, a page that can be hidden or have a
// field focused, and a reload to watch.
function app() {
  const events = new EventTarget();
  const page = new EventTarget();
  const marks = new Set<string>();
  const doc = Object.assign(page, {
    visibilityState: "visible" as DocumentVisibilityState,
    activeElement: null as { tagName: string; isContentEditable?: boolean } | null,
    documentElement: {
      hasAttribute: (name: string) => marks.has(name),
    },
  });
  const reload = vi.fn();
  const navigator = { onLine: true };
  const win = {
    document: doc,
    navigator,
    location: { reload },
    addEventListener: events.addEventListener.bind(events),
    setInterval: (fn: () => void, ms: number) => setInterval(fn, ms),
  } as unknown as Window;
  const update = vi.fn(() => Promise.resolve());
  let options: RegisterSWOptions = {};
  keepUpToDate((o) => {
    options = o;
  }, win);
  options.onRegisteredSW?.("/sw.js", {
    update,
  } as unknown as ServiceWorkerRegistration);
  return {
    doc,
    reload,
    update,
    navigator,
    options,
    tap: () => events.dispatchEvent(new Event("pointerdown")),
    show: (state: DocumentVisibilityState) => {
      doc.visibilityState = state;
      page.dispatchEvent(new Event("visibilitychange"));
    },
    picking: (on: boolean) =>
      on ? marks.add("data-picking-file") : marks.delete("data-picking-file"),
    lostPart: () => events.dispatchEvent(new Event("vite:preloadError")),
    newVersion: () => options.onNeedReload?.(),
  };
}

it("registers at once, and moves to a new version right away when nothing was touched since opening", () => {
  const a = app();
  expect(a.options.immediate).toBe(true);
  vi.advanceTimersByTime(5000);
  a.newVersion();
  expect(a.reload).toHaveBeenCalledTimes(1);
});

it("otherwise waits for a quiet minute, and longer while a field or a dashboard frame has focus", () => {
  const a = app();
  a.tap();
  vi.advanceTimersByTime(30_000);
  a.newVersion();
  expect(a.reload).not.toHaveBeenCalled();
  vi.advanceTimersByTime(quietMs - 30_000 - 1);
  expect(a.reload).not.toHaveBeenCalled();
  vi.advanceTimersByTime(1);
  expect(a.reload).toHaveBeenCalledTimes(1);

  // Typing in a dashboard happens inside its frame, out of the app's sight.
  const b = app();
  b.tap();
  b.doc.activeElement = { tagName: "IFRAME" };
  b.newVersion();
  vi.advanceTimersByTime(quietMs * 5);
  expect(b.reload).not.toHaveBeenCalled();
  vi.advanceTimersByTime(focusGraceMs);
  expect(b.reload).toHaveBeenCalledTimes(1);

  const c = app();
  c.tap();
  c.doc.activeElement = { tagName: "DIV", isContentEditable: true };
  c.newVersion();
  vi.advanceTimersByTime(quietMs * 2);
  expect(c.reload).not.toHaveBeenCalled();
});

it("moves to a waiting version as soon as the app is out of view", () => {
  const a = app();
  a.tap();
  a.newVersion();
  expect(a.reload).not.toHaveBeenCalled();
  a.show("hidden");
  expect(a.reload).toHaveBeenCalledTimes(1);
});

it("doesn't move to a waiting version while a phone's file picker hides the app", () => {
  const a = app();
  a.tap();
  a.newVersion();
  a.picking(true);
  a.show("hidden");
  expect(a.reload).not.toHaveBeenCalled();
  a.picking(false);
  a.show("visible");
  a.show("hidden");
  expect(a.reload).toHaveBeenCalledTimes(1);
});

it("checks for a new version on coming back into view, at most once a minute, and every hour", () => {
  const a = app();
  vi.advanceTimersByTime(10_000);
  a.show("hidden");
  a.show("visible");
  expect(a.update).not.toHaveBeenCalled();
  vi.advanceTimersByTime(60_000);
  a.show("visible");
  expect(a.update).toHaveBeenCalledTimes(1);
  vi.advanceTimersByTime(checkEveryMs);
  expect(a.update).toHaveBeenCalledTimes(2);
  a.navigator.onLine = false;
  vi.advanceTimersByTime(checkEveryMs);
  expect(a.update).toHaveBeenCalledTimes(2);
  // Nothing new was found, so nothing reloads.
  expect(a.reload).not.toHaveBeenCalled();
});

it("moves to the new version at once when a part of the old one fails to load", () => {
  // The new version is already waiting.
  const a = app();
  a.tap();
  a.newVersion();
  a.lostPart();
  expect(a.reload).toHaveBeenCalledTimes(1);

  // Not found yet: it looks, and takes it the moment it arrives.
  const b = app();
  b.tap();
  b.lostPart();
  expect(b.update).toHaveBeenCalledTimes(1);
  expect(b.reload).not.toHaveBeenCalled();
  b.newVersion();
  expect(b.reload).toHaveBeenCalledTimes(1);

  // Offline, a missing part is just offline.
  const c = app();
  c.tap();
  c.navigator.onLine = false;
  c.lostPart();
  c.newVersion();
  expect(c.update).not.toHaveBeenCalled();
  expect(c.reload).not.toHaveBeenCalled();
});

// A stand-in window with a service worker: its registrations, and the order in
// which the worker is let go and the page reloads.
function withWorker({ onLine = true, refuse = false } = {}) {
  const order: string[] = [];
  const registration = {
    unregister: vi.fn(async () => {
      order.push("unregister");
      return true;
    }),
  };
  const win = {
    navigator: {
      onLine,
      serviceWorker: {
        getRegistrations: vi.fn(async () => {
          if (refuse) throw new DOMException("Refused", "SecurityError");
          return [registration];
        }),
      },
    },
    location: { reload: vi.fn(() => order.push("reload")) },
  } as unknown as Window;
  return { win, order, registration };
}

it("reloads past the service worker, so a front door whose session ended can ask to sign in again", async () => {
  const a = withWorker();
  await reloadFromNetwork(a.win);
  expect(a.order).toEqual(["unregister", "reload"]);

  // Offline, the cached copy opens: the worker stays.
  const b = withWorker({ onLine: false });
  await reloadFromNetwork(b.win);
  expect(b.order).toEqual(["reload"]);
  expect(b.registration.unregister).not.toHaveBeenCalled();

  // A browser that won't say still reloads.
  const c = withWorker({ refuse: true });
  await reloadFromNetwork(c.win);
  expect(c.order).toEqual(["reload"]);
});
