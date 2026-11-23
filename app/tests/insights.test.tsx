import { expect, it, vi } from "vitest";
import {
  act,
  render,
  renderHook,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { readFileSync } from "node:fs";
import {
  applyFeedback,
  cleanInsight,
  cleanRuns,
  cleanInsights,
  currentInsight,
  feedbackInput,
  laterUntil,
  runLine,
  sourceStar,
  statusName,
} from "../src/insights";
import type { DailyInsight, InsightRun } from "../src/insights";
import { createDemoInsights, demoInsights } from "../src/demoInsights";
import { createDemoSession, demoData } from "../src/demo";
import { emptyGraph } from "../src/liveGraph";
import { InsightPanel } from "../src/panels/InsightPanel";
import { ChangedLately } from "../src/ChangedLately";
import { createApiSession } from "../src/apiClient";
import { useInsights } from "../src/useInsights";
import type { GraphSession } from "../src/liveGraph";
import { record } from "./synthetic";

const now = Date.parse("2026-10-28T15:00:00Z");
const hours = (h: number) => new Date(now - h * 3_600_000).toISOString();
// A fictional insight, made `age` hours ago.
function insight(
  id: string,
  age: number,
  extra: Partial<DailyInsight> = {},
): DailyInsight {
  return {
    ...demoInsights(now)[0],
    id,
    title: `Fictional insight ${id}`,
    generatedAt: hours(age),
    createdAt: hours(age),
    updatedAt: hours(age),
    status: "new",
    feedback: null,
    supersedes: null,
    ...extra,
  };
}

it("shows the newest new or saved insight under three days old, and a snoozed one once it is back", () => {
  expect(currentInsight([], now)).toBeNull();
  const fresh = insight("fresh", 2),
    saved = insight("saved", 30, { status: "saved" }),
    old = insight("old", 80);
  expect(currentInsight([saved, fresh, old], now)?.id).toBe("fresh");
  expect(currentInsight([saved, old], now)?.id).toBe("saved");
  // About three days, then it steps aside.
  expect(currentInsight([old], now)).toBeNull();
  // Acted on or replaced: never current.
  for (const status of ["explored", "dismissed", "superseded"] as const)
    expect(currentInsight([insight("x", 1, { status })], now)).toBeNull();
  // Snoozed until tomorrow: not yet. Snoozed until an hour ago: back, even
  // though it was made a week ago, and ahead of yesterday's insight.
  const later = (until: string) =>
    insight("later", 24 * 7, {
      status: "snoozed",
      feedback: { value: "later", at: hours(24 * 6), until },
    });
  expect(currentInsight([later(hours(-20))], now)).toBeNull();
  expect(currentInsight([saved, later(hours(1))], now)?.id).toBe("later");
  // Back for three days from its return, then gone.
  expect(currentInsight([later(hours(80))], now)).toBeNull();
  // One that another insight replaced does not return; a far-future date is
  // not believed.
  const replacement = insight("replacement", 40, { supersedes: "fresh" });
  expect(currentInsight([fresh, replacement], now)?.id).toBe("replacement");
  expect(currentInsight([insight("future", -48)], now)).toBeNull();
});

it("names each status in a word or two", () => {
  expect(statusName(insight("a", 1), now)).toBe("New");
  expect(statusName(insight("a", 1, { status: "dismissed" }), now)).toBe(
    "Not useful",
  );
  expect(statusName(insight("a", 1, { status: "superseded" }), now)).toBe(
    "Replaced",
  );
  const snoozed = insight("a", 1, {
    status: "snoozed",
    feedback: { value: "later", at: hours(1), until: hours(-48) },
  });
  expect(statusName(snoozed, now)).toMatch(/^Later · /);
});

it("validates insights against the contract and fails closed", () => {
  const good = insight("good", 1);
  expect(cleanInsight(good)).toEqual(good);
  // Unknown fields (like the graph tools' linkCount) are left behind; a
  // point's source must exist.
  const extra = {
    ...good,
    secret: "not part of the contract",
    stars: good.stars.map((s) => ({ ...s, linkCount: 3 })),
    points: [{ text: "Fictional", type: "observed", source: 99 }],
  };
  const cleaned = cleanInsight(extra)!;
  expect(cleaned).not.toHaveProperty("secret");
  expect(cleaned.stars).toEqual(good.stars);
  expect(cleaned.points[0]).toEqual({ text: "Fictional", type: "observed" });
  for (const bad of [
    { ...good, kind: "news" },
    { ...good, title: "" },
    { ...good, title: "x".repeat(121) },
    { ...good, summary: 4 },
    { ...good, points: Array(13).fill({ text: "a", type: "observed" }) },
    { ...good, points: [{ text: "a", type: "fact" }] },
    { ...good, status: "archived" },
    { ...good, id: "has spaces" },
    { ...good, generatedAt: "not a date" },
    // Web pages and Drive files need an https link.
    {
      ...good,
      sources: [{ kind: "web", title: "Page", retrievedAt: hours(1) }],
    },
    {
      ...good,
      sources: [
        {
          kind: "web",
          title: "Page",
          url: "javascript:alert(1)",
          retrievedAt: hours(1),
        },
      ],
    },
    {
      ...good,
      sources: [
        {
          kind: "drive",
          title: "File",
          url: "http://drive.google.com/x",
          retrievedAt: hours(1),
        },
      ],
    },
    { ...good, sources: [{ kind: "graph", title: "Star" }] },
  ])
    expect(cleanInsight(bad)).toBeNull();
  // A bad envelope is no insights; a bad insight is left out of the list.
  expect(cleanInsights(null)).toEqual([]);
  expect(cleanInsights({ insights: "many" })).toEqual([]);
  expect(cleanInsights({ insights: Array(51).fill(good) })).toEqual([]);
  expect(
    cleanInsights({ insights: [good, { ...good, kind: "news" }, good] }),
  ).toEqual([good]);
});

it("accepts only known feedback, with a return time for later", () => {
  expect(feedbackInput({ id: "a-1", value: "interesting" }, now)).toEqual({
    id: "a-1",
    value: "interesting",
  });
  const until = hours(-24);
  expect(feedbackInput({ id: "a-1", value: "later", until }, now)).toEqual({
    id: "a-1",
    value: "later",
    until,
  });
  for (const bad of [
    null,
    { id: "a-1", value: "love" },
    { id: "a 1", value: "explore" },
    { id: "a-1", value: "later" },
    { id: "a-1", value: "later", until: hours(-24 * 40) },
    { id: "a-1", value: "later", until: "soon" },
    { id: "a-1", value: "explore", until },
    { id: "a-1", value: "explore", extra: true },
  ])
    expect(feedbackInput(bad, now)).toBeNull();
});

it("records feedback as the engine does, and remembers later as a morning", () => {
  const base = insight("a", 1);
  const at = new Date(now);
  expect(applyFeedback(base, "interesting", {}, at).status).toBe("saved");
  expect(applyFeedback(base, "explore", {}, at).status).toBe("explored");
  expect(applyFeedback(base, "not_useful", {}, at).status).toBe("dismissed");
  const until = laterUntil("week", at);
  const later = applyFeedback(base, "later", { until }, at);
  expect(later).toMatchObject({
    status: "snoozed",
    feedback: { value: "later", until, at: at.toISOString() },
  });
  expect(new Date(until).getHours()).toBe(7);
  expect(Date.parse(until) - now).toBeGreaterThan(6 * 86_400_000);
  expect(new Date(laterUntil("tomorrow", at)).getDate()).toBe(
    new Date(now + 86_400_000).getDate(),
  );
});

it("gives the demo one fictional insight of each kind, drawn from stars its sky loads", async () => {
  const list = demoInsights(now);
  expect(list.map((i) => i.kind).sort()).toEqual([
    "discovery",
    "focus",
    "suggestion",
  ]);
  for (const i of list) expect(cleanInsight(i)).toEqual(i);
  expect(currentInsight(list, now)?.kind).toBe("discovery");
  // Every star is in the demo sky after its first refresh.
  const page = await createDemoSession(demoData).refresh(emptyGraph());
  const ids = new Set(page.nodes.map((n) => n.id));
  for (const i of list)
    for (const s of i.stars) expect(ids.has(s.graphId), s.title).toBe(true);
  // Web sources point only at a reserved example domain.
  for (const i of list)
    for (const s of i.sources)
      if (s.url) expect(new URL(s.url).hostname).toBe("example.org");
});

it("changes the demo's insights in memory only", async () => {
  vi.mocked(fetch).mockClear();
  const session = createApiSession(true);
  const before = await session.insights!();
  expect(before.insights[0].status).toBe("new");
  // Two fictional runs: this morning's saved the discovery, one was quiet.
  expect(before.runs.map((r) => r.outcome)).toEqual(["saved", "quiet"]);
  expect(before.runs[0].insightId).toBe(before.insights[0].id);
  const updated = await session.insightFeedback!(
    before.insights[0].id,
    "handled",
    undefined,
    "Linked them last night.",
  );
  expect(updated).toMatchObject({
    status: "handled",
    feedback: { value: "handled", note: "Linked them last night." },
  });
  expect((await session.insights!()).insights[0].status).toBe("handled");
  // A separate demo session starts over, and nothing was sent anywhere.
  expect((await createDemoInsights().list()).insights[0].status).toBe("new");
  expect(fetch).not.toHaveBeenCalled();
  await expect(
    session.insightFeedback!("unknown", "explore"),
  ).rejects.toThrow();
});

it("reads live insights through the route, validating them and failing closed", async () => {
  const good = insight("live", 1);
  const respond = (body: unknown) =>
    new Response(JSON.stringify(body), {
      headers: { "content-type": "application/json" },
    });
  const session = createApiSession(false);
  vi.mocked(fetch).mockResolvedValueOnce(
    respond({ insights: [good, { ...good, id: "bad", kind: "news" }] }),
  );
  expect(await session.insights!()).toEqual({ insights: [good], runs: [] });
  expect(vi.mocked(fetch).mock.calls.at(-1)![0]).toBe("/api/insights");
  // Runs that don't check out are none; the insights still show.
  vi.mocked(fetch).mockResolvedValueOnce(
    respond({ insights: [good], runs: "lots" }),
  );
  expect(await session.insights!()).toEqual({ insights: [good], runs: [] });
  vi.mocked(fetch).mockResolvedValueOnce(respond({ nope: true }));
  await expect(session.insights!()).rejects.toThrow();
  vi.mocked(fetch).mockResolvedValueOnce(respond({ ...good, status: "saved" }));
  expect((await session.insightFeedback!("live", "interesting")).status).toBe(
    "saved",
  );
  const [, init] = vi.mocked(fetch).mock.calls.at(-1)!;
  expect(init?.method).toBe("POST");
  expect(JSON.parse(String(init?.body))).toEqual({
    id: "live",
    value: "interesting",
  });
  // A note goes with the feedback.
  vi.mocked(fetch).mockResolvedValueOnce(
    respond(applyFeedback(good, "handled", { note: "Done" })),
  );
  await session.insightFeedback!("live", "handled", undefined, "Done");
  expect(
    JSON.parse(String(vi.mocked(fetch).mock.calls.at(-1)![1]?.body)),
  ).toEqual({ id: "live", value: "handled", note: "Done" });
  // An answer for another insight is not believed.
  vi.mocked(fetch).mockResolvedValueOnce(respond({ ...good, id: "other" }));
  await expect(session.insightFeedback!("live", "explore")).rejects.toThrow();
});

// Home's insights on every install, memory.tools off included: a source's link is read as long as the engine keeps one
// (InsightDraft.Https, 2048 characters, since run 21; before it 2000), so an insight that save_insight kept is shown and
// its feedback answer believed; one past it is left out of the list, and its feedback answer refused.
it("reads an insight whose source link is as long as the engine keeps, and leaves out one past it", async () => {
  const respond = (body: unknown) =>
    new Response(JSON.stringify(body), {
      headers: { "content-type": "application/json" },
    });
  const linked = (id: string, length: number) =>
    insight(id, 1, {
      sources: [
        {
          kind: "web",
          title: "DEMO: a long link",
          url:
            "https://example.invalid/" +
            "a".repeat(length - "https://example.invalid/".length),
          retrievedAt: hours(2),
        },
      ],
    });
  const longest = linked("longest", 2048);
  const past = linked("past", 2049);
  expect(longest.sources[0].url).toHaveLength(2048);
  const session = createApiSession(false);
  vi.mocked(fetch).mockResolvedValueOnce(
    respond({ insights: [longest, past] }),
  );
  const shown = (await session.insights!()).insights;
  expect(shown.map((i) => [i.id, i.sources[0].url])).toEqual([
    ["longest", longest.sources[0].url],
  ]);
  vi.mocked(fetch).mockResolvedValueOnce(respond(longest));
  expect(
    (await session.insightFeedback!("longest", "interesting")).sources[0].url,
  ).toHaveLength(2048);
  vi.mocked(fetch).mockResolvedValueOnce(respond(past));
  await expect(
    session.insightFeedback!("past", "interesting"),
  ).rejects.toThrow();
});

it("opens a knowledge, engine or Second Brain source at its loaded star", () => {
  const memory = record("memory-1");
  const saved = record("doc-1", "rhythm");
  const nodes = [memory, saved];
  const at = hours(1);
  expect(
    sourceStar(
      { kind: "graph", title: "Star", locator: memory.id, retrievedAt: at },
      nodes,
    ),
  ).toBe(memory.id);
  expect(
    sourceStar(
      { kind: "nas", title: "Saved", locator: "doc-1", retrievedAt: at },
      nodes,
    ),
  ).toBe(saved.id);
  expect(
    sourceStar(
      {
        kind: "second-brain",
        title: "Memory",
        locator: "memory-1",
        retrievedAt: at,
      },
      nodes,
    ),
  ).toBe(memory.id);
  expect(
    sourceStar(
      { kind: "graph", title: "Gone", locator: "missing", retrievedAt: at },
      nodes,
    ),
  ).toBeNull();
  expect(
    sourceStar(
      {
        kind: "web",
        title: "Page",
        url: "https://example.org",
        locator: memory.id,
        retrievedAt: at,
      },
      nodes,
    ),
  ).toBeNull();
});

function panel(props: Partial<Parameters<typeof InsightPanel>[0]> = {}) {
  const list = demoInsights(now);
  const handlers = {
    onFeedback: vi.fn(),
    onTrail: vi.fn(),
    onStar: vi.fn(),
    onOpenInsight: vi.fn(),
  };
  const ritual = record("ritual");
  ritual.id = list[0].stars[0].graphId;
  const view = render(
    <InsightPanel
      insight={list[0]}
      insights={list}
      nodes={[ritual]}
      demo
      {...handlers}
      {...props}
    />,
  );
  return { list, ritual, view, ...handlers };
}

it("shows every part of an insight, with points marked by kind and linked to their sources", async () => {
  const { list, ritual, onStar, onTrail } = panel();
  const discovery = list[0];
  expect(
    screen.getByRole("heading", { level: 2, name: discovery.title }),
  ).toBeInTheDocument();
  expect(screen.getByText(/^DISCOVERY/)).toBeInTheDocument();
  expect(screen.getByText("SAMPLE")).toBeInTheDocument();
  expect(screen.getByText(discovery.summary)).toBeInTheDocument();
  expect(screen.getByText(discovery.whyItMatters)).toBeInTheDocument();
  expect(screen.getByText(discovery.nextStep)).toBeInTheDocument();
  expect(screen.getByText(discovery.limitations)).toBeInTheDocument();
  expect(screen.getByLabelText("moderate evidence")).toBeInTheDocument();
  // Facts, outside claims, inferences and hypotheses each carry their mark.
  const points = screen
    .getByRole("heading", { name: "What it rests on" })
    .parentElement!.querySelectorAll("li");
  expect([...points].map((p) => p.className)).toEqual([
    "insight-point observed",
    "insight-point observed",
    "insight-point observed",
    "insight-point sourced",
    "insight-point inferred",
    "insight-point hypothesis",
  ]);
  for (const name of ["Outside source", "Inference", "Hypothesis"])
    expect(screen.getByText(name)).toBeInTheDocument();
  expect(screen.getAllByText("Observed")).toHaveLength(3);
  // Web pages open in a new tab, without a referrer.
  const sources = screen.getByRole("heading", {
    name: "Sources",
  }).parentElement!;
  const link = within(sources).getByRole("link", {
    name: /A gentle cue for winding down/,
  });
  expect(link).toHaveAttribute(
    "href",
    "https://example.org/fictional/winding-down",
  );
  expect(link).toHaveAttribute("target", "_blank");
  expect(link).toHaveAttribute("rel", "noopener noreferrer");
  expect(within(sources).getByText(/^example\.org/)).toBeInTheDocument();
  // A loaded star opens in the sky; one that isn't loaded is named only.
  const user = userEvent.setup();
  await user.click(
    within(sources).getByRole("button", { name: /Evening ritual/ }),
  );
  expect(onStar).toHaveBeenCalledWith(ritual.id);
  expect(
    within(sources).queryByRole("button", { name: /Home library/ }),
  ).not.toBeInTheDocument();
  expect(within(sources).getAllByText(/not in the loaded sky/)).toHaveLength(2);
  // A point's source opens the same way.
  const first = points[0] as HTMLElement;
  await user.click(
    within(first).getByRole("button", { name: /Evening ritual/ }),
  );
  expect(onStar).toHaveBeenCalledTimes(2);
  // The trail: one of its four stars is loaded.
  expect(
    screen.getByText("Lights its 1 star and frames them"),
  ).toBeInTheDocument();
  await user.click(screen.getByRole("button", { name: "Show in the sky" }));
  expect(onTrail).toHaveBeenCalledWith(discovery);
  expect(screen.getByText(/fictional sample insight/)).toBeInTheDocument();
});

it("sends feedback, offers tomorrow or next week for later, and shows where it stands", async () => {
  const user = userEvent.setup();
  const { list, view, onFeedback, onTrail } = panel();
  await user.click(screen.getByRole("button", { name: "Interesting" }));
  expect(onFeedback).toHaveBeenLastCalledWith(
    "interesting",
    undefined,
    undefined,
  );
  // Explore also opens the trail.
  await user.click(screen.getByRole("button", { name: "Explore" }));
  expect(onFeedback).toHaveBeenLastCalledWith("explore", undefined, undefined);
  expect(onTrail).toHaveBeenCalledWith(list[0]);
  // Later opens two choices in place.
  const later = screen.getByRole("button", { name: "Remind me later" });
  expect(later).toHaveAttribute("aria-expanded", "false");
  await user.click(later);
  expect(later).toHaveAttribute("aria-expanded", "true");
  await user.click(screen.getByRole("button", { name: "Next week" }));
  const [value, until] = onFeedback.mock.lastCall!;
  expect(value).toBe("later");
  expect(Date.parse(until) - Date.now()).toBeGreaterThan(6 * 86_400_000);
  expect(
    screen.queryByRole("button", { name: "Next week" }),
  ).not.toBeInTheDocument();
  const rerender = (props: Partial<Parameters<typeof InsightPanel>[0]>) =>
    view.rerender(
      <InsightPanel
        insight={list[0]}
        insights={list}
        nodes={[]}
        demo
        onFeedback={onFeedback}
        onTrail={onTrail}
        onStar={vi.fn()}
        onOpenInsight={vi.fn()}
        {...props}
      />,
    );
  // While saving, then saved.
  rerender({ pending: "not_useful" });
  expect(screen.getByRole("status")).toHaveTextContent("Saving…");
  expect(screen.getByRole("button", { name: "Not useful" })).toHaveAttribute(
    "aria-pressed",
    "true",
  );
  rerender({ insight: applyFeedback(list[0], "not_useful") });
  expect(screen.getByRole("status")).toHaveTextContent("It won't come back");
  expect(screen.getByRole("button", { name: "Not useful" })).toHaveAttribute(
    "aria-pressed",
    "true",
  );
  expect(screen.getByRole("button", { name: "Interesting" })).toHaveAttribute(
    "aria-pressed",
    "false",
  );
  rerender({
    insight: applyFeedback(list[0], "later", { until: laterUntil("tomorrow") }),
  });
  expect(screen.getByRole("status")).toHaveTextContent(
    "It will come back tomorrow morning.",
  );
  rerender({ failed: "interesting" });
  expect(screen.getByRole("status")).toHaveTextContent("didn't save");
});

it("lists earlier insights with their status and reopens one in the panel", async () => {
  const { onOpenInsight, list } = panel();
  const history = screen.getByRole("region", { name: "Earlier insights" });
  const rows = within(history).getAllByRole("button");
  expect(rows).toHaveLength(2);
  expect(rows[0]).toHaveTextContent(list[1].title);
  expect(rows[0]).toHaveTextContent("Saved");
  expect(rows[1]).toHaveTextContent(list[2].title);
  expect(rows[1]).toHaveTextContent("Explored");
  expect(rows[1]).toHaveTextContent("Idea");
  await userEvent.setup().click(rows[1]);
  expect(onOpenInsight).toHaveBeenCalledWith(list[2].id);
});

it("renders an insight's text as text, never as markup", () => {
  const hostile = insight("hostile", 1, {
    title: '<img src=x onerror="alert(1)">',
    summary: "<script>alert(1)</script>",
  });
  const { container } = render(
    <InsightPanel
      insight={hostile}
      insights={[hostile]}
      nodes={[]}
      demo={false}
      onFeedback={vi.fn()}
      onTrail={vi.fn()}
      onStar={vi.fn()}
      onOpenInsight={vi.fn()}
    />,
  );
  expect(container.querySelector("img, script")).toBeNull();
  expect(
    screen.getByRole("heading", { level: 2, name: hostile.title }),
  ).toBeInTheDocument();
  // A live insight says where it came from instead.
  expect(screen.getByText(/Your AI's daily look · saved/)).toBeInTheDocument();
});

it("says so quietly when there is no insight to show", () => {
  render(
    <InsightPanel
      insight={null}
      insights={[]}
      nodes={[]}
      demo={false}
      onFeedback={vi.fn()}
      onTrail={vi.fn()}
      onStar={vi.fn()}
      onOpenInsight={vi.fn()}
    />,
  );
  expect(
    screen.getByRole("heading", { name: "No insight right now" }),
  ).toBeInTheDocument();
});

it("puts the current insight in the corner, with recent changes a tap away", async () => {
  const user = userEvent.setup();
  const discovery = demoInsights(now)[0];
  const onInsight = vi.fn(),
    onPanel = vi.fn();
  const props = {
    nodes: [],
    edges: [],
    sources: [],
    paused: true,
    onOpen: vi.fn(),
    onSources: vi.fn(),
    onPanel,
  };
  const view = render(
    <ChangedLately {...props} insight={discovery} onInsight={onInsight} />,
  );
  const open = screen.getByRole("button", {
    name: `Open insight · Discovery: ${discovery.title}`,
  });
  expect(open).toHaveTextContent("Discovery");
  expect(open).toHaveTextContent(discovery.summary);
  await user.click(open);
  expect(onInsight).toHaveBeenCalled();
  await user.click(screen.getByRole("button", { name: "Open Changed lately" }));
  expect(onPanel).toHaveBeenCalled();
  // Without one, the corner shows recent changes as before.
  view.rerender(<ChangedLately {...props} insight={null} />);
  expect(
    screen.queryByRole("button", { name: /^Open insight/ }),
  ).not.toBeInTheDocument();
  expect(screen.getByText("Knowledge is taking shape")).toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: /Earlier insights/ }),
  ).not.toBeInTheDocument();
  // Earlier insights stay a quiet tap away, with or without changes to show.
  const onEarlier = vi.fn();
  view.rerender(
    <ChangedLately {...props} insight={null} onEarlier={onEarlier} />,
  );
  await user.click(screen.getByRole("button", { name: /Earlier insights/ }));
  const memory = record("m");
  memory.type = "memory";
  view.rerender(
    <ChangedLately
      {...props}
      nodes={[memory]}
      insight={null}
      onEarlier={onEarlier}
    />,
  );
  expect(
    screen.getByRole("button", { name: /Explore memory/ }),
  ).toBeInTheDocument();
  await user.click(screen.getByRole("button", { name: /Earlier insights/ }));
  expect(onEarlier).toHaveBeenCalledTimes(2);
});

it("holds live insights in memory: none is quiet, a failed save keeps the old state, a failed read keeps the last", async () => {
  const discovery = { ...demoInsights()[0], id: "live-1" };
  const insights = vi.fn<NonNullable<GraphSession["insights"]>>();
  const insightFeedback = vi.fn<NonNullable<GraphSession["insightFeedback"]>>();
  const session = { ...createApiSession(false), insights, insightFeedback };
  // No insights stored yet: nothing to show, and nothing breaks.
  insights.mockResolvedValueOnce({ insights: [], runs: [] });
  const { result } = renderHook(() => useInsights(session));
  await waitFor(() => expect(insights).toHaveBeenCalledTimes(1));
  expect(result.current.insights).toEqual([]);
  expect(result.current.current).toBeNull();
  insights.mockResolvedValueOnce({ insights: [discovery], runs: [] });
  await act(() => result.current.reload());
  expect(result.current.current?.id).toBe("live-1");
  // The engine refuses the feedback (an unknown id, say): the insight stays as
  // it was, and the panel is told quietly.
  insightFeedback.mockRejectedValueOnce(new Error("404"));
  await act(() => result.current.send("live-1", "not_useful"));
  expect(insightFeedback).toHaveBeenCalledWith(
    "live-1",
    "not_useful",
    undefined,
    undefined,
  );
  expect(result.current.failed).toEqual({ "live-1": "not_useful" });
  expect(result.current.pending).toEqual({});
  expect(result.current.current?.status).toBe("new");
  // Saved: the insight takes the engine's answer and steps out of the corner.
  insightFeedback.mockResolvedValueOnce(applyFeedback(discovery, "not_useful"));
  await act(() => result.current.send("live-1", "not_useful"));
  expect(result.current.failed).toEqual({});
  expect(result.current.insights[0].status).toBe("dismissed");
  expect(result.current.current).toBeNull();
  // A read that fails (the app's server restarting, say) keeps what was read last.
  insights.mockRejectedValueOnce(new Error("Source unavailable"));
  await act(() => result.current.reload());
  expect(result.current.insights.map((i) => i.id)).toEqual(["live-1"]);
});

// Local times, as the run line shows them.
const local = (day: number, hour: number, minute = 0) =>
  new Date(2026, 9, day, hour, minute).toISOString();
const run = (
  outcome: InsightRun["outcome"],
  at: string,
  note: string | null = null,
): InsightRun => ({
  id: `run-${outcome}`,
  generatedBy: "harness-daily",
  outcome,
  note,
  insightId: null,
  at,
});

it("says how the engine's last run went, in local time", () => {
  const today = new Date(2026, 9, 28, 15).getTime();
  expect(runLine([], today)).toBeNull();
  expect(runLine([run("quiet", local(28, 6, 2))], today)).toBe(
    "Last run 6:02 AM · quiet day",
  );
  expect(runLine([run("saved", local(28, 6, 2))], today)).toBe(
    "Last run 6:02 AM · new insight",
  );
  // The newest run is the one shown; an older day says which.
  expect(
    runLine(
      [run("saved", local(27, 6, 5)), run("quiet", local(26, 6, 1))],
      today,
    ),
  ).toBe("Last run yesterday, 6:05 AM · new insight");
  // Blocked: the first 80 characters of its note, on one line.
  const note = `Drive  was\noffline ${"x".repeat(100)}`;
  const blocked = runLine([run("blocked", local(28, 6, 2), note)], today)!;
  expect(blocked).toMatch(
    /^Last run 6:02 AM · blocked: Drive was offline x+…$/,
  );
  expect(blocked.split("blocked: ")[1].length).toBe(81);
  expect(runLine([run("blocked", local(28, 6, 2))], today)).toBe(
    "Last run 6:02 AM · blocked",
  );
});

it("reads runs failing closed: a bad list is none, a bad run is left out", () => {
  const good = run("quiet", hours(30)),
    newer = run("saved", hours(2));
  expect(cleanRuns({ runs: [good, newer] })).toEqual([newer, good]);
  expect(cleanRuns({ runs: [good, { ...newer, outcome: "great" }] })).toEqual([
    good,
  ]);
  expect(cleanRuns({ runs: [{ ...good, note: "x".repeat(501) }] })).toEqual([]);
  for (const bad of [
    null,
    {},
    { runs: "many" },
    { runs: Array(51).fill(good) },
  ])
    expect(cleanRuns(bad)).toEqual([]);
});

it("treats handled like dismissed, and checks feedback notes", () => {
  const handled = applyFeedback(insight("h", 1), "handled", {
    note: "  Linked them.  ",
  });
  expect(handled.status).toBe("handled");
  expect(handled.feedback?.note).toBe("Linked them.");
  expect(statusName(handled, now)).toBe("Handled");
  expect(currentInsight([handled], now)).toBeNull();
  expect(cleanInsight(handled)).toEqual(handled);
  expect(
    feedbackInput({ id: "a-1", value: "handled", note: "  Done.  " }, now),
  ).toEqual({ id: "a-1", value: "handled", note: "Done." });
  // The engine takes handled without a note; the panel always asks for one.
  expect(feedbackInput({ id: "a-1", value: "handled" }, now)).toEqual({
    id: "a-1",
    value: "handled",
  });
  expect(
    feedbackInput({ id: "a-1", value: "explore", note: "x".repeat(500) }, now)
      ?.note,
  ).toHaveLength(500);
  for (const note of ["", "   ", "x".repeat(501), 7])
    expect(
      feedbackInput({ id: "a-1", value: "handled", note }, now),
    ).toBeNull();
});

it("asks what happened before marking an insight handled, and lets any choice carry a note", async () => {
  const user = userEvent.setup();
  const { list, view, onFeedback } = panel();
  const handled = screen.getByRole("button", { name: "Already handled" });
  expect(handled).toHaveAttribute("aria-expanded", "false");
  await user.click(handled);
  expect(handled).toHaveAttribute("aria-expanded", "true");
  const field = screen.getByRole("textbox", { name: "What happened?" });
  expect(field).toBeRequired();
  expect(field).toHaveAttribute("maxlength", "500");
  const mark = screen.getByRole("button", { name: "Mark handled" });
  expect(mark).toBeDisabled();
  await user.type(field, "   ");
  expect(mark).toBeDisabled();
  await user.type(field, "Linked them last night.{Enter}");
  expect(onFeedback).toHaveBeenLastCalledWith(
    "handled",
    undefined,
    "Linked them last night.",
  );
  expect(
    screen.queryByRole("textbox", { name: "What happened?" }),
  ).not.toBeInTheDocument();
  // Once saved, it shows where it stands and what happened.
  view.rerender(
    <InsightPanel
      insight={applyFeedback(list[0], "handled", {
        note: "Linked them last night.",
      })}
      insights={list}
      nodes={[]}
      demo
      onFeedback={onFeedback}
      onTrail={vi.fn()}
      onStar={vi.fn()}
      onOpenInsight={vi.fn()}
    />,
  );
  expect(screen.getByRole("status")).toHaveTextContent(
    "Marked as handled. It won't come back.",
  );
  expect(screen.getByText("“Linked them last night.”")).toBeInTheDocument();
  expect(
    screen.getByRole("button", { name: "Already handled" }),
  ).toHaveAttribute("aria-pressed", "true");
  // Any other choice can take a note, folded away until asked for.
  expect(
    screen.queryByRole("textbox", { name: "Note" }),
  ).not.toBeInTheDocument();
  await user.click(screen.getByRole("button", { name: "Add a note" }));
  await user.type(
    screen.getByRole("textbox", { name: "Note" }),
    "Worth a second look",
  );
  await user.click(screen.getByRole("button", { name: "Interesting" }));
  expect(onFeedback).toHaveBeenLastCalledWith(
    "interesting",
    undefined,
    "Worth a second look",
  );
  expect(
    screen.queryByRole("textbox", { name: "Note" }),
  ).not.toBeInTheDocument();
});

it("notes the last run in the panel's footer and with the history, and nothing without runs", () => {
  const { view, list } = panel({
    runs: [run("saved", new Date().toISOString())],
  });
  expect(
    screen.getByText(/A fictional sample insight/).closest("p"),
  ).toHaveTextContent(/Last run .* · new insight$/);
  const props = {
    insights: list,
    nodes: [],
    demo: true,
    onFeedback: vi.fn(),
    onTrail: vi.fn(),
    onStar: vi.fn(),
    onOpenInsight: vi.fn(),
  };
  // With no insight open, it heads the history.
  view.rerender(
    <InsightPanel
      {...props}
      insight={null}
      runs={[run("quiet", new Date().toISOString())]}
    />,
  );
  const line = screen.getByText(/^Last run .* · quiet day$/);
  expect(line.nextElementSibling).toBe(
    screen.getByRole("region", { name: "Earlier insights" }),
  );
  view.rerender(<InsightPanel {...props} insight={list[0]} runs={[]} />);
  expect(screen.queryByText(/Last run/)).not.toBeInTheDocument();
});

it("notes the last run in the corner when no insight is current", () => {
  const props = {
    nodes: [],
    edges: [],
    sources: [],
    paused: true,
    onOpen: vi.fn(),
    onSources: vi.fn(),
  };
  const view = render(
    <ChangedLately {...props} lastRun="Last run 6:02 AM · quiet day" />,
  );
  expect(screen.getByText("Last run 6:02 AM · quiet day")).toBeInTheDocument();
  view.rerender(<ChangedLately {...props} />);
  expect(screen.queryByText(/Last run/)).not.toBeInTheDocument();
});

it("gives sideways phones the slim insight card at the head of the sky's column", () => {
  const css = readFileSync("src/InsightCard.css", "utf8").replace(/\r/g, "");
  const landscape = "(max-height: 500px) and (orientation: landscape)";
  // The card shows in both phone layouts...
  const shared = css.indexOf(
    `@media (max-width: 760px) and (min-height: 501px),\n  ${landscape} {\n  .insight-card {`,
  );
  expect(shared).toBeGreaterThan(-1);
  expect(css.slice(shared, css.indexOf("}", shared))).toContain(
    "display: flex",
  );
  // ...and sideways it heads the sky's column, beside the main slot, so the
  // readings' column keeps its whole height for the readings and the cards.
  const grid = css.slice(css.indexOf(`@media ${landscape} {`));
  expect(grid).toMatch(
    /\.observatory:has\(> \.insight-card\) \{[^}]*"main insight"\s*"main sky"\s*"recovery sky"\s*"training sky";/,
  );
});
