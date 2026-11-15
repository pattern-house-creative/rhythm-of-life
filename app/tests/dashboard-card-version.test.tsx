import { act, render, screen } from "@testing-library/react";
import { afterEach, beforeAll, expect, it, vi } from "vitest";
import App from "../src/App";
import type * as Demo from "../src/demoDashboards";

// The demo's week at a glance, with a second version an AI client saves while
// the home screen is open.
const saved = vi.hoisted(() => ({ second: false }));
vi.mock("../src/demoDashboards", async (original) => {
  const real = await original<typeof Demo>();
  return {
    ...real,
    createDemoDashboards: () => {
      const boards = real.createDemoDashboards();
      const newer = <
        T extends { title: string; version: number; latest: number },
      >(
        d: T,
      ): T =>
        saved.second && d.title === "Week at a glance"
          ? { ...d, title: "Week at a glance, revised", version: 2, latest: 2 }
          : d;
      return {
        ...boards,
        list: () => boards.list().map(newer),
        // Version 2 of the week at a glance draws as version 1 does.
        get: (id: string, version?: number) => {
          const d = boards.get(id, version) ?? boards.get(id, 1);
          return d && newer(d);
        },
        data: (id: string, version?: number) =>
          boards.data(id, version) ?? boards.data(id, 1),
      };
    },
  };
});
// The explorer and the dashboard card are code-split; load them once with their own budget, so a slow machine
// doesn't finish this test while one is still loading.
beforeAll(async () => {
  await import("../src/Explorer");
  await import("../src/DashboardCard");
}, 30_000);
afterEach(() => {
  saved.second = false;
  localStorage.clear();
});

it("draws a corner's card again when its dashboard gets a newer version", async () => {
  render(<App />);
  await screen.findByTitle("Week at a glance card");
  saved.second = true;
  // The dashboards are read again (every 15 minutes, or here on coming back).
  act(() => window.dispatchEvent(new Event("pageshow")));
  expect(
    await screen.findByTitle("Week at a glance, revised card"),
  ).toBeInTheDocument();
  expect(screen.queryByTitle("Week at a glance card")).toBeNull();
});
