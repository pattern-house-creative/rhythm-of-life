import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, expect, it, vi } from "vitest";
import { handleGraph } from "../server/graph";
import { resetEngine } from "../server/engine";
import { readRecall } from "../src/apiClient";
import { cleanRecalled, recallReason } from "../src/recall";
import type { RecallWhy } from "../src/recall";
import { allAppTools, called, closedEngines, rpc, withEngine } from "./engine";

// SB-P2-10: the explorer's combined search through the facade, POST /api/graph {operation:
// "recall", value}. Off unless the engine's listing names app_graph's operation recall, which it
// does only with engine.recall on; then the stars and why, the contract's fixture as the engine's
// answer (contracts/fixtures/app_graph.recall.example.json).
afterEach(() => {
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
});
const fixture = () =>
  JSON.parse(
    readFileSync(
      resolve(
        process.cwd(),
        "../contracts/fixtures/app_graph.recall.example.json",
      ),
      "utf8",
    ),
  );
const question = "how did my glucose look after walking?";
const recallOn = () => {
  withEngine();
  resetEngine(allAppTools, {
    app_graph: ["operation", "value", "operation=search", "operation=recall"],
  });
};
const post = (body: unknown) =>
  handleGraph(
    new Request("http://rhythm.example.invalid/api/graph", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(body),
    }),
  );
const ask = async (value: string = question) => {
  const response = await post({ operation: "recall", value });
  expect(response.status).toBe(200);
  return (await response.json()) as Record<string, any>;
};

it("is off in demo mode and on every closed engine, without calling it", async () => {
  vi.mocked(fetch).mockResolvedValue(rpc(fixture()));
  expect(await ask()).toEqual({ available: false });
  for (const [, close] of closedEngines) {
    close();
    expect(await ask()).toEqual({ available: false });
  }
  expect(fetch).not.toHaveBeenCalled();
});

it("is off while the engine does not list it, which it does only with engine.recall on", async () => {
  vi.mocked(fetch).mockResolvedValue(rpc(fixture()));
  // The engine's listing before it (and with engine.recall off): app_graph's own operations.
  withEngine();
  resetEngine(allAppTools, {
    app_graph: ["operation", "value", "operation=search"],
  });
  expect(await ask()).toEqual({ available: false });
  // A listing that names no enum at all, as the facade kept it before: off too.
  withEngine();
  expect(await ask()).toEqual({ available: false });
  expect(fetch).not.toHaveBeenCalled();
});

it("answers the stars combined retrieval finds, best first, with why", async () => {
  recallOn();
  vi.mocked(fetch).mockResolvedValue(rpc(fixture()));
  const answer = await ask();
  expect(fetch).toHaveBeenCalledTimes(1);
  expect(called(vi.mocked(fetch).mock.calls[0][1])).toEqual({
    name: "app_graph",
    arguments: { operation: "recall", value: question },
  });
  expect(answer.available).toBe(true);
  expect(answer.page.nodes.map((n: { id: string }) => n.id)).toEqual([
    "notes:notes:walks.md",
    "rhythm:data%3Anightscout:alex%3Aglucose",
  ]);
  expect(answer.page.edges).toHaveLength(1);
  expect(answer.page.cursors).toEqual({});
  expect(answer.recall).toMatchObject({
    query: question,
    offMap: 1,
    notices: [],
    receipt: "rc1-5b0e9d3a71c2f468",
  });
  expect(
    answer.recall.results.map((r: { id: string; why: RecallWhy }) => [
      r.id,
      recallReason(r.why),
    ]),
  ).toEqual([
    ["notes:notes:walks.md", "By 2 of your words and its meaning"],
    ["rhythm:data%3Anightscout:alex%3Aglucose", "By 1 of your words"],
  ]);
});

it("leaves out a star the reader can't read, with its result and its links", async () => {
  recallOn();
  const found = fixture();
  found.nodes[1].resource.access.canRead = false;
  vi.mocked(fetch).mockResolvedValue(rpc(found));
  const answer = await ask();
  expect(answer.page.nodes.map((n: { id: string }) => n.id)).toEqual([
    "notes:notes:walks.md",
  ]);
  expect(answer.page.edges).toEqual([]);
  expect(answer.recall.results.map((r: { id: string }) => r.id)).toEqual([
    "notes:notes:walks.md",
  ]);
});

it("says a search failed when the engine's answer is not the contract's", async () => {
  recallOn();
  const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
  for (const broken of [
    { recall: { ...fixture().recall, receipt: "receipt-1" } },
    { recall: undefined },
    { recall: { ...fixture().recall, offMap: ["memory-1"] } },
  ]) {
    vi.mocked(fetch).mockResolvedValueOnce(rpc({ ...fixture(), ...broken }));
    expect(await ask()).toEqual({ available: true, failed: true });
  }
  vi.mocked(fetch).mockResolvedValueOnce(new Response("", { status: 503 }));
  expect(await ask()).toEqual({ available: true, failed: true });
  warn.mockRestore();
});

it("is off again when the engine refuses the operation after its listing said it takes it", async () => {
  recallOn();
  vi.spyOn(console, "warn").mockImplementation(() => {});
  vi.mocked(fetch).mockResolvedValueOnce(
    new Response(
      JSON.stringify({
        jsonrpc: "2.0",
        id: "rhythm-app",
        result: {
          isError: true,
          structuredContent: {
            error: {
              code: "invalid_arguments",
              message: "Arguments are outside the tool's schema.",
            },
          },
        },
      }),
      { headers: { "content-type": "application/json" } },
    ),
  );
  expect(await ask()).toEqual({ available: false });
});

it("takes one question of at most 800 characters and nothing beside it", async () => {
  recallOn();
  vi.mocked(fetch).mockResolvedValue(rpc(fixture()));
  for (const body of [
    { operation: "recall", value: "a".repeat(801) },
    { operation: "recall", value: "   " },
    { operation: "recall" },
    { operation: "recall", value: question, cursors: { rhythm: "o:20" } },
    { operation: "recall", value: question, ids: [] },
  ])
    expect((await post(body)).status).toBe(400);
  expect(fetch).not.toHaveBeenCalled();
  expect((await ask("a".repeat(800))).available).toBe(true);
});

it("the browser's reader checks the answer and never throws for a failed one", async () => {
  const answer = (body: unknown, status = 200) =>
    new Response(JSON.stringify(body), {
      status,
      headers: { "content-type": "application/json" },
    });
  const found = fixture();
  vi.mocked(fetch).mockResolvedValueOnce(
    answer({
      available: true,
      page: { ...found, recall: undefined, cursors: {} },
      recall: found.recall,
    }),
  );
  const read = await readRecall(question);
  expect(read.available && !read.failed && read.recall.results).toHaveLength(2);
  expect(JSON.parse(String(vi.mocked(fetch).mock.calls[0][1]?.body))).toEqual({
    operation: "recall",
    value: question,
  });
  vi.mocked(fetch).mockResolvedValueOnce(answer({ available: false }));
  expect(await readRecall(question)).toEqual({ available: false });
  vi.mocked(fetch).mockResolvedValueOnce(
    answer({ available: false, retry: true }),
  );
  expect(await readRecall(question)).toEqual({
    available: false,
    retry: true,
  });
  // An engine that serves it, with an answer that doesn't check out: failed.
  vi.mocked(fetch).mockResolvedValueOnce(answer({ available: true, page: 1 }));
  expect(await readRecall(question)).toEqual({ available: true, failed: true });
  // No answer that says whether it is on: not known, so asked again, never shown as failed.
  vi.mocked(fetch).mockResolvedValueOnce(answer({ error: "Invalid" }, 400));
  expect(await readRecall(question)).toEqual({ available: false, retry: true });
  vi.mocked(fetch).mockRejectedValueOnce(new TypeError("fetch failed"));
  expect(await readRecall(question)).toEqual({ available: false, retry: true });
  // A search the explorer gave up on is not a failure to show.
  const stop = new AbortController();
  stop.abort();
  vi.mocked(fetch).mockRejectedValueOnce(
    new DOMException("stopped", "AbortError"),
  );
  await expect(readRecall(question, stop.signal)).rejects.toThrow();
});

it("names why in plain words and keeps only the results whose stars came with it", () => {
  const why = (w: Partial<RecallWhy>): RecallWhy => ({
    rank: 1,
    words: null,
    meaning: null,
    imputed: false,
    ...w,
  });
  expect(
    recallReason(why({ words: { rank: 1, terms: ["pl", "3317"], exact: 1 } })),
  ).toBe("By an exact id and 2 of your words");
  expect(recallReason(why({ meaning: { rank: 3, similarity: 0.4 } }))).toBe(
    "By its meaning",
  );
  expect(
    recallReason(
      why({
        words: { rank: 2, terms: [], exact: 0 },
        meaning: { rank: 1, similarity: null },
      }),
    ),
  ).toBe("By your words and its meaning");
  expect(
    recallReason(
      why({
        words: { rank: 2, terms: ["lamp"], exact: 2 },
        meaning: { rank: 1, similarity: 0.7 },
      }),
    ),
  ).toBe("By 2 exact ids, 1 of your words and its meaning");
  const recall = fixture().recall;
  const kept = cleanRecalled(
    { ...recall, results: [...recall.results, recall.results[0]] },
    new Set(["notes:notes:walks.md"]),
  );
  expect(kept?.results.map((r) => r.id)).toEqual(["notes:notes:walks.md"]);
  expect(
    cleanRecalled(
      { ...recall, results: [{ ...recall.results[0], why: {} }] },
      new Set(),
    ),
  ).toBeNull();
});

it("learns that the engine serves it from app_graph's own listing", async () => {
  // The engine's tools/list, as AppEndpoint.Tools writes it with engine.recall on and off.
  const listing = (operations: string[]) =>
    new Response(
      JSON.stringify({
        jsonrpc: "2.0",
        id: "rhythm-app",
        result: {
          tools: [
            {
              name: "app_graph",
              inputSchema: {
                type: "object",
                properties: {
                  operation: { type: "string", enum: operations },
                  value: { type: "string" },
                },
              },
            },
          ],
        },
      }),
      { headers: { "content-type": "application/json" } },
    );
  const own = ["overview", "search", "neighbors", "project", "get", "content", "data"];
  withEngine();
  resetEngine();
  vi.mocked(fetch).mockResolvedValueOnce(listing(own));
  expect(await ask()).toEqual({ available: false });
  expect(fetch).toHaveBeenCalledTimes(1);
  withEngine();
  resetEngine();
  vi.mocked(fetch)
    .mockResolvedValueOnce(listing([...own, "recall"]))
    .mockResolvedValueOnce(rpc(fixture()));
  expect((await ask()).recall.offMap).toBe(1);
  expect(
    JSON.parse(String(vi.mocked(fetch).mock.calls[1][1]?.body)).method,
  ).toBe("tools/list");
});

it("says nothing of a listing it could not read, and asks again once the engine lists it", async () => {
  const listing = (operations: string[]) =>
    new Response(
      JSON.stringify({
        jsonrpc: "2.0",
        id: "rhythm-app",
        result: {
          tools: [
            {
              name: "app_graph",
              inputSchema: {
                type: "object",
                properties: { operation: { type: "string", enum: operations } },
              },
            },
          ],
        },
      }),
      { headers: { "content-type": "application/json" } },
    );
  withEngine();
  resetEngine();
  // The engine's listing could not be read: neither on nor off, so ask again, and nothing asked.
  vi.mocked(fetch).mockRejectedValueOnce(new TypeError("fetch failed"));
  expect(await ask()).toEqual({ available: false, retry: true });
  expect(fetch).toHaveBeenCalledTimes(1);
  // Read again (here at once; the facade retries a failed listing after half a minute): on.
  resetEngine();
  vi.mocked(fetch)
    .mockResolvedValueOnce(listing(["overview", "search", "recall"]))
    .mockResolvedValueOnce(rpc(fixture()));
  expect((await ask()).recall.offMap).toBe(1);
});
