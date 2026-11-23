import { ChevronRight, Lightbulb } from "lucide-react";
import { InsightCorner } from "../InsightCard";
import { newestFirst } from "../insights";
import type { DailyInsight } from "../insights";

// The daily insights as a part of their own (docs/dev/screens.md 10.3): the
// current insight as Home's corner shows it, the way to the earlier ones and
// the engine's last run. Each opens the insight panel, as at Home.
export function InsightsPart({
  shown,
  insights,
  onInsight,
}: {
  // The current insight, else the newest earlier one and the last run
  // (insightsShown in partData.ts).
  shown: {
    current: DailyInsight | null;
    latest: DailyInsight | null;
    lastRun: string | null;
  };
  insights: DailyInsight[];
  onInsight: (id: string) => void;
}) {
  const { current, lastRun } = shown;
  // Beside a current insight, the newest of the others.
  const earlier =
    shown.latest ??
    (current
      ? (newestFirst(insights).find((i) => i.id !== current.id) ?? null)
      : null);
  const earlierLink = earlier && (
    <button
      className="insight-earlier-link"
      onClick={() => onInsight(earlier.id)}
    >
      Earlier insights <ChevronRight size={12} aria-hidden="true" />
    </button>
  );
  if (current)
    return (
      <>
        <InsightCorner insight={current} onOpen={() => onInsight(current.id)} />
        {earlierLink}
      </>
    );
  return (
    <>
      <div className="section-label">
        <span>
          <Lightbulb size={14} />
          Daily insights
        </span>
      </div>
      {!earlier && <p>Nothing yet</p>}
      {earlierLink}
      {lastRun && <span className="insight-run corner">{lastRun}</span>}
    </>
  );
}
