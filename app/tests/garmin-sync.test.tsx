import { act, renderHook } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { requestGarminSync } from "../server/additionalHealth";
import { POST } from "../api/garmin-sync";
import { garminSyncReload, useHealthRefresh } from "../src/useHealthRefresh";
import { unavailableHealth } from "../server/healthState";
import { appToken, called, engineUrl, rpc, withEngine } from "./engine";
import { resetEngine } from "../server/engine";

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
});
const configured = () => withEngine();
const answer = {
  accepted: true,
  pendingSince: "2026-10-28T15:00:00.123456+00:00",
  nextAllowedAt: "2026-10-28T15:15:00.123456+00:00",
  lastSyncedAt: null,
};
const declined = {
  accepted: false,
  pendingSince: null,
  nextAllowedAt: null,
  lastSyncedAt: null,
};
const request = (init?: RequestInit) =>
  new Request("http://rhythm.example.invalid/api/garmin-sync", {
    method: "POST",
    ...init,
  });

it("asks the engine with the app token and passes on only the four fields", async () => {
  configured();
  vi.mocked(fetch).mockResolvedValueOnce(rpc(answer));
  expect(await requestGarminSync(request())).toEqual(answer);
  const [url, init] = vi.mocked(fetch).mock.calls.at(-1)!;
  expect(url).toBe(`${engineUrl}/mcp`);
  expect(init).toMatchObject({
    method: "POST",
    redirect: "error",
    cache: "no-store",
    headers: expect.objectContaining({
      Authorization: `Bearer ${appToken}`,
    }),
  });
  expect(called(init)).toEqual({
    name: "app_request_garmin_sync",
    arguments: {},
  });
  const tooSoon = {
    accepted: false,
    pendingSince: null,
    nextAllowedAt: "2026-10-28T15:15:00+00:00",
    lastSyncedAt: "2026-10-28T15:01:00+00:00",
  };
  vi.mocked(fetch).mockResolvedValueOnce(rpc(tooSoon));
  expect(await requestGarminSync(request())).toEqual(tooSoon);
});

it("treats an unavailable, refused or malformed answer as not accepted", async () => {
  expect(await requestGarminSync(request())).toEqual(declined);
  expect(fetch).not.toHaveBeenCalled();
  configured();
  for (const response of [
    new Response("", { status: 403 }),
    new Response("login", { headers: { "content-type": "text/html" } }),
    rpc({ ...answer, extra: 1 }),
    rpc({ ...answer, accepted: "yes" }),
    rpc({ ...answer, nextAllowedAt: null }),
    rpc({ ...answer, pendingSince: "yesterday" }),
  ]) {
    vi.mocked(fetch).mockResolvedValueOnce(response);
    expect(await requestGarminSync(request())).toEqual(declined);
  }
  vi.mocked(fetch).mockRejectedValueOnce(new Error("timeout"));
  expect(await requestGarminSync(request())).toEqual(declined);
  // Garmin off at the engine: not offered, so not asked.
  resetEngine(["app_graph"]);
  vi.mocked(fetch).mockClear();
  expect(await requestGarminSync(request())).toEqual(declined);
  expect(fetch).not.toHaveBeenCalled();
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
  expect((await POST(request({ body: '{"days":30}' }))).status).toBe(400);
  expect(fetch).toHaveBeenCalledTimes(1);
});

it("refresh asks for a sync and reads Garmin again only when one was accepted or is running", async () => {
  vi.useFakeTimers();
  vi.spyOn(document, "hidden", "get").mockReturnValue(false);
  const latest = { ...unavailableHealth(), healthMode: "live" };
  let sync: Response | Error = new Response(JSON.stringify(answer), {
    headers: { "content-type": "application/json" },
  });
  vi.mocked(fetch).mockImplementation(async (path, init) => {
    if (String(path) === "/api/garmin-sync") {
      expect(init?.method).toBe("POST");
      expect(init?.body).toBeUndefined();
      if (sync instanceof Error) throw sync;
      return sync;
    }
    return new Response(JSON.stringify(latest), {
      headers: { "content-type": "application/json" },
    });
  });
  const calls = (part: string) =>
    vi.mocked(fetch).mock.calls.filter(([p]) => String(p).includes(part))
      .length;
  const { result } = renderHook(useHealthRefresh);
  await act(async () => {});
  expect(calls("/api/garmin-sync")).toBe(0);
  expect(calls("source=garmin")).toBe(1);
  await act(async () => result.current.refresh());
  expect(calls("/api/garmin-sync")).toBe(1);
  expect(calls("source=garmin")).toBe(2);
  await act(async () => vi.advanceTimersByTimeAsync(garminSyncReload - 1000));
  expect(calls("source=garmin")).toBe(2);
  await act(async () => vi.advanceTimersByTimeAsync(1000));
  expect(calls("source=garmin")).toBe(3);
  // Too soon: nothing more than the refresh itself, and no error.
  sync = new Response(JSON.stringify(declined), {
    headers: { "content-type": "application/json" },
  });
  await act(async () => result.current.refresh());
  await act(async () => vi.advanceTimersByTimeAsync(garminSyncReload));
  expect(calls("/api/garmin-sync")).toBe(2);
  expect(calls("source=garmin")).toBe(4);
  expect(result.current.error).toBe(false);
  // A sync already under way also earns a second read.
  sync = new Response(
    JSON.stringify({ ...declined, pendingSince: answer.pendingSince }),
    { headers: { "content-type": "application/json" } },
  );
  await act(async () => result.current.refresh());
  await act(async () => vi.advanceTimersByTimeAsync(garminSyncReload));
  expect(calls("source=garmin")).toBe(6);
  sync = new Error("outage");
  await act(async () => result.current.refresh());
  await act(async () => vi.advanceTimersByTimeAsync(garminSyncReload));
  expect(calls("source=garmin")).toBe(7);
  expect(result.current.error).toBe(false);
  expect(result.current.data).not.toBeNull();
  // Hiding cancels a pending re-read.
  sync = new Response(JSON.stringify(answer), {
    headers: { "content-type": "application/json" },
  });
  await act(async () => result.current.refresh());
  const before = calls("source=garmin");
  vi.spyOn(document, "hidden", "get").mockReturnValue(true);
  act(() => document.dispatchEvent(new Event("visibilitychange")));
  await act(async () => vi.advanceTimersByTimeAsync(garminSyncReload));
  expect(calls("source=garmin")).toBe(before);
});
