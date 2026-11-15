import { callTool, engineMode, answerRejected } from "./engine.js";
import { json, readJson, requestGuard } from "./http.js";
import { object } from "../src/validation.js";
import { screenIdValid } from "../src/screens.js";
import { dashboardIdValid } from "../src/dashboards.js";

// Shares the existing /api/screens function and authenticated viewer boundary.
export async function handleStudio(
  request: Request,
  fetcher: typeof fetch = fetch,
) {
  const rejected = requestGuard(request, "POST");
  if (rejected) return rejected;
  let body: unknown;
  try {
    body = await readJson(request, 8000);
  } catch {
    return json({ error: "Invalid Studio request" }, 400);
  }
  const keys: Record<string, string[]> = {
    active: ["op", "screen"],
    read: ["op", "screen", "sessionId"],
    data: ["op", "screen", "sessionId", "revision"],
    controls: ["op", "screen", "dashboardId", "version"],
    validate: ["op", "screen", "sessionId", "revision", "requestId", "payload"],
    feedback: ["op", "screen", "sessionId", "revision", "requestId", "payload"],
    cancel: ["op", "screen", "sessionId", "revision", "requestId", "payload"],
    discard: ["op", "screen", "sessionId", "revision", "requestId", "payload"],
    save: ["op", "screen", "sessionId", "revision", "requestId", "payload"],
  };
  if (
    !object(body) ||
    typeof body.op !== "string" ||
    !Object.hasOwn(keys, body.op) ||
    !screenIdValid(body.screen)
  )
    return json({ error: "Invalid Studio request" }, 400);
  const allowed = keys[body.op];
  if (
    Object.keys(body).some((k) => !allowed.includes(k)) ||
    allowed.some((k) => !Object.hasOwn(body, k)) ||
    ["sessionId", "requestId", "dashboardId"].some(
      (k) => allowed.includes(k) && !dashboardIdValid(body[k]),
    ) ||
    ["revision", "version"].some(
      (k) =>
        allowed.includes(k) &&
        (!Number.isInteger(body[k]) || (body[k] as number) < 0),
    )
  )
    return json({ error: "Invalid Studio arguments" }, 400);
  if (engineMode() !== "live") return json({ value: null, available: false });
  try {
    const answer = await callTool("app_studio", body, request.signal, fetcher);
    if (
      !object(answer) ||
      (!Object.hasOwn(answer, "value") && !Object.hasOwn(answer, "error"))
    )
      throw new Error();
    return json(
      answer,
      typeof answer.error === "string"
        ? answer.code === "forbidden"
          ? 403
          : 409
        : 200,
    );
  } catch (error) {
    answerRejected("app_studio", error, request.signal);
    return json(
      { error: "Studio is unavailable; reconnect to read its current state." },
      503,
    );
  }
}
