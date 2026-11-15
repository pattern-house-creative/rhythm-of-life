import { object } from "../src/validation.js";
import type { UploadAnswer } from "../src/documents.js";
import {
  cleanUploadAnswer,
  maxUploadBytes,
  uploadable,
  uploadTypes,
} from "../src/documents.js";
import { json, readJson, requestGuard } from "./http.js";
import {
  answerRejected,
  callTool,
  EngineError,
  engineMode,
  offered,
} from "./engine.js";
import { inViewerSession } from "./viewerScope.js";

// The largest request this route takes: the file as base64 plus its name.
export const maxUploadBody = Math.ceil(maxUploadBytes / 3) * 4 + 4096;
const base64 = /^[A-Za-z0-9+/]*={0,2}$/;

// Documents a person adds in the app (contracts/README.md 2.2, app_upload_document): GET says
// whether uploads are on and what they take; POST {name, content (base64)} hands one file to the
// engine, which archives it, reads its text and saves it as a document star. Demo mode, an
// engine without the documents adapter, or any upstream failure answer "not added", with a
// reason to show. Nothing about the file is logged or kept here. Inside a viewer session (only
// while app.viewers is on, viewer.ts) the body may also say `family` (true or false): the engine
// keeps the file in that person's own scope unless it is true (SB-P1-20) and answers the scope it
// is stored in. Anywhere else the body is exactly {name, content}, as before.
export async function handleDocuments(
  request: Request,
  fetcher: typeof fetch = fetch,
) {
  if (request.method === "GET") {
    const rejected = requestGuard(request, "GET");
    if (rejected) return rejected;
    const available =
      engineMode() === "live" &&
      (await offered("app_upload_document", request.signal, fetcher).catch(
        () => false,
      ));
    return json({ available, maxBytes: maxUploadBytes, types: uploadTypes });
  }
  const rejected = requestGuard(request, "POST");
  if (rejected) return rejected;
  let name: string, content: string;
  let family: boolean | undefined;
  try {
    const body = await readJson(request, maxUploadBody);
    const chooses = inViewerSession() && object(body) && "family" in body;
    if (
      !object(body) ||
      Object.keys(body).length !== (chooses ? 3 : 2) ||
      typeof body.name !== "string" ||
      typeof body.content !== "string" ||
      (chooses && typeof body.family !== "boolean")
    )
      throw new Error();
    name = body.name.trim();
    content = body.content;
    if (chooses) family = body.family as boolean;
    if (
      !name ||
      name.length > 200 ||
      /[/\\\p{Cc}]/u.test(name) ||
      !uploadable(name) ||
      content.length < 4 ||
      content.length % 4 !== 0 ||
      !base64.test(content)
    )
      throw new Error();
  } catch {
    return json({ error: "Invalid or oversized request" }, 400);
  }
  if (engineMode() !== "live")
    return answer({
      ok: false,
      reason: "Adding documents needs the Rhythm engine; this is the demo.",
    });
  try {
    const result = await callTool(
      "app_upload_document",
      family === undefined ? { name, content } : { name, content, family },
      request.signal,
      fetcher,
    );
    return answer(cleanUploadAnswer(result));
  } catch (error) {
    answerRejected("app_upload_document", error, request.signal);
    return answer({
      ok: false,
      reason:
        error instanceof EngineError && error.reason === "not_connected"
          ? "Adding documents is off in this install (adapters.documents)."
          : error instanceof EngineError && error.reason === "too_large"
            ? `Files up to ${maxUploadBytes / 1024 / 1024} MiB can be added.`
            : "The document could not be added right now. Try again later.",
    });
  }
}

const answer = (value: UploadAnswer) => json(value);
