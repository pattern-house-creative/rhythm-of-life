import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import Explorer from "../src/Explorer";
import { emptyGraph } from "../src/liveGraph";
import type { OriginalFile } from "../src/liveGraph";
import type { DocumentOriginal } from "../src/documents";
import type { KnowledgeController } from "../src/useKnowledge";
import type { GraphPage } from "../src/knowledge";
import { record, relation } from "./synthetic";

// pdf.js draws on a canvas that jsdom does not have; its part is checked in the browser. Here a
// stand-in says what it was given, and can fail as an unreadable PDF does.
vi.mock("../src/PdfView", () => ({
  default: ({ data, onFailed }: { data: Uint8Array; onFailed: () => void }) => (
    <div className="reader-text">
      <p>Fictional PDF pages from {data.length} bytes</p>
      <button onClick={onFailed}>Fail this PDF</button>
    </div>
  ),
}));

const id = "3f2b8c1e-4d5a-4b6c-9e7f-0a1b2c3d4e5f";
const bytes = (text: string) => new TextEncoder().encode(text);

// A document star someone added as a file, with the session that reads it.
function setup(
  original: Partial<DocumentOriginal> | null,
  file: OriginalFile | (() => Promise<OriginalFile>),
  saved = "Fictional saved text of the document.",
) {
  const star = record("household:upload:fictional", "rhythm", "documents");
  star.type = "document";
  star.title = "Fictional boiler manual";
  star.provenance.demo = false;
  star.resource.content = null;
  star.resource.owner = "household";
  const note = record("fictional-note");
  note.provenance.demo = false;
  const page: GraphPage = {
    ...emptyGraph(),
    nodes: [star, note],
    edges: [relation(note, star)],
  };
  const text = vi.fn().mockResolvedValue({
    text: saved,
    modifiedAt: "2026-10-27T09:30:00Z",
    ...(original
      ? {
          original: {
            id,
            format: "pdf",
            bytes: 41,
            name: "Fictional boiler manual.pdf",
            ...original,
          },
        }
      : {}),
  });
  const read = vi.fn(typeof file === "function" ? file : async () => file);
  const knowledge: KnowledgeController = {
    page,
    accept: vi.fn(),
    pins: [],
    togglePin: vi.fn(),
    busy: false,
    error: null,
    refresh: vi.fn(async () => {}),
    discoverMore: vi.fn(async () => {}),
    lastRefresh: null,
    epoch: 0,
    session: {
      initial: page,
      refresh: vi.fn(async () => page),
      retrieve: vi.fn(async () => ({ ...page, cursors: {} })),
      text,
      original: read,
    },
  };
  render(
    <Explorer
      knowledge={knowledge}
      selected={star.id}
      onSelect={vi.fn()}
      close={vi.fn()}
      paused
      state="ready"
    />,
  );
  return { read, text, star };
}
const openIt = async () => {
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: /Open document/ }));
  return user;
};

it("opens a PDF as its pages, with a way to download the file", async () => {
  const { read } = setup({}, { ok: true, bytes: bytes("%PDF-1.4 fictional") });
  await openIt();
  expect(
    await screen.findByText("Fictional PDF pages from 18 bytes"),
  ).toBeInTheDocument();
  expect(read).toHaveBeenCalledWith(id, expect.any(AbortSignal));
  const download = screen.getByRole("link", { name: /Download/ });
  expect(download).toHaveAttribute(
    "href",
    `/api/document-file?id=${id}&download=1`,
  );
  expect(download).toHaveAttribute("download", "Fictional boiler manual.pdf");
  expect(
    screen.getByText(
      /The file as it was added, from the Rhythm engine's archive · last changed 2026-10-27/,
    ),
  ).toBeInTheDocument();
  expect(screen.queryByText(/Fictional saved text/)).not.toBeInTheDocument();
});

it("renders a Markdown file with the safe renderer, without its front matter", async () => {
  setup(
    { format: "markdown", name: "House rules.md" },
    {
      ok: true,
      bytes: bytes(
        "---\ntitle: DEMO Rules\ntags: [home]\n---\n# DEMO House rules\n\nShoes **off** indoors.\n\n---\n\n<script>x</script>",
      ),
    },
  );
  await openIt();
  expect(
    await screen.findByRole("heading", { name: "DEMO House rules" }),
  ).toBeInTheDocument();
  expect(screen.getByText("off").tagName).toBe("STRONG");
  expect(screen.queryByText(/title: DEMO Rules/)).toBeNull();
  expect(document.querySelector(".reader-text hr")).not.toBeNull();
  // HTML stays text.
  expect(screen.getByText("<script>x</script>")).toBeInTheDocument();
  expect(document.querySelector(".reader-text script")).toBeNull();
});

it("shows a text file as it is", async () => {
  setup(
    { format: "text", name: "Allotment rota.txt" },
    { ok: true, bytes: bytes("DEMO rota\nAlex: Mondays\n# not a heading") },
  );
  await openIt();
  expect(
    await screen.findByText(/DEMO rota\s+Alex: Mondays\s+# not a heading/),
  ).toBeInTheDocument();
  expect(screen.queryByRole("heading", { name: "not a heading" })).toBeNull();
});

it.each([
  [
    { ok: false, reason: "too_large", maxBytes: 4 * 1024 * 1024 } as const,
    /^Showing saved text\. The original is over the file limit here \(4 MiB\)\. To try the original, open Rhythm at your household address\.$/,
  ],
  [
    { ok: false, reason: "missing" } as const,
    /^Showing saved text\. The original file is no longer in the archive\.$/,
  ],
  [
    { ok: false, reason: "unavailable" } as const,
    /^Showing saved text\. The original file couldn’t be opened\. Try again later\.$/,
  ],
])("falls back to the saved text and says why: %o", async (file, why) => {
  setup({}, file);
  await openIt();
  expect(await screen.findByText(why)).toBeInTheDocument();
  expect(
    screen.getByText("Fictional saved text of the document."),
  ).toBeInTheDocument();
  // The file may still download where the front end allows it.
  expect(screen.getByRole("link", { name: /Download/ })).toBeInTheDocument();
});

it("shows the saved text when a PDF can't be drawn, or a text file is very long", async () => {
  setup({}, { ok: true, bytes: bytes("not really a pdf") });
  const user = await openIt();
  await user.click(
    await screen.findByRole("button", { name: "Fail this PDF" }),
  );
  expect(
    screen.getByText(
      "Showing saved text. This PDF can’t be shown here. Download it to open it elsewhere.",
    ),
  ).toBeInTheDocument();
  expect(
    screen.getByText("Fictional saved text of the document."),
  ).toBeInTheDocument();
});

it("doesn't fetch a very long text file, and shows its saved text", async () => {
  const { read } = setup(
    { format: "text", bytes: 3 * 1024 * 1024, name: "Long log.txt" },
    { ok: true, bytes: bytes("never read") },
  );
  await openIt();
  expect(
    await screen.findByText(
      "Showing saved text. This file is too long to open here. Download it for the whole file.",
    ),
  ).toBeInTheDocument();
  expect(read).not.toHaveBeenCalled();
});

it("keeps today's text view for a document with no original", async () => {
  const { read } = setup(null, { ok: true, bytes: bytes("never read") });
  await openIt();
  expect(
    await screen.findByText("Fictional saved text of the document."),
  ).toBeInTheDocument();
  expect(
    screen.getByText(/Read from the Rhythm engine just now/),
  ).toBeInTheDocument();
  expect(screen.queryByRole("link", { name: /Download/ })).toBeNull();
  expect(read).not.toHaveBeenCalled();
});
