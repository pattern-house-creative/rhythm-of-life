import { request as httpRequest } from "node:http";
import type { AddressInfo } from "node:net";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, expect, it, vi } from "vitest";
import { createAppServer, hostAllowed } from "../server/node";
import { parseSettings, resetSettings, SettingsError } from "../server/config";

// The Node server answers only to the install's own names, so a page whose DNS was turned to point
// at this server (DNS rebinding) cannot read or write through it. Fictional names only.
afterEach(() => {
  vi.restoreAllMocks();
});

const settingsText = (extra: string[] = []) =>
  [
    "version: 1",
    "timezone: Europe/London",
    "people:",
    "  - id: alex",
    "    name: Alex",
    "engine:",
    "  url: ''",
    ...extra,
  ].join("\n");

it("answers to localhost, IP addresses and the install's own names only", () => {
  const names = ["rhythm.example.invalid"];
  for (const host of [
    "localhost",
    "localhost:8080",
    // Other containers on the stack's network, such as rhythm doctor's check.
    "app:8080",
    "127.0.0.1:8080",
    "[::1]:8080",
    "192.0.2.10:8080",
    "[2001:db8::1]",
    "rhythm.example.invalid",
    "RHYTHM.example.invalid:443",
    "rhythm.example.invalid.",
  ])
    expect(hostAllowed(host, names), host).toBe(true);
  for (const host of [
    undefined,
    "",
    "rebind.example.invalid:8080",
    "rhythm.example.invalid.rebind.example.invalid",
    "192.0.2.10.rebind.example.invalid",
    "localhost.rebind.example.invalid",
    "app.rebind.example.invalid",
    "user@rhythm.example.invalid",
  ])
    expect(hostAllowed(host, names), String(host)).toBe(false);
});

it("takes the names from app.allowedHosts, app.publicUrl, engine.allowedHosts and the Cloudflare host", () => {
  expect(parseSettings(settingsText()).hosts).toEqual([]);
  expect(
    parseSettings(
      settingsText([
        "  allowedHosts: [localhost, 127.0.0.1, '[::1]', engine, Rhythm-MCP.example.invalid]",
        "app:",
        "  publicUrl: https://rhythm.example.invalid",
        "  allowedHosts: [nas.example.invalid, 'home.example.invalid:8080']",
        "adapters:",
        "  cloudflare:",
        "    enabled: true",
        "    hostname: tunnel.example.invalid",
      ]),
    ).hosts,
  ).toEqual([
    "nas.example.invalid",
    "home.example.invalid",
    "rhythm.example.invalid",
    "localhost",
    "127.0.0.1",
    "[::1]",
    "engine",
    "rhythm-mcp.example.invalid",
    "tunnel.example.invalid",
  ]);
  for (const bad of [
    "  allowedHosts: []",
    "  allowedHosts: nas.example.invalid",
    "  allowedHosts: ['http://nas.example.invalid/']",
    "  allowedHosts: [12]",
  ])
    try {
      parseSettings(settingsText(["app:", bad]));
      expect.unreachable(bad);
    } catch (error) {
      expect((error as SettingsError).issues).toEqual(["app.allowedHosts"]);
    }
});

it("refuses a request for another host name before any route, and serves its own", async () => {
  const dir = mkdtempSync(join(tmpdir(), "rhythm-hosts-"));
  writeFileSync(join(dir, "index.html"), '<div id="root"></div>');
  writeFileSync(
    join(dir, "rhythm.yaml"),
    settingsText(["app:", "  allowedHosts: [rhythm.example.invalid]"]),
  );
  vi.stubEnv("RHYTHM_CONFIG", join(dir, "rhythm.yaml"));
  resetSettings();
  const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
  const server = createAppServer(dir);
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const port = (server.address() as AddressInfo).port;
  const call = (host: string, path: string, method = "GET") =>
    new Promise<{ status: number; text: string }>((resolve, reject) => {
      const origin = `http://${host}`;
      const req = httpRequest(
        {
          host: "127.0.0.1",
          port,
          path,
          method,
          headers: {
            host,
            ...(method === "POST"
              ? { origin, "content-type": "application/json" }
              : {}),
          },
        },
        (res) => {
          const chunks: Buffer[] = [];
          res.on("data", (c) => chunks.push(c));
          res.on("end", () =>
            resolve({
              status: res.statusCode ?? 0,
              text: Buffer.concat(chunks).toString("utf8"),
            }),
          );
        },
      );
      req.on("error", reject);
      req.end(method === "POST" ? '{"operation":"discover"}' : undefined);
    });
  try {
    // A page at a rebinding name: same origin as far as the routes can tell, so refused here.
    const rebound = `rebind.example.invalid:${port}`;
    const refused = await call(rebound, "/api/graph", "POST");
    expect(refused.status).toBe(421);
    expect(refused.text).toContain("app.allowedHosts");
    expect((await call(rebound, "/api/instance")).status).toBe(421);
    expect((await call(rebound, "/")).status).toBe(421);
    expect(warn).toHaveBeenCalledWith("rhythm-app-refused", {
      reason: "host_not_allowed",
    });
    expect(JSON.stringify(warn.mock.calls)).not.toContain("rebind");
    // Its own names and addresses.
    expect((await call(`127.0.0.1:${port}`, "/api/health")).status).toBe(200);
    expect((await call(`localhost:${port}`, "/")).status).toBe(200);
    // rhythm doctor calls http://app:8080/api/health from the stack's network.
    expect((await call("app:8080", "/api/health")).status).toBe(200);
    const own = await call(
      `rhythm.example.invalid:${port}`,
      "/api/graph",
      "POST",
    );
    expect(own.status).toBe(200);
  } finally {
    server.close();
    rmSync(dir, { recursive: true, force: true });
  }
});

it("answers a request body too large with 413, which reaches the browser while it is still sending", async () => {
  const dir = mkdtempSync(join(tmpdir(), "rhythm-large-"));
  writeFileSync(join(dir, "index.html"), '<div id="root"></div>');
  const server = createAppServer(dir);
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const port = (server.address() as AddressInfo).port;
  // `rest` is sent only once an answer came, as a browser on a slow link is still sending.
  const call = (path: string, body?: string, rest?: string) =>
    new Promise<{ status: number; text: string }>((resolve, reject) => {
      const req = httpRequest(
        {
          host: "127.0.0.1",
          port,
          path,
          method: body ? "POST" : "GET",
          // A connection of its own each time.
          agent: false,
          headers: {
            host: `127.0.0.1:${port}`,
            ...(body ? { "content-type": "application/json" } : {}),
          },
        },
        (res) => {
          const chunks: Buffer[] = [];
          res.on("data", (c) => chunks.push(c));
          res.on("end", () => {
            resolve({
              status: res.statusCode ?? 0,
              text: Buffer.concat(chunks).toString("utf8"),
            });
            if (rest !== undefined) req.end(rest);
          });
        },
      );
      req.on("error", reject);
      if (rest === undefined) req.end(body);
      else req.write(body);
    });
  // About what a long-explored map sent on refresh before its ids were capped: over 128 KiB.
  const body = JSON.stringify({
    operation: "refresh",
    ids: Array.from(
      { length: 1400 },
      (_, i) => `memory-${i}-${"x".repeat(90)}`,
    ),
    edgeIds: Array.from(
      { length: 2000 },
      (_, i) => `link-${i}-${"y".repeat(60)}`,
    ),
  });
  expect(body.length).toBeGreaterThan(2 * 131072);
  try {
    const half = Math.ceil(body.length / 2);
    const answer = await call(
      "/api/graph",
      body.slice(0, half),
      body.slice(half),
    );
    expect(answer.status).toBe(413);
    expect(JSON.parse(answer.text)).toEqual({ error: "Request too large" });
    // The server goes on answering.
    expect((await call("/api/health")).status).toBe(200);
  } finally {
    server.close();
    rmSync(dir, { recursive: true, force: true });
  }
});
