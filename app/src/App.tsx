import {
  lazy,
  Suspense,
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
  useMemo,
  useCallback,
  useSyncExternalStore,
} from "react";
import type { ReactNode, RefObject } from "react";
import { AnimatePresence, MotionConfig, motion } from "motion/react";
import { ChevronRight, Settings2, RefreshCw, WifiOff } from "lucide-react";
import { isDemo, previewHealth, stamp, statusLabel } from "./health";
import { createApiSession } from "./apiClient";
import { reloadFromNetwork } from "./appUpdates";
import { catalogProvider, instance } from "./instance";
import { launcherTarget } from "./launcherNavigation";
import { useHealthRefresh } from "./useHealthRefresh";
import { useKnowledge } from "./useKnowledge";
import { useDashboards } from "./useDashboards";
import { dashboardSaved } from "./useDashboardVersion";
import { useMapRebuild } from "./useMapRebuild";
import {
  dashboardOf,
  dashboardStarId,
  isDashboardStar,
  withDashboards,
} from "./dashboards";
import type { Corner } from "./dashboards";
import type { CardShape } from "./dashboardFrame";
import { ambientWindow, workOf } from "./graphView";
import { displayTitle } from "./graph";
import { filterNodes } from "./model";
import {
  adoptScreenName,
  cleanName,
  cut,
  defaultTiles,
  demoScreenIdentity,
  followHome,
  saveGrid,
  saveHome,
  saveLayout,
  saveScreenName,
  saveAutomaticLinks,
  saveTiles,
  saveWorkItems,
  savedGrid,
  savedHome,
  savedLayout,
  savedAutomaticLinks,
  savedTiles,
  savedWorkItems,
  screenIdentity,
  settingsStored,
  takeNewScreenId,
  watchScreenName,
  watchSettings,
} from "./screens";
import type { FullView, Home, Layout, Showing, Tile, Tiles } from "./screens";
import { LiveScreen } from "./LiveScreen";
import { FullScreen } from "./FullScreen";
import { Studio } from "./Studio";
import type { ScreenActions, ScreenView } from "./LiveScreen";
import type { DemoState } from "./model";
import { FeatureBoundary } from "./FeatureBoundary";
import { RhythmMark } from "./RhythmMark";
import { ResurfaceCard } from "./ResurfaceCard";
import { EarlierInsights, InsightCard } from "./InsightCard";
import { useInsights } from "./useInsights";
import type { DailyInsight } from "./insights";
import { glucoseSlot } from "../server/healthState";
import { canonical, cleanHomeGrid, firstHomeGrid, partLabel } from "./parts";
import type { HomeGrid, HomeId, Part } from "./parts";
import { PartsGrid } from "./PartsGrid";
import type { GridChange, GridOutcome, GridPart } from "./PartsGrid";
import { arrangeInPlace, measureHomeGrid, refuseInPlace } from "./arranging";
import type { DashboardParts } from "./DashboardView";
import type { Dashboard } from "./dashboards";
import type { Trail } from "./Explorer";
import { CardPart } from "./parts/CardPart";
import { ChangedPart } from "./parts/ChangedPart";
import { GlucosePart, MainHint } from "./parts/GlucosePart";
import { MapPart } from "./parts/MapPart";
import { PartBoundary } from "./parts/PartBoundary";
import type { ExploreLit, MapSky } from "./parts/MapPart";
import { nativeParts } from "./parts/nativeParts";
import { garminOn, glucoseOn, insightsShown } from "./parts/partData";
import { RecoveryPart } from "./parts/RecoveryPart";
import { TrainingPart } from "./parts/TrainingPart";
const Details = lazy(() =>
  import("./Details").then((m) => ({ default: m.Details })),
);
const Explorer = lazy(() => import("./Explorer"));
const DashboardCard = lazy(() => import("./DashboardCard"));
import "./App.css";
export type View =
  | "ambient"
  | "explore"
  | "glucose"
  | "recovery"
  | "training"
  | "changed"
  | "insight"
  | "dashboard"
  | "sources";
// With no input for this long, an open view eases back to the ambient sky.
export const idleReturnMs = 120_000;
const noStars: readonly string[] = [];
// How the tiles sit around the sky: one in each corner, the main slot as a
// full-height column on the left, or all four in a band along the bottom
// (screens.ts keeps it, and where each tile sits, for this screen).
export type { Layout };
// A tile's place as its grid area (App.css); the changed tile's is "context".
const area = (place: Tile) => (place === "changed" ? "context" : place);
// Whether a media query matches now, following it as the window changes.
function useMedia(query: string) {
  return useSyncExternalStore(
    (change) => {
      const list = window.matchMedia(query);
      list.addEventListener("change", change);
      return () => list.removeEventListener("change", change);
    },
    () => window.matchMedia(query).matches,
  );
}
function Clock() {
  const [time, setTime] = useState(() => new Date());
  useEffect(() => {
    const t = setInterval(() => setTime(new Date()), 30000);
    return () => clearInterval(t);
  }, []);
  return (
    <time dateTime={time.toISOString()}>
      {time.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}
      <span>LOCAL</span>
    </time>
  );
}
// A Home part's group label: "Map", or the corner's name as Sources & settings says it.
const homeLabel = (part: GridPart) =>
  part.id === "map" ? "Map" : `${partLabel({ parts: [part] }, part.id)} corner`;
// A Home part dropped where it can't go says why in the screen's note.
const refuseHome = (reason: string) => void refuseInPlace(reason);
// The grips over a Home drawn as today (docs/dev/screens.md 10.10): one for each of its five
// parts, where the grid the first move or resize would make puts it, measured from what Home
// draws (10.5). Nothing moves until someone uses one.
function HomeArranging({
  home,
  layout,
  tiles,
  drawn,
  arrange,
}: {
  home: RefObject<HTMLDivElement | null>;
  layout: Layout;
  tiles: Tiles;
  // Home's empty corners, which change the areas it draws.
  drawn: string | undefined;
  arrange: (change: GridChange<GridPart>) => Promise<GridOutcome>;
}) {
  const [first, setFirst] = useState(() => firstHomeGrid(layout, tiles));
  useLayoutEffect(() => {
    const element = home.current;
    if (!element) return;
    const measure = () => {
      const next = firstHomeGrid(
        layout,
        tiles,
        measureHomeGrid(element, tiles),
      );
      setFirst((was) => (canonical(was) === canonical(next) ? was : next));
    };
    measure();
    if (typeof ResizeObserver === "undefined") return;
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    return () => observer.disconnect();
  }, [home, layout, tiles, drawn]);
  return (
    <PartsGrid
      parts={first.parts}
      homeMode
      overlay
      label={homeLabel}
      arrange={arrange}
      refused={refuseHome}
    />
  );
}
export default function App({ launcher = false }: { launcher?: boolean }) {
  const [launcherEntry, setLauncherEntry] = useState(() =>
    launcherTarget(location.hash),
  );
  const [launcherPending, setLauncherPending] = useState(!!launcherEntry);
  const [launcherError, setLauncherError] = useState(false);
  const [launcherRetry, setLauncherRetry] = useState(0);
  const ambientScroll = useRef(0);
  const [view, setView] = useState<View>("ambient"),
    [selected, setSelected] = useState<string | null>(null);
  const [state, setState] = useState<DemoState>("ready"),
    [paused, setPaused] = useState(false);
  const browserOriginal = useRef<string | null>(null);
  const {
    data: rawData,
    refreshing,
    refresh,
  } = useHealthRefresh(
    useCallback((failed: boolean) => {
      // Back from the Android browser reopens the file after a fresh access check.
      // Only its identifier survives hiding the reader.
      const openReader = browserOriginal.current;
      browserOriginal.current = null;
      if (!failed && document.hidden && openReader) {
        setLauncherEntry({ kind: "star", id: openReader });
        setLauncherPending(true);
      }
      setView("ambient");
      setSelected(null);
      setState(failed ? "error" : "ready");
    }, []),
  );
  const [online, setOnline] = useState(navigator.onLine);
  const hasData = !!rawData;
  // Access came back by itself (useHealthRefresh tries again): the error it left goes with it.
  useEffect(() => {
    // eslint-disable-next-line react/set-state-in-effect
    if (hasData) setState((s) => (s === "error" ? "ready" : s));
  }, [hasData]);
  const graphDemo = rawData ? isDemo(rawData) : false;
  const data = useMemo(
    () => (rawData ? previewHealth(rawData, state) : null),
    [rawData, state],
  );
  const session = useMemo(
    () => (hasData ? createApiSession(graphDemo) : null),
    [hasData, graphDemo],
  );
  // The star a command opened, which may lie beyond the map's overview (the
  // engine checks its whole map), and a dashboard one opened whatever stars it
  // reads: both kept on the sky until Home.
  const [keptStar, setKeptStar] = useState<string | null>(null),
    [keptDashboard, setKeptDashboard] = useState<string | null>(null);
  // The stars a dashboard's focused map lit in the explorer (its Explore), kept until Home too.
  const [keptLit, setKeptLit] = useState<readonly string[]>(noStars);
  const knowledge = useKnowledge(session, keptStar, keptLit);
  const insights = useInsights(session);
  // Saved dashboards join the sky as stars of their own, linked to every star
  // they read.
  const dashboards = useDashboards(session);
  const skyPage = useMemo(
    () =>
      withDashboards(knowledge.page, dashboards.list, graphDemo, keptDashboard),
    [knowledge.page, dashboards.list, graphDemo, keptDashboard],
  );
  const sky = useMemo(
    () => ({ ...knowledge, page: skyPage }),
    [knowledge, skyPage],
  );
  // Refresh also asks the engine to rebuild the map, so a change at a source,
  // like a deleted Drive file, shows within a couple of minutes.
  const { refresh: rereadMap } = knowledge,
    { reload: rereadDashboards } = dashboards;
  const rebuildMap = useMapRebuild(
    session,
    useCallback(() => {
      void rereadMap().catch(() => {});
      void rereadDashboards();
    }, [rereadMap, rereadDashboards]),
  );
  // A dashboard to open in the explorer's reader as it opens.
  const [reader, setReader] = useState<string | null>(null);
  // What a command asks of the explorer, even while it is open (a star, a
  // dashboard in the reader, a search), and what the explorer shows now.
  const [request, setRequest] = useState<{
    nonce: number;
    star?: string;
    reader?: string;
    search?: string;
    trail?: Trail;
  } | null>(null);
  const requests = useRef(0);
  const explorerState = useRef<{
    selected: string | null;
    reader: string | null;
    query: string;
  }>({ selected: null, reader: null, query: "" });
  const onExplorerState = useCallback(
    (s: typeof explorerState.current) => {
      if (s.reader !== browserOriginal.current) browserOriginal.current = null;
      explorerState.current = s;
    },
    [],
  );
  // A view a command opened stays until the next command, Home, or someone
  // using this screen, instead of drifting back to the sky when idle.
  const [held, setHeld] = useState(false);
  // This copy of the app as a live screen (LiveScreen.tsx): its id and name,
  // a tab of its own in the demo, and where its tiles sit. A new id once the
  // engine refused this one as another app login's (a viewer session started
  // or ended in this browser, docs/dev/screens.md 3.2), or the one another tab
  // took then.
  const [taken, setTaken] = useState<ReturnType<typeof screenIdentity> | null>(
    null,
  );
  const identity = useMemo(
    () => (graphDemo ? demoScreenIdentity() : (taken ?? screenIdentity())),
    [graphDemo, taken],
  );
  const [renamed, setRenamed] = useState<string | null>(null);
  const takeScreenId = (refused: string) => {
    if (graphDemo) return false;
    const next = takeNewScreenId(refused);
    if (!next) return false;
    setTaken(next);
    setRenamed(null);
    return true;
  };
  const screen = { id: identity.id, name: renamed ?? identity.name };
  // With an engine, the tabs of one browser share its screen: a rename in
  // another tab shows here too (looked for again when the page is shown), so
  // this tab's next listen keeps it.
  useEffect(
    () => (graphDemo ? undefined : watchScreenName(identity.id, setRenamed)),
    [graphDemo, identity.id],
  );
  const [stored] = useState(settingsStored);
  const [tiles, setTilesState] = useState<Tiles>(
    () => savedTiles() ?? defaultTiles,
  );
  const setTiles = (next: Tiles) => {
    setTilesState(next);
    saveTiles(next);
  };
  // This screen's Home grid (docs/dev/screens.md 10.5): none until someone moves or resizes a
  // part of Home here, kept like the layout and tiles, and dropped when a layout is chosen.
  const [grid, setGridState] = useState<HomeGrid | null>(savedGrid);
  const setGrid = (next: HomeGrid | null) => {
    const kept = next && cleanHomeGrid(next);
    if (next && !kept) return;
    setGridState(kept);
    saveGrid(kept);
  };
  // Home as it is drawn, which the first move or resize measures.
  const homeRef = useRef<HTMLDivElement>(null);
  // A part moved from the grips over an unchanged Home keeps the keyboard's
  // place: once Home is drawn as a grid, that part's grip has focus.
  const regrip = useRef<string | null>(null);
  useEffect(() => {
    const id = regrip.current;
    regrip.current = null;
    if (id && document.activeElement === document.body)
      homeRef.current
        ?.querySelector<HTMLElement>(`[data-part="${id}"] > .parts-grip`)
        ?.focus({ preventScroll: true });
  }, [grid]);
  // This screen's Home: the sky and tiles, or a dashboard shown full screen
  // whenever it goes Home (and when it opens).
  const [home, setHomeState] = useState<Home>(savedHome);
  // A dashboard filling the display over whatever is open: this screen's Home,
  // or one shown until someone exits, Home or the next command (overHome when
  // it replaced the Home dashboard, which exiting it shows again).
  const [full, setFull] = useState<FullView | null>(() =>
    home ? { id: home.dashboard, home: true } : null,
  );
  // When a person last touched this screen (a pointer, a key or a touch, never
  // a command), so an assistant can tell which screen someone is at. It goes
  // out as how long ago, so the time is the server's clock, not this device's.
  const touchedAt = useRef<number | null>(null);
  useEffect(() => {
    const touched = () => (touchedAt.current = Date.now());
    const events = ["pointerdown", "pointermove", "keydown", "touchstart"];
    const options = { capture: true, passive: true };
    for (const e of events) window.addEventListener(e, touched, options);
    return () => {
      for (const e of events) window.removeEventListener(e, touched, options);
    };
  }, []);
  // The dashboard a corner card grew into, like a stat card into its panel.
  const [cardPanel, setCardPanel] = useState<{
    id: string;
    corner?: Corner;
  } | null>(null);
  // The insight open in its panel and where the panel grew from, and the
  // trail an insight lit in the explorer.
  const [insightId, setInsightId] = useState<string | null>(null),
    [insightFrom, setInsightFrom] = useState("tile-changed");
  // Where a health panel grows from when a dashboard's part opened it (null: Home's tile).
  const [panelFrom, setPanelFrom] = useState<string | null>(null);
  const [trail, setTrail] = useState<{
    title: string;
    stars: { id: string; title: string }[];
  } | null>(null);
  const overview = useMemo(
    () => ambientWindow(skyPage, knowledge.pins),
    [skyPage, knowledge.pins],
  );
  // Load the explorer and detail views shortly after the sky appears, so the
  // first tap opens them in place instead of waiting on a download.
  useEffect(() => {
    if (!hasData) return;
    const t = setTimeout(() => {
      void import("./Details");
      void import("./Explorer");
    }, 1500);
    return () => clearTimeout(t);
  }, [hasData]);
  useLayoutEffect(() => {
    // The old view is already cleared before revealing either fresh data or the access screen.
    document.documentElement.classList.remove("privacy-curtain");
  }, [data, state]);
  useEffect(() => {
    const update = () => setOnline(navigator.onLine);
    window.addEventListener("online", update);
    window.addEventListener("offline", update);
    return () => {
      window.removeEventListener("online", update);
      window.removeEventListener("offline", update);
    };
  }, []);
  // The app keeps a history entry for an open view and one for a dashboard
  // full screen over the sky or a view, so a phone's back gesture (or the
  // browser's Back) steps back through them instead of leaving the app. A Home
  // dashboard full screen is Home itself and keeps none, as the sky does.
  const entries = useRef(0);
  // The sky and tiles, with nothing open.
  const toSky = useCallback(() => {
    setView("ambient");
    setSelected(null);
    setTrail(null);
    setRequest(null);
    setHeld(false);
    setKeptStar(null);
    setKeptDashboard(null);
    setKeptLit(noStars);
    setFull(null);
  }, []);
  // A Home dashboard that was deleted gives way to the default Home (and the
  // screen says so): only the engine's whole list can tell, so a failed read,
  // or one at the most it lists, leaves it shown. One not read yet is shown,
  // and says itself if it can't be.
  const homeId = home?.dashboard ?? null;
  const homeMissing =
    homeId !== null &&
    dashboards.complete &&
    !dashboards.list.some((d) => d.id === homeId);
  // The dashboards whose full screen couldn't open here (their code didn't
  // load, or they broke while drawing), until the app reloads: going Home
  // skips such a Home as it does a deleted one.
  const [unopened, setUnopened] = useState<ReadonlySet<string>>(
    () => new Set(),
  );
  const homeFailed = homeId !== null && unopened.has(homeId);
  const homeShown =
    homeId !== null && !homeMissing && !homeFailed ? homeId : null;
  // Home: the closing of any view, the idle return and the home command.
  const close = useCallback(() => {
    toSky();
    if (homeShown) setFull({ id: homeShown, home: true });
  }, [toSky, homeShown]);
  useEffect(() => {
    // eslint-disable-next-line react/set-state-in-effect
    if (homeMissing) setFull((f) => (f?.home ? null : f));
  }, [homeMissing]);
  // Exiting a full screen (its button, Escape, Back) shows what is under it:
  // the Home dashboard it replaced, else the view or sky beneath.
  const leaveFull = useCallback(
    () =>
      setFull((f) =>
        f?.overHome && homeShown ? { id: homeShown, home: true } : null,
      ),
    [homeShown],
  );
  // A full screen that can't open gives way to what is under it by itself, so
  // an unattended screen never stays on an alert.
  const fullFailed = useCallback(
    (id: string) => {
      setUnopened((was) => new Set(was).add(id));
      setFull((f) =>
        f?.id !== id
          ? f
          : f.overHome && homeShown && homeShown !== id
            ? { id: homeShown, home: true }
            : null,
      );
    },
    [homeShown],
  );
  // A dashboard full screen over what is open; over the Home dashboard, it
  // keeps that to go back to (the Home's own is already shown).
  const fullScreen = (id: string) =>
    setFull((f) =>
      f?.home && f.id === id
        ? f
        : {
            id,
            home: false,
            ...(f?.home || f?.overHome ? { overHome: true } : {}),
          },
    );
  const returnFocus = useRef<HTMLElement | null>(null);
  const leave = () => {
    ambientScroll.current = window.scrollY;
    returnFocus.current = document.activeElement as HTMLElement | null;
  };
  const layered = view !== "ambient";
  // A full screen with an entry of its own: any but the Home dashboard's.
  const fullOver = full !== null && !full.home;
  const fullOpen = useRef(false),
    fullEntry = useRef(false),
    viewOpen = useRef(false),
    homeNow = useRef(homeId);
  useLayoutEffect(() => {
    fullOpen.current = full !== null;
    fullEntry.current = fullOver;
    viewOpen.current = layered;
    homeNow.current = homeId;
  });
  useEffect(() => {
    if (layered) return;
    // Back on the sky, focus returns to what opened the view (a star, a tile
    // or the keyboard entry) instead of the top of the page.
    const target = returnFocus.current;
    returnFocus.current = null;
    if (target?.isConnected && document.activeElement === document.body)
      target.focus({ preventScroll: true });
  }, [layered]);
  // The entries follow what is shown once it is drawn, so a view that gives way
  // to another (a star after a full screen, Undo's full screen after a star)
  // keeps its entry, and what closes takes its own off again. Each such step
  // back comes back as a popstate, perhaps after the next command opened a
  // view, so it is counted and never taken for the person's Back.
  const depth = (layered ? 1 : 0) + (fullOver ? 1 : 0);
  const ownBacks = useRef(0);
  useEffect(() => {
    for (; entries.current < depth; entries.current++)
      history.pushState({ ...history.state, rhythmLayer: true }, "");
    if (entries.current === depth) return;
    const steps = entries.current - depth;
    entries.current = depth;
    ownBacks.current++;
    if (steps === 1) history.back();
    else history.go(-steps);
  });
  useEffect(() => {
    // Back leaves a full screen for what is under it, as Escape does, and any
    // other view for Home; another step back is not the app's.
    const back = () => {
      if (ownBacks.current > 0) {
        ownBacks.current--;
        return;
      }
      if (entries.current === 0) return;
      entries.current--;
      if (fullEntry.current) leaveFull();
      else close();
    };
    // Escape leaves a full screen for what is under it, and any other view for Home; while a
    // part is held it only puts the part back (PartsGrid.tsx).
    const key = (e: KeyboardEvent) => {
      if (
        e.key !== "Escape" ||
        document.documentElement.hasAttribute("data-arranging")
      )
        return;
      if (fullOpen.current) leaveFull();
      else if (viewOpen.current) close();
    };
    window.addEventListener("popstate", back);
    window.addEventListener("keydown", key);
    return () => {
      window.removeEventListener("popstate", back);
      window.removeEventListener("keydown", key);
    };
  }, [close, leaveFull]);
  // An open view left alone drifts back to the ambient sky, unless someone is
  // typing, reading a source inside an embedded viewer, or waiting for what
  // they asked for (a document being added marks itself data-keep-open). Anyone who reads
  // slowly or with a screen reader can turn this off in Sources & settings.
  const [idleReturn, setIdleReturn] = useState(true);
  const [layout, setLayoutState] = useState<Layout>(savedLayout);
  // The rhythm band and the tall column apply on wide screens only (App.css).
  const wide = useMedia("(min-width: 1100px) and (min-height: 501px)");
  const setLayout = (next: Layout) => {
    setLayoutState(next);
    saveLayout(next);
  };
  // Whether the skies draw the dashed links (Sources & settings), kept for this screen beside
  // the layout. Display only: off, a chosen star still shows all of its own (Constellation.tsx).
  const [automaticLinks, setAutomaticState] = useState(savedAutomaticLinks);
  const setAutomaticLinks = (on: boolean) => {
    setAutomaticState(on);
    saveAutomaticLinks(on);
  };
  // Whether the skies draw work items' stars (Sources & settings), kept for this screen the same
  // way. Display only: off, a chosen or pinned one still shows, and nothing is deleted.
  const [workItems, setWorkState] = useState(savedWorkItems);
  // The switch is offered while the map has a work item, and for the rest of a visit that began
  // with them hidden, so turning them back on never takes the switch from under a finger.
  const [workOffered] = useState(() => !savedWorkItems());
  const setWorkItems = (on: boolean) => {
    setWorkState(on);
    saveWorkItems(on);
  };
  // With an engine the tabs of one browser are one screen, so a layout, tiles
  // or Home chosen in another tab show here too (at Home, a new Home at once),
  // and whichever tab listens sends them on. Only a new Home touches what is
  // shown: a layout or tiles change leaves the sky someone went to as it is.
  useEffect(() => {
    if (graphDemo) return;
    return watchSettings(() => {
      const next = savedHome();
      setLayoutState(savedLayout());
      setAutomaticState(savedAutomaticLinks());
      setWorkState(savedWorkItems());
      setTilesState(savedTiles() ?? defaultTiles);
      setGridState(savedGrid());
      setHomeState(next);
      if (!viewOpen.current)
        setFull((f) => followHome(f, homeNow.current, next));
    });
  }, [graphDemo]);
  // The sky shown in place of a Home dashboard (someone left it) drifts back
  // to it the same way; a full screen stays until someone exits.
  const awayFromHome = !layered && homeShown !== null && !full;
  useEffect(() => {
    if ((!layered && !awayFromHome) || full || !idleReturn) return;
    if (held) {
      // Only someone's own input lets it drift again (not focus or scrolling
      // the app moves itself), and the idle minutes start from there.
      const use = () => setHeld(false);
      const events = ["pointerdown", "keydown", "wheel", "touchstart"];
      const options = { capture: true, passive: true };
      for (const e of events) window.addEventListener(e, use, options);
      return () => {
        for (const e of events) window.removeEventListener(e, use, options);
      };
    }
    let timer = 0;
    const arm = () => {
      clearTimeout(timer);
      timer = window.setTimeout(idle, idleReturnMs);
    };
    const idle = () => {
      const active = document.activeElement;
      if (
        document.hidden ||
        active?.matches("iframe, input, select, textarea") ||
        document.querySelector("[data-keep-open]")
      )
        arm();
      else close();
    };
    // Captured, so zooming the sky (which d3 stops from bubbling), scrolling a
    // panel and moving focus all count as activity.
    const events = [
      "pointerdown",
      "pointermove",
      "keydown",
      "wheel",
      "scroll",
      "focusin",
    ];
    const options = { capture: true, passive: true };
    for (const e of events) window.addEventListener(e, arm, options);
    arm();
    return () => {
      clearTimeout(timer);
      for (const e of events) window.removeEventListener(e, arm, options);
    };
  }, [layered, awayFromHome, full, idleReturn, close, held]);
  const locked = view !== "ambient" || full !== null;
  useLayoutEffect(() => {
    if (!locked) return;
    // A full screen over the sky keeps the sky's scroll as it is now.
    const y = viewOpen.current ? ambientScroll.current : window.scrollY;
    const previous = {
      position: document.body.style.position,
      top: document.body.style.top,
      width: document.body.style.width,
      overflow: document.body.style.overflow,
    };
    Object.assign(document.body.style, {
      position: "fixed",
      top: `-${y}px`,
      width: "100%",
      overflow: "hidden",
    });
    return () => {
      Object.assign(document.body.style, previous);
      window.scrollTo({ top: y, behavior: "instant" });
    };
  }, [locked]);
  // Native entry waits for an actual readable result, and can be cancelled or retried.
  // It never writes screen/layout commands. A missing star must not silently open Home.
  useEffect(() => {
    if (!session || !launcherEntry || !launcherPending) return;
    const controller = new AbortController();
    setLauncherError(false);
    void (async () => {
      try {
        if (launcherEntry.kind === "builtin") {
          setView("glucose");
        } else if (launcherEntry.kind === "star") {
          const page = await session.retrieve(
            "neighbors",
            launcherEntry.id,
            {},
            controller.signal,
          );
          if (controller.signal.aborted) return;
          if (!page.nodes.some((n) => n.id === launcherEntry.id))
            throw new Error("Unavailable star");
          knowledge.accept(page, [launcherEntry.id]);
          setKeptStar(launcherEntry.id);
          setSelected(launcherEntry.id);
          setReader(launcherEntry.id);
          setView("explore");
          setRequest({
            nonce: ++requests.current,
            star: launcherEntry.id,
            reader: launcherEntry.id,
          });
        } else {
          setFull({ id: launcherEntry.id, home: false });
        }
        setHeld(true);
        setLauncherPending(false);
        history.replaceState(null, "", location.pathname + location.search);
      } catch {
        if (!controller.signal.aborted) setLauncherError(true);
      }
    })();
    return () => controller.abort();
    // Knowledge/action callbacks change as the sky refreshes; this flight belongs to the entry.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [session, launcherEntry, launcherPending, launcherRetry]);
  if (data && launcherPending)
    return (
      <main className="boot" role="status">
        <RhythmMark size={40} />
        <h1>{launcherError ? "Star unavailable" : "Opening your star"}</h1>
        <p>
          {launcherError
            ? "It may have moved, or household access is unavailable."
            : "Checking access and reading its connections."}
        </p>
        {launcherError && (
          <button onClick={() => setLauncherRetry((n) => n + 1)}>
            Try again
          </button>
        )}
      </main>
    );
  if (!data)
    return (
      <main className="boot" role="status">
        <RhythmMark size={40} />
        <h1>Opening {instance().name}</h1>
        {state === "error" ? (
          <>
            <p>Access or source unavailable. Reload to verify access.</p>
            <button onClick={() => void reloadFromNetwork()}>Try again</button>
          </>
        ) : (
          <p>Checking access and loading {instance().name}…</p>
        )}
      </main>
    );
  const selectNode = (id: string) => {
    if (view === "ambient") leave();
    setFull(null);
    setSelected(id);
    setTrail(null);
    setReader(null);
    setRequest(null);
    setView("explore");
  };
  // A corner's dashboard card grows into the full dashboard, in the reader
  // beside its star.
  const openDashboard = (dashboard: string) => {
    const star = dashboardStarId(dashboard);
    if (!skyPage.nodes.some((n) => n.id === star)) return;
    selectNode(star);
    setReader(star);
  };
  // Where each tile sits: the screen's own map where layouts show, the
  // default elsewhere. The default map changes nothing on screen.
  const placed = wide ? tiles : defaultTiles;
  const placeOf = (tile: Tile) =>
    placed[tile] === tile ? undefined : { gridArea: area(placed[tile]) };
  // A tile's zone: its place's grid area, the place, and the look Home's layout
  // gives that place where layouts show (App.css), the tall column's (the main
  // place in the tall column layout) or the row's (its other places, and every
  // place in the rhythm band). A tile moved to another place takes that place's
  // look, never the one of the place it left.
  const zoneOf = (tile: Tile) => ({
    style: placeOf(tile),
    "data-place": placed[tile],
    "data-look":
      !wide || layout === "corners"
        ? undefined
        : layout === "hero" && placed[tile] === "main"
          ? "tall"
          : "row",
  });
  // The main place asks its dashboard for a tall card, except in the rhythm band
  // on a wide screen, where it sits in the short row; the others ask for compact
  // ones. A new dashboard, a newly saved version or a new shape draws the card afresh.
  const mainShape: CardShape = layout === "band" && wide ? "compact" : "tall";
  const shapeOf = (corner: Corner): CardShape =>
    placed[corner] === "main" ? mainShape : "compact";
  const cardIn = (corner: Corner) => {
    const chosen = dashboards.list.find(
      (d) => d.id === dashboards.cards[corner],
    );
    if (!chosen || !session) return null;
    const shape = shapeOf(corner);
    // A card that breaks says so in its own place, and changes nothing saved.
    return (
      <PartBoundary
        label="This card couldn’t load."
        again={dashboards.list}
        retry
      >
        <Suspense fallback={null}>
          <DashboardCard
            key={`${chosen.id}:${chosen.latest}:${shape}`}
            id={chosen.id}
            title={chosen.title}
            session={session}
            shape={shape}
            unavailable={blocked}
            onOpen={() => {
              setCardPanel({ id: chosen.id, corner });
              open("dashboard");
            }}
          />
        </Suspense>
      </PartBoundary>
    );
  };
  const demo = isDemo(data);
  // A source is connected once its records are on the map. The Rhythm engine's row ("rhythm") is
  // the engine's own stars, under its catalog provider id: "rhythm", or the older engine's id an
  // install kept (RHYTHM_CATALOG_PROVIDER). With none of them on the map, a map the engine
  // answered still says it is connected, retrieved when it answered, and stale, as the map's
  // stars are, while a later read fails.
  const sources = data.sources.map((source) => {
    if (demo || source.id === glucoseSlot || source.id === "garmin")
      return source;
    const engine = source.id === "rhythm";
    const provider = engine ? catalogProvider() : source.id;
    const records = knowledge.page.nodes.filter(
      (node) => node.provenance.sourceId === provider,
    );
    if (!records.length)
      return engine && knowledge.lastRefresh
        ? {
            ...source,
            connected: true,
            status: knowledge.error
              ? ("stale" as const)
              : ("available" as const),
            retrievedAt: knowledge.lastRefresh,
          }
        : source;
    return {
      ...source,
      connected: true,
      status: records.some((n) => n.resource.availability === "stale")
        ? ("stale" as const)
        : ("available" as const),
      sampleAt:
        records
          .map((n) => n.resource.sourceModifiedAt)
          .filter((t): t is string => !!t)
          .sort()
          .at(-1) ?? null,
    };
  });
  const blocked =
    demo && (state === "empty" || state === "error" || state === "loading");
  // A source's tiles show only once it is connected (parts/partData.ts). The
  // Sources panel lists them all, with how to connect each one.
  const glucose = glucoseOn(data),
    garmin = garminOn(data);
  const cards = {
    main: cardIn("main"),
    recovery: cardIn("recovery"),
    training: cardIn("training"),
    changed: cardIn("changed"),
  };
  // Corners with neither a dashboard nor their source give their room to the
  // tiles beside them (App.css), named by the place they sit in.
  const empty =
    [
      !cards.recovery && !garmin && placed.recovery,
      !cards.training && !garmin && placed.training,
    ]
      .filter(Boolean)
      .join(" ") || undefined;
  const modal =
    view === "glucose" ||
    view === "recovery" ||
    view === "training" ||
    view === "changed" ||
    view === "insight" ||
    view === "dashboard" ||
    view === "sources";
  const open = (next: View) => {
    if (view === "ambient") leave();
    setFull(null);
    setView(next);
    setSelected(null);
    setTrail(null);
    setRequest(null);
    setPanelFrom(null);
  };
  // The day's insight takes the context corner (and a card on phones); with
  // none current, the newest earlier one and the engine's last run.
  const shownInsights = insightsShown(insights, blocked);
  const {
    current: shownInsight,
    latest: latestInsight,
    lastRun,
  } = shownInsights;
  const openInsight = (id: string, from: "corner" | "card") => {
    setInsightId(id);
    setInsightFrom(from === "card" ? "tile-insight-card" : "tile-changed");
    open("insight");
  };
  // Its trail: the explorer opens with the insight's stars lit and framed.
  const showTrail = (insight: DailyInsight) => {
    if (view === "ambient") leave();
    setFull(null);
    setSelected(null);
    setRequest(null);
    setTrail({
      title: insight.title,
      stars: insight.stars.map((s) => ({ id: s.graphId, title: s.title })),
    });
    setView("explore");
  };
  // What a command from another screen or an assistant does here, through the
  // same functions a tap uses (LiveScreen.tsx). Each view a command opens is
  // held; the explorer is asked again even when it is already open.
  const ask = (next: Omit<NonNullable<typeof request>, "nonce">) =>
    setRequest({ nonce: ++requests.current, ...next });
  const node = (id: string) => skyPage.nodes.find((n) => n.id === id);
  const showDashboard = (id: string) => {
    const star = dashboardStarId(id);
    if (!node(star)) return;
    openDashboard(id);
    ask({ star, reader: star });
    setHeld(true);
    // What it shows, ahead of the explorer saying so once it has drawn it.
    explorerState.current = { selected: star, reader: star, query: "" };
  };
  const showStar = (id: string) => {
    const star = node(id);
    if (!star) return;
    if (isDashboardStar(star)) return showDashboard(dashboardOf(star)!);
    selectNode(id);
    ask({ star: id });
    setHeld(true);
    // Kept on the map until Home, should it lie beyond the overview.
    setKeptStar(id);
    explorerState.current = { selected: id, reader: null, query: "" };
  };
  const showSearch = (query: string) => {
    if (view === "ambient") leave();
    setFull(null);
    setSelected(null);
    setTrail(null);
    setReader(null);
    setView("explore");
    ask({ search: query });
    setHeld(true);
    explorerState.current = { selected: null, reader: null, query };
    // The same stars the explorer's search lights.
    const matches = filterNodes(skyPage.nodes, query, "all", "all");
    return {
      matches: matches.length,
      titles: matches.slice(0, 10).map((n) => cut(displayTitle(n.title))),
    };
  };
  // A saved dashboard's title, or its star's on the sky until the dashboards
  // are read, so a screen that just opened still names its Home.
  const titleOf = (id: string) =>
    dashboards.list.find((d) => d.id === id)?.title ??
    node(dashboardStarId(id))?.title;
  // A dashboard full screen, over what is open beneath it.
  const nowShowing = (): { at: ScreenView; title?: string } => {
    const under = openNow();
    return full
      ? {
          at: {
            ...under.at,
            full: {
              dashboard: full.id,
              home: full.home,
              ...(full.overHome ? { overHome: true } : {}),
            },
          },
          title: titleOf(full.id),
        }
      : under;
  };
  // What this screen shows now, and the title of its star or dashboard.
  const openNow = (): { at: ScreenView; title?: string } => {
    if (view === "ambient") return { at: { view: "home" } };
    if (view === "dashboard" && cardPanel)
      return {
        at: { view: "dashboard", dashboard: cardPanel.id },
        title: dashboards.list.find((d) => d.id === cardPanel.id)?.title,
      };
    if (view !== "explore") return { at: { view: "other", other: view } };
    const s = explorerState.current;
    const reading = s.reader ? node(s.reader) : undefined;
    if (reading && isDashboardStar(reading))
      return {
        at: { view: "dashboard", dashboard: dashboardOf(reading)! },
        title: reading.title,
      };
    if (s.query) return { at: { view: "search", query: s.query } };
    const star = s.selected ? node(s.selected) : undefined;
    return star
      ? { at: { view: "star", star: star.id }, title: star.title }
      : { at: { view: "other", other: "explore" } };
  };
  // A card part opens its dashboard in the reader, as a command does.
  const openBoard = (id: string) => {
    const star = dashboardStarId(id);
    if (!node(star)) return;
    openDashboard(id);
    ask({ star, reader: star });
  };
  // A star on a dashboard's map opens as a command opens one: one beyond this
  // screen's map (a focused part shows the engine's whole readable map) is
  // fetched onto it first, with its neighbours, and kept there until Home.
  const openPartStar = async (id: string) => {
    const known = node(id);
    if (known && isDashboardStar(known)) return openBoard(dashboardOf(known)!);
    if (!known && session)
      try {
        knowledge.accept(await session.retrieve("neighbors", id), [id]);
      } catch {
        return;
      }
    setKeptStar(id);
    selectNode(id);
    ask({ star: id });
  };
  // A map part's Explore: its stars added to this screen's map and lit in the
  // explorer, or for a focus that is only a query, that search; the whole sky
  // for a part with no focus.
  const exploreLit = (lit: ExploreLit | null) => {
    if (view === "ambient") leave();
    setFull(null);
    setSelected(null);
    setTrail(null);
    setReader(null);
    setView("explore");
    if (lit && "page" in lit) {
      const ids = lit.page.nodes.map((n) => n.id);
      knowledge.accept(lit.page, ids);
      setKeptLit(ids);
      ask({
        trail: {
          title: lit.title,
          stars: lit.page.nodes.map((n) => ({ id: n.id, title: n.title })),
          map: true,
        },
      });
    } else ask(lit ? { search: lit.query } : {});
  };
  // A focused map part's own read (10.7), of the version drawn.
  const readMap = async (
    dashboard: Dashboard,
    part: Part,
    cursor: string | null,
    signal: AbortSignal,
  ) => {
    const map = await session?.dashboardMap?.(
      dashboard.id,
      dashboard.version,
      part.id,
      cursor,
      signal,
    );
    if (!map) throw new Error("This map can't be read right now");
    return map;
  };
  // What Home's sky draws, which a dashboard's map parts draw too.
  const homeSky: MapSky = {
    page: skyPage,
    overview,
    pins: knowledge.pins,
    demo: graphDemo,
    loading: !knowledge.lastRefresh && !knowledge.error,
    readAt: knowledge.lastRefresh,
    automaticLinks,
    workItems,
  };
  // A dashboard's native parts (parts/nativeParts.tsx) in each of its hosts:
  // the explorer's reader, the dashboard panel and full screen. They read
  // Home's data and do what Home's tiles do; a tile's panel grows from its
  // part. `covered`: a full screen may lie over the host.
  const partsIn = (covered: boolean): DashboardParts => ({
    starTitle: (id) => {
      const star = node(id);
      return star ? displayTitle(star.title) : undefined;
    },
    renderPart: (part, { dashboard, fold, showEverything }) =>
      nativeParts({
        data,
        sources,
        state,
        blocked,
        demo,
        session,
        sky: homeSky,
        insights: {
          current: insights.current,
          insights: insights.insights,
          runs: insights.runs,
        },
        dashboards: { list: dashboards.list, complete: dashboards.complete },
        paused: paused || covered,
        openPanel: (panel, from) => {
          open(panel);
          setPanelFrom(from);
        },
        openInsight: (id, from) => {
          setInsightId(id);
          setInsightFrom(from);
          open("insight");
        },
        openSources: () => open("sources"),
        openStar: (id) => void openPartStar(id),
        openDashboard: openBoard,
        explore: exploreLit,
        readMap: session?.dashboardMap
          ? (p, cursor, signal) => readMap(dashboard, p, cursor, signal)
          : undefined,
        showEverything,
      })(part, fold),
  });
  // A new Home for this screen, kept beside its layout; at Home now, it shows
  // at once.
  const setHome = (dashboard: string | null) => {
    const next = dashboard ? { dashboard } : null;
    setHomeState(next);
    saveHome(next);
    if (view === "ambient" && (!full || full.home))
      setFull(dashboard ? { id: dashboard, home: true } : null);
  };
  const screenActions: ScreenActions = {
    showing: (): Showing => {
      const { at, title = "" } = nowShowing();
      // Full screen, it shows that dashboard whatever is under it.
      const shown: ScreenView = at.full
        ? { view: "dashboard", dashboard: at.full.dashboard }
        : at;
      return {
        view: at.full ? "full_screen" : at.view,
        ...(shown.star ? { star: { id: shown.star, title: cut(title) } } : {}),
        ...(shown.dashboard
          ? { dashboard: { id: shown.dashboard, title: cut(title) } }
          : {}),
        ...(shown.query ? { query: shown.query } : {}),
        layout,
        tiles,
        narrow: !wide,
        home: homeId
          ? {
              dashboard: { id: homeId, title: cut(titleOf(homeId) ?? "") },
              ...(homeMissing ? { missing: true as const } : {}),
            }
          : null,
        ...(touchedAt.current !== null
          ? { activeAgo: Math.max(0, Date.now() - touchedAt.current) }
          : {}),
      };
    },
    view: () => nowShowing().at,
    starTitle: (id) => {
      const star = node(id);
      return star ? displayTitle(star.title) : null;
    },
    dashboardTitle: (id) => {
      const star = node(dashboardStarId(id));
      return star ? displayTitle(star.title) : null;
    },
    savedTitle: (id) => {
      const saved = dashboards.list.find((d) => d.id === id);
      return saved ? displayTitle(saved.title) : null;
    },
    // The engine's neighbours of a star reach every star, so they bring one
    // beyond the overview, with its links, as the explorer's do.
    fetchStar: async (id) => {
      if (!session) return;
      try {
        knowledge.accept(await session.retrieve("neighbors", id), [id]);
      } catch {
        // Not reached: the screen says the star isn't there.
      }
    },
    fetchDashboard: async (id) => {
      setKeptDashboard(id);
      if (!dashboards.list.some((d) => d.id === id)) await rereadDashboards();
    },
    reread: rereadDashboards,
    openStar: showStar,
    openDashboard: showDashboard,
    search: showSearch,
    fullScreen,
    exitFullScreen: () => setFull(null),
    home: close,
    // The view first, then the full screen that was over it, if any.
    restore: (to) => {
      if (to.view === "home") toSky();
      else if (to.view === "star" && to.star) showStar(to.star);
      else if (to.view === "dashboard" && to.dashboard)
        showDashboard(to.dashboard);
      else if (to.view === "search" && to.query) showSearch(to.query);
      else open((to.other as View | undefined) ?? "explore");
      setFull(
        to.full
          ? {
              id: to.full.dashboard,
              home: to.full.home,
              ...(to.full.overHome ? { overHome: true } : {}),
            }
          : null,
      );
    },
    homeDashboard: homeId,
    setHome,
    layout,
    setLayout,
    tiles,
    setTiles,
    narrow: !wide,
    cards: session?.setDashboardCard ? dashboards.cards : null,
    setCard: dashboards.setCard,
    grid,
    setGrid,
    // What Home's layouts draw now, where they show and no grid draws Home.
    measureHome: () =>
      wide && !grid ? measureHomeGrid(homeRef.current, tiles) : null,
  };
  // Showing a star on another screen, from its panel in the explorer.
  const showOn =
    session?.screenList && session.screenSend
      ? {
          list: (signal?: AbortSignal) =>
            session.screenList!(screen.id, signal),
          send: (
            to: string,
            action: string,
            args: Record<string, unknown>,
            signal?: AbortSignal,
          ) =>
            session.screenSend!(
              { screen: screen.id, to, action, args },
              signal,
            ),
        }
      : undefined;
  // What this screen can do with a dashboard in the reader or a panel.
  const dashboardScreen = {
    fullScreen,
    home: homeId,
    setHome: (id: string) => setHome(id),
  };
  // A new name for this screen, kept on this browser (this tab in the demo)
  // and sent on; the next listen carries it too, and while the engine keeps
  // another the listening tab sends it again (useScreenListen).
  const renameScreen = async (name: string) => {
    const clean = cleanName(name);
    if (!clean) return false;
    saveScreenName(clean, graphDemo);
    setRenamed(clean);
    try {
      await session?.screenRename?.(screen.id, clean);
      return true;
    } catch {
      return false;
    }
  };
  // The engine's name for this screen (the default, numbered when another
  // screen already had it), taken while this browser is on its default name;
  // else the name a person chose, as this browser keeps it now. A demo tab's
  // screen is never the browser's, so it keeps its own. Answers the name this
  // screen goes by, so the next listen carries it at once.
  const adoptName = (name: string) => {
    const goesBy = adoptScreenName(screen.id, name);
    if (goesBy !== null) setRenamed(goesBy);
    return goesBy;
  };
  // Choosing a layout puts that layout's designed Home back, dropping a grid
  // (docs/dev/screens.md 10.5). With a grid it goes through the screen's
  // runner, which says so with Undo, as the layout command does; without one it
  // is chosen exactly as before.
  const chooseLayout = (next: Layout) => {
    if (grid && arrangeInPlace({ kind: "layout", layout: next })) return;
    setLayout(next);
    setGrid(null);
  };
  // Home arranged by hand: a change dropped on its grid, or on the grips over a
  // Home drawn as today, goes to the screen's runner (LiveScreen.tsx), which
  // keeps it with a note and Undo, as a command's; a swap of two corners stays
  // the tile swap it is.
  const arrangeHome = async (
    change: GridChange<GridPart>,
  ): Promise<GridOutcome> => {
    const before = { parts: change.before } as HomeGrid,
      after = { parts: change.after } as HomeGrid;
    if (!grid && document.activeElement?.closest(".parts.overlay"))
      regrip.current = change.part;
    const ran = arrangeInPlace({
      kind: "home",
      part: change.part,
      before,
      after,
      summary: change.summary,
    });
    if (ran) return ran;
    setGrid(after);
    return { ok: true };
  };
  // Home with a grid shows where its layouts show (1100 × 501 px or more),
  // always as saved: each part lays itself out for its cell, and a corner with
  // neither its source nor a card keeps its cells, saying so quietly.
  const gridded = wide && grid !== null;
  const homeSkyPart = (
    <MapPart
      sky={homeSky}
      blocked={blocked}
      paused={paused || modal || view === "explore" || full !== null}
      own={false}
      selected={selected}
      healthFocus={view === "glucose"}
      onStar={selectNode}
      onExplore={() => open("explore")}
    />
  );
  // Home draws no brief (the owner, 2026-11-09: a summary card above the sky goes against the ambient vision). What needs a
  // person now is the Memories view's, list by list; the brief itself is for AI clients (the MCP tool `brief`).
  // The day's old memory, at the head of the sky's column in Home's own flow, only while the engine serves it (ResurfaceCard.tsx).
  const homeResurfaced = (
    <ResurfaceCard
      client={session?.memories}
      resting={paused || modal || view === "explore" || full !== null}
    />
  );
  const cornerCard = (corner: Corner) => {
    const chosen = dashboards.list.find(
      (d) => d.id === dashboards.cards[corner],
    );
    if (!chosen || !session) return null;
    return (
      <motion.div layoutId={`tile-${corner}`} className="native-part part-card">
        <PartBoundary
          label="This card couldn’t load."
          again={dashboards.list}
          retry
        >
          <CardPart
            dashboard={chosen.id}
            dashboards={dashboards}
            session={session}
            blocked={blocked}
            folded={false}
            onOpen={() => {
              setCardPanel({ id: chosen.id, corner });
              open("dashboard");
            }}
          />
        </PartBoundary>
      </motion.div>
    );
  };
  const homePart = (part: GridPart): ReactNode => {
    const id = part.id as HomeId;
    if (id === "map")
      return (
        <section className="graph-zone" aria-label="Knowledge constellation">
          {homeResurfaced}
          {homeSkyPart}
        </section>
      );
    const card = cornerCard(id);
    if (card) return card;
    if (id === "main")
      return glucose ? (
        <motion.div
          layoutId="tile-glucose"
          className="native-part part-glucose"
        >
          <GlucosePart
            data={data}
            state={state}
            blocked={blocked}
            readData={session?.data}
            spark={view !== "explore"}
            onOpen={() => open("glucose")}
          />
        </motion.div>
      ) : (
        <div className="native-part part-glucose">
          <div className="main-hint">
            <MainHint
              dashboards={dashboards.list.length > 0}
              onSources={() => open("sources")}
            />
          </div>
        </div>
      );
    if (id === "changed")
      return (
        <motion.div
          layoutId="tile-changed"
          className="native-part part-changed"
        >
          <ChangedPart
            nodes={blocked ? [] : overview.nodes}
            edges={overview.edges}
            sources={sources}
            paused={paused}
            shown={shownInsights}
            onStar={selectNode}
            onSources={() => open("sources")}
            onPanel={() => open("changed")}
            onInsight={(insight) => openInsight(insight, "corner")}
          />
        </motion.div>
      );
    const tile =
      id === "recovery" ? (
        <RecoveryPart
          data={data}
          blocked={blocked}
          onOpen={() => open("recovery")}
        />
      ) : (
        <TrainingPart
          data={data}
          blocked={blocked}
          demo={demo}
          onOpen={() => open("training")}
        />
      );
    // Without a training source it says so quietly, and opens nothing.
    return garmin ? (
      <motion.div layoutId={`tile-${id}`} className={`native-part part-${id}`}>
        {tile}
      </motion.div>
    ) : (
      <div className={`native-part part-${id}`}>{tile}</div>
    );
  };
  return (
    <MotionConfig reducedMotion="user">
      <div
        className={`app ${paused ? "motion-paused" : ""} view-${view}`}
        data-layout={layout}
      >
        <div className="atmosphere" aria-hidden="true" />
        <div className="starfield" aria-hidden="true" />
        <div className="main-content" inert={modal || full !== null}>
          <header className="topbar">
            <a
              className="brand"
              href="#"
              onClick={(e) => {
                e.preventDefault();
                close();
              }}
              aria-label={`${instance().name} home`}
            >
              <span className="brand-mark">
                <RhythmMark />
              </span>
              <span>{instance().name.toUpperCase()}</span>
            </a>
            <div className="top-status">
              {demo && <span className="demo-badge">DEMO</span>}
              <button
                className="icon-button"
                aria-label="Refresh observations"
                title="Refresh observations"
                onClick={() => {
                  refresh();
                  void insights.reload();
                  void rebuildMap();
                }}
                disabled={refreshing}
              >
                <RefreshCw
                  size={15}
                  className={refreshing ? "refresh-spinning" : ""}
                />
              </button>
              <Clock />
              <button
                className="icon-button settings"
                aria-label="Sources and settings"
                title="Sources and settings"
                onClick={() => open("sources")}
              >
                <Settings2 size={17} />
              </button>
            </div>
          </header>
          <div className="source-strip">
            <button onClick={() => open("sources")}>
              {online ? <i /> : <WifiOff size={12} aria-label="Offline" />}
              {!online
                ? "Offline · revalidation required"
                : sources.some((s) => s.connected)
                  ? "Source status"
                  : "Your sources"}
              <ChevronRight size={12} />
            </button>
            <div
              className="source-times"
              aria-label={
                demo
                  ? "Fictional per-source timestamps"
                  : "Source observation timestamps"
              }
            >
              {/* Only sources in use; Sources & settings lists the rest. */}
              {sources
                .filter((s) => s.status !== "disconnected")
                .map((s) => (
                  <span key={s.id}>
                    {s.name}{" "}
                    <time dateTime={s.sampleAt ?? undefined}>
                      {s.calendarDate
                        ? `Source day ${s.calendarDate}`
                        : stamp(s.sampleAt)}
                    </time>
                    <span>{statusLabel(s).toUpperCase()}</span>
                  </span>
                ))}
            </div>
            <span className="sample-date">
              {demo ? "Fictional sample day" : "Source observations"}
            </span>
          </div>
          <main>
            {view === "explore" && (
              <FeatureBoundary close={close}>
                <Suspense
                  fallback={
                    <div role="status" className="sr-only">
                      Opening constellation…
                    </div>
                  }
                >
                  <Explorer
                    nativeReader={launcher}
                    knowledge={sky}
                    onOpenOriginal={(id) => { browserOriginal.current = id; }}
                    selected={selected}
                    onSelect={setSelected}
                    close={close}
                    paused={paused || full !== null}
                    state={state}
                    onOpenPanel={open}
                    trail={trail}
                    reader={reader}
                    onRebuild={rebuildMap}
                    request={request}
                    onState={onExplorerState}
                    showOn={showOn}
                    dashboardScreen={dashboardScreen}
                    dashboardParts={partsIn(full !== null)}
                    behind={full !== null}
                    automaticLinks={automaticLinks}
                    workItems={workItems}
                  />
                </Suspense>
              </FeatureBoundary>
            )}
            <div
              ref={homeRef}
              className="observatory"
              data-empty={gridded ? undefined : empty}
              data-grid={gridded ? "" : undefined}
              inert={view === "explore"}
              aria-hidden={view === "explore" || undefined}
            >
              {gridded ? (
                <PartsGrid
                  parts={grid.parts}
                  homeMode
                  label={homeLabel}
                  render={homePart}
                  arrange={arrangeHome}
                  refused={refuseHome}
                />
              ) : (
                <>
                  {cards.main ? (
                    <motion.section
                      layoutId="tile-main"
                      className={`health-zone main-zone dashboard-zone ${shapeOf("main")}`}
                      {...zoneOf("main")}
                    >
                      {cards.main}
                    </motion.section>
                  ) : !glucose ? (
                    <section
                      className="health-zone main-zone main-hint"
                      {...zoneOf("main")}
                    >
                      <MainHint
                        dashboards={dashboards.list.length > 0}
                        onSources={() => open("sources")}
                      />
                    </section>
                  ) : (
                    <motion.section
                      layoutId="tile-glucose"
                      className="health-zone main-zone glucose-zone"
                      {...zoneOf("main")}
                    >
                      <GlucosePart
                        data={data}
                        state={state}
                        blocked={blocked}
                        readData={session?.data}
                        spark={view !== "explore"}
                        onOpen={() => open("glucose")}
                      />
                    </motion.section>
                  )}
                  {cards.recovery ? (
                    <motion.section
                      layoutId="tile-recovery"
                      className="health-zone recovery-zone dashboard-zone"
                      {...zoneOf("recovery")}
                    >
                      {cards.recovery}
                    </motion.section>
                  ) : (
                    garmin && (
                      <motion.section
                        layoutId="tile-recovery"
                        className="health-zone recovery-zone"
                        {...zoneOf("recovery")}
                      >
                        <RecoveryPart
                          data={data}
                          blocked={blocked}
                          onOpen={() => open("recovery")}
                        />
                      </motion.section>
                    )
                  )}
                  <section
                    className="graph-zone"
                    aria-label="Knowledge constellation"
                  >
                    {homeResurfaced}
                    {homeSkyPart}
                  </section>
                  {cards.training ? (
                    <motion.section
                      layoutId="tile-training"
                      className="health-zone training-zone dashboard-zone"
                      {...zoneOf("training")}
                    >
                      {cards.training}
                    </motion.section>
                  ) : (
                    garmin && (
                      <motion.section
                        layoutId="tile-training"
                        className="health-zone training-zone"
                        {...zoneOf("training")}
                      >
                        <TrainingPart
                          data={data}
                          blocked={blocked}
                          demo={demo}
                          onOpen={() => open("training")}
                        />
                      </motion.section>
                    )
                  )}
                  {cards.changed ? (
                    <motion.section
                      layoutId="tile-changed"
                      className="health-zone context-zone dashboard-zone"
                      {...zoneOf("changed")}
                    >
                      {cards.changed}
                    </motion.section>
                  ) : (
                    <motion.section
                      layoutId="tile-changed"
                      className="health-zone context-zone"
                      {...zoneOf("changed")}
                    >
                      <ChangedPart
                        nodes={blocked ? [] : overview.nodes}
                        edges={overview.edges}
                        sources={sources}
                        paused={paused}
                        shown={shownInsights}
                        onStar={selectNode}
                        onSources={() => open("sources")}
                        onPanel={() => open("changed")}
                        onInsight={(id) => openInsight(id, "corner")}
                      />
                    </motion.section>
                  )}
                  {shownInsight ? (
                    <InsightCard
                      insight={shownInsight}
                      onOpen={() => openInsight(shownInsight.id, "card")}
                    />
                  ) : (
                    (latestInsight || lastRun) && (
                      <EarlierInsights
                        latest={latestInsight}
                        lastRun={lastRun}
                        onOpen={() =>
                          latestInsight && openInsight(latestInsight.id, "card")
                        }
                      />
                    )
                  )}
                  {wide && (
                    <HomeArranging
                      home={homeRef}
                      layout={layout}
                      tiles={tiles}
                      drawn={empty}
                      arrange={arrangeHome}
                    />
                  )}
                </>
              )}
            </div>
            {demo && state !== "ready" && (
              <div className={`state-notice ${state}`} role="status">
                <b>
                  {state === "stale"
                    ? "Stale sample preview"
                    : state === "empty"
                      ? "No records available"
                      : state === "loading"
                        ? "Loading sample preview…"
                        : "Unable to load sample preview"}
                </b>
                <span>
                  {state === "stale"
                    ? "Older readings stay visible with their timestamp."
                    : state === "empty"
                      ? "Authorized records will appear after an integration is connected."
                      : state === "loading"
                        ? "This is a paused loading-state preview."
                        : "This simulates an adapter error. No data has been lost."}
                </span>
                <button onClick={() => setState("ready")}>
                  {state === "error" ? "Retry demo" : "Restore demo"}
                </button>
              </div>
            )}
          </main>
        </div>
        {/* Outside the inert content, so its Undo stays in reach over any view. */}
        {/* Native readers must not register/sync a household web screen. */}
        {!launcher && (
          <>
            <LiveScreen
              session={session}
              screen={screen}
              stored={stored}
              actions={screenActions}
              named={adoptName}
              taken={takeScreenId}
            />
            <Studio
              requireRunner
              screen={screen.id}
              enabled={Boolean(session) && !graphDemo}
              parts={partsIn(false)}
              saved={(id) => {
                screenActions.fullScreen(id);
                void screenActions.reread();
                // Open already (Home's own, say): the version just saved replaces it at once.
                dashboardSaved(id);
              }}
            />
          </>
        )}
        {full && session && (
          // A full screen that can't open gives way to what is under it by
          // itself, not to Home, which may be this same dashboard.
          <FeatureBoundary
            key={full.id}
            close={leaveFull}
            failed={() => fullFailed(full.id)}
          >
            <FullScreen
              id={full.id}
              title={titleOf(full.id) ?? "Dashboard"}
              session={session}
              exit={leaveFull}
              nativeReader={launcher}
              // A mark on the page leads into the sky, to a star on the map.
              onOpenStar={(id) => {
                if (node(id)) selectNode(id);
              }}
              parts={partsIn(false)}
            />
          </FeatureBoundary>
        )}
        <AnimatePresence>
          {modal && (
            <FeatureBoundary close={close}>
              <Suspense
                fallback={
                  <div className="detail-backdrop" role="status">
                    <span className="sr-only">Opening details…</span>
                  </div>
                }
              >
                <Details
                  view={view}
                  behind={full !== null}
                  data={{ ...data, sources }}
                  state={state}
                  setState={setState}
                  paused={paused}
                  setPaused={setPaused}
                  layout={layout}
                  setLayout={chooseLayout}
                  arranged={grid !== null}
                  from={panelFrom}
                  home={{
                    dashboard: homeId
                      ? {
                          title: titleOf(homeId) ?? null,
                          missing: homeMissing,
                          failed: homeFailed,
                        }
                      : null,
                    onRestore: () => setHome(null),
                  }}
                  screenName={
                    session?.screenListen
                      ? { name: screen.name, onRename: renameScreen }
                      : undefined
                  }
                  idleReturn={idleReturn}
                  setIdleReturn={setIdleReturn}
                  automaticLinks={automaticLinks}
                  setAutomaticLinks={setAutomaticLinks}
                  workItems={workItems}
                  // Offered only while the map has a work item, or while they are hidden on this screen.
                  setWorkItems={
                    workOffered ||
                    !workItems ||
                    skyPage.nodes.some((n) => workOf(n))
                      ? setWorkItems
                      : undefined
                  }
                  close={close}
                  selectNode={selectNode}
                  readData={session?.data}
                  changes={{
                    nodes: blocked ? [] : overview.nodes,
                    edges: overview.edges,
                  }}
                  onSources={() => open("sources")}
                  onChanged={() => open("changed")}
                  dashboard={
                    view === "dashboard" && cardPanel && session
                      ? {
                          id: cardPanel.id,
                          corner: cardPanel.corner,
                          title:
                            dashboards.list.find((d) => d.id === cardPanel.id)
                              ?.title ?? "Dashboard",
                          session,
                          onReader: skyPage.nodes.some(
                            (node) => node.id === dashboardStarId(cardPanel.id),
                          )
                            ? () => openDashboard(cardPanel.id)
                            : undefined,
                          screen: dashboardScreen,
                          parts: partsIn(full !== null),
                        }
                      : undefined
                  }
                  cards={
                    session?.setDashboardCard
                      ? {
                          dashboards: dashboards.list,
                          chosen: dashboards.cards,
                          onOpen: (id) => {
                            setCardPanel({ id });
                            open("dashboard");
                          },
                          onChoose: dashboards.setCard,
                        }
                      : undefined
                  }
                  insight={{
                    openId: insightId,
                    insights: insights.insights,
                    nodes: knowledge.page.nodes,
                    from: insightFrom,
                    pending: insights.pending,
                    failed: insights.failed,
                    runs: insights.runs,
                    onFeedback: (id, value, until, note) =>
                      void insights.send(id, value, until, note),
                    onTrail: showTrail,
                    onOpen: setInsightId,
                  }}
                />
              </Suspense>
            </FeatureBoundary>
          )}
        </AnimatePresence>
      </div>
    </MotionConfig>
  );
}
