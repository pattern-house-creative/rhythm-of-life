import { expect, it, vi } from "vitest";
import { GET } from "../api/observatory";
import { closedEngines, rpc, withEngine } from "./engine";
import { resetEngine } from "../server/engine";
const url = "http://rhythm.example.invalid/api/observatory";

it("serves the demo without an engine and never calls one after its credential expires", async () => {
  const demo = await (await GET(new Request(url))).json();
  expect(demo.current.provenance.demo).toBe(true);
  for (const [name, close] of closedEngines) {
    close();
    const data = await (await GET(new Request(url))).json();
    if (name === "an expired credential") {
      // Configured but expired: honest and unavailable, never the demo.
      expect(data.current.value).toBeNull();
      expect(data.current.provenance.demo).toBe(false);
      expect(
        data.sources.find((s: { id: string }) => s.id === "glucose").detail,
      ).toBe("Unavailable: credential_expired");
    } else expect(data.current.provenance.demo).toBe(true);
  }
  expect(fetch).not.toHaveBeenCalled();
});

it("says Not connected when no glucose source is on, and asks nothing", async () => {
  withEngine();
  resetEngine(["app_graph", "app_insights", "app_dashboards"]);
  const data = await (await GET(new Request(url))).json();
  expect(data.current.value).toBeNull();
  const glucose = data.sources.find((s: { id: string }) => s.id === "glucose");
  expect(glucose).toMatchObject({ connected: false, status: "disconnected" });
  expect(glucose.detail).toBeUndefined();
  expect(fetch).not.toHaveBeenCalled();
});

it("returns no-store typed unavailable for refused credentials, outages and other people's data", async () => {
  withEngine();
  for (const code of [401, 403, 502, 503]) {
    vi.mocked(fetch)
      .mockClear()
      .mockImplementation(
        async () => new Response("withheld", { status: code }),
      );
    const answer = await GET(new Request(url));
    expect(answer.headers.get("cache-control")).toContain("no-store");
    const data = await answer.json();
    expect(data.current.value).toBeNull();
    expect(data.current.status).toBe("unavailable");
    expect(data.recovery.status).toBe("unavailable");
    const glucose = data.sources.find(
      (s: { id: string }) => s.id === "glucose",
    );
    expect(glucose.connected).toBe(false);
    expect(glucose.status).toBe("unavailable");
    // A gateway's outage is read once more; a refusal never is.
    expect(fetch).toHaveBeenCalledTimes(code >= 500 ? 2 : 1);
  }
  // A subject that is not in the settings is refused, whatever its readings.
  vi.mocked(fetch).mockResolvedValueOnce(
    rpc({ complete: true, selection: "latest", subject: "jo", rows: [] }),
  );
  const data = await (await GET(new Request(url))).json();
  expect(data.current.value).toBeNull();
  expect(data.current.provenance.demo).toBe(false);
});
