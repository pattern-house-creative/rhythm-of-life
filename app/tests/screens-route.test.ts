import { request as httpRequest } from "node:http";
import type { AddressInfo } from "node:net";
import { readFileSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { afterEach, expect, it, vi } from "vitest";
import { handleScreens, refusedBecause } from "../server/screens";
import { callTool, requestId, resetEngine } from "../server/engine";
import { createAppServer } from "../server/node";
import { POST } from "../api/screens";
import { cleanName, commandArgs } from "../src/screens";
import { allAppTools, called, closedEngines, rpc, withEngine } from "./engine";

// POST /api/screens: one app_screens call per request, with exactly the keys its op takes
// (docs/dev/screens.md 3.4 and 4). Fictional screens and commands only.
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
});
const base = "http://rhythm.example.invalid/api/screens";
const ask = (body: unknown, init: RequestInit = {}) =>
  handleScreens(
    new Request(base, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(body),
      ...init,
    }),
  );
const kitchen = "kitchenScreen0000000001";
const hall = "hallScreen00000000000002";
const commandId = "6f1c2b3a-4d5e-4f60-8a7b-9c0d1e2f3a4b";
const command = {
  id: commandId,
  action: "open_star",
  args: { star: "rhythm:memory:fictional" },
  sender: { kind: "assistant" },
  sentAt: "2026-10-31T09:00:00Z",
  deliveredAt: "2026-10-31T09:00:00.4Z",
  doneAt: null,
  result: "delivered",
  detail: {},
  ms: { deliver: 400, show: null },
};
const self = { id: kitchen, name: "Kitchen iPad", settings: {} };
const tiles = {
  main: "main",
  recovery: "recovery",
  training: "training",
  changed: "changed",
};
const home = { view: "home", layout: "hero", tiles, narrow: false };
const starShown = {
  view: "star",
  star: { id: "rhythm:memory:fictional", title: "Training plan" },
  layout: "hero",
  tiles,
  narrow: false,
};

it("sends each op's exact keys to app_screens and answers what the engine said", async () => {
  withEngine();
  const answers: [unknown, unknown, unknown][] = [
    [
      {
        op: "listen",
        screen: kitchen,
        name: " Kitchen iPad ",
        hold: 25,
        showing: home,
        settings: { layout: "band", tiles },
      },
      { command, screen: self },
      { command, screen: self },
    ],
    [
      { op: "listen", screen: kitchen, name: "Kitchen iPad" },
      { command: null, screen: self },
      { command: null, screen: self },
    ],
    [
      {
        op: "done",
        screen: kitchen,
        command: commandId.toUpperCase(),
        result: "shown",
        detail: { summary: "Opened Training plan" },
        showing: starShown,
      },
      { ok: true },
      { ok: true },
    ],
    [
      { op: "list", screen: kitchen },
      {
        screens: [
          {
            id: hall,
            name: "Hall",
            open: true,
            seenAt: "2026-10-31T09:00:00Z",
            showing: { view: "home" },
            self: false,
          },
        ],
      },
      {
        screens: [
          {
            id: hall,
            name: "Hall",
            open: true,
            seenAt: "2026-10-31T09:00:00Z",
            showing: { view: "home" },
            self: false,
          },
        ],
      },
    ],
    [
      { op: "rename", screen: kitchen, name: "Kitchen" },
      { screen: { ...self, name: "Kitchen" } },
      { screen: { ...self, name: "Kitchen" } },
    ],
  ];
  for (const [body, engine, expected] of answers) {
    vi.mocked(fetch).mockResolvedValueOnce(rpc(engine));
    const response = await ask(body);
    expect(response.status).toBe(200);
    expect(await response.json()).toEqual(expected);
  }
  const sent = vi.mocked(fetch).mock.calls.map(([, init]) => called(init));
  expect(sent.map((c) => c.name)).toEqual(
    Array(answers.length).fill("app_screens"),
  );
  expect(sent.map((c) => c.arguments)).toEqual([
    {
      op: "listen",
      screen: kitchen,
      name: "Kitchen iPad",
      hold: 25,
      showing: home,
      settings: { layout: "band", tiles },
    },
    { op: "listen", screen: kitchen, name: "Kitchen iPad" },
    {
      op: "done",
      screen: kitchen,
      command: commandId,
      result: "shown",
      detail: { summary: "Opened Training plan" },
      showing: starShown,
    },
    { op: "list", screen: kitchen },
    { op: "rename", screen: kitchen, name: "Kitchen" },
  ]);
});

it("passes on only what a screen shows and says, cut to fit", async () => {
  withEngine();
  vi.mocked(fetch).mockResolvedValue(rpc({ ok: true }));
  const long = "Á".repeat(150) + "😀".repeat(100);
  expect(
    (
      await ask({
        op: "done",
        screen: kitchen,
        command: commandId,
        result: "shown",
        detail: {
          summary: `Opened ${long}`,
          matches: 12,
          titles: Array(12).fill("Walk\nlog"),
          private: "never kept",
        },
        showing: {
          ...starShown,
          star: { id: "rhythm:memory:fictional", title: long, extra: 1 },
          query: `  ${"q".repeat(120)}`,
          private: "never kept",
        },
      })
    ).status,
  ).toBe(200);
  const sent = called(vi.mocked(fetch).mock.calls[0][1]).arguments as {
    detail: { summary: string; titles: string[] };
    showing: Record<string, unknown>;
  };
  // Cut by characters, so the last emoji stays whole.
  expect(Array.from(sent.detail.summary)).toHaveLength(200);
  expect(sent.detail.summary.endsWith("😀")).toBe(true);
  expect(sent.detail).toEqual({
    summary: sent.detail.summary,
    matches: 12,
    titles: Array(10).fill("Walk log"),
  });
  expect(sent.showing).toEqual({
    ...starShown,
    star: {
      id: "rhythm:memory:fictional",
      title: "Á".repeat(150) + "😀".repeat(50),
    },
    query: "q".repeat(100),
  });
});

it("passes on a screen's Home, its full screen and when it was last touched", async () => {
  withEngine();
  vi.mocked(fetch).mockImplementation(async () =>
    rpc({ command: null, screen: self }),
  );
  const plan = "0f8e7d6c-5b4a-4392-8170-6f5e4d3c2b1a";
  const fullScreen = {
    view: "full_screen",
    dashboard: { id: plan, title: "Training plan" },
    layout: "hero",
    tiles,
    narrow: false,
    home: {
      dashboard: { id: plan.toUpperCase(), title: "Training plan", extra: 1 },
      missing: true,
      extra: 1,
    },
    // Touched a minute and a half ago; a time by this browser's own clock is left out.
    activeAgo: 90_000,
    activeAt: "2026-10-31T09:00:00.000Z",
  };
  const before = Date.now();
  for (const settings of [
    { layout: "band", tiles, home: { dashboard: plan.toUpperCase() } },
    { layout: "band", tiles, home: null },
    // A screen from before the Home choice.
    { layout: "band", tiles },
  ])
    expect(
      (
        await ask({
          op: "listen",
          screen: kitchen,
          name: "Kitchen iPad",
          showing: fullScreen,
          settings,
        })
      ).status,
    ).toBe(200);
  const sent = vi
    .mocked(fetch)
    .mock.calls.map(([, init]) => called(init).arguments) as {
    showing: unknown;
    settings: unknown;
  }[];
  expect(sent.map((a) => a.settings)).toEqual([
    { layout: "band", tiles, home: { dashboard: plan } },
    { layout: "band", tiles, home: null },
    { layout: "band", tiles },
  ]);
  const { activeAgo, ...shown } = fullScreen;
  const at = Date.parse((sent[0].showing as { activeAt: string }).activeAt);
  expect(sent[0].showing).toEqual({
    ...shown,
    home: { dashboard: { id: plan, title: "Training plan" }, missing: true },
    activeAt: new Date(at).toISOString(),
  });
  // When it was touched, by the server's clock.
  expect(at).toBeGreaterThanOrEqual(before - activeAgo);
  expect(at).toBeLessThanOrEqual(Date.now() - activeAgo);
  // A Home it doesn't report, or the default Home, go on as they came.
  vi.mocked(fetch).mockClear();
  await ask({ op: "listen", screen: kitchen, name: "Hall", showing: home });
  await ask({
    op: "listen",
    screen: kitchen,
    name: "Hall",
    showing: { ...home, home: null },
  });
  expect(
    vi
      .mocked(fetch)
      .mock.calls.map(
        ([, init]) => (called(init).arguments as (typeof sent)[0]).showing,
      ),
  ).toEqual([home, { ...home, home: null }]);
});

it("sends each action with only its own arguments", async () => {
  withEngine();
  const dashboard = "0f8e7d6c-5b4a-4392-8170-6f5e4d3c2b1a";
  const cases: [Record<string, unknown>, Record<string, unknown>][] = [
    [{ action: "open_star", star: "rhythm:memory:fictional" }, {}],
    [
      { action: "open_dashboard", dashboard: dashboard.toUpperCase() },
      { dashboard },
    ],
    [{ action: "home" }, {}],
    [{ action: "layout", layout: "corners" }, {}],
    [{ action: "corner_card", corner: "training", dashboard }, {}],
    [{ action: "corner_card", corner: "training" }, { dashboard: null }],
    [{ action: "search", query: "  sleep  " }, { query: "sleep" }],
    [{ action: "undo" }, {}],
    [
      { action: "full_screen", dashboard: dashboard.toUpperCase() },
      { dashboard },
    ],
    [{ action: "full_screen" }, {}],
    [{ action: "set_home", dashboard }, {}],
    [{ action: "set_home", dashboard: null }, {}],
    [{ action: "set_home" }, { dashboard: null }],
  ];
  for (const [{ action, ...args }, changed] of cases)
    // Beside send's own keys, or as one args object; the engine gets args.
    for (const body of [args, { args }]) {
      vi.mocked(fetch).mockResolvedValueOnce(rpc({ command }));
      const response = await ask({
        op: "send",
        screen: kitchen,
        to: hall,
        action,
        ...body,
      });
      expect(await response.json()).toEqual({ command });
      expect(called(vi.mocked(fetch).mock.calls.at(-1)![1]).arguments).toEqual({
        op: "send",
        screen: kitchen,
        to: hall,
        action,
        args: { ...args, ...changed },
      });
    }
  // move_tile's own `to` is a place, so its arguments come as args.
  vi.mocked(fetch).mockResolvedValueOnce(rpc({ command }));
  const moved = {
    op: "send",
    screen: kitchen,
    to: hall,
    action: "move_tile",
    args: { tile: "main", to: "changed" },
  };
  expect(await (await ask(moved)).json()).toEqual({ command });
  expect(called(vi.mocked(fetch).mock.calls.at(-1)![1]).arguments).toEqual(
    moved,
  );
});

it("counts names and searches in characters, as the engine does", () => {
  const wave = "\u{1F30A}";
  expect(cleanName(` ${wave.repeat(60)} `)).toBe(wave.repeat(60));
  expect(cleanName(wave.repeat(61))).toBeNull();
  expect(commandArgs("search", { query: wave.repeat(100) })).toEqual({
    query: wave.repeat(100),
  });
  expect(commandArgs("search", { query: wave.repeat(101) })).toBeNull();
});

it("refuses any other shape before calling the engine", async () => {
  withEngine();
  const bad: unknown[] = [
    null,
    [],
    {},
    { op: "forget", screen: kitchen },
    { op: "constructor", screen: kitchen },
    { op: "__proto__", screen: kitchen },
    { op: "list" },
    { op: "list", screen: "short" },
    { op: "list", screen: kitchen, extra: 1 },
    { op: "listen", screen: kitchen },
    { op: "listen", screen: kitchen, name: "" },
    { op: "listen", screen: kitchen, name: "x".repeat(61) },
    { op: "listen", screen: kitchen, name: "Hall", hold: 26 },
    { op: "listen", screen: kitchen, name: "Hall", hold: 2.5 },
    { op: "listen", screen: kitchen, name: "Hall", showing: [] },
    {
      op: "listen",
      screen: kitchen,
      name: "Hall",
      showing: { view: "x".repeat(4000) },
    },
    { op: "listen", screen: kitchen, name: "Hall", settings: "band" },
    {
      op: "listen",
      screen: kitchen,
      name: "Hall",
      settings: { layout: "band" },
    },
    {
      op: "listen",
      screen: kitchen,
      name: "Hall",
      settings: { layout: "band", tiles, theme: "dark" },
    },
    { op: "listen", screen: kitchen, name: "Hall", showing: { view: "home" } },
    {
      op: "listen",
      screen: kitchen,
      name: "Hall",
      showing: { ...home, view: "elsewhere" },
    },
    {
      op: "done",
      screen: kitchen,
      command: commandId,
      result: "shown",
      detail: "Opened",
    },
    { op: "listen", screen: kitchen, name: "Hall", command: commandId },
    { op: "done", screen: kitchen, command: commandId },
    { op: "done", screen: kitchen, command: "nope", result: "shown" },
    { op: "done", screen: kitchen, command: commandId, result: "expired" },
    { op: "done", screen: kitchen, command: commandId, result: "shown", x: 1 },
    { op: "send", screen: kitchen, to: hall },
    { op: "send", screen: kitchen, action: "home" },
    { op: "send", screen: kitchen, to: "short", action: "home" },
    { op: "send", screen: kitchen, to: hall, action: "dance" },
    { op: "send", screen: kitchen, to: hall, action: "home", star: "x" },
    { op: "send", screen: kitchen, to: hall, action: "open_star" },
    { op: "send", screen: kitchen, to: hall, action: "open_star", star: "" },
    {
      op: "send",
      screen: kitchen,
      to: hall,
      action: "open_dashboard",
      dashboard: "nope",
    },
    { op: "send", screen: kitchen, to: hall, action: "layout", layout: "grid" },
    // send's own `to` is the target screen, so a tile's place cannot ride beside it.
    {
      op: "send",
      screen: kitchen,
      to: hall,
      action: "move_tile",
      tile: "main",
    },
    { op: "send", screen: kitchen, to: hall, action: "home", args: [] },
    {
      op: "send",
      screen: kitchen,
      to: hall,
      action: "layout",
      args: { layout: "band" },
      layout: "band",
    },
    {
      op: "send",
      screen: kitchen,
      to: hall,
      action: "move_tile",
      args: { tile: "main" },
    },
    {
      op: "send",
      screen: kitchen,
      to: hall,
      action: "corner_card",
      corner: "sky",
    },
    { op: "send", screen: kitchen, to: hall, action: "search", query: " " },
    {
      op: "send",
      screen: kitchen,
      to: hall,
      action: "search",
      query: "x".repeat(101),
    },
    { op: "send", screen: kitchen, to: hall, action: "search", query: "a\nb" },
    {
      op: "send",
      screen: kitchen,
      to: hall,
      action: "full_screen",
      dashboard: "nope",
    },
    {
      op: "send",
      screen: kitchen,
      to: hall,
      action: "full_screen",
      star: "rhythm:memory:fictional",
    },
    // The engine refuses a null dashboard for full screen, as for open_dashboard.
    {
      op: "send",
      screen: kitchen,
      to: hall,
      action: "full_screen",
      dashboard: null,
    },
    {
      op: "send",
      screen: kitchen,
      to: hall,
      action: "set_home",
      dashboard: "nope",
    },
    {
      op: "listen",
      screen: kitchen,
      name: "Hall",
      settings: { layout: "band", tiles, home: { dashboard: "nope" } },
    },
    {
      op: "listen",
      screen: kitchen,
      name: "Hall",
      settings: { layout: "band", tiles, home: "training" },
    },
    {
      op: "listen",
      screen: kitchen,
      name: "Hall",
      showing: { ...home, home: { dashboard: { id: "nope", title: "x" } } },
    },
    ...[-1, 1.5, "a minute", null].map((activeAgo) => ({
      op: "listen",
      screen: kitchen,
      name: "Hall",
      showing: { ...home, activeAgo },
    })),
    { op: "rename", screen: kitchen, name: "a\u0007b" },
  ];
  for (const body of bad) expect((await ask(body)).status).toBe(400);
  expect((await ask("{", { body: "{" })).status).toBe(400);
  expect(
    (await handleScreens(new Request(base, { method: "GET" }))).status,
  ).toBe(405);
  expect(
    (
      await ask(
        { op: "list", screen: kitchen },
        {
          headers: {
            "content-type": "application/json",
            origin: "https://elsewhere.example.invalid",
          },
        },
      )
    ).status,
  ).toBe(403);
  expect(fetch).not.toHaveBeenCalled();
});

it("answers available: false in demo mode, and never calls an engine it may not call", async () => {
  vi.spyOn(console, "warn").mockImplementation(() => {});
  for (const [name, setup] of closedEngines) {
    setup();
    // An expired credential is an install with an engine it may no longer call: unavailable.
    const expired = name === "an expired credential";
    for (const body of [
      { op: "listen", screen: kitchen, name: "Kitchen iPad" },
      { op: "list", screen: kitchen },
      { op: "send", screen: kitchen, to: hall, action: "home" },
    ]) {
      const response = await POST(
        new Request(base, {
          method: "POST",
          headers: { "content-type": "application/json" },
          body: JSON.stringify(body),
        }),
      );
      if (!expired) {
        expect(response.status).toBe(200);
        expect(await response.json()).toEqual({ available: false });
      } else
        expect(await response.json()).toEqual(
          body.op === "send"
            ? {
                ok: false,
                reason: "It wasn't sent: screens are unavailable right now.",
              }
            : { error: "Screens unavailable" },
        );
    }
  }
  expect(fetch).not.toHaveBeenCalled();
});

it("answers available: false when the engine doesn't list app_screens", async () => {
  // An engine older than this app: the browser stops listening instead of retrying.
  withEngine({ tools: allAppTools.filter((t) => t !== "app_screens") });
  for (const body of [
    { op: "listen", screen: kitchen, name: "Kitchen iPad" },
    { op: "list", screen: kitchen },
    { op: "send", screen: kitchen, to: hall, action: "home" },
    { op: "rename", screen: kitchen, name: "Kitchen" },
  ]) {
    const response = await ask(body);
    expect(response.status).toBe(200);
    expect(await response.json()).toEqual({ available: false });
  }
  expect(fetch).not.toHaveBeenCalled();
});

it("fails as unavailable, never as lost access, and says whether a send may have happened", async () => {
  withEngine();
  vi.spyOn(console, "warn").mockImplementation(() => {});
  const listen = { op: "listen", screen: kitchen, name: "Kitchen iPad" };
  for (const answer of [
    new Response("", { status: 401 }),
    new Response("", { status: 403 }),
    new Response("sign in", { headers: { "content-type": "text/html" } }),
    rpc({ command: "open_star", screen: self }),
    rpc({ command: null }),
  ]) {
    vi.mocked(fetch).mockResolvedValueOnce(answer);
    const response = await ask(listen);
    expect(response.status).toBe(502);
    expect(await response.json()).toEqual({ error: "Screens unavailable" });
  }
  const send = { op: "send", screen: kitchen, to: hall, action: "home" };
  vi.mocked(fetch).mockResolvedValueOnce(
    rpc({ error: { code: "invalid_arguments", message: "to" } }, true),
  );
  expect(await (await ask(send)).json()).toEqual({
    ok: false,
    reason:
      "It wasn't sent: that screen isn't open now, or it can't show this.",
  });
  vi.mocked(fetch).mockRejectedValueOnce(new TypeError("network"));
  expect(await (await ask(send)).json()).toEqual({
    ok: false,
    reason:
      "It may have been sent: the answer didn't arrive. Check that screen before sending again.",
  });
  // Once only: a send repeated would be a second command.
  expect(fetch).toHaveBeenCalledTimes(7);
});

it("says why the engine refused a send, in the app's words, and nothing else it said", async () => {
  withEngine();
  vi.spyOn(console, "warn").mockImplementation(() => {});
  // The engine's refusal: its HTTP status and an isError result naming the code and why.
  const refusal = (status: number, code: string, message: string) =>
    new Response(
      JSON.stringify({
        jsonrpc: "2.0",
        id: requestId,
        result: {
          structuredContent: { error: { code, message } },
          content: [{ type: "text", text: message }],
          isError: true,
        },
      }),
      { status, headers: { "content-type": "application/json" } },
    );
  const send = { op: "send", screen: kitchen, to: hall, action: "home" };
  for (const [answer, reason] of [
    [
      refusal(
        400,
        "invalid_arguments",
        "That screen already has 20 commands waiting.",
      ),
      "It wasn't sent: that screen already has 20 commands waiting.",
    ],
    [
      refusal(404, "not_found", "No open screen has that id."),
      "It wasn't sent: that screen isn't open now.",
    ],
    [
      refusal(404, "not_found", "That star is not on the map now."),
      "It wasn't sent: that star isn't on the map now.",
    ],
    [
      rpc(
        { error: { code: "not_found", message: "Unknown dashboard." } },
        true,
      ),
      "It wasn't sent: that dashboard doesn't exist now.",
    ],
    // Words the app doesn't know, or an answer too long to be a refusal, read as the general one.
    [
      refusal(404, "not_found", "Fictional words from the engine."),
      "It wasn't sent: that screen isn't open now, or it can't show this.",
    ],
    [
      refusal(400, "invalid_arguments", "x".repeat(3000)),
      "It wasn't sent: that screen isn't open now, or it can't show this.",
    ],
    [
      refusal(503, "unavailable", "No open screen has that id."),
      "It may have been sent: the answer didn't arrive. Check that screen before sending again.",
    ],
  ] as const) {
    vi.mocked(fetch).mockResolvedValueOnce(answer);
    expect(await (await ask(send)).json()).toEqual({ ok: false, reason });
  }
});

it("knows the engine's refusals word for word", () => {
  // Each is a reason migration 020 raises (the engine adds a full stop) or the endpoint gives.
  const engine = [
    "../engine/migrations/020_screens.sql",
    "../engine/src/Core/Mcp/AppEndpoint.cs",
  ]
    .map((file) => readFileSync(file, "utf8"))
    .join(" ");
  for (const said of Object.keys(refusedBecause))
    expect(
      engine.includes(`RAISE EXCEPTION '${said.slice(0, -1)}'`) ||
        engine.includes(`"${said}"`),
    ).toBe(true);
});

it("lets a listen hold past the usual limits, up to 35 seconds in one try", async () => {
  withEngine();
  vi.spyOn(console, "warn").mockImplementation(() => {});
  vi.useFakeTimers();
  vi.spyOn(AbortSignal, "timeout").mockImplementation((ms) => {
    const controller = new AbortController();
    setTimeout(
      () => controller.abort(new DOMException("timeout", "TimeoutError")),
      ms,
    );
    return controller.signal;
  });
  const held = (ms: number) =>
    vi.fn<typeof fetch>(
      (_url, init) =>
        new Promise((resolve, reject) => {
          const timer = setTimeout(
            () => resolve(rpc({ command: null, screen: self })),
            ms,
          );
          init?.signal?.addEventListener("abort", () => {
            clearTimeout(timer);
            reject(init.signal!.reason);
          });
        }),
    );
  const args = { op: "listen", screen: kitchen, name: "Kitchen iPad" };
  // The engine waits 25 seconds and answers a little later: well inside the 35.
  const slow = held(30_000);
  const answered = callTool("app_screens", args, undefined, slow);
  await vi.advanceTimersByTimeAsync(30_000);
  await expect(answered).resolves.toEqual({ command: null, screen: self });
  // Past 35 seconds the call ends, and is never sent again.
  const stuck = held(60_000);
  const ended = callTool("app_screens", args, undefined, stuck);
  ended.catch(() => {});
  await vi.advanceTimersByTimeAsync(35_000);
  await expect(ended).rejects.toThrow("unavailable");
  expect(stuck).toHaveBeenCalledTimes(1);
});

it("ends the engine call when the browser gives up on the request", async () => {
  withEngine();
  const seen: AbortSignal[] = [];
  vi.mocked(fetch).mockImplementation(
    (_url, init) =>
      new Promise((_, reject) => {
        seen.push(init!.signal!);
        init!.signal!.addEventListener("abort", () =>
          reject(init!.signal!.reason),
        );
      }),
  );
  const browser = new AbortController();
  const answer = ask(
    { op: "listen", screen: kitchen, name: "Kitchen iPad" },
    { signal: browser.signal },
  );
  await vi.waitFor(() => expect(seen).toHaveLength(1));
  browser.abort();
  expect((await answer).status).toBe(502);
  expect(seen[0].aborted).toBe(true);
});

it("has a hosted front end run a listen for 60 seconds and cancel it when the browser leaves", () => {
  // Vercel passes a disconnect on to a function only when it opts in.
  const hosted = JSON.parse(readFileSync("vercel.json", "utf8"));
  expect(hosted.functions["api/screens.ts"]).toEqual({
    maxDuration: 60,
    supportsCancellation: true,
  });
});

it("passes a dropped connection on to the route through the Node server", async () => {
  const dir = withEngine();
  writeFileSync(join(dir, "index.html"), '<div id="root"></div>');
  const seen: AbortSignal[] = [];
  vi.mocked(fetch).mockImplementation(
    (_url, init) =>
      new Promise((_, reject) => {
        seen.push(init!.signal!);
        init!.signal!.addEventListener("abort", () =>
          reject(init!.signal!.reason),
        );
      }),
  );
  const server = createAppServer(dir);
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const port = (server.address() as AddressInfo).port;
  try {
    const req = httpRequest({
      host: "127.0.0.1",
      port,
      path: "/api/screens",
      method: "POST",
      headers: {
        host: `127.0.0.1:${port}`,
        "content-type": "application/json",
      },
    });
    req.on("error", () => {});
    req.end(JSON.stringify({ op: "listen", screen: kitchen, name: "Hall" }));
    await vi.waitFor(() => expect(seen).toHaveLength(1));
    expect(seen[0].aborted).toBe(false);
    // The browser leaves mid-wait.
    req.destroy();
    await vi.waitFor(() => expect(seen[0].aborted).toBe(true));
  } finally {
    server.close();
    rmSync(dir, { recursive: true, force: true });
  }
});

// ----- Piece 3 (docs/dev/screens.md 10.5, 10.7 and 10.10).

const grid = {
  parts: [
    { id: "map", x: 3, y: 4, w: 9, h: 8 },
    { id: "main", x: 0, y: 0, w: 3, h: 12 },
    { id: "recovery", x: 3, y: 0, w: 3, h: 4 },
    { id: "training", x: 6, y: 0, w: 3, h: 4 },
    { id: "changed", x: 9, y: 0, w: 3, h: 4 },
  ],
};
// An engine with piece 3 lists `grid` among app_screens' settings.
const piece3 = () =>
  resetEngine(allAppTools, {
    app_screens: ["op", "screen", "settings", "settings.grid", "part"],
  });
const listening = (settings: unknown) => ({
  op: "listen",
  screen: kitchen,
  name: "Kitchen iPad",
  hold: 25,
  showing: home,
  settings,
});

it("passes a Home grid on to an engine with piece 3, and leaves it out for an older one", async () => {
  withEngine();
  piece3();
  vi.mocked(fetch).mockImplementation(async () =>
    rpc({ command: null, screen: self }),
  );
  const settings = { layout: "band", tiles, home: null, grid };
  expect((await ask(listening(settings))).status).toBe(200);
  expect(
    called(vi.mocked(fetch).mock.calls.at(-1)![1]).arguments.settings,
  ).toEqual(settings);
  // An engine whose tool list doesn't take a grid never gets one.
  resetEngine(allAppTools);
  expect((await ask(listening(settings))).status).toBe(200);
  expect(
    called(vi.mocked(fetch).mock.calls.at(-1)![1]).arguments.settings,
  ).toEqual({
    layout: "band",
    tiles,
    home: null,
  });
  // A grid that breaks Home's rules, or null, never reaches any engine.
  const calls = vi.mocked(fetch).mock.calls.length;
  for (const bad of [
    null,
    { parts: grid.parts.slice(1) },
    { parts: grid.parts.map((p) => (p.id === "main" ? { ...p, w: 1 } : p)) },
  ])
    expect(
      (await ask(listening({ layout: "band", tiles, grid: bad }))).status,
    ).toBe(400);
  expect(vi.mocked(fetch).mock.calls.length).toBe(calls);
});

it("sends the commands that arrange parts only to an engine with piece 3", async () => {
  withEngine();
  const move = {
    op: "send",
    screen: kitchen,
    to: hall,
    action: "move",
    part: "map",
    place: "right",
    dashboard: null,
  };
  expect(await (await ask(move)).json()).toEqual({
    ok: false,
    reason: "It wasn't sent: this install's engine can't arrange parts yet.",
  });
  expect(fetch).not.toHaveBeenCalled();
  piece3();
  vi.mocked(fetch).mockResolvedValueOnce(
    rpc({
      command: {
        ...command,
        action: "move",
        args: { part: "map", place: "right", dashboard: null },
      },
    }),
  );
  expect((await ask(move)).status).toBe(200);
  expect(called(vi.mocked(fetch).mock.calls.at(-1)![1]).arguments).toEqual({
    op: "send",
    screen: kitchen,
    to: hall,
    action: "move",
    args: { part: "map", place: "right", dashboard: null },
  });
});

it("reads which engine has piece 3 from its tool list, and sends a refused listen once more without the grid", async () => {
  withEngine();
  resetEngine();
  vi.spyOn(console, "warn").mockImplementation(() => {});
  const listing = (inputs: Record<string, unknown>) =>
    new Response(
      JSON.stringify({
        jsonrpc: "2.0",
        id: requestId,
        result: {
          tools: [
            {
              name: "app_screens",
              inputSchema: { type: "object", properties: inputs },
            },
          ],
        },
      }),
      { headers: { "content-type": "application/json" } },
    );
  const refused = new Response(
    JSON.stringify({
      jsonrpc: "2.0",
      id: requestId,
      result: {
        structuredContent: {
          error: {
            code: "invalid_arguments",
            message: "settings.grid is not allowed.",
          },
        },
        content: [{ type: "text", text: "settings.grid is not allowed." }],
        isError: true,
      },
    }),
    { status: 400, headers: { "content-type": "application/json" } },
  );
  const lists: unknown[] = [];
  const answers = [refused, rpc({ command: null, screen: self })];
  vi.mocked(fetch).mockImplementation(async (_url, init) => {
    const body = JSON.parse(String(init?.body));
    if (body.method === "tools/list") {
      lists.push(body);
      return listing({
        settings: {
          type: "object",
          properties: { layout: {}, tiles: {}, home: {}, grid: {} },
        },
      });
    }
    return answers.shift()!;
  });
  const settings = { layout: "band", tiles, home: null, grid };
  const response = await ask(listening(settings));
  expect(response.status).toBe(200);
  const sent = vi
    .mocked(fetch)
    .mock.calls.map(([, init]) => JSON.parse(String(init?.body)))
    .filter((b) => b.method === "tools/call")
    .map((b) => b.params.arguments.settings);
  expect(sent).toEqual([settings, { layout: "band", tiles, home: null }]);
  // The listing that was wrong is read again.
  expect(lists.length).toBe(2);
});
