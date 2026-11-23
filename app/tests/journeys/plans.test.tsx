// The brief's lists in the Memories view, and a person's word on a row (story 12): tasks and commitments, what is due,
// decisions and their outcomes, each with its Undo. Each count the brief gives an AI client is the length of the list a person
// opens, read from the real engine with the corpus and an AI client's memories in it. Home draws no brief (the owner,
// 2026-11-09), so a person goes into the sky and Memories, as anywhere else. No terminal.
import { expect, it } from "vitest";
import { waitFor, within } from "@testing-library/react";
import {
  aiClient,
  eventually,
  inSandbox,
  journey,
  memories,
  memoriesView,
  rowWith,
  run,
} from "./sandbox";

inSandbox();

// The Memories view from Home, as a person goes: into the sky, Memories, and the strip's button for the list.
async function toList(strip: string | RegExp) {
  const opened = await memoriesView();
  const bar = within(opened.region).getByRole("toolbar", { name: "Memory sections" });
  // The strip names a queue with its count once the counts are in, a moment after the view opens: wait for that name.
  await opened.user.click(
    await within(bar).findByRole(
      "button",
      { name: strip },
      { timeout: 10_000 },
    ),
  );
  return opened;
}

journey("the brief's lists, in the Memories view", () => {
  it("opens You owe at its count, takes Done with its Undo, and the count follows", async () => {
    const owed = await aiClient("journey-alex", "remember", {
      content: `DEMO: return the Sorrel Lane ladder to number nine (${run}).`,
      owed_to: "number nine",
      idempotency_key: `journey-owe-${run}`,
    });
    expect(owed.ok, JSON.stringify(owed)).toBe(true);
    const brief = await memories({ op: "brief" });
    const lists = await memories({ op: "loops", direction: "out", limit: 50 });
    expect(brief.youOwe.count).toBe(lists.count);
    expect(lists.loops.map((l: any) => l.id)).toContain(owed.id);

    const { user, region } = await toList("Tasks");
    const list = await within(region).findByRole("list", { name: "You owe" });
    await within(list).findByText(
      new RegExp(`Sorrel Lane ladder to number nine \\(${run}`),
    );
    expect(within(list).getAllByRole("listitem").length).toBe(
      Math.min(lists.count, 20),
    );
    await user.click(
      within(
        rowWith(
          list,
          new RegExp(`Sorrel Lane ladder to number nine \\(${run}`),
        ),
      ).getByRole("button", { name: "Done" }),
    );
    await waitFor(async () =>
      expect(
        (await memories({ op: "loops", direction: "out", limit: 50 })).count,
      ).toBe(lists.count - 1),
    );
    const said = await within(region).findByRole("status");
    await user.click(within(said).getByRole("button", { name: "Undo" }));
    await waitFor(async () =>
      expect(
        (await memories({ op: "loops", direction: "out", limit: 50 })).count,
      ).toBe(lists.count),
    );
    const history = await memories({ op: "history", id: owed.id });
    expect(
      history.history.changes
        .filter((c: any) => c.channel === "app")
        .map((c: any) => c.event),
    ).toEqual(expect.arrayContaining(["resolved"]));
  });

  it("opens each review queue at the count the brief gives", async () => {
    // What the journeys before this one left waiting, and one of each to be sure of one: a copy, a contradiction an AI
    // client names, an insight it proposes.
    const words = `DEMO: the Bellwether Mews meter cupboard key is on the hook by the door (${run}).`;
    const first = await aiClient("journey-alex", "remember", {
      content: words,
      idempotency_key: `journey-card-a-${run}`,
    });
    await aiClient("journey-alex", "remember", {
      content: `${words} The same hook.`,
      idempotency_key: `journey-card-b-${run}`,
    });
    expect(first.ok, JSON.stringify(first)).toBe(true);
    const earlier = await aiClient("journey-alex", "remember", {
      content: `DEMO: the Bellwether Mews recycling goes out on Tuesdays (${run}).`,
      idempotency_key: `journey-card-c-${run}`,
    });
    const later = await aiClient("journey-alex", "remember", {
      content: `DEMO: the Bellwether Mews recycling goes out on Fridays (${run}).`,
      idempotency_key: `journey-card-d-${run}`,
    });
    const named = await aiClient("journey-alex", "propose_contradiction", {
      id: later.id,
      conflicting_id: earlier.id,
      reason: "DEMO: two days for one collection.",
    });
    expect(named.ok, JSON.stringify(named)).toBe(true);
    const at = new Date().toISOString().replace(/\.\d+Z$/, "Z");
    const insight = await aiClient("journey-alex", "save_insight", {
      kind: "discovery",
      title: `DEMO: the Bellwether Mews collections move after a bank holiday (${run})`,
      summary: "DEMO: a fictional pattern from fictional memories.",
      whyItMatters: "DEMO: it would change the bin rota.",
      nextStep: "DEMO: look at next month's collections.",
      evidenceLevel: "weak",
      limitations: "DEMO: two weeks only.",
      generatedAt: at,
      sourcesAsOf: at,
      generatedBy: "journey",
      points: [{ text: "DEMO: one point.", type: "observed" }],
      stars: [],
      sources: [],
    });
    expect(JSON.stringify(insight), JSON.stringify(insight)).toMatch(
      /"id"|"ok":true/,
    );
    // The brief and the queues are two reads: read again, for a minute at most, until they agree. (Once, in a run by hand,
    // the brief said one copy fewer than the queue on the first read; it was not seen again.)
    const { counts } = await eventually(
      async () => ({
        counts: await memories({ op: "queues" }),
        brief: await memories({ op: "brief" }),
      }),
      ({ counts, brief }) =>
        brief.duplicates.count === counts.duplicates &&
        brief.contradictions.count === counts.contradictions &&
        brief.insights.count === counts.insights &&
        brief.held === counts.held,
      "the brief's counts were the queues' own",
      60,
    );
    // Each queue opens with the strip's button for it pressed, saying the brief's count.
    for (const [title, list, strip, n] of [
      ["Possible copies", "Possible copies", "Copies", counts.duplicates],
      [
        "Possible contradictions",
        "Possible contradictions",
        "Conflicts",
        counts.contradictions,
      ],
      ["Pending insights", "Insights", "Insights", counts.insights],
    ] as const) {
      expect(n, title).toBeGreaterThan(0);
      const { view, region } = await toList(new RegExp(`^${strip},`));
      expect(
        await within(region).findByRole("list", { name: list }),
      ).toBeInTheDocument();
      await waitFor(() =>
        expect(
          within(region).getByRole("button", {
            name: new RegExp(`^${strip}, ${n} waiting`),
          }),
        ).toHaveAttribute("aria-pressed", "true"),
      );
      view.unmount();
    }
  });

  it("snoozes what is due and clears a date, in the Due list", async () => {
    const soon = new Date(Date.now() + 86_400_000).toISOString().slice(0, 10);
    const dated = await aiClient("journey-alex", "remember", {
      content: `DEMO: the Tansy Close gas check is booked (${run}).`,
      when: soon,
      when_kind: "due",
      idempotency_key: `journey-due-${run}`,
    });
    expect(dated.ok, JSON.stringify(dated)).toBe(true);
    const brief = await memories({ op: "brief" });
    const due = await memories({ op: "due", limit: 50 });
    expect(brief.due.overdue).toBe(due.overdue);
    expect(brief.due.upcoming).toBe(due.upcoming);
    expect(due.due.map((d: any) => d.id)).toContain(dated.id);

    const { user, region } = await toList("Due");
    const list = await within(region).findByRole("list", {
      name: "Within 48 hours",
    });
    await within(list).findByText(
      new RegExp(`Tansy Close gas check is booked \\(${run}`),
    );
    const row = () =>
      rowWith(list, new RegExp(`Tansy Close gas check is booked \\(${run}`));
    await user.click(within(row()).getByRole("button", { name: "Snooze…" }));
    await user.click(within(row()).getByRole("button", { name: "In a week" }));
    await waitFor(async () =>
      expect(
        (await memories({ op: "get", id: dated.id })).memory?.revision,
      ).toBeGreaterThan(1),
    );
    const later = await within(region).findByRole("button", { name: /^Later/ });
    await user.click(later);
    const put = await within(region).findByRole("list", { name: "Later" });
    await within(put).findByText(
      new RegExp(`Tansy Close gas check is booked \\(${run}`),
    );
    await user.click(
      within(
        rowWith(put, new RegExp(`Tansy Close gas check is booked \\(${run}`)),
      ).getByRole("button", { name: "Clear date" }),
    );
    await waitFor(async () => {
      const left = await memories({ op: "due", limit: 50 });
      expect(left.due.map((d: any) => d.id)).not.toContain(dated.id);
    });
  });

  it("records a decision's outcome and takes it back, with calibration below its minimum saying so", async () => {
    const decided = await aiClient("journey-alex", "remember", {
      content: `DEMO: decided to resurface the Ivyhatch drive this spring (${run}).`,
      decision: true,
      confidence: 0.7,
      confidence_source: "stated",
      review_by: new Date(Date.now() - 86_400_000).toISOString().slice(0, 10),
      idempotency_key: `journey-decision-${run}`,
    });
    expect(decided.ok, JSON.stringify(decided)).toBe(true);
    const review = await memories({ op: "decisions", state: "due", limit: 50 });
    expect(review.decisions.map((d: any) => d.id)).toContain(decided.id);
    const brief = await memories({ op: "brief" });
    expect(brief.decisions.count).toBe(review.dueNow + review.upcoming);

    const { user, region } = await toList("Decisions");
    const list = await within(region).findByRole("list", { name: /review/i });
    await within(list).findByText(
      new RegExp(`resurface the Ivyhatch drive this spring \\(${run}`),
    );
    const row = rowWith(
      list,
      new RegExp(`resurface the Ivyhatch drive this spring \\(${run}`),
    );
    await user.click(
      within(row).getByRole("button", { name: /Right|It was right/ }),
    );
    await waitFor(async () =>
      expect(
        (await memories({ op: "get", id: decided.id })).memory?.revision,
      ).toBeGreaterThan(1),
    );
    const said = await within(region).findByRole("status");
    await user.click(within(said).getByRole("button", { name: "Undo" }));
    await waitFor(async () => {
      const again = await memories({
        op: "decisions",
        state: "due",
        limit: 50,
      });
      expect(again.decisions.map((d: any) => d.id)).toContain(decided.id);
    });
    const calibration = await memories({ op: "calibration" });
    expect(calibration.ready).toBe(false);
  });
});
