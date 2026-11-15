import assert from "node:assert/strict";
import { spawn, spawnSync } from "node:child_process";
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, unlinkSync, writeFileSync } from "node:fs";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { delimiter, dirname, join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

// The sandbox stacks' checks before they start anything, cli/e2e.sh's, cli/lifecycle.sh's (SB-P3-16) and cli/journeys.sh's (SB-P6-04), with a
// stand-in docker that records each call. Each refuses to run without the pinned MiniLM files (its engine would
// download the model at its first start, and tests never download a model), in a Compose project that already has
// containers or volumes (its cleanup removes everything in its project), with an image tag another stack's containers
// run or another Compose project built (it builds, and e2e.sh retags, it; a stopped install runs no container), or on a
// port something already answers on. Once they pass, every compose call
// it makes carries the extra file that mounts the model folder read-only.
const root = fileURLToPath(new URL("..", import.meta.url));
const tools = spawnSync("sh", ["-c", "command -v curl"], { encoding: "utf8" }).status === 0;

function checkout(t) {
  const dir = mkdtempSync(join(tmpdir(), "rhythm-e2e-"));
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  for (const folder of ["cli", join("deploy", "compose"), "bin", "model", join("app", "node_modules", ".bin")]) mkdirSync(join(dir, folder), { recursive: true });
  for (const file of ["e2e.sh", "lifecycle.sh", "journeys.sh", "chaos.sh", "e2e.compose.yaml"]) copyFileSync(join(root, "cli", file), join(dir, "cli", file));
  // cli/journeys.sh's journeys run with the app's own vitest, which it never fetches.
  writeFileSync(join(dir, "app", "node_modules", ".bin", "vitest"), "stand-in");
  writeFileSync(join(dir, "model", "model_quantized.onnx"), "stand-in");
  writeFileSync(join(dir, "model", "vocab.txt"), "stand-in");
  // Each call goes into bin/calls.log with the compose files and model folder it was given. `ps -aq` and `volume ls`
  // answer FAKE_CONTAINERS and FAKE_VOLUMES; an `ancestor` filter answers FAKE_RUNS when it names FAKE_IMAGE. `image
  // inspect` knows only the images in FAKE_IMAGES, each as sha256:before, except that FAKE_MOVED answers sha256:after
  // once it has been asked about before (a pull moved its tag); asked for its labels, each answers FAKE_LABEL as the
  // Compose project that built it.
  writeFileSync(
    join(dir, "bin", "docker"),
    [
      "#!/bin/sh",
      `printf '%s|%s|%s\\n' "\${COMPOSE_FILE:-}" "\${RHYTHM_E2E_MODEL_DIR:-}" "$*" >> "$(dirname "$0")/calls.log"`,
      'case "$*" in',
      '  *ancestor=*) case "$*" in *"ancestor=${FAKE_IMAGE:-none}"*) echo "${FAKE_RUNS:-}" ;; esac ;;',
      '  "ps -aq"*) echo "${FAKE_CONTAINERS:-}" ;;',
      '  "volume ls"*) echo "${FAKE_VOLUMES:-}" ;;',
      '  "image inspect"*)',
      "    for last; do :; done",
      '    case " ${FAKE_IMAGES:-} " in *" $last "*) ;; *) exit 1 ;; esac',
      '    case "$*" in *Labels*) echo "${FAKE_LABEL:-}"; exit 0 ;; esac',
      '    mark="$(dirname "$0")/seen-$(printf %s "$last" | tr "/:" "__")"',
      '    if [ -e "$mark" ] && [ "$last" = "${FAKE_MOVED:-}" ]; then echo sha256:after; else : > "$mark"; echo sha256:before; fi ;;',
      "esac",
      "exit 0",
      "",
    ].join("\n"),
    { mode: 0o755 },
  );
  return dir;
}

// A port nothing listens on: the system picks one, which is then closed again.
function freePort() {
  return new Promise((resolve) => {
    const server = createServer().listen(0, "127.0.0.1", () => {
      const { port } = server.address();
      server.close(() => resolve(port));
    });
  });
}

// script: e2e.sh, lifecycle.sh, journeys.sh or chaos.sh, run in the checkout as cli/<script>.
async function run(dir, extra = {}, script = "e2e.sh") {
  const env = { ...process.env };
  for (const name of ["RHYTHM_MODEL_DIR", "COMPOSE_PROJECT_NAME", "COMPOSE_FILE", "RHYTHM_VERSION", "RHYTHM_APP_PORT", "RHYTHM_MCP_PORT",
    "RHYTHM_POSTGRES_IMAGE", "RHYTHM_OLLAMA_IMAGE"])
    delete env[name];
  const path = Object.keys(env).find((name) => name.toUpperCase() === "PATH") ?? "PATH";
  env[path] = join(dir, "bin") + delimiter + (env[path] ?? "");
  Object.assign(env, { RHYTHM_APP_PORT: String(await freePort()), RHYTHM_MCP_PORT: String(await freePort()) }, extra);
  const log = join(dir, "bin", "calls.log");
  if (existsSync(log)) unlinkSync(log);
  return new Promise((resolve) => {
    const child = spawn("sh", [`cli/${script}`], { cwd: dir, env });
    let said = "";
    child.stdout.on("data", (d) => (said += d));
    child.stderr.on("data", (d) => (said += d));
    child.on("close", (status) =>
      resolve({ status, said, calls: existsSync(log) ? readFileSync(log, "utf8").split(/\r?\n/).filter(Boolean) : [] }),
    );
  });
}

const composeCalls = (calls) => calls.filter((line) => line.split("|")[2].startsWith("compose"));

// Each stack script removes everything in its Compose project, so it refuses, before any docker call at all, a project
// that is empty or an install's default, rhythm: the project Compose falls back to (compose.yaml's name) once .env has
// gone, as a cleanup's first step removes it. That is how a cleanup that let Compose infer it removed a local install
// (D-240). Past the checks every Compose call names the run's project itself (-p), below.
for (const script of ["e2e.sh", "lifecycle.sh", "journeys.sh", "chaos.sh"]) {
  test(`cli/${script} refuses an empty project or an install's, rhythm, calling no docker`, { skip: !tools }, async (t) => {
    const dir = checkout(t);
    for (const project of ["", "rhythm", "RHYTHM"]) {
      const result = await run(dir, { RHYTHM_MODEL_DIR: join(dir, "model"), COMPOSE_PROJECT_NAME: project }, script);
      assert.equal(result.status, 2, `${JSON.stringify(project)}: ${result.said}`);
      assert.match(result.said, /is empty or an install's default \(rhythm\): give the run its own \(COMPOSE_PROJECT_NAME\)/);
      assert.deepEqual(result.calls, [], `${JSON.stringify(project)}: no docker call`);
    }
  });
}
// Every Compose call a run makes names its project first.
const named = (compose, project) => {
  for (const line of compose) assert.ok(line.split("|")[2].startsWith(`compose -p ${project} `), line);
};

for (const script of ["e2e.sh", "lifecycle.sh", "journeys.sh"]) {
  test(`cli/${script} refuses to start without the pinned MiniLM files, calling no docker`, { skip: !tools }, async (t) => {
    const dir = checkout(t);
    const none = await run(dir, {}, script);
    assert.equal(none.status, 2, none.said);
    assert.match(none.said, /RHYTHM_MODEL_DIR/);
    assert.match(none.said, /D-041/);
    assert.deepEqual(none.calls, []);
    unlinkSync(join(dir, "model", "vocab.txt"));
    const half = await run(dir, { RHYTHM_MODEL_DIR: join(dir, "model") }, script);
    assert.equal(half.status, 2, half.said);
    assert.deepEqual(half.calls, []);
  });

  test(`cli/${script} refuses a project that already has containers or volumes, before any compose call`, { skip: !tools }, async (t) => {
    const dir = checkout(t);
    const model = join(dir, "model");
    for (const fake of [{ FAKE_CONTAINERS: "c0ffee" }, { FAKE_VOLUMES: "someone_pgdata" }]) {
      const result = await run(dir, { RHYTHM_MODEL_DIR: model, COMPOSE_PROJECT_NAME: "someone", ...fake }, script);
      assert.equal(result.status, 2, result.said);
      assert.match(result.said, /someone already has containers or volumes/);
      assert.ok(result.calls.some((line) => line.includes("label=com.docker.compose.project=someone")), result.calls.join("\n"));
      assert.deepEqual(composeCalls(result.calls), [], "nothing is started or removed");
    }
  });

  test(`cli/${script} refuses an image tag another stack's containers run`, { skip: !tools }, async (t) => {
    const dir = checkout(t);
    const extra = { RHYTHM_MODEL_DIR: join(dir, "model"), RHYTHM_VERSION: "shared", FAKE_RUNS: "c0ffee" };
    // e2e.sh retags its images as <tag>-previous for its upgrade steps, moves them to <tag>-next at its last, and builds the
    // backup image; lifecycle.sh and journeys.sh build the engine and app images under their tag alone.
    const images = script === "e2e.sh"
      ? ["rhythm-engine:shared", "rhythm-app:shared-previous", "rhythm-backup:shared", "rhythm-engine:shared-next", "rhythm-app:shared-next-previous"]
      : ["rhythm-engine:shared", "rhythm-app:shared"];
    for (const image of images) {
      const result = await run(dir, { ...extra, FAKE_IMAGE: image }, script);
      assert.equal(result.status, 2, result.said);
      assert.match(result.said, new RegExp(`run ${image}`));
      assert.deepEqual(composeCalls(result.calls), []);
    }
  });

  // A stopped install runs no container under its tag, but its next up runs what the tag names: an image another Compose
  // project built, or none did, is refused too; one this run's own project built before is its own to build again.
  test(`cli/${script} refuses an image tag another Compose project built, even with no container running it`, { skip: !tools }, async (t) => {
    const dir = checkout(t);
    const extra = { RHYTHM_MODEL_DIR: join(dir, "model"), RHYTHM_VERSION: "shared", COMPOSE_PROJECT_NAME: "guards" };
    const images = script === "e2e.sh"
      ? ["rhythm-engine:shared", "rhythm-app:shared-previous", "rhythm-backup:shared", "rhythm-engine:shared-next", "rhythm-app:shared-next-previous"]
      : ["rhythm-engine:shared", "rhythm-app:shared"];
    for (const image of images)
      for (const label of ["rhythm", ""]) {
        const result = await run(dir, { ...extra, FAKE_IMAGES: image, FAKE_LABEL: label }, script);
        assert.equal(result.status, 2, result.said);
        assert.match(result.said, new RegExp(`${image} is already here, built by ${label || "no Compose project"}, not by guards`));
        assert.deepEqual(composeCalls(result.calls), [], "nothing is built or started");
      }
    const own = await run(dir, { ...extra, FAKE_IMAGES: images.join(" "), FAKE_LABEL: "guards" }, script);
    assert.equal(own.status, 1, own.said);
    assert.doesNotMatch(own.said, /is already here/);
    assert.ok(composeCalls(own.calls).length > 0, "past the checks");
  });

  test(`cli/${script} refuses a port something already answers on`, { skip: !tools }, async (t) => {
    const dir = checkout(t);
    const server = createServer((_, response) => response.end("busy"));
    await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
    t.after(() => server.close());
    const { port } = server.address();
    for (const variable of ["RHYTHM_APP_PORT", "RHYTHM_MCP_PORT"]) {
      const result = await run(dir, { RHYTHM_MODEL_DIR: join(dir, "model"), [variable]: String(port) }, script);
      assert.equal(result.status, 2, result.said);
      assert.match(result.said, new RegExp(`127\\.0\\.0\\.1:${port}`));
      assert.deepEqual(composeCalls(result.calls), []);
    }
  });

  test(`cli/${script} refuses a checkout with an install`, { skip: !tools }, async (t) => {
    const dir = checkout(t);
    writeFileSync(join(dir, "deploy", "compose", ".env"), "COMPOSE_PROJECT_NAME=rhythm\n");
    const result = await run(dir, { RHYTHM_MODEL_DIR: join(dir, "model") }, script);
    assert.equal(result.status, 2, result.said);
    assert.match(result.said, /An install exists/);
    assert.deepEqual(result.calls, []);
  });
}

test("cli/e2e.sh gives every compose call the extra file and the model folder", { skip: !tools }, async (t) => {
  const dir = checkout(t);
  // Past the checks the stand-in docker fails the first step (no clear message from postgres), and the cleanup runs.
  const result = await run(dir, { RHYTHM_MODEL_DIR: join(dir, "model"), COMPOSE_PROJECT_NAME: "e2e-guards" });
  assert.equal(result.status, 1, result.said);
  assert.match(result.said, /FAILED: no clear message from postgres/);
  const compose = composeCalls(result.calls);
  named(compose, "e2e-guards");
  assert.ok(compose.some((line) => line.endsWith("|compose -p e2e-guards up -d postgres")), compose.join("\n"));
  assert.ok(compose.some((line) => line.includes("down -v --remove-orphans")), "the cleanup runs");
  for (const line of compose) {
    const [files, model] = line.split("|");
    assert.equal(files, "compose.yaml:../../cli/e2e.compose.yaml", line);
    assert.match(model.replaceAll("\\", "/"), /\/model$/, line);
    // Docker on Windows reads C:/ paths, not Git Bash's /c/ ones.
    if (process.platform === "win32") assert.match(model, /^[A-Za-z]:\//, line);
  }
});

test("cli/lifecycle.sh builds this checkout's images first, every compose call with the extra file, and pulls nothing", { skip: !tools }, async (t) => {
  const dir = checkout(t);
  // Past the checks the stand-in docker builds, then init cannot run (the stand-in checkout has no ./rhythm), and the
  // cleanup runs.
  const result = await run(dir, { RHYTHM_MODEL_DIR: join(dir, "model"), COMPOSE_PROJECT_NAME: "lifecycle-guards" }, "lifecycle.sh");
  assert.equal(result.status, 1, result.said);
  assert.match(result.said, /FAILED: init/);
  const compose = composeCalls(result.calls);
  named(compose, "lifecycle-guards");
  assert.ok(compose[0]?.endsWith("|compose -p lifecycle-guards build engine app"), compose.join("\n"));
  assert.ok(compose.some((line) => line.includes("down -v --remove-orphans")), "the cleanup runs");
  for (const line of compose) {
    const [files, model] = line.split("|");
    assert.equal(files, "compose.yaml:../../cli/e2e.compose.yaml", line);
    assert.match(model.replaceAll("\\", "/"), /\/model$/, line);
    if (process.platform === "win32") assert.match(model, /^[A-Za-z]:\//, line);
  }
  // It never pulls an image or moves a tag, so it records and puts back none (cli/e2e.sh's upgrade steps do, below); it
  // reads only which Compose project built its own tag's images.
  const args = result.calls.map((line) => line.split("|")[2]);
  assert.deepEqual(args.filter((a) => /(^| )pull( |$)|--pull|^tag /.test(a) || (/^image inspect/.test(a) && !/Labels/.test(a))), []);
  assert.deepEqual(args.filter((a) => /^image inspect/.test(a)).map((a) => a.split(" ").at(-1)), ["rhythm-engine:lifecycle", "rhythm-app:lifecycle"]);
  const code = readFileSync(join(root, "cli", "lifecycle.sh"), "utf8").replace(/^\s*#.*$/gm, "");
  assert.doesNotMatch(code, /--pull|docker pull|docker tag|\bdc pull|rhythm upgrade/);
});

test("cli/journeys.sh refuses to start without the app's tests installed, and builds this checkout's images first, pulling nothing", { skip: !tools }, async (t) => {
  const dir = checkout(t);
  unlinkSync(join(dir, "app", "node_modules", ".bin", "vitest"));
  const none = await run(dir, { RHYTHM_MODEL_DIR: join(dir, "model") }, "journeys.sh");
  assert.equal(none.status, 2, none.said);
  assert.match(none.said, /npm --prefix app ci/);
  assert.deepEqual(none.calls, []);
  writeFileSync(join(dir, "app", "node_modules", ".bin", "vitest"), "stand-in");
  // Past the checks the stand-in docker builds, then init cannot run (the stand-in checkout has no ./rhythm), and the
  // cleanup runs.
  const result = await run(dir, { RHYTHM_MODEL_DIR: join(dir, "model"), COMPOSE_PROJECT_NAME: "journeys-guards" }, "journeys.sh");
  assert.equal(result.status, 1, result.said);
  assert.match(result.said, /FAILED: init/);
  const compose = composeCalls(result.calls);
  named(compose, "journeys-guards");
  assert.ok(compose[0]?.endsWith("|compose -p journeys-guards build engine app"), compose.join("\n"));
  assert.ok(compose.some((line) => line.includes("down -v --remove-orphans")), "the cleanup runs");
  for (const line of compose) assert.equal(line.split("|")[0], "compose.yaml:../../cli/e2e.compose.yaml", line);
  const code = readFileSync(join(root, "cli", "journeys.sh"), "utf8").replace(/^\s*#.*$/gm, "");
  assert.doesNotMatch(code, /--pull|docker pull|docker tag|\bdc pull|rhythm upgrade/);
});

// The upgrade steps pull the stack's images and build with --pull (D-133), which can move a third-party tag that other
// stacks and engine/test-db.sh share to upstream's newest build. The run records each such image this machine has by
// id before it starts anything, and its cleanup puts a moved tag back on that image, leaving the others alone.
test("cli/e2e.sh puts each third-party image tag a pull moved back on the image it named", { skip: !tools }, async (t) => {
  const dir = checkout(t);
  const files = [join("deploy", "compose", "engine.compose.yaml"), join("engine", "Dockerfile"), join("engine", "backup", "Dockerfile"), join("app", "Dockerfile")];
  for (const file of files) {
    mkdirSync(dirname(join(dir, file)), { recursive: true });
    copyFileSync(join(root, file), join(dir, file));
  }
  const read = (file) => readFileSync(join(root, file), "utf8");
  const postgres = /\$\{RHYTHM_POSTGRES_IMAGE:-([^}]*)\}/.exec(read(files[0]))[1];
  const ollama = /\$\{RHYTHM_OLLAMA_IMAGE:-([^}]*)\}/.exec(read(files[0]))[1];
  const sdk = /^FROM (\S+)/m.exec(read(files[1]))[1];
  const node = /^ARG NODE_IMAGE=(\S+)/m.exec(read(files[3]))[1];
  // Postgres, the SDK and Node are here; Ollama is not (never pulled on this machine); Postgres's tag moves meanwhile.
  const result = await run(dir, {
    RHYTHM_MODEL_DIR: join(dir, "model"), COMPOSE_PROJECT_NAME: "e2e-pins", FAKE_IMAGES: [postgres, sdk, node].join(" "), FAKE_MOVED: postgres,
  });
  assert.equal(result.status, 1, result.said);
  const args = result.calls.map((line) => line.split("|")[2]);
  const first = args.findIndex((a) => a.startsWith("compose"));
  for (const ref of [postgres, ollama, sdk, node])
    assert.ok(args.slice(0, first).includes(`image inspect -f {{.Id}} ${ref}`), `${ref} is recorded before anything starts`);
  assert.deepEqual(args.filter((a) => a.startsWith("tag ")), [`tag sha256:before ${postgres}`]);
  assert.match(result.said, new RegExp(`${postgres.replace(/[.*+?^${}()|[\]\\/]/g, "\\$&")} had moved; tagged back`));
  assert.ok(args.lastIndexOf(`tag sha256:before ${postgres}`) > args.findLastIndex((a) => a.includes("down -v")), "after the stack is down");
});

// The engine and the cli service (rhythm recall's meaning arm, SB-P3-19) read the model from the host folder, read-only;
// no other service gets it.
test("cli/e2e.compose.yaml mounts the model folder read-only and points the engine and the cli at it", () => {
  const text = readFileSync(join(root, "cli", "e2e.compose.yaml"), "utf8");
  for (const service of ["engine", "cli"]) {
    const start = text.indexOf(`\n  ${service}:\n`);
    assert.ok(start >= 0, `${service} is in the file`);
    const rest = text.slice(start + 1);
    const next = rest.slice(1).search(/\n {2}\S/);
    const block = next < 0 ? rest : rest.slice(0, next + 2);
    assert.match(block, /\n {6}RHYTHM_MODEL_DIR: \/models\/minilm\n/, service);
    assert.match(block, /\n {6}- type: bind\n {8}source: \$\{RHYTHM_E2E_MODEL_DIR:\?[^}]*\}\n {8}target: \/models\/minilm\n {8}read_only: true\n/, service);
  }
  assert.doesNotMatch(text, /\n {2}(migrate|app|postgres|backup|restore):/, "only the engine and the cli get the model");
});
