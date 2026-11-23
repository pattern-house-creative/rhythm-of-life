import { useEffect, useState } from "react";
import { projectSlug } from "./memories";
import type {
  MemoryProject,
  MemoryProjectDeleted,
  MemoryProjectWritten,
  MemoryReads,
  MemoryScope,
} from "./memories";
import { MemoryCapsuleCard } from "./MemoryCapsuleCard";
import { counted, scopeName, when, whose } from "./memoryScreen";
import type { MemoryTools } from "./memoryScreen";

// Projects (SB-P6-01; upstream's projects page): the registry a person reads and keeps. Make one with a preview of its slug,
// which never changes after; rename it, give it other names, archive it and bring it back, delete it with its memories kept;
// each with its capsule's status and the way to its memories in the list. Who may change one is the engine's to say (only its
// creator or a Family admin; with scopes off anyone admitted), and a refusal says why. A project's on-demand digest is an AI
// client's to write (D-033), so nothing here writes one.

const aliasList = (text: string) =>
  text
    .split(",")
    .map((a) => a.trim().toLowerCase())
    .filter(Boolean);

type Draft = {
  name: string;
  slug: string;
  description: string;
  aliases: string;
  scope: MemoryScope;
  showInProjects: boolean;
};

function Fields({
  draft,
  setDraft,
  making,
}: {
  draft: Draft;
  setDraft: (next: Draft) => void;
  making: boolean;
}) {
  return (
    <>
      <label htmlFor="memory-project-name">Name</label>
      <input
        id="memory-project-name"
        className="memory-input"
        value={draft.name}
        maxLength={120}
        onChange={(e) => setDraft({ ...draft, name: e.target.value })}
      />
      <label>
        <input
          type="checkbox"
          checked={draft.showInProjects}
          onChange={(event) =>
            setDraft({ ...draft, showInProjects: event.target.checked })
          }
        />
        Show in Projects
      </label>
      <label htmlFor="memory-project-description">What it is</label>
      <textarea
        id="memory-project-description"
        value={draft.description}
        rows={2}
        maxLength={1000}
        onChange={(e) => setDraft({ ...draft, description: e.target.value })}
      />
      <label htmlFor="memory-project-aliases">
        Other names, separated by commas
      </label>
      <input
        id="memory-project-aliases"
        className="memory-input"
        value={draft.aliases}
        onChange={(e) => setDraft({ ...draft, aliases: e.target.value })}
      />
      <span className="memory-hint">
        A memory tagged with one of these is in the collection too.
        {making &&
          projectSlug(draft.name) &&
          ` It will be known as ${projectSlug(draft.name)}, which never changes.`}
      </span>
      {/* A name with no letters or digits of the slug's alphabet makes none: its person gives one. */}
      {making && draft.name.trim() && !projectSlug(draft.name) && (
        <>
          <label htmlFor="memory-project-slug">
            What it is known as: lower-case letters, digits and hyphens
          </label>
          <input
            id="memory-project-slug"
            className="memory-input"
            value={draft.slug}
            maxLength={64}
            onChange={(e) => setDraft({ ...draft, slug: e.target.value })}
          />
        </>
      )}
    </>
  );
}

export function MemoryProjects({
  opened,
  setOpened,
  showMemories,
  deleted,
  tools,
}: {
  // The project opened, by its id: kept in MemoryView, so it is still open back from a memory.
  opened: string | null;
  setOpened: (id: string | null) => void;
  // The list, filtered to this project.
  showMemories: (p: MemoryProject) => void;
  // It is gone: what filtered by it filters by nothing now.
  deleted: (p: MemoryProject) => void;
  tools: MemoryTools;
}) {
  const [projects, setProjects] = useState<MemoryProject[] | null>(null);
  const [making, setMaking] = useState<Draft | null>(null);
  const [editing, setEditing] = useState<Draft | null>(null);
  const [deleting, setDeleting] = useState<string | null>(null);
  const [counts, setCounts] = useState<Record<string, number>>({});

  async function load() {
    const reply = await tools.call<MemoryReads["projects"]>({
      op: "projects",
      archived: true,
    });
    if (reply) setProjects(reply.projects);
  }

  useEffect(() => {
    void load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const current = projects?.find((p) => p.id === opened) ?? null;

  // How many memories it holds: the list's own length, from the engine's own count.
  useEffect(() => {
    if (!current) return;
    let live = true;
    void tools
      .quiet<MemoryReads["tags"]>({
        op: "tags",
        project: current.slug,
        scope: current.scope,
      })
      .then((reply) => {
        if (live && reply)
          setCounts((c) => ({ ...c, [current.id]: reply.count }));
      });
    return () => {
      live = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [current?.id, current?.revision]);

  // Escape takes back a form or a question first.
  useEffect(() => {
    if (!making && !editing && !deleting) return;
    const { layer } = tools;
    layer.current = () => {
      setMaking(null);
      setEditing(null);
      setDeleting(null);
      return true;
    };
    return () => {
      layer.current = null;
    };
  }, [making, editing, deleting, tools]);

  // Where a new project lands: with scopes off the household's, and Family's for a login with no person; a person's own by
  // default for one with a person, who may name Family.
  const person = tools.settings?.scopes === "on" ? tools.settings.person : null;

  async function make() {
    if (!making) return;
    const reply = await tools.call<MemoryProjectWritten>({
      op: "project_create",
      name: making.name.trim(),
      // Asked for only while the name makes none: once it does, the name's is the one the preview promised.
      ...(!projectSlug(making.name) && making.slug.trim()
        ? { slug: making.slug.trim() }
        : {}),
      description: making.description.trim(),
      aliases: aliasList(making.aliases),
      scope: making.scope,
      ...(making.showInProjects ? { showInProjects: true } : {}),
    });
    if (!reply) return;
    setMaking(null);
    setOpened(reply.project.id);
    tools.say({
      text: `Made the collection ${reply.project.name}: it is known as ${reply.project.slug}.`,
    });
    await load();
  }

  async function change(p: MemoryProject, fields: Record<string, unknown>) {
    return tools.call<MemoryProjectWritten>({
      op: "project_update",
      id: p.id,
      ...fields,
      ...(p.revision !== null ? { expected: p.revision } : {}),
    });
  }

  async function save(p: MemoryProject) {
    if (!editing) return;
    const reply = await change(p, {
      name: editing.name.trim(),
      description: editing.description.trim(),
      aliases: aliasList(editing.aliases),
      ...(editing.showInProjects !== Boolean(p.showInProjects)
        ? { showInProjects: editing.showInProjects }
        : {}),
    });
    if (!reply) {
      await load();
      return;
    }
    setEditing(null);
    tools.say({
      text: reply.unchanged ? "Nothing changed." : "Saved.",
    });
    await load();
  }

  async function archive(p: MemoryProject, archived: boolean) {
    const reply = await change(p, { archived });
    if (!reply) {
      await load();
      return;
    }
    tools.say({
      text: archived
        ? `Archived ${p.name}: its memories stay, and it leaves the lists of collections.`
        : `${p.name} is back among the collections.`,
      action: reply.unchanged
        ? undefined
        : {
            label: "Undo",
            run: () =>
              void (async () => {
                const again = await tools.call<MemoryProjectWritten>({
                  op: "project_update",
                  id: p.id,
                  archived: !archived,
                  ...(reply.project.revision !== null
                    ? { expected: reply.project.revision }
                    : {}),
                });
                if (again) tools.say({ text: "Undone." });
                await load();
              })(),
          },
    });
    await load();
  }

  async function remove(p: MemoryProject) {
    setDeleting(null);
    const reply = await tools.call<MemoryProjectDeleted>({
      op: "project_delete",
      id: p.id,
      ...(p.revision !== null ? { expected: p.revision } : {}),
    });
    if (!reply) {
      await load();
      return;
    }
    setOpened(null);
    deleted(p);
    tools.say({
      text: `Deleted the collection ${p.name}: ${counted(reply.memories, "memory", "memories")} left it and ${reply.memories === 1 ? "stays" : "stay"} as ${reply.memories === 1 ? "it was" : "they were"}, in no collection.`,
    });
    tools.saved();
    await load();
  }

  const active = projects?.filter((p) => p.status === "active") ?? [];
  const archived = projects?.filter((p) => p.status !== "active") ?? [];

  const row = (p: MemoryProject) => (
    <li key={p.id} className="memory-project">
      <button
        className="memory-project-head"
        aria-expanded={opened === p.id}
        onClick={() => {
          setEditing(null);
          setDeleting(null);
          setOpened(opened === p.id ? null : p.id);
        }}
      >
        <span className="memory-preview">{p.name}</span>
        {p.updatedAt && (
          <time className="memory-when" dateTime={p.updatedAt}>
            {when(p.updatedAt)}
          </time>
        )}
        <span className="memory-meta">
          {p.slug} · {scopeName(p)}
          {p.showInProjects ? " · Shown in Projects" : ""}
          {p.aliases.length > 0 ? ` · also ${p.aliases.join(", ")}` : ""}
        </span>
      </button>
      {opened === p.id && (
        <div className="memory-project-open">
          {editing ? (
            <form
              className="memory-edit"
              onSubmit={(e) => {
                e.preventDefault();
                if (editing.name.trim()) void save(p);
              }}
            >
              <Fields draft={editing} setDraft={setEditing} making={false} />
              <div className="memory-row">
                <button type="button" onClick={() => setEditing(null)}>
                  Cancel
                </button>
                <button
                  type="submit"
                  className="primary-action"
                  disabled={!editing.name.trim()}
                >
                  Save
                </button>
              </div>
            </form>
          ) : (
            <>
              {p.description && (
                <p className="source-content memory-text">{p.description}</p>
              )}
              {counts[p.id] !== undefined && (
                <p className="memory-meta">
                  {counted(counts[p.id], "memory", "memories")} in it.
                </p>
              )}
              {deleting === p.id ? (
                <span
                  className="memory-confirm"
                  role="group"
                  aria-label="Delete this collection?"
                >
                  <span>
                    Delete this collection? Its memories stay, in no collection,
                    and any tagged with its other names keep their tags. This
                    can't be undone.
                  </span>
                  <button
                    className="memory-danger"
                    onClick={() => void remove(p)}
                  >
                    Delete the collection
                  </button>
                  <button onClick={() => setDeleting(null)}>Keep it</button>
                </span>
              ) : (
                <div
                  className="memory-actions"
                  role="group"
                  aria-label="This collection"
                >
                  <button onClick={() => showMemories(p)}>
                    Show its memories
                  </button>
                  <button
                    onClick={() => {
                      setMaking(null);
                      setEditing({
                        name: p.name,
                        slug: p.slug,
                        description: p.description,
                        aliases: p.aliases.join(", "),
                        scope: p.scope,
                        showInProjects: Boolean(p.showInProjects),
                      });
                    }}
                  >
                    Edit
                  </button>
                  {p.status === "active" ? (
                    <button onClick={() => void archive(p, true)}>
                      Archive
                    </button>
                  ) : (
                    <button onClick={() => void archive(p, false)}>
                      Bring back
                    </button>
                  )}
                  <button onClick={() => setDeleting(p.id)}>Delete…</button>
                </div>
              )}
              <MemoryCapsuleCard
                name={`The ${p.slug} collection’s capsule`}
                request={{
                  capsule: "project",
                  project: p.slug,
                  scope: p.scope,
                }}
                tools={tools}
              />
            </>
          )}
        </div>
      )}
    </li>
  );

  return (
    <>
      {making ? (
        <form
          className="memory-edit"
          onSubmit={(e) => {
            e.preventDefault();
            if (
              making.name.trim() &&
              (projectSlug(making.name) || making.slug.trim())
            )
              void make();
          }}
        >
          <Fields draft={making} setDraft={setMaking} making />
          {person && (
            <label className="memory-status-pick">
              <span>Whose</span>
              <select
                value={making.scope}
                onChange={(e) =>
                  setMaking({
                    ...making,
                    scope: e.target.value as MemoryScope,
                  })
                }
              >
                <option value="private">
                  {whose({ scope: "private", owner: person })}
                </option>
                <option value="shared">Family's</option>
              </select>
            </label>
          )}
          <div className="memory-row">
            <button type="button" onClick={() => setMaking(null)}>
              Cancel
            </button>
            <button
              type="submit"
              className="primary-action"
              disabled={
                !making.name.trim() ||
                (!projectSlug(making.name) && !making.slug.trim())
              }
            >
              Make the collection
            </button>
          </div>
        </form>
      ) : (
        <button
          className="secondary-action memory-more"
          onClick={() => {
            setEditing(null);
            setMaking({
              name: "",
              slug: "",
              description: "",
              aliases: "",
              scope: person ? "private" : "shared",
              showInProjects: false,
            });
          }}
        >
          New collection…
        </button>
      )}
      <ul className="memory-list" aria-label="Collections">
        {active.map(row)}
      </ul>
      {projects && !active.length && !archived.length && (
        <p className="memory-empty">
          No collections yet. Saving a memory with a new collection name creates
          one.
        </p>
      )}
      {archived.length > 0 && (
        <>
          <h3 className="memory-subhead">Archived</h3>
          <ul className="memory-list" aria-label="Archived collections">
            {archived.map(row)}
          </ul>
        </>
      )}
    </>
  );
}
