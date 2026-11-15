import { afterEach, expect, it, vi } from "vitest";
import { readDeadline } from "../src/readDeadline";
afterEach(() => vi.useRealTimers());
const pending = (signal: AbortSignal) =>
  new Promise((_, reject) => {
    if (signal.aborted) reject(signal.reason);
    else
      signal.addEventListener("abort", () => reject(signal.reason), {
        once: true,
      });
  });
it("cancels reads with their parent's reason, without AbortSignal.any", async () => {
  const any = vi.spyOn(AbortSignal, "any").mockImplementation(() => {
    throw new Error("Unsupported WebView API");
  });
  try {
    const controller = new AbortController();
    const reading = readDeadline(controller.signal, 30000, pending);
    const rejected = expect(reading).rejects.toBe("left screen");
    controller.abort("left screen");
    await rejected;
    expect(any).not.toHaveBeenCalled();
  } finally {
    any.mockRestore();
  }
});
it("bounds an unresponsive read and removes finished timers", async () => {
  vi.useFakeTimers();
  const reading = readDeadline(undefined, 1000, pending);
  const rejected = expect(reading).rejects.toMatchObject({
    name: "TimeoutError",
  });
  await vi.advanceTimersByTimeAsync(1000);
  await rejected;
  expect(await readDeadline(undefined, 1000, async () => "done")).toBe("done");
  expect(vi.getTimerCount()).toBe(0);
});
