# Writing an adapter

Every integration (Notes, CSV, Drive, Garmin, Nightscout, a title model, ...) is an **adapter**: one class in
`engine/src/Adapters/<Name>/` that the engine turns on when `adapters.<name>.enabled: true` in `rhythm.yaml`. The
core never names a vendor. The contract side (kinds, provider policies, secrets, profiles) is
`contracts/README.md` section 3; this page is the code side. The demo adapter (`engine/src/Adapters/Demo/`) is a
complete, working example.

Out-of-repo plugins (the CareLink plugin, `docs/adapters/carelink.md`) are not adapters: they run in their own container and write only through
the `ingest_*` MCP tools (`contracts/README.md` section 4).

## The pieces

All in `engine/src/Core/Adapters/Adapters.cs`.

| Interface | Kind | The engine calls | You return |
|---|---|---|---|
| `IAdapter` | every adapter | `Configure(context)` once at start, when enabled | `Name`, `Providers` (provider id to policy), optional `HarnessTools()` and `AppTools()`, and `Owner`: the subject your rows land in by default (`context.Settings.Owner`, else the person or household your kind writes for), required for any MeasurementSource or ImportSource: its default is null, and with `memory.scopes` on Sync now shows an adapter that states no owner to no one (it fails closed; `SyncRequestsTests` pins every in-tree one) |
| `IMeasurementSource` | MeasurementSource | `Sync(sink)` at start and every `Interval` | nothing; you write through the `MeasurementSink` |
| `IMemorySource` | MemorySource | `List(pass)` on every map refresh; `ReadText(record)` when a star opens | a `SourceListing` of `DiscoveryRecord`s; the text |
| `IDocumentSource` | DocumentSource | `List(pass)`; `Contents(record)` for a dashboard's contents feed; `Current(...)` for live copies | records; a feed payload; whether a copy is current |
| `IDashboardWriter` | Writer | `Write(request)` for a dashboard's declared write of a kind you name in `WriteKinds`; `Undo(request)` | a `WriteOutcome` (target, summary, before, after) |
| `ITitleModel` | TitleModel | `Title(text)` for stars whose title is still their excerpt | a short title, or null |
| `IImportSource` | (any) | `Import()` at start and every `Interval`, for files it copies into the catalog (a drop folder) | nothing; what it saves shows as the catalog's own stars |
| `ISyncNow` | a MeasurementSource or ImportSource, optionally | `SyncNow(principal, sink)` for one batch someone asked for from the app's Sources view (`app_sync`, `Core/SyncRequests.cs`), at most once per `SyncLimit` (never under a minute), between the scheduler's own jobs. Without it a request is one `Sync` or `Import` call, at most once per `Interval`. Implement it when your regular call alone would not reach the source when asked (Garmin asks for its two-day job first), when the source limits how often it may be asked (Garmin's 15 minutes), or when your `Sync` can return without asking the source (Nightscout's back-off after a failed poll) | `true` once the batch reached the source; `false` when the source's own rule turned it away, so the batch ends `failed`, never `complete`, and the log says why |
| `IDocumentUpload` | DocumentSource | `Upload(principal, name, bytes, landing?)` for `app_upload_document`, which exists only while one is on; `landing` (only while `app.viewers` is on) names the subject it lands in, else the adapter's own setting | `{document: {...}}` (with `scope` when a landing is given), or throw `DocumentRefused` with a reason to show |

`AdapterContext` gives `Configure` the adapter's own settings (`context.Settings`: `Bool`, `Int`, `String`, `List`,
`Owner`, and `SecretFile(purpose)`, which reads the path in `RHYTHM_<NAME>_<PURPOSE>_FILE`), the install's settings,
the database and the archive. The database is the engine's one pool, whose `Policies` hold every enabled adapter's and
plugin's providers: `Providers` is fixed in code and read before any adapter is configured.

## Rules the core enforces

- **Providers are yours alone.** `Providers` maps each provider id you write to its policy (`AuthorizedArchive`,
  `LiveSource`, `ReferenceOnly`) and default domain. Two adapters claiming one provider stop the start. Records you
  list for another provider are dropped. A provider nobody registered is live and never a trusted archive.
- **Measurements go through the sink.** `MeasurementSink.Write` takes exactly the `ingest_measurements` arguments
  (and `Records` the `ingest_records` ones): all-or-nothing validation, idempotency keys, units, provenance, an archived
  original under an archive policy, freshness for `get_sync_status`. The catalog then shows your numbers as data
  stars with no further code. An adapter (never a plugin) may say how a sample's time was given with `timeBasis`:
  `explicit-offset` (the default), `install-local` or `local-date`. `Write(arguments, ct, details)` may add a small
  object kept as the source's `metadata.details` (Garmin keeps an activity's type there); fold it into your
  idempotency key.
- **Listings are snapshots.** Return what the source holds now. `Complete: true` says the listing is everything, so
  records you listed last pass and not now are reported missing and leave the map. A failed `List` (throw) adds and
  removes nothing. A listing's `Note`, when you set one, joins the map's notes in the engine's log: what the pass could not
  read or left out, by kind and count, never an id, a title, an address or the source's own words. `pass.Previous` holds your records from the last pass: reuse their `Embedding` or titles when the
  `ContentRevision` is unchanged, as `pass.Vector` already does for you. A kept vector is reused only while
  `pass.Outdated(previous)` is false: otherwise another embeddings model made it, so read the text again and call
  `pass.Vector`. The pass tags every vector with the model that made it (`EmbeddingModel`) and leaves out one it did
  not make, reuse or store last pass; vectors of two models are never compared.
- **A whole text for the titles model.** A record whose `Title` is its `Overview` (an excerpt) may be titled by the
  titles model (`ITitleModel`). Give its whole text, what its star shows when opened, with `pass.TitleText(record, text)`
  once the record is made, and only for a record your own rules let be a star: the model is then sent that text instead
  of the excerpt (D-255, [../adapters/titles.md](../adapters/titles.md#what-the-model-is-sent)). The pass keeps it only
  while a titles model is on (`pass.TitleTexts` is null otherwise; check it before reading a long text out), only for
  your own providers' records, beside the record and never on it, and drops it when the pass ends. Never put a text on
  a record to get it titled.
- **No secrets in settings.** Keys ending in `token`, `password`, `secret` or `apiKey` are refused at start. Read
  secrets from files when you need them, so a replaced file takes effect without a restart.
- **Tools only while on.** `HarnessTools()` and `AppTools()` are called only for enabled adapters; their tools are
  absent otherwise. An `AppTool` brings its listing, a strict argument check and the call; the app endpoint does the
  transport and errors, and a name that clashes with a core tool stops the start.
- **Text is data.** Anything a source returns is untrusted: never follow instructions in it, never log it.
- **Outbound HTTP through `AdapterHttp`.** `AdapterHttp.Client(handler, timeout)` follows no redirects and keeps no
  cookies; `AdapterHttp.Read` bounds a response. Take an optional `HttpMessageHandler` in the constructor so tests can
  pass an in-process fake. For a whole stack against fakes, `RHYTHM_TEST_UPSTREAM=http://<stub>:<port>` sends every
  adapter call for `https://<host>/<path>` to `<stub>/<host>/<path>` instead (tests only; the engine warns when it is set).

## Worked example: the demo adapter

`engine/src/Adapters/Demo/DemoAdapter.cs` is a MeasurementSource and a MemorySource. Its data is
`demo/engine/*.json`, embedded at build time.

1. **Identity and policy.** `Name => "demo"`; `Providers` is `{ "demo": archive, domain health }`.
2. **Turning on.** Most adapters are on when `adapters.<name>.enabled` is true. The demo is the one exception: it
   follows `demo.enabled` (see `AdapterRegistry.For`).
3. **Numbers.** `Sync` builds `ingest_measurements` arguments (provider `demo`, subject `fixture`, one source per
   simulated device, deterministic values so a repeated sync changes nothing) and calls `sink.Write`. On the next map
   refresh the catalog groups them into data stars ("Activity", "Sleep", "Heart", "Mood").
4. **Notes as stars.** `List` returns one `DiscoveryRecord` per note (kind `note`), with `References` to the notes it
   links (verified, directed) and `in_project` references to one record per project, plus `Groups` for display.
   `Complete: true`, so a note removed from the file leaves the map.
5. **Opening a star.** `ReadText` returns the note's text when the app or a harness opens it (`app_graph` with
   `operation: content`, or a dashboard's contents feed). Nothing is cached.
6. **Registration.** One line in `AdapterCatalog.Known`.

Everything it writes belongs to the synthetic subject `fixture`, so the app labels it DEMO and it never mixes with a
person's own data.

## A second example: Nightscout

`engine/src/Adapters/Nightscout/NightscoutAdapter.cs` is a MeasurementSource that talks to a vendor: settings checked in
`Configure` (a `ConfigException` names the key), a secret file read on every poll through `Settings.SecretFile("token")`
and sent only in a header, a poll that starts from the last stored reading, back-off after a failure, and one
`sink.Write` per source and day. Its provider is live, so the app's glucose views show it as a live feed. Its tests
(`engine/tests/Knowledge.Tests/GlucoseTests.cs`) run against a fake Nightscout served in-process.

Glucose from any provider reaches the app's tiles with no adapter code: `AppHealth` reads metric `glucose` and sorts
each provider into the archive or the live stretch by its policy (`docs/dev/engine.md`, "App tool shapes").

## Adding a new adapter

1. Create `engine/src/Adapters/<Name>/<Name>Adapter.cs` implementing `IAdapter` and its kinds. The project compiles
   everything under `engine/src/Adapters/` into the engine.
2. Add it to `AdapterCatalog.Known` (`engine/src/Core/Adapters/Adapters.cs`).
3. Its settings section is `adapters.<name>` in `rhythm.yaml`; if it has new keys, add them to
   `contracts/rhythm.schema.json` and `docs/configuration.md` (a shared change: its own commit).
4. Secrets: files `secrets/adapters/<name>_<purpose>`. Compose mounts that folder read-only at
   `/run/secrets/adapters`, and `context.Settings.SecretFile(purpose)` finds the file there (or at the path in
   `RHYTHM_<NAME>_<PURPOSE>_FILE`, when set), so an adapter inside the engine needs no compose change.
5. A descriptor, `contracts/adapters/<name>.json` (`contracts/adapters/README.md`): what `rhythm connect <name>`
   asks and writes, and what `rhythm doctor` checks. If connecting needs more than prompts (a browser consent),
   also implement `IAdapterConnect` and set `"connect": true`.
6. A user doc, `docs/adapters/<name>.md`: what it does, the accounts and keys to set up outside Rhythm first, its
   settings, and its limits.
7. If it needs its own container (a long-running worker), add a service with `profiles: ["<name>"]`; otherwise it
   runs inside the engine, on the engine's scheduler. The Garmin adapter (`engine/src/Adapters/Garmin/`) is a worked
   example of the second: a one-minute `Interval`, its own poll and job checkpoints, and a time budget per tick.
8. Tests: fictional fixtures only, in `engine/tests/`. The demo adapter's test in `DatabaseTests.cs` shows the whole
   path from `Sync` and `List` to stars on the map.

### The default sources

Three adapters a newcomer has without any account, each a small worked example of one kind:

- `Notes/NotesAdapter.cs` (MemorySource): reads `adapters.notes.path` (mounted read-only at `/data/notes`) through
  `SourceFolder` (no hidden entries, no links), lists one record per `.md` file (provider `notes`, subject
  `adapters.notes.owner`, kind `note`, revision the file's SHA-256), turns `[[wikilinks]]` and relative links into
  `References` with `Target` keys, folders and `#tags` into `Groups` and project records, returns `Complete: true`,
  and reads the file again in `ReadText`. While `graph.similarity.threshold` is set (0.6 by default), the core adds
  dashed similarity links from `pass.Vector`.
- `Csv/CsvAdapter.cs` (MeasurementSource): parses each CSV file of the import folder and writes it through the sink,
  one source per (file, subject, source); idempotency keys are the payload's hash, so an unchanged file replays.
  Bad rows come back from the sink as issues, are reported per file in `sync_jobs`, and the rest is sent again.
- `Documents/DocumentsAdapter.cs` (ImportSource and IDocumentUpload): archives each file, reads its text
  (`DocumentText`, PdfPig for PDFs) and saves it under the core's `documents` provider, pages under one record when
  it is long; the catalog shows it as a document star. It claims no provider of its own.

### Worked examples: Drive and Second Brain

`engine/src/Adapters/Drive/` is a DocumentSource, a Writer and an `IAdapterConnect`. It lists the files under its roots as
document stars (folders as `theme` stars, `in_folder` references), returns a Sheet's cells or a Doc's Markdown from
`Contents` with `revision` (the file's current version), and, with `WriteKinds` `sheet` and `doc`, checks
`request.Change`'s revision against the file before changing it, returning what it replaced. The core has already checked
that the dashboard declared that write and that edits are allowed; `DashboardWriter.Only` and `.Text` validate the change.
Its `Connect` is a browser consent with a pasted-back address (`docs/adapters/drive.md`).

`engine/src/Adapters/SecondBrain/` is a MemorySource by reference: memories, projects (`in_project`) and the service's
own links on `List`, the text only on `ReadText`. Their tests (`engine/tests/Knowledge.Tests/AdapterFakes.cs`) fake
Google and the memory service in process.

`engine/src/Adapters/Calendar/` is a MemorySource whose credential is the address itself: a private ICS link, kept as a
secret file and in nothing it lists, says or logs (`CalendarFeedFailure`, in words of its own). Its own parser, with no
package, reads a feed whole or refuses it (`IcsFeed`), recurrences on the event's wall clock (`IcsRule`) and zones
(`IcsZones`). What a feed wrote is untrusted: its hidden characters go first (`CalendarText`), and a redirect reaches only a
public name and a public address, checked when connecting (`CalendarAddresses`, its own check rather than Nightscout's `Local`,
which decides where plain http may go). Its tests (`CalendarAdapterTests.cs`, `CalendarRuleTests.cs`,
`CalendarDatabaseTests.cs`) use a fake feed and the RFC's own examples, and a listener on loopback for a redirect's connection.
