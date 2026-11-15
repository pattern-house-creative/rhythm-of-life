// Discover's Dismiss kept on the engine (SB-P4-05, Dismissals.tsx; app_memories dismissals, dismiss and undismiss):
// while the engine serves the memories (its memory.tools on), a Dismiss is saved, so the suggestion stays off the map
// after a reload and a rebuild (the engine leaves it out of every read), and Discover lists what was dismissed on this
// map, by this person, by someone else or by an AI client's unlink, with a way to show it again. A link an AI client
// made is removed instead. With the engine not serving the memories, Dismiss hides a suggestion on this screen only, as
// it always did, and nothing else shows. At a phone's size and at a desktop's. Fictional data only.
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import Explorer from "../src/Explorer";
import { emptyGraph } from "../src/liveGraph";
import type { KnowledgeController } from "../src/useKnowledge";
import type { GraphEdge, GraphNode, GraphPage } from "../src/knowledge";
import type { MapDismissal, MemoryClient } from "../src/memories";
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

const uuid = (n: number) =>
  `6d3c2a10-0000-4000-8000-${String(n).padStart(12, "0")}`;
const linkId = (n: number) => `relationship:${String(n).repeat(64)}`;
// A record saved in Rhythm, as the catalog's document star.
function document(external: string, title: string): GraphNode {
  const n = record(`household:${external}`, "rhythm", "documents");
  n.type = "document";
  n.title = title;
  n.provenance.demo = false;
  return n;
}
function memory(n: number, title: string): GraphNode {
  const s = record(uuid(n), "memory", "memory");
  s.title = title;
  s.provenance.demo = false;
  return s;
}
function suggestion(
  id: string,
  a: GraphNode,
  b: GraphNode,
  type: string,
  verification = "suggested",
): GraphEdge {
  return {
    ...relation(a, b),
    id,
    type,
    directed: false,
    verification,
    relationship: "DEMO: alike in meaning",
  };
}
// Two notes about the Quarry Lane bus stop alike in meaning, and a link an AI client made between two memories.
function sky() {
  const bus = document(
    "quarry-bus.txt",
    "DEMO: the Quarry Lane bus stop moved",
  );
  const path = document(
    "quarry-path.txt",
    "DEMO: the Quarry Lane footpath is closed",
  );
  const heron = memory(1, "DEMO: the Pellingham heron nests by the weir");
  const weir = memory(2, "DEMO: the Pellingham heron fishes at dawn");
  const alike = suggestion(linkId(1), bus, path, "similar_to");
  const made = suggestion(
    `made-link:${uuid(9)}`,
    heron,
    weir,
    "relates_to",
    "inferred",
  );
  const page: GraphPage = {
    ...emptyGraph(),
    nodes: [bus, path, heron, weir],
    edges: [alike, made],
  };
  return { page, bus, path, heron, weir, alike, made };
}
const kept = (
  s: ReturnType<typeof sky>,
  by: MapDismissal["by"] = "person",
  mine = true,
): MapDismissal => ({
  id: `stars:${uuid(101)}:${uuid(102)}:similar_to`,
  kind: "stars",
  type: "similar_to",
  stars: [
    { id: s.bus.id, title: s.bus.title },
    { id: s.path.id, title: s.path.title },
  ],
  dismissedAt: "2026-11-04T09:12:00+00:00",
  by,
  mine,
  mayRestore: true,
});

// The engine as the app's memories route answers it: its list of dismissals, and each op as it would answer.
function engine(
  served: boolean,
  list: MapDismissal[],
  answers: Record<string, unknown> = {},
) {
  const calls: Record<string, unknown>[] = [];
  const client: MemoryClient = {
    available: vi.fn(async () => served),
    call: vi.fn(async (request: Record<string, unknown>) => {
      calls.push(request);
      if (request.op in answers) return answers[request.op as string] as never;
      if (request.op === "dismissals")
        return {
          available: true,
          ok: true,
          dismissals: [...list],
          truncated: false,
        } as never;
      if (request.op === "undismiss") {
        list.splice(
          list.findIndex((d) => d.id === request.dismissal),
          1,
        );
        return {
          available: true,
          ok: true,
          op: "undismiss",
          dismissal: request.dismissal,
          unchanged: false,
          back: !String(request.dismissal).endsWith(":relates_to"),
        } as never;
      }
      return { available: false } as never;
    }),
  };
  return { client, calls };
}
const controller = (
  page: GraphPage,
  memories?: MemoryClient,
): KnowledgeController => ({
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
    ...(memories ? { memories } : {}),
  },
});
function show(page: GraphPage, memories?: MemoryClient) {
  const knowledge = controller(page, memories);
  const view = render(
    <Explorer
      knowledge={knowledge}
      selected={null}
      onSelect={vi.fn()}
      close={vi.fn()}
      paused
      state="ready"
    />,
  );
  return { ...view, knowledge };
}
const drawnBetween = (container: HTMLElement, e: GraphEdge) =>
  container.querySelector(
    `path.graph-edge[data-source="${e.source}"][data-target="${e.target}"]`,
  );
const card = (title: string) =>
  screen.getAllByRole("article").find((x) => x.textContent?.includes(title))!;

describe.each(["phone", "desktop"] as const)("at a %s's size", (size) => {
  it("keeps a Dismiss on the engine and shows it again from the dismissed list", async () => {
    at(size);
    const s = sky();
    const list: MapDismissal[] = [];
    const { client, calls } = engine(true, list, {
      dismiss: (() => {
        return {
          available: true,
          ok: true,
          op: "dismiss",
          link: s.alike.id,
          dismissal: kept(s).id,
          removed: false,
          unchanged: false,
        };
      })(),
    });
    const user = userEvent.setup();
    const { container, knowledge } = show(s.page, client);
    await waitFor(() => expect(client.available).toHaveBeenCalled());
    await user.click(
      screen.getByRole("button", { name: "Discover", exact: true }),
    );
    await waitFor(() => expect(calls).toContainEqual({ op: "dismissals" }));
    expect(
      screen.queryByRole("region", { name: "Dismissed suggestions" }),
    ).toBeNull();
    expect(drawnBetween(container, s.alike)).not.toBeNull();

    list.push(kept(s));
    await user.click(
      within(card(s.bus.title)).getByRole("button", { name: "Dismiss" }),
    );
    expect(calls).toContainEqual({ op: "dismiss", link: s.alike.id });
    // Off the sky at once, and kept: it says so, and the list read again names it.
    expect(drawnBetween(container, s.alike)).toBeNull();
    expect(await screen.findByRole("status")).toHaveTextContent(
      "Dismissed. It stays off the map after a reload and a rebuild",
    );
    const region = await screen.findByRole("region", {
      name: "Dismissed suggestions",
    });
    expect(region).toHaveTextContent(`${s.bus.title} · ${s.path.title}`);
    expect(region).toHaveTextContent("similar in meaning · dismissed by you");

    await user.click(
      within(region).getByRole("button", {
        name: `Show again: ${s.bus.title} and ${s.path.title}`,
      }),
    );
    expect(calls).toContainEqual({ op: "undismiss", dismissal: kept(s).id });
    await waitFor(() => expect(knowledge.refresh).toHaveBeenCalled());
    expect(
      screen.queryByRole("region", { name: "Dismissed suggestions" }),
    ).toBeNull();
    expect(screen.getByRole("status")).toHaveTextContent(
      "Shown again: it is back on the map.",
    );
    // What this screen hid comes back with it.
    expect(drawnBetween(container, s.alike)).not.toBeNull();
  });

  it("lists after a reload what an AI client's unlink dismissed, and a person shows it again", async () => {
    at(size);
    const s = sky();
    // As the engine serves the map once it is dismissed: without the suggestion.
    const page = { ...s.page, edges: [s.made] };
    const { client, calls } = engine(true, [kept(s, "ai_client", false)]);
    const user = userEvent.setup();
    const { container, knowledge } = show(page, client);
    await waitFor(() => expect(client.available).toHaveBeenCalled());
    await user.click(
      screen.getByRole("button", { name: "Discover", exact: true }),
    );
    expect(drawnBetween(container, s.alike)).toBeNull();
    const region = await screen.findByRole("region", {
      name: "Dismissed suggestions",
    });
    expect(region).toHaveTextContent(
      "similar in meaning · dismissed by an AI client",
    );
    await user.click(
      within(region).getByRole("button", { name: /^Show again: / }),
    );
    expect(calls).toContainEqual({ op: "undismiss", dismissal: kept(s).id });
    await waitFor(() => expect(knowledge.refresh).toHaveBeenCalled());
  });

  it("says when taking a dismissal back brings no link back, since the link it came from was removed", async () => {
    at(size);
    const s = sky();
    // Kept when a person removed a suggested link between the two memories: nothing suggests it now.
    const gone: MapDismissal = {
      ...kept(s),
      id: `memories:${uuid(1)}:${uuid(2)}:relates_to`,
      kind: "memories",
      type: "relates_to",
      stars: [
        { id: s.heron.id, title: s.heron.title },
        { id: s.weir.id, title: s.weir.title },
      ],
    };
    const page = { ...s.page, edges: [s.alike] };
    const { client, calls } = engine(true, [gone]);
    const user = userEvent.setup();
    show(page, client);
    await waitFor(() => expect(client.available).toHaveBeenCalled());
    await user.click(
      screen.getByRole("button", { name: "Discover", exact: true }),
    );
    const region = await screen.findByRole("region", {
      name: "Dismissed suggestions",
    });
    await user.click(
      within(region).getByRole("button", { name: /^Show again: / }),
    );
    expect(calls).toContainEqual({ op: "undismiss", dismissal: gone.id });
    expect(await screen.findByRole("status")).toHaveTextContent(
      "Taken back. Nothing suggests this link now",
    );
    expect(screen.getByRole("status")).not.toHaveTextContent("Shown again");
  });

  it("says when a Dismiss could not be kept, and hides it on this screen only", async () => {
    at(size);
    const s = sky();
    const { client } = engine(true, [], {
      dismiss: {
        available: true,
        ok: false,
        code: "forbidden",
        reason:
          "Only the owner, the author of either end or a Family admin may dismiss this suggestion",
        revision: null,
        known: [],
      },
    });
    const user = userEvent.setup();
    const { container } = show(s.page, client);
    await waitFor(() => expect(client.available).toHaveBeenCalled());
    await user.click(
      screen.getByRole("button", { name: "Discover", exact: true }),
    );
    await user.click(
      within(card(s.bus.title)).getByRole("button", { name: "Dismiss" }),
    );
    expect(drawnBetween(container, s.alike)).toBeNull();
    expect(await screen.findByRole("status")).toHaveTextContent(
      "Only the owner, the author of either end or a Family admin may dismiss this suggestion Hidden on this screen only.",
    );
    expect(
      screen.queryByRole("region", { name: "Dismissed suggestions" }),
    ).toBeNull();
  });

  it("removes a link an AI client made, and says nothing is kept to show again", async () => {
    at(size);
    const s = sky();
    const { client, calls } = engine(true, [], {
      dismiss: {
        available: true,
        ok: true,
        op: "dismiss",
        link: s.made.id,
        dismissal: null,
        removed: true,
        unchanged: false,
      },
    });
    const user = userEvent.setup();
    show(s.page, client);
    await waitFor(() => expect(client.available).toHaveBeenCalled());
    await user.click(
      screen.getByRole("button", { name: "Discover", exact: true }),
    );
    await user.click(
      within(card(s.heron.title)).getByRole("button", { name: "Dismiss" }),
    );
    expect(calls).toContainEqual({ op: "dismiss", link: s.made.id });
    expect(await screen.findByRole("status")).toHaveTextContent(
      "Removed the link an AI client made.",
    );
  });

  it("says nothing when the list could not be read, which no one asked for", async () => {
    at(size);
    const s = sky();
    const { client, calls } = engine(true, [], {
      dismissals: {
        available: true,
        ok: false,
        code: "unavailable",
        reason: "Memories can't be read right now. Try again later.",
        revision: null,
        known: [],
      },
    });
    const user = userEvent.setup();
    show(s.page, client);
    await waitFor(() => expect(client.available).toHaveBeenCalled());
    await user.click(
      screen.getByRole("button", { name: "Discover", exact: true }),
    );
    await waitFor(() => expect(calls).toContainEqual({ op: "dismissals" }));
    expect(screen.queryByRole("status")).toBeNull();
    expect(
      screen.queryByRole("region", { name: "Dismissed suggestions" }),
    ).toBeNull();
  });

  it("with the engine not serving the memories, Dismiss is this screen's alone, as before", async () => {
    at(size);
    for (const memories of [undefined, engine(false, [kept(s0())]).client]) {
      const s = sky();
      const user = userEvent.setup();
      const { container, unmount } = show(s.page, memories);
      if (memories)
        await waitFor(() => expect(memories.available).toHaveBeenCalled());
      await user.click(
        screen.getByRole("button", { name: "Discover", exact: true }),
      );
      await user.click(
        within(card(s.bus.title)).getByRole("button", { name: "Dismiss" }),
      );
      expect(drawnBetween(container, s.alike)).toBeNull();
      // Nothing asked of the engine, nothing said, no list.
      if (memories) expect(memories.call).not.toHaveBeenCalled();
      expect(screen.queryByRole("status")).toBeNull();
      expect(
        screen.queryByRole("region", { name: "Dismissed suggestions" }),
      ).toBeNull();
      unmount();
    }
  });
});
const s0 = () => sky();
