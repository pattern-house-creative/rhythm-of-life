import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, expect, it, vi } from "vitest";
import { handleMemories } from "../server/memories";
import { resetEngine } from "../server/engine";
import {
  cleanMemoryAnswer,
  cleanPassedAnswer,
  cleanRefusal,
  memoryRequest,
} from "../src/memories";
import { allAppTools, called, closedEngines, rpc, withEngine } from "./engine";

// A person's review queues through the facade (SB-P6-01; contracts/README.md 2.2): each op passed on only while the engine serves the
// view, otherwise the 400 it was before them, sent nowhere. Each with exactly its members, never a channel or a client; its answer
// rebuilt from the contract; a write never sent twice. Fictional data only.
afterEach(() => {
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
  resetEngine();
});
const fixture = (name: string) =>
  JSON.parse(
    readFileSync(resolve(process.cwd(), "../contracts/fixtures", name), "utf8"),
  );
const on = [...allAppTools, "app_memories"];
const ask = (body?: unknown, method = body === undefined ? "GET" : "POST") =>
  new Request("http://rhythm.example.invalid/api/memories", {
    method,
    ...(body === undefined
      ? {}
      : {
          headers: { "content-type": "application/json" },
          body: JSON.stringify(body),
        }),
  });
const read = async (answer: Promise<Response> | Response) => {
  const response = await answer;
  return { status: response.status, body: await response.json() };
};

const id = "6d3c2a10-0000-4000-8000-00000000f201";
const other = "6d3c2a10-0000-4000-8000-00000000f202";

// Each op, as the view sends it, with the fixture of its answer and the op it is cleaned as.
const steps: [Record<string, unknown>, string][] = [
  [{ op: "queues" }, "queues"],
  [{ op: "queues", scope: "shared" }, "queues"],
  [{ op: "duplicates", limit: 20 }, "duplicates"],
  [{ op: "contradictions", limit: 20, cursor: "abc" }, "contradictions"],
  [
    { op: "insight_candidates", state: "waiting", limit: 20 },
    "insight_candidates",
  ],
  [
    { op: "insight_candidates", state: "rejected" },
    "insight_candidates.rejected",
  ],
  [{ op: "insight_stream", view: "proposed", limit: 20 }, "insight_stream"],
  [{ op: "insight_stream", view: "all" }, "insight_stream.quiet"],
  [{ op: "insight_history", id, limit: 100 }, "insight_history"],
  [{ op: "review_insight", id, action: "reopen" }, "review_insight"],
  [{ op: "resolve", id, action: "release", expected: 1 }, "resolve.release"],
  [
    { op: "resolve", id, action: "keep_copy", expected: 2 },
    "resolve.keep_copy",
  ],
  // A keep both naming each memory it is close to at the revision read (073).
  [
    {
      op: "resolve",
      id,
      action: "keep_copy",
      expected: 2,
      copies: [{ id: other, expected: 3 }],
    },
    "resolve.keep_copy",
  ],
  [
    {
      op: "resolve",
      id,
      action: "settle_replace",
      target: other,
      expected: 2,
      targetExpected: 3,
    },
    "resolve.settle",
  ],
  [
    { op: "resolve", id, action: "settle_keep_both", target: other },
    "resolve.unchanged",
  ],
  [
    { op: "resolve", id, action: "still_true", expected: 3 },
    "resolve.still_true",
  ],
  [
    { op: "resolve", id, action: "confirm_insight", note: "DEMO: it holds." },
    "resolve.confirm_insight",
  ],
  [
    {
      op: "resolve",
      id,
      action: "reject_candidate",
      note: "DEMO: nothing new.",
    },
    "resolve.reject_candidate",
  ],
];

it("is the request it was before them while the engine does not serve the view, sent nowhere", async () => {
  // Demo mode, no engine, and an engine whose listing has no app_memories (memory.tools off).
  const fetcher = vi.fn();
  for (const [, close] of closedEngines) {
    close();
    for (const [request] of steps)
      expect(
        (await read(handleMemories(ask(request), fetcher))).status,
        String(request.op),
      ).toBe(400);
  }
  withEngine();
  for (const [request] of steps)
    expect(
      (await read(handleMemories(ask(request), fetcher))).status,
      String(request.op),
    ).toBe(400);
  expect((await read(handleMemories(ask(), fetcher))).body).toEqual({
    available: false,
  });
  expect(fetcher).not.toHaveBeenCalled();
});

it("passes each on as the app and rebuilds its answer from the contract", async () => {
  withEngine({ tools: on });
  for (const [request, form] of steps) {
    const example = fixture(`app_memories.${form}.example.json`);
    const fetcher = vi.fn(async () => rpc(example));
    const op = String(request.op);
    const { status, body } = await read(handleMemories(ask(request), fetcher));
    expect(status, form).toBe(200);
    expect(called(fetcher.mock.calls[0][1] as RequestInit), form).toEqual({
      name: "app_memories",
      arguments: request,
    });
    expect(body.available, form).toBe(true);
    expect(body.ok, form).toBe(true);
    // The browser checks it again in the engine's shape, and reads the same.
    const { available: _, ...rest } = body;
    expect(cleanPassedAnswer(op, rest), form).toEqual(rest);
    // Anything outside the contract is dropped, and an answer that doesn't check out is no answer.
    const extra = { ...example, secret: "DEMO: outside the contract" };
    expect(JSON.stringify(cleanMemoryAnswer(op, extra))).not.toContain(
      "DEMO: outside the contract",
    );
    expect(cleanMemoryAnswer(op, { ok: true }), form).toBeNull();
  }
});

it("takes each op with exactly its members, and never a channel or a client", () => {
  for (const [request] of steps) {
    expect(memoryRequest(request), JSON.stringify(request)).toEqual(request);
    expect(memoryRequest({ ...request, channel: "cli" })).toBeNull();
    expect(memoryRequest({ ...request, client: "DEMO" })).toBeNull();
  }
  for (const bad of [
    { op: "queues", limit: 5 },
    { op: "duplicates", limit: 51 },
    { op: "contradictions", state: "waiting" },
    { op: "insight_candidates", state: "maybe" },
    // A state is the op's own: a decisions list is no candidate's state.
    { op: "insight_candidates", state: "open" },
    { op: "insight_candidates", limit: 51 },
    { op: "insight_stream", view: "maybe" },
    { op: "insight_history" },
    { op: "insight_history", id: "not-a-uuid" },
    { op: "insight_history", id, limit: 101 },
    { op: "review_insight", id },
    { op: "review_insight", id, action: "keep_copy" },
    { op: "review_insight", id, action: "confirm", note: "" },
    { op: "review_insight", id, action: "confirm", expected: 2 },
    { op: "resolve", id },
    { op: "resolve", action: "release" },
    { op: "resolve", id, action: "done" },
    { op: "resolve", id, action: "reopen" },
    { op: "resolve", id, action: "keep_copy", targetExpected: 2 },
    { op: "resolve", id, action: "settle_keep_both", target: "not-a-memory" },
    { op: "resolve", id, action: "keep_copy", key: "resolve-key-0001" },
    { op: "resolve", id, action: "keep_copy", copies: [] },
    {
      op: "resolve",
      id,
      action: "keep_copy",
      copies: { id: other, expected: 1 },
    },
    {
      op: "resolve",
      id,
      action: "keep_copy",
      copies: [1, 2, 3, 4].map((n) => ({
        id: `6d3c2a10-0000-4000-8000-00000000f21${n}`,
        expected: 1,
      })),
    },
    {
      op: "resolve",
      id,
      action: "keep_copy",
      copies: [
        { id: other, expected: 1 },
        { id: other.toUpperCase(), expected: 2 },
      ],
    },
    {
      op: "resolve",
      id,
      action: "keep_copy",
      copies: [{ id: other, expected: 0 }],
    },
    {
      op: "resolve",
      id,
      action: "keep_copy",
      copies: [{ id: other, revision: 1 }],
    },
    { op: "resolve", id, action: "keep_copy", copies: [{ id: other }] },
    {
      op: "resolve",
      id,
      action: "keep_copy",
      copies: [{ id: other, expected: 1, nonce: other }],
    },
    {
      op: "resolve",
      id,
      action: "keep_copy",
      copies: [{ id: "not-a-memory", expected: 1 }],
    },
    {
      op: "resolve",
      id,
      action: "settle_keep_both",
      target: other,
      copies: [{ id: other, expected: 1 }],
    },
    {
      op: "resolve",
      id,
      action: "still_true",
      copies: [{ id: other, expected: 1 }],
    },
    { op: "resolve", id, action: "confirm_insight", note: "x".repeat(501) },
  ])
    expect(memoryRequest(bad), JSON.stringify(bad)).toBeNull();
});

it("never sends a write twice, and says it may or may not have been saved", async () => {
  withEngine({ tools: on });
  const fetcher = vi.fn(async () => {
    throw new Error("the engine went away");
  });
  const { body } = await read(
    handleMemories(
      ask({ op: "resolve", id, action: "keep_copy" }),
      fetcher as unknown as typeof fetch,
    ),
  );
  expect(body.code).toBe("unavailable");
  expect(body.reason).toContain("may or may not have been saved");
  expect(fetcher).toHaveBeenCalledTimes(1);
  // A read says only that it can't be read.
  const again = vi.fn(async () => {
    throw new Error("the engine went away");
  });
  const { body: quiet } = await read(
    handleMemories(ask({ op: "queues" }), again as unknown as typeof fetch),
  );
  expect(quiet.reason).toContain("can't be read");
});

it("passes the refusals a person's step can get, with their reasons", async () => {
  withEngine({ tools: on });
  // Each of the store's codes the review queues can answer: an unknown one would read as the engine going away.
  for (const code of [
    "needs_person",
    "not_offered",
    "settled",
    "protected",
    "limit",
    "not_due",
    "conflict",
    "forbidden",
    "unknown",
    "invalid",
    "held",
    "retracted",
  ])
    expect(
      cleanRefusal({ ok: false, code, reason: "DEMO: why." }),
      code,
    ).toMatchObject({ code, reason: "DEMO: why." });
  const example = fixture("app_memories.refused.not_offered.example.json");
  const { body } = await read(
    handleMemories(
      ask({ op: "resolve", id, action: "settle_keep_both", target: other }),
      vi.fn(async () => rpc(example)) as unknown as typeof fetch,
    ),
  );
  expect(body).toMatchObject({
    available: true,
    ok: false,
    code: "not_offered",
    reason: example.reason,
  });
  expect(
    cleanMemoryAnswer(
      "resolve",
      fixture("app_memories.refused.protected.example.json"),
    ),
  ).toMatchObject({ code: "protected" });
});

it("takes no answer that says what the contract doesn't", () => {
  // A resolve in a word or a queue of no review, a held state with no reason, and an insight whose id isn't the item's.
  const done = fixture("app_memories.resolve.keep_copy.example.json");
  expect(cleanMemoryAnswer("resolve", { ...done, action: "done" })).toBeNull();
  expect(cleanMemoryAnswer("resolve", { ...done, queue: "loops" })).toBeNull();
  expect(
    cleanMemoryAnswer("resolve", { ...done, held: { signals: [] } }),
  ).toBeNull();
  const stream = fixture("app_memories.insight_stream.example.json");
  const [item] = stream.insights;
  const pairs = fixture("app_memories.contradictions.example.json");
  expect(cleanMemoryAnswer("contradictions", pairs)).toEqual(pairs);
  const [k] = pairs.contradictions;
  const { startedAt: _dropped, ...undated } = k.memory;
  expect(
    cleanMemoryAnswer("contradictions", {
      ...pairs,
      contradictions: [{ ...k, memory: undated }],
    }),
  ).toBeNull();
  expect(
    cleanMemoryAnswer("contradictions", {
      ...pairs,
      contradictions: [{ ...k, memory: { ...undated, startedAt: null } }],
    }),
  ).not.toBeNull();
  expect(
    cleanMemoryAnswer("insight_stream", {
      ...stream,
      insights: [{ ...item, id: other }],
    }),
  ).toBeNull();
  expect(
    cleanMemoryAnswer("insight_stream", {
      ...stream,
      insights: [{ ...item, insight: { ...item.insight, title: "" } }],
    }),
  ).toBeNull();
  expect(
    cleanMemoryAnswer("insight_stream", {
      ...stream,
      day: { ...stream.day, says: "" },
    }),
  ).toBeNull();
  // A source's link as long as the engine keeps one (InsightDraft.Https, 2048), and one past it.
  const linked = (length: number) => ({
    ...stream,
    insights: [
      {
        ...item,
        insight: {
          ...item.insight,
          sources: [
            {
              kind: "web",
              title: "DEMO: a long link",
              url:
                "https://example.invalid/" +
                "a".repeat(length - "https://example.invalid/".length),
              retrievedAt: item.insight.createdAt,
            },
          ],
        },
      },
    ],
  });
  expect(cleanMemoryAnswer("insight_stream", linked(2048))).not.toBeNull();
  expect(cleanMemoryAnswer("insight_stream", linked(2049))).toBeNull();
  expect(
    cleanMemoryAnswer("queues", {
      ok: true,
      held: -1,
      duplicates: 0,
      contradictions: 0,
      stale: 0,
      candidates: 0,
      insights: 0,
    }),
  ).toBeNull();
  const copies = fixture("app_memories.duplicates.example.json");
  expect(
    cleanMemoryAnswer("duplicates", { ...copies, count: "many" }),
  ).toBeNull();
});
