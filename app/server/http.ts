export function json(
  value: unknown,
  status = 200,
  extra: Record<string, string> = {},
) {
  return new Response(JSON.stringify(value), {
    status,
    headers: {
      "Content-Type": "application/json; charset=utf-8",
      "Cache-Control": "private, no-store, max-age=0",
      "CDN-Cache-Control": "no-store",
      "Vercel-CDN-Cache-Control": "no-store",
      "X-Content-Type-Options": "nosniff",
      ...extra,
    },
  });
}

// There is no login in the app: who reaches it is decided by where it listens and by
// what stands in front of it (an access proxy, a VPN, a hosted platform's login). This
// is request hygiene, NOT authentication. Never trust a cookie/header as a viewer ID.
export function requestGuard(
  request: Request,
  method: string,
): Response | null {
  if (request.method !== method)
    return json({ error: "Method not allowed" }, 405, { Allow: method });
  const origin = request.headers.get("origin");
  if (
    (origin && origin !== new URL(request.url).origin) ||
    request.headers.get("sec-fetch-site") === "cross-site"
  )
    return json({ error: "Cross-origin request rejected" }, 403);
  return null;
}

export async function readJson(
  request: Request,
  limit = 131072,
): Promise<unknown> {
  if (!request.headers.get("content-type")?.startsWith("application/json"))
    throw new Error("JSON required");
  const reader = request.body?.getReader();
  if (!reader) throw new Error("Body required");
  const chunks: Uint8Array[] = [];
  let size = 0;
  for (;;) {
    const { value, done } = await reader.read();
    if (done) break;
    size += value.length;
    if (size > limit) {
      await reader.cancel();
      throw new Error("Request too large");
    }
    chunks.push(value);
  }
  const bytes = new Uint8Array(size);
  let offset = 0;
  for (const chunk of chunks) {
    bytes.set(chunk, offset);
    offset += chunk.length;
  }
  return JSON.parse(new TextDecoder().decode(bytes));
}

// Web adapters can expose an empty POST as a non-null stream. Inspect its bytes,
// not its presence. Reject payloads immediately and bound even empty chunks.
export async function emptyBody(request: Request): Promise<boolean> {
  const reader = request.body?.getReader();
  if (!reader) return true;
  try {
    for (let chunks = 0; chunks < 4; chunks++) {
      const { value, done } = await reader.read();
      if (done) return true;
      if (value.length) return false;
    }
    return false;
  } finally {
    await reader.cancel();
  }
}
