import { readable } from "./knowledge.js";
import type {
  GraphAdapter,
  GraphEdge,
  GraphNode,
  GraphPage,
} from "./knowledge.js";
import type { SourceId } from "./model.js";

// Mutable, in-memory source simulator. No network, credentials or persistence.
export function createMockSources(
  initialNodes: GraphNode[],
  initialEdges: GraphEdge[],
  pageSize = 12,
) {
  const nodes = new Map(initialNodes.map((n) => [n.id, structuredClone(n)]));
  const edges = new Map(initialEdges.map((e) => [e.id, structuredClone(e)]));
  const removedEdges = new Set<string>();
  const unavailable = new Set<SourceId>();
  const bounded = new Set<SourceId>();
  let tick = Math.max(
    Date.parse("2026-10-18T20:05:00Z"),
    ...initialNodes.map(
      (n) => Date.parse(n.resource.sourceModifiedAt ?? "") || 0,
    ),
  );
  const now = () => new Date(tick).toISOString();
  const accounts = [
    ...new Map(
      initialNodes.map((n) => {
        const { provider, account } = n.resource.identity;
        return [`${provider}:${account}`, { provider, account }];
      }),
    ).values(),
  ];
  const adapters: GraphAdapter[] = accounts.map(({ provider, account }) => {
    const owns = (n: GraphNode) =>
      n.resource.identity.provider === provider &&
      n.resource.identity.account === account;
    const check = (signal?: AbortSignal) => {
      if (signal?.aborted) throw new DOMException("Aborted", "AbortError");
      if (unavailable.has(provider))
        throw new Error("Simulated source unavailable");
    };
    const stamp = (n: GraphNode) => ({
      ...structuredClone(n),
      resource: { ...structuredClone(n.resource), retrievedAt: now() },
    });
    const stampEdge = (e: GraphEdge): GraphEdge => ({
      ...structuredClone(e),
      retrievedAt: now(),
      evidence: e.evidence.map((v) => ({ ...v, retrievedAt: now() })),
    });
    const page = (matches: GraphNode[], cursor?: string): GraphPage => {
      // A keyset remains valid when records are added or the boundary item is removed.
      const boundary: unknown = cursor ? JSON.parse(cursor) : null;
      if (
        boundary !== null &&
        (!Array.isArray(boundary) ||
          boundary.length !== 2 ||
          boundary.some((v) => typeof v !== "string"))
      )
        throw new Error("Invalid cursor");
      const remaining = matches.filter(readable).filter((n) => {
        if (!boundary) return true;
        const [modified, id] = boundary as string[];
        const order = (n.resource.sourceModifiedAt ?? "").localeCompare(
          modified,
        );
        return order < 0 || (order === 0 && n.id.localeCompare(id) > 0);
      });
      const batch = remaining.slice(0, pageSize);
      const ids = new Set(batch.map((n) => n.id));
      const last = batch.at(-1);
      const nextCursor =
        remaining.length > pageSize && !bounded.has(provider) && last
          ? JSON.stringify([last.resource.sourceModifiedAt ?? "", last.id])
          : null;
      return {
        nodes: batch.map(stamp),
        edges: [...edges.values()]
          .filter(
            (e) =>
              (ids.has(e.source) || ids.has(e.target)) &&
              nodes.has(e.source) &&
              nodes.has(e.target) &&
              readable(nodes.get(e.source)!) &&
              readable(nodes.get(e.target)!),
          )
          .map(stampEdge),
        nextCursor,
        completeness:
          nextCursor || bounded.has(provider) ? "partial" : "complete",
        issues: bounded.has(provider)
          ? [
              {
                provider,
                reason:
                  "Provider returned a bounded result without a continuation cursor.",
              },
            ]
          : [],
      };
    };
    const recent = () =>
      [...nodes.values()]
        .filter((n) => owns(n) && readable(n))
        .sort(
          (a, b) =>
            (b.resource.sourceModifiedAt ?? "").localeCompare(
              a.resource.sourceModifiedAt ?? "",
            ) || a.id.localeCompare(b.id),
        );
    return {
      provider,
      key: `${provider}:${account}`,
      capabilities: {
        discovery: "recent",
        changes: "revalidate",
        pagination: "cursor",
        relationships: "both",
      },
      async discover(cursor, signal) {
        check(signal);
        return page(recent(), cursor);
      },
      async search(query, cursor, signal) {
        check(signal);
        return page(
          recent().filter((n) =>
            `${n.title} ${n.overview}`
              .toLowerCase()
              .includes(query.toLowerCase()),
          ),
          cursor,
        );
      },
      async get(id, signal) {
        check(signal);
        const n = nodes.get(id);
        return n && owns(n) && readable(n) ? stamp(n) : null;
      },
      async neighbors(id, cursor, signal) {
        check(signal);
        const root = nodes.get(id);
        if (!root || !readable(root)) return page([]);
        const ids = new Set(
          [...edges.values()]
            .filter((e) => e.source === id || e.target === id)
            .flatMap((e) => [e.source, e.target]),
        );
        const result = page(
          recent().filter((n) => n.id !== id && ids.has(n.id)),
          cursor,
        );
        return { ...result, nodes: [...result.nodes, stamp(root)] };
      },
      async refresh(known, signal) {
        check(signal);
        tick += 1000;
        const requested = known.filter(owns);
        const ids = new Set(requested.map((n) => n.id));
        return {
          nodes: requested.flatMap((n) => {
            const latest = nodes.get(n.id);
            return latest && readable(latest) ? [stamp(latest)] : [];
          }),
          edges: [...edges.values()]
            .filter(
              (e) =>
                (ids.has(e.source) || ids.has(e.target)) &&
                nodes.has(e.source) &&
                nodes.has(e.target) &&
                readable(nodes.get(e.source)!) &&
                readable(nodes.get(e.target)!),
            )
            .map(stampEdge),
          removedNodeIds: requested
            .filter((n) => !nodes.has(n.id) || !readable(nodes.get(n.id)!))
            .map((n) => n.id),
          removedEdgeIds: [...removedEdges],
          nextCursor: null,
          completeness: "complete",
          issues: [],
        };
      },
    };
  });
  return {
    adapters,
    upsertNode(node: GraphNode) {
      tick = Math.max(
        tick + 1000,
        Date.parse(node.resource.sourceModifiedAt ?? "") || 0,
      );
      nodes.set(node.id, structuredClone(node));
    },
    upsertEdge(edge: GraphEdge) {
      tick = Math.max(
        tick + 1000,
        Date.parse(edge.sourceModifiedAt ?? "") || 0,
      );
      removedEdges.delete(edge.id);
      edges.set(edge.id, structuredClone(edge));
    },
    remove(id: string) {
      nodes.delete(id);
    },
    revoke(id: string) {
      const n = nodes.get(id);
      if (n)
        nodes.set(id, {
          ...n,
          resource: {
            ...n.resource,
            access: { ...n.resource.access, canRead: false, canOpen: false },
            availability: "restricted",
          },
        });
    },
    removeEdge(id: string) {
      edges.delete(id);
      removedEdges.add(id);
    },
    setUnavailable(provider: SourceId, value: boolean) {
      if (value) unavailable.add(provider);
      else unavailable.delete(provider);
    },
    setBounded(provider: SourceId, value: boolean) {
      if (value) bounded.add(provider);
      else bounded.delete(provider);
    },
  };
}
