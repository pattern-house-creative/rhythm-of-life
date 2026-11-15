import { act, renderHook } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { useDashboards } from "../src/useDashboards";
import { useInsights } from "../src/useInsights";
import { createApiSession } from "../src/apiClient";
import { demoInsights } from "../src/demoInsights";

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});
it("coalesces opening, return and manual reads without cancelling in-flight dashboards or insights", async () => {
  const session = createApiSession(true);
  let finishBoards!: (value: []) => void;
  let finishInsights!: (value: { insights: []; runs: [] }) => void;
  let boardSignal: AbortSignal | undefined,
    insightSignal: AbortSignal | undefined;
  session.dashboards = vi.fn((signal) => {
    boardSignal = signal;
    return new Promise((r) => {
      finishBoards = r;
    });
  });
  session.insights = vi.fn((signal) => {
    insightSignal = signal;
    return new Promise((r) => {
      finishInsights = r;
    });
  });
  const { result } = renderHook(() => ({
    boards: useDashboards(session),
    insights: useInsights(session),
  }));
  await act(async () => {
    window.dispatchEvent(new Event("pageshow"));
    window.dispatchEvent(new Event("online"));
    void result.current.boards.reload();
    void result.current.insights.reload();
  });
  expect(session.dashboards).toHaveBeenCalledTimes(1);
  expect(session.insights).toHaveBeenCalledTimes(1);
  expect(boardSignal?.aborted).toBe(false);
  expect(insightSignal?.aborted).toBe(false);
  act(() => window.dispatchEvent(new Event("pagehide")));
  expect(boardSignal?.aborted).toBe(true);
  expect(insightSignal?.aborted).toBe(true);
  await act(async () => {
    finishBoards([]);
    finishInsights({ insights: [], runs: [] });
  });
  expect(result.current.boards.list).toEqual([]);
  expect(result.current.insights.insights).toEqual([]);
});

it("keeps the dashboards, corner cards and insights read last through a failed read, and tries again 15 s later", async () => {
  vi.useFakeTimers();
  const session = createApiSession(true);
  const [board] = await session.dashboards!();
  const insight = demoInsights()[0];
  const failed = () => Promise.reject(new Error("Bad gateway"));
  session.dashboards = vi
    .fn()
    .mockResolvedValueOnce([board])
    .mockImplementationOnce(failed)
    .mockResolvedValue([board]);
  session.dashboardCards = vi
    .fn()
    .mockResolvedValueOnce({ main: board.id })
    .mockImplementationOnce(failed)
    .mockImplementationOnce(failed)
    .mockResolvedValue({ main: board.id });
  session.insights = vi
    .fn()
    .mockResolvedValueOnce({ insights: [insight], runs: [] })
    .mockImplementationOnce(failed)
    .mockResolvedValue({ insights: [insight], runs: [] });
  const { result } = renderHook(() => ({
    boards: useDashboards(session),
    insights: useInsights(session),
  }));
  await act(() => vi.advanceTimersByTimeAsync(0));
  const shown = () => ({
    list: result.current.boards.list.map((d) => d.id),
    cards: result.current.boards.cards,
    insights: result.current.insights.insights.map((i) => i.id),
  });
  const before = shown();
  expect(before).toEqual({
    list: [board.id],
    cards: { main: board.id },
    insights: [insight.id],
  });
  // The app's server restarts for a deploy just as they are read again.
  await act(async () => {
    await result.current.boards.reload();
    await result.current.insights.reload();
  });
  expect(shown()).toEqual(before);
  await act(() => vi.advanceTimersByTimeAsync(14_999));
  expect(session.dashboards).toHaveBeenCalledTimes(2);
  expect(session.insights).toHaveBeenCalledTimes(2);
  await act(() => vi.advanceTimersByTimeAsync(1));
  expect(session.dashboards).toHaveBeenCalledTimes(3);
  expect(session.insights).toHaveBeenCalledTimes(3);
  // The list came back but the corners' cards didn't: they stay as they were.
  expect(shown()).toEqual(before);
  // Once read, nothing more is tried before the regular read.
  await act(() => vi.advanceTimersByTimeAsync(300_000));
  expect(session.dashboards).toHaveBeenCalledTimes(3);
  expect(session.insights).toHaveBeenCalledTimes(3);
});
