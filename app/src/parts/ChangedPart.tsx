import { ChangedLately } from "../ChangedLately";
import type { DailyInsight } from "../insights";
import type { GraphNode } from "../knowledge";
import type { KnowledgeEdge, SourceStatus } from "../model";

// The changed-lately corner (docs/dev/screens.md 10.3), with the current
// insight when there is one. Home's changed corner draws it, and so does a
// dashboard's changed part, which says "Nothing yet" quietly where Home
// welcomes the first records.
export function ChangedPart({
  nodes,
  edges,
  sources,
  paused,
  shown,
  onStar,
  onSources,
  onPanel,
  onInsight,
  quiet = false,
}: {
  nodes: GraphNode[];
  edges: KnowledgeEdge[];
  sources: SourceStatus[];
  paused: boolean;
  // The current insight, else the newest earlier one and the last run
  // (insightsShown in partData.ts).
  shown: {
    current: DailyInsight | null;
    latest: DailyInsight | null;
    lastRun: string | null;
  };
  onStar: (id: string) => void;
  onSources: () => void;
  onPanel: () => void;
  onInsight: (id: string) => void;
  quiet?: boolean;
}) {
  const { current, latest, lastRun } = shown;
  return (
    <ChangedLately
      nodes={nodes}
      edges={edges}
      sources={sources}
      paused={paused}
      onOpen={onStar}
      onSources={onSources}
      onPanel={onPanel}
      insight={current}
      onInsight={() => current && onInsight(current.id)}
      onEarlier={latest ? () => onInsight(latest.id) : undefined}
      lastRun={lastRun}
      quiet={quiet}
    />
  );
}
