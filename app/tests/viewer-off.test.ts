import { describe, expect, it, vi } from "vitest";
import { parseSettings, SettingsError } from "../server/config";
import { routes } from "../server/routes";
import { handleInsights } from "../server/insights";
import { allAppTools, appToken, rpc, withEngine } from "./engine";
import { cookieValue } from "../server/viewerScope";

// Viewer sessions as every install has them: off (app.viewers absent or off), and on refused by
// this version at start (viewerGate.ts; docs/security.md, Viewer sessions). With them off nothing
// reads a cookie and every route answers as it did, with the app token.
const head = "version: 1\ntimezone: UTC\npeople: [{ id: alex, name: Alex }]\n";
const origin = "http://rhythm.example.invalid";

describe("viewer sessions, off", () => {
  it("are off unless rhythm.yaml says on, and this version refuses on", () => {
    expect(parseSettings(head).viewers).toBe(false);
    expect(parseSettings(`${head}app:\n  viewers: off\n`).viewers).toBe(false);
    expect(parseSettings(`${head}app:\n  viewers:\n`).viewers).toBe(false);
    for (const value of ["on", "true", "yes", "maybe"]) {
      let issues: string[] = [];
      try {
        parseSettings(`${head}app:\n  viewers: ${value}\n`);
      } catch (error) {
        if (error instanceof SettingsError) issues = error.issues;
      }
      expect(issues.some((issue) => issue.startsWith("app.viewers"))).toBe(
        true,
      );
    }
  });

  it("answer every route as before: the app token, no cookie read or set, even with a session cookie", async () => {
    withEngine({ tools: [...allAppTools, "app_viewer"] });
    const cookie = `rhythm-viewer=${cookieValue(appToken, {
      issued: Math.floor(Date.now() / 1000),
      handle: "h".repeat(43),
    })}`;
    const answers: [number, string, string | null][] = [];
    for (const handler of [
      (request: Request) => handleInsights(request),
      routes["/api/insights"],
    ]) {
      vi.mocked(fetch).mockResolvedValueOnce(rpc({ insights: [], runs: [] }));
      const response = await handler(
        new Request(`${origin}/api/insights`, {
          headers: {
            Cookie: `person=alex; ${cookie}`,
            "X-Rhythm-Person": "alex",
          },
        }),
      );
      const headers = vi.mocked(fetch).mock.calls.at(-1)?.[1]
        ?.headers as Record<string, string>;
      expect(headers.Authorization).toBe(`Bearer ${appToken}`);
      answers.push([
        response.status,
        await response.text(),
        response.headers.get("set-cookie"),
      ]);
    }
    expect(answers[1]).toEqual(answers[0]);
    expect(answers[0][2]).toBeNull();
    // /api/viewer says so, and opens nothing; the engine is not asked.
    const calls = vi.mocked(fetch).mock.calls.length;
    const status = await routes["/api/viewer"](
      new Request(`${origin}/api/viewer`),
    );
    expect(await status.json()).toEqual({ available: false });
    const enrol = await routes["/api/viewer"](
      new Request(`${origin}/api/viewer`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ op: "enrol", code: "ABCD-EFGH-JKMN" }),
      }),
    );
    expect(enrol.status).toBe(404);
    expect(enrol.headers.get("set-cookie")).toBeNull();
    expect(vi.mocked(fetch).mock.calls.length).toBe(calls);
  });

  it("stop at the settings when rhythm.yaml says on: the app answers nothing from the engine", async () => {
    withEngine({ extra: "app:\n  viewers: on" });
    const instance = await routes["/api/instance"](
      new Request(`${origin}/api/instance`),
    );
    expect(instance.status).toBe(503);
  });
});
