import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, expect, it, vi } from "vitest";
import { handleMemories } from "../server/memories";
import { routes, bodyLimit } from "../server/routes";
import { cleanMemoryAnswer, memoryRequest, visibleText } from "../src/memories";
import { resetEngine } from "../server/engine";
import { allAppTools, called, closedEngines, rpc, withEngine } from "./engine";

// The Memories view's route (SB-P3-12; contracts/README.md 2.2, app_memories): only while the engine lists the tool,
// which it does only with memory.tools on; each op with exactly its members; the answer rebuilt from the contract; a
// write never sent twice; the channel never named. Fictional data only.
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

it("is off in demo mode, and whenever the engine's listing lacks the tool, without calling it", async () => {
  for (const [, close] of closedEngines) {
    close();
    const fetcher = vi.fn();
    expect((await read(handleMemories(ask(), fetcher))).body).toEqual({
      available: false,
    });
    expect(
      (await read(handleMemories(ask({ op: "list" }), fetcher))).body,
    ).toEqual({ available: false });
    expect(fetcher).not.toHaveBeenCalled();
  }
  // memory.tools off: the engine lists no app_memories, so nothing is offered and nothing is called.
  withEngine();
  const fetcher = vi.fn();
  expect((await read(handleMemories(ask(), fetcher))).body).toEqual({
    available: false,
  });
  expect(
    (
      await read(
        handleMemories(
          ask({ op: "capture", content: "DEMO: hello." }),
          fetcher,
        ),
      )
    ).body,
  ).toEqual({ available: false });
  expect(fetcher).not.toHaveBeenCalled();
});

it("passes one op on as the app and rebuilds its answer from the contract", async () => {
  withEngine({ tools: on });
  expect((await read(handleMemories(ask()))).body).toEqual({ available: true });
  const fetcher = vi.fn(async () =>
    rpc(fixture("app_memories.list.example.json")),
  );
  const listed = await read(
    handleMemories(ask({ op: "list", tag: "wendle", limit: 20 }), fetcher),
  );
  expect(called(fetcher.mock.calls[0][1] as RequestInit)).toEqual({
    name: "app_memories",
    arguments: { op: "list", tag: "wendle", limit: 20 },
  });
  expect(listed.body.available).toBe(true);
  expect(listed.body.ok).toBe(true);
  expect(listed.body.memories).toHaveLength(2);
  // A held memory in a list carries no words, tags or project, whatever the engine sent.
  const held = fixture("app_memories.list.example.json");
  held.memories[1] = {
    ...held.memories[1],
    content: "DEMO: words",
    tags: ["clipping"],
  };
  fetcher.mockResolvedValueOnce(rpc(held));
  const cleaned = await read(handleMemories(ask({ op: "list" }), fetcher));
  expect(cleaned.body.memories[1]).toMatchObject({
    content: null,
    tags: [],
    project: null,
  });
  // get serves a held memory whole, for a person to review.
  fetcher.mockResolvedValueOnce(rpc(fixture("app_memories.held.example.json")));
  const got = await read(
    handleMemories(
      ask({ op: "get", id: "6d3c2a10-0000-4000-8000-00000000b002" }),
      fetcher,
    ),
  );
  expect(got.body.memory.content).toContain("DEMO");
  expect(got.body.memory.held).toEqual({ reason: "instruction", signals: [] });
  // A refusal passes on with its code and reason; an answer outside the contract is unavailable.
  fetcher.mockResolvedValueOnce(
    rpc(fixture("app_memories.refused.example.json")),
  );
  const stale = await read(
    handleMemories(
      ask({
        op: "append",
        id: "6d3c2a10-0000-4000-8000-00000000b001",
        text: "DEMO: late.",
        expected: 1,
      }),
      fetcher,
    ),
  );
  expect(stale.body).toEqual({
    available: true,
    ok: false,
    code: "conflict",
    reason: expect.stringContaining("revision 2"),
    revision: 2,
    known: [],
  });
  fetcher.mockResolvedValueOnce(rpc({ ok: true, memories: "nope" }));
  const odd = await read(handleMemories(ask({ op: "list" }), fetcher));
  expect(odd.body).toMatchObject({
    available: true,
    ok: false,
    code: "unavailable",
  });
});

it("never sends a write twice, and reads again only what only reads", async () => {
  withEngine({ tools: on });
  const fetcher = vi.fn(async () => new Response("", { status: 502 }));
  const id = "6d3c2a10-0000-4000-8000-00000000b001";
  const lost = await read(
    handleMemories(
      ask({
        op: "capture",
        content: "DEMO: the gate code.",
        scope: "shared",
        key: "app:capture-0001",
      }),
      fetcher,
    ),
  );
  expect(fetcher).toHaveBeenCalledTimes(1);
  expect(lost.body).toMatchObject({
    available: true,
    ok: false,
    code: "unavailable",
    reason: expect.stringContaining("may or may not have been saved"),
  });
  for (const write of [
    { op: "release", id },
    { op: "delete", id, nonce: id, confirm: id },
    { op: "forget", id },
  ]) {
    fetcher.mockClear();
    await handleMemories(ask(write), fetcher);
    expect(fetcher, write.op).toHaveBeenCalledTimes(1);
  }
  fetcher.mockClear();
  const failedRead = await read(
    handleMemories(ask({ op: "get", id }), fetcher),
  );
  expect(fetcher.mock.calls.length).toBeGreaterThan(1);
  expect(failedRead.body.reason).toContain("can't be read");
});

it("takes each op with exactly its members, and never a channel or a client", async () => {
  withEngine({ tools: on });
  const fetcher = vi.fn();
  const id = "6d3c2a10-0000-4000-8000-00000000b001";
  for (const body of [
    {},
    { op: "dance" },
    { op: "list", limit: 51 },
    { op: "get" },
    { op: "get", id: "not-a-uuid" },
    { op: "capture", content: "" },
    { op: "capture", content: "DEMO", channel: "cli" },
    { op: "capture", content: "DEMO", client: "DEMO terminal" },
    { op: "append", id, text: "DEMO", channel: "app" },
    { op: "update", id },
    { op: "release", id, key: "app:release-0001" },
    { op: "delete", id, nonce: id },
    { op: "undo", id, group: "abc" },
  ]) {
    const answer = await read(handleMemories(ask(body), fetcher));
    expect(answer.status, JSON.stringify(body)).toBe(400);
  }
  expect(fetcher).not.toHaveBeenCalled();
  expect(memoryRequest({ op: "update", id, clear: ["project"] })).toEqual({
    op: "update",
    id,
    clear: ["project"],
  });
  // A memory's whole text fits; the route takes a body that large and no larger.
  expect(
    memoryRequest({ op: "capture", content: "x".repeat(131_072) }),
  ).not.toBeNull();
  expect(bodyLimit("/api/memories")).toBeGreaterThan(131_072 * 2);
  expect(bodyLimit("/api/graph")).toBe(131_072);
  expect(routes["/api/memories"]).toBeTypeOf("function");
});

it("reads every contract fixture, and shows a held memory's hidden characters", () => {
  const ops: Record<string, string> = {
    list: "list",
    held: "get",
    version: "get",
    history: "history",
    trash: "trash",
    projects: "projects",
    capture: "capture",
    release: "release",
    forget: "forget",
    restore: "restore",
    delete: "delete",
    refused: "append",
  };
  for (const [form, op] of Object.entries(ops))
    expect(
      cleanMemoryAnswer(op, fixture(`app_memories.${form}.example.json`)),
      form,
    ).not.toBeNull();
  expect(
    cleanMemoryAnswer("forget", fixture("app_memories.forget.example.json")),
  ).toMatchObject({
    written: {
      op: "forget",
      trash: { nonce: "6d3c2a10-0000-4000-8000-00000000c003", days: 14 },
    },
  });
  // An answer naming another op than the one asked for is not that op's.
  expect(
    cleanMemoryAnswer("release", fixture("app_memories.capture.example.json")),
  ).toBeNull();
  // A write burst held by the quarantine (SB-P5-01) reads as any other hold.
  const burst = fixture("app_memories.held.example.json");
  burst.memory.held = { reason: "burst", signals: ["burst"] };
  expect(cleanMemoryAnswer("get", burst)).toMatchObject({
    memory: { held: { reason: "burst" } },
  });
  expect(visibleText("DEMO​note\u{e0041}")).toBe("DEMO[U+200B]note[U+E0041]");
});

// A person's own steps (SB-P3-22): the review queue, a release of several at the revisions read, and a capsule slot.
it("takes a person's own steps with exactly their members, and reads their answers from the contract", async () => {
  withEngine({ tools: on });
  const fetcher = vi.fn();
  const id = "6d3c2a10-0000-4000-8000-00000000b001";
  for (const body of [
    { op: "review", limit: 51 },
    { op: "review", reason: "suspicious" },
    { op: "review", tag: "wendle" },
    { op: "capsule", id, capsule: "core" },
    { op: "capsule", id, capsule: "none", slot: "identity" },
    { op: "capsule", id, capsule: "core", slot: "favourites" },
    { op: "capsule", id, capsule: "team", slot: "identity" },
    { op: "capsule", id, capsule: "core", slot: "identity", channel: "cli" },
    { op: "release_all", memories: [] },
    { op: "release_all", memories: [{ id }] },
    { op: "release_all", memories: [{ id, expected: 1, channel: "cli" }] },
    {
      op: "release_all",
      memories: Array.from({ length: 51 }, () => ({ id, expected: 1 })),
    },
  ]) {
    const answer = await read(handleMemories(ask(body), fetcher));
    expect(answer.status, JSON.stringify(body)).toBe(400);
  }
  expect(fetcher).not.toHaveBeenCalled();
  for (const body of [
    { op: "review", scope: "shared", reason: "burst", limit: 50 },
    { op: "capsule", id, capsule: "project", slot: "decisions", expected: 2 },
    { op: "capsule", id, capsule: "none" },
    { op: "release_all", memories: [{ id, expected: 3 }] },
  ])
    expect(memoryRequest(body), JSON.stringify(body)).toEqual(body);

  for (const [form, op] of [
    ["review", "review"],
    ["release_all", "release_all"],
    ["capsule", "capsule"],
    ["slotted", "get"],
  ])
    expect(
      cleanMemoryAnswer(op, fixture(`app_memories.${form}.example.json`)),
      form,
    ).not.toBeNull();
  expect(
    cleanMemoryAnswer("capsule", fixture("app_memories.capsule.example.json")),
  ).toMatchObject({
    written: { op: "capsule", capsule: { kind: "project", slot: "decisions" } },
  });
  // A slot of the other capsule's kind, or a queue item carrying words, is not the contract's.
  const slotted = fixture("app_memories.slotted.example.json");
  slotted.memory.capsule = { kind: "core", slot: "decisions" };
  expect(cleanMemoryAnswer("get", slotted)).toBeNull();
  const review = fixture("app_memories.review.example.json");
  const listedWords = cleanMemoryAnswer("review", {
    ...review,
    review: [{ ...review.review[0], content: "DEMO: words" }],
  }) as { review: Record<string, unknown>[] };
  expect(listedWords.review[0]).not.toHaveProperty("content");
  // An engine from before SB-P3-22 sends no slot: none.
  expect(
    cleanMemoryAnswer("get", fixture("app_memories.held.example.json")),
  ).toMatchObject({ memory: { capsule: null } });
  // A held memory in a list is served without its slot, whatever came.
  const listed = fixture("app_memories.list.example.json");
  listed.memories[1].capsule = { kind: "core", slot: "identity" };
  expect(
    (cleanMemoryAnswer("list", listed) as { memories: { capsule: unknown }[] })
      .memories[1].capsule,
  ).toBeNull();

  // Through the route: a release of several is a write, sent once.
  const lost = vi.fn(async () => new Response("", { status: 502 }));
  await handleMemories(
    ask({ op: "release_all", memories: [{ id, expected: 1 }] }),
    lost,
  );
  await handleMemories(
    ask({ op: "capsule", id, capsule: "core", slot: "identity" }),
    lost,
  );
  expect(lost).toHaveBeenCalledTimes(2);
  const answered = vi.fn(async () =>
    rpc(fixture("app_memories.release_all.example.json")),
  );
  const released = await read(
    handleMemories(
      ask({ op: "release_all", memories: [{ id, expected: 1 }] }),
      answered,
    ),
  );
  expect(released.body).toMatchObject({
    available: true,
    ok: true,
    op: "release_all",
    results: [
      { result: "released", revision: 2 },
      { result: "conflict", revision: 2 },
      { result: "unknown", revision: null },
    ],
  });
  expect(called(answered.mock.calls[0][1] as RequestInit)).toEqual({
    name: "app_memories",
    arguments: { op: "release_all", memories: [{ id, expected: 1 }] },
  });
});

// A person's share (run 14): offered only while the engine's listing names app_memories' op share (memory.share on,
// and memory scopes on); otherwise GET is {available} and a POST of op share the 400 it was, without calling the engine.
it("passes a person's share on only while the engine's listing names it", async () => {
  const id = "6d3c2a10-0000-4000-8000-00000000b201";
  withEngine({ tools: on });
  const fetcher = vi.fn();
  expect((await read(handleMemories(ask()))).body).toEqual({ available: true });
  for (const body of [
    { op: "share", id },
    { op: "share", id, scope: "private", expected: 2 },
  ])
    expect((await read(handleMemories(ask(body), fetcher))).status).toBe(400);
  expect(fetcher).not.toHaveBeenCalled();
  for (const [, close] of closedEngines) {
    close();
    expect(
      (await read(handleMemories(ask({ op: "share", id }), fetcher))).status,
    ).toBe(400);
  }
  expect(fetcher).not.toHaveBeenCalled();

  withEngine({ tools: on });
  resetEngine(on, {
    app_memories: ["op", "op=list", "op=share", "id", "scope", "expected"],
  });
  expect((await read(handleMemories(ask()))).body).toEqual({
    available: true,
    share: true,
  });
  // Each with exactly its members: never a channel, a key or a scope the engine does not name.
  for (const body of [
    { op: "share" },
    { op: "share", id, scope: "company" },
    { op: "share", id, key: "share-key-0001" },
    { op: "share", id, channel: "cli" },
    { op: "share", id, expected: 0 },
  ])
    expect(memoryRequest(body), JSON.stringify(body)).toBeNull();
  const answered = vi.fn(async () =>
    rpc(fixture("app_memories.share.example.json")),
  );
  const shared = await read(
    handleMemories(ask({ op: "share", id, expected: 1 }), answered),
  );
  expect(called(answered.mock.calls[0][1] as RequestInit)).toEqual({
    name: "app_memories",
    arguments: { op: "share", id, expected: 1 },
  });
  expect(shared.body).toMatchObject({
    available: true,
    ok: true,
    written: {
      op: "share",
      scope: "shared",
      owner: "household",
      revision: 2,
      projectLeft: false,
      project: { slug: "larrow-plot", created: true },
    },
  });
  // Its refusals are the contract's codes, passed on with the engine's words.
  const refused = vi.fn(async () =>
    rpc(fixture("app_memories.share.refused.example.json")),
  );
  expect(
    (
      await read(
        handleMemories(ask({ op: "share", id, scope: "private" }), refused),
      )
    ).body,
  ).toMatchObject({
    available: true,
    ok: false,
    code: "scopes_off",
  });
  expect(
    cleanMemoryAnswer("share", {
      ok: false,
      code: "no_author",
      reason: "DEMO: no person wrote it.",
    }),
  ).toMatchObject({ code: "no_author" });
  expect(
    cleanMemoryAnswer("share", {
      ...fixture("app_memories.share.example.json"),
      projectLeft: true,
    }),
  ).toMatchObject({
    written: { projectLeft: true },
  });
  // A write: never sent twice.
  const lost = vi.fn(async () => new Response("", { status: 502 }));
  await handleMemories(ask({ op: "share", id }), lost);
  expect(lost).toHaveBeenCalledTimes(1);
});

// A person's own link steps (run 17; API-21): a memory's connections, the stale queue, a person's link and unlink, still
// true and a flag clear. Each with exactly its members, never a channel; passed on only while the engine serves the
// view (memory.tools on), and otherwise the 400 each was before them, as a request the facade does not know; each answer
// read from the contract.
it("passes a person's link steps on only while the engine serves the view, and reads their answers from the contract", async () => {
  const id = "6d3c2a10-0000-4000-8000-00000000e001";
  const target = "6d3c2a10-0000-4000-8000-00000000e002";
  const steps = [
    { op: "connections", id },
    { op: "connections", id, type: "relates_to", limit: 50 },
    { op: "stale" },
    { op: "stale", scope: "private", cursor: "abc", limit: 20 },
    { op: "link", id, target },
    { op: "link", id, target, type: "caused_by" },
    { op: "unlink", id, target, type: "similar_to" },
    { op: "still_true", id, expected: 3 },
    { op: "clear_flags", id },
  ];
  for (const body of steps)
    expect(memoryRequest(body), JSON.stringify(body)).toEqual(body);
  for (const body of [
    { op: "connections" },
    { op: "connections", id, limit: 51 },
    { op: "connections", id, type: "Relates To" },
    { op: "stale", reason: "burst" },
    { op: "link", id },
    { op: "link", id, target: "not-a-memory" },
    { op: "link", id, target, confidence: 0.5 },
    { op: "link", id, target, channel: "cli" },
    { op: "unlink", id, target, client: "DEMO" },
    { op: "still_true", id, expected: 0 },
    { op: "still_true", id, key: "still-true-0001" },
    { op: "clear_flags", id, clear: ["retracted_sources"] },
  ])
    expect(memoryRequest(body), JSON.stringify(body)).toBeNull();

  // memory.tools off (no app_memories in the listing), demo mode or no engine: each is the 400 it was, nothing called.
  const fetcher = vi.fn();
  withEngine();
  for (const body of steps)
    expect(
      (await read(handleMemories(ask(body), fetcher))).status,
      JSON.stringify(body),
    ).toBe(400);
  for (const [, close] of closedEngines) {
    close();
    for (const body of steps)
      expect((await read(handleMemories(ask(body), fetcher))).status).toBe(400);
  }
  expect(fetcher).not.toHaveBeenCalled();
  // An op the facade does not know was a 400 before, as it is now: the other ops answer as they did.
  withEngine();
  expect(
    (await read(handleMemories(ask({ op: "list" }), fetcher))).body,
  ).toEqual({ available: false });

  // On: passed on as the app, its answer the contract's.
  withEngine({ tools: on });
  for (const [form, body] of [
    ["connections", { op: "connections", id }],
    ["stale", { op: "stale" }],
    ["link", { op: "link", id, target, type: "caused_by" }],
    ["unlink", { op: "unlink", id, target, type: "similar_to" }],
    ["still_true", { op: "still_true", id, expected: 1 }],
    ["clear_flags", { op: "clear_flags", id, expected: 1 }],
  ] as const) {
    const answered = vi.fn(async () =>
      rpc(fixture(`app_memories.${form}.example.json`)),
    );
    const answer = await read(handleMemories(ask(body), answered));
    expect(called(answered.mock.calls[0][1] as RequestInit)).toEqual({
      name: "app_memories",
      arguments: body,
    });
    expect(answer.body, form).toMatchObject({ available: true, ok: true });
  }
  expect(
    cleanMemoryAnswer(
      "connections",
      fixture("app_memories.connections.example.json"),
    ),
  ).toMatchObject({ ok: true, truncated: false });
  // Outside the contract: a suggestion with a link id, a held neighbour's words, a person's link of no verification.
  const found = fixture("app_memories.connections.example.json");
  const [made, , suggested] = found.connections;
  expect(
    cleanMemoryAnswer("connections", {
      ...found,
      connections: [{ ...suggested, linkId: made.linkId }],
    }),
  ).toBeNull();
  expect(
    cleanMemoryAnswer("connections", {
      ...found,
      connections: [
        { ...suggested, memory: { ...suggested.memory, preview: "DEMO" } },
      ],
    }),
  ).toBeNull();
  expect(
    cleanMemoryAnswer("link", {
      ...fixture("app_memories.link.example.json"),
      verification: "explicit",
    }),
  ).toBeNull();
  expect(
    cleanMemoryAnswer("stale", {
      ...fixture("app_memories.stale.example.json"),
      count: -1,
    }),
  ).toBeNull();
  // Still true's refusal of a memory marked wrong is the contract's code.
  expect(
    cleanMemoryAnswer("still_true", {
      ...fixture("app_memories.refused.example.json"),
      code: "retracted",
    }),
  ).toMatchObject({ ok: false, code: "retracted" });
});

// The old memory (SB-P5-17): a read and a write, passed on only while the engine serves the view; their answers rebuilt from the contract.
it("passes the old memory's ops on only while the engine serves them, and refuses what they don't take", async () => {
  const id = "6d3c2a10-0000-4000-8000-00000000f001";
  // Off, in the demo and with the engine's listing lacking the tool: the requests they were before, sent nowhere.
  withEngine();
  const off = vi.fn();
  for (const request of [{ op: "resurface" }, { op: "resurface_dismiss", id }])
    expect((await read(handleMemories(ask(request), off))).status).toBe(400);
  expect(off).not.toHaveBeenCalled();
  withEngine({ tools: on });
  const example = fixture("app_memories.resurface.example.json");
  const reading = vi.fn(async () => rpc(example));
  const answered = await read(
    handleMemories(ask({ op: "resurface" }), reading),
  );
  expect(called(reading.mock.calls[0][1] as RequestInit)).toEqual({
    name: "app_memories",
    arguments: { op: "resurface" },
  });
  expect(answered.body).toEqual({
    available: true,
    ok: true,
    day: example.day,
    pick: example.pick,
  });
  const none = vi.fn(async () =>
    rpc(fixture("app_memories.resurface.none.example.json")),
  );
  expect(
    (await read(handleMemories(ask({ op: "resurface" }), none))).body,
  ).toMatchObject({ available: true, ok: true, pick: null });
  const done = fixture("app_memories.resurface_dismiss.example.json");
  const writing = vi.fn(async () => rpc(done));
  const dismissed = await read(
    handleMemories(ask({ op: "resurface_dismiss", id }), writing),
  );
  expect(called(writing.mock.calls[0][1] as RequestInit)).toEqual({
    name: "app_memories",
    arguments: { op: "resurface_dismiss", id },
  });
  expect(dismissed.body).toEqual({ available: true, ...done });
  // A member an op doesn't take is refused here, sent nowhere: no day to choose, no channel, no other reader.
  const never = vi.fn();
  for (const bad of [
    { op: "resurface", scope: "shared" },
    { op: "resurface", at: "2026-01-01T00:00:00Z" },
    { op: "resurface_dismiss" },
    { op: "resurface_dismiss", id: "x" },
    { op: "resurface_dismiss", id, channel: "cli" },
    { op: "resurface_dismiss", id, reader: "sam" },
  ])
    expect((await read(handleMemories(ask(bad), never))).status).toBe(400);
  expect(never).not.toHaveBeenCalled();
  // An answer outside the contract is unavailable, in a read's words or a write's.
  const wrong = vi.fn(async () => rpc({ ok: true, pick: "DEMO" }));
  expect(
    (await read(handleMemories(ask({ op: "resurface" }), wrong))).body,
  ).toEqual({
    available: true,
    ok: false,
    code: "unavailable",
    reason: "Memories can't be read right now. Try again later.",
  });
  expect(
    (await read(handleMemories(ask({ op: "resurface_dismiss", id }), wrong)))
      .body,
  ).toEqual({
    available: true,
    ok: false,
    code: "unavailable",
    reason:
      "The engine's answer didn't arrive, so this may or may not have been saved. Read the memory again before trying once more.",
  });
});

// The brief (SB-P5-08): a read, passed on only while the engine serves the view; its answer rebuilt from the contract.
it("passes the brief on only while the engine serves it, as a read, and refuses what it doesn't take", async () => {
  // Off, in the demo and with the engine's listing lacking the tool: the request it was before, sent nowhere.
  withEngine();
  const off = vi.fn();
  expect((await read(handleMemories(ask({ op: "brief" }), off))).status).toBe(
    400,
  );
  expect(off).not.toHaveBeenCalled();
  withEngine({ tools: on });
  const fetcher = vi.fn(async () =>
    rpc(fixture("app_memories.brief.example.json")),
  );
  const answered = await read(
    handleMemories(
      ask({ op: "brief", scope: "shared", project: "pellow-fair" }),
      fetcher,
    ),
  );
  expect(called(fetcher.mock.calls[0][1] as RequestInit)).toEqual({
    name: "app_memories",
    arguments: { op: "brief", scope: "shared", project: "pellow-fair" },
  });
  const example = fixture("app_memories.brief.example.json");
  expect(answered.body).toMatchObject({
    available: true,
    ok: true,
    says: example.says,
    due: { count: example.due.count },
    youOwe: { count: example.youOwe.count },
    standing: { count: example.standing.count },
  });
  // A member the op doesn't take is refused here, sent nowhere.
  fetcher.mockClear();
  for (const bad of [
    { op: "brief", limit: 5 },
    { op: "brief", scope: "team" },
    { op: "brief", id: "x" },
  ])
    expect((await read(handleMemories(ask(bad), fetcher))).status).toBe(400);
  expect(fetcher).not.toHaveBeenCalled();
  // An answer outside the contract is unavailable, in a read's words.
  fetcher.mockResolvedValueOnce(rpc({ ok: true, says: "DEMO" }));
  expect(
    (await read(handleMemories(ask({ op: "brief" }), fetcher))).body,
  ).toEqual({
    available: true,
    ok: false,
    code: "unavailable",
    reason: "Memories can't be read right now. Try again later.",
  });
});
