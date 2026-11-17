import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import Explorer from "../src/Explorer";
import { emptyGraph } from "../src/liveGraph";
import type { ShowOnScreens } from "../src/ShowOn";
import type { ScreenCommand, ScreenSummary } from "../src/screens";
import { record, relation } from "./synthetic";
import type { KnowledgeController } from "../src/useKnowledge";
import type { GraphPage } from "../src/knowledge";

// The explorer as a command drives it (docs/dev/screens.md section 6): a request with a nonce
// opens a star (clearing any search), a search or a dashboard while it is already open, and a
// star's panel offers "Show on…". Fictional stars and screens only.
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
function sky() {
  const plan = record("fictional-plan");
  plan.title = "Fictional plan";
  const walk = record("fictional-walk");
  walk.title = "Fictional walk";
  const notes = record("fictional-notes");
  notes.title = "Fictional plan notes";
  const page = {
    ...emptyGraph(),
    nodes: [plan, walk, notes],
    edges: [relation(notes, plan)],
  };
  return { page, plan, walk, notes };
}
type Request = { nonce: number; star?: string; search?: string };
function show(
  page: GraphPage,
  selected: string | null,
  request: Request | null,
  showOn?: ShowOnScreens,
) {
  const onState = vi.fn();
  const props = {
    knowledge: controller(page),
    onSelect: vi.fn(),
    close: vi.fn(),
    paused: true,
    state: "ready" as const,
    onState,
    showOn,
  };
  const view = render(
    <Explorer {...props} selected={selected} request={request} />,
  );
  const ask = (next: string | null, again: Request) =>
    view.rerender(<Explorer {...props} selected={next} request={again} />);
  return { ...view, ask, onState };
}
const panel = () =>
  screen.getByRole("complementary", { name: "Selected knowledge details" });
const searchBox = () =>
  screen.getByRole("textbox", { name: "Search knowledge" });

it("fills the search and lights the matches, then opens a star over it", async () => {
  const s = sky();
  const { container, ask, onState } = show(s.page, null, {
    nonce: 1,
    search: "plan",
  });
  expect(searchBox()).toHaveValue("plan");
  const lit = [...container.querySelectorAll("g.graph-node.related-node")]
    .map((g) => g.getAttribute("data-id"))
    .sort();
  expect(lit).toEqual([s.plan.id, s.notes.id].sort());
  expect(onState).toHaveBeenLastCalledWith({
    selected: null,
    reader: null,
    query: "plan",
  });
  // Already open, a star from a command clears the search so its panel shows.
  ask(s.walk.id, { nonce: 2, star: s.walk.id });
  await waitFor(() => expect(searchBox()).toHaveValue(""));
  expect(
    within(panel()).getByRole("heading", { name: "Fictional walk" }),
  ).toBeInTheDocument();
  expect(onState).toHaveBeenLastCalledWith({
    selected: s.walk.id,
    reader: null,
    query: "",
  });
  // The same request again changes nothing; only a new nonce is a new request.
  await userEvent.setup().type(searchBox(), "x");
  ask(s.walk.id, { nonce: 2, star: s.walk.id });
  expect(searchBox()).toHaveValue("x");
});

it("lists the other open screens in the panel and says how showing went", async () => {
  const s = sky();
  const user = userEvent.setup();
  const screens: ScreenSummary[] = [
    {
      id: "kitchenScreen0000000001",
      name: "Kitchen iPad",
      open: true,
      seenAt: "2026-10-31T09:00:00Z",
      showing: {},
      self: false,
    },
    {
      id: "thisScreen00000000000002",
      name: "Hall",
      open: true,
      seenAt: "2026-10-31T09:00:00Z",
      showing: {},
      self: true,
    },
    {
      id: "oldScreen000000000000003",
      name: "Spare room",
      open: false,
      seenAt: "2026-10-31T07:00:00Z",
      showing: {},
      self: false,
    },
  ];
  const command = (result: ScreenCommand["result"]): ScreenCommand => ({
    id: "6f1c2b3a-4d5e-4f60-8a7b-9c0d1e2f3a4b",
    action: "open_star",
    args: { star: s.plan.id },
    sender: { kind: "screen", screen: screens[1].id, name: "Hall" },
    sentAt: "2026-10-31T09:00:00Z",
    deliveredAt: null,
    doneAt: null,
    result,
    detail: result === "failed" ? { summary: "Not on its map." } : {},
    ms: { deliver: null, show: null },
  });
  const showOn: ShowOnScreens = {
    list: vi.fn(async () => screens),
    send: vi
      .fn<ShowOnScreens["send"]>()
      .mockResolvedValueOnce({ ok: true, command: command("shown") })
      .mockResolvedValueOnce({ ok: true, command: command("delivered") })
      .mockResolvedValueOnce({ ok: true, command: command("failed") })
      .mockResolvedValueOnce({ ok: false, reason: "It wasn't sent: no." }),
  };
  show(s.page, s.plan.id, null, showOn);
  await user.click(within(panel()).getByRole("button", { name: "Show on…" }));
  const group = within(panel()).getByRole("group", {
    name: "Show on another screen",
  });
  // Only the others that are open now: not this screen, not one gone quiet.
  const kitchen = await within(group).findByRole("button", {
    name: "Kitchen iPad",
  });
  expect(within(group).getAllByRole("button")).toHaveLength(1);
  for (const said of [
    "Shown on Kitchen iPad",
    "Sent to Kitchen iPad, not confirmed yet",
    "Kitchen iPad couldn't show it: Not on its map.",
    "It wasn't sent: no.",
  ]) {
    await user.click(kitchen);
    expect(await within(group).findByText(said)).toBeInTheDocument();
  }
  expect(showOn.send).toHaveBeenCalledWith(
    "kitchenScreen0000000001",
    "open_star",
    { star: s.plan.id },
    expect.any(AbortSignal),
  );
  // Unreachable screens say so, in the panel.
  vi.mocked(showOn.list).mockRejectedValueOnce(new Error("down"));
  await user.click(within(panel()).getByRole("button", { name: "Show on…" }));
  await user.click(within(panel()).getByRole("button", { name: "Show on…" }));
  expect(
    await within(panel()).findByText(
      "Other screens can't be reached right now.",
    ),
  ).toBeInTheDocument();
});

it("says a source couldn't be read only when the engine's read failed", async () => {
  const user = userEvent.setup();
  const s = sky();
  const props = {
    onSelect: vi.fn(),
    close: vi.fn(),
    paused: true,
    state: "ready" as const,
    onState: vi.fn(),
  };
  // What the app's server answers for a neighbours read the engine answered:
  // the engine's note on its snapshot left out, every match there.
  const answered = controller(s.page);
  vi.mocked(answered.session!.retrieve).mockResolvedValueOnce({
    ...s.page,
    completeness: "complete",
    issues: [],
    cursors: { rhythm: null },
  });
  const view = render(
    <Explorer
      {...props}
      knowledge={answered}
      selected={s.plan.id}
      request={null}
    />,
  );
  await user.click(
    within(panel()).getByRole("button", { name: "Explore more connections" }),
  );
  expect(
    await within(panel()).findByText(
      "Available neighbors within the authorized source view have been retrieved.",
    ),
  ).toBeInTheDocument();
  view.unmount();
  // And for one it couldn't read: its own note, and no continuation.
  const failed = controller(s.page);
  vi.mocked(failed.session!.retrieve).mockResolvedValueOnce({
    ...emptyGraph(),
    issues: [
      {
        provider: "rhythm",
        reason:
          "Knowledge map unavailable. Health sources refresh independently.",
      },
    ],
    cursors: {},
  });
  render(
    <Explorer
      {...props}
      knowledge={failed}
      selected={s.plan.id}
      request={null}
    />,
  );
  await user.click(
    within(panel()).getByRole("button", { name: "Explore more connections" }),
  );
  expect(
    await within(panel()).findByText(
      /A source returned incomplete or unavailable results/,
    ),
  ).toBeInTheDocument();
});

it("offers no Show on where screens are off", () => {
  const s = sky();
  show(s.page, s.plan.id, null);
  expect(
    within(panel()).queryByRole("button", { name: "Show on…" }),
  ).toBeNull();
});
