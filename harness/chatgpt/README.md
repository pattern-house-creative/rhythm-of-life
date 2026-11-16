# harness/chatgpt

Connecting ChatGPT to your install's MCP endpoint, and running the daily insight as a ChatGPT
scheduled task. Other clients (Claude Code, Claude Desktop, the MCP Inspector) are in
[../generic-mcp/](../generic-mcp/README.md).

| File | What |
|---|---|
| `README.md` | this page: the connector, the token, the scheduled task, upgrades |
| [`daily-insight.md`](daily-insight.md) | the daily insight prompt, a template with placeholders for your install |

ChatGPT's menus change often; the steps below name what to look for rather than exact clicks.

## 1. A route from OpenAI to `/mcp`

ChatGPT connects from OpenAI's servers, not from your computer. It cannot reach `127.0.0.1`, your home
network or a tailnet. There are two routes:

| | OpenAI Secure MCP Tunnel | Cloudflare Tunnel and Access |
|---|---|---|
| Recipe | [deploy/exposure/openai-tunnel/](../../deploy/exposure/openai-tunnel/README.md) | [deploy/exposure/cloudflare/](../../deploy/exposure/cloudflare/README.md) |
| How | a container dials out to OpenAI and passes ChatGPT's calls to the engine inside the stack | `/mcp` on a public HTTPS host name of your own, carried to the engine by `cloudflared` |
| Reachable from the internet | nothing | `/mcp` (with its token as the gate) and the app behind Cloudflare Access |
| The token | stays on the server: the tunnel adds it | pasted into ChatGPT |
| Who can use it | whoever OpenAI lets use the tunnel (your ChatGPT workspace, associated Platform organizations) | anyone holding the token |
| Needs | an OpenAI Platform organization with tunnels, and a runtime key | a domain on Cloudflare and a Zero Trust organization |
| Other clients | OpenAI's only (ChatGPT, Codex, the Responses API) | any hosted MCP client; the household's phones reach the app too |

Pick the **OpenAI tunnel** when ChatGPT is the hosted client you need: nothing becomes reachable from the
internet and there is no domain to look after. Pick **Cloudflare** when the household should also open the app
away from home without Tailscale, or another hosted AI service needs `/mcp`. Both can run at once; give each
route its own token. For the tunnel, follow its recipe (it makes the token and the connector), then continue at
[step 4](#4-the-daily-insight-task). The rest of this step and steps 2 and 3 are the Cloudflare route.

When the Cloudflare recipe is done you have:
- an MCP URL such as `https://rhythm-mcp.example.invalid/mcp`;
- that host name in `engine.allowedHosts` in `rhythm.yaml` (the engine refuses any other `Host`);
- `engine.publicMcpUrl` set to the same URL.

Check from any computer (an empty `Authorization` must give `401`, not a Cloudflare login page):

```sh
curl -s -o /dev/null -w '%{http_code}\n' -X POST https://rhythm-mcp.example.invalid/mcp
```

## 2. A token for ChatGPT

Each MCP client gets its own token (role `harness`), so you can revoke one without touching the
others. From the repository root:

```sh
./rhythm token create chatgpt     # .\rhythm.ps1 token create chatgpt in PowerShell
```

The token is written to `deploy/compose/secrets/harness_chatgpt_token` and shown to no one. Open that
file once to copy the token into ChatGPT, and don't paste it anywhere else.

A harness token reads everything in the install (the household shares everything), can save
insights, dashboards and documents, and can change what the household's open screens show. It cannot
see the app's or plugins' tools.

## 3. Add the connector

In ChatGPT, find the settings for connectors or apps and create a custom MCP connector (on some plans
this needs developer mode turned on first):

| Field | Value |
|---|---|
| Name | your instance name, for example `Rhythm of Life` |
| MCP server URL | your MCP URL from step 1, ending in `/mcp` |
| Authentication | the bearer token or API key option; paste the token from step 2 |

If your ChatGPT account offers OAuth and no fixed-header option, enable the optional
[OAuth connection](oauth.md) on this same URL. Existing bearer clients continue to work.

Check it: in a new chat with the connector on, ask "List the tools you have from Rhythm of Life", then
"Call list_insights". You should see 29 tools (28 while the local semantic model is off) and the
DEMO insight if demo data is on; an install with `memory.tools: on` adds the memory tools
([../memory.md](../memory.md) lists them), `engine.recall: on` adds `recall` and `memory.capsules: on` adds `get_prompt_capsule`.

## 4. The daily insight task

1. Open [`daily-insight.md`](daily-insight.md), replace the placeholders (`{{instance}}`,
   `{{people}}`, `{{subjects}}`, `{{timezone}}`, `{{label}}`) with your values, and copy everything
   below its line.
2. In ChatGPT, create a scheduled task that runs daily, early in the morning in your time zone, and
   paste the prompt as its instructions.
3. Turn on the Rhythm of Life connector for the task (and any other connector it should use to open
   originals, such as a cloud drive).
4. Let it run once, then look in the app: the insight card shows the new insight, or the run line says
   the run was quiet or blocked, with the reason.

With `memory.tools: on`, steps 1b and 3b of the prompt apply as well: the task reads the old and new memories a nightly
pass paired, declines those that say nothing (recorded, and never offered again) and may write its one insight from a
pair, citing both memories. What it saves is proposed: it never confirms anything, and a person does, through another
client, with `review_insight`. With it off the tools aren't listed, the task skips those two steps, and the prompt runs
as it always did. An existing task needs the new prompt pasted in to read them.

Step 0b of the prompt keeps what the household finds useful in ChatGPT's own memory, if memory is on
for the task. Nothing about that is stored in Rhythm; feedback given in the app is, and the task reads
it with `list_insights`.

## 5. Dashboards

The dashboard design guide (layout, colours, compact and tall cards, reduced motion) ships with the
engine as the MCP resource and prompt `rhythm://guides/dashboards`, and `save_dashboard`'s description tells the
model to read it first. If ChatGPT cannot read MCP resources or prompts, give it the guide once: read
it with the MCP Inspector ([../generic-mcp/](../generic-mcp/README.md#checking-with-the-mcp-inspector),
Resources tab) and paste it into the project or custom instructions you use for dashboards (open
question Q-062).

A dashboard can be a grid of parts rather than one page: its page beside the app's own map, health tiles, daily
insights and other dashboards' cards (`save_dashboard`'s `layout`, on a grid of 12 by 12 cells). The app draws those
parts itself, in its own fonts and colours, so ChatGPT adds them as parts instead of drawing them in the page, and can
narrow a map part to what the dashboard is about with a `focus` (near a star, from a source, matching some text). For
example, "Make me a training dashboard with the map on the left" saves a layout like
`{parts: [{id: map, kind: map, x: 0, y: 0, w: 7, h: 12, focus: {near: {star: <the training plan>}}}, {id: page, kind:
page, x: 7, y: 0, w: 5, h: 8}, {id: glucose, kind: glucose, x: 7, y: 8, w: 5, h: 4}]}`. A later version keeps the
arrangement unless it brings its own, and `dashboardId`, `layout` and `note` alone rearrange the newest version
without making a new one. On a phone the parts stack in one column, top left first.

## 6. Screens

Every open copy of the app is a **screen** with a name: the kind of device and browser at first ("iPad · Safari",
"Windows PC · Chrome", and "Windows PC · Chrome 2" when another screen already has that name), and whatever you type
under "This screen's name" in the app's Details panel after that. ChatGPT sees them with `list_screens`, including
what each one shows now and its Home, and changes what one shows with `control_screen`. The screen shows the change
within about a second, with a small note, "From your assistant: …", and an Undo button.

Ask in plain words, and name the screen when more than one is open:

| You ask | ChatGPT calls `control_screen` with |
|---|---|
| "Show the Training plan star on the living room screen" | `action: open_star`, `screen: Living room`, the star's id (found with `explore_graph`) |
| "Switch the living room screen to the Rhythm band layout" | `action: layout`, `screen: Living room`, `layout: band` (`corners` is Corners, `hero` Tall column) |
| "Move the glucose tile to the bottom right" | `action: move_tile`, `tile: main`, `to: changed` (the tile there takes the top left) |
| "Search the sky for sleep on the kitchen screen" | `action: search`, `screen: Kitchen iPad`, `query: sleep`; the answer says how many stars matched |
| "Undo that" | `action: undo`, which reverses the last command that screen applied |
| "Show the sleep dashboard full screen on the kitchen screen" | `action: full_screen`, `screen: Kitchen iPad`, the dashboard's id (from `list_dashboards`); without a dashboard, the one open there |
| "Make the sleep dashboard the kitchen screen's Home" | `action: set_home`, `screen: Kitchen iPad`, the dashboard's id; that screen then shows it full screen whenever it goes Home |
| "Put the kitchen screen's Home back to normal" | `action: set_home`, `screen: Kitchen iPad`, no dashboard: the sky and tiles again |
| "Put the map on the right" (a dashboard is open there) | `action: move`, `part: map`, `place: right`, and the dashboard's id; the parts beside it trade sides with it |
| "Make the map bigger" | `action: resize`, `part: map`, `size: bigger` (or `w` and `h` in cells) |
| "Only my Garmin stars on this map" | `action: focus_map`, `focus: {providers: [garmin]}`; no focus shows everything again |
| "Make the sky bigger on the kitchen screen" (at Home) | `action: resize`, `screen: Kitchen iPad`, `part: map`, `size: bigger`, `dashboard: null`; that screen's Home only |

- Without a screen name, the command goes to the open screen someone used last (touched, clicked or typed on), and
  when no screen says, the one seen most recently. A screen whose app was just hidden or closed is passed over for one
  that is still listening. That guess can be wrong with several screens on, so ChatGPT is told
  to name the screen then. A name two open screens share is refused, and ChatGPT is told which ones.
- A screen counts as open while the app is visible and online on it, and for a minute after. ChatGPT waits up to 8
  seconds and says whether the screen showed it, failed (and why), or has not confirmed yet. A command the screen has
  not picked up within 30 seconds expires and is never shown later.
- A star, dashboard or search opened this way stays until the next command or Home. Once someone uses that screen,
  it goes back Home after the usual idle minutes again.
- Full screen shows only the dashboard's page: no sky, tiles or panels, and a small exit control that appears when
  someone moves the pointer or touches the screen (Escape exits too). It stays until the screen goes Home, the next
  command, or someone exits. A Home dashboard is kept on that screen only, and the Details panel can restore the
  default Home there; one deleted since leaves the screen on the default Home.
- `move`, `resize` and `focus_map` arrange what the screen shows. ChatGPT reads the part ids in `list_screens`
  (`showing.parts`) and passes the dashboard it saw there (null at Home), so a screen that has moved on since refuses
  instead of changing something else. On a dashboard the change is saved for the whole household, without a new
  version; at Home it is that screen's own, and swapping two corners stays `move_tile`.
- Commands change only what a screen shows, with two exceptions: putting a dashboard in a corner (`corner_card`) sets
  that corner's card for the whole household, as the corner-card picker does, and arranging a dashboard's parts
  changes that dashboard for everyone, as arranging it by hand does. More in
  [docs/security.md](../../docs/security.md#screens).

## 7. After an upgrade

ChatGPT keeps its own copy of the tool list. After every upgrade of the engine, open the connector's
settings and refresh it, so ChatGPT sees new tools and the current arguments. Until you do, new tools
are missing and changed ones can fail with argument errors.

## Revoking

```sh
./rhythm token revoke chatgpt
```

Then delete `secrets/harness_chatgpt_token` and remove the connector in ChatGPT. Calls with the old
token get `401` at once. On the OpenAI tunnel route the token is the tunnel's own, `openai-tunnel`: its
recipe's [Undo](../../deploy/exposure/openai-tunnel/README.md#undo) says what else to remove.

## When something fails

| ChatGPT says | Likely cause |
|---|---|
| it can't connect, or a Cloudflare page in the error | the MCP host name is behind an Access login: give `/mcp` its own host name with a Bypass policy (Cloudflare recipe) |
| `401`, unauthorized | wrong or revoked token, or the token pasted with extra text; create a new one |
| `403` with no message | the host name is missing from `engine.allowedHosts`, or the request carried an `Origin` header |
| `404` | the URL does not end in exactly `/mcp` (no trailing slash) |
| `503` | the engine is starting or the database is down; on the server, `docker compose ps` and the engine's log tell which |
| a tool is missing | refresh the connector; `semantic_search` appears only while the local model is on, `recall` only with `engine.recall: on`, `get_prompt_capsule` only with `memory.capsules: on` and the memory tools only with `memory.tools: on` |
| the tunnel is not listed (OpenAI tunnel route) | the tunnel is not associated with this ChatGPT workspace, or you lack Tunnels Read and Use |
| it can't connect through the tunnel | the `openai-tunnel` container is stopped or refused by OpenAI, or its token was revoked: its log and `/health/control-plane` ([recipe](../../deploy/exposure/openai-tunnel/README.md), step 5) |

More in [../generic-mcp/](../generic-mcp/README.md#troubleshooting).
