// Off-map links where useful (SB-P4-13): with the engine's graph.pluginLinks on, a plugin's own link between two of its
// records joins their stars as that plugin's suggestion, its evidence naming the plugin (ingest:<plugin>). The explorer
// draws it dashed, names it automatic, says which plugin suggested it in Discover and in its evidence, and lets a person
// dismiss it as any suggestion. Every other link reads as before. At a phone's size and at a desktop's. Fictional data.
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import Explorer from "../src/Explorer";
import { emptyGraph } from "../src/liveGraph";
import { autoLinked, pluginLinked } from "../src/graphView";
import type { KnowledgeController } from "../src/useKnowledge";
import type { GraphEdge, GraphNode, GraphPage } from "../src/knowledge";
import { record, relation } from "./synthetic";

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

function plan(external: string, title: string): GraphNode {
  const n = record(`alex:${external}`, "rhythm", "cgm");
  n.type = "document";
  n.title = title;
  n.provenance.demo = false;
  return n;
}
function pluginLink(
  a: GraphNode,
  b: GraphNode,
  verification = "suggested",
): GraphEdge {
  return {
    ...relation(a, b),
    id: `relationship:${"7".repeat(64)}`,
    type: "follows",
    directed: true,
    verification,
    relationship: "follows",
    evidence: [
      {
        resourceId: a.id,
        excerpt: "Linked by the cgm plugin",
        locator: "ingest:cgm",
        retrievedAt: a.resource.retrievedAt,
      },
    ],
  };
}
function sky() {
  const march = plan("rota-a", "DEMO: Brenlow rota for March");
  const february = plan("rota-b", "DEMO: Brenlow rota for February");
  const link = pluginLink(march, february);
  const page: GraphPage = {
    ...emptyGraph(),
    nodes: [march, february],
    edges: [link],
  };
  return { page, march, february, link };
}
const controller = (page: GraphPage): KnowledgeController => ({
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
  },
});
function show(page: GraphPage, selected: string | null = null) {
  return render(
    <Explorer
      knowledge={controller(page)}
      selected={selected}
      onSelect={vi.fn()}
      close={vi.fn()}
      paused
      state="ready"
    />,
  );
}

it("knows a plugin's own link by its evidence, and only that", () => {
  const s = sky();
  expect(pluginLinked(s.link)).toBe("cgm");
  expect(autoLinked(s.link)).toBe(true);
  // Confirmed, it is no automatic link; another source's link names no plugin.
  expect(autoLinked(pluginLink(s.march, s.february, "verified"))).toBe(false);
  const other = relation(s.march, s.february);
  expect(pluginLinked(other)).toBeNull();
  expect(
    pluginLinked({
      ...other,
      evidence: [{ ...other.evidence[0], locator: "ingest:Not A Plugin" }],
    }),
  ).toBeNull();
  expect(
    pluginLinked({
      ...other,
      evidence: [{ ...other.evidence[0], locator: "second-brain:mem-1" }],
    }),
  ).toBeNull();
});

describe.each(["phone", "desktop"] as const)("at a %s's size", (size) => {
  it("draws a plugin's link dashed, says which plugin suggested it, and dismisses it as any suggestion", async () => {
    at(size);
    const s = sky();
    const user = userEvent.setup();
    const { container } = show(s.page, s.march.id);
    const drawn = () =>
      container.querySelector(
        `path.graph-edge[data-source="${s.march.id}"][data-target="${s.february.id}"]`,
      );
    expect(drawn()).not.toBeNull();
    // In the star's panel: an automatic link.
    const panel = screen.getByRole("complementary", {
      name: "Selected knowledge details",
    });
    expect(
      await within(
        within(panel).getByRole("list", { name: "Connected items" }),
      ).findByRole("button", {
        name: `Why connected: ${s.february.title} (follows · auto)`,
      }),
    ).toBeInTheDocument();
    // In Discover: the plugin named, and its own words in the evidence.
    await user.click(
      screen.getByRole("button", { name: "Discover", exact: true }),
    );
    const card = screen
      .getAllByRole("article")
      .find((x) => x.textContent?.includes(s.march.title))!;
    expect(card).toHaveTextContent("Suggested by the cgm plugin");
    await user.click(
      within(card).getByRole("button", { name: /Inspect evidence/ }),
    );
    expect(
      screen.getByText(
        /The cgm plugin linked these records on its own; nobody confirmed the link\./,
      ),
    ).toBeInTheDocument();
    expect(screen.getByText("ingest:cgm")).toBeInTheDocument();
    await user.click(
      screen.getByRole("button", { name: "Back to connections" }),
    );
    await user.click(
      within(
        screen
          .getAllByRole("article")
          .find((x) => x.textContent?.includes(s.march.title))!,
      ).getByRole("button", { name: "Dismiss" }),
    );
    expect(drawn()).toBeNull();
  });
});
