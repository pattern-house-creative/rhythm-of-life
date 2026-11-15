import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import Explorer from "../src/Explorer";
import { emptyGraph } from "../src/liveGraph";
import type { GraphSession } from "../src/liveGraph";
import { record } from "./synthetic";
import type { KnowledgeController } from "../src/useKnowledge";
import type { GraphNode, GraphPage } from "../src/knowledge";
import { fileBase64, maxUploadBytes } from "../src/documents";

const controller = (
  page: GraphPage,
  extra: Partial<GraphSession> = {},
): KnowledgeController => ({
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
    ...extra,
  },
});
// A star from the engine: a note from the notes folder, or a saved document.
function live(node: GraphNode, type: string, title: string) {
  node.type = type;
  node.title = title;
  node.provenance.demo = false;
  node.resource.content = null;
  return node;
}
const props = {
  selected: null,
  onSelect: vi.fn(),
  close: vi.fn(),
  paused: true,
  state: "ready" as const,
};

it("reads a note from the notes folder when it opens", async () => {
  const note = live(
    record("Weekly review.md", "notes", "notes"),
    "note",
    "Weekly review",
  );
  const page = { ...emptyGraph(), nodes: [note] };
  const text = vi.fn(async () => ({
    text: "A fictional Sunday review.",
    modifiedAt: "2026-10-28T09:00:00Z",
  }));
  render(
    <Explorer
      {...props}
      selected={note.id}
      knowledge={controller(page, { text })}
    />,
  );
  await userEvent
    .setup()
    .click(screen.getByRole("button", { name: /Read note/ }));
  const reader = screen.getByRole("region", { name: "Source reader" });
  expect(
    await within(reader).findByText("A fictional Sunday review."),
  ).toBeInTheDocument();
  expect(
    within(reader).getByText(
      /Read from the notes folder just now · last edited 2026-10-28/,
    ),
  ).toBeInTheDocument();
  expect(text).toHaveBeenCalledWith(note.id, expect.anything());
});

it("adds a document, says so, and opens it once the map holds its star", async () => {
  const note = live(record("Welcome.md", "notes", "notes"), "note", "Welcome");
  const added = live(
    record("household:upload:abc", "rhythm", "documents"),
    "document",
    "Allotment rota",
  );
  const before = { ...emptyGraph(), nodes: [note] };
  const upload = vi.fn(async () => ({
    ok: true as const,
    document: {
      graphId: added.id,
      title: "Allotment rota",
      status: "created" as const,
      format: "text" as const,
      pages: 1,
      characters: 30,
    },
  }));
  const session = {
    uploads: vi.fn(async () => ({ available: true, maxBytes: maxUploadBytes })),
    upload,
    text: vi.fn(async () => ({
      text: "DEMO watering rota.",
      modifiedAt: null,
    })),
  };
  const onRebuild = vi.fn();
  const { rerender } = render(
    <Explorer
      {...props}
      knowledge={controller(before, session)}
      onRebuild={onRebuild}
    />,
  );
  const button = await screen.findByRole("button", { name: "Add a document" });
  const input =
    button.parentElement!.querySelector<HTMLInputElement>(
      'input[type="file"]',
    )!;
  expect(input.accept).toContain(".pdf");
  // Wrong kinds and sizes are refused here, before anything is sent.
  fireEvent.change(input, {
    target: { files: [new File(["x"], "rota.docx")] },
  });
  expect(
    await screen.findByText("Only PDF, .txt and .md files can be added."),
  ).toBeInTheDocument();
  expect(upload).not.toHaveBeenCalled();
  const file = new File(["DEMO watering rota."], "Allotment rota.txt", {
    type: "text/plain",
  });
  fireEvent.change(input, { target: { files: [file] } });
  expect(
    await screen.findByText(/^Added Allotment rota\./),
  ).toBeInTheDocument();
  expect(upload).toHaveBeenCalledWith(file);
  // The view does not drift back to the sky while the document is on its way.
  expect(
    screen.getByText(/^Added Allotment rota\./).closest("[data-keep-open]"),
  ).not.toBeNull();
  expect(onRebuild).toHaveBeenCalledOnce();
  rerender(
    <Explorer
      {...props}
      knowledge={controller({ ...before, nodes: [note, added] }, session)}
      onRebuild={onRebuild}
    />,
  );
  const reader = await screen.findByRole("region", { name: "Source reader" });
  expect(
    within(reader).getByRole("heading", { name: "Allotment rota" }),
  ).toBeInTheDocument();
  expect(
    await within(reader).findByText("DEMO watering rota."),
  ).toBeInTheDocument();
  expect(screen.getByText("Allotment rota is on the map.")).toBeInTheDocument();
});

it("shows no upload control when the engine does not take documents", async () => {
  const page = {
    ...emptyGraph(),
    nodes: [live(record("Welcome.md", "notes", "notes"), "note", "Welcome")],
  };
  const uploads = vi.fn(async () => ({ available: false, maxBytes: 0 }));
  render(
    <Explorer
      {...props}
      knowledge={controller(page, { uploads, upload: vi.fn() })}
    />,
  );
  await waitFor(() => expect(uploads).toHaveBeenCalled());
  expect(
    screen.queryByRole("button", { name: "Add a document" }),
  ).not.toBeInTheDocument();
});

it("encodes a file's bytes as base64", async () => {
  const bytes = new Uint8Array(70_000).map((_, i) => i % 251);
  const encoded = await fileBase64(new Blob([bytes]));
  expect(Uint8Array.from(atob(encoded), (c) => c.charCodeAt(0))).toEqual(bytes);
});
