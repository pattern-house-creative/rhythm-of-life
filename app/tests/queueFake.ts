import { vi } from "vitest";
import type {
  Contradiction,
  DuplicateMemory,
  InsightEvent,
  InsightPair,
  InsightView,
  MemoryClient,
  MemoryHold,
  MemoryReply,
  StreamInsight,
} from "../src/memories";
import type { DailyInsight } from "../src/insights";
import { fakeEngine, memory, uuid } from "./memoryFake";

// The engine's side of a person's review queues, as app_memories answers it, over a few fictional items: every call recorded, a list
// paged two at a time, each count the length of its list, an item leaving its queue once, and a replacement only by the side that began
// later, as the engine has it. Anything else goes to the list's fake. Fictional data only.

const now = "2026-11-02T09:30:00+00:00";
const longAgo = "2026-08-04T09:30:00+00:00";

export function copy(
  n: number,
  extra: Partial<DuplicateMemory> = {},
): DuplicateMemory {
  return {
    id: uuid(n),
    scope: "shared",
    owner: "household",
    author: "alex",
    status: "draft",
    createdAt: now,
    preview: `DEMO: copy ${n} of the Wendle rota.`,
    truncated: false,
    duplicateOf: [{ id: uuid(n + 100), score: 0.93 }],
    revision: 2,
    ...extra,
  };
}

export function conflict(
  n: number,
  extra: Partial<Contradiction> = {},
): Contradiction {
  return {
    scope: "shared",
    owner: "household",
    score: 0.88,
    offeredAt: now,
    memory: {
      id: uuid(n),
      revision: 3,
      author: "alex",
      status: "draft",
      preview: `DEMO: the bins go out on Wednesdays, ${n}.`,
      truncated: false,
      startedAt: now,
    },
    conflict: {
      id: uuid(n + 100),
      revision: 4,
      author: "sam",
      status: "canonical",
      preview: `DEMO: the bins go out on Tuesdays, ${n}.`,
      truncated: false,
      startedAt: longAgo,
    },
    proposed: null,
    maySettle: true,
    ...extra,
  };
}

export function insight(
  n: number,
  extra: Partial<StreamInsight> = {},
  about: Partial<DailyInsight> = {},
): StreamInsight {
  return {
    id: uuid(n),
    view: "proposed",
    review: "proposed",
    reviewedAt: null,
    snoozeUntil: null,
    snoozeEnded: false,
    shownAt: now,
    scope: "shared",
    owner: "household",
    candidates: [],
    mayReview: true,
    insight: {
      id: uuid(n),
      kind: "discovery",
      title: `DEMO: insight ${n} about the rota`,
      summary: `DEMO: what insight ${n} says.`,
      whyItMatters: "DEMO: why it matters.",
      nextStep: "DEMO: what to do next.",
      points: [],
      evidenceLevel: "weak",
      limitations: "DEMO: fictional.",
      stars: [],
      sources: [
        {
          kind: "memory",
          title: "DEMO: the rota",
          locator: uuid(1),
          retrievedAt: now,
        },
      ],
      generatedBy: "harness-daily",
      generatedAt: now,
      sourcesAsOf: now,
      supersedes: null,
      status: "new",
      feedback: null,
      createdAt: now,
      updatedAt: now,
      ...about,
    },
    ...extra,
  };
}

export function pair(n: number, extra: Partial<InsightPair> = {}): InsightPair {
  return {
    id: uuid(n),
    scope: "shared",
    owner: "household",
    score: 1.5,
    signals: ["weighty"],
    similarity: 0.2,
    gapDays: 90,
    foundAt: now,
    older: {
      id: uuid(n + 100),
      revision: 1,
      createdAt: "2026-08-04T09:30:00+00:00",
      importance: 4,
      preview: `DEMO: older memory ${n}.`,
      truncated: false,
    },
    newer: {
      id: uuid(n + 200),
      revision: 1,
      createdAt: now,
      importance: 4,
      preview: `DEMO: newer memory ${n}.`,
      truncated: false,
    },
    status: "pending",
    decidedAt: null,
    note: null,
    insight: null,
    ...extra,
  };
}

const refused = (code: string, reason: string) =>
  ({
    available: true,
    ok: false,
    code,
    reason,
    revision: null,
    known: [],
  }) as unknown as MemoryReply<never>;
const ok = <T>(value: T) =>
  ({ available: true, ok: true, ...value }) as unknown as MemoryReply<never>;
const PAGE = 2;

export function fakeQueues({
  copies = [],
  conflicts = [],
  insights = [],
  pairs = [],
  held = 0,
  stale = 0,
  words = {},
  day = "An insight was proposed today.",
}: {
  copies?: DuplicateMemory[];
  conflicts?: Contradiction[];
  insights?: StreamInsight[];
  pairs?: InsightPair[];
  held?: number;
  stale?: number;
  // A memory's whole words, by its id, for what is read whole, and its hold if it has been held since a list was read. A memory a
  // copy is close to reads as words of its own unless named here.
  words?: Record<
    string,
    { content: string; revision: number; held?: MemoryHold }
  >;
  day?: string;
} = {}) {
  const base = fakeEngine({ memories: [memory(1, "DEMO: the Wendle rota.")] });
  const calls: Record<string, unknown>[] = [];
  const liveCopies = [...copies];
  const liveConflicts = [...conflicts];
  const liveInsights = insights.map((i) => ({ ...i }));
  const livePairs = pairs.map((p) => ({ ...p }));
  const history = new Map<string, InsightEvent[]>();
  // A memory taken out ends the review of every pair it is in, and is no longer a copy.
  const takenOut = (memoryId: unknown) => {
    const copyAt = liveCopies.findIndex((c) => c.id === memoryId);
    if (copyAt >= 0) liveCopies.splice(copyAt, 1);
    for (let at = liveConflicts.length - 1; at >= 0; at--)
      if (
        [liveConflicts[at].memory.id, liveConflicts[at].conflict.id].includes(
          String(memoryId),
        )
      )
        liveConflicts.splice(at, 1);
  };
  const written = (op: string, id: unknown, status: string) => ({
    written: {
      op,
      id,
      revision: 3,
      to: null,
      scope: "shared",
      owner: "household",
      status,
      scopes: "off",
      project: null,
      unchanged: false,
      replayed: false,
      droppedTags: [],
      held: null,
      trash: op === "forget" ? { nonce: uuid(7000), days: 14 } : null,
      capsule: null,
      projectLeft: false,
      cascade: null,
    },
  });
  let refuseNext: { op: string; code: string; reason: string } | null = null;
  let stillFlagged = new Set<string>();

  const page = <T>(all: T[], request: Record<string, unknown>) => {
    const from = request.cursor ? Number(request.cursor) : 0;
    const shown = all.slice(from, from + PAGE);
    return {
      shown,
      next: from + PAGE < all.length ? String(from + PAGE) : null,
      count: all.length,
    };
  };
  const inView = (v: InsightView | "all") =>
    liveInsights.filter((i) => v === "all" || i.view === v);
  const nothing = {
    revision: null,
    scope: null,
    owner: null,
    status: null,
    scopes: null,
    changed: [] as string[],
    held: null,
    cascade: null,
    target: null,
    decision: null,
    review: null,
    view: null,
    reviewedAt: null,
    candidate: null,
    decidedAt: null,
    note: null,
    insight: null,
    checkedAt: null,
    was: null,
    retracted: [] as string[],
  };

  const client: MemoryClient = {
    available: vi.fn(async () => true),
    sharing: vi.fn(async () => false),
    call: vi.fn(async (request: Record<string, unknown>) => {
      const op = String(request.op);
      calls.push(request);
      if (refuseNext?.op === op) {
        const { code, reason } = refuseNext;
        refuseNext = null;
        return refused(code, reason);
      }
      switch (op) {
        case "queues":
          return ok({
            held,
            duplicates: liveCopies.length,
            contradictions: liveConflicts.length,
            stale,
            candidates: livePairs.filter((p) => p.status === "pending").length,
            insights: inView("proposed").length,
          });
        case "duplicates": {
          const p = page(liveCopies, request);
          return ok({ duplicates: p.shown, next: p.next, count: p.count });
        }
        case "contradictions": {
          const p = page(liveConflicts, request);
          return ok({ contradictions: p.shown, next: p.next, count: p.count });
        }
        case "insight_candidates": {
          const wanted =
            request.state === "waiting" || !request.state
              ? "pending"
              : request.state;
          const p = page(
            livePairs.filter((x) => x.status === wanted),
            request,
          );
          return ok({ candidates: p.shown, next: p.next, count: p.count });
        }
        case "insight_stream": {
          const asked = (request.view ?? "proposed") as InsightView | "all";
          const p = page(inView(asked), request);
          return ok({
            view: asked,
            count: p.count,
            proposed: inView("proposed").length,
            older: inView("older").length,
            confirmed: inView("confirmed").length,
            snoozed: inView("snoozed").length,
            closed: inView("closed").length,
            candidates: livePairs.filter((x) => x.status === "pending").length,
            day: {
              date: "2026-11-02",
              zone: "Europe/London",
              outcome: "saved",
              note: null,
              insight: null,
              declined: 0,
              says: day,
            },
            insights: p.shown,
            next: p.next,
          });
        }
        case "insight_history": {
          const i = liveInsights.find((x) => x.id === request.id);
          const events = history.get(String(request.id)) ?? [];
          return i
            ? ok({ id: i.id, review: i.review, count: events.length, events })
            : refused("unknown", "Unknown insight");
        }
        case "review_insight": {
          const i = liveInsights.find((x) => x.id === request.id)!;
          i.review = "proposed";
          i.view = "proposed";
          i.reviewedAt = null;
          return ok({
            op,
            id: i.id,
            review: i.review,
            view: i.view,
            reviewedAt: null,
            unchanged: false,
          });
        }
        case "resolve":
          return resolve(request);
        // Several at once (run 22): each item as one resolve of it answers, each on its own.
        case "resolve_all": {
          const results = (
            request.items as { id: string; expected?: number }[]
          ).map((item) => {
            const one = resolve({
              op: "resolve",
              id: item.id,
              action: request.action,
              ...(item.expected ? { expected: item.expected } : {}),
            }) as unknown as Record<string, unknown>;
            return {
              id: item.id,
              result: one.waiting ? "resolved" : "unchanged",
              revision: null,
              review: (one.review as string | null) ?? null,
              view: (one.view as string | null) ?? null,
              candidate: (one.candidate as string | null) ?? null,
            };
          });
          return ok({ op: "resolve_all", action: request.action, results });
        }
        case "get": {
          const id = String(request.id);
          const whole =
            words[id] ??
            (copies.some((c) => c.duplicateOf.some((d) => d.id === id))
              ? {
                  content: `DEMO: the memory a copy is close to, ${id.slice(-3)}.`,
                  revision: 1,
                }
              : undefined);
          if (whole)
            return ok({
              memory: memory(9, whole.content, {
                id,
                revision: whole.revision,
                held: "held" in whole && whole.held ? whole.held : null,
              }),
              version: null,
            });
          return base.client.call(request);
        }
        case "forget":
          takenOut(request.id);
          return ok(written(op, request.id, "draft"));
        case "status":
          takenOut(request.id);
          return ok(written(op, request.id, String(request.status)));
        default:
          return base.client.call(request);
      }
    }),
  };

  function resolve(request: Record<string, unknown>) {
    const action = String(request.action);
    const id = String(request.id);
    const answer = (queue: string, extra: Record<string, unknown>) =>
      ok({
        op: "resolve",
        queue,
        action,
        id,
        unchanged: false,
        waiting: true,
        stillCounted: false,
        ...nothing,
        ...extra,
      });
    const not = { unchanged: true, waiting: false };
    if (action === "keep_copy") {
      const at = liveCopies.findIndex((c) => c.id === id);
      if (at < 0) return answer("duplicates", not);
      if (stillFlagged.has(id)) {
        stillFlagged = new Set([...stillFlagged].filter((x) => x !== id));
        return answer("duplicates", { stillCounted: true });
      }
      liveCopies.splice(at, 1);
      return answer("duplicates", {});
    }
    if (action === "settle_keep_both" || action === "settle_replace") {
      const at = liveConflicts.findIndex(
        (k) =>
          [k.memory.id, k.conflict.id].includes(id) &&
          [k.memory.id, k.conflict.id].includes(String(request.target)),
      );
      const decision = action === "settle_replace" ? "replaced" : "keep_both";
      if (at < 0)
        return answer("contradictions", {
          ...not,
          decision: "keep_both",
          target: request.target,
        });
      if (action === "settle_replace") {
        const { memory: one, conflict: two } = liveConflicts[at];
        const [named, other] = one.id === id ? [one, two] : [two, one];
        const from = (s: { startedAt: string | null }) =>
          s.startedAt === null ? -Infinity : Date.parse(s.startedAt);
        if (from(named) <= from(other))
          return refused(
            "protected",
            "This memory began being true at or before the other did, so it can't replace it.",
          );
      }
      liveConflicts.splice(at, 1);
      return answer("contradictions", { decision, target: request.target });
    }
    if (action === "use_candidate" || action === "reject_candidate") {
      const p = livePairs.find((x) => x.id === id)!;
      if (p.status !== "pending")
        return answer("insight_candidates", { ...not, candidate: p.status });
      p.status = action === "use_candidate" ? "used" : "rejected";
      p.note = (request.note as string | undefined) ?? null;
      p.decidedAt = now;
      return answer("insight_candidates", {
        candidate: p.status,
        note: p.note,
      });
    }
    const i = liveInsights.find((x) => x.id === id)!;
    if (i.review !== "proposed")
      return answer("insights", { ...not, review: i.review, view: i.view });
    i.review = action === "confirm_insight" ? "confirmed" : "dismissed";
    i.view = action === "confirm_insight" ? "confirmed" : "closed";
    i.reviewedAt = now;
    history.set(id, [
      {
        event: i.review,
        value: null,
        until: null,
        note: (request.note as string | undefined) ?? null,
        person: null,
        channel: "app",
        client: null,
        at: now,
      },
      ...(history.get(id) ?? []),
    ]);
    return answer("insights", { review: i.review, view: i.view });
  }

  return {
    client,
    calls,
    asked: (op: string) => calls.filter((c) => c.op === op),
    refuseNext: (op: string, code: string, reason: string) => {
      refuseNext = { op, code, reason };
    },
    // This memory has a further flag, so a keep both leaves it listed once.
    stillFlagged: (id: string) => {
      stillFlagged = new Set([...stillFlagged, id]);
    },
    // This one's flags came off since the list was read, so a keep both changes nothing.
    noLongerFlagged: (id: string) => {
      const at = liveCopies.findIndex((c) => c.id === id);
      if (at >= 0) liveCopies.splice(at, 1);
    },
  };
}
