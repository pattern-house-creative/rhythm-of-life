# CSV import (`csv`)

Your own numbers, from any device or spreadsheet that can export a CSV file: weight, steps, blood
pressure, glasses of water. Drop the file in the import folder and it becomes data stars on the map,
with the generic data view (each measurement's latest reading and its daily lows, averages and highs).
No account, no key. On in a new install: the example settings, and so `rhythm init`, turn it on; a `rhythm.yaml`
without its section has it off.

## Turning it on

1. Say where the import folder is on the host, in `deploy/compose/.env`:
   ```sh
   RHYTHM_IMPORT_DIR=/home/alex/rhythm-import   # relative paths start at deploy/compose/
   ```
   Compose mounts it read-only at `/data/import` in the engine. To try the fictional sample, use
   `RHYTHM_IMPORT_DIR=../../demo/import`.
2. In `rhythm.yaml` (the example already has this):
   ```yaml
   adapters:
     csv:
       enabled: true
       path: /data/import
       owner: household      # whose numbers a row without a subject is
   ```

The engine reads the folder every minute (`*.csv` files, in sub-folders too, except `documents/` and
`garmin/`, which belong to the documents and Garmin adapters) and asks for a map rebuild when something changed, so new
data stars show within a couple of minutes.

## The file

UTF-8, comma-separated, one header row. Columns, in any order, names in any case:

| Column | Required | What |
|---|---|---|
| `time` | yes | ISO 8601: `2026-10-20T07:30:00Z` or `2026-10-20T07:30+01:00` (an instant), `2026-10-20 07:30` (a local time, read in `rhythm.yaml`'s `timezone`), or `2026-10-20` (a daily value) |
| `metric` | yes | a name: lower case letters, digits, `_`, `.` or `-` (`weight`, `resting_heart_rate`, `water_glasses`) |
| `value` | yes | a number, with `.` for decimals (`71.4`) |
| `unit` | yes | up to 16 characters (`kg`, `bpm`, `count`, `mmHg`) |
| `subject` | no | a person id from `people`, or `household`; empty means `owner` |
| `source` | no | where the number came from (`bathroom scale`); each source is kept as its own record |

Other columns are ignored. Fields with a comma, a quote or a line break are quoted (`"scale, hall"`,
`""` for a quote). Example (`demo/import/sample-readings.csv` is a longer, fictional one):

```csv
time,metric,value,unit,subject,source
2026-10-20T07:05:00+01:00,weight,71.6,kg,alex,bathroom scale
2026-10-20T06:50,resting_heart_rate,58,bpm,alex,watch
2026-10-20,steps,8155,count,alex,watch
2026-10-20,water_glasses,6,count,sam,"kitchen tally, by the kettle"
```

Each reading keeps how its time was given: `explicit-offset`, `install-local` (a bare local time; a
time the clocks skipped is refused, and so is one in the hour they repeated, as `time_ambiguous`: it
happened twice, so give those rows their offset, such as `2026-10-25T01:30+01:00`) or `local-date`.
Known metrics take only their units, as for every source: `glucose` `mg/dL` or `mmol/L`;
`heart_rate` and `resting_heart_rate` `bpm`; `hrv` `ms`; `steps` `count`; `sleep_minutes` and
`intensity_minutes` `min`; `insulin` `U`; `carbs` `g`; `weight` `kg`; `body_battery` and `stress`
`score`. Measurements are grouped into stars by kind (Body, Activity, Heart, Sleep, Mood, Glucose, ...);
other metrics share one "Other CSV import data" star per person.

## Changes, mistakes and removals

- **The file is the truth for its rows.** Reading an unchanged file again changes nothing. When a file
  changes, changed values update, new rows are added, and rows it no longer has are removed. A
  reading is identified by its file, subject, source, metric and time.
- **Bad rows never block good ones.** A row with a bad time, metric, value, unit or subject (or a
  second row for the same reading, where the last one wins) is skipped. `get_sync_status` shows one
  line per file: `complete`, `partial` with every skipped row's line number and reason (never its
  values), or `failed` when the file cannot be read (no header, not UTF-8, over 20 MiB). A file that
  cannot be read changes nothing that was imported from it before.
- **A removed file's numbers leave the map** and the tools. Every version of every file stays in the
  engine's archive. A file put back, or returned to an earlier version (from a backup that keeps its
  modified time, say), reads as it is again.

## Limits

- At most 200,000 rows and 20 MiB per file; times from 1970 up to ten minutes from now.
- Values are plain numbers: no text values, no ranges. Censored readings ("below range") are not
  expressible in a CSV file yet.
- Hidden files and folders and links are skipped. On Linux the files must be readable by uid 1654.
