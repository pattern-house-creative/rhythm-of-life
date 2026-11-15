import { describe, expect, it, vi } from "vitest";
import { GET } from "../api/observatory";
import { handleGraph } from "../server/graph";
import { createDemoSession, demoData } from "../src/demo";
import { emptyGraph, refreshGraph } from "../src/liveGraph";
import { mergePage, retrieveGraph } from "../src/knowledge";
import { createApiSession, loadObservatory } from "../src/apiClient";
import { createMockSources } from "../src/mockSources";
import { assertGraphPage, assertObservatory } from "../src/validation";

const request = (body: unknown, headers = {}) =>
  new Request("https://observatory.test/api/graph", {
    method: "POST",
    headers: { "content-type": "application/json", ...headers },
    body: JSON.stringify(body),
  });
describe("same-origin read-only API", () => {
  it("serves validated synthetic health and graph data without cache or CORS permission", async () => {
    const health = await GET(
      new Request("https://observatory.test/api/observatory"),
    );
    expect(health.status).toBe(200);
    assertObservatory(await health.json());
    const graph = await handleGraph(request({ operation: "discover" }));
    expect(graph.status).toBe(200);
    assertGraphPage(await graph.json());
    for (const response of [health, graph]) {
      expect(response.headers.get("cache-control")).toContain("no-store");
      expect(response.headers.get("vercel-cdn-cache-control")).toBe("no-store");
      expect(response.headers.has("access-control-allow-origin")).toBe(false);
    }
  });
  it("rejects writes, cross-origin calls, invalid cursors, excessive IDs and malformed bodies", async () => {
    for (const input of [
      { operation: "sync" },
      { operation: "discover", cursors: { x: 4 } },
      { operation: "refresh", ids: Array(1501).fill("id") },
      { operation: "discover", demo: { generation: 9999, unavailable: false } },
      { operation: "search", value: "x".repeat(201) },
    ])
      expect((await handleGraph(request(input))).status).toBe(400);
    expect(
      (
        await handleGraph(
          request(
            { operation: "discover" },
            { origin: "https://attacker.test" },
          ),
        )
      ).status,
    ).toBe(403);
    expect(
      (
        await handleGraph(
          request(
            { operation: "discover" },
            { "sec-fetch-site": "cross-site" },
          ),
        )
      ).status,
    ).toBe(403);
    expect(
      (await handleGraph(new Request("https://observatory.test/api/graph")))
        .status,
    ).toBe(405);
    expect(
      (
        await handleGraph(
          request({ operation: "discover", extra: "x".repeat(140000) }),
        )
      ).status,
    ).toBe(400);
  });
  it("works through the unchanged HTTP client with another authorized provider implementation", async () => {
    const fixture = createDemoSession(demoData);
    const node = structuredClone(fixture.initial.nodes[0]);
    node.id = "drive:fictional-household:replacement";
    node.title = "Replacement provider record";
    node.provenance.sourceId = "drive";
    node.resource.identity = {
      provider: "drive",
      account: "fictional-household",
      externalId: "replacement",
    };
    const store = createMockSources([node], [], 1);
    const source = {
      ...fixture,
      adapters: store.adapters,
      retrieve: (
        op: "discover" | "search" | "neighbors",
        value: string,
        cursors?: Record<string, string | null>,
        signal?: AbortSignal,
      ) => retrieveGraph(store.adapters, op, value, cursors, signal),
      refresh: (current: ReturnType<typeof emptyGraph>, signal?: AbortSignal) =>
        refreshGraph(store.adapters, current, signal),
    };
    vi.stubGlobal(
      "fetch",
      vi.fn((url: string, init: RequestInit) =>
        handleGraph(
          new Request(new URL(url, "https://observatory.test"), init),
          source,
        ),
      ),
    );
    const client = createApiSession();
    const result = await client.retrieve("search", "Replacement");
    expect(result.nodes.map((n) => n.title)).toEqual([
      "Replacement provider record",
    ]);
    store.revoke(node.id);
    const refreshed = await client.refresh(result);
    expect(refreshed.nodes).toEqual([]);
    expect(refreshed.edges).toEqual([]);
    const record = await handleGraph(
      request({ operation: "get", value: node.id }),
      source,
    );
    expect(await record.json()).toBeNull();
  });
  it("discovers growth over HTTP without putting source fixtures in the browser", async () => {
    const client = createApiSession();
    let current = await client.refresh(emptyGraph());
    const before = current.nodes.length;
    client.demo!.advance();
    current = await client.refresh(current);
    expect(current.nodes.length).toBeGreaterThan(before);
    expect(current.nodes.some((n) => n.title === "Field observation 1")).toBe(
      true,
    );
    expect(current.nodes.some((n) => n.title === "Research notes 1")).toBe(
      true,
    );
    expect(
      current.edges.some((e) => e.type === "drawn_from" && e.evidence.length),
    ).toBe(true);
  });
  it("fails closed on expired protection and rejects malformed provider responses", async () => {
    const lost = vi.fn();
    window.addEventListener("observatory-access-lost", lost);
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => new Response("Sign in", { status: 401 })),
    );
    await expect(loadObservatory()).rejects.toThrow();
    expect(lost).toHaveBeenCalledOnce();
    window.removeEventListener("observatory-access-lost", lost);
    const source = createDemoSession(demoData);
    source.retrieve = vi.fn(async () => ({
      ...emptyGraph(),
      nodes: [{ id: "bad" }],
      cursors: {},
    })) as typeof source.retrieve;
    expect(
      (await handleGraph(request({ operation: "discover" }), source)).status,
    ).toBe(502);
  });
});

describe("discovery continuation across exploration and refresh", () => {
  it("does not skip a burst of additions larger than the refreshed head page", async () => {
    const template = createDemoSession(demoData).initial.nodes.find(
      (n) => n.resource.identity.provider === "drive",
    )!;
    const make = (id: string, modified: string) => ({
      ...structuredClone(template),
      id: `drive:fictional-household:${id}`,
      resource: {
        ...structuredClone(template.resource),
        identity: {
          provider: "drive" as const,
          account: "fictional-household",
          externalId: id,
        },
        sourceModifiedAt: modified,
      },
    });
    const old = Array.from({ length: 6 }, (_, i) =>
      make(`old-${i}`, "2026-10-18T12:00:00Z"),
    );
    const added = Array.from({ length: 7 }, (_, i) =>
      make(`new-${i}`, "2026-10-19T12:00:00Z"),
    );
    const store = createMockSources(old, [], 3);
    let page = await refreshGraph(store.adapters, emptyGraph());
    added.forEach(store.upsertNode);
    page = await refreshGraph(store.adapters, page);
    for (
      let i = 0;
      i < 10 && Object.values(page.discoveryCursors!).some(Boolean);
      i++
    ) {
      const next = await retrieveGraph(
        store.adapters,
        "discover",
        "",
        page.discoveryCursors,
        undefined,
        page.discoveryStates,
      );
      page = {
        ...mergePage(page, next),
        discoveryCursors: next.cursors,
        discoveryStates: next.sourceStates,
        completeness: next.completeness,
      };
    }
    expect(page.completeness).toBe("complete");
    expect(new Set(page.nodes.map((n) => n.id))).toEqual(
      new Set([...old, ...added].map((n) => n.id)),
    );
  });
  it("retains cursors during search, drains to complete, and does not restart exhausted discovery", async () => {
    const session = createDemoSession(demoData);
    let page = await session.refresh(emptyGraph());
    expect(Object.values(page.discoveryCursors!).some(Boolean)).toBe(true);
    const cursor = { ...page.discoveryCursors };
    page = mergePage(page, await session.retrieve("search", "sleep"));
    expect(page.discoveryCursors).toEqual(cursor);
    page = await session.refresh(page);
    expect(page.discoveryCursors).toEqual(cursor);
    for (
      let i = 0;
      i < 20 && Object.values(page.discoveryCursors!).some(Boolean);
      i++
    ) {
      const next = await session.retrieve(
        "discover",
        "",
        page.discoveryCursors,
        undefined,
        page.discoveryStates,
      );
      page = {
        ...mergePage(page, next),
        completeness: next.completeness,
        discoveryCursors: next.cursors,
        discoveryStates: next.sourceStates,
      };
    }
    expect(page.completeness).toBe("complete");
    expect(Object.values(page.discoveryCursors!).every((v) => v === null)).toBe(
      true,
    );
    const refreshed = await session.refresh(page);
    expect(refreshed.discoveryCursors).toEqual(page.discoveryCursors);
    expect(refreshed.completeness).toBe("complete");
  });
  it("preserves bounded partial coverage with no continuation", async () => {
    const fixture = createDemoSession(demoData);
    const store = createMockSources(
      fixture.initial.nodes,
      fixture.initial.edges,
      2,
    );
    store.setBounded("drive", true);
    let page = await refreshGraph(store.adapters, emptyGraph());
    for (
      let i = 0;
      i < 30 && Object.values(page.discoveryCursors!).some(Boolean);
      i++
    ) {
      const next = await retrieveGraph(
        store.adapters,
        "discover",
        "",
        page.discoveryCursors,
        undefined,
        page.discoveryStates,
      );
      page = {
        ...mergePage(page, next),
        discoveryCursors: next.cursors,
        discoveryStates: next.sourceStates,
        completeness: next.completeness,
      };
    }
    expect(page.completeness).toBe("partial");
    expect(page.discoveryCursors!["drive:fictional-household"]).toBeNull();
    expect((await refreshGraph(store.adapters, page)).completeness).toBe(
      "partial",
    );
  });
});
