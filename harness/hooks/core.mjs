// What the hook does with an event: its settings, the one message waiting per session, and the one call it ever makes, POST
// /capture with the person's capture token (SB-P7-02; contracts/README.md section 6). Nothing here runs until a person has
// registered the hook with their agent and written a settings file saying capture "on"; README.md is the how.

import { createHash, randomBytes } from "node:crypto";
import { lstatSync, mkdirSync, readdirSync, readFileSync, realpathSync, renameSync, rmSync, statSync, writeFileSync } from "node:fs";
import { request as httpRequest } from "node:http";
import { request as httpsRequest } from "node:https";
import { homedir } from "node:os";
import { basename, dirname, isAbsolute, join, relative, resolve } from "node:path";

import { AGENTS } from "./agents.mjs";
import { assess, BOUNDS, DEFAULTS } from "./gate.mjs";
import { redact, secretValues } from "./redact.mjs";

export const DEFAULT_ENGINE = "http://127.0.0.1:8181";

// A session whose end never came (a crash, a killed process) is sent with a later session's end; after two weeks undelivered
// its message is deleted unsent. That takes a hook still being run: capture turned off deletes what waits at the next event,
// and once no agent runs this file nothing here can, so README.md says to delete the folder then. At most this many wait at
// once, the oldest going first.
const STALE_MS = 24 * 60 * 60 * 1000;
const EXPIRED_MS = 14 * 24 * 60 * 60 * 1000;
const MAX_WAITING = 50;
const STALE_PER_RUN = 2;

const sha = (text) => createHash("sha256").update(text).digest("hex");
const expand = (path) => (path === "~" || path.startsWith("~/") || path.startsWith("~\\") ? join(homedir(), path.slice(1)) : path);
const real = (path) => {
  try {
    return realpathSync(path);
  } catch {
    return resolve(path);
  }
};

export const configPath = (env) => env.RHYTHM_HOOK_CONFIG || join(homedir(), ".config", "rhythm", "hooks.json");

// Where a capture goes: always /capture on the named engine, never anywhere else. A token never travels in the clear past
// this computer unless the settings say so, and a URL holding credentials is refused rather than used.
export function captureUrl(engineUrl, allowPlainHttp) {
  let url;
  try {
    url = new URL(engineUrl);
  } catch {
    return { problem: "engineUrl is not a URL" };
  }
  if (url.protocol !== "http:" && url.protocol !== "https:") return { problem: "engineUrl is not http or https" };
  if (url.username || url.password) return { problem: "engineUrl must not hold credentials" };
  const loopback = url.hostname === "localhost" || url.hostname === "[::1]" || /^127\.\d+\.\d+\.\d+$/.test(url.hostname);
  if (url.protocol === "http:" && !loopback && allowPlainHttp !== true)
    return { problem: "engineUrl sends the token unencrypted past this computer; use https, or set allowPlainHttp on your own network" };
  url.pathname = "/capture";
  url.search = "";
  url.hash = "";
  return { url };
}

// The settings, or {on: false, why}. Off is every state but a file a person wrote saying capture "on" with what it needs,
// so a wrong setting turns the hook off rather than sending somewhere unintended.
export function loadConfig(env = process.env) {
  const path = configPath(env);
  const off = (why, wrong = false) => ({ path, on: false, why, wrong });
  let raw;
  try {
    raw = JSON.parse(readFileSync(path, "utf8"));
  } catch (e) {
    return e?.code === "ENOENT" ? off("no settings file") : off("the settings file cannot be read as JSON", true);
  }
  if (raw === null || typeof raw !== "object" || Array.isArray(raw)) return off("the settings file is not one JSON object", true);
  // The file saying so is a person's decision for good, so what waits goes (dropWaiting). The variable covers one run only.
  if (raw.capture !== "on") return { ...off('capture is not "on"'), dir: dirname(path), turnedOff: true };
  if (env.RHYTHM_HOOK_CAPTURE === "off") return off("RHYTHM_HOOK_CAPTURE is off for this run");
  const target = captureUrl(raw.engineUrl ?? DEFAULT_ENGINE, raw.allowPlainHttp);
  if (target.problem) return off(target.problem, true);
  if (typeof raw.tokenFile !== "string" || !isAbsolute(expand(raw.tokenFile))) return off("tokenFile must be a full path to the capture token's file", true);
  // A relative folder would mean whichever folder the agent runs in, and so every one of them.
  if (!Array.isArray(raw.folders) || !raw.folders.length || raw.folders.some((f) => typeof f !== "string" || (f !== "*" && !isAbsolute(expand(f)))))
    return off('folders must list full paths to capture from, or "*" for all of them', true);
  if (raw.project !== undefined && (typeof raw.project !== "string" || (raw.project !== "auto" && !/^[a-z0-9][a-z0-9-]{0,63}$/.test(raw.project))))
    return off('project must be "auto" or a slug', true);
  const gate = { ...DEFAULTS };
  for (const [name, [low, high]] of Object.entries(BOUNDS)) {
    if (raw[name] === undefined) continue;
    if (!Number.isInteger(raw[name]) || raw[name] < low || raw[name] > high) return off(`${name} must be a whole number from ${low} to ${high}`, true);
    gate[name] = raw[name];
  }
  return { path, on: true, dir: dirname(path), url: target.url, tokenFile: expand(raw.tokenFile), folders: raw.folders.map(expand), project: raw.project, gate };
}

export function readToken(config) {
  try {
    const token = readFileSync(config.tokenFile, "utf8").trim();
    return /^[A-Za-z0-9._~+/=-]{16,512}$/.test(token) ? token : null;
  } catch {
    return null;
  }
}

// Exact values no message may carry: the token this hook holds, and what the agent's environment names as a secret.
export function ownSecrets(config, env) {
  const token = readToken(config);
  return token ? [token, ...secretValues(env)] : secretValues(env);
}

const within = (root, folder) => {
  const rel = relative(real(root), real(folder));
  return rel === "" || (!rel.startsWith("..") && !isAbsolute(rel));
};

// Every folder of the session must be under one a person listed. "*" says every folder, on purpose.
export function folderAllowed(folders, config) {
  if (!folders.length) return false;
  if (config.folders.includes("*")) return true;
  return folders.every((folder) => typeof folder === "string" && isAbsolute(folder) && config.folders.some((root) => within(root, folder)));
}

const pendingDir = (config) => join(config.dir, "hooks-pending");

// Capture was turned off in the settings file: nothing that waits is sent, now or if it is turned on again later.
export const dropWaiting = (config) => rmSync(pendingDir(config), { recursive: true, force: true });
const pendingFile = (config, agentName, session) => join(pendingDir(config), `${agentName}.${sha(session).slice(0, 32)}.json`);

function writePrivate(file, value) {
  mkdirSync(dirname(file), { recursive: true, mode: 0o700 });
  const scratch = `${file}.${randomBytes(6).toString("hex")}.tmp`;
  try {
    writeFileSync(scratch, JSON.stringify(value), { mode: 0o600, flag: "wx" });
    renameSync(scratch, file);
  } catch (e) {
    rmSync(scratch, { force: true });
    throw e;
  }
}

function readPending(file) {
  try {
    if (!lstatSync(file).isFile()) return null;
    const value = JSON.parse(readFileSync(file, "utf8"));
    if (typeof value?.text === "string" && Object.hasOwn(AGENTS, value.agent)) return value;
  } catch (e) {
    if (e?.code === "ENOENT") return null;
  }
  rmSync(file, { force: true });
  return null;
}

function waiting(config) {
  let names;
  try {
    names = readdirSync(pendingDir(config));
  } catch {
    return [];
  }
  const files = [];
  for (const name of names) {
    const file = join(pendingDir(config), name);
    try {
      const modified = statSync(file).mtimeMs;
      // What a run killed in the middle of a write left behind.
      if (name.endsWith(".tmp") && Date.now() - modified > STALE_MS) rmSync(file, { force: true });
      if (name.endsWith(".json")) files.push({ file, modified });
    } catch {
      // Gone since the listing: another session's end sent it.
    }
  }
  return files.sort((a, b) => a.modified - b.modified);
}

// A turn ended: if its message passes the gate and is at least as good as what this session already holds, it replaces that.
// No network: what waits is redacted already, and leaves only when the session ends. Answers what became of it, for tests.
export function keepTurn(agentName, event, config, env, now = new Date()) {
  if (event.continuing) return "continuing";
  if (!folderAllowed(event.folders, config)) return "folder";
  const verdict = assess(event.text, { ...config.gate, own: ownSecrets(config, env) });
  if (!verdict.keep) return verdict.reason;
  const file = pendingFile(config, agentName, event.session);
  const held = readPending(file);
  if (held && held.score > verdict.score) return "weaker";
  writePrivate(file, { v: 1, agent: agentName, session: sha(event.session), at: now.toISOString(), score: verdict.score, folders: event.folders, text: verdict.text });
  const all = waiting(config);
  for (const old of all.slice(0, Math.max(0, all.length - MAX_WAITING))) rmSync(old.file, { force: true });
  return "kept";
}

// "auto": the listed folder the session is under, the deepest of them, so a session that has moved into a subfolder does not
// make a new project of it. Only under "*", which lists no folder, is it the session folder's own name.
function projectOf(config, folders) {
  if (config.project !== "auto") return config.project;
  const folder = String(folders[0] ?? "");
  const roots = folder ? config.folders.filter((root) => root !== "*" && within(root, folder)) : [];
  const root = roots.reduce((deepest, next) => (deepest === undefined || real(next).length > real(deepest).length ? next : deepest), undefined);
  const slug = basename(root ?? folder).toLowerCase().replace(/[^a-z0-9-]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 64).replace(/-+$/, "");
  return /^[a-z0-9]/.test(slug) ? slug : undefined;
}

// The body of the one call, valid against capture_request.input.schema.json. Redacted once more on the way out, with
// today's token and environment. The key is the content's: the same message sent again applies once, however it comes about
// (a retry, a killed run, a resumed session ending on the same words).
export function captureBody(pending, config, own) {
  const { text } = redact(pending.text, { own });
  const body = { text: `${AGENTS[pending.agent].label} session, ${String(pending.at).slice(0, 10)}\n\n${text}`, source: AGENTS[pending.agent].source, tags: ["session", pending.agent] };
  const project = projectOf(config, pending.folders ?? []);
  if (project) body.project = project;
  body.idempotencyKey = `hook.${pending.agent}.${sha(JSON.stringify([pending.session, body.text, body.tags, body.project ?? null])).slice(0, 40)}`;
  return body;
}

// One POST. No redirect is followed, no proxy is consulted, no Origin is sent: the door refuses one, and a token goes to the
// one address a person named or nowhere. Never rejects; a failure is status 0 with the system's code, never the payload.
export function post(url, token, body, timeoutMs) {
  return new Promise((done) => {
    const payload = Buffer.from(JSON.stringify(body));
    const send = url.protocol === "https:" ? httpsRequest : httpRequest;
    let timer;
    const finish = (result) => {
      clearTimeout(timer);
      done(result);
    };
    const request = send(url, {
      method: "POST",
      agent: false,
      headers: { authorization: `Bearer ${token}`, "content-type": "application/json", "content-length": payload.length, accept: "application/json", "user-agent": "rhythm-hook" },
    });
    timer = setTimeout(() => request.destroy(Object.assign(new Error("timeout"), { code: "TIMEOUT" })), timeoutMs);
    request.on("response", (response) => {
      const chunks = [];
      let size = 0;
      response.on("data", (chunk) => {
        if (size < 65536) chunks.push(chunk);
        size += chunk.length;
      });
      response.on("end", () => {
        let answer;
        try {
          answer = JSON.parse(Buffer.concat(chunks).toString("utf8"));
        } catch {
          answer = undefined;
        }
        finish({ status: response.statusCode, answer });
      });
      response.on("error", (e) => finish({ status: 0, error: e.code ?? "failed" }));
    });
    request.on("error", (e) => finish({ status: 0, error: e.code ?? "failed" }));
    request.end(payload);
  });
}

// What an answer means for the message that was sent. Only a passing failure keeps it for another try: a route that is off,
// a refused token or a refused body will not change by waiting, and the person asked for nothing to be kept then.
export function fate(status) {
  if (status === 200) return "saved";
  if (status === 404) return "off";
  if (status === 401 || status === 403) return "refused";
  if (status === 400 || status === 405 || status === 409 || status === 413) return "refused";
  return "retry";
}

const SAY = {
  off: "POST /capture is off (memory.capture, or nothing is listening there as an engine): nothing was saved, and the message is not kept.",
  refused: "the engine refused this capture: nothing was saved, and the message is not kept. Run this hook's check command.",
  retry: "the engine could not be reached, is not ready or asked to wait: the message waits for a later session's end.",
  stranger: "something answered that is not the engine, such as a proxy or a sign-in page: the message waits for a later session's end.",
};

function remember(config, outcome, status, now) {
  try {
    writePrivate(join(config.dir, "hooks-last.json"), { at: new Date(now).toISOString(), outcome, status });
  } catch {
    // Only check reads it; a read-only folder costs nothing else.
  }
}

// A session ended: send its message if it has one, then a little of what older sessions left waiting. Answers what became
// of each, for tests; says so on stderr, which holds no words of any message and never the token.
export async function sendWaiting(agentName, event, config, env, { budgetMs, now = Date.now(), say = () => {} } = {}) {
  const deadline = Date.now() + (budgetMs ?? AGENTS[agentName].budgetMs);
  const own = pendingFile(config, agentName, event.session);
  const stale = [];
  let mine = false;
  for (const { file, modified } of waiting(config)) {
    if (now - modified > EXPIRED_MS) rmSync(file, { force: true });
    else if (file === own) mine = true;
    else if (now - modified > STALE_MS) stale.push(file);
  }
  const queue = [...(mine ? [own] : []), ...stale.slice(0, STALE_PER_RUN)];
  if (!queue.length) return [];
  const token = readToken(config);
  if (!token) {
    say("the capture token file cannot be read: nothing was sent.");
    return ["no_token"];
  }
  const secrets = ownSecrets(config, env);
  const results = [];
  for (const file of queue) {
    const left = deadline - Date.now();
    if (left < 100) break;
    const pending = readPending(file);
    if (!pending) continue;
    if (!folderAllowed(pending.folders ?? [], config)) {
      rmSync(file, { force: true });
      results.push("folder");
      continue;
    }
    const answer = await post(config.url, token, captureBody(pending, config, secrets), left);
    // Anything on the way can answer 200. Only the engine's own answer names the memory, so only that one is a save.
    const stranger = answer.status === 200 && typeof answer.answer?.id !== "string";
    const outcome = stranger ? "retry" : fate(answer.status);
    results.push(outcome);
    remember(config, outcome, answer.status, now);
    if (outcome !== "retry") rmSync(file, { force: true });
    if (outcome !== "saved") say(`${answer.status || answer.error}: ${SAY[stranger ? "stranger" : outcome]}`);
    if (outcome === "retry") break;
  }
  return results;
}

// What check says, line by line: the state of each thing the hook needs, never the token or a message. Its one call has an
// empty body, which the endpoint refuses (400) past the door without saving anything, so it proves the route and the token.
export async function check(env, write) {
  const config = loadConfig(env);
  write(`settings: ${config.path}`);
  if (!config.on) {
    write(`off: ${config.why}. Nothing is read, kept or sent.`);
    return 1;
  }
  write(`capture on, to ${config.url}`);
  write(`folders: ${config.folders.join(", ")}`);
  write(`waiting to be sent: ${waiting(config).length} (${pendingDir(config)})`);
  try {
    write(`last send: ${readFileSync(join(config.dir, "hooks-last.json"), "utf8").trim()}`);
  } catch {
    write("last send: none yet");
  }
  const token = readToken(config);
  if (!token) {
    write(`token: cannot read one from ${config.tokenFile}`);
    return 1;
  }
  write("token: read from its file");
  if (process.platform !== "win32" && (statSync(config.tokenFile).mode & 0o077) !== 0) write("token: its file can be read by others; make it mode 0600");
  const answer = await post(config.url, token, {}, 5000);
  const code = answer.answer?.result?.structuredContent?.error?.code;
  if (answer.status === 400 && code === "invalid_arguments") {
    write("endpoint: on, and it accepts this token (nothing was saved by this check)");
    return 0;
  }
  if (answer.status === 404) write("endpoint: off. Set memory.capture and memory.tools on in rhythm.yaml, then restart the engine.");
  else if (answer.status === 401) write("endpoint: this token is unknown, revoked, or bound to someone suspended. Make one with rhythm capture connect <name>.");
  else if (answer.status === 403) write(`endpoint: refused (${code ?? "forbidden"}). This is not a capture token, or this address is not in engine.allowedHosts.`);
  else if (answer.status === 0) write(`endpoint: not reached (${answer.error})`);
  else write(`endpoint: answered ${answer.status}`);
  return 1;
}
