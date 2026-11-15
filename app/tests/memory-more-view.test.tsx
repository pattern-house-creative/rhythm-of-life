// The memory UI's last pieces (SB-P6-01, run 22; MemoryList.tsx, MemoryView.tsx, MemoryStanding.tsx, MemoryInsightReview.tsx): words to
// look for and a kind in the Memories list, a memory's kind, importance and how long it holds changed from its reader, the standing
// instructions with a person's stop, several insights reviewed or pairs declined at once, the insight a pair became named when marking it
// used, and still true said of several. Each at a phone's size and a desktop's, in one continuous flow with no popup; every step goes
// through app_memories as the app, a person's channel. Fictional data only.
import { readFileSync } from "node:fs";
import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import Explorer from "../src/Explorer";
import { emptyGraph } from "../src/liveGraph";
import type { GraphSession } from "../src/liveGraph";
import type { KnowledgeController } from "../src/useKnowledge";
import type { MemoryClient, MemoryReply, StaleMemory } from "../src/memories";
import { fakeEngine, memory, standing, uuid } from "./memoryFake";
import { fakeQueues, insight, pair } from "./queueFake";

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

const controller = (client: MemoryClient): KnowledgeController => {
  const page = { ...emptyGraph(), nodes: [] };
  return {
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
      memories: client,
    } as GraphSession,
  };
};

async function opened(client: MemoryClient) {
  render(
    <Explorer
      selected={null}
      onSelect={vi.fn()}
      close={vi.fn()}
      paused
      state="ready"
      knowledge={controller(client)}
    />,
  );
  await userEvent.click(
    await screen.findByRole("button", { name: "Memories" }),
  );
  return screen.findByRole("region", { name: "Memories" });
}
const section = async (region: HTMLElement, name: string | RegExp) =>
  userEvent.click(
    within(within(region).getByRole("toolbar", { name: "Show" })).getByRole(
      "button",
      { name },
    ),
  );
const noPopup = () =>
  expect(
    document.querySelector(
      'dialog, [role="dialog"], [role="alertdialog"], [aria-modal]',
    ),
  ).toBeNull();
const ok = <T,>(value: T) =>
  ({ available: true, ok: true, ...value }) as unknown as MemoryReply<never>;

describe.each(["phone", "desktop"] as const)("at a %s's size", (size) => {
  it("finds memories by their words and filters by kind, the count the list's length, and Clear filters clears both", async () => {
    at(size);
    const fake = fakeEngine({
      memories: [
        memory(1, "DEMO: the Wendle ferry runs at dawn.", { kind: "episodic" }),
        memory(2, "DEMO: the WENDLE FERRY fares rose.", { kind: "semantic" }),
        memory(3, "DEMO: the Wendle mill turns slowly.", { kind: "semantic" }),
        memory(4, "DEMO: a held note about the wendle ferry.", {
          held: { reason: "instruction", signals: [] },
        }),
      ],
    });
    const region = await opened(fake.client);
    await within(region).findByText("DEMO: the Wendle mill turns slowly.");
    const find = within(region).getByRole("search", {
      name: "Find memories by their words",
    });
    // Nothing is asked on each key: only once a person says Find.
    await userEvent.type(
      within(find).getByRole("searchbox", { name: "Words in it" }),
      "  wendle ferry ",
    );
    expect(fake.asked("list").some((c) => "query" in c)).toBe(false);
    await userEvent.click(within(find).getByRole("button", { name: "Find" }));
    const list = within(region).getByRole("list", { name: "Recent memories" });
    await waitFor(() =>
      expect(within(list).getAllByRole("listitem")).toHaveLength(2),
    );
    expect(fake.asked("list").at(-1)).toMatchObject({
      op: "list",
      query: "wendle ferry",
    });
    expect(fake.asked("tags").at(-1)).toMatchObject({
      op: "tags",
      query: "wendle ferry",
    });
    // A held memory's words never select it.
    expect(within(list).queryByText(/Held for review/)).toBeNull();
    expect(await within(region).findByText("2 memories match.")).toBeVisible();

    // A kind narrows it further, and the count follows.
    const filters = within(region).getByRole("group", {
      name: "Filter these memories",
    });
    await userEvent.selectOptions(
      within(filters).getByRole("combobox", { name: "Kind" }),
      "A fact or an idea",
    );
    await waitFor(() =>
      expect(within(list).getAllByRole("listitem")).toHaveLength(1),
    );
    expect(fake.asked("list").at(-1)).toMatchObject({
      kind: "semantic",
      query: "wendle ferry",
    });
    expect(await within(region).findByText("1 memory match.")).toBeVisible();
    expect(within(list).getByText(/FERRY fares rose/)).toBeInTheDocument();

    // Words nobody wrote say so; Clear filters clears the words too.
    await userEvent.clear(
      within(find).getByRole("searchbox", { name: "Words in it" }),
    );
    await userEvent.type(
      within(find).getByRole("searchbox", { name: "Words in it" }),
      "nobody wrote this{Enter}",
    );
    expect(
      await within(region).findByText(
        "No memory you read holds “nobody wrote this” with these filters.",
      ),
    ).toBeVisible();
    await userEvent.click(
      within(filters).getByRole("button", { name: "Clear filters" }),
    );
    await waitFor(() =>
      expect(within(list).getAllByRole("listitem")).toHaveLength(4),
    );
    expect(
      within(find).getByRole("searchbox", { name: "Words in it" }),
    ).toHaveValue("");
    expect(fake.asked("list").at(-1)).not.toHaveProperty("query");
    expect(fake.asked("list").at(-1)).not.toHaveProperty("kind");

    // Words typed but never looked for go too: nothing left in the box is looked for at the next Find.
    await userEvent.type(
      within(find).getByRole("searchbox", { name: "Words in it" }),
      "never looked for",
    );
    await userEvent.selectOptions(
      within(filters).getByRole("combobox", { name: "Kind" }),
      "A fact or an idea",
    );
    await waitFor(() =>
      expect(within(list).getAllByRole("listitem")).toHaveLength(2),
    );
    expect(fake.asked("list").at(-1)).not.toHaveProperty("query");
    await userEvent.click(
      within(filters).getByRole("button", { name: "Clear filters" }),
    );
    expect(
      within(find).getByRole("searchbox", { name: "Words in it" }),
    ).toHaveValue("");
    await waitFor(() =>
      expect(within(list).getAllByRole("listitem")).toHaveLength(4),
    );
    const asked = fake.asked("list").length;
    await userEvent.click(within(find).getByRole("button", { name: "Find" }));
    expect(fake.asked("list")).toHaveLength(asked);
    expect(fake.asked("list").at(-1)).not.toHaveProperty("query");
    noPopup();
  });

  it("changes a memory's kind, importance and how long it holds from its reader, clears one, and Cancel changes nothing", async () => {
    at(size);
    const fake = fakeEngine({
      memories: [memory(1, "DEMO: the Wendle bakery opens at eight.")],
    });
    const region = await opened(fake.client);
    await userEvent.click(
      await within(region).findByText(
        "DEMO: the Wendle bakery opens at eight.",
      ),
    );
    const actions = await within(region).findByRole("group", {
      name: "This memory",
    });
    // Nothing is said of it yet, so there is no line for it.
    expect(within(region).queryByText(/importance \d of 5/i)).toBeNull();
    await userEvent.click(
      within(actions).getByRole("button", { name: "Kind and importance…" }),
    );
    const form = within(region).getByRole("form", { name: "What it is" });
    await waitFor(() =>
      expect(form.contains(document.activeElement)).toBe(true),
    );
    await userEvent.click(within(form).getByRole("button", { name: "Cancel" }));
    expect(fake.asked("update")).toHaveLength(0);

    await userEvent.click(
      within(
        within(region).getByRole("group", { name: "This memory" }),
      ).getByRole("button", { name: "Kind and importance…" }),
    );
    const again = within(region).getByRole("form", { name: "What it is" });
    await userEvent.selectOptions(
      within(again).getByRole("combobox", { name: "Kind" }),
      "A fact or an idea",
    );
    await userEvent.selectOptions(
      within(again).getByRole("combobox", {
        name: "Importance, 1 the least and 5 the most",
      }),
      "4",
    );
    await userEvent.selectOptions(
      within(again).getByRole("combobox", { name: "How long it holds" }),
      "A state, checked after 90 days",
    );
    await userEvent.click(within(again).getByRole("button", { name: "Save" }));
    await waitFor(() => expect(fake.asked("update")).toHaveLength(1));
    expect(fake.asked("update")[0]).toEqual({
      op: "update",
      id: uuid(1),
      kind: "semantic",
      importance: 4,
      volatility: "state",
      expected: 1,
    });
    expect(
      await within(region).findByText(
        "A fact or an idea · importance 4 of 5 · a state",
      ),
    ).toBeVisible();

    // One taken back to not said is cleared, the others left as they are.
    await userEvent.click(
      within(
        within(region).getByRole("group", { name: "This memory" }),
      ).getByRole("button", { name: "Kind and importance…" }),
    );
    const third = within(region).getByRole("form", { name: "What it is" });
    expect(
      within(third).getByRole("combobox", { name: "Kind" }),
    ).toHaveDisplayValue("A fact or an idea");
    await userEvent.selectOptions(
      within(third).getByRole("combobox", {
        name: "Importance, 1 the least and 5 the most",
      }),
      "Not said",
    );
    await userEvent.click(within(third).getByRole("button", { name: "Save" }));
    await waitFor(() => expect(fake.asked("update")).toHaveLength(2));
    expect(fake.asked("update")[1]).toEqual({
      op: "update",
      id: uuid(1),
      clear: ["importance"],
      expected: 2,
    });
    expect(
      await within(region).findByText("A fact or an idea · a state"),
    ).toBeVisible();

    // Refused, the form stays with what was chosen and the reason is said.
    fake.refuseNext("update", "forbidden", "DEMO: not yours to change.");
    await userEvent.click(
      within(
        within(region).getByRole("group", { name: "This memory" }),
      ).getByRole("button", { name: "Kind and importance…" }),
    );
    const fourth = within(region).getByRole("form", { name: "What it is" });
    await userEvent.selectOptions(
      within(fourth).getByRole("combobox", { name: "Kind" }),
      "Something that happened",
    );
    await userEvent.click(within(fourth).getByRole("button", { name: "Save" }));
    expect(
      await within(region).findByText("DEMO: not yours to change."),
    ).toBeVisible();
    expect(
      within(region).getByRole("form", { name: "What it is" }),
    ).toBeInTheDocument();
    expect(
      within(
        within(region).getByRole("form", { name: "What it is" }),
      ).getByRole("combobox", { name: "Kind" }),
    ).toHaveDisplayValue("Something that happened");
    noPopup();
  });

  it("lists the standing instructions with their counts, a held one without its words, and stops one behind a question, with Undo", async () => {
    at(size);
    const fake = fakeEngine({
      memories: [memory(1, "DEMO: the Wendle rota.")],
      instructions: [
        standing(
          201,
          "DEMO: when the Wendle rota comes up, list Sam's evenings first.",
        ),
        standing(202, null, {
          held: { reason: "instruction", signals: [] },
          fires: false,
          reason: "held",
        }),
        standing(
          203,
          "DEMO: when the Wendle ferry comes up, say it's fictional.",
          {
            fires: false,
            reason: "over_limit",
            status: "canonical",
          },
        ),
      ],
      served: false,
    });
    const region = await opened(fake.client);
    await section(region, "Standing");
    expect(
      within(region).getByRole("heading", {
        name: "Standing instructions",
        level: 2,
      }),
    ).toBeInTheDocument();
    const list = await within(region).findByRole("list", {
      name: "Standing instructions",
    });
    await waitFor(() =>
      expect(within(list).getAllByRole("listitem")).toHaveLength(3),
    );
    expect(
      within(region).getByText(/^3 standing instructions; 1 may fire\./),
    ).toBeVisible();
    expect(
      within(region).getByText(
        /None fires just now: recall or the local model is off/,
      ),
    ).toBeVisible();
    const [first, held, over] = within(list).getAllByRole("listitem");
    expect(within(held).getByText("Held for review")).toBeInTheDocument();
    expect(within(held).queryByText(/DEMO:/)).toBeNull();
    expect(within(held).queryByRole("button", { name: /^Stop/ })).toBeNull();
    expect(
      within(over).getByText(/doesn't fire: past the oldest 50 of its scope/),
    ).toBeInTheDocument();
    expect(within(first).getByText(/may fire/)).toBeInTheDocument();

    // A stop is asked about first, in place, and taken back first by Escape.
    await userEvent.click(
      within(first).getByRole("button", { name: "Stop it…" }),
    );
    const asked = within(first).getByRole("group", {
      name: "Stop this instruction?",
    });
    await waitFor(() => expect(asked).toHaveFocus());
    await userEvent.keyboard("{Escape}");
    expect(
      within(first).queryByRole("group", { name: "Stop this instruction?" }),
    ).toBeNull();
    expect(fake.asked("stop_standing")).toHaveLength(0);
    await userEvent.click(
      within(first).getByRole("button", { name: "Stop it…" }),
    );
    await userEvent.click(
      within(
        within(first).getByRole("group", { name: "Stop this instruction?" }),
      ).getByRole("button", { name: "Stop it" }),
    );
    expect(fake.asked("stop_standing")[0]).toEqual({
      op: "stop_standing",
      id: uuid(201),
      expected: 1,
    });
    expect(
      await within(region).findByText(
        /^Stopped: it fires no more and stays a memory/,
      ),
    ).toBeVisible();
    await waitFor(() =>
      expect(within(list).getAllByRole("listitem")).toHaveLength(2),
    );
    expect(within(region).getByText(/^2 standing instructions;/)).toBeVisible();
    // Undo starts it again, at the revision the stop left.
    await userEvent.click(within(region).getByRole("button", { name: "Undo" }));
    await waitFor(() => expect(fake.asked("undo")).toHaveLength(1));
    expect(fake.asked("undo")[0]).toEqual({
      op: "undo",
      id: uuid(201),
      expected: 2,
    });
    await waitFor(() =>
      expect(within(list).getAllByRole("listitem")).toHaveLength(3),
    );
    // Open reads it whole in the reader, as any memory.
    await userEvent.click(
      within(within(list).getAllByRole("listitem")[2]).getByRole("button", {
        name: "Open",
      }),
    );
    await waitFor(() =>
      expect(fake.asked("get").at(-1)).toMatchObject({ id: uuid(203) }),
    );
    noPopup();
  });

  it("reviews several insights at once behind a question, each answered on its own, and takes them back", async () => {
    at(size);
    const fake = fakeQueues({
      insights: [
        insight(1),
        insight(2),
        insight(3),
        insight(4, { mayReview: false }),
      ],
    });
    const region = await opened(fake.client);
    await section(region, /^Insights/);
    const list = await within(region).findByRole("list", { name: "Insights" });
    await waitFor(() =>
      expect(within(list).getAllByRole("listitem").length).toBeGreaterThan(0),
    );
    // Two to a page in the fake: the rest come with Show more.
    await userEvent.click(
      within(region).getByRole("button", { name: "Show more" }),
    );
    await waitFor(() =>
      expect(within(list).getAllByRole("listitem")).toHaveLength(4),
    );
    const rows = within(list).getAllByRole("listitem");
    // One this login may not review has no box to choose it.
    expect(within(rows[3]).queryByRole("checkbox")).toBeNull();
    await userEvent.click(
      within(rows[0]).getByRole("checkbox", { name: "Choose" }),
    );
    await userEvent.click(
      within(rows[1]).getByRole("checkbox", { name: "Choose" }),
    );
    const bar = within(region).getByRole("group", {
      name: "The insights you chose",
    });
    expect(within(bar).getByText("2 insights chosen")).toBeVisible();
    await userEvent.click(
      within(bar).getByRole("button", { name: "They hold…" }),
    );
    const asked = within(bar).getByRole("group", { name: "They hold…" });
    expect(asked).toHaveTextContent(
      "Say these 2 insights hold? They stay insights, no memory changes, and you can take them back.",
    );
    await userEvent.click(
      within(asked).getByRole("button", { name: "They hold" }),
    );
    await waitFor(() => expect(fake.asked("resolve_all")).toHaveLength(1));
    expect(fake.asked("resolve_all")[0]).toEqual({
      op: "resolve_all",
      action: "confirm_insight",
      items: [{ id: uuid(1) }, { id: uuid(2) }],
    });
    expect(
      await within(region).findByText(
        /^Confirmed 2 insights: a person said they hold\. They stay insights; no memory changes\./,
      ),
    ).toBeVisible();
    // They left what waits, and the strip's count is the list's length.
    expect(
      await within(region).findByRole("button", {
        name: "Insights, 2 waiting",
      }),
    ).toBeInTheDocument();
    expect(
      within(region).queryByRole("group", { name: "The insights you chose" }),
    ).toBeNull();
    // Taken back, each as its own reopen.
    await userEvent.click(
      within(region).getByRole("button", { name: "Take them back" }),
    );
    await waitFor(() => expect(fake.asked("review_insight")).toHaveLength(2));
    expect(fake.asked("review_insight").map((c) => c.id)).toEqual([
      uuid(1),
      uuid(2),
    ]);
    expect(
      await within(region).findByRole("button", {
        name: "Insights, 4 waiting",
      }),
    ).toBeInTheDocument();
    noPopup();
  });

  it("keeps a chosen insight's own words: it goes on its own with them, and the rest together", async () => {
    at(size);
    const fake = fakeQueues({
      insights: [insight(1), insight(2), insight(3)],
    });
    const region = await opened(fake.client);
    await section(region, /^Insights/);
    const list = await within(region).findByRole("list", { name: "Insights" });
    await waitFor(() =>
      expect(within(list).getAllByRole("listitem")).toHaveLength(2),
    );
    const rows = within(list).getAllByRole("listitem");
    const box = "What you would say, if anything (kept with it)";
    await userEvent.type(
      within(rows[0]).getByRole("textbox", { name: box }),
      "  DEMO: Alex checked the rota. ",
    );
    for (const row of rows)
      await userEvent.click(
        within(row).getByRole("checkbox", { name: "Choose" }),
      );
    const bar = within(region).getByRole("group", {
      name: "The insights you chose",
    });
    await userEvent.click(
      within(bar).getByRole("button", { name: "They do not hold…" }),
    );
    const asked = within(bar).getByRole("group", {
      name: "They do not hold…",
    });
    expect(asked).toHaveTextContent(
      /you can take them back\. What you wrote beside any is kept with it\./,
    );
    await userEvent.click(
      within(asked).getByRole("button", { name: "They do not hold" }),
    );
    await waitFor(() => expect(fake.asked("resolve")).toHaveLength(1));
    // The one with words goes with them, as its own button sends it; the other goes with the step for several.
    expect(fake.asked("resolve_all")).toEqual([
      {
        op: "resolve_all",
        action: "dismiss_insight",
        items: [{ id: uuid(2) }],
      },
    ]);
    expect(fake.asked("resolve")[0]).toEqual({
      op: "resolve",
      id: uuid(1),
      action: "dismiss_insight",
      note: "DEMO: Alex checked the rota.",
    });
    expect(
      await within(region).findByText(
        /^Dismissed 2 insights: they leave what waits on a person\./,
      ),
    ).toBeVisible();

    // Only rows with words of their own: no step for several is sent at all, and a box left empty asks nothing more.
    const left = await within(list).findAllByRole("listitem");
    expect(left).toHaveLength(1);
    expect(within(left[0]).getByRole("textbox", { name: box })).toHaveValue("");
    await userEvent.type(
      within(left[0]).getByRole("textbox", { name: box }),
      "DEMO: Sam says it holds.",
    );
    await userEvent.click(
      within(left[0]).getByRole("checkbox", { name: "Choose" }),
    );
    const again = within(region).getByRole("group", {
      name: "The insights you chose",
    });
    await userEvent.click(
      within(again).getByRole("button", { name: "They hold…" }),
    );
    await userEvent.click(
      within(
        within(again).getByRole("group", { name: "They hold…" }),
      ).getByRole("button", { name: "They hold" }),
    );
    await waitFor(() => expect(fake.asked("resolve")).toHaveLength(2));
    expect(fake.asked("resolve")[1]).toEqual({
      op: "resolve",
      id: uuid(3),
      action: "confirm_insight",
      note: "DEMO: Sam says it holds.",
    });
    expect(fake.asked("resolve_all")).toHaveLength(1);
    expect(
      await within(region).findByText(
        /^Confirmed 1 insight: a person said it holds\./,
      ),
    ).toBeVisible();
    noPopup();
  });

  it("keeps a chosen pair's own words when several are declined", async () => {
    at(size);
    const fake = fakeQueues({ pairs: [pair(30), pair(31)] });
    const region = await opened(fake.client);
    await section(region, /^Pairs/);
    const list = await within(region).findByRole("list", { name: "Pairs" });
    await waitFor(() =>
      expect(within(list).getAllByRole("listitem")).toHaveLength(2),
    );
    const rows = within(list).getAllByRole("listitem");
    await userEvent.type(
      within(rows[0]).getByRole("textbox", { name: "Why, if you like" }),
      "DEMO: nothing new in either.",
    );
    for (const row of rows)
      await userEvent.click(
        within(row).getByRole("checkbox", { name: "Choose" }),
      );
    const bar = within(region).getByRole("group", {
      name: "The pairs you chose",
    });
    await userEvent.click(
      within(bar).getByRole("button", { name: "Nothing in them…" }),
    );
    const asked = within(bar).getByRole("group", { name: "Nothing in them…" });
    expect(asked).toHaveTextContent(
      /can't be undone\. Neither memory of any changes\. What you wrote beside any is kept with it\./,
    );
    await userEvent.click(
      within(asked).getByRole("button", { name: "Decline them" }),
    );
    await waitFor(() => expect(fake.asked("resolve")).toHaveLength(1));
    expect(fake.asked("resolve_all")).toEqual([
      {
        op: "resolve_all",
        action: "reject_candidate",
        items: [{ id: uuid(31) }],
      },
    ]);
    expect(fake.asked("resolve")[0]).toEqual({
      op: "resolve",
      id: uuid(30),
      action: "reject_candidate",
      note: "DEMO: nothing new in either.",
    });
    expect(
      await within(region).findByText(
        /^Declined 2 pairs: they won't be offered again\./,
      ),
    ).toBeVisible();
    // Among the declined, its words are kept with it.
    await userEvent.click(
      within(
        within(region).getByRole("group", { name: "Which pairs" }),
      ).getByRole("button", { name: "Declined" }),
    );
    expect(
      await within(region).findByText(/· DEMO: nothing new in either\.$/),
    ).toBeInTheDocument();
    noPopup();
  });

  it("declines several pairs at once behind a question, for good", async () => {
    at(size);
    const fake = fakeQueues({ pairs: [pair(30), pair(31), pair(32)] });
    const region = await opened(fake.client);
    await section(region, /^Pairs/);
    const list = await within(region).findByRole("list", { name: "Pairs" });
    await waitFor(() =>
      expect(within(list).getAllByRole("listitem")).toHaveLength(2),
    );
    const rows = within(list).getAllByRole("listitem");
    for (const row of rows)
      await userEvent.click(
        within(row).getByRole("checkbox", { name: "Choose" }),
      );
    const bar = within(region).getByRole("group", {
      name: "The pairs you chose",
    });
    await userEvent.click(
      within(bar).getByRole("button", { name: "Nothing in them…" }),
    );
    const asked = within(bar).getByRole("group", { name: "Nothing in them…" });
    expect(asked).toHaveTextContent(/can't be undone/);
    await userEvent.click(
      within(asked).getByRole("button", { name: "Decline them" }),
    );
    await waitFor(() => expect(fake.asked("resolve_all")).toHaveLength(1));
    expect(fake.asked("resolve_all")[0]).toEqual({
      op: "resolve_all",
      action: "reject_candidate",
      items: [{ id: uuid(30) }, { id: uuid(31) }],
    });
    expect(
      await within(region).findByText(
        /^Declined 2 pairs: they won't be offered again\./,
      ),
    ).toBeVisible();
    expect(
      await within(region).findByText(/^1 pair waits\./),
    ).toBeInTheDocument();
    noPopup();
  });

  it("names the insight a pair became when marking it used, offering only those that cite both its memories", async () => {
    at(size);
    const p = pair(40);
    const cites = (n: number, ids: string[]) =>
      insight(
        n,
        {},
        {
          title: `DEMO: insight ${n} from the pair`,
          sources: ids.map((id) => ({
            kind: "memory" as const,
            title: "DEMO: a memory",
            locator: id,
            retrievedAt: "2026-11-02T09:30:00+00:00",
          })),
        },
      );
    const fake = fakeQueues({
      pairs: [p],
      insights: [
        cites(41, [p.older.id, p.newer.id]),
        cites(42, [p.older.id]),
        cites(43, [p.newer.id.toUpperCase(), p.older.id]),
      ],
    });
    const region = await opened(fake.client);
    await section(region, /^Pairs/);
    const list = await within(region).findByRole("list", { name: "Pairs" });
    const row = (
      await within(list).findByText("DEMO: older memory 40.")
    ).closest("li") as HTMLElement;
    await userEvent.click(
      within(row).getByRole("button", { name: /^Mark used/ }),
    );
    const asked = within(row).getByRole("group", { name: "Decide this pair?" });
    const pick = await within(asked).findByRole("combobox", {
      name: "The insight it became, if one did",
    });
    expect(
      within(pick)
        .getAllByRole("option")
        .map((o) => o.textContent),
    ).toEqual([
      "None named",
      "DEMO: insight 41 from the pair",
      "DEMO: insight 43 from the pair",
    ]);
    expect(fake.asked("insight_stream").at(-1)).toMatchObject({ view: "all" });
    await userEvent.selectOptions(pick, "DEMO: insight 43 from the pair");
    await userEvent.click(
      within(asked).getByRole("button", { name: "Mark it used" }),
    );
    await waitFor(() => expect(fake.asked("resolve")).toHaveLength(1));
    expect(fake.asked("resolve")[0]).toEqual({
      op: "resolve",
      id: uuid(40),
      action: "use_candidate",
      insight: uuid(43),
    });
    noPopup();
  });

  it("says still true of several at once, each at the revision shown, one changed since staying", async () => {
    at(size);
    const base = fakeEngine({
      memories: [memory(1, "DEMO: the Wendle rota.")],
    });
    const row = (n: number, revision = 1): StaleMemory => ({
      id: uuid(n),
      scope: "shared",
      owner: "household",
      author: null,
      status: "draft",
      volatility: "state",
      reason: "not_confirmed",
      since: "2026-07-01T09:30:00+00:00",
      foundAt: "2026-11-02T09:30:00+00:00",
      confirmedAt: null,
      preview: `DEMO: stale ${n} about the Wendle rota.`,
      truncated: false,
      retractedSources: [],
      revision,
    });
    let stale = [row(501), row(502, 3), row(503)];
    const asked: Record<string, unknown>[] = [];
    const client: MemoryClient = {
      available: vi.fn(async () => true),
      call: vi.fn(async (request: Record<string, unknown>) => {
        asked.push(request);
        if (request.op === "stale")
          return ok({ stale, next: null, count: stale.length });
        if (request.op === "queues")
          return ok({
            held: 0,
            duplicates: 0,
            contradictions: 0,
            stale: stale.length,
            candidates: 0,
            insights: 0,
          });
        if (request.op === "resolve_all") {
          const items = request.items as { id: string; expected: number }[];
          const results = items.map((i) => ({
            id: i.id,
            result: i.id === uuid(502) ? "conflict" : "resolved",
            revision: i.id === uuid(502) ? 4 : i.expected,
            review: null,
            view: null,
            candidate: null,
          }));
          stale = stale.filter(
            (s) =>
              !results.some((r) => r.id === s.id && r.result === "resolved"),
          );
          return ok({ op: "resolve_all", action: request.action, results });
        }
        return base.client.call(request);
      }) as unknown as MemoryClient["call"],
    };
    const region = await opened(client);
    await section(region, /^To check/);
    const list = await within(region).findByRole("list", {
      name: "May be out of date",
    });
    await waitFor(() =>
      expect(within(list).getAllByRole("listitem")).toHaveLength(3),
    );
    await userEvent.click(
      within(list).getAllByRole("checkbox", { name: "Choose" })[0],
    );
    const bar = within(region).getByRole("group", {
      name: "The memories you chose",
    });
    await userEvent.click(
      within(bar).getByRole("button", { name: "Choose all 3 shown" }),
    );
    await userEvent.click(
      within(bar).getByRole("button", { name: "Still true" }),
    );
    await waitFor(() =>
      expect(asked.filter((c) => c.op === "resolve_all")).toHaveLength(1),
    );
    expect(asked.find((c) => c.op === "resolve_all")).toEqual({
      op: "resolve_all",
      action: "still_true",
      items: [
        { id: uuid(501), expected: 1 },
        { id: uuid(502), expected: 3 },
        { id: uuid(503), expected: 1 },
      ],
    });
    expect(
      await within(region).findByText(
        /^Marked 2 memories still true: nothing in them changed\. 1 changed since you read it and stays as it is: read it again\./,
      ),
    ).toBeVisible();
    await waitFor(() =>
      expect(within(list).getAllByRole("listitem")).toHaveLength(1),
    );
    expect(within(list).getByText(/DEMO: stale 502/)).toBeInTheDocument();
    noPopup();
  });
});

describe("a finger's room and no zoom for the new controls, upright or sideways", () => {
  it("gives the Find button and a pick in a question 40px, and 16px in the pick", () => {
    const style = document.createElement("style");
    style.textContent = readFileSync("src/MemoryView.css", "utf8");
    document.head.append(style);
    const touch = [...style.sheet!.cssRules].filter(
      (r): r is CSSMediaRule =>
        r instanceof CSSMediaRule &&
        r.media.mediaText === "(max-width: 760px), (max-height: 500px)",
    );
    const said = (selector: string) =>
      touch
        .flatMap((m) => [...m.cssRules] as CSSStyleRule[])
        .filter((r) =>
          r.selectorText
            .split(",")
            .map((s) => s.trim())
            .includes(selector),
        )
        .map((r) => r.style);
    for (const selector of [".memory-find button", ".memory-note select"])
      expect(
        said(selector).some((s) => parseInt(s.minHeight) >= 40),
        selector,
      ).toBe(true);
    expect(said(".memory-note select").map((s) => s.fontSize)).toContain(
      "16px",
    );
    style.remove();
  });
});
