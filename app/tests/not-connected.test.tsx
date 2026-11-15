import {
  act,
  render,
  renderHook,
  screen,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import App from "../src/App";
import { handleObservatory } from "../server/routes";
import { handleGraph } from "../server/graph";
import { useHealthRefresh } from "../src/useHealthRefresh";
import { allAppTools, engineUrl, withEngine } from "./engine";

afterEach(() => vi.unstubAllEnvs());

// The app's own routes answer the browser; the fictional engine is unreachable, so every call
// that does reach for it fails as an outage would.
function routes() {
  vi.mocked(fetch).mockImplementation(async (url, init) => {
    const path = String(url);
    if (path.startsWith(engineUrl)) throw new Error("unreachable");
    const request = new Request(new URL(path, "http://localhost"), init);
    if (path.startsWith("/api/observatory")) return handleObservatory(request);
    if (path === "/api/graph") return handleGraph(request);
    throw new Error("Unexpected request");
  });
}
const tile = (name: RegExp) => screen.findByRole("button", { name });

it("leaves sources the household doesn't use off the home screen, and lists them with how to connect", async () => {
  // The engine offers no glucose and no Garmin tools: those adapters are off.
  withEngine({ tools: ["app_graph", "app_insights", "app_dashboards"] });
  routes();
  const { container } = render(<App />);
  const user = userEvent.setup();
  // The main slot says, once and quietly, that a dashboard can live there.
  const hint = await screen.findByText(/A dashboard can live here/);
  expect(hint).toHaveTextContent("Ask your AI to build one");
  expect(screen.queryByRole("button", { name: /^GLUCOSE/ })).toBeNull();
  expect(screen.queryByRole("button", { name: /^RECOVERY/ })).toBeNull();
  expect(screen.queryByRole("button", { name: /^TRAINING/ })).toBeNull();
  expect(screen.queryByText("Not connected")).toBeNull();
  expect(screen.queryByText("NOT CONNECTED")).toBeNull();
  // Their corners give their room to the tiles beside them.
  expect(container.querySelector(".observatory")).toHaveAttribute(
    "data-empty",
    "recovery training",
  );
  await user.click(
    within(hint).getByRole("button", { name: "Sources & settings" }),
  );
  const sources = await screen.findByRole("dialog", {
    name: "Sources & settings",
  });
  expect(within(sources).getAllByText("NOT CONNECTED").length).toBeGreaterThan(
    1,
  );
  expect(sources).toHaveTextContent("./rhythm connect nightscout");
  expect(sources).toHaveTextContent("./rhythm connect garmin");
});

it("waits for Garmin's part before showing its tiles", async () => {
  withEngine({ tools: allAppTools });
  // The latest glucose reads (and fails, as the engine is unreachable); Garmin's part never answers.
  vi.mocked(fetch).mockImplementation(async (url, init) => {
    const path = String(url);
    if (path.startsWith(engineUrl)) throw new Error("unreachable");
    if (path === "/api/observatory?source=garmin") return new Promise<Response>(() => {});
    const request = new Request(new URL(path, "http://localhost"), init);
    if (path.startsWith("/api/observatory")) return handleObservatory(request);
    if (path === "/api/graph") return handleGraph(request);
    throw new Error("Unexpected request");
  });
  render(<App />);
  // A connected glucose source that fails still shows, as unavailable.
  expect(await tile(/^GLUCOSE/)).toHaveTextContent("Source unavailable");
  expect(screen.queryByRole("button", { name: /^RECOVERY/ })).toBeNull();
  expect(screen.queryByRole("button", { name: /^TRAINING/ })).toBeNull();
});

it("keeps Source unavailable for a connected source that fails", async () => {
  withEngine({ tools: allAppTools });
  routes();
  render(<App />);
  const glucose = await tile(/^GLUCOSE/);
  expect(await within(glucose).findByText("Source unavailable")).toBeVisible();
  const recovery = await tile(/^RECOVERY/);
  expect(recovery).toHaveTextContent(/Source unavailable/);
  expect(await tile(/^TRAINING/)).toHaveTextContent("Activity unavailable");
  for (const name of [/^GLUCOSE/, /^RECOVERY/, /^TRAINING/])
    expect(await tile(name)).not.toHaveTextContent("Not connected");
});

it("never calls a part it could not read Not connected", async () => {
  withEngine({ tools: ["app_graph"] });
  // The browser reads the latest glucose; the Garmin and history reads do not come back.
  vi.mocked(fetch).mockImplementation(async (url, init) => {
    const path = String(url);
    if (path !== "/api/observatory") throw new Error("outage");
    return handleObservatory(
      new Request(new URL(path, "http://localhost"), init),
    );
  });
  const { result } = renderHook(useHealthRefresh);
  await act(async () => {});
  const sources = result.current.data!.sources;
  // The engine said glucose is off; nobody said Garmin is.
  expect(sources.find((s) => s.id === "glucose")?.status).toBe("disconnected");
  expect(sources.find((s) => s.id === "garmin")?.status).toBe("unavailable");
});
