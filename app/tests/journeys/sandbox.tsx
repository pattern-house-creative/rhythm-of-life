// A person's journeys against a sandbox stack (SB-P6-04; cli/journeys.sh starts it and runs each file here): the real
// React app, in jsdom, whose every request goes over plain HTTP to the sandbox's own app container, which talks to its
// engine and database. Nothing here stands in for the server, the engine or the data; only the browser is jsdom's (no
// layout, no service worker, no PDF canvas). Without the sandbox (RHYTHM_JOURNEY_APP unset, as in npm test) every test
// here is skipped. Fictional data only: it talks to no host but the sandbox's two local ports.
import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { request as httpRequest } from "node:http";
import { join, resolve } from "node:path";
import { beforeEach, describe, expect, vi } from "vitest";
import { configure, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import App from "../../src/App";
import { cleanInstance, setInstance } from "../../src/instance";

// Read as the file loads: tests/setup.ts takes every RHYTHM_ setting away before each test.
const shell = { ...process.env };
export const appUrl = shell.RHYTHM_JOURNEY_APP ?? "";
const mcpUrl = shell.RHYTHM_JOURNEY_MCP ?? "";
const checkout = resolve(process.cwd(), "..");
const secrets = join(checkout, "deploy", "compose", "secrets");

// What makes this run's words its own: a journey can run again in the same stack, and every search and count below reads
// the whole install, the synthetic corpus included.
export const run = Date.now().toString(36);

// A journey's tests: skipped without the sandbox. Each only ever takes a local port's address, so a setting can never
// send one anywhere else.
if (appUrl && !/^http:\/\/127\.0\.0\.1:\d+$/.test(appUrl))
  throw new Error("RHYTHM_JOURNEY_APP is this computer's own port");
if (mcpUrl && !/^http:\/\/127\.0\.0\.1:\d+$/.test(mcpUrl))
  throw new Error("RHYTHM_JOURNEY_MCP is this computer's own port");
export const journey = describe.skipIf(!appUrl);

// One request over plain HTTP, so jsdom's own AbortSignal can still stop it, which Node's fetch does not take.
function exchange(url: string, init: RequestInit = {}): Promise<Response> {
  return new Promise((done, fail) => {
    const signal = init.signal;
    const stopped = () =>
      new DOMException("The request was stopped", "AbortError");
    if (signal?.aborted) return fail(stopped());
    const headers = Object.fromEntries(new Headers(init.headers).entries());
    const request = httpRequest(
      url,
      { method: init.method ?? "GET", headers },
      (answer) => {
        const chunks: Buffer[] = [];
        answer.on("data", (c: Buffer) => chunks.push(c));
        answer.on("error", fail);
        answer.on("end", () => {
          const status = answer.statusCode ?? 0;
          const told = new Headers();
          for (const [name, value] of Object.entries(answer.headers))
            if (typeof value === "string") told.set(name, value);
            else if (value) for (const v of value) told.append(name, v);
          done(
            new Response(
              [101, 204, 205, 304].includes(status)
                ? null
                : Buffer.concat(chunks),
              { status, headers: told },
            ),
          );
        });
      },
    );
    request.on("error", fail);
    signal?.addEventListener(
      "abort",
      () => {
        request.destroy();
        fail(stopped());
      },
      { once: true },
    );
    request.end(typeof init.body === "string" ? init.body : undefined);
  });
}
// What the app's fetch reaches: the sandbox's app container, by the path the app asked for.
export const fetchApp = (path: unknown, init?: RequestInit) =>
  exchange(new URL(String(path), `${appUrl}/`).href, init);
export const appJson = async (path: string, body?: unknown) =>
  (
    await fetchApp(
      path,
      body === undefined
        ? undefined
        : {
            method: "POST",
            headers: { "content-type": "application/json" },
            body: JSON.stringify(body),
          },
    )
  ).json() as Promise<any>;
export const memories = (body: Record<string, unknown>) =>
  appJson("/api/memories", body);
// A memory saved by the app's own op, what it wrote: for a journey that is about what comes after saving it.
export async function saved(
  content: string,
  extra: Record<string, unknown> = {},
) {
  const answer = await memories({ op: "capture", content, ...extra });
  expect(answer.ok, JSON.stringify(answer)).toBe(true);
  return answer.written as { id: string; revision: number };
}

// Each test: the app talks to the sandbox only, with the install's own name, people and zone, as main.tsx reads them.
export function inSandbox() {
  vi.setConfig({ testTimeout: 240_000, hookTimeout: 240_000 });
  configure({
    asyncUtilTimeout: 30_000,
    // What the page says when something is not there: more use in a run's log than a dump of its elements.
    getElementError: (message) => {
      const missing = new Error(
        `${message}\n\nThe page says: ${document.body.textContent?.replace(/\s+/g, " ").trim().slice(0, 3000)}`,
      );
      missing.name = "TestingLibraryElementError";
      return missing;
    },
  });
  beforeEach(async () => {
    if (!appUrl) return;
    vi.stubGlobal("fetch", vi.fn(fetchApp));
    const instance = cleanInstance(await appJson("/api/instance"));
    expect(instance?.mode).toBe("live");
    setInstance(instance!);
  });
}

// The two sizes the app lays itself out for, as tests/story-14.test.tsx has them.
const sizes = {
  phone: (query: string) => query.includes("max-width: 760px"),
  desktop: (query: string) => query.includes("min-width: 1100px"),
} as const;
export type Size = keyof typeof sizes;
export function at(size: Size) {
  vi.mocked(window.matchMedia).mockImplementation(
    (query: string) =>
      ({
        matches: sizes[size](query),
        media: query,
        addListener: vi.fn(),
        removeListener: vi.fn(),
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
      }) as unknown as MediaQueryList,
  );
}

// Home, as the app draws it for this install.
export async function home() {
  const user = userEvent.setup();
  const view = render(<App />);
  await screen.findByRole("region", { name: "Knowledge constellation" });
  return { user, view };
}
// Home's way into the sky: the explorer, its stars from the engine.
export async function explorer() {
  const { user, view } = await home();
  await user.click(
    await screen.findByRole("button", { name: "Open Explorer" }),
  );
  const region = await screen.findByRole("region", {
    name: "Knowledge explorer",
  });
  return { user, view, region };
}
// The explorer's way into the Memories view, at its list.
export async function memoriesView() {
  const opened = await explorer();
  await opened.user.click(
    await screen.findByRole("button", { name: "Memories" }),
  );
  const region = await screen.findByRole("region", { name: "Memories" });
  return { ...opened, region };
}
// A section on the view's strip, by its name (with how many wait, when it says so).
export async function section(
  user: ReturnType<typeof userEvent.setup>,
  region: HTMLElement,
  name: string | RegExp,
) {
  const strip = within(region).getByRole("toolbar", { name: "Memory sections" });
  await user.click(await within(strip).findByRole("button", { name }));
}
// A row of a list, by words it holds.
export const rowWith = (list: HTMLElement, words: string | RegExp) => {
  const hit = within(list).getAllByText(words)[0];
  const row = hit.closest("li");
  if (!row) throw new Error(`no row holds ${words}`);
  return row as HTMLElement;
};

// Words in a list, asking for more of it until they are there: a queue shows its head.
export async function shown(
  user: ReturnType<typeof userEvent.setup>,
  region: HTMLElement,
  list: HTMLElement,
  words: string | RegExp,
) {
  for (;;) {
    try {
      return await within(list).findAllByText(words, {}, { timeout: 2000 });
    } catch (missing) {
      const more = within(region).queryByRole("button", { name: "Show more" });
      if (!more) throw missing;
      await user.click(more);
    }
  }
}

// An AI client: its own token through the sandbox's /mcp, as any client calls. Answers the tool's structured content,
// or its text read as JSON when it answers in text, or the text itself.
export const tokenOf = (file: string) =>
  readFileSync(join(secrets, `${file}_token`), "utf8").trim();
export const aiClient = (
  token: string,
  tool: string,
  args: Record<string, unknown> = {},
) =>
  rpc(tokenOf(`harness_${token}`), "tools/call", {
    name: tool,
    arguments: args,
  });
// An external plugin: its ingest token, as its own container would call.
export const plugin = (
  name: string,
  tool: string,
  args: Record<string, unknown>,
) =>
  rpc(tokenOf(`plugin_${name}`), "tools/call", { name: tool, arguments: args });
export async function rpc(
  bearer: string,
  method: string,
  params: Record<string, unknown> = {},
) {
  const answer = await exchange(`${mcpUrl}/mcp`, {
    method: "POST",
    headers: {
      authorization: `Bearer ${bearer}`,
      "content-type": "application/json",
      accept: "application/json, text/event-stream",
      "mcp-protocol-version": "2025-03-26",
    },
    body: JSON.stringify({ jsonrpc: "2.0", id: "journey", method, params }),
  });
  const text = await answer.text();
  const data =
    text
      .split(/\r?\n/)
      .find((line) => line.startsWith("data: "))
      ?.slice(6) ?? text;
  const reply = JSON.parse(data) as { result?: any; error?: any };
  if (reply.error) throw new Error(`${method}: ${JSON.stringify(reply.error)}`);
  if (method !== "tools/call") return reply.result;
  if (reply.result?.structuredContent) return reply.result.structuredContent;
  const said = reply.result?.content?.[0]?.text as string | undefined;
  try {
    return JSON.parse(said ?? "null");
  } catch {
    return said;
  }
}
// A person at the terminal: one rhythm command in this checkout's install. A journey calls it only where the app has no
// way to do the step, and says so; cli/journeys.sh prints each one, so a run lists what still needs a terminal.
export function terminal(...args: string[]) {
  console.log(`terminal: rhythm ${args[0]}`);
  const out = spawnSync("sh", ["./rhythm", ...args], {
    cwd: checkout,
    env: shell,
    encoding: "utf8",
    timeout: 180_000,
  });
  if (out.status !== 0)
    throw new Error(`rhythm ${args.join(" ")}: ${out.stdout}\n${out.stderr}`);
  return out.stdout;
}

// Asks again until it passes: a memory reaches the map at a rebuild, a passage at the index job's next run.
export async function eventually<T>(
  read: () => Promise<T>,
  passes: (value: T) => boolean,
  what: string,
  seconds = 180,
) {
  const until = Date.now() + seconds * 1000;
  for (;;) {
    const value = await read();
    if (passes(value)) return value;
    if (Date.now() > until)
      throw new Error(`never ${what}: ${JSON.stringify(value).slice(0, 2000)}`);
    await new Promise((done) => setTimeout(done, 2000));
  }
}
