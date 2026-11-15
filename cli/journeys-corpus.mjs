#!/usr/bin/env node
// The synthetic parity corpus (engine/tests/Knowledge.Tests/Parity/corpus.json, docs/dev/second-brain-parity/fixtures.md) as
// the file `rhythm import` reads (engine/src/Cli/MemoryArchive.cs), for cli/journeys.sh's sandbox stack: one scope's
// memories and projects, with their commitments, decisions, due dates and standing instructions, and the links between
// them. The corpus stays the one source: nothing of it is copied into another file of the repo.
//
//   node cli/journeys-corpus.mjs family|alex|sam      the file, on standard output
//   node cli/journeys-corpus.mjs documents            Family's documents, as save_document arguments, one JSON line each
//   node cli/journeys-corpus.mjs held family|alex|sam how many of the scope's memories the corpus marks as held
//
// Everything in it is DEMO data: two invented people, invented places. Ids are drawn from the corpus's own, so two runs
// write the same file and importing it again adds nothing. An injection the corpus marks as held is not marked here:
// the import holds it by its words, as it would any file's, and cli/journeys.sh checks it held as many as the corpus
// marks (held).
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scopes = { family: { corpus: "family", subject: "household", scope: "shared" }, alex: { corpus: "personal:alex", subject: "alex", scope: "private" },
  sam: { corpus: "personal:sam", subject: "sam", scope: "private" } };

// An id of its own for a corpus id, the same every time.
export function corpusId(id) {
  const h = createHash("sha256").update(`rhythm-journeys:${id}`).digest("hex");
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-4${h.slice(13, 16)}-8${h.slice(17, 20)}-${h.slice(20, 32)}`;
}

export function readCorpus(path = fileURLToPath(new URL("../engine/tests/Knowledge.Tests/Parity/corpus.json", import.meta.url))) {
  return JSON.parse(readFileSync(path, "utf8"));
}

// A corpus date (a day, or a moment) as the moment an archive holds.
const moment = (at) => (at === null || at === undefined ? null : /^\d{4}-\d{2}-\d{2}$/.test(at) ? `${at}T00:00:00+00:00` : at);
// The other person's name as the store keeps it (058: lower case, spaces to hyphens, anything else dropped; none when
// nothing is left).
const slug = (name) => (name ?? "").toLowerCase().replace(/\s+/g, "-").replace(/[^a-z0-9_-]/g, "").replace(/^[-_]+/, "").slice(0, 64) || null;
// A memory's commitment as the archive has it: from whose side it is owed. An owed by the memory's own author is theirs to do.
function commitment(memory) {
  const c = memory.commitment;
  if (!c) return undefined;
  const out = c.owedBy === memory.author;
  return { direction: out ? "out" : "in", counterparty: slug(out ? c.owedTo : c.owedBy) };
}
// A commitment is a task as remember_memory_commitment has it (058): the task tag is what makes it an open loop.
// The sources the store's checks take for a memory's source (a client's own words stay out of it).
const source = (memory) => (memory.source?.channel === "app" ? "app" : memory.source?.channel === "import" ? "import" : "mcp");

// One scope's file. `now` is when the file says it was made.
export function archive(which, corpus = readCorpus(), now = "2026-11-02T09:00:00+00:00") {
  const place = scopes[which];
  if (!place) throw new Error(`no scope ${which}: family, alex or sam`);
  const mine = corpus.memories.filter((m) => m.scope === place.corpus);
  const ids = new Set(mine.map((m) => m.id));
  const used = new Set(mine.map((m) => m.project).filter(Boolean));
  const projects = corpus.projects.filter((p) => used.has(p.id)).map((p) => ({
    id: corpusId(`project:${which}:${p.id}`), subject: place.subject, scope: place.scope, slug: p.id, name: p.name, description: p.description ?? "",
    status: "active", author: place.subject === "household" ? null : place.subject, aliases: p.aliases ?? [], revision: 1, createdAt: now, updatedAt: now,
  }));
  const projectId = (slug) => (slug ? corpusId(`project:${which}:${slug}`) : null);
  const memories = mine.map((m) => {
    const committed = commitment(m);
    const out = {
      id: corpusId(m.id), subject: place.subject, scope: place.scope, author: m.author ?? null, source: source(m), content: m.text, tags: committed ? [...new Set([...(m.tags ?? []), "task"])] : m.tags ?? [], kind: m.kind ?? null,
      status: m.status, importance: m.importance ?? null, volatility: m.volatility ?? null, project: projectId(m.project), capsule: null, capsuleSlot: null,
      hold: null, holdScore: null, validFrom: moment(m.validFrom), validUntil: moment(m.validUntil), revision: 1, createdAt: moment(m.createdAt), updatedAt: moment(m.updatedAt),
      versions: [], trash: null,
    };
    if (committed) out.commitment = committed;
    if (m.decision)
      out.decision = { confidence: m.decision.confidence ?? null, confidenceSource: "stated",
        states: [{ revision: 1, outcome: null, rearms: 0, reviewAt: moment(m.decision.reviewBy) }] };
    const due = m.when?.at ?? m.commitment?.due;
    if (due) out.due = { at: /T/.test(due) ? due : `${due}T09:00:00Z`, kind: m.when?.kind === "event" ? "event" : "due", source: "explicit" };
    if (m.standing) out.standing = true;
    return out;
  });
  const links = mine.flatMap((m) => (m.links ?? []).filter((l) => ids.has(l.target) && l.type !== "supersedes").map((l) => ({
    id: corpusId(`link:${m.id}:${l.type}:${l.target}`), subject: place.subject, source: corpusId(m.id), target: corpusId(l.target), type: l.type,
    verification: "verified", evidence: "DEMO corpus link", lifecycle: "active", createdAt: moment(m.createdAt), trash: null,
  })));
  return {
    format: "rhythm-memories", version: 1, install: corpusId("install"), export: corpusId(`export:${which}`), exportedAt: now,
    reader: place.subject === "household" ? { person: null } : { person: place.subject }, scopes: place.scope === "private" ? "on" : "off",
    projects, memories, links, counts: { projects: projects.length, memories: memories.length, links: links.length },
  };
}

// The memories of one scope the corpus marks as held for review (expect.held), each with its id here and the phrases of
// its words (24 characters, every 8) that no other memory or document of the corpus holds, so a phrase found in an
// answer can only be its words.
export function held(which, corpus = readCorpus()) {
  const place = scopes[which];
  if (!place) throw new Error(`no scope ${which}: family, alex or sam`);
  return corpus.memories.filter((m) => m.scope === place.corpus && m.expect?.held === true).map((m) => {
    const others = [...corpus.memories.filter((o) => o.id !== m.id).map((o) => o.text), ...corpus.documents.map((d) => d.text)];
    const phrases = [];
    for (let at = 0; at + 24 <= m.text.length; at += 8) {
      const phrase = m.text.slice(at, at + 24);
      if (!others.some((text) => text.includes(phrase))) phrases.push(phrase);
    }
    return { id: corpusId(m.id), phrases };
  });
}

// Family's documents, as an AI client saves its copies through save_document.
export function documents(corpus = readCorpus()) {
  return corpus.documents.filter((d) => d.scope === "family").map((d) => ({
    fileId: d.source?.fileId ?? d.id, title: d.title, content: d.text, modifiedAt: d.source?.modifiedAt, url: d.source?.url, subject: "household", domain: "knowledge",
  }));
}

const same = (a, b) => (process.platform === "win32" ? a.toLowerCase() === b.toLowerCase() : a === b);
if (process.argv[1] && same(resolve(fileURLToPath(import.meta.url)), resolve(process.argv[1]))) {
  const [which] = process.argv.slice(2);
  if (which === "documents") for (const d of documents()) console.log(JSON.stringify(d));
  else if (which === "held") console.log(held(process.argv[3]).length);
  else console.log(JSON.stringify(archive(which)));
}
