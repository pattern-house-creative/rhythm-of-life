# harness

Your AI stays outside the stack. It connects to the engine's one MCP endpoint (`/mcp`) with a token
of its own, made with `rhythm token create <name>`, and sees the harness tools in
`contracts/README.md` section 2.1.

- [`chatgpt/`](chatgpt/README.md): ChatGPT's two routes (the OpenAI tunnel or Cloudflare), its connector
  and the daily insight as a scheduled task ([`chatgpt/daily-insight.md`](chatgpt/daily-insight.md), a
  template for any assistant with a scheduler).
- [`generic-mcp/`](generic-mcp/README.md): Claude Code, Claude Desktop and other MCP clients, the MCP
  Inspector, and a troubleshooting table.
- [`hooks/`](hooks/README.md): session hooks for coding agents (Claude Code, Codex, Cursor, Gemini CLI), off until you
  install them: one finished message of a session, through a content gate and redaction, saved as a draft through the
  capture endpoint.
- [`memory.md`](memory.md): Rhythm's own memories for an AI client, with `memory.tools: on`: the capsule
  first, when to recall, append over a near copy, and the instructions to paste into a client.

The dashboard design guide is part of the engine, as the MCP resource and prompt
`rhythm://guides/dashboards`; both pages say how to give it to a client that cannot read resources.

Reaching the engine from another computer or from a hosted AI is an exposure recipe:
[`deploy/exposure/`](../deploy/exposure/README.md). What a token can do, and what never to expose, is in
[`docs/security.md`](../docs/security.md).
