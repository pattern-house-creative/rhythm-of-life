import { afterEach, expect, it, vi } from "vitest";
import { act, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ChangedLately } from "../src/ChangedLately";
import { ChangedPanel } from "../src/panels/ChangedPanel";
import { ago, changedLatelyMs, recentChanges } from "../src/changes";
import { record, relation } from "./synthetic";
import type { GraphEdge, GraphNode } from "../src/knowledge";
import type { SourceStatus } from "../src/model";

afterEach(() => vi.useRealTimers());

const at = (n: GraphNode, iso: string, type: string, title: string) => {
  n.resource.sourceModifiedAt = iso;
  n.type = type as GraphNode["type"];
  n.title = title;
  return n;
};
// Fictional records changed on different days, plus a folder that must be skipped.
function records() {
  const memory = at(
    record("m"),
    "2026-10-26T10:00:00Z",
    "memory",
    "Fictional memory",
  );
  const doc = at(
    record("d", "drive"),
    "2026-10-20T10:00:00Z",
    "document",
    "Fictional plan",
  );
  const older = at(
    record("o"),
    "2026-09-01T10:00:00Z",
    "memory",
    "Fictional older memory",
  );
  const folder = at(
    record("f", "drive"),
    "2026-10-27T09:00:00Z",
    "theme",
    "Fictional folder",
  );
  folder.provenance.demo = false;
  const brainEvidence = [
    {
      resourceId: memory.id,
      excerpt: "Fictional",
      locator: "second-brain:relates_to:inferred",
      retrievedAt: "2026-10-18T12:01:00Z",
    },
  ];
  // A similarity suggestion: it counts like the links Second Brain made.
  const similar: GraphEdge = {
    ...relation(memory, older),
    id: "similar",
    type: "similar_to",
    verification: "suggested",
    evidence: brainEvidence,
  };
  const auto: GraphEdge = {
    ...relation(older, doc),
    id: "auto",
    type: "relates_to",
    verification: "suggested",
    evidence: brainEvidence,
  };
  const inFolder: GraphEdge = {
    ...relation(doc, folder),
    id: "in_folder",
    type: "in_folder",
  };
  const edges = [relation(memory, doc), inFolder, similar, auto];
  return { nodes: [older, folder, doc, memory], edges, memory, doc, older };
}
const fresh: SourceStatus[] = [
  {
    id: "garmin",
    name: "Garmin",
    connected: true,
    sampleAt: "2026-10-27T08:00:00Z",
    status: "available",
  },
];

it("lists the most recently changed memories, documents and projects first", () => {
  const r = records();
  const items = recentChanges(r.nodes, r.edges);
  // Folders are structure, not changes worth showing.
  expect(items.map((i) => i.node.id)).toEqual([
    r.memory.id,
    r.doc.id,
    r.older.id,
  ]);
  // Confirmed links, Second Brain's own and similarity suggestions count;
  // folder membership does not.
  expect(items[0].linked).toBe(2);
  expect(items[1].linked).toBe(2);
  expect(items[2].linked).toBe(2);
  expect(items[0].kind).toBe("Memory");
});

it("says how long ago something changed in plain words", () => {
  const now = Date.parse("2026-10-27T12:00:00Z");
  expect(ago("2026-10-27T08:00:00Z", now)).toBe("Updated today");
  expect(ago("2026-10-26T08:00:00Z", now)).toBe("Updated yesterday");
  expect(ago("2026-10-24T08:00:00Z", now)).toBe("Updated 3 days ago");
  expect(ago("2026-10-06T08:00:00Z", now)).toBe("Updated 3 weeks ago");
  expect(ago(null, now)).toBeNull();
});

it("moves on every couple of minutes, holds still when paused, and opens the record", async () => {
  const r = records();
  const onOpen = vi.fn();
  vi.useFakeTimers({ shouldAdvanceTime: true });
  const view = render(
    <ChangedLately
      nodes={r.nodes}
      edges={r.edges}
      sources={fresh}
      paused={false}
      onOpen={onOpen}
      onSources={vi.fn()}
    />,
  );
  expect(screen.getByRole("heading")).toHaveTextContent("Fictional memory");
  expect(screen.getByText(/linked to 2 records$/)).toBeInTheDocument();
  act(() => {
    vi.advanceTimersByTime(changedLatelyMs);
  });
  expect(screen.getByRole("heading")).toHaveTextContent("Fictional plan");
  view.rerender(
    <ChangedLately
      nodes={r.nodes}
      edges={r.edges}
      sources={fresh}
      paused
      onOpen={onOpen}
      onSources={vi.fn()}
    />,
  );
  act(() => {
    vi.advanceTimersByTime(changedLatelyMs * 3);
  });
  expect(screen.getByRole("heading")).toHaveTextContent("Fictional plan");
  const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
  await user.click(screen.getByRole("button", { name: /Explore document/ }));
  expect(onOpen).toHaveBeenCalledWith(r.doc.id);
});

it("notes a stale health source above the change, without taking over", async () => {
  const r = records();
  const onSources = vi.fn();
  render(
    <ChangedLately
      nodes={r.nodes}
      edges={r.edges}
      sources={[
        { ...fresh[0], status: "stale" },
        {
          id: "drive",
          name: "Google Drive",
          connected: true,
          sampleAt: null,
          status: "stale",
        },
      ]}
      paused={false}
      onOpen={vi.fn()}
      onSources={onSources}
    />,
  );
  // Only health sources warn here; the latest change still shows.
  const note = screen.getByRole("button", { name: /Garmin is stale/ });
  expect(note).not.toHaveTextContent("Google Drive");
  expect(screen.getByRole("heading")).toHaveTextContent("Fictional memory");
  await userEvent.setup().click(note);
  expect(onSources).toHaveBeenCalled();
});

it("stays within the list when a refresh makes it shorter", () => {
  const r = records();
  vi.useFakeTimers();
  const props = {
    edges: r.edges,
    sources: fresh,
    paused: false,
    onOpen: vi.fn(),
    onSources: vi.fn(),
  };
  const view = render(<ChangedLately nodes={r.nodes} {...props} />);
  act(() => {
    vi.advanceTimersByTime(changedLatelyMs * 2);
  });
  expect(screen.getByRole("heading")).toHaveTextContent(
    "Fictional older memory",
  );
  view.rerender(<ChangedLately nodes={[r.memory, r.doc]} {...props} />);
  expect(screen.getByText(/of 2/)).toHaveTextContent("1 of 2");
  expect(screen.getByRole("heading")).toHaveTextContent("Fictional memory");
});

it("lists more changes for the panel than the tile turns through", () => {
  const r = records();
  const many = Array.from({ length: 8 }, (_, i) =>
    at(
      record(`extra-${i}`),
      `2026-10-${String(10 + i).padStart(2, "0")}T10:00:00Z`,
      "memory",
      `Fictional extra ${i}`,
    ),
  );
  const nodes = [...r.nodes, ...many];
  expect(recentChanges(nodes, r.edges)).toHaveLength(5);
  expect(recentChanges(nodes, r.edges, 40)).toHaveLength(11);
});

it("opens the panel from the tile's label, keeping its turning preview", async () => {
  const r = records();
  const onPanel = vi.fn();
  render(
    <ChangedLately
      nodes={r.nodes}
      edges={r.edges}
      sources={fresh}
      paused
      onOpen={vi.fn()}
      onSources={vi.fn()}
      onPanel={onPanel}
    />,
  );
  expect(screen.getByRole("heading")).toHaveTextContent("Fictional memory");
  await userEvent
    .setup()
    .click(screen.getByRole("button", { name: "Open Changed lately" }));
  expect(onPanel).toHaveBeenCalled();
});

it("lists every recent change by day, each with its kind, links and a way in", async () => {
  const r = records();
  const onOpen = vi.fn(),
    onSources = vi.fn();
  render(
    <ChangedPanel
      nodes={r.nodes}
      edges={r.edges}
      sources={[{ ...fresh[0], status: "stale" }]}
      onOpen={onOpen}
      onSources={onSources}
    />,
  );
  expect(
    screen.getByRole("heading", { name: "Changed lately" }),
  ).toBeInTheDocument();
  // One section per day, newest first; the folder is not a change.
  const days = screen.getAllByRole("region");
  expect(days).toHaveLength(3);
  expect(
    within(days[0]).getByRole("heading", { name: "Fictional memory" }),
  ).toBeInTheDocument();
  expect(
    within(days[0]).getByText(/^Memory · Second Brain/),
  ).toBeInTheDocument();
  expect(within(days[0]).getByText("Linked to 2 records")).toBeInTheDocument();
  expect(within(days[1]).getByText("Linked to 2 records")).toBeInTheDocument();
  expect(
    within(days[1]).getByText(/^Document · Google Drive/),
  ).toBeInTheDocument();
  expect(screen.queryByText("Fictional folder")).not.toBeInTheDocument();
  expect(
    screen.getByText(/^3 records changed in the last/),
  ).toBeInTheDocument();
  const user = userEvent.setup();
  await user.click(
    screen.getByRole("button", { name: "Explore Fictional plan" }),
  );
  expect(onOpen).toHaveBeenCalledWith(r.doc.id);
  // A stale health source is noted at the top.
  await user.click(screen.getByRole("button", { name: /Garmin is stale/ }));
  expect(onSources).toHaveBeenCalled();
});

it("says so when nothing has changed yet", () => {
  render(
    <ChangedPanel
      nodes={[]}
      edges={[]}
      sources={fresh}
      onOpen={vi.fn()}
      onSources={vi.fn()}
    />,
  );
  expect(
    screen.getByText("Records appear here as your sources change."),
  ).toBeInTheDocument();
  expect(screen.queryByRole("region")).not.toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: /stale/ }),
  ).not.toBeInTheDocument();
});
