import { useEffect, useRef, useState } from "react";
import { personName } from "./instance";
import {
  maxQuery,
  releaseAllMax,
  stateNames,
  statusNames,
  workStateNames,
  workStates,
  workTypeNames,
  workTypes,
  workWords,
} from "./memories";
import type {
  Memory,
  MemoryForgottenAll,
  MemoryProject,
  MemoryReads,
  MemoryRestoredAll,
  MemoryScope,
  MemorySharedAll,
  MemoryStatus,
  TagCount,
  WorkListed,
  WorkType,
} from "./memories";
import { cascadeWords } from "./memories";
import { BulkBar, several, severalWords } from "./MemoryBulk";
import type { BulkAction } from "./MemoryBulk";
import {
  cascades,
  counted,
  kindNames,
  scopeName,
  when,
  workWho,
} from "./memoryScreen";
import type { MemoryTools } from "./memoryScreen";

// The list under the quick capture (SB-P6-01): its filters, each narrowing what this reader reads already, a count that is the
// whole list's length and tags with how many carry each, both from the engine's own predicates (op tags), and a box on each row
// for the steps that take several: forget, and with memory.share on share or make private. Each step says what it did, and a
// forget of several can be undone at once. What it filters by and which rows are chosen live in MemoryView, so they survive
// opening a memory and coming back. Since run 22 it also filters by kind and by words its memories hold (op list's and tags'
// query), looked for once a person asks, never on each key, so its count is still the length of what it lists. With the
// engine's memory.work on (migration 079) it is a project's backlog too: its work filter keeps work items only, by state, type
// and priority, and a work item's row says what it is. The list is still the one list; nothing here changes a work item.

export interface MemoryFilters {
  scope?: MemoryScope;
  status?: MemoryStatus;
  kind?: "episodic" | "semantic";
  tag?: string;
  // Words its memories hold, as they were asked for.
  query?: string;
  // A slug may be in each scope a reader reads, so a project is named with its scope.
  project?: { slug: string; scope: MemoryScope };
  // Work items only (migration 079): all of them, the open ones or one state's; of one type; of one priority, 0 for those
  // nobody ranked. Offered only while the install has work items on.
  work?: WorkListed;
  workType?: WorkType;
  workPriority?: number;
}

// A filter as a list and its tags take it. A project's own scope is the one it is read in.
// eslint-disable-next-line react/only-export-components -- Shared feature API; edits to this module may reload its consumers.
export const filterArgs = (
  f: MemoryFilters,
): Record<string, string | number> => ({
  ...(f.scope ? { scope: f.scope } : {}),
  ...(f.status ? { status: f.status } : {}),
  ...(f.kind ? { kind: f.kind } : {}),
  ...(f.tag ? { tag: f.tag } : {}),
  ...(f.query ? { query: f.query } : {}),
  ...(f.project ? { project: f.project.slug, scope: f.project.scope } : {}),
  ...(f.work ? { work: f.work } : {}),
  ...(f.workType ? { workType: f.workType } : {}),
  ...(f.workPriority !== undefined ? { workPriority: f.workPriority } : {}),
});

export function MemoryList({
  list,
  filters,
  setFilters,
  tags,
  projects,
  chosen,
  setChosen,
  tools,
  more,
  reload,
}: {
  list: { items: Memory[]; next: string | null } | null;
  filters: MemoryFilters;
  setFilters: (next: MemoryFilters) => void;
  tags: MemoryReads["tags"] | null;
  projects: MemoryProject[] | null;
  chosen: Memory[];
  setChosen: (next: Memory[]) => void;
  tools: MemoryTools;
  more: () => void;
  reload: () => Promise<void>;
}) {
  const restoring = useRef(false);
  const filtered = Boolean(
    filters.scope ||
    filters.status ||
    filters.kind ||
    filters.tag ||
    filters.project ||
    filters.query ||
    filters.work ||
    filters.workType ||
    filters.workPriority !== undefined,
  );
  // The work filter, only while the install has work items on, or while the list filters by it already.
  const working =
    tools.settings?.work === true ||
    Boolean(
      filters.work || filters.workType || filters.workPriority !== undefined,
    );
  // The words in the box, looked for once asked; what the list filters by is set back into it, and Clear filters empties it, words typed
  // but not yet looked for too.
  const [words, setWords] = useState(filters.query ?? "");
  // eslint-disable-next-line react/set-state-in-effect -- Reset the editable draft when navigation or Clear filters changes the applied query.
  useEffect(() => setWords(filters.query ?? ""), [filters.query]);
  const find = () => {
    const query = words.trim().slice(0, maxQuery) || undefined;
    if (query !== filters.query) setFilters({ ...filters, query });
  };
  const picked = (id: string) => chosen.some((m) => m.id === id);
  const scoped = tools.settings?.scopes === "on" && tools.settings.person;
  const named = (p: MemoryProject) =>
    tools.settings?.scopes === "on" ? `${p.name} (${scopeName(p)})` : p.name;
  // Active ones to choose from, and the one chosen whatever it is now.
  const offered = (projects ?? []).filter(
    (p) =>
      p.status === "active" ||
      (filters.project?.slug === p.slug && filters.project.scope === p.scope),
  );
  const tagged: TagCount[] = [
    ...(filters.tag && !tags?.tags.some((t) => t.tag === filters.tag)
      ? [{ tag: filters.tag, count: 0 }]
      : []),
    ...(tags?.tags ?? []),
  ];

  async function forgetChosen() {
    const asked = chosen.map((m) => ({ id: m.id, expected: m.revision }));
    const { results, failed } = await several<MemoryForgottenAll["results"][0]>(
      tools,
      "forget_all",
      asked,
    );
    // Refused whole, its reason on screen: the choice stays to try again, against what is there now.
    if (!results.length) {
      await reload();
      return;
    }
    setChosen([]);
    const gone = results.filter((r) => r.nonce !== null);
    const days = gone[0]?.days ?? tools.settings?.trashDays ?? 14;
    tools.say({
      text: [
        gone.length
          ? `Moved ${counted(gone.length, "memory", "memories")} to the trash, deleted for good after ${days} days unless you bring ${gone.length === 1 ? "it" : "them"} back.`
          : "Nothing was moved to the trash.",
        severalWords(results, ["forgotten"], failed),
        cascadeWords(cascades(results.map((r) => r.cascade))),
      ]
        .filter(Boolean)
        .join(" "),
      failed: failed || gone.length < results.length,
      action: gone.length
        ? {
            label: "Undo",
            run: () =>
              void restoreForgotten(
                gone.map((r) => ({ id: r.id, nonce: r.nonce! })),
              ),
          }
        : undefined,
    });
    await reload();
  }

  // A forget of several, undone: each from the trash row it just got.
  // The notice's Undo stays on screen until this answers, so a second tap on it sends nothing.
  async function restoreForgotten(memories: { id: string; nonce: string }[]) {
    if (restoring.current) return;
    restoring.current = true;
    try {
      const { results, failed } = await several<
        MemoryRestoredAll["results"][0]
      >(tools, "restore_all", memories);
      if (!results.length) return;
      const back = results.filter((r) => r.result === "restored");
      tools.say({
        text: [
          `Brought back ${counted(back.length, "memory", "memories")}.`,
          severalWords(results, ["restored"], failed),
          cascadeWords(cascades(results.map((r) => r.cascade))),
        ]
          .filter(Boolean)
          .join(" "),
        failed: failed || back.length < results.length,
      });
      await reload();
    } finally {
      restoring.current = false;
    }
  }

  async function moveChosen(to: MemoryScope, memories: Memory[]) {
    const { results, failed } = await several<MemorySharedAll["results"][0]>(
      tools,
      "share_all",
      memories.map((m) => ({ id: m.id, expected: m.revision })),
      { scope: to },
    );
    if (!results.length) {
      await reload();
      return;
    }
    setChosen([]);
    const moved = results.filter((r) => r.result === "moved");
    const held = results.filter((r) => r.result === "held").length;
    const stuck = results.filter((r) => r.result === "invalid").length;
    const left = results.filter((r) => r.projectLeft).length;
    tools.say({
      text: [
        moved.length
          ? to === "shared"
            ? `Shared ${counted(moved.length, "memory", "memories")} with Family: every member reads ${moved.length === 1 ? "it" : "them"} now, with ${moved.length === 1 ? "its author" : "their authors"}.`
            : `Took ${counted(moved.length, "memory", "memories")} back: Family's members no longer read ${moved.length === 1 ? "it" : "them"}.`
          : "Nothing moved.",
        held
          ? `${held === 1 ? "1 is" : `${held} are`} held for review: a person releases ${held === 1 ? "it" : "them"} first.`
          : "",
        stuck
          ? `${stuck === 1 ? "1 couldn't move and stays" : `${stuck} couldn't move and stay`} where ${stuck === 1 ? "it is" : "they are"}${to === "private" ? ": a memory linked to another person's private record stays in Family" : ""}.`
          : "",
        severalWords(
          results.filter((r) => r.result !== "held" && r.result !== "invalid"),
          ["moved", "unchanged"],
          failed,
        ),
        left
          ? `${left === 1 ? "1 left its collection behind, so it is" : `${left} left their collections behind, so they are`} in no collection now.`
          : "",
      ]
        .filter(Boolean)
        .join(" "),
      failed: failed || moved.length === 0,
    });
    if (moved.length) tools.saved();
    await reload();
  }

  // A private memory is shared only once a person has released it; a Family memory goes back only to an author.
  const toShare = chosen.filter((m) => m.scope === "private" && !m.held);
  const toTakeBack = chosen.filter((m) => m.scope === "shared" && m.author);
  const actions: BulkAction[] = [
    {
      label: "Forget…",
      asks: `Move ${chosen.length === 1 ? "this memory" : `these ${chosen.length} memories`} to the trash? Each stays there ${tools.settings?.trashDays ?? 14} days, and you can bring it back.`,
      go: "Move to the trash",
      keep: "Keep them",
      run: forgetChosen,
    },
    ...(tools.sharing && toShare.length
      ? [
          {
            label: "Share with Family…",
            asks: `Share ${toShare.length === 1 ? "this memory" : `these ${toShare.length} memories`} with Family? Every member reads ${toShare.length === 1 ? "it" : "them"} from now on, with the author; earlier history stays the author's. The author or a Family admin can take ${toShare.length === 1 ? "it" : "them"} back.`,
            go: "Share with Family",
            keep: "Keep them private",
            run: () => moveChosen("shared", toShare),
          },
        ]
      : []),
    ...(tools.sharing && toTakeBack.length
      ? [
          {
            label: "Make private…",
            asks: `Take ${toTakeBack.length === 1 ? "this memory" : `these ${toTakeBack.length} memories`} back to ${toTakeBack.length === 1 ? "its author's" : "their authors'"} own memories? Family's members stop reading ${toTakeBack.length === 1 ? "it" : "them"}. Only an author or a Family admin can.`,
            go: "Take them back",
            keep: "Keep them in Family",
            run: () => moveChosen("private", toTakeBack),
          },
        ]
      : []),
  ];

  return (
    <>
      <form
        className="memory-find"
        role="search"
        aria-label="Find memories by their words"
        onSubmit={(e) => {
          e.preventDefault();
          find();
        }}
      >
        <label htmlFor="memory-find">Search memory text</label>
        <input
          id="memory-find"
          type="search"
          className="memory-input"
          value={words}
          maxLength={maxQuery}
          onChange={(e) => setWords(e.target.value)}
        />
        <button type="submit" className="secondary-action">
          Find
        </button>
      </form>
      <div
        className="memory-filters"
        role="group"
        aria-label="Filter these memories"
      >
        {scoped && (
          <label className="memory-status-pick">
            <span>Scope</span>
            <select
              value={filters.scope ?? ""}
              onChange={(e) => {
                const scope = e.target.value as MemoryScope | "";
                setFilters({
                  ...filters,
                  scope: scope || undefined,
                  // A project of the other scope isn't in this one.
                  project:
                    scope && filters.project?.scope !== scope
                      ? undefined
                      : filters.project,
                });
              }}
            >
              <option value="">All I read</option>
              <option value="shared">Family</option>
              <option value="private">
                {personName(tools.settings!.person!)}'s own
              </option>
            </select>
          </label>
        )}
        <label className="memory-status-pick">
          <span>Status</span>
          <select
            value={filters.status ?? ""}
            onChange={(e) =>
              setFilters({
                ...filters,
                status: (e.target.value as MemoryStatus) || undefined,
              })
            }
          >
            <option value="">Any</option>
            {(["draft", "canonical", "deprecated"] as const).map((s) => (
              <option key={s} value={s}>
                {statusNames[s]}
              </option>
            ))}
          </select>
        </label>
        <label className="memory-status-pick">
          <span>Kind</span>
          <select
            value={filters.kind ?? ""}
            onChange={(e) =>
              setFilters({
                ...filters,
                kind:
                  (e.target.value as "episodic" | "semantic" | "") || undefined,
              })
            }
          >
            <option value="">Any</option>
            {(["episodic", "semantic"] as const).map((k) => (
              <option key={k} value={k}>
                {kindNames[k]}
              </option>
            ))}
          </select>
        </label>
        <label className="memory-status-pick">
          <span>Collection</span>
          <select
            value={
              filters.project
                ? `${filters.project.scope}:${filters.project.slug}`
                : ""
            }
            onChange={(e) => {
              const [scope, slug] = e.target.value.split(":");
              setFilters({
                ...filters,
                project: slug
                  ? { slug, scope: scope as MemoryScope }
                  : undefined,
                scope: slug ? undefined : filters.scope,
              });
            }}
          >
            <option value="">Any</option>
            {offered.map((p) => (
              <option key={p.id} value={`${p.scope}:${p.slug}`}>
                {named(p)}
              </option>
            ))}
          </select>
        </label>
        <label className="memory-status-pick">
          <span>Tag</span>
          <select
            value={filters.tag ?? ""}
            onChange={(e) =>
              setFilters({ ...filters, tag: e.target.value || undefined })
            }
          >
            <option value="">Any</option>
            {tagged.map((t) => (
              <option key={t.tag} value={t.tag}>
                {t.tag} ({t.count})
              </option>
            ))}
          </select>
        </label>
        {working && (
          <>
            <label className="memory-status-pick">
              <span>Work</span>
              <select
                value={filters.work ?? ""}
                onChange={(e) =>
                  setFilters({
                    ...filters,
                    work: (e.target.value as WorkListed) || undefined,
                  })
                }
              >
                <option value="">Any memory</option>
                <option value="all">Work items</option>
                <option value="open">Open work</option>
                {workStates.map((s) => (
                  <option key={s} value={s}>
                    {workStateNames[s]}
                  </option>
                ))}
              </select>
            </label>
            <label className="memory-status-pick">
              <span>Work type</span>
              <select
                value={filters.workType ?? ""}
                onChange={(e) =>
                  setFilters({
                    ...filters,
                    workType: (e.target.value as WorkType) || undefined,
                  })
                }
              >
                <option value="">Any</option>
                {workTypes.map((t) => (
                  <option key={t} value={t}>
                    {workTypeNames[t]}
                  </option>
                ))}
              </select>
            </label>
            <label className="memory-status-pick">
              <span>Priority</span>
              <select
                value={
                  filters.workPriority === undefined
                    ? ""
                    : String(filters.workPriority)
                }
                onChange={(e) =>
                  setFilters({
                    ...filters,
                    workPriority:
                      e.target.value === ""
                        ? undefined
                        : Number(e.target.value),
                  })
                }
              >
                <option value="">Any</option>
                {[1, 2, 3, 4].map((p) => (
                  <option key={p} value={p}>
                    {p === 1 ? "1, first" : p === 4 ? "4, last" : String(p)}
                  </option>
                ))}
                <option value="0">Not ranked</option>
              </select>
            </label>
          </>
        )}
        {filtered && (
          <button
            className="memory-filter-clear"
            onClick={() => {
              // The box too, words typed but not yet looked for among them: nothing it held is looked for after this.
              setWords("");
              setFilters({});
            }}
          >
            Clear filters
          </button>
        )}
      </div>
      {tags && (filtered || tags.count > 0) && (
        <p className="memory-hint">
          {tags.count === 0
            ? "No memories match."
            : `${counted(tags.count, "memory", "memories")}${filtered ? " match" : ""}${list && list.items.length < tags.count ? `, the newest ${list.items.length} shown` : ""}.`}
        </p>
      )}
      <ul className="memory-list" aria-label="Recent memories">
        {list?.items.map((item) => (
          <li
            key={item.id}
            className={`memory-listed${item.held ? " memory-listed-held" : ""}`}
          >
            <input
              type="checkbox"
              className="memory-pick"
              aria-label={`Choose: ${item.held ? "a memory held for review" : (item.content ?? "").slice(0, 60)}`}
              checked={picked(item.id)}
              disabled={!picked(item.id) && chosen.length >= releaseAllMax}
              onChange={(e) =>
                setChosen(
                  e.target.checked
                    ? [...chosen, item]
                    : chosen.filter((m) => m.id !== item.id),
                )
              }
            />
            <button onClick={() => tools.open(item.id)}>
              <span className="memory-preview">
                {item.held ? "Held for review" : (item.content ?? "")}
                {item.truncated ? "…" : ""}
              </span>
              {/* The row's time on its own, so every row's time shares the list's right edge. */}
              <time className="memory-when" dateTime={item.updatedAt}>
                {when(item.updatedAt)}
              </time>
              <span className="memory-meta">
                {item.work
                  ? `${[workWords(item.work), workWho(item.work)].filter(Boolean).join(" · ")} · `
                  : ""}
                {statusNames[item.status]} · {scopeName(item)}
                {item.held ? " · held" : ""}
                {stateNames[item.state] &&
                  ` · ${stateNames[item.state]!.toLowerCase()}`}
                {item.retractedSources.length > 0 && " · flagged for a check"}
                {item.project ? ` · ${item.project.slug}` : ""}
                {item.tags.length > 0 &&
                  ` · ${item.tags
                    .slice(0, 3)
                    .map((t) => `#${t}`)
                    .join(" ")}`}
              </span>
            </button>
          </li>
        ))}
      </ul>
      {list && !list.items.length && (
        <p className="memory-empty">
          {filters.query
            ? `No memories match “${filters.query}”${filtered && Object.keys(filterArgs(filters)).length > 1 ? " with these filters" : ""}.`
            : filtered
              ? "No memories match these filters."
              : "No memories yet."}
        </p>
      )}
      {list?.next && (
        <button className="secondary-action memory-more" onClick={more}>
          Show more
        </button>
      )}
      <BulkBar
        count={chosen.length}
        shown={list?.items.length ?? 0}
        max={releaseAllMax}
        actions={actions}
        onAll={() =>
          setChosen(
            [
              ...chosen,
              ...(list?.items ?? []).filter((m) => !picked(m.id)),
            ].slice(0, releaseAllMax),
          )
        }
        onClear={() => setChosen([])}
        tools={tools}
      />
    </>
  );
}
