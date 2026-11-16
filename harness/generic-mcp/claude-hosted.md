# Claude on the web, desktop or mobile, as a custom connector

A custom connector in Claude connects from Anthropic's servers, not from your computer. It can use
Rhythm's MCP endpoint with a bearer token where your Claude account offers request headers, which
Anthropic has in beta for a limited set of accounts. For Claude Code on your own computer, use the
[generic MCP instructions](README.md) instead: it needs none of this.

## Before connecting

Provide a public HTTPS endpoint that routes only the exact MCP path, for example
https://rhythm-mcp.example.invalid/mcp. The [Cloudflare recipe](../../deploy/exposure/cloudflare/README.md)
is optional exposure infrastructure. Keep the app behind its own protection, preserve the incoming
Host header, add the hostname to engine.allowedHosts, and restart the engine after validating its
configuration. Block every other path at the tunnel. Keep TLS certificate checks enabled.

Give this client its own credential:

```sh
./rhythm token create claude-connector
```

The CLI writes the credential privately and registers its hash. Do not put it in repository files,
logs, command arguments or screenshots. A token without a person binding has the household view.
With memory scopes off, separate client credentials do not isolate household data.
They provide separate revocation. A harness token permits reads and supported writes.

## Add the connector

Follow [Anthropic's current custom connector documentation](https://claude.com/docs/connectors/custom/add-unlisted).
Request-header authentication is in beta for a limited set of accounts: if the dialog shows no
**Request headers** section, your account does not have it, and this setup is not available to you.

1. In Customize > Connectors, add a custom connector and enter the HTTPS MCP URL.
2. Select **No sign-in** for this fixed-credential setup.
3. Under Request headers, choose **authorization**, mark it required, and privately enter
   the full value: the Bearer scheme, one space, and this client's token.
4. Save and confirm that Claude discovers Rhythm's tools.

The label No sign-in selects the absence of OAuth in Claude; Rhythm still requires the bearer
credential. Anthropic retains the saved header and sends it with requests. An account without
Request headers cannot use this particular setup. The optional [OAuth setup](../chatgpt/oauth.md)
is verified with ChatGPT; this Claude guide uses the existing bearer header. Do not use Authorization
for two authentication schemes at once.

Review tool permissions before use. In the one account this was tried with, every tool needed
approval at first. Claude's UI also said its Auto mode could permit actions after automated checks, so a
client's permission screen is not a server-enforced data scope.

Authentication settings cannot be edited after saving this connector; replacing a credential
requires removing and re-adding it, per Anthropic's documentation.

## Check it

Check the public endpoint using a client that keeps the credential private:

- No credential and a synthetic invalid credential: 401, with no sign-in redirect.
- Valid credential: initialize, then tools/list; do not call write tools.
- Valid credential with an Origin header: 403.
- Root, health, app, document, capture, legacy SSE and trailing-slash MCP paths: 404.
- Normal TLS verification succeeds, and MCP responses prohibit storage.
- Read only the static resource rhythm://guides/dashboards to check resource transport.
  It contains design guidance, not household records. If a client's UI does not expose resources
  or prompts, verify it directly with an MCP client and state that limitation.

## How far this is tested

Tried by hand once, on 9 November 2026, with one personal Claude account against one install. The
checks above passed, the connector saved the header and listed Rhythm's tools, and the guide was read
through a separate MCP client. Not tried: a tool call inside a Claude conversation, the mobile apps,
write tools, a Team account, and the half of the Cloudflare recipe that puts a login in front of the
app. A later attempt to connect a hosted Claude connector failed and the cause was not found
([Studio's notes](../../docs/dev/studio.md#client-evidence)). On 15 November 2026, the operator
confirmed that the hosted Claude connection works on that install. This is a user report, not a
repeat of the boundary checks above; it does not establish support for every account or client.

## Revoke and remove

```sh
./rhythm token revoke claude-connector
```

The engine checks the enabled credential on each request, so revocation needs no engine restart.
This token has no automatic expiry. Removing a Claude connector does not revoke its token.
To withdraw exposure, remove the dedicated MCP route and its DNS record, then stop only its
connector. If restoring engine settings, preserve any unrelated changes made since setup.

## A second account or organization

Use a distinct, revocable credential for each approved connection. Request-header credentials
may have an organization-wide audience; verify that audience before supplying a household token.
Team or Enterprise custom connectors require the appropriate organization role and policy approval.
A local terminal client is not permission to bypass those controls.
