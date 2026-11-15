import { act, renderHook } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { useHealthRefresh } from "../src/useHealthRefresh";
import { unavailableHealth } from "../server/healthState";

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});
const response = () =>
  new Response(JSON.stringify(unavailableHealth()), {
    headers: { "content-type": "application/json" },
  });
it("refreshes every minute while visible, coalesces restoration, and cancels hidden requests", async () => {
  vi.useFakeTimers();
  vi.mocked(fetch).mockImplementation(async () => response());
  const hidden = vi.spyOn(document, "hidden", "get").mockReturnValue(false);
  const { result } = renderHook(useHealthRefresh);
  await act(async () => {});
  expect(fetch).toHaveBeenCalledTimes(1);
  await act(async () => {
    await vi.advanceTimersByTimeAsync(60_000);
  });
  expect(fetch).toHaveBeenCalledTimes(2);
  await act(async () => {
    window.dispatchEvent(new Event("focus"));
  });
  expect(fetch).toHaveBeenCalledTimes(2);
  hidden.mockReturnValue(true);
  act(() => document.dispatchEvent(new Event("visibilitychange")));
  expect(result.current.data).toBeNull();
  await act(async () => {
    await vi.advanceTimersByTimeAsync(180_000);
  });
  expect(fetch).toHaveBeenCalledTimes(2);
  hidden.mockReturnValue(false);
  await act(async () => {
    document.dispatchEvent(new Event("visibilitychange"));
    window.dispatchEvent(new Event("pageshow"));
    window.dispatchEvent(new Event("focus"));
  });
  expect(fetch).toHaveBeenCalledTimes(3);
  let finish!: (value: Response) => void;
  vi.mocked(fetch).mockImplementation(
    () =>
      new Promise((resolve) => {
        finish = resolve;
      }),
  );
  act(() => {
    result.current.refresh();
    result.current.refresh();
  });
  expect(fetch).toHaveBeenCalledTimes(4);
  act(() => window.dispatchEvent(new Event("pagehide")));
  await act(async () => finish(response()));
  expect(result.current.data).toBeNull();
});

it("replaces a value with unavailable on failure, clears offline, and permanently stops on device exit", async () => {
  const fresh = unavailableHealth();
  fresh.current.value = 123;
  fresh.current.status = "available";
  fresh.current.provenance.observedAt = new Date().toISOString();
  vi.mocked(fetch).mockResolvedValueOnce(
    new Response(JSON.stringify(fresh), {
      headers: { "content-type": "application/json" },
    }),
  );
  const { result } = renderHook(useHealthRefresh);
  await act(async () => {});
  expect(result.current.data?.current.value).toBe(123);
  vi.mocked(fetch).mockImplementation(async () => response());
  await act(async () => result.current.refresh());
  expect(result.current.data?.current.value).toBeNull();
  expect(result.current.data?.current.status).toBe("unavailable");
  act(() => window.dispatchEvent(new Event("offline")));
  expect(result.current.data).toBeNull();
  act(() => result.current.stop());
  const calls = vi.mocked(fetch).mock.calls.length;
  await act(async () => {
    window.dispatchEvent(new Event("online"));
    result.current.refresh();
  });
  expect(fetch).toHaveBeenCalledTimes(calls);
});

it("tries again by itself after access is lost, less often each time, and comes back once access works", async () => {
  vi.useFakeTimers();
  vi.mocked(fetch).mockImplementation(
    async () => new Response("Unauthorized", { status: 401 }),
  );
  const cleared = vi.fn();
  const { result } = renderHook(() => useHealthRefresh(cleared));
  await act(() => vi.advanceTimersByTimeAsync(0));
  // Everything private is cleared at once, and the screen says access is unavailable.
  expect(result.current.data).toBeNull();
  expect(result.current.error).toBe(true);
  expect(cleared).toHaveBeenLastCalledWith(true);
  expect(fetch).toHaveBeenCalledTimes(1);
  await act(() => vi.advanceTimersByTimeAsync(14_999));
  expect(fetch).toHaveBeenCalledTimes(1);
  await act(() => vi.advanceTimersByTimeAsync(1));
  expect(fetch).toHaveBeenCalledTimes(2);
  await act(() => vi.advanceTimersByTimeAsync(29_999));
  expect(fetch).toHaveBeenCalledTimes(2);
  await act(() => vi.advanceTimersByTimeAsync(1));
  expect(fetch).toHaveBeenCalledTimes(3);
  expect(result.current.data).toBeNull();
  // Access works again (the front door's session renewed, say): nobody has to touch the screen.
  vi.mocked(fetch).mockImplementation(async () => response());
  await act(() => vi.advanceTimersByTimeAsync(60_000));
  expect(fetch).toHaveBeenCalledTimes(4);
  expect(result.current.data).not.toBeNull();
  expect(result.current.error).toBe(false);
  // Lost again later: the first try comes 15 s after, as before.
  vi.mocked(fetch).mockImplementation(
    async () => new Response("Unauthorized", { status: 401 }),
  );
  await act(async () => result.current.refresh());
  expect(result.current.data).toBeNull();
  const calls = vi.mocked(fetch).mock.calls.length;
  await act(() => vi.advanceTimersByTimeAsync(15_000));
  expect(fetch).toHaveBeenCalledTimes(calls + 1);
});
