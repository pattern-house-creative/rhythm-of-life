import type { GraphNode } from "./knowledge";
import type { KnowledgeEdge } from "./model";
import { isFolder, isProjectStar, shownLink, structural } from "./graphView";

// The context corner shows what changed lately, one record at a time,
// moving on every couple of minutes. A stale health source is noted above it.
export const changedLatelyMs = 120_000;
const shown = 5;

const kindOf = (n: GraphNode) =>
  isFolder(n)
    ? null
    : isProjectStar(n) || n.type === "project"
      ? "Project"
      : n.type === "memory"
        ? "Memory"
        : n.type === "document"
          ? "Document"
          : null;

export function ago(iso: string | null | undefined, now = Date.now()) {
  const t = Date.parse(iso ?? "");
  if (!Number.isFinite(t)) return null;
  const days = Math.floor((now - t) / 86_400_000);
  if (days <= 0) return "Updated today";
  if (days === 1) return "Updated yesterday";
  if (days < 14) return `Updated ${days} days ago`;
  if (days < 60) return `Updated ${Math.round(days / 7)} weeks ago`;
  return `Updated ${Math.round(days / 30)} months ago`;
}

// The most recently changed memories, documents and projects, each with the
// number of records it links to (every link the sky draws). The tile turns
// through the latest few; its panel lists more.
export function recentChanges(
  nodes: GraphNode[],
  edges: KnowledgeEdge[],
  limit = shown,
) {
  const links = new Map<string, Set<string>>();
  for (const e of edges) {
    // Folder and project membership is structure, not a link worth counting.
    if (!shownLink(e) || ("type" in e && structural(e.type))) continue;
    for (const [a, b] of [
      [e.source, e.target],
      [e.target, e.source],
    ])
      links.set(a, (links.get(a) ?? new Set()).add(b));
  }
  const when = (n: GraphNode) => Date.parse(n.resource.sourceModifiedAt ?? "");
  return nodes
    .filter((n) => kindOf(n) && Number.isFinite(when(n)))
    .sort((a, b) => when(b) - when(a))
    .slice(0, limit)
    .map((node) => ({
      node,
      kind: kindOf(node)!,
      linked: links.get(node.id)?.size ?? 0,
    }));
}
