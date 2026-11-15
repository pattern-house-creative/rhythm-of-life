import { render, screen, waitFor } from "@testing-library/react";
import { expect, it, vi } from "vitest";

// pdf.js itself needs a canvas and a worker, which jsdom does not have (its drawing is checked
// in the browser). This stand-in opens a fictional file whose first page can't be read.
const pdf = vi.hoisted(() => ({
  getDocument: vi.fn(),
}));
vi.mock("pdfjs-dist/legacy/build/pdf.mjs", () => ({
  getDocument: pdf.getDocument,
  GlobalWorkerOptions: {},
  TextLayer: class {},
  VerbosityLevel: { ERRORS: 0 },
}));
import PdfView from "../src/PdfView";

const opened = (doc: unknown) => ({
  promise: Promise.resolve(doc),
  destroy: vi.fn(async () => {}),
});

it.each([
  [
    "with no readable first page",
    () => Promise.reject(new Error("Invalid page request.")),
  ],
  [
    "whose first page has no size",
    async () => ({
      getViewport: () => {
        throw new Error("Invalid page box.");
      },
    }),
  ],
])(
  "falls back to the saved text for a PDF that opens %s",
  async (_, getPage) => {
    pdf.getDocument.mockReturnValue(opened({ numPages: 1, getPage }));
    const onFailed = vi.fn();
    render(
      <PdfView
        data={new TextEncoder().encode("%PDF-1.4 fictional")}
        title="Fictional boiler manual"
        onFailed={onFailed}
      />,
    );
    expect(screen.getByText("Opening this PDF…")).toBeInTheDocument();
    await waitFor(() => expect(onFailed).toHaveBeenCalledOnce());
  },
);

it("falls back to the saved text for a file that doesn't open as a PDF", async () => {
  pdf.getDocument.mockReturnValue({
    promise: Promise.reject(new Error("Invalid PDF structure.")),
    destroy: vi.fn(async () => {}),
  });
  const onFailed = vi.fn();
  render(
    <PdfView
      data={new TextEncoder().encode("not a PDF")}
      title="Fictional note"
      onFailed={onFailed}
    />,
  );
  await waitFor(() => expect(onFailed).toHaveBeenCalledOnce());
});
