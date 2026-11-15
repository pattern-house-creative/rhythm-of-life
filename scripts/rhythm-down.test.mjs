import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { delimiter, join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

// ./rhythm down and rhythm.ps1 down with a stand-in docker that only records its calls. `down -v` deletes a project's
// volumes, the database and the archive, for good: in a checkout that names no Compose project (its .env lost, or never
// written) Compose would take the default project, rhythm, and delete whichever install's data is under that name. So
// both wrappers refuse it there, before docker is called, and go on wherever the project is named (.env or the
// environment). And a first init in a checkout with no .env and no project name, while a project named rhythm already
// has volumes, is refused too: it would start this install on the other one's data.
const root = fileURLToPath(new URL("..", import.meta.url));
const runs = (command, args) => spawnSync(command, args, { encoding: "utf8" }).status === 0;

function environment(dir, extra = {}) {
  const env = { ...process.env };
  for (const name of ["COMPOSE_PROJECT_NAME", "RHYTHM_VERSION", "COMPOSE_PROFILES", "FAKE_VOLUMES"]) delete env[name];
  const path = Object.keys(env).find((name) => name.toUpperCase() === "PATH") ?? "PATH";
  env[path] = join(dir, "bin") + delimiter + (env[path] ?? "");
  return { ...env, ...extra };
}

// A checkout with the wrapper, a stand-in docker and, when given, a .env. `volume ls` answers with one volume when
// FAKE_VOLUMES is set, as Docker does for a project that has data.
function checkout(t, wrapper, envFile) {
  const dir = mkdtempSync(join(tmpdir(), "rhythm-down-"));
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  mkdirSync(join(dir, "deploy", "compose"), { recursive: true });
  mkdirSync(join(dir, "bin"));
  copyFileSync(join(root, wrapper), join(dir, wrapper));
  if (envFile !== undefined) writeFileSync(join(dir, "deploy", "compose", ".env"), envFile);
  writeFileSync(
    join(dir, "bin", "docker"),
    '#!/bin/sh\necho "$*" >> "$(dirname "$0")/calls.log"\n' +
      'case "$*" in "volume ls"*) if [ -n "${FAKE_VOLUMES:-}" ]; then echo rhythm_pgdata; fi ;; esac\nexit 0\n',
    { mode: 0o755 },
  );
  writeFileSync(
    join(dir, "bin", "docker.ps1"),
    `Add-Content (Join-Path $PSScriptRoot 'calls.log') "$args"\n` +
      `if ("$args" -like 'volume ls*' -and $env:FAKE_VOLUMES) { 'rhythm_pgdata' }\nexit 0\n`,
  );
  return dir;
}

const calls = (dir) => {
  const log = join(dir, "bin", "calls.log");
  return existsSync(log) ? readFileSync(log, "utf8").split(/\r?\n/).filter(Boolean) : [];
};

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

for (const wrapper of wrappers) {
  for (const flag of ["-v", "--volumes"]) {
    test(`${wrapper.name} down ${flag} is refused where no project is named, before docker is called`, { skip: wrapper.skip }, (t) => {
      for (const envFile of [undefined, "RHYTHM_VERSION=dev\n", "COMPOSE_PROJECT_NAME=\n"]) {
        const dir = checkout(t, wrapper.file, envFile);
        const result = wrapper.run(dir, ["down", flag], environment(dir));
        assert.equal(result.status, 2, result.stdout + result.stderr);
        assert.match(result.stderr, /names no Compose project/);
        assert.match(result.stderr, /Nothing was removed/);
        assert.deepEqual(calls(dir), [], "docker was not called");
      }
    });
  }

  test(`${wrapper.name} down -v goes on for the project .env names, and says which`, { skip: wrapper.skip }, (t) => {
    const dir = checkout(t, wrapper.file, "COMPOSE_PROJECT_NAME=rhythm-mine\nRHYTHM_VERSION=dev\n");
    const result = wrapper.run(dir, ["down", "-v"], environment(dir));
    assert.equal(result.status, 0, result.stdout + result.stderr);
    assert.match(result.stderr, /Removing the project rhythm-mine with its volumes/);
    assert.deepEqual(calls(dir), ["compose down -v"]);
  });

  test(`${wrapper.name} down -v goes on for a project named in the environment`, { skip: wrapper.skip }, (t) => {
    const dir = checkout(t, wrapper.file, undefined);
    const result = wrapper.run(dir, ["down", "-v", "--remove-orphans"], environment(dir, { COMPOSE_PROJECT_NAME: "rhythm-test" }));
    assert.equal(result.status, 0, result.stdout + result.stderr);
    assert.match(result.stderr, /Removing the project rhythm-test with its volumes/);
    assert.deepEqual(calls(dir), ["compose -p rhythm-test down -v --remove-orphans"]);
  });

  test(`${wrapper.name} down without -v stops the stack as before, named or not`, { skip: wrapper.skip }, (t) => {
    const dir = checkout(t, wrapper.file, undefined);
    const result = wrapper.run(dir, ["down", "--remove-orphans"], environment(dir));
    assert.equal(result.status, 0, result.stdout + result.stderr);
    assert.equal(result.stderr, "");
    assert.deepEqual(calls(dir), ["compose down --remove-orphans"]);
  });

  test(`${wrapper.name} init is refused in a new checkout while the default project already has volumes`, { skip: wrapper.skip }, (t) => {
    const dir = checkout(t, wrapper.file, undefined);
    const result = wrapper.run(dir, ["init", "--no-input"], environment(dir, { FAKE_VOLUMES: "1" }));
    assert.equal(result.status, 2, result.stdout + result.stderr);
    assert.match(result.stderr, /already has volumes on this computer/);
    assert.match(result.stderr, /--project-name/);
    assert.deepEqual(calls(dir), ["volume ls -q --filter label=com.docker.compose.project=rhythm"], "init never ran");
  });

  test(`${wrapper.name} init goes on with a project name, with an .env, or with no volumes there`, { skip: wrapper.skip }, (t) => {
    const init = "--profile cli run --rm cli init";
    // A name given on purpose, the default one included: no question asked of docker at all.
    let dir = checkout(t, wrapper.file, undefined);
    let result = wrapper.run(dir, ["init", "--project-name", "rhythm-two"], environment(dir, { FAKE_VOLUMES: "1" }));
    assert.equal(result.status, 0, result.stdout + result.stderr);
    assert.deepEqual(calls(dir), [`compose -p rhythm-two ${init} --project-name rhythm-two`]);
    dir = checkout(t, wrapper.file, undefined);
    result = wrapper.run(dir, ["init", "--project-name=rhythm"], environment(dir, { FAKE_VOLUMES: "1" }));
    assert.equal(result.status, 0, result.stdout + result.stderr);
    assert.deepEqual(calls(dir), [`compose -p rhythm ${init} --project-name=rhythm`]);
    // An install already set up here (init --force): its own .env decides, as before.
    dir = checkout(t, wrapper.file, "COMPOSE_PROJECT_NAME=rhythm\n");
    result = wrapper.run(dir, ["init", "--force"], environment(dir, { FAKE_VOLUMES: "1" }));
    assert.equal(result.status, 0, result.stdout + result.stderr);
    assert.deepEqual(calls(dir), [`compose ${init} --force`]);
    // The first install on a computer: nothing under the default name yet.
    dir = checkout(t, wrapper.file, undefined);
    result = wrapper.run(dir, ["init"], environment(dir));
    assert.equal(result.status, 0, result.stdout + result.stderr);
    assert.deepEqual(calls(dir), ["volume ls -q --filter label=com.docker.compose.project=rhythm", `compose ${init}`]);
  });
}
