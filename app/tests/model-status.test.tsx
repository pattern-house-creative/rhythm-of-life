import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { render, screen, within } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { handleStatus } from "../server/status";
import { routes } from "../server/routes";
import {
  cleanStatus,
  cleanStatusAnswer,
  indexLine,
} from "../src/modelStatus";
import type { StatusAnswer } from "../src/modelStatus";
import { EngineModels } from "../src/EngineModels";
import { Details } from "../src/Details";
import { demoAdapter } from "../src/demo";
import { setInstance, demoInstance } from "../src/instance";
import { called, rpc, withEngine } from "./engine";

// SB-P2-01: the engine's version and each model's job and state, from app_status through
// GET /api/status into Sources & settings (docs/models.md).
const fixture = () =>
  JSON.parse(
    readFileSync(
      resolve(process.cwd(), "../contracts/fixtures/app_status.example.json"),
      "utf8",
    ),
  );
const get = () =>
  new Request("http://rhythm.example.invalid/api/status", { method: "GET" });

afterEach(() => {
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
  setInstance(demoInstance);
});

it("reads the contract's example and leaves out what it cannot read", () => {
  const status = cleanStatus(fixture())!;
  expect(status.version).toBe("1.0.0");
  expect(status.schema).toBe(26);
  expect(status.models.map((m) => [m.job, m.state])).toEqual([
    ["embeddings", "on"],
    ["titles", "failing"],
  ]);
  expect(status.models[1].failure).toEqual({
    kind: "timeout",
    at: "2026-10-28T07:00:00.0000000+00:00",
  });
  // A model in a state the contract doesn't name, or with a failure kind that could carry
  // words, is left out; a version that isn't one is no answer at all.
  const odd = fixture();
  odd.models[0].state = "melting";
  odd.models[1].failure.kind = "the note about the boiler";
  expect(cleanStatus(odd)!.models).toEqual([]);
  expect(cleanStatus({ ...fixture(), version: "latest" })).toBeNull();
  expect(cleanStatusAnswer({ mode: "live", available: true, ...odd })).toEqual(
    { mode: "live", available: true, version: "1.0.0", schema: 26, models: [] },
  );
  expect(cleanStatusAnswer("nonsense")).toEqual({
    mode: "live",
    available: false,
  });
  expect(
    indexLine({ passages: 1204, indexing: true, pending: 3, pausedUntil: null }),
  ).toBe("1,204 passages · 3 left to index");
  expect(
    indexLine({ passages: 1, indexing: false, pending: null, pausedUntil: null }),
  ).toBe("1 passage");
});

it("serves the engine's answer at /api/status, and nothing in demo mode or from an older engine", async () => {
  expect(Object.keys(routes)).toContain("/api/status");
  // Demo mode: no engine, so no models, and no call.
  const none = vi.fn();
  expect(await (await handleStatus(get(), none)).json()).toEqual({
    mode: "demo",
    available: false,
  });
  expect(none).not.toHaveBeenCalled();
  // A live install asks app_status, with no arguments.
  withEngine();
  const fetcher = vi.fn().mockResolvedValue(rpc(fixture()));
  const answer = await (await handleStatus(get(), fetcher)).json();
  expect(answer).toMatchObject({ mode: "live", available: true, version: "1.0.0" });
  expect(answer.models).toHaveLength(2);
  expect(called(fetcher.mock.calls[0][1])).toEqual({
    name: "app_status",
    arguments: {},
  });
  // An engine older than app_status doesn't list it: no call, and no models.
  withEngine({ tools: ["app_graph", "app_insights"] });
  const older = vi.fn();
  expect(await (await handleStatus(get(), older)).json()).toEqual({
    mode: "live",
    available: false,
  });
  expect(older).not.toHaveBeenCalled();
  // An answer outside the contract is no answer.
  withEngine();
  const wrong = vi.fn().mockResolvedValue(rpc({ version: 1 }));
  expect((await (await handleStatus(get(), wrong)).json()).available).toBe(false);
  // Only GET.
  expect(
    (
      await handleStatus(
        new Request("http://rhythm.example.invalid/api/status", { method: "POST" }),
      )
    ).status,
  ).toBe(405);
});

const live = (): StatusAnswer => ({
  mode: "live",
  available: true,
  ...cleanStatus(fixture())!,
});

it("lists each model with its job and state in Sources & settings", async () => {
  render(<EngineModels load={async () => live()} />);
  const models = await screen.findByRole("region", { name: "Models" });
  expect(await within(models).findByText("MiniLM")).toBeInTheDocument();
  expect(within(models).getByText("Search and similar links")).toBeInTheDocument();
  expect(within(models).getByText("1,204 passages · 3 left to index")).toBeInTheDocument();
  expect(within(models).getByText("ON")).toBeInTheDocument();
  expect(within(models).getByText("fictional-model:1b")).toBeInTheDocument();
  expect(within(models).getByText("Star titles")).toBeInTheDocument();
  expect(within(models).getByText("FAILING")).toBeInTheDocument();
  expect(within(models).getByText(/Stopped on a timeout/)).toBeInTheDocument();
  expect(models).toHaveTextContent("Engine 1.0.0 · schema 26");
});

// D-256: with graph.similarity.model set the engine lists the map's own model; the live model
// then serves search alone while it is on, and the similar links fall back while it is not.
it("shows a map model beside the live one, and what the map uses while it loads", async () => {
  const map = (state: string) => {
    const answer = fixture();
    answer.models.splice(1, 0, {
      job: "embeddings",
      role: "map",
      name: "bge-small",
      id: "bge-small-en-v1.5-quantized-6c9c6101-cls-l2-v1",
      state,
      index: null,
      failure: null,
    });
    return { mode: "live", available: true, ...cleanStatus(answer)! } as StatusAnswer;
  };
  const on = render(<EngineModels load={async () => map("on")} />);
  let models = await screen.findByRole("region", { name: "Models" });
  expect(await within(models).findByText("bge-small")).toBeInTheDocument();
  expect(within(models).getByText("Similar links on the map")).toBeInTheDocument();
  expect(within(models).getByText("Search")).toBeInTheDocument();
  expect(within(models).queryByText("Search and similar links")).toBeNull();
  on.unmount();
  render(<EngineModels load={async () => map("download-failed")} />);
  models = await screen.findByRole("region", { name: "Models" });
  expect(await within(models).findByText("bge-small")).toBeInTheDocument();
  expect(within(models).getByText("Search and similar links")).toBeInTheDocument();
  expect(
    within(models).getByText("Similar links use the search model until it loads."),
  ).toBeInTheDocument();
  expect(within(models).getByText("DOWNLOAD FAILED")).toBeInTheDocument();
});

it("says when the models can't be read, and the demo shows none", async () => {
  render(<EngineModels load={async () => ({ mode: "live", available: false })} />);
  expect(
    await screen.findByText(/The engine’s models can’t be read right now/),
  ).toBeInTheDocument();
});

it("shows the models only in a live install's Sources & settings", async () => {
  const data = await demoAdapter.load();
  const props = {
    view: "sources" as const,
    data,
    state: "ready" as const,
    setState: vi.fn(),
    paused: false,
    setPaused: vi.fn(),
    close: vi.fn(),
    selectNode: vi.fn(),
  };
  const { unmount } = render(<Details {...props} />);
  expect(screen.queryByRole("region", { name: "Models" })).toBeNull();
  unmount();
  // Live: the panel reads /api/status when it opens.
  setInstance({ ...demoInstance, mode: "live" });
  vi.mocked(fetch).mockImplementation(async (url) => {
    if (String(url) === "/api/status")
      return new Response(JSON.stringify(live()), {
        headers: { "Content-Type": "application/json" },
      });
    throw new Error("Unexpected request");
  });
  render(<Details {...props} />);
  const models = await screen.findByRole("region", { name: "Models" });
  expect(await within(models).findByText("MiniLM")).toBeInTheDocument();
});
