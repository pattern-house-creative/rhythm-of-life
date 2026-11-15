import { afterEach, expect, it, vi } from "vitest";
import { handleInsights } from "../server/insights";
import { GET, OPTIONS, POST } from "../api/insights";
import { demoInsights } from "../src/demoInsights";
import { appToken, closedEngines, engineUrl, withEngine } from "./engine";

afterEach(() => {
  vi.unstubAllEnvs();
});
// A fictional engine, reached with the one app token.
const configured = () => withEngine();
function rpc(payload: unknown) {
  return new Response(
    JSON.stringify({
      jsonrpc: "2.0",
      id: "rhythm-app",
      result: { structuredContent: payload },
    }),
    { headers: { "content-type": "application/json" } },
  );
}
const url = "http://rhythm.example.invalid/api/insights";
const list = () => handleInsights(new Request(url));
const feedback = (body: unknown, headers: Record<string, string> = {}) =>
  handleInsights(
    new Request(url, {
      method: "POST",
      headers: { "Content-Type": "application/json", ...headers },
      body: JSON.stringify(body),
    }),
  );
const sample = demoInsights()[0];

it("reads insights from the engine with the app token, privately", async () => {
  configured();
  vi.mocked(fetch).mockResolvedValueOnce(
    rpc({ insights: [sample, { ...sample, id: "bad", kind: "news" }] }),
  );
  const response = await list();
  expect(response.status).toBe(200);
  expect(response.headers.get("cache-control")).toBe(
    "private, no-store, max-age=0",
  );
  expect(await response.json()).toEqual({ insights: [sample], runs: [] });
  const [target, init] = vi.mocked(fetch).mock.calls.at(-1)!;
  expect(target).toBe(`${engineUrl}/mcp`);
  expect(init).toMatchObject({
    redirect: "error",
    cache: "no-store",
    headers: expect.objectContaining({
      Authorization: `Bearer ${appToken}`,
    }),
  });
  expect(JSON.parse(String(init?.body)).params).toEqual({
    name: "app_insights",
    arguments: { limit: 50 },
  });
});

it("answers no insights in demo mode, after the credential expires, or on an upstream failure", async () => {
  // No engine at all: nothing is fetched.
  vi.mocked(fetch).mockClear();
  expect(await (await list()).json()).toEqual({ insights: [], runs: [] });
  expect(fetch).not.toHaveBeenCalled();
  for (const [, close] of closedEngines) {
    close();
    vi.mocked(fetch).mockClear();
    expect(await (await list()).json()).toEqual({ insights: [], runs: [] });
    expect(fetch).not.toHaveBeenCalled();
  }
  configured();
  for (const response of [
    new Response("", { status: 401 }),
    new Response("", { status: 503 }),
    new Response("login", { headers: { "content-type": "text/html" } }),
    rpc({ insights: "many" }),
    rpc({ something: "else" }),
  ]) {
    vi.mocked(fetch).mockResolvedValueOnce(response);
    const answer = await list();
    expect(answer.status).toBe(200);
    expect(await answer.json()).toEqual({ insights: [], runs: [] });
  }
  vi.mocked(fetch).mockRejectedValueOnce(new Error("timeout"));
  expect(await (await list()).json()).toEqual({ insights: [], runs: [] });
});

it("reads the engine's runs with them, newest first, failing closed to none", async () => {
  configured();
  const run = (id: string, hoursAgo: number, extra = {}) => ({
    id,
    generatedBy: "harness-daily",
    outcome: "quiet",
    note: null,
    insightId: null,
    at: new Date(Date.now() - hoursAgo * 3_600_000).toISOString(),
    ...extra,
  });
  vi.mocked(fetch).mockResolvedValueOnce(
    rpc({
      insights: [sample],
      runs: [
        run("older", 30),
        run("newest", 2, {
          outcome: "blocked",
          note: "Drive was offline",
          extra: 1,
        }),
        run("bad", 1, { outcome: "exploded" }),
      ],
    }),
  );
  const { runs } = await (await list()).json();
  expect(runs.map((r: { id: string }) => r.id)).toEqual(["newest", "older"]);
  expect(runs[0]).not.toHaveProperty("extra");
  expect(runs[0].note).toBe("Drive was offline");
  // A malformed runs field is no runs; the insights still come through.
  vi.mocked(fetch).mockResolvedValueOnce(
    rpc({ insights: [sample], runs: { many: true } }),
  );
  expect(await (await list()).json()).toEqual({
    insights: [sample],
    runs: [],
  });
});

it("records feedback on the engine and answers with the updated insight", async () => {
  configured();
  const until = new Date(Date.now() + 86_400_000).toISOString();
  // The engine answers with the updated insight itself.
  vi.mocked(fetch).mockResolvedValueOnce(
    rpc({
      ...sample,
      status: "snoozed",
      feedback: { value: "later", at: new Date().toISOString(), until },
    }),
  );
  const response = await feedback({ id: sample.id, value: "later", until });
  expect(response.status).toBe(200);
  expect(await response.json()).toMatchObject({
    id: sample.id,
    status: "snoozed",
  });
  expect(
    JSON.parse(String(vi.mocked(fetch).mock.calls.at(-1)![1]?.body)).params,
  ).toEqual({
    name: "app_insight_feedback",
    arguments: { id: sample.id, value: "later", until },
  });
  vi.mocked(fetch).mockResolvedValueOnce(rpc({ ...sample, status: "saved" }));
  expect((await feedback({ id: sample.id, value: "interesting" })).status).toBe(
    200,
  );
  expect(
    JSON.parse(String(vi.mocked(fetch).mock.calls.at(-1)![1]?.body)).params
      .arguments,
  ).toEqual({ id: sample.id, value: "interesting" });
});

it("refuses feedback it cannot check or send, without retrieving", async () => {
  configured();
  vi.mocked(fetch).mockClear();
  for (const body of [
    { id: sample.id, value: "handled", note: "   " },
    { id: sample.id, value: "handled", note: "x".repeat(501) },
    { id: sample.id, value: "explore", note: 42 },
    { id: sample.id, value: "love" },
    { id: sample.id, value: "later" },
    { id: "../../etc", value: "explore" },
    { id: sample.id, value: "explore", extra: 1 },
  ])
    expect((await feedback(body)).status).toBe(400);
  expect(
    (
      await feedback(
        { id: sample.id, value: "explore" },
        { Origin: "https://elsewhere.example" },
      )
    ).status,
  ).toBe(403);
  expect(fetch).not.toHaveBeenCalled();
  // Without an engine: refused, and nothing is sent.
  vi.stubEnv("RHYTHM_APP_TOKEN_FILE", "/nonexistent/app_token");
  expect((await feedback({ id: sample.id, value: "explore" })).status).toBe(
    503,
  );
  expect(fetch).not.toHaveBeenCalled();
});

it("sends handled with its note, trimmed, and any other choice's note too", async () => {
  configured();
  const handled = {
    ...sample,
    status: "handled",
    feedback: {
      value: "handled",
      at: new Date().toISOString(),
      until: null,
      note: "Linked them last night.",
    },
  };
  vi.mocked(fetch).mockResolvedValueOnce(rpc(handled));
  const response = await feedback({
    id: sample.id,
    value: "handled",
    note: "  Linked them last night. ",
  });
  expect(response.status).toBe(200);
  expect(await response.json()).toMatchObject({
    status: "handled",
    feedback: { note: "Linked them last night." },
  });
  expect(
    JSON.parse(String(vi.mocked(fetch).mock.calls.at(-1)![1]?.body)).params
      .arguments,
  ).toEqual({
    id: sample.id,
    value: "handled",
    note: "Linked them last night.",
  });
  // The engine accepts handled without a note; so does the route.
  vi.mocked(fetch).mockResolvedValueOnce(rpc(handled));
  expect((await feedback({ id: sample.id, value: "handled" })).status).toBe(
    200,
  );
  vi.mocked(fetch).mockResolvedValueOnce(rpc({ ...sample, status: "saved" }));
  await feedback({ id: sample.id, value: "interesting", note: "Lovely" });
  expect(
    JSON.parse(String(vi.mocked(fetch).mock.calls.at(-1)![1]?.body)).params
      .arguments,
  ).toEqual({ id: sample.id, value: "interesting", note: "Lovely" });
});

it("fails closed when the engine answers feedback badly", async () => {
  configured();
  // The engine refuses a bad shape (403), an unknown id (404) or an until out of
  // range (400), over HTTP or as a tool error. None of them reads as lost
  // access in the app: they are a feedback that didn't save.
  const toolError = new Response(
    JSON.stringify({
      jsonrpc: "2.0",
      id: "rhythm-app",
      result: { isError: true, content: [{ type: "text", text: "404" }] },
    }),
    { headers: { "content-type": "application/json" } },
  );
  for (const response of [
    new Response("", { status: 403 }),
    new Response("", { status: 404 }),
    new Response("", { status: 400 }),
    new Response("", { status: 500 }),
    toolError,
    rpc({ ok: true }),
    rpc({ insight: sample }),
    rpc({ ...sample, id: "another-insight" }),
    rpc({ ...sample, status: "archived" }),
  ]) {
    vi.mocked(fetch).mockResolvedValueOnce(response);
    const answer = await feedback({ id: sample.id, value: "explore" });
    expect(answer.status).toBe(502);
    expect(await answer.json()).toEqual({
      error: "Source response unavailable or invalid",
    });
  }
});

it("serves GET and POST only, through the Vercel function", async () => {
  expect((await GET(new Request(url))).status).toBe(200);
  expect(
    (await POST(new Request(url, { method: "POST", body: "{}" }))).status,
  ).toBe(400);
  const options = await OPTIONS(new Request(url, { method: "OPTIONS" }));
  expect(options.status).toBe(405);
  expect(options.headers.get("allow")).toBe("GET, POST");
});
