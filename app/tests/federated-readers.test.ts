import { afterEach, expect, it, vi } from "vitest";
import { loadAdditionalHealth } from "../server/additionalHealth";
import { readRelationshipGraph } from "../server/relationshipGraph";
import { createApiSession } from "../src/apiClient";
import { record, relation } from "./synthetic";
import { emptyGraph } from "../src/liveGraph";
import { GET } from "../api/observatory";
import { appToken, closedEngines, engineUrl, withEngine } from "./engine";

afterEach(() => {
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
});
const configured = () => withEngine();
function rpc(payload: unknown) {
  return new Response(
    JSON.stringify({
      jsonrpc: "2.0",
      id: "rhythm-app",
      result: { structuredContent: payload },
    }),
    { headers: { "content-type": "application/json" } },
  );
}
const request = () =>
  new Request("http://rhythm.example.invalid/api/observatory?source=garmin");
it("uses the app token and fails closed on upstream denial, outage and malformed data", async () => {
  configured();
  for (const response of [
    new Response("", { status: 401 }),
    new Response("", { status: 503 }),
    rpc({ subject: "jo" }),
    new Response("login", { headers: { "content-type": "text/html" } }),
  ]) {
    vi.mocked(fetch).mockResolvedValueOnce(response);
    const data = await loadAdditionalHealth("garmin", request());
    expect(data.steps.value).toBeNull();
    expect(data.current.value).toBeNull();
    expect(data.steps.provenance.demo).toBe(false);
  }
  const [, init] = vi.mocked(fetch).mock.calls.at(-1)!;
  expect(init).toMatchObject({
    redirect: "error",
    cache: "no-store",
    headers: expect.objectContaining({
      Authorization: `Bearer ${appToken}`,
    }),
  });
  expect(JSON.parse(String(init?.body))).toMatchObject({
    params: { name: "app_garmin_summary", arguments: {} },
  });
  vi.mocked(fetch).mockRejectedValueOnce(new Error("timeout"));
  expect(
    (await loadAdditionalHealth("history", request())).glucose.samples,
  ).toEqual([]);
});
it("never retrieves without an engine, after the credential expires, or from an unusable engine URL", async () => {
  const unusable: [string, () => void][] = [
    [
      "a URL with a path",
      () => {
        configured();
        vi.stubEnv("RHYTHM_ENGINE_URL", `${engineUrl}/elsewhere`);
      },
    ],
  ];
  for (const [, close] of [...closedEngines, ...unusable]) {
    close();
    vi.mocked(fetch).mockClear();
    await loadAdditionalHealth("garmin", request());
    expect(fetch).not.toHaveBeenCalled();
  }
});
it("preserves bounded graph evidence, directions, partial status and removals without inventing source search", async () => {
  configured();
  const a = record("a"),
    b = record("b", "drive");
  a.provenance.demo = false;
  b.provenance.demo = false;
  const edge = relation(a, b);
  edge.type = "native_citation";
  const page = {
    ...emptyGraph(),
    nodes: [a, b],
    edges: [edge],
    removedNodeIds: ["revoked"],
    removedEdgeIds: ["removed-edge"],
    completeness: "partial",
    issues: [],
  };
  vi.mocked(fetch).mockImplementation(async () => rpc(page));
  const response = await readRelationshipGraph(request(), {
    operation: "refresh",
    ids: [a.id],
    edgeIds: [edge.id],
  });
  expect(response).toMatchObject({
    nodes: [a, b],
    edges: [edge],
    completeness: "partial",
    removedNodeIds: ["revoked"],
  });
  const sent = JSON.parse(String(vi.mocked(fetch).mock.calls.at(-1)![1]?.body));
  expect(sent.params).toEqual({
    name: "app_graph",
    arguments: { knownNodeIds: [a.id], knownEdgeIds: [edge.id] },
  });
  // Search runs in the engine; its answer is passed on as it is, with no removals.
  vi.mocked(fetch).mockResolvedValueOnce(
    rpc({ ...emptyGraph(), nodes: [a], completeness: "partial", issues: [] }),
  );
  const search = await readRelationshipGraph(request(), {
    operation: "search",
    value: "Fictional idea a",
  });
  expect(search).toMatchObject({
    nodes: [a],
    edges: [],
    nextCursor: null,
    cursors: { rhythm: null },
  });
  expect(search).not.toHaveProperty("removedNodeIds");
  expect(
    JSON.parse(String(vi.mocked(fetch).mock.calls.at(-1)![1]?.body)).params
      .arguments,
  ).toEqual({ operation: "search", value: "Fictional idea a" });
  vi.mocked(fetch).mockResolvedValueOnce(new Response("", { status: 401 }));
  expect(
    await readRelationshipGraph(request(), { operation: "refresh" }),
  ).toMatchObject({ nodes: [], edges: [] });
});
it("keeps the engine's fictional seed on the map, labelled demo", async () => {
  configured();
  const seed = record("demo-note"),
    own = record("own-note");
  seed.provenance.demo = true;
  own.provenance.demo = false;
  vi.mocked(fetch).mockImplementation(async () =>
    rpc({ ...emptyGraph(), nodes: [seed, own], edges: [relation(seed, own)] }),
  );
  const response = await readRelationshipGraph(request(), {
    operation: "refresh",
  });
  expect(response).toMatchObject({
    nodes: [
      { id: seed.id, provenance: { demo: true } },
      { id: own.id, provenance: { demo: false } },
    ],
    edges: [{ source: seed.id, target: own.id }],
  });
});
it("asks the engine for one memory's text by ID alone and fails closed", async () => {
  configured();
  const a = record("fictional-memory");
  a.provenance.demo = false;
  a.resource.content = "Fictional full memory text.";
  vi.mocked(fetch).mockImplementation(async () =>
    rpc({ ...emptyGraph(), nodes: [a] }),
  );
  expect(
    await readRelationshipGraph(request(), {
      operation: "content",
      value: a.id,
      ids: ["ignored-known-id"],
    }),
  ).toMatchObject({ id: a.id, resource: { content: a.resource.content } });
  const sent = JSON.parse(String(vi.mocked(fetch).mock.calls.at(-1)![1]?.body));
  expect(sent.params).toEqual({
    name: "app_graph",
    arguments: { operation: "content", value: a.id },
  });
  // An engine without the operation, or any denial, yields no record at all.
  vi.mocked(fetch).mockResolvedValueOnce(new Response("", { status: 403 }));
  expect(
    await readRelationshipGraph(request(), {
      operation: "content",
      value: a.id,
    }),
  ).toBeNull();
  // The browser session reads it through the same route and keeps only text and edit time.
  vi.mocked(fetch).mockResolvedValueOnce(
    new Response(JSON.stringify(a), {
      headers: { "content-type": "application/json" },
    }),
  );
  expect(await createApiSession(false).text!(a.id)).toEqual({
    text: a.resource.content,
    modifiedAt: a.resource.sourceModifiedAt,
  });
  expect(
    JSON.parse(String(vi.mocked(fetch).mock.calls.at(-1)![1]?.body)),
  ).toEqual({ operation: "content", value: a.id });
  expect(createApiSession(true).text).toBeUndefined();
});
it("asks the engine for one data star's readings by ID alone and keeps only the readings", async () => {
  configured();
  const star = record("alex:glucose", "rhythm", "data:carelink");
  star.type = "record";
  star.provenance.demo = false;
  star.resource.content = null;
  const withReadings = {
    ...star,
    measurements: [
      {
        metric: "sensor_glucose",
        label: "Sensor glucose",
        unit: "mg/dL",
        latest: {
          value: 130,
          observedAt: "2026-10-27T10:00:00Z",
          date: "2026-10-27",
        },
        days: [{ date: "2026-10-27", min: 110, mean: 120, max: 130, count: 2 }],
      },
    ],
  };
  vi.mocked(fetch).mockImplementation(async () =>
    rpc({ ...emptyGraph(), nodes: [withReadings] }),
  );
  expect(
    await readRelationshipGraph(request(), {
      operation: "data",
      value: star.id,
    }),
  ).toMatchObject({ id: star.id, measurements: withReadings.measurements });
  const sent = JSON.parse(String(vi.mocked(fetch).mock.calls.at(-1)![1]?.body));
  expect(sent.params).toEqual({
    name: "app_graph",
    arguments: { operation: "data", value: star.id },
  });
  vi.mocked(fetch).mockResolvedValueOnce(
    new Response(JSON.stringify(withReadings), {
      headers: { "content-type": "application/json" },
    }),
  );
  expect(await createApiSession(false).data!(star.id)).toEqual(
    withReadings.measurements,
  );
  expect(
    JSON.parse(String(vi.mocked(fetch).mock.calls.at(-1)![1]?.body)),
  ).toEqual({ operation: "data", value: star.id });
  // The demo reads fictional days instead, without a request.
  const calls = vi.mocked(fetch).mock.calls.length;
  const fictional = await createApiSession(true).data!(
    "rhythm:data%3Ademo:fixture%3Asleep",
  );
  expect(fictional?.map((s) => s.metric)).toEqual(["sleep_minutes"]);
  expect(vi.mocked(fetch).mock.calls.length).toBe(calls);
  // A malformed reading fails the whole response rather than drawing a wrong chart.
  vi.mocked(fetch).mockImplementation(async () =>
    rpc({
      ...emptyGraph(),
      nodes: [{ ...withReadings, measurements: [{ metric: "x" }] }],
    }),
  );
  expect(
    await readRelationshipGraph(request(), {
      operation: "data",
      value: star.id,
    }),
  ).toBeNull();
});

it("answers a refresh that failed on the way as no answer, without losing access or enabling demo controls", async () => {
  const listener = vi.fn();
  window.addEventListener("observatory-access-lost", listener);
  const session = createApiSession(false);
  expect(session.demo).toBeUndefined();
  const old = { ...emptyGraph(), nodes: [record("old")] };
  // No answer is no new map: the explorer keeps the one it has, marked stale (useKnowledge.ts).
  vi.mocked(fetch).mockRejectedValueOnce(new Error("graph offline"));
  await expect(session.refresh(old)).rejects.toThrow("graph offline");
  vi.mocked(fetch).mockResolvedValueOnce(
    new Response(JSON.stringify({ error: "Source response unavailable" }), {
      status: 502,
      headers: { "content-type": "application/json" },
    }),
  );
  await expect(session.refresh(old)).rejects.toThrow();
  expect(listener).not.toHaveBeenCalled();
  window.removeEventListener("observatory-access-lost", listener);
});
it("keeps every source route private/no-store and rejects arbitrary source selection", async () => {
  for (const source of ["garmin", "history"]) {
    const response = await GET(
      new Request(
        `http://rhythm.example.invalid/api/observatory?source=${source}`,
      ),
    );
    expect(response.headers.get("cache-control")).toContain("no-store");
    expect(response.headers.get("cache-control")).toContain("private");
    expect((await response.json()).current.provenance.demo).toBe(false);
  }
  expect(
    (
      await GET(
        new Request(
          "http://rhythm.example.invalid/api/observatory?source=insulin",
        ),
      )
    ).status,
  ).toBe(400);
});
