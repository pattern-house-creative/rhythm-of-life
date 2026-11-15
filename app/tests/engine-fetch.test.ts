import { afterEach, expect, it, vi } from "vitest";
import { engineFetch } from "../server/engineFetch";
import { callTool, documentFile, resetEngine } from "../server/engine";
import type { AppTool } from "../server/engine";
import { createApiSession } from "../src/apiClient";
import { rpc, withEngine } from "./engine";

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});
// Built at runtime, so no file holds a token-shaped value.
const secret = ["fictional", "secret"].join("-");
const init = {
  method: "POST",
  headers: { Authorization: `Bearer ${secret}` },
};
const url = "http://engine.example.invalid:8080/mcp";

it("retries one timed-out read with a fresh signal and the same request", async () => {
  const fetcher = vi
    .fn<typeof fetch>()
    .mockRejectedValueOnce(
      new DOMException("private upstream detail", "TimeoutError"),
    )
    .mockResolvedValueOnce(new Response("{}"));
  const result = await engineFetch("app_graph", url, init, 8000, true, fetcher);
  expect(result.ok).toBe(true);
  expect(fetcher).toHaveBeenCalledTimes(2);
  expect(fetcher.mock.calls[0][1]?.signal).not.toBe(
    fetcher.mock.calls[1][1]?.signal,
  );
  expect(fetcher.mock.calls[1][1]?.headers).toEqual(init.headers);
});

it("bounds retries and logs only fixed categories, never engine details or credentials", async () => {
  const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
  const fetcher = vi
    .fn<typeof fetch>()
    .mockRejectedValue(new TypeError(`${secret}: private payload`));
  await expect(
    engineFetch("app_graph", url, init, 8000, true, fetcher),
  ).rejects.toThrow();
  expect(fetcher).toHaveBeenCalledTimes(2);
  expect(warn).toHaveBeenCalledWith("rhythm-engine-transport", {
    call: "app_graph",
    category: "network",
    attempts: 2,
    elapsedMs: expect.any(Number),
    codes: [],
  });
  expect(JSON.stringify(warn.mock.calls)).not.toMatch(
    /fictional-secret|private payload|example\.invalid/,
  );
});

it("does not retry a denial, a cancelled request or a write", async () => {
  for (const status of [401, 403]) {
    const fetcher = vi
      .fn<typeof fetch>()
      .mockResolvedValue(new Response("", { status }));
    expect(
      (await engineFetch("app_graph", url, init, 8000, true, fetcher)).status,
    ).toBe(status);
    expect(fetcher).toHaveBeenCalledTimes(1);
  }
  vi.spyOn(console, "warn").mockImplementation(() => {});
  const controller = new AbortController();
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async () => {
    controller.abort();
    throw new DOMException("aborted", "AbortError");
  });
  await expect(
    engineFetch(
      "app_graph",
      url,
      { ...init, signal: controller.signal },
      8000,
      true,
      fetcher,
    ),
  ).rejects.toThrow();
  expect(fetcher).toHaveBeenCalledTimes(1);
  const write = vi
    .fn<typeof fetch>()
    .mockRejectedValue(new TypeError("network"));
  await expect(
    engineFetch("app_insight_feedback", url, init, 8000, false, write),
  ).rejects.toThrow();
  expect(write).toHaveBeenCalledTimes(1);
});

it("names the kind of failure from allowlisted transport codes in nested errors only", async () => {
  const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
  const failing = (error: unknown) =>
    engineFetch(
      "app_insights",
      url,
      init,
      8000,
      false,
      vi.fn<typeof fetch>().mockRejectedValue(error),
    ).catch(() => {});
  const coded = (message: string, code: string) =>
    Object.assign(new Error(message), { code });
  await failing(
    new TypeError("private endpoint", {
      cause: new AggregateError([
        coded("private address", "UND_ERR_CONNECT_TIMEOUT"),
        coded("private token", secret),
      ]),
    }),
  );
  await failing(
    new TypeError("fetch failed", { cause: coded("x", "ECONNREFUSED") }),
  );
  await failing(
    new TypeError("fetch failed", { cause: coded("x", "ENOTFOUND") }),
  );
  await failing(
    new TypeError("fetch failed", { cause: coded("x", "CERT_HAS_EXPIRED") }),
  );
  expect(warn.mock.calls.map((call) => call[1])).toMatchObject([
    { category: "timeout", codes: ["UND_ERR_CONNECT_TIMEOUT"] },
    { category: "refused", codes: ["ECONNREFUSED"] },
    { category: "dns", codes: ["ENOTFOUND"] },
    { category: "tls", codes: ["CERT_HAS_EXPIRED"] },
  ]);
  expect(JSON.stringify(warn.mock.calls)).not.toMatch(
    /private|fictional-secret|example\.invalid/,
  );
});

it("retries a gateway's 502, 503 or 504 but never a successful empty answer", async () => {
  for (const status of [502, 503, 504]) {
    const fetcher = vi
      .fn<typeof fetch>()
      .mockResolvedValueOnce(new Response("", { status }))
      .mockResolvedValueOnce(new Response("{}"));
    expect(
      (await engineFetch("app_recent_glucose", url, init, 8000, true, fetcher))
        .ok,
    ).toBe(true);
    expect(fetcher).toHaveBeenCalledTimes(2);
  }
  const fetcher = vi.fn<typeof fetch>().mockResolvedValue(new Response("{}"));
  await engineFetch("app_recent_glucose", url, init, 8000, true, fetcher);
  expect(fetcher).toHaveBeenCalledTimes(1);
});

it("keeps a retry within the call's total time", async () => {
  let clock = 0;
  vi.spyOn(Date, "now").mockImplementation(() => clock);
  const timeout = vi
    .spyOn(AbortSignal, "timeout")
    .mockImplementation(() => new AbortController().signal);
  const fetcher = vi
    .fn<typeof fetch>()
    .mockImplementationOnce(async () => {
      clock = 3500;
      throw new TypeError("connection failed");
    })
    .mockResolvedValueOnce(new Response("{}"));
  await engineFetch("app_dashboards", url, init, 25000, true, fetcher, 25000);
  expect(timeout.mock.calls.map((call) => call[0])).toEqual([25000, 21500]);
});

it("counts the tools/list check against a call's total time", async () => {
  withEngine();
  resetEngine();
  let clock = 0;
  vi.spyOn(Date, "now").mockImplementation(() => clock);
  vi.spyOn(console, "warn").mockImplementation(() => {});
  const timeout = vi
    .spyOn(AbortSignal, "timeout")
    .mockImplementation(() => new AbortController().signal);
  const fetcher = vi
    .fn<typeof fetch>()
    .mockImplementationOnce(async () => {
      // The listing times out once and is not retried; the call still goes ahead.
      clock = 8000;
      throw new DOMException("timeout", "TimeoutError");
    })
    .mockImplementationOnce(async () => {
      clock = 12000;
      throw new TypeError("connection failed");
    })
    .mockResolvedValueOnce(rpc({}));
  await callTool("app_latest_glucose", {}, undefined, fetcher);
  expect(fetcher).toHaveBeenCalledTimes(3);
  expect(timeout.mock.calls.map((call) => call[0])).toEqual([8000, 8000, 4000]);
});

it("retries the app's reads but never a change, feedback or request", async () => {
  withEngine();
  vi.spyOn(console, "warn").mockImplementation(() => {});
  const cases: [AppTool, Record<string, unknown>, boolean][] = [
    ["app_graph", {}, true],
    ["app_graph", { operation: "get", value: "fictional" }, true],
    ["app_insights", { limit: 50 }, true],
    ["app_latest_glucose", {}, true],
    ["app_recent_glucose", { days: 14 }, true],
    ["app_garmin_summary", {}, true],
    ["app_dashboards", {}, true],
    ["app_dashboards", { pins: true }, true],
    ["app_dashboards", { cards: true }, true],
    ["app_dashboards", { id: "fictional", version: 1, data: true }, true],
    ["app_dashboards", { pin: "fictional", pinned: true }, false],
    ["app_dashboards", { corner: "training", dashboard: "fictional" }, false],
    [
      "app_dashboards",
      { id: "fictional", version: 1, allowEdits: true },
      false,
    ],
    ["app_dashboards", { id: "fictional", version: 1, write: {} }, false],
    ["app_dashboards", { undo: "fictional" }, false],
    ["app_insight_feedback", { id: "fictional" }, false],
    ["app_request_refresh", {}, false],
    ["app_request_garmin_sync", {}, false],
    ["app_upload_document", { name: "a.txt", content: "YQ==" }, false],
    // A listen claims a command, a send stores one, so neither is ever sent twice.
    ["app_screens", { op: "listen", screen: "fictional", name: "Hall" }, false],
    [
      "app_screens",
      { op: "send", screen: "fictional", to: "x", action: "home" },
      false,
    ],
    ["app_screens", { op: "list", screen: "fictional" }, false],
  ];
  for (const [tool, args, retry] of cases) {
    const fetcher = vi
      .fn<typeof fetch>()
      .mockRejectedValueOnce(new TypeError("network"))
      .mockResolvedValueOnce(rpc({}));
    const call = callTool(tool, args, undefined, fetcher);
    if (retry) await expect(call).resolves.toEqual({});
    else await expect(call).rejects.toThrow();
    expect(fetcher).toHaveBeenCalledTimes(retry ? 2 : 1);
  }
});

it("reads a document's file once more after a gateway's outage", async () => {
  withEngine();
  const fetcher = vi
    .fn<typeof fetch>()
    .mockResolvedValueOnce(new Response("", { status: 502 }))
    .mockResolvedValueOnce(new Response("fictional text"));
  const id = "3f2b8c1e-4d5a-4b6c-9e7f-0a1b2c3d4e5f";
  expect(await (await documentFile(id, undefined, fetcher)).text()).toBe(
    "fictional text",
  );
  expect(fetcher).toHaveBeenCalledTimes(2);
});

it("lets slow dashboard lists and pins finish within the server's 25-second total", async () => {
  vi.useFakeTimers();
  vi.spyOn(AbortSignal, "timeout").mockImplementation((ms) => {
    const controller = new AbortController();
    setTimeout(
      () => controller.abort(new DOMException("timeout", "TimeoutError")),
      ms,
    );
    return controller.signal;
  });
  const signals: AbortSignal[] = [];
  vi.mocked(fetch).mockImplementation(async (path, options) => {
    signals.push(options!.signal!);
    await new Promise((resolve) => setTimeout(resolve, 26000));
    return new Response(
      JSON.stringify(
        String(path).includes("pins") ? { pins: [] } : { dashboards: [] },
      ),
      { headers: { "content-type": "application/json" } },
    );
  });
  const session = createApiSession(false);
  const reads = Promise.all([session.dashboards!(), session.pins!()]);
  await vi.advanceTimersByTimeAsync(26000);
  expect(signals.every((s) => !s.aborted)).toBe(true);
  await expect(reads).resolves.toEqual([[], []]);
});
