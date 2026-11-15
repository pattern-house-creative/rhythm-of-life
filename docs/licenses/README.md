# Third-party licences

Inventory checked 9 November 2026 against `main` at `f360a541`. Rhythm's own code remains Apache-2.0;
each dependency and asset retains its own terms. This inventory covers the core repository, not the
separate CareLink plugin, a user's data, or software a user chooses to add. The Android launcher's
packages were added on 15 November 2026.

## Packages and distributed notices

| Inventory | Coverage | Notices in the built distribution |
|---|---|---|
| [npm.json](npm.json) | all 593 lockfile entries, including optional platform variants; `dev` is npm's installation classification, not a statement that code never reaches the browser | `app/dist/assets/THIRD-PARTY-NOTICES.txt` and `third-party-packages.json`, generated from the browser module graph, the two unbundled server packages and a conservative set of Workbox packages |
| [nuget.json](nuget.json) | all 57 unique name/version pairs in both lockfiles; 12 runtime packages, 45 additional test-only packages | `licenses/nuget/` and `licenses/nuget.json` in the engine's publish output and final image |
| [gradle.json](gradle.json) | the Android launcher (`apps/android-launcher`): all 110 packages in its `launcher/dependencies.txt`, with the licence each declares in its POM. 88 go into the APK, all Apache-2.0; 22 more are used by its tests only (18 Apache-2.0, three BSD-3-Clause, one EPL-1.0) | none: the project publishes no APK. A build packages the two fonts' OFL texts as assets; an APK given to others would also need the packages' own licences and notices |
| [models.json](models.json) | five exact model pins, their model-card URLs and hashes; original-model evidence recorded separately from an export's terms | no model weights are bundled by this change; redistribution of weights needs their own accompanying licences and notices |

The app emits Rhythm's `LICENSE` and `NOTICE`, too, as `assets/LICENSE-rhythm.txt` and
`assets/NOTICE-rhythm.txt`. The engine publishes them as `LICENSE` and `NOTICE`.
The app's legal files remain after `npm prune --omit=dev`; React, chart libraries and fonts are
build-time npm dependencies whose output is nevertheless distributed. The emitted package list is
conservative: being listed does not mean that every file of that package is shipped.

Original NuGet legal documents are preserved in [nuget/](nuget/). The inventory records their source
and SHA-256. This includes ONNX Runtime's and Tokenizers' third-party notices, the MCP SDK notices,
PdfPig's PDFBox/FontBox credits and embedded Adobe glyph-list licence headers, and YamlDotNet's libyaml
licence. The legacy `xunit.abstractions` package uses a licence URL instead of an SPDX expression;
its package-declared licence URL was fetched, the Apache-2.0 declaration verified, and its exact
content hash recorded separately. The old abstractions tag itself has no standalone licence file.

## Assets and exceptions to the permissive-package list

| Component | Terms and required attribution | Treatment |
|---|---|---|
| DM Sans and Manrope variable fonts | SIL OFL-1.1; retain their own copyright and complete OFL, including any reserved-font-name terms; keep the fonts under OFL | unchanged WOFF2 files; full package licences emitted as `assets/LICENSE-dm-sans.txt` and `LICENSE-manrope.txt` and included in the combined notices |
| Lucide icons, including Feather-derived icons | ISC plus MIT for the identified Feather icons; retain both notices, including Cole Bemis's credit | full original `lucide-react/LICENSE` included in browser notices |
| pdf.js and its WebAssembly image decoders | Apache-2.0 for pdf.js and its decoder wrappers; BSD/MIT notices for the underlying decoders | the existing versioned `assets/pdfjs-<version>/LICENSE-*.txt` files remain; standard fonts and CMaps remain excluded |
| Lightning CSS and its native platform variants | MPL-2.0 | build tooling only; absent from the emitted browser package list and pruned from the app runtime image. If distributing the tool binaries or a build image later, supply MPL notices and access to the corresponding source |
| ONNX Runtime's native-component notices | the top-level package is MIT; its vendor notice also lists Eigen under MPL-2.0, Intel MKL under the Intel Simplified Software License, and other components with their own terms | full original vendor notices and source-access information are preserved. The vendor list covers several build configurations; it does not prove which optional providers are in every native binary. Reconcile the actual runtime/image SBOM before claiming all shipped native components are permissive or publishing binary images |
| `caniuse-lite` compatibility data | CC-BY-4.0 | build tooling only; retain its attribution if redistributing the database; absent from the emitted app package list |
| Other npm packages | MIT, MIT-0, ISC, BSD, Apache-2.0, BlueOak-1.0.0, CC0-1.0, 0BSD and the compound expressions in `npm.json` | preserve each component's terms; do not relabel them all Apache-2.0 |
| `app/public/` icons and `demo/import/documents/DEMO Boiler manual.pdf` | repository assets covered by the repository licence; imported with the app or fictional demo | no separately licensed media or model binaries are checked into this repository (the Gradle wrapper below is the one third-party binary); this is a repository provenance check, not a new ownership certification |
| Manrope and DM Sans in the Android launcher | SIL OFL-1.1, as for the web app's fonts | not in the repository: each build fetches the unchanged TTF files from a pinned commit of google/fonts and checks their SHA-256 (`apps/android-launcher/licenses/font-sources.json`). Both OFL texts are in `apps/android-launcher/licenses/` and are packaged in the APK |
| The Gradle wrapper in `apps/android-launcher/` (`gradlew`, `gradlew.bat`, `gradle/wrapper/gradle-wrapper.jar`) | Apache-2.0 | Gradle's own files, unchanged. The jar's SHA-256 is in Gradle's published list of wrapper checksums (the wrapper of Gradle 8.14), and CI validates it on every run |
| Second Brain adaptations | MIT; the existing detailed credit to Rahil Pirani in root `NOTICE` | retained and included with app and engine distributions |

Mozilla's [MPL FAQ](https://www.mozilla.org/en-US/MPL/2.0/FAQ/) explains the distinction between using
the tool privately and distributing its covered code. OFL fonts do not require relicensing Rhythm's
application code; the fonts themselves retain OFL. No dependency was relicensed or replaced here.

## Model terms and unresolved export

| Model | Evidence at the exact runtime/benchmark pin | Status |
|---|---|---|
| Xenova all-MiniLM-L6-v2 | pinned export card declares Apache-2.0; original sentence-transformers card also declares Apache-2.0 | declaration verified |
| Xenova bge-small-en-v1.5 | pinned export card has **no licence declaration** and no LICENSE file; the original BAAI card declares MIT | unresolved export provenance; do not claim this export's licence is verified or redistribute its weights on that assumption |
| BAAI bge-base-en-v1.5 | pinned card declares MIT | declaration verified; benchmark-only |
| BAAI bge-reranker-base | pinned card declares MIT | declaration verified; off by default |
| cross-encoder ms-marco-MiniLM-L6-v2 | pinned card declares Apache-2.0 | declaration verified; off by default |

The JSON links directly to each pinned card and records its hash. These Hugging Face pins contain no
standalone licence files; model-card declarations are evidence of the stated terms, not copies of
copyright notices to accompany a future redistributed model bundle. No weights were downloaded.

The suggested optional titles model, `qwen3:4b-instruct-2507-q4_K_M`, comes from Qwen3-4B-Instruct-2507
([card at `cdbee75f`](https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507/blob/cdbee75f17c01a7cc42f958dc650907174af0554/README.md)),
which declares Apache-2.0. An Ollama tag is not an immutable model digest; verify the actual selected
artifact and its notices before redistributing it. Arbitrary titles models and external AI clients
are selected by the operator and require their own terms review.

**Owner decision before release:** obtain confirmation of the pinned Xenova BGE-small export's terms,
or approve an alternative with clear provenance. Switching models or pins changes vector identity
and requires its own evaluation; this task changes neither pins nor installations.

## Container and system software boundary

The npm/NuGet inventory is not a complete container SBOM. The compose recipes reference Node/.NET,
PostgreSQL/pgvector, Alpine/Ubuntu packages, restic/Python and optional exposure or titles services
(Ollama, cloudflared, Caddy, Tailscale and the selected tunnel client). Their licences and notices
remain with their upstream images/packages. Several tags float and operators can override them.
An exact image inventory, OS-package vulnerability scan, and all source/notice obligations for an
image distribution must be generated from the actual image digests before publishing images. This
task did not pull or publish images, inspect production, or assert container redistribution clearance.

## Refreshing and checking

From the root:

```sh
node scripts/dependency-inventory.mjs --write
npm run licenses:check
npm --prefix app run build
npm --prefix app run smoke
```

For the Android launcher, in `apps/android-launcher`: `./gradlew :launcher:writeDependencyList` rewrites
`launcher/dependencies.txt`, and `node scripts/collect-gradle-licenses.mjs` (public network reads) then reads
each package's declared licence from its POM into `gradle.json`. `licenses:check` compares the two offline,
and CI's Android job checks the list against the build.

`--write` refreshes the npm inventory and copies root `LICENSE`/`NOTICE` into the app's Docker context.
It does not invent NuGet licence declarations. After changing a NuGet version or model pin, restore
the exact packages, refresh their metadata and original legal files using
`NUGET_PACKAGES=<restored-package-cache> node scripts/collect-license-evidence.mjs` (public network reads),
review changes, then run `licenses:check`. The collection script's curated model list must follow any
model-registry change. Checking runs offline, detects lockfile drift and altered legal files, and
explicitly reports the unresolved BGE-small status. The app build fails if a bundled package lacks
its original licence file. The dated [dependency review](../dependency-license-review-20261109.md)
records vulnerability findings, checks and remaining decisions.
