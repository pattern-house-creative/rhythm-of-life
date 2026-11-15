# engine

The knowledge engine: .NET 10, ASP.NET, the official MCP C# SDK, Npgsql, Postgres with pgvector, and an optional
local embedding model (MiniLM via ONNX). Its single interface is MCP at `/mcp`, with three roles: `harness`, `app`
and `ingest` (`contracts/README.md` section 2).

```
src/Core/       catalog, map (relationship engine), dashboards, insights, retrieval, health tools, ingest,
                the MCP server and its roles, the scheduler, settings
src/Adapters/   one folder per adapter (Demo today; Notes, Csv, Drive, Garmin, ... next): docs/dev/adapters.md
src/Cli/        the install commands of `rhythm` (init, connect, doctor, ...): cli/README.md
migrations/     SQL, numbered and checksummed, applied by the one-shot migrate service: docs/dev/migration-compat.md
backup/         the backup image (pg_dump + archive into restic)
tests/          xUnit; database tests run when RHYTHM_TEST_DB names a disposable Postgres with pgvector
test-db.sh      the whole test run against a throwaway pgvector container (test-db.ps1 in PowerShell)
Dockerfile      the engine image (build context: the repository root)
```

```sh
dotnet build engine/Engine.slnx
sh engine/test-db.sh                 # every test, the database tests included
dotnet test engine/Engine.slnx       # without a database: the database tests are skipped
docker build -f engine/Dockerfile -t rhythm-engine:dev .
```

Commands (`rhythm-engine <command>` in the image): `serve` (default), `migrate`, `refresh`, `check-config`, and the
install commands `init`, `connect`, `disconnect`, `doctor`, `preflight`, `ready`, `token create|list|revoke` and
`person list|suspend|unsuspend` (run them through `./rhythm` at the repository root). Settings come from `rhythm.yaml`; secrets only from files named by `*_FILE` variables.
Ports, environment, tokens and tool shapes for integrators: `docs/dev/engine.md`.

Not a medical device. The health tools describe recorded data; they do not diagnose or advise treatment.
