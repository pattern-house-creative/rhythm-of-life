import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import App from "../src/App";
import { handleObservatory } from "../server/routes";
import { demoInstance, setInstance } from "../src/instance";
import type { GraphNode } from "../src/knowledge";
import { engineUrl, withEngine } from "./engine";
import { record } from "./synthetic";

afterEach(() => vi.unstubAllEnvs());

// Sources & settings lists the Rhythm engine below the sources that need an outside account
// (docs/operations.md). Its row is the engine's own stars, under the engine's catalog provider id,
// which an install that kept an older engine's ids names otherwise, here the fictional "legacy".
// Such an install's row read "Not connected" while the engine answered every read, since it looked
// for stars under "rhythm" alone.

function live(nodes: GraphNode[]) {
  withEngine({ tools: ["app_graph", "app_insights", "app_dashboards"] });
  const page = {
    nodes,
    edges: [],
    issues: [],
    nextCursor: null,
    cursors: {},
    completeness: "complete",
  };
  vi.mocked(fetch).mockImplementation(async (url, init) => {
    const path = String(url);
    if (path.startsWith(engineUrl)) throw new Error("unreachable");
    const request = new Request(new URL(path, "http://localhost"), init);
    if (path.startsWith("/api/observatory")) return handleObservatory(request);
    if (path === "/api/graph")
      return new Response(JSON.stringify(page), {
        headers: { "content-type": "application/json" },
      });
    throw new Error("Unexpected request");
  });
}

// A star the engine keeps itself: a data star under the provider id `provider`.
function engineStar(provider: string, at: string) {
  const star = record("alex:recovery", provider, "data:garmin");
  star.type = "record";
  star.title = "Fictional sleep and recovery";
  star.provenance.demo = false;
  star.resource.sourceModifiedAt = at;
  return star;
}

async function engineRow() {
  const user = userEvent.setup();
  await screen.findByLabelText("Source observation timestamps");
  await user.click(
    screen.getByRole("button", { name: "Sources and settings" }),
  );
  const sources = await screen.findByRole("dialog", {
    name: "Sources & settings",
  });
  return within(sources).getByText("Rhythm engine").closest("div")!;
}

it("reads the Rhythm engine connected from its stars under the catalog provider an install kept", async () => {
  setInstance({ ...demoInstance, catalogProvider: "legacy" });
  live([
    engineStar("legacy", "2026-11-08T09:00:00Z"),
    // A source's own star is no star of the engine's.
    record("fx-note", "second-brain"),
  ]);
  render(<App />);
  const strip = await screen.findByLabelText("Source observation timestamps");
  await waitFor(() => expect(strip).toHaveTextContent("Rhythm engine"));
  const entry = [...strip.children].find((s) =>
    s.textContent?.startsWith("Rhythm engine "),
  )!;
  expect(entry.querySelector("time")).toHaveAttribute(
    "datetime",
    "2026-11-08T09:00:00Z",
  );
  const row = await engineRow();
  expect(row).not.toHaveTextContent("NOT CONNECTED");
  expect(row).toHaveTextContent("AVAILABLE · ACCESS AUTHORIZED");
  expect(row).not.toHaveTextContent("Observation time unavailable");
});

it("reads the Rhythm engine connected once it answered the map, when none of the stars on it are its own", async () => {
  setInstance({ ...demoInstance, catalogProvider: "legacy" });
  // Stars under the default id are another provider's on this install.
  live([engineStar("rhythm", "2026-11-08T09:00:00Z")]);
  render(<App />);
  const row = await engineRow();
  await waitFor(() =>
    expect(row).toHaveTextContent("AVAILABLE · ACCESS AUTHORIZED"),
  );
  expect(row).not.toHaveTextContent("NOT CONNECTED");
  expect(row).toHaveTextContent(/Retrieved: /);
});
