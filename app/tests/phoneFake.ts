import { vi } from "vitest";
import type {
  ChangeGroup,
  Memory,
  MemoryChange,
  MemoryClient,
  MemoryReply,
  ReviewItem,
  StaleMemory,
  TrashedMemory,
} from "../src/memories";
import { fakeEngine, memory, project, standing, uuid } from "./memoryFake";
import { conflict, copy, fakeQueues, insight, pair } from "./queueFake";
import { decisionRow, dueRow, loopRow, plansEngine } from "./plansFake";

// One fictional install with something in every section of the Memories view, for SB-P6-02's tests of the whole view: the queues', the
// brief's lists' and the list's own fakes each answer their ops, and the reader's steps, the held queue, the stale queue and the trash
// are answered here. Fictional data only.

const now = "2026-11-02T09:30:00+00:00";
const ok = <T>(value: T) =>
  ({ available: true, ok: true, ...value }) as unknown as MemoryReply<never>;

const written = (op: string, id: unknown, extra: object = {}) => ({
  written: {
    op,
    id,
    revision: 2,
    to: null,
    scope: "shared",
    owner: "household",
    status: "draft",
    scopes: "off",
    project: null,
    unchanged: false,
    replayed: false,
    droppedTags: [],
    held: null,
    trash: op === "forget" ? { nonce: uuid(7001), days: 14 } : null,
    capsule: null,
    projectLeft: false,
    cascade: null,
    ...extra,
  },
});

const change = (revision: number, event: string): MemoryChange => ({
  revision,
  event,
  at: now,
  person: null,
  channel: "app",
  client: null,
  fields: [],
  status: "draft",
  preview: `DEMO: revision ${revision}.`,
  held: false,
  kept: true,
  to: null,
});

const group: ChangeGroup = {
  group: "group-key-phone",
  family: "edit",
  person: null,
  client: "DEMO client",
  changes: 1,
  memories: 1,
  from: now,
  until: now,
  capped: false,
  items: [
    {
      id: uuid(1),
      revision: 2,
      event: "updated",
      at: now,
      changes: 1,
      preview: "DEMO: what the client left.",
      truncated: false,
      held: false,
    },
  ],
} as unknown as ChangeGroup;

const heldRow = (n: number): ReviewItem =>
  ({
    id: uuid(n),
    scope: "shared",
    owner: "household",
    author: null,
    reason: "instruction",
    signals: [],
    heldBy: null,
    heldAt: now,
    channel: "mcp",
    client: "DEMO client",
    mayRelease: true,
    revision: 1,
  }) as unknown as ReviewItem;

const staleRow = (n: number): StaleMemory =>
  ({
    id: uuid(n),
    revision: 1,
    scope: "shared",
    owner: "household",
    author: null,
    status: "draft",
    reason: "not_confirmed",
    since: now,
    preview: `DEMO: stale ${n} about the Wendle rota.`,
    truncated: false,
  }) as unknown as StaleMemory;

const trashRow = (n: number): TrashedMemory =>
  ({
    id: uuid(n),
    scope: "shared",
    owner: "household",
    author: null,
    held: false,
    preview: `DEMO: trashed ${n} about the Wendle rota.`,
    truncated: false,
    daysLeft: 12,
    deletedAt: now,
    nonce: uuid(n + 1000),
    mayDeleteForever: true,
    revision: 1,
  }) as unknown as TrashedMemory;

const queueOps = [
  "queues",
  "duplicates",
  "contradictions",
  "insight_candidates",
  "insight_stream",
  "insight_history",
  "review_insight",
  "resolve",
];
const planOps = [
  "loops",
  "due",
  "decisions",
  "calibration",
  "resolve_loop",
  "resolve_due",
  "record_outcome",
];

export function wholeInstall({
  memories = [
    memory(1, "DEMO: the Wendle rota changes on Tuesdays."),
    memory(2, "DEMO: the Wendle gate code is kept by Sam."),
    memory(3, "DEMO: the Wendle shed needs a new lock."),
  ],
  held = [401, 402],
  stale = [501, 502, 503],
  trash = [601, 602, 603],
  captured = null as object | null,
}: {
  memories?: Memory[];
  held?: number[];
  stale?: number[];
  trash?: number[];
  // What a capture's write says, beside what it always says.
  captured?: object | null;
} = {}) {
  const list = fakeEngine({
    memories,
    projects: [project(40, "wendle")],
    groups: [group],
    history: { [uuid(1)]: [[change(3, "updated")], [change(1, "created")]] },
    // Standing instructions (run 22): one that fires, with its Open and its stop, and one held, with its Open alone.
    instructions: [
      standing(45, "DEMO: when the Wendle rota comes up, ask Sam."),
      standing(46, null, {
        held: { reason: "instruction", signals: [] },
        fires: false,
        reason: "held",
      }),
    ],
  });
  const queues = fakeQueues({
    copies: [copy(10), copy(11), copy(12)],
    conflicts: [conflict(20)],
    insights: [insight(30)],
    pairs: [pair(35)],
    held: held.length,
    stale: stale.length,
  });
  const plans = plansEngine({
    loops: [
      loopRow(70, "DEMO: Alex owes the Wendle letter."),
      loopRow(71, "DEMO: Alex owes the Wendle tarts."),
    ],
    due: [
      dueRow(80, "DEMO: the Wendle rent is due."),
      dueRow(81, "DEMO: the Wendle fence is due.", { window: "upcoming" }),
    ],
    decisions: [decisionRow(90, "DEMO: the Wendle roof was mended.")],
  });
  const liveHeld = held.map(heldRow);
  const liveStale = stale.map(staleRow);
  const liveTrash = trash.map(trashRow);
  const calls: Record<string, unknown>[] = [];
  const asMemory = (id: unknown) =>
    memories.find((m) => m.id === id) ??
    memory(9, "DEMO: the memory opened whole.", {
      id: String(id),
      ...(liveHeld.some((h) => h.id === id)
        ? { held: { reason: "instruction", signals: [] } }
        : {}),
    });

  const call = vi.fn(async (request: Record<string, unknown>) => {
    const op = String(request.op);
    calls.push(request);
    if (queueOps.includes(op)) return queues.client.call(request);
    if (planOps.includes(op)) return plans.client.call(request);
    switch (op) {
      case "get":
        return request.version
          ? ok({
              memory: null,
              version: {
                id: request.id,
                revision: request.version,
                status: "draft",
                content: `DEMO: its words at revision ${request.version}.`,
                held: false,
                writtenAt: now,
              },
            })
          : ok({ memory: asMemory(request.id), version: null });
      case "review":
        return ok({
          review: liveHeld,
          next: null,
          count: liveHeld.length,
        });
      case "stale":
        return ok({
          stale: liveStale,
          next: null,
          count: liveStale.length,
        });
      case "trash":
        return ok({ trash: liveTrash, next: null });
      case "still_true": {
        const at = liveStale.findIndex((s) => s.id === request.id);
        if (at >= 0) liveStale.splice(at, 1);
        return ok({
          op,
          id: request.id,
          revision: 1,
          unchanged: false,
          retracted: [],
        });
      }
      case "restore":
      case "delete": {
        const at = liveTrash.findIndex((t) => t.id === request.id);
        if (at >= 0) liveTrash.splice(at, 1);
        return ok(written(op, request.id));
      }
      case "capture":
        return ok(written(op, uuid(800), captured ?? {}));
      case "connections":
        return ok({
          id: request.id,
          connections: [
            {
              linkId: uuid(900),
              type: "relates_to",
              direction: "outgoing",
              made: true,
              imported: false,
              mine: true,
              mayUnlink: true,
              verification: "verified",
              memory: {
                id: uuid(2),
                scope: "shared",
                owner: "household",
                held: false,
                preview: "DEMO: the Wendle gate code is kept by Sam.",
                truncated: false,
              },
            },
          ],
          truncated: false,
        });
      case "forget":
      case "status":
      case "update":
      case "append":
      case "release":
      case "capsule":
      case "undo":
      case "clear_flags":
        return ok(written(op, request.id));
      default:
        return list.client.call(request);
    }
  });
  const client: MemoryClient = {
    available: vi.fn(async () => true),
    sharing: vi.fn(async () => false),
    call: call as unknown as MemoryClient["call"],
  };
  return { client, calls };
}
