import { afterEach, expect, it, vi } from "vitest";
import {
  healthSubject,
  instance,
  instanceTimeZone,
  keepReading,
  loadInstance,
} from "../src/instance";

afterEach(() => {
  vi.useRealTimers();
});
const live = {
  name: "Test home",
  people: [
    { id: "sam", name: "Sam" },
    { id: "alex", name: "Alex" },
  ],
  timezone: "Europe/London",
  mode: "live",
};
const answer = (value: unknown) =>
  new Response(JSON.stringify(value), {
    headers: { "content-type": "application/json" },
  });

it("answers whether the settings were read, keeping the ones in use when not", async () => {
  vi.mocked(fetch).mockRejectedValueOnce(new TypeError("Failed to fetch"));
  expect(await loadInstance()).toBe(false);
  expect(instance().mode).toBe("demo");
  vi.mocked(fetch).mockResolvedValueOnce(answer({ name: "" }));
  expect(await loadInstance()).toBe(false);
  vi.mocked(fetch).mockResolvedValueOnce(answer(live));
  expect(await loadInstance()).toBe(true);
  expect(instanceTimeZone()).toBe("Europe/London");
  expect(healthSubject()).toBe("sam");
});

it("reads the settings again after a failed read at start, until they are read", async () => {
  vi.useFakeTimers();
  vi.mocked(fetch).mockRejectedValue(new TypeError("Failed to fetch"));
  expect(await loadInstance()).toBe(false);
  const changed = vi.fn();
  const stop = keepReading(changed, { first: 5000, longest: 20_000 });
  // Again after 5 s, then 10 s, then 20 s, and never longer apart.
  await vi.advanceTimersByTimeAsync(4999);
  expect(fetch).toHaveBeenCalledTimes(1);
  await vi.advanceTimersByTimeAsync(1);
  expect(fetch).toHaveBeenCalledTimes(2);
  await vi.advanceTimersByTimeAsync(10_000);
  expect(fetch).toHaveBeenCalledTimes(3);
  await vi.advanceTimersByTimeAsync(20_000);
  expect(fetch).toHaveBeenCalledTimes(4);
  await vi.advanceTimersByTimeAsync(20_000);
  expect(fetch).toHaveBeenCalledTimes(5);
  expect(changed).not.toHaveBeenCalled();
  // The network coming back reads them at once; they differ, so the app draws again.
  vi.mocked(fetch).mockResolvedValue(answer(live));
  window.dispatchEvent(new Event("online"));
  await vi.advanceTimersByTimeAsync(0);
  expect(fetch).toHaveBeenCalledTimes(6);
  expect(changed).toHaveBeenCalledOnce();
  expect(instance().mode).toBe("live");
  expect(instanceTimeZone()).toBe("Europe/London");
  // Read once, they are never read again.
  window.dispatchEvent(new Event("online"));
  await vi.advanceTimersByTimeAsync(600_000);
  expect(fetch).toHaveBeenCalledTimes(6);
  stop();
});

it("draws nothing again when the late read matches the settings in use", async () => {
  vi.useFakeTimers();
  vi.mocked(fetch).mockResolvedValue(answer({ ...instance() }));
  const changed = vi.fn();
  keepReading(changed, { first: 1000 });
  await vi.advanceTimersByTimeAsync(1000);
  expect(fetch).toHaveBeenCalledTimes(1);
  expect(changed).not.toHaveBeenCalled();
});

it("waits for a hidden page to come back before reading", async () => {
  vi.useFakeTimers();
  const hidden = vi.spyOn(document, "hidden", "get").mockReturnValue(true);
  vi.mocked(fetch).mockResolvedValue(answer(live));
  const changed = vi.fn();
  keepReading(changed, { first: 1000 });
  await vi.advanceTimersByTimeAsync(60_000);
  expect(fetch).not.toHaveBeenCalled();
  hidden.mockReturnValue(false);
  document.dispatchEvent(new Event("visibilitychange"));
  await vi.advanceTimersByTimeAsync(0);
  expect(changed).toHaveBeenCalledOnce();
  hidden.mockRestore();
});
