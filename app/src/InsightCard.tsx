import type { ReactNode } from "react";
import { motion } from "motion/react";
import { ArrowUpRight, ChevronRight } from "lucide-react";
import { dayName, kindNames } from "./insights";
import type { DailyInsight, InsightKind } from "./insights";
import "./InsightCard.css";

// A small mark for each kind of insight: a ring for a focus, two joined stars
// for a discovery, a spark for an idea.
export function KindMark({
  kind,
  size = 14,
}: {
  kind: InsightKind;
  size?: number;
}) {
  return (
    <svg
      className={`kind-mark kind-${kind}`}
      width={size}
      height={size}
      viewBox="0 0 14 14"
      aria-hidden="true"
    >
      {kind === "focus" ? (
        <>
          <circle cx="7" cy="7" r="5.3" fill="none" stroke="currentColor" />
          <circle cx="7" cy="7" r="1.8" fill="currentColor" />
        </>
      ) : kind === "discovery" ? (
        <>
          <path
            d="M3.2 10.4 10.8 3.6"
            stroke="currentColor"
            strokeWidth="0.9"
            opacity="0.55"
          />
          <circle cx="3.2" cy="10.4" r="2" fill="currentColor" />
          <circle cx="10.8" cy="3.6" r="2" fill="currentColor" />
        </>
      ) : (
        <path
          d="M7 1.2C7.4 5 9 6.6 12.8 7 9 7.4 7.4 9 7 12.8 6.6 9 5 7.4 1.2 7 5 6.6 6.6 5 7 1.2Z"
          fill="currentColor"
        />
      )}
    </svg>
  );
}

// The ambient corner while an insight is current: its kind, title and one
// line of it. The whole of it opens the insight; recent changes stay a tap
// away below.
export function InsightCorner({
  insight,
  note,
  onOpen,
  onChanged,
}: {
  insight: DailyInsight;
  // A stale health source, noted quietly above.
  note?: ReactNode;
  onOpen: () => void;
  onChanged?: () => void;
}) {
  const kind = kindNames[insight.kind];
  return (
    <div className={`insight-corner kind-${insight.kind}`}>
      <button
        className="insight-corner-open"
        onClick={onOpen}
        aria-label={`Open insight · ${kind}: ${insight.title}`}
      >
        <span className="section-label">
          <span className="insight-kind">
            <KindMark kind={insight.kind} />
            {kind.toUpperCase()}
          </span>
          <span className="insight-when">
            {dayName(insight.generatedAt).toUpperCase()}
            <ArrowUpRight size={16} />
          </span>
        </span>
        <span className="insight-corner-title" key={insight.id}>
          {insight.title}
        </span>
        <span className="insight-corner-line">
          {insight.summary || insight.whyItMatters}
        </span>
        <span className="insight-corner-foot">
          <span className="mini-nodes">•—•</span> Open insight
        </span>
      </button>
      {note}
      {onChanged && (
        <button
          className="insight-changed"
          onClick={onChanged}
          aria-label="Open Changed lately"
        >
          Changed lately <ChevronRight size={12} />
        </button>
      )}
    </div>
  );
}

// Phones without a current insight: a quiet line in the card's place, when
// earlier insights exist (opening the newest of them) or the engine has run.
export function EarlierInsights({
  latest,
  lastRun,
  onOpen,
}: {
  latest: DailyInsight | null;
  lastRun?: string | null;
  onOpen: () => void;
}) {
  const run = lastRun && <small className="insight-run">{lastRun}</small>;
  if (!latest)
    return (
      <div className="insight-card insight-earlier">
        <span className="insight-card-text">{run}</span>
      </div>
    );
  return (
    <motion.button
      layoutId="tile-insight-card"
      className={`insight-card insight-earlier kind-${latest.kind}`}
      onClick={onOpen}
    >
      <KindMark kind={latest.kind} size={12} />
      <span className="insight-card-text">
        <span>Earlier insights</span>
        {run}
      </span>
      <ChevronRight size={14} aria-hidden="true" />
    </motion.button>
  );
}

// Phones have no corner: the current insight sits in a slim card under the
// health readings, and its panel rises from it.
export function InsightCard({
  insight,
  onOpen,
}: {
  insight: DailyInsight;
  onOpen: () => void;
}) {
  const kind = kindNames[insight.kind];
  return (
    <motion.button
      layoutId="tile-insight-card"
      className={`insight-card kind-${insight.kind}`}
      onClick={onOpen}
      aria-label={`Open insight · ${kind}: ${insight.title}`}
    >
      <span className="insight-card-mark">
        <KindMark kind={insight.kind} size={15} />
      </span>
      <span className="insight-card-text">
        <small>
          {kind} · {dayName(insight.generatedAt)}
        </small>
        <span>{insight.title}</span>
      </span>
      <ChevronRight size={15} aria-hidden="true" />
    </motion.button>
  );
}
