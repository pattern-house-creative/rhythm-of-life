import { identityKey } from "./knowledge.js";
import type { SourceId } from "./model.js";
import { applyFeedback } from "./insights.js";
import type { DailyInsight, FeedbackValue, InsightRun } from "./insights.js";

// Three fictional insights for the demo, one of each kind, drawn from sample
// stars the demo sky loads on its first refresh. Their times follow the
// clock, so the newest is always today's. Feedback changes them in memory only.
const star = (provider: SourceId, id: string) =>
  identityKey({
    provider,
    account: "fictional-household",
    externalId: `demo:${id}`,
  });
export const demoStars = {
  ritual: star("second-brain", "ritual"),
  library: star("drive", "library"),
  readingDecision: star("second-brain", "reading-decision"),
  rhythm: star("second-brain", "rhythm"),
  run: star("demo", "run"),
  base: star("drive", "base"),
  mobility: star("drive", "mobility"),
  bookNotes: star("drive", "book-notes"),
  review: star("second-brain", "review"),
  decisions: star("drive", "decisions"),
  focus: star("drive", "focus"),
  craft: star("drive", "craft"),
};

export function demoInsights(now = Date.now()): DailyInsight[] {
  const ago = (hours: number) =>
    new Date(
      Math.floor((now - hours * 3_600_000) / 60_000) * 60_000,
    ).toISOString();
  const s = demoStars;
  const graph = (title: string, id: string, hours: number) => ({
    kind: "graph" as const,
    title,
    locator: id,
    retrievedAt: ago(hours),
  });
  return [
    {
      id: "demo-insight-discovery",
      kind: "discovery",
      title: "Your evening ritual and the home library keep meeting",
      summary:
        "Three places in this sample sky describe the same quiet hour before bed: the Evening ritual memory, the Home library and a decision to read fewer books but connect more. None of them link to each other directly.",
      whyItMatters:
        "One habit is spread across three sources. Seen together, it is easier to protect that hour, and to notice when it slips.",
      nextStep:
        "Link the Evening ritual memory to the Home library, and note tonight's book there.",
      points: [
        {
          type: "observed",
          text: "The Evening ritual memory describes a quiet walk and reading before bed.",
          source: 0,
        },
        {
          type: "observed",
          text: "The Home library collects the books tied to the reading garden.",
          source: 1,
        },
        {
          type: "observed",
          text: "A sample decision says to connect each finished book to one current project.",
          source: 3,
        },
        {
          type: "sourced",
          text: "A fictional article on winding down says one steady cue helps an evening habit stick.",
          source: 2,
        },
        {
          type: "inferred",
          text: "The ritual and the library are probably the same habit, seen from two sides.",
        },
        {
          type: "hypothesis",
          text: "Pairing the evening book with a current project might help the habit hold.",
        },
      ],
      evidenceLevel: "moderate",
      limitations:
        "Sample records only. The ritual memory has no dates, so how often the habit happens can't be checked.",
      stars: [
        { graphId: s.ritual, title: "Evening ritual" },
        { graphId: s.library, title: "Home library" },
        { graphId: s.readingDecision, title: "Read fewer, connect more" },
        { graphId: s.rhythm, title: "Daily rhythms" },
      ],
      sources: [
        {
          kind: "second-brain",
          title: "Evening ritual",
          locator: s.ritual,
          retrievedAt: ago(3.2),
        },
        graph("Home library", s.library, 3.2),
        {
          kind: "web",
          title: "A gentle cue for winding down (fictional article)",
          url: "https://example.org/fictional/winding-down",
          publishedAt: ago(24 * 26),
          retrievedAt: ago(3.1),
        },
        graph("Read fewer, connect more", s.readingDecision, 3.2),
      ],
      generatedBy: "harness-daily",
      generatedAt: ago(3),
      sourcesAsOf: ago(3.2),
      supersedes: null,
      status: "new",
      feedback: null,
      createdAt: ago(3),
      updatedAt: ago(3),
    },
    {
      id: "demo-insight-focus",
      kind: "focus",
      title: "Let Sunday's long run anchor the week",
      summary:
        "The sample training week turns on one session, the Sunday long run. Building a base and Mobility practice both arrange themselves around it.",
      whyItMatters:
        "With one fixed point, the other sessions are easier to place, and a missed weekday matters less.",
      nextStep:
        "Put this week's mobility session on Tuesday evening and keep Sunday morning clear.",
      points: [
        {
          type: "observed",
          text: "The sample Sunday long run was 8.4 km over 52 minutes, on the River trail.",
          source: 0,
        },
        {
          type: "observed",
          text: "Building a base asks for three sessions a week for six weeks.",
          source: 1,
        },
        {
          type: "inferred",
          text: "Sunday is the session the others are planned around.",
        },
        {
          type: "hypothesis",
          text: "A fixed weekday for mobility may make the six weeks easier to keep, as the practice notes suggest.",
          source: 3,
        },
      ],
      evidenceLevel: "strong",
      limitations:
        "One sample week. Weather, travel and rest days aren't in these records.",
      stars: [
        { graphId: s.run, title: "Sunday long run" },
        { graphId: s.base, title: "Building a base" },
        { graphId: s.mobility, title: "Mobility practice" },
        { graphId: s.bookNotes, title: "Notes on deliberate practice" },
      ],
      sources: [
        graph("Sunday long run", s.run, 27.5),
        graph("Building a base", s.base, 27.5),
        graph("Mobility practice", s.mobility, 27.5),
        graph("Notes on deliberate practice", s.bookNotes, 27.5),
      ],
      generatedBy: "harness-daily",
      generatedAt: ago(27),
      sourcesAsOf: ago(27.5),
      supersedes: null,
      status: "saved",
      feedback: { value: "interesting", at: ago(26), until: null, note: null },
      createdAt: ago(27),
      updatedAt: ago(26),
    },
    {
      id: "demo-insight-idea",
      kind: "suggestion",
      title: "Fold the weekly review into the decision journal",
      summary:
        "The sample Weekly review and Decision journal ask the same questions: what changed, and why. One short entry each Friday could serve both.",
      whyItMatters:
        "One place to write makes the review more likely to happen, and decisions keep their reasons.",
      nextStep:
        "Draft plan: each Friday, write one line on what changed and one on what you decided. File it in the Decision journal and link it to the Weekly review.",
      points: [
        {
          type: "observed",
          text: "The Weekly review memory describes a Friday reflection.",
          source: 0,
        },
        {
          type: "observed",
          text: "The Decision journal records why small, reversible experiments were chosen.",
          source: 1,
        },
        {
          type: "sourced",
          text: "A fictional essay on journaling says short entries are kept up for longer.",
          source: 2,
        },
        {
          type: "inferred",
          text: "The two practices answer the same question in different words.",
        },
      ],
      evidenceLevel: "weak",
      limitations:
        "Two sample records and one fictional essay. It can't tell how often either practice is used.",
      stars: [
        { graphId: s.review, title: "Weekly review" },
        { graphId: s.decisions, title: "Decision journal" },
        { graphId: s.focus, title: "Deep work" },
        { graphId: s.craft, title: "Learning in public" },
      ],
      sources: [
        {
          kind: "second-brain",
          title: "Weekly review",
          locator: s.review,
          retrievedAt: ago(24 * 5 + 1),
        },
        graph("Decision journal", s.decisions, 24 * 5 + 1),
        {
          kind: "web",
          title: "Shorter entries, longer habits (fictional essay)",
          url: "https://example.org/fictional/short-entries",
          publishedAt: ago(24 * 60),
          retrievedAt: ago(24 * 5 + 1),
        },
      ],
      generatedBy: "harness-weekly",
      generatedAt: ago(24 * 5),
      sourcesAsOf: ago(24 * 5 + 1),
      supersedes: null,
      status: "explored",
      feedback: { value: "explore", at: ago(24 * 4), until: null, note: null },
      createdAt: ago(24 * 5),
      updatedAt: ago(24 * 4),
    },
  ];
}

// Two fictional runs: this morning's saved the discovery; two days ago was
// quiet.
export function demoRuns(now = Date.now()): InsightRun[] {
  const ago = (hours: number) =>
    new Date(
      Math.floor((now - hours * 3_600_000) / 60_000) * 60_000,
    ).toISOString();
  return [
    {
      id: "demo-run-saved",
      generatedBy: "harness-daily",
      outcome: "saved",
      note: null,
      insightId: "demo-insight-discovery",
      at: ago(3),
    },
    {
      id: "demo-run-quiet",
      generatedBy: "harness-daily",
      outcome: "quiet",
      note: "Nothing new across the sample sources.",
      insightId: null,
      at: ago(51),
    },
  ];
}

// The demo session's insights: read and changed in memory, never sent.
export function createDemoInsights(now = Date.now()) {
  let list = demoInsights(now);
  const runs = demoRuns(now);
  return {
    async list() {
      return structuredClone({ insights: list, runs });
    },
    async feedback(
      id: string,
      value: FeedbackValue,
      until?: string,
      note?: string,
    ) {
      const insight = list.find((i) => i.id === id);
      if (!insight) throw new Error("Unknown insight");
      const next = applyFeedback(insight, value, { until, note });
      list = list.map((i) => (i.id === id ? next : i));
      return structuredClone(next);
    },
  };
}
