# Maintainer conventions

For the people and agents who commit to this repository directly. Contributors sending a pull
request need only [CONTRIBUTING.md](../../CONTRIBUTING.md); everything there (where things live, the
checks to run, the content rules) applies to maintainers too and is not repeated here.

## Branches

- `main` is always green: the scrub check clean, every test passing (the engine's with its database
  tests, `sh engine/test-db.sh`), `docker compose ... config` valid. CI checks the same on every push
  and pull request.
- One short-lived branch per piece of work, named `<area>/<topic>`. Rebase on `main` before merging.
- An agent works in a worktree of its own, one per branch, in a sibling folder of the repository, so
  two pieces of work never share a checkout:
  ```sh
  git worktree add ../rhythm-of-life-oss-wt/<topic> -b <area>/<topic>
  ```
  Worktrees share the repository's config, so the identity and hooks below apply automatically.
- A change to `contracts/`, or to a file another area owns, is its own small commit that says why.

## Commits

- **The project's shared identity.** Maintainers' commits use the identity set in the repository
  config, `Rhythm of Life <rhythm-of-life@example.invalid>`. Never override it with `-c user.*`,
  `--author` or environment variables.
- **The hooks are on** (once per clone): `git config core.hooksPath .githooks`. They run the scrub
  check on the staged files and on the message. Never bypass them (`--no-verify`); fix what they find.
- **Commit in UTC.** A commit date keeps the machine's UTC offset, and an offset names a time zone. Set
  `TZ=UTC` in the shell you commit from (`export TZ=UTC`, or `$env:TZ = "UTC"` in PowerShell); with
  the private pattern file the scrub check refuses any other offset.
- Subject line: imperative, at most 72 characters. Body: what and why, wrapped at 72. Name no person,
  host, account or local path.
- One logical change per commit. An imported file and its manifest entry go in the same commit
  ([importing.md](importing.md)).

## The scrub check, with the private patterns

- It runs the public rules (secret patterns, blocked file names, a 1 MiB size guard) and, for
  maintainers, the **private pattern file**: identifiers that must never appear here, kept outside the
  repository because a public list of them would leak them. With that file it also refuses a commit
  date outside UTC.
- The file's path comes from `RHYTHM_SCRUB_PATTERNS`, default
  `~/.rhythm-oss-private/scrub-patterns.txt`. Without it the check fails closed, unless it is told to
  run the public rules alone (`--public-only`, or `git config rhythm.scrubPublicOnly true` for the
  hooks). That mode is for contributors and CI. A maintainer never sets it.
- Its output names the file, line and rule, never the private pattern or the text it matched.
- Reviewed false positives go in `scripts/scrub-allow.txt`, one per line with a reason. A private
  match cannot be allowlisted: fix the content.

## Before every push

CI cannot hold the private patterns, so the last check is a maintainer's, on their own machine:

| What | Command |
|---|---|
| every tracked file | `node scripts/scrub-check.mjs --all` |
| every commit, message and old file version | `node scripts/scrub-check.mjs --history` |
| the import manifest | `node scripts/import-manifest.mjs --check` |

Both scrub runs must say "clean" with the private patterns loaded. Push `main` by name; never
`--all` or `--mirror`, which would publish every local branch.

## Releases

- Versions follow [Semantic Versioning](https://semver.org/), with `-alpha.N` while the project is in
  alpha. The version is in `package.json`, `app/package.json` and, in three parts, `engine/src/Core/Knowledge.csproj`
  (what `/health` and `rhythm doctor` report); the tag is `v<version>`.
- Every release has an entry in [CHANGELOG.md](../../CHANGELOG.md): what changed, what an operator must
  do when upgrading, and anything added to or removed from [known-limits.md](../known-limits.md).
- A release is cut from a green `main` after the full walk in [operations.md](../operations.md) and
  `cli/e2e.sh`, `cli/lifecycle.sh`, `cli/journeys.sh` and `cli/chaos.sh` have passed on throwaway
  stacks.
