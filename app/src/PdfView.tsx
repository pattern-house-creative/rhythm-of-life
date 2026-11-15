import { useEffect, useRef, useState } from "react";
import {
  getDocument,
  GlobalWorkerOptions,
  TextLayer,
  VerbosityLevel,
} from "pdfjs-dist/legacy/build/pdf.mjs";
import type {
  PDFDocumentProxy,
  RenderTask,
} from "pdfjs-dist/legacy/build/pdf.mjs";
import worker from "pdfjs-dist/legacy/build/pdf.worker.min.mjs?url";
import jbig2 from "pdfjs-dist/wasm/jbig2.wasm?url";
import openjpeg from "pdfjs-dist/wasm/openjpeg.wasm?url";
import qcms from "pdfjs-dist/wasm/qcms_bg.wasm?url";
import "./PdfView.css";

// A PDF's real pages, drawn by Mozilla's pdf.js (Apache-2.0, pinned in package.json) in its own
// worker, bundled here and loaded only when a PDF is opened. Each page fits the reader's width
// and is drawn when it scrolls near, then let go when it is far, so a long document stays light
// on a phone; its text layer stays, so the text can be selected and read by a screen reader. Only
// pdf.js's core is used: no viewer, no forms, no annotations' actions and no scripting, so nothing
// in a PDF ever runs.

GlobalWorkerOptions.workerSrc = worker;
// The image decoders scanned pages need (JBIG2 and CCITT fax, JPEG 2000, colour profiles), one
// folder that keeps their names (vite.config.ts); without it those pages draw blank.
const folders = [jbig2, openjpeg, qcms].map(
  (url) => new URL(".", new URL(url, document.baseURI)).href,
);
const wasmUrl = folders.every((f) => f === folders[0]) ? folders[0] : undefined;
// Pages never wider than this on a wall screen, and canvases never over this many pixels.
const widest = 1100,
  maxPixels = 16_000_000;

export default function PdfView({
  data,
  title,
  onFailed,
}: {
  data: Uint8Array;
  title: string;
  // The file could not be read as a PDF; the reader shows its saved text instead.
  onFailed: () => void;
}) {
  const scroller = useRef<HTMLDivElement>(null);
  const [pdf, setPdf] = useState<PDFDocumentProxy | null>(null);
  const [first, setFirst] = useState(1.294);
  const [width, setWidth] = useState(0);
  const failed = useRef(onFailed);
  useEffect(() => {
    failed.current = onFailed;
  });

  useEffect(() => {
    let open = true;
    // pdf.js takes over the bytes it is given, so it gets its own copy.
    const task = getDocument({
      data: new Uint8Array(data),
      wasmUrl,
      enableXfa: false,
      verbosity: VerbosityLevel.ERRORS,
    });
    // A file that opens but whose first page can't be read (or that has none) fails the
    // same way as one that doesn't open.
    task.promise
      .then(async (doc) => {
        const page = await doc.getPage(1);
        const box = page.getViewport({ scale: 1 });
        if (!open) return;
        setFirst(box.height / box.width);
        setPdf(doc);
      })
      .catch(() => open && failed.current());
    return () => {
      open = false;
      setPdf(null);
      void task.destroy();
    };
  }, [data]);

  useEffect(() => {
    const element = scroller.current;
    if (!element) return;
    const measure = () => {
      const style = getComputedStyle(element);
      const inner =
        element.clientWidth -
        parseFloat(style.paddingLeft || "0") -
        parseFloat(style.paddingRight || "0");
      setWidth(Math.max(0, Math.min(widest, Math.floor(inner))));
    };
    measure();
    if (typeof ResizeObserver === "undefined") return;
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  return (
    <div
      className="reader-text pdf-view"
      ref={scroller}
      aria-busy={!pdf}
      aria-label={pdf ? `${title}, ${pdf.numPages} pages` : title}
      role="document"
    >
      {!pdf ? (
        <p className="source-content">Opening this PDF…</p>
      ) : (
        Array.from({ length: pdf.numPages }, (_, i) => (
          <Page
            key={i}
            pdf={pdf}
            number={i + 1}
            count={pdf.numPages}
            width={width}
            ratio={first}
            root={scroller}
          />
        ))
      )}
    </div>
  );
}

function Page({
  pdf,
  number,
  count,
  width,
  ratio,
  root,
}: {
  pdf: PDFDocumentProxy;
  number: number;
  count: number;
  width: number;
  ratio: number;
  root: React.RefObject<HTMLDivElement | null>;
}) {
  const box = useRef<HTMLElement>(null),
    canvas = useRef<HTMLCanvasElement>(null),
    text = useRef<HTMLDivElement>(null);
  const [near, setNear] = useState(number <= 2);
  // The page's own size in PDF units, once it is read.
  const [size, setSize] = useState<{
    width: number;
    height: number;
    unit: number;
  } | null>(null);
  // The text layer: built once, then only laid out again when the width changes.
  const layer = useRef<{ text: TextLayer; done: boolean } | null>(null);

  useEffect(() => {
    const element = box.current;
    if (!element || typeof IntersectionObserver === "undefined") return;
    // Drawn a screen or so before it shows, let go a few screens after it leaves.
    const observer = new IntersectionObserver(
      ([entry]) => setNear(entry.isIntersecting),
      { root: root.current, rootMargin: "150% 0px" },
    );
    observer.observe(element);
    return () => observer.disconnect();
  }, [root]);

  useEffect(() => {
    if (!near || !width) return;
    let live = true,
      render: RenderTask | null = null;
    const drawn = canvas.current;
    pdf.getPage(number).then(
      (page) => {
        if (!live || !drawn || !text.current || !box.current) return;
        const base = page.getViewport({ scale: 1 });
        const viewport = page.getViewport({ scale: width / base.width });
        // The text layer sizes itself from these (pdf.js's own variables, as its viewer sets them).
        box.current.style.setProperty("--scale-factor", String(viewport.scale));
        box.current.style.setProperty("--user-unit", String(viewport.userUnit));
        setSize({
          width: base.width,
          height: base.height,
          unit: viewport.userUnit,
        });
        const output = Math.min(
          window.devicePixelRatio || 1,
          2,
          Math.sqrt(maxPixels / (viewport.width * viewport.height)),
        );
        drawn.width = Math.floor(viewport.width * output);
        drawn.height = Math.floor(viewport.height * output);
        render = page.render({
          canvas: drawn,
          viewport,
          transform: output === 1 ? undefined : [output, 0, 0, output, 0, 0],
        });
        render.promise.catch(() => {});
        if (!layer.current) {
          const built = {
            text: new TextLayer({
              textContentSource: page.streamTextContent({
                includeMarkedContent: true,
              }),
              container: text.current,
              viewport,
            }),
            done: false,
          };
          layer.current = built;
          built.text.render().then(
            () => (built.done = true),
            () => {
              if (layer.current !== built) return;
              layer.current = null;
              text.current?.replaceChildren();
            },
          );
        } else if (layer.current.done) layer.current.text.update({ viewport });
      },
      () => {},
    );
    return () => {
      live = false;
      render?.cancel();
      // A text layer still being built is started again next time; a built one stays.
      if (layer.current && !layer.current.done) layer.current.text.cancel();
      // A page far away gives its canvas back; it is drawn again when it comes near.
      if (drawn) drawn.width = drawn.height = 0;
    };
  }, [near, width, pdf, number]);

  return (
    <section
      className="pdf-page"
      ref={box}
      aria-label={`Page ${number} of ${count}`}
      style={
        {
          width: width || undefined,
          aspectRatio: size ? `${size.width} / ${size.height}` : `1 / ${ratio}`,
          ...(size && width
            ? {
                "--scale-factor": width / size.width,
                "--user-unit": size.unit,
              }
            : {}),
        } as React.CSSProperties
      }
    >
      <canvas ref={canvas} aria-hidden="true" />
      <div className="textLayer" ref={text} />
    </section>
  );
}
