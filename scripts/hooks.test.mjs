// The session hooks for coding agents (SB-P7-02; harness/hooks/README.md): the redaction fixture, the gate, each agent's
// documented events, off by default, and refused while the endpoint is off. Every secret here is synthetic and assembled at
// runtime, so this file itself holds none. The engine's own door is tested against the real hook in the engine's
// SessionHookTests; here the engine is a fake local server.
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, realpathSync, rmSync, statSync, utimesSync, writeFileSync } from "node:fs";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { after, describe, test } from "node:test";
import { fileURLToPath } from "node:url";

import { AGENTS, readEvent } from "../harness/hooks/agents.mjs";
import { captureUrl, fate } from "../harness/hooks/core.mjs";
import { assess, REASONS, signals } from "../harness/hooks/gate.mjs";
import { redact, REDACTED, secretValues, tokenLike } from "../harness/hooks/redact.mjs";
import { check as schemaCheck } from "./json-schema.mjs";

const here = (path) => fileURLToPath(new URL(path, import.meta.url));
const HOOK = here("../harness/hooks/rhythm-hook.mjs");
const SCHEMAS = here("../contracts/mcp/");
const CAPTURED = JSON.parse(readFileSync(here("../contracts/fixtures/capture.example.json"), "utf8"));

// A repeatable generator: the same synthetic values on every run, so a miss is a real one and not a flake.
const alnum = (n, seed = 7, chars = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789") => {
  let s = "";
  for (let block = 0; s.length < n; block++)
    for (const byte of createHash("sha256").update(`${seed}:${block}`).digest()) s += chars[byte % chars.length];
  return s.slice(0, n);
};
const upperDigits = (n, seed) => alnum(n, seed, "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567");
const hex = (n, seed) => alnum(n, seed, "0123456789abcdef");
const base64 = (n, seed) => alnum(n, seed, "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789+/");
const base64url = (n, seed) => alnum(n, seed, "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_");
const password = alnum(14, 3);

// The redaction fixture: [what it is, the secret that must go, the line it sits in]. Built here at runtime.
const JOINED = (...parts) => parts.join("");
const PEM_BODY = `${alnum(64, 11)}\n${alnum(64, 12)}`;
const FIXTURE = [
  ["an OpenAI-shaped key", JOINED("s", "k-proj-", alnum(40, 21))],
  ["an Anthropic-shaped key", JOINED("s", "k-ant-api03-", alnum(40, 22))],
  ["a GitHub token", JOINED("gh", "p_", alnum(36, 23))],
  ["a GitHub app token", JOINED("gh", "s_", alnum(36, 24))],
  ["a GitHub fine-grained token", JOINED("github", "_pat_", alnum(40, 25))],
  ["a GitLab token", JOINED("gl", "pat-", alnum(20, 26))],
  ["a Slack token", JOINED("xo", "xb-1234567890-", alnum(24, 27))],
  ["a Slack webhook", JOINED("hooks.", "slack.com/services/T", alnum(8, 28), "/B", alnum(8, 29), "/", alnum(24, 30))],
  ["an AWS access key", JOINED("AK", "IA", upperDigits(16, 31))],
  ["an AWS secret key", base64(40, 32), (s) => `aws_secret_access_key = ${s}`],
  ["a Google API key", JOINED("AI", "za", alnum(35, 33))],
  ["a Google client secret", JOINED("GOC", "SPX-", alnum(28, 34))],
  ["a Google refresh token", JOINED("1/", "/0", alnum(40, 35))],
  ["a Google access token", JOINED("ya", "29.", alnum(40, 36))],
  ["a Stripe key", JOINED("s", "k_live_", alnum(24, 37))],
  ["a webhook signing secret", JOINED("wh", "sec_", alnum(32, 38))],
  ["an npm token", JOINED("np", "m_", alnum(36, 39))],
  ["a PyPI token", JOINED("py", "pi-", alnum(40, 40))],
  ["a Hugging Face token", JOINED("h", "f_", alnum(34, 41))],
  ["a SendGrid key", JOINED("S", "G.", alnum(22, 42), ".", alnum(43, 43))],
  ["a Tailscale key", JOINED("ts", "key-auth-", alnum(30, 44))],
  ["a JWT", [JOINED("ey", "J", alnum(20, 45)), JOINED("ey", "J", alnum(30, 46)), alnum(30, 47)].join(".")],
  ["a base64 JSON token", JOINED("ey", "J", base64(64, 48))],
  ["a PEM private key block", PEM_BODY, (s) => `${["-----BEGIN", "RSA", "PRIVATE", "KEY-----"].join(" ")}\n${s}\n${["-----END", "RSA", "PRIVATE", "KEY-----"].join(" ")}`],
  ["a PEM block cut off before its end", alnum(64, 49), (s) => `${["-----BEGIN", "PRIVATE", "KEY-----"].join(" ")}\n${s}`],
  ["a bearer header", alnum(40, 50), (s) => `Authorization: ${JOINED("Bea", "rer ")}${s}`],
  ["a bearer value in words", alnum(24, 51), (s) => `It sent ${JOINED("Bea", "rer ")}${s} to the service.`],
  ["a basic header", base64(24, 52), (s) => `Authorization: Basic ${s}`],
  ["an API key header", alnum(20, 53), (s) => `x-api-key: ${s}`],
  ["a cookie", alnum(24, 54), (s) => `Cookie: session=${s}`],
  ["a password in a Postgres URL", password, (s) => JOINED("postgres", "://alex:", s, "@db.example.invalid:5432/rhythm")],
  ["a password holding an @ in a URL", `${alnum(6, 91)}@${alnum(6, 92)}`, (s) => JOINED("postgres", "://alex:", s, "@db.example.invalid:5432/rhythm")],
  ["a bearer value in small letters", alnum(24, 93), (s) => `It sent ${JOINED("bea", "rer ")}${s} to the service.`],
  ["a password in a Mongo URL",alnum(16, 55), (s) => JOINED("mongodb+srv", "://alex:", s, "@cluster.example.invalid/rhythm")],
  ["a password in a Redis URL", alnum(16, 56), (s) => JOINED("redis", "://:", s, "@cache.example.invalid:6379")],
  ["a token as a URL's user", alnum(40, 57), (s) => JOINED("https", "://", s, "@git.example.invalid/demo.git")],
  ["a token in a URL's query", alnum(12, 58), (s) => JOINED("https", "://api.example.invalid/v1?access_", "token=", s, "&page=2")],
  ["a signature in a URL's query", alnum(20, 59), (s) => JOINED("https", "://blob.example.invalid/f?sv=2024&", "sig=", s)],
  ["an ADO connection string's password", alnum(12, 60), (s) => JOINED("Server=db.example.invalid;Database=rhythm;User Id=alex;Pass", "word=", s, ";")],
  ["an Npgsql connection string's password", alnum(12, 61), (s) => JOINED("Host=db;Database=rhythm;Username=alex;Pass", "word=", s)],
  ["a short Pwd in a connection string", alnum(8, 62), (s) => JOINED("Server=db;Uid=alex;P", "wd=", s, ";")],
  ["an Azure storage account key", base64(44, 63), (s) => JOINED("DefaultEndpointsProtocol=https;AccountName=demo;Account", "Key=", s, ";")],
  ["an Azure shared access key", base64(44, 64), (s) => JOINED("Endpoint=sb://demo.example.invalid/;SharedAccess", "Key=", s)],
  ["a libpq connection string's password", alnum(12, 65), (s) => JOINED("host=db user=alex pass", "word=", s, " dbname=rhythm")],
  ["a JDBC URL's password", alnum(12, 66), (s) => JOINED("jdbc:postgresql://db/rhythm?user=alex&pass", "word=", s)],
  ["an exported password", alnum(12, 67), (s) => JOINED("export PGPASS", "WORD=", s)],
  ["a quoted password with spaces", "correct horse battery staple", (s) => JOINED('DB_PASS', 'WORD="', s, '"')],
  ["a password in JSON", alnum(12, 68), (s) => JOINED('"pass', 'word": "', s, '"')],
  ["a password in escaped JSON", alnum(12, 69), (s) => JOINED('\\"pass', 'word\\":\\"', s, '\\"')],
  ["a password in YAML", alnum(12, 70), (s) => JOINED("pass", "word: ", s)],
  ["an API key in code", alnum(20, 71), (s) => JOINED("api", "_key = '", s, "'")],
  ["a client secret", alnum(20, 72), (s) => JOINED("client", "_secret: ", s)],
  ["a token variable", alnum(20, 73), (s) => JOINED("GITHUB", "_TOKEN=", s)],
  ["a password flag", alnum(12, 74), (s) => JOINED("--pass", "word=", s)],
  ["a token flag", alnum(20, 75), (s) => `--token ${s}`],
  ["a MySQL password", alnum(12, 76), (s) => `mysql -u alex -p${s} rhythm`],
  ["an sshpass password", alnum(12, 77), (s) => `sshpass -p ${s} ssh demo.example.invalid`],
  ["a curl user's password", alnum(12, 78), (s) => `curl -u alex:${s} https://example.invalid`],
  ["a password said in words", alnum(9, 79), (s) => `The test password is ${s}, which I set earlier.`],
  ["a token said in words, quoted", "plainword", (s) => `The staging token is "${s}" for now.`],
  ["a password changed to", alnum(9, 80), (s) => `I changed the password to ${s} for the demo.`],
  ["an engine token (base64url, 43)", base64url(43, 81)],
  ["a long hex key", hex(64, 82)],
  ["a mixed-case random run", alnum(32, 83)],
  ["a lowercase random run", alnum(40, 84, "abcdefghijklmnopqrstuvwxyz0123456789")],
  ["a base64 run with its padding", `${base64(46, 85)}==`],
  // A run right after an equals sign, under a name that says nothing.
  ["a random value under a name that says nothing", alnum(40, 120), (s) => `SESSION=${s}`],
  ["a random value under a short name", alnum(40, 121), (s) => `sid=${s}`],
  ["a random value exported", alnum(40, 122), (s) => `export GITHUB_PAT=${s}`],
  ["a random code in a URL's query", alnum(40, 123), (s) => JOINED("https", "://api.example.invalid/cb?code=", s, "&state=x")],
  ["a random value ending a URL's query", alnum(40, 124), (s) => JOINED("https", "://api.example.invalid/cb?session=", s)],
  // Under a telling name, and only beginning like what names a secret.
  ["a quoted password beginning with a dollar", alnum(20, 125), (s) => JOINED('pass', 'word: "$', s, '"')],
  ["a bare password beginning with a dollar", alnum(20, 126), (s) => JOINED("pass", "word=$", s)],
  ["a password beginning like an example", alnum(20, 127), (s) => JOINED("PASS", "WORD=example", s)],
  ["a token beginning like a sample", alnum(20, 128), (s) => JOINED("API_TOK", "EN=sample", s)],
  ["a secret beginning like a dummy", alnum(20, 129), (s) => JOINED('sec', 'ret="dummy', s, '"')],
  ["a password beginning with a brace", alnum(20, 130), (s) => JOINED('pass', 'word: "{', s, '"')],
  ["a password beginning with an angle", alnum(20, 131), (s) => JOINED('pass', 'word: "<', s, '"')],
  ["a password beginning with an equals sign", alnum(20, 132), (s) => JOINED('pass', 'word: "=', s, '"')],
  ["a token beginning with x's", alnum(20, 133), (s) => JOINED("tok", "en=xxx", s)],
  ["a password beginning like your-", alnum(20, 134), (s) => JOINED("pass", "word=your-", s)],
  ["a password that looks like a call", alnum(20, 135), (s) => JOINED("pass", "word=", s, "(1")],
  ["a URL's password that looks like a call", alnum(20, 136), (s) => JOINED("postgres", "://alex:", s, "(1@db.example.invalid/x")],
  // Quoted with spaces, on a command line and in words.
  ["a flag's value in double quotes", "correct horse battery", (s) => JOINED("--pass", `word "${s}"`)],
  ["a flag's value in single quotes", "correct horse battery", (s) => JOINED("--pass", `word '${s}'`)],
  ["a flag's value in backticks", "correct horse battery", (s) => JOINED("--pass", `word \`${s}\``)],
  ["a passphrase in quotes", "correct horse battery staple", (s) => `The passphrase is "${s}".`],
  ["a password in single quotes", "open sesame please", (s) => `The password was '${s}'.`],
  // A bare value runs on past what only ends one when it is closing something.
  ["a bare value holding a comma", `ab${alnum(20, 137)},cd${alnum(20, 138)}`, (s) => JOINED("pass", "word=", s)],
  ["a bare value holding an ampersand", `ab${alnum(20, 139)}&cd${alnum(20, 140)}`, (s) => JOINED("pass", "word=", s)],
  ["a bare value holding a bracket", `ab${alnum(20, 141)})cd${alnum(20, 142)}`, (s) => JOINED("pass", "word=", s)],
  ["a bare value holding a square bracket", `ab${alnum(20, 143)}]cd${alnum(20, 144)}`, (s) => JOINED("pass", "word=", s)],
  // A plain value under a password's own name is its value, after a colon too.
  ["a plain word as a YAML password", "letmein", (s) => JOINED("db_pass", "word: ", s)],
  ["a capitalised word as a YAML password", "Letmein", (s) => JOINED("pass", "word: ", s)],
  ["digits as a YAML password", "424242", (s) => JOINED("pass", "word: ", s)],
  ["seven digits as a YAML password", "8675309", (s) => JOINED("DB_PASS", "WORD: ", s)],
  // Names that say they hold a secret, whatever the case, and however short the value.
  ...[
    ["a camelCase secret key", (s) => JOINED("secret", "Key=", s)],
    ["a quoted camelCase secret key", (s) => JOINED('"secret', 'Key": "', s, '"')],
    ["a camelCase access key", (s) => JOINED("access", "Key=", s)],
    ["a camelCase private key", (s) => JOINED("private", "Key: ", s)],
    ["a camelCase auth key", (s) => JOINED("auth", "Key=", s)],
    ["a camelCase signing key", (s) => JOINED("signing", "Key=", s)],
    ["a camelCase encryption key", (s) => JOINED("encryption", "Key=", s)],
    ["a camelCase master key", (s) => JOINED("master", "Key=", s)],
    ["a long camelCase access key", (s) => JOINED("secretAccess", "Key: ", s)],
    ["an all-lower-case access key", (s) => JOINED("access", "key=", s)],
    ["an all-lower-case secret key", (s) => JOINED("secret", "key=", s)],
    ["a database's PASS", (s) => JOINED("DB_", "PASS=", s)],
    ["Postgres's PGPASS", (s) => JOINED("PG", "PASS=", s)],
    ["a name that is only pass", (s) => JOINED("pa", "ss=", s)],
    ["an XML password element", (s) => JOINED("<pass", "word>", s, "</pass", "word>")],
    ["an XML element with a prefix", (s) => JOINED("<db.pass", "word>", s, "</db.pass", "word>")],
    [".NET's appSettings password", (s) => JOINED('<add key="pass', 'word" value="', s, '"/>')],
    [".NET's appSettings api key", (s) => JOINED("<add key='Api", "Key' value='", s, "'/>")],
  ].map(([name, line], i) => [name, alnum(14, 300 + i), line]),
];

// What only names a secret, or looks like one and is not: left exactly as it is.
const LEFT_ALONE = [
  "123e4567-e89b-12d3-a456-426614174000",
  hex(40, 90),
  "the commit abc1234 landed",
  "/usr/local/lib/node_modules/some-package/dist/index.js",
  "engine/tests/Knowledge.Tests/CaptureEndpointTests.cs",
  "C:/Work/garden/app/src/index.ts",
  "CaptureEndpointTests_A_body_with_text_and_its_fields_is_a_capture",
  "ThisIsAVeryLongCamelCaseIdentifierNameForAHandler2",
  "process.env.API_TOKEN",
  "$PGPASSWORD",
  "export GITHUB_TOKEN=$(cat token.txt)",
  "token=${GITHUB_TOKEN}",
  "password=<password>",
  "api_key: <your key>",
  '"password": "<your password>"',
  "the token is invalid",
  "the password is wrong.",
  "token: expired",
  "max_tokens: 4096",
  "input_tokens=123",
  "token_count = 17",
  "primary_key: id",
  'sort_key = "created_at"',
  "--token is required",
  "Bearer tokens expire",
  "Bearer authentication is on",
  "Basic authentication stays off",
  "Authorization: the app checks the role of each caller",
  "Cookie: the session is set by the app",
  "const token = readToken(config);",
  "password = getPassword()",
  "secret: string",
  'token = ""',
  JOINED("https", "://example.invalid/path?page=2"),
  JOINED("postgres", "://db.example.invalid:5432/rhythm"),
  "git@example.invalid:org/repo.git",
  "version v1.2.3 of the package",
  "Ordinary prose with no secret in it, about tokens, keys and passwords in general.",
  JOINED("pass", "word=$DB_PASSWORD"),
  JOINED("pass", 'word: "${DB_PASSWORD}"'),
  JOINED("pass", "word={dbPassword}"),
  JOINED("pass", 'word=$env:DB_PASSWORD'),
  "token={props.token}",
  "token: {{ secrets.API_TOKEN }}",
  "credentials: {",
  JOINED("API_TOK", "EN=example-token"),
  JOINED("pass", "word: changeme"),
  JOINED("PASS", "WORD=CHANGE_ME"),
  JOINED("pass", 'word: "your-password"'),
  "secret: <YOUR_SECRET>",
  JOINED("pass", "word: ***"),
  "token: xxxxxxxx",
  '--token "$API_TOKEN"',
  "--token=$(cat token.txt)",
  JOINED("curl -H \"Authori", "zation: Bea", "rer $API_TOKEN\" https://api.example.invalid/x"),
  "if (token === expected) return;",
  "if (token===expected) return;",
  JOINED("def connect(pass", "word: str) -> None:"),
  'const secret = readFileSync(env.DEMO_SECRET_FILE, "utf8");',
  "var token = File.ReadAllText(file);",
  JOINED("var pass", 'word = Guid.NewGuid().ToString("N");'),
  JOINED('$"Username=alex;Pass', 'word={Principals.NewToken()}"'),
  'role == "ingest" ? $"plugin_{plugin}_token" : $"harness_{who}_token"',
  // A plain pass in prose, or a word that only ends in it.
  "all the tests pass: great",
  "the tests pass: 14",
  '"pass": 3, "fail": 0',
  "bypass=1",
  "compass: north",
  "the build did not pass, so it was run again",
  // What only names one in XML, and a key that holds no secret.
  JOINED("<pass", "word>$DB_PASSWORD</pass", "word>"),
  JOINED('<add key="pass', 'word" value="{dbPassword}"/>'),
  '<add key="theme" value="dark"/>',
];

// What a session hook saves is a transcript source's, so it never replaces a memory from another source (SB-P5-04): the engine's
// two lists, which a test keeps equal, are read here, and every agent names one of them.
describe("each agent's source", () => {
  const listed = (path, pattern) => [...pattern.exec(readFileSync(here(path), "utf8"))[1].matchAll(/["']([a-z-]+)["']/g)].map((m) => m[1]);
  const sql = listed("../engine/migrations/063_duplicates.sql", /FUNCTION memory_transcript_sources\(\)[^\n]*\n SELECT ARRAY\[([^\]]*)\]/);
  const engine = listed("../engine/src/Core/Memories.cs", /TranscriptSources = \[([^\]]*)\]/);
  test("the migration and the engine list the same", () => assert.deepEqual(sql, engine));
  for (const [agent, { source }] of Object.entries(AGENTS)) test(`${agent} names one of them`, () => assert.ok(sql.includes(source), source));
  test("no two agents share one, so each keeps its own", () => assert.equal(new Set(Object.values(AGENTS).map((a) => a.source)).size, Object.keys(AGENTS).length));
});

const GOOD =
  "The flaky build was caused by a clock read in the seed step, because the fixture compared local time with UTC. Fixed by reading UTC in both places, and all tests pass now. Keep in mind that the seed step must not read the wall clock; the next step is to do the same in the import step.";

describe("the redaction fixture", () => {
  const lineOf = ([, secret, line]) => (line ? line(secret) : `It printed ${secret} in the log.`);

  for (const entry of FIXTURE) {
    const [name, secret] = entry;
    test(`removes ${name}`, () => {
      const { text, count } = redact(lineOf(entry));
      assert.ok(!text.includes(secret), text);
      assert.ok(text.includes(REDACTED), text);
      assert.ok(count >= 1);
      // No long piece of it is left behind either.
      if (secret.length >= 16) assert.ok(!text.includes(secret.slice(4, 16)), text);
    });
  }

  for (const line of LEFT_ALONE) {
    test(`leaves alone: ${line}`, () => assert.deepEqual(redact(line), { text: line, count: 0 }));
  }

  test("keeps what surrounds a secret: the user, host and name of a connection string", () => {
    const { text } = redact(JOINED("postgres", "://alex:", password, "@db.example.invalid:5432/rhythm"));
    assert.ok(text.includes("alex:") && text.includes("@db.example.invalid:5432/rhythm"));
    assert.ok(redact(JOINED("Server=db;User Id=alex;Pass", "word=", password, ";Database=rhythm")).text.includes(";Database=rhythm"));
  });

  test("what only names a secret, or is too short to be one, hides nothing after it", () => {
    const secret = alnum(12, 150);
    for (const first of ["$DB_PASSWORD", "${DB_PASSWORD}", "<password>", "getPassword()"]) {
      const { text } = redact(JOINED("pass", "word=", first, ",tok", "en=", secret));
      assert.ok(!text.includes(secret), text);
      assert.ok(text.startsWith(JOINED("pass", "word=", first, ",")), text);
    }
    const { text } = redact(JOINED("primary_key=id,sec", "ret=", secret));
    assert.equal(text, JOINED("primary_key=id,sec", "ret=", REDACTED));
  });

  test("a value goes whole: bare with what could end it inside it, or quoted with spaces", () => {
    const [a, b] = [alnum(20, 152), alnum(20, 153)];
    for (const inner of [",", "&", ")", "]", "}"])
      assert.equal(redact(JOINED("pass", "word=", a, inner, b)).text, JOINED("pass", "word=", REDACTED), inner);
    for (const [open, close] of [['"', '"'], ["'", "'"], ["`", "`"]]) {
      const value = `${open}correct horse battery${close}`;
      assert.equal(redact(JOINED("--pass", "word ", value)).text, JOINED("--pass", "word ", open, REDACTED, close));
      assert.equal(redact(`The passphrase is ${value}.`).text, `The passphrase is ${open}${REDACTED}${close}.`);
    }
  });

  test("a bare value gives back what closes around it", () => {
    const secret = alnum(20, 151);
    assert.equal(redact(JOINED("call(tok", "en=", secret, ")")).text, JOINED("call(tok", "en=", REDACTED, ")"));
    assert.equal(redact(JOINED("tok", "en=", secret, ", then more")).text, JOINED("tok", "en=", REDACTED, ", then more"));
    assert.equal(redact(JOINED("[tok", "en=", secret, "]")).text, JOINED("[tok", "en=", REDACTED, "]"));
    assert.equal(redact(JOINED("{tok", "en=", secret, "}; next")).text, JOINED("{tok", "en=", REDACTED, "}; next"));
    assert.equal(redact(JOINED("tok", "en=", secret, "; next")).text, JOINED("tok", "en=", REDACTED, "; next"));
    // Two values one after the other are counted and taken as they come.
    const two = redact(JOINED("tok", "en=", secret, " pass", "word=", secret, "x"));
    assert.equal(two.text, JOINED("tok", "en=", REDACTED, " pass", "word=", REDACTED));
    assert.equal(two.count, 2);
  });

  test("a line redacted once is left as it is the second time, as the hook does before it sends", () => {
    for (const entry of FIXTURE) {
      const once = redact(lineOf(entry));
      assert.deepEqual(redact(once.text), { text: once.text, count: 0 }, entry[0]);
    }
  });

  test("takes out exact values of any shape, longest first", () => {
    const own = "hunter2-own";
    assert.equal(redact(`one ${own} and ${own}x`, { own: [own, `${own}x`] }).text, `one ${REDACTED} and ${REDACTED}`);
    assert.equal(redact(`one ${own}`, { own: [own] }).count, 1);
    assert.equal(redact("seven chars x1", { own: ["chars x"] }).count, 0, "a value too short to be one is left alone");
  });

  test("finds what the environment names as a secret, and not its folders", () => {
    const env = { MY_SERVICE_TOKEN: "abcdefghijkl", DB_PASSWORD: "correct-horse", OTHER_KEY: "shortvalue", NOTES: "not named as one at all", PWD: "/home/alex/code/garden", OLDPWD: "/home/alex", TOKEN_FILE: "/home/alex/token.txt", ANOTHER_SECRET: "two words here" };
    assert.deepEqual(secretValues(env).sort(), ["abcdefghijkl", "correct-horse"]);
  });

  test("tells a random token from a long name or path", () => {
    // It reads the shape alone, so a random value can look like words; an exact own token never relies on this.
    let caught = 0;
    for (let seed = 1; seed <= 1000; seed++) if (tokenLike(base64url(43, seed))) caught++;
    assert.ok(caught >= 990, `${caught} of 1000 engine-shaped tokens`);
    assert.ok(!tokenLike("ThisIsAVeryLongCamelCaseIdentifierNameForAHandler2"));
    assert.ok(!tokenLike("node_modules_some_package_with_a_long_name_v12"));
    assert.ok(!tokenLike("usr/local/share/applications/garden2/resources/app/index"));
    assert.ok(!tokenLike(hex(40, 3)), "a commit id");
  });

  test("a hostile text cannot stall the agent: every pattern stays quick at the size the gate reads", () => {
    const size = 64 * 1024;
    const fill = (piece) => piece.repeat(Math.ceil(size / piece.length)).slice(0, size);
    const pieces = ["password=", "password: ", ["-----BEGIN", "PRIVATE", "KEY-----"].join(" "), "-----BEGIN ", "a://b:", "a://", "curl ", "mysql ", "A1b", "`", "```\n", "<system-reminder>", "x ", "aaaa", "eyJ", "--token ", "Authorization: ", "token is ", "?token=", "=", "_-", "\\\"password\\\":"];
    const inputs = [
      ...pieces.map(fill),
      `${"\n".repeat(size - 1)}x`,
      `some words ${"\n".repeat(size - 22)} more words`,
      `token is ${".".repeat(size - 10)}x`,
      `password=a${",".repeat(size - 11)}x`,
      `--token "${"a".repeat(size - 9)}`,
      fill('--token "a '),
      fill("token is 'a "),
      fill("password=a,"),
      fill("password:a,"),
      fill("$"),
      fill("{"),
      fill("<"),
      `password=example${"a".repeat(size - 17)}!`,
      `password=example${"-a".repeat((size - 17) / 2)}!`,
      `password=$${"aB".repeat((size - 10) / 2)}`,
      `Authorization: "${"a ".repeat((size - 17) / 2)}`,
    ];
    for (const input of inputs) {
      const start = performance.now();
      assess(input, { minChars: 1, minSignals: 0 });
      assert.ok(performance.now() - start < 1500, `${JSON.stringify(input.slice(0, 24))} took ${performance.now() - start} ms`);
    }
  });
});

describe("the content gate", () => {
  const padded = (text) => `${text} ${"Plain words about what happened in this session, written as sentences. ".repeat(4)}`;

  test("keeps a message that says what was decided, why, and how it came out", () => {
    const verdict = assess(GOOD);
    assert.equal(verdict.keep, true);
    assert.equal(verdict.text, GOOD);
    assert.equal(verdict.score, 4);
    assert.deepEqual(signals(GOOD), ["cause", "outcome", "lesson", "next"]);
  });

  const refusals = [
    ["no text at all", undefined, "no_text"],
    ["a text that is not one", 17, "no_text"],
    ["blank", "   \n ", "no_text"],
    ["a short acknowledgement", "Done. All tests pass, because I fixed it.", "too_short"],
    ["a long message saying nothing lasting", "Here is the listing you asked for. ".repeat(12), "no_signal"],
    ["one signal family alone", padded("The build failed because of a clock read."), "no_signal"],
    ["a message that ends by asking", `${GOOD} Should I carry on with the import step?`, "question"],
    ["an instructions file echoed", `Contents of /home/alex/AGENTS.md (project instructions): ${GOOD}`, "instructions"],
    ["an instructions file by its header", `# AGENTS.md instructions for /home/alex\n\n${GOOD}`, "instructions"],
    ["output, not words", `${"{\"a\":1,\"b\":[2,3],\"c\":{\"d\":4}},\n".repeat(40)} because fixed`, "mostly_code"],
    ["nothing but a code block", `\`\`\`json\n${"{\"a\":1}\n".repeat(50)}\`\`\``, "no_text"],
    ["a code block and a few words", `Here it is:\n\`\`\`js\n${"const a = 1;\n".repeat(50)}\`\`\`\nDone.`, "too_short"],
  ];
  for (const [name, message, reason] of refusals) {
    test(`leaves out ${name}`, () => {
      const verdict = assess(message);
      assert.deepEqual(verdict, { keep: false, reason });
      assert.ok(REASONS[reason]);
    });
  }

  test("leaves out a message that is mostly secrets, and nothing of it is kept to send", () => {
    const secrets = Array.from({ length: 8 }, (_, i) => JOINED("gh", "p_", alnum(36, 100 + i)));
    assert.deepEqual(assess(`${secrets.join(" ")} ${GOOD.slice(0, 200)}`), { keep: false, reason: "mostly_secrets" });
    // A secret or two inside real prose is redacted and the prose kept.
    const kept = assess(`${GOOD} The key I used was ${secrets[0]}.`);
    assert.equal(kept.keep, true);
    assert.equal(kept.redactions, 1);
    assert.ok(!kept.text.includes(secrets[0]));
  });

  test("never hands on fenced code, where keys and logs live, and marks where it was", () => {
    const secret = alnum(40, 110);
    const verdict = assess(`${GOOD}\n\n\`\`\`sh\necho ${secret}\n\`\`\`\n\nThat was the whole change.`);
    assert.equal(verdict.keep, true);
    assert.ok(!verdict.text.includes(secret));
    assert.ok(verdict.text.includes("[code left out]"));
    assert.ok(verdict.text.endsWith("That was the whole change."));
    assert.ok(assess(`${GOOD}\n\n~~~\n${secret}`).text.endsWith("[code left out]"), "a fence never closed runs to the end");
    assert.ok(assess(`${GOOD}\n\n\`\`\`\n\`\`\`\nAfter an empty block.`).text.includes("After an empty block."));
  });

  test("drops context a client injected, whole, even when it never closes", () => {
    const injected = `<system-reminder>The standing rules, ${alnum(20, 111)}</system-reminder>`;
    assert.equal(assess(`${injected}\n${GOOD}`).text, GOOD);
    assert.equal(assess(`${GOOD}\n<command-name>/clear</command-name>`).text, GOOD);
    assert.equal(assess(`${GOOD}\n<system-reminder>the rest of the rules, never closed`).text, GOOD);
    // Markup that is not one of those wrappers is part of what was said.
    const button = `${GOOD} The <button> needs an accessible name.`;
    assert.equal(assess(button).text, button);
  });

  test("is never a whole session: one message, cut to maxChars at a sentence or paragraph", () => {
    const long = `${GOOD}\n\n`.repeat(40);
    const verdict = assess(long);
    assert.equal(verdict.keep, true);
    assert.ok(verdict.text.length <= 2000 + "\n\n[cut]".length);
    assert.ok(verdict.text.endsWith("\n\n[cut]"));
    assert.ok(verdict.text.slice(0, -7).endsWith("import step."));
    assert.ok(assess(GOOD, { maxChars: 200 }).text.length <= 207);
  });

  test("redacts before it cuts, so a cut leaves no half of a secret", () => {
    const secret = alnum(60, 112);
    const message = `${GOOD} `.repeat(4).slice(0, 1990) + secret + ` ${GOOD}`;
    const verdict = assess(message);
    assert.ok(!verdict.text.includes(secret.slice(0, 8)), verdict.text.slice(-60));
  });

  test("a message past the size it reads ends between words, so no secret reaches redaction in half", () => {
    const size = 64 * 1024;
    const key = JOINED("gh", "p_", alnum(36, 113));
    const head = `\`\`\`\n${"x = 1\n".repeat(10000)}\`\`\`\n${GOOD}\n`;
    // The fenced code comes out, so the end of what was read falls inside what is kept, and the key sits across that end.
    const message = `${head}${"y ".repeat(Math.floor((size - head.length - 18) / 2))}${key} and the rest.`;
    assert.ok(message.indexOf(key) < size && message.indexOf(key) + key.length > size);
    const verdict = assess(message, { maxChars: 8000 });
    assert.equal(verdict.keep, true);
    assert.ok(!verdict.text.includes(key.slice(0, 8)), verdict.text.slice(-60));
    assert.ok(!verdict.text.includes("and the rest"));
    // A text with no whitespace to end at is cut where it is, and is not kept whole.
    assert.ok(assess("x".repeat(size * 2), { minChars: 1, minSignals: 0 }).text.length <= 2000 + "\n\n[cut]".length);
  });

  test("its thresholds can be moved, and its text can always be stored", () => {
    assert.equal(assess("A short one.", { minChars: 5, minSignals: 0 }).keep, true);
    assert.equal(assess(GOOD, { minSignals: 5 }).reason, "no_signal");
    const odd = assess(`${GOOD}\0 half a pair \ud83d`);
    assert.ok(!odd.text.includes("\0"));
    assert.ok(odd.text.isWellFormed());
    assert.ok(assess(`${GOOD}🌱`.repeat(30), { maxChars: 201 }).text.isWellFormed());
  });
});

// Each agent's input as its own reference documents it (README.md names the page). Synthetic ids and folders.
const EVENTS = {
  "claude-code": {
    turn: (id, folder, text) => ({ session_id: id, transcript_path: join(folder, "transcript.jsonl"), cwd: folder, permission_mode: "default", hook_event_name: "Stop", stop_hook_active: false, last_assistant_message: text, background_tasks: [], session_crons: [] }),
    end: (id, folder) => ({ session_id: id, transcript_path: join(folder, "transcript.jsonl"), cwd: folder, hook_event_name: "SessionEnd", reason: "other" }),
  },
  codex: {
    turn: (id, folder, text) => ({ session_id: id, transcript_path: join(folder, "transcript.jsonl"), cwd: folder, hook_event_name: "Stop", model: "demo-model", turn_id: "turn-1", permission_mode: "default", stop_hook_active: false, last_assistant_message: text }),
    end: (id, folder) => ({ session_id: id, transcript_path: join(folder, "transcript.jsonl"), cwd: folder, hook_event_name: "SessionEnd", model: "demo-model", reason: "other" }),
  },
  cursor: {
    turn: (id, folder, text) => ({ conversation_id: id, generation_id: "gen-1", model: "demo-model", hook_event_name: "afterAgentResponse", cursor_version: "1.0", workspace_roots: [folder], user_email: null, transcript_path: null, text }),
    end: (id, folder) => ({ conversation_id: id, generation_id: "gen-1", model: "demo-model", hook_event_name: "sessionEnd", cursor_version: "1.0", workspace_roots: [folder], user_email: null, transcript_path: null, session_id: id, reason: "completed", duration_ms: 45000, is_background_agent: false, final_status: "completed" }),
  },
  "gemini-cli": {
    turn: (id, folder, text) => ({ session_id: id, transcript_path: join(folder, "transcript.json"), cwd: folder, hook_event_name: "AfterAgent", timestamp: "2026-10-12T09:00:00Z", prompt: "DEMO: fix the build", prompt_response: text, stop_hook_active: false }),
    end: (id, folder) => ({ session_id: id, transcript_path: join(folder, "transcript.json"), cwd: folder, hook_event_name: "SessionEnd", timestamp: "2026-10-12T09:30:00Z", reason: "exit" }),
  },
};

describe("each agent's documented events", () => {
  for (const agent of Object.keys(AGENTS)) {
    test(`${agent}: a finished message and a session's end`, () => {
      const turn = readEvent(agent, EVENTS[agent].turn("s-1", "/work/garden", "DEMO words"));
      assert.deepEqual(turn, { kind: "turn", session: "s-1", folders: ["/work/garden"], text: "DEMO words", continuing: false });
      const end = readEvent(agent, EVENTS[agent].end("s-1", "/work/garden"));
      assert.deepEqual(end, { kind: "end", session: "s-1", folders: ["/work/garden"] });
    });
  }

  test("an agent that names the folder a session started in has it checked as well as where it is now", () => {
    const moved = (agent, env) => readEvent(agent, EVENTS[agent].turn("s", "/work/garden/src", "x"), env).folders;
    assert.deepEqual(moved("claude-code", { CLAUDE_PROJECT_DIR: "/work/garden" }), ["/work/garden", "/work/garden/src"]);
    assert.deepEqual(moved("gemini-cli", { GEMINI_PROJECT_DIR: "/work/garden" }), ["/work/garden", "/work/garden/src"]);
    assert.deepEqual(moved("claude-code", { CLAUDE_PROJECT_DIR: "/work/garden/src" }), ["/work/garden/src"], "the same folder once");
    assert.deepEqual(moved("claude-code", {}), ["/work/garden/src"]);
    assert.deepEqual(moved("claude-code", { CLAUDE_PROJECT_DIR: "" }), ["/work/garden/src"]);
    // Each agent reads only its own variable. Codex's reference names none, and Cursor hands over its workspace's roots.
    assert.deepEqual(moved("gemini-cli", { CLAUDE_PROJECT_DIR: "/elsewhere" }), ["/work/garden/src"]);
    assert.deepEqual(moved("codex", { CLAUDE_PROJECT_DIR: "/elsewhere" }), ["/work/garden/src"]);
    assert.deepEqual(readEvent("cursor", EVENTS.cursor.turn("s", "/work/garden", "x"), { CLAUDE_PROJECT_DIR: "/elsewhere" }).folders, ["/work/garden"]);
    // With no folder in the event there is nothing to go on, whatever the variable says.
    assert.deepEqual(readEvent("claude-code", { session_id: "s", hook_event_name: "SessionEnd" }, { CLAUDE_PROJECT_DIR: "/work/garden" }).folders, []);
  });

  test("an event nobody registered for, or input without a session, is nothing to act on", () => {
    assert.equal(readEvent("claude-code", { session_id: "s", hook_event_name: "PreToolUse" }), null);
    assert.equal(readEvent("claude-code", { hook_event_name: "Stop", last_assistant_message: "x" }), null);
    assert.equal(readEvent("cursor", { conversation_id: "s", hook_event_name: "stop" }), null);
    assert.equal(readEvent("nobody", { session_id: "s", hook_event_name: "Stop" }), null);
    assert.equal(readEvent("constructor", { session_id: "s", hook_event_name: "Stop" }), null);
    assert.equal(readEvent("claude-code", [1]), null);
    assert.equal(readEvent("claude-code", null), null);
  });

  test("a turn a stop hook already forced on is marked, in the agents that say so", () => {
    for (const agent of ["claude-code", "codex", "gemini-cli"])
      assert.equal(readEvent(agent, { ...EVENTS[agent].turn("s", "/w", "x"), stop_hook_active: true }).continuing, true);
  });

  test("the examples register only the events named here, each with its own agent, and hold no secret", () => {
    const examples = here("../harness/hooks/examples/");
    const registered = { "claude-code": "claude-code.settings.example.json", codex: "codex.hooks.example.json", cursor: "cursor.hooks.example.json", "gemini-cli": "gemini-cli.settings.example.json" };
    assert.deepEqual(Object.keys(registered).sort(), Object.keys(AGENTS).sort());
    for (const [agent, name] of Object.entries(registered)) {
      const { hooks } = JSON.parse(readFileSync(join(examples, name), "utf8"));
      assert.deepEqual(Object.keys(hooks), [AGENTS[agent].turn.event, AGENTS[agent].end.event], name);
      const text = JSON.stringify(hooks);
      assert.ok(text.includes(`rhythm-hook.mjs --agent ${agent}`), name);
      assert.ok(!/bearer|authorization|tokenFile/i.test(text), `${name}: no token near an agent's settings`);
    }
    // Codex gives a session's end 3 seconds at most: its example asks for that, and the hook's budget stays inside it.
    const codex = JSON.parse(readFileSync(join(examples, registered.codex), "utf8"));
    assert.equal(codex.hooks.SessionEnd[0].hooks[0].timeout, 3);
    assert.ok(AGENTS.codex.budgetMs < 3000);
    const claude = JSON.parse(readFileSync(join(examples, registered["claude-code"]), "utf8"));
    assert.ok(claude.hooks.SessionEnd[0].hooks[0].timeout * 1000 > AGENTS["claude-code"].budgetMs);
  });

  test("the page says which events it read from where, and that VS Code waits", () => {
    const readme = readFileSync(here("../harness/hooks/README.md"), "utf8");
    for (const agent of Object.values(AGENTS)) {
      assert.ok(readme.includes(agent.label), agent.label);
      assert.ok(readme.includes(`\`${agent.turn.event}\``) && readme.includes(`\`${agent.end.event}\``), agent.label);
      assert.ok(readme.includes(`\`${agent.turn.text}\``), agent.label);
    }
    assert.match(readme, /VS Code[^\n]*waits/);
    assert.match(readme, /Antigravity[^\n]*waits/);
  });
});

// The hook as its agent runs it, against a fake engine on this computer.
const temps = [];
after(() => {
  for (const dir of temps) rmSync(dir, { recursive: true, force: true });
});
const TOKEN = base64url(43, 200);

function workspace(config = {}) {
  const dir = realpathSync(mkdtempSync(join(tmpdir(), "rhythm-hook-")));
  temps.push(dir);
  const folder = join(dir, "garden-app");
  mkdirSync(folder);
  const tokenFile = join(dir, "capture_demo_token");
  writeFileSync(tokenFile, `${TOKEN}\n`, { mode: 0o600 });
  const path = join(dir, "hooks.json");
  const settings = { capture: "on", tokenFile, folders: [folder], ...config };
  if (config !== null) writeFileSync(path, JSON.stringify(settings));
  return { dir, folder, tokenFile, path, settings, pendingDir: join(dir, "hooks-pending") };
}

const waitingIn = (space) => (existsSync(space.pendingDir) ? readdirSync(space.pendingDir).filter((n) => n.endsWith(".json")) : []);

async function fakeEngine(answer = () => [200, CAPTURED]) {
  const seen = [];
  const server = createServer((request, response) => {
    let text = "";
    request.on("data", (chunk) => (text += chunk));
    request.on("end", () => {
      seen.push({ method: request.method, url: request.url, headers: request.headers, text, body: JSON.parse(text || "null") });
      const [status, body, headers] = answer(seen.length, seen.at(-1));
      response.writeHead(status, { "content-type": "application/json", ...headers });
      response.end(typeof body === "string" ? body : JSON.stringify(body ?? {}));
    });
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  // A test that fails before it closes this must not hold the whole run open.
  server.unref();
  const url =`http://127.0.0.1:${server.address().port}`;
  return { url, seen, close: () => new Promise((resolve) => server.close(resolve)) };
}

function run(args, stdin, space, env = {}) {
  return new Promise((resolve, reject) => {
    const base = Object.fromEntries(["PATH", "Path", "SystemRoot", "TEMP", "TMP", "HOME", "USERPROFILE"].filter((k) => process.env[k]).map((k) => [k, process.env[k]]));
    const child = spawn(process.execPath, [HOOK, ...args], { env: { ...base, RHYTHM_HOOK_CONFIG: space.path, ...env }, stdio: ["pipe", "pipe", "pipe"] });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (d) => (stdout += d));
    child.stderr.on("data", (d) => (stderr += d));
    child.on("error", reject);
    child.on("close", (code) => resolve({ code, stdout, stderr }));
    child.stdin.on("error", () => {});
    child.stdin.end(typeof stdin === "string" ? stdin : stdin === undefined ? "" : JSON.stringify(stdin));
  });
}

const fired = (agent, input, space, env) => run(["--agent", agent], input, space, env);
const quiet = (result) => {
  assert.equal(result.code, 0);
  assert.equal(result.stdout, "{}\n");
  return result;
};

const AUTH = { result: { structuredContent: { error: { code: "unauthorized" } } } };
const OFF_BODY = {};

describe("off by default", () => {
  test("with no settings file it reads nothing, keeps nothing and sends nothing", async () => {
    const engine = await fakeEngine();
    const space = workspace();
    rmSync(space.path);
    quiet(await fired("claude-code", EVENTS["claude-code"].turn("s", space.folder, GOOD), space));
    const end = quiet(await fired("claude-code", EVENTS["claude-code"].end("s", space.folder), space));
    assert.equal(end.stderr, "");
    assert.deepEqual(waitingIn(space), []);
    assert.equal(engine.seen.length, 0);
    await engine.close();
  });

  for (const [name, settings] of [["capture not said", { capture: undefined }], ['capture "off"', { capture: "off" }], ["capture true", { capture: true }]]) {
    test(`with ${name} it does nothing either`, async () => {
      const engine = await fakeEngine();
      const space = workspace({ engineUrl: engine.url, ...settings });
      quiet(await fired("codex", EVENTS.codex.turn("s", space.folder, GOOD), space));
      quiet(await fired("codex", EVENTS.codex.end("s", space.folder), space));
      assert.deepEqual(waitingIn(space), []);
      assert.equal(engine.seen.length, 0);
      await engine.close();
    });
  }

  test("RHYTHM_HOOK_CAPTURE=off turns it off for one run of the agent", async () => {
    const engine = await fakeEngine();
    const space = workspace({ engineUrl: engine.url });
    quiet(await fired("claude-code", EVENTS["claude-code"].turn("s", space.folder, GOOD), space, { RHYTHM_HOOK_CAPTURE: "off" }));
    assert.deepEqual(waitingIn(space), []);
    await engine.close();
  });

  test("a folder nobody listed is never read from, nor is a sibling that merely starts the same", async () => {
    const engine = await fakeEngine();
    const space = workspace({ engineUrl: engine.url });
    const sibling = `${space.folder}-private`;
    mkdirSync(sibling);
    quiet(await fired("claude-code", EVENTS["claude-code"].turn("s", sibling, GOOD), space));
    quiet(await fired("claude-code", EVENTS["claude-code"].turn("s2", space.dir, GOOD), space));
    assert.deepEqual(waitingIn(space), []);
    // A folder under one listed is that folder's; every root of a Cursor workspace must be listed.
    const inner = join(space.folder, "src");
    mkdirSync(inner);
    quiet(await fired("claude-code", EVENTS["claude-code"].turn("s3", inner, GOOD), space));
    assert.equal(waitingIn(space).length, 1);
    quiet(await fired("cursor", { ...EVENTS.cursor.turn("s4", space.folder, GOOD), workspace_roots: [space.folder, sibling] }, space));
    assert.equal(waitingIn(space).length, 1);
    await engine.close();
  });

  test("a session started in a folder nobody listed stays out once it moves into a listed one, and the other way round", async () => {
    const space = workspace();
    const unlisted = join(space.dir, "elsewhere");
    mkdirSync(unlisted);
    for (const [agent, variable] of [["claude-code", "CLAUDE_PROJECT_DIR"], ["gemini-cli", "GEMINI_PROJECT_DIR"]]) {
      quiet(await fired(agent, EVENTS[agent].turn("in", space.folder, GOOD), space, { [variable]: unlisted }));
      quiet(await fired(agent, EVENTS[agent].turn("out", unlisted, GOOD), space, { [variable]: space.folder }));
      assert.deepEqual(waitingIn(space), [], agent);
      // Started in a listed folder and still under it, it counts.
      const inner = join(space.folder, `src-${agent}`);
      mkdirSync(inner);
      quiet(await fired(agent, EVENTS[agent].turn("under", inner, GOOD), space, { [variable]: space.folder }));
      assert.equal(waitingIn(space).length, 1, agent);
      rmSync(space.pendingDir, { recursive: true });
    }
  });

  test("capture turned off in the settings deletes what waits at the next event, unsent, and it stays gone if turned on again", async () => {
    const engine = await fakeEngine();
    const space = workspace({ engineUrl: engine.url });
    quiet(await fired("claude-code", EVENTS["claude-code"].turn("s", space.folder, GOOD), space));
    assert.equal(waitingIn(space).length, 1);
    writeFileSync(space.path, JSON.stringify({ ...space.settings, engineUrl: engine.url, capture: "off" }));
    quiet(await fired("codex", EVENTS.codex.end("other", space.folder), space));
    assert.deepEqual(waitingIn(space), []);
    writeFileSync(space.path, JSON.stringify({ ...space.settings, engineUrl: engine.url }));
    quiet(await fired("claude-code", EVENTS["claude-code"].end("s", space.folder), space));
    assert.equal(engine.seen.length, 0);
    await engine.close();
  });

  test("what only stops the hook for now deletes nothing: one run's variable, a wrong setting, no settings file", async () => {
    const engine = await fakeEngine();
    const space = workspace({ engineUrl: engine.url });
    quiet(await fired("claude-code", EVENTS["claude-code"].turn("s", space.folder, GOOD), space));
    const end = EVENTS["claude-code"].end("other", space.folder);
    quiet(await fired("claude-code", end, space, { RHYTHM_HOOK_CAPTURE: "off" }));
    assert.equal(waitingIn(space).length, 1, "one run's variable");
    writeFileSync(space.path, JSON.stringify({ ...space.settings, engineUrl: engine.url, folders: [] }));
    quiet(await fired("claude-code", end, space));
    assert.equal(waitingIn(space).length, 1, "a wrong setting");
    writeFileSync(space.path, "{ not json");
    quiet(await fired("claude-code", end, space));
    assert.equal(waitingIn(space).length, 1, "a settings file that cannot be read");
    rmSync(space.path);
    quiet(await fired("claude-code", end, space));
    assert.equal(waitingIn(space).length, 1, "no settings file");
    assert.equal(engine.seen.length, 0);
    await engine.close();
  });

  test('folders "*" says every folder, on purpose; a relative folder is no folder', async () => {
    const all = workspace({ folders: ["*"] });
    quiet(await fired("claude-code", EVENTS["claude-code"].turn("s", all.dir, GOOD), all));
    assert.equal(waitingIn(all).length, 1);
    const relative = workspace({ folders: ["."] });
    const result = quiet(await fired("claude-code", EVENTS["claude-code"].turn("s", relative.folder, GOOD), relative));
    assert.match(result.stderr, /folders must list full paths/);
    assert.deepEqual(waitingIn(relative), []);
  });

  test("a wrong setting turns it off, and says which", async () => {
    const cases = [
      [{ tokenFile: undefined }, /tokenFile/],
      [{ tokenFile: "capture_token" }, /tokenFile/],
      [{ folders: [] }, /folders/],
      [{ folders: "everywhere" }, /folders/],
      [{ project: "Not A Slug" }, /project/],
      [{ project: 123 }, /project/],
      [{ project: ["garden-app"] }, /project/],
      [{ project: true }, /project/],
      [{ project: null }, /project/],
      [{ maxChars: 5 }, /maxChars/],
      [{ minSignals: 9 }, /minSignals/],
      [{ engineUrl: "ftp://example.invalid" }, /http/],
      [{ engineUrl: "not a url" }, /not a URL/],
      [{ engineUrl: JOINED("https", "://alex:", password, "@engine.example.invalid") }, /credentials/],
      [{ engineUrl: "http://engine.example.invalid:8181" }, /unencrypted/],
    ];
    for (const [settings, said] of cases) {
      const space = workspace(settings);
      const result = quiet(await fired("claude-code", EVENTS["claude-code"].turn("s", space.folder, GOOD), space));
      assert.match(result.stderr, said, JSON.stringify(settings));
      assert.ok(!result.stderr.includes(password));
      assert.deepEqual(waitingIn(space), []);
    }
    const broken = workspace();
    writeFileSync(broken.path, "{ not json");
    assert.match(quiet(await fired("claude-code", EVENTS["claude-code"].turn("s", broken.folder, GOOD), broken)).stderr, /cannot be read as JSON/);
  });

  test("the address is always /capture on the named engine, and a token stays on this computer unless told otherwise", () => {
    assert.equal(captureUrl("http://127.0.0.1:8181").url.href, "http://127.0.0.1:8181/capture");
    assert.equal(captureUrl("http://127.0.0.1:8181/mcp?x=1#y").url.href, "http://127.0.0.1:8181/capture");
    assert.equal(captureUrl("http://localhost:8181").url.href, "http://localhost:8181/capture");
    assert.equal(captureUrl("http://[::1]:8181").url.href, "http://[::1]:8181/capture");
    assert.equal(captureUrl("https://engine.example.invalid").url.href, "https://engine.example.invalid/capture");
    assert.ok(captureUrl("http://192.168.1.20:8181").problem);
    assert.equal(captureUrl("http://192.168.1.20:8181", true).url.href, "http://192.168.1.20:8181/capture");
    assert.ok(captureUrl("http://127.0.0.1.example.invalid:8181").problem, "a name that starts like the loopback is no loopback");
  });
});

describe("what it sends", () => {
  for (const agent of Object.keys(AGENTS)) {
    test(`${agent}: a turn stays here, the session's end sends one valid draft, once`, async () => {
      const engine = await fakeEngine();
      const space = workspace({ engineUrl: engine.url, project: "auto" });
      quiet(await fired(agent, EVENTS[agent].turn("session-1", space.folder, GOOD), space));
      assert.equal(engine.seen.length, 0, "a turn makes no call");
      assert.equal(waitingIn(space).length, 1);
      quiet(await fired(agent, EVENTS[agent].end("session-1", space.folder), space));
      assert.equal(engine.seen.length, 1);
      const [call] = engine.seen;
      assert.equal(call.method, "POST");
      assert.equal(call.url, "/capture");
      assert.equal(call.headers.authorization, `Bearer ${TOKEN}`);
      assert.equal(call.headers.origin, undefined, "the door refuses an Origin");
      assert.equal(call.headers["content-type"], "application/json");
      assert.deepEqual(schemaCheck(SCHEMAS, "capture_request.input.schema.json", call.body), []);
      assert.deepEqual(Object.keys(call.body).sort(), ["idempotencyKey", "project", "source", "tags", "text"]);
      assert.equal(call.body.source, AGENTS[agent].source);
      assert.deepEqual(call.body.tags, ["session", agent]);
      assert.equal(call.body.project, "garden-app");
      assert.match(call.body.text, new RegExp(`^${AGENTS[agent].label} session, \\d{4}-\\d{2}-\\d{2}\\n\\n`));
      assert.ok(call.body.text.endsWith(GOOD));
      assert.deepEqual(waitingIn(space), []);
      // The same end again finds nothing to send.
      quiet(await fired(agent, EVENTS[agent].end("session-1", space.folder), space));
      assert.equal(engine.seen.length, 1);
      await engine.close();
    });
  }

  test("a session's message is its best one, the newest of equals; a weaker later one does not replace it", async () => {
    const engine = await fakeEngine();
    const space = workspace({ engineUrl: engine.url });
    const weaker = `${"We changed the seed step and the import step in the same way. ".repeat(4)} It was caused by a clock read, because the seed step read local time.`;
    const better = `${GOOD} Decided to use UTC everywhere instead of the local clock.`;
    const equal = `${better} Written once more, so that it is the newest.`;
    assert.ok(signals(weaker).length >= 1);
    for (const text of [GOOD, better, weaker, GOOD, equal]) quiet(await fired("claude-code", EVENTS["claude-code"].turn("s", space.folder, text), space));
    quiet(await fired("claude-code", EVENTS["claude-code"].end("s", space.folder), space));
    assert.equal(engine.seen.length, 1);
    assert.ok(engine.seen[0].body.text.endsWith(equal));
    await engine.close();
  });

  test("two sessions keep their own messages, and a turn that ends on nothing worth keeping sends nothing", async () => {
    const engine = await fakeEngine();
    const space = workspace({ engineUrl: engine.url });
    quiet(await fired("claude-code", EVENTS["claude-code"].turn("a", space.folder, GOOD), space));
    quiet(await fired("codex", EVENTS.codex.turn("a", space.folder, `${GOOD} From the other agent.`), space));
    assert.equal(waitingIn(space).length, 2);
    quiet(await fired("claude-code", EVENTS["claude-code"].turn("b", space.folder, "Done."), space));
    quiet(await fired("claude-code", EVENTS["claude-code"].end("b", space.folder), space));
    assert.equal(engine.seen.length, 0);
    quiet(await fired("codex", EVENTS.codex.end("a", space.folder), space));
    assert.equal(engine.seen.length, 1);
    assert.equal(engine.seen[0].body.source, "codex-session");
    assert.equal(waitingIn(space).length, 1);
    await engine.close();
  });

  test("a turn a stop hook forced on is not a result", async () => {
    const space = workspace();
    quiet(await fired("claude-code", { ...EVENTS["claude-code"].turn("s", space.folder, GOOD), stop_hook_active: true }, space));
    assert.deepEqual(waitingIn(space), []);
  });

  test("it never opens the transcript, whatever the event says of it", async () => {
    const engine = await fakeEngine();
    const space = workspace({ engineUrl: engine.url });
    const canary = "CANARY-WHOLE-TRANSCRIPT-LINE";
    const transcript = join(space.folder, "transcript.jsonl");
    writeFileSync(transcript, `${JSON.stringify({ role: "user", content: canary })}\n`);
    quiet(await fired("claude-code", { ...EVENTS["claude-code"].turn("s", space.folder, GOOD), transcript_path: transcript }, space));
    quiet(await fired("claude-code", { ...EVENTS["claude-code"].end("s", space.folder), transcript_path: transcript }, space));
    assert.equal(engine.seen.length, 1);
    assert.ok(!engine.seen[0].text.includes(canary));
    // An end with no message kept has nothing to send, however long its transcript.
    quiet(await fired("claude-code", { ...EVENTS["claude-code"].end("other", space.folder), transcript_path: transcript }, space));
    assert.equal(engine.seen.length, 1);
    await engine.close();
  });

  test("the fixture, whole: every secret shape is redacted on the wire and on the disk", async () => {
    const engine = await fakeEngine();
    const space = workspace({ engineUrl: engine.url, maxChars: 8000 });
    const padding = "and this sentence is here so that the words around each secret stay ordinary prose for a person to read later on";
    // The gate leaves out what is mostly secrets, so each sits in a sentence of its own, a few to a session. The PEM block
    // that never ends takes the rest of its message with it, so it comes last of all.
    const endless = ([name]) => name.includes("cut off");
    const lines = [...FIXTURE.filter((e) => !endless(e)), ...FIXTURE.filter(endless)]
      .map(([, secret, line]) => `${line ? line(secret) : `It printed ${secret} in the log`} ${padding}.`);
    const size = 6;
    let session = 0;
    for (let i = 0; i < lines.length; i += size) {
      session++;
      const message = `${GOOD}\n\n${lines.slice(i, i + size).join("\n\n")}`;
      assert.equal(assess(message, { maxChars: 8000 }).reason, undefined, `lines ${i} on`);
      quiet(await fired("claude-code", EVENTS["claude-code"].turn(`fixture-${session}`, space.folder, message), space));
      for (const name of waitingIn(space)) {
        const kept = readFileSync(join(space.pendingDir, name), "utf8");
        for (const [what, secret] of FIXTURE) assert.ok(!kept.includes(secret), `${what} was written to disk`);
      }
      quiet(await fired("claude-code", EVENTS["claude-code"].end(`fixture-${session}`, space.folder), space));
    }
    assert.equal(engine.seen.length, session);
    const wire = engine.seen.map((call) => call.text).join("\n");
    for (const [what, secret] of FIXTURE) assert.ok(!wire.includes(secret), `${what} went over the wire`);
    const redactions = engine.seen.reduce((n, call) => n + call.body.text.split(REDACTED).length - 1, 0);
    assert.ok(redactions >= FIXTURE.length, `${redactions} redactions for ${FIXTURE.length} secrets`);
    await engine.close();
  });

  test("its own token and the agent's environment never leave, whatever they look like", async () => {
    const engine = await fakeEngine();
    const space = workspace({ engineUrl: engine.url });
    const fromEnv = "plainlowercasevalue";
    const message = `${GOOD} The hook read ${TOKEN} and the service used ${fromEnv} as its value.`;
    const turn = quiet(await fired("claude-code", EVENTS["claude-code"].turn("s", space.folder, message), space, { SERVICE_API_TOKEN: fromEnv }));
    const end = quiet(await fired("claude-code", EVENTS["claude-code"].end("s", space.folder), space, { SERVICE_API_TOKEN: fromEnv }));
    const { text } = engine.seen[0].body;
    assert.ok(!text.includes(TOKEN) && !text.includes(fromEnv), text);
    assert.equal(text.split(REDACTED).length - 1, 2);
    for (const out of [turn.stderr, end.stderr, turn.stdout, end.stdout]) assert.ok(!out.includes(TOKEN));
    await engine.close();
  });

  test("what waits on disk is private to this person and holds no raw session id", async () => {
    const space = workspace();
    const session = "raw-session-identifier";
    quiet(await fired("claude-code", EVENTS["claude-code"].turn(session, space.folder, GOOD), space));
    const [name] = waitingIn(space);
    assert.ok(!name.includes(session));
    assert.ok(!readFileSync(join(space.pendingDir, name), "utf8").includes(session));
    assert.ok(!readFileSync(join(space.pendingDir, name), "utf8").includes(TOKEN));
    if (process.platform !== "win32") {
      assert.equal(statSync(join(space.pendingDir, name)).mode & 0o777, 0o600);
      assert.equal(statSync(space.pendingDir).mode & 0o777, 0o700);
    }
  });

  test("the key is the content's: a retry is the same request, and new words are a new one", async () => {
    let up = false;
    const engine = await fakeEngine(() => (up ? [200, CAPTURED] : [503, { result: { structuredContent: { error: { code: "unavailable" } } } }]));
    const space = workspace({ engineUrl: engine.url });
    quiet(await fired("claude-code", EVENTS["claude-code"].turn("s", space.folder, GOOD), space));
    const failed = quiet(await fired("claude-code", EVENTS["claude-code"].end("s", space.folder), space));
    assert.match(failed.stderr, /503/);
    assert.ok(!failed.stderr.includes("flaky build"), "stderr holds no words of the message");
    assert.equal(waitingIn(space).length, 1, "a passing failure keeps the message");
    up = true;
    quiet(await fired("claude-code", EVENTS["claude-code"].end("s", space.folder), space));
    assert.equal(engine.seen.length, 2);
    assert.deepEqual(engine.seen[1].body, engine.seen[0].body);
    assert.match(engine.seen[0].body.idempotencyKey, /^hook\.claude-code\.[0-9a-f]{40}$/);
    assert.deepEqual(waitingIn(space), []);
    // A resumed session ending on the same words asks with the same key, which the engine answers as a replay.
    quiet(await fired("claude-code", EVENTS["claude-code"].turn("s", space.folder, GOOD), space));
    quiet(await fired("claude-code", EVENTS["claude-code"].end("s", space.folder), space));
    assert.equal(engine.seen[2].body.idempotencyKey, engine.seen[0].body.idempotencyKey);
    quiet(await fired("claude-code", EVENTS["claude-code"].turn("s", space.folder, `${GOOD} And a new sentence.`), space));
    quiet(await fired("claude-code", EVENTS["claude-code"].end("s", space.folder), space));
    assert.notEqual(engine.seen[3].body.idempotencyKey, engine.seen[0].body.idempotencyKey);
    await engine.close();
  });

  test("an engine that cannot be reached costs the agent nothing, and the message waits", async () => {
    const closed = await fakeEngine();
    await closed.close();
    const space = workspace({ engineUrl: closed.url });
    quiet(await fired("codex", EVENTS.codex.turn("s", space.folder, GOOD), space));
    const end = quiet(await fired("codex", EVENTS.codex.end("s", space.folder), space));
    assert.match(end.stderr, /could not be reached/);
    assert.equal(waitingIn(space).length, 1);
    assert.match(readFileSync(join(space.dir, "hooks-last.json"), "utf8"), /"outcome":"retry"/);
  });

  test("an engine that takes the call and never answers is given up on inside the agent's limit", async () => {
    const sockets = new Set();
    const silent = createServer(() => {});
    silent.on("connection", (socket) => sockets.add(socket));
    await new Promise((resolve) => silent.listen(0, "127.0.0.1", resolve));
    silent.unref();
    const space = workspace({ engineUrl: `http://127.0.0.1:${silent.address().port}` });
    quiet(await fired("codex", EVENTS.codex.turn("s", space.folder, GOOD), space));
    const start = performance.now();
    const end = quiet(await fired("codex", EVENTS.codex.end("s", space.folder), space));
    // Codex's limit for a session's end is 3 seconds, and the hook gives up at its 2.5. The bound here allows for a slow start.
    const took = performance.now() - start;
    assert.ok(took >= AGENTS.codex.budgetMs - 200 && took < AGENTS.codex.budgetMs + 3000, `${took} ms`);
    assert.match(end.stderr, /could not be reached/);
    assert.equal(waitingIn(space).length, 1);
    for (const socket of sockets) socket.destroy();
    silent.close();
  });

  test("a redirect is never followed: the token goes to the named address or nowhere", async () => {
    const elsewhere = await fakeEngine();
    const engine = await fakeEngine(() => [307, {}, { location: `${elsewhere.url}/capture` }]);
    const space = workspace({ engineUrl: engine.url });
    quiet(await fired("claude-code", EVENTS["claude-code"].turn("s", space.folder, GOOD), space));
    quiet(await fired("claude-code", EVENTS["claude-code"].end("s", space.folder), space));
    assert.equal(engine.seen.length, 1);
    assert.equal(elsewhere.seen.length, 0);
    assert.equal(waitingIn(space).length, 1);
    await engine.close();
    await elsewhere.close();
  });

  test("a killed session's message goes with a later session's end; after two weeks it is deleted unsent", async () => {
    const engine = await fakeEngine();
    const space = workspace({ engineUrl: engine.url });
    const age = (days) => {
      const when = new Date(Date.now() - days * 24 * 60 * 60 * 1000);
      for (const name of waitingIn(space)) utimesSync(join(space.pendingDir, name), when, when);
    };
    quiet(await fired("claude-code", EVENTS["claude-code"].turn("recent", space.folder, GOOD), space));
    quiet(await fired("claude-code", EVENTS["claude-code"].end("another", space.folder), space));
    assert.equal(engine.seen.length, 0, "a session that may still be open is left alone");
    age(2);
    quiet(await fired("claude-code", EVENTS["claude-code"].end("another", space.folder), space));
    assert.equal(engine.seen.length, 1);
    assert.deepEqual(waitingIn(space), []);
    quiet(await fired("codex", EVENTS.codex.turn("old", space.folder, GOOD), space));
    age(15);
    quiet(await fired("claude-code", EVENTS["claude-code"].end("another", space.folder), space));
    assert.equal(engine.seen.length, 1, "past the two weeks it is not sent");
    assert.deepEqual(waitingIn(space), []);
    await engine.close();
  });

  test("a folder taken off the list takes its waiting message with it", async () => {
    const engine = await fakeEngine();
    const space = workspace({ engineUrl: engine.url });
    quiet(await fired("claude-code", EVENTS["claude-code"].turn("s", space.folder, GOOD), space));
    writeFileSync(space.path, JSON.stringify({ ...space.settings, engineUrl: engine.url, folders: [join(space.dir, "elsewhere")] }));
    quiet(await fired("claude-code", EVENTS["claude-code"].end("s", space.folder), space));
    assert.equal(engine.seen.length, 0);
    assert.deepEqual(waitingIn(space), []);
    await engine.close();
  });

  test("a project is what the settings say: none by default, a slug, or the folder's own name", async () => {
    const engine = await fakeEngine();
    const none = workspace({ engineUrl: engine.url });
    const fixed = workspace({ engineUrl: engine.url, project: "demo-garden-app" });
    for (const space of [none, fixed]) {
      quiet(await fired("claude-code", EVENTS["claude-code"].turn("s", space.folder, GOOD), space));
      quiet(await fired("claude-code", EVENTS["claude-code"].end("s", space.folder), space));
    }
    assert.ok(!("project" in engine.seen[0].body));
    assert.equal(engine.seen[1].body.project, "demo-garden-app");
    assert.ok(!engine.seen[0].text.includes("garden-app"), "no local folder name leaves unless asked for");
    await engine.close();
  });

  test('project "auto" names the listed folder from anywhere under it; only under "*" is it the folder\'s own name', async () => {
    const sent = async (space, agent, folder, env) => {
      quiet(await fired(agent, EVENTS[agent].turn("s", folder, GOOD), space, env));
      quiet(await fired(agent, EVENTS[agent].end("s", folder), space, env));
    };
    const engine = await fakeEngine();
    const listed = workspace({ engineUrl: engine.url, project: "auto" });
    const inner = join(listed.folder, "src", "deep");
    mkdirSync(inner, { recursive: true });
    await sent(listed, "claude-code", inner, { CLAUDE_PROJECT_DIR: listed.folder });
    await sent(listed, "codex", inner);
    assert.deepEqual(engine.seen.map((call) => call.body.project), ["garden-app", "garden-app"]);
    // Of two listed folders it is the deeper one.
    const nested = workspace({ engineUrl: engine.url, project: "auto" });
    writeFileSync(nested.path, JSON.stringify({ ...nested.settings, folders: [nested.dir, nested.folder] }));
    mkdirSync(join(nested.folder, "src"));
    await sent(nested, "codex", join(nested.folder, "src"));
    assert.equal(engine.seen.at(-1).body.project, "garden-app");
    // Every folder listed names none: the folder the session started in, where the agent names it, or else where it is.
    const all = workspace({ engineUrl: engine.url, project: "auto", folders: ["*"] });
    mkdirSync(join(all.folder, "src"));
    await sent(all, "claude-code", join(all.folder, "src"), { CLAUDE_PROJECT_DIR: all.folder });
    assert.equal(engine.seen.at(-1).body.project, "garden-app");
    await sent(all, "codex", join(all.folder, "src"));
    assert.equal(engine.seen.at(-1).body.project, "src");
    await engine.close();
  });

  for (const [name, reply] of [["a page of HTML", "<html><body>Sign in to this network</body></html>"], ["JSON that names no memory", { ok: true }], ["an id that is no text", { id: 7 }], ["nothing at all", ""]]) {
    test(`a 200 with ${name} is not the engine's: nothing is known to be saved, so the message waits`, async () => {
      let engineUp = false;
      const engine = await fakeEngine(() => (engineUp ? [200, CAPTURED] : [200, reply]));
      const space = workspace({ engineUrl: engine.url });
      quiet(await fired("claude-code", EVENTS["claude-code"].turn("s", space.folder, GOOD), space));
      const end = quiet(await fired("claude-code", EVENTS["claude-code"].end("s", space.folder), space));
      assert.match(end.stderr, /200: something answered that is not the engine/);
      assert.ok(!end.stderr.includes("flaky build"));
      assert.equal(waitingIn(space).length, 1);
      assert.match(readFileSync(join(space.dir, "hooks-last.json"), "utf8"), /"outcome":"retry","status":200/);
      engineUp = true;
      assert.equal(quiet(await fired("claude-code", EVENTS["claude-code"].end("s", space.folder), space)).stderr, "");
      assert.deepEqual(engine.seen[1].body, engine.seen[0].body, "the same request, under the same key");
      assert.deepEqual(waitingIn(space), []);
      assert.match(readFileSync(join(space.dir, "hooks-last.json"), "utf8"), /"outcome":"saved"/);
      await engine.close();
    });
  }
});

describe("refused while the endpoint is off", () => {
  test("a 404 saves nothing, keeps nothing, tries nothing again, and the agent carries on", async () => {
    const engine = await fakeEngine(() => [404, OFF_BODY]);
    const space = workspace({ engineUrl: engine.url });
    quiet(await fired("claude-code", EVENTS["claude-code"].turn("s", space.folder, GOOD), space));
    assert.equal(waitingIn(space).length, 1);
    const end = quiet(await fired("claude-code", EVENTS["claude-code"].end("s", space.folder), space));
    assert.equal(engine.seen.length, 1);
    assert.match(end.stderr, /404/);
    assert.match(end.stderr, /is off/);
    assert.deepEqual(waitingIn(space), []);
    quiet(await fired("claude-code", EVENTS["claude-code"].end("s", space.folder), space));
    assert.equal(engine.seen.length, 1);
    await engine.close();
  });

  for (const status of [401, 403, 400, 409, 413]) {
    test(`a ${status} will not change by waiting: the message is not kept`, async () => {
      const engine = await fakeEngine(() => [status, AUTH]);
      const space = workspace({ engineUrl: engine.url });
      quiet(await fired("claude-code", EVENTS["claude-code"].turn("s", space.folder, GOOD), space));
      const end = quiet(await fired("claude-code", EVENTS["claude-code"].end("s", space.folder), space));
      assert.match(end.stderr, new RegExp(String(status)));
      assert.deepEqual(waitingIn(space), []);
      await engine.close();
    });
  }

  test("only a passing failure is tried again", () => {
    assert.deepEqual([200, 404, 401, 403, 400, 405, 409, 413].map(fate), ["saved", "off", "refused", "refused", "refused", "refused", "refused", "refused"]);
    assert.deepEqual([0, 301, 307, 429, 500, 502, 503, 504].map(fate), Array(8).fill("retry"));
  });

  test("a token file that cannot be read sends nothing, and the message waits", async () => {
    const engine = await fakeEngine();
    const space = workspace({ engineUrl: engine.url });
    quiet(await fired("claude-code", EVENTS["claude-code"].turn("s", space.folder, GOOD), space));
    rmSync(space.tokenFile);
    const end = quiet(await fired("claude-code", EVENTS["claude-code"].end("s", space.folder), space));
    assert.match(end.stderr, /token file cannot be read/);
    assert.equal(engine.seen.length, 0);
    assert.equal(waitingIn(space).length, 1);
    await engine.close();
  });
});

describe("it never steers the agent", () => {
  const space = workspace();
  const cases = [
    ["input that is not JSON", ["--agent", "claude-code"], "{ not json"],
    ["no input at all", ["--agent", "claude-code"], ""],
    ["JSON that is no object", ["--agent", "codex"], "[1, 2]"],
    ["an event it does not know", ["--agent", "cursor"], { conversation_id: "s", hook_event_name: "beforeShellExecution" }],
    ["an agent it does not know", ["--agent", "nobody"], { session_id: "s", hook_event_name: "Stop" }],
    ["no agent named", ["--agent"], { session_id: "s", hook_event_name: "Stop" }],
    ["the other way to name one", ["--agent=gemini-cli"], { session_id: "s", hook_event_name: "BeforeTool" }],
    ["input too large to read", ["--agent", "claude-code"], JSON.stringify({ session_id: "s", hook_event_name: "Stop", last_assistant_message: "x".repeat(5 * 1024 * 1024) })],
  ];
  for (const [name, args, input] of cases)
    test(`${name}: {} on stdout, and exit 0`, async () => {
      quiet(await run(args, input, space));
      assert.deepEqual(waitingIn(space), []);
    });

  test("a message far past the gate's size is cut, and a state folder it cannot write costs the agent nothing", async () => {
    const huge = workspace();
    quiet(await fired("claude-code", EVENTS["claude-code"].turn("s", huge.folder, `${GOOD}\n\n`.repeat(4000)), huge));
    const [name] = waitingIn(huge);
    assert.ok(JSON.parse(readFileSync(join(huge.pendingDir, name), "utf8")).text.length <= 2010);
    const blocked = workspace();
    writeFileSync(blocked.pendingDir, "a file where the folder would go");
    const result = quiet(await fired("claude-code", EVENTS["claude-code"].turn("s", blocked.folder, GOOD), blocked));
    assert.match(result.stderr, /failed/);
    assert.ok(!result.stderr.includes("flaky build"));
  });

  test("it never answers with anything an agent would act on: no output but {} on any path", () => {
    const source = readFileSync(HOOK, "utf8");
    assert.ok(!/process\.exit\(\s*[1-9]/.test(source));
    assert.ok(!/exitCode\s*=\s*2/.test(source));
    assert.equal(source.match(/process\.stdout\.write\("\{\}\\n"\)/g).length, 1);
  });
});

describe("check and preview", () => {
  const says = async (space, pattern, code = 1) => {
    const result = await run(["check"], "", space);
    assert.equal(result.code, code, result.stdout);
    assert.match(result.stdout, pattern);
    assert.ok(!result.stdout.includes(TOKEN) && !result.stderr.includes(TOKEN));
  };

  test("check says off, and why", async () => {
    const space = workspace();
    rmSync(space.path);
    await says(space, /off: no settings file/);
    const settingOff = workspace({ capture: "off" });
    await says(settingOff, /off: capture is not "on"/);
  });

  test("check proves the route and the token with a call that saves nothing", async () => {
    const engine = await fakeEngine(() => [400, { result: { structuredContent: { error: { code: "invalid_arguments" } } } }]);
    const space = workspace({ engineUrl: engine.url });
    await says(space, /endpoint: on, and it accepts this token/, 0);
    assert.equal(engine.seen.length, 1);
    assert.deepEqual(engine.seen[0].body, {}, "an empty body has no text to save");
    await engine.close();
  });

  for (const [status, said] of [[404, /endpoint: off/], [401, /unknown, revoked/], [403, /not a capture token/], [500, /answered 500/]]) {
    test(`check explains a ${status}`, async () => {
      const engine = await fakeEngine(() => [status, {}]);
      await says(workspace({ engineUrl: engine.url }), said);
      await engine.close();
    });
  }

  test("check says when nothing listens, when the token is missing, and what waits", async () => {
    const closed = await fakeEngine();
    await closed.close();
    const space = workspace({ engineUrl: closed.url });
    quiet(await fired("claude-code", EVENTS["claude-code"].turn("s", space.folder, GOOD), space));
    await says(space, /waiting to be sent: 1/);
    await says(space, /endpoint: not reached/);
    rmSync(space.tokenFile);
    await says(space, /token: cannot read one/);
  });

  test("preview shows what would leave, with no network and no settings", async () => {
    const space = workspace();
    rmSync(space.path);
    const secret = alnum(40, 300);
    const kept = await run(["preview"], `${GOOD} The key was ${secret}.`, space);
    assert.equal(kept.code, 0);
    assert.match(kept.stdout, /^kept, score 4, 1 redacted\./);
    assert.ok(kept.stdout.includes(REDACTED) && !kept.stdout.includes(secret));
    const left = await run(["preview"], "Done.", space);
    assert.match(left.stdout, /^left out: too short/);
  });

  test("anything else is a usage line and a failure", async () => {
    const result = await run(["install"], "", workspace());
    assert.equal(result.code, 1);
    assert.match(result.stderr, /Usage:/);
  });
});
