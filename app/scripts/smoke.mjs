// Smoke check of the built app: runs build/server/node.js in real Node (not Vite's forgiving
// resolver) against dist/, first in demo mode with every outbound request forbidden, then in
// live mode against a stand-in engine on 127.0.0.1. Fictional data only; nothing leaves this
// computer. Run after `npm run build`.
import assert from "node:assert/strict";
import { createServer, request as httpRequest } from "node:http";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

for (const key of Object.keys(process.env))
  if (key.startsWith("RHYTHM_")) delete process.env[key];
const dir = mkdtempSync(join(tmpdir(), "rhythm-smoke-"));
process.env.RHYTHM_CONFIG = join(dir, "missing.yaml");
process.env.RHYTHM_APP_TOKEN_FILE = join(dir, "missing-token");

const { createAppServer } = await import("../build/server/node.js");
const { resetEngine } = await import("../build/server/engine.js");
const { resetSettings } = await import("../build/server/config.js");

const listen = (server) =>
  new Promise((resolve) =>
    server.listen(0, "127.0.0.1", () => resolve(server.address().port)),
  );
// Plain HTTP to the app, so the app's own fetch can be forbidden.
const call = (port, path, { method = "GET", headers = {}, body } = {}) =>
  new Promise((resolve, reject) => {
    const req = httpRequest(
      { host: "127.0.0.1", port, path, method, headers },
      (res) => {
        const chunks = [];
        res.on("data", (c) => chunks.push(c));
        res.on("end", () => {
          const text = Buffer.concat(chunks).toString("utf8");
          resolve({
            status: res.statusCode,
            headers: res.headers,
            text,
            json: () => JSON.parse(text),
          });
        });
      },
    );
    req.on("error", reject);
    req.end(body);
  });
const post = (port, path, value, headers = {}) =>
  call(port, path, {
    method: "POST",
    headers: { "content-type": "application/json", ...headers },
    body: value === undefined ? undefined : JSON.stringify(value),
  });

const realFetch = globalThis.fetch;
let outbound = 0;
globalThis.fetch = async () => {
  outbound++;
  throw new Error("Outbound requests are forbidden in demo mode");
};

const app = createAppServer();
const port = await listen(app);
try {
  // Demo mode: the built page, the fictional data, and no engine call.
  const page = await call(port, "/");
  assert.equal(page.status, 200);
  assert.match(page.headers["content-type"], /text\/html/);
  assert.equal(page.headers["x-frame-options"], "DENY");
  assert.match(page.text, /<div id="root">/);
  // The font files are shipped after npm prunes dev dependencies: their original
  // copyright and OFL must remain available in the distributed app as well.
  for (const font of ["dm-sans", "manrope"]) {
    const licence = await call(port, `/assets/LICENSE-${font}.txt`);
    assert.equal(licence.status, 200);
    assert.match(licence.headers["content-type"], /text\/plain/);
    assert.equal(
      licence.text,
      readFileSync(
        new URL(
          `../node_modules/@fontsource-variable/${font}/LICENSE`,
          import.meta.url,
        ),
        "utf8",
      ),
    );
  }
  const notices = await call(port, "/assets/THIRD-PARTY-NOTICES.txt");
  assert.equal(notices.status, 200);
  assert.match(notices.headers["content-type"], /text\/plain/);
  for (const name of [
    "react",
    "lucide-react",
    "undici",
    "yaml",
    "workbox-core",
  ])
    assert.ok(notices.text.includes(`=== ${name}@`), `Missing ${name} notice`);
  // Lucide includes Feather-derived icons; keep its full dual attribution.
  assert.ok(notices.text.includes("Cole Bemis"));
  const packages = await call(port, "/assets/third-party-packages.json");
  assert.equal(packages.status, 200);
  assert.ok(packages.json().every((p) => p.name && p.version && p.license));
  // Native launcher entries must ship with the same coherent web build.
  for (const entry of ["/launcher.html", "/launcher-card.html"]) {
    const companion = await call(port, entry);
    assert.equal(companion.status, 200);
    assert.match(companion.headers["content-type"], /text\/html/);
    assert.match(companion.text, /<div id="root">/);
  }
  assert.deepEqual((await call(port, "/launcher-companion.json")).json(), {
    version: 1,
    builtins: ["glucose"],
  });
  assert.equal((await call(port, "/some/view")).status, 200);
  assert.equal((await call(port, "/missing.js")).status, 404);
  assert.equal((await call(port, "/..%2f..%2fpackage.json")).status, 404);
  assert.equal((await call(port, "/api/unknown")).status, 404);
  const instance = await call(port, "/api/instance");
  assert.equal(instance.status, 200);
  assert.match(instance.headers["cache-control"], /no-store/);
  assert.deepEqual(instance.json(), {
    name: "Rhythm of Life",
    people: [
      { id: "alex", name: "Alex" },
      { id: "sam", name: "Sam" },
    ],
    timezone: "UTC",
    mode: "demo",
    catalogProvider: "rhythm",
  });
  assert.deepEqual((await call(port, "/api/health")).json(), {
    status: "ok",
    mode: "demo",
  });
  const health = (await call(port, "/api/observatory")).json();
  assert.equal(health.current.provenance.demo, true);
  assert.equal(health.healthMode, "demo");
  const graph = await post(port, "/api/graph", { operation: "discover" });
  assert.equal(graph.status, 200);
  assert.ok(graph.json().nodes.length > 0);
  assert.deepEqual((await call(port, "/api/insights")).json(), {
    insights: [],
    runs: [],
  });
  assert.deepEqual((await call(port, "/api/dashboards")).json(), {
    dashboards: [],
  });
  assert.deepEqual((await post(port, "/api/graph-refresh")).json(), {
    accepted: false,
    pendingSince: null,
    running: false,
    nextAllowedAt: null,
    lastFinishedAt: null,
  });
  // Screens are the browser's own in demo mode.
  assert.deepEqual(
    (
      await post(port, "/api/screens", {
        op: "list",
        screen: "fictional-screen-id",
      })
    ).json(),
    { available: false },
  );
  // Request hygiene: another site's POST is refused; the proxy's https origin is not.
  const host = `127.0.0.1:${port}`;
  assert.equal(
    (
      await post(
        port,
        "/api/graph",
        { operation: "discover" },
        {
          origin: "https://elsewhere.example.invalid",
        },
      )
    ).status,
    403,
  );
  assert.equal(
    (
      await post(
        port,
        "/api/graph",
        { operation: "discover" },
        {
          origin: `https://${host}`,
          "x-forwarded-proto": "https",
        },
      )
    ).status,
    200,
  );
  assert.equal(
    (await post(port, "/api/graph", "x".repeat(140_000))).status,
    413,
  );
  assert.equal(outbound, 0, "demo mode made an outbound request");
  console.log(
    "Smoke: demo mode passed (page, instance, health, graph, routes).",
  );

  // Live mode: a stand-in engine on this computer answers as the contract says.
  const token = ["fictional", "smoke", "token"].join("-");
  writeFileSync(join(dir, "app_token"), `${token}\n`);
  const seen = [];
  const files = [];
  // A fictional PDF, as the engine serves an archived original (contracts/README.md 1.5).
  const pdf = Buffer.from("%PDF-1.4\n% fictional smoke document\n%%EOF\n");
  const engine = createServer((req, res) => {
    const chunks = [];
    req.on("data", (c) => chunks.push(c));
    req.on("end", () => {
      if (req.method === "GET" && req.url.startsWith("/app/documents/")) {
        files.push({ url: req.url, headers: req.headers });
        res.writeHead(200, {
          "content-type": "application/pdf",
          "content-length": String(pdf.length),
          "content-disposition": 'inline; filename="Smoke manual.pdf"',
        });
        return res.end(pdf);
      }
      const body = JSON.parse(Buffer.concat(chunks).toString("utf8"));
      seen.push({ url: req.url, headers: req.headers, body });
      const result =
        body.method === "tools/list"
          ? { tools: [{ name: "app_graph" }, { name: "app_insights" }] }
          : {
              structuredContent:
                body.params.name === "app_graph"
                  ? {
                      nodes: [],
                      edges: [],
                      nextCursor: null,
                      completeness: "complete",
                      issues: [],
                    }
                  : { insights: [], runs: [] },
              content: [{ type: "text", text: "fictional" }],
              isError: false,
            };
      res.writeHead(200, { "content-type": "application/json" });
      res.end(JSON.stringify({ jsonrpc: "2.0", id: body.id, result }));
    });
  });
  const enginePort = await listen(engine);
  writeFileSync(
    join(dir, "rhythm.yaml"),
    [
      "version: 1",
      "instance:",
      "  name: Smoke home",
      "timezone: Europe/London",
      "people:",
      "  - id: alex",
      "    name: Alex",
      "engine:",
      `  url: http://127.0.0.1:${enginePort}`,
    ].join("\n"),
  );
  process.env.RHYTHM_CONFIG = join(dir, "rhythm.yaml");
  process.env.RHYTHM_APP_TOKEN_FILE = join(dir, "app_token");
  // An install that kept an older engine's ids for its own stars (docs/dev/migration-compat.md).
  process.env.RHYTHM_CATALOG_PROVIDER = "legacy";
  resetSettings();
  resetEngine();
  globalThis.fetch = realFetch;
  try {
    const live = (await call(port, "/api/instance")).json();
    assert.equal(live.mode, "live");
    assert.equal(live.name, "Smoke home");
    assert.equal(live.timezone, "Europe/London");
    assert.equal(live.catalogProvider, "legacy");
    const map = await post(port, "/api/graph", { operation: "discover" });
    assert.equal(map.status, 200);
    assert.deepEqual(map.json().nodes, []);
    // No glucose source is offered: Not connected, and no call for it.
    const latest = (await call(port, "/api/observatory")).json();
    assert.equal(latest.current.provenance.demo, false);
    assert.equal(
      latest.sources.find((s) => s.id === "glucose").status,
      "disconnected",
    );
    // A document's original streams through the app with the app's own headers.
    const id = "3f2b8c1e-4d5a-4b6c-9e7f-0a1b2c3d4e5f";
    const file = await call(port, `/api/document-file?id=${id}`);
    assert.equal(file.status, 200);
    assert.equal(file.headers["content-type"], "application/pdf");
    assert.equal(file.headers["x-content-type-options"], "nosniff");
    assert.equal(file.headers["x-frame-options"], "DENY");
    assert.match(file.headers["content-security-policy"], /sandbox/);
    assert.match(
      file.headers["content-disposition"],
      /^inline; filename="Smoke manual\.pdf"/,
    );
    assert.equal(file.text, pdf.toString("utf8"));
    assert.equal(files.length, 1);
    assert.equal(files[0].url, `/app/documents/${id}`);
    assert.equal(files[0].headers.authorization, `Bearer ${token}`);
    assert.equal(files[0].headers.origin, undefined);
    assert.equal(files[0].headers.cookie, undefined);
    const calls = seen.filter((s) => s.body.method === "tools/call");
    assert.deepEqual(
      calls.map((s) => s.body.params.name),
      ["app_graph"],
    );
    for (const s of seen) {
      assert.equal(s.url, "/mcp");
      assert.equal(s.headers.authorization, `Bearer ${token}`);
      assert.equal(s.headers["mcp-protocol-version"], "2025-03-26");
      assert.equal(s.headers.origin, undefined);
      assert.equal(s.headers.cookie, undefined);
    }
    console.log(
      "Smoke: live mode passed (engine client, token, discovery, a document's file).",
    );
  } finally {
    engine.close();
  }
} finally {
  app.close();
  rmSync(dir, { recursive: true, force: true });
}
