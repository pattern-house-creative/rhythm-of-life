import { createServer } from "node:http";
import type { AddressInfo } from "node:net";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import {
  callTool,
  documentFile,
  listTools,
  resetEngine,
} from "../server/engine";
import { engineFetch } from "../server/engineFetch";
import { readRelationshipGraph } from "../server/relationshipGraph";
import { handleDashboards } from "../server/dashboards";
import { loadHealth } from "../server/health";
import { resetWarnings, warnOnce } from "../server/log";
import { appToken, rpc, withEngine } from "./engine";

// A failed engine call or an answer the facade refused leaves one log line with fixed fields, so
// an expired Access token, a refused host, a wrong app token and a contract mismatch can be told
// apart in the server's log. Never a URL, a header's value, a body or the token. Fictional only.
// Node's own fetch, before the test setup replaces it.
const nativeFetch = globalThis.fetch;
let warn: ReturnType<typeof vi.spyOn>;
beforeEach(() => {
  resetWarnings();
  warn = vi.spyOn(console, "warn").mockImplementation(() => {});
});
afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});
const lines = () =>
  warn.mock.calls.filter((call) => call[0] === "rhythm-engine-call");
const leaks = /fictional-app-token|private|example\.invalid/;

it("names a refused call by its status and the kind of answer, never the answer", async () => {
  withEngine();
  const answers: [Response, Record<string, unknown>][] = [
    // A front door's page: Cloudflare Access refusing the service token.
    [
      new Response("<html>private sign-in page</html>", {
        status: 403,
        headers: { "content-type": "text/html; charset=utf-8" },
      }),
      { reason: "forbidden", status: 403, answer: "html" },
    ],
    // The engine's own bodiless refusal of a host or origin.
    [
      new Response(null, { status: 403 }),
      { reason: "forbidden", status: 403, answer: "none" },
    ],
    // A wrong app token.
    [
      new Response('{"private":"detail"}', {
        status: 401,
        headers: { "content-type": "application/json" },
      }),
      { reason: "engine_auth", status: 401, answer: "json" },
    ],
    [
      new Response("private", { status: 500 }),
      { reason: "unavailable", status: 500, answer: "other" },
    ],
    // An answer that is not the engine's.
    [
      new Response("<html>private</html>", {
        headers: { "content-type": "text/html" },
      }),
      { reason: "invalid_response", status: 200, answer: "html" },
    ],
  ];
  for (const [response, expected] of answers)
    await expect(
      callTool("app_insights", { limit: 50 }, undefined, async () => response),
    ).rejects.toMatchObject({ reason: expected.reason });
  expect(lines().map((call) => call[1])).toEqual(
    answers.map(([, expected]) => ({ call: "app_insights", ...expected })),
  );
  expect(JSON.stringify(warn.mock.calls)).not.toMatch(leaks);
});

it("names a tool's own error and a malformed result as the engine's result", async () => {
  withEngine();
  await expect(
    callTool("app_graph", {}, undefined, async () =>
      rpc({ error: { code: "forbidden", message: "private" } }, true),
    ),
  ).rejects.toMatchObject({ reason: "forbidden" });
  await expect(
    callTool("app_graph", {}, undefined, async () =>
      rpc({ error: { code: "private-code" } }, true),
    ),
  ).rejects.toMatchObject({ reason: "unavailable" });
  await expect(
    callTool("app_graph", {}, undefined, async () => rpc("private")),
  ).rejects.toMatchObject({ reason: "invalid_response" });
  expect(lines().map((call) => call[1])).toEqual([
    { call: "app_graph", reason: "forbidden", status: 200, answer: "result" },
    { call: "app_graph", reason: "unavailable", status: 200, answer: "result" },
    {
      call: "app_graph",
      reason: "invalid_response",
      status: 200,
      answer: "result",
    },
  ]);
  expect(JSON.stringify(warn.mock.calls)).not.toMatch(leaks);
});

it("logs a failed tools/list and a document's refused original under their own names", async () => {
  withEngine();
  resetEngine();
  expect(
    await listTools(undefined, async () => new Response(null, { status: 403 })),
  ).toBeNull();
  await expect(
    documentFile(
      "00000000-0000-4000-8000-000000000001",
      undefined,
      async () => new Response(null, { status: 404 }),
    ),
  ).rejects.toMatchObject({ reason: "not_found" });
  expect(lines().map((call) => call[1])).toEqual([
    { call: "tools/list", reason: "forbidden", status: 403, answer: "none" },
    { call: "document-file", reason: "not_found", status: 404, answer: "none" },
  ]);
});

it("says nothing for demo mode, an adapter that is off or a request the browser gave up on", async () => {
  const never = vi.fn<typeof fetch>();
  await expect(callTool("app_graph", {}, undefined, never)).rejects.toThrow();
  withEngine({ tools: ["app_graph"] });
  await expect(
    callTool("app_garmin_summary", {}, undefined, never),
  ).rejects.toMatchObject({ reason: "not_connected" });
  expect(never).not.toHaveBeenCalled();
  const controller = new AbortController();
  await expect(
    callTool("app_graph", {}, controller.signal, async () => {
      controller.abort();
      return new Response(null, { status: 403 });
    }),
  ).rejects.toThrow();
  expect(warn).not.toHaveBeenCalled();
});

it("logs an answer the facade's checks refused by the call's name and the error's type", async () => {
  withEngine();
  const request = () =>
    new Request("http://rhythm.example.invalid/api/graph", { method: "POST" });
  // A map that is not the contract's shape: a mismatch between app and engine versions.
  const page = await readRelationshipGraph(
    request(),
    { operation: "discover" },
    async () => rpc({ nodes: "private", edges: [] }),
  );
  expect(page.nodes).toEqual([]);
  // A dashboard that is not the one asked for.
  const response = await handleDashboards(
    new Request(
      "http://rhythm.example.invalid/api/dashboards?id=00000000-0000-4000-8000-000000000002",
    ),
    async () => rpc({ dashboard: { id: "private" } }),
  );
  expect(response.status).toBe(502);
  // A reading for no one in the settings.
  const health = await loadHealth(undefined, async () =>
    rpc({ selection: "latest", complete: true, subject: "private", rows: [] }),
  );
  expect(health.current.status).toBe("unavailable");
  expect(lines().map((call) => call[1])).toEqual([
    { call: "app_graph", reason: "answer_rejected", error: "Error" },
    { call: "app_dashboards", reason: "answer_rejected", error: "Error" },
    { call: "app_latest_glucose", reason: "answer_rejected", error: "Error" },
  ]);
  expect(JSON.stringify(warn.mock.calls)).not.toMatch(leaks);
});

it("logs the same failure once a minute, then says how many were not logged", () => {
  const now = 1_800_000_000_000;
  const fields = { call: "app_graph", reason: "forbidden", status: 403 };
  warnOnce("rhythm-engine-call", fields, now);
  warnOnce("rhythm-engine-call", fields, now + 1_000);
  warnOnce("rhythm-engine-call", fields, now + 2_000);
  warnOnce("rhythm-engine-call", { ...fields, status: 401 }, now + 3_000);
  warnOnce("rhythm-engine-call", fields, now + 61_000);
  expect(warn.mock.calls).toEqual([
    ["rhythm-engine-call", fields],
    ["rhythm-engine-call", { ...fields, status: 401 }],
    ["rhythm-engine-call", { ...fields, repeats: 2 }],
  ]);
});

it("names a redirect to a sign-in page as a redirect, with Node's own fetch", async () => {
  const server = createServer((_req, res) => {
    res.writeHead(302, { location: "https://private.example.invalid/login" });
    res.end();
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const port = (server.address() as AddressInfo).port;
  try {
    await expect(
      engineFetch(
        "app_graph",
        `http://127.0.0.1:${port}/mcp`,
        {
          method: "POST",
          redirect: "error",
          headers: { Authorization: `Bearer ${appToken}` },
        },
        5000,
        false,
        nativeFetch,
      ),
    ).rejects.toThrow();
  } finally {
    server.close();
  }
  expect(warn).toHaveBeenCalledWith("rhythm-engine-transport", {
    call: "app_graph",
    category: "redirect",
    attempts: 1,
    elapsedMs: expect.any(Number),
    codes: [],
  });
  expect(JSON.stringify(warn.mock.calls)).not.toMatch(leaks);
});
