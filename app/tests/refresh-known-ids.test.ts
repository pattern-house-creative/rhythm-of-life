import { expect, it } from "vitest";
import type { GraphPage } from "../src/knowledge";
import { knownBytes, knownIds } from "../src/apiClient";

const page = (stars: number, links: number, pad = 80) =>
  ({
    nodes: Array.from({ length: stars }, (_, i) => ({
      id: `memory-${i}-${"x".repeat(pad)}`,
    })),
    edges: Array.from({ length: links }, (_, i) => ({ id: `link-${i}` })),
  }) as unknown as GraphPage;

it("sends every known id of a small map, stars and links in the map's order", () => {
  const small = page(3, 2, 4);
  expect(knownIds(small)).toEqual({
    ids: small.nodes.map((n) => n.id),
    edgeIds: small.edges.map((e) => e.id),
  });
});

it("keeps a long-explored map's known ids well under the app server's 128 KiB body limit", () => {
  const big = page(2000, 5000);
  const known = knownIds(big);
  const bytes = new TextEncoder().encode(JSON.stringify(known)).length;
  expect(bytes).toBeLessThanOrEqual(knownBytes + 64);
  expect(bytes).toBeLessThan(131072 / 2);
  // The most relevant first: the map's own order, stars before links.
  expect(known.ids[0]).toBe(big.nodes[0].id);
  expect(known.ids.length).toBeLessThanOrEqual(500);
  expect(known.edgeIds.every((id, i) => id === big.edges[i].id)).toBe(true);
});
