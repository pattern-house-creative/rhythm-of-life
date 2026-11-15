import { assertObservatory } from "../src/validation.js";
import { emptyBody, json, requestGuard } from "./http.js";
import { loadHealth } from "./health.js";
import { loadAdditionalHealth, requestGarminSync } from "./additionalHealth.js";
import { handleGraph } from "./graph.js";
import { handleInsights } from "./insights.js";
import { handleDashboards } from "./dashboards.js";
import { handleScreens } from "./screens.js";
import { requestRebuild } from "./graphRefresh.js";
import { handleDocuments, maxUploadBody } from "./documents.js";
import { handleDocumentFile } from "./documentFile.js";
import { handleStatus } from "./status.js";
import { handleSync } from "./sync.js";
import { handleMemories } from "./memories.js";
import { maxMemoryBody } from "../src/memories.js";
import { catalogProvider, settings } from "./config.js";
import { answerRejected, engineMode } from "./engine.js";
import { withViewerSessions } from "./viewer.js";

// The app's own /api routes: web-standard Request-to-Response handlers, run by the Node
// server (server/node.ts), by Vite in development (server/devApi.ts) and by a hosted front end
// (api/, deploy/vercel/). The browser reaches nothing else; only these talk to the engine.

// GET /api/instance: this install's name, people and time zone, from rhythm.yaml, whether an
// engine is configured, and the provider id of the engine's own stars (RHYTHM_CATALOG_PROVIDER;
// the demo's own are "rhythm") (contracts/README.md section 1.3). Never read from the engine.
export async function handleInstance(request: Request) {
  const rejected = requestGuard(request, "GET");
  if (rejected) return rejected;
  try {
    const s = settings();
    const mode = engineMode();
    return json({
      name: s.name,
      people: s.people.map((p) => ({ id: p.id, name: p.name })),
      timezone: s.timezone,
      mode,
      catalogProvider: mode === "demo" ? "rhythm" : catalogProvider(),
    });
  } catch {
    return json({ error: "Settings unavailable" }, 503);
  }
}

// GET /api/health: the app itself is up. It calls nothing upstream, so a slow or absent engine
// never makes the container unhealthy. The first one in each process logs the Node version it
// runs on, so a hosted front end's runtime shows in its log (package.json pins Node 24.x).
let runtimeLogged = false;
export async function handleHealth(request: Request) {
  const rejected = requestGuard(request, "GET");
  if (rejected) return rejected;
  if (!runtimeLogged) {
    runtimeLogged = true;
    console.info("rhythm-app-runtime", { node: process.version });
  }
  return json({ status: "ok", mode: engineMode() });
}

// GET /api/observatory: the latest glucose reading (or the demo dataset), ?source=garmin for
// Garmin's summary, ?source=history for 14 days of glucose.
export async function handleObservatory(request: Request) {
  const rejected = requestGuard(request, "GET");
  if (rejected) return rejected;
  const source = new URL(request.url).searchParams.get("source");
  try {
    if (source && source !== "garmin" && source !== "history")
      return json({ error: "Unknown source" }, 400);
    const data =
      source === "garmin" || source === "history"
        ? await loadAdditionalHealth(source, request)
        : await loadHealth(request.signal);
    // The app's mode, not the reading's: the engine's own demo seed is live data labelled DEMO,
    // and its history and other parts load like any source's.
    data.healthMode = engineMode() === "demo" ? "demo" : "live";
    assertObservatory(data);
    return json(data);
  } catch (error) {
    answerRejected(
      source === "garmin"
        ? "app_garmin_summary"
        : source === "history"
          ? "app_recent_glucose"
          : "app_latest_glucose",
      error,
      request.signal,
    );
    return json({ error: "Source response unavailable or invalid" }, 502);
  }
}

// POST /api/garmin-sync: asks the engine for a Garmin sync now. Always answers the same four
// fields; an unavailable or refused request is just "not accepted".
export async function handleGarminSync(request: Request) {
  const rejected = requestGuard(request, "POST");
  if (rejected) return rejected;
  if (!(await emptyBody(request)))
    return json({ error: "No request body expected" }, 400);
  return json(await requestGarminSync(request));
}

// POST /api/graph-refresh: asks the engine to rebuild the map now. Always answers the same five
// fields; an unavailable or refused request is just "not accepted".
export async function handleGraphRefresh(request: Request) {
  const rejected = requestGuard(request, "POST");
  if (rejected) return rejected;
  if (!(await emptyBody(request)))
    return json({ error: "No request body expected" }, 400);
  return json(await requestRebuild(request));
}

// /api/status, /api/sync and /api/memories from one hosted function, app/api/sources.ts
// (deploy/vercel/): Vercel Hobby deploys at most twelve functions, so vercel.json rewrites each
// path to it, naming the route (?route=status, ?route=sync or ?route=memories). Which one is the
// path the browser asked for when the function sees it, else the route the rewrite named; any
// other is 404. The Node server and Vite route each path to its own handler and never come here.
export async function handleSources(request: Request) {
  const url = new URL(request.url);
  const route =
    url.pathname === "/api/status" ||
    url.pathname === "/api/sync" ||
    url.pathname === "/api/memories"
      ? url.pathname.slice("/api/".length)
      : url.searchParams.get("route");
  if (route === "status") return handleStatus(request);
  if (route === "sync") return handleSync(request);
  if (route === "memories") return handleMemories(request);
  return json({ error: "Not found" }, 404);
}

const handlers: Record<string, (request: Request) => Promise<Response>> = {
  "/api/instance": handleInstance,
  "/api/health": handleHealth,
  "/api/observatory": handleObservatory,
  "/api/graph": (request) => handleGraph(request),
  "/api/insights": (request) => handleInsights(request),
  "/api/dashboards": (request) => handleDashboards(request),
  "/api/graph-refresh": handleGraphRefresh,
  "/api/garmin-sync": handleGarminSync,
  "/api/documents": (request) => handleDocuments(request),
  "/api/document-file": (request) => handleDocumentFile(request),
  "/api/screens": (request) => handleScreens(request),
  "/api/status": (request) => handleStatus(request),
  // Sync now from Sources & settings (SB-P6-09): the list (GET) and one batch now (POST).
  "/api/sync": (request) => handleSync(request),
  // The Memories view (SB-P3-12), only while the engine's memory.tools is on: GET whether, POST one op.
  "/api/memories": (request) => handleMemories(request),
};

// The route table: the handlers above, and /api/viewer. Only while app.viewers is on (which this
// version refuses) does a viewer session's request reach the engine with its own credential
// (viewer.ts); while it is off each route answers as it did.
export const routes = withViewerSessions(handlers);

// The largest request body a route accepts; the server refuses more before a handler runs.
// Only an added document (its file as base64) and a memory's whole text are larger than 128 KiB.
export const maxBody = 131072;
export const bodyLimit = (path: string) =>
  path === "/api/documents"
    ? maxUploadBody
    : path === "/api/memories"
      ? maxMemoryBody
      : maxBody;
