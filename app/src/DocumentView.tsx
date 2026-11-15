import { lazy, Suspense, useEffect, useMemo, useState } from "react";
import { Markdown } from "./markdown";
import type { DocumentOriginal } from "./documents";
import type { OriginalFile } from "./liveGraph";

// pdf.js loads only when a PDF is opened, never with the home screen.
const PdfView = lazy(() => import("./PdfView"));
// A text or Markdown file longer than this shows its saved text (its first 200,000 characters).
const longestText = 2 * 1024 * 1024;

type Shown =
  | { id: string; state: "loading" }
  | { id: string; state: "ready"; bytes: Uint8Array }
  | { id: string; state: "saved"; why: string };

const unreadable =
  "The original file can't be read right now, so its saved text is shown. Try again later.";
const mib = (bytes: number) =>
  `${Math.round((bytes / 1024 / 1024) * 10) / 10} MiB`;
// A Markdown file's YAML front matter (from a first line "---" to the next "---" or "...") holds its
// settings, not its text: left out, as the notes reader does.
const body = (text: string) =>
  text.replace(
    /^---\r?\n(?:[\s\S]*?\r?\n)?(?:---|\.\.\.)[ \t]*(?:\r?\n|$)/,
    "",
  );
const why = (file: Exclude<OriginalFile, { ok: true }>) =>
  file.reason === "too_large"
    ? `This file is larger than this front end passes on${file.maxBytes ? ` (${mib(file.maxBytes)})` : ""}, so its saved text is shown. Open Rhythm at its own address to see the file.`
    : file.reason === "missing"
      ? "The original file is no longer in the archive, so its saved text is shown."
      : unreadable;

// A document someone added as a file, shown as the file itself: a PDF's pages, a Markdown file
// rendered (the same safe renderer as notes), a text file as text. The file is read from the
// engine's archive when the reader opens and never kept. When it can't be had (gone, too large
// for a hosted front end, the engine not answering) or a PDF can't be drawn, the reader shows the
// document's saved text, as it did before, with one line saying why.
export default function DocumentView({
  original,
  text,
  read,
  title,
}: {
  original: DocumentOriginal;
  // The document's saved text, read with it: what shows when the file can't.
  text: string;
  read: (id: string, signal?: AbortSignal) => Promise<OriginalFile>;
  title: string;
}) {
  const [shown, setShown] = useState<Shown | null>(null);
  const { id, format, bytes } = original;
  const long = format !== "pdf" && bytes > longestText;
  useEffect(() => {
    if (long) return;
    const controller = new AbortController();
    const show = (next: Shown) => {
      if (!controller.signal.aborted) setShown(next);
    };
    read(id, controller.signal).then(
      (file) =>
        show(
          file.ok
            ? { id, state: "ready", bytes: file.bytes }
            : { id, state: "saved", why: why(file) },
        ),
      () => show({ id, state: "saved", why: unreadable }),
    );
    return () => controller.abort();
  }, [id, long, read]);
  const current: Shown = long
    ? {
        id,
        state: "saved",
        why: "This file is long, so its saved text is shown here. Download it for the whole file.",
      }
    : shown?.id === id
      ? shown
      : { id, state: "loading" };
  const decoded = useMemo(
    () =>
      shown?.state === "ready" && format !== "pdf"
        ? new TextDecoder().decode(shown.bytes)
        : "",
    [shown, format],
  );

  if (current.state === "loading")
    return (
      <div className="reader-text">
        <p className="source-content" aria-busy="true">
          Opening the original file…
        </p>
      </div>
    );
  if (current.state === "saved")
    return (
      <SavedText
        why={current.why}
        text={text}
        markdown={format === "markdown"}
      />
    );
  if (format === "pdf")
    return (
      <Suspense
        fallback={
          <div className="reader-text">
            <p className="source-content" aria-busy="true">
              Opening this PDF…
            </p>
          </div>
        }
      >
        <PdfView
          data={current.bytes}
          title={title}
          onFailed={() =>
            setShown({
              id,
              state: "saved",
              why: "This PDF can't be shown here, so its saved text is shown. Download it to open it elsewhere.",
            })
          }
        />
      </Suspense>
    );
  return (
    <div className="reader-text">
      {format === "markdown" ? (
        <Markdown className="source-content" text={body(decoded)} />
      ) : (
        <p className="source-content">{decoded}</p>
      )}
    </div>
  );
}

// The document's saved text, as the reader showed it before it showed files, and why.
function SavedText({
  why,
  text,
  markdown,
}: {
  why: string;
  text: string;
  markdown: boolean;
}) {
  return (
    <div className="reader-text">
      <p className="reader-note" role="status">
        {why}
      </p>
      {markdown ? (
        <Markdown className="source-content" text={body(text)} />
      ) : (
        <p className="source-content">{text}</p>
      )}
    </div>
  );
}
