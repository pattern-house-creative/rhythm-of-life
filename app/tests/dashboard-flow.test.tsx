import { beforeAll, expect, it } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import App from "../src/App";
import { wrapperPolicy } from "../src/dashboardFrame";

// The explorer and the dashboard view are code-split; load them once with their own budget.
beforeAll(async () => {
  await import("../src/Explorer");
  await import("../src/DashboardView");
}, 30_000);

it("shows the demo's dashboard as a star, lists it on the stars it reads, and opens it in the reader's sandbox", async () => {
  const user = userEvent.setup();
  render(<App />);
  await user.click(
    await screen.findByRole("button", { name: "Enter constellation" }),
  );
  const explorer = await screen.findByRole("region", {
    name: "Knowledge explorer",
  });
  // Stars are chosen from the keyboard; the sky's drag handling needs a real pointer.
  const pick = async (title: string) => {
    within(explorer)
      .getAllByRole("button", { name: `Explore ${title}` })[0]
      .focus();
    await user.keyboard("{Enter}");
  };
  // A star the dashboard reads lists it in its drawer.
  await pick("Training");
  const panel = within(explorer).getByRole("complementary", {
    name: "Selected knowledge details",
  });
  // It is listed on a star it reads, as a way in and among the star's links.
  const listed = await within(panel).findAllByRole(
    "button",
    { name: /Sample training week/ },
    { timeout: 10000 },
  );
  expect(listed.some((b) => b.classList.contains("dashboard-action"))).toBe(
    true,
  );
  // (The demo's dashboard with a layout reads Training too.)
  expect(
    within(
      within(panel).getByRole("list", { name: "Connected items" }),
    ).getAllByText("dashboard").length,
  ).toBeGreaterThanOrEqual(1);
  // The dashboard is a star of its own, linked to what it reads.
  await pick("Sample training week");
  expect(
    within(panel).getByRole("heading", { name: "Sample training week" }),
  ).toBeInTheDocument();
  expect(within(panel).getByText(/Dashboard ·/)).toBeInTheDocument();
  const connected = within(panel).getByRole("list", {
    name: "Connected items",
  });
  expect(within(connected).getAllByText("reads").length).toBeGreaterThanOrEqual(
    2,
  );
  expect(
    within(panel).queryByRole("button", { name: "Explore more connections" }),
  ).toBeNull();
  await user.click(
    within(panel).getByRole("button", { name: /Open dashboard/ }),
  );
  const reader = await screen.findByRole("region", { name: "Source reader" });
  expect(within(reader).getByText("SAMPLE DASHBOARD")).toBeInTheDocument();
  // The page runs in a sandboxed frame with no origin, behind the no-navigation wrapper.
  const frame = await within(reader).findByTitle(
    "Sample training week dashboard",
    undefined,
    { timeout: 10000 },
  );
  expect(frame.tagName).toBe("IFRAME");
  expect(frame.getAttribute("sandbox")).toBe("allow-scripts allow-forms");
  expect(frame.getAttribute("referrerpolicy")).toBe("no-referrer");
  expect(frame.getAttribute("srcdoc")).toContain(wrapperPolicy);
  // Its versions are reachable, and what was read is said outside the frame.
  // The version is a short pill; its list unfolds in place, each version with
  // its date and its note beneath, and folds away once one is chosen.
  const pill = within(reader).getByRole("button", { name: /^Version 2/ });
  expect(pill).toHaveTextContent(/^Version 2latest· Oct 18$/);
  expect(pill).toHaveAttribute("aria-expanded", "false");
  expect(
    await within(reader).findByText(/Read from the engine just now · all 4 feeds/),
  ).toBeInTheDocument();
  // On a phone that line folds to one row, which unfolds the rest in place.
  const summary = within(reader).getByRole("button", {
    name: /^Read just now/,
  });
  expect(summary).toHaveAttribute("aria-expanded", "false");
  const details = document.getElementById(summary.getAttribute("aria-controls")!);
  expect(details).toHaveTextContent(/Read from the engine just now/);
  const allow = within(reader).getByRole("switch", { name: "Allow edits" });
  expect(allow).toHaveAttribute("aria-checked", "false");
  expect(details).not.toContainElement(allow);
  await user.click(summary);
  expect(summary).toHaveAttribute("aria-expanded", "true");
  await user.click(summary);
  await user.click(pill);
  const list = within(reader).getByRole("list", { name: "Versions" });
  const rows = within(list).getAllByRole("button");
  expect(rows.map((r) => r.querySelector("b")?.textContent)).toEqual([
    "Version 2",
    "Version 1",
  ]);
  expect(rows[0]).toHaveAttribute("aria-current", "true");
  expect(rows[1]).toHaveTextContent(/a first count of the week's sessions/);
  await user.click(rows[1]);
  expect(within(reader).queryByRole("list", { name: "Versions" })).toBeNull();
  expect(
    await within(reader).findByText(/An earlier version/),
  ).toBeInTheDocument();
  // Back from the reader; the way in on a star it reads opens it again.
  await user.click(
    within(reader).getByRole("button", { name: "Close the reader" }),
  );
  await pick("Training");
  await user.click(
    within(panel)
      .getAllByRole("button", { name: /Sample training week/ })
      .find((b) => b.classList.contains("dashboard-action"))!,
  );
  expect(
    await screen.findByTitle("Sample training week dashboard"),
  ).toBeInTheDocument();
}, 40_000);
