import { afterEach, expect, it, vi } from "vitest";
import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import App, { idleReturnMs } from "../src/App";
import { createDemoDashboards } from "../src/demoDashboards";
import { createDemoScreens } from "../src/demoScreens";

// A dashboard view that can't open (its code didn't load, or it broke while drawing).
vi.mock("../src/DashboardView", () => ({
  default: () => {
    throw new Error("This part of the app didn't load.");
  },
}));
const RealChannel = globalThis.BroadcastChannel;
afterEach(() => {
  vi.useRealTimers();
  localStorage.clear();
  sessionStorage.clear();
});
const full = () => screen.queryByRole("region", { name: /, full screen$/ });

it("leaves a Home dashboard that can't open for the sky by itself, and doesn't go back to it", async () => {
  const [board] = createDemoDashboards().list();
  localStorage.setItem(
    "rhythm-of-life.home",
    JSON.stringify({ dashboard: board.id }),
  );
  // React reports the error it caught.
  vi.spyOn(console, "error").mockImplementation(() => {});
  const user = userEvent.setup();
  render(<App />);
  await screen.findByRole("region", { name: "Knowledge constellation" });
  // The app opens at its Home, which can't be shown: the sky shows instead, with no alert to
  // dismiss, so an unattended screen never stays on one.
  await waitFor(() =>
    expect(document.querySelector(".main-content")).not.toHaveAttribute(
      "inert",
    ),
  );
  expect(full()).toBeNull();
  expect(screen.queryByRole("alert")).toBeNull();
  // The idle minutes don't take it back to a Home that can't open.
  vi.useFakeTimers();
  act(() => {
    vi.advanceTimersByTime(idleReturnMs * 2);
  });
  vi.useRealTimers();
  expect(full()).toBeNull();
  expect(screen.queryByRole("alert")).toBeNull();
  // Details says why, and the Home can be restored.
  await user.click(
    screen.getByRole("button", { name: "Sources and settings" }),
  );
  expect(
    await screen.findByText(
      "This screen's Home dashboard couldn't be opened here, so it shows the sky and tiles until the app is reloaded.",
    ),
  ).toBeInTheDocument();
  await user.click(
    await screen.findByRole("button", { name: "Restore default Home" }),
  );
  expect(localStorage.getItem("rhythm-of-life.home")).toBeNull();
}, 40_000);

it("gives way to what is under a full screen another screen asked for, when it can't open", async () => {
  vi.stubGlobal("BroadcastChannel", RealChannel);
  vi.spyOn(console, "error").mockImplementation(() => {});
  const [board] = createDemoDashboards().list();
  render(<App />);
  await screen.findByRole("region", { name: "Knowledge constellation" });
  const kitchen = createDemoScreens()!;
  const kitchenId = "kitchenScreen0000000001";
  const wait = new AbortController();
  void kitchen
    .screenListen(
      { screen: kitchenId, name: "Kitchen iPad", hold: 25 },
      wait.signal,
    )
    .catch(() => null);
  try {
    const app = await waitFor(async () => {
      const other = (await kitchen.screenList(kitchenId)).find((s) => !s.self);
      expect(other).toBeDefined();
      return other!;
    });
    const sent = await kitchen.screenSend({
      screen: kitchenId,
      to: app.id,
      action: "full_screen",
      args: { dashboard: board.id },
    });
    expect(sent.ok).toBe(true);
    // It gave way to the sky it was over; nothing else waits on it.
    await waitFor(async () =>
      expect(
        (await kitchen.screenList(kitchenId)).find((s) => s.id === app.id)!
          .showing,
      ).toMatchObject({ view: "home" }),
    );
    expect(full()).toBeNull();
    expect(screen.queryByRole("alert")).toBeNull();
    expect(document.querySelector(".main-content")).not.toHaveAttribute(
      "inert",
    );
  } finally {
    wait.abort();
    kitchen.screenStop();
  }
}, 40_000);
