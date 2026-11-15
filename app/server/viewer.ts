import { settings } from "./config.js";
import { json, readJson, requestGuard } from "./http.js";
import {
  answerRejected,
  callTool,
  EngineError,
  listTools,
  sharedToken,
} from "./engine.js";
import {
  cookieValue,
  credentialOf,
  maxAgeSeconds,
  newSession,
  runAs,
  sessionOf,
} from "./viewerScope.js";
import type { ViewerSession } from "./viewerScope.js";

// Viewer sessions (docs/security.md, Viewer sessions; SB-P1-11): off unless rhythm.yaml says
// app.viewers: on, which this version refuses at start (viewerGate.ts). While off, nothing here
// reads a cookie or changes a call: every route answers exactly as before, with the app token's
// household view, and /api/viewer answers {available: false}.
//
// While on, someone at the app types a one-time code the operator made for one person on the host
// (rhythm viewer invite). This server makes a new session (viewerScope.ts), registers its
// credential with the engine using the app token and that code (app_viewer enrol), and sets an
// HttpOnly cookie that holds the session's handle, never a credential and never a person: the
// engine reads the person from the credential alone. Each later request with that cookie reaches
// the engine with the session's own credential. A request without one, or with a cookie this
// server did not make, gets the household view, as before. A header or a cookie is never taken as
// identity, and no token reaches the browser.
//
// The Node server and the dev server run every route through withViewerSessions; a hosted front
// end's api/ functions call the handlers directly, so there every reader gets the household view.

const plainName = "rhythm-viewer";
// Over HTTPS the cookie takes the __Host- prefix: sent only to this exact host, over HTTPS, path /.
const secureName = "__Host-rhythm-viewer";
const secure = (request: Request) => new URL(request.url).protocol === "https:";
const cookieName = (request: Request) =>
  secure(request) ? secureName : plainName;

const on = () => {
  try {
    return settings().viewers;
  } catch {
    return false;
  }
};

// The value of this request's session cookie, or null when it sent none.
function cookieOf(request: Request): string | null {
  const wanted = cookieName(request);
  for (const part of (request.headers.get("cookie") ?? "").split(";")) {
    const at = part.indexOf("=");
    if (at > 0 && part.slice(0, at).trim() === wanted)
      return part.slice(at + 1).trim();
  }
  return null;
}

const setCookie = (request: Request, value: string, age: number) =>
  `${cookieName(request)}=${value}; Path=/; HttpOnly; SameSite=Strict; Max-Age=${age}${secure(request) ? "; Secure" : ""}`;

function withCookie(response: Response, cookie: string): Response {
  const headers = new Headers(response.headers);
  headers.append("Set-Cookie", cookie);
  return new Response(response.body, {
    status: response.status,
    statusText: response.statusText,
    headers,
  });
}
const cleared = (request: Request, response: Response) =>
  withCookie(response, setCookie(request, "", 0));

// This request's session: none (no cookie), forged (a cookie this server did not make with this app
// token, or one past its 30 days), or a session.
function readSession(request: Request): {
  sent: boolean;
  session: ViewerSession | null;
} {
  const value = cookieOf(request);
  if (value === null) return { sent: false, session: null };
  return { sent: true, session: sessionOf(value, sharedToken()) };
}

type Handler = (request: Request) => Promise<Response>;

// Every route but /api/viewer itself: with viewer sessions off, the handler as it was. With them
// on, a request with a valid session cookie is answered with the session's credential; one whose
// cookie is not valid gets the household view and the cookie cleared; and when the engine refuses
// the session's credential (signed out, revoked, its person suspended) the cookie is cleared too,
// so the browser's next request is the household view's.
export function withViewerSessions(
  routes: Record<string, Handler>,
): Record<string, Handler> {
  const wrapped: Record<string, Handler> = {};
  for (const [path, handler] of Object.entries(routes))
    wrapped[path] = async (request) => {
      if (!on()) return handler(request);
      const { sent, session } = readSession(request);
      if (!sent) return handler(request);
      if (!session) return cleared(request, await handler(request));
      const { value, ended } = await runAs(session, () => handler(request));
      return ended ? cleared(request, value) : value;
    };
  wrapped["/api/viewer"] = (request) => handleViewer(request);
  return wrapped;
}

const person = (id: string) => {
  const known = settings().people.find((p) => p.id === id);
  return known ? { id: known.id, name: known.name } : { id, name: id };
};
const personOf = (result: Record<string, unknown>) =>
  typeof result.person === "string" &&
  /^[a-z][a-z0-9-]{0,31}$/.test(result.person)
    ? person(result.person)
    : null;

// Whether the engine offers viewer sessions now (app_viewer is listed only while its app.viewers
// is on): true or false from a listing it gave, null when there is none to go by (the engine is
// down or restarting, the listing failed a moment ago, or the app token's credential expired).
// Asked with the app token, outside any session.
async function offered(
  signal: AbortSignal,
  fetcher: typeof fetch,
): Promise<boolean | null> {
  const tools = await runAs(null, () => listTools(signal, fetcher));
  return tools.value ? tools.value.has("app_viewer") : null;
}

// GET /api/viewer: {available, person}: whether viewer sessions can be opened here, and the person
// this browser's session speaks for (null: the household view). POST {op: "enrol", code} opens
// one with an operator's code; POST {op: "end"} signs this browser's session out.
export async function handleViewer(
  request: Request,
  fetcher: typeof fetch = fetch,
): Promise<Response> {
  if (request.method !== "GET" && request.method !== "POST")
    return json({ error: "Method not allowed" }, 405, { Allow: "GET, POST" });
  const rejected = requestGuard(request, request.method);
  if (rejected) return rejected;
  if (!on() || !sharedToken())
    return request.method === "GET"
      ? json({ available: false })
      : json({ error: "Viewer sessions are off" }, 404);
  const { sent, session } = readSession(request);
  if (request.method === "GET") {
    // The engine no longer offers them (its app.viewers is off): a cookie this browser still sends
    // is cleared, as the engine refuses its credential (401) anyway. When it could not be asked,
    // nothing is known: the cookie stays, so a session outlives an engine restart.
    const offers = await offered(request.signal, fetcher);
    if (offers === null)
      return json({ error: "Source response unavailable or invalid" }, 502);
    if (!offers) {
      const answer = json({ available: false });
      return sent ? cleared(request, answer) : answer;
    }
    if (!session) {
      const answer = json({ available: true, person: null });
      return sent ? cleared(request, answer) : answer;
    }
    try {
      const { value, ended } = await runAs(session, () =>
        callTool("app_viewer", { op: "whoami" }, request.signal, fetcher),
      );
      const who = personOf(value);
      const answer = json({ available: true, person: who });
      return ended || !who ? cleared(request, answer) : answer;
    } catch (error) {
      if (error instanceof EngineError && error.reason === "engine_auth")
        return cleared(request, json({ available: true, person: null }));
      answerRejected("app_viewer", error, request.signal);
      return json({ error: "Source response unavailable or invalid" }, 502);
    }
  }
  let body: unknown;
  try {
    body = await readJson(request, 1024);
  } catch {
    return json({ error: "Invalid or oversized request" }, 400);
  }
  const fields =
    body && typeof body === "object" && !Array.isArray(body)
      ? (body as Record<string, unknown>)
      : {};
  const keys = Object.keys(fields).sort().join(",");
  if (fields.op === "end" && keys === "op") {
    // Signed out here whatever the engine answers. When the engine can't be reached the credential
    // stays enabled there until its 30 days end (ViewerSessions.Lifetime) or the operator revokes
    // it; no browser holds its handle any more.
    if (session)
      try {
        await runAs(session, () =>
          callTool("app_viewer", { op: "end" }, request.signal, fetcher),
        );
      } catch (error) {
        answerRejected("app_viewer", error, request.signal);
      }
    return cleared(request, json({ available: true, person: null }));
  }
  if (
    fields.op !== "enrol" ||
    keys !== "code,op" ||
    typeof fields.code !== "string" ||
    fields.code.length < 1 ||
    fields.code.length > 40
  )
    return json({ error: "Invalid or oversized request" }, 400);
  // A new session for this browser; one it had before is signed out first.
  if (session)
    await runAs(session, () =>
      callTool("app_viewer", { op: "end" }, request.signal, fetcher),
    ).catch(() => {});
  const token = sharedToken();
  const fresh = newSession();
  try {
    const { value } = await runAs(null, () =>
      callTool(
        "app_viewer",
        {
          op: "enrol",
          code: fields.code,
          credential: credentialOf(token, fresh),
        },
        request.signal,
        fetcher,
      ),
    );
    const who = personOf(value);
    if (!who) {
      const refused = json(
        { error: "That code did not work. Ask for a new one." },
        400,
      );
      return session ? cleared(request, refused) : refused;
    }
    return withCookie(
      json({ available: true, person: who }),
      setCookie(request, cookieValue(token, fresh), maxAgeSeconds),
    );
  } catch (error) {
    if (error instanceof EngineError && error.reason === "not_connected")
      return json({ error: "Viewer sessions are off" }, 404);
    answerRejected("app_viewer", error, request.signal);
    return json({ error: "Source response unavailable or invalid" }, 502);
  }
}
