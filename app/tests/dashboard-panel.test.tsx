import { afterEach, beforeAll, expect, it, vi } from "vitest";
import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import App from "../src/App";
import DashboardView from "../src/DashboardView";
import { dashboardCache } from "../src/dashboardCache";
import { createDemoDashboards } from "../src/demoDashboards";
import { awakeMs, framedMs } from "../src/FullScreen";

// The explorer, the settings panel and the dashboard view are code-split; load them once with their own budget.
beforeAll(async () => {
  await import("../src/Explorer");
  await import("../src/DashboardView");
  await import("../src/DashboardCard");
}, 30_000);
afterEach(() => {
  dashboardCache.clear();
  vi.useRealTimers();
  vi.restoreAllMocks();
  localStorage.clear();
});

const boards = createDemoDashboards();
const [summary] = boards.list();

it("keeps the last version and data read in memory until the tab is hidden", () => {
  const dashboard = boards.get(summary.id)!;
  const data = boards.data(summary.id)!;
  dashboardCache.keepVersion(dashboard);
  dashboardCache.keepData(data);
  expect(dashboardCache.version(summary.id)).toBe(dashboard);
  expect(dashboardCache.version(summary.id, 2)).toBe(dashboard);
  expect(dashboardCache.data(summary.id, 2)).toBe(data);
  const hidden = vi.spyOn(document, "hidden", "get").mockReturnValue(true);
  document.dispatchEvent(new Event("visibilitychange"));
  hidden.mockRestore();
  expect(dashboardCache.version(summary.id)).toBeUndefined();
  expect(dashboardCache.data(summary.id, 2)).toBeUndefined();
  dashboardCache.keepData(data);
  window.dispatchEvent(new Event("observatory-access-lost"));
  expect(dashboardCache.data(summary.id, 2)).toBeUndefined();
});

it("reopens a dashboard from what was read last, while it is read again", async () => {
  const dashboard = boards.get(summary.id)!;
  const data = boards.data(summary.id)!;
  dashboardCache.keepVersion(dashboard);
  dashboardCache.keepData(data);
  // The engine is slow this time; the last read shows meanwhile.
  let finish: (d: typeof data) => void = () => {};
  const session = {
    dashboard: vi.fn(async () => dashboard),
    dashboardData: vi.fn(
      () => new Promise<typeof data>((resolve) => (finish = resolve)),
    ),
  };
  render(
    <DashboardView id={summary.id} session={session} onOpenStar={() => {}} />,
  );
  // The version opens at once, without waiting for the engine.
  expect(
    await screen.findByTitle("Sample training week dashboard", undefined, {
      timeout: 1000,
    }),
  ).toBeInTheDocument();
  expect(
    screen.getByText(/Data read .+ feeds\. Read again/),
  ).toBeInTheDocument();
  expect(session.dashboardData).toHaveBeenCalled();
  finish(data);
});

it("grows a corner's dashboard card into a panel like a stat card, with a way to the reader", async () => {
  const user = userEvent.setup();
  render(<App />);
  await screen.findByRole("region", { name: "Knowledge constellation" });
  // Choose the sample dashboard for the Recovery corner.
  await user.click(
    screen.getByRole("button", { name: "Sources and settings" }),
  );
  await user.selectOptions(
    await screen.findByRole("combobox", { name: "Recovery corner" }),
    "Sample training week",
  );
  await user.keyboard("{Escape}");
  // Tapping the card opens its dashboard in a panel, not the explorer.
  await user.click(
    await screen.findByRole("button", {
      name: "Open the Sample training week dashboard",
    }),
  );
  const panel = await screen.findByRole("dialog", {
    name: "Sample training week",
  });
  expect(
    within(panel).getByRole("heading", { name: "Sample training week" }),
  ).toBeInTheDocument();
  expect(
    await within(panel).findByTitle("Sample training week dashboard"),
  ).toBeInTheDocument();
  expect(
    screen.queryByRole("region", { name: "Knowledge explorer" }),
  ).toBeNull();
  // It opens in the explorer's reader too, beside its stars.
  await user.click(
    within(panel).getByRole("button", { name: /Open in the explorer/ }),
  );
  const reader = await screen.findByRole("region", { name: "Source reader" });
  expect(
    await within(reader).findByTitle("Sample training week dashboard"),
  ).toBeInTheDocument();
  await waitFor(() =>
    expect(
      screen.queryByRole("dialog", { name: "Sample training week" }),
    ).toBeNull(),
  );
}, 40_000);

// The panel's dashboard, grown out of the Recovery corner's card.
async function panel() {
  const user = userEvent.setup();
  render(<App />);
  await user.click(
    await screen.findByRole("button", {
      name: "Open the Sample training week dashboard",
    }),
  );
  return {
    user,
    panel: await screen.findByRole("dialog", { name: "Sample training week" }),
  };
}
const full = () =>
  screen.queryByRole("region", { name: "Sample training week, full screen" });

it("fills the display with a panel's dashboard until someone exits, back to the panel", async () => {
  const { user, panel: opened } = await panel();
  await user.click(within(opened).getByRole("button", { name: /Full screen/ }));
  const shown = await screen.findByRole("region", {
    name: "Sample training week, full screen",
  });
  // Only the page, in the same sealed frame, and the exit control.
  const frame = await within(shown).findByTitle(
    "Sample training week dashboard",
  );
  expect(frame).toHaveAttribute("sandbox", "allow-scripts allow-forms");
  expect(within(shown).queryByRole("button", { name: /Version 2/ })).toBeNull();
  // What it covers can't be reached.
  expect(opened.closest("[inert]")).not.toBeNull();
  expect(document.querySelector(".main-content")).toHaveAttribute("inert");
  // Escape exits, back to the panel it came from.
  await user.keyboard("{Escape}");
  await waitFor(() => expect(full()).toBeNull());
  expect(
    screen.getByRole("dialog", { name: "Sample training week" }),
  ).toBeInTheDocument();
  expect(opened.closest("[inert]")).toBeNull();
}, 40_000);

it("shows the exit control when someone moves or touches, and hides it again", async () => {
  const { user, panel: opened } = await panel();
  await user.click(within(opened).getByRole("button", { name: /Full screen/ }));
  const shown = await screen.findByRole("region", {
    name: "Sample training week, full screen",
  });
  vi.useFakeTimers();
  act(() => {
    vi.advanceTimersByTime(awakeMs + 50);
  });
  // Hidden, a clear layer over the page takes the first movement.
  expect(shown).not.toHaveClass("awake");
  const wake = shown.querySelector(".full-screen-wake")!;
  expect(wake).not.toBeNull();
  fireEvent.pointerMove(wake);
  expect(shown).toHaveClass("awake");
  // Shown, the page can be used.
  expect(shown.querySelector(".full-screen-wake")).toBeNull();
  act(() => {
    vi.advanceTimersByTime(awakeMs + 50);
  });
  expect(shown).not.toHaveClass("awake");
  // A touch, or a tap into the page (focus leaves the app for its frame), shows it too.
  fireEvent.touchStart(shown.querySelector(".full-screen-wake")!);
  expect(shown).toHaveClass("awake");
  act(() => {
    vi.advanceTimersByTime(awakeMs + 50);
  });
  fireEvent.blur(window);
  expect(shown).toHaveClass("awake");
  fireEvent.click(
    within(shown).getByRole("button", { name: "Exit full screen" }),
  );
  expect(full()).toBeNull();
}, 40_000);

it("lets a scroll show the exit control, and keeps it while the page itself has focus", async () => {
  const { user, panel: opened } = await panel();
  await user.click(within(opened).getByRole("button", { name: /Full screen/ }));
  const shown = await screen.findByRole("region", {
    name: "Sample training week, full screen",
  });
  const frame = await within(shown).findByTitle(
    "Sample training week dashboard",
  );
  vi.useFakeTimers();
  act(() => {
    vi.advanceTimersByTime(awakeMs + 50);
  });
  fireEvent.wheel(shown.querySelector(".full-screen-wake")!);
  expect(shown).toHaveClass("awake");
  // A tap into the page moves focus into its frame, and the app hears nothing more from
  // there: the layer keeps away, so it never takes the next tap, and the control stays.
  act(() => {
    frame.focus();
    fireEvent.blur(window);
  });
  expect(document.activeElement).toBe(frame);
  act(() => {
    vi.advanceTimersByTime(awakeMs + 50);
  });
  expect(shown).toHaveClass("awake");
  expect(shown.querySelector(".full-screen-wake")).toBeNull();
  act(() => {
    vi.advanceTimersByTime(framedMs);
  });
  expect(shown).not.toHaveClass("awake");
}, 40_000);

it("steps back with Back from a full screen to the panel under it, then Home", async () => {
  // An entry for the panel and one for the full screen over it, each opened by a tap.
  const push = vi.spyOn(history, "pushState");
  const { user, panel: opened } = await panel();
  await user.click(
    within(opened).getByRole("button", { name: /Set as Home here/ }),
  );
  await user.click(within(opened).getByRole("button", { name: /Full screen/ }));
  await screen.findByRole("region", {
    name: "Sample training week, full screen",
  });
  expect(push).toHaveBeenCalledTimes(2);
  act(() => history.back());
  await waitFor(() => expect(full()).toBeNull());
  expect(
    screen.getByRole("dialog", { name: "Sample training week" }),
  ).toBeInTheDocument();
  // Back closes the panel in turn and goes Home: its dashboard, full screen,
  // which is Home itself and, like the sky, adds no entry.
  act(() => history.back());
  await screen.findByRole("region", {
    name: "Sample training week, full screen",
  });
  expect(push).toHaveBeenCalledTimes(2);
  // Its exit control shows the sky.
  await user.click(screen.getByRole("button", { name: "Exit full screen" }));
  expect(full()).toBeNull();
  expect(document.querySelector(".main-content")).not.toHaveAttribute("inert");
  expect(push).toHaveBeenCalledTimes(2);
}, 40_000);

it("makes a panel's dashboard this screen's Home, shown whenever it goes Home", async () => {
  const { user, panel: opened } = await panel();
  await user.click(
    within(opened).getByRole("button", { name: /Set as Home here/ }),
  );
  expect(within(opened).getByText("This screen's Home")).toBeInTheDocument();
  expect(JSON.parse(localStorage.getItem("rhythm-of-life.home")!)).toEqual({
    dashboard: summary.id,
  });
  // Closing the panel goes Home: the dashboard, full screen.
  await user.click(within(opened).getByRole("button", { name: /^Close/ }));
  expect(
    await screen.findByRole("region", {
      name: "Sample training week, full screen",
    }),
  ).toBeInTheDocument();
  // Exiting shows the sky; Details says what Home is and restores the default.
  await user.click(screen.getByRole("button", { name: "Exit full screen" }));
  await user.click(
    await screen.findByRole("button", { name: "Sources and settings" }),
  );
  expect(
    await screen.findByText(
      "This screen's Home is Sample training week, full screen: it shows each time this screen goes Home.",
    ),
  ).toBeInTheDocument();
  await user.click(
    screen.getByRole("button", { name: "Restore default Home" }),
  );
  expect(localStorage.getItem("rhythm-of-life.home")).toBeNull();
  await user.keyboard("{Escape}");
  await waitFor(() =>
    expect(
      screen.queryByRole("dialog", { name: "Sources & settings" }),
    ).toBeNull(),
  );
  expect(full()).toBeNull();
}, 40_000);

it("offers full screen and Home in the reader too", async () => {
  const { user, panel: opened } = await panel();
  await user.click(
    within(opened).getByRole("button", { name: /Open in the explorer/ }),
  );
  const reader = await screen.findByRole("region", { name: "Source reader" });
  await user.click(
    within(reader).getByRole("button", { name: /Set as Home here/ }),
  );
  expect(within(reader).getByText("This screen's Home")).toBeInTheDocument();
  await user.click(within(reader).getByRole("button", { name: /Full screen/ }));
  await screen.findByRole("region", {
    name: "Sample training week, full screen",
  });
  // Escape leaves the full screen for the reader, not the explorer for Home.
  await user.keyboard("{Escape}");
  await waitFor(() => expect(full()).toBeNull());
  expect(
    screen.getByRole("region", { name: "Source reader" }),
  ).toBeInTheDocument();
}, 40_000);
