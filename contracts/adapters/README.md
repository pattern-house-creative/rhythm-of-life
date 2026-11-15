# Adapter descriptors

One JSON file per adapter, `contracts/adapters/<name>.json`, valid against `adapter.schema.json`. The `rhythm` CLI
reads them (they are copied into the engine image) to:

- list what can be connected (`rhythm connect` with no arguments);
- ask for an adapter's settings and secrets (`rhythm connect <name>`), write the settings to `adapters.<name>` in
  `rhythm.yaml` with `enabled: true`, and write each secret to `secrets/adapters/<name>_<purpose>` (mode 0600);
- add the adapter's compose `profile`, if it has one, to `COMPOSE_PROFILES` in `.env`;
- run the adapter's own `IAdapterConnect.Connect` when `connect` is true (for example a browser consent);
- check in `rhythm doctor` that an enabled adapter's required secrets exist and are private.

A setting's `pattern` is a regular expression its answer (for a list, each item) must match (the OpenAI tunnel's `tunnelId`, say);
`rhythm doctor` does not report a value of that form as a secret-shaped value. `harnessToken: true` says the adapter's
container calls `/mcp` with a harness token of its own, `secrets/harness_<name>_token`, made with
`rhythm token create <name>`: `rhythm connect` refuses until it exists (Docker would make a folder in its place when
the container starts) and `rhythm doctor` checks it.

The engine finds an adapter secret through `AdapterSettings.SecretFile(purpose)`: the path in
`RHYTHM_<NAME>_<PURPOSE>_FILE` when set, else `<name>_<purpose>` in `RHYTHM_ADAPTER_SECRETS_DIR` (default
`/run/secrets/adapters`, mounted read-only from `deploy/compose/secrets/adapters/`). So connecting an adapter that
runs inside the engine needs no compose change.

Settings keys never end in `token`, `password`, `secret` or `apiKey`; those are secrets.
