# OpenAI Secure MCP Tunnel

Let ChatGPT reach the engine's `/mcp` with no public address: OpenAI's tunnel client runs as one more
container in the stack, opens an outbound HTTPS connection to OpenAI, and carries ChatGPT's MCP calls to
`http://engine:8080/mcp` on the egress network. No port is published, no domain or DNS record is needed,
and nothing on the internet can connect in. OpenAI's guide:
[Secure MCP Tunnels](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels).

| | |
|---|---|
| Exposes | the engine's harness tools (`contracts/README.md` section 2.1), which read everything in the install, to whoever OpenAI lets use the tunnel ([below](#who-can-use-it)) |
| Protects it | the runtime API key (only a client holding it can serve the tunnel), OpenAI's access rules for the tunnel, the tunnel's own harness token (checked by the engine on every call), and outbound-only traffic |
| Never exposed | the app, Postgres, the host's ports (they stay on `127.0.0.1`); the client's target is the engine's `/mcp` |
| Checked | with the pinned client image against a local stack cut off from the internet: settings, secrets and the engine accepting the tunnel's token; not against a real tunnel or ChatGPT |

## How it works

```
ChatGPT ──> OpenAI's tunnel service <── outbound HTTPS ── openai-tunnel ──> http://engine:8080/mcp
```

- The `openai-tunnel` container (image `ghcr.io/openai/tunnel-client`, pinned to an exact version) polls
  `api.openai.com:443` with the runtime key and the tunnel ID, takes each MCP request ChatGPT sends to the
  tunnel, passes it to the engine with `Authorization: Bearer <the tunnel's harness token>`, and returns the
  answer the same way. Only the MCP channel is on (`--control-plane.poll-channel=main`).
- The engine sees an ordinary harness client on its internal name: `Host: engine:8080` (in
  `engine.allowedHosts` by default), no `Origin`, a harness token. Nothing in `rhythm.yaml`'s `engine` section
  changes.
- The token never leaves the server: ChatGPT's connector has no authentication of its own, and the client adds
  the token, read from a file. Revoking it (`./rhythm token revoke openai-tunnel`) cuts ChatGPT off at once.
- The container runs as uid 65532 with a read-only file system, no capabilities, `no-new-privileges`, a tmpfs
  `/tmp`, 256 MB of memory and half a CPU. Its health page and the client's own admin page answer only inside
  the container.

## Who can use it

Everyone OpenAI lets use the tunnel reaches the harness tools: people in the ChatGPT workspace you associate
with it who can add custom MCP servers there, and any other Platform organization you associate with it (for
Codex or the Responses API) whose keys have Tunnels Use. The engine cannot tell them apart: they all arrive with
the tunnel's token. Associate the tunnel only with the household's own workspace, and give Tunnels Use only to
the household (open question Q-124).

## Steps

You need an OpenAI Platform organization in which you can manage tunnels (Tunnels Read and Manage), and a
ChatGPT workspace in which you can add a custom MCP server. OpenAI's menus change; look for these names.

1. **Create the tunnel.** In the OpenAI platform, open the organization's tunnel settings and create a tunnel.
   Associate it with your ChatGPT workspace (and with any other Platform organization that will call it). Copy its
   tunnel ID: `tunnel_` followed by 32 letters and digits.
2. **Create the runtime key.** On the organization's API keys page, create a **restricted** key with Tunnels
   **Read** and **Use** and nothing else. Never give the client an admin key or one with all permissions: it runs
   all the time. Keep the key for step 4 and paste it nowhere else.
3. **Make the tunnel's harness token**, with the stack running (`./rhythm up`):
   ```sh
   ./rhythm token create openai-tunnel
   ```
   It goes to `deploy/compose/secrets/harness_openai-tunnel_token`, owned by uid 65532, the client's user.
   Nobody needs to open it.
4. **Connect the recipe:**
   ```sh
   ./rhythm connect openai-tunnel
   ```
   It asks for the tunnel ID (written to `adapters.openai-tunnel.tunnelId` in `rhythm.yaml`) and, with hidden
   input, the runtime key (written to `deploy/compose/secrets/adapters/openai-tunnel_key`), adds `openai-tunnel`
   to `COMPOSE_PROFILES` in `.env` and, when the stack is running, starts the container. It refuses until step 3
   is done, and refuses a tunnel ID of the wrong form (a key pasted there, say).
5. **Start and check it.** If the stack was not running, `./rhythm up`. Then:
   ```sh
   ./rhythm doctor
   docker compose -f deploy/compose/compose.yaml logs openai-tunnel | grep -E 'WARN|ERROR'
   docker compose -f deploy/compose/compose.yaml exec openai-tunnel wget -q -O - http://127.0.0.1:8080/health/control-plane
   ```
   At start the log says `mcp session initialized ... server_name=rhythm-engine`: the client reached the engine
   with its token (`Unauthorized` there means the token was revoked, or disabled by a restore: make a new one as
   in [Rotating](#rotating)). The control-plane status says `backoff`, with a `failure_category`, and the log
   `poll failed; backing off`, while the client cannot reach OpenAI or OpenAI refuses it; a `403` usually means
   the key lacks Tunnels Use. The container stays healthy meanwhile: its health check only asks whether the client
   runs (open question Q-121).
6. **Add the connector in ChatGPT.** In ChatGPT's settings for plugins or connectors, add a custom MCP server.
   Under Connection choose **Tunnel** and pick the tunnel (or paste its ID). Leave authentication off: the
   tunnel adds the token, and a header ChatGPT sent would replace it. Read the risk notice, accept it if you
   agree, and create it. Then, in a new chat with it on, ask "List the tools you have from Rhythm of Life": 26
   tools (25 while the local semantic model is off). The rest of [harness/chatgpt/](../../../harness/chatgpt/README.md)
   (the daily insight, dashboards, screens, upgrades) is the same for both routes.

## Rotating

The client reads its key and token when it starts. Stop it before you replace a file: on Docker Desktop a file
deleted under a running container's mount cannot be written again until the container is gone, and a container
that starts without its file gets an empty folder in its place.

- **The runtime key**, before it expires (if you gave it an expiry) and whenever it may have leaked: create a new
  restricted key (Tunnels Read and Use), then
  ```sh
  docker compose -f deploy/compose/compose.yaml rm -sf openai-tunnel
  ./rhythm connect openai-tunnel     # keep the tunnel ID; answer n to keeping the key, and paste the new one
  ```
  `connect` starts the container again when the stack is running (`./rhythm up` otherwise). Then revoke the old
  key in the OpenAI platform.
- **The harness token:**
  ```sh
  docker compose -f deploy/compose/compose.yaml rm -sf openai-tunnel
  ./rhythm token revoke openai-tunnel
  rm -f deploy/compose/secrets/harness_openai-tunnel_token
  ./rhythm token create openai-tunnel
  ./rhythm up
  ```
- **The client image:** OpenAI releases it often. Set `RHYTHM_OPENAI_TUNNEL_IMAGE=ghcr.io/openai/tunnel-client:<version>`
  in `.env`, an exact version from its [releases](https://github.com/openai/tunnel-client/releases) and never
  `latest`, then `./rhythm up` (open question Q-122).

## Undo

1. Turn it off and remove the container:
   ```sh
   ./rhythm disconnect openai-tunnel     # adapters.openai-tunnel.enabled: false, and the profile leaves COMPOSE_PROFILES
   docker compose -f deploy/compose/compose.yaml rm -sf openai-tunnel
   ```
2. Revoke its token and delete its files:
   ```sh
   ./rhythm token revoke openai-tunnel
   rm -f deploy/compose/secrets/harness_openai-tunnel_token deploy/compose/secrets/adapters/openai-tunnel_key
   ```
3. In the OpenAI platform, revoke the runtime key and delete the tunnel. In ChatGPT, remove the connector.
