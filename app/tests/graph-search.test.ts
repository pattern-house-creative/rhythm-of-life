import { afterEach, expect, it, vi } from "vitest";
import { handleGraph } from "../server/graph";
import { emptyGraph } from "../src/liveGraph";
import type { GraphPage } from "../src/knowledge";
import { record, relation } from "./synthetic";
import { withEngine } from "./engine";

// Search and neighbours reach every star the engine holds, not only the overview's few hundred.
// A fictional engine of 400 stars answers app_graph the way the engine's snapshot read does:
// the overview is the first 300, and search and neighbours run over all of them, 300 a page.
afterEach(() => {
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
});
const limit = 300;
const stars = Array.from({ length: 400 }, (_, i) => {
  const n = record(`star-${i}`);
  n.provenance.demo = false;
  return n;
});
// The last star, outside the overview, links to the first and to another one outside it.
const links = [
  relation(stars[399], stars[0]),
  relation(stars[399], stars[350]),
];
const sent: Record<string, unknown>[] = [];

function engine() {
  withEngine();
  vi.mocked(fetch).mockImplementation(async (_url, init) => {
    const args = JSON.parse(String(init?.body)).params.arguments;
    sent.push(args);
    const offset = args.cursor ? Number(String(args.cursor).slice(2)) : 0;
    const center = stars.find((s) => s.id === args.value);
    const around = new Set(
      links
        .filter((l) => l.source === args.value || l.target === args.value)
        .flatMap((l) => [l.source, l.target]),
    );
    const selected =
      args.operation === "search"
        ? stars.filter((s) => s.title.includes(args.value))
        : args.operation === "neighbors"
          ? center
            ? [center, ...stars.filter((s) => around.has(s.id) && s !== center)]
            : []
          : stars;
    const shown = selected.slice(offset, offset + limit);
    const ids = new Set(shown.map((s) => s.id));
    const page: GraphPage = {
      ...emptyGraph(),
      nodes: shown,
      edges: links.filter((l) => ids.has(l.source) && ids.has(l.target)),
      nextCursor:
        offset + limit < selected.length ? `o:${offset + limit}` : null,
    };
    return new Response(
      JSON.stringify({
        jsonrpc: "2.0",
        id: "rhythm-app",
        result: { structuredContent: page },
      }),
      { headers: { "content-type": "application/json" } },
    );
  });
}
const graph = async (body: Record<string, unknown>) => {
  const response = await handleGraph(
    new Request("http://rhythm.example.invalid/api/graph", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(body),
    }),
  );
  expect(response.status).toBe(200);
  return (await response.json()) as GraphPage & {
    cursors: Record<string, string | null>;
  };
};

it("finds a star outside the overview by its title", async () => {
  engine();
  const overview = await graph({ operation: "discover" });
  expect(overview.nodes).toHaveLength(limit);
  expect(overview.nodes.map((n) => n.id)).not.toContain(stars[399].id);

  const found = await graph({ operation: "search", value: "star-399" });
  expect(found.nodes.map((n) => n.id)).toEqual([stars[399].id]);
  expect(sent.at(-1)).toEqual({ operation: "search", value: "star-399" });
  expect(found.cursors).toEqual({ rhythm: null });
});

it("pages through every match with the engine's cursor", async () => {
  engine();
  const first = await graph({ operation: "search", value: "Fictional idea" });
  expect(first.nodes).toHaveLength(limit);
  expect(first.cursors).toEqual({ rhythm: `o:${limit}` });
  const second = await graph({
    operation: "search",
    value: "Fictional idea",
    cursors: first.cursors,
  });
  expect(sent.at(-1)).toEqual({
    operation: "search",
    value: "Fictional idea",
    cursor: `o:${limit}`,
  });
  expect(second.nodes).toHaveLength(100);
  expect(second.cursors).toEqual({ rhythm: null });
  expect(new Set([...first.nodes, ...second.nodes].map((n) => n.id)).size).toBe(
    400,
  );
  // A continuation that is not the engine's starts again from the first page.
  await graph({
    operation: "search",
    value: "Fictional idea",
    cursors: { rhythm: "elsewhere" },
  });
  expect(sent.at(-1)).toEqual({ operation: "search", value: "Fictional idea" });
});

it("reads a star's neighbours outside the overview, with their links", async () => {
  engine();
  const around = await graph({ operation: "neighbors", value: stars[399].id });
  expect(sent.at(-1)).toEqual({
    operation: "neighbors",
    value: stars[399].id,
  });
  expect(around.nodes.map((n) => n.id).sort()).toEqual(
    [stars[0].id, stars[350].id, stars[399].id].sort(),
  );
  expect(around.edges.map((e) => e.id).sort()).toEqual(
    links.map((l) => l.id).sort(),
  );
});

it("claims no continuation when the engine can't be read", async () => {
  engine();
  vi.mocked(fetch).mockResolvedValueOnce(new Response("", { status: 503 }));
  vi.mocked(fetch).mockResolvedValueOnce(new Response("", { status: 503 }));
  const failed = await graph({ operation: "search", value: "star-1" });
  expect(failed.nodes).toEqual([]);
  expect(failed.cursors).toEqual({});
  expect(failed.issues.length).toBeGreaterThan(0);
});
