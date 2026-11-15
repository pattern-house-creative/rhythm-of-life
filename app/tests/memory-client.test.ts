import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, expect, it, vi } from "vitest";
import { handleMemories } from "../server/memories";
import { memoryClient } from "../src/apiClient";
import { resetEngine } from "../server/engine";
import { allAppTools, rpc, withEngine } from "./engine";

// The Memories view's client (src/apiClient.ts, memoryClient) through the app's real facade (server/memories.ts), whose
// engine answers each op with the contract's fixture: what the view reads (reply.memory, reply.version, reply.history,
// reply.written) comes back whole for every op, and a request the facade refuses itself says nothing was sent.
// Fictional data only.
afterEach(() => {
  vi.unstubAllEnvs();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});
const fixture = (name: string) =>
  JSON.parse(
    readFileSync(
      resolve(
        process.cwd(),
        "../contracts/fixtures",
        `app_memories.${name}.example.json`,
      ),
      "utf8",
    ),
  );
const id = (n: string) => `6d3c2a10-0000-4000-8000-00000000${n}`;

// The browser's fetch, answered by the facade; the facade's engine answers with the fixture each op names.
function served(answers: Record<string, string>) {
  const engine = vi.fn(async (_url: unknown, init?: RequestInit) => {
    const args = JSON.parse(String(init?.body)).params.arguments;
    return rpc(
      fixture(answers[args.op === "get" && args.version ? "version" : args.op]),
    );
  });
  vi.stubGlobal(
    "fetch",
    vi.fn(async (path: string, init: RequestInit) => {
      expect(path).toBe("/api/memories");
      return handleMemories(
        new Request("http://rhythm.example.invalid/api/memories", {
          method: init.method,
          headers: init.headers,
          body: init.body,
        }),
        engine as unknown as typeof fetch,
      );
    }),
  );
  return engine;
}

it("gets every op's answer through the facade in the shape the view reads", async () => {
  withEngine({ tools: [...allAppTools, "app_memories"] });
  const engine = served({
    list: "list",
    trash: "trash",
    projects: "projects",
    get: "held",
    version: "version",
    history: "history",
    capture: "capture",
    release: "release",
    forget: "forget",
    restore: "restore",
    delete: "delete",
    status: "refused",
  });
  expect(await memoryClient.available()).toBe(true);
  const call = (request: Record<string, unknown>) =>
    memoryClient.call<Record<string, unknown>>(request) as Promise<
      Record<string, unknown>
    >;
  const ok = (reply: Record<string, unknown>) => {
    expect(reply).toMatchObject({ available: true, ok: true });
    return reply;
  };
  expect(
    (ok(await call({ op: "list", limit: 20 })).memories as unknown[]).length,
  ).toBeGreaterThan(0);
  expect(
    (ok(await call({ op: "trash", limit: 20 })).trash as unknown[]).length,
  ).toBeGreaterThan(0);
  expect(
    (ok(await call({ op: "projects" })).projects as unknown[]).length,
  ).toBe(1);
  const got = ok(await call({ op: "get", id: id("b002") }));
  expect(got.memory).toMatchObject({
    id: id("b002"),
    held: { reason: "instruction" },
  });
  expect(got.version).toBeNull();
  const version = ok(await call({ op: "get", id: id("b001"), version: 1 }));
  expect(version.memory).toBeNull();
  expect(version.version).toMatchObject({ id: id("b001"), revision: 1 });
  const history = ok(await call({ op: "history", id: id("b001"), limit: 50 }));
  expect(history.history).toMatchObject({ id: id("b001"), revision: 3 });
  expect(
    (history.history as { changes: unknown[] }).changes.length,
  ).toBeGreaterThan(0);
  for (const [request, op] of [
    [
      {
        op: "capture",
        content: "DEMO: the Wendle plot's gate code.",
        scope: "shared",
        key: "app:demo-capture-1",
      },
      "capture",
    ],
    [{ op: "release", id: id("b002"), expected: 1 }, "release"],
    [{ op: "forget", id: id("b003") }, "forget"],
    [{ op: "restore", id: id("b003"), nonce: id("c003") }, "restore"],
    [
      { op: "delete", id: id("b003"), nonce: id("c003"), confirm: id("b003") },
      "delete",
    ],
  ] as const) {
    const reply = ok(await call(request));
    expect(reply.written).toMatchObject({ op });
  }
  expect(
    (
      ok(await call({ op: "forget", id: id("b003") })).written as {
        trash: unknown;
      }
    ).trash,
  ).toMatchObject({
    nonce: id("c003"),
  });
  // A refusal comes back as the engine's own words.
  expect(
    await call({
      op: "status",
      id: id("b001"),
      status: "canonical",
      expected: 1,
    }),
  ).toMatchObject({
    available: true,
    ok: false,
    code: "conflict",
    revision: 2,
  });
  expect(engine).toHaveBeenCalledTimes(13);
});

it("says nothing was sent when the facade refuses the request itself", async () => {
  withEngine({ tools: [...allAppTools, "app_memories"] });
  const engine = served({ capture: "capture" });
  // Under the box's 131 072 characters, but more than the route takes as UTF-8.
  const large = await memoryClient.call({
    op: "capture",
    content: "界".repeat(100_000),
    scope: "shared",
  });
  expect(large).toMatchObject({
    available: true,
    ok: false,
    code: "too_large",
  });
  expect((large as { reason: string }).reason).toContain("Nothing was sent");
  const odd = await memoryClient.call({ op: "list", channel: "cli" });
  expect(odd).toMatchObject({ available: true, ok: false, code: "invalid" });
  expect((odd as { reason: string }).reason).toContain("Nothing was sent");
  expect(engine).not.toHaveBeenCalled();
});

// A person's own steps (SB-P3-22) through the facade: the queue and a release of several keep the engine's shape, and a
// capsule op comes back as a write with the slot it is in.
it("gets the review queue, a release of several and a capsule slot through the facade", async () => {
  withEngine({ tools: [...allAppTools, "app_memories"] });
  const engine = served({
    review: "review",
    release_all: "release_all",
    capsule: "capsule",
    get: "slotted",
  });
  const call = (request: Record<string, unknown>) =>
    memoryClient.call<Record<string, unknown>>(request) as Promise<
      Record<string, unknown>
    >;
  const queue = await call({ op: "review", limit: 20 });
  expect(queue).toMatchObject({
    available: true,
    ok: true,
    count: 2,
    next: null,
  });
  expect((queue.review as { reason: string }[]).map((i) => i.reason)).toEqual([
    "burst",
    "instruction",
  ]);
  const released = await call({
    op: "release_all",
    memories: [{ id: id("b002"), expected: 1 }],
  });
  expect(released).toMatchObject({
    available: true,
    ok: true,
    op: "release_all",
  });
  expect(
    (released.results as { result: string }[]).map((r) => r.result),
  ).toEqual(["released", "conflict", "unknown"]);
  const slotted = await call({
    op: "capsule",
    id: id("b001"),
    capsule: "project",
    slot: "decisions",
    expected: 2,
  });
  expect(slotted.written).toMatchObject({
    op: "capsule",
    capsule: { kind: "project", slot: "decisions" },
  });
  expect((await call({ op: "get", id: id("b001") })).memory).toMatchObject({
    capsule: { kind: "project", slot: "decisions" },
  });
  expect(engine).toHaveBeenCalledTimes(4);
});

// A person's share (run 14): offered only while the facade says the engine takes it, and its answer read whole.
it("offers a person's share only while the engine takes it, and reads its answer whole", async () => {
  const tools = [...allAppTools, "app_memories"];
  withEngine({ tools });
  served({ share: "share" });
  expect(await memoryClient.available()).toBe(true);
  expect(await memoryClient.sharing!()).toBe(false);
  // Not taken now: refused by the facade itself, so nothing was sent.
  expect(
    await memoryClient.call({ op: "share", id: id("b201") }),
  ).toMatchObject({
    available: true,
    ok: false,
    code: "invalid",
  });
  resetEngine(tools, { app_memories: ["op", "op=share"] });
  expect(await memoryClient.sharing!()).toBe(true);
  expect(
    await memoryClient.call({ op: "share", id: id("b201"), expected: 1 }),
  ).toMatchObject({
    available: true,
    ok: true,
    written: {
      op: "share",
      id: id("b201"),
      scope: "shared",
      projectLeft: false,
    },
  });
  vi.stubGlobal(
    "fetch",
    vi.fn(async () => {
      throw new TypeError("offline");
    }),
  );
  expect(await memoryClient.sharing!()).toBe(false);
});

// A memory's truth and a change's retraction cascade (SB-P4-11, the app half of D-192) through the facade: get and a
// list carry each memory's state, what replaced it and its retracted sources; a write whose cascade did something names
// it. An engine from before them sends none, which reads as current with nothing; a state the contract lacks is not
// read, and a cascade that doesn't check out is left out without losing the write.
it("reads a memory's truth and a write's cascade through the facade, and an older engine's answers as before", async () => {
  withEngine({ tools: [...allAppTools, "app_memories"] });
  const engine = served({ get: "replaced", status: "cascade", list: "list" });
  const call = (request: Record<string, unknown>) =>
    memoryClient.call<Record<string, unknown>>(request) as Promise<
      Record<string, unknown>
    >;
  const got = await call({ op: "get", id: id("c001") });
  expect(got.memory).toMatchObject({
    id: id("c001"),
    state: "replaced",
    replacedBy: id("c002"),
    retractedSources: [],
  });
  const marked = await call({
    op: "status",
    id: id("c002"),
    status: "deprecated",
    expected: 2,
  });
  expect(marked.written).toMatchObject({
    op: "status",
    cascade: {
      reopened: [id("c001")],
      reclosed: [],
      flagged: [id("c003")],
      unflagged: [],
    },
  });
  const listed = await call({ op: "list", limit: 20 });
  for (const m of listed.memories as Record<string, unknown>[])
    expect(m).toMatchObject({
      state: "current",
      replacedBy: null,
      retractedSources: [],
    });
  expect(engine).toHaveBeenCalledTimes(3);
});

it("reads an older engine's memories as current, and keeps the contract's truth or nothing", async () => {
  const { cleanMemoryAnswer, cleanPassedAnswer, cascadeWords } =
    await import("../src/memories");
  const replaced = fixture("replaced");
  const {
    state: _s,
    replacedBy: _r,
    retractedSources: _f,
    ...older
  } = replaced.memory;
  expect(cleanMemoryAnswer("get", { ok: true, memory: older })).toMatchObject({
    memory: { state: "current", replacedBy: null, retractedSources: [] },
  });
  for (const bad of [
    { state: "superseded" },
    { state: "replaced", replacedBy: null },
    { state: "current", replacedBy: id("c002") },
    { retractedSources: ["not-a-memory"] },
  ])
    expect(
      cleanMemoryAnswer("get", {
        ok: true,
        memory: { ...replaced.memory, ...bad },
      }),
    ).toBeNull();
  // A cascade that doesn't check out says nothing; the write is still read.
  const cascade = fixture("cascade");
  for (const odd of [
    { reopened: ["x"] },
    { ...cascade.cascade, moved: undefined, reopened: "x" },
    null,
  ])
    expect(
      cleanMemoryAnswer("status", { ...cascade, cascade: odd }),
    ).toMatchObject({
      written: { op: "status", cascade: null },
    });
  const { cascade: _c, ...plain } = cascade;
  const read = cleanMemoryAnswer("status", plain);
  expect(read).toMatchObject({ written: { op: "status", cascade: null } });
  // The browser's check of what the facade passed on reads the same.
  expect(
    cleanPassedAnswer("status", cleanMemoryAnswer("status", cascade)),
  ).toEqual(cleanMemoryAnswer("status", cascade));
  expect(cascadeWords(null)).toBe("");
  expect(
    cascadeWords({ reopened: [], reclosed: [], flagged: [], unflagged: [] }),
  ).toBe("");
});

// The map's kept dismissals (SB-P4-05): the list, Discover's Dismiss and taking one back, through the facade in the
// engine's shape, each answer checked against the contract; a request outside an op's shape is refused before any call.
it("gets the map's dismissals, a Dismiss and a take-back through the facade", async () => {
  withEngine({ tools: [...allAppTools, "app_memories"] });
  const engine = served({
    dismissals: "dismissals",
    dismiss: "dismiss",
    undismiss: "undismiss",
  });
  const call = (request: Record<string, unknown>) =>
    memoryClient.call<Record<string, unknown>>(request) as Promise<
      Record<string, unknown>
    >;
  const listed = await call({ op: "dismissals" });
  expect(listed).toMatchObject({ available: true, ok: true, truncated: false });
  expect((listed.dismissals as { by: string }[]).map((d) => d.by)).toEqual([
    "person",
    "ai_client",
  ]);
  const link = fixture("dismiss").link as string;
  expect(await call({ op: "dismiss", link })).toMatchObject({
    available: true,
    ok: true,
    op: "dismiss",
    link,
    removed: false,
    dismissal: fixture("dismiss").dismissal,
  });
  const dismissal = fixture("undismiss").dismissal as string;
  expect(await call({ op: "undismiss", dismissal })).toMatchObject({
    ok: true,
    op: "undismiss",
    dismissal,
    unchanged: false,
  });
  expect(engine).toHaveBeenCalledTimes(3);
  // Not a link's id or a dismissal's: the facade refuses it, and nothing is sent.
  for (const request of [
    { op: "dismiss", link: "similar_to:a:b" },
    { op: "dismiss" },
    { op: "undismiss", dismissal: "stars:a:b:similar_to" },
    { op: "dismissals", limit: 51 },
  ])
    expect(await call(request)).toMatchObject({ ok: false, code: "invalid" });
  expect(engine).toHaveBeenCalledTimes(3);
});

it("gets a person's link steps through the facade in the shape the view reads", async () => {
  withEngine({ tools: [...allAppTools, "app_memories"] });
  const engine = served({
    connections: "connections",
    stale: "stale",
    link: "link",
    unlink: "unlink",
    still_true: "still_true",
    clear_flags: "clear_flags",
  });
  const call = (request: Record<string, unknown>) =>
    memoryClient.call<Record<string, unknown>>(request) as Promise<
      Record<string, unknown>
    >;
  const memory = id("e001");
  const found = await call({ op: "connections", id: memory });
  expect(found).toMatchObject({ available: true, ok: true, id: memory });
  expect(
    (found.connections as { made: boolean; type: string }[]).map(
      (c) => `${c.made}:${c.type}`,
    ),
  ).toEqual(["true:caused_by", "true:relates_to", "false:similar_to"]);
  const stale = await call({ op: "stale" });
  expect(stale).toMatchObject({ ok: true, count: 2, next: null });
  expect((stale.stale as { reason: string }[]).map((s) => s.reason)).toEqual([
    "not_confirmed",
    "retracted_source",
  ]);
  expect(
    await call({ op: "link", id: memory, target: id("e002") }),
  ).toMatchObject({
    ok: true,
    op: "link",
    verification: "verified",
    created: true,
  });
  expect(
    await call({
      op: "unlink",
      id: memory,
      target: id("e004"),
      type: "similar_to",
    }),
  ).toMatchObject({ ok: true, op: "unlink", dismissed: ["similar_to"] });
  expect(
    await call({ op: "still_true", id: id("e012"), expected: 1 }),
  ).toMatchObject({ ok: true, op: "still_true", retracted: [id("e013")] });
  expect(
    await call({ op: "clear_flags", id: id("e012"), expected: 1 }),
  ).toMatchObject({
    ok: true,
    written: { op: "clear_flags", unchanged: false, revision: 1 },
  });
  expect(engine).toHaveBeenCalledTimes(6);
  // Not one the facade takes: refused here, nothing sent.
  for (const request of [
    { op: "link", id: memory },
    { op: "still_true", id: memory, channel: "cli" },
    { op: "stale", limit: 51 },
  ])
    expect(await call(request)).toMatchObject({ ok: false, code: "invalid" });
  expect(engine).toHaveBeenCalledTimes(6);
});

it("takes a preview the engine cut by code points, an emoji in it", async () => {
  const { cleanMemoryAnswer } = await import("../src/memories");
  // Postgres' left() counts code points: 120 of them with ten emoji are 130 UTF-16 units here.
  const cut = (points: number) => "a".repeat(points - 10) + "🙂".repeat(10);
  const connections = fixture("connections");
  const near = (preview: string) => ({
    ...connections,
    connections: connections.connections.map(
      (c: { memory: Record<string, unknown> }, i: number) =>
        i === 0 ? { ...c, memory: { ...c.memory, preview } } : c,
    ),
  });
  expect(cleanMemoryAnswer("connections", near(cut(120)))).not.toBeNull();
  expect(cleanMemoryAnswer("connections", near(cut(121)))).toBeNull();
  const stale = fixture("stale");
  const due = (preview: string) => ({
    ...stale,
    stale: stale.stale.map((s: Record<string, unknown>, i: number) =>
      i === 0 ? { ...s, preview } : s,
    ),
  });
  expect(cleanMemoryAnswer("stale", due(cut(300)))).not.toBeNull();
  expect(cleanMemoryAnswer("stale", due(cut(301)))).toBeNull();
});

// SB-P7-01: a capture connection's channel is one the view reads, on a held memory and in a memory's history; any other
// word is still refused, and a trash row, which a capture never writes, keeps its own.
it("reads a capture connection's channel in the review queue and a memory's history", async () => {
  const { cleanMemoryAnswer } = await import("../src/memories");
  const review = fixture("review");
  const held = (channel: string) => ({
    ...review,
    review: review.review.map((i: Record<string, unknown>, n: number) =>
      n === 0 ? { ...i, channel, client: "ci-main" } : i,
    ),
  });
  expect(cleanMemoryAnswer("review", held("capture"))).not.toBeNull();
  expect(cleanMemoryAnswer("review", held("webhook"))).toBeNull();
  const history = fixture("history");
  const changed = (channel: string) => ({
    ...history,
    changes: history.changes.map((c: Record<string, unknown>, n: number) =>
      n === 0 ? { ...c, channel, client: "ci-main" } : c,
    ),
  });
  expect(cleanMemoryAnswer("history", changed("capture"))).not.toBeNull();
  expect(cleanMemoryAnswer("history", changed("webhook"))).toBeNull();
});
