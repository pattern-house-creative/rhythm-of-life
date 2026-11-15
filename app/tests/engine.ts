// Test helpers: point the server's handlers at a fictional engine. Nothing here reaches a
// network: tests stub fetch and answer as the engine would (contracts/README.md section 1).
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { vi } from "vitest";
import { resetEngine, requestId } from "../server/engine";
import type { AppTool } from "../server/engine";
import { resetSettings } from "../server/config";

export const engineUrl = "http://engine.example.invalid:8080";
export const allAppTools: AppTool[] = [
  "app_graph",
  "app_insights",
  "app_insight_feedback",
  "app_dashboards",
  "app_request_refresh",
  "app_latest_glucose",
  "app_recent_glucose",
  "app_garmin_summary",
  "app_request_garmin_sync",
  "app_upload_document",
  "app_screens",
  "app_status",
  "app_sync",
];
// Built at runtime, so no file holds a token-shaped value.
export const appToken = ["fictional", "app", "token"].join("-");

// A live install: an engine URL, the app token in its file, and settings with the fictional
// people alex and sam. `tools` is what the engine's tools/list offers.
export function withEngine({
  tools = allAppTools as string[],
  timezone = "Europe/London",
  url = engineUrl,
  token = appToken,
  extra = "",
}: {
  tools?: string[];
  timezone?: string;
  url?: string;
  token?: string;
  extra?: string;
} = {}) {
  const dir = mkdtempSync(join(tmpdir(), "rhythm-test-"));
  writeFileSync(join(dir, "app_token"), token ? `${token}\n` : "");
  writeFileSync(
    join(dir, "rhythm.yaml"),
    [
      "version: 1",
      "instance:",
      "  name: Test home",
      `timezone: ${timezone}`,
      "people:",
      "  - id: alex",
      "    name: Alex",
      "  - id: sam",
      "    name: Sam",
      "engine:",
      `  url: "${url}"`,
      extra,
    ].join("\n"),
  );
  vi.stubEnv("RHYTHM_CONFIG", join(dir, "rhythm.yaml"));
  vi.stubEnv("RHYTHM_APP_TOKEN_FILE", join(dir, "app_token"));
  vi.stubEnv("RHYTHM_ENGINE_URL", "");
  resetSettings();
  resetEngine(tools);
  return dir;
}

// The engine's answer to one tools/call: a JSON-RPC result carrying structuredContent.
export const rpc = (structuredContent: unknown, isError = false) =>
  new Response(
    JSON.stringify({
      jsonrpc: "2.0",
      id: requestId,
      result: {
        structuredContent,
        content: [{ type: "text", text: "fictional" }],
        isError,
      },
    }),
    { headers: { "content-type": "application/json" } },
  );

// The tool name and arguments of a recorded engine call.
export const called = (init?: RequestInit) => {
  const body = JSON.parse(String(init?.body));
  return { name: body.params?.name, arguments: body.params?.arguments };
};

// Installs where the app must not call the engine at all: demo mode (no token, an empty token
// file, no engine URL) and a credential past app.credentialExpiry.
export const closedEngines: [string, () => void][] = [
  [
    "no token file",
    () => {
      withEngine();
      vi.stubEnv("RHYTHM_APP_TOKEN_FILE", "/nonexistent/app_token");
    },
  ],
  ["an empty token file", () => void withEngine({ token: "" })],
  ["no engine URL", () => void withEngine({ url: "" })],
  [
    "an expired credential",
    () =>
      void withEngine({
        extra: "app:\n  credentialExpiry: 2020-01-01T00:00:00Z",
      }),
  ],
];
