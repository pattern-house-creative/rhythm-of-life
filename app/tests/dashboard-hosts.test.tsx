import { afterEach, beforeAll, expect, it, vi } from "vitest";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import App from "../src/App";
import { dashboardCache } from "../src/dashboardCache";
import { createDemoDashboards } from "../src/demoDashboards";
import { Details } from "../src/Details";
import type { GraphSession } from "../src/liveGraph";
import { sampleHealth } from "./sampleHealth";

// A dashboard's grid in each of its hosts (docs/dev/screens.md 10.3, 10.4 and 10.9): the
// explorer's reader, the dashboard panel and full screen draw its native parts from Home's own
// data; a focused map's Show everything clears its focus in place with the note and Undo, and
// its Explore lights its stars in the explorer; Escape while a part is held only puts it back.
// Fictional data only (the demo's dashboard with a layout: the map, its page and glucose).
beforeAll(async () => {
  await import("../src/Explorer");
  await import("../src/DashboardView");
  await import("../src/DashboardCard");
  await import("../src/Details");
}, 30_000);
afterEach(() => {
  dashboardCache.clear();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  localStorage.clear();
  sessionStorage.clear();
});

// The pages' sides of their channels, as each frame loads.
const channels: MessageChannel[] = [];
const Channel = globalThis.MessageChannel;
function capture() {
  vi.stubGlobal(
    "MessageChannel",
    class extends Channel {
      constructor() {
        super();
        channels.push(this);
      }
    },
  );
}
// The pages loaded since `from` say they have drawn.
async function drawn(from: number) {
  await waitFor(() => expect(channels.length).toBeGreaterThan(from));
  await act(async () => {
    for (const c of channels.slice(from))
      c.port2.postMessage({ type: "ready" });
    await new Promise((resolve) => setTimeout(resolve, 50));
  });
}

// The demo's "Training map" in the explorer's reader.
async function reader() {
  capture();
  const user = userEvent.setup();
  render(<App />);
  await user.click(
    await screen.findByRole("button", { name: "Open Explorer" }),
  );
  const explorer = await screen.findByRole("region", {
    name: "Knowledge explorer",
  });
  within(explorer)
    .getAllByRole("button", { name: "Explore Training map" })[0]
    .focus();
  await user.keyboard("{Enter}");
  const before = channels.length;
  await user.click(
    await within(explorer).findByRole("button", { name: /Open dashboard/ }),
  );
  const opened = await screen.findByRole("region", { name: "Source reader" });
  const map = await within(opened).findByRole("group", {
    name: "Map, near Training",
  });
  return { user, opened, map, before };
}

it("draws a dashboard's native parts in the reader, and clears a map's focus in place with Undo", async () => {
  const { user, opened, map, before } = await reader();
  // The app's own parts around the page: the sky narrowed to its focus, and glucose.
  expect(within(map).getByText(/near Training/)).toBeInTheDocument();
  expect(
    within(map).getByRole("button", { name: "Explore" }),
  ).toBeInTheDocument();
  expect(
    within(opened).getByRole("group", { name: "Glucose" }),
  ).toBeInTheDocument();
  expect(
    within(opened).getByRole("group", { name: "Training map" }),
  ).toContainElement(within(opened).getByTitle("Training map dashboard"));
  // Nothing is arranged, or refocused, until the newest version's page has drawn.
  expect(
    within(map).queryByRole("button", { name: "Show everything" }),
  ).toBeNull();
  await drawn(before);
  await user.click(
    await within(map).findByRole("button", { name: "Show everything" }),
  );
  const note = (
    await screen.findByText("The map shows everything again")
  ).closest("p")!;
  await within(opened).findByRole("group", { name: "Map" });
  await user.click(within(note).getByRole("button", { name: /Undo/ }));
  await within(opened).findByRole("group", { name: "Map, near Training" });
}, 40_000);

it("lights a focused map's stars in the explorer through its Explore", async () => {
  const { user, map } = await reader();
  await user.click(within(map).getByRole("button", { name: "Explore" }));
  await waitFor(() =>
    expect(screen.queryByRole("region", { name: "Source reader" })).toBeNull(),
  );
  const explorer = screen.getByRole("region", { name: "Knowledge explorer" });
  expect(
    within(explorer).getByRole("heading", { name: "near Training" }),
  ).toBeInTheDocument();
  expect(within(explorer).getByText("FROM A MAP")).toBeInTheDocument();
  const lit = within(explorer).getByRole("list", { name: "Stars from a map" });
  expect(within(lit).getByText("Training")).toBeInTheDocument();
  expect(within(lit).getByText("Sunday long run")).toBeInTheDocument();
}, 40_000);

it("puts a held part back on Escape and leaves the reader open", async () => {
  const { opened, before } = await reader();
  await drawn(before);
  const grip = await within(opened).findByRole("button", {
    name: "Arrange map",
  });
  grip.focus();
  const user = userEvent.setup();
  await user.keyboard("{Enter}");
  expect(document.documentElement).toHaveAttribute("data-arranging");
  await user.keyboard("{Escape}");
  expect(document.documentElement).not.toHaveAttribute("data-arranging");
  expect(within(opened).getByText("Map put back")).toBeInTheDocument();
  // The explorer's own Escape stood aside: the reader is still open.
  expect(
    screen.getByRole("region", { name: "Source reader" }),
  ).toBeInTheDocument();
}, 40_000);

it("draws the grid full screen", async () => {
  const { user, opened } = await reader();
  await user.click(within(opened).getByRole("button", { name: /Full screen/ }));
  const full = await screen.findByRole("region", {
    name: "Training map, full screen",
  });
  expect(
    await within(full).findByRole("group", { name: "Map, near Training" }),
  ).toBeInTheDocument();
  expect(
    within(full).getByRole("group", { name: "Glucose" }),
  ).toBeInTheDocument();
  // A grid, so its clear layers cover only its frames (App.css), not the parts.
  expect(full.querySelector(".parts")).not.toHaveClass("plain");
}, 40_000);

it("draws the grid in the dashboard panel a corner's card grows into", async () => {
  // (The demo offers only its first two dashboards as corner cards: the panel is drawn alone.)
  const boards = createDemoDashboards();
  const maps = boards.list().find((d) => d.title === "Training map")!;
  const session = {
    dashboard: vi.fn(async () => boards.get(maps.id)!),
    dashboardData: vi.fn(async () => boards.data(maps.id)!),
  } as unknown as GraphSession;
  render(
    <Details
      view="dashboard"
      data={sampleHealth}
      state="ready"
      setState={() => {}}
      paused={false}
      setPaused={() => {}}
      close={() => {}}
      selectNode={() => {}}
      dashboard={{
        id: maps.id,
        corner: "recovery",
        title: maps.title,
        session,
        onReader: () => {},
        parts: {
          renderPart: (part) => <p>{`The ${part.kind} part`}</p>,
          starTitle: () => "Training",
        },
      }}
    />,
  );
  const panel = await screen.findByRole("dialog", { name: "Training map" });
  expect(
    await within(panel).findByRole("group", { name: "Map, near Training" }),
  ).toHaveTextContent("The map part");
  expect(
    within(panel).getByRole("group", { name: "Glucose" }),
  ).toHaveTextContent("The glucose part");
});
