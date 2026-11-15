import { AsyncLocalStorage } from "node:async_hooks";
import { createHmac, randomBytes, timingSafeEqual } from "node:crypto";

// A viewer session as the app's server holds it (docs/security.md, Viewer sessions; off unless app.viewers is on,
// which this version refuses): when its cookie was issued and a random handle. Nothing else is kept anywhere on the
// app's side. The session's engine credential is derived from the two and the app token (credentialOf), the app's own
// secret, so the browser holds no credential (its cookie is HttpOnly and opens nothing without the app token) and the
// engine never sees the handle. The engine registered that credential, bound to one person, when the session was opened
// with an operator's one-time code (app_viewer enrol), and reads the person from it alone. A cookie names no one: who
// the session speaks for is the engine's to say.
export interface ViewerSession {
  issued: number;
  handle: string;
}

// How long a session lasts in this browser at most: 30 days from when it was opened.
export const maxAgeSeconds = 30 * 24 * 60 * 60;

const mac = (key: string, label: string, session: ViewerSession) =>
  createHmac("sha256", key)
    .update(`rhythm-viewer-${label}-v1\n${session.issued}\n${session.handle}`)
    .digest();

// The engine credential of a session: 43 characters of base64url, as a token is.
export const credentialOf = (appToken: string, session: ViewerSession) =>
  mac(appToken, "credential", session).toString("base64url");

const tagOf = (appToken: string, session: ViewerSession) =>
  mac(appToken, "cookie", session).subarray(0, 16).toString("base64url");

export const newSession = (now = Date.now()): ViewerSession => ({
  issued: Math.floor(now / 1000),
  handle: randomBytes(32).toString("base64url"),
});

// The cookie's value: version, issue time, handle and a tag, so a cookie the app's server did not make (or made with
// another app token) is told apart without asking the engine and read as no session at all.
export const cookieValue = (appToken: string, session: ViewerSession) =>
  `v1.${session.issued}.${session.handle}.${tagOf(appToken, session)}`;

// The session a cookie's value stands for, or null: malformed, older than maxAgeSeconds (or issued in the future), or
// not made with this app token.
export function sessionOf(
  value: string,
  appToken: string,
  now = Date.now(),
): ViewerSession | null {
  const match =
    /^v1\.(\d{1,12})\.([A-Za-z0-9_-]{43})\.([A-Za-z0-9_-]{22})$/.exec(value);
  if (!match || !appToken) return null;
  const session = { issued: Number(match[1]), handle: match[2] };
  const age = Math.floor(now / 1000) - session.issued;
  if (age > maxAgeSeconds || age < -300) return null;
  const want = Buffer.from(tagOf(appToken, session));
  const got = Buffer.from(match[3]);
  return want.length === got.length && timingSafeEqual(want, got)
    ? session
    : null;
}

// The request being answered: the session it belongs to, if any, and whether the engine refused its credential.
interface Scope {
  session: ViewerSession | null;
  ended: boolean;
}
const scope = new AsyncLocalStorage<Scope>();

// Runs `work` with every engine call made for it carrying `session`'s credential (null: the app token, the household
// view). The answer says whether the engine refused that credential meanwhile (401: signed out, revoked, or its person
// suspended or gone), so the caller can end the session in the browser.
export async function runAs<T>(
  session: ViewerSession | null,
  work: () => Promise<T>,
): Promise<{ value: T; ended: boolean }> {
  const store: Scope = { session, ended: false };
  const value = await scope.run(store, work);
  return { value, ended: store.ended };
}

// engine.ts: the bearer token for a call. Inside a viewer session its credential, else the app token, as always.
export function viewerToken(appToken: string): string {
  const session = scope.getStore()?.session;
  return session ? credentialOf(appToken, session) : appToken;
}

// Whether the request being answered is a viewer session's (never true while app.viewers is off:
// viewer.ts then runs no request inside one). documents.ts takes a file's Family choice only then.
export const inViewerSession = () => !!scope.getStore()?.session;

// engine.ts, on a 401: inside a viewer session it was the session's credential the engine refused.
export function viewerRefused() {
  const store = scope.getStore();
  if (store?.session) store.ended = true;
}
