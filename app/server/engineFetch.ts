import { Agent } from "undici";

// One HTTP request to the engine, bounded in time, with one retry for a read. A read that failed
// to connect, timed out or met a gateway's 502/503/504 is sent once more, inside the same total
// budget; a denial, a cancelled request or any other answer is returned as it is, and a write is
// never repeated. A failure is logged by the call's fixed name, its kind and allowlisted transport
// codes only: never a URL, a header, a body, a token or the error's own text.

// How the app connects to the engine: try the host's other resolved addresses instead of letting
// one stalled connection use up a call's whole time, and stop connecting after five seconds. Its
// own dispatcher, passed with each request; the process-wide one is left alone. Undici 7, the
// version inside Node 24's own fetch, which takes it (undici 8 does not).
export const engineConnection = {
  autoSelectFamily: true,
  // Node can give up on a healthy first address during a brief event-loop stall (its default is
  // 250 ms); one second per address stays well inside every call's time.
  autoSelectFamilyAttemptTimeout: 1000,
  connectTimeout: 5000,
  allowH2: false,
};
const dispatcher = new Agent(engineConnection);

// Transport codes worth naming, and the kind of failure each one is.
const kinds: Record<string, string> = {
  UND_ERR_CONNECT_TIMEOUT: "timeout",
  UND_ERR_HEADERS_TIMEOUT: "timeout",
  ETIMEDOUT: "timeout",
  ECONNREFUSED: "refused",
  ECONNRESET: "reset",
  UND_ERR_SOCKET: "reset",
  ENOTFOUND: "dns",
  EAI_AGAIN: "dns",
  ENETUNREACH: "unreachable",
  EHOSTUNREACH: "unreachable",
  CERT_HAS_EXPIRED: "tls",
  DEPTH_ZERO_SELF_SIGNED_CERT: "tls",
  SELF_SIGNED_CERT_IN_CHAIN: "tls",
  UNABLE_TO_VERIFY_LEAF_SIGNATURE: "tls",
  ERR_TLS_CERT_ALTNAME_INVALID: "tls",
};

// Fetch refuses a redirect (redirect: "error") with this fixed message on its cause. A front door
// that sends the call to a sign-in page, as Cloudflare Access does without a valid service token,
// shows as this kind rather than as a network failure.
const redirected = (error: unknown) =>
  !!error &&
  typeof error === "object" &&
  "cause" in error &&
  error.cause instanceof Error &&
  error.cause.message === "unexpected redirect";

// The allowlisted codes on an error, its causes and an aggregate's errors, a few levels deep.
function transportCodes(error: unknown): string[] {
  const codes = new Set<string>();
  function visit(value: unknown, depth: number) {
    if (!value || typeof value !== "object" || depth > 3) return;
    if (
      "code" in value &&
      typeof value.code === "string" &&
      Object.hasOwn(kinds, value.code)
    )
      codes.add(value.code);
    if ("cause" in value) visit(value.cause, depth + 1);
    if ("errors" in value && Array.isArray(value.errors))
      value.errors.slice(0, 8).forEach((e) => visit(e, depth + 1));
  }
  visit(error, 0);
  return [...codes];
}

export async function engineFetch(
  call: string,
  url: string,
  init: RequestInit,
  timeout: number,
  retry: boolean,
  fetcher: typeof fetch = fetch,
  budget = timeout * 2,
) {
  const started = Date.now();
  for (let attempt = 0; ; attempt++) {
    const left = Math.max(
      1,
      Math.min(timeout, budget - (Date.now() - started)),
    );
    const signal = init.signal
      ? AbortSignal.any([init.signal, AbortSignal.timeout(left)])
      : AbortSignal.timeout(left);
    const again = () =>
      retry &&
      attempt === 0 &&
      Date.now() - started < budget &&
      !init.signal?.aborted;
    try {
      const options: RequestInit & { dispatcher: Agent } = {
        ...init,
        signal,
        dispatcher,
      };
      const response = await fetcher(url, options);
      if ([502, 503, 504].includes(response.status) && again()) {
        await response.body?.cancel().catch(() => {});
        continue;
      }
      return response;
    } catch (error) {
      if (init.signal?.aborted) throw error;
      const name =
        error && typeof error === "object" && "name" in error
          ? error.name
          : null;
      if ((name === "TimeoutError" || name === "TypeError") && again())
        continue;
      const codes = transportCodes(error);
      console.warn("rhythm-engine-transport", {
        call,
        category:
          name === "TimeoutError"
            ? "timeout"
            : codes.length
              ? kinds[codes[0]]
              : redirected(error)
                ? "redirect"
                : name === "TypeError"
                  ? "network"
                  : "transport",
        attempts: attempt + 1,
        elapsedMs: Date.now() - started,
        codes,
      });
      throw error;
    }
  }
}
