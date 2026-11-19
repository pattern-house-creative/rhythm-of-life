import { afterEach, beforeAll, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { StrictMode } from "react";
import App from "../src/App";
import { withSampleHealth } from "./sampleHealth";
import { LiveScreen } from "../src/LiveScreen";
import { Studio } from "../src/Studio";
vi.mock("../src/LiveScreen", () => ({ LiveScreen: vi.fn(() => null) }));
vi.mock("../src/Studio", () => ({ Studio: vi.fn(() => null) }));
import type { GraphPage } from "../src/knowledge";

beforeAll(async () => {
  await import("../src/Explorer");
  await import("../src/DashboardView");
}, 30000);
afterEach(() => history.replaceState(null, "", "/"));

it("opens the actual built-in glucose panel without a saved-dashboard lookup", async () => {
  withSampleHealth();
  history.replaceState(null, "", "/#rhythm-launcher=builtin:glucose");
  const { container } = render(
    <StrictMode>
      <App launcher />
    </StrictMode>,
  );
  await waitFor(() =>
    expect(container.querySelector(".glucose-panel")).not.toBeNull(),
  );
  expect(container.querySelector(".dashboard-frame")).toBeNull();
}, 30000);

it("opens an initial star in its reader under StrictMode instead of losing the entry during boot", async () => {
  const response = await fetch("/api/graph", {
    method: "POST",
    body: JSON.stringify({ operation: "discover" }),
    headers: { "content-type": "application/json" },
  });
  const graph = (await response.json()) as GraphPage;
  const star = graph.nodes.find((n) => n.resource.access.canRead)!;
  history.replaceState(
    null,
    "",
    `/#rhythm-launcher=star:${encodeURIComponent(star.id)}`,
  );
  render(
    <StrictMode>
      <App launcher />
    </StrictMode>,
  );
  expect(
    await screen.findByRole("region", { name: "Source reader" }),
  ).toBeInTheDocument();
  await waitFor(() => expect(location.hash).toBe(""));
  expect(LiveScreen).not.toHaveBeenCalled();
  expect(Studio).not.toHaveBeenCalled();
}, 30000);

it("shows a retryable missing-star error and cancels an interrupted entry", async () => {
  history.replaceState(null, "", "/#rhythm-launcher=star:missing-fixture");
  const original = globalThis.fetch;
  const user = userEvent.setup();
  const mounted = render(<App launcher />);
  expect(await screen.findByText("Star unavailable")).toBeInTheDocument();
  let release: ((r: Response) => void) | undefined;
  vi.stubGlobal(
    "fetch",
    vi.fn((url: string, init?: RequestInit) => {
      if (url === "/api/graph" && String(init?.body).includes('"neighbors"'))
        return new Promise<Response>((r) => {
          release = r;
        });
      return original(url, init);
    }),
  );
  await user.click(screen.getByRole("button", { name: "Try again" }));
  await waitFor(() => expect(release).toBeDefined());
  mounted.unmount();
  release!(
    new Response(
      JSON.stringify({
        nodes: [],
        edges: [],
        cursors: {},
        nextCursor: null,
        completeness: "complete",
        issues: [],
      }),
    ),
  );
  expect(location.hash).toContain("missing-fixture");
}, 30000);

it("opens a dashboard entry through the existing full-screen sandbox", async () => {
  history.replaceState(
    null,
    "",
    "/#rhythm-launcher=dashboard:00000000-0000-4000-8000-00000000d0a5",
  );
  render(<App launcher />);
  const frame = await screen.findByTitle("Sample training week dashboard");
  expect(frame.getAttribute("sandbox")).toBe("allow-scripts allow-forms");
  expect(frame.closest(".full-screen")).toHaveClass("native-reader");
  const allow = screen.getByRole("switch", { name: "Allow edits" });
  expect(allow).toHaveAccessibleDescription(/training log/i);
  expect(allow).toHaveAttribute("aria-checked", "false");
  const user = userEvent.setup();
  await user.click(allow);
  await waitFor(() => expect(allow).toHaveAttribute("aria-checked", "true"));
  await user.click(allow);
  await waitFor(() => expect(allow).toHaveAttribute("aria-checked", "false"));
  expect(
    screen.queryByRole("button", { name: "Exit full screen" }),
  ).not.toBeInTheDocument();
  expect(document.querySelector(".full-screen-wake")).toBeNull();
  // Touch/focus must not bring back a control that covers the dashboard.
  window.dispatchEvent(new Event("pointerdown"));
  window.dispatchEvent(new Event("blur"));
  expect(document.querySelector(".full-screen-bar")).toBeNull();
}, 30000);

it("keeps ordinary web screen behavior outside the native entry", async () => {
  render(<App />);
  await waitFor(() => expect(LiveScreen).toHaveBeenCalled());
  expect(Studio).toHaveBeenCalled();
}, 30000);
