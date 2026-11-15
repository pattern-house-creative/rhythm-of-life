import { afterEach, expect, it } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import App from "../src/App";
import { chartModel, recentHours } from "../src/chartModel";
import { sampleHealth } from "./sampleHealth";

afterEach(() => localStorage.clear());

it("draws the glucose tile's line over the last three hours", () => {
  // Fictional readings every five minutes for six hours.
  const start = Date.parse("2026-10-18T06:00:00Z");
  const samples = Array.from({ length: 73 }, (_, i) => ({
    hour: 6 + Math.floor(i / 12),
    value: 100 + (i % 20),
    timestamp: new Date(start + i * 300_000).toISOString(),
  }));
  const { samples: recent, label } = chartModel(
    { ...sampleHealth.glucose, samples },
    "all",
    true,
  );
  expect(recentHours).toBe(3);
  // Three hours at five-minute steps, both ends included.
  expect(recent).toHaveLength(37);
  expect(
    Date.parse(recent.at(-1)!.timestamp) - Date.parse(recent[0].timestamp),
  ).toBe(3 * 3_600_000);
  expect(label).toContain("last three hours of glucose readings");
});

it("lets the layout be chosen in settings and remembers it on this browser", async () => {
  const user = userEvent.setup();
  const { container, unmount } = render(<App />);
  await screen.findByRole("region", { name: "Knowledge constellation" });
  // The tall column is the starting layout.
  expect(container.querySelector(".app")).toHaveAttribute(
    "data-layout",
    "hero",
  );
  await user.click(
    screen.getByRole("button", { name: "Sources and settings" }),
  );
  const choice = within(
    await screen.findByRole("group", { name: "Layout of the home screen" }),
  );
  expect(choice.getByRole("button", { name: "Tall column" })).toHaveAttribute(
    "aria-pressed",
    "true",
  );
  await user.click(choice.getByRole("button", { name: "Rhythm band" }));
  expect(container.querySelector(".app")).toHaveAttribute(
    "data-layout",
    "band",
  );
  expect(localStorage.getItem("rhythm-of-life.layout")).toBe("band");
  unmount();
  // A later visit starts in the chosen layout.
  const again = render(<App />);
  await screen.findByRole("region", { name: "Knowledge constellation" });
  expect(again.container.querySelector(".app")).toHaveAttribute(
    "data-layout",
    "band",
  );
});
