# Dependency and licence review, 9 November 2026

Baseline: local `main` at `f360a541`. Work is isolated on
`review/dependency-license-readiness-20261109`; nothing is merged, deployed or published.
The earlier security branch contains only font-packaging commit `b9c09876`; its exact patch was
applied to this current baseline, then extended to cover the rest of the distributed notices.

## Dependency findings

### 18 November: cleared

`npm audit` on the app reports zero findings, full and production-only. The picomatch advisory
no longer appears in the registry's audit data, and the remaining four moderate entries
(`vite-plugin-pwa -> workbox-build -> glob 11 -> minimatch 10`) are cleared by an `overrides` entry
in `app/package.json` that gives workbox-build `glob ^14.0.6`, which brings `minimatch 11.0.9`.
Workbox calls only `globSync(pattern, { cwd, follow, ignore })`, which glob 14 keeps. The lint,
tests, build (service worker precaches 58 entries) and smoke checks pass with the override.
Remove the override once workbox-build depends on a patched glob itself.

### 17 November recheck

With the committed app lockfile on `90d3e288`, `npm audit --json` reported 12 high and two
moderate dependency entries. The production-only check, `npm audit --omit=dev`, reported zero.
The counts include packages affected through dependencies. A high-severity picomatch advisory
now covers all its published versions, including 4.0.7 in Vite 8.3.5 and Vitest 5.0.5.
Updating or replacing only the PWA plugin cannot clear those paths. The registry still lists
vite-plugin-pwa 2.0.0 and workbox-build 7.4.1 as their latest versions.
[picomatch advisory](https://github.com/advisories/GHSA-fc5q-39xc-vjpf)

### 9 November baseline

A fresh `npm audit --json` returned **three advisories affecting six entries: two low and four
moderate, no high or critical**. All affected entries are in the PWA build chain:
`vite-plugin-pwa -> workbox-build -> @rollup/plugin-node-resolve / glob -> deepmerge / minimatch`.
The production-only audit returned no reported vulnerabilities. Root `package.json` has no package
dependencies. Both NuGet projects restored in locked mode and `dotnet list package --vulnerable
--include-transitive --format json` returned no reported vulnerable packages.

| Advisory | Current package | Available fix and decision |
|---|---|---|
| [GHSA-gcqx-w48m-w2h4](https://github.com/advisories/GHSA-gcqx-w48m-w2h4): prototype poisoning | deepmerge 4.3.1, low | no patched release listed; latest registry version remains 4.3.1 |
| [GHSA-3g3m-2fjr-6pw4](https://github.com/advisories/GHSA-3g3m-2fjr-6pw4): traversal continuing after cancellation | glob 11.1.0, moderate | patched in 14.0.2; Workbox requires `^11.0.1`, so this would require a major override or upstream change |
| [GHSA-g3hv-75wg-m249](https://github.com/advisories/GHSA-g3hv-75wg-m249): pattern amplification | minimatch 10.2.6, moderate | patched in 11.0.1; current chain requires major 10, so this also requires a major override or upstream change |

Registry metadata still lists vite-plugin-pwa 2.0.0, workbox-build 7.4.1 and node-resolve 16.0.3 as
their latest releases. npm's suggested forced remedy is a downgrade to vite-plugin-pwa 1.1.0 across
a major version. It was not applied. There is no compatible published fix to test within the
existing ranges; no dependency versions, lockfiles, APIs or model pins were changed.

Reachability assessment from the installed source: node-resolve calls deepmerge with its static
defaults. Workbox calls `globSync` against the local `dist` tree with patterns fixed in Vite config
and supplies no AbortSignal. The app server does not take user glob patterns or load these build
packages after pruning. This reduces direct runtime exposure; it does not erase the advisories or
prove arbitrary untrusted builds safe. These remain open build-tool risks. Approval is needed before
testing/adopting major overrides, downgrading the PWA plugin, replacing the build chain, or accepting
the remaining risks for a release. No `npm audit fix --force`, advisory suppression or homemade fork
was introduced.

## Licence fixes and inventory

- Reconciled the earlier font patch, preserving both fonts' original OFL and copyright text in dist.
- Added notices generated from the app's package module graph plus server packages and Workbox;
  Lucide's Feather credit, chart dependencies, fonts and pdf.js are retained after pruning.
- Included Rhythm's own Apache licence and the detailed Second Brain `NOTICE` with app and engine.
- Preserved full runtime NuGet legal documents in publish output, including nested native/data
  notices that were absent from the original build recipe.
- Added the [full inventory](licenses/README.md), exact npm/NuGet records, model-card references,
  hashes, and an offline drift check. Corrected the old blanket permissive-dependency statement.

The pinned Xenova BGE-small export has no licence declaration. Its original BAAI model declares MIT,
but export provenance remains unresolved. This is a release blocker for representing that export as
licence-cleared or bundling its weights. Obtain confirmation or approve a separately evaluated
alternative. No incompatible component was added or enabled.

## Validation and limits

Passed: the offline inventory and legal-file hash check (593 npm entries and 57 NuGet packages),
118 app test files / 1240 tests (10 journey files and 39 tests skipped), the app production build,
both synthetic local smoke modes, four attribution tests, .NET locked restore and Release publish.
All 25 original runtime legal files plus Rhythm's LICENSE and NOTICE match the published copies byte
for byte. The app emits notices for 63 package entries. The engine Docker context allowlist was
updated to include these legal inputs; no Docker image was built. The initial sandboxed app test
attempt hit denied loopback connections; the completed run used local loopback with two workers.
These checks do not
exercise production, backup/restore, upgrades, mobile devices or unrelated release flows.

Registry audits are dated observations, not an outside security review. This review does not
inventory OS packages in actual container images, the separate CareLink repository, operator-selected
models, external AI services, or untracked user content. ONNX's vendor notice includes MPL and Intel
terms alongside MIT/BSD/Apache material; the exact native-provider/image SBOM must be reconciled
before declaring a binary release cleared. The core repository is still unreleased.
