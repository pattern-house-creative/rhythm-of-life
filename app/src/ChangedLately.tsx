import { useEffect, useMemo, useState } from "react";
import type { ReactNode } from "react";
import {
  AlertTriangle,
  ArrowUpRight,
  ChevronRight,
  Sparkles,
} from "lucide-react";
import type { GraphNode } from "./knowledge";
import type { KnowledgeEdge, SourceStatus } from "./model";
import { displayTitle } from "./graph";
import { stamp } from "./health";
import { ago, changedLatelyMs, recentChanges } from "./changes";
import { InsightCorner } from "./InsightCard";
import type { DailyInsight } from "./insights";
import { glucoseSlot } from "../server/healthState";
import "./panels/ChangedPanel.css";

// Health sources are the ones whose staleness matters at a glance; a single
// knowledge record that failed to revalidate is not worth a warning here.
const watched = new Set([glucoseSlot, "garmin"]);

// A stale health source, noted quietly above what changed; it leads to
// Sources & settings.
export function FreshnessNote({
  sources,
  onSources,
}: {
  sources: SourceStatus[];
  onSources: () => void;
}) {
  const stale = sources.filter(
    (s) => s.status === "stale" && watched.has(s.id),
  );
  if (!stale.length) return null;
  return (
    <button className="freshness-note" onClick={onSources}>
      <AlertTriangle size={13} />
      {stale.map((s) => s.name).join(" and ")}{" "}
      {stale.length === 1 ? "is" : "are"} stale · last observed{" "}
      {stamp(stale[0].sampleAt)}
    </button>
  );
}

export function ChangedLately({
  nodes,
  edges,
  sources,
  paused,
  onOpen,
  onSources,
  onPanel,
  insight,
  onInsight,
  onEarlier,
  lastRun,
  quiet = false,
}: {
  nodes: GraphNode[];
  edges: KnowledgeEdge[];
  sources: SourceStatus[];
  paused: boolean;
  onOpen: (id: string) => void;
  onSources: () => void;
  // Opens the Changed lately panel, which lists everything at once.
  onPanel?: () => void;
  // The day's insight, when one is current, takes the corner; recent
  // changes stay a tap away under it.
  insight?: DailyInsight | null;
  onInsight?: () => void;
  // With no current insight, earlier ones stay a quiet tap away, and the
  // engine's last run is noted in a line.
  onEarlier?: () => void;
  lastRun?: string | null;
  // A dashboard's part (parts/ChangedPart.tsx) says "Nothing yet" quietly
  // where Home's corner welcomes the first records.
  quiet?: boolean;
}) {
  const items = useMemo(() => recentChanges(nodes, edges), [nodes, edges]);
  const [index, setIndex] = useState(0);
  const moving = !paused && items.length > 1;
  useEffect(() => {
    if (!moving) return;
    const t = setInterval(
      () => setIndex((i) => (i + 1) % items.length),
      changedLatelyMs,
    );
    return () => clearInterval(t);
  }, [moving, items.length]);
  // The list can shrink after a refresh; stay within it.
  const current = items.length ? index % items.length : 0;
  const item = items[current];
  const warning = <FreshnessNote sources={sources} onSources={onSources} />;
  if (insight && onInsight)
    return (
      <InsightCorner
        insight={insight}
        note={warning}
        onOpen={onInsight}
        onChanged={onPanel}
      />
    );
  const earlier = onEarlier && (
    <button className="insight-earlier-link" onClick={onEarlier}>
      Earlier insights <ChevronRight size={12} aria-hidden="true" />
    </button>
  );
  const run = lastRun && <span className="insight-run corner">{lastRun}</span>;
  // The label is the way into the panel, as the health tiles' are.
  const label = (dots?: ReactNode) =>
    onPanel ? (
      <button
        className="section-label changed-open"
        onClick={onPanel}
        aria-label="Open Changed lately"
      >
        <span>
          <Sparkles size={14} />
          Changed lately
        </span>
        <span className="changed-label-end">
          {dots}
          <ArrowUpRight size={16} />
        </span>
      </button>
    ) : (
      <div className="section-label">
        <span>
          <Sparkles size={14} />
          Changed lately
        </span>
        {dots}
      </div>
    );
  if (!item)
    return (
      <>
        {label()}
        {warning}
        {quiet ? (
          <p>Nothing yet</p>
        ) : (
          <>
            <h2>Knowledge is taking shape</h2>
            <p>Records appear here as your sources change.</p>
          </>
        )}
        {earlier}
        {run}
      </>
    );
  return (
    <>
      {label(
        <span className="changed-dots" aria-hidden="true">
          {items.map((it, i) => (
            <i key={it.node.id} className={i === current ? "on" : ""} />
          ))}
        </span>,
      )}
      {warning}
      <h2 key={item.node.id} className="changed-title" title={item.node.title}>
        {displayTitle(item.node.title)}
      </h2>
      <p>
        {item.kind} · {ago(item.node.resource.sourceModifiedAt)} ·{" "}
        {item.linked
          ? `linked to ${item.linked} ${item.linked === 1 ? "record" : "records"}`
          : "no links yet"}
      </p>
      {earlier ? (
        <div className="context-row">
          <button className="context-link" onClick={() => onOpen(item.node.id)}>
            <span className="mini-nodes">•—•</span> Explore{" "}
            {item.kind.toLowerCase()} <ArrowUpRight size={15} />
          </button>
          {earlier}
        </div>
      ) : (
        <button className="context-link" onClick={() => onOpen(item.node.id)}>
          <span className="mini-nodes">•—•</span> Explore{" "}
          {item.kind.toLowerCase()} <ArrowUpRight size={15} />
        </button>
      )}
      {run}
      <span className="context-foot">
        {current + 1} of {items.length} · most recent first
      </span>
    </>
  );
}
