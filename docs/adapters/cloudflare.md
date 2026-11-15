# cloudflare

An Exposure adapter: a Cloudflare Tunnel (`cloudflared`, compose profile `cloudflare`) carries two
public host names to the stack. Cloudflare Access is the login in front of the app; the second host
name reaches only the engine's `/mcp`, where each AI client's bearer token is the gate. This is the
route ChatGPT and other hosted AI services use; ChatGPT can also use [openai-tunnel](openai-tunnel.md).

- **Set up outside Rhythm first:** a domain whose DNS is on Cloudflare, a Cloudflare Zero Trust
  organization, a tunnel (its token), two routes and two Access applications.
- **Settings:** `adapters.cloudflare.hostname` and `mcpHostname`; the MCP host name also goes into
  `engine.allowedHosts`, and its URL into `engine.publicMcpUrl`.
- **Secret:** `secrets/adapters/cloudflare_tunnel_token`.
- **Limits:** needs cloudflared 2025.4.0 or later (for the token file); ChatGPT cannot use an Access
  service token, so its host name bypasses Access.

Steps, checks and how to undo it: [deploy/exposure/cloudflare/](../../deploy/exposure/cloudflare/README.md).
