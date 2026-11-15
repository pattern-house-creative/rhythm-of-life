import { readFileSync, statSync } from "node:fs";
import { parse } from "yaml";
import { providerId } from "../src/validation.js";
import { viewersAccepted } from "./viewerGate.js";

// The part of rhythm.yaml the app's server uses (docs/configuration.md). Nothing in it is
// secret. The file is read at start and again whenever it changes; a file that turns invalid
// while running keeps the last good settings.
export interface Person {
  id: string;
  name: string;
}
export interface Settings {
  name: string;
  timezone: string;
  people: Person[];
  // Empty: no engine, the app runs in demo mode.
  engineUrl: string;
  // After this instant the server stops calling the engine (credential_expired).
  credentialExpiry: string | null;
  // The host names the Node server answers to besides localhost, app and IP addresses, lower
  // case and without a port: app.allowedHosts, the host of app.publicUrl, engine.allowedHosts and
  // adapters.cloudflare.hostname. Any other Host is refused, which stops DNS rebinding.
  hosts: string[];
  // app.viewers: whether someone at the app may open a viewer session (docs/security.md, Viewer
  // sessions). Off unless the file says on, which this version refuses (viewerGate.ts).
  viewers: boolean;
}

// Without a settings file the app runs in demo mode with fictional people.
export const defaultSettings: Settings = Object.freeze({
  name: "Rhythm of Life",
  timezone: "UTC",
  people: [
    { id: "alex", name: "Alex" },
    { id: "sam", name: "Sam" },
  ],
  engineUrl: "",
  credentialExpiry: null,
  hosts: [],
  viewers: false,
});

const defaultPath = "/etc/rhythm/rhythm.yaml";
const personId = /^[a-z][a-z0-9-]{0,31}$/;
const zonePattern = /^(UTC|[A-Za-z_]+(\/[A-Za-z0-9_+-]+){1,2})$/;
const urlPattern = /^(|https?:\/\/[^/?#@\s]+\/?)$/;

export class SettingsError extends Error {
  readonly issues: string[];
  constructor(issues: string[]) {
    super(`rhythm.yaml is not valid: ${issues.join("; ")}`);
    this.issues = issues;
  }
}

export function validTimeZone(zone: unknown): zone is string {
  if (typeof zone !== "string" || !zonePattern.test(zone)) return false;
  try {
    new Intl.DateTimeFormat("en-US", { timeZone: zone });
    return true;
  } catch {
    return false;
  }
}

const record = (v: unknown): v is Record<string, unknown> =>
  !!v && typeof v === "object" && !Array.isArray(v);

// A Host header's or a setting's host name: lower case, without a port or a final dot; IPv6 in
// brackets. Null for anything that is not a host.
export function hostName(value: unknown): string | null {
  if (typeof value !== "string" || !value || value.length > 300) return null;
  if (!/^[A-Za-z0-9.:[\]-]+$/.test(value)) return null;
  try {
    const host = new URL(`http://${value}`).hostname.replace(/\.$/, "");
    return host || null;
  } catch {
    return null;
  }
}
const urlHost = (value: unknown) => {
  try {
    return typeof value === "string" ? hostName(new URL(value).host) : null;
  } catch {
    return null;
  }
};

// Reads only the keys the app needs; the engine validates the rest of the file against
// contracts/rhythm.schema.json. Issues name the key, never its value.
export function parseSettings(text: string): Settings {
  let doc: unknown;
  try {
    doc = parse(text, { maxAliasCount: 0 });
  } catch {
    throw new SettingsError(["not readable as YAML"]);
  }
  if (doc === null || doc === undefined) return defaultSettings;
  if (!record(doc)) throw new SettingsError(["the file is not a mapping"]);
  const issues: string[] = [];
  const instance = doc.instance === undefined ? {} : doc.instance;
  const engine = doc.engine === undefined ? {} : doc.engine;
  const app = doc.app === undefined ? {} : doc.app;
  if (!record(instance)) issues.push("instance");
  if (!record(engine)) issues.push("engine");
  if (!record(app)) issues.push("app");
  const name = record(instance) ? (instance.name ?? defaultSettings.name) : "";
  if (typeof name !== "string" || !name.trim() || name.length > 60)
    issues.push("instance.name");
  if (!validTimeZone(doc.timezone)) issues.push("timezone");
  const people: Person[] = [];
  if (
    !Array.isArray(doc.people) ||
    doc.people.length < 1 ||
    doc.people.length > 20
  )
    issues.push("people");
  else
    doc.people.forEach((p: unknown, i) => {
      if (
        !record(p) ||
        typeof p.id !== "string" ||
        !personId.test(p.id) ||
        p.id === "household" ||
        typeof p.name !== "string" ||
        !p.name.trim() ||
        p.name.length > 60 ||
        people.some((q) => q.id === p.id)
      )
        issues.push(`people[${i}]`);
      else people.push({ id: p.id, name: p.name.trim() });
    });
  const url = record(engine) ? (engine.url ?? "http://engine:8080") : "";
  if (url !== null && (typeof url !== "string" || !urlPattern.test(url)))
    issues.push("engine.url");
  const expiry = record(app) ? (app.credentialExpiry ?? null) : null;
  if (
    expiry !== null &&
    !(
      (typeof expiry === "string" &&
        /(?:Z|[+-]\d\d:\d\d)$/.test(expiry) &&
        Number.isFinite(Date.parse(expiry))) ||
      expiry instanceof Date
    )
  )
    issues.push("app.credentialExpiry");
  // The names this install answers to. app.allowedHosts is the app's own and is checked here; the
  // others belong to keys the engine checks or only prints, so a value that is no host is skipped.
  const allowed = record(app) ? app.allowedHosts : undefined;
  if (
    allowed !== undefined &&
    (!Array.isArray(allowed) ||
      allowed.length < 1 ||
      allowed.length > 20 ||
      allowed.some(
        (h) => typeof h !== "string" || h.length > 253 || !hostName(h),
      ))
  )
    issues.push("app.allowedHosts");
  const viewers = record(app) ? (app.viewers ?? "off") : "off";
  if (viewers !== "off" && viewers !== "on") issues.push("app.viewers");
  else if (viewers === "on" && !viewersAccepted)
    issues.push(
      "app.viewers (on is refused by this version: viewer sessions wait for the owner's approval)",
    );
  const adapters = record(doc.adapters) ? doc.adapters : {};
  const hosts = [
    ...(Array.isArray(allowed) ? allowed : []).map(hostName),
    record(app) ? urlHost(app.publicUrl) : null,
    ...(record(engine) && Array.isArray(engine.allowedHosts)
      ? engine.allowedHosts
      : []
    ).map(hostName),
    record(adapters.cloudflare) ? hostName(adapters.cloudflare.hostname) : null,
  ].filter((h): h is string => !!h);
  if (issues.length) throw new SettingsError(issues);
  return {
    name: (name as string).trim(),
    timezone: doc.timezone as string,
    people,
    engineUrl: ((url as string | null) ?? "").replace(/\/$/, ""),
    credentialExpiry:
      expiry === null
        ? null
        : expiry instanceof Date
          ? expiry.toISOString()
          : (expiry as string),
    hosts: [...new Set(hosts)],
    viewers: viewers === "on",
  };
}

let cache: { key: string; settings: Settings } | null = null;

// RHYTHM_CONFIG names the file (default /etc/rhythm/rhythm.yaml, where compose mounts it).
// RHYTHM_CONFIG_YAML holds the file's text instead, for hosts that cannot mount a file
// (deploy/vercel/). No file at all means the demo defaults.
export function settings(env: NodeJS.ProcessEnv = process.env): Settings {
  const inline = env.RHYTHM_CONFIG_YAML;
  if (inline) {
    const key = `inline:${inline}`;
    if (cache?.key !== key) cache = { key, settings: parseSettings(inline) };
    return cache.settings;
  }
  const path = env.RHYTHM_CONFIG || defaultPath;
  let key: string;
  try {
    const stat = statSync(path);
    if (!stat.isFile()) return defaultSettings;
    key = `${path}:${stat.mtimeMs}:${stat.size}`;
  } catch {
    return defaultSettings;
  }
  if (cache?.key === key) return cache.settings;
  try {
    cache = { key, settings: parseSettings(readFileSync(path, "utf8")) };
  } catch (error) {
    // A file that was valid before stays in force; a first bad read is the caller's error.
    if (cache && !cache.key.startsWith("inline:")) {
      console.error(
        `rhythm.yaml changed and is not valid; keeping the previous settings (${error instanceof SettingsError ? error.issues.join(", ") : "unreadable"})`,
      );
      cache = { key, settings: cache.settings };
      return cache.settings;
    }
    throw error;
  }
  return cache.settings;
}

// The provider id the engine names its own stars under (data stars, saved documents, dashboards),
// which starts each one's graph id: RHYTHM_CATALOG_PROVIDER, read as the engine reads it (unset or
// empty is "rhythm"). Only an install that kept an older engine's ids sets it, for the engine and
// the app alike (docs/dev/migration-compat.md). A value that is no provider id is an error naming
// the variable, never its value.
export function catalogProvider(env: NodeJS.ProcessEnv = process.env) {
  const value = env.RHYTHM_CATALOG_PROVIDER;
  if (!value) return "rhythm";
  if (!providerId(value))
    throw new Error(
      "RHYTHM_CATALOG_PROVIDER is not a provider id (a lower-case letter, then lower-case letters, digits or hyphens)",
    );
  return value;
}

// For tests: forget what was read.
export function resetSettings() {
  cache = null;
}
