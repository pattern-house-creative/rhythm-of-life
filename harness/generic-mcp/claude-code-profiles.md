# Two Claude Code accounts on one computer

For someone with two Claude accounts, such as a personal one and a work one, who wants each to run
Claude Code with its own settings and its own Rhythm token. It uses the same MCP endpoint as the
[one-account setup](README.md#2-claude-code); nothing new is installed.

## Decide first what each account may read

A second token is not a second view of your data.

| What you separate | What that gives you | What it does not give you |
|---|---|---|
| A Claude settings folder per account (`CLAUDE_CONFIG_DIR`) | separate settings, plugins and session history; both accounts can run side by side | a sandbox, or any change to what Rhythm returns |
| A Rhythm token per account | each can be revoked on its own | a read-only or narrower view: every harness token reads the whole install and can write |
| `memory.scopes: on`, with a token made for a person (`--person`) | that person's private memories and what Family shares | a barrier between two tokens of the same person |
| A separate install holding only what that account may see | a different set of data and tokens | permission to copy household data into it |

With `memory.scopes` off, which is the default, every harness token reads everything in the install,
even one made with `--person` ([security](../../docs/security.md#per-person-memory-scopes)). Whatever an
account reads is then handled under that account's own terms and its organization's settings.

So if a work account must not receive household data, do not give it a token for the household's
install. Connect it to a separate install, or to none.

## Set it up

1. Make a token for each account, on the install it should read:

   ```sh
   ./rhythm token create claude-personal
   ./rhythm token create claude-work
   ```

   Each is written to `deploy/compose/secrets/harness_<name>_token`.

2. Make a working folder for each account, outside any project, and put the `.mcp.json` from the
   [one-account setup](README.md#2-claude-code) in each, with your install's MCP address. That file
   names the variable `RHYTHM_MCP_TOKEN` and holds no token.

3. Run each account in a terminal of its own. In sh:

   ```sh
   export CLAUDE_CONFIG_DIR="$HOME/.claude-personal"    # an absolute path, different for each account
   cd ~/rhythm-claude/personal
   claude auth login                                     # the first time
   claude auth status                                    # which account, and which settings folder
   RHYTHM_MCP_TOKEN="$(cat /path/to/harness_claude-personal_token)" \
     claude --strict-mcp-config --mcp-config .mcp.json
   ```

   In Windows PowerShell:

   ```powershell
   $env:CLAUDE_CONFIG_DIR = Join-Path $env:LOCALAPPDATA 'claude-personal'
   Set-Location "$env:USERPROFILE\rhythm-claude\personal"
   claude auth login
   claude auth status
   $env:RHYTHM_MCP_TOKEN = (Get-Content -LiteralPath 'C:\path\to\harness_claude-personal_token' -Raw).Trim()
   try { claude --strict-mcp-config --mcp-config .\.mcp.json }
   finally { Remove-Item Env:RHYTHM_MCP_TOKEN }
   ```

   Do the same in a second terminal with the other account's folder, settings folder and token.

Inside each, `/mcp` shows the `rhythm` server and how many tools it found.

- **`--strict-mcp-config`** loads only the servers in that file for this session. Servers you set up
  elsewhere are left as they are; they are just not loaded here.
- **An organization's rules still apply.** A work account's administrator can allow or refuse MCP
  servers. If the server is refused, ask them; do not work around it.
- **The token is in the terminal's environment** while Claude Code runs, where other programs of your
  user can read it. It is in no settings file. Close the terminal when you are done.
- **From another computer,** use an HTTPS address that device can reach, such as the
  [Tailscale recipe](../../deploy/exposure/tailscale/README.md)'s `https://rhythm.<tailnet>.ts.net/mcp`.

## Check it, and undo it

Revoke one of the two tokens and ask each account for something. The revoked one's next call is
refused with `401`; the other still works.

```sh
./rhythm token revoke claude-work
```

Closing Claude Code or deleting a settings folder does not revoke a token.

## Claude on the web, desktop or mobile

A custom connector there is a different thing: it connects from Anthropic's servers, so it needs a
public HTTPS address and an account that offers request headers. See [claude-hosted.md](claude-hosted.md).

## How far this is tested

The commands and flags were checked against Claude Code's documentation on 15 November 2026:
[MCP servers](https://code.claude.com/docs/en/mcp), the
[command line](https://code.claude.com/docs/en/cli-reference) and
[`CLAUDE_CONFIG_DIR`](https://code.claude.com/docs/en/env-vars). Running two signed-in accounts side by
side against an install has not been tried.
