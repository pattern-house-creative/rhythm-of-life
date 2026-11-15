import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import App from "../src/App";
import { handleObservatory } from "../server/routes";
import { handleGraph } from "../server/graph";
import { unavailableHealth } from "../server/healthState";
import { connectHints } from "../src/health";
import { engineUrl, withEngine } from "./engine";
import { record } from "./synthetic";

afterEach(() => vi.unstubAllEnvs());

// API-45 (SB-P7-09): the calendar is off and connected to nothing until its person enters their own private ICS link
// (docs/adapters/calendar.md), so Sources & settings lists it beside the other sources that need an outside account,
// as not connected, with the command that connects it, and the home screen shows nothing of it.
it("lists the calendar in Sources & settings as not connected, waiting for a link only its person enters", async () => {
  withEngine({ tools: ["app_graph", "app_insights", "app_dashboards"] });
  vi.mocked(fetch).mockImplementation(async (url, init) => {
    const path = String(url);
    if (path.startsWith(engineUrl)) throw new Error("unreachable");
    const request = new Request(new URL(path, "http://localhost"), init);
    if (path.startsWith("/api/observatory")) return handleObservatory(request);
    if (path === "/api/graph") return handleGraph(request);
    throw new Error("Unexpected request");
  });
  render(<App />);
  const user = userEvent.setup();
  const hint = await screen.findByText(/A dashboard can live here/);
  expect(screen.queryByText("Calendar")).toBeNull();
  await user.click(
    within(hint).getByRole("button", { name: "Sources & settings" }),
  );
  const sources = await screen.findByRole("dialog", {
    name: "Sources & settings",
  });
  const row = within(sources).getByText("Calendar").closest("div")!;
  expect(row).toHaveTextContent("NOT CONNECTED");
  expect(row).toHaveTextContent("./rhythm connect calendar");
  expect(row).toHaveTextContent("a private ICS link you enter yourself");
  expect(row).not.toHaveTextContent("AUTHORIZED");
});

// Once the calendar's events are on the map (its person entered a link, a rebuild read it), the row reads connected and
// available, as Drive's and Second Brain's do (App.tsx: a source with records on the map), and so the home screen's
// source strip names it too. The health answer still says disconnected: the map is what turns it on.
it("reads the calendar connected once its events are on the map, in Sources & settings and on the home strip", async () => {
  withEngine({ tools: ["app_graph", "app_insights", "app_dashboards"] });
  const event = record("fx-calendar-event-1", "calendar");
  event.type = "record";
  event.title = "Fictional dentist appointment";
  event.provenance.demo = false;
  event.resource.sourceModifiedAt = "2026-11-08T09:00:00Z";
  const page = {
    nodes: [event],
    edges: [],
    issues: [],
    nextCursor: null,
    cursors: {},
    completeness: "complete",
  };
  vi.mocked(fetch).mockImplementation(async (url, init) => {
    const path = String(url);
    if (path.startsWith(engineUrl)) throw new Error("unreachable");
    const request = new Request(new URL(path, "http://localhost"), init);
    if (path.startsWith("/api/observatory")) return handleObservatory(request);
    if (path === "/api/graph")
      return new Response(JSON.stringify(page), {
        headers: { "content-type": "application/json" },
      });
    throw new Error("Unexpected request");
  });
  render(<App />);
  const user = userEvent.setup();
  const strip = await screen.findByLabelText("Source observation timestamps");
  await waitFor(() => expect(strip).toHaveTextContent("Calendar"));
  // The strip's own entry for it: its name, the newest event's time, and its state.
  const entry = [...strip.children].find((s) =>
    s.textContent?.startsWith("Calendar "),
  )!;
  expect(entry.querySelector("time")).toHaveAttribute(
    "datetime",
    "2026-11-08T09:00:00Z",
  );
  expect(entry.lastElementChild).toHaveTextContent(/^AVAILABLE$/);
  await user.click(
    screen.getByRole("button", { name: "Sources and settings" }),
  );
  const sources = await screen.findByRole("dialog", {
    name: "Sources & settings",
  });
  const row = within(sources).getByText("Calendar").closest("div")!;
  expect(row).not.toHaveTextContent("NOT CONNECTED");
  expect(row).not.toHaveTextContent("./rhythm connect calendar");
  expect(row).not.toHaveTextContent(/unavailable/i);
  expect(row).toHaveTextContent(/available/i);
});

it("starts every live source list with the calendar off", () => {
  expect(unavailableHealth().sources.find((s) => s.id === "calendar")).toEqual({
    id: "calendar",
    name: "Calendar",
    connected: false,
    sampleAt: null,
    status: "disconnected",
  });
  expect(connectHints.calendar.command).toBe("./rhythm connect calendar");
});
