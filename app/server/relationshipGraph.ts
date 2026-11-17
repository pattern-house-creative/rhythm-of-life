import { emptyGraph } from "../src/liveGraph.js";
import { readable } from "../src/knowledge.js";
import { assertGraphPage } from "../src/validation.js";
import { catalogProvider } from "./config.js";
import { answerRejected, callTool, EngineError } from "./engine.js";

// The engine refuses a request body over 32 KiB, and a refused refresh empties
// the map. The IDs the app already holds only let it name what has left the
// view, which a refresh replaces whole anyway, so they go while they fit.
const knownBytes = 30_000;
// The app's continuation for the engine's pages, under the one source it reads.
const source = "rhythm";
const enginePage = /^o:[0-9]{1,5}$/;

// The map from the engine (app_graph). Discovery and refresh read the overview; one star's
// record, text or readings are read when it is opened. Search and neighbours run in the engine
// over every star a person can read, not only the overview's, a page at a time.
export async function readRelationshipGraph(
  request: Request,
  input: Record<string, unknown>,
  fetcher: typeof fetch = fetch,
) {
  let page = emptyGraph();
  page.issues = [
    {
      provider: "rhythm",
      reason:
        "Knowledge map unavailable. Health sources refresh independently.",
    },
  ];
  // One record, its current text or one data star's readings, read by the engine when it is
  // opened. Nothing else is sent or kept.
  const one =
    input.operation === "get" ||
    input.operation === "content" ||
    input.operation === "data";
  const query = input.operation === "search" || input.operation === "neighbors";
  const cursors =
    input.cursors && typeof input.cursors === "object"
      ? (input.cursors as Record<string, unknown>)
      : {};
  const cursor =
    typeof cursors[source] === "string" && enginePage.test(cursors[source])
      ? cursors[source]
      : undefined;
  let read = false;
  try {
    // Stars first, then links, each in the app's order.
    let room = knownBytes;
    const known = (value: unknown) =>
      (Array.isArray(value)
        ? value
            .filter((v) => typeof v === "string" && v.length <= 200)
            .slice(0, 500)
        : []
      ).filter((id) => {
        room -= new TextEncoder().encode(JSON.stringify(id)).length + 1;
        return room >= 0;
      });
    const result = await callTool(
      "app_graph",
      one
        ? { operation: input.operation, value: String(input.value) }
        : query
          ? {
              operation: input.operation,
              value: String(input.value),
              ...(cursor ? { cursor } : {}),
            }
          : {
              knownNodeIds: known(input.ids),
              knownEdgeIds: known(input.edgeIds),
            },
      request.signal,
      fetcher,
    );
    assertGraphPage(result);
    // The engine's own fictional seed (demo.enabled in rhythm.yaml) arrives with
    // provenance.demo true and keeps it, so the sky labels it DEMO. Nothing here
    // falls back to the app's built-in demo data.
    page = { ...result, nodes: result.nodes.filter(readable) };
    const allowed = new Set(page.nodes.map((n) => n.id));
    page.edges = page.edges.filter(
      (edge) => allowed.has(edge.source) && allowed.has(edge.target),
    );
    read = true;
    // A search or neighbours read the engine answered: its only issue is the
    // engine's own note on the snapshot it read (under its catalog provider),
    // which is no source that failed, and with no page left it holds every
    // match the reader may see. So the explorer says a source couldn't be read
    // only when the read failed, and that every match came when none is left.
    if (query) {
      const engine = catalogProvider();
      page.issues = page.issues.filter((i) => i.provider !== engine);
      page.completeness =
        page.nextCursor || page.issues.length ? "partial" : "complete";
    }
    if (!query)
      page.issues = [
        ...page.issues,
        {
          provider: "rhythm",
          reason:
            "Bounded map: the best-connected stars. Search and neighbours reach every star.",
        },
      ];
  } catch (error) {
    // Fail closed, without leaking provider errors or retaining removed content.
    answerRejected("app_graph", error, request.signal);
    // A refresh the engine didn't answer at all (restarting, say) is no answer, not an empty
    // map: the browser keeps the map it read last, marked stale, and asks again soon. A refusal
    // (the engine's or its credential's) still answers an empty map.
    if (
      input.operation === "refresh" &&
      error instanceof EngineError &&
      error.reason === "unavailable"
    )
      throw error;
  }
  if (one) return page.nodes.find((n) => n.id === input.value) ?? null;
  // A failed read claims no continuation, so it never reads as every match retrieved.
  const next = query && read ? page.nextCursor : null;
  return {
    ...page,
    cursors: query && read ? { [source]: next } : {},
    nextCursor: next,
  };
}
