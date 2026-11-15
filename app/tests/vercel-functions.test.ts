import { readdirSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, expect, it, vi } from "vitest";
import { routes } from "../server/routes";
import { GET, OPTIONS, POST } from "../api/sources";
import { rpc, withEngine } from "./engine";

// SB-X-04: the hosted recipe (deploy/vercel/) deploys each app/api/*.ts as one Vercel Function, and
// Vercel Hobby takes at most twelve in a deployment. /api/status and /api/sync share one,
// app/api/sources.ts, through vercel.json's rewrites; a new route folds into a function in the
// same way rather than adding one.
afterEach(() => {
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
});
const app = (...path: string[]) => resolve(process.cwd(), ...path);
// Vercel serves no function from a file whose name starts with an underscore (a shared helper).
const functions = readdirSync(app("api")).filter(
  (n) => /\.[cm]?[jt]s$/.test(n) && !n.startsWith("_"),
);
const recipe = JSON.parse(readFileSync(app("vercel.json"), "utf8")) as {
  rewrites?: { source: string; destination: string }[];
  functions?: Record<string, unknown>;
};

it("deploys at most twelve functions", () => {
  expect(functions.length).toBeGreaterThan(0);
  expect(functions.length).toBeLessThanOrEqual(12);
});

it("reaches every route of the app's server, by its own function or a rewrite to one", () => {
  const rewrites = new Map(
    (recipe.rewrites ?? []).map((r) => [r.source, r.destination]),
  );
  const own = new Set(
    functions.map((n) => `/api/${n.replace(/\.[^.]+$/, "")}`),
  );
  for (const path of Object.keys(routes)) {
    // Viewer sessions are refused by this version (app.viewers, D-046); /api/viewer is the
    // Node server's own while they are.
    if (path === "/api/viewer") continue;
    const target = rewrites.get(path)?.split("?")[0];
    expect(own.has(path) !== Boolean(target), path).toBe(true);
    if (target) expect(own.has(target), `${path} -> ${target}`).toBe(true);
  }
  // Every rewrite lands on a function, and every function vercel.json configures exists.
  for (const target of rewrites.values())
    expect(own.has(target.split("?")[0]), target).toBe(true);
  for (const name of Object.keys(recipe.functions ?? {}))
    expect(functions, name).toContain(name.replace(/^api\//, ""));
});

const ask = (url: string, init: RequestInit = {}) =>
  new Request(`http://rhythm.example.invalid${url}`, init);
const read = async (answer: Promise<Response> | Response) => {
  const response = await answer;
  return { status: response.status, body: await response.json() };
};

it("answers /api/status, /api/sync and /api/memories through the rewrite as their own routes do", async () => {
  // Demo mode first, then a live engine; each path's answer is the same by either way in.
  const pairs: [string, string, RequestInit?][] = [
    ["/api/status", "/api/sources?route=status"],
    ["/api/sync", "/api/sources?route=sync"],
    [
      "/api/sync",
      "/api/sources?route=sync",
      {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ adapter: "drive" }),
      },
    ],
    ["/api/status", "/api/sources?route=status", { method: "POST" }],
    // The Memories view (SB-P3-12): its availability and one op, folded into the same function.
    ["/api/memories", "/api/sources?route=memories"],
    [
      "/api/memories",
      "/api/sources?route=memories",
      {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ op: "list" }),
      },
    ],
  ];
  const handler = (init?: RequestInit) =>
    init?.method === "POST" ? POST : GET;
  for (const live of [false, true]) {
    if (live) {
      withEngine();
      vi.mocked(fetch).mockImplementation(async () =>
        rpc({ version: "0.0.0-test", schema: 1, models: [], adapters: [] }),
      );
    }
    for (const [path, rewritten, init] of pairs) {
      const direct = await read(routes[path](ask(path, init)));
      // The function as Vercel runs it: the rewritten URL, or the path the browser asked for.
      expect(
        await read(handler(init)(ask(rewritten, init))),
        rewritten,
      ).toEqual(direct);
      expect(await read(handler(init)(ask(path, init))), path).toEqual(direct);
    }
  }
  expect((await read(GET(ask("/api/sources")))).status).toBe(404);
  expect((await read(GET(ask("/api/sources?route=graph")))).status).toBe(404);
  expect(
    (
      await read(
        OPTIONS(ask("/api/sources?route=status", { method: "OPTIONS" })),
      )
    ).status,
  ).toBe(
    (
      await read(
        routes["/api/status"](ask("/api/status", { method: "OPTIONS" })),
      )
    ).status,
  );
});
