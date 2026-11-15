import { useEffect, useRef, useState } from "react";
import type { ReactNode } from "react";
import {
  AlarmClock,
  ArrowUpRight,
  Brain,
  Check,
  CheckCheck,
  CircleSlash2,
  ExternalLink,
  FileText,
  Globe,
  HardDrive,
  Heart,
  Sparkle,
  Telescope,
} from "lucide-react";
import type { LucideIcon } from "lucide-react";
import type { GraphNode } from "../knowledge";
import {
  clockTime,
  dayName,
  kindNames,
  laterName,
  laterUntil,
  newestFirst,
  pointNames,
  pointOrder,
  runLine,
  sourceStar,
  statusName,
} from "../insights";
import type {
  DailyInsight,
  FeedbackValue,
  InsightRun,
  InsightSource,
} from "../insights";
import { KindMark } from "../InsightCard";
import "./InsightPanel.css";

// One daily insight, read in full: what it says,
// why it matters and what to do next, then what it rests on (each point
// marked as observed, from outside, inferred or a hypothesis), its sources and
// limits, a way to see its trail in the sky, and feedback on it. Earlier
// insights are listed quietly at the foot. Every line of an insight's text is
// untrusted, and only ever rendered as text.
const sourceIcons: Record<InsightSource["kind"], LucideIcon> = {
  web: Globe,
  drive: FileText,
  document: FileText,
  memory: Brain,
  "second-brain": Brain,
  record: HardDrive,
  rhythm: HardDrive,
  nas: HardDrive,
  graph: Sparkle,
};
const sourceNames: Record<InsightSource["kind"], string> = {
  web: "Web",
  drive: "Google Drive",
  document: "Document",
  memory: "Memory",
  "second-brain": "Second Brain",
  record: "Rhythm record",
  rhythm: "Rhythm engine",
  nas: "Rhythm engine",
  graph: "Knowledge sky",
};
const madeByNames: Record<string, string> = {
  "harness-daily": "Your AI's daily look",
  "harness-weekly": "Your AI's weekly look",
  "chatgpt-daily": "ChatGPT's daily look",
  "chatgpt-weekly": "ChatGPT's weekly look",
  manual: "Added by hand",
};
const madeBy = (label: DailyInsight["generatedBy"]) =>
  Object.hasOwn(madeByNames, label) ? madeByNames[label] : `Saved by ${label}`;
const evidenceNames = { strong: 3, moderate: 2, weak: 1 } as const;
const feedbackOptions: [FeedbackValue, string, LucideIcon][] = [
  ["interesting", "Interesting", Heart],
  ["explore", "Explore", Telescope],
  ["not_useful", "Not useful", CircleSlash2],
  ["later", "Remind me later", AlarmClock],
  ["handled", "Already handled", CheckCheck],
];

// A link only ever opens an https page, in a new tab.
function safeLink(url: string | undefined) {
  if (!url) return null;
  try {
    return new URL(url).protocol === "https:" ? url : null;
  } catch {
    return null;
  }
}
const host = (url: string) => new URL(url).hostname.replace(/^www\./, "");
const when = (iso: string, now: number) => {
  const day = dayName(iso, now);
  return day === "today" || day === "yesterday"
    ? `${day}, ${clockTime(iso)}`
    : day;
};

export function InsightPanel({
  insight,
  insights,
  nodes,
  demo,
  runs = [],
  pending,
  failed,
  onFeedback,
  onTrail,
  onStar,
  onOpenInsight,
}: {
  // The insight open in the panel, and every insight for the history.
  insight: DailyInsight | null;
  insights: DailyInsight[];
  // The loaded sky, to open a source's star.
  nodes: GraphNode[];
  demo: boolean;
  // The engine's latest runs, newest first.
  runs?: InsightRun[];
  pending?: FeedbackValue;
  failed?: FeedbackValue;
  onFeedback: (value: FeedbackValue, until?: string, note?: string) => void;
  onTrail: (insight: DailyInsight) => void;
  onStar: (id: string) => void;
  onOpenInsight: (id: string) => void;
}) {
  const root = useRef<HTMLDivElement>(null),
    heading = useRef<HTMLHeadingElement>(null);
  const [now] = useState(() => Date.now());
  const [later, setLater] = useState(false);
  // "Already handled" asks what happened; any other choice can carry a note.
  const [handling, setHandling] = useState(false),
    [noting, setNoting] = useState(false),
    [note, setNote] = useState("");
  const id = insight?.id;
  // Another insight opened from the history starts at its top.
  const first = useRef(true);
  useEffect(() => {
    // eslint-disable-next-line react/set-state-in-effect
    setLater(false);
    setHandling(false);
    setNoting(false);
    setNote("");
    if (first.current) {
      first.current = false;
      return;
    }
    const scroller = root.current?.closest(".detail-panel");
    if (scroller) scroller.scrollTop = 0;
    heading.current?.focus({ preventScroll: true });
  }, [id]);
  const history = newestFirst(insights).filter((i) => i.id !== id);
  const lastRun = runLine(runs, now);
  // Sends a choice with the note, if any, and folds the note away.
  const send = (value: FeedbackValue, until?: string) => {
    onFeedback(value, until, note.trim() || undefined);
    setLater(false);
    setHandling(false);
    setNoting(false);
    setNote("");
  };
  const historyList = history.length > 0 && (
    <section className="insight-history" aria-label="Earlier insights">
      <h3>Earlier insights</h3>
      <ol>
        {history.slice(0, 20).map((i) => (
          <li key={i.id}>
            <button onClick={() => onOpenInsight(i.id)}>
              <KindMark kind={i.kind} />
              <span className="history-title">{i.title}</span>
              <small>
                {kindNames[i.kind]} · {dayName(i.generatedAt, now)}
              </small>
              <span className={`history-status ${i.status}`}>
                {statusName(i, now)}
              </span>
            </button>
          </li>
        ))}
      </ol>
    </section>
  );
  if (!insight)
    return (
      <div className="insight-panel" ref={root}>
        <div className="detail-title">
          <div>
            <p className="eyebrow">DAILY INSIGHT</p>
            <h2 ref={heading} tabIndex={-1}>
              No insight right now
            </h2>
            <p>When a new one is saved, it appears in the corner.</p>
          </div>
        </div>
        {lastRun && <p className="insight-run">{lastRun}</p>}
        {historyList}
      </div>
    );
  const loaded = new Set(nodes.map((n) => n.id));
  const lit = insight.stars.filter((s) => loaded.has(s.graphId)).length;
  // A source opens its page in a new tab, or its star in the sky; one with
  // neither is named without a link.
  const sourceAction = (source: InsightSource, children: ReactNode) => {
    const url =
      source.kind === "web" || source.kind === "drive"
        ? safeLink(source.url)
        : null;
    if (url)
      return (
        <a
          href={url}
          target="_blank"
          rel="noopener noreferrer"
          referrerPolicy="no-referrer"
        >
          {children}
          <ExternalLink size={12} aria-hidden="true" />
        </a>
      );
    const star = sourceStar(source, nodes);
    if (star)
      return (
        <button onClick={() => onStar(star)}>
          {children}
          <ArrowUpRight size={12} aria-hidden="true" />
        </button>
      );
    return <span>{children}</span>;
  };
  const points = pointOrder.flatMap((type) =>
    insight.points.filter((p) => p.type === type),
  );
  const chosen = pending ?? insight.feedback?.value;
  const until = insight.feedback?.until;
  const status = pending
    ? "Saving…"
    : failed
      ? "That didn't save this time."
      : insight.feedback?.value === "interesting"
        ? "Saved. Glad it was interesting."
        : insight.feedback?.value === "explore"
          ? "Marked as explored."
          : insight.feedback?.value === "not_useful"
            ? "Noted. It won't come back."
            : insight.feedback?.value === "later" && until
              ? `It will come back ${laterName(until, now)}.`
              : insight.feedback?.value === "handled"
                ? "Marked as handled. It won't come back."
                : "";
  const savedNote = !pending && !failed ? insight.feedback?.note : null;
  const level = evidenceNames[insight.evidenceLevel];
  return (
    <div className={`insight-panel kind-${insight.kind}`} ref={root}>
      <header className="insight-head">
        <p className="insight-eyebrow">
          <KindMark kind={insight.kind} size={15} />
          {kindNames[insight.kind].toUpperCase()}
          <span>· {when(insight.generatedAt, now).toUpperCase()}</span>
          {demo && <span className="insight-sample">SAMPLE</span>}
        </p>
        <h2 ref={heading} tabIndex={-1}>
          {insight.title}
        </h2>
        {insight.summary && (
          <p className="insight-summary">{insight.summary}</p>
        )}
        <p
          className="insight-evidence"
          aria-label={`${insight.evidenceLevel} evidence`}
        >
          <span className="evidence-dots" aria-hidden="true">
            {[1, 2, 3].map((n) => (
              <i key={n} className={n <= level ? "on" : ""} />
            ))}
          </span>
          {insight.evidenceLevel[0].toUpperCase() +
            insight.evidenceLevel.slice(1)}{" "}
          evidence
          <span>
            · {insight.sources.length}{" "}
            {insight.sources.length === 1 ? "source" : "sources"}
          </span>
        </p>
      </header>
      {insight.whyItMatters && (
        <section className="insight-why">
          <h3>Why it matters</h3>
          <p>{insight.whyItMatters}</p>
        </section>
      )}
      {insight.nextStep && (
        <section className="insight-next">
          <h3>Next step</h3>
          <p>{insight.nextStep}</p>
        </section>
      )}
      <div className="insight-sky">
        <button
          className="insight-sky-button"
          onClick={() => onTrail(insight)}
          disabled={!lit}
        >
          <span className="sky-glyph" aria-hidden="true">
            <i />
            <i />
            <i />
          </span>
          Show in the sky
        </button>
        <span>
          {lit
            ? `Lights its ${lit} ${lit === 1 ? "star" : "stars"} and frames them`
            : insight.stars.length
              ? "Its stars aren't in the loaded sky yet."
              : "This insight names no stars."}
        </span>
      </div>
      {points.length > 0 && (
        <section className="insight-points">
          <h3>What it rests on</h3>
          <ol>
            {points.map((p, i) => {
              const source =
                p.source !== undefined ? insight.sources[p.source] : undefined;
              return (
                <li key={i} className={`insight-point ${p.type}`}>
                  <span className="point-type">
                    <i aria-hidden="true" />
                    {pointNames[p.type]}
                  </span>
                  <p>{p.text}</p>
                  {source && (
                    <span className="point-source">
                      {sourceAction(
                        source,
                        <>
                          <b>{p.source! + 1}</b> {source.title}
                        </>,
                      )}
                    </span>
                  )}
                </li>
              );
            })}
          </ol>
        </section>
      )}
      {insight.sources.length > 0 && (
        <section className="insight-sources">
          <h3>Sources</h3>
          <ol>
            {insight.sources.map((s, i) => {
              const Icon = sourceIcons[s.kind];
              const url = safeLink(s.url);
              const star = sourceStar(s, nodes);
              return (
                <li key={i}>
                  <span className="source-number">{i + 1}</span>
                  <Icon size={15} aria-hidden="true" className="source-icon" />
                  <div>
                    <span className="source-title">
                      {sourceAction(s, s.title)}
                    </span>
                    <small>
                      {s.kind === "web" && url
                        ? host(url)
                        : sourceNames[s.kind]}
                      {s.publishedAt &&
                        ` · published ${dayName(s.publishedAt, now)}`}
                      {` · read ${dayName(s.retrievedAt, now)}`}
                      {!url && !star && s.kind !== "web" && s.kind !== "drive"
                        ? " · not in the loaded sky"
                        : ""}
                    </small>
                  </div>
                </li>
              );
            })}
          </ol>
        </section>
      )}
      {insight.limitations && (
        <section className="insight-limits">
          <h3>Limitations</h3>
          <p>{insight.limitations}</p>
        </section>
      )}
      <section className="insight-feedback" aria-label="Feedback">
        <h3>Was this useful?</h3>
        <div className="feedback-buttons">
          {feedbackOptions.map(([value, label, Icon]) => (
            <button
              key={value}
              aria-pressed={chosen === value}
              aria-expanded={
                value === "later"
                  ? later
                  : value === "handled"
                    ? handling
                    : undefined
              }
              disabled={!!pending}
              className={pending === value ? "saving" : ""}
              onClick={() => {
                if (value === "later") {
                  setHandling(false);
                  return setLater(!later);
                }
                if (value === "handled") {
                  setLater(false);
                  return setHandling(!handling);
                }
                send(value);
                if (value === "explore") onTrail(insight);
              }}
            >
              {chosen === value && !pending ? (
                <Check size={14} aria-hidden="true" />
              ) : (
                <Icon size={14} aria-hidden="true" />
              )}
              {label}
            </button>
          ))}
        </div>
        {later && (
          <div className="feedback-later" role="group" aria-label="Remind me">
            {(
              [
                ["tomorrow", "Tomorrow morning"],
                ["week", "Next week"],
              ] as const
            ).map(([choice, label]) => (
              <button
                key={choice}
                disabled={!!pending}
                onClick={() => send("later", laterUntil(choice))}
              >
                {label}
              </button>
            ))}
          </div>
        )}
        {handling ? (
          <form
            className="feedback-note"
            onSubmit={(e) => {
              e.preventDefault();
              if (note.trim()) send("handled");
            }}
          >
            <input
              aria-label="What happened?"
              placeholder="What happened?"
              required
              maxLength={500}
              value={note}
              autoFocus
              onChange={(e) => setNote(e.target.value)}
            />
            <button type="submit" disabled={!note.trim() || !!pending}>
              Mark handled
            </button>
          </form>
        ) : (
          <div className="feedback-note optional">
            <button
              className="note-toggle"
              aria-expanded={noting}
              onClick={() => setNoting(!noting)}
            >
              {noting ? "No note" : "Add a note"}
            </button>
            {noting && (
              <input
                aria-label="Note"
                placeholder="A few words to go with your choice"
                maxLength={500}
                value={note}
                autoFocus
                onChange={(e) => setNote(e.target.value)}
              />
            )}
          </div>
        )}
        <p className="feedback-status" role="status">
          {status}
        </p>
        {savedNote && <p className="feedback-saved-note">“{savedNote}”</p>}
      </section>
      <p className="insight-made">
        {demo
          ? "A fictional sample insight. No real sources were read."
          : `${madeBy(insight.generatedBy)} · saved ${when(insight.generatedAt, now)} · sources as of ${when(insight.sourcesAsOf, now)}`}
        {lastRun && (
          <span className="insight-run">
            <br />
            {lastRun}
          </span>
        )}
      </p>
      {historyList}
    </div>
  );
}
