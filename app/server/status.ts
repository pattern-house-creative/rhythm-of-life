import { cleanStatus } from "../src/modelStatus.js";
import { json, requestGuard } from "./http.js";
import { answerRejected, callTool, EngineError, engineMode } from "./engine.js";

// GET /api/status: the engine's version and each model's job and state (app_status,
// contracts/README.md 2.2; docs/models.md), for Sources & settings. In demo mode there is no
// engine, so no models: {mode: "demo", available: false}. An engine older than app_status, or a
// call that fails or answers outside the contract, is {mode: "live", available: false}; the
// failure is logged by its kind only (engine.ts). Nothing is kept.
export async function handleStatus(
  request: Request,
  fetcher: typeof fetch = fetch,
) {
  const rejected = requestGuard(request, "GET");
  if (rejected) return rejected;
  if (engineMode() === "demo") return json({ mode: "demo", available: false });
  try {
    const status = cleanStatus(
      await callTool("app_status", {}, request.signal, fetcher),
    );
    if (!status) throw new Error("Invalid status answer");
    return json({ mode: "live", available: true, ...status });
  } catch (error) {
    if (!(error instanceof EngineError && error.reason === "not_connected"))
      answerRejected("app_status", error, request.signal);
    return json({ mode: "live", available: false });
  }
}
