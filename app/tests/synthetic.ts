import { identityKey } from "../src/knowledge";
import type { GraphNode, GraphEdge, GraphPage } from "../src/knowledge";
import type { SourceId } from "../src/model";
export function record(
  externalId: string,
  provider: SourceId = "second-brain",
  account = "fictional-test",
): GraphNode {
  const identity = { provider, account, externalId };
  return {
    id: identityKey(identity),
    title: `Fictional idea ${externalId}`,
    type: provider === "drive" ? "document" : "memory",
    overview: "Synthetic source record",
    groups: [{ id: "research", label: "Research" }],
    provenance: {
      sourceId: provider,
      recordId: externalId,
      observedAt: "2026-10-18T12:00:00Z",
      demo: true,
    },
    resource: {
      identity,
      canonicalUrl: null,
      sourceUrl: null,
      sourceModifiedAt: "2026-10-18T12:00:00Z",
      retrievedAt: "2026-10-18T12:01:00Z",
      revision: "1",
      owner: "Synthetic owner",
      access: { scope: "demo", canRead: true, canOpen: false },
      availability: "available",
      representation: "original",
      content: "Fictional content",
    },
  };
}
export function relation(a: GraphNode, b: GraphNode): GraphEdge {
  return {
    id: `${a.id}->${b.id}`,
    source: a.id,
    target: b.id,
    type: "part_of_project",
    directed: true,
    verification: "verified",
    strength: 0.6,
    origin: {
      ...a.resource.identity,
      externalId: `edge-${a.resource.identity.externalId}`,
    },
    relationship: "An explicit fictional citation links these records.",
    evidence: [
      {
        resourceId: a.id,
        excerpt: "Direct source citation",
        locator: "section 1",
        retrievedAt: a.resource.retrievedAt,
      },
    ],
    sourceModifiedAt: a.resource.sourceModifiedAt,
    retrievedAt: a.resource.retrievedAt,
  };
}
export function population(count: number): GraphPage {
  const nodes = Array.from({ length: count }, (_, i) => {
    const n = record(
      String(i),
      ["second-brain", "drive", "rhythm"][i % 3] as SourceId,
    );
    n.groups = [
      { id: `group-${i % 8}`, label: `Research area ${(i % 8) + 1}` },
    ];
    return n;
  });
  const edges = nodes
    .slice(1)
    .map((n, i) => relation(n, nodes[Math.floor(i / 2)]));
  return {
    nodes,
    edges,
    nextCursor: null,
    completeness: "complete",
    issues: [],
  };
}
