import { devApi } from "./server/devApi.js";
import { thirdPartyLicences } from "./scripts/thirdPartyLicences.js";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";
import type { Plugin } from "vite";
import tailwindcss from "@tailwindcss/vite";
import { VitePWA } from "vite-plugin-pwa";
import { readFileSync } from "node:fs";

// pdf.js loads its image decoders (WebAssembly, for scanned pages) by name from one folder, so the
// build keeps their names, in a folder named after pdf.js's version: a cached copy never meets
// another version's worker. Everything else keeps Vite's hashed names.
const pdfjs: string = JSON.parse(
  readFileSync(
    new URL("./node_modules/pdfjs-dist/package.json", import.meta.url),
    "utf8",
  ),
).version;

// The licences of what the PDF reader ships (pdf.js, Apache-2.0; its image decoders, BSD and
// MIT), in the same folder as the decoders.
function pdfjsLicences(): Plugin {
  const files: [string, string][] = [
    ["LICENSE", "LICENSE-pdfjs.txt"],
    ["wasm/LICENSE_JBIG2", "LICENSE-jbig2.txt"],
    ["wasm/LICENSE_PDFJS_JBIG2", "LICENSE-pdfjs-jbig2.txt"],
    ["wasm/LICENSE_OPENJPEG", "LICENSE-openjpeg.txt"],
    ["wasm/LICENSE_PDFJS_OPENJPEG", "LICENSE-pdfjs-openjpeg.txt"],
    ["wasm/LICENSE_QCMS", "LICENSE-qcms.txt"],
    ["wasm/LICENSE_PDFJS_QCMS", "LICENSE-pdfjs-qcms.txt"],
  ];
  return {
    name: "rhythm-pdfjs-licences",
    apply: "build",
    generateBundle() {
      for (const [from, name] of files)
        this.emitFile({
          type: "asset",
          fileName: `assets/pdfjs-${pdfjs}/${name}`,
          source: readFileSync(
            new URL(`./node_modules/pdfjs-dist/${from}`, import.meta.url),
          ),
        });
    },
  };
}

// Keep the fonts' own copyright notices and OFL with their distributed WOFF2 files,
// even after the Docker runtime prunes the font packages as dev dependencies.
function fontLicences(): Plugin {
  return {
    name: "rhythm-font-licences",
    apply: "build",
    generateBundle() {
      for (const font of ["dm-sans", "manrope"])
        this.emitFile({
          type: "asset",
          fileName: `assets/LICENSE-${font}.txt`,
          source: readFileSync(
            new URL(
              `./node_modules/@fontsource-variable/${font}/LICENSE`,
              import.meta.url,
            ),
          ),
        });
    },
  };
}

// https://vite.dev/config/
export default defineConfig({
  server: { host: "127.0.0.1" },
  build: {
    rolldownOptions: {
      input: {
        main: "index.html",
        launcherCard: "launcher-card.html",
        launcher: "launcher.html",
      },
      output: {
        assetFileNames: (asset) =>
          asset.names.some((n) => n.endsWith(".wasm"))
            ? `assets/pdfjs-${pdfjs}/[name][extname]`
            : "assets/[name]-[hash][extname]",
      },
    },
  },
  plugins: [
    devApi(),
    pdfjsLicences(),
    fontLicences(),
    thirdPartyLicences(),
    react(),
    tailwindcss(),
    VitePWA({
      registerType: "autoUpdate",
      // Send cookies with the manifest request too, for a front door (an access proxy or a
      // hosted platform's login) that signs every request in.
      useCredentials: true,
      includeAssets: ["favicon.svg", "icon-192.png", "icon-512.png"],
      manifest: {
        name: "Rhythm of Life",
        short_name: "Rhythm",
        description:
          "A quiet space for source-aware health signals and connected ideas.",
        theme_color: "#0b1015",
        background_color: "#0b1015",
        display: "standalone",
        start_url: "/",
        id: "/",
        icons: [
          { src: "/icon-192.png", sizes: "192x192", type: "image/png" },
          {
            src: "/icon-512.png",
            sizes: "512x512",
            type: "image/png",
            purpose: "any maskable",
          },
        ],
      },
      workbox: {
        globPatterns: ["**/*.{js,css,html,svg,png,woff2}"],
        globIgnores: [
          "**/Details-*.js",
          "**/HealthChart-*.js",
          "**/Explorer-*.js",
          "**/Explorer-*.css",
          // The PDF reader (pdf.js) loads only when a PDF is opened.
          "**/PdfView-*.js",
          "**/PdfView-*.css",
        ],
        navigateFallback: "/index.html",
        navigateFallbackDenylist: [
          /^\/api(?:\/|$)/,
          /^\/launcher(?:-card)?\.html$/,
        ],
        runtimeCaching: [],
        cleanupOutdatedCaches: true,
      },
    }),
  ],
});
