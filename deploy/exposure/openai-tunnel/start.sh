#!/bin/sh
# The openai-tunnel container's entry point (compose.yaml next to this file): checks its setting and secrets, turns
# the harness token into the Authorization header the client sends to the engine, and starts the client. Nothing
# secret is printed, put in the environment or on a command line: the client reads the key and the header from
# files in /tmp (a tmpfs, private to this user).
set -eu
umask 077

fail() { echo "openai-tunnel: $*" >&2; exit 1; }
settings=/etc/rhythm/rhythm.yaml
key=/run/secrets/openai-tunnel_key
token=/run/secrets/openai-tunnel_token

# Docker makes an empty folder in place of a secret file that did not exist at up (docs/configuration.md).
[ -f "$key" ] && [ -s "$key" ] || fail "deploy/compose/secrets/adapters/openai-tunnel_key is missing or empty: ./rhythm connect openai-tunnel"
[ -f "$token" ] && [ -s "$token" ] ||
  fail "deploy/compose/secrets/harness_openai-tunnel_token is missing or empty: ./rhythm token create openai-tunnel"
[ -r "$key" ] && [ -r "$token" ] || fail "uid $(id -u) cannot read its secret files: ./rhythm up gives them their owner"

# adapters.openai-tunnel.tunnelId, the only tunnelId key in rhythm.yaml (contracts/rhythm.schema.json).
tunnel=$(sed -n "s/^[[:space:]]*tunnelId:[[:space:]]*[\"']\{0,1\}\([A-Za-z0-9_]*\).*/\1/p" "$settings" | head -n 1)
echo "$tunnel" | grep -Eq '^tunnel_([a-z0-9]{4}_)?[a-z0-9]{32}$' ||
  fail "adapters.openai-tunnel.tunnelId in rhythm.yaml is missing or not a tunnel ID (tunnel_...): ./rhythm connect openai-tunnel"

# The token file holds the bare token; the client sends a header's value from a file (file:), so it gets one.
value=$(tr -d '\r\n' < "$token")
case "$value" in '' | *[!A-Za-z0-9_-]*) fail "deploy/compose/secrets/harness_openai-tunnel_token does not hold one token" ;; esac
[ "${#value}" -ge 32 ] && [ "${#value}" -le 512 ] || fail "deploy/compose/secrets/harness_openai-tunnel_token does not hold one token"
printf 'Bearer %s' "$value" > /tmp/authorization
unset value
tr -d '\r\n' < "$key" > /tmp/openai-key

# poll-channel main: the MCP channel only; the client's other channels stay off. The health listener (/healthz,
# /readyz, the client's own admin page) answers only inside this container.
exec /usr/bin/tunnel-client run \
  --control-plane.tunnel-id "$tunnel" \
  --control-plane.api-key=file:/tmp/openai-key \
  --control-plane.poll-channel=main \
  --mcp.server-url=http://engine:8080/mcp \
  --mcp.extra-headers "Authorization: file:/tmp/authorization" \
  --health.listen-addr=127.0.0.1:8080
