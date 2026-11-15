// Typed links and replaced facts on the map and in the explorer (SB-P4-11): a native memory's star that a newer memory
// of its scope replaced (truth, sent only while the engine's memory.tools is on) is drawn faded, never hidden, says it
// is no longer true and leads to what replaced it; the newer memory names what it replaced; upstream's link types read
// in their plain words from the side the reader is on, while Rhythm's own keep the words they had. At a phone's size
// and at a desktop's. A map with no replaced facts draws exactly as one whose engine sends no truth. Fictional data only.
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import Explorer from "../src/Explorer";
import { emptyGraph } from "../src/liveGraph";
import {
  ambientWindow,
  graphWindow,
  linkLabel,
  rankNodes,
  replacedBy,
  replacements,
} from "../src/graphView";
import type { KnowledgeController } from "../src/useKnowledge";
import type { GraphEdge, GraphNode, GraphPage } from "../src/knowledge";
import type { MemoryClient } from "../src/memories";
import { record, relation } from "./synthetic";

const sizes = {
  phone: (query: string) => query.includes("max-width: 760px"),
  desktop: (query: string) => query.includes("min-width: 1100px"),
} as const;
let standard: ((query: string) => MediaQueryList) | undefined;
function at(size: keyof typeof sizes) {
  standard ??= vi.mocked(window.matchMedia).getMockImplementation() as (
    query: string,
  ) => MediaQueryList;
  vi.mocked(window.matchMedia).mockImplementation(
    (query: string) =>
      ({
        matches: sizes[size](query),
        media: query,
        addListener: vi.fn(),
        removeListener: vi.fn(),
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
      }) as unknown as MediaQueryList,
  );
}
afterEach(() => {
  if (standard) vi.mocked(window.matchMedia).mockImplementation(standard);
});

// The engine's memory.tools on, as a replaced fact's truth needs: the Memories are served, and links read in their
// plain words (with it off they read as before; explorer-panel.test.tsx).
const served = (): MemoryClient => ({
  available: vi.fn(async () => true),
  call: vi.fn(),
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
    memories: served(),
  },
});
const uuid = (n: number) =>
  `6d3c2a10-0000-4000-8000-${String(n).padStart(12, "0")}`;
// A native memory's star, as the engine names one (MemoryStars.Key: provider and account memory).
function star(n: number, title: string): GraphNode {
  const s = record(uuid(n), "memory", "memory");
  s.title = title;
  s.overview = title;
  s.provenance.demo = false;
  s.groups = [{ id: "memory:family", label: "Family" }];
  return s;
}
function link(a: GraphNode, b: GraphNode, type: string): GraphEdge {
  return {
    ...relation(a, b),
    id: `${type}:${a.id}:${b.id}`,
    type,
    origin: { ...a.resource.identity, externalId: `${type}-${a.id}-${b.id}` },
  };
}
// The Pellow choir's rehearsal day changed: the older fact replaced by the newer, a supersedes link between them, the
// newer one related to a note and referencing another, and the older one in a project.
function sky(truth = true) {
  const fresh = star(2, "DEMO: the Pellow choir rehearses on Wednesdays now.");
  const old: GraphNode = {
    ...star(1, "DEMO: the Pellow choir rehearses on Tuesdays."),
    ...(truth
      ? { truth: { state: "replaced" as const, replacedBy: fresh.id } }
      : {}),
  };
  const note = record("fictional-choir-note", "notes");
  note.type = "note";
  note.title = "Fictional choir note";
  note.provenance.demo = false;
  const hall = record("fictional-hall", "drive");
  hall.title = "Fictional hall booking";
  hall.provenance.demo = false;
  const project = star(3, "Fictional choir project");
  project.type = "project";
  const page: GraphPage = {
    ...emptyGraph(),
    nodes: [fresh, old, note, hall, project],
    edges: [
      link(fresh, old, "supersedes"),
      link(fresh, note, "relates_to"),
      link(fresh, hall, "references"),
      link(old, project, "in_project"),
    ],
  };
  return { page, fresh, old, note, hall, project };
}
function show(page: GraphPage, selected: string | null) {
  const onSelect = vi.fn();
  const view = render(
    <Explorer
      knowledge={controller(page)}
      selected={selected}
      onSelect={onSelect}
      close={vi.fn()}
      paused
      state="ready"
    />,
  );
  return { ...view, onSelect };
}
const panel = () =>
  screen.getByRole("complementary", { name: "Selected knowledge details" });
const rows = () =>
  within(within(panel()).getByRole("list", { name: "Connected items" }));
const drawn = (container: HTMLElement, id: string) =>
  container.querySelector<SVGGElement>(
    `svg.graph-svg g.graph-node[data-id="${id}"]`,
  )!;

describe.each(["phone", "desktop"] as const)("at a %s's size", (size) => {
  it("draws a replaced fact faded, never hidden, and leads to what replaced it", async () => {
    at(size);
    const s = sky();
    const user = userEvent.setup();
    const { container, onSelect } = show(s.page, s.old.id);
    // On the sky: there, faded, and named as replaced; the newer fact is drawn as any star.
    const old = drawn(container, s.old.id);
    expect(old).toHaveClass("replaced-star");
    expect(Number(old.style.opacity)).toBeLessThan(1);
    expect(old).toHaveAttribute(
      "aria-label",
      `Explore ${s.old.title} (replaced by a newer memory)`,
    );
    expect(old.querySelector("title")).toHaveTextContent(
      "replaced by a newer memory",
    );
    const fresh = drawn(container, s.fresh.id);
    expect(fresh).not.toHaveClass("replaced-star");
    expect(fresh).toHaveAttribute("aria-label", `Explore ${s.fresh.title}`);
    // In its panel: no longer true, before anything else, with the way to what replaced it.
    const note = within(panel()).getByRole("note");
    expect(note).toHaveTextContent(
      "No longer true: a newer memory replaced it.",
    );
    // The supersedes link reads from this side as what it is: replaced by; its project as before.
    expect(
      await rows().findByRole("button", {
        name: `Why connected: ${s.fresh.title} (replaced by)`,
      }),
    ).toBeInTheDocument();
    expect(
      rows().getByRole("button", {
        name: `Why connected: ${s.project.title} (project)`,
      }),
    ).toBeInTheDocument();
    await user.click(
      within(note).getByRole("button", {
        name: `Replaced by: ${s.fresh.title}`,
      }),
    );
    expect(onSelect).toHaveBeenLastCalledWith(s.fresh.id);
  });

  it("names what the newer memory replaced, and each typed link in plain words", async () => {
    at(size);
    const s = sky();
    const user = userEvent.setup();
    const { onSelect } = show(s.page, s.fresh.id);
    const note = within(panel()).getByRole("note");
    expect(note).not.toHaveTextContent("No longer true");
    await user.click(
      within(note).getByRole("button", { name: `Replaces: ${s.old.title}` }),
    );
    expect(onSelect).toHaveBeenLastCalledWith(s.old.id);
    // Upstream's types in their plain words from this side; Rhythm's own reference as it always read.
    await rows().findByRole("button", {
      name: `Why connected: ${s.old.title} (replaces)`,
    });
    for (const [title, words] of [
      [s.old.title, "replaces"],
      [s.note.title, "related to"],
      [s.hall.title, "references"],
    ])
      expect(
        rows().getByRole("button", {
          name: `Why connected: ${title} (${words})`,
        }),
      ).toBeInTheDocument();
    // Why connected: the link's own words, then its verification and direction.
    await user.click(
      rows().getByRole("button", {
        name: `Why connected: ${s.old.title} (replaces)`,
      }),
    );
    expect(
      within(panel()).getByText(/^replaces · verified · directed$/),
    ).toBeInTheDocument();
  });

  it("says so when what replaced a fact is not on the map", () => {
    at(size);
    const s = sky();
    const page = {
      ...s.page,
      nodes: s.page.nodes.filter((n) => n.id !== s.fresh.id),
      edges: s.page.edges.filter(
        (e) => e.source !== s.fresh.id && e.target !== s.fresh.id,
      ),
    };
    const { container } = show(page, s.old.id);
    expect(drawn(container, s.old.id)).toHaveClass("replaced-star");
    const note = within(panel()).getByRole("note");
    expect(note).toHaveTextContent("What replaced it is not on the map yet.");
    expect(within(note).queryByRole("button")).toBeNull();
  });

  it("draws a map without replaced facts exactly as one whose engine sends no truth", () => {
    at(size);
    // React's own ids differ between two renders; nothing else may.
    const html = (e: HTMLElement) =>
      e.innerHTML.replace(/_r_[0-9a-z]+_/g, "_r_");
    const none = show(sky(false).page, null);
    const sent = html(none.container);
    none.unmount();
    // A truth the app does not know (another state, no replacing star, or itself) is no truth.
    const s = sky(false);
    const odd: GraphPage = {
      ...s.page,
      nodes: s.page.nodes.map((n, i) =>
        i === 1
          ? ({
              ...n,
              truth: { state: "ended", replacedBy: s.fresh.id },
            } as unknown as GraphNode)
          : i === 2
            ? ({ ...n, truth: { state: "replaced" } } as unknown as GraphNode)
            : i === 3
              ? { ...n, truth: { state: "replaced", replacedBy: n.id } }
              : n,
      ),
    };
    const { container } = show(odd, null);
    expect(html(container)).toBe(sent);
    expect(container.querySelector(".replaced-star")).toBeNull();
  });
});

describe("replaced facts in the map's order", () => {
  it("reads truth only as the contract has it", () => {
    const s = sky();
    expect(replacedBy(s.old)).toBe(s.fresh.id);
    expect(replacedBy(s.fresh)).toBeNull();
    expect(replacedBy({ ...s.old, truth: undefined })).toBeNull();
    expect(
      replacedBy({
        ...s.old,
        truth: { state: "ended", replacedBy: s.fresh.id },
      } as unknown as GraphNode),
    ).toBeNull();
    expect(
      replacedBy({
        ...s.old,
        truth: { state: "replaced", replacedBy: "" },
      }),
    ).toBeNull();
    expect([...replacements(s.page.nodes)]).toEqual([[s.fresh.id, [s.old]]]);
  });

  it("ranks a replaced fact after every current memory, and never leaves it out", () => {
    const s = sky();
    const ranked = rankNodes(s.page.nodes, s.page.edges).map((n) => n.id);
    expect(ranked.indexOf(s.old.id)).toBeGreaterThan(
      ranked.indexOf(s.fresh.id),
    );
    // Without truth it is a memory like any other.
    const plain = sky(false);
    expect(
      rankNodes(plain.page.nodes, plain.page.edges).map((n) => n.id),
    ).toContain(plain.old.id);
    // Faded, never hidden: the sky and a window with room hold it.
    expect(ambientWindow(s.page).nodes.map((n) => n.id)).toContain(s.old.id);
    expect(graphWindow(s.page, 48).nodes.map((n) => n.id)).toContain(s.old.id);
  });

  it("lifts nothing for a replaced fact: what only it cites ranks as any record", () => {
    const records = (truth: boolean) => {
      const s = sky(truth);
      const named = (id: string) => {
        const n = record(id, "notes");
        n.type = "note";
        return n;
      };
      // One record only the older fact cites; another linked to three others, so busier, cited by no memory.
      const only = named("fictional-old-flyer");
      const busy = named("fictional-busy-sheet");
      const others = [1, 2, 3].map((i) => named(`fictional-sheet-${i}`));
      const ranked = rankNodes(
        [s.fresh, s.old, only, busy, ...others],
        [
          link(s.old, only, "references"),
          ...others.map((o) => link(busy, o, "relates_to")),
        ],
      ).map((n) => n.id);
      return { ranked, only: only.id, busy: busy.id };
    };
    // Replaced: its citation lifts nothing, so the busier record comes first.
    const replaced = records(true);
    expect(replaced.ranked.indexOf(replaced.busy)).toBeLessThan(
      replaced.ranked.indexOf(replaced.only),
    );
    // The same memory still current lifts what it cites, as before.
    const current = records(false);
    expect(current.ranked.indexOf(current.only)).toBeLessThan(
      current.ranked.indexOf(current.busy),
    );
  });

  it("names upstream's types in plain words and Rhythm's own as before", () => {
    expect([linkLabel("supersedes"), linkLabel("supersedes", false)]).toEqual([
      "replaces",
      "replaced by",
    ]);
    expect([linkLabel("caused_by"), linkLabel("caused_by", false)]).toEqual([
      "caused by",
      "led to",
    ]);
    expect(linkLabel("relates_to", false)).toBe("related to");
    // Rhythm's own and a source's own word read as the explorer always named them.
    for (const type of ["references", "about", "copy_of", "contains"])
      expect([linkLabel(type), linkLabel(type, false)]).toEqual([
        type.replaceAll("_", " "),
        type.replaceAll("_", " "),
      ]);
    expect(linkLabel("draws_on")).toBe("draws on");
  });
});
