import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

// deploy/image-pins.txt against the files: every third-party image the stack runs or builds from is listed, each dated
// pin is set where the list says, and every mention of a pinned image in the repository names the pinned version.
const root = fileURLToPath(new URL("..", import.meta.url));
const read = (path) => readFileSync(join(root, path), "utf8");
const tracked = spawnSync("git", ["ls-files", "-z"], { cwd: root, encoding: "utf8" }).stdout.split("\0").filter(Boolean);

const pins = read("deploy/image-pins.txt")
  .split(/\r?\n/)
  .filter((line) => line.trim() && !line.startsWith("#"))
  .map((line) => {
    const [image, since, where] = line.trim().split(/\s+/);
    return { image, since, where };
  });

// Defaults of `image: ${VAR:-<image>}` in the deploy compose files, and the bases the Dockerfiles build FROM.
function usedImages() {
  const images = new Set();
  for (const path of tracked.filter((p) => p.startsWith("deploy/") && p.endsWith(".yaml"))) {
    for (const [, value] of read(path).matchAll(/^\s*image:\s*(\S+)/gm)) {
      const image = value.match(/^\$\{[A-Z0-9_]+:-(.+)\}$/)?.[1] ?? value;
      if (!image.startsWith("rhythm-")) images.add(image);
    }
  }
  for (const path of tracked.filter((p) => /(?:^|\/)Dockerfile$/.test(p))) {
    const args = new Map();
    const stages = new Set();
    for (const line of read(path).split(/\r?\n/)) {
      const arg = line.match(/^ARG\s+(\w+)=(\S+)/);
      if (arg) args.set(arg[1], arg[2]);
      const from = line.match(/^FROM\s+(?:--platform=\S+\s+)?(\S+)(?:\s+AS\s+(\S+))?/i);
      if (!from) continue;
      const image = from[1].replace(/^\$\{?(\w+)\}?$/, (whole, name) => args.get(name) ?? whole);
      if (!stages.has(image)) images.add(image);
      if (from[2]) stages.add(from[2]);
    }
  }
  return images;
}

test("the pin list names exactly the images the deploy files and Dockerfiles use", () => {
  assert.deepEqual([...usedImages()].sort(), pins.map((p) => p.image).sort());
  assert.equal(new Set(pins.map((p) => p.image)).size, pins.length, "an image is listed twice");
});

test("every image has a SHA-256 digest and a valid check date at its declared location", () => {
  const tomorrow = Date.now() + 24 * 3600 * 1000;
  for (const { image, since, where } of pins) {
    assert.match(image, /^[^@]+:[^@]+@sha256:[0-9a-f]{64}$/, image);
    assert.match(since, /^\d{4}-\d{2}-\d{2}$/, image);
    const day = Date.parse(`${since}T00:00:00Z`);
    assert.ok(!Number.isNaN(day) && new Date(day).toISOString().startsWith(since) && day < tomorrow, `${image}: ${since}`);
    assert.ok(where && existsSync(join(root, where)), `${image}: ${where}`);
    assert.ok(read(where).includes(image), `${where} does not set ${image}`);
  }
});

test("every mention of a pinned image names the pinned version", () => {
  const skip = (path) => /(?:^|\/)(?:packages\.lock\.json|package-lock\.json)$/.test(path) || path === "deploy/image-pins.txt";
  const texts = tracked.filter((p) => !skip(p) && existsSync(join(root, p))).map((p) => [p, readFileSync(join(root, p))]);
  const stale = [];
  for (const { image } of pins) {
    const [taggedImage, digest] = image.split("@");
    const at = taggedImage.lastIndexOf(":");
    const name = taggedImage.slice(0, at).replace(/[.*+?^${}()|[\]\\/]/g, "\\$&");
    const mention = new RegExp(`(?<![\\w.@/-])${name}:(v?[0-9][A-Za-z0-9._-]*[A-Za-z0-9]|[0-9])(?:@(sha256:[a-f0-9]{64}))?`, "g");
    for (const [path, bytes] of texts) {
      if (bytes.includes(0)) continue;
      for (const [whole, tag, hash] of bytes.toString("utf8").matchAll(mention))
        if (tag !== taggedImage.slice(at + 1) || (hash && hash !== digest)) stale.push(`${path}: ${whole}`);
    }
  }
  assert.deepEqual(stale, []);
});

test("engine/global.json accepts the SDK the engine image builds with", () => {
  const { version, rollForward = "latestPatch" } = JSON.parse(read("engine/global.json")).sdk;
  const sdk = read("engine/Dockerfile").match(/^FROM\s+mcr\.microsoft\.com\/dotnet\/sdk:(\d+)\.(\d+)\.(\d)(\d\d)\b/m);
  assert.ok(sdk, "engine/Dockerfile builds FROM an exact dotnet/sdk release");
  const [major, minor, band, patch] = version.match(/^(\d+)\.(\d+)\.(\d)(\d\d)$/).slice(1).map(Number);
  const [imageMajor, imageMinor, imageBand, imagePatch] = sdk.slice(1).map(Number);
  assert.deepEqual([imageMajor, imageMinor], [major, minor], "the same .NET release");
  const accepted = {
    latestPatch: imageBand === band && imagePatch >= patch,
    latestFeature: imageBand > band || (imageBand === band && imagePatch >= patch),
  };
  assert.ok(accepted[rollForward], `global.json (${version}, ${rollForward}) refuses the image's SDK`);
});

test("upgrades pull the configured image bases when rebuilding", () => {
  for (const wrapper of ["rhythm", "rhythm.ps1"])
    assert.match(read(wrapper), /\bdc --profile backup build --pull(?=[\s;|&}]|$)/m);
  const builds = [...read("deploy/truenas/README.md").matchAll(/docker compose [^`\n]*\bbuild\b[^`\n]*/g)].map((m) => m[0]);
  assert.ok(builds.length >= 2);
  for (const command of builds) assert.match(command, /--pull\b/, command);
});
