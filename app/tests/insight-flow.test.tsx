import { beforeAll, expect, it } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import App from "../src/App";
import { demoInsights } from "../src/demoInsights";

// The drawer and the explorer are code-split; load them once with their own budget.
beforeAll(async () => {
  await import("../src/Details");
  await import("../src/Explorer");
}, 30_000);

const [discovery, focus] = demoInsights();

it("grows the demo's insight from the corner, takes feedback, and lights its trail in the sky", async () => {
  const user = userEvent.setup();
  render(<App />);
  // The corner and the phone card both lead to today's insight.
  const opens = await screen.findAllByRole("button", {
    name: `Open insight · Discovery: ${discovery.title}`,
  });
  expect(opens).toHaveLength(2);
  expect(opens[1]).toHaveClass("insight-card");
  await user.click(opens[0]);
  const dialog = await screen.findByRole(
    "dialog",
    { name: "Insight" },
    { timeout: 10000 },
  );
  expect(
    within(dialog).getByRole("heading", { level: 2, name: discovery.title }),
  ).toBeInTheDocument();
  // Feedback shows where it stands once it is saved.
  await user.click(within(dialog).getByRole("button", { name: "Interesting" }));
  expect(
    await within(dialog).findByText("Saved. Glad it was interesting."),
  ).toBeInTheDocument();
  expect(
    within(dialog).getByRole("button", { name: "Interesting" }),
  ).toHaveAttribute("aria-pressed", "true");
  // An earlier insight reopens in the same panel.
  await user.click(
    within(
      within(dialog).getByRole("region", { name: "Earlier insights" }),
    ).getByRole("button", { name: new RegExp(focus.title) }),
  );
  expect(
    within(dialog).getByRole("heading", { level: 2, name: focus.title }),
  ).toBeInTheDocument();
  // Show in the sky: the explorer opens on the insight's trail.
  await user.click(
    within(dialog).getByRole("button", { name: "Show in the sky" }),
  );
  const details = await screen.findByRole(
    "complementary",
    { name: "Selected knowledge details" },
    { timeout: 10000 },
  );
  expect(within(details).getByText("INSIGHT TRAIL")).toBeInTheDocument();
  expect(
    within(details).getByRole("heading", { name: focus.title }),
  ).toBeInTheDocument();
  const trail = within(details).getByRole("list", { name: "Insight trail" });
  const stars = within(trail).getAllByRole("button");
  expect(stars.map((b) => b.textContent)).toEqual(
    focus.stars.map((s) => expect.stringContaining(s.title)),
  );
  expect(
    screen.getByText(`${focus.stars.length} stars lit · select one to explore`),
  ).toBeInTheDocument();
  // A star of the trail opens as usual; clearing it comes back to the trail.
  await user.click(stars[0]);
  expect(
    within(details).getByRole("heading", { name: focus.stars[0].title }),
  ).toBeInTheDocument();
  await user.click(
    within(details).getByRole("button", { name: "Clear focus" }),
  );
  expect(within(details).getByText("INSIGHT TRAIL")).toBeInTheDocument();
  await user.click(
    within(details).getByRole("button", { name: "See the whole sky" }),
  );
  expect(within(details).getByText("START EXPLORING")).toBeInTheDocument();
}, 30_000);

it("moves the corner on as insights are dismissed, keeping earlier ones in reach", async () => {
  const user = userEvent.setup();
  render(<App />);
  const [corner] = await screen.findAllByRole("button", {
    name: /^Open insight · Discovery/,
  });
  await user.click(corner);
  const dialog = await screen.findByRole(
    "dialog",
    { name: "Insight" },
    { timeout: 10000 },
  );
  await user.click(within(dialog).getByRole("button", { name: "Not useful" }));
  expect(
    await within(dialog).findByText("Noted. It won't come back."),
  ).toBeInTheDocument();
  // Yesterday's saved focus is the newest one left to show.
  expect(
    screen.getAllByRole("button", {
      name: /^Open insight · Focus/,
      hidden: true,
    }),
  ).toHaveLength(2);
  expect(
    screen.queryByRole("button", { name: /Earlier insights/, hidden: true }),
  ).not.toBeInTheDocument();
  // Dismiss that one too: nothing is current, and the corner (and the
  // phone's line) open the newest earlier insight, with the rest below it.
  await user.click(
    within(
      within(dialog).getByRole("region", { name: "Earlier insights" }),
    ).getByRole("button", { name: new RegExp(focus.title) }),
  );
  await user.click(within(dialog).getByRole("button", { name: "Not useful" }));
  expect(
    await within(dialog).findByText("Noted. It won't come back."),
  ).toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: /^Open insight/, hidden: true }),
  ).not.toBeInTheDocument();
  await user.click(
    within(dialog).getByRole("button", { name: "Close insight" }),
  );
  const earlier = await screen.findAllByRole("button", {
    name: /Earlier insights/,
  });
  expect(earlier).toHaveLength(2);
  expect(earlier[1]).toHaveClass("insight-card");
  await user.click(earlier[0]);
  const reopened = await screen.findByRole(
    "dialog",
    { name: "Insight" },
    { timeout: 10000 },
  );
  expect(
    within(reopened).getByRole("heading", { level: 2, name: discovery.title }),
  ).toBeInTheDocument();
  expect(
    within(
      within(reopened).getByRole("region", { name: "Earlier insights" }),
    ).getAllByRole("button"),
  ).toHaveLength(2);
}, 30_000);

it("marks an insight handled with what happened, and moves the corner on", async () => {
  const user = userEvent.setup();
  render(<App />);
  const [corner] = await screen.findAllByRole("button", {
    name: /^Open insight · Discovery/,
  });
  await user.click(corner);
  const dialog = await screen.findByRole(
    "dialog",
    { name: "Insight" },
    { timeout: 10000 },
  );
  // The demo's runs: this morning's saved this insight.
  expect(
    within(dialog).getByText(/^Last run .* · new insight$/),
  ).toBeInTheDocument();
  await user.click(
    within(dialog).getByRole("button", { name: "Already handled" }),
  );
  await user.type(
    within(dialog).getByRole("textbox", { name: "What happened?" }),
    "Linked the ritual to the library.",
  );
  await user.click(
    within(dialog).getByRole("button", { name: "Mark handled" }),
  );
  expect(
    await within(dialog).findByText("Marked as handled. It won't come back."),
  ).toBeInTheDocument();
  expect(
    within(dialog).getByText("“Linked the ritual to the library.”"),
  ).toBeInTheDocument();
  // Handled counts like dismissed: yesterday's focus takes the corner.
  expect(
    screen.getAllByRole("button", {
      name: /^Open insight · Focus/,
      hidden: true,
    }),
  ).toHaveLength(2);
}, 30_000);
