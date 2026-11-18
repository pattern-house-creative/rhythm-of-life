# Reverse proxy (Caddy example)

Put your own HTTPS reverse proxy in front of the stack: for a home server with a domain name, or a
machine on your network where you want HTTPS and a login.

**Rhythm of Life has no login.** Whoever reaches the app sees everything in the install. Behind a
proxy, the proxy (or the network around it) is the only thing that decides who gets in. Do not put
Rhythm on a public address without a login in front of the app.

| | |
|---|---|
| Exposes | `https://<your site>/` (the app) and `https://<your site>/mcp` (the engine's MCP endpoint), on ports 80 and 443 of the host |
| Protects the app | the proxy's login: basic auth in this example, or single sign-on through `forward_auth` |
| Protects `/mcp` | each AI client's bearer token, checked by the engine, and the engine's `Host` allow-list |
| Never exposed | Postgres, the engine's `/health` and everything else on the engine; the app's and engine's own ports stay on `127.0.0.1` |
| Checked | run end to end on `localhost` with Caddy's local certificate (login, app, same-origin writes, `/mcp` with a token) |

Why `/mcp` has no proxy login: MCP clients authenticate with `Authorization: Bearer <token>`, and basic
auth uses the same header, so the two cannot be stacked on one request. The engine refuses anything
without a valid harness token.

## Files

| File | What |
|---|---|
| `compose.yaml` | an extra compose file adding a `caddy` service on the stack's networks |
| `Caddyfile` | `/mcp` to `engine:8080`; everything else to `app:8080` behind basic auth |

The proxy keeps the browser's `Host` and sets `X-Forwarded-Proto: https`, which the app needs for its
same-origin check on writes (insight feedback, dashboard writes) and the engine needs for its `Host`
allow-list. Any proxy you use instead must do the same.

## Steps

All commands from the repository root unless said otherwise.

1. **Choose the site name.** A domain name whose DNS points at this host (Caddy then gets a
   certificate from Let's Encrypt by itself; ports 80 and 443 must be reachable from the internet for
   that), or `localhost` for a test on this computer.

2. **Allow it in the engine.** In `deploy/compose/rhythm.yaml`, add the name to `engine.allowedHosts`
   and set the MCP address your clients will use:
   ```yaml
   engine:
     allowedHosts: [localhost, 127.0.0.1, "[::1]", engine, rhythm.example.invalid]
     publicMcpUrl: https://rhythm.example.invalid/mcp
   ```
   Restart the engine after changing it.

3. **Make the login file.** One line per person, `<name> <bcrypt hash>`. The hash comes from Caddy,
   which asks for the password without echoing it:
   ```sh
   docker run --rm -it caddy:2.10.2-alpine@sha256:4c6e91c6ed0e2fa03efd5b44747b625fec79bc9cd06ac5235a779726618e530d caddy hash-password
   ```
   Write the lines to `deploy/compose/secrets/proxy_users`, for example
   `alex $2a$14$...` and `sam $2a$14$...`. It is a secret file like the others: never committed.

4. **Set the compose values** in `deploy/compose/.env`:
   ```sh
   RHYTHM_SITE=rhythm.example.invalid
   # optional: RHYTHM_PROXY_BIND (default 0.0.0.0), RHYTHM_PROXY_HTTP_PORT (80), RHYTHM_PROXY_HTTPS_PORT (443)
   ```
   Leave `RHYTHM_BIND` at its default `127.0.0.1`: only the proxy should be reachable from other
   computers.

5. **Start it** with the extra file (or put both files in `COMPOSE_FILE` in `.env` so plain
   `docker compose` commands include it):
   ```sh
   docker compose -f deploy/compose/compose.yaml -f deploy/exposure/reverse-proxy/compose.yaml up -d
   ```

6. **Check it.**
   ```sh
   curl -s -o /dev/null -w '%{http_code}\n' https://rhythm.example.invalid/            # 401: login needed
   curl -s -o /dev/null -w '%{http_code}\n' -u alex https://rhythm.example.invalid/    # 200 after the password
   curl -s -o /dev/null -w '%{http_code}\n' -X POST https://rhythm.example.invalid/mcp # 401 from the engine: token needed
   ```
   Then connect an AI client with its token to `https://rhythm.example.invalid/mcp`
   ([harness/generic-mcp/](../../../harness/generic-mcp/README.md)).

With `RHYTHM_SITE=localhost`, Caddy makes its own certificate authority. Browsers warn until you trust
its root certificate on each device:
```sh
docker compose -f deploy/compose/compose.yaml -f deploy/exposure/reverse-proxy/compose.yaml \
  exec caddy cat /data/caddy/pki/authorities/local/root.crt > rhythm-local-ca.crt
```

## Single sign-on instead of basic auth

Basic auth is simple and works for a household, but every device asks once and keeps the password.
If you already run an identity proxy (Authelia, Authentik, oauth2-proxy), replace the `basic_auth`
block with Caddy's `forward_auth` pointing at it, and keep `/mcp` outside it as above.

## Using a proxy you already have

Point it at the ports the stack already publishes on the host, `127.0.0.1:8080` (app) and
`127.0.0.1:8181` (engine), and keep these rules:

- `/mcp` exactly goes to the engine, with no proxy login and the `Authorization` header passed through;
- everything else goes to the app, behind your login;
- the original `Host` is kept and `X-Forwarded-Proto` is set; the site name is in `engine.allowedHosts`;
- nothing else on the engine is routed (no `/health`, no other paths), and Postgres is never routed.

## Undo

```sh
docker compose -f deploy/compose/compose.yaml -f deploy/exposure/reverse-proxy/compose.yaml rm -sf caddy
docker volume rm rhythm_caddy-data rhythm_caddy-config   # its certificates and state
```

Then remove the site name from `engine.allowedHosts` (and `publicMcpUrl`), delete
`secrets/proxy_users`, drop the extra file from `COMPOSE_FILE`, and point DNS elsewhere. Revoke any
token you gave a client that used the proxy address if it should no longer connect.
