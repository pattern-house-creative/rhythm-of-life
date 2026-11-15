# app

The Rhythm of Life PWA (React, Vite, TypeScript) and its server facade.

- `src/`: the app. Its contract types are `src/model.ts` (see `contracts/README.md` section 5).
  `src/instance.ts` holds the install's name, people and time zone, read from `GET /api/instance`
  with the provider id of the engine's own stars; nothing about a person, a household or a time zone
  is hardcoded.
- `server/`: the facade, web-standard Request-to-Response handlers (`server/routes.ts`), run by the
  small Node server `server/node.ts`, by Vite in development (`server/devApi.ts`) and by a hosted
  front end (`api/`, see `deploy/vercel/`). It is the only part that talks to the engine, through
  one client (`server/engine.ts`) and the one app token. See `server/README.md`.
- Demo mode, with fictional data labelled DEMO, whenever no engine is configured. With an engine
  configured, nothing falls back to demo data: a failed read shows as unavailable, and an adapter
  that is off keeps its tiles off the home screen and says Not connected in Sources & settings.
- The home screen: a tall main slot on the left (a dashboard's tall card, else the glucose tile once
  a glucose source has data, else a short hint) and three corners (the Garmin tiles while Garmin is
  connected, and what changed lately), any of which can show a dashboard's card instead.
- Live screens (`docs/dev/screens.md`): every open copy of the app is a named screen
  (`src/screens.ts`) that keeps one request waiting for a command (`src/useScreenListen.ts`) from
  an AI client or another screen's "Show on…", applies it through the app's own functions with a
  note and Undo (`src/LiveScreen.tsx`), and keeps its layout, where each tile sits and its Home:
  the sky and tiles, or a dashboard it shows full screen (`src/FullScreen.tsx`) whenever it goes
  Home. The demo's tabs find each other over a `BroadcastChannel` instead (`src/demoScreens.ts`).

## Commands

```sh
npm ci
npm run dev       # Vite on http://127.0.0.1:5173 with the same /api handlers (demo mode)
npm run verify    # tests, type check, build of the PWA and the server, smoke check
npm run lint
npm start         # the built server: http://127.0.0.1:8080 (HOST, PORT to change)
```

`npm run verify` is the build gate. The smoke check (`scripts/smoke.mjs`) runs the built server in
real Node, in demo mode with all outbound requests forbidden, then in live mode against a stand-in
engine on 127.0.0.1.

## Container

`Dockerfile` builds a two-stage image: the runtime stage holds only `dist/`, `build/` and the two
runtime dependencies (`yaml`, and `undici` for the engine connection), runs as the non-root `node`
user and writes nothing, so it runs with a read-only root filesystem. It is the `app` service in
`deploy/compose/app.compose.yaml`; health is `GET /api/health`.

Everything the browser runs (React, pdf.js, recharts, d3, motion, the service worker) is a
`devDependency`: Vite bundles it into `dist/` at build time, so the runtime image needs none of it.
It is still code every household device runs, so audit the app with plain `npm audit`, never with
`--omit=dev`, and treat an advisory for `pdfjs-dist`, `react` or anything else that reaches `dist/`
as a runtime one.

Code arrives from the private version through the import manifest (`docs/dev/importing.md`).
