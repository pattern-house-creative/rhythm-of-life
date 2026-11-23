// The brief's lists in the Memories view (SB-P6-01; MemoryPlans.tsx, one entry in Explorer.tsx): each opens at the length its own count
// says, in one flow with no popup, at a phone's size and a desktop's; a person's word on each row goes through app_memories, the app's
// channel, at the revision shown; Home draws no brief (the owner, 2026-11-09); with the setting off none of it is drawn. Fictional data
// only.
import {
  act,
  cleanup,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import App from "../src/App";
import Explorer from "../src/Explorer";
import { emptyGraph } from "../src/liveGraph";
import type { GraphSession } from "../src/liveGraph";
import type { MemoryClient } from "../src/memories";
import type { KnowledgeController } from "../src/useKnowledge";
import { installDay } from "../src/memoryScreen";
import type { BriefTarget } from "../src/memoryScreen";
import type * as Api from "../src/apiClient";
import {
  decisionRow,
  dueRow,
  fixture,
  loopRow,
  plansEngine,
  uuid,
} from "./plansFake";

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
  localStorage.clear();
  session.memories = undefined;
});

const controller = (extra: Partial<GraphSession> = {}): KnowledgeController => {
  const page = emptyGraph();
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
      ...extra,
    },
  };
};

// The Memories view opened at one of the brief's lists, as the explorer's request asks it.
async function fromCard(client: MemoryClient, memories: BriefTarget) {
  const close = vi.fn();
  render(
    <Explorer
      selected={null}
      onSelect={vi.fn()}
      close={close}
      paused
      state="ready"
      knowledge={controller({ memories: client })}
      request={{ nonce: 1, memories }}
    />,
  );
  const region = await screen.findByRole("region", { name: "Memories" });
  return { region, close };
}

const rows = (list: HTMLElement) => within(list).queryAllByRole("listitem");
// The zone the fake engine's pages name, unless a test gives it another.
const london = "Europe/London";

// An op's answer kept back until let go, as a slow engine's is, and how many of it the view has sent so far.
function hold(engine: ReturnType<typeof plansEngine>, op: string) {
  const call = vi.mocked(engine.client.call);
  const answer = call.getMockImplementation()!;
  let go = () => {};
  const gate = new Promise<void>((open) => {
    go = open;
  });
  call.mockImplementation((async (request: Record<string, unknown>) => {
    if (request.op === op) await gate;
    return answer(request);
  }) as unknown as MemoryClient["call"]);
  return {
    go: () => go(),
    sent: () =>
      call.mock.calls.filter(
        ([request]) => (request as Record<string, unknown>).op === op,
      ).length,
  };
}

// One install's rows, a card's and its lists' alike.
const install = () => ({
  loops: [
    loopRow(1, "DEMO: Alex will bake tarts for the Pellow sale.", {
      counterparty: "Pellow Choir",
    }),
    loopRow(2, "DEMO: Alex posts the Pellow letter."),
    loopRow(3, "DEMO: Alex mends the Pellow gate.", { project: null }),
    loopRow(4, "DEMO: Sam promised the Pellow ladder.", {
      direction: "in",
      counterparty: "Sam",
    }),
  ],
  due: [
    dueRow(10, "DEMO: return the Pellow library books.", {
      window: "overdue",
      at: "2026-11-03T00:00:00+00:00",
      local: "2026-11-03 00:00",
      loop: "out",
    }),
    dueRow(11, "DEMO: ask about the Pellow shed key.", {
      window: "upcoming",
      at: "2026-11-05T09:30:00+00:00",
      local: "2026-11-05 09:30",
      kind: "wake",
      source: "explicit",
    }),
    dueRow(12, "DEMO: book the Pellow hall.", {
      window: "later",
      at: "2026-11-20T09:00:00+00:00",
      local: "2026-11-20 09:00",
      kind: "event",
    }),
  ],
  decisions: [
    decisionRow(20, "DEMO: Alex chose the Pellow allotment.", { edited: true }),
    decisionRow(21, "DEMO: Alex chose the Pellow van.", {
      window: "upcoming",
      reviewAt: "2026-11-05T09:00:00+00:00",
      confidence: null,
      confidenceSource: null,
    }),
    decisionRow(22, "DEMO: Alex chose the Pellow canopy.", {
      window: "later",
      reviewAt: "2027-01-05T09:00:00+00:00",
      confidenceSource: "inferred",
      confidence: 0.6,
    }),
    decisionRow(23, "DEMO: Alex chose the Pellow bus.", {
      outcome: "wrong",
      window: null,
      reviewAt: null,
    }),
  ],
  stale: 3,
  held: 0,
  changeGroups: 1,
});

describe.each(["phone", "desktop"] as const)("at a %s's size", (size) => {
  it("opens each list at the length its own count says, with no popup", async () => {
    at(size);
    // Each opened: the number the list says, and how many rows it has, are its count's.
    const lists: [BriefTarget, string, number][] = [
      ["youOwe", "You owe", 3],
      ["owedToYou", "Owed to you", 1],
    ];
    for (const [target, name, count] of lists) {
      const { region } = await fromCard(plansEngine(install()).client, target);
      const list = await within(region).findByRole("list", { name });
      await waitFor(() => expect(rows(list)).toHaveLength(count));
      expect(region).toHaveTextContent(
        count === 1
          ? `1 thing ${target === "youOwe" ? "you owe" : "owed to you"}`
          : `${count} things ${target === "youOwe" ? "you owe" : "owed to you"}`,
      );
      expect(screen.queryByRole("dialog")).toBeNull();
      cleanup();
    }

    const due = await fromCard(plansEngine(install()).client, "due");
    const overdue = await within(due.region).findByRole("list", {
      name: "Overdue",
    });
    const soon = within(due.region).getByRole("list", {
      name: "Within 48 hours",
    });
    expect(rows(overdue).length + rows(soon).length).toBe(2);
    expect(due.region).toHaveTextContent("2 due: 1 overdue, 1 within 48 hours");
    // The later date is no part of the count, nor of this list.
    expect(due.region).not.toHaveTextContent("book the Pellow hall");
    cleanup();

    const decisions = await fromCard(
      plansEngine(install()).client,
      "decisions",
    );
    const come = await within(decisions.region).findByRole("list", {
      name: "Its review date has come",
    });
    const near = await within(decisions.region).findByRole("list", {
      name: "Within 48 hours",
    });
    expect(rows(come).length + rows(near).length).toBe(2);
    expect(decisions.region).toHaveTextContent("2 decisions to look at again");
    cleanup();

    const stale = await fromCard(plansEngine(install()).client, "stale");
    const staleList = await within(stale.region).findByRole("list", {
      name: "May be out of date",
    });
    await waitFor(() => expect(rows(staleList)).toHaveLength(3));
    expect(stale.region).toHaveTextContent("3 memories may be out of date");
    cleanup();

    const held = await fromCard(
      plansEngine({ ...install(), held: 2 }).client,
      "held",
    );
    const heldList = await within(held.region).findByRole("list", {
      name: "Held for review",
    });
    await waitFor(() => expect(rows(heldList)).toHaveLength(2));
    expect(held.region).toHaveTextContent("2 memories are held for review");
  });

  it("takes a person's word on a task through the app's channel, at the revision shown, with Undo", async () => {
    at(size);
    const user = userEvent.setup();
    const engine = plansEngine(install());
    const { region } = await fromCard(engine.client, "youOwe");
    const list = await within(region).findByRole("list", { name: "You owe" });
    await waitFor(() => expect(rows(list)).toHaveLength(3));
    const row = rows(list)[0];
    expect(row).toHaveTextContent("You owe Pellow Choir");
    await user.click(within(row).getByRole("button", { name: "Done" }));
    await waitFor(() => expect(rows(list)).toHaveLength(2));
    expect(engine.of("resolve_loop")).toEqual([
      { op: "resolve_loop", id: uuid(1), action: "done", expected: 1 },
    ]);
    expect(region).toHaveTextContent("2 things you owe");
    expect(region).toHaveTextContent("Marked done");
    expect(JSON.stringify(engine.calls).includes("channel")).toBe(false);
    // Undo: back on the list, through the undo a person has anywhere.
    await user.click(within(region).getByRole("button", { name: "Undo" }));
    await waitFor(() => expect(rows(list)).toHaveLength(3));
    expect(engine.of("undo")).toEqual([
      { op: "undo", id: uuid(1), expected: 2 },
    ]);

    // Owed to you is received, and a tap on the other direction reads its own list with the same counts.
    await user.click(
      within(region).getByRole("button", { name: "Owed to you 1" }),
    );
    const owed = await within(region).findByRole("list", {
      name: "Owed to you",
    });
    await waitFor(() => expect(rows(owed)).toHaveLength(1));
    expect(rows(owed)[0]).toHaveTextContent("Sam owes you");
    await user.click(
      within(rows(owed)[0]).getByRole("button", { name: "Received" }),
    );
    await waitFor(() => expect(rows(owed)).toHaveLength(0));
    expect(region).toHaveTextContent("Nothing is owed to you here.");
    expect(engine.of("resolve_loop").at(-1)).toMatchObject({
      action: "received",
      id: uuid(4),
    });

    // Not a task keeps the memory.
    await user.click(within(region).getByRole("button", { name: "You owe 3" }));
    const back = await within(region).findByRole("list", { name: "You owe" });
    await waitFor(() => expect(rows(back)).toHaveLength(3));
    await user.click(
      within(rows(back)[1]).getByRole("button", { name: "Not a task" }),
    );
    await waitFor(() => expect(rows(back)).toHaveLength(2));
    expect(region).toHaveTextContent("the memory stays");
  });

  it("says why a step was refused, and reads the list again", async () => {
    at(size);
    const user = userEvent.setup();
    const engine = plansEngine(install());
    const { region } = await fromCard(engine.client, "youOwe");
    const list = await within(region).findByRole("list", { name: "You owe" });
    await waitFor(() => expect(rows(list)).toHaveLength(3));
    engine.refuse(
      "resolve_loop",
      "conflict",
      "DEMO: it changed since you read it.",
    );
    const reads = engine.of("loops").length;
    await user.click(
      within(rows(list)[0]).getByRole("button", { name: "Done" }),
    );
    expect(
      await within(region).findByText("DEMO: it changed since you read it."),
    ).toBeInTheDocument();
    await waitFor(() =>
      expect(engine.of("loops").length).toBeGreaterThan(reads),
    );
    expect(rows(list)).toHaveLength(3);
  });

  it("snoozes and clears a date in its row, and Escape takes the question back", async () => {
    at(size);
    const user = userEvent.setup();
    const engine = plansEngine(install());
    const { region } = await fromCard(engine.client, "due");
    const overdue = await within(region).findByRole("list", {
      name: "Overdue",
    });
    expect(rows(overdue)[0]).toHaveTextContent("Overdue");
    expect(rows(overdue)[0]).toHaveTextContent("2026-11-03 00:00");
    expect(rows(overdue)[0]).toHaveTextContent("You owe it");
    await user.click(
      within(rows(overdue)[0]).getByRole("button", { name: "Snooze…" }),
    );
    // In the row: no popup.
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(
      within(rows(overdue)[0]).getByRole("group", { name: "Snooze until" }),
    ).toBeInTheDocument();
    await user.keyboard("{Escape}");
    await waitFor(() =>
      expect(
        within(rows(overdue)[0]).queryByRole("group", { name: "Snooze until" }),
      ).toBeNull(),
    );
    // The view is still there: Escape took only the question.
    expect(
      screen.getByRole("region", { name: "Memories" }),
    ).toBeInTheDocument();

    await user.click(
      within(rows(overdue)[0]).getByRole("button", { name: "Snooze…" }),
    );
    await user.click(
      within(rows(overdue)[0]).getByRole("button", { name: "In a week" }),
    );
    await waitFor(() =>
      expect(region).toHaveTextContent("1 due: 0 overdue, 1 within 48 hours"),
    );
    const until = `${installDay(7, london)}T09:00`;
    expect(engine.of("resolve_due")).toEqual([
      { op: "resolve_due", id: uuid(10), action: "snooze", until, expected: 1 },
    ]);
    expect(region).toHaveTextContent(
      `Put off until ${until.replace("T", " ")}`,
    );
    expect(region).toHaveTextContent("Later 2");

    // Undo puts the date back, and the count with it.
    await user.click(within(region).getByRole("button", { name: "Undo" }));
    await waitFor(() =>
      expect(region).toHaveTextContent("2 due: 1 overdue, 1 within 48 hours"),
    );

    // Clear: its date comes off and the list shortens.
    const list = within(region).getByRole("list", { name: "Within 48 hours" });
    await user.click(
      within(rows(list)[0]).getByRole("button", { name: "Clear date" }),
    );
    await waitFor(() =>
      expect(region).toHaveTextContent("1 due: 1 overdue, 0 within 48 hours"),
    );
    expect(engine.of("resolve_due").at(-1)).toEqual({
      op: "resolve_due",
      id: uuid(11),
      action: "clear_date",
      expected: 1,
    });
    expect(region).toHaveTextContent("Its date came off");

    // Later is its own list, and a date a person picks is sent as they picked it, at 09:00.
    await user.click(within(region).getByRole("button", { name: "Later 1" }));
    const later = await within(region).findByRole("list", { name: "Later" });
    await waitFor(() => expect(rows(later)).toHaveLength(1));
    await user.click(
      within(rows(later)[0]).getByRole("button", { name: "Snooze…" }),
    );
    const picker = within(rows(later)[0]).getByLabelText("On");
    await user.clear(picker);
    await user.type(picker, installDay(30, london));
    await user.click(
      within(rows(later)[0]).getByRole("button", { name: "Put off" }),
    );
    await waitFor(() =>
      expect(engine.of("resolve_due").at(-1)).toMatchObject({
        id: uuid(12),
        action: "snooze",
        until: `${installDay(30, london)}T09:00`,
      }),
    );
  });

  it("says where a snoozed date went: within 48 hours one already there stays, and a later one comes into Due now", async () => {
    at(size);
    const user = userEvent.setup();
    const engine = plansEngine(install());
    const { region } = await fromCard(engine.client, "due");
    const overdue = await within(region).findByRole("list", {
      name: "Overdue",
    });
    await user.click(
      within(rows(overdue)[0]).getByRole("button", { name: "Snooze…" }),
    );
    await user.click(
      within(rows(overdue)[0]).getByRole("button", { name: "Tomorrow" }),
    );
    await waitFor(() =>
      expect(region).toHaveTextContent(
        "That is within 48 hours, so it stays here.",
      ),
    );
    expect(region).not.toHaveTextContent("the brief counts it");
    expect(
      within(region).getByRole("button", { name: "Due now 2" }),
    ).toBeInTheDocument();

    // From Later it leaves the list it was on, and it is the one snooze that raises the brief's count.
    await user.click(within(region).getByRole("button", { name: "Later 1" }));
    const later = await within(region).findByRole("list", { name: "Later" });
    await waitFor(() => expect(rows(later)).toHaveLength(1));
    await user.click(
      within(rows(later)[0]).getByRole("button", { name: "Snooze…" }),
    );
    await user.click(
      within(rows(later)[0]).getByRole("button", { name: "Tomorrow" }),
    );
    await waitFor(() =>
      expect(region).toHaveTextContent(
        "That is within 48 hours, so it moves to Due now and the brief counts it.",
      ),
    );
    expect(region).not.toHaveTextContent("stays here");
    expect(
      within(region).getByRole("button", { name: "Due now 3" }),
    ).toBeInTheDocument();
    expect(
      within(region).getByRole("button", { name: "Later 0" }),
    ).toBeInTheDocument();
    expect(engine.of("resolve_due").at(-1)).toMatchObject({
      id: uuid(12),
      until: `${installDay(1, london)}T09:00`,
    });
  });

  it("takes a row's open question away with its list, so the next Escape is the view's and it doesn't come back", async () => {
    at(size);
    const user = userEvent.setup();
    const { region, close } = await fromCard(
      plansEngine(install()).client,
      "due",
    );
    const overdue = await within(region).findByRole("list", {
      name: "Overdue",
    });
    await user.click(
      within(rows(overdue)[0]).getByRole("button", { name: "Snooze…" }),
    );
    await user.click(within(region).getByRole("button", { name: "Later 1" }));
    await within(region).findByRole("list", { name: "Later" });
    await user.click(within(region).getByRole("button", { name: "Due now 2" }));
    const back = await within(region).findByRole("list", { name: "Overdue" });
    expect(
      within(region).queryByRole("group", { name: "Snooze until" }),
    ).toBeNull();

    await user.click(
      within(rows(back)[0]).getByRole("button", { name: "Snooze…" }),
    );
    await user.click(within(region).getByRole("button", { name: "Later 1" }));
    await within(region).findByRole("list", { name: "Later" });
    await user.keyboard("{Escape}");
    await waitFor(() => expect(close).toHaveBeenCalledTimes(1));
  });

  it("sends one undo for two taps on a notice's Undo, and says it was undone", async () => {
    at(size);
    const user = userEvent.setup();
    const engine = plansEngine(install());
    const { region } = await fromCard(engine.client, "youOwe");
    const list = await within(region).findByRole("list", { name: "You owe" });
    await waitFor(() => expect(rows(list)).toHaveLength(3));
    await user.click(
      within(rows(list)[0]).getByRole("button", { name: "Done" }),
    );
    await waitFor(() => expect(rows(list)).toHaveLength(2));

    const slow = hold(engine, "undo");
    const undo = within(region).getByRole("button", { name: "Undo" });
    await user.click(undo);
    await user.click(undo);
    expect(slow.sent()).toBe(1);
    // While it runs, no row's step goes out beside it.
    expect(
      within(rows(list)[0]).getByRole("button", { name: "Done" }),
    ).toBeDisabled();
    slow.go();
    await waitFor(() => expect(rows(list)).toHaveLength(3));
    expect(region).toHaveTextContent("Undone: it is back as it was.");
    expect(region).not.toHaveTextContent("it changed since");
    expect(engine.of("undo")).toHaveLength(1);
  });

  it("takes no snooze choice while another row's step runs, and keeps the question for after it", async () => {
    at(size);
    const user = userEvent.setup();
    const engine = plansEngine(install());
    const { region } = await fromCard(engine.client, "due");
    const overdue = await within(region).findByRole("list", {
      name: "Overdue",
    });
    const soon = within(region).getByRole("list", { name: "Within 48 hours" });
    await user.click(
      within(rows(overdue)[0]).getByRole("button", { name: "Snooze…" }),
    );
    const slow = hold(engine, "resolve_due");
    await user.click(
      within(rows(soon)[0]).getByRole("button", { name: "Clear date" }),
    );
    const question = within(rows(overdue)[0]).getByRole("group", {
      name: "Snooze until",
    });
    for (const name of ["Tomorrow", "In a week", "Put off"])
      expect(within(question).getByRole("button", { name })).toBeDisabled();
    expect(
      within(question).getByRole("button", { name: "Keep it" }),
    ).toBeEnabled();
    slow.go();
    await waitFor(() =>
      expect(
        within(question).getByRole("button", { name: "Tomorrow" }),
      ).toBeEnabled(),
    );
    await user.click(
      within(question).getByRole("button", { name: "In a week" }),
    );
    await waitFor(() =>
      expect(engine.of("resolve_due").at(-1)).toMatchObject({
        id: uuid(10),
        action: "snooze",
      }),
    );
  });

  it("records how a decision turned out, keeps the outcome beside it, and lets a person change it", async () => {
    at(size);
    const user = userEvent.setup();
    const engine = plansEngine(install());
    const { region } = await fromCard(engine.client, "decisions");
    const come = await within(region).findByRole("list", {
      name: "Its review date has come",
    });
    const row = rows(come)[0];
    expect(row).toHaveTextContent("You were 70% sure");
    expect(row).toHaveTextContent("Its words changed since");
    await user.click(within(row).getByRole("button", { name: "Right" }));
    await waitFor(() =>
      expect(
        within(region).queryByRole("list", {
          name: "Its review date has come",
        }),
      ).toBeNull(),
    );
    expect(engine.of("record_outcome")).toEqual([
      { op: "record_outcome", id: uuid(20), result: "right", expected: 1 },
    ]);
    expect(region).toHaveTextContent("It turned out right.");
    expect(region).toHaveTextContent("1 decision to look at again");

    // Undo, then to the list with outcomes, where each says how it turned out.
    await user.click(within(region).getByRole("button", { name: "Undo" }));
    await waitFor(() =>
      expect(region).toHaveTextContent("2 decisions to look at again"),
    );
    await user.click(
      within(region).getByRole("button", { name: "With an outcome 1" }),
    );
    const resolved = await within(region).findByRole("list", {
      name: "With an outcome",
    });
    expect(rows(resolved)).toHaveLength(1);
    const wrong = rows(resolved)[0];
    expect(wrong).toHaveTextContent("It turned out wrong");
    // Saying it again changes nothing, so it isn't offered; another word is a person's correction.
    expect(within(wrong).getByRole("button", { name: "Wrong" })).toBeDisabled();
    await user.click(within(wrong).getByRole("button", { name: "Mixed" }));
    await waitFor(() =>
      expect(within(resolved).getByRole("listitem")).toHaveTextContent(
        "It turned out mixed",
      ),
    );
    expect(engine.of("record_outcome").at(-1)).toEqual({
      op: "record_outcome",
      id: uuid(23),
      result: "mixed",
      expected: 1,
    });

    // No outcome yet holds what is not due too; too early to tell says when it asks again.
    await user.click(
      within(region).getByRole("button", { name: "No outcome yet 3" }),
    );
    const open = await within(region).findByRole("list", {
      name: "No outcome yet",
    });
    await waitFor(() => expect(rows(open)).toHaveLength(3));
    const canopy = rows(open).find((r) =>
      r.textContent!.includes("the Pellow canopy"),
    )!;
    expect(canopy).toHaveTextContent(
      "You were 60% sure (read from your words)",
    );
    await user.click(within(canopy).getByRole("button", { name: "Too early" }));
    await waitFor(() => expect(region).toHaveTextContent("it asks again on"));

    // A decision with no confidence is said to leave calibration out.
    const van = rows(open).find((r) =>
      r.textContent!.includes("the Pellow van"),
    )!;
    expect(van).toHaveTextContent("No confidence given");
    await user.click(within(van).getByRole("button", { name: "Right" }));
    await waitFor(() =>
      expect(region).toHaveTextContent("calibration leaves it out"),
    );
  });

  it("shows calibration with what each figure rests on, no figure below its minimum, and the list it rests on", async () => {
    at(size);
    const user = userEvent.setup();
    const below = plansEngine({
      ...install(),
      calibration: fixture("app_memories.calibration.not_ready.example.json"),
    });
    const first = await fromCard(below.client, "decisions");
    const few = await within(first.region).findByRole("region", {
      name: "Calibration",
    });
    expect(few).toHaveTextContent("there are 3 so far");
    expect(few).toHaveTextContent("70-79% sure: 2 decisions, too few to say");
    expect(few).not.toHaveTextContent("of the time");
    cleanup();

    const ready = plansEngine({
      ...install(),
      calibration: fixture("app_memories.calibration.example.json"),
    });
    const second = await fromCard(ready.client, "decisions");
    const part = await within(second.region).findByRole("region", {
      name: "Calibration",
    });
    expect(part).toHaveTextContent("about 74% sure you were right 63%");
    expect(part).toHaveTextContent("On pellow you were right less often");
    expect(part).toHaveTextContent(
      "70-79% sure: 8 decisions, right 63% of the time",
    );
    expect(part).toHaveTextContent("60-69% sure: 2 decisions, too few to say");
    await user.click(
      within(part).getByRole("button", { name: /Show the 12 decisions/ }),
    );
    await within(second.region).findByRole("list", {
      name: "What calibration rests on",
    });
    expect(ready.of("decisions").at(-1)).toMatchObject({ state: "scored" });
  });

  it("opens a row whole and comes back to the list it was on, with the filter kept", async () => {
    at(size);
    const user = userEvent.setup();
    const engine = plansEngine(install());
    const { region } = await fromCard(engine.client, "owedToYou");
    const list = await within(region).findByRole("list", {
      name: "Owed to you",
    });
    await waitFor(() => expect(rows(list)).toHaveLength(1));
    await user.click(
      within(rows(list)[0]).getByRole("button", { name: "Open" }),
    );
    await within(region).findByRole("article", { name: "Memory" });
    await user.click(
      within(region).getByRole("button", { name: "Back to the list" }),
    );
    expect(
      await within(region).findByRole("list", { name: "Owed to you" }),
    ).toBeInTheDocument();
    expect(
      within(region).getByRole("button", { name: /^Owed to you/ }),
    ).toHaveAttribute("aria-pressed", "true");
  });

  it("goes back to Home from the list a count opened, by its button and by Escape", async () => {
    at(size);
    const user = userEvent.setup();
    const one = await fromCard(plansEngine(install()).client, "due");
    await user.click(
      within(one.region).getByRole("button", { name: "Close memories" }),
    );
    expect(one.close).toHaveBeenCalledTimes(1);
    cleanup();
    const two = await fromCard(plansEngine(install()).client, "due");
    await user.keyboard("{Escape}");
    await waitFor(() => expect(two.close).toHaveBeenCalledTimes(1));
    cleanup();
    // Opened from the map's own button, closing it stays on the map.
    const close = vi.fn();
    render(
      <Explorer
        selected={null}
        onSelect={vi.fn()}
        close={close}
        paused
        state="ready"
        knowledge={controller({ memories: plansEngine(install()).client })}
      />,
    );
    await user.click(await screen.findByRole("button", { name: "Memories" }));
    const region = await screen.findByRole("region", { name: "Memories" });
    await user.click(
      within(region).getByRole("button", { name: "Close memories" }),
    );
    expect(close).not.toHaveBeenCalled();
    expect(screen.queryByRole("region", { name: "Memories" })).toBeNull();
  });

  it("reads AI changes as the brief counts them when its count opened them, and the wider list on asking", async () => {
    at(size);
    const user = userEvent.setup();
    const engine = plansEngine(install());
    const { region } = await fromCard(engine.client, "changes");
    await within(region).findByRole("list", { name: "AI changes" });
    expect(engine.of("changes")[0]).toEqual({ op: "changes", notHeld: true });
    await waitFor(() =>
      expect(region).toHaveTextContent(
        "1 group of changes, as the brief counts them",
      ),
    );
    expect(
      within(within(region).getByRole("list", { name: "AI changes" }))
        .getAllByRole("listitem")
        .filter((li) => li.className === "memory-change-group"),
    ).toHaveLength(1);
    await user.click(
      within(region).getByRole("button", { name: "Show the last 7 days" }),
    );
    await waitFor(() => expect(engine.of("changes").length).toBe(2));
    expect(engine.of("changes")[1]).toMatchObject({ op: "changes" });
    expect(engine.of("changes")[1]).not.toHaveProperty("notHeld");
    expect(region).not.toHaveTextContent("as the brief counts them");
  });

  it("offers the new sections by hand too, each reading its own list", async () => {
    at(size);
    const user = userEvent.setup();
    const engine = plansEngine(install());
    render(
      <Explorer
        selected={null}
        onSelect={vi.fn()}
        close={vi.fn()}
        paused
        state="ready"
        knowledge={controller({ memories: engine.client })}
      />,
    );
    await user.click(await screen.findByRole("button", { name: "Memories" }));
    const region = await screen.findByRole("region", { name: "Memories" });
    const strip = within(region).getByRole("toolbar", { name: "Memory sections" });
    await user.click(within(strip).getByRole("button", { name: "Tasks" }));
    expect(
      await within(region).findByRole("list", { name: "You owe" }),
    ).toBeInTheDocument();
    await user.click(within(strip).getByRole("button", { name: "Due" }));
    expect(
      await within(region).findByRole("list", { name: "Overdue" }),
    ).toBeInTheDocument();
    await user.click(within(strip).getByRole("button", { name: "Decisions" }));
    expect(
      await within(region).findByRole("list", {
        name: "Its review date has come",
      }),
    ).toBeInTheDocument();
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("says when each list is empty, and shows more of a long one", async () => {
    at(size);
    const user = userEvent.setup();
    const empty = plansEngine();
    const a = await fromCard(empty.client, "youOwe");
    expect(
      await within(a.region).findByText("You owe nothing here."),
    ).toBeInTheDocument();
    cleanup();
    const b = await fromCard(empty.client, "due");
    expect(
      await within(b.region).findByText("Nothing is due in the next 48 hours."),
    ).toBeInTheDocument();
    await user.click(within(b.region).getByRole("button", { name: "Later 0" }));
    expect(
      await within(b.region).findByText("Nothing has a later date."),
    ).toBeInTheDocument();
    expect(within(b.region).queryByRole("list", { name: "Later" })).toBeNull();
    cleanup();
    const c = await fromCard(empty.client, "decisions");
    expect(
      await within(c.region).findByText("No decision is due for a look."),
    ).toBeInTheDocument();
    cleanup();

    // Past a page: every row of the card's count is one tap away, and no further than it.
    const many = plansEngine({
      due: [
        ...Array.from({ length: 23 }, (_, i) =>
          dueRow(100 + i, `DEMO: errand ${i}.`, {
            at: `2026-10-${String(i + 1).padStart(2, "0")}T09:00:00+00:00`,
          }),
        ),
        dueRow(200, "DEMO: a later errand.", {
          window: "later",
          at: "2027-01-01T09:00:00+00:00",
        }),
      ],
      loops: Array.from({ length: 23 }, (_, i) =>
        loopRow(300 + i, `DEMO: task ${i}.`),
      ),
    });
    const d = await fromCard(many.client, "due");
    const overdue = await within(d.region).findByRole("list", {
      name: "Overdue",
    });
    expect(rows(overdue)).toHaveLength(20);
    expect(d.region).toHaveTextContent("23 due: 23 overdue");
    // A part's heading is its window's whole length, not what a page has shown of it.
    expect(
      within(d.region).getByRole("heading", { name: "Overdue 23" }),
    ).toBeInTheDocument();
    await user.click(
      within(d.region).getByRole("button", { name: "Show more" }),
    );
    await waitFor(() => expect(rows(overdue)).toHaveLength(23));
    expect(
      within(d.region).queryByRole("button", { name: "Show more" }),
    ).toBeNull();
    expect(d.region).not.toHaveTextContent("a later errand");
    cleanup();

    // A step on a long list leaves the rows shown, less the one.
    const e = await fromCard(many.client, "youOwe");
    const list = await within(e.region).findByRole("list", { name: "You owe" });
    await waitFor(() => expect(rows(list)).toHaveLength(20));
    await user.click(
      within(e.region).getByRole("button", { name: "Show more" }),
    );
    await waitFor(() => expect(rows(list)).toHaveLength(23));
    await user.click(
      within(rows(list)[0]).getByRole("button", { name: "Done" }),
    );
    await waitFor(() =>
      expect(e.region).toHaveTextContent("22 things you owe"),
    );
    expect(rows(list)).toHaveLength(22);
  });

  it("goes on when an engine from before these ops can't read them", async () => {
    at(size);
    const engine = plansEngine({ ...install(), older: true });
    const { region } = await fromCard(engine.client, "youOwe");
    expect(
      await within(region).findByText("DEMO: this engine can't read that."),
    ).toBeInTheDocument();
    expect(within(region).queryByRole("listitem")).toBeNull();
  });
});

it("names a snooze's day on the engine's clocks, though the app's config names another zone", async () => {
  // Noon on the app's clocks (UTC, the demo's) is already the next day on the install's.
  vi.useFakeTimers({
    toFake: ["Date"],
    now: new Date("2026-11-05T12:00:00Z"),
  });
  try {
    const user = userEvent.setup();
    const engine = plansEngine({ ...install(), zone: "Pacific/Kiritimati" });
    const { region } = await fromCard(engine.client, "due");
    const overdue = await within(region).findByRole("list", {
      name: "Overdue",
    });
    const row = rows(overdue)[0];
    await user.click(within(row).getByRole("button", { name: "Snooze…" }));
    const picker = within(row).getByLabelText("On");
    expect(picker).toHaveValue("2026-11-13");
    expect(picker).toHaveAttribute("min", "2026-11-07");
    await user.click(within(row).getByRole("button", { name: "Tomorrow" }));
    await waitFor(() =>
      expect(engine.of("resolve_due").at(-1)).toMatchObject({
        until: "2026-11-07T09:00",
      }),
    );
  } finally {
    vi.useRealTimers();
  }
});

// Home itself, on the demo's session with a client of the test's own.
const session = vi.hoisted(() => ({
  memories: undefined as MemoryClient | undefined,
}));
vi.mock("../src/apiClient", async (original) => {
  const real = await original<typeof Api>();
  return {
    ...real,
    createApiSession: (demo?: boolean) => ({
      ...real.createApiSession(demo),
      get memories() {
        return session.memories;
      },
    }),
  };
});
beforeAll(async () => {
  await import("../src/Explorer");
  await import("../src/DashboardCard");
}, 30_000);

describe.each(["phone", "desktop"] as const)(
  "on Home at a %s's size",
  (size) => {
    it("draws no brief: the sky's column holds no summary card, and the brief is never asked, with the memories served", async () => {
      at(size);
      const engine = plansEngine(install());
      session.memories = engine.client;
      const { container } = render(<App />);
      const sky = await screen.findByRole("region", {
        name: "Knowledge constellation",
      });
      // The memories are served: Home's other card asks for its one memory, and still nothing asks for the brief.
      await waitFor(() =>
        expect(engine.of("resurface").length).toBeGreaterThan(0),
      );
      await act(async () => {});
      expect(within(sky).queryByRole("region", { name: "Brief" })).toBeNull();
      expect(container.querySelector(".brief-card")).toBeNull();
      expect(engine.of("brief")).toHaveLength(0);
    }, 30_000);
  },
);

it("with the setting off, Home is as it was and asks for none of the lists", async () => {
  const engine = plansEngine(install());
  vi.mocked(engine.client.available).mockImplementation(async () => false);
  session.memories = engine.client;
  const { container } = render(<App />);
  await screen.findByRole("region", { name: "Knowledge constellation" });
  await waitFor(() => expect(engine.client.available).toHaveBeenCalled());
  await act(async () => {});
  expect(screen.queryByRole("region", { name: "Brief" })).toBeNull();
  expect(container.querySelector(".brief-card")).toBeNull();
  expect(screen.queryByRole("region", { name: "Memories" })).toBeNull();
  expect(engine.client.call).not.toHaveBeenCalled();
}, 30_000);
