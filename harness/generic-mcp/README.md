# harness/generic-mcp

Connecting Claude Code, Claude Desktop or any other MCP client to your install. ChatGPT has its own
page, [../chatgpt/](../chatgpt/README.md).

The engine has one MCP endpoint, `POST /mcp`, over Streamable HTTP, stateless, with a bearer token per
client. A client needs three things:

| What | Value |
|---|---|
| URL | `engine.publicMcpUrl` from `rhythm.yaml`; by default `http://127.0.0.1:8181/mcp` (this computer only) |
| Transport | Streamable HTTP (often called `http`; not `sse`, not `stdio`) |
| Header | `Authorization: Bearer <token>`, the token from the client's own token file |

A client on any MCP protocol from 2024-11-05 to the newest connects this way, and gets the same tools and answers on
each; the older HTTP+SSE transport (`sse`) and JSON-RPC batches are refused. Every request carries its token and
nothing is kept between requests, so a client carries on across an engine restart without reconnecting.

A client on another computer needs an exposure recipe first ([deploy/exposure/](../../deploy/exposure/README.md)):
Tailscale for your own devices, Cloudflare for services on the internet.

## 1. A token per client

From the repository root:

```sh
./rhythm token create claude-code     # .\rhythm.ps1 token create claude-code in PowerShell
```

- The token goes to `deploy/compose/secrets/harness_claude-code_token` and is shown to no one. Names
  are 1-40 lower case letters, digits, `-`, `_` or `:`; one per client, so revoking one leaves the
  others working. The first `./rhythm up` already made one named `assistant`.
- On Linux the file belongs to you (the owner of `deploy/compose`) with mode 0600.
- `./rhythm token list` shows the tokens by name; `./rhythm token revoke <name>` turns one off at once.

A harness token reads everything in the install (the household shares everything), can save
insights, dashboards and documents, and can change what the household's open screens show
([../chatgpt/](../chatgpt/README.md#6-screens) has example requests). It does not see the app's or
plugins' tools.

## 2. Claude Code

```sh
claude mcp add --transport http rhythm http://127.0.0.1:8181/mcp \
  --header "Authorization: Bearer $(cat deploy/compose/secrets/harness_claude-code_token)"
claude mcp list
```

This stores the token in Claude Code's own settings (scope `local` by default; `--scope user` for
every project). To keep it out of any settings file, use a project `.mcp.json`, which Claude Code
fills from your environment:

```json
{
  "mcpServers": {
    "rhythm": {
      "type": "http",
      "url": "http://127.0.0.1:8181/mcp",
      "headers": { "Authorization": "Bearer ${RHYTHM_MCP_TOKEN}" }
    }
  }
}
```

and start Claude Code with `RHYTHM_MCP_TOKEN="$(cat deploy/compose/secrets/harness_claude-code_token)" claude`.
Inside Claude Code, `/mcp` shows whether it connected and how many tools it found.

For two Claude accounts on one computer, such as a personal and a work one, see
[claude-code-profiles.md](claude-code-profiles.md).

## 3. Claude Desktop and other clients

- **Clients that take a URL and headers** (Claude Code, VS Code, Cursor, most editors): use the URL,
  `http` transport and the `Authorization` header as above. Look for "headers" in the client's MCP
  settings.
- **Claude Desktop** reads local servers from `claude_desktop_config.json` as commands. A small
  stdio bridge such as `mcp-remote` connects it to an HTTP server with a header:

  ```json
  {
    "mcpServers": {
      "rhythm": {
        "command": "npx",
        "args": ["-y", "mcp-remote@0.15.1", "http://127.0.0.1:8181/mcp", "--header", "Authorization:${RHYTHM_AUTH}"],
        "env": { "RHYTHM_AUTH": "Bearer <paste the token here>" }
      }
    }
  }
  ```

  The token then lives in that file: keep it private. The bridge's own README has its flags (for
  example for a non-HTTPS address that is not `localhost`).
- **Connectors added by URL in a hosted app** (custom connectors in Claude Desktop or on the web, and
  ChatGPT) connect from the provider's servers, so they need a public HTTPS URL (the Cloudflare
  recipe). A Claude custom connector can send a bearer token where the account offers request
  headers, which not every account does: see [claude-hosted.md](claude-hosted.md). Hosted ChatGPT can
  use the optional [OAuth setup](../chatgpt/oauth.md) on the same URL; it is off by default.

## 4. The dashboard guide

Before building a dashboard, a model should read the design guide: layout, colours, compact and tall
cards for the ambient screen, reduced motion, no outside requests. The engine serves it as the MCP **resource**
`rhythm://guides/dashboards` and as a **prompt** of the same name, and `save_dashboard`'s description
points to it.

- In Claude Code, mention it (`@` lists MCP resources) or ask Claude to read `rhythm://guides/dashboards`
  before it calls `save_dashboard`.
- A client that shows the model no resources or prompts: read the guide once with the Inspector
  (below) and paste it into the instructions you use for dashboards (open question Q-062).

## Checking with the MCP Inspector

The [MCP Inspector](https://github.com/modelcontextprotocol/inspector) is the reference client for
checking a server. Its CLI makes one call per run:

```sh
TOKEN="$(cat deploy/compose/secrets/harness_my-assistant_token)"
npx -y @modelcontextprotocol/inspector@2.11.0 --cli http://127.0.0.1:8181/mcp --transport http \
  --header "Authorization: Bearer $TOKEN" --method tools/list
npx -y @modelcontextprotocol/inspector@2.11.0 --cli http://127.0.0.1:8181/mcp --transport http \
  --header "Authorization: Bearer $TOKEN" --method resources/read --uri rhythm://guides/dashboards
```

`tools/list` shows 29 tools (28 while the local semantic model is off), and more where the install turns them on:
`recall` with `engine.recall: on`, `get_prompt_capsule` with `memory.capsules: on`, and the 41 memory tools
(`remember` to `resolve`, [../memory.md](../memory.md)) with `memory.tools: on`. `--web` instead of `--cli`
opens its browser UI on `http://127.0.0.1:6274`, which connects through its own local backend; add the
`Authorization` header in its connection settings.

Without any client, `curl` shows the same:

```sh
curl -s http://127.0.0.1:8181/mcp -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

## Troubleshooting

The engine's answers (`docs/dev/engine.md`), from the outside:

| You see | Cause | Fix |
|---|---|---|
| connection refused | nothing listens there: the stack is down, the port differs (`RHYTHM_MCP_PORT`), or the engine is bound to `127.0.0.1` and you are on another computer | `docker compose ps`; use an exposure recipe for other computers |
| `401`, `unauthorized` | no `Authorization: Bearer` header (the word `Bearer` exactly so, then a space), a token that was revoked or never created, or extra text pasted with it. A revoked token is refused on its very next call, even mid-conversation | check the header; `token list`; create a new token. OAuth clients need the optional [OAuth setup](../chatgpt/oauth.md) enabled by the operator |
| `403`, "does not answer to this host name" or "Browser calls are not accepted" | the `Host` the engine received is not in `engine.allowedHosts`, or the request carried an `Origin` header (a browser, or a web page calling the engine directly) | add the public host name to `engine.allowedHosts` and restart the engine; call from a client, not a page |
| `403`, `forbidden` | the token is not a harness token (the app's or a plugin's token), or the tool is not in this role. A plugin's token connects but lists only its two ingest tools | use a token made with `token create <name>` |
| `404` | the path is not exactly `/mcp`: a trailing slash, or the base URL without `/mcp` | end the URL in `/mcp` |
| `405` | a `GET` (or other method) where the engine expects `POST`: often a client set to the old `sse` transport | set the client's transport to Streamable HTTP (`http`) |
| `406`, "must accept both" | the client's `Accept` header names neither both `application/json` and `text/event-stream` nor a wildcard such as `*/*` (only one of them, or none, is refused) | use a Streamable HTTP client, or send both as the `curl` example does |
| `400`, "Mcp-Session-Id header is not supported" | the client sends a session id, kept from another server at this address: the engine keeps no session and hands out none | remove the server from the client and add it again |
| `413`, `too_large` | the request body is over the limit | send smaller arguments (dashboard pages are at most 300,000 characters) |
| a write answered `write_limit` | `engine.writeLimit` is on and this token made 300 writes in 10 minutes | wait the seconds it names; reads still answer, and nothing was saved |
| `503` | the engine is starting (migrations not recorded yet) or the database is not answering | wait; `docker compose ps` and `logs engine`; `GET /health` on the engine says `starting` until ready. A client already connected carries on once it is ready, with no new connection: the engine keeps no session, across a restart too |
| a `400` or `-32602` for a tool | arguments outside the tool's schema, the tool belongs to an adapter that is off, or a memory tool on an install with `memory.tools` off | read the tool's description; refresh the client's tool list after an upgrade or a settings change, and reconnect a client connected while `memory.tools` was on, whose instructions still name the memory tools |
| Cloudflare or proxy login page instead of JSON | the proxy asks for a login on `/mcp` | give `/mcp` no proxy login (the engine's token is the gate there); see the recipe |
| `502` or `504` from a proxy | the proxy cannot reach `engine:8080` | an app-facing proxy uses the compose `edge` network, or point at `127.0.0.1:8181` on the host |
