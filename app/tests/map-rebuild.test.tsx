import { act, renderHook } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { requestRebuild } from "../server/graphRefresh";
import { POST } from "../api/graph-refresh";
import { createApiSession } from "../src/apiClient";
import { rebuildRereads, useMapRebuild } from "../src/useMapRebuild";
import type { GraphSession } from "../src/liveGraph";
import { appToken, closedEngines, withEngine } from "./engine";

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
});
const configured = () => withEngine();
const rpc = (payload: unknown) =>
  new Response(
    JSON.stringify({
      jsonrpc: "2.0",
      id: "rhythm-app",
      result: { structuredContent: payload },
    }),
    { headers: { "content-type": "application/json" } },
  );
const answer = {
  accepted: true,
  pendingSince: "2026-10-28T15:00:00.123456+00:00",
  running: false,
  nextAllowedAt: null,
  lastFinishedAt: "2026-10-28T14:45:31.5+00:00",
};
const declined = {
  accepted: false,
  pendingSince: null,
  running: false,
  nextAllowedAt: null,
  lastFinishedAt: null,
};
const request = (init?: RequestInit) =>
  new Request("http://rhythm.example.invalid/api/graph-refresh", {
    method: "POST",
    ...init,
  });

it("asks the engine with the app token and passes on only the five fields", async () => {
  configured();
  vi.mocked(fetch).mockResolvedValueOnce(rpc(answer));
  expect(await requestRebuild(request())).toEqual(answer);
  const [, init] = vi.mocked(fetch).mock.calls.at(-1)!;
  expect(init).toMatchObject({
    method: "POST",
    redirect: "error",
    cache: "no-store",
    headers: expect.objectContaining({
      Authorization: `Bearer ${appToken}`,
    }),
  });
  expect(JSON.parse(String(init?.body))).toMatchObject({
    params: { name: "app_request_refresh", arguments: {} },
  });
  const tooSoon = {
    accepted: false,
    pendingSince: null,
    running: false,
    nextAllowedAt: "2026-10-28T15:03:00+00:00",
    lastFinishedAt: "2026-10-28T15:01:00Z",
  };
  vi.mocked(fetch).mockResolvedValueOnce(rpc(tooSoon));
  expect(await requestRebuild(request())).toEqual(tooSoon);
});

it("treats an unavailable, refused or malformed answer as not accepted", async () => {
  expect(await requestRebuild(request())).toEqual(declined);
  expect(fetch).not.toHaveBeenCalled();
  configured();
  for (const response of [
    new Response("", { status: 403 }),
    new Response("login", { headers: { "content-type": "text/html" } }),
    rpc({ ...answer, extra: 1 }),
    rpc({ ...answer, accepted: "yes" }),
    rpc({ ...answer, running: null }),
    rpc({ ...answer, pendingSince: "yesterday" }),
    rpc({ ...answer, lastFinishedAt: "2026-10-28T15:00:00" }),
  ]) {
    vi.mocked(fetch).mockResolvedValueOnce(response);
    expect(await requestRebuild(request())).toEqual(declined);
  }
  vi.mocked(fetch).mockRejectedValueOnce(new Error("timeout"));
  expect(await requestRebuild(request())).toEqual(declined);
  // Demo mode, or an expired credential, never asks.
  for (const [, close] of closedEngines) {
    close();
    vi.mocked(fetch).mockClear();
    expect(await requestRebuild(request())).toEqual(declined);
    expect(fetch).not.toHaveBeenCalled();
  }
});

it("accepts only a same-origin POST without a body", async () => {
  configured();
  vi.mocked(fetch).mockResolvedValueOnce(rpc(answer));
  const accepted = await POST(request());
  expect(accepted.status).toBe(200);
  expect(accepted.headers.get("cache-control")).toContain("no-store");
  expect(await accepted.json()).toEqual(answer);
  expect((await POST(request({ method: "GET" }))).status).toBe(405);
  expect(
    (await POST(request({ body: "{}", headers: { origin: "https://x.test" } })))
      .status,
  ).toBe(403);
  expect((await POST(request({ body: '{"now":true}' }))).status).toBe(400);
  expect(fetch).toHaveBeenCalledTimes(1);
});

it("the live session asks the app's route; the demo has nothing to rebuild", async () => {
  expect(createApiSession(true).requestRebuild).toBeUndefined();
  const live = createApiSession(false);
  vi.mocked(fetch).mockResolvedValueOnce(
    new Response(JSON.stringify({ ...declined, pendingSince: answer.pendingSince }), {
      headers: { "content-type": "application/json" },
    }),
  );
  expect(await live.requestRebuild!()).toEqual({
    accepted: false,
    pending: true,
  });
  const [path, init] = vi.mocked(fetch).mock.calls.at(-1)!;
  expect(path).toBe("/api/graph-refresh");
  expect(init).toMatchObject({ method: "POST", credentials: "same-origin" });
  expect(init?.body).toBeUndefined();
});

it("refresh reads the map again at once, and twice more only when a rebuild was accepted or is waiting", async () => {
  vi.useFakeTimers();
  vi.spyOn(document, "hidden", "get").mockReturnValue(false);
  let next: { accepted: boolean; pending: boolean } | Error = {
    accepted: true,
    pending: true,
  };
  const ask = vi.fn(async () => {
    if (next instanceof Error) throw next;
    return next;
  });
  const session = { requestRebuild: ask } as unknown as GraphSession;
  const reread = vi.fn();
  const { result } = renderHook(() => useMapRebuild(session, reread));
  await act(async () => result.current());
  expect(ask).toHaveBeenCalledTimes(1);
  expect(reread).toHaveBeenCalledTimes(1);
  await act(async () => vi.advanceTimersByTimeAsync(rebuildRereads[0] - 1000));
  expect(reread).toHaveBeenCalledTimes(1);
  await act(async () => vi.advanceTimersByTimeAsync(1000));
  expect(reread).toHaveBeenCalledTimes(2);
  await act(async () =>
    vi.advanceTimersByTimeAsync(rebuildRereads[1] - rebuildRereads[0]),
  );
  expect(reread).toHaveBeenCalledTimes(3);
  // Too soon: the immediate read only, and no error.
  next = { accepted: false, pending: false };
  await act(async () => result.current());
  await act(async () => vi.advanceTimersByTimeAsync(rebuildRereads[1]));
  expect(reread).toHaveBeenCalledTimes(4);
  // A request already waiting also earns the later reads.
  next = { accepted: false, pending: true };
  await act(async () => result.current());
  await act(async () => vi.advanceTimersByTimeAsync(rebuildRereads[1]));
  expect(reread).toHaveBeenCalledTimes(7);
  // Unavailable: the immediate read only.
  next = new Error("outage");
  await act(async () => result.current());
  await act(async () => vi.advanceTimersByTimeAsync(rebuildRereads[1]));
  expect(reread).toHaveBeenCalledTimes(8);
  // Hiding, going offline or losing access cancels the later reads.
  next = { accepted: true, pending: true };
  for (const leave of [
    () => {
      vi.spyOn(document, "hidden", "get").mockReturnValue(true);
      document.dispatchEvent(new Event("visibilitychange"));
      vi.spyOn(document, "hidden", "get").mockReturnValue(false);
    },
    () => window.dispatchEvent(new Event("offline")),
    () => window.dispatchEvent(new Event("observatory-access-lost")),
  ]) {
    await act(async () => result.current());
    const before = reread.mock.calls.length;
    act(leave);
    await act(async () => vi.advanceTimersByTimeAsync(rebuildRereads[1]));
    expect(reread).toHaveBeenCalledTimes(before);
  }
  // Without a rebuild (the demo), refresh still reads again at once.
  const { result: demo } = renderHook(() =>
    useMapRebuild({} as GraphSession, reread),
  );
  const before = reread.mock.calls.length;
  await act(async () => demo.current());
  await act(async () => vi.advanceTimersByTimeAsync(rebuildRereads[1]));
  expect(reread).toHaveBeenCalledTimes(before + 1);
});
