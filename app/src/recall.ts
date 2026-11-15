import type { GraphPage } from "./knowledge.js";

// The explorer's combined search (SB-P2-10; contracts/README.md 2.2, app_graph operation recall,
// only while the engine's engine.recall is on): what combined retrieval finds by words and
// meaning, as the stars on this reader's map that stand for it, best first, each with why.
// Nothing here is kept; a search in demo mode, or with it off, has none.

// Why a star is here: its place, the keyword arm's rank, how many of the question's words it
// holds and its exact id hits, the meaning arm's rank and closeness. An arm that did not find it
// is null. Imputed: it has no passages for the meaning arm, so it took that arm's share at its
// keyword rank.
export interface RecallWhy {
  rank: number;
  words: { rank: number; terms: string[]; exact: number } | null;
  meaning: { rank: number; similarity: number | null } | null;
  imputed: boolean;
}
export interface RecallResult {
  id: string;
  score: number;
  excerpt: string;
  truncated: boolean;
  why: RecallWhy;
}
// One question's answer: its results, what the reader must be told (semantic search skipped, live
// files left out), how many records were found that no star on this map stands for (a count,
// never named) and the receipt that cites this answer.
export interface Recalled {
  query: string;
  results: RecallResult[];
  notices: string[];
  semanticSkipped: boolean;
  offMap: number;
  receipt: string;
}
// What /api/graph answers for the operation recall: off (demo mode, the engine's engine.recall
// off, or an engine without it), not known this time (retry: the facade could not read the
// engine's tool listing, or the browser could not reach the facade; the explorer shows nothing
// for it and asks again), on but this search failed, or the stars and why.
export type RecallAnswer =
  | { available: false; retry?: true }
  | { available: true; failed: true }
  | {
      available: true;
      failed?: false;
      page: GraphPage & { cursors: Record<string, string | null> };
      recall: Recalled;
    };

export const maxQuestion = 800;
const maxResults = 20;
const record = (v: unknown): v is Record<string, unknown> =>
  !!v && typeof v === "object" && !Array.isArray(v);
const count = (v: unknown, min = 0, max = Number.MAX_SAFE_INTEGER) =>
  Number.isInteger(v) && (v as number) >= min && (v as number) <= max;
const text = (v: unknown, max: number) =>
  typeof v === "string" && v.length <= max;

function cleanWhy(value: unknown): RecallWhy | null {
  if (!record(value) || !count(value.rank, 1, maxResults)) return null;
  if (typeof value.imputed !== "boolean") return null;
  let words: RecallWhy["words"] = null;
  if (value.words !== null) {
    const w = value.words;
    if (
      !record(w) ||
      !count(w.rank, 1) ||
      !count(w.exact) ||
      !Array.isArray(w.terms) ||
      w.terms.length > 64 ||
      !w.terms.every((t) => text(t, 200))
    )
      return null;
    words = {
      rank: w.rank as number,
      terms: [...(w.terms as string[])],
      exact: w.exact as number,
    };
  }
  let meaning: RecallWhy["meaning"] = null;
  if (value.meaning !== null) {
    const m = value.meaning;
    if (
      !record(m) ||
      !count(m.rank, 1) ||
      !(
        m.similarity === null ||
        (typeof m.similarity === "number" && Number.isFinite(m.similarity))
      )
    )
      return null;
    meaning = {
      rank: m.rank as number,
      similarity: m.similarity as number | null,
    };
  }
  return { rank: value.rank as number, words, meaning, imputed: value.imputed };
}

// The engine's recall member, checked field by field against the stars it came with: a result
// whose star is not among them (one the facade left out as unreadable) goes, the rest keep their
// order. Null when it is not one.
export function cleanRecalled(
  value: unknown,
  stars: ReadonlySet<string>,
): Recalled | null {
  if (
    !record(value) ||
    !text(value.query, maxQuestion) ||
    !Array.isArray(value.results) ||
    value.results.length > maxResults ||
    !Array.isArray(value.notices) ||
    value.notices.length > 4 ||
    !value.notices.every((n) => text(n, 400)) ||
    typeof value.semanticSkipped !== "boolean" ||
    !count(value.offMap, 0, maxResults) ||
    typeof value.receipt !== "string" ||
    !/^rc1-[0-9a-f]{16}$/.test(value.receipt)
  )
    return null;
  const results: RecallResult[] = [];
  for (const r of value.results) {
    if (
      !record(r) ||
      !text(r.id, 1000) ||
      typeof r.score !== "number" ||
      !Number.isFinite(r.score) ||
      !text(r.excerpt, 300) ||
      typeof r.truncated !== "boolean"
    )
      return null;
    const why = cleanWhy(r.why);
    if (!why) return null;
    if (!stars.has(r.id as string) || results.some((x) => x.id === r.id))
      continue;
    results.push({
      id: r.id as string,
      score: r.score,
      excerpt: r.excerpt as string,
      truncated: r.truncated,
      why,
    });
  }
  return {
    query: value.query as string,
    results,
    notices: [...(value.notices as string[])],
    semanticSkipped: value.semanticSkipped,
    offMap: value.offMap as number,
    receipt: value.receipt,
  };
}

// Why a star was found, in plain words: "By an exact id and 2 of your words", "By its meaning",
// "By 1 of your words and its meaning". The words themselves are the search's stems, so they are
// counted rather than shown.
export function recallReason(why: RecallWhy): string {
  const parts: string[] = [];
  if (why.words) {
    if (why.words.exact > 0)
      parts.push(
        why.words.exact === 1 ? "an exact id" : `${why.words.exact} exact ids`,
      );
    const words = why.words.terms.length;
    if (words > 0) parts.push(`${words} of your words`);
    else if (!why.words.exact) parts.push("your words");
  }
  if (why.meaning) parts.push("its meaning");
  if (!parts.length) return "Found by words and meaning";
  return `By ${parts.length > 1 ? `${parts.slice(0, -1).join(", ")} and ${parts.at(-1)}` : parts[0]}`;
}
