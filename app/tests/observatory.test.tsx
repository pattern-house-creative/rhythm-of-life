import { beforeAll, describe, it, expect, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import App from "../src/App";
import { demoData } from "../src/demo";
import { sampleHealth, withSampleHealth } from "./sampleHealth";
import {
  filterNodes,
  sleepDuration,
  sampleChange,
  weeklyWorkouts,
} from "../src/model";
import { createGraph, containGraph, insideSky, skyBounds } from "../src/graph";

// A household with a fictional glucose source connected, its 90-day archive,
// and the glucose tile kept in the main slot: the demo itself connects none.
vi.mock("../src/demoReadings", async (original) => {
  const real = await original<typeof import("../src/demoReadings")>();
  const { sampleShapes } = await import("./sampleShapes");
  return {
    ...real,
    demoReadings: (id: string, today?: Date) =>
      real.readingsFrom(sampleShapes, id, today) ?? real.demoReadings(id, today),
  };
});
vi.mock("../src/demoDashboards", async (original) => {
  const real = await original<typeof import("../src/demoDashboards")>();
  return {
    ...real,
    createDemoDashboards: () => {
      const boards = real.createDemoDashboards();
      boards.setCard("main", null);
      return boards;
    },
  };
});
describe("demo integrity", () => {
  it("has explicit provenance and only valid, explained edges", () => {
    const ids = new Set(demoData.nodes.map((n) => n.id));
    expect(ids.size).toBe(demoData.nodes.length);
    for (const e of demoData.edges) {
      expect(ids.has(e.source) && ids.has(e.target)).toBe(true);
      expect(e.relationship.length).toBeGreaterThan(10);
    }
    for (const n of demoData.nodes) {
      expect(n.provenance.demo).toBe(true);
      expect(n.provenance.recordId).toMatch(/^demo:/);
    }
    expect(demoData.sources.every((s) => !s.connected)).toBe(true);
    // Everyday wellbeing: no glucose source and no Garmin, which the sources
    // panel lists as not connected, and nothing about diabetes care.
    expect(
      demoData.sources
        .filter((s) => s.status === "disconnected")
        .map((s) => s.id),
    ).toEqual(["glucose", "garmin"]);
    expect(demoData.glucose.samples).toEqual([]);
    expect(demoData.workouts).toEqual([]);
    for (const n of demoData.nodes)
      expect(`${n.title} ${n.overview}`).not.toMatch(
        /glucose|cgm|insulin|pump|diabet|time in range|gmi/i,
      );
    expect(
      sampleHealth.workouts.reduce((sum, w) => sum + w.durationMinutes, 0),
    ).toBe(134);
  });
  it("combines search, source, and type filters", () => {
    expect(
      filterNodes(demoData.nodes, "sleep", "drive", "note").map((n) => n.id),
    ).toEqual(["sleep"]);
    expect(
      filterNodes(demoData.nodes, "sleep", "demo", "all").map((n) => n.id),
    ).toEqual(["recovery"]);
  });
  it("keeps missing metrics distinct from zero and derives recorded weekly totals", () => {
    expect(sleepDuration(null)).toBe("Unavailable");
    expect(sleepDuration(0)).toBe("0h 0m");
    expect(sampleChange(sampleHealth.glucose)?.delta).toBe(-14);
    expect(sampleChange({ ...sampleHealth.glucose, samples: [] })).toBeNull();
    const previous = {
      ...sampleHealth.workouts[0],
      startedAt: "2026-10-11T09:00:00Z",
      durationMinutes: 200,
    };
    const next = { ...previous, startedAt: "2026-10-19T09:00:00Z" };
    const week = weeklyWorkouts(
      [...sampleHealth.workouts, previous, next],
      sampleHealth.current.provenance.observedAt,
    );
    expect(week.records).toHaveLength(3);
    expect(week.minutes).toBe(134);
    expect(week.days).toEqual([0, 0, 34, 0, 48, 0, 52]);
  });
  it("settles the graph within padded bounds without mutating source data", () => {
    const before = JSON.stringify({
      nodes: demoData.nodes,
      edges: demoData.edges,
    });
    for (const height of [600, 820, 1250]) {
      const graph = createGraph(demoData.nodes, demoData.edges, height);
      graph.simulation.tick(200);
      containGraph(graph.points, height);
      for (const n of graph.points) {
        expect(Number.isFinite(n.x) && Number.isFinite(n.y)).toBe(true);
        // Inside the sky's rounded box, which sits within its margins.
        const inside = insideSky(n.x, n.y, height);
        expect(inside.x).toBeCloseTo(n.x, 6);
        expect(inside.y).toBeCloseTo(n.y, 6);
        const area = skyBounds(height);
        expect(n.x).toBeGreaterThanOrEqual(area.left);
        expect(n.x).toBeLessThanOrEqual(area.right);
        expect(n.y).toBeGreaterThanOrEqual(area.top);
        expect(n.y).toBeLessThanOrEqual(area.bottom);
      }
      graph.simulation.stop();
    }
    expect(
      JSON.stringify({ nodes: demoData.nodes, edges: demoData.edges }),
    ).toBe(before);
    const filtered = createGraph(
      demoData.nodes.filter((n) => n.id === "sleep"),
      demoData.edges,
      820,
    );
    expect(() => filtered.simulation.tick(50)).not.toThrow();
    filtered.simulation.stop();
  });
});
describe("primary journeys", () => {
  // The detail dialog is code-split. Load its chunk once, with its own budget, so
  // the journeys' UI waits measure rendering rather than a cold module transform.
  // A broken chunk now fails here with its own error instead of a missing dialog.
  beforeAll(async () => {
    await import("../src/Details");
  }, 30_000);
  it("opens glucose, switches segments, and follows an explicit graph link", async () => {
    // A connected glucose source (fictional); the demo connects none.
    withSampleHealth();
    const user = userEvent.setup();
    render(<App />);
    await screen.findByRole("region", { name: "Knowledge constellation" });
    await user.click(screen.getByRole("button", { name: /Glucose 112/ }));
    // The full suite runs files in parallel; allow a loaded runner to finish
    // rendering the dashboard before the dialog appears.
    const dialog = await screen.findByRole(
      "dialog",
      { name: "glucose" },
      { timeout: 10000 },
    );
    expect(
      within(dialog).getByRole("heading", { name: "Glucose", exact: true }),
    ).toBeInTheDocument();
    expect(
      within(dialog).getByRole("region", {
        name: "Glucose management indicator",
      }),
    ).toBeInTheDocument();
    expect(
      within(dialog).getByRole("region", { name: "Time in ranges" }),
    ).toBeInTheDocument();
    expect(within(dialog).getByText(/GMI is not lab A1C/)).toBeInTheDocument();
    await user.click(within(dialog).getByRole("button", { name: "Overnight" }));
    expect(
      within(dialog).getByRole("button", { name: "Overnight" }),
    ).toHaveAttribute("aria-pressed", "true");
    await user.click(
      within(dialog).getByRole("button", { name: /Linked sample activity/ }),
    );
    expect(
      await screen.findByRole("complementary", {
        name: "Selected knowledge details",
      }),
    ).toHaveTextContent("Sunday long run");
  }, 20_000);
  it("searches knowledge and shows honest empty results", async () => {
    const user = userEvent.setup();
    render(<App />);
    await screen.findByRole("region", { name: "Knowledge constellation" });
    await user.click(
      screen.getByRole("button", { name: "Open Explorer" }),
    );
    await user.type(
      await screen.findByRole("textbox", { name: "Search knowledge" }),
      "zzzz",
    );
    expect(screen.getByText("No stars match your search")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Reset filters" }));
    expect(
      screen.queryByText("No stars match your search"),
    ).not.toBeInTheDocument();
  });
  // The demo's sources read as a working sample, not a failed connection: its own provider is
  // "Sample data", each sample says no account is connected, and a source with no time says so once.
  it("names the demo's sources as a sample with no account connected", async () => {
    const user = userEvent.setup();
    render(<App />);
    await screen.findByRole("region", { name: "Knowledge constellation" });
    const strip = screen.getByLabelText("Fictional per-source timestamps");
    expect(strip).toHaveTextContent("Sample data");
    expect(strip).toHaveTextContent("SAMPLE");
    expect(strip).not.toHaveTextContent("DEMO");
    await user.click(
      screen.getByRole("button", { name: "Sources and settings" }),
    );
    const sources = await screen.findByRole("dialog", {
      name: "Sources & settings",
    });
    const row = within(sources).getByText("Sample data").closest("div")!;
    expect(row).toHaveTextContent("SAMPLE · NO ACCOUNT CONNECTED");
    expect(row).toHaveTextContent(/Observed: /);
    expect(sources).not.toHaveTextContent(/DISCONNECTED|AUTHORIZED/);
    const garmin = within(sources).getByText("Garmin").closest("div")!;
    expect(garmin).toHaveTextContent("NOT CONNECTED");
    expect(garmin).toHaveTextContent("Observation time unavailable");
    expect(garmin).not.toHaveTextContent("Observed:");
  });
  it("previews stale and error states, then recovers", async () => {
    withSampleHealth();
    const user = userEvent.setup();
    render(<App />);
    await screen.findByRole("region", { name: "Knowledge constellation" });
    await user.click(
      screen.getByRole("button", { name: "Sources and settings" }),
    );
    await user.selectOptions(
      await screen.findByRole("combobox", { name: "Preview data state" }),
      "stale",
    );
    await user.click(screen.getByRole("button", { name: "Close sources" }));
    expect(screen.getByText("Reading is stale")).toBeInTheDocument();
    await user.click(
      screen.getByRole("button", { name: "Sources and settings" }),
    );
    await user.selectOptions(
      await screen.findByRole("combobox", { name: "Preview data state" }),
      "error",
    );
    await user.click(screen.getByRole("button", { name: "Close sources" }));
    expect(screen.getByText("Source unavailable")).toBeInTheDocument();
    expect(screen.getByText("Sample records couldn’t load")).toBeInTheDocument();
    expect(screen.getByText("No stars in this preview")).toBeInTheDocument();
    expect(screen.getByText("0 stars shown")).toBeInTheDocument();
    expect(screen.queryByText("Select a star to explore")).toBeNull();
    await user.click(screen.getByRole("button", { name: "Retry demo" }));
    expect(screen.getByText("Demo reading")).toBeInTheDocument();
  });
});
