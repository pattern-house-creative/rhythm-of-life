import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { cleanInsights, sourceStar } from "../src/insights";
import type { GraphNode } from "../src/knowledge";
import { InsightPanel } from "../src/panels/InsightPanel";

// SB-P5-07 (Q-076): the app shows an insight whatever kind of source it cites, as the engine
// accepts them: a star, a record the engine keeps, a document, a memory, a web page, and the
// older nas, drive and second-brain. One contract fixture per kind (contracts/fixtures).
const read = (path: string) =>
  JSON.parse(readFileSync(resolve(process.cwd(), "..", path), "utf8"));
const fixtureOf = (kind: string) =>
  read(
    `contracts/fixtures/app_insights${kind === "graph" ? "" : `.${kind}`}.example.json`,
  );
const kinds: string[] = read("contracts/mcp/app_insights.output.schema.json")
  .$defs.insight.properties.sources.items.properties.kind.enum;

// A star as the engine pages it: the catalog's own (provider rhythm, the subject in its
// external id), a native memory's, a Second Brain memory's; the document is the contract's own
// opened document star.
function star(
  provider: string,
  account: string,
  externalId: string,
  owner: string,
  type: GraphNode["type"],
): GraphNode {
  const id = [provider, account, externalId].map(encodeURIComponent).join(":");
  return {
    id,
    title: `Fictional ${type} ${externalId}`,
    type,
    overview: "",
    provenance: {
      sourceId: provider,
      recordId: externalId,
      observedAt: "2026-10-28T06:00:00+00:00",
      demo: false,
    },
    resource: {
      identity: { provider, account, externalId },
      canonicalUrl: null,
      sourceUrl: null,
      sourceModifiedAt: null,
      retrievedAt: "2026-10-28T06:00:00+00:00",
      revision: null,
      owner,
      access: { scope: "private", canRead: true, canOpen: false },
      availability: "available",
      content: null,
    },
  };
}
const document = read("contracts/fixtures/app_graph.content.example.json")
  .nodes[0] as GraphNode;
const stars: GraphNode[] = [
  star("rhythm", "data:csv", "alex:body", "alex", "record"),
  document,
  star("memory", "memory", "6d3c2a10-0000-4000-8000-00000000a001", "alex", "memory"),
  star("second-brain", "second-brain", "sb-fictional-0007", "household", "memory"),
];
const names: Record<string, string> = {
  record: "Rhythm record",
  document: "Document",
  memory: "Memory",
  nas: "Rhythm engine",
  drive: "Google Drive",
  "second-brain": "Second Brain",
};

it("has a fixture for every source kind the contract names, and keeps each insight", () => {
  expect([...kinds].sort()).toEqual(
    [
      "document",
      "drive",
      "graph",
      "memory",
      "nas",
      "record",
      "second-brain",
      "web",
    ].sort(),
  );
  for (const kind of kinds) {
    const insights = cleanInsights(fixtureOf(kind));
    expect(insights, kind).toHaveLength(1);
    expect(insights[0].sources.map((s) => s.kind)).toContain(kind);
  }
});

it.each(["record", "document", "memory", "nas", "second-brain"])(
  "shows an insight citing a %s and opens its star",
  async (kind) => {
    const [insight] = cleanInsights(fixtureOf(kind));
    const onStar = vi.fn();
    render(
      <InsightPanel
        insight={insight}
        insights={[insight]}
        nodes={stars}
        onFeedback={vi.fn()}
        onTrail={vi.fn()}
        onStar={onStar}
        onOpenInsight={vi.fn()}
      />,
    );
    expect(
      screen.getByRole("heading", { level: 2, name: insight.title }),
    ).toBeInTheDocument();
    const sources = screen.getByRole("heading", { name: "Sources" })
      .parentElement!;
    const source = insight.sources[0];
    expect(within(sources).getByText(new RegExp(`^${names[kind]}`))).toBeInTheDocument();
    expect(within(sources).queryByText(/not in the loaded sky/)).toBeNull();
    await userEvent
      .setup()
      .click(within(sources).getByRole("button", { name: new RegExp(source.title) }));
    expect(onStar).toHaveBeenCalledWith(sourceStar(source, stars));
    expect(sourceStar(source, stars)).not.toBeNull();
  },
);

it.each(["web", "drive"])("shows an insight citing a %s page by its link", (kind) => {
  const [insight] = cleanInsights(fixtureOf(kind));
  render(
    <InsightPanel
      insight={insight}
      insights={[insight]}
      nodes={stars}
      onFeedback={vi.fn()}
      onTrail={vi.fn()}
      onStar={vi.fn()}
      onOpenInsight={vi.fn()}
    />,
  );
  const sources = screen.getByRole("heading", { name: "Sources" })
    .parentElement!;
  const link = within(sources).getByRole("link", {
    name: new RegExp(insight.sources[0].title),
  });
  expect(link).toHaveAttribute("href", insight.sources[0].url);
  expect(link).toHaveAttribute("rel", "noopener noreferrer");
  if (kind === "drive")
    expect(within(sources).getByText(/^Google Drive/)).toBeInTheDocument();
  else expect(within(sources).getByText(/^example\.invalid/)).toBeInTheDocument();
});

it("matches each kind only among stars of that kind, and names one that isn't loaded", () => {
  const at = "2026-10-28T06:55:00+00:00";
  // A memory's id names a memory star, never a record of the same id elsewhere.
  const lookalike = star("rhythm", "documents", "alex:sb-fictional-0007", "alex", "document");
  expect(
    sourceStar(
      { kind: "memory", title: "Shed key", locator: "sb-fictional-0007", retrievedAt: at },
      [lookalike],
    ),
  ).toBeNull();
  // A record names the engine's own star by the id after its subject, a document a document
  // star of any source, and a resource id from get_resource names no star.
  expect(
    sourceStar({ kind: "record", title: "Body", locator: "body", retrievedAt: at }, stars),
  ).toBe(stars[0].id);
  expect(
    sourceStar(
      { kind: "document", title: "Plan", locator: "plan.md", retrievedAt: at },
      [star("notes", "notes", "plan.md", "household", "document")],
    ),
  ).toBe("notes:notes:plan.md");
  expect(
    sourceStar(
      {
        kind: "record",
        title: "Saved",
        locator: "3f2b8c1e-4d5a-4b6c-9e7f-0a1b2c3d4e5f",
        retrievedAt: at,
      },
      stars,
    ),
  ).toBeNull();
  const [insight] = cleanInsights(fixtureOf("memory"));
  render(
    <InsightPanel
      insight={insight}
      insights={[insight]}
      nodes={[]}
      onFeedback={vi.fn()}
      onTrail={vi.fn()}
      onStar={vi.fn()}
      onOpenInsight={vi.fn()}
    />,
  );
  expect(screen.getByText(/Memory · read .* · not in the loaded sky/)).toBeInTheDocument();
});
