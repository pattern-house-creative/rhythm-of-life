// A stub of the services the drive and second-brain adapters call, for running a whole stack against fakes (README.md
// here). The engine reaches it through RHYTHM_TEST_UPSTREAM: a call to https://<host>/<path> arrives as /<host>/<path>.
// Everything is fictional and kept in memory; GET /state shows the Sheet, so a test can check a write and its Undo.
// No dependencies: node upstream.mjs (PORT, default 8080).
import { createServer } from "node:http";

const SHEET = "application/vnd.google-apps.spreadsheet", DOC = "application/vnd.google-apps.document";
const FOLDER = "application/vnd.google-apps.folder";
let version = 7;
const files = {
  "stub-root": { name: "Household (stub)", mimeType: FOLDER, parents: ["my-drive"] },
  "stub-budget": { name: "Budget (stub)", mimeType: SHEET, parents: ["stub-root"] },
  "stub-plan": { name: "Plan (stub)", mimeType: DOC, parents: ["stub-root"] },
  "stub-notes": { name: "Notes.md", mimeType: "text/markdown", parents: ["stub-root"],
    body: "# Notes\nThe numbers are in https://docs.google.com/spreadsheets/d/stub-budget/edit\n" },
};
let sheet = [["Item", "Amount"], ["Rent", 1000], ["Food", 240]];
const memories = [
  { id: "00000000-0000-4000-8000-000000000101", content: "Weekly review on Sundays. Plan: https://docs.google.com/document/d/stub-plan/edit",
    tags: ["project:routines"], created_at: 1790000000000 },
  { id: "00000000-0000-4000-8000-000000000102", content: "Keep the budget simple: three lines are enough.", tags: [], created_at: 1790000100000 },
];

const json = (res, status, value) => {
  res.writeHead(status, { "content-type": "application/json" });
  res.end(JSON.stringify(value));
};
const meta = (id) => {
  const f = files[id];
  const link = f.mimeType === SHEET ? `https://docs.google.com/spreadsheets/d/${id}/edit` : f.mimeType === DOC
    ? `https://docs.google.com/document/d/${id}/edit` : f.mimeType === FOLDER ? `https://drive.google.com/drive/folders/${id}`
    : `https://drive.google.com/file/d/${id}/view`;
  return { id, name: f.name, mimeType: f.mimeType, parents: f.parents, version: String(version), trashed: false,
    modifiedTime: "2026-10-01T09:00:00.000Z", webViewLink: link, size: String((f.body ?? "").length) };
};
// 'Budget'!B2:C3 or Budget!A4 -> zero-based rows and columns.
const range = (text) => {
  const m = /^(?:'((?:[^']|'')+)'|([^!]+))!([A-Z]+)(\d+)(?::([A-Z]+)(\d+))?$/.exec(text);
  if (!m) return null;
  const col = (s) => [...s].reduce((n, c) => n * 26 + c.charCodeAt(0) - 64, 0) - 1;
  const r1 = Number(m[4]) - 1, c1 = col(m[3]);
  return { tab: m[1] ? m[1].replaceAll("''", "'") : m[2], r1, c1, r2: m[6] ? Number(m[6]) - 1 : r1, c2: m[5] ? col(m[5]) : c1 };
};
const grid = (r) => sheet.slice(r.r1, r.r2 + 1).map((row) => row.slice(r.c1, r.c2 + 1));

function google(req, res, url, body) {
  const [host, ...rest] = url.pathname.slice(1).split("/");
  const path = "/" + rest.join("/");
  if (host === "oauth2.googleapis.com") {
    const form = new URLSearchParams(body);
    return json(res, 200, { access_token: "stub-" + form.get("refresh_token"), expires_in: 3600 });
  }
  if (host === "www.googleapis.com") {
    if (path === "/drive/v3/files") {
      const parent = /^'([^']+)' in parents/.exec(url.searchParams.get("q") ?? "")?.[1];
      return json(res, 200, { files: Object.keys(files).filter((id) => files[id].parents.includes(parent)).map(meta) });
    }
    const id = path.replace("/drive/v3/files/", "").replace("/export", "");
    if (!files[id]) return json(res, 404, { error: { code: 404 } });
    if (path.endsWith("/export")) return json(res, 403, { error: { code: 403, message: "stub: no exports" } });
    if (url.searchParams.get("alt") === "media") { res.writeHead(200); return res.end(files[id].body ?? ""); }
    return json(res, 200, meta(id));
  }
  if (host === "sheets.googleapis.com") {
    if (path === "/v4/spreadsheets/stub-budget") return json(res, 200, { sheets: [{ properties: { sheetId: 0, title: "Budget", index: 0 } }] });
    if (path === "/v4/spreadsheets/stub-budget/values:batchGet") return json(res, 200, { valueRanges: [{ range: "Budget!A1:Z1000", values: sheet }] });
    if (path === "/v4/spreadsheets/stub-budget:batchUpdate") {
      const d = JSON.parse(body).requests[0].deleteDimension.range;
      sheet.splice(d.startIndex, d.endIndex - d.startIndex); version++;
      return json(res, 200, {});
    }
    const asked = decodeURIComponent(path.replace("/v4/spreadsheets/stub-budget/values/", ""));
    if (asked.endsWith(":append")) {
      const row = JSON.parse(body).values[0]; sheet.push(row); version++;
      return json(res, 200, { updates: { updatedRange: `Budget!A${sheet.length}:${String.fromCharCode(64 + row.length)}${sheet.length}` } });
    }
    const r = range(asked);
    if (!r) return json(res, 400, { error: { message: "stub: bad range" } });
    if (req.method === "GET") return json(res, 200, { values: grid(r) });
    const values = JSON.parse(body).values;
    values.forEach((row, i) => row.forEach((value, j) => {
      while (sheet.length <= r.r1 + i) sheet.push([]);
      const target = sheet[r.r1 + i];
      while (target.length <= r.c1 + j) target.push("");
      // USER_ENTERED reads a leading apostrophe as "text"; RAW keeps everything as given.
      target[r.c1 + j] = url.searchParams.get("valueInputOption") === "USER_ENTERED" && typeof value === "string" && value.startsWith("'") ? value.slice(1) : value;
    }));
    version++;
    return json(res, 200, {});
  }
  return json(res, 404, { error: { message: "stub: unknown Google route" } });
}

function brain(req, res, url) {
  const route = url.pathname.replace("/brain.example.invalid/", "");
  if (route === "entry") {
    const m = memories.find((x) => x.id === url.searchParams.get("id"));
    if (!m) return json(res, 404, { ok: false });
    return json(res, 200, { ok: true, entry: { ...m, workspace: "personal", validity_state: "current", retracted_source: false, updated_at: m.created_at } });
  }
  if (route === "projects") return json(res, 200, { projects: [{ id: "routines", name: "Routines (stub)", aliases: [] }] });
  if (route === "graph") return json(res, 200, { nodes: [], edges: [{ source: memories[0].id, target: memories[1].id, type: "relates_to", weight: 0.6, provenance: "explicit" }] });
  if (route === "list") return json(res, 200, memories.map((m) => ({ ...m, tags: JSON.stringify(m.tags), source: "api", workspace: "personal",
    workspace_id: "ws-personal", validity_state: "current", retracted_source: false })));
  return json(res, 404, { ok: false });
}

createServer((req, res) => {
  let body = "";
  req.on("data", (chunk) => (body += chunk));
  req.on("end", () => {
    const url = new URL(req.url, "http://upstream");
    if (url.pathname === "/health") return json(res, 200, { ok: true });
    if (url.pathname === "/state") return json(res, 200, { version, sheet });
    // Every adapter call carries a bearer token; the stub never sees a real one.
    if (!(req.headers.authorization ?? "").startsWith("Bearer ") && !url.pathname.startsWith("/oauth2.googleapis.com/"))
      return json(res, 401, { error: "stub: no bearer token" });
    console.log(`${req.method} ${url.pathname}`);
    return url.pathname.startsWith("/brain.example.invalid/") ? brain(req, res, url) : google(req, res, url, body);
  });
}).listen(Number(process.env.PORT ?? 8080), () => console.log("upstream stub listening"));
