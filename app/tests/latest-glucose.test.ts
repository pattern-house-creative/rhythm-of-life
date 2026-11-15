import { describe, it, expect, vi } from "vitest";
import { mapLiveGlucose } from "../server/glucose";
import { loadHealth } from "../server/health";
import { assertObservatory } from "../src/validation";
import { resetEngine } from "../server/engine";
import { appToken, engineUrl, rpc, withEngine } from "./engine";

// Synthetic only: one fictional reading for the fictional person alex.
const now = "2026-10-26T20:00:00Z";
const subject = "alex";
const row = {
  subject_id: subject,
  provider: "carelink",
  metric: "glucose",
  source_kind: "carelink-live-day",
  unit: "mg/dL",
  value: 121,
  resource_id: "fictional-resource",
  sample_key: "sample-1",
  quality: "source_timezone=UTC",
  observed_at: "2026-10-26T19:55:00Z",
  total_samples: 1,
};
const payload = { selection: "latest", complete: true, subject, rows: [row] };
const read = (fetcher: typeof fetch) =>
  loadHealth(undefined, fetcher, () => now);

describe("latest glucose through the engine (synthetic only)", () => {
  it("only sends the fixed, argument-free read and preserves provenance without inventing history", async () => {
    withEngine();
    const fetcher = vi.fn().mockResolvedValue(rpc(payload));
    const data = await read(fetcher);
    assertObservatory(data);
    expect(data.current.value).toBe(121);
    expect(data.current.provenance).toMatchObject({
      sourceId: "carelink",
      observedAt: row.observed_at,
      retrievedAt: now,
      demo: false,
    });
    expect(data.glucose.samples).toEqual([]);
    expect(data.glucoseHistory).toBeNull();
    expect(data.steps.value).toBeNull();
    // The glucose slot is named after the reading's provider.
    expect(data.sources.find((s) => s.id === "glucose")).toMatchObject({
      name: "CareLink",
      connected: true,
      status: "available",
    });
    const [url, init] = fetcher.mock.calls[0];
    expect(String(url)).toBe(`${engineUrl}/mcp`);
    expect(init).toMatchObject({
      method: "POST",
      redirect: "error",
      cache: "no-store",
    });
    expect(init.headers).toMatchObject({
      Authorization: `Bearer ${appToken}`,
      "MCP-Protocol-Version": "2025-03-26",
    });
    expect(init.headers).not.toHaveProperty("Cookie");
    expect(init.headers).not.toHaveProperty("Origin");
    expect(JSON.parse(init.body)).toEqual({
      jsonrpc: "2.0",
      id: "rhythm-app",
      method: "tools/call",
      params: { name: "app_latest_glucose", arguments: {} },
    });
  });
  it("names the reading's own provider and reads the older metric name", async () => {
    withEngine();
    const data = await read(
      vi.fn().mockResolvedValue(
        rpc({
          ...payload,
          rows: [{ ...row, provider: "nightscout", metric: "sensor_glucose" }],
        }),
      ),
    );
    expect(data.current.value).toBe(121);
    expect(data.current.provenance.sourceId).toBe("nightscout");
  });
  it("shows the engine's fictional seed labelled demo, and only that, for a subject outside people", async () => {
    withEngine();
    const seed = {
      ...row,
      subject_id: "fixture",
      provider: "demo",
      source_kind: "demo-readings",
      demo: true,
    };
    const data = await read(
      vi
        .fn()
        .mockResolvedValue(
          rpc({ ...payload, subject: "fixture", rows: [seed] }),
        ),
    );
    assertObservatory(data);
    expect(data.current.value).toBe(121);
    expect(data.current.provenance).toMatchObject({
      sourceId: "demo",
      demo: true,
    });
    // A reading that is not labelled demo still needs a person from the settings.
    const refused = await read(
      vi.fn().mockResolvedValue(
        rpc({
          ...payload,
          subject: "fixture",
          rows: [{ ...seed, demo: false }],
        }),
      ),
    );
    expect(refused.current.value).toBeNull();
    expect(refused.current.status).toBe("unavailable");
  });
  it("fails closed for refused credentials, outage, redirects, HTML, malformed and oversized responses", async () => {
    withEngine();
    for (const code of [301, 401, 403, 429, 500, 503]) {
      const data = await read(
        vi
          .fn()
          .mockResolvedValue(new Response("private body", { status: code })),
      );
      expect(data.current).toMatchObject({
        value: null,
        status: "unavailable",
      });
      expect(data.current.provenance.demo).toBe(false);
      expect(JSON.stringify(data)).not.toContain("private body");
    }
    for (const value of [
      new Response("login", { headers: { "content-type": "text/html" } }),
      new Response("x".repeat(2_000_001), {
        headers: { "content-type": "application/json" },
      }),
      rpc({ ...payload, complete: false }),
      rpc({ ...payload, subject: "jo" }),
      rpc({ ...payload, rows: [row, row] }),
      rpc({ error: { code: "unavailable", message: "fictional" } }, true),
      new Response("{", { headers: { "content-type": "application/json" } }),
    ])
      expect(
        (await read(vi.fn().mockResolvedValue(value))).current.value,
      ).toBeNull();
    expect(
      (await read(vi.fn().mockRejectedValue(new Error("secret")))).current
        .value,
    ).toBeNull();
  });
  it("distinguishes missing, stale and invalid observations", async () => {
    withEngine();
    const rows = (r: unknown[]) =>
      read(vi.fn().mockResolvedValue(rpc({ ...payload, rows: r })));
    expect((await rows([])).current.status).toBe("missing");
    expect(
      (await rows([{ ...row, observed_at: "2026-10-26T19:39:00Z" }])).current
        .status,
    ).toBe("stale");
    for (const patch of [
      { observed_at: "2026-10-26T20:10:00Z" },
      { observed_at: "2026-10-26 19:55:00" },
      { source_kind: "carelink-csv" },
      { source_kind: "archive" },
      { metric: "blood_glucose" },
      { provider: "Not A Provider" },
      { resource_id: "" },
      { value: -1 },
    ])
      expect((await rows([{ ...row, ...patch }])).current.value).toBeNull();
  });
  it("treats equivalent instants as the same time and rejects conflicting readings", () => {
    const rows = [
      { ...row, total_samples: 2 },
      {
        ...row,
        observed_at: "2026-10-26T20:55:00+01:00",
        value: 140,
        total_samples: 2,
      },
    ];
    expect(mapLiveGlucose(rows, subject, now).status).toBe("unavailable");
    expect(mapLiveGlucose([...rows].reverse(), subject, now).value).toBeNull();
  });
  // The engine lists the glucose tools only once a glucose source has data (contracts/README.md 1.4 and 2.2), which a plugin's
  // first reading changes with no setting changed and no restart, as a CareLink-style plugin's does. A listing without them is
  // asked again at the next glucose read (at most every ten seconds), so that reading reaches Home at the next read, a minute
  // on, rather than at the next five-minute listing; a listing that names them keeps its five minutes.
  it("shows a source's first reading at the next read, though the listing before it named no glucose tool", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    try {
      vi.setSystemTime(new Date("2026-10-26T20:00:00Z"));
      withEngine();
      resetEngine();
      let listed = ["app_graph", "app_insights"];
      const fetcher = vi.fn(async (_url: unknown, init: RequestInit) => {
        const body = JSON.parse(String(init.body));
        return body.method === "tools/list"
          ? new Response(
              JSON.stringify({
                jsonrpc: "2.0",
                id: body.id,
                result: { tools: listed.map((name) => ({ name })) },
              }),
              { headers: { "content-type": "application/json" } },
            )
          : rpc(payload);
      });
      const asked = () =>
        fetcher.mock.calls.map(
          ([, init]) => JSON.parse(String(init.body)).method,
        );
      const glucose = (data: Awaited<ReturnType<typeof read>>) =>
        data.sources.find((s) => s.id === "glucose");
      // No glucose yet: not connected, and nothing asked for it.
      expect(glucose(await read(fetcher))).toMatchObject({
        connected: false,
        status: "disconnected",
      });
      expect(asked()).toEqual(["tools/list"]);
      // Two reads within ten seconds ask once.
      vi.setSystemTime(new Date("2026-10-26T20:00:05Z"));
      await read(fetcher);
      expect(asked()).toEqual(["tools/list"]);
      // The plugin's first reading; Home reads again a minute on, as it does every minute.
      listed = [...listed, "app_latest_glucose", "app_recent_glucose"];
      vi.setSystemTime(new Date("2026-10-26T20:01:00Z"));
      const first = await read(fetcher);
      expect(first.current.value).toBe(121);
      expect(glucose(first)).toMatchObject({
        connected: true,
        status: "available",
      });
      expect(asked()).toEqual(["tools/list", "tools/list", "tools/call"]);
      // Listed now, the listing keeps its five minutes.
      vi.setSystemTime(new Date("2026-10-26T20:02:00Z"));
      expect((await read(fetcher)).current.value).toBe(121);
      expect(asked()).toEqual([
        "tools/list",
        "tools/list",
        "tools/call",
        "tools/call",
      ]);
    } finally {
      vi.useRealTimers();
    }
  });
  it("propagates caller cancellation without leaking errors", async () => {
    withEngine();
    const controller = new AbortController();
    const fetcher = vi.fn(
      (_url, init: RequestInit) =>
        new Promise<Response>((_resolve, reject) => {
          init.signal!.addEventListener("abort", () =>
            reject(new Error("aborted")),
          );
        }),
    );
    const pending = loadHealth(controller.signal, fetcher, () => now);
    await vi.waitFor(() => expect(fetcher).toHaveBeenCalled());
    controller.abort();
    expect((await pending).current.value).toBeNull();
    expect(fetcher.mock.calls[0][1].signal?.aborted).toBe(true);
  });
});
