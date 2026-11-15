# tailscale

An Exposure adapter: a Tailscale container (compose profile `tailscale`) joins your tailnet as its own
machine and serves the app at `https://rhythm.<tailnet>.ts.net/` and the engine's `/mcp` beside it,
with the tailnet's own HTTPS certificate. Nothing is published on the host or the internet.

- **Set up outside Rhythm first:** a Tailscale account and tailnet with MagicDNS and HTTPS
  certificates on.
- **Settings:** none in `adapters.tailscale` beyond `enabled`; the machine name is
  `RHYTHM_TAILSCALE_HOSTNAME` in `.env`, and its full MagicDNS name goes into `engine.allowedHosts`.
- **Secrets:** none. The first start prints a login link; the machine key stays in a volume.
- **Limits:** only devices on the tailnet reach it; hosted AI services cannot (use `cloudflare` or, for ChatGPT, `openai-tunnel` for
  them).

Steps, checks and how to undo it: [deploy/exposure/tailscale/](../../deploy/exposure/tailscale/README.md).
