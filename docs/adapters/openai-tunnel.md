# openai-tunnel

An Exposure adapter: OpenAI's tunnel client (`ghcr.io/openai/tunnel-client`, compose profile `openai-tunnel`)
dials out to OpenAI and carries ChatGPT's MCP calls to the engine's `/mcp` on the egress network, adding the
tunnel's own harness token. Nothing is published on the host and nothing on the internet can connect in. A route
for ChatGPT (and Codex or the Responses API) without a domain; [harness/chatgpt/](../../harness/chatgpt/README.md)
compares it with Cloudflare.

- **Set up outside Rhythm first:** a tunnel in an OpenAI Platform organization, associated with your ChatGPT
  workspace, and a restricted runtime API key with Tunnels Read and Use.
- **Settings:** `adapters.openai-tunnel.tunnelId` (`tunnel_` and 32 letters and digits). Nothing changes in
  `engine.allowedHosts`: the client calls the engine by its internal name.
- **Secrets:** `secrets/adapters/openai-tunnel_key` (the runtime key) and `secrets/harness_openai-tunnel_token`
  (`./rhythm token create openai-tunnel`, before `connect`), both owned by uid 65532, the client's user.
- **Limits:** whoever OpenAI lets use the tunnel reaches the harness tools with its token; the client is pre-1.0 and
  pinned to an exact version; the health check says only that the client runs, not that OpenAI accepts it.

Steps, checks, rotation and how to undo it: [deploy/exposure/openai-tunnel/](../../deploy/exposure/openai-tunnel/README.md).
