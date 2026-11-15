import {
  cleanInsight,
  cleanInsights,
  cleanRuns,
  feedbackInput,
} from "../src/insights.js";
import type { DailyInsight, InsightRun } from "../src/insights.js";
import { json, readJson, requestGuard } from "./http.js";
import { answerRejected, callTool, engineMode } from "./engine.js";

// The daily insights, read from the engine (app_insights), and the household's feedback on
// one of them (app_insight_feedback). In demo mode the app keeps its fictional insights in the
// browser, so the list here is empty and feedback is refused; on any upstream failure too.
// Nothing is kept; a failure is logged by its kind only (engine.ts).
export async function handleInsights(
  request: Request,
  fetcher: typeof fetch = fetch,
) {
  if (request.method !== "GET" && request.method !== "POST")
    return json({ error: "Method not allowed" }, 405, { Allow: "GET, POST" });
  const rejected = requestGuard(request, request.method);
  if (rejected) return rejected;
  const allowed = () => engineMode() === "live";
  if (request.method === "GET") {
    let insights: DailyInsight[] = [],
      runs: InsightRun[] = [];
    try {
      if (allowed()) {
        const result = await callTool(
          "app_insights",
          { limit: 50 },
          request.signal,
          fetcher,
        );
        insights = cleanInsights(result);
        runs = cleanRuns(result);
      }
    } catch (error) {
      // Fail closed, without leaking provider errors.
      answerRejected("app_insights", error, request.signal);
    }
    return json({ insights, runs });
  }
  let input: NonNullable<ReturnType<typeof feedbackInput>>;
  try {
    const parsed = feedbackInput(await readJson(request));
    if (!parsed) throw new Error();
    input = parsed;
  } catch {
    return json({ error: "Invalid or oversized request" }, 400);
  }
  if (!allowed()) return json({ error: "Feedback unavailable" }, 503);
  try {
    const result = await callTool(
      "app_insight_feedback",
      input,
      request.signal,
      fetcher,
    );
    // The engine answers with the updated insight itself.
    const insight = cleanInsight(result);
    if (!insight || insight.id !== input.id) throw new Error();
    return json(insight);
  } catch (error) {
    answerRejected("app_insight_feedback", error, request.signal);
    // An unknown id, a refused shape or an out-of-range time (the engine's 404,
    // 403 and 400) are all a feedback that didn't save; the app keeps the
    // insight as it was. Never a 401/403 here, which would read as lost access.
    return json({ error: "Source response unavailable or invalid" }, 502);
  }
}
