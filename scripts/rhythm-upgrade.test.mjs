import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { randomBytes } from "node:crypto";
import { copyFileSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { delimiter, join, relative } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

// ./rhythm upgrade and rhythm.ps1 upgrade with a stand-in docker that only answers `compose config --images` and finds
// every image it is asked about: each wrapper builds with --pull, so the floating base images get their patch releases,
// and after a successful upgrade names, changing nothing, the pins the install uses that were last checked over 90 days
// ago and the images it runs that deploy/image-pins.txt does not list (an .env override). And what an upgrade must leave as it is (SB-P7-11, story
// 17): the settings, the ports and profiles, the secret and token files and the connectors turned off, on the way to a
// new tag and on the way back from a failed one, with no secret in anything a wrapper says or hands to docker. And the
// order of its steps (cli/README.md): the backup first, the images tagged <tag>-previous before anything can move them,
// and on a failure those images back under their tag before the stack starts again. cli/e2e.sh proves what it leaves on a
// real stack, where the engine and its logs are real too; only here does a failed rebuild reach the step that restores.
const root = fileURLToPath(new URL("..", import.meta.url));
const day = (daysAgo) => new Date(Date.now() - daysAgo * 24 * 3600 * 1000).toISOString().slice(0, 10);
const runs = (command, args) => spawnSync(command, args, { encoding: "utf8" }).status === 0;
// The environment for a wrapper: the stand-in docker first on the path, and no RHYTHM_VERSION, so .env's applies, nor an
// exported Compose project, which the wrapper would name to Compose (-p).
function environment(dir) {
  const env = { ...process.env };
  for (const name of ["RHYTHM_VERSION", "RHYTHM_BIND", "RHYTHM_APP_PORT", "RHYTHM_MCP_PORT", "COMPOSE_PROFILES", "COMPOSE_PROJECT_NAME", "FAKE_FAIL"]) delete env[name];
  const path = Object.keys(env).find((name) => name.toUpperCase() === "PATH") ?? "PATH";
  env[path] = join(dir, "bin") + delimiter + (env[path] ?? "");
  return env;
}

function install(t, wrapper) {
  const dir = mkdtempSync(join(tmpdir(), "rhythm-upgrade-"));
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  mkdirSync(join(dir, "deploy", "compose"), { recursive: true });
  mkdirSync(join(dir, "bin"));
  copyFileSync(join(root, wrapper), join(dir, wrapper));
  writeFileSync(join(dir, "deploy", "compose", ".env"), "RHYTHM_VERSION=old\n");
  writeFileSync(
    join(dir, "deploy", "image-pins.txt"),
    [
      "# a comment line",
      `example/fresh:1    ${day(30)}  deploy/compose/engine.compose.yaml (RHYTHM_FRESH_IMAGE)`,
      `example/stale:1    ${day(200)}  deploy/compose/engine.compose.yaml (RHYTHM_STALE_IMAGE)`,
      `example/unused:1   ${day(200)}  deploy/exposure/unused/compose.yaml (RHYTHM_UNUSED_IMAGE)`,
      `example/sdk:1      ${day(200)}  engine/Dockerfile (engine/global.json accepts it)`,
      "example/base:1     floating    app/Dockerfile",
      "",
    ].join("\n"),
  );
  // Each call goes into bin/calls.log. What Compose lists for this install: its own images, two pins, and one image an
  // .env line overrides. `image inspect` (./rhythm) and `image ls` (rhythm.ps1) find each image. bin/seen.log has, for
  // each call, the image tag it ran under and what a wrapper could have set for the stack itself instead of .env
  // (SB-P7-11). A call that holds FAKE_FAIL fails, the first time only.
  const images = ["rhythm-engine:new", "example/fresh:1", "example/stale:1", "example/override:9"];
  writeFileSync(
    join(dir, "bin", "docker"),
    `#!/bin/sh\necho "$*" >> "$(dirname "$0")/calls.log"\n` +
      'echo "${RHYTHM_VERSION:-}|${RHYTHM_BIND:-}|${RHYTHM_APP_PORT:-}|${RHYTHM_MCP_PORT:-}|${COMPOSE_PROFILES:-}|$*" >> "$(dirname "$0")/seen.log"\n' +
      'if [ -n "${FAKE_FAIL:-}" ] && [ ! -e "$(dirname "$0")/failed" ]; then case "$*" in *"$FAKE_FAIL"*) : > "$(dirname "$0")/failed"; exit 1 ;; esac; fi\n' +
      // `image ls` answers here as in docker.ps1 below: off Windows, PowerShell finds this file for `docker`, not that one.
      'case "$*" in "image ls"*) echo sha256:0123456789ab ;; esac\n' +
      `case "$*" in *"config --images"*) printf '%s\\n' ${images.join(" ")} ;; esac\nexit 0\n`,
    { mode: 0o755 },
  );
  writeFileSync(
    join(dir, "bin", "docker.ps1"),
    `Add-Content (Join-Path $PSScriptRoot 'calls.log') "$args"\n` +
      `Add-Content (Join-Path $PSScriptRoot 'seen.log') "$env:RHYTHM_VERSION|$env:RHYTHM_BIND|$env:RHYTHM_APP_PORT|$env:RHYTHM_MCP_PORT|$env:COMPOSE_PROFILES|$args"\n` +
      `if ($env:FAKE_FAIL -and -not (Test-Path (Join-Path $PSScriptRoot 'failed')) -and "$args" -like "*$env:FAKE_FAIL*") { Set-Content (Join-Path $PSScriptRoot 'failed') ''; exit 1 }\n` +
      `if ("$args" -like 'image ls*') { 'sha256:0123456789ab' }\n` +
      `if ("$args" -like '*config --images*') { ${images.map((i) => `'${i}'`).join(", ")} }\nexit 0\n`,
  );
  return dir;
}

function check(dir, result) {
  assert.equal(result.status, 0, result.stdout + result.stderr);
  const said = result.stdout + result.stderr;
  assert.match(said, /Upgraded to new\./);
  assert.equal(readFileSync(join(dir, "deploy", "compose", ".env"), "utf8").trim(), "RHYTHM_VERSION=new");
  const calls = readFileSync(join(dir, "bin", "calls.log"), "utf8").split(/\r?\n/);
  assert.ok(calls.includes("compose --profile backup build --pull"), "the build pulls its base images again");
  const lines = said.split(/\r?\n/).filter((line) => line.includes("example/"));
  const named = (image) => lines.filter((line) => line.includes(image));
  assert.equal(named("example/stale:1").length, 1, said);
  assert.match(named("example/stale:1")[0], new RegExp(`last checked on ${day(200)}, over 90 days ago`));
  assert.equal(named("example/sdk:1").length, 1, "a dated base a Dockerfile builds from counts as used");
  assert.equal(named("example/override:9").length, 1, said);
  assert.match(named("example/override:9")[0], /Not pinned in deploy\/image-pins\.txt/);
  for (const quiet of ["example/fresh:1", "example/unused:1", "example/base:1"]) assert.deepEqual(named(quiet), [], quiet);
  assert.doesNotMatch(said, /rhythm-engine:new/);
  return said;
}

const sh = runs("sh", ["-c", "exit 0"]);
test("./rhythm upgrade builds with --pull, then names stale pins and overrides on stderr", { skip: !sh }, (t) => {
  const dir = install(t, "rhythm");
  const result = spawnSync("sh", ["./rhythm", "upgrade", "new"], { cwd: dir, encoding: "utf8", env: environment(dir) });
  check(dir, result);
  assert.doesNotMatch(result.stdout, /example\//, "the reminder goes to stderr");
  assert.match(result.stdout.trim().split("\n").at(-1), /^Upgraded to new\./);
});

const pwsh = ["pwsh", "powershell"].find((shell) => runs(shell, ["-NoProfile", "-Command", "exit 0"]));
test("rhythm.ps1 upgrade builds with --pull, then names stale pins and overrides as warnings", { skip: !pwsh }, (t) => {
  const dir = install(t, "rhythm.ps1");
  const args = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", join(dir, "rhythm.ps1"), "upgrade", "new"];
  const result = spawnSync(pwsh, args, { cwd: dir, encoding: "utf8", env: environment(dir) });
  check(dir, result);
});

// What an upgrade must leave as it is (SB-P7-11, story 17). Each switch an install can hold, off by default: its value
// off and its value on. app.viewers stays off even here: this version refuses on (D-046). engine.rerank stays off too:
// on, the engine would fetch a reranker's files, which no test does (D-041), and the gate kept neither (D-251). The list has to grow with the
// settings: the first test below holds it to docs/configuration.md and the schema. Two have another word for off, so that
// test cannot find them by it, and each was added by hand.
const offByAnotherWord = ["adapters.embeddings.thresholds", "engine.searchKnowledge"];
const switches = [
  { key: "memory.scopes", off: "off", on: "on" },
  { key: "memory.capsules", off: "off", on: "on" },
  { key: "memory.tools", off: "off", on: "on" },
  { key: "memory.share", off: "off", on: "on" },
  { key: "memory.secondBrainImport", off: "off", on: "on" },
  { key: "memory.capture", off: "off", on: "on" },
  { key: "memory.capsuleWrites", off: "off", on: "on" },
  { key: "memory.work", off: "off", on: "on" },
  { key: "engine.recall", off: "off", on: "on" },
  { key: "engine.searchKnowledge", off: "fulltext", on: "combined" },
  { key: "engine.recallLog", off: "off", on: "on" },
  { key: "engine.nightSummary", off: "off", on: "on" },
  { key: "engine.writeLimit", off: "off", on: "on" },
  { key: "app.viewers", off: "off", on: "off" },
  { key: "engine.rerank", off: "off", on: "off" },
  { key: "graph.pluginLinks", off: "off", on: "on" },
  { key: "adapters.embeddings.thresholds", off: "today", on: "tuned" },
];
// The connectors an install keeps off: each off by default, and csv, on by default, as an operator's rhythm disconnect
// leaves it.
const connectorsOff = ["csv", "titles", "drive", "garmin", "second-brain", "nightscout", "calendar", "tailscale", "cloudflare", "openai-tunnel"];

// rhythm.yaml as an install holds it: from before every switch (none of the keys, as an upgrade finds an older file), with
// each one off, or with each one on. The comment and the spacing are an operator's, and stay too.
function settings(form) {
  const value = (key) => switches.find((s) => s.key === key)[form];
  const section = (name) =>
    form === "predates"
      ? []
      : [`${name}:`, ...switches.filter((s) => s.key.startsWith(`${name}.`)).map((s) => `  ${s.key.slice(name.length + 1)}: ${value(s.key)}   # as written`)];
  return [
    "# an operator's own words",
    "version: 1",
    "timezone:   Europe/London",
    "people:",
    "  - { id: alex, name: Alex }",
    "  - { id: sam,  name: Sam }",
    ...section("memory"),
    ...section("engine"),
    ...section("app"),
    ...section("graph"),
    "adapters:",
    "  embeddings:",
    "    enabled: true",
    ...(form === "predates" ? [] : [`    thresholds: ${value("adapters.embeddings.thresholds")}`]),
    ...connectorsOff.flatMap((name) => [`  ${name}:`, "    enabled: false"]),
    "",
  ].join("\n");
}

// .env as rhythm init and an operator leave it: nothing published beyond this computer, and no profile.
const envFile = [
  "COMPOSE_PROJECT_NAME=sandbox",
  "# an operator's own words",
  "RHYTHM_VERSION=old",
  "RHYTHM_CONFIG_FILE=./rhythm.yaml",
  "RHYTHM_BIND=127.0.0.1",
  "RHYTHM_APP_PORT=18080",
  "RHYTHM_MCP_PORT=18181",
  "COMPOSE_PROFILES=",
  "RHYTHM_NOTES_DIR=./data/nötes",
  "",
].join("\n");

// Every kind of secret and token file an install holds, each value made here and now: a connector's stays while it is
// off. engine_db's password is searched for by itself as well, since a log would hold that and not the whole line.
function holdSecrets(compose) {
  const secret = () => randomBytes(24).toString("base64url");
  const password = secret();
  const held = {
    postgres_password: secret(),
    engine_db: "Host=postgres;Database=rhythm;Username=rhythm_engine;" + "Pass" + "word=" + password,
    app_token: secret(),
    backup_password: secret(),
    harness_assistant_token: secret(),
    harness_alex_token: secret(),
    capture_phone_token: secret(),
    plugin_carelink_token: secret(),
    "adapters/nightscout_token": secret(),
    "adapters/second-brain_token": secret(),
    "adapters/calendar_link": "https://calendar.example.invalid/ical/" + secret() + "/basic.ics",
  };
  mkdirSync(join(compose, "secrets", "adapters"), { recursive: true });
  for (const [name, value] of Object.entries(held)) writeFileSync(join(compose, "secrets", name), value);
  return [...Object.values(held), password];
}

// Every file under a folder, with its bytes.
function files(folder, under = folder, found = new Map()) {
  for (const entry of readdirSync(folder, { withFileTypes: true })) {
    const path = join(folder, entry.name);
    if (entry.isDirectory()) files(path, under, found);
    else found.set(relative(under, path).replaceAll("\\", "/"), readFileSync(path));
  }
  return found;
}

function filled(t, wrapper, form) {
  const dir = install(t, wrapper);
  const compose = join(dir, "deploy", "compose");
  writeFileSync(join(compose, "rhythm.yaml"), settings(form));
  writeFileSync(join(compose, ".env"), envFile);
  const secrets = holdSecrets(compose);
  return { dir, compose, secrets, before: files(compose) };
}

const wrappers = [
  { name: "./rhythm", file: "rhythm", skip: !sh, run: (dir, args, env) => spawnSync("sh", ["./rhythm", ...args], { cwd: dir, encoding: "utf8", env }) },
  {
    name: "rhythm.ps1",
    file: "rhythm.ps1",
    skip: !pwsh,
    run: (dir, args, env) =>
      spawnSync(pwsh, ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", join(dir, "rhythm.ps1"), ...args], { cwd: dir, encoding: "utf8", env }),
  },
];

// cli/README.md's steps 1 and 2, on the way to any tag: the backup first of all, then each image tagged <old>-previous
// before a build or a pull can move its tag, or Compose start anything.
const built = ["rhythm-engine", "rhythm-app", "rhythm-backup"];
function backedUpThenTagged(calls) {
  assert.match(calls[0].args, /^compose --profile backup run --rm backup backup /, "the backup comes before anything else");
  const moved = calls.findIndex(({ args }) => /^compose (?:--profile backup build|pull)\b/.test(args));
  assert.ok(moved > 0, "it builds or pulls");
  for (const image of built) {
    const at = calls.findIndex(({ args }) => args === `tag ${image}:old ${image}:old-previous`);
    assert.ok(at > 0 && at < moved, `${image}:old is tagged ${image}:old-previous before anything can move it`);
  }
}

// Whatever the upgrade did: every file as it was but .env's tag, the install's own, and no secret in anything a wrapper
// said or handed to docker. It sets no address, port or profile for the stack, starts no profile's container, and takes
// nothing down. Returns the calls, each with the tag it ran under.
function leftAsItWas({ dir, compose, secrets, before }, result, tag) {
  const after = files(compose);
  assert.deepEqual([...after.keys()].sort(), [...before.keys()].sort(), "an upgrade adds and removes no file of the install");
  for (const [name, bytes] of before) if (name !== ".env") assert.ok(bytes.equals(after.get(name)), `${name} changed`);
  assert.equal(after.get(".env").toString("utf8"), before.get(".env").toString("utf8").replace(/^RHYTHM_VERSION=.*$/m, `RHYTHM_VERSION=${tag}`));
  const seen = readFileSync(join(dir, "bin", "seen.log"), "utf8");
  const heard = readFileSync(join(dir, "bin", "calls.log"), "utf8") + seen + result.stdout + result.stderr;
  // A secret that reached a log is named by its place in the list, so a failure prints no value.
  secrets.forEach((value, i) => assert.ok(!heard.includes(value), `secret number ${i} reached a log`));
  const calls = seen.split(/\r?\n/).filter(Boolean).map((line) => {
    const [version, bind, app, mcp, profiles, args] = line.split("|");
    assert.deepEqual([bind, app, mcp, profiles], ["", "", "", ""], `only .env says where the stack listens and what it starts: ${line}`);
    return { version, args };
  });
  for (const { args } of calls) {
    for (const [, profile] of args.matchAll(/--profile (\S+)/g)) assert.ok(["cli", "backup"].includes(profile), args);
    assert.doesNotMatch(args, /^compose (?:down|rm|stop)\b|^volume\b|^system\b|\bprune\b/, "an upgrade removes nothing");
  }
  const ups = calls.filter(({ args }) => args.startsWith("compose up"));
  assert.ok(ups.length >= 1, "it starts the stack again");
  for (const { args } of ups) assert.equal(args, "compose up -d --wait --force-recreate", "the stack's own services and nothing else");
  return calls;
}

test("every setting off by default is in this test's list, and cli/e2e.sh upgrades with each one on", () => {
  const listed = switches.map((s) => s.key);
  const documented = [...readFileSync(join(root, "docs", "configuration.md"), "utf8").matchAll(/^\| `([\w.-]+)` \| `off` \|/gm)].map((m) => m[1]);
  const inSchema = [];
  const walk = (node, path) => {
    if (node?.default === "off") inSchema.push(path.join("."));
    for (const [name, child] of Object.entries(node?.properties ?? {})) walk(child, [...path, name]);
  };
  walk(JSON.parse(readFileSync(join(root, "contracts", "rhythm.schema.json"), "utf8")), []);
  assert.ok(documented.length >= 10, "the settings table reads as this test expects");
  assert.ok(inSchema.length >= 9, "the schema reads as this test expects");
  for (const key of [...documented, ...inSchema]) assert.ok(listed.includes(key), `${key} is off by default: add it to this test's list and to cli/e2e.sh's last upgrade`);
  for (const key of listed) assert.ok(offByAnotherWord.includes(key) || documented.includes(key), `${key} is no setting the docs name off by default`);
  // Each switch's two values are the schema's, its off the default, so the file this test writes is one an editor takes.
  const schema = JSON.parse(readFileSync(join(root, "contracts", "rhythm.schema.json"), "utf8"));
  for (const { key, off, on } of switches) {
    const setting = key.split(".").reduce((node, name) => node?.properties?.[name], schema);
    assert.ok(setting, `${key} is not in the schema's section for it`);
    assert.equal(setting.default, off, `${key}'s default`);
    assert.ok(setting.enum.includes(off) && setting.enum.includes(on), `${key} takes ${off} and ${on}`);
  }
  // combined needs legacySearch false, which the engine checks at start: rhythm init writes it from the example, so
  // cli/e2e.sh's every setting on can start.
  assert.match(readFileSync(join(root, "deploy", "compose", "rhythm.example.yaml"), "utf8"), /^engine:\r?\n(?: .*\r?\n)*? {2}legacySearch: false\b/m);

  const e2e = readFileSync(join(root, "cli", "e2e.sh"), "utf8");
  for (const { key, on } of switches) {
    const leaf = key.split(".").at(-1);
    const line =
      key === "memory.scopes" ? "s/^  scopes: off /  scopes: on  /"
      : key === "adapters.embeddings.thresholds" ? `/^  embeddings:/a\\    thresholds: ${on}`
      : `/^${key.split(".")[0]}:/a\\  ${leaf}: ${on}`;
    assert.ok(e2e.includes(line), `cli/e2e.sh does not set ${key} before its last upgrade: ${line}`);
  }
});

test("a new install publishes the app and the engine alone, on RHYTHM_BIND, this computer by default, and every recipe waits for its profile", () => {
  const compose = join(root, "deploy", "compose");
  const main = readFileSync(join(compose, "compose.yaml"), "utf8");
  const included = [...main.matchAll(/^ {2}- path: (\S+)$/gm)].map((m) => m[1]);
  assert.deepEqual([...included].sort(), ["../exposure/cloudflare/compose.yaml", "../exposure/openai-tunnel/compose.yaml", "../exposure/tailscale/compose.yaml", "app.compose.yaml", "engine.compose.yaml"]);
  const published = [];
  const always = [];
  for (const file of ["compose.yaml", ...included]) {
    let service = null;
    let inPorts = false;
    const unprofiled = new Set();
    for (const line of readFileSync(join(compose, file), "utf8").split(/\r?\n/)) {
      if (/^\S/.test(line)) service = null;
      if (/^services:/.test(line)) service = "";
      const named = service !== null && line.match(/^ {2}([\w-]+):\s*$/);
      if (named) {
        service = named[1];
        unprofiled.add(service);
        inPorts = false;
      }
      if (service && /^ {4}profiles:/.test(line)) unprofiled.delete(service);
      if (/^ {4}ports:/.test(line)) {
        inPorts = true;
        continue;
      }
      if (inPorts && /^\s*#/.test(line)) continue;
      if (inPorts && /^ {6}- /.test(line)) published.push(`${service} ${line.trim().slice(2)}`);
      else inPorts = false;
    }
    always.push(...unprofiled);
  }
  assert.deepEqual(published.sort(), ['app "${RHYTHM_BIND:-127.0.0.1}:${RHYTHM_APP_PORT:-8080}:8080"', 'engine "${RHYTHM_BIND:-127.0.0.1}:${RHYTHM_MCP_PORT:-8181}:8080"']);
  assert.deepEqual(always.sort(), ["app", "engine", "migrate", "postgres"], "whatever else runs does so only by its profile");
  const example = readFileSync(join(compose, ".env.example"), "utf8");
  assert.match(example, /^RHYTHM_BIND=$/m);
  assert.match(example, /^COMPOSE_PROFILES=$/m);
});

for (const { name, file, skip, run } of wrappers) {
  for (const [form, args] of [["predates", ["upgrade", "new"]], ["off", ["upgrade", "new"]], ["on", ["upgrade", "new", "--pull"]]]) {
    test(`${name} ${args.join(" ")} leaves settings (${form}), ports, connectors and tokens as they are, with no secret in a log`, { skip }, (t) => {
      const it = filled(t, file, form);
      const result = run(it.dir, args, environment(it.dir));
      assert.equal(result.status, 0, result.stdout + result.stderr);
      assert.match(result.stdout + result.stderr, /Upgraded to new\./);
      const calls = leftAsItWas(it, result, "new");
      assert.equal(calls.filter(({ args: a }) => a.startsWith("compose up")).at(-1).version, "new");
      backedUpThenTagged(calls);
      for (const image of built) assert.ok(!calls.some(({ args: a }) => a === `tag ${image}:old-previous ${image}:old`), "nothing to put back");
    });
  }

  for (const [why, args, fails] of [
    ["its build fails", ["upgrade", "new"], "build --pull"],
    ["its pull fails", ["upgrade", "new", "--pull"], "compose pull"],
    ["its stack does not start", ["upgrade", "new"], "up -d --wait --force-recreate"],
    // The tag itself moved here, so the images before it come back only by the tag kept in step 2.
    ["its rebuild under the same tag does not start", ["upgrade"], "up -d --wait --force-recreate"],
  ]) {
    test(`${name} rolling back when ${why} leaves every file as it was, the tag included, with no secret in a log`, { skip }, (t) => {
      const it = filled(t, file, "on");
      const result = run(it.dir, args, { ...environment(it.dir), FAKE_FAIL: fails });
      assert.equal(result.status, 1, result.stdout + result.stderr);
      assert.match(result.stdout + result.stderr, new RegExp(`Upgrade to ${args[1] ?? "old"} failed: rolling back to old`));
      assert.equal(files(it.compose).get(".env").toString("utf8"), envFile, "the failed tag was not kept");
      const calls = leftAsItWas(it, result, "old");
      backedUpThenTagged(calls);
      // Step 4: after what failed, each image back under its tag, and only then the stack, under that tag.
      const failed = calls.findIndex(({ args: a }) => a.includes(fails));
      const last = calls.findLastIndex(({ args: a }) => a.startsWith("compose up"));
      assert.ok(failed > 0 && failed < last, "the stack starts again after what failed");
      assert.equal(calls[last].version, "old", "the stack starts again under the tag before it");
      for (const image of built) {
        const back = calls.findIndex(({ args: a }) => a === `tag ${image}:old-previous ${image}:old`);
        assert.ok(back > failed && back < last, `${image}:old-previous is back under ${image}:old before the stack starts again`);
      }
    });
  }

  test(`${name} upgrade adds the tag to an .env that has none and no last line ending, and keeps that last line`, { skip }, (t) => {
    const dir = install(t, file);
    const env = join(dir, "deploy", "compose", ".env");
    writeFileSync(env, "RHYTHM_APP_PORT=18080\nRHYTHM_BIND=0.0.0.0");
    const result = run(dir, ["upgrade", "new"], environment(dir));
    assert.equal(result.status, 0, result.stdout + result.stderr);
    assert.deepEqual(readFileSync(env, "utf8").trim().split("\n"), ["RHYTHM_APP_PORT=18080", "RHYTHM_BIND=0.0.0.0", "RHYTHM_VERSION=new"]);
  });
}
