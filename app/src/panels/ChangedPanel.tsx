import { useMemo, useState } from "react";
import { ArrowUpRight, Brain, FileText, Sparkle, Sparkles } from "lucide-react";
import type { LucideIcon } from "lucide-react";
import type { GraphNode } from "../knowledge";
import type { KnowledgeEdge, SourceStatus } from "../model";
import { displayTitle } from "../graph";
import { clusterColor, primaryGroup, sourceName } from "../graphView";
import { ago, recentChanges } from "../changes";
import { FreshnessNote } from "../ChangedLately";
import "./ChangedPanel.css";

// Everything that changed lately at once, newest first and grouped by day:
// what it is, when it changed, how much it links to, and a way into the
// constellation at that record. The tile turns through the latest few.
const listed = 40;
const icons: Record<string, LucideIcon> = {
  Project: Sparkle,
  Memory: Brain,
  Document: FileText,
};
// Local calendar days, so a change late in the evening stays on its day.
const dayKey = (d: Date) =>
  `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
const noon = (day: string) => new Date(`${day}T12:00:00`);
const daysBetween = (from: string, to: string) =>
  Math.round((noon(to).getTime() - noon(from).getTime()) / 86_400_000);
function dayHeading(day: string, today: string) {
  const label = noon(day).toLocaleDateString("en-US", {
    weekday: "long",
    month: "short",
    day: "numeric",
  });
  const back = daysBetween(day, today);
  if (back <= 0) return ["Today", label] as const;
  if (back === 1) return ["Yesterday", label] as const;
  return [
    label,
    ago(noon(day).toISOString(), noon(today).getTime())?.replace(
      "Updated ",
      "",
    ) ?? "",
  ] as const;
}
const plural = (n: number, one: string, many: string) =>
  `${n} ${n === 1 ? one : many}`;

export function ChangedPanel({
  nodes,
  edges,
  sources,
  onOpen,
  onSources,
}: {
  nodes: GraphNode[];
  edges: KnowledgeEdge[];
  sources: SourceStatus[];
  onOpen: (id: string) => void;
  onSources: () => void;
}) {
  // "Today" as of opening the panel.
  const [today] = useState(() => dayKey(new Date()));
  const items = useMemo(
    () => recentChanges(nodes, edges, listed),
    [nodes, edges],
  );
  const days: { day: string; items: typeof items }[] = [];
  for (const item of items) {
    const day = dayKey(new Date(item.node.resource.sourceModifiedAt!));
    const last = days.at(-1);
    if (last?.day === day) last.items.push(item);
    else days.push({ day, items: [item] });
  }
  const counts = (["Memory", "Document", "Project"] as const)
    .map((kind) => ({
      kind,
      n: items.filter((i) => i.kind === kind).length,
    }))
    .filter((k) => k.n > 0);
  // Calendar days from the oldest change to today, both counted.
  const oldest = days.at(-1)?.day;
  const span = oldest ? daysBetween(oldest, today) + 1 : 0;
  const within =
    span <= 1
      ? "today"
      : span < 60
        ? `in the last ${span} days`
        : `in the last ${Math.round(span / 30)} months`;
  return (
    <div className="changed-panel">
      <div className="detail-title">
        <div>
          <p className="eyebrow">KNOWLEDGE · NEWEST FIRST</p>
          <h2>Changed lately</h2>
          <p>
            {items.length
              ? `${plural(items.length, "record", "records")} changed ${within}, across your memories, documents and projects.`
              : "Records appear here as your sources change."}
          </p>
        </div>
        <Sparkles size={28} strokeWidth={1} />
      </div>
      <FreshnessNote sources={sources} onSources={onSources} />
      {counts.length > 0 && (
        <div className="changed-counts">
          {counts.map(({ kind, n }) => {
            const Icon = icons[kind];
            return (
              <span key={kind}>
                <Icon size={14} aria-hidden="true" />
                <b>{n}</b>{" "}
                {kind === "Memory"
                  ? n === 1
                    ? "memory"
                    : "memories"
                  : `${kind.toLowerCase()}${n === 1 ? "" : "s"}`}
              </span>
            );
          })}
        </div>
      )}
      {days.map(({ day, items: list }) => {
        const [title, detail] = dayHeading(day, today);
        return (
          <section key={day} className="changed-day" aria-label={title}>
            <h3>
              {title}
              {detail && <small>{detail}</small>}
            </h3>
            <ol className="changed-list">
              {list.map(({ node, kind, linked }) => {
                const Icon = icons[kind] ?? FileText;
                const time = new Date(
                  node.resource.sourceModifiedAt!,
                ).toLocaleTimeString("en-US", {
                  hour: "numeric",
                  minute: "2-digit",
                });
                return (
                  <li key={node.id} className="changed-item">
                    <span
                      className="changed-icon"
                      style={{
                        color: clusterColor(primaryGroup(node)?.id),
                      }}
                    >
                      <Icon size={16} aria-hidden="true" />
                    </span>
                    <div className="changed-body">
                      <h4 title={node.title}>{displayTitle(node.title)}</h4>
                      <p className="changed-meta">
                        {kind} · {sourceName(node.provenance.sourceId)} ·{" "}
                        {time}
                      </p>
                      {node.overview && node.overview !== node.title && (
                        <p className="changed-overview">{node.overview}</p>
                      )}
                      <p className="changed-links">
                        <span className="changed-dots-line" aria-hidden="true">
                          {Array.from(
                            { length: Math.min(linked, 8) },
                            (_, i) => (
                              <i key={i} />
                            ),
                          )}
                        </span>
                        {linked
                          ? `Linked to ${plural(linked, "record", "records")}`
                          : "No links yet"}
                      </p>
                    </div>
                    <button
                      className="changed-explore"
                      onClick={() => onOpen(node.id)}
                      aria-label={`Explore ${displayTitle(node.title)}`}
                    >
                      Explore <ArrowUpRight size={14} aria-hidden="true" />
                    </button>
                  </li>
                );
              })}
            </ol>
          </section>
        );
      })}
    </div>
  );
}
