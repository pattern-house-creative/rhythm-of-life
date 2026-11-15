import { readDeadline } from "./readDeadline";
import type { GraphSession, OriginalFile } from "./liveGraph";
import type { GraphPage } from "./knowledge";
import { demoReadings } from "./demoReadings";
import { emptyGraph } from "./liveGraph";
import { mergePage } from "./knowledge";
import {
  assertGraphNode,
  assertGraphPage,
  assertInsight,
  assertInsightList,
  assertObservatory,
  cursorsValid,
  object,
} from "./validation";
import { cleanInsight, cleanInsights, cleanRuns } from "./insights";
import { createDemoInsights } from "./demoInsights";
import {
  cleanPins,
  cleanCards,
  cleanDashboard,
  cleanDashboardData,
  cleanDashboardMap,
  cleanLayoutAnswer,
  cleanWriteAnswer,
  listedSummaries,
} from "./dashboards";
import type { DashboardWriteAnswer, LayoutAnswer } from "./dashboards";
import { createDemoDashboards } from "./demoDashboards";
import {
  cleanOriginal,
  cleanUploadAnswer,
  fileBase64,
  originalPath,
  originalTypes,
} from "./documents";
import {
  cleanCommand,
  cleanListen,
  cleanRenamed,
  cleanScreens,
  listenTaken,
} from "./screens";
import type { SendAnswer } from "./screens";
import { createDemoScreens } from "./demoScreens";
import { cleanStatusAnswer } from "./modelStatus";
import { cleanSyncAnswer, cleanSyncList } from "./sync";
import { cleanRecalled } from "./recall";
import type { RecallAnswer } from "./recall";
import {
  cleanPassedAnswer,
  maxMemoryBody,
  memoryReads,
  utf8Bytes,
} from "./memories";
import type { MemoryClient, MemoryReply } from "./memories";

const dashboardPath = (id: string, version?: number, data = false) =>
  `/api/dashboards?id=${encodeURIComponent(id)}${version ? `&version=${version}` : ""}${data ? "&data=1" : ""}`;

async function read(
  path: string,
  signal?: AbortSignal,
  body?: unknown,
  method = body ? "POST" : "GET",
  // Leave room for the server's bounded read retry and for dashboard reads.
  timeout = path.split("?")[0] === "/api/dashboards" ? 30000 : 22000,
) {
  return readDeadline(signal, timeout, async (requestSignal) => {
    const response = await fetch(path, {
      method,
      credentials: "same-origin",
      cache: "no-store",
      // A redirect is never followed: it is a front door sending this read to its sign-in.
      redirect: "manual",
      signal: requestSignal,
      ...(body
        ? {
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(body),
          }
        : {}),
    });
    if (
      response.type === "opaqueredirect" ||
      [401, 403].includes(response.status) ||
      (response.ok &&
        !response.headers.get("content-type")?.includes("application/json"))
    )
      window.dispatchEvent(new Event("observatory-access-lost"));
    if (
      !response.ok ||
      !response.headers.get("content-type")?.includes("application/json")
    )
      throw new ReadFailed(response.status);
    return response.json() as Promise<unknown>;
  });
}

// A read the app's server answered with an error or something that isn't JSON: its status, for the one caller that
// tells them apart (memoryClient: a 400 or 413 from /api/memories means nothing reached the engine).
class ReadFailed extends Error {
  readonly status: number;
  constructor(status: number) {
    super("Access or source unavailable. Reload to verify access.");
    this.status = status;
  }
}
// The ids of the map's stars and links a refresh sends, which only let the engine name what has
// left the view (the map is replaced whole anyway). The app's server takes at most 128 KiB and
// passes on about 30 KB of them (server/relationshipGraph.ts), so no more than fits in 48 KB is
// sent: stars first, then links, each in the map's order, the most relevant first.
export const knownBytes = 48_000;
const utf8 = new TextEncoder();
export function knownIds(page: GraphPage) {
  let room = knownBytes;
  const fit = (ids: string[]) =>
    ids.slice(0, 500).filter((id) => {
      room -= utf8.encode(JSON.stringify(id)).length + 1;
      return room >= 0;
    });
  return {
    ids: fit(page.nodes.map((n) => n.id)),
    edgeIds: fit(page.edges.map((e) => e.id)),
  };
}

export async function loadObservatory(
  signal?: AbortSignal,
  source?: "garmin" | "history",
) {
  const data = await read(
    source ? `/api/observatory?source=${source}` : "/api/observatory",
    signal,
  );
  assertObservatory(data);
  return data;
}

// The engine's version and each model's state, for Sources & settings (docs/models.md). Never
// throws: an unreadable or failed answer is "unavailable".
export async function loadStatus(signal?: AbortSignal) {
  try {
    return cleanStatusAnswer(await read("/api/status", signal));
  } catch {
    return cleanStatusAnswer(null);
  }
}

// Sync now from Sources & settings (SB-P6-09): the adapters on the engine's clock, and one batch
// now of one of them. Neither throws: an unreadable or failed answer is "unavailable".
export async function loadSyncs(signal?: AbortSignal) {
  try {
    return cleanSyncList(await read("/api/sync", signal));
  } catch {
    return cleanSyncList(null);
  }
}
export async function requestSync(adapter: string, signal?: AbortSignal) {
  try {
    return cleanSyncAnswer(await read("/api/sync", signal, { adapter }));
  } catch {
    return cleanSyncAnswer(null);
  }
}

// The explorer's combined search (SB-P2-10): the stars combined retrieval finds for one question,
// with why, while the engine's engine.recall is on; {available: false} otherwise (and in demo
// mode, which never asks). No answer, or one that says nothing of whether it is on, is
// {available: false, retry: true}, which the explorer asks again after; an answer of an engine
// that serves it but doesn't check out is a failed search; only a search the explorer gave up on
// throws.
export async function readRecall(
  question: string,
  signal?: AbortSignal,
): Promise<RecallAnswer> {
  let answer: unknown;
  try {
    answer = await read("/api/graph", signal, {
      operation: "recall",
      value: question,
    });
  } catch (error) {
    if (signal?.aborted) throw error;
    return { available: false, retry: true };
  }
  if (!object(answer) || typeof answer.available !== "boolean")
    return { available: false, retry: true };
  if (!answer.available)
    return answer.retry === true
      ? { available: false, retry: true }
      : { available: false };
  if (answer.failed === true) return { available: true, failed: true };
  try {
    const page = answer.page;
    assertGraphPage(page);
    const recall = cleanRecalled(
      answer.recall,
      new Set(page.nodes.map((n) => n.id)),
    );
    if (!recall) return { available: true, failed: true };
    return {
      available: true,
      page: { ...page, cursors: {} },
      recall,
    };
  } catch {
    return { available: true, failed: true };
  }
}

// Sync now. The engine answers whether it accepted the request or a sync is already under way.
export async function requestGarminSync(signal?: AbortSignal) {
  const answer = await read("/api/garmin-sync", signal, undefined, "POST");
  if (!object(answer) || typeof answer.accepted !== "boolean")
    throw new Error("Invalid sync answer");
  return {
    accepted: answer.accepted,
    pending: typeof answer.pendingSince === "string",
  };
}

// A write or undo answers what changed, or why nothing did; an unreadable
// answer says nothing was confirmed.
async function written(body: unknown, signal?: AbortSignal) {
  const answer = cleanWriteAnswer(
    await read("/api/dashboards", signal, body, "POST", 30000),
  );
  return (
    answer ??
    ({
      ok: false,
      reason:
        "The engine's answer couldn't be read; check the file before trying again.",
    } satisfies DashboardWriteAnswer)
  );
}

// An arrangement saved, or why not; an answer that didn't arrive or can't be
// read may still have been saved, and says so (it is never sent again, though
// an Undo of it may be tried again later).
async function arranged(
  body: Record<string, unknown>,
  id: string,
  signal?: AbortSignal,
): Promise<LayoutAnswer> {
  const lost: LayoutAnswer = {
    ok: false,
    transient: true,
    reason:
      "The arrangement may have been saved: no answer came back. It shows here once the dashboard is read again.",
  };
  try {
    return (
      cleanLayoutAnswer(
        await read("/api/dashboards", signal, body, "POST", 30000),
        id,
      ) ?? lost
    );
  } catch {
    return lost;
  }
}

// One document's original file from /api/document-file, whole, for the reader to show. A front
// door that answers instead (a sign-in page) is access lost, as for every other read.
async function readOriginal(
  id: string,
  signal?: AbortSignal,
): Promise<OriginalFile> {
  return readDeadline(signal, 120000, async (requestSignal) => {
    let response: Response;
    try {
      response = await fetch(originalPath(id), {
        credentials: "same-origin",
        cache: "no-store",
        redirect: "manual",
        signal: requestSignal,
      });
    } catch (error) {
      if (signal?.aborted) throw error;
      return { ok: false, reason: "unavailable" };
    }
    const type = response.headers.get("content-type") ?? "";
    if (response.status === 413) {
      const answer: unknown = await response.json().catch(() => null);
      return {
        ok: false,
        reason: "too_large",
        maxBytes:
          object(answer) && Number.isSafeInteger(answer.maxBytes)
            ? (answer.maxBytes as number)
            : 0,
      };
    }
    const known = Object.values(originalTypes).some(
      (t) => type.split(";")[0] === t.split(";")[0],
    );
    if (
      response.type === "opaqueredirect" ||
      [401, 403].includes(response.status) ||
      (response.ok && !known && !type.includes("application/json"))
    )
      window.dispatchEvent(new Event("observatory-access-lost"));
    if (!response.ok || !known) {
      await response.body?.cancel().catch(() => {});
      return {
        ok: false,
        reason: response.status === 404 ? "missing" : "unavailable",
      };
    }
    return { ok: true, bytes: new Uint8Array(await response.arrayBuffer()) };
  });
}

// One live-screens call. A listen waits up to 25 seconds at the engine and 35 at the facade, so
// the browser waits 40 for it; a send waits up to 8 for its outcome.
const screens = (body: Record<string, unknown>, signal?: AbortSignal) =>
  read("/api/screens", signal, body, "POST", 40000);
const offHere = (answer: unknown) =>
  object(answer) && answer.available === false;

// A command for another screen: what became of it, or why it wasn't sent. An answer that didn't
// arrive may still have reached the engine, so it never reads as not sent.
async function sendCommand(
  body: Record<string, unknown>,
  signal?: AbortSignal,
): Promise<SendAnswer> {
  let answer: unknown;
  try {
    answer = await screens({ op: "send", ...body }, signal);
  } catch {
    return {
      ok: false,
      reason:
        "It may have been sent: the answer didn't arrive. Check that screen before sending again.",
    };
  }
  if (offHere(answer))
    return { ok: false, reason: "Screens are off on this install." };
  if (
    object(answer) &&
    answer.ok === false &&
    typeof answer.reason === "string"
  )
    return { ok: false, reason: answer.reason.slice(0, 300) };
  const command = object(answer) ? cleanCommand(answer.command) : null;
  return command
    ? { ok: true, command }
    : {
        ok: false,
        reason:
          "It may have been sent: the answer couldn't be read. Check that screen before sending again.",
      };
}

// The Memories view (SB-P3-13; /api/memories, app_memories): whether the engine serves it, and one op. Neither throws
// but for a call the view gave up on: an answer that didn't arrive or can't be read is a refusal coded unavailable,
// which for a write says it may have been saved (a write is never sent again by itself; a capture's key makes the
// person's own retry store once). The app's server refusing the request itself (400 or 413: too large, or not one it
// takes) is a refusal that says nothing was sent, since nothing reached the engine. The answer is the facade's, which
// cleanPassedAnswer checks again in the engine's shape.
export const memoryClient: MemoryClient = {
  async available(signal) {
    try {
      const answer = await read("/api/memories", signal);
      return object(answer) && answer.available === true;
    } catch {
      return false;
    }
  },
  // A person's share (run 14): only while the facade says the engine takes it; false on any failure.
  async sharing(signal) {
    try {
      const answer = await read("/api/memories", signal);
      return (
        object(answer) && answer.available === true && answer.share === true
      );
    } catch {
      return false;
    }
  },
  async call<T>(request: Record<string, unknown>, signal?: AbortSignal) {
    const op = String(request.op);
    const write = !memoryReads.includes(op);
    const lost: MemoryReply<T> = {
      available: true,
      ok: false,
      code: "unavailable",
      reason: write
        ? "No answer came back, so this may or may not have been saved. Read the memory again before trying once more."
        : "Memories can't be read right now. Try again later.",
      revision: null,
      known: [],
    };
    let answer: unknown;
    try {
      answer = await read(
        "/api/memories",
        signal,
        request,
        "POST",
        write ? 30000 : 22000,
      );
    } catch (error) {
      if (signal?.aborted) throw error;
      if (
        error instanceof ReadFailed &&
        (error.status === 400 || error.status === 413)
      ) {
        const large = utf8Bytes(JSON.stringify(request)) > maxMemoryBody;
        return {
          available: true,
          ok: false,
          code: large ? "too_large" : "invalid",
          reason: large
            ? "This is too long to save: a memory holds at most 128 KiB of text. Nothing was sent."
            : "The app couldn't send this as it is. Nothing was sent or saved.",
          revision: null,
          known: [],
        } as MemoryReply<T>;
      }
      return lost;
    }
    if (!object(answer) || answer.available !== true)
      return { available: false };
    const { available: _, ...rest } = answer;
    const clean = cleanPassedAnswer(op, rest);
    return (clean ? { available: true, ...clean } : lost) as MemoryReply<T>;
  },
};

export function createApiSession(demoMode = true): GraphSession {
  const demo = { generation: 0, unavailable: false };
  // The demo's pins live with its session, in memory.
  let demoPins: string[] = [];
  // The demo's fictional insights live with this session, in memory.
  const sample = demoMode ? createDemoInsights() : null;
  // And its one fictional dashboard.
  const boards = demoMode ? createDemoDashboards() : null;
  // And its screens, between this browser's tabs; never /api/screens.
  const hub = demoMode ? createDemoScreens() : null;
  return {
    initial: emptyGraph(),
    async retrieve(operation, value, cursors = {}, signal, states) {
      const result = await read("/api/graph", signal, {
        operation,
        value,
        cursors,
        states,
        demo,
      });
      assertGraphPage(result);
      if (!object(result) || !cursorsValid(result.cursors))
        throw new Error("Invalid continuation");
      return {
        ...result,
        cursors: result.cursors as Record<string, string | null>,
      };
    },
    async refresh(current, signal) {
      // A refresh that fails throws: the map keeps what it read last, marked stale, and asks
      // again soon (useKnowledge.ts). Lost access clears it there.
      const ask = (known: { ids: string[]; edgeIds: string[] }) =>
        read("/api/graph", signal, {
          operation: "refresh",
          ...known,
          cursors: current.discoveryCursors,
          states: current.discoveryStates,
          demo,
        });
      let result: unknown;
      try {
        result = await ask(knownIds(current));
      } catch (error) {
        // Refused as too large or not taken: the same refresh once more without the ids.
        if (
          signal?.aborted ||
          !(error instanceof ReadFailed) ||
          (error.status !== 400 && error.status !== 413)
        )
          throw error;
        result = await ask({ ids: [], edgeIds: [] });
      }
      assertGraphPage(result);
      // Real graph responses are the entire currently authorized bounded view: a
      // refresh that answers replaces the map whole, so nothing revoked stays.
      if (!demoMode) return result;
      const freshIds = new Set(result.nodes.map((n) => n.id));
      const retained = {
        ...current,
        nodes: current.nodes.map((n) =>
          freshIds.has(n.id)
            ? n
            : {
                ...n,
                resource: { ...n.resource, availability: "stale" as const },
              },
        ),
      };
      return {
        ...mergePage(retained, result),
        completeness: result.completeness,
        issues: result.issues,
      };
    },
    text: demoMode
      ? undefined
      : async (id, signal) => {
          const node = await read("/api/graph", signal, {
            operation: "content",
            value: id,
          });
          assertGraphNode(node);
          const original = cleanOriginal(node?.resource.original);
          return node?.id === id && node.resource.content
            ? {
                text: node.resource.content,
                modifiedAt: node.resource.sourceModifiedAt,
                ...(original ? { original } : {}),
              }
            : null;
        },
    // A document's original: the demo keeps none.
    original: demoMode ? undefined : readOriginal,
    // The explorer's combined search: the demo's sources have none.
    recall: demoMode ? undefined : readRecall,
    // The demo's data stars read fictional days, so the panels can show them.
    data: demoMode
      ? async (id) => demoReadings(id)
      : async (id, signal) => {
          const node = await read("/api/graph", signal, {
            operation: "data",
            value: id,
          });
          assertGraphNode(node);
          return node?.id === id && node.measurements
            ? node.measurements
            : null;
        },
    insights: sample
      ? () => sample.list()
      : async (signal) => {
          const result = await read("/api/insights", signal);
          assertInsightList(result);
          // Runs that don't check out are none; the insights still show.
          return { insights: cleanInsights(result), runs: cleanRuns(result) };
        },
    insightFeedback: sample
      ? (id, value, until, note) => sample.feedback(id, value, until, note)
      : async (id, value, until, note, signal) => {
          const result = await read("/api/insights", signal, {
            id,
            value,
            ...(until ? { until } : {}),
            ...(note ? { note } : {}),
          });
          assertInsight(result);
          const insight = cleanInsight(result);
          if (insight?.id !== id) throw new Error("Invalid insight response");
          return insight;
        },
    dashboards: boards
      ? async () => boards.list()
      : async (signal) =>
          listedSummaries(await read("/api/dashboards", signal)),
    dashboard: boards
      ? async (id, version) => boards.get(id, version)
      : async (id, version, signal) =>
          cleanDashboard(await read(dashboardPath(id, version), signal)),
    // A dashboard's feeds may read Drive and several stars, so they get longer.
    dashboardData: boards
      ? async (id, version) => boards.data(id, version)
      : async (id, version, signal) =>
          cleanDashboardData(
            await read(
              dashboardPath(id, version, true),
              signal,
              undefined,
              "GET",
              30000,
            ),
          ),
    dashboardWrite: boards
      ? async (id, version, name, change) =>
          boards.write(id, version, name, change)
      : (id, version, name, change, signal) =>
          written({ action: "write", id, version, name, change }, signal),
    dashboardUndo: boards
      ? async (write) => boards.undo(write)
      : (write, signal) => written({ action: "undo", write }, signal),
    // The demo arranges its dashboards and focuses its maps in the browser.
    dashboardLayout: boards
      ? async (id, version, layout, base, note) =>
          boards.layout(id, version, layout, base, note)
      : (id, version, layout, base, note, signal) =>
          arranged(
            { action: "layout", id, version, layout, base, note },
            id,
            signal,
          ),
    dashboardMap: boards
      ? (id, version, part, cursor) => boards.map(id, version, part, cursor)
      : async (id, version, part, cursor, signal) =>
          cleanDashboardMap(
            await read(
              `${dashboardPath(id, version)}&map=${encodeURIComponent(part)}${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ""}`,
              signal,
            ),
            part,
          ),
    dashboardAllowEdits: boards
      ? async (id, version, allowed) => boards.allowEdits(id, version, allowed)
      : async (id, version, allowed, signal) => {
          const answer = await read(
            "/api/dashboards",
            signal,
            { action: "edits", id, version, allowed },
            "POST",
          );
          if (!object(answer) || typeof answer.editsAllowed !== "boolean")
            throw new Error("Invalid answer");
          return answer.editsAllowed;
        },
    dashboardCards: boards
      ? async () => boards.cards()
      : async (signal) =>
          cleanCards(await read("/api/dashboards?cards=1", signal)),
    setDashboardCard: boards
      ? async (corner, dashboard) => boards.setCard(corner, dashboard)
      : async (corner, dashboard, signal) =>
          cleanCards(
            await read(
              "/api/dashboards",
              signal,
              { action: "card", corner, dashboard },
              "POST",
            ),
          ),
    pins: demoMode
      ? async () => [...demoPins]
      : async (signal) =>
          cleanPins(await read("/api/dashboards?pins=1", signal)),
    setPin: demoMode
      ? async (star, pinned) => {
          demoPins = pinned
            ? [...demoPins.filter((p) => p !== star), star].slice(-24)
            : demoPins.filter((p) => p !== star);
          return [...demoPins];
        }
      : async (star, pinned, signal) =>
          cleanPins(
            await read(
              "/api/dashboards",
              signal,
              { action: "pin", star, pinned },
              "POST",
            ),
          ),
    // Only an install with the engine keeps documents someone adds.
    uploads: demoMode
      ? undefined
      : async (signal) => {
          const answer = await read("/api/documents", signal);
          return object(answer) &&
            answer.available === true &&
            Number.isInteger(answer.maxBytes)
            ? { available: true, maxBytes: answer.maxBytes as number }
            : { available: false, maxBytes: 0 };
        },
    upload: demoMode
      ? undefined
      : async (file, signal) =>
          cleanUploadAnswer(
            await read(
              "/api/documents",
              signal,
              { name: file.name, content: await fileBase64(file) },
              "POST",
              90000,
            ),
          ),
    // Native memories: only an install with the engine, and only while it serves them.
    memories: demoMode ? undefined : memoryClient,
    // The demo's sky never changes at its source, so there is nothing to rebuild.
    requestRebuild: demoMode
      ? undefined
      : async (signal) => {
          const answer = await read(
            "/api/graph-refresh",
            signal,
            undefined,
            "POST",
          );
          if (!object(answer) || typeof answer.accepted !== "boolean")
            throw new Error("Invalid rebuild answer");
          return {
            accepted: answer.accepted,
            pending: typeof answer.pendingSince === "string",
          };
        },
    ...(demoMode
      ? (hub ?? {})
      : {
          async screenListen(listen, signal) {
            const answer = await screens({ op: "listen", ...listen }, signal);
            if (offHere(answer)) return null;
            // Another app login's screen: this browser takes a new id (useScreenListen).
            if (listenTaken(answer)) return { taken: true };
            const clean = cleanListen(answer);
            if (!clean) throw new Error("Invalid listen answer");
            return clean;
          },
          async screenDone(done, signal) {
            await screens({ op: "done", ...done }, signal);
          },
          // The action's arguments ride as one object, since move_tile's own `to` would
          // clash with send's.
          screenSend: ({ screen, to, action, args }, signal) =>
            sendCommand({ screen, to, action, args }, signal),
          async screenList(screen, signal) {
            const answer = await screens({ op: "list", screen }, signal);
            if (offHere(answer)) return [];
            const list = cleanScreens(answer);
            if (!list) throw new Error("Invalid screens answer");
            return list;
          },
          async screenRename(screen, name, signal) {
            const answer = await screens(
              { op: "rename", screen, name },
              signal,
            );
            if (!offHere(answer) && !cleanRenamed(answer))
              throw new Error("Invalid rename answer");
          },
        }),
    demo: demoMode
      ? {
          advance() {
            demo.generation = Math.min(20, demo.generation + 1);
          },
          setUnavailable(value) {
            demo.unavailable = value;
          },
        }
      : undefined,
  };
}
