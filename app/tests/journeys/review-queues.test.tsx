// A person's review queues, in the app alone (stories 9, 11 and 12): an AI client's near copy flagged, read and kept, a pair it
// names a contradiction settled by a person, one where the later replaces the earlier, one where the earlier stands by the later
// being marked wrong, a memory built on one marked wrong checked and said to hold, an insight an AI client proposes confirmed
// and its review taken back, a pair worth a look marked. Each count on the strip and the brief is its list's whole length, and
// each step changes the count once. No terminal.
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
  section,
  shown,
} from "./sandbox";

inSandbox();

// A queue's whole list, page by page.
async function whole(op: string, member: string) {
  let n = 0,
    cursor: string | null = null;
  do {
    const page: any = await memories({
      op,
      limit: 50,
      ...(cursor ? { cursor } : {}),
    });
    expect(page.ok, JSON.stringify(page).slice(0, 300)).toBe(true);
    n += page[member].length;
    cursor = page.next;
  } while (cursor);
  return n;
}
// Every count is its own list's length.
async function countsAreLengths() {
  const { available: _a, ok: _o, ...said } = await memories({ op: "queues" });
  expect(said).toEqual({
    held: await whole("review", "review"),
    duplicates: await whole("duplicates", "duplicates"),
    contradictions: await whole("contradictions", "contradictions"),
    stale: await whole("stale", "stale"),
    candidates: await whole("insight_candidates", "candidates"),
    insights: await whole("insight_stream", "insights"),
  });
  return said as Record<
    | "held"
    | "duplicates"
    | "contradictions"
    | "stale"
    | "candidates"
    | "insights",
    number
  >;
}
const count = async (queue: string) =>
  (await memories({ op: "queues" }))[queue] as number;
// The contradiction review's row for the pair of these two memories, either way round, from its whole list; null if none.
async function pairOf(a: string, b: string) {
  const pair = [a, b].sort().join();
  let cursor: string | null = null;
  do {
    const page: any = await memories({
      op: "contradictions",
      limit: 50,
      ...(cursor ? { cursor } : {}),
    });
    expect(page.ok, JSON.stringify(page).slice(0, 300)).toBe(true);
    const row = page.contradictions.find(
      (p: any) => [p.memory.id, p.conflict.id].sort().join() === pair,
    );
    if (row) return row;
    cursor = page.next;
  } while (cursor);
  return null;
}
const pairWaits = async (a: string, b: string) => (await pairOf(a, b)) !== null;
// Which of a waiting pair began later, as the engine and the view count it (an unknown start before every other): two saves
// milliseconds apart come in either order on a clock that steps back, so the journey asks rather than assumes.
async function byStart(a: { id: string }, b: { id: string }) {
  const row = await pairOf(a.id, b.id);
  expect(row, "the pair waits").not.toBeNull();
  const start = (id: string) => {
    const side = [row.memory, row.conflict].find((x: any) => x.id === id);
    return side.startedAt === null ? -Infinity : Date.parse(side.startedAt);
  };
  expect(start(a.id), "the two began at different moments").not.toBe(
    start(b.id),
  );
  return start(a.id) < start(b.id) ? [a, b] : [b, a];
}

journey("the review queues", () => {
  it("flags a near copy and keeps both, once, from the strip's count", async () => {
    const original = `DEMO: the Larkmoor Road allotment gate is locked at dusk every evening, and the key is at number twelve (${run}).`;
    const first = await aiClient("journey-alex", "remember", {
      content: original,
      idempotency_key: `journey-copy-a-${run}`,
    });
    expect(first.ok, JSON.stringify(first)).toBe(true);
    // The model embeds the second's words as it is saved: it is flagged at once, or the engine is not comparing.
    const copy = await aiClient("journey-sam", "remember", {
      content: `${original} Same gate, same key.`,
      idempotency_key: `journey-copy-b-${run}`,
    });
    expect(JSON.stringify(copy), JSON.stringify(copy)).toContain(first.id);
    const before = await countsAreLengths();
    expect(before.duplicates).toBeGreaterThan(0);

    const { user, region } = await memoriesView();
    await section(
      user,
      region,
      new RegExp(`^Copies, ${before.duplicates} waiting`),
    );
    const list = await within(region).findByRole("list", {
      name: "Possible copies",
    });
    const named = /Same gate, same key/;
    await shown(user, region, list, named);
    const row = () => rowWith(list, named);
    // Keep both settles them for good, so it is offered once what the copy is close to has been read, and asked.
    expect(
      within(row()).queryByRole("button", { name: /Keep both/ }),
    ).toBeNull();
    for (const read of within(row()).getAllByRole("button", {
      name: "Read it",
    }))
      await user.click(read);
    await user.click(
      await within(row()).findByRole("button", { name: "Keep both…" }),
    );
    await user.click(
      within(
        within(row()).getByRole("group", { name: "Keep both?" }),
      ).getByRole("button", { name: "Keep both" }),
    );
    expect(await within(region).findByText(/^Kept both/)).toBeInTheDocument();
    await waitFor(async () =>
      expect(await count("duplicates")).toBe(before.duplicates - 1),
    );
    // Kept: both stand, and it is not flagged again by asking again.
    expect((await memories({ op: "get", id: copy.id })).memory?.state).toBe(
      "current",
    );
    expect((await memories({ op: "get", id: first.id })).memory?.state).toBe(
      "current",
    );
    await countsAreLengths();
  });

  it("settles a named contradiction: both stand, and one that replaces the other", async () => {
    const earlier = await aiClient("journey-alex", "remember", {
      content: `DEMO: the Fennimore Street bins go out on Mondays (${run}).`,
      idempotency_key: `journey-conflict-a-${run}`,
    });
    const later = await aiClient("journey-alex", "remember", {
      content: `DEMO: the Fennimore Street bins go out on Thursdays (${run}).`,
      idempotency_key: `journey-conflict-b-${run}`,
    });
    const named = await aiClient("journey-alex", "propose_contradiction", {
      id: later.id,
      conflicting_id: earlier.id,
      reason: "DEMO: two days for one collection.",
    });
    expect(named.ok, JSON.stringify(named)).toBe(true);
    // An AI client can name it, and never settle it: resolve's settling (it takes no other memory over MCP) is a
    // person's alone, refused as such, and the pair waits as it did.
    const queued = await count("contradictions");
    expect(await pairWaits(later.id, earlier.id)).toBe(true);
    const refused = await aiClient("journey-alex", "resolve", {
      id: later.id,
      action: "settle_keep_both",
    });
    expect(refused?.code, JSON.stringify(refused)).toBe("needs_person");
    expect(await count("contradictions")).toBe(queued);
    expect(await pairWaits(later.id, earlier.id)).toBe(true);
    const before = await countsAreLengths();

    const { user, region } = await memoriesView();
    await section(
      user,
      region,
      new RegExp(`^Conflicts, ${before.contradictions} waiting`),
    );
    const list = await within(region).findByRole("list", {
      name: "Possible contradictions",
    });
    const thursdays = new RegExp(
      `Fennimore Street bins go out on Thursdays \\(${run}`,
    );
    await shown(user, region, list, thursdays);
    const row = rowWith(list, thursdays);
    await user.click(within(row).getByRole("button", { name: /Both stand/ }));
    await user.click(
      within(
        within(row).getByRole("group", { name: "Settle this pair?" }),
      ).getByRole("button", { name: "Keep both" }),
    );
    expect(
      await within(region).findByText(/^Settled: both stand/),
    ).toBeInTheDocument();
    await waitFor(async () =>
      expect(await count("contradictions")).toBe(before.contradictions - 1),
    );

    // The next pair: the one that began later replaces the other, which is kept in its history.
    const old = await aiClient("journey-alex", "remember", {
      content: `DEMO: the Harrowgate Yard skip is collected in March (${run}).`,
      idempotency_key: `journey-conflict-c-${run}`,
    });
    const newer = await aiClient("journey-alex", "remember", {
      content: `DEMO: the Harrowgate Yard skip is collected in May (${run}).`,
      idempotency_key: `journey-conflict-d-${run}`,
    });
    expect(
      (
        await aiClient("journey-alex", "propose_contradiction", {
          id: newer.id,
          conflicting_id: old.id,
          reason: "DEMO: two months for one skip.",
        })
      ).ok,
    ).toBe(true);
    const [first, last] = await byStart(old, newer);
    const words = (m: { id: string }) =>
      new RegExp(
        `Harrowgate Yard skip is collected in ${m.id === newer.id ? "May" : "March"} \\(${run}`,
      );
    await section(user, region, /^Recent/);
    await section(user, region, /^Conflicts/);
    const again = await within(region).findByRole("list", {
      name: "Possible contradictions",
    });
    await shown(user, region, again, words(last));
    const pair = rowWith(again, words(last));
    const standing = within(
      within(pair)
        .getByText(words(last))
        .closest("[role=group]") as HTMLElement,
    );
    await user.click(standing.getByRole("button", { name: /This one stands/ }));
    const question = within(
      within(pair).getByRole("group", { name: "Settle this pair?" }),
    );
    expect(
      question.queryByRole("button", { name: "Mark the other wrong" }),
    ).toBeNull();
    await user.click(
      question.getByRole("button", { name: "Replace the other" }),
    );
    expect(
      await within(region).findByText(
        /^Settled: the one you chose stands, and the other is kept as replaced\./,
      ),
    ).toBeInTheDocument();
    // The earlier ends where the later began, and reads as replaced once the database's clock is past that moment: on a clock
    // that steps back it can read as current for a second or two after the settling (049's memory_validity_state).
    const replaced = await eventually(
      async () => (await memories({ op: "get", id: first.id })).memory,
      (m: any) => m?.state === "replaced",
      "read as replaced",
      30,
    );
    expect(replaced.replacedBy).toBe(last.id);
    expect(replaced.validUntil).not.toBeNull();
    expect((await memories({ op: "get", id: last.id })).memory?.state).toBe(
      "current",
    );
    expect(await pairWaits(old.id, newer.id)).toBe(false);
    await countsAreLengths();
  });

  it("has the earlier of a named pair stand by marking the later wrong", async () => {
    // Sam's AI client, so Alex's stays the journeys' busiest token (WriteLimitTests).
    const june = await aiClient("journey-sam", "remember", {
      content: `DEMO: the Pellow Lane hedge is cut in June (${run}).`,
      idempotency_key: `journey-conflict-e-${run}`,
    });
    const september = await aiClient("journey-sam", "remember", {
      content: `DEMO: the Pellow Lane hedge is cut in September (${run}).`,
      idempotency_key: `journey-conflict-f-${run}`,
    });
    expect(
      (
        await aiClient("journey-sam", "propose_contradiction", {
          id: september.id,
          conflicting_id: june.id,
          reason: "DEMO: two months for one hedge.",
        })
      ).ok,
    ).toBe(true);
    const [first, last] = await byStart(june, september);
    const words = (m: { id: string }) =>
      new RegExp(
        `Pellow Lane hedge is cut in ${m.id === june.id ? "June" : "September"} \\(${run}`,
      );
    const before = await countsAreLengths();

    const { user, region } = await memoriesView();
    await section(
      user,
      region,
      new RegExp(`^Conflicts, ${before.contradictions} waiting`),
    );
    const list = await within(region).findByRole("list", {
      name: "Possible contradictions",
    });
    await shown(user, region, list, words(first));
    const pair = rowWith(list, words(first));
    const standing = within(
      within(pair)
        .getByText(words(first))
        .closest("[role=group]") as HTMLElement,
    );
    await user.click(standing.getByRole("button", { name: /This one stands/ }));
    // The earlier can't replace the later: it stands by taking the later out, which ends the review.
    const question = within(
      within(pair).getByRole("group", { name: "Settle this pair?" }),
    );
    expect(
      question.queryByRole("button", { name: "Replace the other" }),
    ).toBeNull();
    expect(
      question.getByRole("button", { name: "Forget the other" }),
    ).toBeInTheDocument();
    await user.click(
      question.getByRole("button", { name: "Mark the other wrong" }),
    );
    expect(
      await within(region).findByText(
        /^This one stands: the other is marked wrong/,
      ),
    ).toBeInTheDocument();
    const wrong = (await memories({ op: "get", id: last.id })).memory;
    expect(wrong?.status, JSON.stringify(wrong)).toBe("deprecated");
    expect((await memories({ op: "get", id: first.id })).memory?.state).toBe(
      "current",
    );
    await waitFor(async () =>
      expect(await count("contradictions")).toBe(before.contradictions - 1),
    );
    expect(await pairWaits(june.id, september.id)).toBe(false);
    await countsAreLengths();
  });

  it("checks a memory built on one marked wrong and says it still holds", async () => {
    const source = await aiClient("journey-alex", "remember", {
      content: `DEMO: the Oxlip Row boiler was serviced by Tarnbrook Heating (${run}).`,
      idempotency_key: `journey-stale-a-${run}`,
    });
    const built = await aiClient("journey-alex", "remember", {
      content: `DEMO: so the Oxlip Row boiler warranty runs another year (${run}).`,
      idempotency_key: `journey-stale-b-${run}`,
    });
    expect(
      (
        await aiClient("journey-alex", "link", {
          source_id: built.id,
          target_id: source.id,
          type: "drawn_from",
        })
      ).ok,
    ).toBe(true);
    const wrong = await aiClient("journey-alex", "set_status", {
      id: source.id,
      status: "deprecated",
    });
    expect(wrong.ok, JSON.stringify(wrong)).toBe(true);
    const before = await countsAreLengths();
    expect(before.stale).toBeGreaterThan(0);

    const { user, region } = await memoriesView();
    await section(
      user,
      region,
      new RegExp(`^To check, ${before.stale} waiting`),
    );
    const list = await within(region).findByRole("list", {
      name: "May be out of date",
    });
    const warranty = new RegExp(
      `Oxlip Row boiler warranty runs another year \\(${run}`,
    );
    await shown(user, region, list, warranty);
    await user.click(
      within(rowWith(list, warranty)).getByRole("button", {
        name: "Still true",
      }),
    );
    await waitFor(async () =>
      expect(await count("stale")).toBe(before.stale - 1),
    );
    await countsAreLengths();
  });

  it("confirms an AI client's proposed insight, and takes the review back", async () => {
    const title = `DEMO: the Hazelmere rota shifts after a late start (${run})`;
    const at = new Date().toISOString().replace(/\.\d+Z$/, "Z");
    const insight = await aiClient("journey-alex", "save_insight", {
      kind: "discovery",
      title,
      summary: "DEMO: a fictional pattern from fictional memories.",
      whyItMatters: "DEMO: it would change the rota.",
      nextStep: "DEMO: look at next month's rota.",
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
    const before = await countsAreLengths();
    const { user, region } = await memoriesView();
    await section(
      user,
      region,
      new RegExp(`^Insights, ${before.insights} waiting`),
    );
    const list = await within(region).findByRole("list", { name: "Insights" });
    const named = new RegExp(title.replace(/[()]/g, "."));
    await shown(user, region, list, named);
    await user.click(
      within(rowWith(list, named)).getByRole("button", { name: "It holds" }),
    );
    expect(
      await within(region).findByText(/^Confirmed: a person said it holds/),
    ).toBeInTheDocument();
    await waitFor(async () =>
      expect(await count("insights")).toBe(before.insights - 1),
    );
    // The same review, read in its own view, taken back.
    const views = within(region).getByRole("group", { name: "Which insights" });
    await user.click(within(views).getByRole("button", { name: /^Confirmed/ }));
    const confirmed = await within(region).findByRole("list", {
      name: "Insights",
    });
    await shown(user, region, confirmed, named);
    await user.click(
      within(rowWith(confirmed, named)).getByRole("button", {
        name: "Take the review back",
      }),
    );
    await waitFor(async () =>
      expect(await count("insights")).toBe(before.insights),
    );
    await countsAreLengths();
  });

  it("marks a pair worth a look, and declines one", async () => {
    // Found by the nightly pass itself, after cli/journeys.sh restarted the engine: its daily turn runs the pass, or the
    // scheduler does at the first minute the model is on when the turn found it still loading (InsightCandidateClockTests).
    const before = await countsAreLengths();
    expect(before.candidates).toBeGreaterThanOrEqual(2);
    const { user, region } = await memoriesView();
    await section(
      user,
      region,
      new RegExp(`^Pairs, ${before.candidates} waiting`),
    );
    const list = await within(region).findByRole("list", { name: "Pairs" });
    const rows = await within(list).findAllByRole("listitem");
    expect(rows.length).toBe(Math.min(before.candidates, 20));
    await user.click(
      within(rows[0]).getByRole("button", { name: /Mark used/ }),
    );
    await user.click(
      within(
        within(rows[0]).getByRole("group", { name: "Decide this pair?" }),
      ).getByRole("button", { name: "Mark it used" }),
    );
    await waitFor(async () =>
      expect(await count("candidates")).toBe(before.candidates - 1),
    );
    await waitFor(() =>
      expect(within(list).getAllByRole("listitem").length).toBe(
        Math.min(before.candidates - 1, 20),
      ),
    );
    const next = within(list).getAllByRole("listitem");
    await user.click(
      within(next[0]).getByRole("button", { name: /Nothing in it/ }),
    );
    await user.click(
      within(
        within(next[0]).getByRole("group", { name: "Decide this pair?" }),
      ).getByRole("button", { name: "Decline it" }),
    );
    await waitFor(async () =>
      expect(await count("candidates")).toBe(before.candidates - 2),
    );
    await countsAreLengths();
  });
});
