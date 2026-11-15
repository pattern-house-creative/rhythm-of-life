import { act, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeAll, expect, it, vi } from "vitest";
import App from "../src/App";
import type * as Card from "../src/DashboardCard";
import type * as Demo from "../src/demoDashboards";

// A corner's dashboard card that throws while drawing (docs/dev/screens.md 10.3): it says so in
// its own place, quietly, and never changes what the household saved. Fictional dashboards only.
const broken = vi.hoisted(() => ({
  title: null as string | null,
  setCard: vi.fn(),
}));
vi.mock("../src/DashboardCard", async (original) => {
  const real = await original<typeof Card>();
  const Real = real.default;
  return {
    ...real,
    default: (props: Parameters<typeof Real>[0]) => {
      if (props.title === broken.title)
        throw new Error("The card broke while drawing");
      return <Real {...props} />;
    },
  };
});
vi.mock("../src/demoDashboards", async (original) => {
  const real = await original<typeof Demo>();
  return {
    ...real,
    createDemoDashboards: () => {
      const boards = real.createDemoDashboards();
      return {
        ...boards,
        setCard: (...args: Parameters<typeof boards.setCard>) => {
          broken.setCard(...args);
          return boards.setCard(...args);
        },
      };
    },
  };
});
beforeAll(async () => {
  await import("../src/Explorer");
  await import("../src/DashboardCard");
}, 30_000);
afterEach(() => {
  broken.title = null;
  broken.setCard.mockClear();
  localStorage.clear();
});

it("says a corner's card couldn't load in its own place, changes no saved card, and draws it again when asked", async () => {
  vi.spyOn(console, "error").mockImplementation(() => {});
  broken.title = "Week at a glance";
  render(<App />);
  expect(
    await screen.findByText("This card couldn’t load."),
  ).toBeInTheDocument();
  // No view over Home, and nothing that would take the card away for everyone.
  expect(screen.queryByText("This view could not be opened")).toBeNull();
  expect(
    screen.queryByRole("button", { name: "Return to ambient" }),
  ).toBeNull();
  // The other corner's card goes on drawing.
  expect(
    await screen.findByTitle("Sample training week card"),
  ).toBeInTheDocument();
  expect(broken.setCard).not.toHaveBeenCalled();
  // Fixed (a new version, say): asked again, it draws.
  broken.title = null;
  await userEvent.click(screen.getByRole("button", { name: "Try again" }));
  expect(await screen.findByTitle("Week at a glance card")).toBeInTheDocument();
  expect(screen.queryByText("This card couldn’t load.")).toBeNull();
  expect(broken.setCard).not.toHaveBeenCalled();
});

it("draws a card that couldn't load again at the next read of the dashboards", async () => {
  vi.spyOn(console, "error").mockImplementation(() => {});
  broken.title = "Week at a glance";
  render(<App />);
  await screen.findByText("This card couldn’t load.");
  broken.title = null;
  // The dashboards are read again (every 15 minutes, or here on coming back).
  act(() => window.dispatchEvent(new Event("pageshow")));
  expect(await screen.findByTitle("Week at a glance card")).toBeInTheDocument();
  expect(screen.queryByText("This card couldn’t load.")).toBeNull();
});
