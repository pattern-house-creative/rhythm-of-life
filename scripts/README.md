# scripts

| Script | What |
|---|---|
| `scrub-check.mjs` | keeps secrets, private identifiers and blocked files out (`--staged`, `--all`, `--history`, `--message`) |
| `scrub-allow.txt` | reviewed exceptions, each with a reason |
| `import-manifest.json` | every file imported from a private source |
| `import-manifest.mjs` | checks the manifest (`--check`, `--list`) |
| `json-schema.mjs` | the small JSON Schema subset the contracts use; `contracts.test.mjs` checks every fixture against its schema |
| `fake-nightscout.mjs` | a fictional Nightscout site for trying the nightscout adapter (`docs/adapters/nightscout.md`) |
| `story-14.mjs` | story 14's regression suite (SB-P6-06): stars, dashboards, health views and their original links, engine and app, run for every release candidate (`--list` names its tests) |
| `acceptance.mjs` | the acceptance script (SB-P6-08): every acceptance story's tests, the engine's on throwaway databases, the app's and the scripts', and `cli/e2e.sh`, then `cli/lifecycle.sh`, then `cli/journeys.sh`, as the sandbox stacks, one after the other in one Compose project and ports of its own, then a pass or fail table, one row per story (`--list` names each story's tests and its backlog items still open; needs Docker and `RHYTHM_MODEL_DIR`) |
| `rhythm-down.test.mjs` | both wrappers refuse `down -v`, and a first `init` on the default project's existing volumes, in a checkout that names no Compose project |
| `dependency-inventory.mjs` | the licence inventory check (`npm run licenses:check`): `docs/licenses/` against the npm, NuGet and Gradle package lists, offline |
| `collect-license-evidence.mjs`, `collect-gradle-licenses.mjs` | refresh the NuGet and model evidence, and the Android launcher's `gradle.json` (public network reads) |
| `notice.test.mjs` | `NOTICE` credits every piece of shipped code that says it adapts Second Brain, and every path it names exists |
| `*.test.mjs` | tests, run with `npm test` |

No dependencies: Node 22 or later. See `docs/dev/conventions.md`.
