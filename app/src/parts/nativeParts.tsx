import type { ReactNode } from "react";
import type { DashboardSummary } from "../dashboards";
import type { DailyInsight, InsightRun } from "../insights";
import type { GraphSession } from "../liveGraph";
import type { FocusedPage } from "../mapFocus";
import type { DemoState, ObservatoryData, SourceStatus } from "../model";
import type { Fold, Part } from "../parts";
import type { ExploreLit, MapSky } from "./MapPart";
import { NativePart } from "./NativePart";

// The native parts of a dashboard's grid (docs/dev/screens.md 10.3): what the
// app draws itself from its own data, around the page and never inside it.
// Each is the working piece Home draws, laid out by its cell's size. The grid
// takes `render` from nativeParts(context) and draws the page itself: `render`
// answers null for a page, and for a kind this app doesn't know, whose cell
// stays empty.

// What the parts read and do, as Home's tiles read and do it.
export interface PartsContext {
  // Home's data as App reads it; null while access is lost, when no part
  // shows anything, as Home shows nothing.
  data: ObservatoryData | null;
  // The sources as Home lists them (with each source's records).
  sources: SourceStatus[];
  state: DemoState;
  // The demo's preview of a missing or failed source.
  blocked: boolean;
  demo: boolean;
  session: GraphSession | null;
  sky: MapSky;
  insights: {
    current: DailyInsight | null;
    insights: DailyInsight[];
    runs: InsightRun[];
  };
  dashboards: { list: DashboardSummary[]; complete: boolean };
  // A panel, the explorer or a full screen covers the grid.
  paused: boolean;
  // A tile's panel, growing from the element with layout id `from`.
  openPanel: (
    view: "glucose" | "recovery" | "training" | "changed",
    from: string,
  ) => void;
  openInsight: (id: string, from: string) => void;
  openSources: () => void;
  // A star as a command opens one: fetched onto the screen's map first, with
  // its neighbours, when it lies beyond it.
  openStar: (id: string) => void;
  openDashboard: (id: string) => void;
  explore: (lit: ExploreLit | null) => void;
  // A focused map part's read (10.7): absent with an engine older than
  // piece 3, when a focused part says the map can't be read.
  readMap?: (
    part: Part,
    cursor: string | null,
    signal: AbortSignal,
  ) => Promise<FocusedPage>;
  // Clears a map part's focus as focus_map would; absent where nothing is
  // arranged (an earlier version, a layout this app can't arrange).
  showEverything?: (part: Part) => void;
}

export function nativeParts(context: PartsContext) {
  return (part: Part, fold: Fold = "saved"): ReactNode =>
    part.kind === "page" ? null : (
      <NativePart part={part} fold={fold} context={context} />
    );
}
