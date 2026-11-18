import { readFileSync } from "node:fs";
import { join } from "node:path";
import { afterEach, expect, it, vi } from "vitest";
import { handleHealth } from "../server/routes";

// The app runs on one Node major everywhere: the image, a hosted front end (which takes
// package.json's engines over its own setting) and the types it is built against. A range that
// floats lets a host move the live app to a major nothing here tests.
afterEach(() => {
  vi.restoreAllMocks();
});
const app = join(__dirname, "..");
const read = (name: string) => readFileSync(join(app, name), "utf8");

it("pins one Node major in engines, the image and the types", () => {
  const pkg = JSON.parse(read("package.json"));
  const lock = JSON.parse(read("package-lock.json"));
  expect(pkg.engines.node).toBe("24.x");
  expect(lock.packages[""].engines.node).toBe("24.x");
  expect(read("Dockerfile")).toMatch(/^ARG NODE_IMAGE=node:24-alpine@sha256:[0-9a-f]{64}$/m);
  expect(pkg.devDependencies["@types/node"]).toMatch(/^\^24\./);
});

it("logs the Node version on the first health check of a process only", async () => {
  const info = vi.spyOn(console, "info").mockImplementation(() => {});
  const health = () =>
    handleHealth(new Request("http://rhythm.example.invalid/api/health"));
  expect(await (await health()).json()).toEqual({ status: "ok", mode: "demo" });
  await health();
  expect(info.mock.calls).toEqual([
    ["rhythm-app-runtime", { node: process.version }],
  ]);
});
