import { beforeAll, expect, it } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import App from "../src/App";

// The detail drawer is code-split; load it once with its own budget.
beforeAll(async () => {
  await import("../src/Details");
}, 30_000);

it("opens Changed lately from its tile and explores a change in the constellation", async () => {
  const user = userEvent.setup();
  render(<App />);
  await screen.findByRole("region", { name: "Knowledge constellation" });
  // The demo's insight takes the corner; recent changes stay a tap away.
  await screen.findAllByRole("button", { name: /^Open insight/ });
  await user.click(
    await screen.findByRole("button", { name: "Open Changed lately" }),
  );
  const dialog = await screen.findByRole(
    "dialog",
    { name: "Changed lately" },
    { timeout: 10000 },
  );
  expect(
    within(dialog).getByRole("heading", { name: "Changed lately" }),
  ).toBeInTheDocument();
  const explore = within(dialog).getAllByRole("button", {
    name: /^Explore /,
  });
  // The panel lists more than the tile's five.
  expect(explore.length).toBeGreaterThan(5);
  const title = explore[0].getAttribute("aria-label")!.replace("Explore ", "");
  await user.click(explore[0]);
  expect(
    await screen.findByRole("complementary", {
      name: "Selected knowledge details",
    }),
  ).toHaveTextContent(title);
}, 20_000);
