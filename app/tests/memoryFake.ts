import { vi } from "vitest";
import type {
  ChangeGroup,
  Memory,
  MemoryCapsuleStatus,
  MemoryCascade,
  MemoryChange,
  MemoryClient,
  MemoryProject,
  MemoryReply,
  MemorySettingsRead,
  StandingInstruction,
} from "../src/memories";

// The engine's side of the full memory UI's list, as app_memories answers it, over a few fictional memories and projects: every
// call recorded, each filter applied, several at once answered each on its own. Since run 22 a list's words and kind, a memory's kind,
// importance and how long it holds, and the standing instructions with a person's stop and its undo. Fictional data only.

export const uuid = (n: number) =>
  `6d3c2a10-0000-4000-8000-${String(n).padStart(12, "0")}`;
const now = "2026-11-02T09:30:00+00:00";

export function memory(
  n: number,
  content: string,
  extra: Partial<Memory> = {},
): Memory {
  return {
    id: uuid(n),
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
    content,
    truncated: false,
    held: null,
    validFrom: null,
    validUntil: null,
    revision: 1,
    createdAt: now,
    updatedAt: now,
    capsule: null,
    state: "current",
    replacedBy: null,
    retractedSources: [],
    ...extra,
  };
}

export function project(
  n: number,
  slug: string,
  extra: Partial<MemoryProject> = {},
): MemoryProject {
  return {
    id: uuid(n),
    slug,
    name: slug,
    scope: "shared",
    owner: "household",
    aliases: [],
    status: "active",
    description: "",
    author: null,
    revision: 1,
    createdAt: now,
    updatedAt: now,
    ...extra,
  };
}

export function standing(
  n: number,
  content: string | null,
  extra: Partial<StandingInstruction> = {},
): StandingInstruction {
  return {
    id: uuid(n),
    revision: 1,
    scope: "shared",
    owner: "household",
    author: "alex",
    source: "mcp",
    status: "draft",
    project: null,
    createdAt: now,
    content,
    held: null,
    fires: true,
    reason: null,
    ...extra,
  };
}

export const defaultSettings: MemorySettingsRead = {
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

export function capsuleStatus(
  extra: Partial<MemoryCapsuleStatus> = {},
): MemoryCapsuleStatus {
  return {
    kind: "core",
    project: null,
    scope: "shared",
    complete: false,
    populated: true,
    hash: `sha256:${"a".repeat(64)}`,
    chars: 214,
    maxChars: 12_000,
    served: false,
    slots: [
      {
        slot: "identity",
        state: "filled",
        filledBy: uuid(1),
        duplicates: [],
        invalid: [],
        waiting: [],
      },
      {
        slot: "preferences",
        state: "duplicate",
        filledBy: null,
        duplicates: [uuid(2), uuid(3)],
        invalid: [],
        waiting: [],
      },
      {
        slot: "constraints",
        state: "empty",
        filledBy: null,
        duplicates: [],
        invalid: [],
        waiting: [
          { id: uuid(4), status: "draft", held: false, expired: false },
          { id: uuid(5), status: "canonical", held: true, expired: false },
        ],
      },
      {
        slot: "principles",
        state: "empty",
        filledBy: null,
        duplicates: [],
        invalid: [],
        waiting: [],
      },
    ],
    ...extra,
  };
}

const refusal = (
  code: string,
  reason: string,
  revision: number | null = null,
) =>
  ({
    available: true,
    ok: false,
    code,
    reason,
    revision,
    known: [],
  }) as unknown as MemoryReply<never>;

const ok = <T>(value: T) =>
  ({ available: true, ok: true, ...value }) as unknown as MemoryReply<never>;

export function fakeEngine({
  memories = [],
  projects = [],
  settings = defaultSettings,
  groups = [],
  history = {},
  sharing = false,
  forbidden = [],
  older = false,
  shareAnswers = {},
  cascades = {},
  instructions = [],
  served = true,
}: {
  memories?: Memory[];
  projects?: MemoryProject[];
  settings?: MemorySettingsRead | null;
  groups?: ChangeGroup[];
  history?: Record<string, MemoryChange[][]>;
  sharing?: boolean;
  // Memories this reader may not change.
  forbidden?: string[];
  // An engine from before these ops: each is refused as unavailable.
  older?: boolean;
  // What a share of several answers for a memory it leaves where it is, by its id.
  shareAnswers?: Record<string, string>;
  // What a restore's retraction cascade did, by the memory's id.
  cascades?: Record<string, MemoryCascade>;
  // The standing instructions this reader reads, oldest first, and whether any fires at all now.
  instructions?: StandingInstruction[];
  served?: boolean;
} = {}) {
  const orders = instructions.map((s) => ({ ...s }));
  const stopped = new Set<string>();
  const live = new Map(memories.map((m) => [m.id, m]));
  const trash = new Map<string, { memory: Memory; nonce: string }>();
  const registry = new Map(projects.map((p) => [p.id, p]));
  let nextNonce = 7000;
  let undoCalls = 0;
  const calls: Record<string, unknown>[] = [];
  let refuseNext: {
    op: string;
    code: string;
    reason: string;
    skip: number;
  } | null = null;
  // A call held back on its way, until the test lets it answer.
  let holding: { op: string; gate: Promise<void> } | null = null;

  const applies = (m: Memory, r: Record<string, unknown>) =>
    (!r.status || m.status === r.status) &&
    (!r.kind || m.kind === r.kind) &&
    (!r.query ||
      (!m.held &&
        (m.content ?? "")
          .toLowerCase()
          .includes(String(r.query).trim().toLowerCase()))) &&
    (!r.scope || m.scope === r.scope) &&
    (!r.tag || (!m.held && m.tags.includes(String(r.tag)))) &&
    (!r.project || (!m.held && m.project?.slug === r.project));

  const client: MemoryClient = {
    available: vi.fn(async () => true),
    sharing: vi.fn(async () => sharing),
    call: vi.fn(async (request: Record<string, unknown>) => {
      calls.push(request);
      const op = String(request.op);
      if (holding?.op === op) {
        const { gate } = holding;
        holding = null;
        await gate;
      }
      if (refuseNext?.op === op) {
        if (refuseNext.skip > 0) refuseNext.skip -= 1;
        else {
          const { code, reason } = refuseNext;
          refuseNext = null;
          return refusal(code, reason);
        }
      }
      if (
        older &&
        [
          "tags",
          "changes",
          "capsule_status",
          "settings",
          "undo_group",
          "forget_all",
          "restore_all",
          "delete_all",
          "share_all",
          "project_create",
          "project_update",
          "project_delete",
        ].includes(op)
      )
        return refusal("unavailable", "Memories can't be read right now.");
      switch (op) {
        case "settings":
          return settings
            ? ok(settings)
            : refusal("unavailable", "Memories can't be read right now.");
        case "list":
          return ok({
            memories: [...live.values()]
              .filter((m) => applies(m, request))
              .map((m) =>
                m.held ? { ...m, content: null, tags: [], project: null } : m,
              )
              .reverse(),
            next: null,
          });
        case "tags": {
          const kept = [...live.values()].filter((m) => applies(m, request));
          const counts = new Map<string, number>();
          for (const m of kept)
            if (!m.held)
              for (const t of m.tags) counts.set(t, (counts.get(t) ?? 0) + 1);
          return ok({
            tags: [...counts.entries()].map(([tag, count]) => ({ tag, count })),
            truncated: false,
            count: kept.length,
          });
        }
        case "get": {
          const m = live.get(String(request.id));
          return m
            ? ok({ memory: m, version: null })
            : refusal("unknown", "No memory with that id that you can read.");
        }
        case "history": {
          const pages = history[String(request.id)] ?? [[]];
          const at = request.before ? 1 : 0;
          return ok({
            history: {
              id: request.id,
              revision: live.get(String(request.id))?.revision ?? 1,
              changes: pages[at] ?? [],
              next: pages.length > at + 1 ? 1 : null,
              cut: false,
              pruned: pages.length > 1,
              oldestKept: pages.length > 1 ? 3 : 0,
            },
          });
        }
        case "projects":
          return ok({
            projects: [...registry.values()].filter(
              (p) => request.archived || p.status === "active",
            ),
          });
        case "project_create": {
          const slug = String(request.name)
            .toLowerCase()
            .replace(/[^a-z0-9]+/g, "-")
            .replace(/^-|-$/g, "");
          if ([...registry.values()].some((p) => p.slug === slug))
            return refusal(
              "taken",
              `Another project in this scope already answers to ${slug}`,
            );
          const made = project(8000 + registry.size, slug, {
            name: String(request.name),
            description: String(request.description ?? ""),
            aliases: (request.aliases as string[]) ?? [],
            scope: (request.scope as MemoryProject["scope"]) ?? "shared",
          });
          registry.set(made.id, made);
          return ok({
            op: "project_create",
            project: made,
            scopes: "off",
            changed: [],
            unchanged: false,
          });
        }
        case "project_update": {
          const p = registry.get(String(request.id));
          if (!p) return refusal("unknown", "Unknown project");
          if (forbidden.includes(p.id))
            return refusal(
              "forbidden",
              "Only this project's creator or a Family admin may change it",
            );
          if (request.expected && request.expected !== p.revision)
            return refusal("conflict", "The project changed.", p.revision);
          const changed = {
            ...p,
            ...(request.name ? { name: String(request.name) } : {}),
            ...(request.description !== undefined
              ? { description: String(request.description) }
              : {}),
            ...(request.aliases
              ? { aliases: request.aliases as string[] }
              : {}),
            ...(request.archived !== undefined
              ? { status: request.archived ? "archived" : "active" }
              : {}),
            revision: (p.revision ?? 1) + 1,
          };
          registry.set(p.id, changed);
          return ok({
            op: "project_update",
            project: changed,
            scopes: "off",
            changed: ["name"],
            unchanged: false,
          });
        }
        case "project_delete": {
          const p = registry.get(String(request.id));
          if (!p) return refusal("unknown", "Unknown project");
          registry.delete(p.id);
          return ok({
            op: "project_delete",
            id: p.id,
            slug: p.slug,
            owner: p.owner,
            memories: 2,
            scopes: "off",
          });
        }
        case "capsule_status":
          return ok(
            capsuleStatus(
              request.capsule === "project"
                ? {
                    kind: "project",
                    project: String(request.project),
                    slots: [
                      {
                        slot: "current-state",
                        state: "filled",
                        filledBy: uuid(1),
                        duplicates: [],
                        invalid: [],
                        waiting: [],
                      },
                      {
                        slot: "decisions",
                        state: "empty",
                        filledBy: null,
                        duplicates: [],
                        invalid: [],
                        waiting: [],
                      },
                      {
                        slot: "open-questions",
                        state: "empty",
                        filledBy: null,
                        duplicates: [],
                        invalid: [],
                        waiting: [],
                      },
                    ],
                  }
                : {},
            ),
          );
        case "changes":
          return ok({
            groups,
            truncated: false,
            since: "2026-10-31T09:30:00+00:00",
          });
        // Five a call, as the engine takes a group: what is left comes with the next.
        case "undo_group": {
          const group = groups.find((g) => g.group === request.group);
          if (!group && request.group !== "redo-key")
            return refusal(
              "unknown",
              "Unknown group: list the changes again for a current one",
            );
          // The undo's own key takes the same memories back again.
          const members = group?.items ?? groups.flatMap((g) => g.items);
          const page = members.slice(undoCalls * 5, (undoCalls + 1) * 5);
          undoCalls += 1;
          const remaining = Math.max(0, members.length - undoCalls * 5);
          return ok({
            op: "undo_group",
            group: request.group,
            total: members.length,
            remaining,
            done: remaining === 0,
            capped: false,
            results: page.map((i, n) => ({
              id: i.id,
              result: n === 1 && page.length > 1 ? "changed_since" : "undone",
            })),
            redo: "redo-key",
          });
        }
        case "forget_all":
          return ok({
            op: "forget_all",
            results: (
              request.memories as { id: string; expected: number }[]
            ).map((asked) => {
              const m = live.get(asked.id);
              if (!m)
                return {
                  id: asked.id,
                  result: "unknown",
                  revision: null,
                  nonce: null,
                  days: null,
                  cascade: null,
                };
              if (forbidden.includes(asked.id))
                return {
                  id: asked.id,
                  result: "forbidden",
                  revision: null,
                  nonce: null,
                  days: null,
                  cascade: null,
                };
              if (m.revision !== asked.expected)
                return {
                  id: asked.id,
                  result: "conflict",
                  revision: m.revision,
                  nonce: null,
                  days: null,
                  cascade: null,
                };
              const nonce = uuid(nextNonce++);
              live.delete(asked.id);
              trash.set(asked.id, { memory: m, nonce });
              return {
                id: asked.id,
                result: "forgotten",
                revision: m.revision,
                nonce,
                days: 14,
                cascade: null,
              };
            }),
          });
        case "restore_all":
          return ok({
            op: "restore_all",
            results: (request.memories as { id: string; nonce: string }[]).map(
              (asked) => {
                const t = trash.get(asked.id);
                if (!t || t.nonce !== asked.nonce)
                  return {
                    id: asked.id,
                    result: "unknown",
                    revision: null,
                    held: null,
                    cascade: null,
                  };
                trash.delete(asked.id);
                live.set(asked.id, t.memory);
                return {
                  id: asked.id,
                  result: "restored",
                  revision: t.memory.revision,
                  held: t.memory.held,
                  cascade: cascades[asked.id] ?? null,
                };
              },
            ),
          });
        case "delete_all":
          return ok({
            op: "delete_all",
            results: (
              request.memories as {
                id: string;
                nonce: string;
                confirm: string;
              }[]
            ).map((asked) => {
              const t = trash.get(asked.id);
              if (!t || t.nonce !== asked.nonce || asked.confirm !== asked.id)
                return { id: asked.id, result: "unknown" };
              trash.delete(asked.id);
              return { id: asked.id, result: "deleted" };
            }),
          });
        case "share_all":
          return ok({
            op: "share_all",
            results: (
              request.memories as { id: string; expected: number }[]
            ).map((asked) => {
              const m = live.get(asked.id)!;
              if (m.held)
                return {
                  id: asked.id,
                  result: "held",
                  revision: null,
                  projectLeft: false,
                };
              if (shareAnswers[asked.id])
                return {
                  id: asked.id,
                  result: shareAnswers[asked.id],
                  revision: null,
                  projectLeft: false,
                };
              const to = request.scope as Memory["scope"];
              live.set(asked.id, {
                ...m,
                scope: to,
                owner: to === "shared" ? "household" : (m.author ?? m.owner),
                revision: m.revision + 1,
              });
              return {
                id: asked.id,
                result: "moved",
                revision: m.revision + 1,
                projectLeft: Boolean(m.project),
              };
            }),
          });
        case "trash":
          return ok({
            trash: [...trash.values()].map((t) => ({
              id: t.memory.id,
              scope: t.memory.scope,
              owner: t.memory.owner,
              status: t.memory.status,
              preview: t.memory.content,
              truncated: false,
              held: null,
              trashedAt: now,
              purgeAfter: "2026-11-16T09:30:00+00:00",
              daysLeft: 14,
              nonce: t.nonce,
              mayDeleteForever: !forbidden.includes(t.memory.id),
              revision: t.memory.revision,
            })),
            next: null,
          });
        case "standing": {
          const listed = orders.filter((s) => !stopped.has(s.id));
          const from = request.cursor ? Number(request.cursor) : 0;
          const size = Number(request.limit ?? 20);
          return ok({
            standing: listed.slice(from, from + size),
            next: from + size < listed.length ? String(from + size) : null,
            count: listed.length,
            firing: listed.filter((s) => s.fires).length,
            held: listed.filter((s) => s.reason === "held").length,
            overLimit: listed.filter((s) => s.reason === "over_limit").length,
            maxInScope: 50,
            maxFires: 2,
            served,
          });
        }
        case "stop_standing": {
          const s = orders.find((x) => x.id === request.id);
          if (!s)
            return refusal("invalid", "It is not a standing instruction.");
          if (request.expected && request.expected !== s.revision)
            return refusal("conflict", "It changed since.", s.revision);
          const already = stopped.has(s.id);
          if (!already) {
            stopped.add(s.id);
            s.revision += 1;
          }
          return ok({
            written: {
              op: "stop_standing",
              id: s.id,
              revision: s.revision,
              to: null,
              scope: s.scope,
              owner: s.owner,
              status: s.status,
              scopes: "off",
              project: null,
              unchanged: already,
              replayed: false,
              droppedTags: [],
              held: null,
              trash: null,
              capsule: null,
              projectLeft: false,
              cascade: null,
            },
          });
        }
        case "update": {
          const m = live.get(String(request.id))!;
          const clear = (request.clear as string[] | undefined) ?? [];
          const changed: Memory = {
            ...m,
            ...(request.tags ? { tags: request.tags as string[] } : {}),
            ...(request.kind ? { kind: String(request.kind) } : {}),
            ...(request.importance
              ? { importance: Number(request.importance) }
              : {}),
            ...(request.volatility
              ? { volatility: String(request.volatility) }
              : {}),
            ...(clear.includes("kind") ? { kind: null } : {}),
            ...(clear.includes("importance") ? { importance: null } : {}),
            ...(clear.includes("volatility") ? { volatility: null } : {}),
            ...(request.project
              ? {
                  project: {
                    id: uuid(9000),
                    slug: String(request.project),
                    name: String(request.project),
                  },
                }
              : {}),
            ...(clear.includes("project") ? { project: null } : {}),
            revision: m.revision + 1,
          };
          live.set(m.id, changed);
          return ok({
            written: {
              op: "update",
              id: m.id,
              revision: changed.revision,
              to: null,
              scope: m.scope,
              owner: m.owner,
              status: m.status,
              scopes: "off",
              project: null,
              unchanged: false,
              replayed: false,
              droppedTags: [],
              held: null,
              trash: null,
              capsule: null,
              projectLeft: false,
              cascade: null,
            },
          });
        }
        // An undo of a stop starts the instruction again; no other undo is this fake's.
        case "undo": {
          const s = orders.find((x) => x.id === request.id);
          if (!s || !stopped.has(s.id)) break;
          stopped.delete(s.id);
          s.revision += 1;
          return ok({
            written: {
              op: "undo",
              id: s.id,
              revision: s.revision,
              to: s.revision - 2,
              scope: s.scope,
              owner: s.owner,
              status: s.status,
              scopes: "off",
              project: null,
              unchanged: false,
              replayed: false,
              droppedTags: [],
              held: null,
              trash: null,
              capsule: null,
              projectLeft: false,
              cascade: null,
            },
          });
        }
      }
      throw new Error(`unexpected op ${op}`);
    }),
  };
  return {
    client,
    calls,
    live,
    trash,
    registry,
    stopped,
    asked: (op: string) => calls.filter((c) => c.op === op),
    // The next call of this op is refused, or the one after `skip` more.
    refuseNext: (op: string, code: string, reason: string, skip = 0) => {
      refuseNext = { op, code, reason, skip };
    },
    // The next call of this op waits until the answer's function is called.
    hold: (op: string) => {
      let release = () => {};
      const gate = new Promise<void>((resolve) => {
        release = resolve;
      });
      holding = { op, gate };
      return () => release();
    },
    resetUndo: () => {
      undoCalls = 0;
    },
  };
}
