#!/usr/bin/env node
// scrub-check: keeps secrets, personal identifiers and blocked files out of this repo.
//
//   node scripts/scrub-check.mjs [--staged | --all | --history | --message <file>]
//                                [--public-only] [--patterns <file>] [--max-bytes <n>]
//                                [--root <dir>] [--quiet]
//
//   --staged    files staged for commit, plus the author and committer identity (hook default)
//   --all       every tracked file (index content)
//   --history   every blob in history, every path ever committed, and every commit's
//               author, committer, dates and message
//   --message   one commit message file, plus the author and committer identity (commit-msg hook)
//
// Checks: built-in secret rules, the private pattern file (RHYTHM_SCRUB_PATTERNS, default
// ~/.rhythm-oss-private/scrub-patterns.txt; missing means fail closed unless --public-only),
// blocked file names, a size guard, and commit dates in UTC (an offset names a time zone).
// --public-only is for whoever does not hold the private pattern file (a contributor, CI): it runs the public rules
// and leaves commit dates alone, since a contributor's time zone is theirs to show.
// scripts/scrub-allow.txt lists reviewed exceptions.
//
// Exit codes: 0 clean, 1 findings, 2 configuration error.
// Output never prints a private pattern or the text it matched, and redacts secret matches.
// No dependencies: Node 20 or later.

import { spawnSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { homedir } from "node:os";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

export const DEFAULT_MAX_BYTES = 1024 * 1024;

// ---------------------------------------------------------------------------
// Built-in secret rules (public). Each regex runs per line, case-sensitive unless flagged.
// ---------------------------------------------------------------------------
export const SECRET_RULES = [
  { id: "secret:private-key", re: /-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY(?: BLOCK)?-----/ },
  { id: "secret:github-token", re: /\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{30,}\b/ },
  { id: "secret:github-pat", re: /\bgithub_pat_[A-Za-z0-9_]{22,}\b/ },
  { id: "secret:sk-key", re: /\bsk-(?:proj-|ant-|svcacct-|admin-)?[A-Za-z0-9_-]{20,}/ },
  { id: "secret:stripe-key", re: /\b[rs]k_(?:live|test)_[A-Za-z0-9]{16,}\b/ },
  { id: "secret:slack-token", re: /\bxox[abposre]-[A-Za-z0-9-]{10,}/ },
  { id: "secret:slack-webhook", re: /hooks\.slack\.com\/services\/T[A-Za-z0-9]+\/B[A-Za-z0-9]+\/[A-Za-z0-9]+/ },
  { id: "secret:aws-access-key", re: /\b(?:AKIA|ASIA|ABIA|ACCA)[0-9A-Z]{16}\b/ },
  { id: "secret:google-api-key", re: /\bAIza[0-9A-Za-z_-]{35}\b/ },
  { id: "secret:google-oauth-secret", re: /\bGOCSPX-[A-Za-z0-9_-]{20,}/ },
  { id: "secret:google-refresh-token", re: /\b1\/\/0[A-Za-z0-9_-]{30,}/ },
  { id: "secret:jwt", re: /\beyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}/ },
  { id: "secret:base64-json-token", re: /\beyJ[A-Za-z0-9+/_-]{60,}={0,2}/ },
  { id: "secret:npm-token", re: /\bnpm_[A-Za-z0-9]{36}\b/ },
  { id: "secret:url-credentials", re: /\b[a-z][a-z0-9+.-]{1,20}:\/\/[^\s/:@'"`<>${}]+:[^\s/:@'"`<>${}]{3,}@[^\s/'"`]+/i },
  {
    id: "secret:bearer",
    re: /\bBearer\s+([A-Za-z0-9._~+/=-]{20,})/,
    check: (m) => highEntropy(m[1], 3.0),
  },
  {
    id: "secret:assignment",
    // password / secret / token / api key style names assigned a long, high-entropy literal
    re: /[A-Za-z0-9_]*(?:passw(?:or)?d|passwd|pwd|secret|token|api[_-]?key|apikey|access[_-]?key|private[_-]?key|credential)[A-Za-z0-9_]*["']?\s*(?::|=|:=|=>)\s*["'`]?([A-Za-z0-9+/=_.~-]{16,})/i,
    check: (m) => assignedSecret(m[1]),
  },
  { id: "path:windows-user", re: /\b[A-Za-z]:(?:\\{1,2}|\/)Users(?:\\{1,2}|\/)[A-Za-z0-9._-]+/ },
  { id: "release:image-digest", re: /@sha256:[0-9a-f]{64}\b/ },
];

// ---------------------------------------------------------------------------
// Blocked file names (public). Matched against the repo-relative path with forward slashes.
// ---------------------------------------------------------------------------
export const FILE_RULES = [
  { id: "file:env", test: (p) => /(?:^|\/)\.env(?:\.|$)/.test(p) && !/(?:^|\/)\.env\.example$/.test(p) },
  { id: "file:secrets-dir", test: (p) => /(?:^|\/)secrets\//i.test(p) },
  { id: "file:pem", test: (p) => /\.(?:pem|p12|pfx|jks|keystore)$/i.test(p) },
  { id: "file:ssh-key", test: (p) => /(?:^|\/)(?:id_rsa|id_dsa|id_ecdsa|id_ed25519)(?:\.pub)?$/.test(p) || /(?:^|\/)known_hosts$/.test(p) },
  { id: "file:dpapi", test: (p) => /\.dpapi$/i.test(p) },
  { id: "file:trx", test: (p) => /\.trx$/i.test(p) },
  { id: "file:evidence-json", test: (p) => /evidence[^/]*\.json$/i.test(p) },
  { id: "file:garmin-openapi", test: (p) => /(?:^|\/)garmin-openapi[^/]*\.json$/i.test(p) },
  { id: "file:agpl-carelink-reference", test: (p) => /carelink-python-reference/i.test(p) },
  { id: "file:release-chain", test: (p) => /_release\.py$/i.test(p) || /(?:^|\/)candidate\.json$/i.test(p) || /(?:^|\/)releases\//.test(p) },
  { id: "file:private-plan", test: (p) => p === "PLAN.md" },
];

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------
export function entropy(text) {
  if (!text) return 0;
  const counts = new Map();
  for (const ch of text) counts.set(ch, (counts.get(ch) ?? 0) + 1);
  let bits = 0;
  for (const n of counts.values()) {
    const p = n / text.length;
    bits -= p * Math.log2(p);
  }
  return bits;
}

export function highEntropy(value, threshold) {
  return /[A-Za-z]/.test(value) && /[0-9]/.test(value) && entropy(value) >= threshold;
}

const PLACEHOLDER = /(?:example|placeholder|changeme|change-me|redacted|dummy|sample|your[_-]|xxxx|\.\.\.)/i;

export function assignedSecret(value) {
  if (value.length < 16) return false;
  if (/^(?:\/|\.\.?\/|~)/.test(value)) return false; // a file path, not a value
  if (PLACEHOLDER.test(value)) return false;
  if (/^[a-z]+(?:[._-][a-z0-9]+)+$/i.test(value) && !/[0-9]{4,}/.test(value)) return false; // dotted identifiers
  return highEntropy(value, 3.5);
}

export function redact(text) {
  const t = String(text);
  return t.length <= 4 ? "****" : `${t.slice(0, 4)}****`;
}

export function globToRegExp(glob) {
  let out = "";
  for (let i = 0; i < glob.length; i++) {
    const c = glob[i];
    if (c === "*") {
      if (glob[i + 1] === "*") {
        i++;
        if (glob[i + 1] === "/") {
          i++;
          out += "(?:.*/)?";
        } else out += ".*";
      } else out += "[^/]*";
    } else if (c === "?") out += "[^/]";
    else out += c.replace(/[.+^${}()|[\]\\]/g, "\\$&");
  }
  return new RegExp(`^${out}$`);
}

// Allowlist: "<rule-id> <path-glob> -- <reason>". Every entry needs a reason.
// Private-pattern matches cannot be allowlisted: fix the pattern file instead.
export function parseAllowlist(text, source = "scrub-allow.txt") {
  const entries = [];
  const errors = [];
  text.split(/\r?\n/).forEach((raw, index) => {
    const line = raw.trim();
    if (!line || line.startsWith("#")) return;
    const m = /^(\S+)\s+(\S+)\s+--\s+(.{8,})$/.exec(line);
    if (!m) {
      errors.push(`${source}:${index + 1}: expected "<rule-id> <path-glob> -- <reason of 8+ characters>"`);
      return;
    }
    const [, rule, glob, reason] = m;
    if (rule.startsWith("private")) {
      errors.push(`${source}:${index + 1}: private pattern matches cannot be allowlisted`);
      return;
    }
    entries.push({ rule, glob, reason: reason.trim(), re: globToRegExp(glob) });
  });
  return { entries, errors };
}

export function allowed(allow, rule, path) {
  return allow.some((e) => (e.rule === rule || (e.rule.endsWith(":*") && rule.startsWith(e.rule.slice(0, -1)))) && e.re.test(path));
}

// Private patterns: one JavaScript regex per line, # comments, matched case-insensitively.
export function parsePatterns(text) {
  const patterns = [];
  const errors = [];
  text.split(/\r?\n/).forEach((raw, index) => {
    const line = raw.trim();
    if (!line || line.startsWith("#")) return;
    try {
      patterns.push({ n: patterns.length + 1, re: new RegExp(line, "i") });
    } catch {
      errors.push(`pattern file line ${index + 1}: not a valid regular expression`);
    }
  });
  return { patterns, errors };
}

export function defaultPatternFile() {
  return process.env.RHYTHM_SCRUB_PATTERNS || join(homedir(), ".rhythm-oss-private", "scrub-patterns.txt");
}

const globals = new WeakMap();
function globalOf(re) {
  let g = globals.get(re);
  if (!g) {
    g = new RegExp(re.source, re.flags.includes("g") ? re.flags : `${re.flags}g`);
    globals.set(re, g);
  }
  return g;
}

function isBinary(buf) {
  const n = Math.min(buf.length, 8000);
  for (let i = 0; i < n; i++) if (buf[i] === 0) return true;
  return false;
}

// Scan one piece of text. Returns findings without the matched private text.
export function scanText(text, where, { patterns, allow, binary = false, secretRules = SECRET_RULES }) {
  const findings = [];
  const lines = text.split(/\r?\n/);
  lines.forEach((line, i) => {
    const at = `${where}:${i + 1}`;
    for (const p of patterns) {
      if (p.re.test(line)) findings.push({ where: at, rule: "private", detail: `private pattern #${p.n}` });
    }
    if (binary) return;
    for (const rule of secretRules) {
      // Look past a benign first match: the first one that passes the rule's check counts.
      const m = [...line.matchAll(globalOf(rule.re))].find((x) => !rule.check || rule.check(x));
      if (!m) continue;
      if (allowed(allow, rule.id, where)) continue;
      findings.push({ where: at, rule: rule.id, detail: redact(m[1] ?? m[0]) });
    }
  });
  return findings;
}

export function scanPath(path, { patterns, allow }) {
  const findings = [];
  for (const rule of FILE_RULES) {
    if (rule.test(path) && !allowed(allow, rule.id, path)) findings.push({ where: path, rule: rule.id, detail: "blocked file name" });
  }
  for (const p of patterns) {
    if (p.re.test(path)) findings.push({ where: path, rule: "private", detail: `private pattern #${p.n} in path` });
  }
  return findings;
}

export function scanBlob(path, buf, ctx) {
  const findings = [...scanPath(path, ctx)];
  if (buf.length > ctx.maxBytes && !allowed(ctx.allow, "size", path)) {
    findings.push({ where: path, rule: "size", detail: `${buf.length} bytes > ${ctx.maxBytes}` });
  }
  const binary = isBinary(buf);
  const text = buf.toString(binary ? "latin1" : "utf8");
  findings.push(...scanText(text, path, { ...ctx, binary }));
  return findings;
}

// ---------------------------------------------------------------------------
// Git plumbing
// ---------------------------------------------------------------------------
function git(root, args, input) {
  const r = spawnSync("git", ["-C", root, "-c", "core.quotepath=off", ...args], {
    input,
    maxBuffer: 1024 * 1024 * 1024,
    windowsHide: true,
  });
  if (r.error) throw r.error;
  if (r.status !== 0) throw new Error(`git ${args.join(" ")} failed: ${r.stderr.toString().trim()}`);
  return r.stdout;
}

// Reads many objects at once through cat-file --batch. names: object names (sha or :path).
function readObjects(root, names) {
  const out = new Map();
  if (names.length === 0) return out;
  const buf = git(root, ["cat-file", "--batch"], names.join("\n") + "\n");
  let pos = 0;
  for (const name of names) {
    const nl = buf.indexOf(10, pos);
    const header = buf.subarray(pos, nl).toString("utf8");
    pos = nl + 1;
    if (header.endsWith(" missing")) continue;
    const size = Number(header.split(" ")[2]);
    out.set(name, buf.subarray(pos, pos + size));
    pos += size + 1;
  }
  return out;
}

function indexEntries(root) {
  // "<mode> <sha> <stage>\t<path>" NUL-separated
  const raw = git(root, ["ls-files", "-s", "-z"]).toString("utf8");
  const entries = [];
  for (const rec of raw.split("\0")) {
    if (!rec) continue;
    const tab = rec.indexOf("\t");
    const [mode, sha, stage] = rec.slice(0, tab).split(" ");
    if (stage !== "0" || mode === "160000") continue;
    entries.push({ path: rec.slice(tab + 1), sha });
  }
  return entries;
}

function stagedPaths(root) {
  const raw = git(root, ["diff", "--cached", "--name-only", "-z", "--diff-filter=ACMRT"]).toString("utf8");
  return new Set(raw.split("\0").filter(Boolean));
}

// A commit date carries the committer's UTC offset, which tells where they live: commits here are in UTC.
export function zoneFindings(offset, where, ctx) {
  if (ctx?.zones === false) return [];
  return offset === "+0000" ? [] : [{ where, rule: "identity:timezone", detail: "date not in UTC; commit with TZ=UTC" }];
}

function identities(root) {
  const ids = [];
  for (const v of ["GIT_AUTHOR_IDENT", "GIT_COMMITTER_IDENT"]) {
    const r = spawnSync("git", ["-C", root, "var", v], { windowsHide: true });
    if (r.status !== 0) continue;
    const ident = r.stdout.toString("utf8");
    const date = /\s+\d+\s+([+-]\d{4})\s*$/.exec(ident);
    ids.push({ label: v === "GIT_AUTHOR_IDENT" ? "author" : "committer", text: ident.slice(0, date?.index), offset: date?.[1] });
  }
  return ids;
}

function scanIdentities(root, ctx) {
  const findings = [];
  for (const id of identities(root)) {
    findings.push(...scanText(id.text, `commit ${id.label}`, ctx));
    findings.push(...zoneFindings(id.offset, `commit ${id.label}`, ctx));
  }
  return findings;
}

// ---------------------------------------------------------------------------
// Modes
// ---------------------------------------------------------------------------
export function runStaged(root, ctx) {
  const staged = stagedPaths(root);
  const entries = indexEntries(root).filter((e) => staged.has(e.path));
  const blobs = readObjects(root, entries.map((e) => e.sha));
  const findings = [];
  for (const e of entries) findings.push(...scanBlob(e.path, blobs.get(e.sha) ?? Buffer.alloc(0), ctx));
  findings.push(...scanIdentities(root, ctx));
  return { findings, scanned: entries.length };
}

export function runAll(root, ctx) {
  const entries = indexEntries(root);
  const blobs = readObjects(root, [...new Set(entries.map((e) => e.sha))]);
  const findings = [];
  for (const e of entries) findings.push(...scanBlob(e.path, blobs.get(e.sha) ?? Buffer.alloc(0), ctx));
  return { findings, scanned: entries.length };
}

export function runMessage(root, file, ctx) {
  const text = readFileSync(file, "utf8")
    .split(/\r?\n/)
    .filter((l) => !l.startsWith("#"))
    .join("\n");
  const findings = [...scanText(text, "commit message", ctx), ...scanIdentities(root, ctx)];
  return { findings, scanned: 1 };
}

export function runHistory(root, ctx) {
  const findings = [];
  let hasCommits = true;
  try {
    git(root, ["rev-parse", "--verify", "-q", "HEAD"]);
  } catch {
    hasCommits = spawnSync("git", ["-C", root, "rev-list", "-n", "1", "--all"], { windowsHide: true }).stdout.length > 0;
  }
  if (!hasCommits) return { findings, scanned: 0 };

  // Commit metadata: author, committer, their dates' UTC offsets and message of every commit on every ref.
  const meta = git(root, ["log", "--all", "--date=raw", "--format=%H%x00%an <%ae>%x00%cn <%ce>%x00%ad%x00%cd%x00%B%x1e"]).toString("utf8");
  let commits = 0;
  for (const rec of meta.split("\x1e")) {
    const trimmed = rec.replace(/^\s+/, "");
    if (!trimmed) continue;
    const [sha, author, committer, authorDate, committerDate, message] = trimmed.split("\0");
    const short = sha.slice(0, 10);
    commits++;
    findings.push(...scanText(author, `commit ${short} author`, ctx));
    findings.push(...scanText(committer, `commit ${short} committer`, ctx));
    findings.push(...zoneFindings(authorDate.split(" ")[1], `commit ${short} author`, ctx));
    findings.push(...zoneFindings(committerDate.split(" ")[1], `commit ${short} committer`, ctx));
    findings.push(...scanText(message ?? "", `commit ${short} message`, ctx));
  }

  // Every path ever added or changed on any ref.
  const names = git(root, ["log", "--all", "--format=", "--name-only", "--diff-filter=ACMRT", "-z"]).toString("utf8");
  const paths = new Set(names.split(/[\0\n]/).filter(Boolean));
  for (const p of paths) findings.push(...scanPath(p, ctx));

  // Every blob reachable from any ref, scanned once under the path it was first seen at.
  const objects = git(root, ["rev-list", "--objects", "--all"]).toString("utf8").split("\n").filter(Boolean);
  const candidates = objects.map((l) => ({ sha: l.slice(0, 40), path: l.slice(41) })).filter((o) => o.path);
  const kinds = git(root, ["cat-file", "--batch-check=%(objectname) %(objecttype)"], candidates.map((c) => c.sha).join("\n") + "\n")
    .toString("utf8")
    .split("\n")
    .filter(Boolean)
    .map((l) => l.split(" "));
  const isBlob = new Set(kinds.filter(([, t]) => t === "blob").map(([s]) => s));
  const blobs = candidates.filter((c) => isBlob.has(c.sha));
  const contents = readObjects(root, blobs.map((b) => b.sha));
  for (const b of blobs) {
    const buf = contents.get(b.sha) ?? Buffer.alloc(0);
    const where = `${b.path}@${b.sha.slice(0, 10)}`;
    if (buf.length > ctx.maxBytes && !allowed(ctx.allow, "size", b.path)) findings.push({ where, rule: "size", detail: `${buf.length} bytes > ${ctx.maxBytes}` });
    const binary = isBinary(buf);
    for (const f of scanText(buf.toString(binary ? "latin1" : "utf8"), b.path, { ...ctx, binary })) {
      findings.push({ ...f, where: f.where.replace(b.path, where) });
    }
  }
  return { findings, scanned: blobs.length, commits };
}

// ---------------------------------------------------------------------------
// CLI
// ---------------------------------------------------------------------------
function parseArgs(argv) {
  const opts = { mode: "staged", publicOnly: false, quiet: false, maxBytes: DEFAULT_MAX_BYTES };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--staged" || a === "--all" || a === "--history") opts.mode = a.slice(2);
    else if (a === "--message") {
      opts.mode = "message";
      opts.messageFile = argv[++i];
    } else if (a === "--public-only") opts.publicOnly = true;
    else if (a === "--quiet") opts.quiet = true;
    else if (a === "--patterns") opts.patterns = argv[++i];
    else if (a === "--root") opts.root = argv[++i];
    else if (a === "--max-bytes") opts.maxBytes = Number(argv[++i]);
    else if (a === "--help" || a === "-h") opts.help = true;
    else throw new Error(`unknown argument: ${a}`);
  }
  if (opts.mode === "message" && !opts.messageFile) throw new Error("--message needs a file");
  if (!Number.isFinite(opts.maxBytes) || opts.maxBytes <= 0) throw new Error("--max-bytes needs a positive number");
  return opts;
}

export function main(argv = process.argv.slice(2)) {
  let opts;
  try {
    opts = parseArgs(argv);
  } catch (e) {
    console.error(`scrub-check: ${e.message}`);
    return 2;
  }
  if (opts.help) {
    console.log("usage: node scripts/scrub-check.mjs [--staged|--all|--history|--message <file>] [--public-only] [--patterns <file>] [--max-bytes <n>] [--root <dir>] [--quiet]");
    return 0;
  }

  let root;
  try {
    root = git(resolve(opts.root ?? process.cwd()), ["rev-parse", "--show-toplevel"]).toString("utf8").trim();
  } catch (e) {
    console.error(`scrub-check: not a git repository (${e.message})`);
    return 2;
  }

  // Private patterns: fail closed when missing, unless explicitly public-only.
  let patterns = [];
  if (!opts.publicOnly) {
    const file = opts.patterns ?? defaultPatternFile();
    if (!existsSync(file)) {
      console.error("scrub-check: private pattern file not found. Set RHYTHM_SCRUB_PATTERNS to its path,");
      console.error("             or pass --public-only to run the public rules alone (never before a push).");
      return 2;
    }
    const parsed = parsePatterns(readFileSync(file, "utf8"));
    if (parsed.errors.length) {
      for (const e of parsed.errors) console.error(`scrub-check: ${e}`);
      return 2;
    }
    if (parsed.patterns.length === 0) {
      console.error("scrub-check: private pattern file has no patterns; refusing to run as if it were complete.");
      return 2;
    }
    patterns = parsed.patterns;
  }

  const allowFile = join(root, "scripts", "scrub-allow.txt");
  const { entries: allow, errors } = existsSync(allowFile) ? parseAllowlist(readFileSync(allowFile, "utf8")) : { entries: [], errors: [] };
  if (errors.length) {
    for (const e of errors) console.error(`scrub-check: ${e}`);
    return 2;
  }

  const ctx = { patterns, allow, maxBytes: opts.maxBytes, zones: !opts.publicOnly };
  let result;
  try {
    if (opts.mode === "staged") result = runStaged(root, ctx);
    else if (opts.mode === "all") result = runAll(root, ctx);
    else if (opts.mode === "history") result = runHistory(root, ctx);
    else result = runMessage(root, opts.messageFile, ctx);
  } catch (e) {
    console.error(`scrub-check: ${e.message}`);
    return 2;
  }

  const scope = opts.publicOnly ? "public rules only" : `public rules + ${patterns.length} private patterns`;
  const what = opts.mode === "history" ? `${result.scanned} blobs, ${result.commits ?? 0} commits` : `${result.scanned} item(s)`;
  if (result.findings.length === 0) {
    if (!opts.quiet) console.log(`scrub-check --${opts.mode}: clean (${what}; ${scope})`);
    return 0;
  }
  console.error(`scrub-check --${opts.mode}: ${result.findings.length} finding(s) (${what}; ${scope})`);
  for (const f of result.findings) console.error(`  ${f.where}  ${f.rule}  ${f.detail}`);
  console.error("Fix the content, or for a reviewed false positive add a line to scripts/scrub-allow.txt with a reason.");
  return 1;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = main();
}
