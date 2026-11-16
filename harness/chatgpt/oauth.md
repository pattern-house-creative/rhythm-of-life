# OAuth on the existing MCP endpoint

OAuth is optional and off by default. Hosted ChatGPT needs it when its connection dialog cannot
send a fixed Authorization header. Existing Claude and other bearer clients keep the same `/mcp`
URL, credentials, roles and revocation behavior.

The engine uses OpenIddict for authorization-code + S256 PKCE, encrypted protocol state, reference
access tokens and refresh-token rotation. A predefined public client has exact allowed callbacks;
there is no open dynamic registration. No new user database is introduced: consent requires a
dedicated existing harness token, and the resulting OAuth grant speaks for that same principal.
The approval page describes its read and write access. The harness token never goes to ChatGPT.

## Configure

After applying the normal migrations (including `077_oauth.sql`), configure:

```yaml
engine:
  allowedHosts: [localhost, engine, rhythm-mcp.example.invalid]
  publicMcpUrl: https://rhythm-mcp.example.invalid/mcp
  oauth:
    enabled: true
    clientId: rhythm-chatgpt
    redirectUris:
      - https://chatgpt.com/connector_platform_oauth_redirect
```

Use the exact redirect URI displayed by ChatGPT. Issuer identification is advertised and returned
on authorization responses. Copy the callback into the allowlist; never use a wildcard.

Create a separate harness token with `./rhythm token create chatgpt-oauth`, optionally with `--person`
as appropriate for the install's existing scope settings. Use that token only on Rhythm's HTTPS
approval page, never in a URL, command argument, repository, or chat. Separate tokens provide
independent revocation; they do not create narrower data scopes.

On the existing Cloudflare hostname preserve the exact `/mcp` route and add only these engine routes:

- `/.well-known/oauth-protected-resource` and `/.well-known/oauth-protected-resource/mcp`
- `/.well-known/oauth-authorization-server`
- `/.well-known/jwks`
- `/oauth/authorize`, `/oauth/token`, `/oauth/revoke`

Keep the default 404 for all other paths. TLS terminates at Cloudflare; the engine requires the
configured public host on OAuth routes and never trusts forwarded headers to select its issuer.
Browser requests remain forbidden on `/mcp`. Only the approval page accepts a same-origin form POST,
with an antiforgery token and Secure, HttpOnly, SameSite cookie. OAuth requests are size and rate limited.

In ChatGPT, choose the existing HTTPS `/mcp` URL and OAuth. Select a predefined public client, enter
the configured client ID, leave the client secret empty, select token endpoint auth method `none`,
and request `rhythm offline_access`. This is OAuth, without OpenID Connect or an email-profile endpoint.
Follow the sign-in link and approve with the dedicated Rhythm token. The exact dialog varies.

## Lifecycle and rollback

Access tokens last 15 minutes; refresh tokens last 30 days and rotate. Codes last two minutes and
are single use. OpenIddict validates the client, PKCE and grant state; the engine also checks the
exact resource, required scope and the underlying principal on every use. Revoking the harness token
or suspending its person stops OAuth access and refresh without affecting other client credentials.

Signing, encryption and antiforgery keys live under the engine data directory's `oauth-keys`, with
owner-only permissions. Back them up privately with the engine data and database. Do not delete them
on restart. Certificates last five years; plan replacement and reauthorization before expiry.
Restored installations revoke their underlying principals as usual, so restored OAuth grants do
not bypass the restore quarantine.

To withdraw OAuth, set `enabled: false`, restart the engine and remove only the added OAuth routes.
Existing bearer authentication continues. Keep the additive database tables for a later re-enable.

## Verification

Verified with a hosted ChatGPT account over an operator's existing Cloudflare tunnel: browser consent,
the callback and token exchange completed, ChatGPT reported OAuth in use, and tool refresh completed.
The preexisting Claude bearer credential still listed the same tools before and after the rollout.
This does not establish compatibility with every hosted client or account variant.

Exercise discovery, sign-in, PKCE exchange, refresh and revocation with synthetic data first.
Reject invalid callbacks, wrong resource or verifier, reused codes, revoked principals, foreign
Origin and unauthenticated MCP calls. Verify a preexisting bearer token still works before and
after enabling OAuth, including while OAuth credentials are invalid or revoked.
