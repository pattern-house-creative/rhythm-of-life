import { readable } from "../src/knowledge.js";
import { cleanRecalled } from "../src/recall.js";
import type { RecallAnswer } from "../src/recall.js";
import { assertGraphPage } from "../src/validation.js";
import {
  answerRejected,
  callTool,
  EngineError,
  engineMode,
  forgetTools,
  hasRecall,
} from "./engine.js";

// POST /api/graph {operation: "recall", value}: the explorer's combined search (SB-P2-10;
// contracts/README.md 2.2, app_graph operation recall). Only while the engine lists it, which
// it does only with engine.recall on: otherwise, and in demo mode, {available: false} without
// calling the engine, and the explorer's search is the title search it always was. A listing
// the facade couldn't read says nothing either way: {available: false, retry: true}, so the
// explorer shows the title search alone, as on an install with it off, and asks again with the
// next search (it says the search is unavailable only once this session has seen it on). On, the stars
// on this reader's map that stand for what combined retrieval finds, best first, with why; an
// unreadable star is left out, with its result and every link to it, as the map leaves them
// out. A failed call is {available: true, failed: true}, logged by its kind only (engine.ts);
// nothing is kept.
export async function readRecall(
  request: Request,
  question: string,
  fetcher: typeof fetch = fetch,
): Promise<RecallAnswer> {
  if (engineMode() === "demo") return { available: false };
  const listed = await hasRecall(request.signal, fetcher);
  if (listed === null) return { available: false, retry: true };
  if (!listed) return { available: false };
  try {
    const result = await callTool(
      "app_graph",
      { operation: "recall", value: question },
      request.signal,
      fetcher,
    );
    assertGraphPage(result);
    const nodes = result.nodes.filter(readable);
    const stars = new Set(nodes.map((n) => n.id));
    const recall = cleanRecalled(
      (result as Record<string, unknown>).recall,
      stars,
    );
    if (!recall) throw new Error("Invalid recall answer");
    return {
      available: true,
      page: {
        nodes,
        edges: result.edges.filter(
          (e) => stars.has(e.source) && stars.has(e.target),
        ),
        nextCursor: null,
        completeness: result.completeness,
        issues: result.issues,
        cursors: {},
      },
      recall,
    };
  } catch (error) {
    // The engine turned it off since its listing was read: off, and the listing read again.
    if (error instanceof EngineError && error.reason === "invalid_arguments") {
      forgetTools();
      return { available: false };
    }
    answerRejected("app_graph", error, request.signal);
    return { available: true, failed: true };
  }
}
