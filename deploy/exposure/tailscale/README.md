# Tailscale

Reach the app and the MCP endpoint from your own devices (phones, laptops) wherever they are, over
your Tailscale network (tailnet), with HTTPS on a MagicDNS name. Nothing is opened on the internet or
published on the host.

| | |
|---|---|
| Exposes | `https://rhythm.<tailnet>.ts.net/` (the app) and `https://rhythm.<tailnet>.ts.net/mcp` (the engine's MCP endpoint), only inside your tailnet |
| Protects the app | membership of your tailnet, and its access rules (ACLs or grants), which you control in Tailscale's admin console |
| Protects `/mcp` | the tailnet as above, plus each AI client's bearer token and the engine's `Host` allow-list |
| Never exposed | Postgres, the engine's `/health` and its other paths; the host's own ports stay on `127.0.0.1`; Funnel (public internet) stays off |
| Checked | by `docker compose config` only: signing in needs a Tailscale account, which these docs were not tested with |

Hosted AI services (ChatGPT, connectors added by URL) cannot reach a tailnet. For them, use the
[Cloudflare recipe](../cloudflare/README.md) alongside this one.

## How it works

`compose.yaml` here adds a `tailscale` container (profile `tailscale`) on the stack's `edge` network. It joins your tailnet as its own machine, named `rhythm` by default, and
`tailscale serve` answers HTTPS on that name with the tailnet's certificate:

- `/` goes to `http://app:8080`;
- `/mcp` goes to `http://engine:8080/mcp`.

Tailscale keeps the browser's `Host` and sets `X-Forwarded-Proto: https`, so the app's same-origin
check works; the engine checks that `Host` against `engine.allowedHosts`. It runs in userspace mode:
no `/dev/net/tun`, no added capabilities, and its state (the machine key) in the volume
`tailscale-state`.

## Steps

1. **Turn on HTTPS for the tailnet.** In Tailscale's admin console, under DNS: MagicDNS on, and HTTPS
   certificates on. Note your tailnet's DNS name (`<tailnet>.ts.net`).

2. **Allow the name in the engine.** In `deploy/compose/rhythm.yaml`:
   ```yaml
   engine:
     allowedHosts: [localhost, 127.0.0.1, "[::1]", engine, rhythm.<tailnet>.ts.net]
     publicMcpUrl: https://rhythm.<tailnet>.ts.net/mcp
   adapters:
     tailscale:
       enabled: true
   ```
   (A different machine name: set `RHYTHM_TAILSCALE_HOSTNAME` in `.env` and use it here.)

3. **Turn on the profile.** In `deploy/compose/.env`, add `tailscale` to `COMPOSE_PROFILES`
   (comma-separated with any others). Leave `RHYTHM_BIND` at `127.0.0.1`.

4. **Start it and sign in once.**
   ```sh
   docker compose -f deploy/compose/compose.yaml up -d
   docker compose -f deploy/compose/compose.yaml logs -f tailscale
   ```
   The log prints a login link. Open it, sign in to your tailnet and approve the machine. The key stays
   in the `tailscale-state` volume, so later starts need nothing. Restart the engine so it reads the new
   `allowedHosts`: `docker compose -f deploy/compose/compose.yaml up -d --force-recreate --no-deps engine`.
   For an unattended first start, use an auth key from the admin console instead: write it to
   `deploy/compose/secrets/adapters/tailscale_authkey`, add it to the service as a compose secret and set
   `TS_AUTHKEY: file:/run/secrets/tailscale_authkey` (the `tailscale up` command reads the file).

5. **Limit who can reach it** (recommended). In the tailnet's access rules, allow only the household's
   users or devices to reach the `rhythm` machine on port 443. Tag the machine if you manage rules by
   tag.

6. **Check it** from another device on the tailnet:
   ```sh
   curl -s -o /dev/null -w '%{http_code}\n' https://rhythm.<tailnet>.ts.net/api/health   # 200
   curl -s -o /dev/null -w '%{http_code}\n' -X POST https://rhythm.<tailnet>.ts.net/mcp  # 401: token needed
   ```
   Open `https://rhythm.<tailnet>.ts.net/` in a browser and install the app from there. Connect AI
   clients on tailnet devices to `https://rhythm.<tailnet>.ts.net/mcp` with their own tokens
   ([harness/generic-mcp/](../../../harness/generic-mcp/README.md)).

If the log says the serve config was not applied because HTTPS is not enabled, finish step 1 and
restart the container.

## Tailscale on the host instead

If Tailscale already runs on the server itself, skip the container and serve the published ports:

```sh
sudo tailscale serve --bg --https=443 http://127.0.0.1:8080
sudo tailscale serve --bg --https=443 --set-path /mcp http://127.0.0.1:8181/mcp
```

and put the host's MagicDNS name in `engine.allowedHosts`. `tailscale serve status` shows what is
served; `tailscale serve reset` undoes it.

## Undo

1. Remove `tailscale` from `COMPOSE_PROFILES`, then stop and remove the container and its state:
   ```sh
   docker compose -f deploy/compose/compose.yaml rm -sf tailscale
   docker volume rm rhythm_tailscale-state
   ```
2. In the admin console, remove the `rhythm` machine (this also revokes its key).
3. Remove the name from `engine.allowedHosts`, reset `publicMcpUrl`, set
   `adapters.tailscale.enabled: false`, restart the engine.
4. Revoke tokens of clients that only used the tailnet address, if they should no longer connect.
