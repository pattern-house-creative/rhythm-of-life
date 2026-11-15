# Garmin

Brings one person's Garmin numbers into Rhythm of Life: sleep, overnight HRV, heart rate, body battery, daily steps
and intensity minutes, and recorded activities with their duration, distance, heart rate, training load and training
effect. They fill the app's Recovery and Training tiles, become data stars on the map, and are there for your AI
through the health tools (`query_health_metrics`, `investigate_health` and the rest).

Off by default. It runs inside the engine; there is no extra container.

## Two ways in

| | Personal API key | Exported files |
|---|---|---|
| You need | a Garmin Connect account and a Personal API key from it | a Garmin Connect account |
| Fresh | every `pollSeconds` (6 hours by default), and "sync now" from the app | whenever you drop new files in the folder |
| Covers | everything below, day by day | activities, and daily steps, resting heart rate, intensity minutes and sleep |
| Leaves your machine | requests to Garmin with your key | nothing |

You can use either or both, but not for the same days: an activity that arrives both ways is counted twice.

### A Personal API key

1. Sign in to Garmin Connect on the web and open the **Personal API Key** page
   ([Garmin's description](https://developer.garmin.com/gc-developer-program/personal/personal-api/)).
2. Create a key, accept Garmin's terms, and choose how long it lives.
3. Copy it at once: Garmin shows it only once. Treat it like a password.
4. Save it as one line in `deploy/compose/secrets/adapters/garmin_api_key`, readable only by you (`chmod 600`), or
   paste it when `rhythm connect garmin` asks. It never goes in `rhythm.yaml` or `.env`.

The engine exchanges the key for a short-lived access token at Garmin, then reads one day at a time, at most one
request a second. It reads the key file each time it needs a new token, so a replaced key works without a restart.
When the key expires, syncing stops with `garmin_credentials_rejected` (see `get_sync_status`) until you replace it.

### Exported files

Put Garmin Connect's own exports in the `garmin` folder of your import folder: `RHYTHM_IMPORT_DIR/garmin` on your
computer (by default `deploy/compose/data/import/garmin`), which the engine sees as `/data/import/garmin`.

- **Activity list** (`*.csv`): Garmin Connect on the web, Activities, then Export CSV. One activity per row. The
  columns read are Activity Type, Date, Time, Distance, Calories, Avg HR, Max HR, Aerobic TE and Total Reps, by their
  English names. Distances are in the unit Garmin Connect shows you, which the file does not say: set
  `distanceUnit` to `km` or `mi`. Swims are imported without a distance (the file does not say yards or metres).
- **Daily files of the account export** (`*.json`): Garmin's "Export Your Data" sends a zip with daily summary and
  sleep files. Copy the JSON files that hold a list of days (each with a `calendarDate`); the importer reads the same
  fields as the API's daily summary and sleep, and skips any file or record it does not recognise.

A file is read again when it changes; unchanged rows are not written twice. FIT files are not read (open question
Q-081).

## Turning it on

In `rhythm.yaml`:

```yaml
adapters:
  garmin:
    enabled: true
    owner: alex                     # the person whose Garmin it is
    pollSeconds: 21600              # 3600 to 86400; only with a key
    deviceTimezone: null            # the watch's days; null means the install's timezone
    historyDays: 14                 # how far the first sync reaches back
    importPath: /data/import/garmin
    distanceUnit: km                # km or mi, for exported activity lists
```

Then restart the engine (`docker compose -f deploy/compose/compose.yaml up -d --force-recreate --no-deps engine`; `./rhythm connect garmin` does this for you). The app picks up the
Garmin tiles within five minutes (at once after `./rhythm connect garmin`, which restarts the app too). Settings are in `docs/configuration.md`; the descriptor `rhythm connect` uses is
`contracts/adapters/garmin.json`.

## How syncing works

- The first sync with a key covers the last `historyDays` days, later ones the last 7. Each day is fetched whole,
  written, then checkpointed, so a sync cut short (a restart, Garmin asking to wait) resumes where it stopped.
- "Sync now" in the app asks for yesterday and today, at most once every 15 minutes and never while a sync is
  unfinished; it starts within a minute. Without a key it rescans the export folder.
- A failed sync waits for the next regular one and, if Garmin asked for a longer pause, for that. After five failures
  it is given up, keeping its error, so it never blocks "sync now".
- An activity you delete at Garmin leaves the tiles the next time its day is synced.
- `get_sync_status` shows each sync with its days, state and error code; the engine log names the failure only by
  its kind.

## What is kept

Provider `garmin`, archive policy, domain health. Each day's stream (daily summary, sleep, HRV, heart rate, body
battery) and each activity is one source with provenance: Garmin's URL for it, the device id, the fetch time and a
checksum. The engine keeps the normalized samples of every changed source as its original; it does not keep Garmin's
raw responses or activity files (Q-083). An activity keeps its type key, its pool unit and its multisport role, never
its name, route or location.

Daily totals carry the start of their calendar day as their time (Q-084). Heart rates are in bpm.

## Limits

- One Garmin account per install, belonging to `owner` (Q-082).
- Garmin's scores (body battery, stress, training load and effect, sleep score) are Garmin's own, proprietary numbers;
  Rhythm shows them as Garmin reports them. Nothing here is a medical device or medical advice.
- What exists depends on the watch: no HRV, sleep score or body battery from a device that does not record them.
- The Personal API, its terms and its limits are Garmin's and may change; if Garmin changes a field this adapter
  reads, that number is simply missing until the adapter is updated.
- The export formats are read from their published shape, with English headers; another language's headers or a
  changed export are skipped, not guessed.
