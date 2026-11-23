import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import Explorer from "../src/Explorer";
import { emptyGraph } from "../src/liveGraph";
import type { GraphSession } from "../src/liveGraph";
import type { RecallAnswer } from "../src/recall";
import { record, relation } from "./synthetic";
import type { KnowledgeController } from "../src/useKnowledge";
import type { GraphNode, GraphPage } from "../src/knowledge";

// SB-P2-10: the explorer's own search, with the engine's combined search beside it. One
// continuous panel: the stars found by words and meaning come first, each saying why, lit in the
// sky as the title matches are; without it (demo mode, engine.recall off, an engine without it)
// the search is exactly the title search it was.
const real = (n: GraphNode) => {
  n.provenance.demo = false;
  return n;
};
function sky() {
  const boiler = real(record("fictional-boiler", "drive"));
  boiler.type = "document";
  boiler.title = "Fictional boiler service";
  const quote = real(record("fictional-quote", "drive"));
  quote.type = "document";
  quote.title = "Fictional heating quote";
  const plan = real(record("fictional-plan"));
  plan.title = "Fictional plan to keep warm";
  const alone = real(record("fictional-alone"));
  alone.title = "Fictional unlinked memory";
  const page = {
    ...emptyGraph(),
    nodes: [boiler, quote, plan, alone],
    edges: [relation(quote, boiler)],
  };
  return { page, boiler, quote, plan, alone };
}
const question = "warm";
const found = (s: ReturnType<typeof sky>): RecallAnswer => ({
  available: true,
  page: { ...emptyGraph(), nodes: [s.quote, s.boiler], cursors: {} },
  recall: {
    query: question,
    results: [
      {
        id: s.quote.id,
        score: 0.0325,
        excerpt: "DEMO: the heating quote keeps the house warm",
        truncated: true,
        why: {
          rank: 1,
          words: { rank: 1, terms: ["warm"], exact: 0 },
          meaning: { rank: 1, similarity: 0.61 },
          imputed: false,
        },
      },
      {
        id: s.boiler.id,
        score: 0.0161,
        excerpt: "",
        truncated: false,
        why: {
          rank: 2,
          words: null,
          meaning: { rank: 2, similarity: 0.52 },
          imputed: false,
        },
      },
    ],
    notices: [],
    semanticSkipped: false,
    offMap: 2,
    receipt: "rc1-0123456789abcdef",
  },
});
function show(page: GraphPage, recall?: GraphSession["recall"], demo = false) {
  const accept = vi.fn();
  const onSelect = vi.fn();
  const session: GraphSession = {
    initial: page,
    refresh: vi.fn(async () => page),
    retrieve: vi.fn(async () => ({ ...page, cursors: {} })),
    ...(recall ? { recall } : {}),
    ...(demo ? { demo: { advance: vi.fn(), setUnavailable: vi.fn() } } : {}),
  };
  const knowledge: KnowledgeController = {
    page,
    accept,
    pins: [],
    togglePin: vi.fn(),
    busy: false,
    error: null,
    refresh: vi.fn(async () => {}),
    discoverMore: vi.fn(async () => {}),
    lastRefresh: null,
    epoch: 0,
    session,
  };
  const view = render(
    <Explorer
      knowledge={knowledge}
      selected={null}
      onSelect={onSelect}
      close={vi.fn()}
      paused
      state="ready"
    />,
  );
  return { ...view, accept, onSelect, session };
}
const panel = () =>
  screen.getByRole("complementary", { name: "Selected knowledge details" });
const lit = (container: HTMLElement) =>
  [...container.querySelectorAll("g.graph-node.related-node")]
    .map((g) => g.getAttribute("data-id"))
    .sort();
async function search(text: string) {
  const user = userEvent.setup();
  await user.type(
    screen.getByRole("textbox", { name: "Search knowledge" }),
    text,
  );
  await user.click(
    within(panel()).getByRole("button", { name: /Search (authorized|sample)/ }),
  );
  return user;
}

it("lists the stars found by words and meaning first, each saying why, and lights them", async () => {
  const s = sky();
  const recall = vi.fn(async () => found(s));
  const { container, accept, onSelect } = show(s.page, recall);
  const user = await search(question);
  expect(recall).toHaveBeenCalledWith(question, expect.any(AbortSignal));
  const list = await within(panel()).findByRole("list", {
    name: "Found by words and meaning",
  });
  const rows = within(list).getAllByRole("listitem");
  expect(rows.map((r) => r.querySelector(".row-title")!.textContent)).toEqual([
    "Fictional heating quote",
    "Fictional boiler service",
  ]);
  expect(rows[0]).toHaveTextContent("By 1 of your words and its meaning");
  expect(rows[0]).toHaveTextContent(
    "DEMO: the heating quote keeps the house warm…",
  );
  expect(rows[1]).toHaveTextContent("By its meaning");
  expect(panel()).toHaveTextContent(
    "2 more found have no star on the map yet.",
  );
  // Its stars joined the sky, kept past the map's bound, and are lit with the title match.
  const answer = found(s) as Extract<RecallAnswer, { page: unknown }>;
  expect(accept).toHaveBeenLastCalledWith(answer.page, [
    s.quote.id,
    s.boiler.id,
  ]);
  expect(lit(container)).toEqual([s.quote.id, s.boiler.id, s.plan.id].sort());
  // The title matches follow, as they always were.
  expect(
    within(
      within(panel()).getByRole("list", { name: "Matching items" }),
    ).getAllByRole("listitem"),
  ).toHaveLength(1);
  expect(panel()).toHaveTextContent("BY TITLE");
  // A result opens like any star; another word puts the panel back to its title matches.
  await user.click(within(rows[1]).getByRole("button"));
  expect(onSelect).toHaveBeenCalledWith(s.boiler.id);
});

it("counts the stars it found when no title matches, and leaves with another word", async () => {
  const s = sky();
  const { container } = show(s.page, async () => found(s));
  const user = await search("what keeps the house cosy");
  expect(
    await within(panel()).findByRole("heading", {
      name: "2 found by words and meaning",
    }),
  ).toBeInTheDocument();
  expect(panel()).not.toHaveTextContent("Try another word");
  await user.type(
    screen.getByRole("textbox", { name: "Search knowledge" }),
    "?",
  );
  expect(
    within(panel()).queryByRole("list", { name: "Found by words and meaning" }),
  ).not.toBeInTheDocument();
  expect(
    within(panel()).getByRole("heading", {
      name: "No stars match your search",
    }),
  ).toBeInTheDocument();
  expect(lit(container)).toEqual([]);
});

it("is the title search it always was when the engine has no combined search, and asks once", async () => {
  const s = sky();
  const before = show(s.page);
  await search(question);
  await within(panel()).findByRole("button", {
    name: "Search authorized graph",
  });
  const plain = panel().textContent;
  const plainLit = lit(before.container);
  before.unmount();

  const recall = vi.fn(async (): Promise<RecallAnswer> => ({
    available: false,
  }));
  const { container } = show(s.page, recall);
  await search(question);
  await vi.waitFor(() => expect(recall).toHaveBeenCalledTimes(1));
  await within(panel()).findByRole("button", {
    name: "Search authorized graph",
  });
  expect(panel().textContent).toBe(plain);
  expect(lit(container)).toEqual(plainLit);
  await userEvent
    .setup()
    .click(within(panel()).getByRole("button", { name: /Search authorized/ }));
  expect(recall).toHaveBeenCalledTimes(1);
});

it("is the title search it always was when the engine's listing could not be read, and asks again", async () => {
  const s = sky();
  const before = show(s.page);
  await search(question);
  await within(panel()).findByRole("button", {
    name: "Search authorized graph",
  });
  const plain = panel().textContent;
  const plainLit = lit(before.container);
  before.unmount();

  // An install with engine.recall off whose listing the facade could not read: nothing shown,
  // not "unavailable right now", and asked again on the next search.
  const recall = vi.fn(
    async (): Promise<RecallAnswer> => ({ available: false, retry: true }),
  );
  const { container } = show(s.page, recall);
  const user = await search(question);
  await vi.waitFor(() => expect(recall).toHaveBeenCalledTimes(1));
  await within(panel()).findByRole("button", {
    name: "Search authorized graph",
  });
  expect(panel().textContent).toBe(plain);
  expect(lit(container)).toEqual(plainLit);
  await user.click(
    within(panel()).getByRole("button", { name: /Search authorized/ }),
  );
  await vi.waitFor(() => expect(recall).toHaveBeenCalledTimes(2));
  await within(panel()).findByRole("button", {
    name: "Search authorized graph",
  });
  expect(panel().textContent).toBe(plain);
});

it("says a search it could not answer was unavailable once this session has seen it on", async () => {
  const s = sky();
  const answers: RecallAnswer[] = [found(s), { available: false, retry: true }];
  const recall = vi.fn(async () => answers.shift()!);
  show(s.page, recall);
  const user = await search(question);
  await within(panel()).findByRole("list", {
    name: "Found by words and meaning",
  });
  await user.click(
    within(panel()).getByRole("button", { name: /Search authorized/ }),
  );
  expect(
    await within(panel()).findByText(
      /Search by words and meaning is unavailable right now/,
    ),
  ).toBeInTheDocument();
  expect(recall).toHaveBeenCalledTimes(2);
});

it("says when the combined search failed and keeps the title matches", async () => {
  const s = sky();
  show(s.page, async () => ({ available: true, failed: true }));
  await search(question);
  expect(
    await within(panel()).findByText(
      /Search by words and meaning is unavailable right now/,
    ),
  ).toBeInTheDocument();
  expect(
    within(panel()).getByRole("list", { name: "Matching items" }),
  ).toBeInTheDocument();
});

it("shows what the engine said, such as meaning skipped, beside its keyword results", async () => {
  const s = sky();
  const answer = found(s) as Extract<RecallAnswer, { page: unknown }>;
  answer.recall.notices = [
    "Semantic search was skipped: the model is off. These are keyword results.",
  ];
  answer.recall.semanticSkipped = true;
  answer.recall.results = [
    {
      ...answer.recall.results[0],
      why: { ...answer.recall.results[0].why, meaning: null },
    },
  ];
  show(s.page, async () => answer);
  await search(question);
  expect(
    await within(panel()).findByText(/Semantic search was skipped/),
  ).toBeInTheDocument();
  expect(panel()).toHaveTextContent("By 1 of your words");
  expect(panel()).not.toHaveTextContent("its meaning");
});
