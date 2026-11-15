// Tests for scrub-check. Every sample is synthetic and assembled at runtime, so this file
// itself never contains a secret-shaped string or a private identifier.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { after, describe, test } from "node:test";
import { fileURLToPath } from "node:url";

import {
  FILE_RULES,
  SECRET_RULES,
  assignedSecret,
  entropy,
  globToRegExp,
  parseAllowlist,
  parsePatterns,
  scanPath,
  scanText,
} from "./scrub-check.mjs";

const SCRIPT = join(dirname(fileURLToPath(import.meta.url)), "scrub-check.mjs");
const ctx = { patterns: [], allow: [], maxBytes: 1024 * 1024 };
const rulesHit = (line) => scanText(line, "x.txt", ctx).map((f) => f.rule);

// Synthetic values, assembled so the source never holds them literally.
const alnum = (n, seed = 7) => {
  const chars = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
  let s = "";
  let x = seed;
  for (let i = 0; i < n; i++) {
    x = (x * 1103515245 + 12345) % 2147483648;
    s += chars[x % chars.length];
  }
  return s;
};
const sample = {
  privateKey: ["-----BEGIN", "RSA", "PRIVATE", "KEY-----"].join(" "),
  githubToken: "gh" + "p_" + alnum(36),
  githubPat: "github" + "_pat_" + alnum(40),
  skKey: "s" + "k-" + "proj-" + alnum(40),
  slack: "xo" + "xb-" + "1234567890-" + alnum(24),
  aws: "AK" + "IA" + "ABCDEFGHIJKLMNOP".replace(/./g, (c, i) => (i % 3 ? c : String(i % 10))),
  google: "AI" + "za" + alnum(35),
  jwt: ["ey" + "J" + alnum(20), "ey" + "J" + alnum(30), alnum(30)].join("."),
  bearer: "Authorization: " + "Bea" + "rer " + alnum(40),
  assignment: "api_key = " + '"' + alnum(32) + '"',
  url: "postgres" + "://alex:" + alnum(12) + "@db.internal.test:5432/rhythm",
  windows: "C" + ":\\" + "Users\\" + "alex\\code\\thing",
  digest: "image: example/app" + "@sha" + "256:" + "ab12".repeat(16),
};

describe("secret rules", () => {
  test("every rule id is unique", () => {
    const ids = SECRET_RULES.map((r) => r.id);
    assert.equal(new Set(ids).size, ids.length);
  });

  for (const [name, rule] of [
    ["privateKey", "secret:private-key"],
    ["githubToken", "secret:github-token"],
    ["githubPat", "secret:github-pat"],
    ["skKey", "secret:sk-key"],
    ["slack", "secret:slack-token"],
    ["aws", "secret:aws-access-key"],
    ["google", "secret:google-api-key"],
    ["jwt", "secret:jwt"],
    ["bearer", "secret:bearer"],
    ["assignment", "secret:assignment"],
    ["url", "secret:url-credentials"],
    ["windows", "path:windows-user"],
    ["digest", "release:image-digest"],
  ]) {
    test(`${rule} catches a synthetic ${name}`, () => {
      assert.ok(rulesHit(sample[name]).includes(rule), `${rule} missed: ${rulesHit(sample[name]).join(",")}`);
    });
  }

  test("benign lines pass", () => {
    for (const line of [
      "const token = request.headers.authorization;",
      "headers: { Authorization: `Bearer ${token}` },",
      "RHYTHM_APP_TOKEN_FILE=/run/secrets/app_token",
      "password_file: ./secrets/postgres_password",
      'token: "change-me-before-use-0000000000"',
      "secret: postgres.password.file",
      "postgres://rhythm@postgres:5432/rhythm",
      "The tokenizer splits words; see docs/configuration.md.",
      "id: 00000000-0000-4000-8000-000000000001",
    ]) {
      assert.deepEqual(rulesHit(line), [], line);
    }
  });

  test("findings redact what they matched", () => {
    const [finding] = scanText(sample.githubToken, "a.txt", ctx);
    assert.ok(!finding.detail.includes(sample.githubToken.slice(6)));
    assert.match(finding.detail, /\*\*\*\*$/);
  });

  test("assignedSecret needs length, mixed characters and entropy", () => {
    assert.equal(assignedSecret("short1"), false);
    assert.equal(assignedSecret("aaaaaaaaaaaaaaaaaaaa1"), false);
    assert.equal(assignedSecret("/run/secrets/app_token"), false);
    assert.equal(assignedSecret(alnum(32)), true);
    assert.ok(entropy(alnum(32)) > 3.5);
  });
});

describe("file rules", () => {
  const blocked = (p) => FILE_RULES.filter((r) => r.test(p)).map((r) => r.id);
  test("blocks secret, evidence and private files", () => {
    assert.deepEqual(blocked(".env"), ["file:env"]);
    assert.deepEqual(blocked("deploy/compose/.env.local"), ["file:env"]);
    assert.deepEqual(blocked("deploy/compose/secrets/app_token"), ["file:secrets-dir"]);
    assert.deepEqual(blocked("certs/server.pem"), ["file:pem"]);
    assert.deepEqual(blocked("docs/run.trx"), ["file:trx"]);
    assert.deepEqual(blocked("docs/latency-evidence.json"), ["file:evidence-json"]);
    assert.deepEqual(blocked("docs/garmin-openapi.json"), ["file:garmin-openapi"]);
    assert.deepEqual(blocked("tools/thing_release.py"), ["file:release-chain"]);
    assert.deepEqual(blocked("PLAN.md"), ["file:private-plan"]);
  });
  test("allows ordinary files", () => {
    for (const p of ["deploy/compose/.env.example", "docs/PLAN-notes.md", "engine/src/Core/SecretFiles.cs", "app/src/model.ts", "contracts/rhythm.schema.json"]) {
      assert.deepEqual(blocked(p), [], p);
    }
  });
});

describe("pattern and allowlist parsing", () => {
  test("patterns skip comments and blank lines and match case-insensitively", () => {
    const { patterns, errors } = parsePatterns("# comment\n\nzq-marker-\\d+\n  # indented comment\n");
    assert.deepEqual(errors, []);
    assert.equal(patterns.length, 1);
    assert.ok(patterns[0].re.test("see ZQ-MARKER-42 here"));
  });
  test("an invalid pattern is an error, without echoing it", () => {
    const { errors } = parsePatterns("ok\n(unclosed\n");
    assert.equal(errors.length, 1);
    assert.ok(!errors[0].includes("unclosed"));
  });
  test("private findings never include the pattern or the matched text", () => {
    const { patterns } = parsePatterns("zq-marker-\\d+");
    const findings = scanText("hello zq-marker-99", "a.md", { ...ctx, patterns });
    assert.equal(findings.length, 1);
    assert.equal(findings[0].rule, "private");
    assert.ok(!JSON.stringify(findings).includes("zq-marker"));
  });
  test("private patterns also check paths", () => {
    const { patterns } = parsePatterns("zq-marker");
    assert.equal(scanPath("docs/zq-marker.md", { patterns, allow: [] })[0].rule, "private");
  });
  test("allowlist entries need a reason and cannot cover private matches", () => {
    const ok = parseAllowlist("size app/package-lock.json -- generated lockfile, reviewed\n");
    assert.deepEqual(ok.errors, []);
    assert.equal(ok.entries[0].reason, "generated lockfile, reviewed");
    assert.equal(parseAllowlist("size app/package-lock.json\n").errors.length, 1);
    assert.equal(parseAllowlist("private docs/*.md -- please let it through\n").errors.length, 1);
  });
  test("allowlisted rule and path suppress a finding", () => {
    const { entries } = parseAllowlist("secret:assignment docs/examples/** -- synthetic value in a doc\n");
    assert.deepEqual(scanText(sample.assignment, "docs/examples/a.md", { ...ctx, allow: entries }), []);
    assert.equal(scanText(sample.assignment, "docs/other.md", { ...ctx, allow: entries }).length, 1);
  });
  test("glob semantics", () => {
    assert.ok(globToRegExp("docs/*.md").test("docs/a.md"));
    assert.ok(!globToRegExp("docs/*.md").test("docs/x/a.md"));
    assert.ok(globToRegExp("docs/**/*.md").test("docs/a.md"));
    assert.ok(globToRegExp("docs/**/*.md").test("docs/x/y/a.md"));
  });
});

describe("git modes", () => {
  const dirs = [];
  after(() => {
    for (const d of dirs) rmSync(d, { recursive: true, force: true });
  });

  const fresh = () => {
    const dir = mkdtempSync(join(tmpdir(), "scrub-test-"));
    dirs.push(dir);
    const repo = join(dir, "repo");
    mkdirSync(repo);
    const patterns = join(dir, "patterns.txt");
    writeFileSync(patterns, "# synthetic private patterns\nzq-marker-\\d+\nsam\\.example\\.invalid\n");
    // Commits here are in UTC (the time-zone rule); dates: [author, committer] overrides it.
    const g = (...args) => {
      const r = spawnSync("git", ["-C", repo, ...args], { encoding: "utf8", env: { ...process.env, TZ: "UTC" } });
      assert.equal(r.status, 0, r.stderr);
      return r.stdout;
    };
    g("init", "-q", "-b", "main");
    g("config", "user.name", "Test");
    g("config", "user.email", "test@example.invalid");
    g("config", "commit.gpgsign", "false");
    g("config", "core.hooksPath", "no-hooks");
    g("config", "core.autocrlf", "false");
    const write = (p, text) => {
      mkdirSync(dirname(join(repo, p)), { recursive: true });
      writeFileSync(join(repo, p), text);
    };
    const run = (...args) => {
      const r = spawnSync(process.execPath, [SCRIPT, "--root", repo, ...args], {
        encoding: "utf8",
        env: { ...process.env, TZ: "UTC", RHYTHM_SCRUB_PATTERNS: patterns, ...run.env },
      });
      return { code: r.status, out: r.stdout + r.stderr };
    };
    return { repo, g, write, run, patterns };
  };

  test("--staged passes clean files and blocks private and secret content", () => {
    const { g, write, run } = fresh();
    write("README.md", "Rhythm of Life\n");
    g("add", "README.md");
    assert.equal(run("--staged").code, 0);
    write("docs/note.md", "contact zq-marker-12\n");
    write("src/config.ts", sample.assignment + "\n");
    g("add", ".");
    const r = run("--staged");
    assert.equal(r.code, 1);
    assert.match(r.out, /docs\/note\.md:1\s+private\s+private pattern #1/);
    assert.match(r.out, /src\/config\.ts:1\s+secret:assignment/);
    assert.ok(!r.out.includes("zq-marker"), "output must not echo private text");
  });

  test("--staged blocks file names and the size guard", () => {
    const { g, write, run } = fresh();
    write(".env", "A=1\n");
    write("big.txt", "x".repeat(2048));
    g("add", ".");
    const r = run("--staged", "--max-bytes", "1024");
    assert.equal(r.code, 1);
    assert.match(r.out, /\.env\s+file:env/);
    assert.match(r.out, /big\.txt\s+size/);
  });

  test("--staged checks the commit identity", () => {
    const { g, write, run } = fresh();
    g("config", "user.email", "alex@sam.example.invalid");
    write("a.txt", "ok\n");
    g("add", "a.txt");
    const r = run("--staged");
    assert.equal(r.code, 1);
    assert.match(r.out, /commit author\S*\s+private/);
  });

  test("--staged and --history refuse commit dates outside UTC", () => {
    const { repo, g, write, run } = fresh();
    write("a.txt", "ok\n");
    g("add", "a.txt");
    assert.equal(run("--staged").code, 0);
    const offset = { GIT_AUTHOR_DATE: "2026-10-01T10:00:00+01:00", GIT_COMMITTER_DATE: "2026-10-01T10:00:00+01:00" };
    run.env = offset;
    const staged = run("--staged");
    assert.equal(staged.code, 1);
    assert.match(staged.out, /commit author\s+identity:timezone/);
    run.env = undefined;
    const r = spawnSync("git", ["-C", repo, "commit", "-q", "-m", "first"], { encoding: "utf8", env: { ...process.env, ...offset } });
    assert.equal(r.status, 0, r.stderr);
    const history = run("--history");
    assert.equal(history.code, 1);
    assert.match(history.out, /commit [0-9a-f]{10} author\s+identity:timezone/);
    assert.match(history.out, /commit [0-9a-f]{10} committer\s+identity:timezone/);
    // Without the private pattern file (a contributor, CI) a commit's time zone is its author's to show.
    run.env = offset;
    assert.equal(run("--staged", "--public-only").code, 0);
    run.env = undefined;
    assert.equal(run("--history", "--public-only").code, 0);
  });

  test("--history finds what --all no longer sees, including messages", () => {
    const { g, write, run } = fresh();
    write("a.txt", "zq-marker-1\n");
    g("add", ".");
    g("commit", "-q", "-m", "first");
    g("rm", "-q", "a.txt");
    write("b.txt", "clean\n");
    g("add", ".");
    g("commit", "-q", "-m", "second mentions zq-marker-2");
    assert.equal(run("--all").code, 0);
    const r = run("--history");
    assert.equal(r.code, 1);
    assert.match(r.out, /a\.txt@[0-9a-f]{10}:1\s+private/);
    assert.match(r.out, /commit [0-9a-f]{10} message:1\s+private/);
    assert.ok(!r.out.includes("zq-marker"));
  });

  test("--history is clean on a clean repo and on an empty one", () => {
    const { g, write, run } = fresh();
    assert.equal(run("--history").code, 0);
    write("a.txt", "clean\n");
    g("add", ".");
    g("commit", "-q", "-m", "clean start");
    assert.equal(run("--history").code, 0);
  });

  test("--message checks a commit message file", () => {
    const { repo, run } = fresh();
    const msg = join(repo, "..", "MSG");
    writeFileSync(msg, "Add notes for zq-marker-5\n# comment lines are ignored\n");
    assert.equal(run("--message", msg).code, 1);
    writeFileSync(msg, "Add notes\n# zq-marker-6 in a comment line\n");
    assert.equal(run("--message", msg).code, 0);
  });

  test("fails closed without the pattern file unless --public-only", () => {
    const { repo } = fresh();
    const env = { ...process.env, RHYTHM_SCRUB_PATTERNS: join(repo, "missing.txt") };
    const closed = spawnSync(process.execPath, [SCRIPT, "--root", repo, "--all"], { encoding: "utf8", env });
    assert.equal(closed.status, 2);
    const open = spawnSync(process.execPath, [SCRIPT, "--root", repo, "--all", "--public-only"], { encoding: "utf8", env });
    assert.equal(open.status, 0);
  });

  test("an allowlist entry without a reason is a configuration error", () => {
    const { g, write, run } = fresh();
    write("scripts/scrub-allow.txt", "size big.txt\n");
    g("add", ".");
    assert.equal(run("--staged").code, 2);
  });
});
