#!/usr/bin/env node
// A session hook for a coding agent (SB-P7-02). A file a person installs on purpose; README.md in this folder is the how.
//
//   node rhythm-hook.mjs --agent <claude-code|codex|cursor|gemini-cli>    as the agent runs it, an event's JSON on stdin
//   node rhythm-hook.mjs check                                            what it would do, and whether the engine agrees
//   node rhythm-hook.mjs preview                                          what the gate makes of a text on stdin; no network
//
// As a hook it never steers the agent: it writes {} to stdout, the one output every agent here takes as nothing to say, and
// exits 0 whatever happened. Exit 2 would block an agent from stopping, so no path here can reach it.

import { AGENTS, readEvent } from "./agents.mjs";
import { check, dropWaiting, keepTurn, loadConfig, ownSecrets, sendWaiting } from "./core.mjs";
import { assess, REASONS } from "./gate.mjs";

const STDIN_CAP = 4 * 1024 * 1024;
const LONGEST_MS = Math.max(...Object.values(AGENTS).map((agent) => agent.budgetMs)) + 2000;
const say = (line) => process.stderr.write(`rhythm hook: ${line}\n`);

async function stdin() {
  const chunks = [];
  let size = 0;
  for await (const chunk of process.stdin) {
    size += chunk.length;
    if (size <= STDIN_CAP) chunks.push(chunk);
  }
  return size <= STDIN_CAP ? Buffer.concat(chunks).toString("utf8") : null;
}

async function hook(agentName) {
  process.stdout.write("{}\n");
  // Nothing an agent waits for may hang: past this the process ends, and what waits stays for a later session.
  setTimeout(() => process.exit(0), LONGEST_MS).unref();
  const text = await stdin();
  if (!Object.hasOwn(AGENTS, agentName ?? "")) return say("--agent names an agent these hooks do not know.");
  const config = loadConfig(process.env);
  if (!config.on) {
    if (config.wrong) say(`off: ${config.why}.`);
    if (config.turnedOff) dropWaiting(config);
    return;
  }
  if (text === null) return;
  let input;
  try {
    input = JSON.parse(text);
  } catch {
    return;
  }
  const event = readEvent(agentName, input, process.env);
  if (!event) return;
  if (event.kind === "turn") keepTurn(agentName, event, config, process.env);
  else await sendWaiting(agentName, event, config, process.env, { say });
}

async function preview() {
  const config = loadConfig(process.env);
  const options = config.on ? { ...config.gate, own: ownSecrets(config, process.env) } : {};
  const verdict = assess(await stdin(), options);
  if (verdict.keep) process.stdout.write(`kept, score ${verdict.score}, ${verdict.redactions} redacted. This is what would be sent:\n\n${verdict.text}\n`);
  else process.stdout.write(`left out: ${REASONS[verdict.reason]}.\n`);
}

const [command, value] = process.argv.slice(2);
const asHook = command === "--agent" || command?.startsWith("--agent=");
try {
  if (command === "check") process.exitCode = await check(process.env, (line) => process.stdout.write(`${line}\n`));
  else if (command === "preview") await preview();
  else if (asHook) await hook(command === "--agent" ? value : command.slice("--agent=".length));
  else {
    process.stderr.write(`Usage: rhythm-hook.mjs --agent <${Object.keys(AGENTS).join("|")}> | check | preview\n`);
    process.exitCode = 1;
  }
} catch (e) {
  // A hook's failure is never the agent's: the code alone, with no words of any message in it.
  say(`failed: ${e?.code ?? e?.name ?? "error"}`);
  process.exitCode = asHook ? 0 : 1;
}
