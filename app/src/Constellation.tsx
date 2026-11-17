import {
  useEffect,
  useId,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import { useReducedMotion } from "motion/react";
import { select } from "d3-selection";
import { drag } from "d3-drag";
import { zoom, zoomIdentity, zoomTransform } from "d3-zoom";
import { Brain, Minus, Plus, Scan, Tags } from "lucide-react";
import type { GraphWork } from "./knowledge";
import {
  doubleTapMs,
  flickMin,
  flickVelocity,
  glide,
  glideStop,
  holdMs,
  isDoubleTap,
  nearestStar,
  tapMs,
  tapSlop,
} from "./gestures";
import type { ScreenStar, Tap } from "./gestures";
import { isDashboardId } from "./dashboards";
import {
  containGraph,
  createGraph,
  edgeClearance,
  graphLabelPriorities,
  insideSky,
  graphEdgeGeometry,
  labelCandidates,
  labelFontSize,
  labelLines,
  labelText,
  nodeHitRadius,
  placeCaptions,
  placeLabels,
  restorePositions,
} from "./graph";
import {
  clusterColor,
  dashedLink,
  drawnLinks,
  isFolder,
  isProjectStar,
  lineStyle,
  primaryGroup,
  replacedBy,
  sourceName,
  structural,
  withoutWork,
  workFinished,
  workLabel,
  workOf,
} from "./graphView";
import type { GraphPoint, LabelBox, SkySeed } from "./graph";
import type { KnowledgeEdge, KnowledgeNode } from "./model";
// Where the ambient sky last drew each star. The explorer opens on this same
// sky: touching a star brings it forward with the camera moving to it, so a
// star never jumps to another part of the sky between the two views.
const ambientSky: SkySeed = { height: 820, positions: new Map() };
// How long a map part's frame keeps one size before its sky is laid out again
// for it, so a resize settles it once.
const settleMs = 400;
// What a pointer rests on when it rests on a star: its target, its glyph or
// its name.
const starParts = ".graph-node, .graph-label, .graph-hit";
export function Constellation({
  nodes: givenNodes,
  edges: givenEdges,
  selected,
  onSelect,
  interactive,
  healthFocus,
  paused = false,
  highlightedIds,
  path,
  focusIds,
  onExplain,
  onBackground,
  cameraKey,
  emptyMessage,
  emptyTitle,
  loading = false,
  minHeight,
  onPanStart,
  insetBottom = 0,
  pinned: pinnedIds = [],
  own = false,
  still = false,
  automaticLinks = true,
  workItems = true,
  calm = true,
}: {
  nodes: KnowledgeNode[];
  edges: KnowledgeEdge[];
  selected: string | null;
  onSelect: (id: string) => void;
  interactive: boolean;
  healthFocus: boolean;
  paused?: boolean;
  highlightedIds?: string[];
  path?: string[];
  // Records the camera frames when nothing is traced: a selection and its links.
  focusIds?: string[];
  onExplain?: (edge: KnowledgeEdge) => void;
  // A tap on empty sky, not on a star, link or label, and not the end of a pan.
  onBackground?: () => void;
  cameraKey?: string;
  emptyMessage?: string;
  // The heading over emptyMessage, when the sky is empty for another reason
  // than an unavailable map (a map part's focus that matches nothing).
  emptyTitle?: string;
  // The first graph response has not arrived; never present that as unavailable.
  loading?: boolean;
  // The fewest graph units of height, so a wide, short sky spreads sideways
  // instead of being letterboxed. Defaults suit the original layouts.
  minHeight?: number;
  // A finger has started moving the sky (not a tap, pinch or held star).
  onPanStart?: () => void;
  // Stars pinned to the sky: always named, with a soft ring.
  pinned?: string[];
  // Screen pixels at the bottom of the sky covered by a resting panel: the
  // camera frames records above it.
  insetBottom?: number;
  // A map part's sky (parts/MapPart.tsx), drawn beside Home's: it never seeds
  // the explorer's star positions, and a resize lays it out again once, when
  // the resize ends.
  own?: boolean;
  // Drawn settled and still, as a map part in a folded dashboard is: nothing
  // pans, zooms or moves a star, and a vertical swipe scrolls the page. A tap
  // still opens a star.
  still?: boolean;
  // Dashed links drawn ("Automatic links on the sky", Sources & settings).
  // Off, only the selected star's own are drawn. Display only: the layout,
  // labels and every list still read all of `edges`, so it never moves a star.
  automaticLinks?: boolean;
  // Work items' stars drawn ("Work items on the sky", Sources & settings). Off,
  // none is, nor a link to one, but the selected star and the pinned ones.
  // Display only: nothing is deleted, and every list still reads them.
  workItems?: boolean;
  // A calm sky (the owner, 2026-11-09): folder and project lines only for the
  // selected star, quiet lines at rest and a bright focus, a hovered star's
  // included. The explorer's Trace and Discover keep the look they had.
  // Display only, as `automaticLinks` is.
  calm?: boolean;
}) {
  const frame = useRef<HTMLDivElement>(null),
    svg = useRef<SVGSVGElement>(null),
    layer = useRef<SVGGElement>(null);
  const controller =
    useRef<ReturnType<typeof zoom<SVGSVGElement, unknown>>>(null);
  const [scale, setScale] = useState(1),
    [height, setHeight] = useState(820),
    // The height the ambient sky is laid out for. A neighbouring tile growing
    // a line (a long title in Changed lately) changes the room it is given by
    // a few percent; laying the sky out again for that moved every star.
    // Only a real resize lays it out again; smaller changes keep the layout
    // and only nudge stars that would fall outside the frame. Unset until the
    // frame is first measured, which always lays the sky out for its height.
    [layoutHeight, setLayoutHeight] = useState<number | null>(null);
  const laidOut = layoutHeight ?? height;
  const [allLabels, setAllLabels] = useState(false);
  const [width, setWidth] = useState(820);
  // Screen pixels per graph unit at the current zoom, for screen-sized targets.
  const [unit, setUnit] = useState(1);
  // Phones get shorter, slightly smaller labels so more of them fit.
  const compact = width < 520;
  const lastCamera = useRef("");
  const cameraFrame = useRef(0);
  // A star picked up by a press and hold: the sky's layout moves it.
  const held = useRef<{
    grab: (id: string) => boolean;
    move: (id: string, x: number, y: number) => void;
    release: (id: string) => void;
  } | null>(null);
  const fitView = useRef(() => {});
  const redraw = useRef(() => {});
  const positionContext = useRef("");
  const positions = useRef(
    new Map<string, { x: number; y: number; height: number }>(),
  );
  // Keyed by its contents, so a new array of the same pins draws nothing new.
  const pinKey = pinnedIds.join("\n");
  const pinnedSet = useMemo(
    () => new Set(pinKey ? pinKey.split("\n") : []),
    [pinKey],
  );
  // The stars this sky draws (graphView.ts withoutWork): every one given, or
  // with work items off all but those, the selected star and the pinned ones
  // kept. Everything below reads these, so a hidden star takes no room either.
  const keptWork =
    !workItems &&
    selected !== null &&
    givenNodes.some((n) => n.id === selected && workOf(n))
      ? selected
      : null;
  const { nodes, edges } = useMemo(
    () =>
      withoutWork(
        givenNodes,
        givenEdges,
        workItems,
        new Set([...pinnedSet, ...(keptWork ? [keptWork] : [])]),
      ),
    [givenNodes, givenEdges, workItems, pinnedSet, keptWork],
  );
  const labelPriorities = useMemo(
    () => graphLabelPriorities(nodes, edges),
    [nodes, edges],
  );
  const reduced = useReducedMotion();
  const explainable = interactive && Boolean(onExplain);
  const id = useId().replace(/:/g, "");
  const graph = useMemo(
    () =>
      createGraph(nodes, edges, laidOut, interactive ? ambientSky : undefined),
    [nodes, edges, laidOut, interactive],
  );
  // The links drawn (graphView.ts drawnLinks): no dashed one while automatic
  // links are off and, on a calm sky, no folder or project line, each but the
  // selected star's own.
  const drawn = useMemo(
    () => drawnLinks(edges, automaticLinks, selected, calm),
    [edges, automaticLinks, selected, calm],
  );
  // The star a mouse or pen rests on: a calm sky lights its lines as it does
  // a selected star's. Touch has no hover; a tap selects.
  const [hovered, setHovered] = useState<string | null>(null);
  // At rest, the ambient sky names folders and memories linked to two or
  // more records; everything else is named on hover, selection or zoom.
  const memoryHubs = useMemo(() => {
    const memories = new Set(
      nodes.filter((n) => n.type === "memory").map((n) => n.id),
    );
    const linked = new Map<string, Set<string>>();
    for (const e of edges)
      for (const [a, b] of [
        [e.source, e.target],
        [e.target, e.source],
      ])
        if (memories.has(a) && a !== b)
          linked.set(a, (linked.get(a) ?? new Set()).add(b));
    return new Set(
      [...linked].filter(([, others]) => others.size >= 2).map(([id]) => id),
    );
  }, [nodes, edges]);
  // Folder and memory groups have no star of their own, so they carry a
  // faint caption.
  // Groups named by a theme star (the fictional sample's topics) do not.
  const captions = useMemo(() => {
    const facets = new Map<string, string>();
    for (const n of nodes) {
      const facet = primaryGroup(n);
      if (facet) facets.set(facet.id, facet.label);
    }
    // Groups named by a star of their own (a topic, or a Second Brain
    // project) need no caption as well.
    const themed = new Set(
      nodes
        .filter((n) => n.type === "theme" || isProjectStar(n))
        .map((n) => graph.clusters.groups.get(n.id)),
    );
    const members = new Map<string, string[]>();
    for (const n of nodes) {
      const key = graph.clusters.groups.get(n.id);
      if (!key || !facets.has(key) || themed.has(key) || graph.loose.has(n.id))
        continue;
      members.set(key, [...(members.get(key) ?? []), n.id]);
    }
    return [...members]
      .filter(([, ids]) => ids.length >= 2)
      .map(([id, ids]) => ({ id, label: facets.get(id)!, members: ids }));
  }, [nodes, graph]);
  const neighbors = useMemo(
    () =>
      new Set(
        edges
          .filter((e) => e.source === selected || e.target === selected)
          .flatMap((e) => [e.source, e.target]),
      ),
    [edges, selected],
  );
  const byId = new Map(nodes.map((n) => [n.id, n]));
  // A selection, a trace or a search brightens its records and dims the rest.
  const focusing = Boolean(selected || highlightedIds);
  // A hovered star with no line drawn (a folder at rest) lights and dims nothing.
  const hover =
    calm &&
    hovered !== null &&
    drawn.some((e) => e.source === hovered || e.target === hovered)
      ? hovered
      : null;
  useEffect(() => {
    if (!frame.current) return;
    let settle = 0;
    const observer = new ResizeObserver(([entry]) => {
      if (entry.contentRect.width <= 0) return;
      setWidth(entry.contentRect.width);
      const next = Math.max(
        minHeight ?? (interactive ? 300 : 220),
        Math.min(
          1800,
          (820 * entry.contentRect.height) / entry.contentRect.width,
        ),
      );
      setHeight(next);
      // The explorer frames its records at every size, so it always follows.
      const lay = (laid: number | null) =>
        laid === null || interactive || Math.abs(next - laid) > laid * 0.15
          ? next
          : laid;
      if (!own) return setLayoutHeight(lay);
      // A part being resized keeps its stars where they are until it stops.
      setLayoutHeight((laid) => laid ?? next);
      window.clearTimeout(settle);
      settle = window.setTimeout(() => setLayoutHeight(lay), settleMs);
    });
    observer.observe(frame.current);
    return () => {
      window.clearTimeout(settle);
      observer.disconnect();
    };
  }, [interactive, minHeight, own]);
  useEffect(() => {
    if (!svg.current || !layer.current) return;
    const behavior = zoom<SVGSVGElement, unknown>()
      .extent([
        [0, 0],
        [820, height],
      ])
      .scaleExtent([0.55, 3.2])
      .translateExtent([
        [-410, -height / 2],
        [1230, height * 1.5],
      ])
      .clickDistance(5)
      .filter(
        (e) =>
          !still &&
          (!e.ctrlKey || e.type === "wheel") &&
          !e.button &&
          (interactive || e.type !== "wheel" || e.ctrlKey) &&
          // A finger pans or pinches wherever it lands; a mouse on a star
          // drags the star instead.
          (e.type === "wheel" ||
            e.type.startsWith("touch") ||
            !e.target.closest(".graph-node, .graph-label, .graph-hit")),
      )
      .on("start", (e) => {
        // Someone grabbing the sky takes the camera; an easing camera move
        // stops. A finger takes it once it moves, so a tap on a star the
        // camera is easing towards does not strand it half way.
        if (e.sourceEvent && !e.sourceEvent.type.startsWith("touch"))
          cancelAnimationFrame(cameraFrame.current);
      })
      .on("zoom", (e) => {
        if (e.sourceEvent?.type === "touchmove")
          cancelAnimationFrame(cameraFrame.current);
        select(layer.current).attr("transform", e.transform.toString());
        setScale(e.transform.k);
        const ctm = svg.current?.getScreenCTM?.();
        setUnit((ctm && ctm.a > 0 ? ctm.a : width / 820) * e.transform.k);
        redraw.current();
      });
    controller.current = behavior;
    const element = select(svg.current);
    element.call(behavior).on("dblclick.zoom", null);
    const fit = (ease = false) => {
      cancelAnimationFrame(cameraFrame.current);
      if (!graph.points.length) {
        element.call(behavior.transform, zoomIdentity);
        return;
      }
      const framed = path?.length ? path : focusIds;
      const focused = framed?.length
        ? graph.points.filter((n) => framed.includes(n.id))
        : graph.points;
      if (!focused.length) return;
      const xs = focused.map((n) => n.x),
        ys = focused.map((n) => n.y);
      const minX = Math.min(...xs) - 115,
        maxX = Math.max(...xs) + 115,
        minY = Math.min(...ys) - 40,
        maxY = Math.max(...ys) + 45;
      // A record with no links is shown among its neighbours in the sky,
      // not alone in an empty close-up.
      const most = framed?.length && focused.length < 2 ? 1.1 : 1.65;
      // The part of the sky not under a resting panel, in graph units.
      const open = height - (insetBottom * 820) / Math.max(1, width);
      const k = Math.max(
        0.55,
        Math.min(most, 780 / (maxX - minX), (open - 75) / (maxY - minY)),
      );
      // The ambient sky is already laid out to fill its frame. Fitting it
      // again with room for names only shrank it, whenever the screen
      // changed size after the stars arrived.
      const target =
        !interactive && !framed?.length
          ? zoomIdentity
          : zoomIdentity
              .translate(
                410 - (k * (minX + maxX)) / 2,
                (open - 65) / 2 - (k * (minY + maxY)) / 2,
              )
              .scale(k);
      if (!ease || reduced || !svg.current) {
        element.call(behavior.transform, target);
        return;
      }
      // The camera glides to a new selection instead of cutting to it.
      const from = zoomTransform(svg.current);
      const start = performance.now();
      const step = (now: number) => {
        const t = Math.min(1, (now - start) / 650),
          e = 1 - (1 - t) ** 3;
        element.call(
          behavior.transform,
          zoomIdentity
            .translate(
              from.x + (target.x - from.x) * e,
              from.y + (target.y - from.y) * e,
            )
            .scale(from.k + (target.k - from.k) * e),
        );
        if (t < 1) cameraFrame.current = requestAnimationFrame(step);
      };
      cameraFrame.current = requestAnimationFrame(step);
    };
    fitView.current = () => fit(true);
    const size = `${Math.round(height)}:${Math.round(width)}`;
    const camera = `${cameraKey ?? "ambient"}:${size}`;
    // New records do not take the camera away from a user who has panned.
    if (lastCamera.current !== camera) {
      // Only a new subject eases; a new screen size simply refits.
      fit(lastCamera.current.endsWith(`:${size}`));
      lastCamera.current = camera;
    }
    return () => {
      element.on(".zoom", null);
    };
  }, [
    height,
    width,
    interactive,
    nodes,
    graph,
    cameraKey,
    path,
    focusIds,
    reduced,
    insetBottom,
    still,
  ]);
  // A glide outlives effect re-runs (so a re-render never strands the camera
  // half way) but not the sky itself.
  useEffect(() => () => cancelAnimationFrame(cameraFrame.current), []);
  // Touch, on top of d3-zoom's one-finger pan and two-finger pinch: a press
  // and hold picks up a star, a tap means the nearest star, a double tap on
  // empty sky zooms in and a two-finger tap zooms out, and a flick keeps the
  // sky gliding. Callbacks are read through a ref: the sky re-renders while
  // it moves, and a gesture must survive that.
  const touchProps = useRef({
    onSelect,
    onBackground,
    onPanStart,
    reduced,
    still,
  });
  useLayoutEffect(() => {
    touchProps.current = { onSelect, onBackground, onPanStart, reduced, still };
  }, [onSelect, onBackground, onPanStart, reduced, still]);
  useEffect(() => {
    const el = svg.current;
    if (!el) return;
    let hold = 0,
      pending = 0,
      grabbed: string | null = null,
      grabId = -1,
      glideFrame = 0,
      start:
        | (Tap & {
            panned: boolean;
            stopped: boolean;
            star: ScreenStar | null;
          })
        | null = null,
      moved = false,
      lastTap: Tap | null = null,
      samples: Tap[] = [];
    let pair: {
      t: number;
      x: number;
      y: number;
      at: Map<number, { x: number; y: number }>;
      moved: boolean;
    } | null = null;
    const stars = (): ScreenStar[] =>
      [...el.querySelectorAll<SVGGElement>("g.graph-node")].map((g) => {
        const b = g.getBoundingClientRect();
        return {
          id: g.dataset.id!,
          x: b.x + b.width / 2,
          y: b.y + b.height / 2,
        };
      });
    // What is exactly under the finger. A touch's own target is not: the
    // browser moves it onto anything nearby that takes taps, such as a name.
    const under = (x: number, y: number, selector: string) =>
      el.ownerDocument.elementFromPoint(x, y)?.closest(selector) ?? null;
    // A name belongs to its star, wherever the name sits; elsewhere a touch
    // means the nearest star.
    const starAt = (x: number, y: number) => {
      const named = under(x, y, ".graph-label")?.getAttribute("data-id");
      const all = stars();
      return (
        (named && all.find((s) => s.id === named)) || nearestStar(all, x, y)
      );
    };
    const userPoint = (x: number, y: number) => {
      const ctm = el.getScreenCTM();
      return ctm ? new DOMPoint(x, y).matrixTransform(ctm.inverse()) : null;
    };
    const mark = (id: string, on: boolean) => {
      for (const g of el.querySelectorAll(`[data-id="${CSS.escape(id)}"]`))
        g.classList.toggle("held", on);
    };
    const release = () => {
      if (!grabbed) return;
      mark(grabbed, false);
      held.current?.release(grabbed);
      grabbed = null;
      grabId = -1;
    };
    // A tap on empty sky steps back only if nothing else follows it.
    const cancelPending = () => {
      window.clearTimeout(pending);
      pending = 0;
      lastTap = null;
    };
    const zoomAt = (factor: number, x: number, y: number) => {
      const behavior = controller.current,
        p = userPoint(x, y);
      if (!behavior || !p) return;
      cancelAnimationFrame(cameraFrame.current);
      const sky = select(el),
        from = zoomTransform(el).k;
      if (touchProps.current.reduced) {
        sky.call(behavior.scaleTo, from * factor, [p.x, p.y]);
        return;
      }
      const began = performance.now();
      const step = (now: number) => {
        const t = Math.min(1, (now - began) / 260),
          eased = 1 - (1 - t) ** 3;
        sky.call(behavior.scaleTo, from * factor ** eased, [p.x, p.y]);
        if (t < 1) cameraFrame.current = requestAnimationFrame(step);
      };
      cameraFrame.current = requestAnimationFrame(step);
    };
    const glideFrom = (speed: { x: number; y: number }) => {
      const behavior = controller.current;
      if (
        !behavior ||
        touchProps.current.reduced ||
        Math.hypot(speed.x, speed.y) < flickMin
      )
        return;
      let vx = speed.x,
        vy = speed.y,
        last = performance.now();
      const began = last,
        sky = select(el);
      const step = (now: number) => {
        const dt = Math.min(40, now - last),
          ctm = el.getScreenCTM(),
          k = zoomTransform(el).k;
        last = now;
        if (!ctm) return;
        sky.call(
          behavior.translateBy,
          (vx * dt) / ctm.a / k,
          (vy * dt) / ctm.a / k,
        );
        vx = glide(vx, dt);
        vy = glide(vy, dt);
        glideFrame =
          Math.hypot(vx, vy) > glideStop && now - began < 1200
            ? (cameraFrame.current = requestAnimationFrame(step))
            : 0;
      };
      glideFrame = cameraFrame.current = requestAnimationFrame(step);
    };
    const onStart = (e: TouchEvent) => {
      window.clearTimeout(hold);
      if (grabbed) {
        // While a star is held, other fingers are ignored: d3-zoom never
        // sees them, so the sky cannot jump when they move.
        if (e.touches.length > 1) {
          e.stopImmediatePropagation();
          return;
        }
        // The holding finger's lift was lost (its star went away): let go.
        release();
      }
      // A touch on a gliding sky stops it, and that touch is not a tap. A
      // camera easing to a chosen star keeps going until the finger moves.
      const gliding = glideFrame !== 0 && glideFrame === cameraFrame.current;
      if (gliding) {
        cancelAnimationFrame(glideFrame);
        glideFrame = 0;
      }
      if (e.touches.length === 1) {
        const touch = e.touches[0];
        // The star under the finger as it lands is the one a tap means: a
        // camera still easing to an earlier choice slides the stars before
        // the finger lifts, and a neighbour can be nearest by then.
        const near = gliding ? null : starAt(touch.clientX, touch.clientY);
        start = {
          x: touch.clientX,
          y: touch.clientY,
          t: e.timeStamp,
          panned: false,
          stopped: gliding,
          star: near,
        };
        moved = false;
        samples = [{ x: start.x, y: start.y, t: start.t }];
        pair = null;
        if (near && !touchProps.current.still)
          hold = window.setTimeout(() => {
            if (moved || !start || !held.current?.grab(near.id)) return;
            cancelPending();
            grabbed = near.id;
            grabId = touch.identifier;
            mark(near.id, true);
            navigator.vibrate?.(12);
          }, holdMs);
        return;
      }
      // A second finger: a pinch, or a two-finger tap.
      cancelPending();
      start = null;
      if (e.touches.length === 2) {
        const [a, b] = [e.touches[0], e.touches[1]];
        pair = {
          t: e.timeStamp,
          x: (a.clientX + b.clientX) / 2,
          y: (a.clientY + b.clientY) / 2,
          at: new Map(
            [a, b].map((t) => [t.identifier, { x: t.clientX, y: t.clientY }]),
          ),
          moved: false,
        };
      } else pair = null;
    };
    const onMove = (e: TouchEvent) => {
      if (grabbed) {
        // The held star follows its finger; the sky stays put.
        e.preventDefault();
        e.stopImmediatePropagation();
        const touch = [...e.touches].find((t) => t.identifier === grabId),
          p = touch && userPoint(touch.clientX, touch.clientY);
        if (!p) return;
        const [x, y] = zoomTransform(el).invert([p.x, p.y]);
        held.current?.move(grabbed, x, y);
        return;
      }
      if (pair)
        for (const touch of e.touches) {
          const from = pair.at.get(touch.identifier);
          if (
            !from ||
            Math.hypot(touch.clientX - from.x, touch.clientY - from.y) > tapSlop
          )
            pair.moved = true;
        }
      if (start && e.touches.length === 1) {
        const touch = e.touches[0];
        if (
          !moved &&
          Math.hypot(touch.clientX - start.x, touch.clientY - start.y) > tapSlop
        ) {
          moved = true;
          window.clearTimeout(hold);
          cancelPending();
          if (!start.panned) {
            start.panned = true;
            touchProps.current.onPanStart?.();
          }
        }
        samples.push({ x: touch.clientX, y: touch.clientY, t: e.timeStamp });
        if (samples.length > 12) samples.shift();
      }
    };
    const onEnd = (e: TouchEvent) => {
      window.clearTimeout(hold);
      if (grabbed) {
        e.preventDefault();
        if ([...e.changedTouches].some((t) => t.identifier === grabId))
          release();
        start = null;
        return;
      }
      if (pair) {
        if (!e.touches.length) {
          if (
            !pair.moved &&
            e.timeStamp - pair.t <= tapMs &&
            !touchProps.current.still
          ) {
            e.preventDefault();
            zoomAt(0.5, pair.x, pair.y);
          }
          pair = null;
        }
        start = null;
        return;
      }
      if (!start || e.touches.length) return;
      const began = start;
      start = null;
      if (moved) {
        if (!touchProps.current.still)
          glideFrom(flickVelocity(samples, e.timeStamp));
        return;
      }
      const tap = { x: began.x, y: began.y, t: e.timeStamp };
      const link = under(tap.x, tap.y, ".graph-edge-hit");
      // Stopping a glide, or a long press that picked nothing up, is not a
      // tap: nothing happens (a press on a link still explains it).
      if (began.stopped || e.timeStamp - began.t >= holdMs) {
        cancelPending();
        if (!link) e.preventDefault();
        return;
      }
      const star = began.star;
      // A link's own strip wins unless the finger was right on a star.
      if (star && !(link && Math.hypot(star.x - tap.x, star.y - tap.y) > 14)) {
        e.preventDefault();
        cancelPending();
        touchProps.current.onSelect(star.id);
        return;
      }
      if (link) {
        cancelPending();
        return;
      }
      // Empty sky: wait a moment for a second tap before stepping back. A
      // still sky never zooms, so it steps back at once.
      e.preventDefault();
      window.clearTimeout(pending);
      if (touchProps.current.still) {
        lastTap = null;
        touchProps.current.onBackground?.();
        return;
      }
      if (isDoubleTap(lastTap, tap)) {
        lastTap = null;
        zoomAt(2, tap.x, tap.y);
        return;
      }
      lastTap = tap;
      pending = window.setTimeout(() => {
        pending = 0;
        lastTap = null;
        touchProps.current.onBackground?.();
      }, doubleTapMs);
    };
    const onCancel = () => {
      window.clearTimeout(hold);
      cancelPending();
      release();
      start = null;
      pair = null;
    };
    el.addEventListener("touchstart", onStart, {
      capture: true,
      passive: true,
    });
    el.addEventListener("touchmove", onMove, { capture: true, passive: false });
    el.addEventListener("touchend", onEnd, { capture: true, passive: false });
    el.addEventListener("touchcancel", onCancel, { capture: true });
    return () => {
      window.clearTimeout(hold);
      window.clearTimeout(pending);
      release();
      el.removeEventListener("touchstart", onStart, { capture: true });
      el.removeEventListener("touchmove", onMove, { capture: true });
      el.removeEventListener("touchend", onEnd, { capture: true });
      el.removeEventListener("touchcancel", onCancel, { capture: true });
    };
  }, []);
  useEffect(() => {
    if (!layer.current) return;
    const previous = positions.current;
    positions.current = new Map();
    // Positions belong to a layout, so they carry over while its height holds.
    const context = `${cameraKey ?? "ambient"}:${Math.round(laidOut)}`;
    if (positionContext.current === context)
      restorePositions(graph.points, previous, laidOut);
    positionContext.current = context;
    // The ambient sky's drawn positions are what the explorer opens on.
    if (!interactive && !own) {
      ambientSky.height = laidOut;
      ambientSky.positions = positions.current;
    }
    const root = select(layer.current);
    const nodeElements = root
      .selectAll<SVGGElement, GraphPoint>(
        ".graph-node, .graph-label, .graph-hit",
      )
      .datum(function () {
        return graph.byId.get(this.dataset.id!)!;
      });
    const edgeElements = root.selectAll<SVGPathElement, unknown>(
      ".graph-edge, .graph-edge-hit",
    );
    const rank = { primary: 2, secondary: 1 } as const;
    const types = new Map(nodes.map((n) => [n.id, n.type]));
    const folders = new Set(
      nodes.filter((n) => isFolder(n) || isProjectStar(n)).map((n) => n.id),
    );
    const dashboards = new Set(
      nodes.filter((n) => isDashboardId(n.id)).map((n) => n.id),
    );
    const labelElements = [
      ...(layer.current?.querySelectorAll<SVGGElement>(".graph-label") ?? []),
    ].map((element) => {
      const point = graph.byId.get(element.dataset.id!)!;
      const priority = labelPriorities.get(point.id);
      return {
        element,
        text: element.querySelector("text")!,
        point,
        // Selected, dashboards, themes, named hubs, memories, linked records,
        // then loose.
        weight:
          Number(point.id === selected) * 1000 +
          Number(dashboards.has(point.id)) * 500 +
          Number(pinnedSet.has(point.id)) * 400 +
          Number(point.theme) * 100 +
          Number(folders.has(point.id)) * 50 +
          (priority ? rank[priority] : 0) * 10 +
          Number(types.get(point.id) === "memory") * 2 +
          Number(!graph.loose.has(point.id)),
      };
    });
    const captionElements = [
      ...(layer.current?.querySelectorAll<SVGTextElement>(".graph-caption") ??
        []),
    ].map((text) => ({
      text,
      members: (
        captions.find((c) => c.id === text.dataset.group)?.members ?? []
      )
        .map((id) => graph.byId.get(id))
        .filter((p): p is GraphPoint => !!p),
    }));
    const preferred = new Map<string, number>();
    // Labels shown in the last pass keep their places, so drifting stars do
    // not make neighbouring labels trade places and blink.
    let shown = new Set<string>();
    let lastLabels = -Infinity;
    const draw = () => {
      containGraph(graph.points, height);
      for (const point of graph.points)
        positions.current.set(point.id, {
          x: point.x,
          y: point.y,
          height: laidOut,
        });
      nodeElements.attr("transform", (n) => `translate(${n.x},${n.y})`);
      edgeElements.attr("d", function () {
        const a = graph.byId.get(this.dataset.source!),
          b = graph.byId.get(this.dataset.target!);
        return a && b ? graphEdgeGeometry(a, b).path : "";
      });
      if (performance.now() - lastLabels > 120) {
        lastLabels = performance.now();
        const camera = svg.current ? zoomTransform(svg.current) : zoomIdentity;
        const left = (8 - camera.x) / camera.k,
          right = (812 - camera.x) / camera.k;
        const top = (8 - camera.y) / camera.k,
          bottom =
            (height - Math.max(60, (64 * 820) / width) - camera.y) / camera.k;
        const bounds = { left, right, top, bottom };
        const occupied: LabelBox[] = [];
        const ctm = layer.current?.getScreenCTM?.();
        const pixelsPerUnit = Math.max(
          ctm && ctm.a > 0 ? ctm.a : (width / 820) * camera.k,
          0.05,
        );
        // Labels remain clickable, but must not swallow the middle of a link.
        // Reserve a screen-sized square at each visible midpoint, not the whole
        // edge bounding box; off-screen links cannot collide with visible labels.
        if (explainable) {
          const clearance = edgeClearance(pixelsPerUnit);
          for (const edge of drawn) {
            const a = graph.byId.get(edge.source);
            const b = graph.byId.get(edge.target);
            if (!a || !b) continue;
            const { midpoint } = graphEdgeGeometry(a, b);
            if (
              midpoint.x < left - clearance ||
              midpoint.x > right + clearance ||
              midpoint.y < top - clearance ||
              midpoint.y > bottom + clearance
            )
              continue;
            occupied.push({
              x: midpoint.x - clearance,
              y: midpoint.y - clearance,
              w: clearance * 2,
              h: clearance * 2,
              reserved: true,
            });
          }
        }
        // Labels keep off other records' stars, except on phones, where
        // that would leave almost no room for names.
        const star = 7 / pixelsPerUnit;
        for (const p of compact ? [] : graph.points)
          if (
            p.x > left - star &&
            p.x < right + star &&
            p.y > top - star &&
            p.y < bottom + star
          )
            occupied.push({
              x: p.x - star,
              y: p.y - star,
              w: star * 2,
              h: star * 2,
              reserved: true,
              owner: p.id,
            });
        // Folder names go first; labels then keep clear of them.
        const captionFont = 11 / pixelsPerUnit;
        const captionPlacements = placeCaptions(
          captionElements.map(({ text, members }) => ({
            id: text.dataset.group!,
            members,
            font: captionFont,
            width:
              typeof text.getComputedTextLength === "function"
                ? text.getComputedTextLength()
                : (text.textContent?.length ?? 0) * captionFont * 0.7,
          })),
          bounds,
          8 + captionFont * 0.9,
          // Only groups whose records sit together get a name: within about
          // a quarter of the sky's width, whatever the screen size.
          220,
        );
        for (const { text } of captionElements) {
          const placed = captionPlacements.get(text.dataset.group!);
          text.style.visibility = placed ? "visible" : "hidden";
          if (!placed) continue;
          text.setAttribute("x", String(placed.x));
          text.setAttribute("y", String(placed.y));
          occupied.push(placed.box);
        }
        // Measure every label before positioning any of them, so the browser
        // lays text out once per pass instead of once per label. Collisions
        // hide labels with visibility, never opacity, so opacity says only
        // whether the view wants this label at all.
        const measured = labelElements.map((label) => {
          const { text } = label;
          const style = getComputedStyle(text);
          if (Number(style.opacity) === 0)
            return { ...label, size: null, lines: null };
          const font = parseFloat(style.fontSize) || 13;
          const lines = [...text.querySelectorAll("tspan")];
          const size = {
            font,
            height: lines.length * font * 1.2,
            width: Math.max(
              ...lines.map((l) =>
                typeof l.getComputedTextLength === "function"
                  ? l.getComputedTextLength()
                  : (l.textContent?.length ?? 0) * font * 0.54,
              ),
            ),
          };
          return { ...label, size, lines };
        });
        const lit = new Set(
          path ?? highlightedIds ?? (selected ? [selected, ...neighbors] : []),
        );
        const placements = placeLabels(
          measured.flatMap(({ point, size, weight }) =>
            size
              ? [
                  {
                    id: point.id,
                    point,
                    size,
                    weight,
                    // "Show all labels" names every star, overlapping where
                    // there is no free spot. In the explorer, a focus names
                    // the selection and everything it lights up (its links,
                    // a search's matches, a traced path), even on a phone
                    // where they would not all fit beside each other.
                    // A dashboard is always named, so it reads as one.
                    forced:
                      allLabels ||
                      point.id === selected ||
                      dashboards.has(point.id) ||
                      pinnedSet.has(point.id) ||
                      (interactive && lit.has(point.id)),
                  },
                ]
              : [],
          ),
          occupied,
          bounds,
          pixelsPerUnit,
          shown,
          preferred,
        );
        for (const { text, point, lines } of measured) {
          const placed = placements.get(point.id);
          // Labels the view does not want are already transparent.
          text.setAttribute("data-collision", String(!placed && !!lines));
          text.style.pointerEvents = placed ? "bounding-box" : "none";
          if (!placed) continue;
          const c = placed.candidate;
          text.setAttribute("x", String(c.x));
          text.setAttribute("y", String(c.y));
          text.setAttribute("text-anchor", c.a);
          for (const line of lines!) line.setAttribute("x", String(c.x));
        }
        shown = new Set(placements.keys());
      }
    };
    redraw.current = () => {
      lastLabels = -Infinity;
      draw();
    };
    const chosen = graph.byId.get(selected ?? "");
    const targets = graph.points.map((n) => {
      const related = chosen && n.id !== selected && neighbors.has(n.id);
      const dx = related ? n.anchorX - chosen.anchorX : 0,
        dy = related ? n.anchorY - chosen.anchorY : 0;
      const length = Math.max(1, Math.hypot(dx, dy));
      return {
        x: n.anchorX + (dx / length) * 24,
        y: n.anchorY + (dy / length) * 24,
      };
    });
    // The ambient sky breathes very slightly: about 2 graph units, inside the
    // 3px a showing name tolerates, so names never trade places. The explorer
    // holds still while someone reads it.
    const drift = interactive ? 0 : 0.2;
    const setTargets = (elapsed: number) => {
      graph.x.x(
        (n, i) =>
          targets[i].x +
          (n.fx == null ? Math.sin(elapsed / 5200 + i * 1.7) * 10 * drift : 0),
      );
      graph.y.y(
        (n, i) =>
          targets[i].y +
          (n.fy == null ? Math.cos(elapsed / 6800 + i * 1.3) * 12 * drift : 0),
      );
    };
    if (reduced) {
      setTargets(0);
      graph.simulation.alpha(1).tick(100);
    }
    draw();
    const behavior = drag<SVGGElement, GraphPoint>()
      .container(() => layer.current!)
      .clickDistance(5)
      // Touch moves a star only after a press and hold (see the touch
      // gestures below); a touch drag pans the sky.
      .filter((e) => !still && !e.ctrlKey && !e.button && !("touches" in e))
      .on("start", (e, n) => {
        e.sourceEvent.stopPropagation();
        n.fx = n.x;
        n.fy = n.y;
      })
      .on("drag", (e, n) => {
        const at = insideSky(e.x, e.y, height);
        n.fx = n.x = at.x;
        n.fy = n.y = at.y;
        draw();
      })
      .on("end", (_e, n) => {
        n.fx = null;
        n.fy = null;
        graph.simulation.alpha(0.35);
        // Label placement is throttled during the drag; place labels for the
        // final position too, since reduced motion has no frame loop to do it.
        redraw.current();
      });
    nodeElements.call(behavior);
    held.current = {
      grab(id) {
        const n = graph.byId.get(id);
        if (!n) return false;
        n.fx = n.x;
        n.fy = n.y;
        return true;
      },
      move(id, x, y) {
        const n = graph.byId.get(id);
        if (!n) return;
        const at = insideSky(x, y, height);
        n.fx = n.x = at.x;
        n.fy = n.y = at.y;
        draw();
      },
      release(id) {
        const n = graph.byId.get(id);
        if (n) {
          n.fx = null;
          n.fy = null;
        }
        graph.simulation.alpha(0.35);
        redraw.current();
      },
    };
    let raf = 0,
      last = 0,
      elapsed = 0,
      inView = true;
    const tick = (time: number) => {
      if (time - last >= 32) {
        elapsed += Math.min(40, time - last);
        last = time;
        setTargets(elapsed);
        graph.simulation.alpha(Math.max(graph.simulation.alpha(), 0.14)).tick();
        draw();
      }
      raf = requestAnimationFrame(tick);
    };
    const update = () => {
      cancelAnimationFrame(raf);
      if (!paused && !reduced && !document.hidden && inView) {
        last = performance.now();
        raf = requestAnimationFrame(tick);
      }
    };
    const observer =
      typeof IntersectionObserver !== "undefined"
        ? new IntersectionObserver(([entry]) => {
            inView = entry.isIntersecting;
            update();
          })
        : null;
    if (frame.current) observer?.observe(frame.current);
    document.addEventListener("visibilitychange", update);
    update();
    return () => {
      cancelAnimationFrame(raf);
      observer?.disconnect();
      document.removeEventListener("visibilitychange", update);
      nodeElements.on(".drag", null);
      held.current = null;
      graph.simulation.stop();
      redraw.current = () => {};
    };
  }, [
    graph,
    height,
    laidOut,
    paused,
    reduced,
    selected,
    neighbors,
    path,
    highlightedIds,
    interactive,
    allLabels,
    width,
    cameraKey,
    drawn,
    explainable,
    labelPriorities,
    nodes,
    captions,
    compact,
    pinnedSet,
    own,
    still,
  ]);
  // Zoom buttons and keys take the camera from a glide in progress.
  const adjust = (factor: number) => {
    cancelAnimationFrame(cameraFrame.current);
    if (svg.current && controller.current)
      select(svg.current).call(controller.current.scaleBy, factor);
  };
  const reset = () => {
    fitView.current();
  };
  return (
    <div
      ref={frame}
      className={`constellation ${focusing ? "has-focus" : ""} ${interactive ? "is-interactive" : ""} ${allLabels || nodes.length < 10 || scale > 1.6 ? "show-labels" : ""} ${allLabels ? "all-labels" : ""} ${paused || reduced ? "is-still" : ""}${still ? " is-fixed" : ""}${calm ? " is-calm" : ""}`}
    >
      <div className="orbital orbital-one" />
      <div className="orbital orbital-two" />
      <svg
        ref={svg}
        tabIndex={0}
        onClick={(e) => {
          const target = e.target as Element;
          if (
            onBackground &&
            !target.closest(
              ".graph-node, .graph-label, .graph-hit, .graph-edge-hit",
            )
          )
            onBackground();
        }}
        // Hover only lights lines: a pointer crossing the sky never moves,
        // names or lays out anything.
        onPointerOver={(e) => {
          if (!calm || e.pointerType === "touch") return;
          const star = (e.target as Element).closest<SVGGElement>(starParts);
          setHovered(star?.dataset.id ?? null);
        }}
        onPointerOut={(e) => {
          if (!calm || e.pointerType === "touch") return;
          const next =
            e.relatedTarget instanceof Element
              ? e.relatedTarget.closest<SVGGElement>(starParts)
              : null;
          setHovered(next?.dataset.id ?? null);
        }}
        onKeyDown={(e) => {
          if (
            still ||
            e.target !== e.currentTarget ||
            !svg.current ||
            !controller.current
          )
            return;
          const move: Record<string, [number, number]> = {
            ArrowLeft: [45, 0],
            ArrowRight: [-45, 0],
            ArrowUp: [0, 45],
            ArrowDown: [0, -45],
          };
          if (move[e.key]) {
            e.preventDefault();
            cancelAnimationFrame(cameraFrame.current);
            select(svg.current).call(
              controller.current.translateBy,
              ...move[e.key],
            );
          }
          if (e.key === "+" || e.key === "=") {
            e.preventDefault();
            adjust(1.25);
          }
          if (e.key === "-") {
            e.preventDefault();
            adjust(0.8);
          }
          if (e.key === "0") {
            e.preventDefault();
            reset();
          }
        }}
        viewBox={`0 0 820 ${height}`}
        aria-label={
          interactive
            ? "Knowledge constellation. Drag or pinch to navigate. Arrow keys pan, plus and minus zoom, zero resets. Tab to explore nodes and links."
            : still
              ? "Knowledge constellation. Select an item to explore its connections."
              : "Knowledge constellation. Drag stars to move them, drag the sky to pan, or pinch to zoom. Arrow keys pan, plus and minus zoom, zero resets. Select an item to explore its connections."
        }
        className="graph-svg"
      >
        <defs>
          <filter
            id={`${id}-glow`}
            x="-150%"
            y="-150%"
            width="400%"
            height="400%"
          >
            <feGaussianBlur stdDeviation="5" />
          </filter>
          <radialGradient id={`${id}-halo`}>
            <stop stopColor="#84bad8" stopOpacity=".09" />
            <stop offset="1" stopColor="#84bad8" stopOpacity="0" />
          </radialGradient>
        </defs>
        <g ref={layer}>
          <ellipse
            cx="410"
            cy={height / 2}
            rx="370"
            ry={height * 0.43}
            fill={`url(#${id}-halo)`}
          />
          <g className="graph-dust" aria-hidden="true">
            {Array.from({ length: 22 }, (_, i) => (
              <circle
                key={i}
                cx={65 + ((i * 137) % 700)}
                cy={70 + ((i * 173) % (height - 140))}
                r={i % 3 === 0 ? 1.3 : 0.7}
                fill="#adc9da"
                opacity={0.12 + (i % 4) * 0.045}
                style={{ animationDelay: `-${i * 1.7}s` }}
              />
            ))}
          </g>
          {drawn.map((e) => {
            const a = byId.get(e.source),
              b = byId.get(e.target);
            if (!a || !b) return null;
            const inPath = path?.some(
              (p, i) =>
                i < path.length - 1 &&
                ((p === e.source && path[i + 1] === e.target) ||
                  (p === e.target && path[i + 1] === e.source)),
            );
            const active = path
              ? inPath
              : highlightedIds
                ? highlightedIds.includes(e.source) &&
                  highlightedIds.includes(e.target)
                : selected && (e.source === selected || e.target === selected);
            // A hovered star's lines light as a selected star's do.
            const lit =
              Boolean(active) ||
              (hover !== null && (e.source === hover || e.target === hover));
            const suggested = dashedLink(e);
            const member = "type" in e && structural(e.type);
            // Lit, dimmed while something else is in focus, or at rest (a
            // file's folder line fainter than the links between ideas).
            const look = lineStyle(e.strength, {
              calm,
              lit,
              dim: (focusing || hover !== null) && !lit,
              member,
            });
            return (
              <g key={"id" in e ? String(e.id) : `${e.source}-${e.target}`}>
                <path
                  className={`graph-edge ${lit ? "is-related" : ""} ${member ? "folder-edge" : ""}`}
                  data-source={e.source}
                  data-target={e.target}
                  stroke={
                    suggested
                      ? "#c3a4da"
                      : lit
                        ? "#b7dcdf"
                        : clusterColor(graph.clusters.groups.get(a.id))
                  }
                  strokeDasharray={suggested ? "5 8" : undefined}
                  strokeWidth={look.width}
                  opacity={look.opacity}
                  fill="none"
                />
                {interactive && onExplain && (
                  <path
                    className="graph-edge-hit"
                    data-source={e.source}
                    data-target={e.target}
                    stroke="transparent"
                    // A 24px screen-space strip at every width and zoom level.
                    strokeWidth={24}
                    vectorEffect="non-scaling-stroke"
                    fill="none"
                    role="button"
                    tabIndex={0}
                    aria-label={`Explain connection: ${a.title} to ${b.title}`}
                    onClick={() => onExplain(e)}
                    onKeyDown={(event) => {
                      if (event.key === "Enter" || event.key === " ") {
                        event.preventDefault();
                        onExplain(e);
                      }
                    }}
                  />
                )}
              </g>
            );
          })}
          <g className="graph-groups" aria-hidden="true">
            {captions.map((c) => (
              <text
                key={c.id}
                className="graph-caption"
                data-group={c.id}
                textAnchor="middle"
                style={{
                  fontSize: 11 / Math.max(unit, 0.05),
                  strokeWidth: 3 / Math.max(unit, 0.05),
                }}
              >
                {labelText(c.label, 26).toUpperCase()}
              </text>
            ))}
          </g>
          {["hit", "node", "label"].flatMap((part) =>
            nodes.map((n) => {
              const label = part === "label",
                hit = part === "hit";
              const folder = isFolder(n),
                project = isProjectStar(n),
                dashboard = isDashboardId(n.id),
                theme = n.type === "theme" && !folder,
                related = path
                  ? path.includes(n.id)
                  : highlightedIds
                    ? highlightedIds.includes(n.id)
                    : neighbors.has(n.id) || selected === n.id;
              const faded = focusing
                ? !related
                : healthFocus &&
                  !["demo", "garmin"].includes(n.provenance.sourceId);
              // A fact a newer memory replaced (SB-P4-11): drawn faded, never
              // hidden, and never as the current fact.
              const replaced = replacedBy(n) !== null;
              // A work item (migration 079): a small box in place of a
              // memory's glyph, quieter once the work is finished.
              const work: GraphWork | null = workOf(n);
              const finished = work !== null && workFinished(work);
              const font = labelFontSize(theme, unit, compact);
              return (
                <g
                  key={`${part}-${n.id}`}
                  data-id={n.id}
                  aria-hidden={label || hit || undefined}
                  role={label || hit ? undefined : "button"}
                  tabIndex={label || hit ? undefined : 0}
                  aria-label={`Explore ${n.title}${replaced ? " (replaced by a newer memory)" : ""}${work ? ` (work item: ${workLabel(work)})` : ""}`}
                  aria-pressed={selected === n.id}
                  onClick={(e) => {
                    if (!e.defaultPrevented) onSelect(n.id);
                  }}
                  onKeyDown={(e) => {
                    if (e.key === "Enter" || e.key === " ") {
                      e.preventDefault();
                      onSelect(n.id);
                    }
                  }}
                  className={`${label ? "graph-label" : hit ? "graph-hit" : "graph-node"} ${theme ? "theme-node" : ""} ${related ? "related-node" : ""} ${path?.includes(n.id) ? "path-node" : ""} ${labelPriorities.has(n.id) ? `label-${labelPriorities.get(n.id)}` : ""} ${graph.loose.has(n.id) ? "loose-node" : ""} ${folder ? "folder-node" : ""} ${project ? "project-star" : ""} ${dashboard ? "dashboard-star" : ""} ${pinnedSet.has(n.id) ? "pinned-star" : ""} ${memoryHubs.has(n.id) ? "memory-hub" : ""}${replaced ? " replaced-star" : ""}${work ? ` work-star work-${work.state}` : ""}`}
                  style={{
                    opacity: faded
                      ? 0.22
                      : replaced
                        ? 0.45
                        : finished
                          ? 0.6
                          : 1,
                    // Unlinked records are quieter than the clusters they orbit.
                    color: graph.loose.has(n.id)
                      ? "#6f8796"
                      : clusterColor(graph.clusters.groups.get(n.id)),
                  }}
                >
                  <title>
                    {n.title} · {sourceName(n.provenance.sourceId)} · {n.type}
                    {replaced && " · replaced by a newer memory"}
                    {work && ` · work item: ${workLabel(work)}`}
                  </title>
                  {hit && (
                    <circle
                      r={nodeHitRadius(theme, unit)}
                      fill="transparent"
                      className="node-hit"
                    />
                  )}
                  {!label && !hit && (
                    <>
                      <circle
                        r={nodeHitRadius(theme, unit)}
                        fill="transparent"
                        className="node-hit"
                        pointerEvents="none"
                      />
                      <circle
                        r={theme ? 18 : dashboard ? 12 : 8}
                        fill="currentColor"
                        opacity={dashboard ? ".36" : ".28"}
                        filter={`url(#${id}-glow)`}
                      />
                      {pinnedSet.has(n.id) && (
                        <circle
                          className="pin-ring"
                          r={theme ? 23 : 12}
                          fill="none"
                          stroke="currentColor"
                          strokeWidth=".8"
                        />
                      )}
                      {theme && (
                        <circle
                          r="19"
                          fill="none"
                          stroke="currentColor"
                          strokeWidth=".6"
                          opacity=".3"
                        />
                      )}
                      {dashboard ? (
                        // A dashboard: a small panel with three rising bars,
                        // a size up from a record so it reads at a glance.
                        <>
                          <rect
                            x="-8"
                            y="-8"
                            width="16"
                            height="16"
                            rx="4"
                            fill="currentColor"
                            fillOpacity=".16"
                            stroke="currentColor"
                            strokeWidth="1.1"
                          />
                          <path
                            className="dashboard-glyph"
                            d="M-4,4.2V1.2M0,4.2V-4.2M4,4.2V-1.4"
                            fill="none"
                            stroke="currentColor"
                            strokeWidth="1.9"
                            strokeLinecap="round"
                          />
                        </>
                      ) : project ? (
                        <path
                          className="project-glyph"
                          d="M0,-6.5L1.7,-1.7L6.5,0L1.7,1.7L0,6.5L-1.7,1.7L-6.5,0L-1.7,-1.7Z"
                          fill="currentColor"
                          fillOpacity=".3"
                          stroke="currentColor"
                          strokeWidth="1"
                          strokeLinejoin="round"
                        />
                      ) : folder ? (
                        <path
                          className="folder-glyph"
                          d="M-6,-4.5h4l1.4,1.6h6.6v7.4h-12z"
                          fill="currentColor"
                          fillOpacity=".18"
                          stroke="currentColor"
                          strokeWidth="1.1"
                          strokeLinejoin="round"
                        />
                      ) : n.type === "document" ? (
                        <rect
                          x="-4"
                          y="-4"
                          width="8"
                          height="8"
                          rx="1"
                          fill="none"
                          stroke="currentColor"
                        />
                      ) : work ? (
                        // A work item: a small box, a dot in it while the
                        // work is open, a tick once done and a bar when
                        // cancelled, so project work reads apart from a
                        // memory at a glance, by shape and not colour alone.
                        <>
                          <rect
                            className="work-glyph"
                            x="-5.5"
                            y="-5.5"
                            width="11"
                            height="11"
                            rx="2.5"
                            fill="currentColor"
                            fillOpacity={finished ? ".08" : ".22"}
                            stroke="currentColor"
                            strokeWidth="1.2"
                          />
                          {work.state === "done" ? (
                            <path
                              d="M-2.8,0.2L-0.8,2.3L3,-2.2"
                              fill="none"
                              stroke="currentColor"
                              strokeWidth="1.5"
                              strokeLinecap="round"
                              strokeLinejoin="round"
                            />
                          ) : work.state === "cancelled" ? (
                            <path
                              d="M-2.6,0H2.6"
                              fill="none"
                              stroke="currentColor"
                              strokeWidth="1.5"
                              strokeLinecap="round"
                            />
                          ) : (
                            <circle r="1.5" fill="currentColor" />
                          )}
                        </>
                      ) : n.type === "memory" ? (
                        // Memories are small brains; those that tie two or
                        // more records together glow a little more.
                        <>
                          <circle
                            r="6"
                            fill="currentColor"
                            opacity={memoryHubs.has(n.id) ? 0.2 : 0.1}
                          />
                          <Brain
                            className="memory-glyph"
                            x={-5.5}
                            y={-5.5}
                            width={11}
                            height={11}
                            strokeWidth={2.2}
                            aria-hidden="true"
                          />
                        </>
                      ) : (
                        <circle
                          r={theme ? 5 : n.type === "project" ? 4 : 3}
                          fill="currentColor"
                          stroke="currentColor"
                          strokeWidth="1"
                        />
                      )}
                      {selected === n.id && (
                        <circle
                          r="25"
                          fill="none"
                          stroke="currentColor"
                          strokeDasharray="2 5"
                          opacity=".7"
                        />
                      )}
                    </>
                  )}
                  {label && (
                    <text
                      y={labelCandidates(theme, font, 0)[0].y}
                      textAnchor="middle"
                      fill={theme ? "#dce9ed" : "#93a7b2"}
                      // Text and its 3px outline keep one on-screen size at
                      // every width and zoom; CSS does not set either.
                      style={{
                        fontSize: font,
                        strokeWidth: 3 / Math.max(unit, 0.05),
                        letterSpacing: theme ? "0.065em" : "0.01em",
                      }}
                    >
                      {n.type === "memory" ? (
                        // Memory titles are written to be read whole: two
                        // short lines rather than one cut short.
                        labelLines(n.title, compact ? 20 : 26).map(
                          (line, i) => (
                            <tspan key={i} x="0" dy={i ? "1.2em" : undefined}>
                              {line}
                            </tspan>
                          ),
                        )
                      ) : dashboard ? (
                        <>
                          <tspan x="0">
                            {labelText(n.title, compact ? 20 : 30)}
                          </tspan>
                          <tspan x="0" dy="1.3em" className="dashboard-tag">
                            DASHBOARD
                          </tspan>
                        </>
                      ) : (
                        <tspan x="0">
                          {labelText(n.title, compact ? 20 : 30)}
                        </tspan>
                      )}
                    </text>
                  )}
                </g>
              );
            }),
          )}
        </g>
      </svg>
      {!nodes.length && (
        <div className="graph-empty" role="status">
          <h3>
            {loading
              ? "Loading knowledge"
              : emptyMessage
                ? (emptyTitle ?? "Knowledge unavailable")
                : "No matching constellations"}
          </h3>
          <p>
            {loading
              ? "Retrieving authorized graph records…"
              : (emptyMessage ?? "Try another search or clear your filters.")}
          </p>
        </div>
      )}
      <div className="graph-controls">
        <button aria-label="Zoom out" onClick={() => adjust(0.8)}>
          <Minus size={15} />
        </button>
        <span>{Math.round(scale * 100)}%</span>
        <button aria-label="Zoom in" onClick={() => adjust(1.25)}>
          <Plus size={15} />
        </button>
        <button aria-label="Reset graph view" onClick={reset}>
          <Scan size={15} />
        </button>
        <button
          aria-label="Show all labels"
          title={allLabels ? "Use fewer labels" : "Show all labels"}
          aria-pressed={allLabels}
          onClick={() => setAllLabels((value) => !value)}
        >
          <Tags size={15} />
        </button>
      </div>
      {interactive && (
        <p className="graph-hint">Drag a star · Pan the sky · Scroll to zoom</p>
      )}
    </div>
  );
}
