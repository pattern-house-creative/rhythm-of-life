import { afterEach, expect, it, vi } from "vitest";
import { rmSync } from "node:fs";
import { withEngine, rpc, called } from "./engine";
import { handleScreens } from "../server/screens";
const folders: string[] = [];
afterEach(() => {
  for (const folder of folders.splice(0))
    rmSync(folder, { recursive: true, force: true });
});
const id = "aaaaaaaa-0000-4000-8000-000000000001",
  screen = "studio-screen-1234567890";
const request = (args: unknown) =>
  new Request("http://localhost/api/screens?studio=1", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(args),
  });
it("preserves forbidden responses so the browser purges revoked draft data", async () => {
  folders.push(withEngine({ tools: ["app_studio"] }));
  const fetcher = vi.fn(async () =>
    rpc({ error: "Studio sources are no longer readable", code: "forbidden" }),
  );
  expect(
    (
      await handleScreens(
        request({ op: "read", screen, sessionId: id }),
        fetcher,
      )
    ).status,
  ).toBe(403);
});
it("uses the existing screen facade and authenticated Studio tool", async () => {
  folders.push(withEngine({ tools: ["app_studio"] }));
  const fetcher = vi.fn(async (_url, init) => {
    expect(called(init)).toEqual({
      name: "app_studio",
      arguments: { op: "active", screen },
    });
    return rpc({ value: { id, revision: 2 } });
  });
  const response = await handleScreens(
    request({ op: "active", screen }),
    fetcher,
  );
  expect(response.status).toBe(200);
  expect(await response.json()).toEqual({ value: { id, revision: 2 } });
});
it("returns a concrete save conflict and never retries publication", async () => {
  folders.push(withEngine({ tools: ["app_studio"] }));
  const fetcher = vi.fn(async () =>
    rpc({
      error:
        "Saved dashboard changed; start a new draft from its current version",
    }),
  );
  const response = await handleScreens(
    request({
      op: "save",
      screen,
      sessionId: id,
      requestId: id,
      revision: 2,
      payload: {},
    }),
    fetcher,
  );
  expect(response.status).toBe(409);
  expect((await response.json()).error).toContain("changed");
  expect(fetcher).toHaveBeenCalledTimes(1);
});
it("fails closed on an older engine and refuses app-authored draft payloads", async () => {
  folders.push(withEngine({ tools: ["app_screens"] }));
  const fetcher = vi.fn();
  expect(
    (await handleScreens(request({ op: "active", screen }), fetcher)).status,
  ).toBe(503);
  expect(fetcher).not.toHaveBeenCalled();
  expect(
    (
      await handleScreens(
        request({
          op: "validate",
          screen,
          sessionId: id,
          requestId: id,
          revision: 1,
          payload: { ok: true },
          html: "not allowed",
        }),
        fetcher,
      )
    ).status,
  ).toBe(400);
});
