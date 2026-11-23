import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
} from "react";
import { Undo2, X } from "lucide-react";
import type { GraphSession } from "./liveGraph";
import { followHousehold } from "./dashboards";
import type { DashboardCards } from "./dashboards";
import {
  cleanSettings,
  commandArgs,
  layoutNames,
  swapTiles,
  tileNames,
} from "./screens";
import type {
  Layout,
  ScreenCommand,
  ScreenSettings,
  Showing,
  Tile,
  Tiles,
} from "./screens";
import {
  arrangeCommand,
  canonical,
  changeHome,
  changeLayout,
  cleanHomeGrid,
  cornerSwap,
  designedTiles,
  firstHomeGrid,
  homePlaces,
  homePresets,
  homeShowingParts,
  showingParts,
  swapCornerRects,
} from "./parts";
import type {
  ArrangeCommand,
  HomeGrid,
  HomeMeasure,
  HomePlaces,
  Layout as PartsLayout,
} from "./parts";
import {
  cantArrange,
  dashboardGrid,
  replay,
  restoreArrangement,
  runGrids,
  saveArrangement,
} from "./arranging";
import type { DashboardGrid, InPlace, InPlaceOutcome } from "./arranging";
import { useScreenListen } from "./useScreenListen";
import {
  runDashboardControl,
  undoDashboardSelection,
} from "./dashboardControls";
import type { SelectionUndo } from "./dashboardControls";
import { openStudio } from "./studioProtocol";

// This copy of the app as a live screen (docs/dev/screens.md section 6): it listens for
// commands, applies each through App's own functions (handed in fresh each render and read
// at the moment a command runs), keeps what each one changed so Undo can put it back, and
// shows a small note naming the sender. It renders only that note, outside App's inert content.
// From piece 3 (10.6) it also arranges the parts of what the screen shows: a dashboard's
// through the grid drawing it (arranging.ts), Home's through App's grid. A person's own
// change on a grid comes here too, for the same note and the same Undo.

// A view as Undo puts it back: Home, a star, a dashboard, a search, or another of App's views,
// and a dashboard full screen over it (this screen's Home, or one shown until someone exits,
// overHome when it replaced the Home dashboard).
export interface ScreenView {
  view: Exclude<Showing["view"], "full_screen">;
  star?: string;
  dashboard?: string;
  query?: string;
  other?: string;
  full?: { dashboard: string; home: boolean; overHome?: boolean };
}
export interface ScreenActions {
  showing: () => Showing;
  view: () => ScreenView;
  // A star's or a dashboard's title while it is on this screen's map, else null; and a saved
  // dashboard's title while it is among this screen's dashboards, which a corner can show.
  starTitle: (id: string) => string | null;
  dashboardTitle: (id: string) => string | null;
  savedTitle: (id: string) => string | null;
  // Brings a star this screen's map lacks onto it from the engine's whole map, or a dashboard
  // onto the sky whatever stars it reads, and keeps it there until Home.
  fetchStar: (id: string) => Promise<void>;
  fetchDashboard: (id: string) => Promise<void>;
  // Reads the dashboards again, for a corner's card newer than them.
  reread: () => Promise<void>;
  openStar: (id: string) => void;
  openDashboard: (id: string) => void;
  search: (query: string) => { matches: number; titles: string[] };
  // Shows a dashboard full screen over what is open, and takes it away again.
  fullScreen: (id: string) => void;
  exitFullScreen: () => void;
  // Goes Home: this screen's Home dashboard full screen, or the sky and tiles.
  home: () => void;
  restore: (view: ScreenView) => void;
  // This screen's Home dashboard (null for the default Home), and choosing another.
  homeDashboard: string | null;
  setHome: (dashboard: string | null) => void;
  layout: Layout;
  setLayout: (layout: Layout) => void;
  tiles: Tiles;
  setTiles: (tiles: Tiles) => void;
  narrow: boolean;
  // The corners' dashboard cards, or null where they can't be chosen. Choosing followHousehold
  // drops a viewer session's own choice, so the corner shows the household's again.
  cards: DashboardCards | null;
  setCard: (corner: Tile, dashboard: string | null) => Promise<void>;
  // This screen's Home grid (10.5), null while it has none, kept like the layout and tiles;
  // and what Home's CSS grid draws now, for the grid the first move or resize makes (null on a
  // screen not drawing Home's layouts). An app without them takes no part commands at Home.
  grid?: HomeGrid | null;
  setGrid?: (grid: HomeGrid | null) => void;
  measureHome?: () => HomeMeasure | null;
}
// What one command changed, kept so Undo can put it back: for a dashboard's arrangement, its
// id, version and layout before the change, with the revision the change was made on and the
// one it saved (which nobody may have changed since); at Home, the grid or its absence; for a
// corner, its card before, or followHousehold where a viewer session's corner showed the
// household's (no choice of its person's own, migration 032).
interface Saved {
  selection?: SelectionUndo;
  summary: string;
  view?: ScreenView;
  layout?: Layout;
  tiles?: Tiles;
  corner?: { corner: Tile; dashboard: string | null };
  home?: { dashboard: string | null };
  arrangement?: {
    id: string;
    version: number;
    layout: PartsLayout;
    base: number;
    revision: number;
  };
  grid?: { grid: HomeGrid | null };
}
type Outcome = { result: "shown" | "failed"; detail: Record<string, unknown> };
// At most 20 commands can be undone, newest first; the note fades after 10 seconds.
const undoDepth = 20,
  noteMs = 10_000;
const narrowNote = "Saved; this screen is too narrow to show layouts.";
const foldedNote = "Saved; this screen shows the parts in a column";
const nothingHere = "Nothing here has parts; open a dashboard or go Home";
const earlier = "This is an earlier version; open the newest to arrange it";
const notDrawn =
  "This dashboard hasn't finished drawing yet; nothing was changed";
const failed = (summary: string): Outcome => ({
  result: "failed",
  detail: { summary },
});
// This screen's Home as its settings keep it.
const home = (dashboard: string | null) => (dashboard ? { dashboard } : null);
// This screen's settings as they are sent: the Home grid only once it has one.
const settingsOf = (a: ScreenActions): ScreenSettings => ({
  layout: a.layout,
  tiles: a.tiles,
  home: home(a.homeDashboard),
  ...(a.grid ? { grid: a.grid } : {}),
});
// The grid the first move or resize at Home makes: measured where Home's layouts show.
const firstGrid = (a: ScreenActions) =>
  firstHomeGrid(a.layout, a.tiles, a.narrow ? null : a.measureHome?.());
// What a part command changes (10.6): the dashboard open here, in the reader, a panel or full
// screen (a Home dashboard included), or else Home when the screen is at its default Home.
function arrangeTarget(
  view: ScreenView,
): { id: string; full: boolean } | "home" | null {
  if (view.full) return { id: view.full.dashboard, full: true };
  if (view.view === "dashboard" && view.dashboard)
    return { id: view.dashboard, full: false };
  return view.view === "home" ? "home" : null;
}

export function LiveScreen({
  session,
  screen,
  stored,
  actions,
  named,
  taken,
}: {
  session: GraphSession | null;
  screen: { id: string; name: string };
  // Whether this browser had its own settings when the app opened.
  stored: boolean;
  actions: ScreenActions;
  // The name the engine keeps for this screen, from each answer; answers the name this
  // screen goes by now (null to keep its own).
  named?: (name: string) => string | null;
  // The engine refused this screen's id as another app login's (docs/dev/screens.md 3.2): takes
  // a new one, which comes back as `screen`, and answers whether it did.
  taken?: (refused: string) => boolean;
}) {
  const latest = useRef(actions),
    commits = useRef<(() => void)[]>([]),
    // The settings taken from the engine and those they replace, until App has drawn them
    // (or someone changed them since): the next listen goes out at once, before App draws
    // them, and must not send back the ones they replaced.
    took = useRef<{ settings: ScreenSettings; over: string } | null>(null);
  useLayoutEffect(() => {
    latest.current = actions;
    if (
      took.current &&
      JSON.stringify(settingsOf(actions)) !== took.current.over
    )
      took.current = null;
    for (const committed of commits.current.splice(0)) committed();
  });
  // What a command changed shows once App has drawn it (and the explorer said what it shows,
  // just after); a change that draws nothing waits a moment instead. Then it is reported.
  const drawn = () =>
    new Promise<void>((resolve) => {
      const timer = setTimeout(finish, 300);
      function finish() {
        clearTimeout(timer);
        resolve();
      }
      commits.current.push(() => setTimeout(finish, 0));
    });
  const stack = useRef<Saved[]>([]);
  const [note, setNote] = useState<{
    key: number;
    from: string | null;
    text: string;
    undo: boolean;
  } | null>(null);
  const notes = useRef(0);
  const say = (from: string | null, text: string, undo: boolean) =>
    setNote({ key: ++notes.current, from, text, undo });
  useEffect(() => {
    if (!note) return;
    const timer = setTimeout(() => setNote(null), noteMs);
    return () => clearTimeout(timer);
  }, [note]);
  // The settings last sent (or taken from the engine): a browser with its own copy sends it on
  // the first listen, one without takes the engine's and sends only after a change. Only a
  // browser that kept its screen id has an engine's copy: a cleared one is a new screen.
  const opened = JSON.stringify(settingsOf(actions));
  const sentSettings = useRef<string | null>(stored ? null : opened);
  // The settings this screen opened with: the engine's copy comes with the first answer, up
  // to 25 seconds later, and never replaces a choice someone made here meanwhile.
  const openedWith = useRef(opened);
  // Whether the engine keeps this screen's Home grid: one that answers a listen carrying the
  // grid without it is older than piece 3 (the facade left it out), so it is not sent again
  // for that.
  const gridKept = useRef(true);
  // A new id (the browser's login changed, 3.2) is a new screen at the engine: its first listen
  // carries this browser's settings, and what was done as the old one is not undone as the new.
  const listenedAs = useRef(screen.id);
  useLayoutEffect(() => {
    if (listenedAs.current === screen.id) return;
    listenedAs.current = screen.id;
    sentSettings.current = null;
    took.current = null;
    gridKept.current = true;
    stack.current = [];
  }, [screen.id]);
  const sessionNow = useRef(session);
  useLayoutEffect(() => {
    sessionNow.current = session;
  });
  // The engine checked the star or dashboard against its whole map, while this screen holds
  // only the overview of the best-connected stars (read every 15 minutes) and what it reached
  // from there: on a miss, fetch it and look once more.
  const known = async (
    look: () => string | null,
    fetch: () => Promise<void>,
  ) => {
    const found = look();
    if (found !== null) return found;
    await fetch();
    await drawn();
    return look();
  };
  const knownStar = (id: string) =>
    known(
      () => latest.current.starTitle(id),
      () => latest.current.fetchStar(id),
    );
  const knownDashboard = (id: string) =>
    known(
      () => latest.current.dashboardTitle(id),
      () => latest.current.fetchDashboard(id),
    );
  // A dashboard among this screen's saved ones (a corner, full screen or Home shows only those),
  // reading them again when it is newer than they are.
  const savedBoard = (id: string) =>
    known(
      () => latest.current.savedTitle(id),
      () => latest.current.reread(),
    );

  // A dashboard a command opened says what its parts are once its grid draws, which may take
  // a moment more than App's drawing: the outcome waits for that, briefly, on a screen that
  // reads dashboards.
  const gridDrawn = async (id: string, full: boolean) => {
    if (!sessionNow.current?.dashboard) return;
    for (let i = 0; i < 15 && !dashboardGrid(id, full); i++)
      await new Promise((resolve) => setTimeout(resolve, 100));
  };
  // A star's title for a map's focus in plain words.
  const titleOf = (star: string) => latest.current.starTitle(star) ?? undefined;
  // Arranges a dashboard's newest version as a grid draws it, and saves it as a layout
  // revision (arranging.ts): what Undo needs, or why not.
  const arrangeDashboard = async (
    grid: DashboardGrid,
    change: (layout: PartsLayout) => ReturnType<typeof changeLayout>,
  ): Promise<{ ok: true; saved: Saved } | { ok: false; reason: string }> => {
    // Nothing is arranged until the newest version's page has drawn (10.3), since one that fails
    // to draw falls back to the version before it: a command that comes while it is still
    // loading waits a moment for it.
    for (let i = 0; i < 30 && !grid.drawn && grid.version === grid.latest; i++)
      await new Promise((resolve) => setTimeout(resolve, 100));
    const save = sessionNow.current?.dashboardLayout;
    if (grid.version !== grid.latest) return { ok: false, reason: earlier };
    if (!grid.drawn) return { ok: false, reason: notDrawn };
    if (!grid.whole || !save) return { ok: false, reason: cantArrange };
    const result = await saveArrangement(save, grid, change);
    if (!result.ok) return result;
    return {
      ok: true,
      saved: {
        summary: result.summary,
        arrangement: {
          id: grid.id,
          version: result.version,
          layout: result.before,
          base: result.base,
          revision: result.revision,
        },
      },
    };
  };
  // The places of the designed Home this screen's grid came from: what its layout drew (measured
  // where layouts show) when a change was last made without a grid. A grid kept from before this
  // screen opened goes by its layout's table.
  const designed = useRef<{ layout: Layout; places: HomePlaces } | null>(null);
  const placesOf = (a: ScreenActions, drawn: HomeGrid): HomePlaces => {
    if (!a.grid) {
      designed.current = {
        layout: a.layout,
        places: homePlaces(drawn, a.tiles),
      };
      return designed.current.places;
    }
    return designed.current?.layout === a.layout
      ? designed.current.places
      : homePresets[a.layout];
  };
  // Keeps a Home change (10.5), `after` being the grid it comes to from `drawn`: one that comes
  // to the designed Home as places of its corners, with no grid, so a corner takes its place's
  // look and moves made back leave Home exactly as it was; a swap of two corners as move_tile
  // (their places, and their rectangles in the grid); anything else as the grid.
  const keepHome = (
    a: ScreenActions,
    drawn: HomeGrid,
    after: HomeGrid,
    swap: { tile: Tile; to: Tile } | null,
    saved: Saved,
  ) => {
    const tiles = designedTiles(after, placesOf(a, drawn));
    if (tiles) {
      if (canonical(tiles) !== canonical(a.tiles)) {
        saved.tiles = a.tiles;
        a.setTiles(tiles);
      }
      if (a.grid) {
        saved.grid = { grid: a.grid };
        a.setGrid?.(null);
      }
      return;
    }
    if (swap) {
      saved.tiles = a.tiles;
      a.setTiles(swapTiles(a.tiles, swap.tile, swap.to));
    }
    saved.grid = { grid: a.grid ?? null };
    a.setGrid?.(after);
  };
  const push = (saved: Saved) => {
    stack.current = [saved, ...stack.current].slice(0, undoDepth);
  };

  // A change made by hand that wasn't made says why, in the same note (with no Undo), so the
  // reason shows to everyone and not only to a screen reader.
  const refuse = (reason: string): InPlaceOutcome => {
    say(null, reason, false);
    return { ok: false, reason };
  };
  // A person's own change on a grid, by hand: saved as a command's would be, with the same
  // note (naming no sender) and on the same Undo stack.
  const inPlace = async (change: InPlace): Promise<InPlaceOutcome> => {
    let saved: Saved;
    if (change.kind === "dashboard") {
      const made = await arrangeDashboard(change.grid, (layout) =>
        replay(layout, change.commands, change.summary),
      );
      if (!made.ok) return refuse(made.reason);
      saved = made.saved;
    } else if (change.kind === "layout") {
      // A layout chosen in Sources & settings puts its designed Home back and drops the grid,
      // as the layout command does (10.5); Undo brings the grid back.
      const a = latest.current;
      saved = {
        summary: `Switched to the ${layoutNames[change.layout]} layout`,
        layout: a.layout,
      };
      a.setLayout(change.layout);
      if (a.grid) {
        saved.grid = { grid: a.grid };
        a.setGrid?.(null);
      }
    } else {
      const a = latest.current;
      if (!a.setGrid) return refuse("This screen can't do that.");
      const swap = cornerSwap(
        change.before,
        change.after,
        a.tiles,
        change.part,
      );
      saved = {
        summary: swap
          ? `Moved ${tileNames[swap.tile]} to the ${tileNames[swap.to]} place`
          : change.summary,
      };
      keepHome(a, change.before, change.after, swap, saved);
    }
    push(saved);
    say(null, saved.summary, true);
    return { ok: true };
  };
  const inPlaceNow = useRef(inPlace);
  useLayoutEffect(() => {
    inPlaceNow.current = inPlace;
  });
  useEffect(
    () =>
      runGrids({
        arrange: (change) => inPlaceNow.current(change),
        refuse: (reason) => void refuse(reason),
      }),
    [],
  );

  // Puts back what the newest command changed; says what, or why not.
  const undo = useCallback(async (): Promise<Outcome> => {
    const saved = stack.current.shift();
    if (!saved) return failed("Nothing to undo");
    if (saved.selection) {
      const outcome = await undoDashboardSelection(saved.selection);
      if (outcome.result === "shown") {
        const below = stack.current.find(
          (s) =>
            s.selection?.dashboard === saved.selection!.dashboard &&
            s.selection.version === saved.selection!.version,
        );
        if (
          below?.selection &&
          canonical(below.selection.after) === canonical(outcome.detail.values)
        )
          below.selection.base = outcome.detail.selectionRevision as number;
      }
      return outcome;
    }
    try {
      if (saved.corner)
        await latest.current.setCard(
          saved.corner.corner,
          saved.corner.dashboard,
        );
    } catch {
      stack.current.unshift(saved);
      return failed("The corner couldn't be changed back right now.");
    }
    // A dashboard's earlier arrangement goes back as a new revision, provided nobody changed
    // it since; then every grid drawing that version shows it.
    const save = sessionNow.current?.dashboardLayout;
    if (saved.arrangement) {
      const back = save
        ? await restoreArrangement(
            save,
            saved.arrangement,
            `Undone: ${saved.summary}`,
          )
        : { ok: false as const, reason: cantArrange, transient: true as const };
      if (!back.ok) {
        // Kept for another try only when it may work later (no answer came back, or it
        // couldn't be sent right now); a refusal stands, so it goes, and says why.
        if ("transient" in back && back.transient) {
          stack.current.unshift(saved);
          return failed(back.reason);
        }
        if ("stale" in back && back.stale) return failed(back.reason);
        const { id, version } = saved.arrangement;
        return failed(
          (dashboardGrid(id)?.latest ?? version) > version
            ? "The dashboard has a newer version since; nothing undone"
            : `Nothing undone: ${back.reason}`,
        );
      }
      // The change before it on this screen left the layout as it is again now, at the
      // revision Undo saved, so it can be undone in turn.
      const { id, version, base } = saved.arrangement;
      stack.current = stack.current.map((below) =>
        below.arrangement?.id === id &&
        below.arrangement.version === version &&
        below.arrangement.revision === base
          ? {
              ...below,
              arrangement: { ...below.arrangement, revision: back.revision },
            }
          : below,
      );
    }
    // The star or dashboard it goes back to may have left this screen's map since (Home lets
    // go of one a command fetched, and a refresh brings back only the overview).
    const back = saved.view;
    if (back?.view === "star" && back.star) await knownStar(back.star);
    if (back?.view === "dashboard" && back.dashboard)
      await knownDashboard(back.dashboard);
    const a = latest.current;
    if (saved.layout) a.setLayout(saved.layout);
    if (saved.tiles) a.setTiles(saved.tiles);
    if (saved.grid) a.setGrid?.(saved.grid.grid);
    // The Home choice first: choosing one may go Home, and the view it was on comes after.
    if (saved.home) a.setHome(saved.home.dashboard);
    if (back) a.restore(back);
    return { result: "shown", detail: { summary: `Undone: ${saved.summary}` } };
  }, []);

  const apply = async (command: ScreenCommand): Promise<Outcome> => {
    // App's functions as they are now, again after any wait.
    let a = latest.current;
    // A screen's name is a label it chose, so it is always shown as a screen's: no name can
    // read as the assistant.
    const from =
      command.sender.kind === "assistant"
        ? "your assistant"
        : `the screen “${command.sender.name}”`;
    const args = commandArgs(command.action, command.args);
    if (!args) return failed("This screen can't do that.");
    if (command.action === "open_studio") {
      return (await openStudio(args.session as string))
        ? {
            result: "shown",
            detail: {
              summary: "Studio is open; waiting for validated draft updates",
            },
          }
        : failed(
            "Studio did not confirm opening; the durable session is preserved for reconnect.",
          );
    }
    if (
      command.action === "dashboard_controls" ||
      command.action === "dashboard_action"
    ) {
      const current = a.showing();
      if (current.dashboard?.id !== args.dashboard)
        return failed("This screen is showing another dashboard.");
      const outcome = await runDashboardControl(command.action, args);
      if (outcome.undo) {
        push({
          summary: "Changed dashboard selection",
          selection: outcome.undo,
        });
        say(from, "Changed dashboard selection", true);
      }
      return { result: outcome.result, detail: outcome.detail };
    }
    if (command.action === "undo") {
      const outcome = await undo();
      if (outcome.result === "shown") {
        say(from, String(outcome.detail.summary), false);
        await drawn();
      }
      return outcome;
    }
    const saved: Saved = { summary: "" };
    const detail: Record<string, unknown> = {};
    // A change to the Home tiles ends a full screen, so it shows; Undo brings it back.
    const leaveFullScreen = () => {
      const now = latest.current.view();
      if (!now.full) return;
      saved.view = now;
      latest.current.exitFullScreen();
    };
    switch (command.action) {
      case "open_star": {
        const title = await knownStar(args.star as string);
        if (title === null)
          return failed("That star isn't on this screen's map.");
        a = latest.current;
        saved.view = a.view();
        a.openStar(args.star as string);
        saved.summary = `Opened ${title}`;
        break;
      }
      case "open_dashboard": {
        const title = await knownDashboard(args.dashboard as string);
        if (title === null)
          return failed("That dashboard isn't on this screen's map.");
        a = latest.current;
        saved.view = a.view();
        a.openDashboard(args.dashboard as string);
        saved.summary = `Opened ${title}`;
        break;
      }
      case "search": {
        const query = args.query as string;
        saved.view = a.view();
        const found = a.search(query);
        saved.summary = `Searched for “${query}”: ${found.matches} ${found.matches === 1 ? "star" : "stars"}`;
        detail.matches = found.matches;
        detail.titles = found.titles;
        break;
      }
      case "home":
        saved.view = a.view();
        a.home();
        saved.summary = "Back to Home";
        break;
      case "full_screen": {
        // Without a dashboard, the one open here: in the reader or a panel, or full screen.
        const now = a.view();
        const id =
          (args.dashboard as string | undefined) ??
          now.full?.dashboard ??
          (now.view === "dashboard" ? now.dashboard : undefined);
        if (!id) return failed("No dashboard is open");
        const title = await savedBoard(id);
        if (title === null)
          return failed("That dashboard isn't among this screen's dashboards.");
        a = latest.current;
        saved.view = a.view();
        a.fullScreen(id);
        saved.summary = `Showing ${title} full screen`;
        break;
      }
      case "set_home": {
        const dashboard = args.dashboard as string | null;
        const title = dashboard === null ? null : await savedBoard(dashboard);
        if (dashboard !== null && title === null)
          return failed("That dashboard isn't among this screen's dashboards.");
        a = latest.current;
        // Choosing a Home while the screen is at Home shows it now, so that view is kept too.
        saved.home = { dashboard: a.homeDashboard };
        const now = a.view();
        if (now.view === "home" && (!now.full || now.full.home))
          saved.view = now;
        a.setHome(dashboard);
        saved.summary =
          dashboard === null
            ? "This screen's Home is the sky and tiles again"
            : `${title} is this screen's Home now`;
        break;
      }
      case "layout": {
        const layout = args.layout as Layout;
        leaveFullScreen();
        saved.layout = a.layout;
        a.setLayout(layout);
        // A layout, even the one shown, is the way back to the designed Home: it drops the
        // grid, which Undo brings back.
        if (a.grid) {
          saved.grid = { grid: a.grid };
          a.setGrid?.(null);
        }
        saved.summary = `Switched to the ${layoutNames[layout]} layout`;
        if (a.narrow) detail.note = narrowNote;
        break;
      }
      case "move_tile": {
        const tile = args.tile as Tile,
          to = args.to as Tile;
        leaveFullScreen();
        // With a grid the two corners trade their rectangles too, sizes included (and a grid
        // that comes back to the designed Home goes).
        if (a.grid && a.setGrid) {
          const swapped = swapCornerRects(a.grid, a.tiles, tile, to);
          keepHome(a, a.grid, swapped, { tile, to }, saved);
        } else {
          saved.tiles = a.tiles;
          a.setTiles(swapTiles(a.tiles, tile, to));
        }
        saved.summary = `Moved ${tileNames[tile]} to the ${tileNames[to]} place`;
        if (a.narrow) detail.note = narrowNote;
        break;
      }
      case "move":
      case "resize":
      case "focus_map": {
        const change = arrangeCommand(command.action, args);
        if (!change) return failed("This screen can't do that.");
        const target = arrangeTarget(a.view());
        if (!target) return failed(nothingHere);
        // `dashboard` is a guard: the screen must show that dashboard, or null its Home.
        if (Object.hasOwn(args, "dashboard")) {
          if (args.dashboard === null && target !== "home")
            return failed("This screen isn't at its Home now");
          if (
            args.dashboard !== null &&
            (target === "home" || target.id !== args.dashboard)
          )
            return failed("This screen isn't showing that dashboard now");
        }
        if (target === "home") {
          if (!a.setGrid) return failed("This screen can't do that.");
          const drawn = a.grid ?? firstGrid(a);
          const made = changeHome(a.grid ?? null, drawn, a.tiles, change);
          if (!made.ok) return failed(made.reason);
          // A swap on a screen without a grid comes back with none: its grid is the swap.
          const after =
            made.grid ??
            swapCornerRects(drawn, a.tiles, made.swap!.tile, made.swap!.to);
          keepHome(a, drawn, after, made.swap, saved);
          saved.summary = made.summary;
          if (a.narrow) detail.note = narrowNote;
          break;
        }
        // The grid drawing it, once it has opened here.
        let grid = dashboardGrid(target.id, target.full);
        if (!grid) {
          await drawn();
          grid = dashboardGrid(target.id, target.full);
        }
        if (!grid) return failed("That dashboard hasn't opened here yet");
        const made = await arrangeDashboard(grid, (layout) =>
          changeLayout(layout, change as ArrangeCommand, titleOf),
        );
        if (!made.ok) return failed(made.reason);
        Object.assign(saved, made.saved);
        if (grid.fold !== "saved") detail.note = foldedNote;
        break;
      }
      case "corner_card": {
        const corner = args.corner as Tile,
          dashboard = args.dashboard as string | null;
        if (!a.cards) return failed("Corner cards aren't available here.");
        // A card this screen can't draw is never reported shown.
        const title = dashboard === null ? null : await savedBoard(dashboard);
        if (dashboard !== null && title === null)
          return failed("That dashboard isn't among this screen's dashboards.");
        a = latest.current;
        if (!a.cards) return failed("Corner cards aren't available here.");
        // A corner a viewer session hasn't chosen for itself shows the household's: Undo drops
        // the choice this makes, rather than keep the household's card as the session's own.
        const own = !a.cards.own || a.cards.own.includes(corner);
        saved.corner = {
          corner,
          dashboard: own ? (a.cards[corner] ?? null) : followHousehold,
        };
        try {
          await a.setCard(corner, dashboard);
        } catch {
          return failed(
            `The ${tileNames[corner]} corner couldn't be changed right now.`,
          );
        }
        // Only a card that changed ends a full screen.
        leaveFullScreen();
        saved.summary =
          dashboard === null
            ? `The ${tileNames[corner]} corner shows its own tile again`
            : `Showing ${title} in the ${tileNames[corner]} corner`;
        break;
      }
      default:
        return failed("This screen can't do that.");
    }
    push(saved);
    say(from, saved.summary, true);
    await drawn();
    const opened = arrangeTarget(latest.current.view());
    if (
      (command.action === "open_dashboard" ||
        command.action === "full_screen") &&
      opened &&
      opened !== "home"
    )
      await gridDrawn(opened.id, opened.full);
    return { result: "shown", detail: { summary: saved.summary, ...detail } };
  };

  // What this screen shows, with its parts (10.8) when App doesn't say them: a dashboard's
  // layout as its grid draws it (the page alone until it has), Home's grid or the one the first
  // move would make, else none. A folded dashboard is narrow too.
  const showing = (): Showing => {
    const a = latest.current,
      now = a.showing();
    if (now.parts) return now;
    const board =
      (now.view === "dashboard" || now.view === "full_screen") && now.dashboard
        ? dashboardGrid(now.dashboard.id, now.view === "full_screen")
        : undefined;
    const parts =
      now.view === "home" && a.setGrid
        ? homeShowingParts(a.grid ?? firstGrid(a))
        : board !== undefined || now.dashboard
          ? showingParts(board?.layout ?? null, titleOf)
          : [];
    return {
      ...now,
      ...(board && board.fold !== "saved" ? { narrow: true } : {}),
      parts,
    };
  };

  useScreenListen(session, screen, {
    showing,
    settings: () => {
      const value = took.current?.settings ?? settingsOf(latest.current);
      const mark = JSON.stringify(value);
      return mark === sentSettings.current ? null : { value, mark };
    },
    settingsSent: (mark) => (sentSettings.current = mark),
    adopt: (settings) => {
      const a = latest.current,
        engine = cleanSettings(settings);
      if (stored || JSON.stringify(settingsOf(a)) !== openedWith.current)
        return;
      const layout = engine.layout ?? a.layout,
        tiles = engine.tiles ?? a.tiles,
        grid = engine.grid && a.setGrid ? engine.grid : (a.grid ?? null),
        chosen =
          engine.home === undefined
            ? a.homeDashboard
            : (engine.home?.dashboard ?? null);
      if (engine.layout) a.setLayout(layout);
      if (engine.tiles) a.setTiles(tiles);
      if (grid !== (a.grid ?? null)) a.setGrid?.(grid);
      if (chosen !== a.homeDashboard) a.setHome(chosen);
      const taken: ScreenSettings = {
        layout,
        tiles,
        home: home(chosen),
        ...(grid ? { grid } : {}),
      };
      sentSettings.current = JSON.stringify(taken);
      if (sentSettings.current !== openedWith.current)
        took.current = { settings: taken, over: openedWith.current };
    },
    // A later answer whose settings lack the Home grid this browser keeps (an older tab of the
    // same browser listened since, 10.5): this screen sends its settings again with its next
    // listen. Not to an engine that dropped the grid this screen sent.
    kept: (settings, carried) => {
      const theirs = cleanHomeGrid(settings.grid);
      if (carried) {
        if (carried.grid && !theirs) gridKept.current = false;
        return;
      }
      if (
        gridKept.current &&
        canonical(latest.current.grid ?? null) !== canonical(theirs)
      )
        sentSettings.current = null;
    },
    named: (name) => named?.(name) ?? null,
    taken: (refused) => taken?.(refused) ?? false,
    apply,
  });

  if (!note) return null;
  return (
    <p className="screen-note" role="status" key={note.key}>
      <span>
        {note.from && <b>From {note.from}: </b>}
        {note.text}
      </span>
      {note.undo && (
        <button
          onClick={() =>
            // What was undone, or why nothing was.
            void undo().then((outcome) =>
              say(null, String(outcome.detail.summary), false),
            )
          }
        >
          <Undo2 size={15} aria-hidden="true" /> Undo
        </button>
      )}
      <button aria-label="Dismiss" onClick={() => setNote(null)}>
        <X size={16} aria-hidden="true" />
      </button>
    </p>
  );
}
