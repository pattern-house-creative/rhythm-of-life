import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, expect, it, vi } from "vitest";
import { handleMemories } from "../server/memories";
import {
  cleanMemoryAnswer,
  cleanPassedAnswer,
  memoryReads,
  memoryRequest,
} from "../src/memories";
import {
  allAppTools,
  appToken,
  called,
  closedEngines,
  rpc,
  withEngine,
} from "./engine";

// The brief's lists through the facade (SB-P6-01; contracts/README.md 2.2): each op passed on only while the engine serves the
// view, otherwise the 400 it was before them, sent nowhere. Each op with exactly its members, never a channel or a client; its
// answer rebuilt from the contract; a write never sent twice. Fictional data only.
afterEach(() => {
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
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

const id = "6d3c2a10-0000-4000-8000-00000000f301";

// Each op, as the view sends it, with the fixture of its answer.
const steps: [Record<string, unknown>, string, string][] = [
  [{ op: "loops", direction: "out", limit: 20 }, "loops", "loops"],
  [{ op: "loops", direction: "in", cursor: "abc" }, "loops", "loops.owed"],
  [{ op: "due", limit: 20 }, "due", "due"],
  [{ op: "due", window: "later", scope: "shared" }, "due", "due"],
  [{ op: "decisions", state: "due", limit: 20 }, "decisions", "decisions"],
  [{ op: "decisions", state: "resolved" }, "decisions", "decisions.resolved"],
  [{ op: "calibration" }, "calibration", "calibration"],
  [
    { op: "calibration", source: "stated" },
    "calibration",
    "calibration.not_ready",
  ],
  [
    { op: "resolve_loop", id, action: "done", expected: 1 },
    "resolve_loop",
    "resolve_loop",
  ],
  [
    {
      op: "resolve_due",
      id,
      action: "snooze",
      until: "2026-12-01T09:00",
      expected: 1,
    },
    "resolve_due",
    "resolve_due",
  ],
  [
    { op: "resolve_due", id, action: "clear_date", expected: 1 },
    "resolve_due",
    "resolve_due.cleared",
  ],
  [
    { op: "record_outcome", id, result: "unknown", expected: 2 },
    "record_outcome",
    "record_outcome",
  ],
  [{ op: "changes", notHeld: true }, "changes", "changes"],
];

it("is the request it was before them while the engine does not serve the view, sent nowhere", async () => {
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
  for (const [request, op, form] of steps) {
    const example = fixture(`app_memories.${form}.example.json`);
    const fetcher = vi.fn(async () => rpc(example));
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
    expect(memoryRequest(request), String(request.op)).toEqual(request);
    expect(memoryRequest({ ...request, channel: "cli" })).toBeNull();
    expect(memoryRequest({ ...request, client: "DEMO" })).toBeNull();
  }
  for (const bad of [
    { op: "loops", direction: "all" },
    { op: "loops", window: "later" },
    { op: "loops", limit: 51 },
    { op: "due", window: "soon" },
    { op: "due", direction: "out" },
    { op: "decisions", state: "later" },
    // A state is the op's own: an insight candidate's is no decisions list.
    { op: "decisions", state: "waiting" },
    { op: "calibration", source: "all" },
    { op: "calibration", limit: 5 },
    { op: "resolve_loop", id },
    { op: "resolve_loop", action: "done" },
    { op: "resolve_loop", id, action: "snooze" },
    { op: "resolve_loop", id, action: "done", key: "resolve-key-001" },
    { op: "resolve_due", id, action: "done" },
    // A snooze says when it comes back, and a clear names no date.
    { op: "resolve_due", id, action: "snooze" },
    { op: "resolve_due", id, action: "clear_date", until: "2026-12-01" },
    { op: "resolve_due", id, action: "snooze", until: "" },
    { op: "resolve_due", id, action: "snooze", until: "x".repeat(41) },
    { op: "record_outcome", id },
    { op: "record_outcome", id, result: "maybe" },
    { op: "record_outcome", id, result: "right", note: "DEMO" },
    { op: "changes", notHeld: "yes" },
  ])
    expect(memoryRequest(bad), JSON.stringify(bad)).toBeNull();
});

it("speaks to the engine with the app's own token, whatever a caller brings", async () => {
  withEngine({ tools: on });
  const fetcher = vi.fn(async () =>
    rpc(fixture("app_memories.resolve_loop.example.json")),
  );
  const { status } = await read(
    handleMemories(
      new Request("http://rhythm.example.invalid/api/memories", {
        method: "POST",
        headers: {
          "content-type": "application/json",
          authorization: "Bearer " + "DEMO-an-ai-clients-word",
        },
        body: JSON.stringify({ op: "resolve_loop", id, action: "done" }),
      }),
      fetcher,
    ),
  );
  expect(status).toBe(200);
  const sent = new Headers(
    (fetcher.mock.calls[0] as unknown as [string, RequestInit])[1].headers,
  );
  expect(sent.get("authorization")).toBe(`Bearer ${appToken}`);
});

it("never sends a step twice, and says it may have been saved; a read says it can't be read", async () => {
  withEngine({ tools: on });
  const lost = vi.fn(async () => {
    throw new Error("engine down");
  });
  vi.spyOn(console, "error").mockImplementation(() => {});
  for (const [request] of steps) {
    const { body } = await read(handleMemories(ask(request), lost));
    const write = !memoryReads.includes(String(request.op));
    expect(body.code).toBe("unavailable");
    expect(body.reason).toMatch(
      write ? /may or may not have been saved/ : /can't be read/,
    );
  }
  expect(lost).toHaveBeenCalledTimes(steps.length);
});

it("reads a row only as the contract has it: a preview, a window and a count no shorter than the page", () => {
  const page = fixture("app_memories.due.example.json");
  const row = page.due[0];
  expect(cleanMemoryAnswer("due", page)).toMatchObject({ count: 3, later: 1 });
  for (const bad of [
    { ...page, count: 1 },
    { ...page, due: [{ ...row, preview: "x".repeat(301) }] },
    { ...page, due: [{ ...row, window: "soon" }] },
    { ...page, due: [{ ...row, kind: "alarm" }] },
    { ...page, due: [{ ...row, loop: "both" }] },
    { ...page, zone: "" },
  ])
    expect(cleanMemoryAnswer("due", bad)).toBeNull();

  const loops = fixture("app_memories.loops.example.json");
  for (const bad of [
    { ...loops, count: 0 },
    { ...loops, youOwe: -1 },
    { ...loops, loops: [{ ...loops.loops[0], direction: "both" }] },
  ])
    expect(cleanMemoryAnswer("loops", bad)).toBeNull();
  // Whatever else came with a row stays behind.
  const cleaned = cleanMemoryAnswer("loops", {
    ...loops,
    loops: [{ ...loops.loops[0], content: "DEMO: its whole words" }],
  });
  expect(JSON.stringify(cleaned)).not.toContain("its whole words");

  const decided = fixture("app_memories.decisions.resolved.example.json");
  for (const bad of [
    { ...decided, decisions: [{ ...decided.decisions[0], outcome: "maybe" }] },
    { ...decided, decisions: [{ ...decided.decisions[0], rearms: 3 }] },
    { ...decided, decisions: [{ ...decided.decisions[0], confidence: 7 }] },
    { ...decided, scored: "three" },
  ])
    expect(cleanMemoryAnswer("decisions", bad)).toBeNull();

  const figures = fixture("app_memories.calibration.example.json");
  for (const bad of [
    { ...figures, bands: figures.bands.slice(1) },
    { ...figures, kind: "guess" },
    { ...figures, line: "" },
    { ...figures, topic: { name: "pellow" } },
  ])
    expect(cleanMemoryAnswer("calibration", bad)).toBeNull();

  const write = fixture("app_memories.record_outcome.example.json");
  for (const bad of [
    { ...write, result: "maybe" },
    { ...write, rearms: 3 },
    { ...write, reviewAt: "later" },
  ])
    expect(cleanMemoryAnswer("record_outcome", bad)).toBeNull();
  const snooze = fixture("app_memories.resolve_due.example.json");
  for (const bad of [
    { ...snooze, action: "done" },
    { ...snooze, due: { at: "soon" } },
  ])
    expect(cleanMemoryAnswer("resolve_due", bad)).toBeNull();
  const done = fixture("app_memories.resolve_loop.example.json");
  expect(
    cleanMemoryAnswer("resolve_loop", { ...done, action: "snooze" }),
  ).toBeNull();
});

it("reads an engine's changes with its count, and one from before without it", () => {
  const changes = fixture("app_memories.changes.example.json");
  expect(cleanMemoryAnswer("changes", changes)).not.toHaveProperty("count");
  expect(cleanMemoryAnswer("changes", { ...changes, count: 2 })).toMatchObject({
    count: 2,
  });
});
