import { readFileSync } from "node:fs";
import { afterEach, expect, it, vi } from "vitest";
import { fireEvent, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import App from "../src/App";
import Explorer from "../src/Explorer";
import {
  ambientWindow,
  dashedLink,
  drawnLinks,
  lineLook,
} from "../src/graphView";
import { emptyGraph } from "../src/liveGraph";
import { MapPart } from "../src/parts/MapPart";
import type { MapSky } from "../src/parts/MapPart";
import {
  saveAutomaticLinks,
  savedAutomaticLinks,
  watchSettings,
} from "../src/screens";
import type { GraphEdge, GraphNode, GraphPage } from "../src/knowledge";
import type { KnowledgeController } from "../src/useKnowledge";
import { record, relation } from "./synthetic";

// A calm sky (the owner, 2026-11-09: the links were "starting to look crazy and more like a crazy
// spider web"). Three display changes, none of which deletes a link or moves a star:
// - "Automatic links on the sky" (first "Similar links on the sky", its key kept) in Sources &
//   settings: off, no dashed line is drawn, by the one rule that dashes a line;
// - folder and project lines are drawn only for the selected star, a folder's or project's
//   members' included;
// - lines are quiet at rest, and a selected or hovered star's own are lit while the rest dim.
// A chosen star always draws every link it has, its panel lists them all, and the explorer's
// Trace and Discover keep every line and the look they had.

afterEach(() => localStorage.clear());

const link = (
  a: GraphNode,
  b: GraphNode,
  over: Partial<GraphEdge>,
): GraphEdge => ({
  ...relation(a, b),
  id: `${over.type ?? "references"}:${a.title}:${b.title}`,
  ...over,
});
const evidence = (from: GraphNode, locator: string, excerpt: string) => [
  {
    resourceId: from.id,
    excerpt,
    locator,
    retrievedAt: from.resource.retrievedAt,
  },
];
// Four fictional stars, a Drive folder and a Second Brain project, joined once each way by every
// kind of link the live map has. Each ordered pair carries one link, so a line is found by its ends.
function fixture() {
  const [a, b, c, d] = ["a", "b", "c", "d"].map((id) => {
    const n = record(`calm-${id}`);
    n.title = `Fictional star ${id.toUpperCase()}`;
    n.provenance.demo = false;
    return n;
  });
  const folder = record("calm-folder", "drive");
  folder.type = "theme";
  folder.title = "Fictional folder";
  folder.provenance.demo = false;
  const project = record("calm-project");
  project.type = "project";
  project.title = "Fictional project";
  project.provenance.demo = false;
  const confirmed = {
    reference: link(a, b, { type: "references" }),
    similar: link(c, d, {
      type: "similar_to",
      directed: false,
      verification: "verified",
    }),
    relates: link(a, d, { type: "relates_to", directed: false }),
  };
  const members = {
    folder: link(c, folder, { type: "in_folder" }),
    project: link(d, project, { type: "in_project" }),
    projectB: link(b, project, { type: "in_project" }),
  };
  const automatic = {
    similar: link(a, c, {
      type: "similar_to",
      directed: false,
      verification: "suggested",
    }),
    similarBD: link(b, d, {
      type: "similar_to",
      directed: false,
      verification: "suggested",
    }),
    topic: link(b, c, {
      type: "about",
      verification: "inferred",
      evidence: evidence(b, "catalog:topic", "Fictional topic"),
    }),
    plugin: link(d, a, {
      type: "relates_to",
      verification: "suggested",
      evidence: evidence(d, "ingest:fictional-plugin", "Fictional plugin"),
    }),
    secondBrain: link(b, a, {
      type: "relates_to",
      verification: "inferred",
      evidence: evidence(b, "second-brain:fictional", "Fictional auto link"),
    }),
    relates: link(c, b, { type: "relates_to", verification: "suggested" }),
    follows: link(d, c, { type: "follows", verification: "suggested" }),
  };
  const page: GraphPage = {
    ...emptyGraph(),
    nodes: [a, b, c, d, folder, project],
    edges: [
      ...Object.values(confirmed),
      ...Object.values(members),
      ...Object.values(automatic),
    ],
  };
  return { page, a, b, c, d, folder, project, confirmed, members, automatic };
}
type Fixture = ReturnType<typeof fixture>;
const confirmedOf = (s: Fixture) => Object.values(s.confirmed);
const membersOf = (s: Fixture) => Object.values(s.members);
const automaticOf = (s: Fixture) => Object.values(s.automatic);
const touching = (e: GraphEdge, id: string) =>
  e.source === id || e.target === id;
// The link's line on a sky.
const line = (root: ParentNode, e: GraphEdge) =>
  root.querySelector<SVGPathElement>(
    `path.graph-edge[data-source="${e.source}"][data-target="${e.target}"]`,
  );
const drawnOf = (root: ParentNode, edges: GraphEdge[]) =>
  edges.filter((e) => line(root, e) !== null);
const look = (root: ParentNode, e: GraphEdge) => {
  const path = line(root, e)!;
  return {
    width: Number(path.getAttribute("stroke-width")),
    opacity: Number(path.getAttribute("opacity")),
  };
};
const positions = (root: ParentNode) =>
  [...root.querySelectorAll("g.graph-node")].map((g) => [
    (g as SVGGElement).dataset.id,
    g.getAttribute("transform"),
  ]);
const star = (root: ParentNode, n: GraphNode) =>
  root.querySelector<SVGGElement>(`g.graph-node[data-id="${n.id}"]`)!;

it("dashes and hides by one rule: every link nobody confirmed, whatever its kind", () => {
  const s = fixture();
  for (const e of automaticOf(s)) expect(dashedLink(e)).toBe(true);
  for (const e of [...confirmedOf(s), ...membersOf(s)])
    expect(dashedLink(e)).toBe(false);
  // Off, outside a calm sky (Trace): exactly the dashed links go.
  const { edges } = s.page;
  expect(drawnLinks(edges, false, null, false)).toEqual(
    edges.filter((e) => !dashedLink(e)),
  );
  // On, outside a calm sky (Discover): the same array.
  expect(drawnLinks(edges, true, null, false)).toBe(edges);
});

it("draws no folder or project line at rest, and the focus's own links of every kind", () => {
  const s = fixture();
  const { edges } = s.page;
  // At rest, on: confirmed and automatic links, no member lines.
  expect(drawnLinks(edges, true)).toEqual([
    ...confirmedOf(s),
    ...automaticOf(s),
  ]);
  // At rest, off: confirmed links only.
  expect(drawnLinks(edges, false)).toEqual(confirmedOf(s));
  // A chosen star draws all of its own, of every kind, whatever the switch says.
  for (const automatic of [true, false])
    for (const focus of [s.a, s.b, s.c, s.d]) {
      const shown = drawnLinks(edges, automatic, focus.id);
      for (const e of edges.filter((x) => touching(x, focus.id)))
        expect(shown).toContain(e);
      // Another star's member and (while off) automatic links stay hidden.
      for (const e of shown.filter((x) => !touching(x, focus.id)))
        expect([
          ...confirmedOf(s),
          ...(automatic ? automaticOf(s) : []),
        ]).toContain(e);
    }
  // A chosen folder or project draws its members' lines.
  expect(drawnLinks(edges, false, s.folder.id)).toEqual([
    ...confirmedOf(s),
    s.members.folder,
  ]);
  expect(drawnLinks(edges, false, s.project.id)).toEqual([
    ...confirmedOf(s),
    s.members.project,
    s.members.projectB,
  ]);
});

it("keeps its key, so an off chosen as Similar links on the sky holds, and follows other tabs", () => {
  expect(savedAutomaticLinks()).toBe(true);
  // The key the switch was first kept under.
  localStorage.setItem("rhythm-of-life.similar-links", "off");
  expect(savedAutomaticLinks()).toBe(false);
  saveAutomaticLinks(true);
  expect(localStorage.getItem("rhythm-of-life.similar-links")).toBeNull();
  expect(savedAutomaticLinks()).toBe(true);
  saveAutomaticLinks(false);
  expect(localStorage.getItem("rhythm-of-life.similar-links")).toBe("off");
  // Anything else stored there reads as the default.
  localStorage.setItem("rhythm-of-life.similar-links", "nonsense");
  expect(savedAutomaticLinks()).toBe(true);
  const changed = vi.fn();
  const stop = watchSettings(changed);
  window.dispatchEvent(
    new StorageEvent("storage", { key: "rhythm-of-life.similar-links" }),
  );
  expect(changed).toHaveBeenCalledTimes(1);
  stop();
});

it("is a switch in Sources & settings, on until turned off, and stays off after a reload", async () => {
  const user = userEvent.setup();
  const first = render(<App />);
  await user.click(
    await screen.findByRole("button", { name: "Sources and settings" }),
  );
  const on = await screen.findByRole("button", {
    name: "Automatic links on the sky: on",
  });
  expect(on).toHaveAttribute("aria-pressed", "true");
  expect(on).toHaveAccessibleDescription(
    "Automatic links are the dashed lines nobody has confirmed: stars alike in meaning, topics, plugins' and Second Brain's links, and suggestions.",
  );
  on.focus();
  await user.keyboard("{Enter}");
  const off = screen.getByRole("button", {
    name: "Automatic links on the sky: off",
  });
  expect(off).toHaveAttribute("aria-pressed", "false");
  expect(off).toHaveAccessibleDescription(
    "Automatic links are hidden on the sky. A star you choose still shows all of its own, and its panel still lists them.",
  );
  expect(localStorage.getItem("rhythm-of-life.similar-links")).toBe("off");
  first.unmount();
  render(<App />);
  await user.click(
    await screen.findByRole("button", { name: "Sources and settings" }),
  );
  expect(
    await screen.findByRole("button", {
      name: "Automatic links on the sky: off",
    }),
  ).toHaveAttribute("aria-pressed", "false");
});

const sky = (page: GraphPage, automaticLinks?: boolean): MapSky => ({
  page,
  overview: ambientWindow(page),
  pins: [],
  demo: true,
  loading: false,
  readAt: null,
  ...(automaticLinks === undefined ? {} : { automaticLinks }),
});
function drawHome(page: GraphPage, automaticLinks?: boolean) {
  const element = (selected: string | null) => (
    <section className="part-map">
      <MapPart
        sky={sky(page, automaticLinks)}
        blocked={false}
        paused
        own
        selected={selected}
        onStar={vi.fn()}
        onExplore={vi.fn()}
      />
    </section>
  );
  const view = render(element(null));
  return { ...view, choose: (id: string | null) => view.rerender(element(id)) };
}

it("on Home's sky draws what the rules say, at rest and for a chosen star, without moving a star", () => {
  const s = fixture();
  const edges = s.page.edges;
  const on = drawHome(s.page);
  expect(on.container.querySelector(".constellation")).toHaveClass("is-calm");
  expect(drawnOf(on.container, edges)).toEqual([
    ...confirmedOf(s),
    ...automaticOf(s),
  ]);
  // Every drawn line the rule calls dashed is dashed, and no other.
  for (const e of drawnOf(on.container, edges))
    expect(line(on.container, e)!.hasAttribute("stroke-dasharray")).toBe(
      dashedLink(e),
    );
  const where = positions(on.container);
  expect(where).toHaveLength(6);
  for (const [, at] of where) expect(at).toMatch(/^translate\(/);
  for (const chosen of [s.c, s.folder, s.project, null]) {
    on.choose(chosen?.id ?? null);
    expect(positions(on.container)).toEqual(where);
  }
  on.unmount();
  const off = drawHome(s.page, false);
  // Off: no dashed line, every confirmed one, still no member line.
  expect(drawnOf(off.container, edges)).toEqual(confirmedOf(s));
  expect(positions(off.container)).toEqual(where);
  // A chosen star draws all of its own links, of every kind.
  for (const chosen of [s.a, s.b, s.c, s.d, s.folder, s.project]) {
    off.choose(chosen.id);
    const own = edges.filter((e) => touching(e, chosen.id));
    expect(own.length).toBeGreaterThan(0);
    for (const e of own) expect(line(off.container, e)).not.toBeNull();
    for (const e of drawnOf(off.container, edges))
      expect(touching(e, chosen.id) || confirmedOf(s).includes(e)).toBe(true);
    expect(positions(off.container)).toEqual(where);
  }
  // Let go: back to rest.
  off.choose(null);
  expect(drawnOf(off.container, edges)).toEqual(confirmedOf(s));
  expect(positions(off.container)).toEqual(where);
});

it("draws quiet lines at rest and lights a chosen or hovered star's own while the rest dim", async () => {
  const user = userEvent.setup();
  const s = fixture();
  const home = drawHome(s.page);
  const root = home.container;
  const strength = s.confirmed.reference.strength;
  // At rest: thinner and fainter than before, the same for every drawn line.
  const rest = look(root, s.confirmed.reference);
  expect(rest.width).toBeCloseTo(lineLook.calm.width(strength));
  expect(rest.width).toBeLessThan(lineLook.before.width(strength));
  expect(rest.opacity).toBe(lineLook.calm.rest);
  expect(rest.opacity).toBeLessThan(lineLook.before.rest);
  for (const e of drawnOf(root, s.page.edges))
    expect(look(root, e).opacity).toBe(lineLook.calm.rest);
  const lit = (focus: GraphNode) => {
    for (const e of drawnOf(root, s.page.edges)) {
      const now = look(root, e);
      if (touching(e, focus.id)) {
        // At least as bright as a chosen star's lines were.
        expect(now.width).toBeGreaterThanOrEqual(lineLook.before.lit.width);
        expect(now.opacity).toBeGreaterThanOrEqual(lineLook.before.lit.opacity);
        expect(line(root, e)).toHaveClass("is-related");
      } else {
        // Dimmer than at rest, and than other lines were beside a chosen star.
        expect(now.opacity).toBeLessThan(lineLook.calm.rest);
        expect(now.opacity).toBeLessThan(lineLook.before.dim);
        expect(line(root, e)).not.toHaveClass("is-related");
      }
    }
  };
  // Chosen.
  home.choose(s.a.id);
  lit(s.a);
  home.choose(null);
  expect(look(root, s.confirmed.reference)).toEqual(rest);
  // Hovered with a mouse: lit the same way, and back at rest when it leaves.
  await user.hover(star(root, s.b));
  lit(s.b);
  // Hover lights lines only: no star dims, and nothing is selected.
  expect(star(root, s.d)).toHaveStyle({ opacity: "1" });
  expect(root.querySelector(".constellation")).not.toHaveClass("has-focus");
  await user.unhover(star(root, s.b));
  for (const e of drawnOf(root, s.page.edges))
    expect(look(root, e).opacity).toBe(lineLook.calm.rest);
  // A folder at rest has no line drawn: hovering it dims nothing.
  await user.hover(star(root, s.folder));
  for (const e of drawnOf(root, s.page.edges))
    expect(look(root, e).opacity).toBe(lineLook.calm.rest);
  await user.unhover(star(root, s.folder));
  // A finger has no hover: touching a star lights nothing until it is chosen.
  fireEvent.pointerOver(star(root, s.b), { pointerType: "touch" });
  for (const e of drawnOf(root, s.page.edges))
    expect(look(root, e).opacity).toBe(lineLook.calm.rest);
});

it("changes its lines at once for anyone who asks for less motion", () => {
  const css = readFileSync("src/App.css", "utf8");
  const style = document.createElement("style");
  style.textContent = css;
  document.head.append(style);
  const rules = [...style.sheet!.cssRules];
  const calm = ".constellation.is-calm .graph-edge";
  const rule = (list: CSSRule[]) =>
    list.find(
      (r): r is CSSStyleRule =>
        r instanceof CSSStyleRule && r.selectorText === calm,
    );
  // Lines ease between rest and focus...
  expect(rule(rules)?.style.transition).toMatch(/opacity/);
  // ...except with reduced motion, where they change at once.
  const less = rules.filter(
    (r): r is CSSMediaRule =>
      r instanceof CSSMediaRule &&
      r.media.mediaText === "(prefers-reduced-motion: reduce)",
  );
  expect(
    less.map((m) => rule([...m.cssRules])?.style.transition).filter(Boolean),
  ).toEqual(["none"]);
  style.remove();
});

const controller = (page: GraphPage): KnowledgeController => ({
  page,
  accept: vi.fn(),
  pins: [],
  togglePin: vi.fn(),
  busy: false,
  error: null,
  refresh: vi.fn(async () => {}),
  discoverMore: vi.fn(async () => {}),
  lastRefresh: null,
  epoch: 0,
  session: {
    initial: page,
    refresh: vi.fn(async () => page),
    retrieve: vi.fn(async () => ({ ...page, cursors: {} })),
  },
});
function explore(
  page: GraphPage,
  selected: string | null,
  automaticLinks?: boolean,
) {
  const knowledge = controller(page);
  const onSelect = vi.fn();
  const element = (chosen: string | null) => (
    <Explorer
      knowledge={knowledge}
      selected={chosen}
      onSelect={onSelect}
      close={vi.fn()}
      paused
      state="ready"
      {...(automaticLinks === undefined ? {} : { automaticLinks })}
    />
  );
  const view = render(element(selected));
  return {
    ...view,
    onSelect,
    choose: (id: string | null) => view.rerender(element(id)),
  };
}
const sky$ = (root: HTMLElement) =>
  root.querySelector<HTMLElement>(".explorer-sky")!;
const connected = () =>
  within(
    within(
      screen.getByRole("complementary", { name: "Selected knowledge details" }),
    ).getByRole("list", { name: "Connected items" }),
  );

it("in the explorer's Focus draws a chosen star's links of every kind while off, and hides them again once it is let go", () => {
  const s = fixture();
  const { container, choose } = explore(s.page, null, false);
  const drawn = sky$(container);
  expect(drawnOf(drawn, s.page.edges)).toEqual(confirmedOf(s));
  expect(drawn).toHaveTextContent(
    "Automatic links hidden · a chosen star shows its own",
  );
  const where = positions(drawn);
  // Focus on D: its similar, plugin, follows and project links show; others' stay hidden.
  choose(s.d.id);
  for (const e of s.page.edges.filter((e) => touching(e, s.d.id)))
    expect(line(drawn, e)).not.toBeNull();
  for (const e of [s.automatic.topic, s.members.folder, s.members.projectB])
    expect(line(drawn, e)).toBeNull();
  expect(positions(drawn)).toEqual(where);
  // Its lines can be reached from the keyboard, as every drawn link can.
  for (const name of [
    "Explain connection: Fictional star D to Fictional project",
    "Explain connection: Fictional star D to Fictional star C",
  ])
    expect(within(drawn).getByRole("button", { name })).toHaveAttribute(
      "tabindex",
      "0",
    );
  // A chosen folder draws its member's line.
  choose(s.folder.id);
  expect(line(drawn, s.members.folder)).not.toBeNull();
  expect(line(drawn, s.members.project)).toBeNull();
  // Let go: hidden again, and out of the keyboard's way.
  choose(null);
  expect(drawnOf(drawn, s.page.edges)).toEqual(confirmedOf(s));
  expect(
    within(drawn).queryByRole("button", {
      name: "Explain connection: Fictional star D to Fictional project",
    }),
  ).toBeNull();
  expect(positions(drawn)).toEqual(where);
});

it("keeps a star chosen from the keyboard as it was", async () => {
  const user = userEvent.setup();
  const s = fixture();
  const { container, onSelect } = explore(s.page, null);
  const node = star(sky$(container), s.c);
  expect(node).toHaveAttribute("tabindex", "0");
  node.focus();
  await user.keyboard("{Enter}");
  expect(onSelect).toHaveBeenCalledWith(s.c.id);
});

it("lists a star's links of every kind in its panel whether the switch is on or off", () => {
  for (const automaticLinks of [true, false]) {
    const s = fixture();
    const view = explore(s.page, s.d.id, automaticLinks);
    // One row a neighbour, with each way they are related.
    for (const name of [
      "Why connected: Fictional star B (similar in meaning)",
      "Why connected: Fictional project (project)",
      "Why connected: Fictional star A (relates to · relates to · auto)",
      "Why connected: Fictional star C (similar to · follows · suggested)",
    ])
      expect(connected().getByRole("button", { name })).toBeInTheDocument();
    view.unmount();
  }
});

it("leaves Trace and Discover as they were: every folder line, the look they had, and Discover every suggestion", async () => {
  const user = userEvent.setup();
  const s = fixture();
  const strength = s.confirmed.reference.strength;
  const before = (root: ParentNode, e: GraphEdge) => {
    expect(look(root, e).width).toBeCloseTo(lineLook.before.width(strength));
    expect(look(root, e).opacity).toBe(
      e.type === "in_folder" || e.type === "in_project"
        ? lineLook.before.member
        : lineLook.before.rest,
    );
  };
  const on = explore(s.page, null);
  const where = positions(sky$(on.container));
  await user.click(screen.getByRole("button", { name: "Trace" }));
  const traced = sky$(on.container);
  expect(traced.querySelector(".constellation")).not.toHaveClass("is-calm");
  expect(drawnOf(traced, s.page.edges)).toEqual(s.page.edges);
  for (const e of s.page.edges) before(traced, e);
  // Hover lights nothing there.
  await user.hover(star(traced, s.b));
  for (const e of s.page.edges) before(traced, e);
  expect(positions(traced)).toEqual(where);
  on.unmount();
  const off = explore(s.page, null, false);
  await user.click(screen.getByRole("button", { name: "Discover" }));
  const discovered = sky$(off.container);
  expect(discovered.querySelector(".constellation")).not.toHaveClass("is-calm");
  expect(drawnOf(discovered, s.page.edges)).toEqual(s.page.edges);
  for (const e of s.page.edges) before(discovered, e);
  expect(discovered).not.toHaveTextContent("Automatic links hidden");
  expect(positions(discovered)).toEqual(where);
});
