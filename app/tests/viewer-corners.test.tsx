import { act, render } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { LiveScreen } from "../src/LiveScreen";
import type { ScreenActions } from "../src/LiveScreen";
import { cleanCards, followHousehold } from "../src/dashboards";
import type { DashboardCards } from "../src/dashboards";
import type { GraphSession } from "../src/liveGraph";
import { defaultTiles } from "../src/screens";
import type { ListenAnswer, ScreenCommand } from "../src/screens";
import { POST } from "../api/dashboards";
import { handleDashboards } from "../server/dashboards";
import { withEngine } from "./engine";

// A viewer session's own corner choices (SB-P1-20's open point, migration 032; docs/dev/screens.md
// 6, Undo): its cards say which corners are its person's own, and a screen's Undo of corner_card
// on a corner that showed the household's drops the choice it made (the nil uuid) instead of
// keeping the household's card as the session's own. Without `own`, every answer and every Undo
// is as before. Fictional dashboards only.
afterEach(() => {
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
});
const theirs = "0f8e7d6c-5b4a-4392-8170-6f5e4d3c2b1a";
const mine = "1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d";
const plan = "6a5b4c3d-2e1f-4a0b-9c8d-7e6f5a4b3c2d";
const rpc = (payload: unknown) =>
  new Response(
    JSON.stringify({
      jsonrpc: "2.0",
      id: "rhythm-app",
      result: { structuredContent: payload },
    }),
    { headers: { "content-type": "application/json" } },
  );
const sent = () =>
  JSON.parse(String(vi.mocked(fetch).mock.calls.at(-1)![1]?.body)).params;

it("keeps which corners are the reader's own when the engine says, and nothing else", () => {
  expect(followHousehold).toBe("00000000-0000-0000-0000-000000000000");
  expect(
    cleanCards({
      cards: {
        main: theirs.toUpperCase(),
        training: mine,
        own: ["training", "ceiling", "main", 7, "training"],
      },
    }),
  ).toEqual({ main: theirs, training: mine, own: ["main", "training"] });
  expect(cleanCards({ cards: { main: theirs, own: [] } })).toEqual({
    main: theirs,
    own: [],
  });
  // Without it (the household view, scopes off, an engine before 032), or malformed: no `own`.
  for (const own of [undefined, "main", { main: true }, null])
    expect(cleanCards({ cards: { main: theirs, own } })).toEqual({
      main: theirs,
    });
});

it("passes a session's own corners on and sends its drop to the engine", async () => {
  withEngine();
  const change = (body: unknown) =>
    POST(
      new Request("http://rhythm.example.invalid/api/dashboards", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(body),
      }),
    );
  vi.mocked(fetch).mockResolvedValueOnce(
    rpc({ cards: { main: theirs, own: ["training"] } }),
  );
  expect(
    await (
      await handleDashboards(
        new Request("http://rhythm.example.invalid/api/dashboards?cards=1"),
      )
    ).json(),
  ).toEqual({ cards: { main: theirs, own: ["training"] } });
  vi.mocked(fetch).mockResolvedValueOnce(
    rpc({ cards: { main: theirs, own: [] } }),
  );
  expect(
    await (
      await change({
        action: "card",
        corner: "training",
        dashboard: followHousehold,
      })
    ).json(),
  ).toEqual({ cards: { main: theirs, own: [] } });
  expect(sent()).toEqual({
    name: "app_dashboards",
    arguments: { corner: "training", dashboard: followHousehold },
  });
});

// The runner against a waiting engine, as tests/live-screen.test.tsx has it.
const me = { id: "hallScreen00000000000002", name: "Hall" };
function engine() {
  const calls: { answer: (value: ListenAnswer | null) => void }[] = [];
  const session = {
    initial: { nodes: [], edges: [] },
    screenListen: vi.fn(
      (_args: unknown, signal?: AbortSignal) =>
        new Promise<ListenAnswer | null>((resolve, reject) => {
          calls.push({ answer: resolve });
          signal?.addEventListener("abort", () => reject(new Error("aborted")));
        }),
    ),
    screenDone: vi.fn(async () => {}),
  } as unknown as GraphSession;
  return { calls, session };
}
function actions(cards: DashboardCards): ScreenActions {
  return {
    showing: () => ({
      view: "home",
      layout: "hero",
      tiles: defaultTiles,
      narrow: false,
    }),
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
    layout: "hero",
    setLayout: vi.fn(),
    tiles: defaultTiles,
    setTiles: vi.fn(),
    narrow: false,
    cards,
    setCard: vi.fn(async () => {}),
  };
}
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
async function runner(cards: DashboardCards) {
  const { calls, session } = engine();
  const a = actions(cards);
  render(<LiveScreen session={session} screen={me} stored actions={a} />);
  const step = async (c: ScreenCommand) => {
    const n = calls.length;
    await act(async () =>
      calls[n - 1].answer({
        command: c,
        screen: { id: me.id, name: me.name, settings: {} },
      }),
    );
    await vi.waitFor(() => expect(calls).toHaveLength(n + 1));
    return vi.mocked(session.screenDone!).mock.calls.at(-1)![0];
  };
  return { a, step };
}

it("drops a session's choice on Undo where its corner showed the household's, and puts its own back", async () => {
  // Main shows the household's card, Training the session's own, Recovery the session's own stat card.
  const { a, step } = await runner({
    main: theirs,
    training: mine,
    own: ["recovery", "training"],
  });
  for (const [corner, before] of [
    ["main", followHousehold],
    ["training", mine],
    ["recovery", null],
  ] as const) {
    expect(
      await step(command("corner_card", { corner, dashboard: plan })),
    ).toMatchObject({ result: "shown" });
    expect(a.setCard).toHaveBeenLastCalledWith(corner, plan);
    expect(await step(command("undo"))).toMatchObject({ result: "shown" });
    expect(a.setCard).toHaveBeenLastCalledWith(corner, before);
  }
});

it("puts the card back on Undo as before when the cards say nothing of their own", async () => {
  // The household view, or memory.scopes off: every corner is the reader's own.
  const { a, step } = await runner({ main: theirs });
  await step(command("corner_card", { corner: "main", dashboard: plan }));
  await step(command("undo"));
  expect(a.setCard).toHaveBeenLastCalledWith("main", theirs);
  await step(command("corner_card", { corner: "training", dashboard: plan }));
  await step(command("undo"));
  expect(a.setCard).toHaveBeenLastCalledWith("training", null);
  expect(vi.mocked(a.setCard).mock.calls.flat()).not.toContain(followHousehold);
});
