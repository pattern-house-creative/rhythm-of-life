import { adapterPattern, cleanAsked, cleanSyncs } from "../src/sync.js";
import { json, readJson, requestGuard } from "./http.js";
import { answerRejected, callTool, EngineError, engineMode } from "./engine.js";

const object = (v: unknown): v is Record<string, unknown> =>
  !!v && typeof v === "object" && !Array.isArray(v);

// Sync now, for Sources & settings (SB-P6-09; app_sync, contracts/README.md 2.2). GET /api/sync:
// the adapters on the engine's own clock, {available: true, adapters}. POST /api/sync {adapter}:
// asks one of them for a batch now, {available: true, accepted, refused, adapter}; the engine
// runs it between its own jobs, and a later GET says how it ended. In demo mode there is no
// engine, so nothing to sync and no call: {available: false}. An engine older than app_sync, a
// call that fails or answers outside the contract, and an adapter the engine doesn't offer this
// caller are {available: false} too; a failure is logged by its kind only (engine.ts). Nothing
// is kept, and a request is never sent twice.
export async function handleSync(
  request: Request,
  fetcher: typeof fetch = fetch,
) {
  if (request.method === "POST") return ask(request, fetcher);
  const rejected = requestGuard(request, "GET");
  if (rejected) return rejected;
  if (engineMode() === "demo") return json({ available: false });
  try {
    const adapters = cleanSyncs(
      await callTool("app_sync", {}, request.signal, fetcher),
    );
    if (!adapters) throw new Error("Invalid sync list");
    return json({ available: true, adapters });
  } catch (error) {
    if (!(error instanceof EngineError && error.reason === "not_connected"))
      answerRejected("app_sync", error, request.signal);
    return json({ available: false });
  }
}

async function ask(request: Request, fetcher: typeof fetch) {
  const rejected = requestGuard(request, "POST");
  if (rejected) return rejected;
  let body: unknown;
  try {
    body = await readJson(request, 1024);
  } catch {
    return json({ error: "Invalid or oversized request" }, 400);
  }
  if (
    !object(body) ||
    Object.keys(body).length !== 1 ||
    typeof body.adapter !== "string" ||
    !adapterPattern.test(body.adapter)
  )
    return json({ error: "Invalid request" }, 400);
  if (engineMode() === "demo") return json({ available: false });
  try {
    const asked = cleanAsked(
      await callTool(
        "app_sync",
        { adapter: body.adapter },
        request.signal,
        fetcher,
      ),
    );
    if (!asked) throw new Error("Invalid sync answer");
    return json({ available: true, ...asked });
  } catch (error) {
    if (!(
      error instanceof EngineError &&
      (error.reason === "not_connected" || error.reason === "not_found")
    ))
      answerRejected("app_sync", error, request.signal);
    return json({ available: false });
  }
}
