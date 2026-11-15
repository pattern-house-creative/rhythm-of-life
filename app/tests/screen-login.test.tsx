import { act, render, renderHook } from "@testing-library/react";
import { useState } from "react";
import { afterEach, expect, it, vi } from "vitest";
import { LiveScreen } from "../src/LiveScreen";
import type { ScreenActions } from "../src/LiveScreen";
import { createApiSession } from "../src/apiClient";
import type { GraphSession } from "../src/liveGraph";
import {
  adoptScreenName,
  defaultScreenName,
  defaultTiles,
  saveScreenName,
  screenIdentity,
  takeNewScreenId,
} from "../src/screens";
import type { ListenAnswer, ListenTaken, ScreenCommand } from "../src/screens";
import { listenBackoff, useScreenListen } from "../src/useScreenListen";
import type { ScreenHandlers } from "../src/useScreenListen";
import { allAppTools, withEngine } from "./engine";

// A browser whose login changes takes a new screen id (SB-P1-19's open point; docs/dev/screens.md
// 3.2): with viewer sessions on, a screen's id is the app login's that first listened with it, so
// once a session starts or ends here the engine refuses the listen as another login's. The facade
// answers {taken: true}, the listen loop has the app take a new id, and the screen listens again
// as a new screen with this browser's settings. With viewer sessions off nothing is refused, and
// every answer is as before. Fictional screens only.
vi.mock("../server/viewerGate", () => ({ viewersAccepted: true }));
// The setup file has loaded the server's modules with the real gate already, so the facade's
// test loads them afresh, with the replaced one (as tests/viewer-session.test.ts does).
async function facade(extra = "") {
  vi.resetModules();
  const { handleScreens } = await import("../server/screens");
  const config = await import("../server/config");
  const engine = await import("../server/engine");
  withEngine({ extra });
  config.resetSettings();
  engine.resetEngine(allAppTools);
  return (body: unknown) =>
    handleScreens(
      new Request("http://rhythm.example.invalid/api/screens", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(body),
      }),
    );
}
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
  localStorage.clear();
});
const kitchen = "kitchenScreen0000000001";
const hall = "hallScreen00000000000002";
const json = (value: unknown) =>
  new Response(JSON.stringify(value), {
    headers: { "content-type": "application/json" },
  });
// The engine's refusal of another login's screen: 403 and an isError result naming forbidden.
const forbidden = () =>
  new Response(
    JSON.stringify({
      jsonrpc: "2.0",
      id: "rhythm-app",
      result: {
        structuredContent: {
          error: {
            code: "forbidden",
            message: "Not available to this caller.",
          },
        },
        content: [{ type: "text", text: "Not available to this caller." }],
        isError: true,
      },
    }),
    { status: 403, headers: { "content-type": "application/json" } },
  );
const listen = { op: "listen", screen: kitchen, name: "Kitchen iPad" };

it("answers a listen refused as another login's screen with taken, only while viewer sessions are on", async () => {
  vi.spyOn(console, "warn").mockImplementation(() => {});
  let ask = await facade("app:\n  viewers: on");
  for (const refusal of [forbidden(), new Response("", { status: 403 })]) {
    vi.mocked(fetch).mockResolvedValueOnce(refusal);
    const answer = await ask(listen);
    expect(answer.status).toBe(200);
    expect(await answer.json()).toEqual({ taken: true });
  }
  // The other ops claim nothing for another login's screen and answer as before.
  vi.mocked(fetch).mockResolvedValueOnce(forbidden());
  expect(
    (await ask({ op: "rename", screen: kitchen, name: "Kitchen" })).status,
  ).toBe(502);
  vi.mocked(fetch).mockResolvedValueOnce(forbidden());
  expect(
    await (
      await ask({ op: "send", screen: kitchen, to: hall, action: "home" })
    ).json(),
  ).toEqual({
    ok: false,
    reason: "It wasn't sent: screens are unavailable right now.",
  });
  // Any other failure of a listen is as before.
  vi.mocked(fetch).mockResolvedValueOnce(new Response("", { status: 401 }));
  expect((await ask(listen)).status).toBe(502);
  // Viewer sessions off: no login is refused, and a 403 is the failure it always was.
  ask = await facade();
  vi.mocked(fetch).mockResolvedValueOnce(forbidden());
  const off = await ask(listen);
  expect(off.status).toBe(502);
  expect(await off.json()).toEqual({ error: "Screens unavailable" });
});

it("reads the facade's taken answer in the browser", async () => {
  const session = createApiSession(false);
  vi.mocked(fetch).mockResolvedValueOnce(json({ taken: true }));
  expect(
    await session.screenListen!({ screen: kitchen, name: "Kitchen iPad" }),
  ).toEqual({ taken: true });
});

it("takes a new id for this browser's screen once, keeping a chosen name, until a listen with it is answered", () => {
  const first = screenIdentity();
  saveScreenName("Hall", false);
  // A refusal of an id this browser no longer listens as: another tab took a new one already.
  expect(takeNewScreenId(kitchen)).toEqual({
    ...first,
    name: "Hall",
    chosen: true,
  });
  const next = takeNewScreenId(first.id)!;
  expect(next.id).not.toBe(first.id);
  expect(next).toMatchObject({ name: "Hall", chosen: true });
  expect(screenIdentity()).toEqual(next);
  // Its first listen refused too: not another login's, so no other id is taken for it.
  expect(takeNewScreenId(next.id)).toBeNull();
  expect(screenIdentity().id).toBe(next.id);
  // An answer for it makes it this login's; a later login change takes another.
  expect(adoptScreenName(next.id, "Hall 2")).toBe("Hall");
  expect(JSON.parse(localStorage.getItem("rhythm-of-life.screen")!)).toEqual(
    next,
  );
  const third = takeNewScreenId(next.id)!;
  expect(third.id).not.toBe(next.id);
  // A default name, numbered by the engine for the old screen, starts again from the device's.
  localStorage.clear();
  const plain = screenIdentity();
  adoptScreenName(plain.id, `${defaultScreenName()} 2`);
  expect(screenIdentity().name).toBe(`${defaultScreenName()} 2`);
  expect(takeNewScreenId(plain.id)).toMatchObject({
    name: defaultScreenName(),
    chosen: false,
  });
});

// The listen loop against a waiting engine (as tests/screen-listen.test.tsx has it).
function waiting() {
  const calls: {
    args: Parameters<NonNullable<GraphSession["screenListen"]>>[0];
    answer: (value: ListenAnswer | ListenTaken | null) => void;
  }[] = [];
  const session = {
    initial: { nodes: [], edges: [] },
    screenListen: vi.fn(
      (
        args: Parameters<NonNullable<GraphSession["screenListen"]>>[0],
        signal?: AbortSignal,
      ) =>
        new Promise<ListenAnswer | ListenTaken | null>((resolve, reject) => {
          calls.push({ args, answer: resolve });
          signal?.addEventListener("abort", () =>
            reject(new DOMException("aborted", "AbortError")),
          );
        }),
    ),
    screenDone: vi.fn(async () => {}),
    screenRename: vi.fn(async () => {}),
  } as unknown as GraphSession;
  return { calls, session };
}
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
    apply: vi.fn(async () => ({ result: "shown" as const, detail: {} })),
    ...over,
  };
}

it("stops listening as a screen another login took once the app takes a new id, and listens as that", async () => {
  const { calls, session } = waiting();
  const taken = vi.fn(() => true);
  const { rerender } = renderHook(
    ({ id }) =>
      useScreenListen(session, { id, name: "Hall" }, handlers({ taken })),
    { initialProps: { id: hall } },
  );
  await act(async () => calls[0].answer({ taken: true }));
  expect(taken).toHaveBeenCalledWith(hall);
  // Nothing more as the old id, however long it waits.
  await act(async () => new Promise((r) => setTimeout(r, 50)));
  expect(calls).toHaveLength(1);
  rerender({ id: kitchen });
  expect(calls).toHaveLength(2);
  expect(calls[1].args.screen).toBe(kitchen);
});

it("backs off as after any failure when no new id is taken", async () => {
  vi.useFakeTimers();
  const { calls, session } = waiting();
  const taken = vi.fn(() => false);
  renderHook(() =>
    useScreenListen(session, { id: hall, name: "Hall" }, handlers({ taken })),
  );
  await act(async () => calls[0].answer({ taken: true }));
  expect(taken).toHaveBeenCalledWith(hall);
  await act(async () => vi.advanceTimersByTime(listenBackoff[0] - 1));
  expect(calls).toHaveLength(1);
  await act(async () => vi.advanceTimersByTime(1));
  expect(calls).toHaveLength(2);
  expect(calls[1].args.screen).toBe(hall);
  // A screen without the handler backs off the same way.
  const other = waiting();
  renderHook(() =>
    useScreenListen(
      other.session,
      { id: kitchen, name: "Kitchen" },
      handlers(),
    ),
  );
  await act(async () => other.calls[0].answer({ taken: true }));
  await act(async () => vi.advanceTimersByTime(listenBackoff[0]));
  expect(other.calls).toHaveLength(2);
});

// The runner: a new id is a new screen at the engine, so its first listen carries this browser's
// settings again, and what was done as the old screen is not undone as the new one.
const command = (
  action: string,
  args: Record<string, unknown> = {},
): ScreenCommand => ({
  id: "6f1c2b3a-4d5e-4f60-8a7b-9c0d1e2f3a4b",
  action,
  args,
  sender: { kind: "assistant" },
  sentAt: "2026-10-31T09:00:00Z",
  deliveredAt: "2026-10-31T09:00:00.2Z",
  doneAt: null,
  result: "delivered",
  detail: {},
  ms: { deliver: 200, show: null },
});
function actions(): ScreenActions {
  return {
    showing: () => showing,
    view: () => ({ view: "home" }),
    starTitle: () => null,
    dashboardTitle: () => "Fictional plan",
    savedTitle: () => "Fictional plan",
    fetchStar: vi.fn(async () => {}),
    fetchDashboard: vi.fn(async () => {}),
    reread: vi.fn(async () => {}),
    openStar: vi.fn(),
    openDashboard: vi.fn(),
    search: vi.fn(() => ({ matches: 0, titles: [] })),
    fullScreen: vi.fn(),
    exitFullScreen: vi.fn(),
    home: vi.fn(),
    restore: vi.fn(),
    homeDashboard: null,
    setHome: vi.fn(),
    layout: "band",
    setLayout: vi.fn(),
    tiles: defaultTiles,
    setTiles: vi.fn(),
    narrow: false,
    cards: {},
    setCard: vi.fn(async () => {}),
  };
}
// As App holds it: this browser's identity, and a new one when the engine refuses it.
function Screen({ session, a }: { session: GraphSession; a: ScreenActions }) {
  const [screen, setScreen] = useState(() => screenIdentity());
  return (
    <LiveScreen
      session={session}
      screen={screen}
      stored
      actions={a}
      taken={(refused) => {
        const next = takeNewScreenId(refused);
        if (next) setScreen(next);
        return next !== null;
      }}
    />
  );
}

it("listens as a new screen with this browser's settings once its login changed", async () => {
  saveScreenName("Hall", false);
  const before = screenIdentity();
  const { calls, session } = waiting();
  const a = actions();
  render(<Screen session={session} a={a} />);
  const settings = { layout: "band", tiles: defaultTiles, home: null };
  expect(calls[0].args).toMatchObject({
    screen: before.id,
    name: "Hall",
    settings,
  });
  const self = { id: before.id, name: "Hall", settings };
  await act(async () =>
    calls[0].answer({
      command: command("corner_card", {
        corner: "main",
        dashboard: "0f8e7d6c-5b4a-4392-8170-6f5e4d3c2b1a",
      }),
      screen: self,
    }),
  );
  await vi.waitFor(() => expect(calls).toHaveLength(2));
  // Sent once already, so the next listen as the same screen carries none.
  expect(calls[1].args.settings).toBeUndefined();
  // A session started (or ended) here: the engine refuses this screen as another login's.
  await act(async () => calls[1].answer({ taken: true }));
  await vi.waitFor(() => expect(calls).toHaveLength(3));
  const after = screenIdentity();
  expect(after.id).not.toBe(before.id);
  expect(calls[2].args).toMatchObject({
    screen: after.id,
    name: "Hall",
    settings,
  });
  // The corner the old screen chose is not undone as the new one.
  await act(async () =>
    calls[2].answer({
      command: command("undo"),
      screen: { ...self, id: after.id },
    }),
  );
  await vi.waitFor(() => expect(session.screenDone).toHaveBeenCalledTimes(2));
  expect(vi.mocked(session.screenDone!).mock.calls[1][0]).toMatchObject({
    screen: after.id,
    result: "failed",
    detail: { summary: "Nothing to undo" },
  });
  expect(a.setCard).toHaveBeenCalledTimes(1);
});
