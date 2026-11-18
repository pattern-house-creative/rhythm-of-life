import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, copyFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { delimiter, join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

// ./rhythm restore and rhythm.ps1 restore (SB-P7-10) with a stand-in docker that records each call and fails the one a
// test names: the restored folder, a plain copy of the whole database, is removed whatever becomes of the load; a load
// that refuses can resume only containers already running, and an uncertain load keeps services stopped; a
// snapshot that cannot be restored stops nothing; a step between them that fails (the stop) still removes it.
// cli/chaos.sh runs a failed load through ./rhythm restore on a real stack.
const root = fileURLToPath(new URL("..", import.meta.url));
const runs = (command, args) => spawnSync(command, args, { encoding: "utf8" }).status === 0;

function install(t, wrapper) {
  const dir = mkdtempSync(join(tmpdir(), "rhythm-restore-"));
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  mkdirSync(join(dir, "deploy", "compose"), { recursive: true });
  mkdirSync(join(dir, "bin"));
  copyFileSync(join(root, wrapper), join(dir, wrapper));
  writeFileSync(join(dir, "deploy", "compose", ".env"), "RHYTHM_VERSION=old\n");
  writeFileSync(
    join(dir, "bin", "docker"),
    `#!/bin/sh\necho "$*" >> "$(dirname "$0")/calls.log"\n` +
      'case "$*" in *"ps --status running -q"*) [ -z "${FAKE_STOPPED:-}" ] && echo feedface0011 ;; esac\n' +
      'if [ -n "${FAKE_FAIL:-}" ]; then case "$*" in *"$FAKE_FAIL"*) exit "${FAKE_CODE:-1}" ;; esac; fi\nexit 0\n',
    { mode: 0o755 },
  );
  writeFileSync(
    join(dir, "bin", "docker.ps1"),
    `Add-Content (Join-Path $PSScriptRoot 'calls.log') "$args"\n` +
      `if ("$args" -like "*ps --status running -q*" -and -not $env:FAKE_STOPPED) { "feedface0011" }\n` +
      `if ($env:FAKE_FAIL -and "$args" -like "*$env:FAKE_FAIL*") { if ($env:FAKE_CODE) { exit ([int]$env:FAKE_CODE) }; exit 1 }\nexit 0\n`,
  );
  return dir;
}

// An exported project is left out, so the calls are as an install's own commands make them; one test exports it.
function environment(dir, fail, project) {
  const env = { ...process.env };
  for (const name of ["RHYTHM_VERSION", "COMPOSE_PROFILES", "COMPOSE_PROJECT_NAME", "FAKE_FAIL", "FAKE_CODE", "FAKE_STOPPED"]) delete env[name];
  const path = Object.keys(env).find((name) => name.toUpperCase() === "PATH") ?? "PATH";
  env[path] = join(dir, "bin") + delimiter + (env[path] ?? "");
  if (fail) env.FAKE_FAIL = fail;
  if (project) env.COMPOSE_PROJECT_NAME = project;
  return env;
}

const sh = runs("sh", ["-c", "exit 0"]);
const pwsh = ["pwsh", "powershell"].find((shell) => runs(shell, ["-NoProfile", "-Command", "exit 0"]));
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

function calls(dir) {
  return readFileSync(join(dir, "bin", "calls.log"), "utf8").split(/\r?\n/).filter(Boolean);
}
const guard = "/staging/restore-in-progress";
const acquire = 'compose --profile restore run --rm --no-deps --entrypoint mkdir restore -m 700 ' + guard;
const restore = 'compose --profile restore run --rm restore restore latest ' + guard + '/snapshot';
const load = 'compose --profile restore run --rm restore load ' + guard + '/snapshot';
const cleanup = 'compose --profile restore run --rm --no-deps --entrypoint rm restore -rf ' + guard;
const remove = cleanup + '/snapshot';
const stop = "compose stop engine app";
const started = "compose up -d --wait";

for (const w of wrappers) {
  test(`${w.name} restore removes the restored folder once the load is done, then starts the stack`, { skip: w.skip }, (t) => {
    const dir = install(t, w.file);
    const result = w.run(dir, ["restore", "latest"], environment(dir));
    assert.equal(result.status, 0, result.stdout + result.stderr);
    const made = calls(dir);
    for (const [before, after] of [[acquire, restore], [restore, stop], [stop, load], [load, remove], [remove, started], [started, cleanup]])
      assert.ok(made.indexOf(before) >= 0 && made.indexOf(before) < made.indexOf(after), `${before} before ${after}:\n${made.join("\n")}`);
    assert.equal(made.filter((c) => c === remove).length, 1);
  });

  for (const [scenario, exitCode, wasStopped, shouldResume] of [
    ["confirmed refusal on a running stack", "12", false, true],
    ["refusal during recovery from a stopped upgrade", "12", true, false],
    ["unconfirmed outcome on a running stack", "1", false, false],
  ]) {
    test(w.name + " restore handles " + scenario, { skip: w.skip }, (t) => {
      const dir = install(t, w.file);
      const env = { ...environment(dir, "restore load"), FAKE_CODE: exitCode };
      if (wasStopped) env.FAKE_STOPPED = "1";
      const result = w.run(dir, ["restore", "latest"], env);
      assert.notEqual(result.status, 0);
      assert.match(result.stdout + result.stderr, /The restore did not load/);
      const made = calls(dir);
      assert.ok(made.includes(load) && made.indexOf(load) < made.indexOf(remove), made.join("\n"));
      assert.equal(made.includes("start feedface0011"), shouldResume);
      assert.ok(!made.includes(started), "a failed load must never launch migrations");
      assert.equal(made.at(-1), cleanup);
    });
  }

  test(w.name + " leaves an occupied restore stage alone", { skip: w.skip }, (t) => {
    const dir = install(t, w.file);
    const result = w.run(dir, ["restore", "latest"], environment(dir, "entrypoint mkdir"));
    assert.notEqual(result.status, 0);
    assert.deepEqual(calls(dir), [acquire]);
  });

  test(`${w.name} restore of a snapshot that cannot be restored stops nothing and leaves no folder`, { skip: w.skip }, (t) => {
    const dir = install(t, w.file);
    const result = w.run(dir, ["restore", "latest"], environment(dir, "restore restore latest"));
    assert.notEqual(result.status, 0);
    // No load ran, so none is said to have failed.
    assert.doesNotMatch(result.stdout + result.stderr, /The restore did not load/);
    const made = calls(dir);
    assert.ok(made.includes(cleanup), made.join("\n"));
    for (const never of [stop, load, started]) assert.ok(!made.includes(never), `${never} was called:\n${made.join("\n")}`);
  });

  // A step between the restore and the load that fails (here the stop) still takes the folder away, once.
  test(`${w.name} restore removes the restored folder when stopping the stack fails, and loads nothing`, { skip: w.skip }, (t) => {
    const dir = install(t, w.file);
    const result = w.run(dir, ["restore", "latest"], environment(dir, "stop engine app"));
    assert.notEqual(result.status, 0);
    assert.doesNotMatch(result.stdout + result.stderr, /The restore did not load/);
    const made = calls(dir);
    assert.ok(made.indexOf(stop) >= 0 && made.indexOf(stop) < made.indexOf(cleanup), made.join("\n"));
    assert.equal(made.filter((c) => c === cleanup).length, 1, made.join("\n"));
    for (const never of [load, started]) assert.ok(!made.includes(never), `${never} was called:\n${made.join("\n")}`);
  });

  // A project exported in the environment (a test stack's, D-240) is named to Compose on every call, as Compose would
  // take it anyway, so nothing rests on the project it would infer once .env is gone; without one nothing is added.
  test(`${w.name} names an exported Compose project on every call`, { skip: w.skip }, (t) => {
    const dir = install(t, w.file);
    const result = w.run(dir, ["restore", "latest"], environment(dir, null, "sb-restore-test"));
    assert.equal(result.status, 0, result.stdout + result.stderr);
    const made = readFileSync(join(dir, "bin", "calls.log"), "utf8").split(/\r?\n/).filter(Boolean);
    assert.ok(made.length > 3, made.join("\n"));
    for (const call of made) assert.match(call, /^compose -p sb-restore-test /, call);
  });
}
