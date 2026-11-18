# Cloudflare Tunnel and Access

Reach the install from the internet without opening a port: a Cloudflare Tunnel (`cloudflared`, an
outbound-only connection from your server) carries two public host names to the stack, and
Cloudflare Access puts a login in front of the app. This is also the recipe that lets ChatGPT and
other hosted AI services reach `/mcp` (ChatGPT can also use the [OpenAI tunnel](../openai-tunnel/README.md)).

| | |
|---|---|
| Exposes | `https://rhythm.example.invalid/` (the app) and `https://rhythm-mcp.example.invalid/mcp` (the engine's MCP endpoint, and nothing else on that host name), on the public internet |
| Protects the app | Cloudflare Access: only the people you list can sign in (one-time PIN by email, or your identity provider) |
| Protects `/mcp` | each AI client's bearer token, checked by the engine, and the engine's `Host` allow-list; optionally an Access service token for clients that can send extra headers |
| Never exposed | Postgres, the engine's `/health` and its other paths, the host's own ports (they stay on `127.0.0.1`) |
| Checked | One TrueNAS installation serves MCP through Cloudflare to ChatGPT and Claude, with the app reached separately over a tailnet. The two-host example and optional Access policy are checked by Compose configuration; that installation uses the MCP route only. |

Use your own names; `rhythm` and `rhythm-mcp` are examples. Keep both one level below your domain
(`rhythm-mcp.example.invalid`, not `mcp.rhythm.example.invalid`): Cloudflare's free certificate
covers only that level.

## Why two host names

The app needs a login. MCP clients cannot do Cloudflare's login: ChatGPT sends only its bearer token,
so an Access login page in front of `/mcp` would stop it. A separate host name that reaches only the
engine's `/mcp` lets you bypass Access there and nowhere else. On that host name, the engine is the
gate: it answers `401` to anything without a token it knows, `403` to a `Host` it does not know or a
request with an `Origin` header (a browser), and offers nothing but `/mcp`. The host role filter below accepts harness tokens on the
public MCP name; the app and plugin keep using the engine's internal name. Keep the original public
Host on the tunnel's requests to the engine
([docs/security.md](../../../docs/security.md#tokens-and-roles)).

## Steps

You need a domain whose DNS is on Cloudflare, and a Cloudflare Zero Trust organization (the free plan
is enough for a household).

1. **Create the tunnel.** In Zero Trust, under Networks, Tunnels: create a tunnel of type cloudflared,
   named for example `rhythm`. Cloudflare shows an install command containing a long token. Copy only
   the token (the part after `--token`). Create the folder with `mkdir -p deploy/compose/secrets/adapters`,
   open `deploy/compose/secrets/adapters/cloudflare_tunnel_token` in an editor and paste the token on
   one line. Never put it on a command line, in `.env` or in `rhythm.yaml`. On Linux, `cloudflared` runs as uid 65532 and must be able to read the file (file permissions:
   [docs/security.md](../../../docs/security.md#secret-files)).

2. **Add the two routes** (public host names) to the tunnel:

   | Host name | Path | Service |
   |---|---|---|
   | `rhythm.example.invalid` | (empty) | `HTTP`, `app:8080` |
   | `rhythm-mcp.example.invalid` | `^/mcp$` | `HTTP`, `engine:8080` |

   Add nothing else for the MCP host name: any other path there gets the tunnel's catch-all `404`.

   **MCP-only installation:** If people reach the app another way, omit the app host and its
   Access application. Set `RHYTHM_CLOUDFLARE_NETWORK=egress` in `deploy/compose/.env` and
   recreate cloudflared when applying the setting. The tunnel then shares a network with the
   engine and has no network in common with the app or Postgres. The two-host default uses `edge`.

3. **Put Access in front of the app.** In Zero Trust, under Access, Applications: add a self-hosted
   application for `rhythm.example.invalid` with one Allow policy that includes exactly the household's
   email addresses (or your identity provider's group). Pick a long session (for example a month) so
   the installed app on a phone does not ask every day.

4. **Decide how `/mcp` is let through.** Add a second self-hosted application for
   `rhythm-mcp.example.invalid` with one of:
   - a **Bypass** policy, including Everyone: needed for ChatGPT and any client that cannot send
     custom headers. Access adds nothing here; the bearer token is the gate.
   - a **Service Auth** policy with an Access service token: only for clients that can send two more
     headers, `CF-Access-Client-Id` and `CF-Access-Client-Secret`, with every request (Claude Code's
     `--header`, the hosted front end in [deploy/vercel/](../../vercel/README.md)). ChatGPT cannot use
     this.
   If Bot Fight Mode or a WAF challenge is on for the zone, skip it for the MCP host name, or automated
   clients get a challenge page instead of JSON. **Do add a rate-limiting rule for that host name**:
   with Access bypassed, a token is the only gate there, and the engine checks every attempt against
   its database. See [Rate limiting the MCP route](#rate-limiting-the-mcp-route).

5. **Tell the engine its public name.** In `deploy/compose/rhythm.yaml`:
   ```yaml
   engine:
     allowedHosts: [localhost, 127.0.0.1, "[::1]", engine, rhythm-mcp.example.invalid]
     hostRoles: [{ host: rhythm-mcp.example.invalid, roles: [harness] }]
     publicMcpUrl: https://rhythm-mcp.example.invalid/mcp
     writeLimit: on
   adapters:
     cloudflare:
       enabled: true
       hostname: rhythm.example.invalid
   ```
   The tunnel passes the public host name through as `Host`; the engine refuses names not listed.
   `writeLimit: on` refuses a token's writes past 300 in ten minutes, so a client that runs away, or a
   stolen token, is stopped ([docs/security.md](../../../docs/security.md#the-write-limit)). It is off by
   default; turn it on for any install with a public address.
   `./rhythm init --exposure cloudflare --host rhythm.example.invalid` writes these lines for a new
   install, all but `enabled: true`: it asks for the MCP host name too (`--mcp-host`, by default the
   app's with `-mcp`).

6. **Start the tunnel.** In `deploy/compose/.env`, add `cloudflare` to `COMPOSE_PROFILES`, and leave
   `RHYTHM_BIND` at `127.0.0.1`. Then:
   ```sh
   docker compose -f deploy/compose/compose.yaml up -d
   docker compose -f deploy/compose/compose.yaml up -d --force-recreate --no-deps engine     # reads the new allowedHosts
   docker compose -f deploy/compose/compose.yaml logs cloudflared   # "Registered tunnel connection"
   ```

7. **Check it** from a computer outside your network:
   ```sh
   curl -s -o /dev/null -w '%{http_code}\n' https://rhythm.example.invalid/              # 302: Access login
   curl -s -o /dev/null -w '%{http_code}\n' -X POST https://rhythm-mcp.example.invalid/mcp  # 401 from the engine
   curl -s -o /dev/null -w '%{http_code}\n' https://rhythm-mcp.example.invalid/health    # 404: not routed
   ```
   The second must be the engine's `401` JSON, not a Cloudflare login page. Then connect ChatGPT
   ([harness/chatgpt/](../../../harness/chatgpt/README.md)) or another client with its own token.

## Rate limiting the MCP route

In the zone's Security Rules, create a **rate limiting rule**. Use the actual MCP hostname
and exact path. The expression for the example host is:

```text
(http.host eq "rhythm-mcp.example.invalid" and http.request.uri.path eq "/mcp")
```

Host matching is available on Pro and higher plans. A Free rule can match the path but cannot
match the host. On a shared zone, a path-only rule covers every hostname's `/mcp`; check the
zone's other routes and obtain the operator's agreement before considering that wider scope.
If the scope must be exact, leave the rule pending until a host-scoped option is available.
See Cloudflare's [plan limits](https://developers.cloudflare.com/waf/rate-limiting-rules/#availability).

Count by source IP. Look at the busiest normal client session in the chosen period, including
tool discovery and parallel reads; several hosted sessions can share an egress address. Start above
that peak with headroom for retries. Use a short supported period and timeout, with **Block** as
the action so an automated client receives HTTP 429. Record the match expression, threshold,
period and timeout before applying. [Threshold selection](https://developers.cloudflare.com/waf/rate-limiting-rules/find-rate-limit/)
and [dashboard settings](https://developers.cloudflare.com/waf/rate-limiting-rules/create-zone-dashboard/).

After enabling, open fresh ChatGPT and Claude sessions and run a harmless read from each.
Check Security Events for the rule ID and any unexpected matches. Confirm the app host and the
MCP host's OAuth discovery routes still work. If normal traffic is blocked, disable this rule,
repeat both reads and review the threshold. Avoid load-testing a household endpoint to verify
the limit; exercise an above-limit case on a separate test host.

## How ChatGPT reaches `/mcp`

ChatGPT's servers call `https://rhythm-mcp.example.invalid/mcp` with `Authorization: Bearer <its
token>`. Cloudflare's edge matches the host name, Access lets it through (Bypass), the tunnel carries
it to `engine:8080` on the stack's edge network, and the engine checks the `Host`, the absence of
`Origin` and the token, then serves the harness tools. Revoking the token (`token revoke chatgpt`)
cuts it off at once, whatever Cloudflare does.

## Undo

1. Remove `cloudflare` from `COMPOSE_PROFILES`, then stop and remove the container:
   ```sh
   docker compose -f deploy/compose/compose.yaml rm -sf cloudflared
   ```
2. In Zero Trust, delete the tunnel and both Access applications; delete the two DNS records for the
   host names if they remain. Delete any Access service token you made for it.
3. Delete `deploy/compose/secrets/adapters/cloudflare_tunnel_token`.
4. Remove the MCP host name from `engine.allowedHosts`, reset `publicMcpUrl`, set
   `adapters.cloudflare.enabled: false`, and restart the engine.
5. Revoke the tokens of clients that used the public address (`token revoke <name>`), and remove the
   connector in ChatGPT.
