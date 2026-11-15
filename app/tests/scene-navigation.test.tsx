import { afterEach, expect, it, vi } from "vitest";
import { act, fireEvent, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import App, { idleReturnMs } from "../src/App";
import { Constellation } from "../src/Constellation";
import { demoData } from "../src/demo";

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

async function explore() {
  const user = userEvent.setup();
  render(<App />);
  await user.click(
    await screen.findByRole("button", { name: "Enter constellation" }),
  );
  const explorer = await screen.findByRole("region", {
    name: "Knowledge explorer",
  });
  return { user, explorer };
}
const stars = (explorer: HTMLElement) =>
  explorer.querySelectorAll("svg.graph-svg g.graph-node");

it("is named Rhythm of Life and has no mode switch, tab bar or footer", async () => {
  const { container } = render(<App />);
  await screen.findByRole("region", { name: "Knowledge constellation" });
  expect(
    screen.getByRole("link", { name: "Rhythm of Life home" }),
  ).toHaveTextContent("RHYTHM OF LIFE");
  expect(
    screen.queryByRole("navigation", { name: "Display mode" }),
  ).not.toBeInTheDocument();
  expect(
    screen.queryByRole("navigation", { name: "Mobile navigation" }),
  ).not.toBeInTheDocument();
  expect(container.querySelector("footer")).toBeNull();
  // One route to sources: the settings control and the status line.
  expect(
    screen.getAllByRole("button", {
      name: /Source status|Your sources|Sources and settings/,
    }),
  ).toHaveLength(2);
});

it("keeps the whole sky while focusing and brightens the selection's links", async () => {
  const { user, explorer } = await explore();
  const before = stars(explorer).length;
  await user.click(
    within(explorer).getByRole("button", {
      name: "Rhythm of Life",
      exact: true,
    }),
  );
  // Nothing is re-laid out: every star stays, the subject and its links light up.
  expect(stars(explorer)).toHaveLength(before);
  const chosen = within(explorer).getByRole("button", {
    name: "Explore Rhythm of Life",
  });
  expect(chosen).toHaveAttribute("aria-pressed", "true");
  const related = explorer.querySelectorAll("g.graph-node.related-node");
  expect(related.length).toBeGreaterThan(1);
  expect(related.length).toBeLessThan(before);
  const quiet = [...stars(explorer)].filter(
    (g) => !g.classList.contains("related-node"),
  );
  expect((quiet[0] as SVGGElement).style.opacity).toBe("0.22");
});

it("steps back one layer per Escape before returning to the ambient sky", async () => {
  const { user, explorer } = await explore();
  await user.click(
    within(explorer).getByRole("button", {
      name: "Rhythm of Life",
      exact: true,
    }),
  );
  expect(
    explorer.querySelector("g.graph-node[aria-pressed='true']"),
  ).not.toBeNull();
  await user.keyboard("{Escape}");
  expect(screen.getByRole("region", { name: "Knowledge explorer" })).toBe(
    explorer,
  );
  expect(
    explorer.querySelector("g.graph-node[aria-pressed='true']"),
  ).toBeNull();
  await user.keyboard("{Escape}");
  expect(
    screen.queryByRole("region", { name: "Knowledge explorer" }),
  ).not.toBeInTheDocument();
});

it("a tap on empty sky clears the selection, then returns to the ambient sky", async () => {
  const { user, explorer } = await explore();
  await user.click(
    within(explorer).getByRole("button", {
      name: "Rhythm of Life",
      exact: true,
    }),
  );
  const sky = explorer.querySelector("svg.graph-svg")!;
  fireEvent.click(sky);
  expect(
    explorer.querySelector("g.graph-node[aria-pressed='true']"),
  ).toBeNull();
  fireEvent.click(sky);
  expect(
    screen.queryByRole("region", { name: "Knowledge explorer" }),
  ).not.toBeInTheDocument();
});

it("Back returns to the sky, and closing uses the one entry the app added", async () => {
  // As a browser does, the step back comes back as a popstate a moment later.
  const back = vi
    .spyOn(history, "back")
    .mockImplementation(() =>
      setTimeout(() => window.dispatchEvent(new PopStateEvent("popstate"))),
    );
  const { user } = await explore();
  await user.click(screen.getByRole("button", { name: "Return to ambient" }));
  expect(back).toHaveBeenCalledTimes(1);
  // A second close (Escape on the ambient sky) never walks further back.
  await user.keyboard("{Escape}");
  expect(back).toHaveBeenCalledTimes(1);
  // The browser's own Back (or a phone's back gesture) also returns.
  await user.click(screen.getByRole("button", { name: "Enter constellation" }));
  await screen.findByRole("region", { name: "Knowledge explorer" });
  act(() => {
    window.dispatchEvent(new PopStateEvent("popstate"));
  });
  expect(
    screen.queryByRole("region", { name: "Knowledge explorer" }),
  ).not.toBeInTheDocument();
});

it("drifts back to the ambient sky after two idle minutes, but not while typing or waiting", async () => {
  const { explorer } = await explore();
  vi.useFakeTimers();
  // Input re-arms the idle timer on the fake clock.
  fireEvent.pointerMove(window);
  within(explorer).getByRole("textbox", { name: "Search knowledge" }).focus();
  act(() => {
    vi.advanceTimersByTime(idleReturnMs + 50);
  });
  expect(screen.getByRole("region", { name: "Knowledge explorer" })).toBe(
    explorer,
  );
  (document.activeElement as HTMLElement).blur();
  // Something it asked for and is waiting on (a document being added) holds it too.
  const waiting = document.createElement("p");
  waiting.dataset.keepOpen = "true";
  explorer.append(waiting);
  act(() => {
    vi.advanceTimersByTime(idleReturnMs + 50);
  });
  expect(screen.getByRole("region", { name: "Knowledge explorer" })).toBe(
    explorer,
  );
  waiting.remove();
  act(() => {
    vi.advanceTimersByTime(idleReturnMs + 50);
  });
  expect(
    screen.queryByRole("region", { name: "Knowledge explorer" }),
  ).not.toBeInTheDocument();
});

it("adds one history entry however many views open before returning", async () => {
  const push = vi.spyOn(history, "pushState");
  vi.spyOn(history, "back").mockImplementation(() => {});
  const { user } = await explore();
  await user.click(
    screen.getByRole("button", { name: "Sources and settings" }),
  );
  await screen.findByRole("dialog", { name: "Sources & settings" });
  expect(push).toHaveBeenCalledTimes(1);
});

it("Escape steps back even when focus has left the explorer", async () => {
  const { user, explorer } = await explore();
  await user.click(
    within(explorer).getByRole("button", {
      name: "Rhythm of Life",
      exact: true,
    }),
  );
  // As after a click on plain text in the drawer.
  (document.activeElement as HTMLElement | null)?.blur();
  expect(document.activeElement).toBe(document.body);
  await user.keyboard("{Escape}");
  expect(screen.getByRole("region", { name: "Knowledge explorer" })).toBe(
    explorer,
  );
  expect(
    explorer.querySelector("g.graph-node[aria-pressed='true']"),
  ).toBeNull();
});

it("returns focus to what opened the explorer", async () => {
  const { user } = await explore();
  await user.keyboard("{Escape}");
  expect(
    screen.getByRole("button", { name: "Enter constellation" }),
  ).toHaveFocus();
});

it("stays open when idle return is switched off", async () => {
  const user = userEvent.setup();
  render(<App />);
  await user.click(
    await screen.findByRole("button", { name: "Sources and settings" }),
  );
  await user.click(
    await screen.findByRole("button", {
      name: "Return to the sky after 2 idle minutes: on",
    }),
  );
  expect(
    screen.getByRole("button", {
      name: "Return to the sky after 2 idle minutes: off",
    }),
  ).toHaveAttribute("aria-pressed", "false");
  await user.keyboard("{Escape}");
  await user.click(screen.getByRole("button", { name: "Enter constellation" }));
  const explorer = await screen.findByRole("region", {
    name: "Knowledge explorer",
  });
  vi.useFakeTimers();
  act(() => {
    vi.advanceTimersByTime(idleReturnMs * 3);
  });
  expect(screen.getByRole("region", { name: "Knowledge explorer" })).toBe(
    explorer,
  );
});

it("reports taps on empty sky, but not taps on a star", () => {
  const onSelect = vi.fn(),
    onBackground = vi.fn();
  const { container } = render(
    <Constellation
      nodes={demoData.nodes}
      edges={demoData.edges}
      selected={null}
      onSelect={onSelect}
      onBackground={onBackground}
      interactive
      healthFocus={false}
      paused
    />,
  );
  fireEvent.click(screen.getByRole("button", { name: "Explore Projects" }));
  expect(onSelect).toHaveBeenCalledWith("projects");
  expect(onBackground).not.toHaveBeenCalled();
  fireEvent.click(container.querySelector("svg.graph-svg")!);
  expect(onBackground).toHaveBeenCalledTimes(1);
});
