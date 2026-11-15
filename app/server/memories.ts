import { object } from "../src/validation.js";
import {
  cleanMemoryAnswer,
  maxMemoryBody,
  memoryReads,
  memoryRequest,
  personLinkOps,
  personShareOps,
} from "../src/memories.js";
import { json, readJson, requestGuard } from "./http.js";
import {
  answerRejected,
  callTool,
  EngineError,
  engineMode,
  forgetTools,
  hasMemoryShare,
  listTools,
} from "./engine.js";

// The app's Memories view (SB-P3-12; app_memories, contracts/README.md 2.2). GET /api/memories: {available}, whether
// the engine serves it, which it does only with memory.tools on (its tools/list then names app_memories). POST
// /api/memories {op, ...}: one op with exactly its members (src/memories.ts, memoryRequest), passed on to the engine as
// the app, and its answer rebuilt from the contract's fields: {available: true, ok: true, ...} or {available: true, ok:
// false, code, reason}. The engine fixes the channel (app) itself; nothing here names one. In demo mode, and while the
// engine's listing lacks the tool or could not be read, {available: false} without calling it. A call that failed or
// answered outside the contract is {available: true, ok: false, code: "unavailable", reason}, logged by its kind only
// (engine.ts); a write is never sent twice (a capture's key makes the person's own retry safe). Nothing is kept here.
// A person's share (run 14): GET answers {available: true, share: true} while the engine's listing names app_memories'
// op share (memory.share on, memory scopes on, and listed to a login that speaks for a person: never the app's own
// token, the household view, whose every share would be refused), and a POST of op share is passed on only then;
// otherwise GET is {available} and that POST the 400 it was, as before. A person's own link steps (run 17: connections,
// stale, link, unlink, still_true, clear_flags) are passed on only while the engine serves the view; otherwise each is
// the 400 it was before them, as a request the facade does not know. So is Home's brief (SB-P5-08) and its old memory (SB-P5-17:
// resurface, resurface_dismiss), and the full memory UI's list (SB-P6-01: tags, changes, undo_group, forget_all, restore_all,
// delete_all, a project's writes, capsule_status and settings), a person's review queues (SB-P6-01: queues, duplicates,
// contradictions, insight_candidates, insight_stream, insight_history, review_insight and resolve) and the brief's lists behind its
// Home card (SB-P6-01: loops, due, decisions, calibration, and a person's word on one, resolve_loop, resolve_due, record_outcome),
// and the memory UI's last pieces (run 22: standing, stop_standing, resolve_all, and the words a list or its tags look for, query);
// share_all is share's, passed on only while the engine's listing names it.
export async function handleMemories(
  request: Request,
  fetcher: typeof fetch = fetch,
) {
  if (request.method === "POST") return ask(request, fetcher);
  const rejected = requestGuard(request, "GET");
  if (rejected) return rejected;
  const available = await served(request.signal, fetcher);
  return json(
    available && (await hasMemoryShare(request.signal, fetcher))
      ? { available, share: true }
      : { available },
  );
}

// Only an engine whose listing names the tool: an unreadable listing says nothing either way, so nothing is offered.
async function served(signal: AbortSignal, fetcher: typeof fetch) {
  if (engineMode() !== "live") return false;
  const tools = await listTools(signal, fetcher).catch(() => null);
  return tools?.has("app_memories") === true;
}

const unavailable = (write: boolean) => ({
  available: true,
  ok: false,
  code: "unavailable",
  reason: write
    ? "The engine's answer didn't arrive, so this may or may not have been saved. Read the memory again before trying once more."
    : "Memories can't be read right now. Try again later.",
});

async function ask(request: Request, fetcher: typeof fetch) {
  const rejected = requestGuard(request, "POST");
  if (rejected) return rejected;
  let body: unknown;
  try {
    body = await readJson(request, maxMemoryBody);
  } catch {
    return json({ error: "Invalid or oversized request" }, 400);
  }
  const args = memoryRequest(body);
  if (!args) return json({ error: "Invalid request" }, 400);
  const op = String(args.op);
  // Not one the engine takes now: the request it was before the share, refused here.
  if (
    personShareOps.includes(op) &&
    !(await hasMemoryShare(request.signal, fetcher))
  )
    return json({ error: "Invalid request" }, 400);
  const on = await served(request.signal, fetcher);
  // A person's link steps (run 17) while the engine serves no view: the request each was before them, refused here. So is a list's or
  // its tags' words (run 22), a member no request had before them.
  if (!on && (personLinkOps.includes(op) || Object.hasOwn(args, "query")))
    return json({ error: "Invalid request" }, 400);
  if (!on) return json({ available: false });
  try {
    const answer = cleanMemoryAnswer(
      op,
      await callTool("app_memories", args, request.signal, fetcher),
    );
    if (!answer || !object(answer)) throw new Error("Invalid memories answer");
    return json({ available: true, ...answer });
  } catch (error) {
    // The engine turned memory.tools off since its listing was read: off, and the listing read again.
    if (error instanceof EngineError && error.reason === "forbidden") {
      forgetTools();
      return json({ available: false });
    }
    answerRejected("app_memories", error, request.signal);
    return json(unavailable(!memoryReads.includes(op)));
  }
}
