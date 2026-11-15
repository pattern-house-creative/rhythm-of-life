# CareLink (optional plugin)

CareLink is not an adapter in this repo. It is a separate, optional plugin repository, **rhythm-of-life-carelink**,
that runs its own small container beside the stack and writes into Rhythm of Life only through the ingest contract
(`contracts/README.md` section 4). Nothing in the default install needs it, and it does nothing until you turn it on.

**The plugin's repository is not published yet.** This page describes it so that the engine's side of the contract
(the `plugins.carelink` settings, the ingest token, the providers it writes) is documented where it lives.

It is **unofficial** (not affiliated with or endorsed by Medtronic; it reads CareLink the way CareLink's own apps
do, and can break when CareLink changes) and **not a medical device**: never make treatment decisions from it.

## What it brings in

| Provider | Policy | What |
|---|---|---|
| `carelink-live` | live | CGM readings (`sensor_glucose`, mg/dL) polled from CareLink every few minutes, one source per pump day |
| `carelink-csv` | archive | CareLink CSV exports, dropped in a folder or downloaded daily: sensor glucose, meter readings (`blood_glucose`), basal rate, boluses, carbs, one source per pump day, plus each export as an original record |

The engine shows them as data stars ("Glucose", "Insulin and carbs"), the latest live reading on the glucose tile,
and both in the health tools (`glucoseSource: carelink-live` or `carelink-csv` keeps glucose to one of them,
`provider` keeps every metric to one; the older `carelinkSource: live | csv` is a deprecated alias of
`glucoseSource`). `get_sync_status` lists the plugin's last accepted writes per provider.

CSV exports keep only the pump's wall clock. The plugin turns those times into instants with its configured device
time zone, so the CSV days are device-local, not verified instants: the engine joins them with the live feed only
by its own per-day selection (live for today, the CSV for completed days), never by matching readings.

## Turn it on

1. In `rhythm.yaml`, allow the plugin, with a policy for each of its providers:

   ```yaml
   plugins:
     carelink:
       enabled: true
       providers: [carelink-live, carelink-csv]
       subjects: [alex]
       policy: live
       policies: { carelink-csv: archive }
       domain: health
   ```

   Restart the engine so it reads the entry:
   `docker compose -f deploy/compose/compose.yaml up -d --force-recreate --no-deps engine` (open question Q-056).

2. Create the plugin's ingest token, from the repository root:

   ```sh
   ./rhythm token create --role ingest --plugin carelink
   ```

   It is written to `deploy/compose/secrets/plugin_carelink_token`, owned by uid 1000, the user the plugin's container
   runs as (open question Q-104).

3. Follow the plugin repo's README: a one-time CareLink sign-in in a browser on any computer that has one (the plugin
   never sees or stores your password; it keeps CareLink's tokens in a private folder), its settings
   (`CARELINK_SUBJECT`, `CARELINK_DEVICE_TIMEZONE`), and `docker compose up -d` in the plugin's folder. Its compose
   file joins this stack's networks as external networks, `rhythm_internal` (the engine) and `rhythm_egress`
   (CareLink); with another compose project name they are `<project>_internal` and `<project>_egress`.

Turn it off with `docker compose down` in the plugin's folder and `enabled: false` here. Data already written stays,
under its providers.

`./rhythm upgrade` does not touch the plugin. To update it, in its folder after getting the new version:
`docker compose build --pull`, then `docker compose up -d`. `--pull` fetches the current Node base image, whose tag
follows Node's patch releases; a build without it keeps the copy Docker pulled first.

After `./rhythm restore` every token from before is disabled, the plugin's too. Stop the plugin (`docker compose down`
in its folder), delete `deploy/compose/secrets/plugin_carelink_token`, run step 2 again, and start the plugin. Stop it
first: on Docker Desktop a token file that a running container mounts cannot be written again.

## Limits

- US CareLink accounts only, and pumps that CareLink serves with its current (v13) data format (open questions
  Q-103, Q-105).
- No sign-in automation: when CareLink refuses the login, the plugin waits until you sign in again in a browser.
- Live readings depend on the phone app uploading; a gap at CareLink is a gap here.
- Resyncs cover one to seven completed days within the last ninety (`resync` in the plugin's container), one job at a
  time.
