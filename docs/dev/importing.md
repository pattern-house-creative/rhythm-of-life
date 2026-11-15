# Importing code from a private source

Rhythm of Life grows out of a private, single-household system. Code from it enters this repo only
deliberately, one file at a time, and every file is recorded in `scripts/import-manifest.json`.
Never clone a private repo into this one, never copy a folder tree (`cp -r`, drag and drop), and
never import a file you have not read.

## Sources

The manifest names sources by **label**. The labels, and the commit each is pinned to, are in the
manifest's `sources` section:

| Label | What it is |
|---|---|
| `private-app` | the private app repository (PWA, facade handlers, tests) |
| `private-engine` | the private engine repository (.NET engine, migrations, tests) |
| `platform` | the original platform folder, not under git (Dockerfiles, compose, scripts); pinned as a dated snapshot |
| `private-harness` | the private AI-harness notes, not under git (the scheduled daily-insight task); pinned as a dated snapshot |
| `carelink-reader` | the CareLink reader (third-party MIT code plus local changes); only for the separate CareLink plugin repo |

Where each source lives on a machine is never written into this repo. Keep it in your own shell, for
example `RHYTHM_SRC_APP` and `RHYTHM_SRC_ENGINE` environment variables, and read sources only with
commands that cannot change them:

```sh
git -C "$RHYTHM_SRC_APP" show 5feddc8:src/model.ts > app/src/model.ts
git -C "$RHYTHM_SRC_ENGINE" ls-tree -r --name-only 27ab85d
```

Never check out, switch, stash, fetch, commit or add worktrees in a source repository.

## The flow for one file

1. **Decide it belongs.** It is reusable (see the plan's "reusable vs specific" lists), not a
   pilot, an operator script, a release script, validation evidence or private documentation.
2. **Bring it in** with `git show <commit>:<path>` into its target path here.
3. **Read all of it** and make it public-safe and general:
   - people, households, time zones, hosts, accounts and IDs become config or neutral examples
     (`alex`, `sam`, `Europe/London` or UTC, `example.invalid`);
   - comments, UI copy and tool descriptions name no one;
   - fixtures and tests use fictional data only; a fixture captured from a real device is not
     imported, it is regenerated;
   - pilot gates, leases and hosting-specific checks are removed or turned into config.
4. **Record it** in `scripts/import-manifest.json`, keeping `entries` sorted by `targetPath`:
   ```json
   {
     "source": "private-app",
     "commit": "5feddc8",
     "sourcePath": "src/model.ts",
     "targetPath": "app/src/model.ts",
     "reason": "the app contract types; everything else in the app builds on them",
     "changes": ["SourceId is now a provider id string", "removed a hosting-specific comment"]
   }
   ```
   `changes` is never empty: write `["none: copied as is"]` when nothing changed. Optional `notes`
   holds anything a reviewer should know. Paths are relative, use `/`, and never contain a drive
   letter or `..`.
5. **Check** before committing:
   ```sh
   node scripts/import-manifest.mjs --check
   node scripts/scrub-check.mjs --all
   ```
   The pre-commit hook runs the scrub check on staged files again. If the scrub check flags
   something, fix the content. Allowlisting (`scripts/scrub-allow.txt`) is for reviewed false
   positives only, each with a reason, and never for private-pattern matches.
6. **Commit** the file and its manifest entry together, with the repo's own identity. The message
   says what was imported and why, never where it came from on disk.

## What never comes in

- Secrets of any kind, token hashes, encrypted or DPAPI-protected copies.
- Real health data, evidence JSON, test result files (`.trx`), screenshots of real data.
- Personal documents, memories, insights or saved dashboards.
- Names, emails, hostnames, IPs, account, document or resource IDs, local Windows paths, the home
  time zone as a default.
- Release artifacts: image digests, file hashes, release-chain scripts.
- Garmin's OpenAPI document (Garmin's, not redistributable) and the AGPL CareLink Python reference.
- Git history of the private repos.

The scrub check blocks most of these mechanically; the rest is on the importer and the reviewer.

## Migrations

SQL migrations would ideally come in byte for byte, so an existing install's recorded checksums
still match. Several private migrations name people or hardcode a household, so they cannot: they
are rewritten neutrally, each change is listed in the entry's `changes`, and the mapping from old to
new checksums for an existing install belongs in that install's private overlay, never in this repo
(open question Q-013).
