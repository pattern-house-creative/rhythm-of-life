import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { ArrowRight } from "lucide-react";
import { Constellation } from "../Constellation";
import { isDashboardStar } from "../dashboards";
import { displayTitle } from "../graph";
import { ambientWindow, clusterGraph } from "../graphView";
import { catalogProvider } from "../instance";
import type { GraphPage } from "../knowledge";
import { focusSummary, focusedPage } from "../mapFocus";
import type { FocusedPage } from "../mapFocus";
import { canonical } from "../parts";
import type { MapFocus } from "../parts";
import { useMapPartShowing } from "./mapParts";

// The map (docs/dev/screens.md 10.3, 10.4, 10.9): the real sky, with the stars,
// links, drift, labels and pins Home's sky shows. Home's sky draws it, and so
// does each map part of a dashboard, in a view of its own: a part never moves
// Home's stars, never seeds the explorer, and narrows only its own sky to its
// focus.

// What the app holds of its map, as Home's sky draws it.
export interface MapSky {
  // The app's map with its dashboard stars, held whole in the demo.
  page: GraphPage;
  // What Home's sky draws of it: its overview, pinned stars first.
  overview: Pick<GraphPage, "nodes" | "edges">;
  pins: string[];
  // The demo's map is in the browser: a focus filters it here.
  demo: boolean;
  // The first map read hasn't answered yet.
  loading: boolean;
  // When the map was last read (every 15 minutes, after Sync now or a
  // rebuild): a focused part reads its own again then.
  readAt: string | null;
  // Whether it draws the dashed links ("Automatic links on the sky", Sources
  // & settings; absent: drawn). Off, a selected star still draws its own.
  automaticLinks?: boolean;
  // Whether it draws work items' stars ("Work items on the sky", Sources &
  // settings; absent: drawn). Off, a selected or pinned one still shows.
  workItems?: boolean;
}

// What "Explore" lights in the explorer: a focused part's stars, added to the
// screen's map, or for a focus that is only a query, that search.
export type ExploreLit = { title: string; page: GraphPage } | { query: string };

// The dashboard stars linked to a shown star, as Home's sky draws them.
function withDashboardStars(page: GraphPage, map: GraphPage): GraphPage {
  const shown = new Set(page.nodes.map((n) => n.id));
  const boards = new Map(
    map.nodes.filter(isDashboardStar).map((n) => [n.id, n]),
  );
  const links = map.edges.filter(
    (e) =>
      (boards.has(e.source) && shown.has(e.target)) ||
      (boards.has(e.target) && shown.has(e.source)),
  );
  const added = new Set(
    links.flatMap((e) => [e.source, e.target]).filter((id) => boards.has(id)),
  );
  return {
    ...page,
    nodes: [...page.nodes, ...[...added].map((id) => boards.get(id)!)],
    edges: [...page.edges, ...links],
  };
}

const count = (n: number) => n.toLocaleString();

export function MapPart({
  sky,
  focus = null,
  blocked,
  paused,
  own = true,
  still = false,
  selected = null,
  healthFocus = false,
  read,
  onStar,
  onExplore,
  onShowEverything,
}: {
  sky: MapSky;
  // Null shows everything Home's sky shows.
  focus?: MapFocus | null;
  // The demo's preview of a missing or failed source shows no stars.
  blocked: boolean;
  // Hidden behind a panel, the explorer or a full screen.
  paused: boolean;
  // A part's sky, not Home's.
  own?: boolean;
  // Folded: drawn settled and still, a vertical swipe scrolling the column.
  still?: boolean;
  selected?: string | null;
  healthFocus?: boolean;
  // The engine's focused read of this part's map (10.7), first page only.
  read?: (cursor: string | null, signal: AbortSignal) => Promise<FocusedPage>;
  onStar: (id: string) => void;
  onExplore: (lit: ExploreLit | null) => void;
  // Clears the focus, as focus_map would; absent where nothing is arranged.
  onShowEverything?: () => void;
}) {
  // While a part's sky shows, the explorer's own rests (mapParts.ts).
  useMapPartShowing(own);
  // The focus as stored: a new object with the same members is the same focus.
  const key = focus ? canonical(focus) : null;
  const wanted = useMemo(
    () => (key ? (JSON.parse(key) as MapFocus) : null),
    [key],
  );
  // A live focused part asks the engine, which filters the whole readable map;
  // the demo filters its map, held whole, the same way.
  const reader = useRef(read);
  useLayoutEffect(() => {
    reader.current = read;
  });
  const [answer, setAnswer] = useState<{
    key: string;
    page: FocusedPage | null;
  } | null>(null);
  const { demo: inBrowser, readAt } = sky;
  useEffect(() => {
    if (!key || inBrowser) return;
    let flight: AbortController | null = null;
    const ask = () => {
      flight?.abort();
      const controller = new AbortController();
      flight = controller;
      if (!reader.current) {
        setAnswer({ key, page: null });
        return;
      }
      reader.current(null, controller.signal).then(
        (page) => {
          if (!controller.signal.aborted) setAnswer({ key, page });
        },
        () => {
          if (!controller.signal.aborted) setAnswer({ key, page: null });
        },
      );
    };
    ask();
    // Read again when the app comes back into view, as the map is.
    const visible = () => {
      if (!document.hidden) ask();
    };
    document.addEventListener("visibilitychange", visible);
    return () => {
      flight?.abort();
      document.removeEventListener("visibilitychange", visible);
    };
    // Read again with each of the map's own reads, too.
  }, [key, inBrowser, readAt]);
  const { page: map, overview, pins } = sky;
  const demoPage = useMemo(
    () =>
      wanted && inBrowser
        ? focusedPage(
            {
              nodes: map.nodes.filter((n) => !isDashboardStar(n)),
              edges: map.edges,
            },
            wanted,
          )
        : null,
    [wanted, inBrowser, map],
  );
  const focused = inBrowser
    ? demoPage
    : answer?.key === key
      ? answer.page
      : undefined;
  const failed = !!wanted && focused === null;
  const waiting = !!wanted && focused === undefined;
  // A source that couldn't be read: an issue a source gave. The engine notes on
  // every map read it answers what it read (a scheduled-refresh snapshot, and
  // how much of it is shown), under its own catalog provider; that note is no
  // failure, so it says nothing here.
  const unread =
    !!focused &&
    focused.issues.some((issue) => issue.provider !== catalogProvider());
  const drawn = useMemo(
    () =>
      wanted
        ? ambientWindow(
            focused ? withDashboardStars(focused, map) : emptyPage,
            pins,
          )
        : overview,
    [wanted, focused, map, overview, pins],
  );
  const clusterCount = useMemo(
    () => clusterGraph(drawn.nodes, drawn.edges).labels.size,
    [drawn],
  );
  const title = (id: string) => {
    const star =
      focused?.nodes.find((n) => n.id === id) ??
      map.nodes.find((n) => n.id === id);
    return star ? displayTitle(star.title) : undefined;
  };
  const named = wanted ? focusSummary(wanted, title) : "";
  // What the unfocused sky draws: its stars and the groups they form.
  const starCount = blocked ? 0 : drawn.nodes.length,
    groupCount = blocked ? 0 : clusterCount;
  const matched = focused?.matched ?? 0,
    shown = focused?.nodes.length ?? 0;
  // Home's sky read live with nothing to draw: its first read failed (it is
  // no longer loading and has no read time), or the map it read has no stars.
  const skyEmpty = !wanted && !inBrowser && !drawn.nodes.length;
  const skyFailed = failed || (skyEmpty && !sky.loading && !sky.readAt);
  // What an empty sky says, from what this part knows: the demo's preview of
  // an empty, loading or failed source (its notice has the way back), a read
  // that failed, a read with no stars, or a focus that matches none.
  const empty = blocked
    ? {
        title: "No stars in this preview",
        message: "Sample stars return when the demo is restored.",
      }
    : skyFailed
      ? {
          title: "Sky unavailable",
          message: "Rhythm couldn’t load these records.",
        }
      : skyEmpty
        ? {
            title: "No stars to show",
            message: "Rhythm found no records you can view.",
          }
        : wanted && focused && !matched
          ? { title: "Nothing on the map matches this focus", message: named }
          : undefined;
  const demo =
    drawn.nodes.length > 0 && drawn.nodes.every((n) => n.provenance.demo);
  // A focus that is only a query opens the explorer's search for it.
  const explore = () =>
    onExplore(
      wanted?.query !== undefined && Object.keys(wanted).length === 1
        ? { query: wanted.query }
        : focused
          ? { title: named, page: focused }
          : null,
    );
  return (
    <>
      <div className="graph-heading">
        <span className="eyebrow">
          KNOWLEDGE
          {demo ? " · DEMO" : ""}
        </span>
        {wanted ? (
          <span className="map-focus" title={named}>
            {named}
            {focused && matched > 0
              ? ` · ${
                  shown < matched
                    ? `${count(shown)} of ${count(matched)} stars`
                    : `${count(matched)} ${matched === 1 ? "star" : "stars"}`
                }`
              : ""}
          </span>
        ) : (
          <span>
            {starCount
              ? `${count(starCount)} ${starCount === 1 ? "star" : "stars"} · ${groupCount} ${groupCount === 1 ? "group" : "groups"}`
              : "0 stars shown"}
          </span>
        )}
      </div>
      {/* The sky is the way in. Keyboards get a named entry ahead of its
          stars, shown when focused; a focused part's caption has its own. */}
      {!wanted && (
        <button className="sky-entry" onClick={() => onExplore(null)}>
          Open Explorer <ArrowRight size={15} />
        </button>
      )}
      <Constellation
        emptyMessage={empty?.message}
        emptyTitle={empty?.title}
        loading={wanted ? waiting : !inBrowser && sky.loading}
        nodes={blocked ? [] : drawn.nodes}
        edges={drawn.edges}
        automaticLinks={sky.automaticLinks ?? true}
        workItems={sky.workItems ?? true}
        pinned={pins}
        selected={selected}
        onSelect={onStar}
        interactive={false}
        healthFocus={healthFocus}
        paused={paused || still}
        own={own}
        still={still}
      />
      <div className="graph-caption">
        {skyFailed && (
          <p role="status">Sky unavailable · health sources are independent</p>
        )}
        {unread && (
          <p role="status">A source couldn’t be read · the rest is shown</p>
        )}
        {/* No stars drawn, nothing to select. */}
        {!wanted && starCount > 0 && (
          <p className="sky-hint" aria-hidden="true">
            Select a star to explore
          </p>
        )}
        {wanted && (focused || onShowEverything) && (
          <span className="map-actions">
            {focused && matched > 0 && (
              <button onClick={explore}>
                Explore <ArrowRight size={12} aria-hidden="true" />
              </button>
            )}
            {onShowEverything && (
              <button onClick={onShowEverything}>Show everything</button>
            )}
          </span>
        )}
      </div>
    </>
  );
}

const emptyPage: GraphPage = {
  nodes: [],
  edges: [],
  nextCursor: null,
  completeness: "complete",
  issues: [],
};
