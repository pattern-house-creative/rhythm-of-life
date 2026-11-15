import { createDemoSession, demoData } from "../src/demo.js";
import { emptyGraph } from "../src/liveGraph.js";
import { readable } from "../src/knowledge.js";
import type { GraphPage } from "../src/knowledge.js";
import {
  assertGraphNode,
  assertGraphPage,
  cursorsValid,
  object,
  statesValid,
} from "../src/validation.js";
import { json, readJson, requestGuard } from "./http.js";
import { readRelationshipGraph } from "./relationshipGraph.js";
import { engineMode } from "./engine.js";
import { readRecall } from "./recall.js";
import { maxQuestion } from "../src/recall.js";

// The optional source argument is a contract-test seam, not a provider registry.
export async function handleGraph(
  request: Request,
  source?: ReturnType<typeof createDemoSession>,
) {
  const rejected = requestGuard(request, "POST");
  if (rejected) return rejected;
  let input: Record<string, unknown>;
  try {
    const value = await readJson(request);
    if (
      !object(value) ||
      ![
        "discover",
        "search",
        "neighbors",
        "get",
        "content",
        "data",
        "refresh",
        "recall",
      ].includes(String(value.operation))
    )
      throw new Error();
    input = value;
    if (
      input.value !== undefined &&
      (typeof input.value !== "string" || input.value.length > 1000)
    )
      throw new Error();
    if (
      ["get", "content", "data", "neighbors", "search", "recall"].includes(
        String(input.operation),
      ) &&
      !input.value
    )
      throw new Error();
    if (input.operation === "search" && String(input.value).length > 200)
      throw new Error();
    // The explorer's combined search: one question, nothing else (server/recall.ts).
    if (
      input.operation === "recall" &&
      (String(input.value).length > maxQuestion ||
        !String(input.value).trim() ||
        Object.keys(input).some((k) => k !== "operation" && k !== "value"))
    )
      throw new Error();
    if (input.cursors !== undefined && !cursorsValid(input.cursors))
      throw new Error();
    if (input.states !== undefined && !statesValid(input.states))
      throw new Error();
    if (
      input.ids !== undefined &&
      (!Array.isArray(input.ids) ||
        input.ids.length > 1500 ||
        input.ids.some((id) => typeof id !== "string" || id.length > 1000))
    )
      throw new Error();
    if (
      input.demo !== undefined &&
      (!object(input.demo) ||
        !Number.isInteger(input.demo.generation) ||
        Number(input.demo.generation) < 0 ||
        Number(input.demo.generation) > 20 ||
        typeof input.demo.unavailable !== "boolean")
    )
      throw new Error();
  } catch {
    return json({ error: "Invalid or oversized request" }, 400);
  }
  try {
    // Combined search is the engine's alone: the demo's sources have none (server/recall.ts).
    if (input.operation === "recall")
      return json(
        source
          ? { available: false }
          : await readRecall(request, String(input.value).trim()),
      );
    // With an engine configured, the map comes from it; the fictional sky is demo mode only.
    if (!source && engineMode() === "live")
      return json(await readRelationshipGraph(request, input));
    const session = source ?? createDemoSession(demoData);
    if (!source && object(input.demo)) {
      // Stateless, bounded fictional scenarios. No writes, sessions or persistence.
      for (let i = 0; i < Number(input.demo.generation); i++)
        session.demo!.advance();
      session.demo!.setUnavailable(Boolean(input.demo.unavailable));
    }
    const cursors = (input.cursors ?? {}) as Record<string, string | null>;
    const states = (input.states ?? {}) as GraphPage["sourceStates"];
    const owner = (id: string) =>
      session.adapters.find((a) =>
        id.startsWith(
          `${a.provider}:${encodeURIComponent(a.key?.slice(a.provider.length + 1) ?? "")}:`,
        ),
      );
    // Fictional records already carry their sample text and have no readings, so content and data are get.
    if (
      input.operation === "get" ||
      input.operation === "content" ||
      input.operation === "data"
    ) {
      const node =
        (await owner(String(input.value))?.get(
          String(input.value),
          request.signal,
        )) ?? null;
      assertGraphNode(node);
      return json(node && readable(node) ? node : null);
    }
    if (input.operation === "refresh") {
      const current = {
        ...emptyGraph(),
        discoveryCursors: cursors,
        discoveryStates: states,
      };
      const removed: string[] = [];
      // Resolve IDs server-side. Client-supplied titles, evidence or access flags
      // are never accepted as source records or used to grant access.
      const ids = [...new Set((input.ids ?? []) as string[])];
      for (let i = 0; i < ids.length; i += 8) {
        const results = await Promise.allSettled(
          ids.slice(i, i + 8).map(async (id) => {
            const node = (await owner(id)?.get(id, request.signal)) ?? null;
            if (node && readable(node)) current.nodes.push(node);
            else removed.push(id);
          }),
        );
        // A failed lookup is not a deletion. A failed refresh preserves no falsely
        // fresh health/graph claims; the client marks retained records stale.
        if (results.some((r) => r.status === "rejected")) {
          current.issues.push({
            provider: owner(ids[i])?.provider ?? "rhythm",
            reason: "Some records could not be revalidated.",
          });
        }
      }
      const result = await session.refresh(current, request.signal);
      result.removedNodeIds = [
        ...new Set([...removed, ...(result.removedNodeIds ?? [])]),
      ];
      assertGraphPage(result);
      return json(result);
    }
    const result = await session.retrieve(
      input.operation as "discover" | "search" | "neighbors",
      String(input.value ?? ""),
      cursors,
      request.signal,
      states,
    );
    assertGraphPage(result);
    return json(result);
  } catch {
    // Do not log upstream response bodies, URLs, records, or credentials.
    return json({ error: "Source response unavailable or invalid" }, 502);
  }
}
