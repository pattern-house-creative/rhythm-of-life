# The starter workspace

Run `./rhythm init` and `./rhythm up`, then open **Sources & settings → Dashboards**. On Windows use `.\rhythm.ps1`. Setup offers Projects and Agents by default, independently of demo data. For an empty workspace choose No at the starter prompt or pass `--starter no`. The starter enables `memory.tools`, `memory.work` and `memory.capsules`. Capsule editing remains available to the owner through Memories or the CLI.

## Collections and the Projects dashboard

A collection groups memories, tickets and capsule entries. Open **Memories → Collections**, choose a collection, and select **Edit → Show in Projects** to list it on the Projects dashboard. A designated collection appears with zero tickets. Archiving hides it and restoring brings it back with its previous designation. The Ideas inbox gathers ideas independently. In Memories, open an idea and choose **Start a project** to give it its own collection. Its original note becomes the overview and keeps its source links and history. The existing agent tools still accept the collection slug in the `project` field.

On upgrade, Rhythm recognizes collections with open work, tagged project overviews, or a starter project guide. It keeps memory and collection IDs, source links, and capsule assignments. Existing starter and supported portfolio pages receive a new dashboard version. A separately customized page can add a feed of kind `projects` to render its reader’s designated collections alongside its existing ideas feeds.

## T3 team reporting

Open **Agents → T3 team** to see the master orchestrator, project leads and their workers when a connected dispatcher reports. Each worker links to its original ticket. The view distinguishes a ticket claim, the last observed session state, and an overdue report. The dispatcher stores its latest shared report in the Agent Handoffs collection. A fresh workspace shows a waiting message until a dispatcher connects.

## Try a project

1. Connect your agent using the address and token file reported by `rhythm up`. Ask it to read the core capsule and the `first-project` capsule. On installs with personal scopes, request the shared workspace to see these shared starter records.
2. Ask it to save an idea in `project-ideas`. Open Projects → Ideas inbox. Memories tagged `project-idea` from other projects appear here too.
3. Ask it to create a Ready item in `first-project` with an outcome and a check. Have it claim the item. Open Agents to see who holds it. When it reaches Review, check the evidence and move it to Done. Items, dependencies and milestone progress come from the same records on both boards.
4. Edit your first project's current state, decisions and open questions in Memories. Use `./rhythm starter project my-project` to add that structure to a named shared project. Core identity and personal preferences are yours to supply.

### Instructions for a repository agent

Copy this paragraph into your repository's agent instructions if your client needs a reminder. MCP clients also receive the workflow when they connect.

> At the start of Rhythm project work, read the core and project capsules and look at open work. Claim an available Ready item before beginning. Keep the claim while working and record checks on the item. Move it to review when it needs its owner. Log verified shared work in the activity log identified by the Agent Handoffs capsule. Keep private work in its original scope. If stopping early, record what remains and release the claim. Ask the owner before merging, deploying, publishing, or setting up automatic dispatch.

## Add the optional grocery list

Run `./rhythm starter groceries`, open **Dashboards → Groceries** and choose **Allow edits**. Add an item and mark it purchased when you shop. The list uses your household’s native Rhythm log. Everyone with access to that shared list sees the same items, including when two additions have the same name. Choose **Undo** after a purchase to put that item back on the list. Google Drive and external writers are optional elsewhere in Rhythm.

## Existing installations

To add Projects and Agents, set `memory.tools`, `memory.work` and `memory.capsules` to `on` in your existing settings, run `./rhythm up`, then `./rhythm starter workspace`. For Groceries alone, enable `memory.tools`. These commands use the first configured person’s terminal login for the shared workspace. They preserve existing dashboard titles, occupied capsule slots and the current sharing settings. An installation receipt in the database prevents retries, restarts and upgrades from replacing your edits or recreating a template you removed.
