import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { test } from "node:test";

const compose = spawnSync("docker", ["compose", "version", "--short"], { encoding: "utf8" });
test("the supported exposure recipes isolate database access and the MCP-only tunnel", {
  skip: compose.status !== 0 && "Docker Compose is unavailable",
}, () => {
  const args = ["compose", "-f", "deploy/compose/compose.yaml", "-f", "deploy/truenas/truenas.compose.yaml",
    "-f", "deploy/exposure/reverse-proxy/compose.yaml", "--profile", "*", "config", "--format", "json"];
  const render = (cloudflareNetwork) => {
    const result = spawnSync("docker", args, {
      encoding: "utf8",
      env: { ...process.env, RHYTHM_PGDATA_DIR: "/mnt/example/pgdata", RHYTHM_ENGINE_DATA_DIR: "/mnt/example/engine",
        RHYTHM_CLOUDFLARE_NETWORK: cloudflareNetwork },
    });
    assert.equal(result.status, 0, result.stderr);
    return JSON.parse(result.stdout);
  };
  const { networks, services } = render("");
  const members = (service) => Object.keys(services[service].networks);
  const connected = (left, right) => members(left).some((name) => members(right).includes(name));
  assert.deepEqual(members("postgres"), ["database"]);
  assert.equal(networks.database.internal, true);
  assert.deepEqual(Object.keys(services).filter((s) => members(s).includes("database")).sort(),
    ["backup", "cli", "engine", "migrate", "postgres", "restore"]);
  for (const service of ["cloudflared", "tailscale", "caddy", "openai-tunnel"]) {
    assert.equal(connected(service, "engine"), true, service);
    assert.equal(connected(service, "postgres"), false, service);
    assert.equal(connected(service, "app"), service !== "openai-tunnel", service);
  }
  for (const target of ["app", "engine"]) assert.equal(connected("cli", target), true, target);
  assert.equal(connected("engine", "titles"), true);

  const mcpOnly = render("egress").services;
  const tunnelNetworks = Object.keys(mcpOnly.cloudflared.networks);
  assert.ok(tunnelNetworks.some((n) => n in mcpOnly.engine.networks));
  for (const service of ["app", "postgres"])
    assert.ok(tunnelNetworks.every((n) => !(n in mcpOnly[service].networks)), service);

});
