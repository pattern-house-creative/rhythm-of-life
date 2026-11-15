import { afterEach, expect, it, vi } from "vitest";
import { readRelationshipGraph } from "../server/relationshipGraph";
import { handleGraph } from "../server/graph";
import { emptyGraph } from "../src/liveGraph";
import { record, relation } from "./synthetic";
import { withEngine } from "./engine";

afterEach(() => {
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
});
const configured = () => withEngine();
const request = () => new Request("http://rhythm.example.invalid/api/graph");

// The engine's /mcp reads at most 32 KiB of request body and answers 413
// beyond that, which the app takes as the graph being unavailable.
const engineLimit = 32768;

it("keeps a large map on refresh by sending only the known IDs that fit the engine's request limit", async () => {
  configured();
  // About the live map's size: long Second Brain IDs and a few hundred links.
  const account = "users/" + "usr-fictional-".repeat(5);
  const nodes = Array.from({ length: 110 }, (_, i) => {
    const n = record(`memory-${i}-${"x".repeat(80)}`, "second-brain", account);
    n.provenance.demo = false;
    return n;
  });
  const edges = Array.from({ length: 270 }, (_, i) => {
    const edge = relation(nodes[i % 110], nodes[(i * 7 + 1) % 110]);
    edge.id = `link-${i}-${"y".repeat(64)}`;
    return edge;
  });
  const page = { ...emptyGraph(), nodes, edges, completeness: "partial" };
  const sent: string[] = [];
  vi.mocked(fetch).mockImplementation(async (_url, init) => {
    const body = String(init?.body);
    sent.push(body);
    return new TextEncoder().encode(body).length > engineLimit
      ? new Response("", { status: 413 })
      : new Response(
          JSON.stringify({
            jsonrpc: "2.0",
            id: "rhythm-app",
            result: { structuredContent: page },
          }),
          { headers: { "content-type": "application/json" } },
        );
  });
  const ids = nodes.map((n) => n.id),
    edgeIds = edges.map((e) => e.id);
  expect(Math.max(...ids.map((id) => id.length))).toBeLessThanOrEqual(200);
  // Everything the app holds would be well over the limit.
  expect(JSON.stringify({ ids, edgeIds }).length).toBeGreaterThan(engineLimit);

  const result = await readRelationshipGraph(request(), {
    operation: "refresh",
    ids,
    edgeIds,
  });

  expect(result.nodes).toHaveLength(110);
  expect(result.edges.length).toBeGreaterThan(0);
  expect(sent).toHaveLength(1);
  expect(new TextEncoder().encode(sent[0]).length).toBeLessThanOrEqual(
    engineLimit,
  );
  // Stars go first and links fill what is left, each in the app's order.
  const { knownNodeIds, knownEdgeIds } = JSON.parse(sent[0]).params.arguments;
  expect(knownNodeIds).toEqual(ids);
  expect(knownEdgeIds.length).toBeGreaterThan(0);
  expect(knownEdgeIds).toEqual(edgeIds.slice(0, knownEdgeIds.length));
});

it("answers a refresh the engine didn't answer as no answer, which the browser keeps its map through, and a refusal as an empty map", async () => {
  configured();
  const refresh = () =>
    handleGraph(
      new Request("http://localhost/api/graph", {
        method: "POST",
        headers: {
          "content-type": "application/json",
          origin: "http://localhost",
        },
        body: JSON.stringify({ operation: "refresh", ids: [], edgeIds: [] }),
      }),
    );
  // The engine restarting for a deploy.
  vi.mocked(fetch).mockResolvedValue(new Response("", { status: 503 }));
  expect((await refresh()).status).toBe(502);
  vi.mocked(fetch).mockRejectedValue(new TypeError("fetch failed"));
  expect((await refresh()).status).toBe(502);
  // Refused (the app's credential, say): still an empty map, fail closed.
  vi.mocked(fetch).mockResolvedValue(new Response("", { status: 401 }));
  const refused = await refresh();
  expect(refused.status).toBe(200);
  expect(await refused.json()).toMatchObject({ nodes: [], edges: [] });
});
