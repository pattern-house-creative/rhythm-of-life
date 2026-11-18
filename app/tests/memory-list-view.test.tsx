// The full memory UI's list (SB-P6-01; MemoryList.tsx, MemoryBulk.tsx, MemoryProjects.tsx, MemoryCapsuleCard.tsx,
// MemoryChanges.tsx, MemoryTimeline.tsx, MemorySettingsView.tsx; one entry in Explorer.tsx): each screen at a phone's size, where
// the view covers the sky as the reader does, and at a desktop's. Every step goes through app_memories as the app, a person's
// channel; a screen an older engine can't fill goes on without it. Fictional data only.
import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import Explorer from "../src/Explorer";
import { emptyGraph } from "../src/liveGraph";
import type { GraphSession } from "../src/liveGraph";
import type { KnowledgeController } from "../src/useKnowledge";
import type { GraphNode, GraphPage } from "../src/knowledge";
import type { ChangeGroup, MemoryChange, MemoryClient } from "../src/memories";
import {
  defaultSettings,
  fakeEngine,
  memory,
  project,
  uuid,
} from "./memoryFake";
import { record } from "./synthetic";

const sizes = {
  phone: (query: string) => query.includes("max-width: 760px"),
  desktop: (query: string) => query.includes("min-width: 1100px"),
} as const;
let standard: ((query: string) => MediaQueryList) | undefined;
function at(size: keyof typeof sizes) {
  standard ??= vi.mocked(window.matchMedia).getMockImplementation() as (
    query: string,
  ) => MediaQueryList;
  vi.mocked(window.matchMedia).mockImplementation(
    (query: string) =>
      ({
        matches: sizes[size](query),
        media: query,
        addListener: vi.fn(),
        removeListener: vi.fn(),
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
      }) as unknown as MediaQueryList,
  );
}
afterEach(() => {
  if (standard) vi.mocked(window.matchMedia).mockImplementation(standard);
});

const controller = (
  page: GraphPage,
  extra: Partial<GraphSession> = {},
): KnowledgeController => ({
  page,
  accept: vi.fn(),
  pins: [],
  togglePin: vi.fn(),
  busy: false,
  error: null,
  refresh: vi.fn(async () => {}),
  discoverMore: vi.fn(async () => {}),
  lastRefresh: null,
  epoch: 0,
  session: {
    initial: page,
    refresh: vi.fn(async () => page),
    retrieve: vi.fn(async () => ({ ...page, cursors: {} })),
    ...extra,
  },
});
const props = {
  selected: null,
  onSelect: vi.fn(),
  close: vi.fn(),
  paused: true,
  state: "ready" as const,
};

async function opened(
  client: MemoryClient,
  extra: Partial<Parameters<typeof Explorer>[0]> = {},
  nodes: GraphNode[] = [],
) {
  const page = { ...emptyGraph(), nodes };
  render(
    <Explorer
      {...props}
      knowledge={controller(page, { memories: client })}
      {...extra}
    />,
  );
  await userEvent.click(
    await screen.findByRole("button", { name: "Memories" }),
  );
  return screen.findByRole("region", { name: "Memories" });
}

const section = async (region: HTMLElement, name: string) => {
  await userEvent.click(within(region).getByRole("button", { name }));
};
const change = (
  n: number,
  extra: Partial<MemoryChange> = {},
): MemoryChange => ({
  revision: n,
  event: "updated",
  at: "2026-11-02T09:30:00+00:00",
  person: null,
  channel: "app",
  client: null,
  fields: ["content"],
  status: "draft",
  preview: `DEMO: revision ${n}.`,
  held: false,
  kept: true,
  to: null,
  ...extra,
});
const group = (
  key: string,
  ids: number[],
  extra: Partial<ChangeGroup> = {},
): ChangeGroup => ({
  group: key,
  family: "edit",
  person: null,
  client: "DEMO client",
  changes: ids.length,
  memories: ids.length,
  from: "2026-11-02T09:30:00+00:00",
  until: "2026-11-02T09:34:00+00:00",
  capped: false,
  items: ids.map((n) => ({
    id: uuid(n),
    revision: 2,
    event: "updated",
    at: "2026-11-02T09:34:00+00:00",
    changes: 1,
    preview: `DEMO: memory ${n} as the client left it.`,
    truncated: false,
    held: false,
  })),
  ...extra,
});

describe.each(["phone", "desktop"] as const)("at a %s's size", (size) => {
  it("offers every section, in order, and each title follows", async () => {
    at(size);
    const fake = fakeEngine({ memories: [memory(1, "DEMO: the list.")] });
    const region = await opened(fake.client);
    const strip = within(region).getByRole("toolbar", { name: "Show" });
    expect(
      within(strip)
        .getAllByRole("button")
        .map((b) => b.textContent?.trim()),
    ).toEqual([
      "Recent",
      "Tasks",
      "Due",
      "Decisions",
      "Standing",
      "Projects",
      "AI changes",
      "Review",
      "Copies",
      "Conflicts",
      "To check",
      "Insights",
      "Pairs",
      "Trash",
      "Settings",
    ]);
    for (const [name, title] of [
      ["Projects", "Projects"],
      ["AI changes", "AI changes"],
      ["Settings", "Memory settings"],
      ["Recent", "Memories"],
    ]) {
      await section(region, name);
      expect(
        within(region).getByRole("heading", { name: title }),
      ).toHaveFocus();
    }
  });

  it("filters the list by status, project and tag, says how many match, and clears", async () => {
    at(size);
    const fake = fakeEngine({
      memories: [
        memory(1, "DEMO: the Wendle rota.", {
          tags: ["rota"],
          project: { id: uuid(50), slug: "wendle", name: "Wendle" },
        }),
        memory(2, "DEMO: the bins go out.", { status: "canonical" }),
        memory(3, "DEMO: a held one's words", {
          held: { reason: "instruction", signals: [] },
        }),
      ],
      projects: [
        project(50, "wendle", { name: "Wendle" }),
        project(51, "gone", { name: "Gone", status: "archived" }),
      ],
    });
    const region = await opened(fake.client);
    const list = await within(region).findByRole("list", {
      name: "Recent memories",
    });
    expect(await within(list).findAllByRole("listitem")).toHaveLength(3);
    expect(await within(region).findByText("3 memories.")).toBeInTheDocument();
    // A held memory's row says it is held, without its words.
    expect(within(list).getByText("Held for review")).toBeInTheDocument();
    expect(
      within(list).queryByText(/a held one's words/),
    ).not.toBeInTheDocument();

    const filters = within(region).getByRole("group", {
      name: "Filter these memories",
    });
    // With scopes off there is no scope to filter by; an archived project isn't offered.
    expect(within(filters).queryByLabelText("Scope")).not.toBeInTheDocument();
    const project_ = within(filters).getByLabelText("Project");
    await waitFor(() =>
      expect(
        within(project_).getByRole("option", { name: "Wendle" }),
      ).toBeInTheDocument(),
    );
    expect(
      within(project_).queryByRole("option", { name: "Gone" }),
    ).not.toBeInTheDocument();

    await userEvent.selectOptions(
      within(filters).getByLabelText("Status"),
      "canonical",
    );
    await waitFor(() =>
      expect(fake.asked("list").at(-1)).toEqual({
        op: "list",
        limit: 20,
        status: "canonical",
      }),
    );
    expect(fake.asked("tags").at(-1)).toEqual({
      op: "tags",
      status: "canonical",
    });
    expect(
      await within(region).findByText("1 memory match."),
    ).toBeInTheDocument();
    expect(await within(list).findAllByRole("listitem")).toHaveLength(1);

    await userEvent.click(
      within(filters).getByRole("button", { name: "Clear filters" }),
    );
    expect(await within(region).findByText("3 memories.")).toBeInTheDocument();

    await userEvent.selectOptions(project_, "shared:wendle");
    await waitFor(() =>
      expect(fake.asked("list").at(-1)).toEqual({
        op: "list",
        limit: 20,
        project: "wendle",
        scope: "shared",
      }),
    );
    expect(
      await within(region).findByText("1 memory match."),
    ).toBeInTheDocument();
    // Tags with how many carry each, from the engine's own count.
    const tag = within(filters).getByLabelText("Tag");
    await waitFor(() =>
      expect(
        within(tag).getByRole("option", { name: "rota (1)" }),
      ).toBeInTheDocument(),
    );
    await userEvent.selectOptions(tag, "rota");
    await waitFor(() =>
      expect(fake.asked("list").at(-1)).toEqual({
        op: "list",
        limit: 20,
        tag: "rota",
        project: "wendle",
        scope: "shared",
      }),
    );
    // Nothing matches: it says so, and the filter stays to be cleared.
    await userEvent.selectOptions(
      within(filters).getByLabelText("Status"),
      "deprecated",
    );
    expect(
      await within(region).findByText("No memories match these filters."),
    ).toBeInTheDocument();
    expect(
      within(filters).getByRole("button", { name: "Clear filters" }),
    ).toBeInTheDocument();
  });

  it("offers the scope filter only to a person with scopes on", async () => {
    at(size);
    const fake = fakeEngine({
      memories: [
        memory(1, "DEMO: Alex's own.", {
          scope: "private",
          owner: "alex",
          author: "alex",
        }),
        memory(2, "DEMO: Family's."),
      ],
      settings: { ...defaultSettings, scopes: "on", person: "alex" },
    });
    const region = await opened(fake.client);
    const scope = await within(region).findByLabelText("Scope");
    await userEvent.selectOptions(scope, "private");
    await waitFor(() =>
      expect(fake.asked("list").at(-1)).toEqual({
        op: "list",
        limit: 20,
        scope: "private",
      }),
    );
    expect(
      await within(region).findByRole("button", { name: /Alex's own/ }),
    ).toBeInTheDocument();
    expect(
      within(region).queryByRole("button", { name: /Family's\./ }),
    ).not.toBeInTheDocument();
  });

  it("forgets several behind a confirmation at the revisions shown, answers each, and undoes them at once", async () => {
    at(size);
    const fake = fakeEngine({
      memories: [
        memory(1, "DEMO: forget me."),
        memory(2, "DEMO: me too."),
        memory(3, "DEMO: not yours.", { revision: 4 }),
        memory(4, "DEMO: stay."),
      ],
      forbidden: [uuid(3)],
    });
    const region = await opened(fake.client);
    const list = await within(region).findByRole("list", {
      name: "Recent memories",
    });
    await within(list).findAllByRole("listitem");
    // No bar until a row is chosen: no mode to enter, a box on each row.
    expect(
      within(region).queryByRole("group", { name: "The memories you chose" }),
    ).not.toBeInTheDocument();
    for (const name of [/forget me/, /me too/, /not yours/])
      await userEvent.click(within(list).getByRole("checkbox", { name }));
    const bar = within(region).getByRole("group", {
      name: "The memories you chose",
    });
    expect(within(bar).getByText("3 memories chosen")).toBeInTheDocument();
    await userEvent.click(within(bar).getByRole("button", { name: "Forget…" }));
    // Nothing is sent before the person says so.
    expect(fake.asked("forget_all")).toEqual([]);
    const ask = within(region).getByRole("group", { name: "Forget…" });
    expect(ask).toHaveTextContent(
      "Move these 3 memories to the trash? Each stays there 14 days",
    );
    await userEvent.click(
      within(ask).getByRole("button", { name: "Move to the trash" }),
    );
    await waitFor(() => expect(fake.asked("forget_all")).toHaveLength(1));
    expect(fake.asked("forget_all")[0]).toEqual({
      op: "forget_all",
      memories: [
        { id: uuid(1), expected: 1 },
        { id: uuid(2), expected: 1 },
        { id: uuid(3), expected: 4 },
      ],
    });
    const said = await within(region).findByRole("status");
    expect(said).toHaveTextContent("Moved 2 memories to the trash");
    expect(said).toHaveTextContent("1 was not yours to change");
    await waitFor(() =>
      expect(within(list).getAllByRole("listitem")).toHaveLength(2),
    );
    expect(fake.trash.size).toBe(2);
    // The choice went with them, and Undo brings each back from the trash row it got.
    expect(
      within(region).queryByRole("group", { name: "The memories you chose" }),
    ).not.toBeInTheDocument();
    await userEvent.click(within(said).getByRole("button", { name: "Undo" }));
    await waitFor(() => expect(fake.asked("restore_all")).toHaveLength(1));
    expect(await within(region).findByRole("status")).toHaveTextContent(
      "Brought back 2 memories.",
    );
    expect(fake.trash.size).toBe(0);
    await waitFor(() =>
      expect(within(list).getAllByRole("listitem")).toHaveLength(4),
    );
  });

  it("keeps a memory that changed since it was shown, and says so", async () => {
    at(size);
    const fake = fakeEngine({ memories: [memory(1, "DEMO: changed later.")] });
    const region = await opened(fake.client);
    const list = await within(region).findByRole("list", {
      name: "Recent memories",
    });
    await within(list).findAllByRole("listitem");
    await userEvent.click(
      within(list).getByRole("checkbox", { name: /changed later/ }),
    );
    // Another reader changes it meanwhile.
    fake.live.set(uuid(1), memory(1, "DEMO: changed later.", { revision: 2 }));
    const bar = within(region).getByRole("group", {
      name: "The memories you chose",
    });
    await userEvent.click(within(bar).getByRole("button", { name: "Forget…" }));
    await userEvent.click(
      within(region).getByRole("button", { name: "Move to the trash" }),
    );
    const said = await within(region).findByRole("status");
    expect(said).toHaveTextContent("Nothing was moved to the trash.");
    expect(said).toHaveTextContent("1 changed since you read it and stays");
    expect(within(said).queryByRole("button", { name: "Undo" })).toBeNull();
    expect(fake.live.has(uuid(1))).toBe(true);
  });

  it("takes a question back with Escape before the view, and a changed filter clears the choice", async () => {
    at(size);
    const fake = fakeEngine({
      memories: [memory(1, "DEMO: choose me.", { status: "canonical" })],
    });
    const region = await opened(fake.client);
    const list = await within(region).findByRole("list", {
      name: "Recent memories",
    });
    await within(list).findAllByRole("listitem");
    await userEvent.click(
      within(list).getByRole("checkbox", { name: /choose me/ }),
    );
    await userEvent.click(
      within(region).getByRole("button", { name: "Forget…" }),
    );
    expect(
      within(region).getByRole("button", { name: "Move to the trash" }),
    ).toBeInTheDocument();
    await userEvent.keyboard("{Escape}");
    await waitFor(() =>
      expect(
        within(region).queryByRole("button", { name: "Move to the trash" }),
      ).not.toBeInTheDocument(),
    );
    expect(
      screen.getByRole("region", { name: "Memories" }),
    ).toBeInTheDocument();
    await userEvent.selectOptions(
      within(region).getByLabelText("Status"),
      "canonical",
    );
    await waitFor(() =>
      expect(
        within(region).queryByRole("group", {
          name: "The memories you chose",
        }),
      ).not.toBeInTheDocument(),
    );
  });

  it("offers a share of several only while the engine takes one, each scope's way, and says what moved", async () => {
    at(size);
    const rows = [
      memory(1, "DEMO: private one.", {
        scope: "private",
        owner: "alex",
        author: "alex",
      }),
      memory(2, "DEMO: private two.", {
        scope: "private",
        owner: "alex",
        author: "alex",
        project: { id: uuid(50), slug: "plot", name: "Plot" },
      }),
      memory(3, "DEMO: Family's, by Alex.", { author: "alex" }),
    ];
    // Off: no share is offered, whatever is chosen.
    {
      const fake = fakeEngine({ memories: rows });
      const region = await opened(fake.client);
      const list = await within(region).findByRole("list", {
        name: "Recent memories",
      });
      await within(list).findAllByRole("listitem");
      await userEvent.click(
        within(list).getByRole("checkbox", { name: /private one/ }),
      );
      expect(
        within(region).queryByRole("button", { name: /Share with Family/ }),
      ).not.toBeInTheDocument();
      expect(
        within(region).queryByRole("button", { name: /Make private/ }),
      ).not.toBeInTheDocument();
      document.body.innerHTML = "";
    }
    const fake = fakeEngine({ memories: rows, sharing: true });
    const region = await opened(fake.client);
    const list = await within(region).findByRole("list", {
      name: "Recent memories",
    });
    await within(list).findAllByRole("listitem");
    for (const name of [/private one/, /private two/, /Family's, by Alex/])
      await userEvent.click(within(list).getByRole("checkbox", { name }));
    const bar = within(region).getByRole("group", {
      name: "The memories you chose",
    });
    await waitFor(() =>
      expect(
        within(bar).getByRole("button", { name: "Share with Family…" }),
      ).toBeInTheDocument(),
    );
    await userEvent.click(
      within(bar).getByRole("button", { name: "Share with Family…" }),
    );
    expect(
      within(region).getByRole("group", { name: "Share with Family…" }),
    ).toHaveTextContent("Share these 2 memories with Family?");
    await userEvent.click(
      within(region).getByRole("button", { name: "Share with Family" }),
    );
    await waitFor(() => expect(fake.asked("share_all")).toHaveLength(1));
    // Only those of the other scope: a Family memory is not shared again.
    expect(fake.asked("share_all")[0]).toEqual({
      op: "share_all",
      scope: "shared",
      memories: [
        { id: uuid(1), expected: 1 },
        { id: uuid(2), expected: 1 },
      ],
    });
    const said = await within(region).findByRole("status");
    expect(said).toHaveTextContent("Shared 2 memories with Family");
    expect(said).toHaveTextContent("1 left its project behind");
  });

  it("says when a memory couldn't be taken back, and the rest of the step still went", async () => {
    at(size);
    const fake = fakeEngine({
      memories: [
        memory(1, "DEMO: Family's, first.", { author: "alex" }),
        memory(2, "DEMO: Family's, linked.", { author: "alex" }),
        memory(3, "DEMO: Family's, last.", { author: "alex" }),
      ],
      sharing: true,
      shareAnswers: { [uuid(2)]: "invalid" },
    });
    const region = await opened(fake.client);
    const list = await within(region).findByRole("list", {
      name: "Recent memories",
    });
    await within(list).findAllByRole("listitem");
    for (const name of [/first/, /linked/, /last/])
      await userEvent.click(within(list).getByRole("checkbox", { name }));
    const bar = within(region).getByRole("group", {
      name: "The memories you chose",
    });
    await userEvent.click(
      await within(bar).findByRole("button", { name: "Make private…" }),
    );
    await userEvent.click(
      within(region).getByRole("button", { name: "Take them back" }),
    );
    await waitFor(() => expect(fake.asked("share_all")).toHaveLength(1));
    const said = await within(region).findByRole("status");
    expect(said).toHaveTextContent("Took 2 memories back");
    expect(said).toHaveTextContent(
      "1 couldn't move and stays where it is: a memory linked to another person's private record stays in Family.",
    );
    expect(said).not.toHaveTextContent("not yours to change");
    // Each was answered, so the choice has gone with them.
    expect(
      within(region).queryByRole("group", { name: "The memories you chose" }),
    ).not.toBeInTheDocument();
  });

  it("sends a step of several once, however often it is tapped", async () => {
    at(size);
    const fake = fakeEngine({
      memories: [memory(1, "DEMO: forget me."), memory(2, "DEMO: stay.")],
    });
    const region = await opened(fake.client);
    const list = await within(region).findByRole("list", {
      name: "Recent memories",
    });
    await within(list).findAllByRole("listitem");
    await userEvent.click(
      within(list).getByRole("checkbox", { name: /forget me/ }),
    );
    const bar = within(region).getByRole("group", {
      name: "The memories you chose",
    });
    await userEvent.click(within(bar).getByRole("button", { name: "Forget…" }));
    const letForget = fake.hold("forget_all");
    await userEvent.click(
      within(region).getByRole("button", { name: "Move to the trash" }),
    );
    await waitFor(() => expect(fake.asked("forget_all")).toHaveLength(1));
    // While it is on its way the bar takes nothing more.
    const forget = within(bar).getByRole("button", { name: "Forget…" });
    expect(forget).toBeDisabled();
    expect(
      within(bar).getByRole("button", { name: "Clear choice" }),
    ).toBeDisabled();
    await userEvent.click(forget);
    expect(fake.asked("forget_all")).toHaveLength(1);
    letForget();
    const said = await within(region).findByRole("status");
    await waitFor(() => expect(said).toHaveTextContent("Moved 1 memory"));

    // Its Undo stays on screen until the restore answers, and a second tap sends nothing.
    const letRestore = fake.hold("restore_all");
    const undo = within(said).getByRole("button", { name: "Undo" });
    await userEvent.click(undo);
    await userEvent.click(undo);
    expect(fake.asked("restore_all")).toHaveLength(1);
    letRestore();
    await waitFor(() =>
      expect(within(region).getByRole("status")).toHaveTextContent(
        "Brought back 1 memory.",
      ),
    );
    expect(fake.asked("restore_all")).toHaveLength(1);
  });

  it("keeps projects: makes one with its slug's preview, edits, archives and brings back, shows its memories and capsule, and deletes it", async () => {
    at(size);
    const fake = fakeEngine({
      memories: [
        memory(1, "DEMO: in the plot.", {
          project: { id: uuid(50), slug: "wendle", name: "Wendle" },
        }),
      ],
      projects: [
        project(50, "wendle", {
          name: "Wendle",
          description: "DEMO: the allotment.",
          aliases: ["plot"],
        }),
      ],
    });
    const region = await opened(fake.client);
    await section(region, "Projects");
    const list = await within(region).findByRole("list", { name: "Projects" });
    expect(
      await within(list).findByRole("button", { name: /Wendle/ }),
    ).toHaveTextContent("also plot");

    await userEvent.click(
      within(region).getByRole("button", { name: "New project…" }),
    );
    const name = within(region).getByLabelText("Name");
    await userEvent.type(name, "Canal Walks!");
    expect(
      within(region).getByText(/It will be known as canal-walks/),
    ).toBeInTheDocument();
    await userEvent.type(
      within(region).getByLabelText("What it is"),
      "DEMO: towpaths.",
    );
    await userEvent.type(
      within(region).getByLabelText("Other names, separated by commas"),
      "Towpath, walks",
    );
    await userEvent.click(
      within(region).getByRole("button", { name: "Make the project" }),
    );
    await waitFor(() => expect(fake.asked("project_create")).toHaveLength(1));
    expect(fake.asked("project_create")[0]).toEqual({
      op: "project_create",
      name: "Canal Walks!",
      description: "DEMO: towpaths.",
      aliases: ["towpath", "walks"],
      scope: "shared",
    });
    expect(await within(region).findByRole("status")).toHaveTextContent(
      "Made the project Canal Walks!: it is known as canal-walks.",
    );
    // A name taken is the engine's to say, and the form stays.
    await userEvent.click(
      within(region).getByRole("button", { name: "New project…" }),
    );
    await userEvent.type(within(region).getByLabelText("Name"), "Wendle");
    await userEvent.click(
      within(region).getByRole("button", { name: "Make the project" }),
    );
    expect(await within(region).findByRole("status")).toHaveTextContent(
      "Another project in this scope already answers to wendle",
    );
    await userEvent.click(
      within(region).getByRole("button", { name: "Cancel" }),
    );

    // The project opens in place: its words, how many memories, its capsule.
    await userEvent.click(within(list).getByRole("button", { name: /Wendle/ }));
    expect(
      await within(region).findByText("DEMO: the allotment."),
    ).toBeInTheDocument();
    expect(
      await within(region).findByText("1 memory in it."),
    ).toBeInTheDocument();
    const capsule = await within(region).findByRole("region", {
      name: "The wendle project's capsule",
    });
    expect(capsule).toHaveTextContent("1 of 3 slots filled");
    expect(fake.asked("capsule_status").at(-1)).toEqual({
      op: "capsule_status",
      capsule: "project",
      project: "wendle",
      scope: "shared",
    });

    const actions = within(region).getByRole("group", { name: "This project" });
    await userEvent.click(
      within(actions).getByRole("button", { name: "Edit" }),
    );
    const editName = within(region).getByLabelText("Name");
    expect(editName).toHaveValue("Wendle");
    await userEvent.clear(editName);
    await userEvent.type(editName, "Wendle Plot");
    await userEvent.click(within(region).getByRole("button", { name: "Save" }));
    await waitFor(() => expect(fake.asked("project_update")).toHaveLength(1));
    expect(fake.asked("project_update")[0]).toEqual({
      op: "project_update",
      id: uuid(50),
      name: "Wendle Plot",
      description: "DEMO: the allotment.",
      aliases: ["plot"],
      expected: 1,
    });
    await within(region).findByRole("button", { name: /Wendle Plot/ });

    // Archived, it moves under its own heading; Undo brings it back at the revision the archive left.
    await userEvent.click(
      await within(region).findByRole("button", { name: "Archive" }),
    );
    const archived = await within(region).findByRole("list", {
      name: "Archived projects",
    });
    expect(
      within(archived).getByRole("button", { name: /Wendle Plot/ }),
    ).toBeInTheDocument();
    expect(fake.asked("project_update").at(-1)).toMatchObject({
      archived: true,
      expected: 2,
    });
    await userEvent.click(
      within(await within(region).findByRole("status")).getByRole("button", {
        name: "Undo",
      }),
    );
    await waitFor(() =>
      expect(fake.asked("project_update").at(-1)).toMatchObject({
        archived: false,
        expected: 3,
      }),
    );

    // Its memories in the list, the filter set.
    await userEvent.click(
      await within(region).findByRole("button", { name: "Show its memories" }),
    );
    await waitFor(() =>
      expect(fake.asked("list").at(-1)).toEqual({
        op: "list",
        limit: 20,
        project: "wendle",
        scope: "shared",
      }),
    );
    expect(
      within(region).getByRole("heading", { name: "Memories" }),
    ).toBeInTheDocument();
    expect(within(region).getByLabelText("Project")).toHaveValue(
      "shared:wendle",
    );

    // Deleted behind a confirmation in the same flow: its memories stay.
    await section(region, "Projects");
    await userEvent.click(
      await within(region).findByRole("button", { name: "Delete…" }),
    );
    expect(fake.asked("project_delete")).toEqual([]);
    const ask = within(region).getByRole("group", {
      name: "Delete this project?",
    });
    expect(ask).toHaveTextContent("Its memories stay, in no project");
    await userEvent.click(
      within(ask).getByRole("button", { name: "Delete the project" }),
    );
    await waitFor(() => expect(fake.asked("project_delete")).toHaveLength(1));
    expect(await within(region).findByRole("status")).toHaveTextContent(
      "2 memories left it and stay as they were, in no project.",
    );
    expect(fake.live.has(uuid(1))).toBe(true);
  });

  it("asks for a slug when a name makes none, and stops filtering by a project deleted", async () => {
    at(size);
    const fake = fakeEngine({
      projects: [project(50, "wendle", { name: "Wendle" })],
    });
    const region = await opened(fake.client);
    await section(region, "Projects");
    await userEvent.click(
      within(region).getByRole("button", { name: "New project…" }),
    );
    await userEvent.type(within(region).getByLabelText("Name"), "!!!");
    const make = within(region).getByRole("button", {
      name: "Make the project",
    });
    expect(make).toBeDisabled();
    await userEvent.type(
      within(region).getByLabelText(/What it is known as/),
      "bangs",
    );
    expect(make).toBeEnabled();
    await userEvent.click(make);
    await waitFor(() => expect(fake.asked("project_create")).toHaveLength(1));
    expect(fake.asked("project_create")[0]).toMatchObject({ slug: "bangs" });

    // A slug typed for a name that made none is not sent once the name makes one: the preview's is the one it gets.
    await userEvent.click(
      await within(region).findByRole("button", { name: "New project…" }),
    );
    const named = within(region).getByLabelText("Name");
    await userEvent.type(named, "!!!");
    await userEvent.type(
      within(region).getByLabelText(/What it is known as/),
      "foo",
    );
    await userEvent.clear(named);
    await userEvent.type(named, "Canal Walks");
    expect(
      within(region).getByText(/It will be known as canal-walks/),
    ).toBeInTheDocument();
    await userEvent.click(
      within(region).getByRole("button", { name: "Make the project" }),
    );
    await waitFor(() => expect(fake.asked("project_create")).toHaveLength(2));
    expect(fake.asked("project_create")[1].name).toBe("Canal Walks");
    expect(fake.asked("project_create")[1]).not.toHaveProperty("slug");

    await section(region, "Recent");
    const filter = await within(region).findByLabelText("Project");
    await waitFor(() =>
      expect(
        within(filter).getByRole("option", { name: "Wendle" }),
      ).toBeInTheDocument(),
    );
    await userEvent.selectOptions(filter, "shared:wendle");
    await section(region, "Projects");
    await userEvent.click(
      await within(region).findByRole("button", { name: /Wendle/ }),
    );
    await userEvent.click(
      await within(region).findByRole("button", { name: "Delete…" }),
    );
    await userEvent.click(
      within(region).getByRole("button", { name: "Delete the project" }),
    );
    await waitFor(() => expect(fake.asked("project_delete")).toHaveLength(1));
    await section(region, "Recent");
    await waitFor(() =>
      expect(fake.asked("list").at(-1)).toEqual({ op: "list", limit: 20 }),
    );
    expect(await within(region).findByLabelText("Project")).toHaveValue("");
  });

  it("keeps the choice when a step of several is refused whole", async () => {
    at(size);
    const fake = fakeEngine({ memories: [memory(1, "DEMO: refused whole.")] });
    const region = await opened(fake.client);
    const list = await within(region).findByRole("list", {
      name: "Recent memories",
    });
    await within(list).findAllByRole("listitem");
    await userEvent.click(
      within(list).getByRole("checkbox", { name: /refused whole/ }),
    );
    await userEvent.click(
      within(region).getByRole("button", { name: "Forget…" }),
    );
    fake.refuseNext("forget_all", "unavailable", "DEMO: no answer came back.");
    await userEvent.click(
      within(region).getByRole("button", { name: "Move to the trash" }),
    );
    expect(await within(region).findByRole("status")).toHaveTextContent(
      "DEMO: no answer came back.",
    );
    expect(
      within(region).getByRole("group", { name: "The memories you chose" }),
    ).toHaveTextContent("1 memory chosen");
  });

  it("shows why a project can't be changed, in the engine's words", async () => {
    at(size);
    const fake = fakeEngine({
      projects: [project(50, "wendle", { name: "Wendle", author: "alex" })],
      forbidden: [uuid(50)],
    });
    const region = await opened(fake.client);
    await section(region, "Projects");
    await userEvent.click(
      await within(region).findByRole("button", { name: /Wendle/ }),
    );
    await userEvent.click(
      await within(region).findByRole("button", { name: "Archive" }),
    );
    expect(await within(region).findByRole("status")).toHaveTextContent(
      "Only this project's creator or a Family admin may change it",
    );
    expect(fake.registry.get(uuid(50))?.status).toBe("active");
  });

  it("reads a capsule slot by slot, with what waits, and opens a memory from it", async () => {
    at(size);
    const fake = fakeEngine({
      memories: [memory(1, "DEMO: who we are."), memory(4, "DEMO: a draft.")],
    });
    const region = await opened(fake.client);
    await section(region, "Settings");
    const capsule = await within(region).findByRole("region", {
      name: "Family's core capsule",
    });
    expect(capsule).toHaveTextContent("1 of 4 slots filled");
    expect(capsule).toHaveTextContent("Identity · Filled.");
    expect(capsule).toHaveTextContent(
      "Preferences · Left out: 2 memories fill it, and it takes one.",
    );
    expect(capsule).toHaveTextContent("Constraints · No memory fills it.");
    expect(capsule).toHaveTextContent(
      "One is a draft: served once it is confirmed.",
    );
    expect(capsule).toHaveTextContent(
      "One is held for review: served once a person releases it.",
    );
    expect(capsule).toHaveTextContent(
      "AI clients are offered no capsule on this install",
    );
    await userEvent.click(
      within(capsule).getAllByRole("button", { name: "Open it" })[0],
    );
    expect(
      await within(region).findByText("DEMO: who we are."),
    ).toBeInTheDocument();
    expect(fake.asked("get").at(-1)).toEqual({ op: "get", id: uuid(1) });
  });

  it("shows AI changes by group, and one tap undoes the whole group, as many calls as it takes, then redoes", async () => {
    at(size);
    const fake = fakeEngine({
      memories: [1, 2, 3, 4, 5, 6, 7].map((n) =>
        memory(n, `DEMO: memory ${n}.`),
      ),
      groups: [
        group("group-key-one", [1, 2, 3, 4, 5, 6, 7], {
          person: "alex",
          changes: 9,
        }),
      ],
    });
    const region = await opened(fake.client);
    await section(region, "AI changes");
    const list = await within(region).findByRole("list", {
      name: "AI changes",
    });
    const row = await within(list).findByText(
      /An AI client \(DEMO client\) for/,
    );
    expect(row).toHaveTextContent("edited 7 memories (9 changes)");
    expect(fake.asked("changes")[0].since).toEqual(expect.any(String));
    await userEvent.click(
      within(list).getByRole("button", { name: "Undo these" }),
    );
    // Five a call: the second call finishes it.
    await waitFor(() => expect(fake.asked("undo_group")).toHaveLength(2));
    expect(fake.asked("undo_group")[0]).toEqual({
      op: "undo_group",
      group: "group-key-one",
    });
    const said = await within(region).findByRole("status");
    expect(said).toHaveTextContent("Undone: 5 memories are back");
    // The trail keeps its changes, so it stays listed, and says it is undone rather than offer it again.
    expect(
      await within(list).findByRole("button", { name: "Undone" }),
    ).toBeDisabled();
    expect(said).toHaveTextContent(
      "2 changed after these and stay as they are",
    );
    fake.resetUndo();
    const before = fake.asked("undo_group").length;
    // Its Redo stays on screen until the undo answers, and a second tap sends nothing more.
    const letRedo = fake.hold("undo_group");
    const redo = within(said).getByRole("button", { name: "Redo" });
    await userEvent.click(redo);
    await userEvent.click(redo);
    expect(fake.asked("undo_group")).toHaveLength(before + 1);
    letRedo();
    await waitFor(() =>
      expect(fake.asked("undo_group").at(-1)).toEqual({
        op: "undo_group",
        group: "redo-key",
      }),
    );
    expect(
      await within(list).findByRole("button", { name: "Undo these" }),
    ).toBeEnabled();
    // Five a call, so the redo's own second call finished it, and no more were sent.
    expect(fake.asked("undo_group")).toHaveLength(before + 2);
  });

  it("keeps a group's undo that stopped partway, and the same key takes the rest", async () => {
    at(size);
    const fake = fakeEngine({
      memories: [1, 2, 3, 4, 5, 6, 7].map((n) =>
        memory(n, `DEMO: memory ${n}.`),
      ),
      groups: [group("group-key-one", [1, 2, 3, 4, 5, 6, 7])],
    });
    const region = await opened(fake.client);
    await section(region, "AI changes");
    const list = await within(region).findByRole("list", {
      name: "AI changes",
    });
    await within(list).findByRole("button", { name: "Undo these" });
    fake.refuseNext("undo_group", "unavailable", "DEMO: no answer.", 1);
    await userEvent.click(
      within(list).getByRole("button", { name: "Undo these" }),
    );
    await waitFor(() => expect(fake.asked("undo_group")).toHaveLength(2));
    const said = await within(region).findByRole("status");
    await waitFor(() => expect(said).toHaveTextContent("The rest didn't go"));
    expect(said).toHaveTextContent("Undone: 4 memories are back");
    // It isn't all undone, so it doesn't say so: the button is there, and takes the rest.
    const again = await within(list).findByRole("button", {
      name: "Undo these",
    });
    expect(again).toBeEnabled();
    await userEvent.click(again);
    await waitFor(() => expect(fake.asked("undo_group")).toHaveLength(3));
    expect(fake.asked("undo_group")[2]).toEqual({
      op: "undo_group",
      group: "group-key-one",
    });
    expect(
      await within(list).findByRole("button", { name: "Undone" }),
    ).toBeDisabled();
  });

  it("names a held memory in an AI change without its words, opens one, and widens the window", async () => {
    at(size);
    const base = group("group-key-two", [1, 2]);
    const fake = fakeEngine({
      memories: [memory(1, "DEMO: as the client left it, whole.")],
      groups: [
        {
          ...base,
          memories: 25,
          items: [
            { ...base.items[0], changes: 3 },
            { ...base.items[1], preview: null, held: true },
          ],
        },
      ],
    });
    const region = await opened(fake.client);
    await section(region, "AI changes");
    const list = await within(region).findByRole("list", {
      name: "AI changes",
    });
    expect(
      await within(list).findByText("Held for review"),
    ).toBeInTheDocument();
    expect(within(list).getByText("3 changes")).toBeInTheDocument();
    expect(within(list).getByText("And 23 more.")).toBeInTheDocument();
    await userEvent.click(
      within(region).getByRole("button", { name: "Show the last 7 days" }),
    );
    await waitFor(() => expect(fake.asked("changes")).toHaveLength(2));
    const days =
      (Date.now() - Date.parse(String(fake.asked("changes")[1].since))) /
      86_400_000;
    expect(Math.round(days)).toBe(7);
    await userEvent.click(
      within(list).getByRole("button", { name: /memory 1 as the client/ }),
    );
    expect(
      await within(region).findByText("DEMO: as the client left it, whole."),
    ).toBeInTheDocument();
  });

  it("says when no AI client has changed anything, and an undo refused in the engine's words", async () => {
    at(size);
    const quiet = fakeEngine();
    const region = await opened(quiet.client);
    await section(region, "AI changes");
    expect(
      await within(region).findByText(
        "No AI client has changed anything in this time.",
      ),
    ).toBeInTheDocument();
    document.body.innerHTML = "";
    const fake = fakeEngine({ groups: [group("group-key-three", [1])] });
    const again = await opened(fake.client);
    await section(again, "AI changes");
    fake.refuseNext(
      "undo_group",
      "unknown",
      "Unknown group: list the changes again for a current one",
    );
    await userEvent.click(
      await within(again).findByRole("button", { name: "Undo these" }),
    );
    expect(fake.asked("undo_group")).toHaveLength(1);
    expect(await within(again).findByRole("status")).toHaveTextContent(
      "Unknown group: list the changes again for a current one",
    );
  });

  it("reads a memory's history as a timeline: by day, an AI client's named as one, earlier pages on asking", async () => {
    at(size);
    const fake = fakeEngine({
      memories: [memory(1, "DEMO: a timeline.", { revision: 3 })],
      history: {
        [uuid(1)]: [
          [
            change(3, {
              at: "2026-11-02T09:30:00+00:00",
              channel: "mcp",
              person: "alex",
              client: "DEMO client",
            }),
            change(2, { at: "2026-11-01T09:30:00+00:00", person: "alex" }),
          ],
          [change(1, { at: "2026-10-30T09:30:00+00:00", event: "created" })],
        ],
      },
    });
    const region = await opened(fake.client);
    await userEvent.click(
      await within(region).findByRole("button", { name: /a timeline/ }),
    );
    await userEvent.click(
      await within(region).findByRole("button", { name: "History" }),
    );
    const history = await within(region).findByRole("list", {
      name: "History",
    });
    const rows = within(history).getAllByRole("listitem");
    expect(rows).toHaveLength(2);
    expect(rows[0]).toHaveTextContent("2 November 2026");
    expect(rows[1]).toHaveTextContent("1 November 2026");
    // An AI client that speaks for a person is an AI client's change, never that person's.
    expect(rows[0]).toHaveTextContent(
      "Edited (content) · an AI client (DEMO client) for Alex",
    );
    expect(rows[0].className).toContain("memory-by-ai");
    expect(rows[1].className).not.toContain("memory-by-ai");
    expect(rows[1]).toHaveTextContent("Edited (content) · Alex");

    await userEvent.click(
      within(region).getByRole("button", { name: "Show earlier changes" }),
    );
    await waitFor(() =>
      expect(within(history).getAllByRole("listitem")).toHaveLength(3),
    );
    expect(fake.asked("history").at(-1)).toEqual({
      op: "history",
      id: uuid(1),
      before: 1,
      limit: 50,
    });
    expect(within(history).getAllByRole("listitem")[2]).toHaveTextContent(
      "30 October 2026",
    );
    expect(within(region).getByText(/some earlier revisions can no longer be opened/i)).toBeInTheDocument();
  });

  it("never lets a client's own name read as a person or the terminal", async () => {
    at(size);
    const fake = fakeEngine({
      memories: [memory(1, "DEMO: names.", { revision: 3 })],
      history: {
        [uuid(1)]: [
          [
            change(3, { channel: "mcp", client: "Alex" }),
            change(2, { channel: "mcp", client: null }),
            change(1, { channel: "capture", client: "the terminal" }),
          ],
        ],
      },
    });
    const region = await opened(fake.client);
    await userEvent.click(
      await within(region).findByRole("button", { name: /names/ }),
    );
    await userEvent.click(
      await within(region).findByRole("button", { name: "History" }),
    );
    const rows = within(
      await within(region).findByRole("list", { name: "History" }),
    ).getAllByRole("listitem");
    expect(rows[0]).toHaveTextContent("· an AI client (Alex) ·");
    expect(rows[1]).toHaveTextContent("· an AI client ·");
    expect(rows[2]).toHaveTextContent(
      "· a capture connection (the terminal) ·",
    );
  });

  it("gives a memory its project and tags, sending only what changed", async () => {
    at(size);
    const fake = fakeEngine({
      memories: [
        memory(1, "DEMO: organise me.", {
          tags: ["rota"],
          project: { id: uuid(50), slug: "wendle", name: "Wendle" },
        }),
        memory(2, "DEMO: a held one's words", {
          held: { reason: "instruction", signals: [] },
        }),
      ],
      projects: [
        project(50, "wendle", { name: "Wendle" }),
        project(51, "canal", { name: "Canal" }),
        project(52, "theirs", {
          name: "Theirs",
          scope: "private",
          owner: "sam",
        }),
      ],
    });
    const region = await opened(fake.client);
    await userEvent.click(
      await within(region).findByRole("button", { name: /organise me/ }),
    );
    await userEvent.click(
      await within(region).findByRole("button", { name: "Project and tags…" }),
    );
    const pick = within(region).getByLabelText("Project");
    await waitFor(() =>
      expect(
        within(pick).getByRole("option", { name: "Canal" }),
      ).toBeInTheDocument(),
    );
    // Only the projects of its own scope.
    expect(
      within(pick).queryByRole("option", { name: "Theirs" }),
    ).not.toBeInTheDocument();
    expect(pick).toHaveValue("wendle");
    const tags = within(region).getByLabelText("Tags, separated by commas");
    expect(tags).toHaveValue("rota");
    await userEvent.click(within(region).getByRole("button", { name: "Save" }));
    // Nothing changed: nothing sent.
    expect(fake.asked("update")).toEqual([]);

    await userEvent.click(
      await within(region).findByRole("button", { name: "Project and tags…" }),
    );
    await userEvent.selectOptions(
      within(region).getByLabelText("Project"),
      "canal",
    );
    await userEvent.click(within(region).getByRole("button", { name: "Save" }));
    await waitFor(() => expect(fake.asked("update")).toHaveLength(1));
    expect(fake.asked("update")[0]).toEqual({
      op: "update",
      id: uuid(1),
      project: "canal",
      expected: 1,
    });

    await userEvent.click(
      await within(region).findByRole("button", { name: "Project and tags…" }),
    );
    await userEvent.selectOptions(within(region).getByLabelText("Project"), "");
    const again = within(region).getByLabelText("Tags, separated by commas");
    await userEvent.clear(again);
    await userEvent.type(again, "#Rota, bins");
    await userEvent.click(within(region).getByRole("button", { name: "Save" }));
    await waitFor(() => expect(fake.asked("update")).toHaveLength(2));
    expect(fake.asked("update")[1]).toEqual({
      op: "update",
      id: uuid(1),
      tags: ["rota", "bins"],
      clear: ["project"],
      expected: 2,
    });

    // A held memory waits for a person's release first.
    await userEvent.click(
      within(region).getByRole("button", { name: "Back to the list" }),
    );
    await userEvent.click(
      await within(region).findByRole("button", { name: /Held for review/ }),
    );
    await within(region).findByRole("group", { name: "This memory" });
    expect(
      within(region).queryByRole("button", { name: "Project and tags…" }),
    ).not.toBeInTheDocument();
  });

  it("restores and deletes several from the trash, delete only behind a confirmation and only those this person may", async () => {
    at(size);
    const fake = fakeEngine({
      memories: [
        memory(1, "DEMO: one."),
        memory(2, "DEMO: two."),
        memory(3, "DEMO: three."),
      ],
      forbidden: [uuid(3)],
      cascades: {
        [uuid(3)]: {
          reopened: [],
          reclosed: [uuid(9)],
          flagged: [],
          unflagged: [],
        },
      },
    });
    for (const n of [1, 2, 3]) {
      fake.trash.set(uuid(n), {
        memory: fake.live.get(uuid(n))!,
        nonce: uuid(7000 + n),
      });
      fake.live.delete(uuid(n));
    }
    const region = await opened(fake.client);
    await section(region, "Trash");
    const list = await within(region).findByRole("list", { name: "Trash" });
    const boxes = await within(list).findAllByRole("checkbox", {
      name: "Choose",
    });
    expect(boxes).toHaveLength(3);
    await userEvent.click(boxes[0]);
    let bar = within(region).getByRole("group", {
      name: "The memories you chose",
    });
    await userEvent.click(
      within(bar).getByRole("button", { name: "Choose all 3 shown" }),
    );
    expect(within(bar).getByText("3 memories chosen")).toBeInTheDocument();
    await userEvent.click(
      within(bar).getByRole("button", { name: "Delete for good…" }),
    );
    expect(fake.asked("delete_all")).toEqual([]);
    // Two may be deleted by this person; the third is not sent.
    expect(
      within(region).getByRole("group", { name: "Delete for good…" }),
    ).toHaveTextContent(
      "Delete these 2 memories for good? This can't be undone.",
    );
    await userEvent.click(
      within(region).getByRole("button", { name: "Delete for good" }),
    );
    await waitFor(() => expect(fake.asked("delete_all")).toHaveLength(1));
    expect(fake.asked("delete_all")[0]).toEqual({
      op: "delete_all",
      memories: [uuid(1), uuid(2)].map((id, i) => ({
        id,
        nonce: uuid(7001 + i),
        confirm: id,
      })),
    });
    expect(await within(region).findByRole("status")).toHaveTextContent(
      "Deleted 2 memories for good.",
    );

    await waitFor(() =>
      expect(within(list).getAllByRole("listitem")).toHaveLength(1),
    );
    await userEvent.click(
      within(list).getByRole("checkbox", { name: "Choose" }),
    );
    bar = within(region).getByRole("group", { name: "The memories you chose" });
    expect(
      within(bar).queryByRole("button", { name: "Delete for good…" }),
    ).not.toBeInTheDocument();
    // A step with no question goes at the first tap: while it is on its way a second sends nothing.
    const letRestore = fake.hold("restore_all");
    const restore = within(bar).getByRole("button", { name: "Restore" });
    await userEvent.click(restore);
    await waitFor(() => expect(restore).toBeDisabled());
    await userEvent.click(restore);
    expect(fake.asked("restore_all")).toHaveLength(1);
    letRestore();
    const said = await within(region).findByRole("status");
    expect(said).toHaveTextContent("Brought back 1 memory.");
    // What the restore did to the others is said, as one memory's restore says it.
    expect(said).toHaveTextContent(
      "1 memory it had replaced is replaced by it again.",
    );
    expect(fake.live.has(uuid(3))).toBe(true);
  });

  it("shows the memory settings to read, and changes nothing", async () => {
    at(size);
    const fake = fakeEngine({
      settings: {
        ...defaultSettings,
        scopes: "on",
        person: "alex",
        share: true,
        capsules: true,
      },
    });
    const region = await opened(fake.client);
    await section(region, "Settings");
    const before = fake.calls.length;
    expect(
      await within(region).findByText(/You read as Alex/),
    ).toBeInTheDocument();
    const list = within(region).getByText("Per-person scopes").closest("dl")!;
    expect(list).toHaveTextContent("Per-person scopes On");
    expect(list).toHaveTextContent("Sharing On");
    expect(list).toHaveTextContent("Capsules for AI clients On");
    expect(list).toHaveTextContent("Capture route Off");
    expect(list).toHaveTextContent("The trash keeps 14 days");
    expect(list).toHaveTextContent("Earlier states kept 20 or more");
    expect(
      within(region).getByText(/These change in rhythm.yaml/),
    ).toBeInTheDocument();
    // Nothing to change here: no box to tick and no button in the list.
    expect(within(list).queryByRole("checkbox")).toBeNull();
    expect(within(list).queryByRole("button")).toBeNull();
    expect(
      fake.calls.slice(before).every((c) => c.op === "capsule_status"),
    ).toBe(true);
    // A person also has an own core capsule.
    expect(
      await within(region).findByRole("region", {
        name: "Alex's own core capsule",
      }),
    ).toBeInTheDocument();
  });

  it("goes on without what an older engine can't say", async () => {
    at(size);
    const fake = fakeEngine({
      memories: [memory(1, "DEMO: still listed.")],
      older: true,
    });
    const region = await opened(fake.client);
    expect(
      await within(region).findByRole("button", { name: /still listed/ }),
    ).toBeInTheDocument();
    // No count, no tags, nothing said in error for reads it could do without.
    expect(within(region).queryByRole("status")).not.toBeInTheDocument();
    await section(region, "Settings");
    expect(
      await within(region).findByText(/doesn't say its memory settings yet/),
    ).toBeInTheDocument();
    expect(within(region).queryByRole("status")).not.toBeInTheDocument();
  });

  it("says when memories go off while a section is open", async () => {
    at(size);
    const fake = fakeEngine({ groups: [] });
    const region = await opened(fake.client);
    vi.mocked(fake.client.call).mockImplementationOnce(async () => ({
      available: false,
    }));
    await section(region, "AI changes");
    expect(
      await within(region).findByText("Memories are off on this install."),
    ).toBeInTheDocument();
  });
});

describe("the explorer's way in", () => {
  it("opens at a native project's star, in its project", async () => {
    at("desktop");
    const star = record(uuid(50), "memory", "project");
    star.provenance.demo = false;
    const fake = fakeEngine({
      projects: [project(50, "wendle", { name: "Wendle" })],
    });
    const region = await opened(fake.client, { selected: star.id }, [star]);
    expect(
      within(region).getByRole("heading", { name: "Projects" }),
    ).toBeInTheDocument();
    const row = await within(region).findByRole("button", { name: /Wendle/ });
    expect(row).toHaveAttribute("aria-expanded", "true");
    expect(fake.asked("get")).toEqual([]);
  });

  it("offers Open in Memories on a native star only while the engine serves the view", async () => {
    at("desktop");
    const star = record(uuid(1), "memory", "memory");
    star.provenance.demo = false;
    const other = record("other", "drive", "alex");
    other.provenance.demo = false;
    const fake = fakeEngine({
      memories: [memory(1, "DEMO: from the panel.")],
    });
    const { rerender } = render(
      <Explorer
        {...props}
        selected={star.id}
        knowledge={controller(
          { ...emptyGraph(), nodes: [star, other] },
          { memories: fake.client },
        )}
      />,
    );
    await userEvent.click(
      await screen.findByRole("button", { name: "Open in Memories" }),
    );
    const region = await screen.findByRole("region", { name: "Memories" });
    expect(
      await within(region).findByText("DEMO: from the panel."),
    ).toBeInTheDocument();

    // Any other star offers none.
    rerender(
      <Explorer
        {...props}
        selected={other.id}
        knowledge={controller(
          { ...emptyGraph(), nodes: [star, other] },
          { memories: fake.client },
        )}
      />,
    );
    await waitFor(() =>
      expect(
        screen.queryByRole("button", { name: "Open in Memories" }),
      ).not.toBeInTheDocument(),
    );
  });

  // SB-P6-02: the view closes back to the button it was opened from, not down to the dock.
  it.each(["phone", "desktop"] as const)(
    "at a %s's size, gives focus back to a star's Open in Memories when the view closes",
    async (size) => {
      at(size);
      const user = userEvent.setup();
      const star = record(uuid(1), "memory", "memory");
      star.provenance.demo = false;
      const fake = fakeEngine({
        memories: [memory(1, "DEMO: from the panel.")],
      });
      render(
        <Explorer
          {...props}
          selected={star.id}
          knowledge={controller(
            { ...emptyGraph(), nodes: [star] },
            { memories: fake.client },
          )}
        />,
      );
      const way = await screen.findByRole("button", {
        name: "Open in Memories",
      });
      way.focus();
      await user.keyboard("{Enter}");
      const region = await screen.findByRole("region", { name: "Memories" });
      await within(region).findByText("DEMO: from the panel.");
      // Back from the memory to its list, then out of the view.
      await user.keyboard("{Escape}");
      await within(region).findByRole("button", { name: "Close memories" });
      await user.keyboard("{Escape}");
      await waitFor(() =>
        expect(screen.queryByRole("region", { name: "Memories" })).toBeNull(),
      );
      expect(
        screen.getByRole("button", { name: "Open in Memories" }),
      ).toHaveFocus();

      // A tap on a browser that gives a button no focus comes back to it too.
      (document.activeElement as HTMLElement).blur();
      screen.getByRole("button", { name: "Open in Memories" }).click();
      const again = await screen.findByRole("region", { name: "Memories" });
      await within(again).findByText("DEMO: from the panel.");
      within(again).getByRole("button", { name: "Back to the list" }).click();
      (
        await within(again).findByRole("button", { name: "Close memories" })
      ).click();
      await waitFor(() =>
        expect(screen.queryByRole("region", { name: "Memories" })).toBeNull(),
      );
      expect(
        screen.getByRole("button", { name: "Open in Memories" }),
      ).toHaveFocus();
    },
  );

  it("offers nothing of it with the engine's memory.tools off", async () => {
    at("desktop");
    const star = record(uuid(1), "memory", "memory");
    star.provenance.demo = false;
    const off: MemoryClient = {
      available: vi.fn(async () => false),
      call: vi.fn(),
    };
    render(
      <Explorer
        {...props}
        selected={star.id}
        knowledge={controller(
          { ...emptyGraph(), nodes: [star] },
          { memories: off },
        )}
      />,
    );
    await waitFor(() => expect(off.available).toHaveBeenCalled());
    expect(
      screen.queryByRole("button", { name: "Open in Memories" }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "Memories" }),
    ).not.toBeInTheDocument();
    expect(off.call).not.toHaveBeenCalled();
  });
});
