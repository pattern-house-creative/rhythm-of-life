import fs from "node:fs";
import path from "node:path";
import { createHash } from "node:crypto";
import { fileURLToPath } from "node:url";

const repo = fileURLToPath(new URL("../", import.meta.url));
const cache = process.env.NUGET_PACKAGES;
if (!cache)
  throw Error(
    "Set NUGET_PACKAGES to the restored package cache before collecting public license documents.",
  );
const out = path.join(repo, "docs/licenses");
fs.mkdirSync(out, { recursive: true });
const sha = (s) => createHash("sha256").update(s).digest("hex");
const readJson = (f) => JSON.parse(fs.readFileSync(path.join(repo, f), "utf8"));
const unescape = (s) =>
  s
    ?.replaceAll("&amp;", "&")
    .replaceAll("&lt;", "<")
    .replaceAll("&gt;", ">")
    .replaceAll("&quot;", '"');
async function get(url) {
  const r = await fetch(url);
  if (r.status === 404) return null;
  if (!r.ok) throw Error(`${r.status} ${url}`);
  return await r.text();
}
const legalName = (n) =>
  /^(?:(?:licen[cs]e|copying|notices?)(?:[._-].*)?|third[-_]?party.*)$/i.test(
    n,
  );
const all = new Map();
for (const [role, file] of [
  ["runtime", "engine/src/Core/packages.lock.json"],
  ["test", "engine/tests/Knowledge.Tests/packages.lock.json"],
]) {
  for (const [tfm, packages] of Object.entries(readJson(file).dependencies))
    for (const [name, m] of Object.entries(packages)) {
      if (m.type === "Project") continue;
      const key = `${name}@${m.resolved}`;
      const entry = all.get(key) || {
        name,
        version: m.resolved,
        roles: [],
        contentHash: m.contentHash,
      };
      if (!entry.roles.includes(role)) entry.roles.push(role);
      all.set(key, entry);
    }
}

for (const p of [...all.values()].sort((a, b) =>
  a.name.localeCompare(b.name, "en"),
)) {
  const dir = path.join(cache, p.name.toLowerCase(), p.version);
  const files = fs.readdirSync(dir, { withFileTypes: true });
  const spec = fs.readFileSync(
    path.join(dir, files.find((f) => f.name.endsWith(".nuspec")).name),
    "utf8",
  );
  const license = spec.match(/<license type="(.*?)">(.*?)<\/license>/);
  p.licenseType = license?.[1] || "legacy-url";
  p.license = license?.[2] || null;
  p.licenseUrl = unescape(spec.match(/<licenseUrl>(.*?)<\/licenseUrl>/)?.[1]);
  p.copyright =
    unescape(spec.match(/<copyright>(.*?)<\/copyright>/)?.[1]) || null;
  const repository = spec.match(/<repository\b[^>]+>/)?.[0];
  p.repository = unescape(repository?.match(/url="(.*?)"/)?.[1]) || null;
  p.commit = repository?.match(/commit="(.*?)"/)?.[1] || null;
  p.notices = [];
  if (p.roles.includes("runtime")) {
    let docs = files
      .filter((f) => f.isFile() && legalName(f.name))
      .map((f) => ({
        file: f.name,
        text: fs.readFileSync(path.join(dir, f.name), "utf8"),
        source: `NuGet ${p.name}@${p.version}/${f.name}`,
      }));
    if (!docs.some((d) => /^licen[cs]e/i.test(d.file))) {
      if (!p.commit || !p.repository?.startsWith("https://github.com/"))
        throw Error("No pinned source " + p.name);
      const slug = p.repository
        .replace("https://github.com/", "")
        .replace(/\.git$/, "");
      const base =
        "https://raw.githubusercontent.com/" + slug + "/" + p.commit + "/";
      const listing = JSON.parse(
        await get(
          "https://api.github.com/repos/" + slug + "/contents?ref=" + p.commit,
        ),
      );
      for (const item of listing.filter(
        (x) => x.type === "file" && legalName(x.name),
      )) {
        const text = await get(base + item.name);
        if (text)
          docs.push({
            // Package and repository notices can differ only in case. Keep both on Windows too.
            file: docs.some((d) => d.file.toLowerCase() === item.name.toLowerCase())
              ? "SOURCE-" + item.name : item.name,
            text, source: base + item.name,
          });
      }
    }
    if (!docs.some((d) => /^licen[cs]e/i.test(d.file)))
      throw Error("Missing licence " + p.name);
    const folder = path.join(out, "nuget", p.name + "-" + p.version);
    fs.mkdirSync(folder, { recursive: true });
    for (const d of docs) {
      fs.writeFileSync(path.join(folder, d.file), d.text);
      p.notices.push({
        file: `nuget/${p.name}-${p.version}/${d.file}`,
        source: d.source,
        sha256: sha(d.text),
      });
    }
    if (p.licenseType === "file")
      p.license = /MIT License/.test(docs[0].text) ? "MIT" : p.license;
    if (p.name === "PdfPig") {
      for (const name of ["glyphlist", "zapfdingbats", "additional"]) {
        const url = `https://raw.githubusercontent.com/UglyToad/PdfPig/${p.commit}/src/UglyToad.PdfPig.Fonts/Resources/GlyphList/${name}`;
        const text = await get(url);
        const lines = text.split(String.fromCharCode(10));
        const end = lines.findIndex((l) => !l.startsWith("#") && l.trim());
        const notice =
          lines.slice(0, end).join(String.fromCharCode(10)) +
          String.fromCharCode(10);
        const file = `NOTICE-${name}.txt`;
        fs.writeFileSync(path.join(folder, file), notice);
        p.notices.push({
          file: `nuget/${p.name}-${p.version}/${file}`,
          source: url,
          sourceSha256: sha(text),
          extraction:
            "Unchanged leading comment header, excluding glyph mappings",
          sha256: sha(notice),
        });
      }
    }
  }
  if (p.name === "xunit.abstractions") {
    const url = p.licenseUrl;
    const text = await get(url);
    if (text) {
      p.license = "Apache-2.0";
      p.licenseEvidence = { url, sha256: sha(text) };
    }
  }
  console.log(p.name, p.version, p.license, p.notices.length);
}
fs.writeFileSync(
  path.join(out, "nuget.json"),
  JSON.stringify(
    {
      schemaVersion: 1,
      packages: [...all.values()].sort((a, b) =>
        `${a.name}@${a.version}`.localeCompare(`${b.name}@${b.version}`, "en"),
      ),
    },
    null,
    2,
  ) + "\n",
);

const models = [
  [
    "minilm",
    "Xenova/all-MiniLM-L6-v2",
    "751bff37182d3f1213fa05d7196b954e230abad9",
  ],
  [
    "bge-small",
    "michaelfeil/bge-small-en-v1.5",
    "ab7b31bd10f9bfbb915a28662ec4726b06c6552a",
    "BAAI/bge-small-en-v1.5",
  ],
  [
    "bge-base",
    "BAAI/bge-base-en-v1.5",
    "a5beb1e3e68b9ab74eb54cfd186867f64f240e1a",
  ],
  [
    "bge-reranker-base",
    "BAAI/bge-reranker-base",
    "2cfc18c9415c912f9d8155881c133215df768a70",
  ],
  [
    "ms-marco-minilm",
    "cross-encoder/ms-marco-MiniLM-L6-v2",
    "233902d25c440f23af6f7d6e94d2946bac0bee0a",
  ],
];
const evidence = [];
for (const [name, repository, revision, upstream] of models) {
  const base = `https://huggingface.co/${repository}/raw/${revision}/`;
  const card = await get(base + "README.md");
  const p = {
    name,
    repository,
    revision,
    license: card?.match(/^license:\s*(.*)$/m)?.[1],
    card: base + "README.md",
    cardSha256: sha(card || ""),
    notices: [],
  };
  if (!p.license)
    p.licenseStatus = "No license declaration in the pinned export model card";
  fs.mkdirSync(path.join(out, "models", name), { recursive: true });
  for (const file of ["LICENSE", "LICENSE.txt", "LICENSE.md", "NOTICE"]) {
    const text = await get(base + file);
    if (text) {
      fs.writeFileSync(path.join(out, "models", name, file), text);
      p.notices.push({
        file: `models/${name}/${file}`,
        source: base + file,
        sha256: sha(text),
      });
    }
  }
  const original = upstream ?? card?.match(
    /^base_model:[ \t]*([^\r\n ]+\/[^\r\n ]+)[ \t]*$/m,
  )?.[1];
  if (original) {
    const metadata = JSON.parse(
      await get(`https://huggingface.co/api/models/${original}`),
    );
    const originalCardUrl = `https://huggingface.co/${original}/raw/${metadata.sha}/README.md`;
    const originalCard = await get(originalCardUrl);
    p.original = {
      repository: original,
      revision: metadata.sha,
      license: originalCard?.match(/^license:\s*(.*)$/m)?.[1],
      card: originalCardUrl,
      cardSha256: sha(originalCard || ""),
    };
    for (const file of ["LICENSE", "LICENSE.txt", "LICENSE.md", "NOTICE"]) {
      const url = `https://huggingface.co/${original}/raw/${metadata.sha}/${file}`;
      const text = await get(url);
      if (text) {
        fs.writeFileSync(
          path.join(out, "models", name, "original-" + file),
          text,
        );
        p.notices.push({
          file: `models/${name}/original-${file}`,
          source: url,
          sha256: sha(text),
        });
      }
    }
  }
  evidence.push(p);
  console.log("Model", name, p.license, p.notices.length);
}
fs.writeFileSync(
  path.join(out, "models.json"),
  JSON.stringify({ schemaVersion: 1, models: evidence }, null, 2) + "\n",
);
