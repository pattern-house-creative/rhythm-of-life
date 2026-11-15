import { beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import App from "../src/App";
import { TrendArrows } from "../src/TrendArrows";
import type { GlucoseTrend } from "../src/trend";
import { withSampleHealth } from "./sampleHealth";

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

const trend = (arrows: 0 | 1 | 2 | 3, rate: number): GlucoseTrend => ({
  arrows,
  direction: arrows ? (rate > 0 ? "up" : "down") : null,
  rate,
  delta: rate * 20,
  minutes: 20,
  at: "2026-10-18T22:00:00Z",
  from: "2026-10-18T21:40:00Z",
});

describe("trend arrows", () => {
  it("stacks one to three slim chevrons and draws nothing while steady", () => {
    const { container, rerender } = render(
      <TrendArrows trend={trend(3, -3.4)} />,
    );
    expect(container.querySelectorAll("polyline")).toHaveLength(3);
    expect(
      screen.getByRole("img", {
        name: "Falling very quickly: 3 down arrows, 3.4 mg/dL per minute",
      }),
    ).toBeInTheDocument();
    rerender(<TrendArrows trend={trend(1, 1.2)} />);
    expect(container.querySelectorAll("polyline")).toHaveLength(1);
    expect(screen.getByRole("img")).toHaveAccessibleName(
      "Rising: 1 up arrow, 1.2 mg/dL per minute",
    );
    rerender(<TrendArrows trend={trend(0, 0.4)} />);
    expect(container).toBeEmptyDOMElement();
    rerender(<TrendArrows trend={null} />);
    expect(container).toBeEmptyDOMElement();
  });
});

describe("glucose tile and panel", () => {
  beforeAll(async () => {
    await import("../src/Details");
  }, 30_000);
  beforeEach(withSampleHealth);

  it("shows the ring and the 90-day GMI on the tile", async () => {
    render(<App />);
    const tile = await screen.findByRole("button", { name: /GLUCOSE 112/ });
    expect(within(tile).getByText("Time in range")).toBeInTheDocument();
    expect(
      await within(tile).findByText("90-day estimate"),
    ).toBeInTheDocument();
    // The demo evening is steady: no arrow, and the line says so.
    expect(within(tile).getByText(/^Steady · /)).toBeInTheDocument();
  });

  it("leads with the reading and the day, then the longer view", async () => {
    const user = userEvent.setup();
    render(<App />);
    await screen.findByRole("region", { name: "Knowledge constellation" });
    await user.click(screen.getByRole("button", { name: /GLUCOSE 112/ }));
    const dialog = await screen.findByRole(
      "dialog",
      { name: "glucose" },
      { timeout: 10000 },
    );
    const now = within(dialog).getByRole("region", { name: "Current glucose" });
    expect(now).toHaveTextContent("112");
    expect(now).toHaveTextContent(
      "Arrows show the 20-minute rate of change",
    );
    expect(dialog).not.toHaveTextContent("not a device trend arrow");
    const segments = within(dialog).getByRole("group", {
      name: "Chart time segment",
    });
    const ranges = within(dialog).getByRole("region", {
      name: "Time in ranges",
    });
    // The 24-hour switch sits above the ring, right under the reading.
    expect(
      now.compareDocumentPosition(segments) & Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
    expect(
      segments.compareDocumentPosition(ranges) &
        Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
    const ring = within(ranges).getByRole("img");
    expect(ring.getAttribute("aria-label")).toMatch(
      /^Last 14 days: very high above 250 .*in range 70–180 8\d\.\d%.*tight range 70–140/,
    );
    expect(within(ranges).getByText("Variability")).toBeInTheDocument();
    expect(within(ranges).getByText("Risk index")).toBeInTheDocument();

    const gmi = within(dialog).getByRole("region", {
      name: "Glucose management indicator",
    });
    expect(
      await within(gmi).findByText(/^90-day estimate/),
    ).toBeInTheDocument();
    expect(
      within(gmi).getByText(/of 90 days · \d+% of readings/),
    ).toBeInTheDocument();
    await user.click(within(gmi).getByRole("button", { name: "14 days" }));
    expect(
      within(gmi).getByRole("button", { name: "14 days" }),
    ).toHaveAttribute("aria-pressed", "true");
    expect(within(gmi).getByText(/^14-day estimate/)).toBeInTheDocument();

    expect(
      within(dialog).getByRole("region", { name: "Typical day" }),
    ).toBeInTheDocument();
    const lows = within(dialog).getByRole("region", {
      name: "Lows and best day",
    });
    expect(lows).toHaveTextContent("episode below 70");
    expect(lows).toHaveTextContent("BEST DAY");
  }, 20_000);
});
