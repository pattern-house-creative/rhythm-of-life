import { act, renderHook } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { useKnowledge } from "../src/useKnowledge";
import { emptyGraph, type GraphSession } from "../src/liveGraph";
import { record } from "./synthetic";

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});
const fixture = () => ({
  ...emptyGraph(),
  nodes: [record("fictional-memory")],
});
function source(): GraphSession {
  return {
    initial: emptyGraph(),
    refresh: vi.fn(async () => fixture()),
    retrieve: vi.fn(async () => ({ ...fixture(), cursors: {} })),
  };
}

it("shows a valid graph before optional pins finish and ignores pins after access loss", async () => {
  let finish!: (pins: string[]) => void;
  const session = {
    ...source(),
    pins: vi.fn(
      () =>
        new Promise<string[]>((resolve) => {
          finish = resolve;
        }),
    ),
  };
  const { result } = renderHook(() => useKnowledge(session));
  await act(async () => {});
  expect(result.current.page.nodes).toHaveLength(1);
  expect(result.current.busy).toBe(false);
  act(() => window.dispatchEvent(new Event("observatory-access-lost")));
  await act(async () => finish(["fictional-memory"]));
  expect(result.current.page.nodes).toHaveLength(0);
  expect(result.current.pins).toEqual([]);
});

it("keeps Refresh, Discover and a search working while saved pins load, and applies them after", async () => {
  let finish!: (pins: string[]) => void;
  const session = {
    ...source(),
    pins: vi.fn(
      () =>
        new Promise<string[]>((resolve) => {
          finish = resolve;
        }),
    ),
  };
  const { result } = renderHook(() => useKnowledge(session));
  await act(async () => {});
  expect(result.current.busy).toBe(false);
  // The pins read is still out: the map's own reads go ahead.
  await act(async () => result.current.discoverMore());
  expect(session.retrieve).toHaveBeenCalledTimes(1);
  await act(async () => result.current.refresh());
  expect(session.refresh).toHaveBeenCalledTimes(2);
  // One pins read at a time.
  expect(session.pins).toHaveBeenCalledTimes(1);
  // A search result merges in; the saved pins still apply when they arrive.
  const found = record("fictional-found");
  act(() => result.current.accept({ ...emptyGraph(), nodes: [found] }));
  await act(async () => finish([found.id]));
  expect(result.current.pins).toEqual([found.id]);
  expect(result.current.page.nodes.map((n) => n.id)).toContain(found.id);
});

it("refreshes the authorized graph every 15 minutes, pauses hidden pages and coalesces restoration", async () => {
  vi.useFakeTimers();
  const hidden = vi.spyOn(document, "hidden", "get").mockReturnValue(false);
  const session = source();
  const { result } = renderHook(() => useKnowledge(session));
  await act(async () => {});
  expect(session.refresh).toHaveBeenCalledTimes(1);
  expect(result.current.page.nodes).toHaveLength(1);
  await act(async () => {
    await vi.advanceTimersByTimeAsync(899_999);
  });
  expect(session.refresh).toHaveBeenCalledTimes(1);
  await act(async () => {
    await vi.advanceTimersByTimeAsync(1);
  });
  expect(session.refresh).toHaveBeenCalledTimes(2);
  await act(async () => window.dispatchEvent(new Event("focus")));
  expect(session.refresh).toHaveBeenCalledTimes(2);
  hidden.mockReturnValue(true);
  act(() => document.dispatchEvent(new Event("visibilitychange")));
  expect(result.current.page.nodes).toHaveLength(0);
  expect(result.current.lastRefresh).toBeNull();
  await act(async () => {
    await vi.advanceTimersByTimeAsync(1_800_000);
  });
  expect(session.refresh).toHaveBeenCalledTimes(2);
  hidden.mockReturnValue(false);
  await act(async () => {
    document.dispatchEvent(new Event("visibilitychange"));
    window.dispatchEvent(new Event("pageshow"));
    window.dispatchEvent(new Event("focus"));
  });
  expect(session.refresh).toHaveBeenCalledTimes(3);
});

it("keeps the map it read last through a failed refresh, clears it on access loss, and rejects late private responses", async () => {
  const session = source();
  const { result } = renderHook(() => useKnowledge(session));
  await act(async () => {});
  vi.mocked(session.refresh).mockRejectedValueOnce(
    new Error("source unavailable"),
  );
  await act(async () => result.current.refresh());
  // Kept, and marked as not confirmed since.
  expect(result.current.page.nodes).toHaveLength(1);
  expect(result.current.page.nodes[0].resource.availability).toBe("stale");
  expect(result.current.error).toContain("still available");
  let finish!: (value: ReturnType<typeof fixture>) => void;
  vi.mocked(session.refresh).mockImplementationOnce(
    () =>
      new Promise((resolve) => {
        finish = resolve;
      }),
  );
  act(() => {
    void result.current.refresh();
    void result.current.refresh();
  });
  expect(session.refresh).toHaveBeenCalledTimes(3);
  act(() => window.dispatchEvent(new Event("observatory-access-lost")));
  await act(async () => finish(fixture()));
  expect(result.current.page.nodes).toHaveLength(0);
  await act(async () => result.current.refresh());
  expect(session.refresh).toHaveBeenCalledTimes(3);
});

it("tries a failed refresh again after 15 s, then 30 s, and is back on the regular 15 minutes once one works", async () => {
  vi.useFakeTimers();
  const session = source();
  const { result } = renderHook(() => useKnowledge(session));
  await act(() => vi.advanceTimersByTimeAsync(0));
  expect(session.refresh).toHaveBeenCalledTimes(1);
  // The app's server restarts for a deploy.
  vi.mocked(session.refresh).mockRejectedValue(new Error("502"));
  await act(() => vi.advanceTimersByTimeAsync(900_000));
  expect(session.refresh).toHaveBeenCalledTimes(2);
  expect(result.current.page.nodes).toHaveLength(1);
  await act(() => vi.advanceTimersByTimeAsync(14_999));
  expect(session.refresh).toHaveBeenCalledTimes(2);
  await act(() => vi.advanceTimersByTimeAsync(1));
  expect(session.refresh).toHaveBeenCalledTimes(3);
  await act(() => vi.advanceTimersByTimeAsync(29_999));
  expect(session.refresh).toHaveBeenCalledTimes(3);
  // Back: the map is confirmed again, and no more tries come between the regular reads.
  vi.mocked(session.refresh).mockImplementation(async () => fixture());
  await act(() => vi.advanceTimersByTimeAsync(1));
  expect(session.refresh).toHaveBeenCalledTimes(4);
  expect(result.current.error).toBeNull();
  expect(result.current.page.nodes[0].resource.availability).toBe("available");
  // The next regular read comes 15 minutes after the one before it.
  await act(() => vi.advanceTimersByTimeAsync(854_999));
  expect(session.refresh).toHaveBeenCalledTimes(4);
  // A try waiting when access is lost never runs.
  vi.mocked(session.refresh).mockRejectedValue(new Error("502"));
  await act(() => vi.advanceTimersByTimeAsync(1));
  expect(session.refresh).toHaveBeenCalledTimes(5);
  act(() => window.dispatchEvent(new Event("observatory-access-lost")));
  expect(result.current.page.nodes).toHaveLength(0);
  await act(() => vi.advanceTimersByTimeAsync(300_000));
  expect(session.refresh).toHaveBeenCalledTimes(5);
});
