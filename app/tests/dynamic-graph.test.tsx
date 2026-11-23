import { expect, it } from "vitest";
import { render, screen, within, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMockSources } from "../src/mockSources";
import { emptyGraph, refreshGraph, normalizeRecords } from "../src/liveGraph";
import { graphWindow, canonicalView, clusterGraph } from "../src/graphView";
import { createGraph } from "../src/graph";
import { mergePage, retrieveGraph, identityKey } from "../src/knowledge";
import { record, relation } from "./synthetic";
import App from "../src/App";

it("discovers additions, refreshes native links and removes deleted/revoked records without changing layout code", async () => {
  const seed = record("seed");
  seed.type = "project";
  const old = record("old", "drive");
  const originalEdge = relation(old, seed);
  const store = createMockSources([seed, old], [originalEdge]);
  let page = await refreshGraph(store.adapters, emptyGraph());
  expect(page.nodes).toHaveLength(2);
  const memory = record(crypto.randomUUID());
  const document = record(crypto.randomUUID(), "drive");
  for (const n of [memory, document]) {
    n.resource.sourceModifiedAt = "2026-10-19T12:00:00Z";
    n.resource.revision = "2";
  }
  store.upsertNode(memory);
  store.upsertNode(document);
  store.upsertEdge(relation(memory, seed));
  store.upsertEdge(relation(document, memory));
  const updated = {
    ...originalEdge,
    type: "supersedes",
    directed: false,
    verification: "inferred" as const,
    evidence: [
      {
        ...originalEdge.evidence[0],
        excerpt: "Updated, unverified association",
      },
    ],
    sourceModifiedAt: "2026-10-19T12:00:00Z",
  };
  store.upsertEdge(updated);
  page = await refreshGraph(store.adapters, page);
  expect(page.nodes.map((n) => n.id)).toEqual(
    expect.arrayContaining([
      identityKey(memory.resource.identity),
      identityKey(document.resource.identity),
    ]),
  );
  expect(page.nodes.find((n) => n.id === memory.id)?.resource).toMatchObject({
    revision: "2",
    sourceModifiedAt: memory.resource.sourceModifiedAt,
  });
  expect(page.nodes.find((n) => n.id === memory.id)?.provenance).toEqual(
    memory.provenance,
  );
  expect(
    Date.parse(
      page.nodes.find((n) => n.id === memory.id)!.resource.retrievedAt,
    ),
  ).toBeGreaterThanOrEqual(Date.parse(memory.resource.sourceModifiedAt!));
  const refreshedEdge = page.edges.find((e) => e.id === updated.id)!;
  expect(refreshedEdge).toMatchObject({
    origin: updated.origin,
    type: updated.type,
    directed: updated.directed,
    verification: updated.verification,
    sourceModifiedAt: updated.sourceModifiedAt,
    evidence: [
      {
        excerpt: updated.evidence[0].excerpt,
        resourceId: updated.evidence[0].resourceId,
      },
    ],
  });
  expect(Date.parse(refreshedEdge.retrievedAt)).toBeGreaterThanOrEqual(
    Date.parse(updated.sourceModifiedAt),
  );
  expect(refreshedEdge.evidence[0].retrievedAt).toBe(refreshedEdge.retrievedAt);
  expect(
    page.edges.some((e) => e.source === document.id && e.target === memory.id),
  ).toBe(true);
  const layout = createGraph(graphWindow(page).nodes, page.edges, 820);
  expect(
    layout.points.every((n) => Number.isFinite(n.x) && Number.isFinite(n.y)),
  ).toBe(true);
  store.remove(old.id);
  store.revoke(memory.id);
  store.removeEdge(relation(document, memory).id);
  page = await refreshGraph(store.adapters, page);
  expect(page.nodes.some((n) => [old.id, memory.id].includes(n.id))).toBe(
    false,
  );
  expect(
    page.edges.some(
      (e) =>
        [old.id, memory.id].includes(e.source) ||
        [old.id, memory.id].includes(e.target),
    ),
  ).toBe(false);
  expect(
    await store.adapters
      .find((a) => a.provider === "second-brain")!
      .get(memory.id),
  ).toBeNull();
  expect(
    await store.adapters.find((a) => a.provider === "drive")!.get(old.id),
  ).toBeNull();
});

it("retains bounded provider states and issues without cursors, including on later pages", async () => {
  const a = record("a"),
    b = record("b", "drive");
  const store = createMockSources([a, b], [relation(a, b)]);
  store.setBounded("second-brain", true);
  const page = await retrieveGraph(store.adapters, "discover", "");
  expect(page.completeness).toBe("partial");
  expect(Object.values(page.cursors).every((c) => c === null)).toBe(true);
  expect(page.issues.some((i) => i.provider === "second-brain")).toBe(true);
  expect(
    mergePage(page, { ...emptyGraph(), completeness: "complete" }).completeness,
  ).toBe("partial");
  expect(page.edges).toHaveLength(1);
});

it("does not delete absent search results and preserves last retrieved data through an outage", async () => {
  const a = record("a"),
    b = record("b", "drive");
  const store = createMockSources([a, b], []);
  let page = await refreshGraph(store.adapters, emptyGraph());
  store.setUnavailable("drive", true);
  page = await refreshGraph(store.adapters, page);
  expect(page.nodes.find((n) => n.id === b.id)?.resource.availability).toBe(
    "stale",
  );
  expect(page.completeness).toBe("partial");
  store.setUnavailable("drive", false);
  store.revoke(b.id);
  page = await refreshGraph(store.adapters, page);
  expect(page.nodes.some((n) => n.id === b.id)).toBe(false);
});

it("keeps canonical originals and archive provenance distinct and never merges by title", () => {
  const original = record("same", "drive"),
    copy = record("copy", "rhythm"),
    other = record("same", "drive", "other-account");
  copy.title = original.title;
  other.title = original.title;
  copy.resource.representation = "archive";
  copy.resource.canonicalIdentity = original.resource.identity;
  const result = canonicalView([original, copy, other]);
  expect(result.nodes).toEqual([original, other]);
  expect(result.copies.get(original.id)).toEqual([copy]);
  expect(canonicalView([copy]).nodes).toEqual([copy]);
  const normalized = normalizeRecords(
    [original, copy, other],
    [relation(copy, original)],
  );
  expect(new Set(normalized.nodes.map((n) => n.id)).size).toBe(3);
});

it("derives groups from unfamiliar source metadata and relationships without positions", () => {
  const a = record("unfamiliar-a"),
    b = record("unfamiliar-b", "drive");
  a.groups = [{ id: "new-project", label: "Unfamiliar project" }];
  delete b.groups;
  const group = clusterGraph([a, b], [relation(a, b)]);
  expect(group.groups.get(b.id)).toBe("new-project");
  expect(group.labels.get("new-project")).toBe("Unfamiliar project");
  const graph = createGraph([a, b], [relation(a, b)], 600);
  expect(graph.points.every((n) => Number.isFinite(n.x))).toBe(true);
});

it("keeps distinct native relationships between the same endpoints and updates by source identity", () => {
  const a = record("a"),
    b = record("b", "drive");
  const first = relation(a, b);
  const second = {
    ...first,
    id: "other-link",
    origin: { ...first.origin, externalId: "other-link" },
    type: "caused_by",
    verification: "disputed" as const,
  };
  const initial = { ...emptyGraph(), nodes: [a, b], edges: [first, second] };
  const next = mergePage(initial, {
    ...emptyGraph(),
    edges: [{ ...second, directed: false }],
  });
  expect(next.edges).toHaveLength(2);
  expect(
    next.edges.find((e) => e.origin.externalId === "other-link"),
  ).toMatchObject({
    type: "caused_by",
    verification: "disputed",
    directed: false,
    evidence: second.evidence,
  });
});

it("retains partial revalidation even when discovery reports a complete page", async () => {
  const a = record("revalidate");
  const store = createMockSources([a], []);
  const adapter = {
    ...store.adapters[0],
    refresh: async () => ({
      ...emptyGraph(),
      nodes: [a],
      completeness: "partial" as const,
    }),
  };
  const page = await refreshGraph([adapter], { ...emptyGraph(), nodes: [a] });
  expect(page.completeness).toBe("partial");
  expect(page.sourceStates?.[adapter.key!].completeness).toBe("partial");
});

it("adds mock source records through the UI and retains them in ambient view", async () => {
  const user = userEvent.setup();
  render(<App />);
  await user.click(
    await screen.findByRole("button", { name: "Open Explorer" }),
  );
  const explorer = await screen.findByRole("region", {
    name: "Knowledge explorer",
  });
  await user.click(
    within(explorer).getByRole("button", { name: "Add sample records" }),
  );
  await waitFor(() =>
    expect(
      within(explorer).getByRole("button", {
        name: "Explore Field observation 1",
        exact: true,
      }),
    ).toBeInTheDocument(),
  );
  await user.click(
    within(explorer).getByRole("button", { name: "Back to Home" }),
  );
  expect(
    screen.getByRole("button", {
      name: "Explore Field observation 1",
      exact: true,
    }),
  ).toBeInTheDocument();
});
