import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { act, fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { handleSync } from "../server/sync";
import { routes } from "../server/routes";
import {
  cleanAsked,
  cleanSyncAnswer,
  cleanSyncList,
  cleanSyncs,
  every,
  stateWord,
  waitingFor,
} from "../src/sync";
import type { AdapterSync, SyncAnswer, SyncList } from "../src/sync";
import { SyncNow } from "../src/SyncNow";
import { Details } from "../src/Details";
import { demoAdapter } from "../src/demo";
import { setInstance, demoInstance } from "../src/instance";
import { called, rpc, withEngine } from "./engine";

// SB-P6-09 (API-47): Sync now for any adapter on the engine's clock, from Sources & settings,
// through GET and POST /api/sync and app_sync (contracts/README.md 2.2). Stand-ins only: no
// engine, adapter or service is reached.
const fixture = (name = "app_sync.example.json") =>
  JSON.parse(
    readFileSync(
      resolve(process.cwd(), `../contracts/fixtures/${name}`),
      "utf8",
    ),
  );
const url = "http://rhythm.example.invalid/api/sync";
const get = () => new Request(url, { method: "GET" });
const post = (body: unknown, type = "application/json") =>
  new Request(url, {
    method: "POST",
    headers: { "content-type": type },
    body: typeof body === "string" ? body : JSON.stringify(body),
  });

afterEach(() => {
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
  vi.useRealTimers();
  setInstance(demoInstance);
});

it("reads the contract's examples and leaves out what it cannot read", () => {
  const adapters = cleanSyncs(fixture())!;
  expect(adapters.map((a) => [a.adapter, a.kind, a.outcome])).toEqual([
    ["csv", "measurements", null],
    ["documents", "imports", null],
    ["nightscout", "measurements", "failed"],
  ]);
  expect(cleanAsked(fixture("app_sync.request.example.json"))).toMatchObject({
    accepted: false,
    refused: "too-soon",
    adapter: { adapter: "garmin", limit: 900 },
  });
  // An outcome that could carry words, a name that isn't an adapter's, a limit under a minute:
  // that adapter is left out; an answer that isn't a list is none at all.
  const odd = fixture();
  odd.adapters[0].outcome = "the file was locked";
  odd.adapters[1].adapter = "Documents folder";
  odd.adapters[2].limit = 5;
  expect(cleanSyncs(odd)).toEqual([]);
  expect(cleanSyncs({ adapter: "csv" })).toBeNull();
  // Accepted with a refusal, or refused without one, is no answer.
  const asked = fixture("app_sync.request.example.json");
  expect(cleanAsked({ ...asked, accepted: true })).toBeNull();
  expect(cleanAsked({ ...asked, refused: null })).toBeNull();
  expect(cleanAsked({ ...asked, refused: "busy" })).toBeNull();
  expect(cleanSyncList("nonsense")).toEqual({ available: false });
  expect(cleanSyncAnswer({ available: true, ...asked }).available).toBe(true);
  expect([every(30), every(60), every(300), every(900), every(21600)]).toEqual([
    "every 30 seconds",
    "every minute",
    "every 5 minutes",
    "every 15 minutes",
    "every 6 hours",
  ]);
});

it("serves the list at GET /api/sync, and nothing in demo mode or from an older engine", async () => {
  expect(Object.keys(routes)).toContain("/api/sync");
  // Demo mode: no engine, nothing to sync, and no call.
  const none = vi.fn();
  expect(await (await handleSync(get(), none)).json()).toEqual({
    available: false,
  });
  expect(none).not.toHaveBeenCalled();
  // A live install asks app_sync with no arguments.
  withEngine();
  const fetcher = vi.fn().mockResolvedValue(rpc(fixture()));
  const answer = await (await handleSync(get(), fetcher)).json();
  expect(answer.available).toBe(true);
  expect(answer.adapters).toHaveLength(3);
  expect(called(fetcher.mock.calls[0][1])).toEqual({
    name: "app_sync",
    arguments: {},
  });
  // An engine older than app_sync doesn't list it: no call, nothing offered.
  withEngine({ tools: ["app_graph", "app_status"] });
  const older = vi.fn();
  expect(await (await handleSync(get(), older)).json()).toEqual({
    available: false,
  });
  expect(older).not.toHaveBeenCalled();
  // An answer outside the contract is no answer; the list is a read, so it is tried once more.
  withEngine();
  const wrong = vi.fn().mockResolvedValue(rpc({ adapters: "csv" }));
  expect((await (await handleSync(get(), wrong)).json()).available).toBe(false);
  const flaky = vi
    .fn()
    .mockRejectedValueOnce(new TypeError("network"))
    .mockResolvedValueOnce(rpc(fixture()));
  expect((await (await handleSync(get(), flaky)).json()).available).toBe(true);
  expect(flaky).toHaveBeenCalledTimes(2);
  expect((await handleSync(new Request(url, { method: "PUT" }))).status).toBe(
    405,
  );
});

it("asks for one batch with POST /api/sync, never twice, and only with an adapter's name", async () => {
  withEngine();
  const asked = {
    ...fixture("app_sync.request.example.json"),
    accepted: true,
    refused: null,
  };
  const fetcher = vi.fn().mockResolvedValue(rpc(asked));
  const answer = await (
    await handleSync(post({ adapter: "garmin" }), fetcher)
  ).json();
  expect(answer).toMatchObject({
    available: true,
    accepted: true,
    refused: null,
  });
  expect(called(fetcher.mock.calls[0][1])).toEqual({
    name: "app_sync",
    arguments: { adapter: "garmin" },
  });
  // A lost answer is not asked again: the engine may have accepted it.
  const lost = vi
    .fn()
    .mockRejectedValueOnce(new TypeError("network"))
    .mockResolvedValueOnce(rpc(asked));
  expect(
    await (await handleSync(post({ adapter: "garmin" }), lost)).json(),
  ).toEqual({
    available: false,
  });
  expect(lost).toHaveBeenCalledTimes(1);
  // An adapter the engine doesn't offer this caller answers 404: unavailable, nothing said.
  const unknown = vi.fn().mockResolvedValue(
    new Response(JSON.stringify({ error: "not_found" }), {
      status: 404,
      headers: { "content-type": "application/json" },
    }),
  );
  expect(
    await (await handleSync(post({ adapter: "nightscout" }), unknown)).json(),
  ).toEqual({
    available: false,
  });
  // Only exactly an adapter's name, as JSON.
  const never = vi.fn();
  for (const body of [
    { adapter: "Garmin" },
    { adapter: "garmin", now: true },
    { adapters: ["garmin"] },
    {},
    "[]",
  ])
    expect((await handleSync(post(body), never)).status).toBe(400);
  expect(
    (await handleSync(post({ adapter: "garmin" }, "text/plain"), never)).status,
  ).toBe(400);
  expect(never).not.toHaveBeenCalled();
  // Demo mode: nothing to ask.
  vi.unstubAllEnvs();
  withEngine({ token: "" });
  expect(
    await (await handleSync(post({ adapter: "csv" }), never)).json(),
  ).toEqual({
    available: false,
  });
  expect(never).not.toHaveBeenCalled();
});

const ready = (): AdapterSync[] => [
  {
    adapter: "csv",
    kind: "measurements",
    interval: 60,
    limit: 60,
    running: false,
    pendingSince: null,
    nextAllowedAt: null,
    lastAskedAt: null,
    lastFinishedAt: null,
    outcome: null,
  },
  {
    adapter: "garmin",
    kind: "measurements",
    interval: 60,
    limit: 900,
    running: false,
    pendingSince: null,
    nextAllowedAt: "2999-01-01T00:00:00Z",
    lastAskedAt: "2026-11-01T12:00:00Z",
    lastFinishedAt: "2026-11-01T12:00:31Z",
    outcome: "complete",
  },
];

it("lists each source with its own clock and a Sync now that waits out its limit", async () => {
  render(
    <SyncNow load={async () => ({ available: true, adapters: ready() })} />,
  );
  const panel = await screen.findByRole("region", { name: "Sync now" });
  expect(await within(panel).findByText("CSV files")).toBeInTheDocument();
  expect(
    within(panel).getByText(
      /Syncs on its own every minute; Sync now at most every minute/,
    ),
  ).toBeInTheDocument();
  expect(
    within(panel).getByText(/Sync now at most every 15 minutes/),
  ).toBeInTheDocument();
  expect(within(panel).getByText(/Last synced now:/)).toBeInTheDocument();
  expect(within(panel).getByText(/Sync now again from/)).toBeInTheDocument();
  expect(
    within(panel).getByRole("button", { name: "Sync CSV files now" }),
  ).toBeEnabled();
  // Inside its limit the button waits; the state says why.
  expect(
    within(panel).getByRole("button", { name: "Sync Garmin now" }),
  ).toBeDisabled();
  expect(within(panel).getByText("ASKED LATELY")).toBeInTheDocument();
  expect(stateWord(ready()[0])).toBe("Ready");
  expect(waitingFor({ ...ready()[0], running: true })).toBe("running");
  expect(
    waitingFor({ ...ready()[0], pendingSince: "2026-11-01T12:00:00Z" }),
  ).toBe("pending");
});

it("asks in the same view, follows the batch and says how it ended, with nothing opened over it", async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  const [csv] = ready();
  const states: SyncList[] = [
    { available: true, adapters: [csv] },
    {
      available: true,
      adapters: [
        { ...csv, running: true, lastAskedAt: "2026-11-01T12:00:00Z" },
      ],
    },
    {
      available: true,
      adapters: [
        {
          ...csv,
          lastAskedAt: "2026-11-01T12:00:00Z",
          lastFinishedAt: "2026-11-01T12:00:02Z",
          nextAllowedAt: "2999-01-01T00:00:00Z",
          outcome: "complete",
        },
      ],
    },
  ];
  const load = vi.fn(async () => states.shift() ?? states[0]!);
  const ask = vi.fn(async (): Promise<SyncAnswer> => ({
    available: true,
    accepted: true,
    refused: null,
    adapter: {
      ...csv,
      pendingSince: "2026-11-01T12:00:00Z",
      lastAskedAt: "2026-11-01T12:00:00Z",
    },
  }));
  render(<SyncNow load={load} ask={ask} follow={1000} />);
  const button = await screen.findByRole("button", {
    name: "Sync CSV files now",
  });
  fireEvent.click(button);
  expect(
    await screen.findByText("Asked for: it starts within seconds."),
  ).toBeInTheDocument();
  expect(ask).toHaveBeenCalledWith("csv", expect.anything());
  expect(screen.getByText(/Waiting to start/)).toBeInTheDocument();
  expect(
    screen.getByRole("button", { name: "Sync CSV files now" }),
  ).toBeDisabled();
  await act(async () => void (await vi.advanceTimersByTimeAsync(1000)));
  expect(await screen.findByText("Syncing now…")).toBeInTheDocument();
  await act(async () => void (await vi.advanceTimersByTimeAsync(1000)));
  expect(await screen.findByText(/Last synced now:/)).toBeInTheDocument();
  expect(screen.queryByText("Asked for: it starts within seconds.")).toBeNull();
  // Ended: no more reads.
  const reads = load.mock.calls.length;
  await act(async () => void (await vi.advanceTimersByTimeAsync(5000)));
  expect(load.mock.calls.length).toBe(reads);
  expect(screen.queryByRole("dialog")).toBeNull();
});

it("keeps following a batch after a read that finds the engine unavailable", async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  const [csv] = ready();
  const finished: SyncList = {
    available: true,
    adapters: [
      {
        ...csv,
        lastAskedAt: "2026-11-01T12:00:00Z",
        lastFinishedAt: "2026-11-01T12:00:02Z",
        nextAllowedAt: "2999-01-01T00:00:00Z",
        outcome: "complete",
      },
    ],
  };
  const states: SyncList[] = [
    { available: true, adapters: [csv] },
    { available: false },
    finished,
  ];
  const load = vi.fn(async () => states.shift() ?? finished);
  const ask = vi.fn(async (): Promise<SyncAnswer> => ({
    available: true,
    accepted: true,
    refused: null,
    adapter: {
      ...csv,
      pendingSince: "2026-11-01T12:00:00Z",
      lastAskedAt: "2026-11-01T12:00:00Z",
    },
  }));
  render(<SyncNow load={load} ask={ask} follow={1000} />);
  fireEvent.click(
    await screen.findByRole("button", { name: "Sync CSV files now" }),
  );
  expect(await screen.findByText(/Waiting to start/)).toBeInTheDocument();
  // The first follow-up read finds the engine unavailable: the row still waits, and the next
  // read is still made, which finds the batch finished.
  await act(async () => void (await vi.advanceTimersByTimeAsync(1000)));
  expect(screen.getByText(/Waiting to start/)).toBeInTheDocument();
  await act(async () => void (await vi.advanceTimersByTimeAsync(1000)));
  expect(await screen.findByText(/Last synced now:/)).toBeInTheDocument();
  expect(
    screen.getByRole("button", { name: "Sync CSV files now" }),
  ).toBeDisabled();
  expect(load).toHaveBeenCalledTimes(3);
});

it("says why a request was refused, and when Sync now can't be offered", async () => {
  const [csv] = ready();
  const ask = vi.fn(async (): Promise<SyncAnswer> => ({
    available: true,
    accepted: false,
    refused: "running",
    adapter: { ...csv, running: true },
  }));
  const { unmount } = render(
    <SyncNow
      load={async () => ({ available: true, adapters: [csv] })}
      ask={ask}
    />,
  );
  fireEvent.click(
    await screen.findByRole("button", { name: "Sync CSV files now" }),
  );
  expect(await screen.findByText(/It is syncing already/)).toBeInTheDocument();
  expect(screen.getByText("SYNCING")).toBeInTheDocument();
  unmount();
  const lost = vi.fn(async (): Promise<SyncAnswer> => ({ available: false }));
  const second = render(
    <SyncNow
      load={async () => ({ available: true, adapters: [csv] })}
      ask={lost}
    />,
  );
  fireEvent.click(
    await screen.findByRole("button", { name: "Sync CSV files now" }),
  );
  expect(
    await screen.findByText("Sync now couldn’t be asked for right now."),
  ).toBeInTheDocument();
  second.unmount();
  const off = render(<SyncNow load={async () => ({ available: false })} />);
  expect(
    await screen.findByText(/Sync now can’t be offered right now/),
  ).toBeInTheDocument();
  off.unmount();
  render(<SyncNow load={async () => ({ available: true, adapters: [] })} />);
  expect(
    await screen.findByText(/No source here syncs on the engine’s own clock/),
  ).toBeInTheDocument();
});

it("shows Sync now only in a live install's Sources & settings", async () => {
  const data = await demoAdapter.load();
  const props = {
    view: "sources" as const,
    data,
    state: "ready" as const,
    setState: vi.fn(),
    paused: false,
    setPaused: vi.fn(),
    close: vi.fn(),
    selectNode: vi.fn(),
  };
  const { unmount } = render(<Details {...props} />);
  expect(screen.queryByRole("region", { name: "Sync now" })).toBeNull();
  unmount();
  // Live: the panel reads /api/sync when it opens; pressing posts the adapter's name.
  setInstance({ ...demoInstance, mode: "live" });
  vi.mocked(fetch).mockImplementation(async (path, init) => {
    if (String(path) === "/api/sync" && !init?.body)
      return new Response(
        JSON.stringify({ available: true, adapters: ready() }),
        {
          headers: { "Content-Type": "application/json" },
        },
      );
    if (String(path) === "/api/sync")
      return new Response(
        JSON.stringify({
          available: true,
          accepted: true,
          refused: null,
          adapter: ready()[0],
        }),
        { headers: { "Content-Type": "application/json" } },
      );
    throw new Error("Unexpected request");
  });
  render(<Details {...props} />);
  const panel = await screen.findByRole("region", { name: "Sync now" });
  fireEvent.click(
    await within(panel).findByRole("button", { name: "Sync CSV files now" }),
  );
  expect(
    await within(panel).findByText("Asked for: it starts within seconds."),
  ).toBeInTheDocument();
  const posted = vi
    .mocked(fetch)
    .mock.calls.find(([, init]) => init?.method === "POST")!;
  expect(JSON.parse(String(posted[1]!.body))).toEqual({ adapter: "csv" });
});
