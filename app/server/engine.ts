import { readFileSync, statSync } from "node:fs";
import { settings } from "./config.js";
import { engineFetch } from "./engineFetch.js";
import { warnOnce } from "./log.js";
import { viewerRefused, viewerToken } from "./viewerScope.js";

// The one engine client (contracts/README.md section 1). Every call is a single JSON-RPC
// tools/call to {engineUrl}/mcp with the one app token, except a document's original file, a
// plain GET (section 1.5). Inside a viewer session (only with app.viewers on, viewerScope.ts) a
// call carries that session's own credential instead of the app token, and never both. The
// browser never reaches the engine and never sees the token or a credential;
// nothing here logs a token, a request body or a response body (engineFetch.ts names a failed
// connection by the call and the kind of failure only; failure() below names any other failure).

export type AppTool =
  | "app_graph"
  | "app_insights"
  | "app_insight_feedback"
  | "app_dashboards"
  | "app_request_refresh"
  | "app_latest_glucose"
  | "app_recent_glucose"
  | "app_garmin_summary"
  | "app_request_garmin_sync"
  | "app_upload_document"
  | "app_screens"
  | "app_status"
  | "app_viewer"
  | "app_sync"
  | "app_memories"
  | "app_studio";

// Why a call did not happen or did not succeed. The app shows these as "unavailable" (or, for
// not_connected, as "Not connected"); none of them ever falls back to demo data.
export type EngineReason =
  | "not_configured"
  | "credential_expired"
  | "not_connected"
  | "engine_auth"
  | "forbidden"
  | "invalid_arguments"
  | "not_found"
  | "too_large"
  | "unavailable"
  | "invalid_response";

export class EngineError extends Error {
  readonly reason: EngineReason;
  // The engine's own words for a refusal (an invalid_arguments or not_found error's message),
  // when it gave them: for a caller to match against what it knows, never to pass on or log.
  readonly said: string | null;
  constructor(reason: EngineReason, said: string | null = null) {
    super(`Engine call failed: ${reason}`);
    this.reason = reason;
    this.said = said;
  }
}

// Tools that register only while their adapter (or a glucose source) is on (section 2.2), and
// app_screens and app_status, which an engine older than live screens or the model status
// doesn't have: screens are then off, and Sources & settings shows no models. app_viewer is
// listed only while viewer sessions are on (app.viewers, docs/security.md). app_sync is a core
// tool an engine older than Sync now doesn't have: Sources & settings then offers none. app_memories is listed only
// while the engine's memory.tools is on: the Memories view is then offered (memories.ts).
const gated: ReadonlySet<AppTool> = new Set([
  "app_latest_glucose",
  "app_recent_glucose",
  "app_garmin_summary",
  "app_request_garmin_sync",
  "app_upload_document",
  "app_screens",
  "app_status",
  "app_viewer",
  "app_sync",
  "app_memories",
  "app_studio",
]);

const defaultTokenFile = "/run/secrets/app_token";
// Every call fits in 32 KiB except an added document, which carries its file (up to 7 MiB), and a memory's write,
// which may carry its whole text (up to 288 KiB).
const maxRequest = (method: string, tool: unknown) =>
  method === "tools/call" && tool === "app_upload_document"
    ? 7 * 1024 * 1024
    : method === "tools/call" && tool === "app_memories"
      ? 288 * 1024
      : 32 * 1024;
// A call's largest answer, its time for one try, and its total time with the tools/list check and
// a retry, by tool and shape. A dashboard's feeds can read several stars and files, so they get
// longer, but a retry shares that same total; a map part's focused sky is a page of the map, so
// it gets app_graph's. A screen's listen waits up to 25 seconds on the engine for a command, so
// it gets 35, one try only (below): the browser waits 40 and a hosted front end 60.
const limits = (
  tool: string,
  args: Record<string, unknown> = {},
): [bytes: number, ms: number, total: number] =>
  tool === "app_studio"
    ? [6_400_000, 25_000, 25_000]
    : tool === "app_dashboards" && !Object.hasOwn(args, "map")
      ? [3_600_000, 25_000, 25_000]
      : tool === "app_screens"
        ? [2_000_000, 35_000, 35_000]
        : tool === "app_upload_document"
          ? [100_000, 60_000, 60_000]
          : [2_000_000, 8_000, 16_000];

// Calls that only read, so a second try changes nothing on the engine. Feedback, a dashboard's
// change (a layout save above all: a second try after a lost answer would meet its own revision
// as stale and apply a relative change twice), a pin or a card, a rebuild or sync request, an
// added document and every app_screens call (a listen claims a command, a send stores one) are
// never repeated. A map part's focused sky only reads.
const reads: ReadonlySet<AppTool> = new Set([
  "app_graph",
  "app_insights",
  "app_latest_glucose",
  "app_recent_glucose",
  "app_garmin_summary",
  "app_status",
]);
const dashboardReads = [
  "id",
  "version",
  "data",
  "cards",
  "pins",
  "map",
  "cursor",
];
// app_sync's list only reads; asking an adapter for a batch is never sent twice. app_memories' list, trash, get,
// history and projects only read; its writes are never sent twice.
const memoryReadOps = ["list", "trash", "get", "history", "projects"];
const repeatable = (tool: AppTool, args: Record<string, unknown>) =>
  reads.has(tool) ||
  (tool === "app_memories" && memoryReadOps.includes(String(args.op))) ||
  (tool === "app_dashboards" &&
    Object.keys(args).every((key) => dashboardReads.includes(key))) ||
  (tool === "app_sync" && !Object.hasOwn(args, "adapter"));

let token: { key: string; value: string } | null = null;

// The token, re-read whenever its file changes. Missing, empty, unreadable or a directory
// (Docker makes an empty directory for a secret file that did not exist) means "not configured".
// RHYTHM_APP_TOKEN is only for hosts whose secrets are environment values (deploy/vercel/) and
// is ignored whenever RHYTHM_APP_TOKEN_FILE is set.
function appToken(env: NodeJS.ProcessEnv): string {
  if (!env.RHYTHM_APP_TOKEN_FILE && env.RHYTHM_APP_TOKEN)
    return env.RHYTHM_APP_TOKEN.trim();
  const path = env.RHYTHM_APP_TOKEN_FILE || defaultTokenFile;
  try {
    const stat = statSync(path);
    if (!stat.isFile()) return "";
    const key = `${path}:${stat.mtimeMs}:${stat.size}`;
    if (token?.key !== key)
      token = { key, value: readFileSync(path, "utf8").trim() };
    return token.value;
  } catch (error) {
    // The one thing worth saying out loud: the file is there but this user cannot read it.
    if ((error as NodeJS.ErrnoException).code === "EACCES" && !warned) {
      warned = true;
      console.error(
        `The app token file is not readable by this user${typeof process.getuid === "function" ? ` (uid ${process.getuid()})` : ""}; running in demo mode.`,
      );
    }
    return "";
  }
}
let warned = false;

// Optional Cloudflare Access service-token headers, for a hosted front end that reaches the
// engine through Cloudflare Access (deploy/vercel/). Off unless both parts are set.
function accessHeaders(env: NodeJS.ProcessEnv): Record<string, string> {
  const id = env.RHYTHM_CF_ACCESS_CLIENT_ID;
  let secret = env.RHYTHM_CF_ACCESS_CLIENT_SECRET ?? "";
  if (env.RHYTHM_CF_ACCESS_CLIENT_SECRET_FILE)
    try {
      secret = readFileSync(env.RHYTHM_CF_ACCESS_CLIENT_SECRET_FILE, "utf8");
    } catch {
      secret = "";
    }
  secret = secret.trim();
  return id && secret
    ? { "CF-Access-Client-Id": id, "CF-Access-Client-Secret": secret }
    : {};
}

export interface EngineTarget {
  url: string;
  token: string;
  expired: boolean;
}

// The engine this app talks to, or null in demo mode. RHYTHM_ENGINE_URL, when set and not empty,
// wins over engine.url in rhythm.yaml. Its token is the app token, except while answering a
// request of a viewer session (viewerScope.ts, only with app.viewers on), whose own credential
// it is then.
export function engineTarget(
  env: NodeJS.ProcessEnv = process.env,
  now = Date.now(),
): EngineTarget | null {
  const s = settings(env);
  const url = (env.RHYTHM_ENGINE_URL || s.engineUrl).replace(/\/$/, "");
  if (!url || !/^https?:\/\/[^/?#@\s]+$/.test(url)) return null;
  const value = appToken(env);
  if (!value) return null;
  const expiry = s.credentialExpiry ? Date.parse(s.credentialExpiry) : NaN;
  return {
    url,
    token: viewerToken(value),
    expired: Number.isFinite(expiry) && now >= expiry,
  };
}

// The app token itself, never a viewer session's credential, or "" in demo mode: the key a
// viewer session's cookie and credential are made with (viewer.ts).
export function sharedToken(env: NodeJS.ProcessEnv = process.env): string {
  return engineTarget(env) ? appToken(env) : "";
}

export const engineMode = (env: NodeJS.ProcessEnv = process.env) =>
  engineTarget(env) ? ("live" as const) : ("demo" as const);

const reasonFor = (status: number): EngineReason =>
  status === 401
    ? "engine_auth"
    : status === 403
      ? "forbidden"
      : status === 400
        ? "invalid_arguments"
        : status === 404
          ? "not_found"
          : status === 413
            ? "too_large"
            : "unavailable";
const codes: Record<string, EngineReason> = {
  unauthorized: "engine_auth",
  forbidden: "forbidden",
  invalid_arguments: "invalid_arguments",
  not_found: "not_found",
  too_large: "too_large",
  unavailable: "unavailable",
};
const object = (v: unknown): v is Record<string, unknown> =>
  !!v && typeof v === "object" && !Array.isArray(v);

// What kind of answer came back, from its Content-Type: a front door's page (html) or nothing at
// all reads differently from the engine's own JSON. Never the header itself.
const answerKind = (response: Response) => {
  const type = (response.headers.get("content-type") ?? "").toLowerCase();
  return !type
    ? "none"
    : type.includes("json")
      ? "json"
      : type.includes("event-stream")
        ? "event-stream"
        : type.includes("html")
          ? "html"
          : "other";
};

// A failed call, as its EngineError, logged once a minute for each kind (log.ts) with fixed fields
// only: the call's name, the reason, and the engine's HTTP status and kind of answer when an answer
// came (a JSON-RPC result that is a tool's error or the wrong shape is status 200, answer
// "result"). Never a URL, a header, a body, a value or a token. A request the browser gave up on is
// not logged, nor demo mode or an adapter that is off (never passed here); a connection that failed
// is logged by engineFetch.
function failure(
  call: string,
  reason: EngineReason,
  signal: AbortSignal | undefined,
  response?: Response | "result",
  said: string | null = null,
): EngineError {
  if (!signal?.aborted)
    warnOnce("rhythm-engine-call", {
      call,
      reason,
      status: response === "result" ? 200 : (response?.status ?? null),
      answer:
        response === "result"
          ? "result"
          : response
            ? answerKind(response)
            : null,
    });
  return new EngineError(reason, said);
}

// An engine answer the facade's own checks refused (the contract's shapes, in src/validation.ts
// and the clean functions) or a fault in the facade while reading it: logged by the call's name
// and the error's type, once a minute (log.ts), so a mismatch between app and engine versions shows
// in the log rather than only as "unavailable". An EngineError was logged where it was thrown.
export function answerRejected(
  call: AppTool | "document-file",
  error: unknown,
  signal?: AbortSignal,
) {
  if (error instanceof EngineError || signal?.aborted) return;
  const type =
    error instanceof Error && /^[A-Za-z]{1,40}$/.test(error.name)
      ? error.name
      : "unknown";
  warnOnce("rhythm-engine-call", {
    call,
    reason: "answer_rejected",
    error: type,
  });
}

// A refusal's message as the engine wrote it (an isError result naming its code and message,
// contracts/README.md 1.4), read from at most 2 KiB of the answer; null when there is none.
async function refusalOf(response: Response): Promise<string | null> {
  if (!(response.headers.get("content-type") ?? "").includes("json"))
    return null;
  const reader = response.body?.getReader();
  if (!reader) return null;
  try {
    const chunks: Uint8Array[] = [];
    let size = 0;
    for (;;) {
      const part = await reader.read();
      if (part.done) break;
      size += part.value.length;
      if (size > 2048) return null;
      chunks.push(part.value);
    }
    const all = new Uint8Array(size);
    let offset = 0;
    for (const chunk of chunks) {
      all.set(chunk, offset);
      offset += chunk.length;
    }
    const answer: unknown = JSON.parse(new TextDecoder().decode(all));
    const inside = (v: unknown, key: string) =>
      object(v) && object(v[key]) ? v[key] : {};
    const error = inside(
      inside(inside(answer, "result"), "structuredContent"),
      "error",
    );
    return typeof error.message === "string" ? error.message : null;
  } catch {
    return null;
  } finally {
    await reader.cancel().catch(() => {});
  }
}

// One request per HTTP request, so one id serves to pair every answer with its call.
export const requestId = "rhythm-app";

// One JSON-RPC request; returns its `result` member. Bounded in size and time, no redirects,
// no cookies, no Origin. Only a read (`retry`) is sent a second time, within the total.
async function rpc(
  target: EngineTarget,
  method: string,
  params: Record<string, unknown>,
  limit: [bytes: number, ms: number, total: number],
  signal: AbortSignal | undefined,
  fetcher: typeof fetch,
  env: NodeJS.ProcessEnv,
  retry = false,
): Promise<Record<string, unknown>> {
  const call = method === "tools/call" ? String(params.name) : method;
  if (target.expired) throw failure(call, "credential_expired", signal);
  const id = requestId;
  const body = JSON.stringify({ jsonrpc: "2.0", id, method, params });
  if (new TextEncoder().encode(body).length > maxRequest(method, params.name))
    throw failure(call, "too_large", signal);
  const [bytes, ms, total] = limit;
  if (signal?.aborted) throw new EngineError("unavailable");
  let response: Response;
  try {
    response = await engineFetch(
      call,
      `${target.url}/mcp`,
      {
        method: "POST",
        redirect: "error",
        cache: "no-store",
        credentials: "omit",
        signal,
        headers: {
          "Content-Type": "application/json",
          Accept: "application/json, text/event-stream",
          "MCP-Protocol-Version": "2025-03-26",
          ...accessHeaders(env),
          Authorization: `Bearer ${target.token}`,
        },
        body,
      },
      ms,
      retry,
      fetcher,
      total,
    );
  } catch {
    // Logged by engineFetch, unless the browser gave up.
    throw new EngineError("unavailable");
  }
  const type = response.headers.get("content-type") ?? "";
  if (!response.ok) {
    const reason = reasonFor(response.status);
    // Inside a viewer session, a 401 ends it: its credential was signed out, revoked or its
    // person suspended.
    if (reason === "engine_auth") viewerRefused();
    const said =
      reason === "invalid_arguments" || reason === "not_found"
        ? await refusalOf(response)
        : null;
    await response.body?.cancel().catch(() => {});
    throw failure(call, reason, signal, response, said);
  }
  const stream = type.includes("text/event-stream");
  if (!stream && !type.includes("application/json")) {
    await response.body?.cancel().catch(() => {});
    throw failure(call, "invalid_response", signal, response);
  }
  const chunks: Uint8Array[] = [];
  let size = 0;
  try {
    const reader = response.body?.getReader();
    if (!reader) throw failure(call, "invalid_response", signal, response);
    for (;;) {
      const part = await reader.read();
      if (part.done) break;
      size += part.value.length;
      if (size > bytes) {
        await reader.cancel().catch(() => {});
        throw failure(call, "too_large", signal, response);
      }
      chunks.push(part.value);
    }
  } catch (error) {
    // The answer broke off on the way (or ran out of time) after it began.
    throw error instanceof EngineError
      ? error
      : failure(call, "unavailable", signal, response);
  }
  const all = new Uint8Array(size);
  let offset = 0;
  for (const chunk of chunks) {
    all.set(chunk, offset);
    offset += chunk.length;
  }
  const text = new TextDecoder().decode(all);
  let message: unknown;
  try {
    if (stream) {
      // The first data: message that answers this request.
      message = undefined;
      for (const event of text.split(/\r?\n\r?\n/)) {
        const data = event
          .split(/\r?\n/)
          .filter((line) => line.startsWith("data:"))
          .map((line) => line.slice(5).trimStart())
          .join("\n");
        if (!data) continue;
        const parsed: unknown = JSON.parse(data);
        if (object(parsed) && parsed.id === id) {
          message = parsed;
          break;
        }
      }
    } else message = JSON.parse(text);
  } catch {
    throw failure(call, "invalid_response", signal, response);
  }
  if (
    !object(message) ||
    message.jsonrpc !== "2.0" ||
    message.id !== id ||
    "error" in message ||
    !object(message.result)
  )
    throw failure(call, "invalid_response", signal, response);
  return message.result;
}

// tools/list, cached for at most five minutes (section 1.4). A failed listing is retried after
// half a minute and says nothing about which tools exist. Beside each tool's name it keeps the
// names of its input's properties, and of their own properties one level down as `name.sub`
// (`settings.grid`), which is how the facade learns what an engine takes (docs/dev/screens.md
// 10.10), and each string a property's enum lists as `name=value` (`operation=recall`).
const listTtl = 5 * 60_000,
  retryTtl = 30_000;
// The glucose tools are listed once any glucose source has data (section 2.2), which a plugin's or a feed's first reading
// changes with no setting changed and no restart. A listing that lacks the one a read asks for is asked again at that read,
// at most every absentTtl, so the first reading reaches Home at the next read rather than at the next five-minute listing.
// A listing that names it keeps its five minutes.
const listedOnData: ReadonlySet<string> = new Set([
    "app_latest_glucose",
    "app_recent_glucose",
  ]),
  absentTtl = 10_000;
type Listing = Map<string, Set<string>>;
// One listing per token: the app token's, and while viewer sessions are on each session's (the
// engine lists some tools only to a caller that reads their rows); the oldest is dropped past 64.
const listings = new Map<string, { at: number; tools: Listing | null }>();
// Set by tests: the tools the engine offers, and their input properties, without asking it.
let primed: Listing | null = null;
// A tool's input properties as tools/list names them.
function inputsOf(tool: Record<string, unknown>): Set<string> {
  const names = new Set<string>();
  const schema = object(tool.inputSchema) ? tool.inputSchema : {};
  const properties = object(schema.properties) ? schema.properties : {};
  for (const [name, value] of Object.entries(properties)) {
    names.add(name);
    if (object(value) && object(value.properties))
      for (const inner of Object.keys(value.properties))
        names.add(`${name}.${inner}`);
    if (object(value) && Array.isArray(value.enum))
      for (const listed of value.enum.slice(0, 50))
        if (typeof listed === "string" && listed.length <= 40)
          names.add(`${name}=${listed}`);
  }
  return names;
}

async function readListing(
  signal?: AbortSignal,
  fetcher: typeof fetch = fetch,
  env: NodeJS.ProcessEnv = process.env,
  now = Date.now(),
  wanted?: string,
): Promise<Listing | null> {
  const target = engineTarget(env, now);
  if (!target || target.expired) return null;
  if (primed) return primed;
  const key = `${target.url}\n${target.token}`;
  const listing = listings.get(key);
  const ttl = !listing?.tools
    ? retryTtl
    : wanted && listedOnData.has(wanted) && !listing.tools.has(wanted)
      ? absentTtl
      : listTtl;
  if (listing && now - listing.at < ttl) return listing.tools;
  let tools: Listing | null = new Map();
  try {
    let cursor: string | undefined;
    for (let page = 0; page < 5; page++) {
      const result = await rpc(
        target,
        "tools/list",
        cursor ? { cursor } : {},
        // One try: an unknown listing lets the call go ahead, and the call retries itself.
        [2_000_000, 8_000, 8_000],
        signal,
        fetcher,
        env,
      );
      if (!Array.isArray(result.tools))
        throw failure("tools/list", "invalid_response", signal, "result");
      for (const t of result.tools)
        if (object(t) && typeof t.name === "string")
          tools.set(t.name, inputsOf(t));
      if (typeof result.nextCursor !== "string" || !result.nextCursor) break;
      cursor = result.nextCursor;
    }
  } catch {
    tools = null;
  }
  listings.delete(key);
  listings.set(key, { at: now, tools });
  if (listings.size > 64) listings.delete(listings.keys().next().value!);
  return tools;
}
export async function listTools(
  signal?: AbortSignal,
  fetcher: typeof fetch = fetch,
  env: NodeJS.ProcessEnv = process.env,
  now = Date.now(),
): Promise<Set<string> | null> {
  const tools = await readListing(signal, fetcher, env, now);
  return tools && new Set(tools.keys());
}

// Whether the engine has piece 3 (docs/dev/screens.md 10.10): it lists `grid` among app_screens'
// settings, and `layout` and `map` among app_dashboards' arguments. A listing the facade
// couldn't read counts as no piece 3, the opposite of offered's default, since an older engine
// refuses settings members and argument shapes it doesn't know.
export async function hasPiece3(
  tool: "app_screens" | "app_dashboards",
  signal?: AbortSignal,
  fetcher: typeof fetch = fetch,
  env: NodeJS.ProcessEnv = process.env,
): Promise<boolean> {
  const inputs = (await readListing(signal, fetcher, env))?.get(tool);
  if (!inputs) return false;
  return tool === "app_screens"
    ? inputs.has("settings.grid")
    : inputs.has("layout") && inputs.has("map");
}
// Whether the engine serves the explorer's combined search (SB-P2-10): app_graph lists the
// operation recall, which it does only while engine.recall is on. Off with no engine to ask (no
// URL or token, or an expired credential). null when the facade couldn't read the listing: the
// engine has said neither, so the caller answers that it does not know (retry), the explorer
// shows its title search alone and asks again later (the listing itself is retried after half
// a minute).
export async function hasRecall(
  signal?: AbortSignal,
  fetcher: typeof fetch = fetch,
  env: NodeJS.ProcessEnv = process.env,
): Promise<boolean | null> {
  const target = engineTarget(env);
  if (!target || target.expired) return false;
  const listing = await readListing(signal, fetcher, env);
  if (!listing) return null;
  return !!listing.get("app_graph")?.has("operation=recall");
}
// Whether the engine takes a person's share in the Memories view (run 14): app_memories lists the op share, which it
// does only while memory.share is on beside memory.tools and memory scopes are on, and only to an app login that
// speaks for a person. The listing read here is the app's own token's (the household view, no person), so in this
// version it never names share: the view offers none, since every share the household view asked for would be
// refused; a viewer session's own listing is for when app.viewers is turned on (D-046). False with no engine to ask,
// and when the facade couldn't read the listing (the view then offers no share, and asks again with the listing).
export async function hasMemoryShare(
  signal?: AbortSignal,
  fetcher: typeof fetch = fetch,
  env: NodeJS.ProcessEnv = process.env,
): Promise<boolean> {
  const target = engineTarget(env);
  if (!target || target.expired) return false;
  return !!(await readListing(signal, fetcher, env))
    ?.get("app_memories")
    ?.has("op=share");
}
// Forgets the cached tool listing, so the next call asks the engine again: after the engine
// refused what its listing said it takes.
export function forgetTools() {
  listings.clear();
}

// Whether the engine offers a tool now. Core tools always count as offered; an adapter tool
// absent from the latest listing is "adapter off". An unknown listing lets the call go ahead,
// so an outage shows as unavailable rather than as not connected.
export async function offered(
  tool: AppTool,
  signal?: AbortSignal,
  fetcher: typeof fetch = fetch,
  env: NodeJS.ProcessEnv = process.env,
): Promise<boolean> {
  if (!gated.has(tool)) return true;
  const tools = await readListing(signal, fetcher, env, Date.now(), tool);
  return tools === null || tools.has(tool);
}

// Calls one app tool and returns its structuredContent. Throws EngineError: not_configured in
// demo mode, not_connected for an adapter tool the engine does not offer, and the contract's
// failure reasons otherwise.
export async function callTool(
  tool: AppTool,
  args: Record<string, unknown> = {},
  signal?: AbortSignal,
  fetcher: typeof fetch = fetch,
  env: NodeJS.ProcessEnv = process.env,
): Promise<Record<string, unknown>> {
  const target = engineTarget(env);
  if (!target) throw new EngineError("not_configured");
  if (target.expired) throw failure(tool, "credential_expired", signal);
  const started = Date.now();
  const [bytes, ms, total] = limits(tool, args);
  if (!(await offered(tool, signal, fetcher, env)))
    throw new EngineError("not_connected");
  const result = await rpc(
    target,
    "tools/call",
    { name: tool, arguments: args },
    [bytes, ms, total - (Date.now() - started)],
    signal,
    fetcher,
    env,
    repeatable(tool, args),
  );
  const content = result.structuredContent;
  if (result.isError === true) {
    const error =
      object(content) && object(content.error) ? content.error : null;
    const reason =
      (error && typeof error.code === "string" && codes[error.code]) ||
      "unavailable";
    throw failure(
      tool,
      reason,
      signal,
      "result",
      (reason === "invalid_arguments" || reason === "not_found") &&
        typeof error?.message === "string"
        ? error.message
        : null,
    );
  }
  if (!object(content))
    throw failure(tool, "invalid_response", signal, "result");
  return content;
}

// A document's original file (contracts/README.md 1.5): one GET {engineUrl}/app/documents/<id>
// with the app token, no redirects, no cookies, no Origin. Answers the engine's response for the
// caller to stream on; the body is never read or kept here. Throws EngineError like a tool call.
export async function documentFile(
  id: string,
  signal?: AbortSignal,
  fetcher: typeof fetch = fetch,
  env: NodeJS.ProcessEnv = process.env,
  ms = 120_000,
): Promise<Response> {
  const target = engineTarget(env);
  if (!target) throw new EngineError("not_configured");
  if (target.expired)
    throw failure("document-file", "credential_expired", signal);
  if (
    !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(id)
  )
    throw new EngineError("invalid_arguments");
  if (signal?.aborted) throw new EngineError("unavailable");
  let response: Response;
  try {
    // A read: a failed connection or a gateway's 502/503/504 is tried once more, in the same time.
    response = await engineFetch(
      "document-file",
      `${target.url}/app/documents/${id}`,
      {
        method: "GET",
        redirect: "error",
        cache: "no-store",
        credentials: "omit",
        signal,
        headers: {
          // The bytes as stored, never compressed on the way: the length is checked against
          // Content-Length, which a compressing proxy drops or leaves at the compressed size.
          "Accept-Encoding": "identity",
          ...accessHeaders(env),
          Authorization: `Bearer ${target.token}`,
        },
      },
      ms,
      true,
      fetcher,
      ms,
    );
  } catch {
    // Logged by engineFetch, unless the browser gave up.
    throw new EngineError("unavailable");
  }
  if (!response.ok) {
    if (response.status === 401) viewerRefused();
    await response.body?.cancel().catch(() => {});
    throw failure(
      "document-file",
      reasonFor(response.status),
      signal,
      response,
    );
  }
  return response;
}

// For tests: forget the cached token and tool listing, and optionally fix the listing, with
// each tool's input properties when given (none reads as an engine from before piece 3).
export function resetEngine(
  tools?: Iterable<string>,
  inputs: Record<string, Iterable<string>> = {},
) {
  token = null;
  listings.clear();
  primed = tools
    ? new Map([...tools].map((t) => [t, new Set(inputs[t] ?? [])]))
    : null;
}
