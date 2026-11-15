import { createServer } from "node:http";
import type { IncomingMessage, Server, ServerResponse } from "node:http";
import { createReadStream, realpathSync, statSync } from "node:fs";
import { Readable } from "node:stream";
import { pipeline } from "node:stream/promises";
import type { ReadableStream as WebStream } from "node:stream/web";
import { extname, resolve, sep } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { routes, bodyLimit } from "./routes.js";
import {
  catalogProvider,
  hostName,
  settings,
  SettingsError,
} from "./config.js";
import { engineMode } from "./engine.js";
import { warnOnce } from "./log.js";

// The app's own small server: the built PWA from dist/ plus the /api routes, the same
// web-standard handlers a hosted front end runs. No login: who reaches it is decided by where it
// listens (HOST, PORT) and what stands in front of it (docs/configuration.md).
//
//   node build/server/node.js
//
// HOST (default 127.0.0.1; the container sets 0.0.0.0), PORT (default 8080), RHYTHM_STATIC_DIR
// (default ../../dist next to this file). Settings: RHYTHM_CONFIG, RHYTHM_ENGINE_URL,
// RHYTHM_APP_TOKEN_FILE, RHYTHM_CATALOG_PROVIDER (docs/configuration.md, server/README.md).
// Nothing is written to disk. It answers only to the install's own names (hostAllowed), as the
// engine does with engine.allowedHosts.

const security: Record<string, string> = {
  "X-Content-Type-Options": "nosniff",
  "Referrer-Policy": "no-referrer",
  "Permissions-Policy": "camera=(), microphone=(), geolocation=()",
  "X-Frame-Options": "DENY",
};
const types: Record<string, string> = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".mjs": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".webmanifest": "application/manifest+json; charset=utf-8",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".ico": "image/x-icon",
  ".woff2": "font/woff2",
  ".woff": "font/woff",
  ".txt": "text/plain; charset=utf-8",
  ".map": "application/json; charset=utf-8",
  ".wasm": "application/wasm",
};

function send(
  res: ServerResponse,
  status: number,
  body: string,
  headers: Record<string, string> = {},
) {
  res.writeHead(status, {
    ...security,
    "Content-Type": "application/json; charset=utf-8",
    "Cache-Control": "no-store",
    ...headers,
  });
  res.end(body);
}

// The request as a web-standard Request. The scheme follows X-Forwarded-Proto from a proxy in
// front (only http or https), so the handlers' same-origin check sees the browser's origin.
// `signal` is the browser's disconnect; Vercel passes one on only with supportsCancellation.
// The body's chunks, or 413 as soon as it passes the limit. A body over the limit is answered at
// once and the rest of it read and dropped (up to 1 MiB more, then the connection is closed), so
// the browser sees the 413 instead of a connection cut off while it was still sending.
function readBody(
  req: IncomingMessage,
  limit: number,
): Promise<Buffer[] | 413> {
  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = [];
    let size = 0;
    const take = (chunk: Buffer) => {
      size += chunk.length;
      if (size <= limit) return void chunks.push(Buffer.from(chunk));
      req.off("data", take);
      chunks.length = 0;
      let dropped = 0;
      req.on("data", (rest: Buffer) => {
        dropped += rest.length;
        if (dropped > 1_048_576) req.destroy();
      });
      resolve(413);
    };
    req.on("data", take);
    req.once("end", () => resolve(chunks));
    req.once("error", reject);
    // A connection that closes before the body ended (after an end or a 413 this does nothing).
    req.once("close", () => reject(new Error("Request closed")));
  });
}

async function toRequest(
  req: IncomingMessage,
  limit: number,
  signal?: AbortSignal,
): Promise<Request | number> {
  const chunks = await readBody(req, limit);
  if (chunks === 413) return 413;
  const size = chunks.reduce((sum, chunk) => sum + chunk.length, 0);
  const forwarded = String(req.headers["x-forwarded-proto"] ?? "")
    .split(",")[0]
    .trim();
  const scheme =
    forwarded === "https" || forwarded === "http" ? forwarded : "http";
  const host = req.headers.host;
  if (!host || !/^[A-Za-z0-9.:[\]-]{1,300}$/.test(host)) return 400;
  const headers = new Headers();
  for (const [key, value] of Object.entries(req.headers))
    if (value) headers.set(key, Array.isArray(value) ? value.join(",") : value);
  const method = req.method ?? "GET";
  return new Request(`${scheme}://${host}${req.url ?? "/"}`, {
    method,
    headers,
    signal,
    ...(size && method !== "GET" && method !== "HEAD"
      ? { body: Buffer.concat(chunks) }
      : {}),
  });
}

async function api(req: IncomingMessage, res: ServerResponse, path: string) {
  const handler = Object.hasOwn(routes, path) ? routes[path] : undefined;
  if (!handler) {
    req.resume();
    return send(res, 404, JSON.stringify({ error: "Not found" }));
  }
  // A browser that leaves (a dropped long wait on /api/screens, a closed tab) ends the request's
  // engine call too, instead of leaving it to run out its time.
  const gone = new AbortController();
  res.on("close", () => {
    if (!res.writableFinished) gone.abort();
  });
  const request = await toRequest(req, bodyLimit(path), gone.signal);
  if (typeof request === "number")
    return send(
      res,
      request,
      JSON.stringify({
        error: request === 413 ? "Request too large" : "Bad request",
      }),
    );
  const response = await handler(request);
  // One of each header: the handler's (always lower case) win over the defaults.
  const headers: Record<string, string> = Object.fromEntries(
    Object.entries(security).map(([key, value]) => [key.toLowerCase(), value]),
  );
  response.headers.forEach((value, key) => (headers[key] = value));
  res.writeHead(response.status, headers);
  // Streamed as it comes, so a document's original (up to 20 MiB) is never held here whole.
  if (!response.body) return res.end();
  await pipeline(Readable.fromWeb(response.body as WebStream), res);
}

// Localhost, `app` (this service's name on the stack's own network, which `rhythm doctor` calls),
// any IP address and the install's own host names (config.ts). A page reaches this server by an IP
// address only when it was served from that address, so an address cannot be used for DNS
// rebinding; any other name in Host is a site whose DNS was turned to point here, and its
// same-origin requests would pass the routes' origin check. Refused before any route.
export function hostAllowed(
  header: string | undefined,
  names: readonly string[],
): boolean {
  const host = hostName(header);
  return (
    !!host &&
    (host === "localhost" ||
      host === "app" ||
      host.startsWith("[") ||
      /^\d{1,3}(\.\d{1,3}){3}$/.test(host) ||
      names.includes(host))
  );
}

function staticFile(
  req: IncomingMessage,
  res: ServerResponse,
  root: string,
  path: string,
) {
  if (req.method !== "GET" && req.method !== "HEAD") {
    req.resume();
    return send(res, 405, JSON.stringify({ error: "Method not allowed" }), {
      Allow: "GET, HEAD",
    });
  }
  let decoded: string;
  try {
    decoded = decodeURIComponent(path);
  } catch {
    return send(res, 400, JSON.stringify({ error: "Bad request" }));
  }
  const candidate = resolve(root, `.${decoded}`);
  let file: string | null = null;
  if (
    !decoded.includes("\0") &&
    (candidate === root || candidate.startsWith(root + sep))
  )
    try {
      const real = realpathSync(candidate);
      if (real.startsWith(root + sep) && statSync(real).isFile()) file = real;
    } catch {
      file = null;
    }
  // A page the app routes itself falls back to index.html; a missing asset is a 404.
  if (!file) {
    if (extname(decoded) && !decoded.endsWith(".html"))
      return send(res, 404, "Not found", {
        "Content-Type": "text/plain; charset=utf-8",
      });
    file = resolve(root, "index.html");
  }
  const type = types[extname(file)] ?? "application/octet-stream";
  const immutable = file.startsWith(resolve(root, "assets") + sep);
  let size: number;
  try {
    size = statSync(file).size;
  } catch {
    return send(res, 404, "Not found", {
      "Content-Type": "text/plain; charset=utf-8",
    });
  }
  res.writeHead(200, {
    ...security,
    "Content-Type": type,
    "Content-Length": String(size),
    "Cache-Control": immutable
      ? "public, max-age=31536000, immutable"
      : "no-cache",
  });
  if (req.method === "HEAD") return res.end();
  createReadStream(file)
    .on("error", () => res.destroy())
    .pipe(res);
}

export function createAppServer(
  staticDir = process.env.RHYTHM_STATIC_DIR ||
    fileURLToPath(new URL("../../dist", import.meta.url)),
): Server {
  let root: string;
  try {
    root = realpathSync(resolve(staticDir));
  } catch {
    root = resolve(staticDir);
  }
  return createServer((req, res) => {
    let names: readonly string[] = [];
    try {
      names = settings().hosts;
    } catch {
      /* settings unreadable: localhost and IP addresses only */
    }
    if (!hostAllowed(req.headers.host, names)) {
      req.resume();
      warnOnce("rhythm-app-refused", { reason: "host_not_allowed" });
      return send(
        res,
        421,
        JSON.stringify({
          error:
            "This install doesn't answer to that host name; add it to app.allowedHosts in rhythm.yaml.",
        }),
      );
    }
    const path = (req.url ?? "/").split("?")[0] || "/";
    const work =
      path === "/api" || path.startsWith("/api/")
        ? api(req, res, path)
        : Promise.resolve(staticFile(req, res, root, path));
    work.catch(() => {
      // Never log request bodies, upstream responses or credentials.
      if (!res.headersSent)
        send(res, 500, JSON.stringify({ error: "Server error" }));
      else res.destroy();
    });
  });
}

function main() {
  try {
    settings();
  } catch (error) {
    console.error(
      error instanceof SettingsError
        ? `${error.message}. Fix the file and start again.`
        : "rhythm.yaml could not be read.",
    );
    process.exit(1);
  }
  try {
    catalogProvider();
  } catch (error) {
    console.error(`${(error as Error).message}. Fix it and start again.`);
    process.exit(1);
  }
  const host = process.env.HOST || "127.0.0.1";
  const port = Number(process.env.PORT || 8080);
  const server = createAppServer();
  server.listen(port, host, () =>
    console.log(
      `Rhythm of Life app on http://${host.includes(":") ? `[${host}]` : host}:${port} (${engineMode()} mode)`,
    ),
  );
  const stop = () => {
    server.close(() => process.exit(0));
    setTimeout(() => process.exit(0), 5000).unref();
  };
  process.on("SIGTERM", stop);
  process.on("SIGINT", stop);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href)
  main();
