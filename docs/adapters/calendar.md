# Calendar

The `calendar` adapter shows the events of your calendar as stars on the map, read from its own private ICS link. It is
**off by default and connected to nothing**: it reads nothing until you turn it on and enter your own link yourself. No
link ships with Rhythm, and nobody can enter yours for you. It reads only: nothing is ever written to your calendar.

This version has been tested against a fake feed only, not yet against a real Google, Outlook or iCloud calendar.

## What you get

- **A star per occurrence.** Each event in the window (7 days back and 30 ahead, by default) is a star: a weekly class is
  one star for each week in the window. Its title is the event's name, and its overview says when and where, on your
  install's clock: `Tue 2026-03-24 13:00 to 13:30 Europe/London; at Main Street Clinic`.
- **Its words stay in the calendar.** Only that title and overview are stored. Opening a star reads its description and
  address from the calendar, for that view only.
- **For your AI clients.** `explore_graph` finds events by name, place or date (`2026-03-24`), and a date is how a client
  sees what is coming up: the daily insight's template asks for today's and tomorrow's ([step 1c](../../harness/chatgpt/daily-insight.md)).
  An overview names an event's first and last day, so a day in the middle of a long trip does not find it. `graph_changes`
  shows only what the calendar itself says was created or changed since then, so not an event booked long ago, nor a series'
  later weeks, nor one from a feed that does not say. Those two read the stored title and overview. `dashboard_data`, asked
  for a star's `contents`, also reads its description and address from the calendar, so an AI client can read those when it
  asks. `search_knowledge` and `recall` do not serve events, as they serve no source read by reference, and a client's `brief`
  has no calendar section of its own: the client asks the map (Q-162).

An event's name, place and description are text someone else may have written: an invitation you were sent lands on your
calendar, and so here. They are read as data, never as instructions, and an AI client is told so, and characters that only
serve to hide words from you are taken out, but know that this text reaches your AI clients.

## The link is a secret

Whoever holds a calendar's private ICS link can read that calendar, with no password. So:

- It lives in one file, `deploy/compose/secrets/adapters/calendar_link`, never in `rhythm.yaml`, the database or a backup.
  The engine refuses to start with one in `rhythm.yaml` (as `adapters.calendar.url`, `link`, `links`, `feed` or `ics`),
  whether the calendar is on or off, and `rhythm doctor` and `rhythm check-config` name the key; while the calendar is
  on, `rhythm doctor` also reports that file's link if it finds it written anywhere else in `rhythm.yaml`.
- It is entered at a hidden prompt and never shown back: not by `rhythm connect`, `rhythm doctor`, the app or an AI client.
- It is not logged. What the engine logs about a read says what went wrong in its own words, never the link or the
  server's address. A feed that repeats its own link has it taken out of what is shown.
- `rhythm export` carries none of it: an export holds native memories, and no star of an adapter.
- It is sent only as the address of the request, and over https, unless the calendar is on your own network. A redirect is
  never followed from https down to plain http, or into your own network: not by the name it gives, nor by the address that
  name turns out to have when Rhythm connects.

Think it has leaked? Reset the private address in your calendar (that ends the old one), then connect again.

## Before you start

Copy your calendar's private ICS address. Not its web page, and not a "share" page: it is usually long, and ends in `.ics`
or begins with `webcal://`.

- **Google Calendar:** Settings, your calendar under *Settings for my calendars*, *Integrate calendar*, **Secret address
  in iCal format**.
- **Outlook:** Settings, Calendar, *Shared calendars*, *Publish a calendar*, then the **ICS** link.
- **iCloud:** the calendar's sharing button, *Public Calendar*, then copy the link. iCloud has no private form of this: a
  public calendar is reachable by anyone with the link, the same as the others here.

## Connecting

```sh
./rhythm connect calendar        # from the repository root; .\rhythm.ps1 connect calendar in PowerShell
```

It asks whose calendar this is, then for the link at a hidden prompt, and up to two more for a second and third calendar
(leave them empty for none). It checks only the link's form: the command itself does not read your calendar. The engine
reads it at the next map rebuild; `./rhythm logs engine` says so if it cannot. Without the command: write the link, alone,
to `deploy/compose/secrets/adapters/calendar_link` (mode 0600), set the settings below, and restart the engine.

A replaced link file takes effect at the next read, with no restart.

## Turning it off

`./rhythm disconnect calendar` turns it off and restarts the engine. It reads nothing more, and its stars leave the map and
every answer at once, as a provider that is off does. It leaves two things behind: the link's file, for you to delete, and the
names and places of the stars last read, in their database rows and in any backup, where no tool serves them. Turned on again,
the stars come back from the calendar under the same ids.

## Settings

```yaml
adapters:
  calendar:
    enabled: true
    owner: alex          # a person id, or household for a calendar the whole family shares
    daysBack: 7          # optional, 0 to 90
    daysAhead: 30        # optional, 1 to 365
    pollSeconds: 900     # optional, 300 to 86400: at most how often a good read is repeated
```

| Secret file (under `deploy/compose/secrets/adapters/`) | What |
|---|---|
| `calendar_link` | the private ICS link: `https://` or `webcal://`, or plain `http://` only on your own network (a single-label host or a private address). No user name or password in it. Or its path in `RHYTHM_CALENDAR_LINK_FILE` |
| `calendar_link_2`, `calendar_link_3` | a second and third calendar, optional; one link a file |

## Who sees these events

- **Today:** everyone the install admits, whatever `owner` says. The household shares everything
  ([../security.md](../security.md)).
- **With `memory.scopes` on** (off by default): a person's calendar reaches only AI clients and app sessions tied to that
  person, plus Family if a source share opens it (`rhythm share add alex calendar`); `owner: household` reaches everyone in
  Family ([../dev/memory.md](../dev/memory.md) section 10.2).

## How it reads

- **Recurring events** are read on their own clock, so a 9 o'clock class stays at 9 across a clock change. Daily, weekly,
  monthly, yearly and shorter (`HOURLY`, `MINUTELY`) rules are read with their counts, end dates, intervals and `BY` parts,
  and `RDATE`, `EXDATE` and `RECURRENCE-ID`: an occurrence that was moved keeps its star and shows its new time, and one
  deleted leaves. A series of more than 200 occurrences in the window is cut to its first 200, and the log says so.
- **Time zones.** A time in a named zone (an IANA name, or a Windows one where the system knows it), a UTC time, a time with
  no zone (read on your install's clock) and an all-day date are all read. A zone the system does not know is read from the
  definition inside the feed, which is how Outlook often writes them. A zone with neither is read on your install's clock,
  and the star says its time zone is not known here. So does a definition that costs more than a real one could: any rule but
  a yearly one, a zone past a feed's first 500, or more clock changes than a zone has. A time the clocks skip is read with the
  offset before the change, and a time they pass twice as the first of the two.
- **Cancelled events.** A cancelled event, series or single occurrence is no star, and its star leaves the map at that
  rebuild. Older copies of an event in the same feed give way to its newest.
- **A bad feed.** An answer that is not a whole calendar (a sign-in page, a feed cut off part way) adds nothing and removes
  nothing, and the log says so. One event it cannot read (no start, a rule it does not understand) is left out and counted,
  and the rest are listed.
- **An unreachable feed.** A failed read adds nothing, removes nothing and waits longer each time, from `pollSeconds` or a
  quarter of an hour, whichever is shorter, doubling up to six hours. Stars go on showing while the last read is recent
  (`graph.freshnessSeconds`), then leave the map, as every source's do when it is down, and return under the same ids when it
  answers.

What a read could not do is in the engine's log, each map rebuild, by kind and count: `Map rebuild: calendar: the calendar's
server no longer knows the link; it may have been reset`.

## Limits

- Read only. It never adds, changes or answers an event.
- Stars have no similarity links: the occurrences of a series share their words, so they would all read as copies of each
  other. Events link to nothing on the map.
- `EXRULE` is not read (the format has dropped it). A rule that repeats by the second is not read.
- A redirect is followed up to three times, and only to `https` on a public name and a public address: no private address in
  any form, no name without a dot or with a trailing one, none in a private suffix (`.local`, `.lan`, `.internal`,
  `.home.arpa` and the like), and none that resolves into your own network. A calendar on your own network is read from the
  link you enter, and a redirect it gives is not followed.
- A feed larger than 16 MiB, or holding more than 50 000 events, is refused. At most 1000 events a pass become stars,
  and never more than what `graph.maxMemories` leaves after your memories and other sources.
