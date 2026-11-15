import { afterEach, describe, expect, it, vi } from "vitest";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import App from "../src/App";
import { sampleHealth, withSampleHealth } from "./sampleHealth";
import { healthStatus, previewHealth } from "../src/health";
import { assertObservatory } from "../src/validation";
import { loadHealth, unavailableHealth } from "../server/health";
import { mapLiveGlucose } from "../server/glucose";
import { GET } from "../api/observatory";
import { closedEngines, withEngine } from "./engine";

afterEach(() => vi.unstubAllEnvs());
const now = "2026-10-26T17:00:00Z";
const row = {
  subject_id: "fictional-subject",
  provider: "carelink",
  metric: "sensor_glucose",
  source_kind: "carelink-live-day",
  unit: "mg/dL",
  value: 123,
  resource_id: "fictional-resource",
  sample_key: "fictional-sample",
  quality: "source_timezone=UTC",
  observed_at: "2026-10-26T16:55:00Z",
  total_samples: 1,
};

describe("real-capable health contract using fictional inputs", () => {
  it("preserves missing, stale, unavailable, observed and retrieved timestamps separately", () => {
    const data = unavailableHealth(now);
    assertObservatory(data);
    expect(data.current.value).toBeNull();
    expect(data.current.provenance.observedAt).toBeNull();
    expect(data.current.provenance.retrievedAt).toBe(now);
    const metric = mapLiveGlucose([row], row.subject_id, now);
    expect(metric.value).toBe(123);
    expect(metric.status).toBe("available");
    expect(healthStatus(metric, Date.parse(now) + 21 * 60000)).toBe("stale");
    expect(mapLiveGlucose([], row.subject_id, now).status).toBe("missing");
    expect(previewHealth(data, "ready")).toBe(data);
    expect(
      previewHealth(sampleHealth, "stale").current.provenance.observedAt,
    ).toBe(sampleHealth.current.provenance.observedAt);
  });
  it("never applies demo state overrides to a non-demo measurement, even with incorrect source badges", () => {
    const data = structuredClone(sampleHealth);
    data.current.provenance.demo = false;
    expect(previewHealth(data, "error")).toBe(data);
    expect(previewHealth(data, "stale")).toBe(data);
  });
  it("rejects archives, wrong subjects, bounds, future timestamps and incomplete ascending pages", () => {
    for (const patch of [
      { source_kind: "carelink-csv" },
      { subject_id: "other" },
      { observed_at: null },
      { observed_at: "2099-01-01T00:00:00Z" },
      { total_samples: 600 },
      { quality: "comparison=>" },
      { value: NaN },
    ]) {
      expect(
        mapLiveGlucose([{ ...row, ...patch }], row.subject_id, now).value,
      ).toBeNull();
    }
  });
  it("never substitutes demo measurements for an unreachable or expired engine", async () => {
    const expired = closedEngines.find(
      ([name]) => name === "an expired credential",
    )!;
    for (const configure of [() => void withEngine(), expired[1]]) {
      configure();
      const data = await loadHealth(
        undefined,
        vi.fn().mockRejectedValue(new Error("unreachable")),
      );
      assertObservatory(data);
      expect(data.current.value).toBeNull();
      expect(data.current.provenance.demo).toBe(false);
      expect(data.glucose.samples).toEqual([]);
      expect(data.glucoseHistory).toBeNull();
    }
    withEngine();
    const response = await GET(
      new Request("http://rhythm.example.invalid/api/observatory"),
    );
    expect(response.status).toBe(200);
    expect((await response.json()).current.value).toBeNull();
    expect(response.headers.get("cache-control")).toContain("no-store");
  });
  it("renders independently missing health without fake values, history or demo controls", async () => {
    withEngine();
    render(<App />);
    const glucose = await screen.findByRole("button", { name: /GLUCOSE —/ });
    expect(glucose).toHaveTextContent("Source unavailable");
    expect(glucose).not.toHaveTextContent("112");
    await userEvent.click(glucose);
    const dialog = await screen.findByRole("dialog", { name: "glucose" });
    expect(
      within(dialog).getByText(/no estimate inferred/),
    ).toBeInTheDocument();
    expect(
      within(dialog).queryByText(/Linked sample activity/),
    ).not.toBeInTheDocument();
  });
  it("clears visible data on access loss and revalidates after page restoration", async () => {
    withSampleHealth();
    render(<App />);
    await screen.findByRole("button", { name: /GLUCOSE 112/ });
    act(() => window.dispatchEvent(new Event("pagehide")));
    expect(
      screen.queryByRole("button", { name: /GLUCOSE 112/ }),
    ).not.toBeInTheDocument();
    vi.mocked(fetch).mockResolvedValue(
      new Response("Unauthorized", { status: 401 }),
    );
    act(() => window.dispatchEvent(new Event("pageshow")));
    await screen.findByText(/Access or source unavailable/);
    expect(
      screen.queryByRole("button", { name: /GLUCOSE 112/ }),
    ).not.toBeInTheDocument();
    await waitFor(() =>
      expect(fetch).toHaveBeenCalledWith(
        "/api/observatory",
        expect.objectContaining({
          cache: "no-store",
          credentials: "same-origin",
          redirect: "manual",
        }),
      ),
    );
  });
  it("comes back by itself once access works again, with no one touching the screen", async () => {
    withSampleHealth();
    const working = vi.mocked(fetch).getMockImplementation()!;
    render(<App />);
    await screen.findByRole("button", { name: /GLUCOSE 112/ });
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    try {
      vi.mocked(fetch).mockResolvedValue(
        new Response("Unauthorized", { status: 401 }),
      );
      act(() => window.dispatchEvent(new Event("observatory-access-lost")));
      expect(
        screen.getByText(/Access or source unavailable/),
      ).toBeInTheDocument();
      expect(
        screen.queryByRole("button", { name: "Refresh observations" }),
      ).not.toBeInTheDocument();
      // Access works again (a front door's session renewed, say).
      vi.mocked(fetch).mockImplementation(working);
      await act(() => vi.advanceTimersByTimeAsync(15_000));
    } finally {
      vi.useRealTimers();
    }
    expect(
      await screen.findByRole("button", { name: "Refresh observations" }),
    ).toBeInTheDocument();
    expect(
      screen.queryByText(/Access or source unavailable/),
    ).not.toBeInTheDocument();
    expect(screen.getByText(/10,482 sample steps/)).toBeInTheDocument();
  });
});
