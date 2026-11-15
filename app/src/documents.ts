// Documents a person adds in the app: what the engine's documents adapter takes
// (contracts/README.md 2.2, app_upload_document), shared by the browser and the server.
export const maxUploadBytes = 5 * 1024 * 1024;
export const uploadTypes = [".pdf", ".txt", ".md", ".markdown"];
// What the file picker offers.
export const uploadAccept = [
  ...uploadTypes,
  "application/pdf",
  "text/plain",
  "text/markdown",
].join(",");

export type UploadedDocument = {
  graphId: string;
  title: string;
  status: "created" | "updated" | "unchanged";
  format: "pdf" | "text" | "markdown";
  pages: number;
  characters: number;
  // Only while viewer sessions are on (app.viewers): where the engine keeps it, a person's own
  // (private) or Family's (shared).
  scope?: "private" | "shared";
};
// Added (the star it becomes, once the map is rebuilt), or not, with a reason to show.
export type UploadAnswer =
  { ok: true; document: UploadedDocument } | { ok: false; reason: string };

const object = (v: unknown): v is Record<string, unknown> =>
  !!v && typeof v === "object" && !Array.isArray(v);

// The facade's answer as the browser receives it; anything else is "not added".
export function cleanUploadAnswer(v: unknown): UploadAnswer {
  const failed: UploadAnswer = {
    ok: false,
    reason: "The document could not be added right now. Try again later.",
  };
  if (!object(v)) return failed;
  if (v.ok === false)
    return typeof v.reason === "string" && v.reason && v.reason.length <= 300
      ? { ok: false, reason: v.reason }
      : failed;
  const d = v.document;
  return v.ok === true &&
    object(d) &&
    typeof d.graphId === "string" &&
    d.graphId.length > 0 &&
    d.graphId.length <= 1000 &&
    typeof d.title === "string" &&
    d.title.length > 0 &&
    d.title.length <= 300 &&
    ["created", "updated", "unchanged"].includes(String(d.status)) &&
    ["pdf", "text", "markdown"].includes(String(d.format)) &&
    Number.isInteger(d.pages) &&
    Number.isInteger(d.characters)
    ? {
        ok: true,
        document: {
          graphId: d.graphId,
          title: d.title,
          status: d.status as UploadedDocument["status"],
          format: d.format as UploadedDocument["format"],
          pages: d.pages as number,
          characters: d.characters as number,
          ...(d.scope === "private" || d.scope === "shared"
            ? { scope: d.scope }
            : {}),
        },
      }
    : failed;
}

export const uploadable = (name: string) =>
  uploadTypes.some((t) => name.toLowerCase().endsWith(t));

// A document's original file, kept in the engine's archive when someone added it as a file
// (contracts/README.md 1.5): the resource id /api/document-file takes, its format, its size and
// a name safe to save it under. app_graph's content answer names it in resource.original.
export type DocumentFormat = "pdf" | "text" | "markdown";
export type DocumentOriginal = {
  id: string;
  format: DocumentFormat;
  bytes: number;
  name: string;
};
export const originalTypes: Record<DocumentFormat, string> = {
  pdf: "application/pdf",
  text: "text/plain; charset=utf-8",
  markdown: "text/markdown; charset=utf-8",
};
const extensions: Record<DocumentFormat, string> = {
  pdf: ".pdf",
  text: ".txt",
  markdown: ".md",
};
export const documentId = (v: unknown): v is string =>
  typeof v === "string" &&
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(v);
export const documentFormat = (v: unknown): v is DocumentFormat =>
  v === "pdf" || v === "text" || v === "markdown";

// One plain file name of the format, as the engine makes it: no folders, quotes or control
// characters, at most 200 characters, ending in the format's extension.
export function fileName(name: string, format: DocumentFormat) {
  const extension = extensions[format];
  let clean = (name.split(/[/\\]/).pop() ?? "")
    .replace(/[\p{Cc}"/\\:*?<>|]/gu, "_")
    .trim()
    .replace(/^\.+/, "");
  if (!clean) clean = `document${extension}`;
  const lower = clean.toLowerCase();
  if (
    !lower.endsWith(extension) &&
    !(format === "markdown" && lower.endsWith(".markdown"))
  )
    clean += extension;
  return clean.length <= 200
    ? clean
    : `${clean.slice(0, 196 - extension.length)}…${extension}`;
}

// What the engine said about a document's original, or null when it has none the app can show.
export function cleanOriginal(v: unknown): DocumentOriginal | null {
  if (
    !object(v) ||
    !documentId(v.id) ||
    !documentFormat(v.format) ||
    !Number.isSafeInteger(v.bytes) ||
    (v.bytes as number) < 1 ||
    typeof v.name !== "string" ||
    !v.name ||
    v.name.length > 200
  )
    return null;
  return {
    id: v.id,
    format: v.format,
    bytes: v.bytes as number,
    name: fileName(v.name, v.format),
  };
}

// Where the browser reads an original: shown in the reader, or saved with Download.
export const originalPath = (id: string, download = false) =>
  `/api/document-file?id=${encodeURIComponent(id)}${download ? "&download=1" : ""}`;

// A file's bytes as base64, a slice at a time so a large file never overflows the call stack.
export async function fileBase64(file: Blob): Promise<string> {
  const bytes = new Uint8Array(await file.arrayBuffer());
  let binary = "";
  for (let i = 0; i < bytes.length; i += 0x8000)
    binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(binary);
}
