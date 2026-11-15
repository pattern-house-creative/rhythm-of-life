# deploy/exposure

How people and AI clients reach an install. Rhythm of Life has no built-in login, so the exposure you
choose is the gate: whoever reaches the app sees everything in the install. The model, what is
exposed by default and what never to expose are in [docs/security.md](../../docs/security.md).

| Recipe | Who can reach the app | Who can reach `/mcp` | Needs |
|---|---|---|---|
| this computer only (default) | programs on this computer (`127.0.0.1:8080`) | clients on this computer with a token (`127.0.0.1:8181/mcp`) | nothing |
| home network | anyone on your network (`RHYTHM_BIND=0.0.0.0`) | anyone on your network with a token | a network you trust; no HTTPS |
| [tailscale/](tailscale/README.md) | devices on your tailnet, as its access rules allow | the same, with a token | a Tailscale account |
| [cloudflare/](cloudflare/README.md) | the people Cloudflare Access lets sign in | anyone on the internet with a token (one route for ChatGPT) | a domain on Cloudflare |
| [openai-tunnel/](openai-tunnel/README.md) | no one: the app is not carried | ChatGPT, for whoever OpenAI lets use the tunnel; the tunnel adds its own token | an OpenAI Platform organization with tunnels |
| [reverse-proxy/](reverse-proxy/README.md) | whoever passes your proxy's login | anyone who reaches the proxy, with a token | a domain or a local name, ports 80 and 443 |

You can combine them: Tailscale for the household's devices plus the OpenAI tunnel or Cloudflare for a
hosted AI, for example ([harness/chatgpt/](../../harness/chatgpt/README.md) compares the two routes for
ChatGPT). The home network option is just a setting; the others have a folder here.

## What every recipe keeps

- **Postgres is never reachable**: it lives only on the compose `internal` network, which cannot
  publish ports or reach the internet.
- **The app and engine ports stay on `127.0.0.1`** (`RHYTHM_BIND` default). Recipes reach the
  services over the compose networks (`app:8080`, `engine:8080`) or the loopback ports, never by
  opening them wider.
- **Only `/mcp` of the engine is routed**, with no proxy login on it: MCP clients send
  `Authorization: Bearer <token>` and the engine checks it. The engine's `/health` and any other path
  stay inside. A hosted front end that shows documents' original files also needs `/app/documents/`, which answers
  the app token alone ([../vercel/](../vercel/README.md)).
- **The public name is in `engine.allowedHosts`**: the engine refuses any `Host` it does not know (and
  any request with an `Origin` header), so a name you did not plan for cannot reach it. The OpenAI
  tunnel has no public name: its client calls the engine as `engine:8080`.
- **The proxy keeps `Host` and sets `X-Forwarded-Proto`**, so the app's same-origin check on writes
  works.

## Files and profiles

| Folder | Container | How it is turned on |
|---|---|---|
| `tailscale/` | `tailscale` (`tailscale/tailscale`) | profile `tailscale` (`COMPOSE_PROFILES` in `.env`); the file is included from `deploy/compose/compose.yaml` |
| `cloudflare/` | `cloudflared` (`cloudflare/cloudflared`) | profile `cloudflare`, included the same way |
| `openai-tunnel/` | `openai-tunnel` (`ghcr.io/openai/tunnel-client`) | profile `openai-tunnel`, included the same way |
| `reverse-proxy/` | `caddy`, an example | an extra file: `-f deploy/exposure/reverse-proxy/compose.yaml` |

Tailscale, Cloudflare and the OpenAI tunnel are Exposure adapters (`contracts/README.md` section 3):
`adapters.<name>` in `rhythm.yaml` records that they are on (and holds the OpenAI tunnel's ID), and their
containers follow the profile convention. The Caddy file is an example of "your own proxy", so it is not an
adapter.
