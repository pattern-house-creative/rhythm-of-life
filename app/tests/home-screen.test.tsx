import { afterEach, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import App from "../src/App";

afterEach(() => localStorage.clear());

it("fills the demo's home screen with its dashboards, and no glucose or Garmin tiles", async () => {
  const { container } = render(<App />);
  await screen.findByRole("region", { name: "Knowledge constellation" });
  // The week at a glance draws a tall card in the main slot; the training week a corner.
  const tall = await screen.findByTitle("Week at a glance card");
  expect(tall.getAttribute("srcdoc")).toContain('data-shape=\\"tall\\"');
  expect(tall.closest(".main-zone")).toHaveClass("dashboard-zone", "tall");
  const corner = await screen.findByTitle("Sample training week card");
  expect(corner.getAttribute("srcdoc")).toContain('data-shape=\\"compact\\"');
  expect(corner.closest(".recovery-zone")).not.toBeNull();
  for (const name of [/^GLUCOSE/, /^RECOVERY/, /^TRAINING/])
    expect(screen.queryByRole("button", { name })).toBeNull();
  expect(screen.queryByText(/A dashboard can live here/)).toBeNull();
  // Only the training corner is empty, and gives its room away.
  expect(container.querySelector(".observatory")).toHaveAttribute(
    "data-empty",
    "training",
  );
  // Nothing on the home screen is about diabetes care.
  expect(container.querySelector(".main-content")?.textContent).not.toMatch(
    /glucose|cgm|insulin|time in range|gmi/i,
  );
});

it("offers every dashboard for every corner, the tall slot included", async () => {
  const user = userEvent.setup();
  render(<App />);
  await screen.findByTitle("Week at a glance card");
  await user.click(
    screen.getByRole("button", { name: "Sources and settings" }),
  );
  const sources = await screen.findByRole("dialog", {
    name: "Sources & settings",
  });
  for (const corner of ["Main", "Recovery", "Training", "Focus"]) {
    const select = within(sources).getByRole("combobox", {
      name: `${corner} corner`,
    });
    expect(
      within(select)
        .getAllByRole("option")
        .map((o) => o.textContent),
    ).toEqual(
      expect.arrayContaining(["Week at a glance", "Sample training week"]),
    );
  }
  // Clearing the main slot leaves one quiet hint there.
  await user.selectOptions(
    within(sources).getByRole("combobox", { name: "Main corner" }),
    "",
  );
  await user.click(
    within(sources).getByRole("button", { name: "Close sources" }),
  );
  expect(
    await screen.findByText(/A dashboard can live here: choose one/),
  ).toBeInTheDocument();
});

it("draws a corner's new dashboard at once when the choice changes", async () => {
  const user = userEvent.setup();
  const { container } = render(<App />);
  await screen.findByTitle("Week at a glance card");
  await user.click(
    screen.getByRole("button", { name: "Sources and settings" }),
  );
  const sources = await screen.findByRole("dialog", {
    name: "Sources & settings",
  });
  await user.selectOptions(
    within(sources).getByRole("combobox", { name: "Main corner" }),
    "Sample training week",
  );
  const main = container.querySelector(".main-zone") as HTMLElement;
  expect(
    await within(main).findByTitle("Sample training week card"),
  ).toHaveAttribute("srcdoc", expect.stringContaining('data-shape=\\"tall\\"'));
  expect(within(main).queryByText(/can’t be drawn/)).toBeNull();
});

it("asks for a compact card in the rhythm band on a wide screen", async () => {
  localStorage.setItem("rhythm-of-life.layout", "band");
  const narrow = vi.mocked(window.matchMedia).getMockImplementation()!;
  vi.mocked(window.matchMedia).mockImplementation(
    (query: string) =>
      ({
        matches: query.includes("min-width: 1100px"),
        media: query,
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
      }) as unknown as MediaQueryList,
  );
  render(<App />);
  const card = await screen.findByTitle("Week at a glance card");
  await waitFor(() =>
    expect(card.getAttribute("srcdoc")).toContain('data-shape=\\"compact\\"'),
  );
  vi.mocked(window.matchMedia).mockImplementation(narrow);
});
