# Second Brain comparison (SB-P8-03)

The comparison harness of the move from Second Brain to Rhythm's native memories (the migration runbook
section 2; stories 16 and 18), on synthetic data only: the fictional deployment `sb-export-brain.json` and the export
files taken from it ([fixtures.md](fixtures.md#second-brain-export-fixtures)). No real Second Brain, export, Drive or
credential was read. Built in run 17 (`sb/p8-import`), with the importer it measures (SB-P8-01, migration 059,
[../memory.md](../memory.md) section 9).

## What it does

`SecondBrainComparisonTests` (with `engine/tests/Knowledge.Tests/Parity/SecondBrainComparison.cs`) makes one throwaway
install with memory scopes on, Alex and Sam in Family, and puts both sides of the brain in it:

- **the read-only adapter**, unchanged, reading a fake Second Brain service (`BrainService`) that answers Alex's token
  from the brain as upstream's routes would at the brain's moment: her personal workspace and the Family team, live
  memories with their validity and retraction state, projects and edges. The service answers GET alone and fails the
  test on anything else; the map is rebuilt once over it;
- **the native memories** the importer brought from Alex's and Sam's version 3 exports, with the owner's scope map
  (every memory of the Family workspace).

It pairs a record of one side with the other's only through the origin map (the Second Brain id), never by title or
text, and writes a report in the brain's own keys, so the same brain and code give the same report:

1. **Memories on the map**, per reader (Alex, Sam and the household view): on both sides, on one side only, and why.
2. **Owner and project** of each pair on Alex's map.
3. **Links** between two memories, type and verification, against the brain's edges Alex reads, with why a side lacks
   one.
4. **Who may view and edit** each memory: upstream's rule as the brain holds it (a member reads their personal
   workspace and every team they are on; on a team's memory only its author or an admin edits, SEC-04) against
   Rhythm's read predicate and author lock (`memory_may_change`), for Alex and Sam.
5. **Core capsules** of Alex's private scope and Family: upstream's built from the brain's canonical slotted memories by
   the same select and layout rule (`Capsule.Select`, `Capsule.Answer`, upstream's text byte for byte, SB-P3-08),
   against `PromptCapsules.Build`: slots, text (`prompt_hash`) and ETag.
6. **Retention**: upstream's trash, versions and audit events against what came across.
7. **Questions** asked as Alex: `recall` as the engine serves it (words only, no model, with its source checks), and the
   map's search box, for twelve questions each naming one memory; the expected memory's rank on each side.

The report is checked line for line (`Parity/sb-comparison-report.json`), so a change of either side that moves any
comparison shows in the test. To change it on purpose, run `RHYTHM_SB_COMPARE_WRITE=<folder> sh engine/test-db.sh
--filter SecondBrainComparisonTests`, copy the file it writes over the checked-in one, and say why in the commit.

## What it found

- **The same memories, owners and projects** wherever both sides serve a memory: 20 pairs on Alex's map, owner and
  project the same for each. Each side leaves out what it means to: the adapter a memory held upstream, a draft, one
  built on a retracted source, one no longer current, and one whose id is not a uuid (an id a client chose before 4.0);
  the native map a memory marked wrong (`status:deprecated`), which the adapter does serve, since its eligibility rule
  filters drafts and retractions but not deprecated memories.
- **Who views what matches** upstream's rule for both people (29 memories for Alex, 25 for Sam).
- **Who edits what does not, for Family memories someone else wrote.** The export names no author, so the importing
  person is every memory's author: the five Family memories Sam wrote in Second Brain are Alex's here, and Sam may no
  longer change them (Rhythm's author lock lets the author or a Family admin change a Family memory). Alex, the
  deployment's admin, edits the same memories on both sides. See the proposed decision below.
- **Links agree** where both ends are on both sides. The adapter shows none whose end it leaves off its map (the
  replaced tariff's supersede, the insight's sources, the retracted stall's cause, the running plan's link); the
  native side has each. Neither side links a Family memory to Alex's private one (two workspaces upstream, two scopes
  here).
- **Capsules**: the same memory in the same slot and the same text (`prompt_hash`); the ETag differs, because the
  answer it is the hash of names each side's own memory ids (`source_entry_id`). Identical ETags would need the native
  answer to name Second Brain's ids, which it should not; the runbook's check is the slots and the text.
- **Retention**: none of upstream's 2 trashed memories, 5 versions or 10 audit events comes across; each imported
  memory's history starts at its import, which its audit row says.
- **Recall**: the native side finds every expected memory first (12 of 12). Through the adapter, recall and search serve
  none of its memories at all: a reference-only record passes no source check (`Retrieval.Validate` serves an
  archive's rows and a live record its document source confirms; the adapter is neither), so today the adapter's
  memories reach a reader on the map alone. The map's search box finds a memory by its title, its opening words, on
  either side (native 9 of 12, adapter 8 of 12); words further in (a booking reference, a breakdown cover's company)
  only recall finds, natively.

## Proposed decisions (for the integrator)

- **The author of an imported Family memory.** Today the importing person (the export names none). Proposal: let the
  owner's reviewed scope map name a Family memory's author as a person of this install, taken from the same approved
  read as the map itself; without one the importing person stays the author, and the importer's report says how many
  Family memories that leaves changeable by them and the Family admins only.
- **The runbook's capsule check** is slots and text, not the ETag (above).
- **The adapter's eligibility rule and deprecated memories.** The adapter serves a memory upstream marked wrong; the
  native map does not. Changing the adapter changes what existing installs show, so it is a separate item, off by
  default if taken.

## Limits

- Upstream's routes and rules are restated from the pinned source and the brain (`BrainService`, SEC-04 for the author
  lock); nothing here ran upstream's code. `validity_state` is taken as current, replaced (a supersedes edge names it)
  or ended, and `retracted_source` from the `retracted-source` tag.
- One token: the adapter reads Alex's workspaces only, as an install configures it with one token. Sam's personal
  workspace and the beekeepers' team are compared through the view and edit rules, not through the adapter.
- Words only: the model is off in the harness, so recall's meaning arm and the adapter's vectors are not compared.

## The report

```text
Second Brain comparison: the read-only adapter (Alex's token) against the native memories imported from Alex's and Sam's exports with the owner's scope map.
Brain: 40 live memories in 5 workspaces, 2 in the trash; native: 39 imported.

1. Memories on the map, per reader (paired by the origin map, never by title)
  alex: 20 on both sides; 1 on the adapter's side only; 3 on the native side only
    adapter only: family-fair-wrong (marked wrong: Rhythm's map leaves it out, the adapter does not filter deprecated)
    native only: alex-running-plan (its id is not a uuid (chosen by a client before 4.0): the adapter leaves it off)
    native only: family-fair-stall (built on a retracted source: the adapter leaves it off)
    native only: family-insight (a draft: the adapter leaves drafts off)
  sam: 10 on both sides; 1 on the adapter's side only; 9 on the native side only
    adapter only: family-fair-wrong (marked wrong: Rhythm's map leaves it out, the adapter does not filter deprecated)
    native only: bees-inspection (in Wickmere beekeepers, which Alex's token does not read)
    native only: bees-swarm (in Wickmere beekeepers, which Alex's token does not read)
    native only: family-fair-stall (built on a retracted source: the adapter leaves it off)
    native only: family-insight (a draft: the adapter leaves drafts off)
    native only: sam-allotment (in Sam, which Alex's token does not read)
    native only: sam-japanese (in Sam, which Alex's token does not read)
    native only: sam-owed (in Sam, which Alex's token does not read)
    native only: sam-party (in Sam, which Alex's token does not read)
    native only: sam-promise (in Sam, which Alex's token does not read)
  household: 10 on both sides; 1 on the adapter's side only; 2 on the native side only
    adapter only: family-fair-wrong (marked wrong: Rhythm's map leaves it out, the adapter does not filter deprecated)
    native only: family-fair-stall (built on a retracted source: the adapter leaves it off)
    native only: family-insight (a draft: the adapter leaves drafts off)

2. Owner and project of each memory on both sides (Alex's map)
  20 pairs: owner the same for 20, project the same for 20

3. Links between two memories (type, verification), against the brain's edges Alex reads
  the brain: 13; the adapter's map: 7; native (every imported link): 14
    about_person family-choir-concert family-about-sam verified: adapter yes, native yes
    caused_by family-fair-stall family-fair-wrong verified: adapter no (an end is off the adapter's map), native yes
    decided alex-sabbatical-decision alex-sabbatical-route verified: adapter yes, native yes
    drawn_from family-insight family-tariff-new inferred: adapter no (an end is off the adapter's map), native yes
    drawn_from family-insight family-tariff-old inferred: adapter no (an end is off the adapter's map), native yes
    follows alex-sabbatical-budget alex-sabbatical-route suggested: adapter yes, native yes
    part_of_project family-boiler-visit family-home-energy-hub verified: adapter yes, native yes
    part_of_project family-heatpump-quote family-home-energy-hub verified: adapter yes, native yes
    relates_to alex-contract-link alex-job verified: adapter yes, native yes
    relates_to alex-knee alex-running-plan suggested: adapter no (an end is off the adapter's map), native yes
    relates_to alex-payment-plan family-boiler-visit verified: adapter no (two workspaces), native no (its ends are in two scopes here, Family and alex, which never link)
    relates_to family-heatpump-quote family-tariff-new suggested: adapter yes, native yes
    supersedes family-tariff-new family-tariff-old inferred: adapter no (an end is off the adapter's map), native yes
    follows bees-swarm bees-inspection suggested: not one of the brain's edges Alex reads (adapter no, native yes)
    relates_to sam-allotment sam-promise suggested: not one of the brain's edges Alex reads (adapter no, native yes)

4. Who may view and edit each memory: upstream's rule (the brain's workspaces, memberships and authors) against Rhythm's predicate and author lock
  alex views: 29 on both; 0 upstream only; 0 native only
  alex edits (of those both sides let them view): 29 on both; 0 upstream only; 0 native only
  sam views: 25 on both; 0 upstream only; 0 native only
  sam edits (of those both sides let them view): 10 on both; 5 upstream only; 0 native only
    upstream only: family-bake-sale (written upstream by Sam; here its author is alex, the importing person)
    upstream only: family-choir-concert (written upstream by Sam; here its author is alex, the importing person)
    upstream only: family-fair-date (written upstream by Sam; here its author is alex, the importing person)
    upstream only: family-fair-wrong (written upstream by Sam; here its author is alex, the importing person)
    upstream only: family-tariff-new (written upstream by Sam; here its author is alex, the importing person)

5. Core capsules: the same canonical memories in the same slots, the same text, and the ETag
  Alex's private core: upstream [identity=alex-capsule-identity], native [identity=alex-capsule-identity]; slots the same, text (prompt_hash) the same, ETag differs (the answer names each side's own memory ids)
  Family's core: upstream [], native []; slots the same, text (prompt_hash) the same, ETag the same

6. Retention
  trash: upstream 2 (alex-library in Alex, family-bin-day in Family), native 0: the export carries none
  versions: upstream 5 of 5 memories, native 0: history starts at the import (39 imported audit rows)
  audit events: upstream 10, not exported

7. Questions asked as Alex: recall, as an AI client asks it (words only, no model), and the map's search box (a star's title)
  "Larkspur Labs audio lead" -> alex-job: recall native #1, adapter missing; map search "audio lead" native found, adapter found
  "lemon drizzle bake sale" -> family-bake-sale: recall native #1, adapter missing; map search "lemon drizzle" native found, adapter found
  "heat pump quote grant" -> family-heatpump-quote: recall native #1, adapter missing; map search "grant" native found, adapter found
  "Roadwise breakdown cover" -> family-car: recall native #1, adapter missing; map search "Roadwise" native missing, adapter missing
  "BW-5521" -> family-boiler-visit: recall native #1, adapter missing; map search "BW-5521" native missing, adapter missing
  "Marram coast path" -> alex-sabbatical-decision: recall native #1, adapter missing; map search "Marram" native missing, adapter missing
  "summer fair Saturday 19 June" -> family-fair-date: recall native #1, adapter missing; map search "19 June" native found, adapter found
  "physio knee appointment" -> alex-knee: recall native #1, adapter missing; map search "knee" native found, adapter found
  "electricity tariff per kWh" -> family-tariff-new: recall native #1, adapter missing; map search "Lumen Agile" native found, adapter found
  "Saltreach Singers" -> family-about-sam: recall native #1, adapter missing; map search "Saltreach" native found, adapter found
  "KW-20931 pottery" -> alex-present: recall native #1, adapter missing; map search "pottery" native found, adapter found
  "half marathon long run" -> alex-running-plan: recall native #1, adapter missing; map search "Half marathon" native found, adapter missing
  recall found native 12 of 12, adapter 0 of 12 (0 adapter rows served in all): a reference-only record passes no source check (Retrieval.Validate serves an archive's rows and a live record its document source confirms), so recall and search serve none of the adapter's memories
  map search found native 9 of 12, adapter 8 of 12: a star's title is its opening words, so words further in are on neither side's map, and the adapter misses besides a memory it leaves off its map
```
