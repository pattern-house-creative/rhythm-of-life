# Nightscout

Brings your CGM readings into Rhythm of Life from your own [Nightscout](https://nightscout.github.io/) site. Many CGM
users already run one: it collects readings from the sensor, a phone app or a pump uploader every few minutes. The
adapter reads them; it never writes to your site.

What you get:

- the glucose tile's current reading, named "Nightscout", and its last 24 hours;
- the 14-day glucose panel (time in range, GMI once 14 days are covered);
- a Glucose data star on the map, for dashboards and for your AI over MCP.

It runs inside the engine. There is no extra container.

## Before you start

You need a Nightscout site you can reach from the computer that runs Rhythm of Life, and, unless your site is readable
without logging in, an access token for it:

1. In Nightscout, open **Admin tools**, then **Subjects**, and add one, for example `rhythm`, with the role `readable`.
2. Copy its access token (it looks like `rhythm-` followed by 16 letters and digits).

The site's API secret works too, but it can change everything on the site; a `readable` token is the safer choice.

## Connect it

With the CLI, `rhythm connect nightscout` asks for the address, whose readings these are and the token, and writes
them for you. By hand:

1. In `deploy/compose/rhythm.yaml`:
   ```yaml
   adapters:
     nightscout:
       enabled: true
       url: https://nightscout.example.invalid/
       owner: alex            # a person id from people
       pollSeconds: 300       # optional, 60 to 86400
   ```
2. Put the token, alone, in `deploy/compose/secrets/adapters/nightscout_token` (mode 0600). Leave the file out if your
   site is readable without one.
3. Restart the engine: `docker compose -f deploy/compose/compose.yaml up -d --force-recreate --no-deps engine`.

The token file is read on every poll, so replacing it needs no restart.

## Settings

| Key | Default | Meaning |
|---|---|---|
| `url` | none, required | the site's address. `https://`, or plain `http://` only for a site on your own network: a single-label host (`http://nightscout:1337/`) or a private address (`http://192.168.1.20/`). No user name, password or query in it |
| `owner` | the first person | whose readings these are: a person id from `people` (not `household`) |
| `pollSeconds` | `300` | how often to read new entries |

Secret: `secrets/adapters/nightscout_token` (or the path in `RHYTHM_NIGHTSCOUT_TOKEN_FILE`). An access token is sent as
`Authorization: Bearer`; anything else is treated as the API secret and sent as its SHA-1 in the `api-secret` header, as
Nightscout expects. It never goes into a URL and is never logged.

## How it works

- Every `pollSeconds` it asks `/api/v1/entries/sgv.json` for sensor readings newer than the last one it stored, less
  three hours so readings an uploader fills in late still arrive. The first poll reaches back 14 days.
- Readings are stored in mg/dL, as Nightscout keeps them. An entry that says its units are mmol is converted. Values
  below 39 are device status codes and are skipped; 39 and 401 or more are the sensor's LOW and HIGH and are kept as
  bounds (shown as below or above range, left out of averages).
- One source per day (in the install's time zone), named "Nightscout glucose, 28 October 2026", with each reading's
  time as its key, so a repeated poll changes nothing.
- Provider `nightscout`, policy **live**: your site keeps the originals, so Rhythm keeps no archive copy. In the 14-day
  panel a live feed follows any archive the same person has (a CSV import, or an archive plugin), never mixed with it.
- A failed poll waits longer each time, doubling up to an hour. The engine log says why in one line (for example
  `the site refused the credential (HTTP 401)`), never with the token or a reading.

## Limits

- Sensor glucose (`sgv`) only. Treatments, meter checks, device status and profiles are not read.
- History before the first poll reaches back 14 days; older readings are not fetched.
- The site's own redirect (for example `http` to `https`) is not followed: set `url` to the final address.
- Live readings are not used as evidence by `investigate_health` and `evaluate_health_hypothesis`, which read archives
  only; `query_health_metrics`, `compare_health_trends`, data stars and dashboards do read them (open question Q-070).

## Try it without a site

`scripts/fake-nightscout.mjs` is a fictional Nightscout with 30 days of made-up readings (no dependencies). On the
compose network, run it as a container named `nightscout` and point the adapter at `http://nightscout:1337/`; see the
comments at the top of the script.
