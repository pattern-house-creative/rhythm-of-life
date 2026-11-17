import { Suspense, lazy, useEffect, useId, useRef, useState } from "react";
import { motion } from "motion/react";
import {
  Check,
  Clock3,
  Download,
  ListChecks,
  Pause,
  Play,
  Settings2,
  Spline,
  X,
  PanelRight,
} from "lucide-react";
import {
  connectHints,
  isDemo,
  notConnected,
  stamp,
  statusLabel,
} from "./health";
import { ConnectHint } from "./ConnectHint";
import { EngineModels } from "./EngineModels";
import { SyncNow } from "./SyncNow";
import { instance } from "./instance";
import { glucoseSlot } from "../server/healthState";
import type { DemoState, KnowledgeEdge, ObservatoryData } from "./model";
import type { GraphNode } from "./knowledge";
import type { Layout, View } from "./App";
import { cornerNames, corners } from "./dashboards";
import type { Corner, DashboardCards, DashboardSummary } from "./dashboards";
import { cleanName } from "./screens";
import { GlucosePanel } from "./panels/GlucosePanel";
import { RecoveryPanel } from "./panels/RecoveryPanel";
import { TrainingPanel } from "./panels/TrainingPanel";
import { ChangedPanel } from "./panels/ChangedPanel";
import { InsightPanel } from "./panels/InsightPanel";
import type { ReadData } from "./panels/readings";
import type { DailyInsight, FeedbackValue, InsightRun } from "./insights";
import type { GraphSession } from "./liveGraph";
import { DashboardActions } from "./DashboardActions";
import type { DashboardScreen } from "./DashboardActions";
import type { DashboardParts } from "./DashboardView";

const DashboardView = lazy(() => import("./DashboardView"));
type InstallEvent = Event & {
  prompt: () => Promise<void>;
  userChoice: Promise<{ outcome: string }>;
};
export { Origin } from "./panels/Origin";
export function Details({
  view,
  behind = false,
  data,
  state,
  setState,
  paused,
  setPaused,
  idleReturn = true,
  setIdleReturn,
  automaticLinks = true,
  setAutomaticLinks,
  workItems = true,
  setWorkItems,
  layout,
  setLayout,
  arranged = false,
  home,
  screenName,
  close,
  selectNode,
  readData,
  changes,
  onSources,
  cards,
  dashboard,
  insight,
  from,
}: {
  view: View;
  // A full screen covers the panel, which waits under it.
  behind?: boolean;
  data: ObservatoryData;
  state: DemoState;
  setState: (s: DemoState) => void;
  paused: boolean;
  setPaused: (p: boolean) => void;
  idleReturn?: boolean;
  setIdleReturn?: (on: boolean) => void;
  // Whether the skies draw the dashed links, and turning that on or off (display only).
  automaticLinks?: boolean;
  setAutomaticLinks?: (on: boolean) => void;
  // Whether the skies draw work items' stars, and turning that on or off (display only). Offered only while the map has
  // a work item, or while they are hidden here.
  workItems?: boolean;
  setWorkItems?: (on: boolean) => void;
  layout?: Layout;
  setLayout?: (layout: Layout) => void;
  // This screen's Home is arranged by hand (a Home grid, docs/dev/screens.md 10.5): choosing a
  // layout puts that layout's design back.
  arranged?: boolean;
  // This screen's Home: a dashboard's title (null while the dashboards haven't been read), or
  // none for the default; whether that dashboard was deleted, or couldn't be opened here; and
  // going back to the default.
  home?: {
    dashboard: {
      title: string | null;
      missing: boolean;
      failed?: boolean;
    } | null;
    onRestore: () => void;
  };
  // This screen's name, as other screens and an assistant see it, and renaming it.
  screenName?: { name: string; onRename: (name: string) => Promise<boolean> };
  close: () => void;
  selectNode: (id: string) => void;
  // Reads a data star's recent days, for panels that show more than the summary.
  readData?: ReadData;
  // What changed lately in the knowledge sky, for its panel.
  changes?: { nodes: GraphNode[]; edges: KnowledgeEdge[] };
  onSources?: () => void;
  // A corner's dashboard, grown out of its card: it opens in the explorer's
  // reader beside its stars, fills the display, or becomes this screen's Home.
  dashboard?: {
    id: string;
    corner: Corner;
    title: string;
    session: GraphSession;
    onReader: () => void;
    screen?: DashboardScreen;
    // Its native parts: the map, the tiles and the cards (App.tsx).
    parts?: DashboardParts;
  };
  // Which dashboard's card each ambient corner shows instead of its stat card.
  cards?: {
    dashboards: DashboardSummary[];
    chosen: DashboardCards;
    onChoose: (corner: Corner, dashboard: string | null) => Promise<void>;
  };
  // The insight open in its panel, every insight for its history, and where
  // the panel grew from (the ambient corner, or the card on phones).
  insight?: {
    openId: string | null;
    insights: DailyInsight[];
    nodes: GraphNode[];
    from: string;
    pending: Record<string, FeedbackValue>;
    failed: Record<string, FeedbackValue>;
    runs: InsightRun[];
    onFeedback: (
      id: string,
      value: FeedbackValue,
      until?: string,
      note?: string,
    ) => void;
    onTrail: (insight: DailyInsight) => void;
    onOpen: (id: string) => void;
  };
  // Where a tile's panel grows from when it isn't Home's tile: a dashboard's part.
  from?: string | null;
}) {
  const ref = useRef<HTMLElement>(null);
  const automaticNote = useId();
  const workNote = useId();
  const [installMessage, setInstallMessage] = useState("");
  useEffect(() => {
    const before = document.activeElement as HTMLElement | null;
    return () => {
      if (before?.isConnected) before.focus({ preventScroll: true });
    };
  }, []);
  useEffect(() => {
    if (ref.current) ref.current.scrollTop = 0;
    ref.current?.focus({ preventScroll: true });
  }, [view]);
  const demo = isDemo(data);
  const blocked =
    demo && (state === "empty" || state === "loading" || state === "error");
  // A health panel whose source's adapter is off says so instead of showing empty readings.
  const slot =
    view === "glucose"
      ? glucoseSlot
      : view === "recovery" || view === "training"
        ? "garmin"
        : null;
  const offSlot = slot && notConnected(data, slot) ? slot : null;
  const name =
    view === "sources"
      ? "Sources & settings"
      : view === "dashboard"
        ? (dashboard?.title ?? "Dashboard")
        : view === "changed"
          ? "Changed lately"
          : view === "insight"
            ? "Insight"
            : view;
  const label =
    view === "changed"
      ? "changed lately"
      : view === "insight"
        ? "insight"
        : view;
  const opened =
    view === "insight"
      ? (insight?.insights.find((i) => i.id === insight.openId) ?? null)
      : null;
  const requestInstall = async () => {
    const event = (window as Window & { observatoryInstall?: InstallEvent })
      .observatoryInstall;
    if (event) {
      await event.prompt();
      const choice = await event.userChoice;
      setInstallMessage(
        choice.outcome === "accepted"
          ? "Installation accepted."
          : "Installation dismissed. Try again from your browser menu.",
      );
      delete (window as Window & { observatoryInstall?: InstallEvent })
        .observatoryInstall;
    } else
      setInstallMessage(
        "Use your browser’s “Install app” or “Add to Home Screen” option. On iPhone, open in Safari, tap Share, then Add to Home Screen. Availability depends on your browser.",
      );
  };
  return (
    <motion.div
      className={`detail-backdrop detail-${view}`}
      inert={behind}
      initial={{ opacity: 0 }}
      animate={{ opacity: 1 }}
      exit={{ opacity: 0 }}
      transition={{ duration: 0.35 }}
      // The sky stays in view beside the panel; tapping it returns there.
      onClick={(e) => {
        if (e.target === e.currentTarget) close();
      }}
    >
      <motion.section
        ref={ref}
        tabIndex={-1}
        role="dialog"
        aria-modal="true"
        aria-label={name}
        className="detail-panel"
        // A health tile grows into its panel and shrinks back into place;
        // the settings drawer slides in from the edge.
        layoutId={
          view === "sources"
            ? undefined
            : view === "insight"
              ? (insight?.from ?? "tile-changed")
              : view === "dashboard"
                ? `tile-${dashboard?.corner ?? "training"}`
                : (from ?? `tile-${view}`)
        }
        initial={view === "sources" ? { opacity: 0, x: 40 } : undefined}
        animate={view === "sources" ? { opacity: 1, x: 0 } : undefined}
        exit={view === "sources" ? { opacity: 0, x: 40 } : undefined}
        transition={{ type: "spring", stiffness: 260, damping: 32 }}
        onKeyDown={(e) => {
          // A dashboard's part held in the panel: Escape puts it back instead.
          if (
            e.key === "Escape" &&
            !document.documentElement.hasAttribute("data-arranging")
          )
            close();
          if (e.key === "Tab") {
            const f = Array.from(
              ref.current?.querySelectorAll<HTMLElement>(
                'button, a[href], input, select, summary, [tabindex="0"]',
              ) ?? [],
            ).filter((el) => !el.hasAttribute("disabled"));
            const first = f[0],
              last = f[f.length - 1];
            if (
              e.shiftKey &&
              (document.activeElement === first ||
                document.activeElement === ref.current)
            ) {
              e.preventDefault();
              last?.focus();
            } else if (!e.shiftKey && document.activeElement === last) {
              e.preventDefault();
              first?.focus();
            }
          }
        }}
      >
        <div className="detail-top">
          <span className="eyebrow">RHYTHM OF LIFE / {label}</span>
          <button
            className="icon-button"
            aria-label={`Close ${label}`}
            onClick={close}
          >
            <X size={19} />
          </button>
        </div>
        {/* Contents settle in once the panel has grown out of its tile. */}
        <motion.div
          initial={{ opacity: 0 }}
          animate={{ opacity: 1 }}
          transition={{ delay: view === "sources" ? 0 : 0.16, duration: 0.25 }}
        >
          {view === "dashboard" && dashboard ? (
            <div className="dashboard-panel">
              <div className="dashboard-panel-head">
                <h2>{dashboard.title}</h2>
                <div className="dashboard-panel-actions">
                  <button
                    className="dashboard-full"
                    onClick={dashboard.onReader}
                  >
                    <PanelRight size={14} aria-hidden="true" /> Open in the
                    explorer
                  </button>
                  {dashboard.screen && (
                    <DashboardActions
                      id={dashboard.id}
                      screen={dashboard.screen}
                      className="dashboard-full"
                    />
                  )}
                </div>
              </div>
              <Suspense
                fallback={
                  <p className="dashboard-status" aria-busy="true">
                    Opening this dashboard…
                  </p>
                }
              >
                <DashboardView
                  id={dashboard.id}
                  session={dashboard.session}
                  onOpenStar={selectNode}
                  {...dashboard.parts}
                />
              </Suspense>
            </div>
          ) : view === "changed" ? (
            <ChangedPanel
              nodes={changes?.nodes ?? []}
              edges={changes?.edges ?? []}
              sources={data.sources}
              onOpen={selectNode}
              onSources={onSources ?? close}
            />
          ) : view === "insight" ? (
            <InsightPanel
              insight={opened}
              insights={insight?.insights ?? []}
              nodes={insight?.nodes ?? []}
              demo={demo}
              pending={opened ? insight?.pending[opened.id] : undefined}
              failed={opened ? insight?.failed[opened.id] : undefined}
              runs={insight?.runs ?? []}
              onFeedback={(value, until, note) =>
                opened && insight?.onFeedback(opened.id, value, until, note)
              }
              onTrail={(i) => insight?.onTrail(i)}
              onStar={selectNode}
              onOpenInsight={(id) => insight?.onOpen(id)}
            />
          ) : offSlot ? (
            <div className="empty-detail">
              <h3>Not connected</h3>
              <p>
                <ConnectHint slot={offSlot} /> Its readings show here once it
                has synced.
              </p>
              {onSources && (
                <button onClick={onSources}>Sources & settings</button>
              )}
            </div>
          ) : view !== "sources" && blocked ? (
            <div className="empty-detail">
              <h3>
                {state === "loading"
                  ? "Loading preview…"
                  : state === "error"
                    ? "Sample could not load"
                    : "No health records"}
              </h3>
              <button onClick={() => setState("ready")}>Load demo data</button>
            </div>
          ) : (
            <>
              {view === "glucose" && (
                <GlucosePanel
                  data={data}
                  state={state}
                  selectNode={selectNode}
                  readData={readData}
                />
              )}
              {view === "recovery" && (
                <RecoveryPanel
                  data={data}
                  selectNode={selectNode}
                  readData={readData}
                />
              )}
              {view === "training" && (
                <TrainingPanel
                  data={data}
                  selectNode={selectNode}
                  readData={readData}
                />
              )}
            </>
          )}
          {view === "sources" && (
            <>
              <div className="detail-title">
                <div>
                  <p className="eyebrow">
                    {demo ? "DEMO ENVIRONMENT" : "SOURCE STATUS"}
                  </p>
                  <h2>Sources & settings</h2>
                  <p>
                    {demo
                      ? "Every signal here is fictional. No account is connected."
                      : "Measurements keep their source, observation time and availability. Missing readings are never replaced with demo data."}
                  </p>
                </div>
                <Settings2 size={28} strokeWidth={1} />
              </div>
              <div className="sources-list">
                {data.sources.map((s) => (
                  <div key={s.id}>
                    <span>
                      <b>{s.name}</b>
                      {s.detail && <small>{s.detail}</small>}
                      <small>
                        {s.calendarDate
                          ? `Source calendar date: ${s.calendarDate}`
                          : `Observed: ${stamp(s.sampleAt)}`}
                      </small>
                      {s.lastSyncedAt && (
                        <small>
                          Last successful sync: {stamp(s.lastSyncedAt)}
                        </small>
                      )}
                      {s.retrievedAt && (
                        <small>Retrieved: {stamp(s.retrievedAt)}</small>
                      )}
                      {s.status === "disconnected" && connectHints[s.id] && (
                        <small>
                          <ConnectHint slot={s.id} />
                        </small>
                      )}
                    </span>
                    <span className="source-status">
                      {statusLabel(s).toUpperCase()}
                      {s.status !== "disconnected" &&
                        ` · ${s.connected ? "AUTHORIZED" : "DISCONNECTED"}`}
                    </span>
                  </div>
                ))}
              </div>
              {/* Sync now for each source on the engine's clock (SB-P6-09); the demo has none. */}
              {instance().mode === "live" && <SyncNow />}
              {/* Each model's job and state, from the engine; the demo has none. */}
              {instance().mode === "live" && <EngineModels />}
              <div className="capability-note">
                <h3>Designed around available signals</h3>
                <p>
                  What your sources record, each with its observation time:
                  numbers from your own files, such as sleep, steps, heart rate
                  or mood, and once connected, glucose readings and a watch's
                  sleep, heart rate, steps and workouts.
                </p>
                <p>
                  {demo
                    ? "All values here remain fictional."
                    : "Readings may be missing or stale."}{" "}
                  Field availability can vary by day. Sleep scores, readiness
                  assessments, scheduled workouts and automatic knowledge
                  relationships are not assumed.
                </p>
              </div>
              {demo && (
                <label className="state-picker">
                  Preview data state
                  <select
                    value={state}
                    onChange={(e) => setState(e.target.value as DemoState)}
                    aria-label="Preview data state"
                  >
                    <option value="ready">Ready demo</option>
                    <option value="stale">Stale readings</option>
                    <option value="empty">Empty data</option>
                    <option value="loading">Loading</option>
                    <option value="error">Source error</option>
                  </select>
                </label>
              )}
              {demo && (
                <p className="chart-note">
                  State previews demonstrate unavailable data without connecting
                  to any provider. Browser connectivity is reported separately.
                </p>
              )}
              {setLayout && (
                <div className="layout-choice">
                  <h3>Layout</h3>
                  <div
                    className="segmented"
                    role="group"
                    aria-label="Layout of the home screen"
                  >
                    {(
                      [
                        ["corners", "Corners"],
                        ["hero", "Tall column"],
                        ["band", "Rhythm band"],
                      ] as const
                    ).map(([id, label]) => (
                      <button
                        key={id}
                        aria-pressed={!arranged && layout === id}
                        onClick={() => setLayout(id)}
                      >
                        {label}
                      </button>
                    ))}
                  </div>
                  {arranged && (
                    <p className="chart-note">
                      This screen's Home is arranged by hand. Choosing a layout
                      puts that layout's design back.
                    </p>
                  )}
                </div>
              )}
              {home && <HomeChoice {...home} />}
              {screenName && (
                <ScreenName
                  name={screenName.name}
                  onRename={screenName.onRename}
                />
              )}
              {cards && (
                <CornerCards
                  dashboards={cards.dashboards}
                  chosen={cards.chosen}
                  onChoose={cards.onChoose}
                />
              )}
              <div className="settings-actions">
                <button onClick={() => setPaused(!paused)}>
                  {paused ? <Play size={16} /> : <Pause size={16} />}{" "}
                  {paused ? "Resume ambient motion" : "Pause ambient motion"}
                </button>
                {setIdleReturn && (
                  <button
                    aria-pressed={idleReturn}
                    onClick={() => setIdleReturn(!idleReturn)}
                  >
                    <Clock3 size={16} />{" "}
                    {idleReturn
                      ? "Return to the sky after 2 idle minutes: on"
                      : "Return to the sky after 2 idle minutes: off"}
                  </button>
                )}
                {/* Display only: off hides the dashed links on the skies, never from the map or a star's panel. */}
                {setAutomaticLinks && (
                  <button
                    aria-pressed={automaticLinks}
                    aria-describedby={automaticNote}
                    onClick={() => setAutomaticLinks(!automaticLinks)}
                  >
                    <Spline size={16} aria-hidden="true" />{" "}
                    {automaticLinks
                      ? "Automatic links on the sky: on"
                      : "Automatic links on the sky: off"}
                  </button>
                )}
                {/* Display only: off leaves work items' stars off the skies, never out of the map or the Memories view. */}
                {setWorkItems && (
                  <button
                    aria-pressed={workItems}
                    aria-describedby={workNote}
                    onClick={() => setWorkItems(!workItems)}
                  >
                    <ListChecks size={16} aria-hidden="true" />{" "}
                    {workItems
                      ? "Work items on the sky: on"
                      : "Work items on the sky: off"}
                  </button>
                )}
                <button onClick={requestInstall}>
                  <Download size={16} /> Install Rhythm of Life
                </button>
              </div>
              {setAutomaticLinks && (
                <p className="chart-note" id={automaticNote}>
                  {automaticLinks
                    ? "Automatic links are the dashed lines nobody has confirmed: stars alike in meaning, topics, plugins' and Second Brain's links, and suggestions."
                    : "Automatic links are hidden on the sky. A star you choose still shows all of its own, and its panel still lists them."}
                </p>
              )}
              {setWorkItems && (
                <p className="chart-note" id={workNote}>
                  {workItems
                    ? "Work items are a project's tasks, bugs, features and improvements: stars drawn as a small box."
                    : "Work items are hidden on the sky. Nothing is deleted: the Memories view still lists them, and one you choose still shows."}
                </p>
              )}
              {installMessage && (
                <p className="install-message" role="status">
                  {installMessage}
                </p>
              )}
              <div className="origin">
                <Check size={15} />
                <p>
                  Only static interface assets are cached. Measurements require
                  an authenticated connection and are cleared when the tab is
                  hidden or access is lost.
                </p>
              </div>
            </>
          )}
        </motion.div>
      </motion.section>
    </motion.div>
  );
}

// What a corner shows with no dashboard chosen for it.
const cornerTiles: Record<Corner, string> = {
  main: "Glucose tile when connected, else a hint",
  recovery: "Garmin recovery when connected",
  training: "Garmin training when connected",
  changed: "Changed lately",
};
// Each corner of the ambient screen shows its own tile, or a dashboard's card:
// tall in the main slot, compact in the others. Tapping a card opens its
// dashboard.
function CornerCards({
  dashboards,
  chosen,
  onChoose,
}: {
  dashboards: DashboardSummary[];
  chosen: DashboardCards;
  onChoose: (corner: Corner, dashboard: string | null) => Promise<void>;
}) {
  const [saving, setSaving] = useState<Corner | null>(null);
  const [failed, setFailed] = useState("");
  const choose = async (corner: Corner, value: string) => {
    setSaving(corner);
    setFailed("");
    try {
      await onChoose(corner, value || null);
    } catch {
      setFailed(
        `The ${cornerNames[corner]} corner couldn't be changed right now.`,
      );
    } finally {
      setSaving(null);
    }
  };
  return (
    <div className="corner-cards">
      <h3>Corner cards</h3>
      <p className="chart-note">
        {dashboards.length
          ? "Each corner shows its own tile, or a dashboard's card instead: tall in the main slot on the left, compact in the others."
          : "Once your AI saves a dashboard, its card can go in a corner here: tall in the main slot on the left, compact in the others."}
      </p>
      {dashboards.length > 0 &&
        corners.map((corner) => (
          <label key={corner} className="corner-card">
            <span>{cornerNames[corner]}</span>
            <select
              aria-label={`${cornerNames[corner]} corner`}
              value={chosen[corner] ?? ""}
              disabled={saving !== null}
              onChange={(e) => void choose(corner, e.target.value)}
            >
              <option value="">{cornerTiles[corner]}</option>
              {dashboards.map((d) => (
                <option key={d.id} value={d.id}>
                  {d.title}
                </option>
              ))}
            </select>
          </label>
        ))}
      {failed && (
        <p className="chart-note" role="status">
          {failed}
        </p>
      )}
    </div>
  );
}

// This screen's Home, next to the layout it keeps: the sky and tiles, or a dashboard it shows
// full screen each time it goes Home (chosen from the dashboard itself, Set as Home here).
function HomeChoice({
  dashboard,
  onRestore,
}: NonNullable<Parameters<typeof Details>[0]["home"]>) {
  return (
    <div className="home-choice">
      <h3>Home</h3>
      <p className="chart-note">
        {!dashboard
          ? "This screen's Home is the sky and tiles. To show a dashboard here instead, open it and choose Set as Home here."
          : dashboard.missing
            ? "This screen's Home dashboard was deleted, so it shows the sky and tiles."
            : dashboard.failed
              ? "This screen's Home dashboard couldn't be opened here, so it shows the sky and tiles until the app is reloaded."
              : `This screen's Home is ${dashboard.title ?? "a saved dashboard"}, full screen: it shows each time this screen goes Home.`}
      </p>
      {dashboard && (
        <button className="dashboard-full" onClick={onRestore}>
          Restore default Home
        </button>
      )}
    </div>
  );
}

// This screen's name, next to the layout it keeps: what "Show on…" on another screen and an
// assistant call it. Saved on Enter or when the field is left. A rename made in another tab
// shows here unless someone is changing the name here, so leaving the field never puts the
// old name back.
export function ScreenName({
  name,
  onRename,
}: {
  name: string;
  onRename: (name: string) => Promise<boolean>;
}) {
  const [value, setValue] = useState(name);
  const [from, setFrom] = useState(name);
  if (name !== from) {
    setFrom(name);
    if (value.trim() === from) setValue(name);
  }
  const [said, setSaid] = useState("");
  const save = async () => {
    // Counted in characters, as the engine counts them: an emoji is one.
    const next = cleanName(value);
    if (next === name) return;
    if (!next) {
      setSaid("A name takes 1 to 60 characters.");
      return;
    }
    setSaid(
      (await onRename(next))
        ? "Saved."
        : "Saved on this screen; other screens see it once it reaches them.",
    );
  };
  return (
    <form
      className="screen-name"
      onSubmit={(e) => {
        e.preventDefault();
        void save();
      }}
    >
      <label>
        <span>This screen's name</span>
        <input
          value={value}
          onChange={(e) => {
            setValue(Array.from(e.target.value).slice(0, 60).join(""));
            setSaid("");
          }}
          onBlur={() => void save()}
        />
      </label>
      {said && (
        <p className="chart-note" role="status">
          {said}
        </p>
      )}
    </form>
  );
}
