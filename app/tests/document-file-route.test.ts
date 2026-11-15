import { createServer } from "node:http";
import type { AddressInfo } from "node:net";
import { gzipSync } from "node:zlib";
import { afterEach, expect, it, vi } from "vitest";
import {
  disposition,
  dispositionName,
  handleDocumentFile,
  hostedFileBytes,
  maxFileBytes,
} from "../server/documentFile";
import { GET } from "../api/document-file";
import { routes } from "../server/routes";
import { cleanOriginal, fileName, originalPath } from "../src/documents";
import { createApiSession } from "../src/apiClient";
import { appToken, engineUrl, withEngine } from "./engine";

// Node's own fetch, taken before the suite's per-test stub is installed.
const nativeFetch = globalThis.fetch;

afterEach(() => {
  vi.unstubAllEnvs();
});
const id = "3f2b8c1e-4d5a-4b6c-9e7f-0a1b2c3d4e5f";
const url = (query = `id=${id}`) =>
  `http://rhythm.example.invalid/api/document-file?${query}`;
const open = (query?: string, headers: Record<string, string> = {}) =>
  handleDocumentFile(new Request(url(query), { headers }));
// A fictional PDF's first bytes are enough: the facade never reads what it passes on.
const pdf = new TextEncoder().encode(
  "%PDF-1.4\nfictional boiler manual\n%%EOF\n",
);
const engineFile = (
  body: BodyInit = pdf,
  headers: Record<string, string> = {},
  status = 200,
) =>
  new Response(body, {
    status,
    headers: {
      "content-type": "application/pdf",
      "content-length": String(
        typeof body === "string" ? body.length : pdf.length,
      ),
      "content-disposition":
        "inline; filename=\"Fictional manual (copy).pdf\"; filename*=UTF-8''Fictional%20manual%20%28copy%29.pdf",
      ...headers,
    },
  });

it("is a route of the app's server", () => {
  expect(Object.keys(routes)).toContain("/api/document-file");
});

it("reads one document's original from the engine and streams it on", async () => {
  withEngine();
  vi.mocked(fetch).mockResolvedValueOnce(engineFile());
  const response = await open();
  expect(response.status).toBe(200);
  expect([...new Uint8Array(await response.arrayBuffer())]).toEqual([...pdf]);
  expect(Object.fromEntries(response.headers)).toMatchObject({
    "content-type": "application/pdf",
    "content-length": String(pdf.length),
    "content-disposition":
      "inline; filename=\"Fictional manual (copy).pdf\"; filename*=UTF-8''Fictional%20manual%20%28copy%29.pdf",
    "x-content-type-options": "nosniff",
    "content-security-policy": "default-src 'none'; sandbox",
    "cache-control": "private, no-store, max-age=0",
  });
  // One plain GET to the engine's file route with the app token, nothing else.
  const [called, init] = vi.mocked(fetch).mock.calls.at(-1)!;
  expect(called).toBe(`${engineUrl}/app/documents/${id}`);
  expect(init).toMatchObject({
    method: "GET",
    redirect: "error",
    credentials: "omit",
  });
  expect(init?.body).toBeUndefined();
  expect(init?.headers).toEqual({
    "Accept-Encoding": "identity",
    Authorization: `Bearer ${appToken}`,
  });
  // Download asks for an attachment.
  vi.mocked(fetch).mockResolvedValueOnce(engineFile());
  expect(
    (await open(`id=${id}&download=1`)).headers.get("content-disposition"),
  ).toMatch(/^attachment; filename="Fictional manual \(copy\)\.pdf"/);
});

it("passes on only document types, under a plain name of their type", async () => {
  withEngine();
  vi.mocked(fetch).mockResolvedValueOnce(
    engineFile("DEMO rota", {
      "content-type": "text/plain; charset=utf-8",
      "content-disposition":
        "inline; filename*=UTF-8''..%2F..%2FR%C3%B6ta%22x%22",
    }),
  );
  const text = await open();
  expect(text.headers.get("content-type")).toBe("text/plain; charset=utf-8");
  expect(text.headers.get("content-disposition")).toBe(
    "inline; filename=\"R_ta_x_.txt\"; filename*=UTF-8''R%C3%B6ta_x_.txt",
  );
  expect(await text.text()).toBe("DEMO rota");
  vi.mocked(fetch).mockResolvedValueOnce(
    engineFile("# Rules", { "content-type": "text/markdown; charset=utf-8" }),
  );
  expect((await open()).headers.get("content-type")).toBe(
    "text/markdown; charset=utf-8",
  );
  // Anything else the engine (or something in front of it) answers is not passed on.
  for (const headers of [
    { "content-type": "text/html" },
    { "content-type": "image/svg+xml" },
    { "content-length": "" },
  ]) {
    vi.mocked(fetch).mockResolvedValueOnce(engineFile(pdf, headers));
    expect((await open()).status).toBe(502);
  }
});

it("says not found, unavailable or too large, and never what the engine said", async () => {
  withEngine();
  vi.mocked(fetch).mockResolvedValueOnce(
    new Response('{"error":"fictional"}', { status: 404 }),
  );
  expect(await (await open()).json()).toEqual({ error: "not_found" });
  for (const status of [401, 403, 503]) {
    vi.mocked(fetch).mockResolvedValueOnce(new Response("", { status }));
    const response = await open();
    expect(response.status).toBe(502);
    expect(await response.json()).toEqual({ error: "unavailable" });
  }
  vi.mocked(fetch).mockRejectedValueOnce(new Error("offline"));
  expect((await open()).status).toBe(502);
  // A file over the front end's limit answers 413 with that limit, before any of it is read.
  vi.mocked(fetch).mockResolvedValueOnce(
    engineFile(pdf, { "content-length": String(maxFileBytes + 1) }),
  );
  const large = await open();
  expect(large.status).toBe(413);
  expect(await large.json()).toEqual({
    error: "too_large",
    maxBytes: maxFileBytes,
  });
  // A hosted front end passes on less (deploy/vercel/).
  vi.mocked(fetch).mockResolvedValueOnce(
    engineFile(pdf, { "content-length": String(hostedFileBytes + 1) }),
  );
  const hosted = await GET(new Request(url()));
  expect(hosted.status).toBe(413);
  expect((await hosted.json()).maxBytes).toBe(hostedFileBytes);
  // More bytes than the engine announced are cut off, not passed on.
  vi.mocked(fetch).mockResolvedValueOnce(
    engineFile("x".repeat(64), { "content-length": "8" }),
  );
  await expect((await open()).arrayBuffer()).rejects.toThrow();
});

it("takes only a resource id, from this site, and calls nothing in demo mode", async () => {
  for (const query of [
    "",
    "id=../archive",
    `id=${id.toUpperCase()}`,
    `id=${id}&id=${id}`,
    `id=${id}&download=yes`,
    `id=${id}&path=x`,
  ])
    expect((await open(query)).status).toBe(400);
  expect((await open()).status).toBe(404);
  expect(fetch).not.toHaveBeenCalled();
  withEngine();
  expect(
    (await open(undefined, { origin: "https://elsewhere.example.invalid" }))
      .status,
  ).toBe(403);
  expect(
    (
      await handleDocumentFile(
        new Request(url(), { method: "POST", body: "{}" }),
      )
    ).status,
  ).toBe(405);
  expect(fetch).not.toHaveBeenCalled();
});

it("reads and writes file names the way the engine does", () => {
  expect(dispositionName(null)).toBe("");
  expect(dispositionName('inline; filename="Plain.pdf"')).toBe("Plain.pdf");
  expect(dispositionName("inline; filename*=UTF-8''%E0%A4%A")).toBe("");
  expect(disposition("inline", "Résumé (draft).pdf")).toBe(
    "inline; filename=\"R_sum_ (draft).pdf\"; filename*=UTF-8''R%C3%A9sum%C3%A9%20%28draft%29.pdf",
  );
  expect(fileName("../../etc/passwd", "text")).toBe("passwd.txt");
  expect(fileName("a\\b.pdf", "pdf")).toBe("b.pdf");
  expect(fileName("...", "markdown")).toBe("document.md");
  expect(fileName("Rules.markdown", "markdown")).toBe("Rules.markdown");
  expect(fileName("scan.png", "pdf")).toBe("scan.png.pdf");
  expect(fileName(`${"a".repeat(300)}.pdf`, "pdf")).toHaveLength(197);
  expect(originalPath(id, true)).toBe(`/api/document-file?id=${id}&download=1`);
  // What the engine says about an original, or nothing.
  expect(
    cleanOriginal({ id, format: "pdf", bytes: 1541, name: "Manual.pdf" }),
  ).toEqual({ id, format: "pdf", bytes: 1541, name: "Manual.pdf" });
  for (const bad of [
    null,
    { id: "x", format: "pdf", bytes: 1, name: "a.pdf" },
    { id, format: "docx", bytes: 1, name: "a.docx" },
    { id, format: "pdf", bytes: 0, name: "a.pdf" },
    { id, format: "pdf", bytes: 1, name: "" },
  ])
    expect(cleanOriginal(bad)).toBeNull();
});

it("lets the browser read an original whole, and says why when it can't", async () => {
  const session = createApiSession(false);
  const lost = vi.fn();
  window.addEventListener("observatory-access-lost", lost);
  const answer = (body: BodyInit | null, init: ResponseInit) =>
    vi.mocked(fetch).mockResolvedValueOnce(new Response(body, init));
  answer(pdf, { headers: { "content-type": "application/pdf" } });
  const read = await session.original!(id);
  expect(read.ok && [...read.bytes]).toEqual([...pdf]);
  expect(vi.mocked(fetch).mock.calls.at(-1)![0]).toBe(originalPath(id));
  answer('{"error":"too_large","maxBytes":4194304}', {
    status: 413,
    headers: { "content-type": "application/json" },
  });
  expect(await session.original!(id)).toEqual({
    ok: false,
    reason: "too_large",
    maxBytes: 4194304,
  });
  answer('{"error":"not_found"}', {
    status: 404,
    headers: { "content-type": "application/json" },
  });
  expect(await session.original!(id)).toEqual({ ok: false, reason: "missing" });
  answer('{"error":"unavailable"}', {
    status: 502,
    headers: { "content-type": "application/json" },
  });
  expect(await session.original!(id)).toEqual({
    ok: false,
    reason: "unavailable",
  });
  expect(lost).not.toHaveBeenCalled();
  // A sign-in page from a front door in place of the file is access lost.
  answer("<html>sign in</html>", { headers: { "content-type": "text/html" } });
  expect(await session.original!(id)).toEqual({
    ok: false,
    reason: "unavailable",
  });
  expect(lost).toHaveBeenCalledTimes(1);
  window.removeEventListener("observatory-access-lost", lost);
  // The demo keeps no originals.
  expect(createApiSession(true).original).toBeUndefined();
});

it("reads a text original whole through a proxy that compresses what the client accepts", async () => {
  // A stand-in for a front door such as Cloudflare: it gzips a text answer when the request
  // accepts gzip, and then sends it without a Content-Length. Real Node fetch, on this computer.
  const text = "Fictional boiler notes.\n".repeat(40);
  const accepted: (string | undefined)[] = [];
  const proxy = createServer((request, response) => {
    accepted.push(request.headers["accept-encoding"]);
    if (/gzip/.test(request.headers["accept-encoding"] ?? "")) {
      response.writeHead(200, {
        "content-type": "text/plain; charset=utf-8",
        "content-encoding": "gzip",
      });
      return response.end(gzipSync(text));
    }
    response.writeHead(200, {
      "content-type": "text/plain; charset=utf-8",
      "content-length": String(Buffer.byteLength(text)),
      "content-disposition": 'inline; filename="Fictional notes.txt"',
    });
    response.end(text);
  });
  await new Promise<void>((resolve) => proxy.listen(0, "127.0.0.1", resolve));
  try {
    withEngine({
      url: `http://127.0.0.1:${(proxy.address() as AddressInfo).port}`,
    });
    const response = await handleDocumentFile(new Request(url()), nativeFetch);
    expect(response.status).toBe(200);
    expect(response.headers.get("content-length")).toBe(
      String(Buffer.byteLength(text)),
    );
    expect(await response.text()).toBe(text);
    expect(accepted).toEqual(["identity"]);
  } finally {
    proxy.close();
  }
});
