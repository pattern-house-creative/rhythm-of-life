import { mkdirSync, mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it, vi } from "vitest";
import {
  callTool,
  EngineError,
  engineMode,
  engineTarget,
  listTools,
  resetEngine,
} from "../server/engine";
import { parseSettings, SettingsError, resetSettings } from "../server/config";
import { handleInstance } from "../server/routes";
import { appToken, engineUrl, rpc, withEngine } from "./engine";

const reason = async (promise: Promise<unknown>) => {
  try {
    await promise;
    return "resolved";
  } catch (error) {
    return error instanceof EngineError ? error.reason : "other";
  }
};
// The engine's answer to tools/list.
const listing = (...names: string[]) =>
  new Response(
    JSON.stringify({
      jsonrpc: "2.0",
      id: "rhythm-app",
      result: { tools: names.map((name) => ({ name })) },
    }),
    { headers: { "content-type": "application/json" } },
  );

describe("the one engine client", () => {
  it("is in demo mode without an engine URL or a usable token", () => {
    expect(engineMode()).toBe("demo");
    withEngine();
    expect(engineMode()).toBe("live");
    expect(engineTarget()).toMatchObject({ url: engineUrl, token: appToken });
    // A secret Docker made into a directory is "not configured".
    const dir = mkdtempSync(join(tmpdir(), "rhythm-token-"));
    mkdirSync(join(dir, "app_token"));
    vi.stubEnv("RHYTHM_APP_TOKEN_FILE", join(dir, "app_token"));
    expect(engineMode()).toBe("demo");
  });
  it("lets RHYTHM_ENGINE_URL win, drops a trailing slash and refuses a URL with a path", () => {
    withEngine();
    vi.stubEnv("RHYTHM_ENGINE_URL", "http://engine:8080/");
    expect(engineTarget()?.url).toBe("http://engine:8080");
    vi.stubEnv("RHYTHM_ENGINE_URL", "http://engine:8080/elsewhere");
    expect(engineTarget()).toBeNull();
  });
  it("re-reads the token file when it changes", () => {
    const dir = withEngine();
    writeFileSync(join(dir, "app_token"), "  a-rotated-fictional-value \n");
    expect(engineTarget()?.token).toBe("a-rotated-fictional-value");
  });
  it("reads the first matching message of an event stream", async () => {
    withEngine();
    const stream = [
      `data: ${JSON.stringify({ jsonrpc: "2.0", method: "notifications/progress" })}`,
      "",
      `data: ${JSON.stringify({ jsonrpc: "2.0", id: "rhythm-app", result: { structuredContent: { ok: 1 } } })}`,
      "",
    ].join("\n");
    const fetcher = vi.fn().mockResolvedValue(
      new Response(stream, {
        headers: { "content-type": "text/event-stream" },
      }),
    );
    expect(await callTool("app_insights", {}, undefined, fetcher)).toEqual({
      ok: 1,
    });
  });
  it("maps the contract's failures to reasons and never returns a partial answer", async () => {
    withEngine();
    const answer = (response: Response) =>
      reason(
        callTool(
          "app_insights",
          {},
          undefined,
          vi.fn().mockResolvedValue(response),
        ),
      );
    expect(await answer(new Response("", { status: 401 }))).toBe("engine_auth");
    expect(await answer(new Response("", { status: 403 }))).toBe("forbidden");
    expect(await answer(new Response("", { status: 404 }))).toBe("not_found");
    expect(await answer(new Response("", { status: 413 }))).toBe("too_large");
    expect(await answer(new Response("", { status: 503 }))).toBe("unavailable");
    expect(
      await answer(rpc({ error: { code: "not_found", message: "x" } }, true)),
    ).toBe("not_found");
    expect(await answer(rpc("not an object"))).toBe("invalid_response");
    expect(
      await answer(
        new Response(
          JSON.stringify({ jsonrpc: "2.0", id: "other", result: {} }),
          { headers: { "content-type": "application/json" } },
        ),
      ),
    ).toBe("invalid_response");
    expect(
      await answer(
        new Response(
          JSON.stringify({ jsonrpc: "2.0", id: "rhythm-app", error: {} }),
          { headers: { "content-type": "application/json" } },
        ),
      ),
    ).toBe("invalid_response");
    // A request over 32 KiB never leaves.
    const fetcher = vi.fn();
    expect(
      await reason(
        callTool(
          "app_dashboards",
          { pin: "x".repeat(40_000), pinned: true },
          undefined,
          fetcher,
        ),
      ),
    ).toBe("too_large");
    expect(fetcher).not.toHaveBeenCalled();
  });
  it("treats an adapter tool missing from tools/list as Not connected, and lists at most every five minutes", async () => {
    withEngine();
    resetEngine();
    const fetcher = vi
      .fn()
      .mockResolvedValueOnce(listing("app_graph", "app_latest_glucose"));
    expect(
      await reason(callTool("app_garmin_summary", {}, undefined, fetcher)),
    ).toBe("not_connected");
    expect(fetcher).toHaveBeenCalledTimes(1);
    expect(JSON.parse(String(fetcher.mock.calls[0][1].body))).toMatchObject({
      method: "tools/list",
      params: {},
    });
    const now = Date.now();
    expect([
      ...((await listTools(undefined, fetcher, process.env, now + 60_000)) ??
        []),
    ]).toEqual(["app_graph", "app_latest_glucose"]);
    expect(fetcher).toHaveBeenCalledTimes(1);
    fetcher.mockResolvedValueOnce(listing("app_graph"));
    await listTools(undefined, fetcher, process.env, now + 6 * 60_000);
    expect(fetcher).toHaveBeenCalledTimes(2);
  });
  it("sends Cloudflare Access headers only when both parts are set", async () => {
    withEngine();
    const fetcher = vi.fn(async () => rpc({ insights: [], runs: [] }));
    await callTool("app_insights", {}, undefined, fetcher);
    expect(fetcher.mock.calls[0][1].headers).not.toHaveProperty(
      "CF-Access-Client-Id",
    );
    vi.stubEnv("RHYTHM_CF_ACCESS_CLIENT_ID", "fictional-id");
    vi.stubEnv("RHYTHM_CF_ACCESS_CLIENT_SECRET", "fictional-secret-value");
    await callTool("app_insights", {}, undefined, fetcher);
    expect(fetcher.mock.calls[1][1].headers).toMatchObject({
      "CF-Access-Client-Id": "fictional-id",
      "CF-Access-Client-Secret": "fictional-secret-value",
    });
  });
});

describe("settings and /api/instance", () => {
  it("serves the install's name, people and time zone, and the mode", async () => {
    withEngine();
    const response = await handleInstance(
      new Request("http://rhythm.example.invalid/api/instance"),
    );
    expect(response.headers.get("cache-control")).toContain("no-store");
    expect(await response.json()).toEqual({
      name: "Test home",
      people: [
        { id: "alex", name: "Alex" },
        { id: "sam", name: "Sam" },
      ],
      timezone: "Europe/London",
      mode: "live",
      catalogProvider: "rhythm",
    });
    expect(
      (
        await handleInstance(
          new Request("http://rhythm.example.invalid/api/instance", {
            method: "POST",
          }),
        )
      ).status,
    ).toBe(405);
  });
  it("names the keys that are wrong, never their values", () => {
    const bad = (text: string) => {
      try {
        parseSettings(text);
        return [];
      } catch (error) {
        return error instanceof SettingsError ? error.issues : ["other"];
      }
    };
    expect(
      bad(
        "version: 1\ntimezone: Mars/Base\npeople:\n  - id: Alex\n    name: A",
      ),
    ).toEqual(["timezone", "people[0]"]);
    expect(bad("timezone: UTC\npeople: []")).toEqual(["people"]);
    expect(
      bad(
        "timezone: UTC\npeople:\n  - {id: alex, name: A}\nengine:\n  url: http://user@engine/",
      ),
    ).toEqual(["engine.url"]);
    expect(bad("[1, 2]")).toEqual(["the file is not a mapping"]);
    expect(
      parseSettings(
        "timezone: UTC\npeople:\n  - {id: alex, name: Alex}\nengine:\n  url: ''",
      ).engineUrl,
    ).toBe("");
  });
  it("keeps the last good settings when the file turns invalid", async () => {
    const dir = withEngine();
    resetSettings();
    expect(engineMode()).toBe("live");
    const error = vi.spyOn(console, "error").mockImplementation(() => {});
    writeFileSync(join(dir, "rhythm.yaml"), "timezone: [");
    const response = await handleInstance(
      new Request("http://rhythm.example.invalid/api/instance"),
    );
    expect((await response.json()).name).toBe("Test home");
    expect(error).toHaveBeenCalledOnce();
    expect(String(error.mock.calls[0][0])).not.toContain("timezone: [");
  });
});
