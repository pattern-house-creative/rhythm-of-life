import { act, render, screen } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { unavailableHealth } from "../server/healthState";
import { GlucosePart } from "../src/parts/GlucosePart";
import BuiltinGlucosePreview from "../src/BuiltinGlucosePreview";
import { loadObservatory } from "../src/apiClient";
vi.mock("../src/apiClient", () => ({ loadObservatory: vi.fn() }));
const now = Date.parse("2026-11-14T01:00:00Z");
function fixture(age = 2, unit = "mg/dL", value = 123) {
  const data = unavailableHealth(new Date(now).toISOString());
  data.healthMode = "live";
  data.current = {
    ...data.current,
    value,
    unit,
    status: "available",
    provenance: {
      ...data.current.provenance,
      observedAt: new Date(now - age * 60_000).toISOString(),
      sourceId: "fixture",
    },
  };
  return data;
}
afterEach(() => {
  vi.useRealTimers();
  vi.mocked(loadObservatory).mockReset();
  vi.restoreAllMocks();
});
const view = (data = fixture(), clock = now) => (
  <GlucosePart
    data={data}
    state="ready"
    blocked={false}
    onOpen={() => {}}
    launcherNow={clock}
  />
);
it.each([
  ["mg/dL", 123],
  ["mmol/L", 6.8],
] as const)("uses the actual %s value and unit with its age", (unit, value) => {
  render(view(fixture(2, unit, value)));
  expect(screen.getByText(String(value))).toBeInTheDocument();
  expect(screen.getByText(unit)).toBeInTheDocument();
  expect(screen.getByText("2 min ago")).toBeInTheDocument();
  expect(screen.getByText("Latest reading")).toBeInTheDocument();
});
it("marks a retained old reading stale and never calls it current", () => {
  render(view(fixture(31)));
  expect(screen.getByText("Stale reading")).toBeInTheDocument();
  expect(screen.getByText("31 min ago")).toBeInTheDocument();
  expect(screen.queryByText("Latest reading")).not.toBeInTheDocument();
});
it.each(["missing", "unavailable"] as const)(
  "withholds any residual value for %s",
  (status) => {
    const data = fixture();
    data.current.status = status;
    render(view(data));
    expect(screen.queryByText("123")).not.toBeInTheDocument();
    expect(screen.queryByText("mg/dL")).not.toBeInTheDocument();
    expect(
      screen.getByText(
        status === "missing" ? "No reading" : "Source unavailable",
      ),
    ).toBeInTheDocument();
  },
);
it("does not show a future or untimed observation as current", () => {
  const { rerender } = render(view(fixture(-10)));
  expect(screen.queryByText("123")).not.toBeInTheDocument();
  const data = fixture();
  data.current.provenance.observedAt = null;
  rerender(view(data));
  expect(screen.queryByText("123")).not.toBeInTheDocument();
});
it("ages between reads, clears failed refreshes, recovers and aborts on unmount", async () => {
  vi.useFakeTimers();
  vi.setSystemTime(now);
  let reads = 0;
  vi.mocked(loadObservatory).mockImplementation(async (_signal, source) => {
    if (source === "history") return fixture();
    reads++;
    if (reads === 2) throw Error("offline");
    return reads === 1 ? fixture(19.9) : fixture();
  });
  const { unmount } = render(<BuiltinGlucosePreview />);
  await act(async () => {});
  expect(screen.getByText("123")).toBeInTheDocument();
  await act(async () => {
    await vi.advanceTimersByTimeAsync(15_000);
  });
  expect(screen.getByText("Stale reading")).toBeInTheDocument();
  await act(async () => {
    await vi.advanceTimersByTimeAsync(45_000);
  });
  expect(screen.queryByText("123")).not.toBeInTheDocument();
  expect(screen.getByText(/Glucose unavailable/)).toBeInTheDocument();
  await act(async () => {
    await vi.advanceTimersByTimeAsync(60_000);
  });
  expect(screen.getByText("123")).toBeInTheDocument();
  const signal = vi.mocked(loadObservatory).mock.calls.at(-1)![0]!;
  unmount();
  expect(signal.aborted).toBe(true);
});
it("refuses demo readings on the connected built-in surface", async () => {
  const data = fixture();
  data.current.provenance.demo = true;
  vi.mocked(loadObservatory).mockResolvedValue(data);
  render(<BuiltinGlucosePreview />);
  await act(async () => {});
  expect(screen.queryByText("123")).not.toBeInTheDocument();
  expect(screen.getByText(/Glucose unavailable/)).toBeInTheDocument();
});

it("labels an empty value missing and an archive explicitly", () => {
  const data = fixture();
  data.current.value = null;
  const { rerender } = render(view(data));
  expect(screen.getByText("No reading")).toBeInTheDocument();
  const archive = fixture();
  archive.current.provenance.kind = "archive";
  rerender(view(archive));
  expect(screen.getByText("Archived reading")).toBeInTheDocument();
});

it("shows the reading before slow history and keeps it when history fails", async () => {
  let fail!: (e: Error) => void;
  vi.mocked(loadObservatory).mockImplementation(async (_signal, source) => {
    if (source === "history")
      return new Promise((_resolve, reject) => {
        fail = reject;
      });
    return fixture();
  });
  render(<BuiltinGlucosePreview />);
  await act(async () => {});
  expect(screen.getByText("123")).toBeInTheDocument();
  await act(async () => fail(Error("history unavailable")));
  expect(screen.getByText("123")).toBeInTheDocument();
  expect(screen.getByText("Graph unavailable")).toBeInTheDocument();
});

it("clears on offline, aborts outstanding reads and rejects late results before reconnecting", async () => {
  const online = vi.spyOn(navigator, "onLine", "get").mockReturnValue(true);
  let finish!: (d: ReturnType<typeof fixture>) => void;
  vi.mocked(loadObservatory).mockImplementation(async (_signal, source) => {
    if (source === "history")
      return new Promise((resolve) => {
        finish = resolve;
      });
    return fixture();
  });
  render(<BuiltinGlucosePreview />);
  await act(async () => {});
  const signal = vi.mocked(loadObservatory).mock.calls[0][0]!;
  online.mockReturnValue(false);
  await act(async () => window.dispatchEvent(new Event("offline")));
  expect(signal.aborted).toBe(true);
  expect(screen.queryByText("123")).not.toBeInTheDocument();
  await act(async () => finish(fixture()));
  expect(screen.queryByText("123")).not.toBeInTheDocument();
  vi.mocked(loadObservatory).mockResolvedValue(fixture());
  online.mockReturnValue(true);
  await act(async () => window.dispatchEvent(new Event("online")));
  expect(screen.getByText("123")).toBeInTheDocument();
});
