# harness/hooks

Session hooks for coding agents: at the end of a session in a folder you named, one finished message of the agent's, if it
says something worth keeping, becomes a draft memory for you to review. Nothing here runs by itself. A hook is a file you
install on purpose, once for each agent, and it stays off until you also write a settings file saying so.

Its one call is `POST /capture` with a capture token ([contracts/README.md](../../contracts/README.md) section 6). That token
reads nothing, so a hook gives an agent's machine no way to read your memories. To bring memories into a session, connect
the agent over MCP instead ([generic-mcp/](../generic-mcp/README.md), [memory.md](../memory.md)); hooks only write.

## What happens

| When | What the hook does | Network |
|---|---|---|
| The agent finishes a message | Reads the message the event hands it, runs the [gate](#the-gate) on it and [redacts](#redaction) it. If it passes and is at least as good as what this session holds, it replaces that, in a private file on this computer | none |
| The session ends | Sends the one message waiting for this session as a draft, then deletes the file | one `POST /capture` |

What it never does:

- **It never opens a transcript.** Every agent here gives a hook a transcript's path, and these hooks do not use it: they read
  only the field the agent documents as holding the message. So tool output, file contents and what you typed stay out,
  and the message is the agent's own summary of what happened. Claude Code's reference warns that the transcript may not
  yet hold the final message at that moment, and Codex's says its format is no stable interface.
- **It never keeps a whole session.** One message per session, cut to 2000 characters by default, fenced code left out.
- **It never steers the agent.** It writes `{}` to stdout and exits 0 whatever happened, so it cannot block, retry or
  add to a session, and a failure costs you the memory rather than the conversation.
- **It never installs itself** and never edits an agent's settings. You paste the entry below, or you don't.

What arrives is an ordinary capture: a draft, never canonical before you confirm it, labelled with the agent's own source
name (`claude-code`, `codex-session`, `cursor-session`, `gemini-session`), which never replaces a memory from another source
and tagged `session`, held for your review if it reads like instructions to an AI or holds hidden characters
([docs/security.md](../../docs/security.md#capturing-from-outside)). With `memory.scopes` off everyone in the household reads
it, as with every memory; with it on, a token bound to a person (`--person`) writes into that person's private scope.

## The agents

Each event and field below was read from that agent's own published reference in November 2026. A hook is added here only
that way, so check the reference again if a newer version of an agent stops calling this one.

| Agent | A finished message | Its field | A session's end | Settings file | Reference |
|---|---|---|---|---|---|
| Claude Code | `Stop` | `last_assistant_message` | `SessionEnd` | `~/.claude/settings.json`, or a project's `.claude/settings.local.json` | [hooks](https://code.claude.com/docs/en/hooks) |
| Codex | `Stop` | `last_assistant_message` | `SessionEnd` | `~/.codex/hooks.json`, or a project's `.codex/hooks.json` | [hooks](https://developers.openai.com/codex/hooks) |
| Cursor | `afterAgentResponse` | `text` | `sessionEnd` | `~/.cursor/hooks.json`, or a project's `.cursor/hooks.json` | [hooks](https://cursor.com/docs/agent/hooks) |
| Gemini CLI | `AfterAgent` | `prompt_response` | `SessionEnd` | `~/.gemini/settings.json`, or a project's `.gemini/settings.json` | [hooks reference](https://geminicli.com/docs/hooks/reference/) |

Two wait, and no hook is built for them:

- **VS Code waits.** Its agent hooks are published and marked Preview. Its `sessionEnd` input holds no message, and its
  `agentStop` input names a transcript but holds no message either ([GitHub's reference](https://docs.github.com/en/copilot/reference/hooks-reference)),
  so nothing it documents hands over a finished message without opening that transcript. It can be added when one does.
- **Antigravity CLI waits.** Gemini CLI's pages say Antigravity CLI replaced it for unpaid and Google One users on 18 June
  2026, and Google says hooks carry over without one-to-one parity. Its own hook events could not be confirmed from a
  published reference. The Gemini CLI entry below is for those who still run Gemini CLI.

## Set it up

Node 22 or later, on the `PATH` your agent's hooks see.

**1. Turn the endpoint on and make a token.** In `rhythm.yaml`, `memory.tools: on` and `memory.capture: on`
([docs/configuration.md](../../docs/configuration.md)), then restart the engine, and make a connection:

```sh
./rhythm capture connect my-laptop     # add --person alex to write into a person's scope once scopes are on
```

The token goes to `deploy/compose/secrets/capture_my-laptop_token`. On another computer, copy that file somewhere private
(mode 0600). `/capture` is on the engine's one port and no exposure recipe routes it, so it has to be reachable from there
already.

**2. Write the settings file**, `~/.config/rhythm/hooks.json` ([example](examples/hooks.config.example.json);
`RHYTHM_HOOK_CONFIG` names another path):

```json
{
  "capture": "on",
  "engineUrl": "http://127.0.0.1:8181",
  "tokenFile": "/path/to/rhythm/deploy/compose/secrets/capture_my-laptop_token",
  "folders": ["/path/to/code/garden-app"],
  "project": "garden-app"
}
```

| Setting | Means |
|---|---|
| `capture` | `"on"`, exactly, or the hook does nothing at all |
| `folders` | Full paths. Only a session in or under one of these is ever looked at, and a Cursor workspace only if every root is. An agent can change directory during a session: Claude Code and Gemini CLI name the folder a session started in (`CLAUDE_PROJECT_DIR`, `GEMINI_PROJECT_DIR`), so that folder and the one it has moved to must both be listed, which leaves out a worktree outside them. Codex names only its working folder, so that alone is checked. `["*"]` says every folder, on purpose. Leave out anything you would not want summarised in a memory |
| `tokenFile` | Full path to the capture token's file. The token is never in the settings file, an agent's settings or a command line |
| `engineUrl` | The engine; the hook always posts to `/capture` on it. `http://` only to this computer, unless `allowPlainHttp` is `true`, since it carries the token |
| `project` | A project's slug, or `"auto"` for the name of the listed folder the session is under (under `["*"]`, the session folder's own name). The engine makes a project that is new. Left out, no project and no folder name is sent |
| `minChars`, `minSignals`, `maxChars` | The [gate](#the-gate)'s numbers: 200, 2 and 2000 by default. `maxChars` goes up to 8000 |

**3. Check it.** This says what it would do and whether the engine agrees, with a call that saves nothing:

```sh
node harness/hooks/rhythm-hook.mjs check
```

**4. See what it would send**, on any text of your own. No network:

```sh
node harness/hooks/rhythm-hook.mjs preview < some-message.txt
```

**5. Register it with the agent**, by hand, in its settings file from the table above, with the full path to
`rhythm-hook.mjs`. Each entry names the two events and nothing else:
[Claude Code](examples/claude-code.settings.example.json), [Codex](examples/codex.hooks.example.json),
[Cursor](examples/cursor.hooks.example.json), [Gemini CLI](examples/gemini-cli.settings.example.json). What each asks of you:

- **Claude Code** gives a session's end 1.5 seconds unless a hook asks for more, which the example's `timeout` does.
  An interactive session holds hooks back until you accept its workspace trust dialog.
- **Codex** runs a hook only after you review and trust it under `/hooks`, and again whenever the hook changes. It gives a session's end 1 second by default and 3 at most, so the example asks for 3 and
  the hook gives up inside that, leaving the message for later.
- **Cursor** does not wait for `sessionEnd`. On Windows its desktop app runs commands through PowerShell, where this command
  works as written.
- **Gemini CLI** takes its `timeout` in milliseconds and does not wait for `SessionEnd` either. `/hooks panel` shows what it
  has; it warns before running a project's hook whose name or command changed.

## The gate

`gate.mjs` decides on the words alone, on this computer, with no model. English only: a message in another language is
rarely kept unless you set `minSignals` to 0. A message is left out when:

| Why | Rule |
|---|---|
| Too short | under `minChars` of words, once fenced code is out |
| Mostly code or output | under 60% letters and spaces |
| A question | it ends by asking something, so nothing is settled yet |
| Nothing lasting | fewer than `minSignals` of five families: a decision, a cause, an outcome, a lesson, a next step, each counted once |
| It echoes an instructions file | a header such as "Contents of ... .md" |
| Mostly secrets | more than one redaction in 15 words |

Of the messages that pass, a session keeps the one with the most families, the newest of equals. Context a client
injected (`<system-reminder>` and its kind) is dropped and fenced code becomes `[code left out]` before any of this.

## Redaction

`redact.mjs` runs on the message before it is written to disk, and again just before it is sent, with each secret replaced by
`[redacted]`:

- exact values: this hook's own token, and any value in the environment the agent gives the hook whose name says it is a
  secret (`*_TOKEN`, `*_SECRET`, `*_PASSWORD`, `*_KEY` and so on), whatever it looks like. Gemini CLI says it runs hooks with
  a sanitized environment, so there this finds little;
- private key blocks, whole, even when cut off before their end;
- provider key shapes (OpenAI, Anthropic, GitHub, GitLab, Slack, AWS, Google, Stripe, npm, PyPI, Hugging Face and others), JWTs;
- connection strings: the password in a database or any other URL, `Password=` and `Pwd=` in ADO, libpq and JDBC forms, Azure's
  `AccountKey=` and `SharedAccessKey=`, a token in a URL's query or as its user;
- credentials in headers (`Authorization`, `Cookie`, `x-api-key`) and on command lines (`--password`, `--token`, `mysql -p`,
  `sshpass -p`, `curl -u`);
- values assigned to a name that says it holds a secret, in shell, code, JSON, YAML or XML (`DB_PASS=`, `secretKey:`,
  `<password>`, `<add key="password" value="...">`), and those said in words ("the password is ...");
- long runs of characters that read as random rather than as words.

Left as they are, on purpose: a UUID, a commit id, a file path, ordinary prose about tokens, `process.env.API_TOKEN`, `$PGPASSWORD`
and other things that only name a secret.

**Redaction is a pattern match, not a promise.** A secret with no telling name or shape, such as a plain word given as a
password in a sentence, can get through, and so can a bare hex key under 48 characters (a commit id looks the same). So can the
rest of an unquoted value after a quote inside it, and a URL's password holding an unencoded `/` or `'`. That is why it is one message and not a session, why you list the folders, and why
what arrives is a draft: look at what your first few sessions leave in the review queue, and delete any that hold what
they should not (the app's Memories view, or `rhythm delete-forever`).

## Turning it off

| To stop | Do this |
|---|---|
| For one run of the agent | Start it with `RHYTHM_HOOK_CAPTURE=off` in its environment |
| In a folder | Take it off `folders`. A message waiting from there is dropped at the next session's end |
| Everywhere | `"capture": "off"`, or delete the settings file, then remove the entries from the agent's settings and delete `hooks-pending/` beside the settings file |
| At the engine | `rhythm token revoke <name>` refuses that token at once; `rhythm capture disconnect <name>` removes the connection; `memory.capture: off` takes the route away, and `rhythm capture list` and `disconnect` still answer once a connection was ever made, since a mirror stays read-only until its connection is removed |

A message waits in `hooks-pending/` beside the settings file, private to you, already redacted, and you may delete it at any
time. It leaves when its session ends. If that never comes (a crash, a killed process), it goes with some other session's end
a day later; if it still cannot be sent, it is deleted unsent after two weeks. At most 50 wait at once. With `"capture": "off"`
in the settings file, the next event of any agent still registered deletes all that waits, unsent. All of this needs the hook
to run: once no agent runs it nothing here is deleted, which is why stopping everywhere means deleting the folder yourself.
`RHYTHM_HOOK_CAPTURE=off` covers one run and deletes nothing.

## What it says when something is wrong

An agent usually hides a hook's stderr when it exits 0, so use `check`; its `last send` line says what happened last.

| It says | Means |
|---|---|
| `404`, the route is off | `memory.capture` or `memory.tools` is off. Nothing was saved and the message is not kept |
| `401`, `403` | The token is revoked or unknown, is not a capture token, or this address is not in `engine.allowedHosts`. The message is not kept |
| `400`, `409`, `413` | The engine refused this body. The message is not kept |
| Not reached, or `503` | The engine is down or starting. The message waits |
| `429` | This token is past its write limit (`engine.writeLimit`). The message waits |
| `200`, something that is not the engine | A proxy or a sign-in page answered in its place. Nothing is known to be saved, so the message waits |
| `off: ...` | A setting is wrong, and the hook is off until it is right |

The tests are `scripts/hooks.test.mjs`, with a fixture of runtime-built secrets, and the engine's `SessionHookTests.cs`, which
runs this hook against the real door and a real database.
