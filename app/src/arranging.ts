import type { GraphSession } from "./liveGraph";
import { changeLayout, layoutOf, measureHome } from "./parts";
import type {
  ArrangeCommand,
  Arranged,
  Fold,
  HomeGrid,
  HomeMeasure,
  Layout,
} from "./parts";
import type { Layout as HomeLayout, Tiles } from "./screens";

// Where this screen's runner (LiveScreen.tsx) meets the grids that draw its dashboards
// (DashboardView.tsx) and its Home (docs/dev/screens.md 10.6 and 10.9). Each drawn dashboard
// says here what it shows: the version, its layout as drawn with its revision, whether that was
// read whole, and how it is folded. The runner arranges what the screen shows through it, and a
// person's own change on a grid goes to the runner, so it shows the same note and goes on the
// same Undo stack as a command. In memory only, like the dashboards' cache.

// One dashboard as a grid draws it.
export interface DashboardGrid {
  id: string;
  // Drawn full screen (FullScreen.tsx), rather than in the reader or the dashboard panel.
  full: boolean;
  version: number;
  latest: number;
  // The layout as drawn (null: the page alone), its revision, and whether it was read whole:
  // only a layout read whole is arranged.
  layout: Layout | null;
  revision: number;
  whole: boolean;
  fold: Fold;
  // Whether this version's page has drawn: nothing is arranged before it has (10.3), since a
  // page that fails to draw falls back to the version before it.
  drawn: boolean;
  // Draws an arrangement in place, without loading any frame again; whole unless said.
  show: (layout: Layout | null, revision: number, whole?: boolean) => void;
  // Reads the dashboard's feeds again, which say how it is arranged now, and whether it has a
  // newer version.
  reread: () => void;
}
const grids = new Set<DashboardGrid>();
// Keeps a dashboard's grid here while it is drawn; answers how to let it go.
export function drawGrid(grid: DashboardGrid) {
  grids.add(grid);
  return () => void grids.delete(grid);
}
// The grid drawing a dashboard: full screen when the screen shows it so, else in the reader or
// a panel, else any.
export function dashboardGrid(id: string, full = false) {
  const drawn = [...grids].filter((g) => g.id === id);
  return drawn.find((g) => g.full === full) ?? drawn[0];
}
// Every grid drawing that version of a dashboard (the reader or the dashboard panel stays drawn
// under a full screen of it), `also` among them: each shows an arrangement once it is saved.
const drawingOf = (id: string, version: number, also?: DashboardGrid) =>
  new Set([
    ...(also ? [also] : []),
    ...[...grids].filter((g) => g.id === id && g.version === version),
  ]);

// A person's own change, made on a grid by hand: a dashboard's moves and resizes, made again on
// a layout that changed meanwhile; Home's grid before and after (the grid the first change
// makes, on a screen without one); or a layout chosen in Sources & settings while Home has a
// grid, which drops it (10.5).
export type InPlace =
  | {
      kind: "dashboard";
      grid: DashboardGrid;
      commands: ArrangeCommand[];
      summary: string;
    }
  | {
      kind: "home";
      part: string;
      before: HomeGrid;
      after: HomeGrid;
      summary: string;
    }
  | { kind: "layout"; layout: HomeLayout };
export type InPlaceOutcome = { ok: true } | { ok: false; reason: string };
export interface GridRunner {
  arrange: (change: InPlace) => Promise<InPlaceOutcome>;
  // Says why a change made by hand wasn't made, where a change's note shows.
  refuse: (reason: string) => void;
}
let runner: GridRunner | null = null;
// The runner takes the grids' changes while it runs; answers how to stop.
export function runGrids(next: GridRunner) {
  runner = next;
  return () => {
    if (runner === next) runner = null;
  };
}
// A change made by hand, through the runner: null when no runner takes it (the grid saves it
// itself then, with no note or Undo).
export const arrangeInPlace = (change: InPlace) =>
  runner ? runner.arrange(change) : null;
// A change made by hand that the grid itself refused (no room where it was dropped), said
// where its note would have shown: false when no runner takes it.
export const refuseInPlace = (reason: string) =>
  runner ? (runner.refuse(reason), true) : false;

// Moves and resizes made again in turn, as a person made them, summed up as they were.
export function replay(
  layout: Layout,
  commands: ArrangeCommand[],
  summary: string,
): Arranged<Layout> {
  let result: Arranged<Layout> = {
    ok: false,
    reason: "Nothing was changed",
  };
  for (const command of commands) {
    result = changeLayout(result.ok ? result.grid : layout, command);
    if (!result.ok) return result;
  }
  return result.ok ? { ...result, summary } : result;
}

// A change's note as the engine keeps it: the summary, on one line, well inside 500 characters.
const noteOf = (summary: string) =>
  Array.from(summary.replace(/\p{Cc}/gu, " "))
    .slice(0, 300)
    .join("");
export type SavedArrangement =
  | {
      ok: true;
      summary: string;
      announcement: string;
      // The layout before the change, the revision it was made on and the one it saved,
      // which Undo needs.
      before: Layout;
      version: number;
      base: number;
      revision: number;
    }
  | { ok: false; reason: string };
export const cantArrange = "This screen can't arrange this dashboard yet";
// The dashboards with an arrangement being saved now. A newer version found meanwhile waits for
// it to be answered (useDashboardVersion.ts), so the change is never drawn over another version.
const saving = new Map<string, number>(),
  waiting = new Map<string, (() => void)[]>();
export const arrangingNow = (id: string) => saving.has(id);
// Runs `then` once no arrangement of the dashboard is being saved: now, or when the last is answered.
export function afterArranging(id: string, then: () => void) {
  if (!saving.has(id)) return then();
  waiting.set(id, [...(waiting.get(id) ?? []), then]);
}
// Makes a change to a dashboard's newest layout and saves it as a layout revision (10.7): shown
// at once, and put back when it isn't saved. When the layout changed since it was drawn, the
// change is made once more on the layout as it stands and saved again; a second change meanwhile
// is refused. Never sent twice for one answer: a lost answer is reported, and the dashboard is
// read again to show what is true.
export async function saveArrangement(
  save: NonNullable<GraphSession["dashboardLayout"]>,
  grid: DashboardGrid,
  change: (layout: Layout) => Arranged<Layout>,
): Promise<SavedArrangement> {
  const id = grid.id;
  saving.set(id, (saving.get(id) ?? 0) + 1);
  try {
    return await arrangeAndSave(save, grid, change);
  } finally {
    const left = saving.get(id)! - 1;
    if (left) saving.set(id, left);
    else {
      saving.delete(id);
      const then = waiting.get(id) ?? [];
      waiting.delete(id);
      for (const f of then) f();
    }
  }
}
async function arrangeAndSave(
  save: NonNullable<GraphSession["dashboardLayout"]>,
  grid: DashboardGrid,
  change: (layout: Layout) => Arranged<Layout>,
): Promise<SavedArrangement> {
  const drawn = { layout: grid.layout, revision: grid.revision };
  // The layout as it stands, shown by every grid drawing this version; the change itself shows
  // on this grid alone until it is saved.
  const everywhere = (
    layout: Layout | null,
    revision: number,
    whole?: boolean,
  ) => {
    for (const g of drawingOf(grid.id, grid.version, grid))
      g.show(layout, revision, whole);
  };
  let before = layoutOf(drawn.layout),
    base = drawn.revision;
  let made = change(before);
  if (!made.ok) return made;
  grid.show(made.grid, base);
  let answer = await save(
    grid.id,
    grid.version,
    made.grid,
    base,
    noteOf(made.summary),
  );
  if (!answer.ok && "layoutRevision" in answer) {
    // Someone changed it meanwhile: the same change, on the layout as it stands now.
    everywhere(answer.layout, answer.layoutRevision, answer.layoutWhole);
    if (!answer.layoutWhole) return { ok: false, reason: cantArrange };
    before = layoutOf(answer.layout);
    base = answer.layoutRevision;
    made = change(before);
    if (!made.ok) return made;
    grid.show(made.grid, base);
    const current = { layout: answer.layout, revision: base };
    answer = await save(
      grid.id,
      grid.version,
      made.grid,
      base,
      noteOf(made.summary),
    );
    if (!answer.ok && "layoutRevision" in answer) {
      everywhere(answer.layout, answer.layoutRevision, answer.layoutWhole);
      return {
        ok: false,
        reason: "The layout changed again meanwhile; nothing was changed",
      };
    }
    if (!answer.ok) grid.show(current.layout, current.revision);
  } else if (!answer.ok) grid.show(drawn.layout, drawn.revision);
  if (!answer.ok) {
    grid.reread();
    return { ok: false, reason: answer.reason };
  }
  everywhere(answer.layout, answer.layoutRevision);
  return {
    ok: true,
    summary: made.summary,
    announcement: made.announcement,
    before,
    version: grid.version,
    base,
    revision: answer.layoutRevision,
  };
}
// Puts a dashboard's earlier layout back as a new revision (Undo, 10.6), provided nobody
// changed it since the change being undone; every grid drawing that version shows it. Answers
// the revision it saved, or why not: `stale` when the layout changed since, `transient` when it
// may work later (no answer came back, or the engine couldn't take it right now).
export async function restoreArrangement(
  save: NonNullable<GraphSession["dashboardLayout"]>,
  saved: { id: string; version: number; layout: Layout; revision: number },
  note: string,
): Promise<
  | { ok: true; revision: number }
  | { ok: false; reason: string; stale?: true; transient?: true }
> {
  const answer = await save(
    saved.id,
    saved.version,
    saved.layout,
    saved.revision,
    noteOf(note),
  );
  const drawing = drawingOf(saved.id, saved.version);
  if (!answer.ok && "layoutRevision" in answer) {
    for (const g of drawing)
      g.show(answer.layout, answer.layoutRevision, answer.layoutWhole);
    return {
      ok: false,
      reason: "The layout changed since; nothing undone",
      stale: true,
    };
  }
  if (!answer.ok) return answer;
  for (const g of drawing) g.show(answer.layout, answer.layoutRevision);
  return { ok: true, revision: answer.layoutRevision };
}

// What Home's CSS grid draws now, as the first move or resize measures it (10.5): its tracks,
// gaps and areas as the browser resolved them, each corner in the area of the place `tiles`
// gives it. Null when they can't be read (a screen not drawing Home's layouts).
export function measureHomeGrid(
  element: Element | null,
  tiles: Tiles,
): HomeMeasure | null {
  if (!element) return null;
  const style = getComputedStyle(element);
  const tracks = (value: string) =>
    value.trim().split(/\s+/).map(Number.parseFloat);
  const columns = tracks(style.gridTemplateColumns),
    rows = tracks(style.gridTemplateRows),
    gap = {
      column: Number.parseFloat(style.columnGap) || 0,
      row: Number.parseFloat(style.rowGap) || 0,
    };
  if (
    !style.gridTemplateAreas ||
    style.gridTemplateAreas === "none" ||
    [...columns, ...rows].some((t) => !Number.isFinite(t) || t < 0)
  )
    return null;
  return measureHome(
    { columns, rows, gap, areas: style.gridTemplateAreas },
    tiles,
  );
}
