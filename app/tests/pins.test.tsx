import { act, renderHook, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { handleDashboards } from "../server/dashboards";
import { cleanPins } from "../src/dashboards";
import { emptyGraph } from "../src/liveGraph";
import type { GraphSession } from "../src/liveGraph";
import { useKnowledge } from "../src/useKnowledge";
import { withEngine } from "./engine";

afterEach(() => {
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
});
const configured = () => withEngine();
const rpc = (payload: unknown) =>
  new Response(
    JSON.stringify({
      jsonrpc: "2.0",
      id: "rhythm-app",
      result: { structuredContent: payload },
    }),
    { headers: { "content-type": "application/json" } },
  );
const sent = () =>
  JSON.parse(String(vi.mocked(fetch).mock.calls.at(-1)![1]?.body)).params;
const base = "http://rhythm.example.invalid/api/dashboards";

it("reads and saves pins with the app token, failing closed", async () => {
  expect(
    cleanPins({ pins: ["rhythm:a", "rhythm:a", 3, "", "drive:b"] }),
  ).toEqual(["rhythm:a", "drive:b"]);
  expect(
    cleanPins({ pins: Array.from({ length: 25 }, (_, i) => `s${i}`) }),
  ).toEqual([]);
  expect(cleanPins(null)).toEqual([]);
  // Without the gate: nothing pinned, nothing sent.
  expect(
    await (await handleDashboards(new Request(`${base}?pins=1`))).json(),
  ).toEqual({ pins: [] });
  expect(fetch).not.toHaveBeenCalled();
  configured();
  vi.mocked(fetch).mockResolvedValueOnce(rpc({ pins: ["rhythm:a"] }));
  expect(
    await (await handleDashboards(new Request(`${base}?pins=1`))).json(),
  ).toEqual({ pins: ["rhythm:a"] });
  expect(sent().arguments).toEqual({ pins: true });
  vi.mocked(fetch).mockResolvedValueOnce(
    rpc({ pins: ["rhythm:a", "drive:b"] }),
  );
  const pinned = await handleDashboards(
    new Request(base, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ action: "pin", star: "drive:b", pinned: true }),
    }),
  );
  expect(await pinned.json()).toEqual({ pins: ["rhythm:a", "drive:b"] });
  expect(sent().arguments).toEqual({ pin: "drive:b", pinned: true });
  for (const bad of [
    { action: "pin", star: "", pinned: true },
    { action: "pin", star: "drive:b" },
    { action: "pin", star: "drive:b", pinned: "yes" },
  ])
    expect(
      (
        await handleDashboards(
          new Request(base, {
            method: "POST",
            headers: { "content-type": "application/json" },
            body: JSON.stringify(bad),
          }),
        )
      ).status,
    ).toBe(400);
  expect((await handleDashboards(new Request(`${base}?pins=2`))).status).toBe(
    400,
  );
});

it("loads pins with the map, saves a toggle on the engine, and puts them back if saving fails", async () => {
  vi.spyOn(document, "hidden", "get").mockReturnValue(false);
  let saved = ["rhythm:kept"];
  const setPin = vi.fn(async (star: string, pinned: boolean) => {
    saved = pinned ? [...saved, star] : saved.filter((s) => s !== star);
    return saved;
  });
  const session: GraphSession = {
    initial: emptyGraph(),
    refresh: vi.fn(async () => emptyGraph()),
    retrieve: vi.fn(async () => ({ ...emptyGraph(), cursors: {} })),
    pins: vi.fn(async () => saved),
    setPin,
  };
  const { result } = renderHook(() => useKnowledge(session));
  await waitFor(() => expect(result.current.pins).toEqual(["rhythm:kept"]));
  await act(async () => result.current.togglePin("drive:new"));
  expect(setPin).toHaveBeenCalledWith("drive:new", true);
  expect(result.current.pins).toEqual(["rhythm:kept", "drive:new"]);
  await act(async () => result.current.togglePin("rhythm:kept"));
  expect(result.current.pins).toEqual(["drive:new"]);
  // A failed save puts the pins back as they were.
  setPin.mockRejectedValueOnce(new Error("down"));
  await act(async () => result.current.togglePin("drive:other"));
  await waitFor(() => expect(result.current.pins).toEqual(["drive:new"]));
});
