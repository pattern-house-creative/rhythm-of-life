# demo

The fictional seed dataset: a sky of stars, everyday wellbeing numbers (sleep, steps, resting heart
rate and an evening mood), dashboards and insights, all labelled DEMO. There is no glucose and no
Garmin in it: their tiles appear only once a household connects those sources. The people are `alex` and `sam`; nothing is drawn from a real person or device.
`demo.enabled` in `rhythm.yaml` turns it on or off.

`demo/notes/` and `demo/import/` are fictional sample folders for the default sources, to point
`RHYTHM_NOTES_DIR` and `RHYTHM_IMPORT_DIR` at (README "Try it"). Unlike the seed they become the
household's own stars, so every note and document title says DEMO (and every CSV source):

- `notes/`: a small Markdown vault with wikilinks (by name, alias, path and heading), relative links,
  two folders and tags ([docs/adapters/notes.md](../docs/adapters/notes.md)).
- `import/sample-readings.csv`: two weeks of weight, resting heart rate, steps and glasses of water for
  `alex` and `sam`, with every kind of time ([docs/adapters/csv.md](../docs/adapters/csv.md)). Its
  dates are fixed (October 2026); the data view shows the last 90 days.
- `import/documents/`: a Markdown, a text and a three-page PDF document
  ([docs/adapters/documents.md](../docs/adapters/documents.md)).

The engine's tests read these folders too, so they stay valid.

`demo/engine/` is the engine's part, read by the demo adapter (`engine/src/Adapters/Demo/`): notes with links and
projects (`notes.json`), how fictional measurements are generated (`measurements.json`: steps, sleep and resting heart
rate from a fictional tracker, and a mood check-in from one to five) and two dashboards and one insight
(`showcase.json`). It all belongs to the synthetic subject `fixture`, never to a person in `people`. "Week at a glance"
draws a tall card for the home screen's main slot and "Mood log" a compact one for a corner; each takes its corner
once, while it is free.

The app's own demo mode (no engine configured, `app/src/demo*.ts`) has its own fictional sky in the same spirit: mood,
sleep, steps and resting heart rate as data stars, "Week at a glance" in the main slot and a sample training week in a
corner.
