import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { vi } from "vitest";
import { cleanMemoryAnswer } from "../src/memories";
import type {
  DecisionRow,
  DueRow,
  MemoryCalibration,
  MemoryClient,
  OpenLoop,
} from "../src/memories";

// The engine's side of the brief's lists, as app_memories answers it, over a few fictional memories: the brief computed from the
// same rows the lists page through, so a count in the brief is the length of its list only if the app reads it so; every answer in
// the engine's shape, rebuilt from the contract as the facade does; every call recorded. Fictional data only.

export const uuid = (n: number) =>
  `6d3c2a10-0000-4000-8000-${String(n).padStart(12, "0")}`;

export const fixture = (name: string) =>
  JSON.parse(
    readFileSync(resolve(process.cwd(), "../contracts/fixtures", name), "utf8"),
  );

const created = "2026-10-20T09:30:00+00:00";
const base = (n: number, preview: string) => ({
  id: uuid(n),
  revision: 1,
  scope: "shared" as const,
  owner: "household",
  author: null,
  status: "draft" as const,
  tags: [] as string[],
  project: null,
  createdAt: created,
  preview,
  truncated: false,
});

export const loopRow = (
  n: number,
  preview: string,
  extra: Partial<OpenLoop> = {},
): OpenLoop => ({
  ...base(n, preview),
  tags: ["task"],
  direction: "out",
  counterparty: null,
  ...extra,
});

export const dueRow = (
  n: number,
  preview: string,
  extra: Partial<DueRow> = {},
): DueRow => ({
  ...base(n, preview),
  window: "overdue",
  at: "2026-11-03T00:00:00+00:00",
  local: "2026-11-03 00:00",
  kind: "due",
  source: "text",
  loop: null,
  ...extra,
});

export const decisionRow = (
  n: number,
  preview: string,
  extra: Partial<DecisionRow> = {},
): DecisionRow => ({
  ...base(n, preview),
  confidence: 0.7,
  confidenceSource: "stated",
  outcome: null,
  rearms: 0,
  reviewAt: "2026-11-01T09:00:00+00:00",
  window: "overdue",
  current: true,
  edited: false,
  ...extra,
});

const WHEN = "2026-12-01T09:00:00+00:00";

export function plansEngine({
  loops = [],
  due = [],
  decisions = [],
  calibration = fixture("app_memories.calibration.not_ready.example.json"),
  stale = 0,
  held = 0,
  changeGroups = 0,
  older = false,
  zone = "Europe/London",
}: {
  loops?: OpenLoop[];
  due?: DueRow[];
  decisions?: DecisionRow[];
  calibration?: MemoryCalibration & { ok?: true };
  stale?: number;
  held?: number;
  changeGroups?: number;
  // An engine from before these ops: each is refused.
  older?: boolean;
  // The install's clocks, as its pages name them.
  zone?: string;
} = {}) {
  const state = {
    loops: [...loops],
    due: [...due],
    decisions: [...decisions],
  };
  const calls: Record<string, unknown>[] = [];
  // Each step's before, by the revision it made, for Undo.
  const undo = new Map<string, () => void>();
  let refuseNext: { op: string; code: string; reason: string } | null = null;

  const page = <T>(rows: T[], r: Record<string, unknown>) => {
    const from = Number(r.cursor ?? 0);
    const limit = Number(r.limit ?? 20);
    const items = rows.slice(from, from + limit);
    return {
      items,
      next: from + limit < rows.length ? String(from + limit) : null,
      count: rows.length,
    };
  };
  const decisionsIn = (list: string) =>
    state.decisions.filter((d) =>
      list === "due"
        ? d.outcome === null && d.window === "overdue"
        : list === "upcoming"
          ? d.window === "upcoming"
          : list === "open"
            ? d.outcome === null
            : list === "resolved"
              ? d.outcome !== null
              : list === "scored"
                ? d.outcome !== null &&
                  d.outcome !== "unknown" &&
                  d.confidence !== null
                : true,
    );
  const engine = (r: Record<string, unknown>): Record<string, unknown> => {
    const op = String(r.op);
    const write = (
      revision: number,
      id: string,
      extra: Record<string, unknown> = {},
    ) => ({
      ok: true,
      op,
      id,
      revision,
      to: null,
      scope: "shared",
      owner: "household",
      status: "draft",
      scopes: "off",
      project: null,
      changed: [],
      unchanged: false,
      replayed: false,
      droppedTags: [],
      held: null,
      ...extra,
    });
    switch (op) {
      case "loops": {
        const rows = state.loops.filter(
          (l) => !r.direction || l.direction === r.direction,
        );
        const { items, next, count } = page(rows, r);
        return {
          ok: true,
          loops: items,
          next,
          count,
          youOwe: state.loops.filter((l) => l.direction === "out").length,
          owedToYou: state.loops.filter((l) => l.direction === "in").length,
        };
      }
      case "due": {
        const order = ["overdue", "upcoming", "later"];
        const sorted = [...state.due].sort(
          (a, b) =>
            order.indexOf(a.window) - order.indexOf(b.window) ||
            a.at.localeCompare(b.at),
        );
        const rows = sorted.filter((d) => !r.window || d.window === r.window);
        const { items, next, count } = page(rows, r);
        const inWindow = (w: string) =>
          state.due.filter((d) => d.window === w).length;
        return {
          ok: true,
          due: items,
          next,
          count,
          overdue: inWindow("overdue"),
          upcoming: inWindow("upcoming"),
          later: inWindow("later"),
          zone,
        };
      }
      case "decisions": {
        const { items, next, count } = page(
          decisionsIn(String(r.state ?? "open")),
          r,
        );
        return {
          ok: true,
          decisions: items,
          next,
          count,
          open: decisionsIn("open").length,
          dueNow: decisionsIn("due").length,
          upcoming: decisionsIn("upcoming").length,
          resolved: decisionsIn("resolved").length,
          scored: decisionsIn("scored").length,
        };
      }
      case "calibration":
        return { ...calibration, ok: true };
      case "brief": {
        const owe = state.loops.filter((l) => l.direction === "out");
        const owed = state.loops.filter((l) => l.direction === "in");
        const overdue = state.due.filter((d) => d.window === "overdue");
        const upcoming = state.due.filter((d) => d.window === "upcoming");
        const come = decisionsIn("due");
        const soon = decisionsIn("upcoming");
        const briefed = (p: {
          id: string;
          revision: number;
          scope: string;
          owner: string;
          preview: string;
          truncated: boolean;
        }) => ({
          id: p.id,
          revision: p.revision,
          scope: p.scope,
          owner: p.owner,
          preview: p.preview,
          truncated: p.truncated,
        });
        const says = [
          overdue.length + upcoming.length
            ? `${overdue.length + upcoming.length} due`
            : "",
          come.length + soon.length
            ? `${come.length + soon.length} decisions to review`
            : "",
          owe.length ? `${owe.length} you owe` : "",
          owed.length ? `${owed.length} owed to you` : "",
          stale ? `${stale} may be out of date` : "",
          held ? `${held} held for your review` : "",
          changeGroups ? `${changeGroups} groups of AI changes` : "",
        ]
          .filter(Boolean)
          .join(", ");
        return {
          ok: true,
          project: null,
          unknownProject: false,
          zone,
          says: says ? `${says}.` : "Nothing needs attention.",
          due: {
            count: overdue.length + upcoming.length,
            overdue: overdue.length,
            upcoming: upcoming.length,
            items: [...overdue, ...upcoming].slice(0, 5).map((d) => ({
              ...briefed(d),
              window: d.window,
              at: d.at,
              local: d.local,
            })),
          },
          decisions: {
            count: come.length + soon.length,
            overdue: come.length,
            upcoming: soon.length,
            items: [...come, ...soon].slice(0, 3).map((d) => ({
              ...briefed(d),
              window: d.window,
              at: d.reviewAt,
            })),
          },
          youOwe: {
            count: owe.length,
            items: owe.slice(0, 5).map((l) => ({
              ...briefed(l),
              counterparty: l.counterparty,
            })),
          },
          owedToYou: {
            count: owed.length,
            items: owed.slice(0, 5).map((l) => ({
              ...briefed(l),
              counterparty: l.counterparty,
            })),
          },
          stale: {
            count: stale,
            items: staleRows.slice(0, Math.min(stale, 2)).map((s) => ({
              id: s.id,
              revision: s.revision,
              scope: s.scope,
              owner: s.owner,
              preview: s.preview,
              truncated: false,
              reason: s.reason,
            })),
          },
          insights: { count: 0, items: [] },
          contradictions: { count: 0, items: [] },
          duplicates: { count: 0, items: [] },
          held,
          standing: { count: 0, items: [] },
          calibration: {
            ready: calibration.ready,
            n: calibration.n,
            line: calibration.ready ? calibration.line : null,
          },
          changes: {
            count: changeGroups,
            changes: changeGroups,
            truncated: false,
            // No more groups than its count, as the contract holds the brief to.
            groups: groups.slice(0, Math.min(5, changeGroups)).map((g) => ({
              group: g.group,
              family: g.family,
              client: g.client,
              changes: g.changes,
              memories: g.memories,
              until: g.until,
            })),
          },
        };
      }
      case "stale":
        return { ok: true, stale: staleRows, next: null, count: stale };
      case "review":
        return {
          ok: true,
          review: Array.from({ length: held }, (_, i) => reviewRow(i)),
          next: null,
          count: held,
        };
      case "changes":
        return {
          ok: true,
          groups: r.notHeld
            ? groups
            : [...groups, { ...groups[0], group: "heldToo", changes: 2 }].slice(
                0,
                changeGroups + 1,
              ),
          truncated: false,
          since: created,
          count: r.notHeld ? changeGroups : changeGroups + 1,
        };
      case "settings":
        return {
          ok: true,
          scopes: "off",
          person: null,
          share: false,
          capsules: false,
          capture: false,
          secondBrainImport: false,
          recall: false,
          recallLog: false,
          nightSummary: false,
          trashDays: 14,
          versionsKept: 20,
          maxBytes: 131_072,
        };
      case "get":
        return {
          ok: true,
          memory: {
            id: r.id,
            scope: "shared",
            owner: "household",
            author: null,
            source: "app",
            kind: null,
            importance: null,
            volatility: null,
            status: "draft",
            tags: [],
            project: null,
            content: "DEMO: the memory opened whole.",
            truncated: false,
            held: null,
            validFrom: null,
            unknownStart: false,
            validUntil: null,
            revision: 1,
            createdAt: created,
            updatedAt: created,
            capsule: null,
            state: "current",
            replacedBy: null,
            retractedSources: [],
          },
        };
      case "resolve_loop": {
        const i = state.loops.findIndex((l) => l.id === r.id);
        if (i < 0) throw new Refused("unknown", "DEMO: no such task.");
        const row = state.loops[i];
        if (r.expected !== undefined && r.expected !== row.revision)
          throw new Refused("conflict", "DEMO: it changed since.");
        state.loops.splice(i, 1);
        const revision = row.revision + 1;
        undo.set(`${row.id}:${revision}`, () =>
          state.loops.splice(i, 0, { ...row, revision: revision + 1 }),
        );
        return write(revision, row.id, {
          changed: ["tags"],
          action: r.action,
          direction: row.direction,
          counterparty: row.counterparty,
        });
      }
      case "resolve_due": {
        const i = state.due.findIndex((d) => d.id === r.id);
        if (i < 0) throw new Refused("unknown", "DEMO: no such date.");
        const row = state.due[i];
        if (r.expected !== undefined && r.expected !== row.revision)
          throw new Refused("conflict", "DEMO: it changed since.");
        const revision = row.revision + 1;
        const was = {
          at: row.at,
          local: row.local,
          zone,
          kind: row.kind,
          source: row.source,
        };
        undo.set(`${row.id}:${revision}`, () => {
          state.due = state.due.filter((d) => d.id !== row.id);
          state.due.splice(i, 0, { ...row, revision: revision + 1 });
        });
        if (r.action === "clear_date") {
          state.due.splice(i, 1);
          return write(revision, row.id, {
            changed: ["due"],
            action: "clear_date",
            due: null,
            before: was,
          });
        }
        const until = String(r.until);
        const at = `${until}:00+00:00`;
        const soon = new Date(at).getTime() - Date.now() <= 48 * 3_600_000;
        state.due[i] = {
          ...row,
          revision,
          at,
          local: until.replace("T", " "),
          window: soon ? "upcoming" : "later",
          kind: row.kind,
        };
        return write(revision, row.id, {
          changed: ["due"],
          action: "snooze",
          due: { ...was, at, local: until.replace("T", " ") },
          before: was,
        });
      }
      case "record_outcome": {
        const i = state.decisions.findIndex((d) => d.id === r.id);
        if (i < 0) throw new Refused("unknown", "DEMO: no such decision.");
        const row = state.decisions[i];
        if (r.expected !== undefined && r.expected !== row.revision)
          throw new Refused("conflict", "DEMO: it changed since.");
        const revision = row.revision + 1;
        const asksAgain = r.result === "unknown" && row.rearms < 2;
        state.decisions[i] = {
          ...row,
          revision,
          outcome: r.result as DecisionRow["outcome"],
          rearms: r.result === "unknown" ? row.rearms + 1 : 0,
          reviewAt: asksAgain ? WHEN : null,
          window: asksAgain ? "later" : null,
        };
        undo.set(`${row.id}:${revision}`, () => {
          state.decisions[i] = { ...row, revision: revision + 1 };
        });
        return write(revision, row.id, {
          status: "canonical",
          changed: ["outcome"],
          result: r.result,
          previous: row.outcome,
          rearms: r.result === "unknown" ? row.rearms + 1 : 0,
          reviewAt: asksAgain ? WHEN : null,
          reviewsDone: r.result === "unknown" && !asksAgain,
        });
      }
      case "undo": {
        const back = undo.get(`${r.id}:${r.expected}`);
        if (!back) throw new Refused("conflict", "DEMO: it changed since.");
        back();
        return write(Number(r.expected) + 1, String(r.id), {
          changed: ["tags"],
        });
      }
      default:
        throw new Refused("invalid", `DEMO: no op ${op} here.`);
    }
  };

  const client: MemoryClient = {
    available: vi.fn(async () => true),
    sharing: vi.fn(async () => false),
    call: vi.fn(async (request: Record<string, unknown>) => {
      calls.push(request);
      const op = String(request.op);
      const refusal = (code: string, reason: string) => ({
        available: true,
        ok: false,
        code,
        reason,
        revision: null,
        known: [],
      });
      if (older && ["loops", "due", "decisions", "calibration"].includes(op))
        return refusal("unavailable", "DEMO: this engine can't read that.");
      if (refuseNext?.op === op) {
        const { code, reason } = refuseNext;
        refuseNext = null;
        return refusal(code, reason);
      }
      try {
        // In the engine's shape, rebuilt as the facade does: what does not check out is no answer at all.
        const clean = cleanMemoryAnswer(op, engine(request));
        if (!clean)
          throw new Error(`DEMO: ${op}'s answer is outside the contract`);
        return { available: true, ...clean };
      } catch (e) {
        if (e instanceof Refused) return refusal(e.code, e.message);
        throw e;
      }
    }) as unknown as MemoryClient["call"],
  };
  return {
    client,
    calls,
    state,
    refuse: (op: string, code: string, reason: string) => {
      refuseNext = { op, code, reason };
    },
    of: (op: string) => calls.filter((c) => c.op === op),
  };
}

class Refused extends Error {
  constructor(
    public code: string,
    message: string,
  ) {
    super(message);
  }
}

const staleRows = [1, 2, 3].map((n) => ({
  id: uuid(900 + n),
  scope: "shared",
  owner: "household",
  author: null,
  status: "draft",
  volatility: "state",
  reason: "not_confirmed",
  since: created,
  foundAt: created,
  confirmedAt: null,
  preview: `DEMO: stale fact ${n}.`,
  truncated: false,
  retractedSources: [],
  revision: 1,
}));
const reviewRow = (n: number) => ({
  id: uuid(970 + n),
  scope: "shared",
  owner: "household",
  author: "sam",
  source: "mcp",
  status: "draft",
  reason: "burst",
  score: 1,
  signals: ["burst"],
  heldAt: created,
  heldBy: "sam",
  channel: "mcp",
  client: "DEMO client",
  mayRelease: true,
  revision: 1,
});
const groups = [
  {
    group: "djEuNmQzYzJhMTA",
    family: "edit",
    person: null,
    client: "DEMO client",
    changes: 1,
    memories: 1,
    from: created,
    until: created,
    capped: false,
    items: [
      {
        id: uuid(950),
        revision: 2,
        event: "updated",
        at: created,
        changes: 1,
        preview: "DEMO: what the client left.",
        truncated: false,
        held: false,
      },
    ],
  },
];
