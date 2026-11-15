# Upstream stub

`upstream.mjs` stands in for Google (OAuth, Drive, Sheets) and a Second Brain memory service, so a whole stack can run the
`drive` and `second-brain` adapters with no account and no network. Everything in it is fictional and kept in memory.
The unit and database tests don't need it: they fake the same services in process (`../Knowledge.Tests/AdapterFakes.cs`).

How it is reached: with `RHYTHM_TEST_UPSTREAM=http://upstream:8080` the engine sends every adapter call for
`https://<host>/<path>` to `http://upstream:8080/<host>/<path>`, so `rhythm.yaml` keeps its real shape. Never set that
variable in an install.

1. Settings (`deploy/compose/rhythm.yaml`, never committed): the example with
   ```yaml
   adapters:
     drive: { enabled: true, roots: [stub-root], edits: true, owner: household }
     second-brain: { enabled: true, url: https://brain.example.invalid/, owner: alex }
   ```
2. Secrets: the usual three (`deploy/compose/README.md`), plus any fictional values in `secrets/adapters/drive_read_token`,
   `drive_edit_token` and `second-brain_token`, and a client JSON in `drive_oauth_client` such as
   `{"installed":{"client_id":"stub.apps.googleusercontent.com","client_secret":"stub"}}`.
3. Start it under its own project name and ports:
   ```sh
   RHYTHM_CONFIG_FILE=./rhythm.yaml docker compose -f deploy/compose/compose.yaml -f engine/tests/upstream/compose.yaml -p rhythm-test up -d --build
   ```
4. Ask for a map rebuild (`app_request_refresh`), then the map shows the stub's folder, Sheet, Doc and Markdown file, two
   memories and a project. A dashboard over `drive:drive:stub-budget` with a `sheet` write can set cells and undo them;
   `GET /state` on the stub (`docker compose ... exec upstream wget -qO- http://127.0.0.1:8080/state`) shows the Sheet.
5. `docker compose ... -p rhythm-test down -v` when done.
