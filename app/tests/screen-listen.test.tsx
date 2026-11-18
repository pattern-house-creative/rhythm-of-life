import { act, render, renderHook } from "@testing-library/react";
import { useEffect, useState } from "react";
import { afterEach, expect, it, vi } from "vitest";
import {
  listenBackoff,
  renameAgainMs,
  touchedMs,
  useScreenListen,
} from "../src/useScreenListen";
import type { ScreenHandlers } from "../src/useScreenListen";
import type { GraphSession } from "../src/liveGraph";
import {
  adoptScreenName,
  defaultTiles,
  saveScreenName,
  screenIdentity,
  watchScreenName,
} from "../src/screens";
import type { ListenAnswer, ScreenCommand } from "../src/screens";

// The listen loop (docs/dev/screens.md section 6): one listen waiting while the page is visible
// and online, a command applied and its outcome reported, backing off after errors.
afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
  Object.defineProperty(document, "hidden", {
    configurable: true,
    value: false,
  });
});
const screen = { id: "kitchenScreen0000000001", name: "Kitchen iPad" };
const self = { id: screen.id, name: screen.name, settings: {} };
const command: ScreenCommand = {
  id: "6f1c2b3a-4d5e-4f60-8a7b-9c0d1e2f3a4b",
  action: "home",
  args: {},
  sender: { kind: "assistant" },
  sentAt: "2026-10-31T09:00:00Z",
  deliveredAt: "2026-10-31T09:00:00.2Z",
  doneAt: null,
  result: "delivered",
  detail: {},
  ms: { deliver: 200, show: null },
};
const showing = {
  view: "home" as const,
  layout: "hero" as const,
  tiles: defaultTiles,
  narrow: false,
};
function handlers(over: Partial<ScreenHandlers> = {}): ScreenHandlers {
  return {
    showing: () => showing,
    settings: () => null,
    settingsSent: vi.fn(),
    adopt: vi.fn(),
    named: vi.fn(),
    apply: vi.fn(async () => ({
      result: "shown" as const,
      detail: { summary: "Back to Home" },
    })),
    ...over,
  };
}
// A listen that waits until the test answers it, or until its signal ends it.
function waiting() {
  const calls: {
    args: Parameters<NonNullable<GraphSession["screenListen"]>>[0];
    signal: AbortSignal;
    answer: (value: ListenAnswer | null) => void;
    fail: () => void;
  }[] = [];
  const listen = vi.fn(
    (
      args: Parameters<NonNullable<GraphSession["screenListen"]>>[0],
      signal?: AbortSignal,
    ) =>
      new Promise<ListenAnswer | null>((resolve, reject) => {
        calls.push({
          args,
          signal: signal!,
          answer: resolve,
          fail: () => reject(new Error("unavailable")),
        });
        signal?.addEventListener("abort", () =>
          reject(new DOMException("aborted", "AbortError")),
        );
      }),
  );
  const session = {
    initial: { nodes: [], edges: [] },
    screenListen: listen,
    screenDone: vi.fn(async () => {}),
    screenRename: vi.fn(async () => {}),
    screenStop: vi.fn(),
  } as unknown as GraphSession;
  return { calls, listen, session };
}
const hide = (hidden: boolean) => {
  Object.defineProperty(document, "hidden", {
    configurable: true,
    value: hidden,
  });
  document.dispatchEvent(new Event("visibilitychange"));
};

it("keeps one listen waiting, applies what it claims and reports the outcome", async () => {
  const { calls, session } = waiting();
  const h = handlers();
  renderHook(() => useScreenListen(session, screen, h));
  expect(calls).toHaveLength(1);
  expect(calls[0].args).toEqual({
    screen: screen.id,
    name: "Kitchen iPad",
    hold: 25,
    showing,
  });
  await act(async () => calls[0].answer({ command, screen: self }));
  expect(h.apply).toHaveBeenCalledWith(command);
  expect(session.screenDone).toHaveBeenCalledWith({
    screen: screen.id,
    command: command.id,
    result: "shown",
    detail: { summary: "Back to Home" },
    showing,
  });
  // Then straight back to waiting; an empty wait is just another listen.
  await vi.waitFor(() => expect(calls).toHaveLength(2));
  expect(h.adopt).toHaveBeenCalledTimes(1);
  // Every answer says the name the engine keeps, which may be numbered.
  expect(h.named).toHaveBeenLastCalledWith("Kitchen iPad");
  await act(async () =>
    calls[1].answer({
      command: null,
      screen: { ...self, name: "Kitchen iPad 2" },
    }),
  );
  expect(h.named).toHaveBeenLastCalledWith("Kitchen iPad 2");
});

it("sends the name it takes from an answer with the very next listen, before the app draws it", async () => {
  vi.useFakeTimers();
  const { calls, session } = waiting();
  // Still on its default name, so it takes the one the engine keeps.
  const h = handlers({ named: vi.fn((name: string) => name) });
  const { rerender } = renderHook(
    (props: typeof screen) => useScreenListen(session, props, h),
    { initialProps: screen },
  );
  // Another screen had the default first, so the engine numbered this one. The answer came
  // after the listen's wait: the next listen goes out at once, before the app draws the name.
  await act(async () => vi.advanceTimersByTime(25_000));
  await act(async () =>
    calls[0].answer({
      command: null,
      screen: { ...self, name: "Kitchen iPad 2" },
    }),
  );
  expect(h.named).toHaveBeenLastCalledWith("Kitchen iPad 2");
  expect(calls).toHaveLength(2);
  expect(calls[1].args.name).toBe("Kitchen iPad 2");
  // A render still carrying the old name doesn't put it back: a touch's short listen
  // carries the new one too.
  rerender(screen);
  act(() => {
    vi.advanceTimersByTime(1000);
    window.dispatchEvent(new Event("pointerdown"));
  });
  await act(async () => vi.advanceTimersByTime(touchedMs));
  expect(calls).toHaveLength(3);
  expect(calls[2].args).toMatchObject({ hold: 0, name: "Kitchen iPad 2" });
  // A new name from the app (someone renamed the screen) is the one sent from then on.
  rerender({ ...screen, name: "Hall" });
  await act(async () => vi.advanceTimersByTime(14_000));
  await act(async () =>
    calls[1].answer({ command: null, screen: { ...self, name: "Hall" } }),
  );
  expect(calls).toHaveLength(4);
  expect(calls[3].args.name).toBe("Hall");
});

it("keeps sending a name a person chose over the engine's, and renames the engine back to it at most once a minute", async () => {
  vi.useFakeTimers();
  const { calls, session } = waiting();
  const rename = vi.mocked(session.screenRename!);
  // The first rename doesn't reach the engine.
  rename.mockRejectedValueOnce(new Error("unavailable"));
  const h = handlers({ named: vi.fn(() => "Kitchen iPad") });
  renderHook(() => useScreenListen(session, screen, h));
  const answered = async (i: number, name: string) => {
    await act(async () => vi.advanceTimersByTime(25_000));
    await act(async () =>
      calls[i].answer({ command: null, screen: { ...self, name } }),
    );
  };
  // The engine keeps another name (a rename that never reached it, say). The listens carry the
  // chosen one, and it goes to the engine again as a rename, since a listen never strips a
  // number the engine gave.
  await answered(0, "Kitchen iPad 2");
  expect(h.named).toHaveBeenLastCalledWith("Kitchen iPad 2");
  expect(calls[1].args.name).toBe("Kitchen iPad");
  expect(rename).toHaveBeenCalledTimes(1);
  expect(rename).toHaveBeenLastCalledWith(screen.id, "Kitchen iPad");
  // Not again within the minute, though that one failed; again after it.
  await answered(1, "Kitchen iPad 2");
  await answered(2, "Kitchen iPad 2");
  expect(rename).toHaveBeenCalledTimes(1);
  expect(renameAgainMs).toBe(60_000);
  await answered(3, "Kitchen iPad 2");
  expect(rename).toHaveBeenCalledTimes(2);
  // Once an answer carries it, nothing more goes.
  for (let i = 4; i < 8; i++) await answered(i, "Kitchen iPad");
  expect(rename).toHaveBeenCalledTimes(2);
  expect(calls[8].args.name).toBe("Kitchen iPad");
  // A screen that keeps its own name (a demo tab's) sends no rename.
  vi.mocked(h.named).mockReturnValue(null);
  for (let i = 8; i < 12; i++) await answered(i, "Hall");
  expect(rename).toHaveBeenCalledTimes(2);
  expect(calls[12].args.name).toBe("Kitchen iPad");
});

it("stops when the page is hidden and starts again when it is shown", async () => {
  const { calls, session } = waiting();
  const { unmount } = renderHook(() =>
    useScreenListen(session, screen, handlers()),
  );
  expect(calls).toHaveLength(1);
  act(() => hide(true));
  expect(calls[0].signal.aborted).toBe(true);
  await act(async () => {});
  expect(calls).toHaveLength(1);
  act(() => hide(false));
  expect(calls).toHaveLength(2);
  expect(calls[1].signal.aborted).toBe(false);
  // Offline, pagehide and lost access stop it too; lost access until a new session.
  act(() => {
    window.dispatchEvent(new Event("offline"));
  });
  expect(calls[1].signal.aborted).toBe(true);
  act(() => {
    window.dispatchEvent(new Event("online"));
  });
  expect(calls).toHaveLength(3);
  act(() => {
    window.dispatchEvent(new Event("pagehide"));
  });
  expect(calls[2].signal.aborted).toBe(true);
  act(() => {
    window.dispatchEvent(new Event("pageshow"));
  });
  expect(calls).toHaveLength(4);
  act(() => {
    window.dispatchEvent(new Event("observatory-access-lost"));
  });
  expect(calls[3].signal.aborted).toBe(true);
  act(() => hide(false));
  act(() => {
    window.dispatchEvent(new Event("online"));
  });
  expect(calls).toHaveLength(4);
  unmount();
  expect(session.screenStop).toHaveBeenCalled();
});

it("backs off after errors: 1, 2, 5, 10, then 30 seconds", async () => {
  vi.useFakeTimers();
  const { calls, session } = waiting();
  renderHook(() => useScreenListen(session, screen, handlers()));
  expect(listenBackoff).toEqual([1000, 2000, 5000, 10000, 30000]);
  for (const wait of [1000, 2000, 5000, 10000, 30000, 30000]) {
    const before = calls.length;
    await act(async () => calls.at(-1)!.fail());
    await act(async () => vi.advanceTimersByTime(wait - 1));
    expect(calls).toHaveLength(before);
    await act(async () => vi.advanceTimersByTime(1));
    expect(calls).toHaveLength(before + 1);
  }
  // An answer resets it.
  await act(async () => calls.at(-1)!.answer({ command: null, screen: self }));
  await act(async () => vi.advanceTimersByTime(1000));
  const before = calls.length;
  await act(async () => calls.at(-1)!.fail());
  await act(async () => vi.advanceTimersByTime(1000));
  expect(calls).toHaveLength(before + 1);
});

it("waits out a backoff no longer once hidden, and never spins on an empty answer", async () => {
  vi.useFakeTimers();
  const { calls, session } = waiting();
  renderHook(() => useScreenListen(session, screen, handlers()));
  await act(async () => calls[0].fail());
  act(() => hide(true));
  await act(async () => vi.advanceTimersByTime(60_000));
  expect(calls).toHaveLength(1);
  act(() => hide(false));
  expect(calls).toHaveLength(2);
  // An empty answer that came back at once waits a second before the next listen.
  await act(async () => calls[1].answer({ command: null, screen: self }));
  expect(calls).toHaveLength(2);
  await act(async () => vi.advanceTimersByTime(1000));
  expect(calls).toHaveLength(3);
});

it("stops for good where screens are off", async () => {
  const { calls, session } = waiting();
  renderHook(() => useScreenListen(session, screen, handlers()));
  await act(async () => calls[0].answer(null));
  act(() => hide(true));
  act(() => hide(false));
  expect(calls).toHaveLength(1);
});

it("sends settings when asked to, and confirms them once a listen carried them", async () => {
  const { calls, session } = waiting();
  const value = { layout: "band" as const, tiles: defaultTiles };
  let pending: { value: typeof value; mark: string } | null = {
    value,
    mark: "m1",
  };
  const h = handlers({
    settings: () => pending,
    settingsSent: vi.fn((mark: string) => {
      if (pending?.mark === mark) pending = null;
    }),
  });
  renderHook(() => useScreenListen(session, screen, h));
  expect(calls[0].args.settings).toEqual(value);
  await act(async () =>
    calls[0].answer({
      command: null,
      screen: { ...self, settings: { layout: "corners" } },
    }),
  );
  expect(h.settingsSent).toHaveBeenCalledWith("m1");
  expect(h.adopt).toHaveBeenCalledWith({ layout: "corners" });
  await act(async () => new Promise((r) => setTimeout(r, 1050)));
  expect(calls[1].args.settings).toBeUndefined();
});

it("has one tab of a browser listen for the screen they share, and the next once it stops", async () => {
  // This browser's locks: one holder for each name, the others waiting their turn.
  const queue = new Map<string, Promise<unknown>>();
  const locks = {
    request: (
      name: string,
      options: { signal?: AbortSignal },
      task: () => Promise<unknown>,
    ) => {
      const turn = (queue.get(name) ?? Promise.resolve()).then(() => {
        if (options.signal?.aborted)
          throw new DOMException("aborted", "AbortError");
        return task();
      });
      queue.set(
        name,
        turn.catch(() => {}),
      );
      return turn;
    },
  };
  Object.defineProperty(navigator, "locks", {
    configurable: true,
    value: locks,
  });
  try {
    const { calls, session } = waiting();
    const first = handlers(),
      second = handlers();
    const tab = renderHook(() => useScreenListen(session, screen, first));
    renderHook(() => useScreenListen(session, screen, second));
    await vi.waitFor(() => expect(calls).toHaveLength(1));
    await act(async () => {});
    // Only the first tab listens; whatever it claims, Undo included, it applies.
    expect(calls).toHaveLength(1);
    await act(async () => calls[0].answer({ command, screen: self }));
    expect(first.apply).toHaveBeenCalledWith(command);
    await vi.waitFor(() => expect(calls).toHaveLength(2));
    // It closes: the other tab takes over.
    tab.unmount();
    expect(calls[1].signal.aborted).toBe(true);
    await vi.waitFor(() => expect(calls).toHaveLength(3));
    expect(calls[2].signal.aborted).toBe(false);
    await act(async () => calls[2].answer({ command, screen: self }));
    expect(second.apply).toHaveBeenCalledWith(command);
    expect(first.apply).toHaveBeenCalledTimes(1);
  } finally {
    Reflect.deleteProperty(navigator, "locks");
  }
});

// This browser's locks as the Web Locks API keeps them: one holder for each name and the others
// queued in turn; a request whose signal ends leaves the queue, and a lock goes when its task ends.
function browserLocks() {
  const held = new Set<string>(),
    queues = new Map<string, (() => void)[]>();
  const next = (name: string) => {
    if (!held.has(name)) queues.get(name)?.shift()?.();
  };
  return {
    request: (
      name: string,
      options: { signal?: AbortSignal },
      task: () => Promise<unknown>,
    ) =>
      new Promise((resolve, reject) => {
        const queue = queues.get(name) ?? [];
        queues.set(name, queue);
        const leave = () => {
          queue.splice(queue.indexOf(grant), 1);
          reject(new DOMException("aborted", "AbortError"));
        };
        function grant() {
          options.signal?.removeEventListener("abort", leave);
          held.add(name);
          void task()
            .then(resolve, reject)
            .finally(() => {
              held.delete(name);
              next(name);
            });
        }
        options.signal?.addEventListener("abort", leave);
        queue.push(grant);
        next(name);
      }),
  };
}
// The engine as it is: a new screen whose name another one has is numbered, and a screen it
// knows keeps the name its listen sends, except that a listen never strips the number it gave
// ("Hall 2" stays when a listen sends "Hall"). A rename sets any name, unless the test has
// renames fail. Each listen holds for its wait, then answers with the name stored by then.
function fakeEngine(taken: string) {
  const names = new Map([["otherScreen0000000000001", taken]]);
  const listens: { name: string; hold: number; signal?: AbortSignal }[] = [];
  const renames: string[] = [];
  const state = { renaming: true };
  const numbered = (stored: string, sent: string) =>
    /^(.*) ([2-9]|[1-9][0-9]+)$/.exec(stored)?.[1].toLowerCase() ===
    sent.toLowerCase();
  const listen = (
    {
      screen: id,
      name,
      hold = 25,
    }: { screen: string; name: string; hold?: number },
    signal?: AbortSignal,
  ) => {
    const stored = names.get(id);
    if (stored === undefined)
      names.set(id, [...names.values()].includes(name) ? `${name} 2` : name);
    else if (!numbered(stored, name)) names.set(id, name);
    listens.push({ name, hold, signal });
    return new Promise<ListenAnswer>((resolve, reject) => {
      const timer = setTimeout(
        () =>
          resolve({
            command: null,
            screen: { id, name: names.get(id)!, settings: {} },
          }),
        hold * 1000,
      );
      signal?.addEventListener("abort", () => {
        clearTimeout(timer);
        reject(new DOMException("aborted", "AbortError"));
      });
    });
  };
  const rename = async (id: string, name: string) => {
    renames.push(name);
    if (!state.renaming) throw new Error("unavailable");
    names.set(id, name);
  };
  const session = {
    initial: { nodes: [], edges: [] },
    screenListen: vi.fn(listen),
    screenDone: vi.fn(async () => {}),
    screenRename: vi.fn(rename),
  } as unknown as GraphSession;
  return { names, listens, renames, state, session };
}
// One tab of this browser running its screen as App does: the browser's identity, a rename made
// in another tab followed, the name an answer says it goes by taken, and a rename here (saved,
// drawn and sent on, the rename's outcome aside).
function Tab({
  session,
  renames,
}: {
  session: GraphSession;
  renames?: { current?: (name: string) => void };
}) {
  const [identity] = useState(screenIdentity);
  const [renamed, setRenamed] = useState<string | null>(null);
  const screen = { id: identity.id, name: renamed ?? identity.name };
  useEffect(() => watchScreenName(identity.id, setRenamed), [identity.id]);
  useEffect(() => {
    if (renames)
      // eslint-disable-next-line react/immutability -- The test harness passes this mutable ref to drive a tab rename from outside React.
      renames.current = (name) => {
        saveScreenName(name, false);
        setRenamed(name);
        void session.screenRename!(identity.id, name).catch(() => {});
      };
  });
  useScreenListen(
    session,
    screen,
    handlers({
      named: (name) => {
        const goesBy = adoptScreenName(screen.id, name);
        if (goesBy !== null) setRenamed(goesBy);
        return goesBy;
      },
    }),
  );
  return null;
}

it("keeps listening under the name the engine keeps, through its number, renames and another tab taking over", async () => {
  vi.useFakeTimers();
  Object.defineProperty(navigator, "locks", {
    configurable: true,
    value: browserLocks(),
  });
  try {
    const { id, name } = screenIdentity();
    // Another screen already has this browser's default name.
    const engine = fakeEngine(name);
    const renames: { current?: (name: string) => void } = {};
    const first = render(<Tab session={engine.session} renames={renames} />);
    render(<Tab session={engine.session} />);
    const waited = () => act(async () => vi.advanceTimersByTime(25_000));
    // The browser tells its other tabs that the screen's identity changed.
    const told = () =>
      act(() => {
        window.dispatchEvent(
          new StorageEvent("storage", {
            key: "rhythm-of-life.screen",
            newValue: localStorage.getItem("rhythm-of-life.screen"),
          }),
        );
      });
    const listening = () => {
      const last = engine.listens.at(-1)!;
      return { name: last.name, waiting: !last.signal!.aborted };
    };
    // One tab listens. The engine numbered the screen; from the next listen on it goes by that
    // number, and the engine keeps it, listen after listen.
    expect(engine.listens).toHaveLength(1);
    await waited();
    told();
    for (let i = 0; i < 3; i++) await waited();
    expect(engine.listens.map((l) => l.name)).toEqual([
      name,
      ...Array(4).fill(`${name} 2`),
    ]);
    expect(engine.names.get(id)).toBe(`${name} 2`);
    expect(engine.renames).toEqual([]);
    // Someone gives it the bare default in Details here, and the rename doesn't reach the
    // engine. The listens carry the name, but a listen never strips the number, so the
    // listening tab sends the rename again, once a minute, until the engine has it.
    engine.state.renaming = false;
    act(() => renames.current!(name));
    told();
    await waited();
    expect(listening()).toEqual({ name, waiting: true });
    expect(engine.names.get(id)).toBe(`${name} 2`);
    expect(engine.renames).toEqual([name, name]);
    engine.state.renaming = true;
    for (let i = 0; i < 2; i++) await waited();
    expect(engine.renames).toHaveLength(2);
    await waited();
    expect(engine.renames).toEqual([name, name, name]);
    expect(engine.names.get(id)).toBe(name);
    for (let i = 0; i < 3; i++) await waited();
    expect(engine.renames).toHaveLength(3);
    expect(engine.names.get(id)).toBe(name);
    expect(listening()).toEqual({ name, waiting: true });
    // Renamed in Details here again, and this time it reaches the engine at once.
    act(() => renames.current!("Porch"));
    told();
    for (let i = 0; i < 2; i++) await waited();
    expect(engine.names.get(id)).toBe("Porch");
    expect(engine.renames).toEqual([name, name, name, "Porch"]);
    expect(listening()).toEqual({ name: "Porch", waiting: true });
    // This tab closes: the other one takes the lock over and listens under the same name.
    const before = engine.listens.length;
    first.unmount();
    await act(async () => {});
    expect(engine.listens).toHaveLength(before + 1);
    expect(engine.listens[before - 1].signal!.aborted).toBe(true);
    await waited();
    expect(engine.names.get(id)).toBe("Porch");
    expect(listening()).toEqual({ name: "Porch", waiting: true });
  } finally {
    Reflect.deleteProperty(navigator, "locks");
    localStorage.clear();
  }
});

it("goes by a name chosen in another tab from the next answer, though it missed the storage event", async () => {
  vi.useFakeTimers();
  try {
    const { id, name } = screenIdentity();
    const engine = fakeEngine("Hall");
    render(<Tab session={engine.session} />);
    const waited = () => act(async () => vi.advanceTimersByTime(25_000));
    await waited();
    expect(engine.names.get(id)).toBe(name);
    // Another tab names the screen Porch and its rename reaches the engine; this one (kept in
    // the back-forward cache meanwhile, say) never heard. From the next answer on its listens
    // carry Porch, and the engine keeps it.
    saveScreenName("Porch", false);
    await engine.session.screenRename!(id, "Porch");
    for (let i = 0; i < 3; i++) await waited();
    expect(engine.listens.slice(-3).map((l) => l.name)).toEqual(
      Array(3).fill("Porch"),
    );
    expect(engine.names.get(id)).toBe("Porch");
    // A listen that left with the old name before then, arriving after the rename, put it
    // back: the next answer has this tab send Porch again as a rename.
    engine.names.set(id, name);
    await waited();
    expect(engine.renames).toEqual(["Porch", "Porch"]);
    expect(engine.names.get(id)).toBe("Porch");
    for (let i = 0; i < 2; i++) await waited();
    expect(engine.names.get(id)).toBe("Porch");
    expect(engine.listens.at(-1)!.name).toBe("Porch");
  } finally {
    localStorage.clear();
  }
});

it("renames a screen the engine numbered as new back to the name a person chose", async () => {
  vi.useFakeTimers();
  try {
    // A screen named Kitchen by a person, which the engine no longer knows (unseen for 180
    // days, say), while another screen is called Kitchen.
    const id = "kitchenScreen0000000001";
    localStorage.setItem(
      "rhythm-of-life.screen",
      JSON.stringify({ id, name: "Kitchen", chosen: true }),
    );
    const engine = fakeEngine("Kitchen");
    render(<Tab session={engine.session} />);
    const waited = () => act(async () => vi.advanceTimersByTime(25_000));
    expect(engine.names.get(id)).toBe("Kitchen 2");
    await waited();
    expect(engine.renames).toEqual(["Kitchen"]);
    expect(engine.names.get(id)).toBe("Kitchen");
    for (let i = 0; i < 3; i++) await waited();
    expect(engine.listens.map((l) => l.name)).toEqual(Array(5).fill("Kitchen"));
    expect(engine.names.get(id)).toBe("Kitchen");
    expect(engine.renames).toHaveLength(1);
  } finally {
    localStorage.clear();
  }
});

it("reports a touch beside the waiting listen, at most once every 10 seconds, never cutting it short", async () => {
  vi.useFakeTimers();
  const { calls, session } = waiting();
  let touched: number | undefined;
  const h = handlers({
    showing: () => ({
      ...showing,
      ...(touched !== undefined ? { activeAgo: touched } : {}),
    }),
  });
  renderHook(() => useScreenListen(session, screen, h));
  expect(calls).toHaveLength(1);
  // A touch soon after the listen was sent reaches the engine 10 seconds after it, in a listen
  // that holds for no time; the waiting one stays, since it may already hold a command.
  act(() => {
    vi.advanceTimersByTime(2000);
    touched = 0;
    window.dispatchEvent(new Event("pointerdown"));
  });
  await act(async () => vi.advanceTimersByTime(touchedMs - 2000 - 50));
  expect(calls).toHaveLength(1);
  await act(async () => vi.advanceTimersByTime(100));
  expect(calls).toHaveLength(2);
  expect(calls[0].signal.aborted).toBe(false);
  expect(calls[1].args).toMatchObject({
    hold: 0,
    showing: { activeAgo: 0 },
  });
  // What it claims is applied and reported like any other command.
  await act(async () => calls[1].answer({ command, screen: self }));
  await vi.waitFor(() => expect(session.screenDone).toHaveBeenCalledTimes(1));
  expect(h.apply).toHaveBeenCalledWith(command);
  // More touches wait out 10 seconds from that report.
  act(() => {
    vi.advanceTimersByTime(1000);
    window.dispatchEvent(new Event("keydown"));
    window.dispatchEvent(new Event("pointermove"));
  });
  await act(async () => vi.advanceTimersByTime(touchedMs - 1000 - 200));
  expect(calls).toHaveLength(2);
  await act(async () => vi.advanceTimersByTime(300));
  expect(calls).toHaveLength(3);
  await act(async () => calls[2].answer({ command: null, screen: self }));
  // With no touch since, nothing more is sent, and the first listen still waits.
  await act(async () => vi.advanceTimersByTime(touchedMs * 2));
  expect(calls).toHaveLength(3);
  expect(calls[0].signal.aborted).toBe(false);
  expect(h.apply).toHaveBeenCalledTimes(1);
});

it("applies commands one at a time, whichever listen claimed them", async () => {
  vi.useFakeTimers();
  const { calls, session } = waiting();
  const finish: (() => void)[] = [];
  const h = handlers({
    apply: vi.fn(
      () =>
        new Promise<{ result: "shown"; detail: Record<string, unknown> }>(
          (resolve) =>
            finish.push(() => resolve({ result: "shown", detail: {} })),
        ),
    ),
  });
  renderHook(() => useScreenListen(session, screen, h));
  act(() => {
    vi.advanceTimersByTime(1000);
    window.dispatchEvent(new Event("pointerdown"));
  });
  await act(async () => vi.advanceTimersByTime(touchedMs));
  expect(calls).toHaveLength(2);
  // The touch's report claims one; the waiting listen another while it is being shown.
  await act(async () => calls[1].answer({ command, screen: self }));
  const second = { ...command, id: "7a2d3c4b-5e6f-4a70-8b8c-9d0e1f2a3b4c" };
  await act(async () => calls[0].answer({ command: second, screen: self }));
  expect(h.apply).toHaveBeenCalledTimes(1);
  await act(async () => finish[0]());
  await vi.waitFor(() => expect(h.apply).toHaveBeenCalledTimes(2));
  expect(h.apply).toHaveBeenLastCalledWith(second);
  await act(async () => finish[1]());
  await vi.waitFor(() => expect(session.screenDone).toHaveBeenCalledTimes(2));
  expect(
    vi.mocked(session.screenDone!).mock.calls.map(([d]) => d.command),
  ).toEqual([command.id, second.id]);
});

it("leaves a command being applied alone when someone touches the screen", async () => {
  vi.useFakeTimers();
  const { calls, session } = waiting();
  let finish: () => void = () => {};
  const h = handlers({
    apply: vi.fn(
      () =>
        new Promise<{ result: "shown"; detail: Record<string, unknown> }>(
          (resolve) =>
            (finish = () => resolve({ result: "shown", detail: {} })),
        ),
    ),
  });
  renderHook(() => useScreenListen(session, screen, h));
  await act(async () => calls[0].answer({ command, screen: self }));
  expect(h.apply).toHaveBeenCalledTimes(1);
  act(() => window.dispatchEvent(new Event("pointerdown")));
  await act(async () => vi.advanceTimersByTime(touchedMs * 2));
  // Its report and the next listen carry what the screen shows anyway.
  expect(calls).toHaveLength(1);
  await act(async () => finish());
  await vi.waitFor(() => expect(calls).toHaveLength(2));
  expect(session.screenDone).toHaveBeenCalledTimes(1);
});
