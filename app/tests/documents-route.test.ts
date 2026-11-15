import { afterEach, expect, it, vi } from "vitest";
import { handleDocuments, maxUploadBody } from "../server/documents";
import { GET, POST } from "../api/documents";
import { bodyLimit, maxBody } from "../server/routes";
import { cleanUploadAnswer, maxUploadBytes } from "../src/documents";
import { allAppTools, called, rpc, withEngine } from "./engine";

afterEach(() => {
  vi.unstubAllEnvs();
});
const url = "http://rhythm.example.invalid/api/documents";
const add = (body: unknown, headers: Record<string, string> = {}) =>
  handleDocuments(
    new Request(url, {
      method: "POST",
      headers: { "Content-Type": "application/json", ...headers },
      body: JSON.stringify(body),
    }),
  );
// A fictional text file, as the browser sends it.
const content = btoa("DEMO watering rota: Alex on Mondays.");
const document = {
  graphId: "rhythm:documents:household%3Aupload%3Aabc",
  resourceId: "6d3c2a10-0000-4000-8000-00000000d001",
  title: "Allotment rota",
  format: "text",
  pages: 1,
  characters: 36,
  status: "created",
};

it("says whether the engine takes documents, and what they may be", async () => {
  expect(await (await GET(new Request(url))).json()).toEqual({
    available: false,
    maxBytes: maxUploadBytes,
    types: [".pdf", ".txt", ".md", ".markdown"],
  });
  withEngine();
  expect((await (await GET(new Request(url))).json()).available).toBe(true);
  withEngine({ tools: allAppTools.filter((t) => t !== "app_upload_document") });
  expect((await (await GET(new Request(url))).json()).available).toBe(false);
});

it("hands one file to the engine and answers the star it becomes", async () => {
  withEngine();
  vi.mocked(fetch).mockResolvedValueOnce(rpc({ ok: true, document }));
  const response = await POST(
    new Request(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name: " Allotment rota.txt ", content }),
    }),
  );
  expect(response.headers.get("cache-control")).toBe(
    "private, no-store, max-age=0",
  );
  const { resourceId: _, ...shown } = document;
  expect(await response.json()).toEqual({ ok: true, document: shown });
  expect(called(vi.mocked(fetch).mock.calls.at(-1)![1])).toEqual({
    name: "app_upload_document",
    arguments: { name: "Allotment rota.txt", content },
  });
  // A file the engine does not take says why; anything malformed is "not added".
  vi.mocked(fetch).mockResolvedValueOnce(
    rpc({
      ok: false,
      code: "pdf_unreadable",
      reason: "This PDF could not be read.",
    }),
  );
  expect(await (await add({ name: "a.pdf", content })).json()).toEqual({
    ok: false,
    reason: "This PDF could not be read.",
  });
  vi.mocked(fetch).mockResolvedValueOnce(
    rpc({ ok: true, document: { ...document, graphId: 7 } }),
  );
  expect((await (await add({ name: "a.txt", content })).json()).ok).toBe(false);
  vi.mocked(fetch).mockResolvedValueOnce(new Response("", { status: 503 }));
  expect((await (await add({ name: "a.txt", content })).json()).reason).toMatch(
    /right now/,
  );
});

it("sends a large file in one call, past the usual 32 KiB", async () => {
  withEngine();
  vi.mocked(fetch).mockResolvedValueOnce(rpc({ ok: true, document }));
  const large = btoa("x".repeat(300_000));
  expect(
    (await (await add({ name: "big.txt", content: large })).json()).ok,
  ).toBe(true);
  expect(called(vi.mocked(fetch).mock.calls.at(-1)![1]).arguments.content).toBe(
    large,
  );
  expect(bodyLimit("/api/documents")).toBe(maxUploadBody);
  expect(maxUploadBody).toBeGreaterThan((maxUploadBytes / 3) * 4);
  expect(bodyLimit("/api/graph")).toBe(maxBody);
});

it("refuses a bad request before calling anything, and adds nothing in demo mode", async () => {
  withEngine();
  vi.mocked(fetch).mockClear();
  for (const body of [
    { name: "rota.docx", content },
    { name: "../rota.txt", content },
    { name: "", content },
    { name: "rota.txt", content: "not base64!" },
    { name: "rota.txt", content, subject: "alex" },
    { name: "rota.txt" },
  ])
    expect((await add(body)).status).toBe(400);
  expect(
    (
      await add(
        { name: "rota.txt", content },
        { Origin: "https://elsewhere.example.invalid" },
      )
    ).status,
  ).toBe(403);
  expect(fetch).not.toHaveBeenCalled();
  vi.unstubAllEnvs();
  withEngine({ url: "" });
  const demo = await (await add({ name: "rota.txt", content })).json();
  expect(demo.ok).toBe(false);
  expect(demo.reason).toMatch(/demo/);
  expect(fetch).not.toHaveBeenCalled();
  withEngine({ tools: allAppTools.filter((t) => t !== "app_upload_document") });
  expect(
    (await (await add({ name: "rota.txt", content })).json()).reason,
  ).toMatch(/off in this install/);
  expect(fetch).not.toHaveBeenCalled();
});

it("cleans the answer in the browser too", () => {
  const { resourceId: _, ...shown } = document;
  expect(cleanUploadAnswer({ ok: true, document })).toEqual({
    ok: true,
    document: shown,
  });
  expect(cleanUploadAnswer({ ok: false, reason: "No." })).toEqual({
    ok: false,
    reason: "No.",
  });
  expect(cleanUploadAnswer({ ok: true }).ok).toBe(false);
  expect(cleanUploadAnswer("yes").ok).toBe(false);
});

// Viewer sessions are off here, as on every install: a file's Family choice (SB-P1-20) is taken only
// inside a session, so anywhere else it is a body this route refuses, before calling anything.
it("takes no Family choice outside a viewer session, and keeps an answer's scope when the engine names one", async () => {
  withEngine();
  vi.mocked(fetch).mockClear();
  for (const family of [true, false])
    expect((await add({ name: "rota.txt", content, family })).status).toBe(400);
  expect(fetch).not.toHaveBeenCalled();
  const { resourceId: _, ...shown } = document;
  for (const scope of ["private", "shared"] as const)
    expect(
      cleanUploadAnswer({ ok: true, document: { ...document, scope } }),
    ).toEqual({ ok: true, document: { ...shown, scope } });
  expect(
    cleanUploadAnswer({ ok: true, document: { ...document, scope: "all" } }),
  ).toEqual({ ok: true, document: shown });
});
