import { beforeEach, describe, expect, it, vi } from "vitest";
import { createHmac, randomBytes } from "node:crypto";
import { allAppTools, appToken, called, rpc, withEngine } from "./engine";
import {
  cookieValue,
  credentialOf,
  maxAgeSeconds,
} from "../server/viewerScope";

// Viewer sessions with app.viewers on (docs/security.md, Viewer sessions; SB-P1-11). This version
// refuses on (viewerGate.ts), so these tests replace the gate to reach the path an approved version
// would take. Everything here is fictional: the people alex and sam, the codes and the tokens.
vi.mock("../server/viewerGate", () => ({ viewersAccepted: true }));

// The setup file has loaded the server's modules with the real gate already, so each test loads
// them afresh, with the replaced one.
let routes: (typeof import("../server/routes"))["routes"];
let engine: typeof import("../server/engine");
let config: typeof import("../server/config");
beforeEach(async () => {
  vi.resetModules();
  ({ routes } = await import("../server/routes"));
  engine = await import("../server/engine");
  config = await import("../server/config");
});

const origin = "http://rhythm.example.invalid";
const on = (tools: string[] = [...allAppTools, "app_viewer"]) => {
  withEngine({ tools, extra: "app:\n  viewers: on" });
  config.resetSettings();
  engine.resetEngine(tools);
};

const viewer = (body: unknown, cookie?: string, base = origin) =>
  routes["/api/viewer"](
    new Request(`${base}/api/viewer`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        ...(cookie ? { Cookie: cookie } : {}),
      },
      body: JSON.stringify(body),
    }),
  );
const insights = (headers: Record<string, string> = {}) =>
  routes["/api/insights"](new Request(`${origin}/api/insights`, { headers }));

// The Authorization header of the engine call made last.
const bearer = () =>
  (
    vi.mocked(fetch).mock.calls.at(-1)?.[1]?.headers as
      Record<string, string> | undefined
  )?.Authorization;

// Opens a session for alex: the cookie the browser keeps and the credential the engine registered.
async function open(base = origin) {
  vi.mocked(fetch).mockResolvedValueOnce(rpc({ person: "alex" }));
  const response = await viewer(
    { op: "enrol", code: "abcd-efgh-jkmn" },
    undefined,
    base,
  );
  const call = called(vi.mocked(fetch).mock.calls.at(-1)?.[1]);
  const header = response.headers.get("set-cookie") ?? "";
  const cookie = header.split(";")[0];
  return {
    response,
    header,
    cookie,
    credential: String(call.arguments.credential),
    call,
  };
}

describe("viewer sessions, on", () => {
  it("opens a session with a code: the app token registers a credential the browser never sees", async () => {
    on();
    const { response, header, cookie, credential, call } = await open();
    expect(response.status).toBe(200);
    expect(await response.json()).toEqual({
      available: true,
      person: { id: "alex", name: "Alex" },
    });
    // The code goes to the engine with the app token and a credential made here, never the token.
    expect(bearer()).toBe(`Bearer ${appToken}`);
    expect(call).toEqual({
      name: "app_viewer",
      arguments: { op: "enrol", code: "abcd-efgh-jkmn", credential },
    });
    expect(credential).toMatch(/^[A-Za-z0-9_-]{43}$/);
    expect(credential).not.toBe(appToken);
    // The cookie holds a handle: HttpOnly, SameSite=Strict, no credential, no person, no token.
    expect(header).toContain("HttpOnly");
    expect(header).toContain("SameSite=Strict");
    expect(header).toContain("Path=/");
    expect(header).toContain(`Max-Age=${maxAgeSeconds}`);
    expect(header).not.toContain("Secure");
    expect(cookie).toMatch(
      /^rhythm-viewer=v1\.\d+\.[A-Za-z0-9_-]{43}\.[A-Za-z0-9_-]{22}$/,
    );
    for (const secret of [credential, appToken, "alex"])
      expect(cookie).not.toContain(secret);
    // Over HTTPS the cookie is __Host- and Secure.
    const secure = await open("https://rhythm.example.invalid");
    expect(secure.cookie.startsWith("__Host-rhythm-viewer=v1.")).toBe(true);
    expect(secure.header).toContain("; Secure");
  });

  it("answers a session's requests with its own credential, and others with the app token", async () => {
    on();
    const { cookie, credential } = await open();
    vi.mocked(fetch).mockResolvedValueOnce(rpc({ insights: [], runs: [] }));
    const mine = await insights({ Cookie: `theme=dark; ${cookie}` });
    expect(mine.status).toBe(200);
    expect(mine.headers.get("set-cookie")).toBeNull();
    expect(bearer()).toBe(`Bearer ${credential}`);
    // No session: the household view, with the app token, as before.
    vi.mocked(fetch).mockResolvedValueOnce(rpc({ insights: [], runs: [] }));
    await insights();
    expect(bearer()).toBe(`Bearer ${appToken}`);
  });

  it("never takes a header or a cookie as identity: a forged, foreign or old session cookie is the household view", async () => {
    on();
    const now = Math.floor(Date.now() / 1000);
    const handle = randomBytes(32).toString("base64url");
    const otherToken = ["another", "fictional", "app", "token"].join("-");
    const forged = [
      // A cookie of the right shape that this server did not make.
      `rhythm-viewer=v1.${now}.${handle}.${randomBytes(16).toString("base64url")}`,
      // One made with another install's app token.
      `rhythm-viewer=${cookieValue(otherToken, { issued: now, handle })}`,
      // One made here, past its 30 days.
      `rhythm-viewer=${cookieValue(appToken, { issued: now - maxAgeSeconds - 60, handle })}`,
      // Anything else under the name.
      "rhythm-viewer=alex",
    ];
    for (const cookie of forged) {
      vi.mocked(fetch).mockResolvedValueOnce(rpc({ insights: [], runs: [] }));
      const response = await insights({
        Cookie: `person=alex; ${cookie}`,
        "X-Rhythm-Person": "alex",
        "X-Forwarded-User": "alex@example.invalid",
      });
      expect(bearer()).toBe(`Bearer ${appToken}`);
      expect(called(vi.mocked(fetch).mock.calls.at(-1)?.[1]).arguments).toEqual(
        {
          limit: 50,
        },
      );
      // The cookie is cleared, so the browser stops sending it.
      expect(response.headers.get("set-cookie")).toMatch(
        /^rhythm-viewer=; .*Max-Age=0/,
      );
    }
    // A person named only in headers and other cookies sets nothing and changes nothing.
    vi.mocked(fetch).mockResolvedValueOnce(rpc({ insights: [], runs: [] }));
    const plain = await insights({
      Cookie: "person=alex",
      "X-Rhythm-Person": "alex",
    });
    expect(bearer()).toBe(`Bearer ${appToken}`);
    expect(plain.headers.get("set-cookie")).toBeNull();
  });

  it("ends the session in the browser when the engine refuses its credential (a suspended person: 401)", async () => {
    on();
    const { cookie, credential } = await open();
    vi.mocked(fetch).mockResolvedValueOnce(
      new Response("{}", {
        status: 401,
        headers: { "content-type": "application/json" },
      }),
    );
    const refused = await insights({ Cookie: cookie });
    expect(bearer()).toBe(`Bearer ${credential}`);
    // Fail closed: no data, and the cookie cleared.
    expect(await refused.json()).toEqual({ insights: [], runs: [] });
    expect(refused.headers.get("set-cookie")).toMatch(
      /^rhythm-viewer=; .*Max-Age=0/,
    );
    // The same refusal answers who is viewing: no one.
    vi.mocked(fetch).mockResolvedValueOnce(
      new Response("{}", {
        status: 401,
        headers: { "content-type": "application/json" },
      }),
    );
    const who = await routes["/api/viewer"](
      new Request(`${origin}/api/viewer`, { headers: { Cookie: cookie } }),
    );
    expect(await who.json()).toEqual({ available: true, person: null });
    expect(who.headers.get("set-cookie")).toMatch(/Max-Age=0/);
  });

  it("says who the session speaks for, as the engine reads it from the credential", async () => {
    on();
    const { cookie, credential } = await open();
    vi.mocked(fetch).mockResolvedValueOnce(rpc({ person: "alex" }));
    const who = await routes["/api/viewer"](
      new Request(`${origin}/api/viewer`, { headers: { Cookie: cookie } }),
    );
    expect(await who.json()).toEqual({
      available: true,
      person: { id: "alex", name: "Alex" },
    });
    expect(bearer()).toBe(`Bearer ${credential}`);
    expect(called(vi.mocked(fetch).mock.calls.at(-1)?.[1])).toEqual({
      name: "app_viewer",
      arguments: { op: "whoami" },
    });
    // Without a session: the household view, and the engine is not asked.
    const calls = vi.mocked(fetch).mock.calls.length;
    const nobody = await routes["/api/viewer"](
      new Request(`${origin}/api/viewer`),
    );
    expect(await nobody.json()).toEqual({ available: true, person: null });
    expect(vi.mocked(fetch).mock.calls.length).toBe(calls);
  });

  it("refuses a code that does not work and any other shape, setting no cookie", async () => {
    on();
    vi.mocked(fetch).mockResolvedValueOnce(rpc({ person: null }));
    const refused = await viewer({ op: "enrol", code: "WRONG-CODE" });
    expect(refused.status).toBe(400);
    expect(refused.headers.get("set-cookie")).toBeNull();
    const calls = vi.mocked(fetch).mock.calls.length;
    for (const body of [
      { op: "enrol" },
      { op: "enrol", code: "ABCD-EFGH-JKMN", person: "alex" },
      { op: "enrol", code: "x".repeat(41) },
      { op: "become", person: "alex" },
      { op: "end", person: "alex" },
      ["enrol"],
    ]) {
      const response = await viewer(body);
      expect(response.status).toBe(400);
      expect(response.headers.get("set-cookie")).toBeNull();
    }
    expect(vi.mocked(fetch).mock.calls.length).toBe(calls);
    // A cross-site request is refused before anything else.
    const crossSite = await routes["/api/viewer"](
      new Request(`${origin}/api/viewer`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          Origin: "https://elsewhere.example.invalid",
        },
        body: JSON.stringify({ op: "enrol", code: "ABCD-EFGH-JKMN" }),
      }),
    );
    expect(crossSite.status).toBe(403);
  });

  it("signs out: the engine ends the credential and the cookie is cleared", async () => {
    on();
    const { cookie, credential } = await open();
    vi.mocked(fetch).mockResolvedValueOnce(rpc({ ended: true }));
    const out = await viewer({ op: "end" }, cookie);
    expect(await out.json()).toEqual({ available: true, person: null });
    expect(out.headers.get("set-cookie")).toMatch(
      /^rhythm-viewer=; .*Max-Age=0/,
    );
    expect(bearer()).toBe(`Bearer ${credential}`);
    expect(called(vi.mocked(fetch).mock.calls.at(-1)?.[1])).toEqual({
      name: "app_viewer",
      arguments: { op: "end" },
    });
  });

  it("offers nothing when the engine does not list viewer sessions", async () => {
    on(allAppTools);
    const status = await routes["/api/viewer"](
      new Request(`${origin}/api/viewer`),
    );
    expect(await status.json()).toEqual({ available: false });
    const enrol = await viewer({ op: "enrol", code: "ABCD-EFGH-JKMN" });
    expect(enrol.status).toBe(404);
    expect(enrol.headers.get("set-cookie")).toBeNull();
  });

  it("clears a session's cookie once the engine stops offering viewer sessions", async () => {
    on();
    const { cookie } = await open();
    on(allAppTools);
    const status = await routes["/api/viewer"](
      new Request(`${origin}/api/viewer`, { headers: { Cookie: cookie } }),
    );
    expect(await status.json()).toEqual({ available: false });
    expect(status.headers.get("set-cookie")).toMatch(
      /^rhythm-viewer=; .*Max-Age=0/,
    );
  });

  it("keeps a session's cookie when the engine can't be asked whether it offers sessions", async () => {
    on();
    const { cookie } = await open();
    // No primed listing: the facade asks the engine, which can't be reached (down or restarting).
    engine.resetEngine();
    vi.mocked(fetch).mockRejectedValue(new TypeError("fetch failed"));
    const status = await routes["/api/viewer"](
      new Request(`${origin}/api/viewer`, { headers: { Cookie: cookie } }),
    );
    expect(status.status).toBe(502);
    expect(await status.json()).toEqual({
      error: "Source response unavailable or invalid",
    });
    expect(status.headers.get("set-cookie")).toBeNull();
    // Within the retry window the failed listing is remembered: still unknown, still kept.
    const again = await routes["/api/viewer"](
      new Request(`${origin}/api/viewer`, { headers: { Cookie: cookie } }),
    );
    expect(again.status).toBe(502);
    expect(again.headers.get("set-cookie")).toBeNull();
    // Once a listing comes back with sessions in it, the session answers as before.
    vi.mocked(fetch).mockReset();
    engine.resetEngine([...allAppTools, "app_viewer"]);
    vi.mocked(fetch).mockResolvedValueOnce(rpc({ person: "alex" }));
    const back = await routes["/api/viewer"](
      new Request(`${origin}/api/viewer`, { headers: { Cookie: cookie } }),
    );
    expect(await back.json()).toEqual({
      available: true,
      person: { id: "alex", name: "Alex" },
    });
    expect(back.headers.get("set-cookie")).toBeNull();
  });

  it("derives a session's credential from its handle and the app token alone", () => {
    const session = { issued: 1793001600, handle: "h".repeat(43) };
    const expected = createHmac("sha256", appToken)
      .update(
        `rhythm-viewer-credential-v1\n${session.issued}\n${session.handle}`,
      )
      .digest("base64url");
    expect(credentialOf(appToken, session)).toBe(expected);
    expect(credentialOf(`${appToken}-rotated`, session)).not.toBe(expected);
  });

  describe("a viewer session's uploads (SB-P1-20)", () => {
    const document = {
      graphId: "rhythm:documents:alex%3Aupload%3Aabc",
      resourceId: "6d3c2a10-0000-4000-8000-00000000d002",
      title: "Allotment rota",
      format: "text",
      pages: 1,
      characters: 36,
      status: "created",
    };
    const content = btoa("DEMO watering rota: Alex on Mondays.");
    const upload = (body: unknown, cookie?: string) =>
      routes["/api/documents"](
        new Request(`${origin}/api/documents`, {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
            ...(cookie ? { Cookie: cookie } : {}),
          },
          body: JSON.stringify(body),
        }),
      );

    it("hands the engine a session's Family choice with its own credential, and the scope it was kept in back", async () => {
      on();
      const { cookie, credential } = await open();
      for (const [family, scope] of [
        [undefined, "private"],
        [false, "private"],
        [true, "shared"],
      ] as const) {
        vi.mocked(fetch).mockResolvedValueOnce(
          rpc({ ok: true, document: { ...document, scope } }),
        );
        const body =
          family === undefined
            ? { name: "rota.txt", content }
            : { name: "rota.txt", content, family };
        const answer = await (await upload(body, cookie)).json();
        expect(answer).toEqual({
          ok: true,
          document: {
            graphId: document.graphId,
            title: document.title,
            status: "created",
            format: "text",
            pages: 1,
            characters: 36,
            scope,
          },
        });
        expect(bearer()).toBe(`Bearer ${credential}`);
        expect(
          called(vi.mocked(fetch).mock.calls.at(-1)?.[1]).arguments,
        ).toEqual(body);
      }
      // Only a boolean, and nothing else beside the file.
      vi.mocked(fetch).mockClear();
      for (const body of [
        { name: "rota.txt", content, family: "yes" },
        { name: "rota.txt", content, family: true, person: "sam" },
        { name: "rota.txt", content, scope: "private" },
      ])
        expect((await upload(body, cookie)).status).toBe(400);
      expect(fetch).not.toHaveBeenCalled();
    });

    it("takes no Family choice from the household view: its body is {name, content}, as before", async () => {
      on();
      vi.mocked(fetch).mockClear();
      expect(
        (await upload({ name: "rota.txt", content, family: true })).status,
      ).toBe(400);
      expect(fetch).not.toHaveBeenCalled();
      vi.mocked(fetch).mockResolvedValueOnce(rpc({ ok: true, document }));
      expect(
        (await (await upload({ name: "rota.txt", content })).json()).ok,
      ).toBe(true);
      expect(bearer()).toBe(`Bearer ${appToken}`);
      expect(called(vi.mocked(fetch).mock.calls.at(-1)?.[1]).arguments).toEqual(
        {
          name: "rota.txt",
          content,
        },
      );
    });
  });
});
