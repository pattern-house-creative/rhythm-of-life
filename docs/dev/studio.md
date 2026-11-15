# Rhythm Studio

Studio is an external-agent authoring session, separate from saved dashboard versions. The engine owns durable sessions; the app validates candidates in its existing sealed sandbox and keeps the last good frame. Payloads are complete, bounded snapshots received through MCP, not a representation of private model tokens.

## Contract and ownership

`studio_dashboard` accepts begin, read, update, progress, cancel, discard and save. Each mutation carries a UUID request ID; update/progress/save carry the expected draft revision. A session has one immutable harness principal owner, one target screen and a seven-day lifetime. There is no implicit lease takeover. Reconnect reads that session; a different principal cannot resume it. The target screen's bound app login can read, validate, steer, cancel, discard and explicitly save. A screen accepts one active session at a time.

An update is a complete DashboardDraft with optional native layout and typed controls. Reference and scope checks reuse DashboardFeeds, DashboardLayout and DerivedScopes. A candidate becomes saveable only after the target app acknowledges that exact revision rendered. Invalid candidates remain inspectable as actual received code, while the last validated preview remains visible. No preview can write source data. Candidate code never reaches event/log output except the explicitly requested session's code view.

Cancel is a terminal fence, not a promise to interrupt a remote model. Discard also ends the session without touching a saved dashboard. Save locks the session and original dashboard, compares saved version and layout revision, calls the existing save policy atomically, then records the publication. Request replay cannot publish twice. Screen feedback is an ordered, bounded queue read at agent checkpoints.

## Typed controls

Controls are host-owned selections: tab/filter enumerations, integer week offsets and ISO date ranges. They are declared per saved version. Discovery and invocation use the existing authenticated screen commands and acknowledgements. Calls identify dashboard, version and selection revision; stale guards fail. Selection changes flow into the sandbox's existing data channel and never regenerate code or save a version. The host renders the same controls for people, and guarded Undo uses the same state registry. Known graph actions continue through focus_map.

## Client evidence

The prior synthetic ChatGPT test observed incremental completed calls. It did not establish true token streaming. A test with Claude's hosted connector failed to connect; compatibility remains unverified. Studio deliberately requires no client-specific streaming extensions. Runtime receipts must distinguish received, validated, shown and saved.

Studio has no setting of its own: the three harness tools (`studio_dashboard`, `discover_dashboard_controls`, `invoke_dashboard_action`) and the app's `app_studio` are always listed by an engine with migration 075.

## Verification — 10 November 2026

Merged into `main` with migration 075. The checks below were run on the branch before the merge.

- App: complete regression suite 1,258 passed, 39 skipped. Final focused Studio/facade/screen tests after the access-revocation fix: 50 passed. Production TypeScript/Vite/server build and isolated demo/live server smoke checks passed.
- Engine: 92 focused Studio, dashboard, screen scope/binding and MCP compatibility tests passed against disposable PostgreSQL. An additional real-service test covered source normalization, scoped feed reads, app validation, idempotent publication and persisted controls; the four service/database tests passed. Typed control/schema contract checks passed.
- Browser: actual sealed frames rendered received header then card snapshots; Watch code showed actual batches; an invalid candidate retained the good preview; reload recovered it; feedback queued; save opened version 1; typed week/tab/filter changes received real draw acknowledgements without another version; cancel rejected a late update and discard returned to the preserved view. The browser transport was synthetic and explicitly labelled DEMO; this is not a hosted-client or production-deployment claim.
- MCP descriptors: existing descriptors remain unchanged; the pinned listing adds the three Studio tools. Streamable HTTP protocol compatibility tests passed. Claude's hosted connector remains unverified after that connection failure; true token streaming remains unestablished.
- Existing lint limitation: the full lint run reported one error in unchanged `src/memoryFocus.ts` (rules-of-hooks, fixed since), plus warnings. No lint error is in Studio files. The source-path-dependent memory contract test passed when run with the expected output-directory structure.

The synthetic browser fixture does not show the real phone-agent → named-screen flow, including Claude's hosted connector: check that on a real install, and do not infer it from the fixture.
