import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, expect, it, vi } from "vitest";
import { handleMemories } from "../server/memories";
import { resetEngine } from "../server/engine";
import {
  cleanMemoryAnswer,
  cleanPassedAnswer,
  memoryRequest,
} from "../src/memories";
import { allAppTools, called, closedEngines, rpc, withEngine } from "./engine";

// The memory UI's last pieces through the facade (SB-P6-01, run 22; contracts/README.md 2.2): a list's and its tags' words, the standing
// instructions and a person's stop of one, and a review queue's step for several. Each passed on only while the engine serves the view,
// otherwise the 400 it was before them, sent nowhere; each with exactly its members, never a channel or a client; its answer rebuilt
// from the contract; a write never sent twice. Fictional data only.
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

const id = "6d3c2a10-0000-4000-8000-00000000f301";
const other = "6d3c2a10-0000-4000-8000-00000000f302";

// The new ops, as the view sends them, with the fixture of each answer.
const fresh: [Record<string, unknown>, string][] = [
  [{ op: "standing", limit: 20 }, "standing"],
  [{ op: "standing", scope: "shared", cursor: "abc" }, "standing"],
  [{ op: "stop_standing", id, expected: 1 }, "stop_standing"],
  [
    {
      op: "resolve_all",
      action: "dismiss_insight",
      items: [{ id }, { id: other }],
    },
    "resolve_all",
  ],
  [
    {
      op: "resolve_all",
      action: "still_true",
      items: [
        { id, expected: 2 },
        { id: other, expected: 1 },
      ],
    },
    "resolve_all",
  ],
];
// A list and its tags with words, which an older facade took without them.
const worded: [Record<string, unknown>, string][] = [
  [{ op: "list", query: "Wendle ferry", limit: 20 }, "list"],
  [{ op: "list", query: "ferry", kind: "semantic", status: "draft" }, "list"],
  [{ op: "tags", query: "Wendle ferry", kind: "episodic" }, "tags"],
];

it("is the request it was before them while the engine does not serve the view, sent nowhere", async () => {
  const fetcher = vi.fn();
  for (const [, close] of closedEngines) {
    close();
    // A list's words too: no request named them before.
    for (const [request] of [...fresh, ...worded])
      expect(
        (await read(handleMemories(ask(request), fetcher))).status,
        String(request.op),
      ).toBe(400);
    // A list without them answers as every list did, unavailable.
    expect(
      (
        await read(
          handleMemories(ask({ op: "list", kind: "semantic" }), fetcher),
        )
      ).body,
    ).toEqual({ available: false });
  }
  withEngine();
  for (const [request] of [...fresh, ...worded])
    expect(
      (await read(handleMemories(ask(request), fetcher))).status,
      String(request.op),
    ).toBe(400);
  expect(fetcher).not.toHaveBeenCalled();
});

it("passes each on as the app and rebuilds its answer from the contract", async () => {
  withEngine({ tools: on });
  for (const [request, form] of [...fresh, ...worded]) {
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
    const { available: _, ...rest } = body;
    expect(cleanPassedAnswer(op, rest), form).toEqual(rest);
    const extra = { ...example, secret: "DEMO: outside the contract" };
    expect(JSON.stringify(cleanMemoryAnswer(op, extra))).not.toContain(
      "DEMO: outside the contract",
    );
    expect(cleanMemoryAnswer(op, { ok: true }), form).toBeNull();
  }
});

it("takes each op with exactly its members, and never a channel or a client", () => {
  for (const [request] of [...fresh, ...worded]) {
    expect(memoryRequest(request), JSON.stringify(request)).toEqual(request);
    expect(memoryRequest({ ...request, channel: "cli" })).toBeNull();
    expect(memoryRequest({ ...request, client: "DEMO" })).toBeNull();
  }
  expect(memoryRequest({ op: "list", query: "x".repeat(200) })).not.toBeNull();
  // At most 200 characters as sent, as the engine's door and its listing count them; the engine trims what it takes.
  expect(
    memoryRequest({ op: "tags", query: ` ${"x".repeat(199)}` }),
  ).not.toBeNull();
  for (const bad of [
    { op: "list", query: "" },
    { op: "list", query: "   " },
    { op: "list", query: "x".repeat(201) },
    { op: "list", query: ` ${"x".repeat(200)}` },
    { op: "list", query: ["ferry"] },
    { op: "tags", query: 7 },
    { op: "list", kind: "procedural" },
    { op: "standing", limit: 51 },
    { op: "standing", query: "ferry" },
    { op: "stop_standing" },
    { op: "stop_standing", id, key: "stop-standing-key-1" },
    { op: "resolve_all", action: "dismiss_insight" },
    { op: "resolve_all", items: [{ id }] },
    { op: "resolve_all", action: "dismiss_insight", items: [] },
    {
      op: "resolve_all",
      action: "dismiss_insight",
      items: Array.from({ length: 51 }, () => ({ id })),
    },
    { op: "resolve_all", action: "dismiss_insight", items: [{ id: "x" }] },
    {
      op: "resolve_all",
      action: "dismiss_insight",
      items: [{ id, note: "DEMO" }],
    },
    { op: "resolve_all", action: "still_true", items: [{ id, expected: 0 }] },
    ...[
      "release",
      "keep_copy",
      "settle_keep_both",
      "use_candidate",
      "done",
    ].map((action) => ({ op: "resolve_all", action, items: [{ id }] })),
    { op: "resolve_all", action: "confirm", items: [{ id }] },
  ])
    expect(memoryRequest(bad), JSON.stringify(bad)).toBeNull();
});

it("never sends a write twice, and says it may or may not have been saved", async () => {
  withEngine({ tools: on });
  for (const request of [
    { op: "resolve_all", action: "confirm_insight", items: [{ id }] },
    { op: "stop_standing", id, expected: 1 },
  ]) {
    const fetcher = vi.fn(async () => {
      throw new Error("the engine went away");
    });
    const { body } = await read(
      handleMemories(ask(request), fetcher as unknown as typeof fetch),
    );
    expect(body.code).toBe("unavailable");
    expect(body.reason).toContain("may or may not have been saved");
    expect(fetcher).toHaveBeenCalledTimes(1);
  }
  const again = vi.fn(async () => {
    throw new Error("the engine went away");
  });
  const { body: quiet } = await read(
    handleMemories(ask({ op: "standing" }), again as unknown as typeof fetch),
  );
  expect(quiet.reason).toContain("can't be read");
});

it("takes no answer that says what the contract doesn't", () => {
  const standing = fixture("app_memories.standing.example.json");
  const [fires, held] = standing.standing;
  expect(cleanMemoryAnswer("standing", standing)).toEqual(standing);
  for (const row of [
    { ...held, content: "DEMO: a held one's words" },
    { ...held, project: { id, slug: "wendle" } },
    { ...fires, reason: "held" },
    { ...held, reason: null },
    { ...fires, content: "x".repeat(501) },
    { ...fires, reason: "maybe", fires: false },
  ])
    expect(
      cleanMemoryAnswer("standing", { ...standing, standing: [row] }),
      JSON.stringify(row),
    ).toBeNull();
  // A page never holds more than its count, nor counts more firing than the list.
  expect(cleanMemoryAnswer("standing", { ...standing, count: 1 })).toBeNull();
  expect(
    cleanMemoryAnswer("standing", { ...standing, firing: standing.count + 1 }),
  ).toBeNull();

  const several = fixture("app_memories.resolve_all.example.json");
  expect(cleanMemoryAnswer("resolve_all", several)).toEqual(several);
  expect(
    cleanMemoryAnswer("resolve_all", { ...several, action: "release" }),
  ).toBeNull();
  expect(
    cleanMemoryAnswer("resolve_all", { ...several, results: [] }),
  ).toBeNull();
  expect(
    cleanMemoryAnswer("resolve_all", {
      ...several,
      results: [{ ...several.results[0], result: "maybe" }],
    }),
  ).toBeNull();
  const kept = cleanMemoryAnswer("resolve_all", {
    ...several,
    results: [{ ...several.results[0], preview: "DEMO: words" }],
  });
  expect(JSON.stringify(kept)).not.toContain("DEMO: words");

  const stopped = fixture("app_memories.stop_standing.example.json");
  expect(cleanMemoryAnswer("stop_standing", stopped)).toMatchObject({
    ok: true,
    written: { op: "stop_standing", revision: 2, unchanged: false },
  });
  expect(
    cleanMemoryAnswer("stop_standing", { ...stopped, op: "update" }),
  ).toBeNull();
});
