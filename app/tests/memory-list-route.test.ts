import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, expect, it, vi } from "vitest";
import { handleMemories } from "../server/memories";
import { resetEngine } from "../server/engine";
import {
  cleanMemoryAnswer,
  cleanPassedAnswer,
  memoryRequest,
  projectSlug,
} from "../src/memories";
import { allAppTools, called, closedEngines, rpc, withEngine } from "./engine";

// The full memory UI's list through the facade (SB-P6-01; contracts/README.md 2.2): each op passed on only while the engine
// serves the view, and share_all only while its listing names the share; otherwise each is the 400 it was before them, sent
// nowhere. Each op with exactly its members, its answer rebuilt from the contract, a write never sent twice. Fictional data only.
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

const id = "6d3c2a10-0000-4000-8000-00000000f201";
const nonce = "6d3c2a10-0000-4000-8000-00000000f202";
const group = "djEuNmQzYzJhMTA";

// Each op, as the view sends it, with the fixture of its answer.
const steps: [Record<string, unknown>, string][] = [
  [{ op: "tags", project: "wendle-plot", status: "draft" }, "tags"],
  [{ op: "changes", since: "2026-11-01T09:30:00Z" }, "changes"],
  [
    {
      op: "capsule_status",
      capsule: "project",
      project: "wendle",
      scope: "shared",
    },
    "capsule_status",
  ],
  [{ op: "settings" }, "settings"],
  [{ op: "undo_group", group }, "undo_group"],
  [{ op: "forget_all", memories: [{ id, expected: 1 }] }, "forget_all"],
  [{ op: "restore_all", memories: [{ id, nonce }] }, "restore_all"],
  [{ op: "delete_all", memories: [{ id, nonce, confirm: id }] }, "delete_all"],
  [
    {
      op: "project_create",
      name: "Canal Walks",
      slug: "canal-walks",
      description: "DEMO",
      aliases: ["towpath"],
      scope: "shared",
    },
    "project_create",
  ],
  [{ op: "project_update", id, archived: true, expected: 1 }, "project_update"],
  [{ op: "project_delete", id, expected: 2 }, "project_delete"],
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
  // GET is as it was, too.
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
    expect(cleanPassedAnswer(form, rest), form).toEqual(rest);
    // Anything outside the contract is dropped, and an answer that doesn't check out is no answer.
    const extra = { ...example, secret: "DEMO: outside the contract" };
    expect(JSON.stringify(cleanMemoryAnswer(form, extra))).not.toContain(
      "DEMO: outside the contract",
    );
    expect(cleanMemoryAnswer(form, { ok: true }), form).toBeNull();
  }
});

it("takes each op with exactly its members, and never a channel or a client", () => {
  for (const [request] of steps) {
    expect(memoryRequest(request), String(request.op)).toEqual(request);
    expect(memoryRequest({ ...request, channel: "cli" })).toBeNull();
    expect(memoryRequest({ ...request, client: "DEMO" })).toBeNull();
  }
  for (const bad of [
    { op: "tags", limit: 5 },
    { op: "tags", cursor: "abc" },
    { op: "changes", since: "last week" },
    { op: "changes", scope: "company" },
    { op: "capsule_status" },
    { op: "capsule_status", capsule: "none" },
    { op: "capsule_status", capsule: "project" },
    { op: "capsule_status", capsule: "core", project: "wendle" },
    { op: "settings", scope: "shared" },
    { op: "undo_group" },
    { op: "undo_group", group: "not a key" },
    { op: "undo_group", group, key: "undo-group-key-1" },
    { op: "forget_all", memories: [] },
    { op: "forget_all", memories: [{ id }] },
    { op: "forget_all", memories: [{ id, nonce }] },
    {
      op: "forget_all",
      memories: Array.from({ length: 51 }, () => ({ id, expected: 1 })),
    },
    { op: "restore_all", memories: [{ id, expected: 1 }] },
    { op: "delete_all", memories: [{ id, nonce }] },
    // A delete's confirmation is the memory's own id, each one.
    { op: "delete_all", memories: [{ id, nonce, confirm: nonce }] },
    { op: "project_create" },
    { op: "project_create", name: "" },
    { op: "project_create", name: "x".repeat(121) },
    { op: "project_create", name: "DEMO", slug: "Canal Walks" },
    { op: "project_create", name: "DEMO", description: "x".repeat(1001) },
    { op: "project_create", name: "DEMO", aliases: Array(17).fill("a") },
    { op: "project_update", id },
    { op: "project_update", id, expected: 2 },
    { op: "project_update", id, slug: "another" },
    { op: "project_delete" },
  ])
    expect(memoryRequest(bad), JSON.stringify(bad)).toBeNull();
  // A description may be emptied, and aliases too.
  expect(
    memoryRequest({ op: "project_update", id, description: "", aliases: [] }),
  ).not.toBeNull();
});

it("never sends a write twice, and says it may have been saved; a read says it can't be read", async () => {
  withEngine({ tools: on });
  for (const [request] of steps.filter(([r]) => r.op !== "settings")) {
    const fetcher = vi.fn(async () => new Response("no", { status: 500 }));
    const { body } = await read(handleMemories(ask(request), fetcher));
    expect(body.code, String(request.op)).toBe("unavailable");
    const reads = ["tags", "changes", "capsule_status"].includes(
      String(request.op),
    );
    expect(body.reason).toContain(reads ? "can't be read" : "may or may not");
    if (!reads) expect(fetcher).toHaveBeenCalledTimes(1);
  }
});

it("reads the refusals these ops add, and nothing else", () => {
  expect(
    cleanMemoryAnswer("capsule_status", {
      ok: false,
      code: "too_many_candidates",
      reason: "DEMO: too many.",
    }),
  ).toMatchObject({ code: "too_many_candidates" });
  expect(
    cleanMemoryAnswer("project_create", {
      ok: false,
      code: "taken",
      reason: "DEMO: another project answers to it.",
    }),
  ).toMatchObject({ code: "taken" });
  expect(
    cleanMemoryAnswer("settings", { ok: false, code: "channel", reason: "x" }),
  ).toBeNull();
});

it("keeps a held memory's words out of its AI change, whatever came", () => {
  const example = fixture("app_memories.changes.example.json");
  const held = example.groups[0].items[1];
  expect(held.held).toBe(true);
  held.preview = "DEMO: words";
  const clean = cleanMemoryAnswer("changes", example) as {
    groups: { items: { preview: string | null }[] }[];
  };
  expect(clean.groups[0].items[1].preview).toBeNull();
});

it("reads a capsule's slots only in their fixed order, and only those of its kind", () => {
  const example = fixture("app_memories.capsule_status.example.json");
  expect(cleanMemoryAnswer("capsule_status", example)).not.toBeNull();
  expect(
    cleanMemoryAnswer("capsule_status", {
      ...example,
      slots: [...example.slots].reverse(),
    }),
  ).toBeNull();
  expect(
    cleanMemoryAnswer("capsule_status", { ...example, kind: "core" }),
  ).toBeNull();
  expect(
    cleanMemoryAnswer("capsule_status", { ...example, text: "DEMO" }),
  ).not.toHaveProperty("text");
});

it("reads a project an engine from before the project writes sends, with nothing it does not say", () => {
  const read = (name: string) =>
    (
      cleanMemoryAnswer("projects", fixture(name)) as {
        projects: Record<string, unknown>[];
      }
    ).projects[0];
  expect(read("app_memories.projects.full.example.json")).toMatchObject({
    slug: "canal-walks",
    aliases: ["towpath"],
    author: "alex",
    revision: 2,
  });
  expect(read("app_memories.projects.example.json")).toMatchObject({
    slug: "wendle-plot",
    description: "",
    author: null,
    revision: null,
    createdAt: null,
  });
});

it("counts a project's and a tag's length as the database does, in code points", () => {
  const emoji = "\u{1F33F}";
  const base = fixture("app_memories.projects.full.example.json").projects[0];
  const projects = (extra: Record<string, unknown>) =>
    cleanMemoryAnswer("projects", {
      ok: true,
      projects: [{ ...base, ...extra }],
    });
  // 600 emoji are 600 to the database (035) and 1200 to JavaScript: one imported project never takes the whole read down.
  expect(projects({ description: emoji.repeat(600) })).toMatchObject({
    ok: true,
  });
  expect(projects({ name: emoji.repeat(120) })).toMatchObject({ ok: true });
  expect(projects({ aliases: [emoji.repeat(128)] })).toMatchObject({
    ok: true,
  });
  expect(projects({ description: emoji.repeat(1001) })).toBeNull();
  expect(projects({ name: emoji.repeat(121) })).toBeNull();
  const tags = (tag: string) =>
    cleanMemoryAnswer("tags", {
      ok: true,
      tags: [{ tag, count: 1 }],
      truncated: false,
      count: 1,
    });
  expect(tags(emoji.repeat(128))).toMatchObject({ ok: true });
  expect(tags(emoji.repeat(129))).toBeNull();
});

it("reads a share of several answering one memory invalid, and no word the contract doesn't have", () => {
  const shared = fixture("app_memories.share_all.example.json");
  expect(
    (
      cleanMemoryAnswer("share_all", shared) as {
        results: { result: string }[];
      }
    ).results.map((r) => r.result),
  ).toEqual(["moved", "unchanged", "held", "invalid"]);
  expect(
    cleanMemoryAnswer("share_all", {
      ...shared,
      results: [{ ...shared.results[0], result: "linked" }],
    }),
  ).toBeNull();
});

it("reads history's paging words from an engine that says them, and none from one that doesn't", () => {
  const example = fixture("app_memories.history.example.json");
  const said = cleanMemoryAnswer("history", {
    ...example,
    pruned: true,
    oldestKept: 3,
  }) as { history: Record<string, unknown> };
  expect(said.history).toMatchObject({ pruned: true, oldestKept: 3 });
  const { pruned: _pruned, oldestKept: _kept, ...older } = example;
  const none = cleanMemoryAnswer("history", older) as {
    history: Record<string, unknown>;
  };
  expect(none.history).toMatchObject({ pruned: false, oldestKept: 0 });
});

it("passes a share of several on only while the engine's listing names the share", async () => {
  const request = {
    op: "share_all",
    memories: [{ id, expected: 1 }],
    scope: "shared",
  };
  const fetcher = vi.fn();
  for (const [, close] of closedEngines) {
    close();
    expect((await read(handleMemories(ask(request), fetcher))).status).toBe(
      400,
    );
  }
  withEngine({ tools: on });
  expect((await read(handleMemories(ask(request), fetcher))).status).toBe(400);
  expect(fetcher).not.toHaveBeenCalled();

  withEngine({ tools: on });
  resetEngine(on, {
    app_memories: ["op", "op=list", "op=share", "id", "scope", "expected"],
  });
  const taken = vi.fn(async () =>
    rpc(fixture("app_memories.share_all.example.json")),
  );
  const { body } = await read(handleMemories(ask(request), taken));
  expect(called(taken.mock.calls[0][1] as RequestInit)).toEqual({
    name: "app_memories",
    arguments: request,
  });
  expect(body).toMatchObject({
    available: true,
    ok: true,
    op: "share_all",
    results: [
      { result: "moved", projectLeft: true },
      { result: "unchanged" },
      { result: "held" },
      { result: "invalid" },
    ],
  });
  for (const bad of [
    { op: "share_all" },
    { op: "share_all", memories: [{ id }] },
    { op: "share_all", memories: [{ id, expected: 1 }], scope: "company" },
    { op: "share_all", memories: [{ id, expected: 1 }], key: "share-key-0001" },
  ])
    expect((await read(handleMemories(ask(bad), taken))).status).toBe(400);
});

it("makes a project's slug as the engine does", () => {
  expect(projectSlug("Canal Walks!")).toBe("canal-walks");
  expect(projectSlug("  Wendle__Plot  ")).toBe("wendle-plot");
  expect(projectSlug("Rose   Garden 2")).toBe("rose-garden-2");
  expect(projectSlug("---Hedge---")).toBe("hedge");
  expect(projectSlug("!!!")).toBe("");
  expect(projectSlug("")).toBe("");
  expect(projectSlug("x".repeat(70))).toHaveLength(64);
});
