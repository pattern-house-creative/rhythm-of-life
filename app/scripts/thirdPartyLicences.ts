import { readFileSync, readdirSync } from "node:fs";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import type { Plugin } from "vite";

const app = fileURLToPath(new URL("../", import.meta.url));

// Package metadata alone does not preserve copyright notices. Collect the original
// legal files for packages whose modules enter the browser, plus the unbundled
// server dependencies and Workbox's separately generated service worker.
export function thirdPartyLicences(): Plugin {
  return {
    name: "rhythm-third-party-licences",
    apply: "build",
    generateBundle() {
      for (const file of ["LICENSE", "NOTICE"])
        this.emitFile({
          type: "asset",
          fileName: `assets/${file}-rhythm.txt`,
          source: readFileSync(join(app, file)),
        });
      const roots = new Set<string>();
      for (const id of this.getModuleIds()) {
        const clean = id.replaceAll("\\", "/").split("?")[0];
        const marker = clean.lastIndexOf("/node_modules/");
        if (marker < 0) continue;
        const parts = clean.slice(marker + 14).split("/");
        const count = parts[0].startsWith("@") ? 2 : 1;
        roots.add(
          clean.slice(0, marker + 14) + parts.slice(0, count).join("/"),
        );
      }
      for (const name of [
        "undici",
        "yaml",
        ...readdirSync(join(app, "node_modules")).filter((n) =>
          n.startsWith("workbox-"),
        ),
      ])
        roots.add(resolve(app, "node_modules", name).replaceAll("\\", "/"));

      const packages = [...roots]
        .map((root) => {
          // Vite may represent a module in a package's nested folder. Only package
          // roots selected above are read; no machine paths enter the emitted file.
          const info = JSON.parse(
            readFileSync(join(root, "package.json"), "utf8"),
          );
          const legal = readdirSync(root, { withFileTypes: true })
            .filter(
              (f) =>
                f.isFile() &&
                /^(?:(?:licen[cs]e|copying|notices?)(?:[._-].*)?|third[-_]?party.*)$/i.test(
                  f.name,
                ),
            )
            .map((f) => f.name)
            .sort();
          if (!legal.some((name) => /^(licen[cs]e|copying)/i.test(name)))
            this.error(`Missing licence text for ${info.name}@${info.version}`);
          return {
            name: info.name as string,
            version: info.version as string,
            license: info.license as string,
            notices: legal
              .map(
                (name) =>
                  `--- ${name} ---\n${readFileSync(join(root, name), "utf8").trim()}`,
              )
              .join("\n\n"),
          };
        })
        .sort((a, b) =>
          `${a.name}@${a.version}`.localeCompare(
            `${b.name}@${b.version}`,
            "en",
          ),
        );
      this.emitFile({
        type: "asset",
        fileName: "assets/THIRD-PARTY-NOTICES.txt",
        source:
          "Rhythm of Life — third-party software\nOriginal package licences and notices; each component keeps its own licence.\n\n" +
          packages
            .map(
              (p) =>
                `=== ${p.name}@${p.version} (${p.license}) ===\n\n${p.notices}`,
            )
            .join("\n\n"),
      });
      this.emitFile({
        type: "asset",
        fileName: "assets/third-party-packages.json",
        source:
          JSON.stringify(
            packages.map(({ notices: _notices, ...p }) => p),
            null,
            2,
          ) + "\n",
      });
    },
  };
}
