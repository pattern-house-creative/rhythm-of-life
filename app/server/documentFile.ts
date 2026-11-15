import type { DocumentFormat } from "../src/documents.js";
import { documentId, fileName, originalTypes } from "../src/documents.js";
import { json, requestGuard } from "./http.js";
import {
  answerRejected,
  documentFile,
  EngineError,
  engineMode,
} from "./engine.js";

// The largest original the engine keeps: a file dropped in the documents folder, up to 20 MiB.
export const maxFileBytes = 20 * 1024 * 1024;
// A hosted front end passes on less: Vercel Functions answer at most 4.5 MB (deploy/vercel/).
export const hostedFileBytes = 4 * 1024 * 1024;

const formats: Record<string, DocumentFormat> = {
  "application/pdf": "pdf",
  "text/plain": "text",
  "text/markdown": "markdown",
};

// GET /api/document-file?id=<resource id>[&download=1]: one document's original file, as the
// engine archived it when someone added it (contracts/README.md 1.5), so the reader shows a PDF's
// pages rather than its text. Streamed from the engine and never kept or logged here. The type,
// name and headers are this server's own: only the three document types pass, under a plain
// name, never sniffed, sandboxed if opened directly. A file over `limit` (a hosted front end's
// cap) answers 413 with that limit, so the reader can say so; demo mode keeps no originals.
export async function handleDocumentFile(
  request: Request,
  fetcher: typeof fetch = fetch,
  limit = maxFileBytes,
) {
  const rejected = requestGuard(request, "GET");
  if (rejected) return rejected;
  const params = new URL(request.url).searchParams;
  const id = params.get("id");
  const download = params.get("download");
  if (
    !documentId(id) ||
    [...params.keys()].some((k) => k !== "id" && k !== "download") ||
    params.getAll("id").length !== 1 ||
    (download !== null && download !== "1")
  )
    return json({ error: "Invalid request" }, 400);
  if (engineMode() !== "live") return json({ error: "not_found" }, 404);
  let response: Response;
  try {
    response = await documentFile(id, request.signal, fetcher);
  } catch (error) {
    return error instanceof EngineError && error.reason === "not_found"
      ? json({ error: "not_found" }, 404)
      : json({ error: "unavailable" }, 502);
  }
  const type = (response.headers.get("content-type") ?? "")
    .split(";")[0]
    .trim()
    .toLowerCase();
  const format = Object.hasOwn(formats, type) ? formats[type] : undefined;
  const length = Number(response.headers.get("content-length") ?? NaN);
  if (
    !format ||
    !Number.isSafeInteger(length) ||
    length < 1 ||
    !response.body
  ) {
    await response.body?.cancel().catch(() => {});
    answerRejected(
      "document-file",
      new Error("No document type or length"),
      request.signal,
    );
    return json({ error: "unavailable" }, 502);
  }
  if (length > limit) {
    await response.body.cancel().catch(() => {});
    return json({ error: "too_large", maxBytes: limit }, 413);
  }
  const name = fileName(
    dispositionName(response.headers.get("content-disposition")),
    format,
  );
  return new Response(exactly(response.body, length), {
    status: 200,
    headers: {
      "Content-Type": originalTypes[format],
      "Content-Length": String(length),
      "Content-Disposition": disposition(
        download ? "attachment" : "inline",
        name,
      ),
      "Cache-Control": "private, no-store, max-age=0",
      "CDN-Cache-Control": "no-store",
      "Vercel-CDN-Cache-Control": "no-store",
      "X-Content-Type-Options": "nosniff",
      "Content-Security-Policy": "default-src 'none'; sandbox",
    },
  });
}

// The name the engine gave the file (RFC 6266: filename*, else filename), or "" for none.
export function dispositionName(header: string | null) {
  if (!header) return "";
  const extended = /filename\*\s*=\s*UTF-8''([^;\s]+)/i.exec(header);
  if (extended)
    try {
      return decodeURIComponent(extended[1]);
    } catch {
      return "";
    }
  return /filename\s*=\s*"([^"]*)"/i.exec(header)?.[1] ?? "";
}

// A Content-Disposition with a plain ASCII name for old clients and the exact one, encoded.
export function disposition(kind: "inline" | "attachment", name: string) {
  const ascii = name.replace(/[^\x20-\x7e]|["\\%]/g, "_");
  const encoded = encodeURIComponent(name).replace(
    /['()*]/g,
    (c) => `%${c.charCodeAt(0).toString(16).toUpperCase()}`,
  );
  return `${kind}; filename="${ascii}"; filename*=UTF-8''${encoded}`;
}

// The engine's bytes as they come, refusing to pass on more than it announced.
function exactly(body: ReadableStream<Uint8Array>, length: number) {
  let seen = 0;
  return body.pipeThrough(
    new TransformStream<Uint8Array, Uint8Array>({
      transform(chunk, controller) {
        seen += chunk.length;
        if (seen > length) controller.error(new Error("More than announced"));
        else controller.enqueue(chunk);
      },
    }),
  );
}
