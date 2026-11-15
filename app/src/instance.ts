import { providerId } from "./validation.js";

// This install: its name, the people in it and its time zone, from the app's server
// (GET /api/instance, read from rhythm.yaml; contracts/README.md section 1.3), and the provider id
// of the engine's own stars. Nothing about a person, a household or a time zone is hardcoded
// anywhere else in the app.
export interface Person {
  id: string;
  name: string;
}
export interface Instance {
  name: string;
  people: Person[];
  timezone: string;
  mode: "demo" | "live";
  // The provider id the engine names its own stars under (data stars, saved documents,
  // dashboards): "rhythm", or the older engine's id an install kept (RHYTHM_CATALOG_PROVIDER).
  catalogProvider: string;
}

// The fictional person the demo's health readings and logs belong to.
export const demoSubject = "alex";
// Used until the server answers: the demo's own settings.
export const demoInstance: Instance = Object.freeze({
  name: "Rhythm of Life",
  people: [
    { id: demoSubject, name: "Alex" },
    { id: "sam", name: "Sam" },
  ],
  timezone: "UTC",
  mode: "demo",
  catalogProvider: "rhythm",
});

let current: Instance = demoInstance;
export const instance = () => current;
export function setInstance(next: Instance) {
  current = next;
}

const validZone = (zone: string) => {
  try {
    new Intl.DateTimeFormat("en-US", { timeZone: zone });
    return true;
  } catch {
    return false;
  }
};
const record = (v: unknown): v is Record<string, unknown> =>
  !!v && typeof v === "object" && !Array.isArray(v);

// The server's answer, checked field by field, or null.
export function cleanInstance(value: unknown): Instance | null {
  if (
    !record(value) ||
    typeof value.name !== "string" ||
    !value.name.trim() ||
    value.name.length > 60 ||
    typeof value.timezone !== "string" ||
    value.timezone.length > 64 ||
    !validZone(value.timezone) ||
    (value.mode !== "demo" && value.mode !== "live") ||
    !Array.isArray(value.people) ||
    value.people.length > 20 ||
    (value.catalogProvider !== undefined && !providerId(value.catalogProvider))
  )
    return null;
  const people: Person[] = [];
  for (const p of value.people) {
    if (
      !record(p) ||
      typeof p.id !== "string" ||
      !/^[a-z][a-z0-9-]{0,31}$/.test(p.id) ||
      typeof p.name !== "string" ||
      !p.name.trim() ||
      p.name.length > 60
    )
      return null;
    people.push({ id: p.id, name: p.name });
  }
  return {
    name: value.name,
    people,
    timezone: value.timezone,
    mode: value.mode,
    // A server that names none uses the engine's default.
    catalogProvider: (value.catalogProvider as string | undefined) ?? "rhythm",
  };
}

// Reads the install's settings: true once they are read. Any failure keeps the settings in
// use (the demo's, at start, which are always safe to show) and answers false.
export async function loadInstance(timeout = 4000): Promise<boolean> {
  try {
    const response = await fetch("/api/instance", {
      credentials: "same-origin",
      cache: "no-store",
      redirect: "error",
      signal: AbortSignal.timeout(timeout),
    });
    if (
      response.ok &&
      response.headers.get("content-type")?.includes("application/json")
    ) {
      const next = cleanInstance(await response.json());
      if (next) {
        setInstance(next);
        return true;
      }
    }
  } catch {
    // Unreachable or refused: the settings in use stay.
  }
  return false;
}

// One failed read at start (a weak connection, a front end starting cold) must not leave a
// live install on the demo's time zone and person for the rest of the visit. Until the
// settings are read they are read again: after `first` ms, then twice as long each time up
// to `longest`, and at once when the network or the page comes back. `changed` runs once
// they are read, if they differ from the ones in use, so the app can draw again with them.
export function keepReading(
  changed: () => void,
  { first = 5000, longest = 300_000, timeout = 10_000 } = {},
) {
  let delay = first,
    timer: ReturnType<typeof setTimeout> | undefined,
    reading = false,
    done = false;
  const stop = () => {
    done = true;
    clearTimeout(timer);
    window.removeEventListener("online", attempt);
    window.removeEventListener("pageshow", attempt);
    document.removeEventListener("visibilitychange", attempt);
  };
  async function attempt() {
    if (done || reading || document.hidden || !navigator.onLine) return;
    clearTimeout(timer);
    reading = true;
    const before = JSON.stringify(current);
    const read = await loadInstance(timeout);
    reading = false;
    if (done) return;
    if (read) {
      stop();
      if (JSON.stringify(current) !== before) changed();
      return;
    }
    timer = setTimeout(attempt, delay);
    delay = Math.min(longest, delay * 2);
  }
  window.addEventListener("online", attempt);
  window.addEventListener("pageshow", attempt);
  document.addEventListener("visibilitychange", attempt);
  timer = setTimeout(attempt, delay);
  delay = Math.min(longest, delay * 2);
  return stop;
}

// The time zone "today", day boundaries and charts follow.
export const instanceTimeZone = () => current.timezone;
// The provider id that starts the graph id of each star the engine keeps itself.
export const catalogProvider = () => current.catalogProvider;
// Whose health readings the panels show: the demo's fictional person, or the first person
// in the settings.
export const healthSubject = () =>
  current.mode === "demo"
    ? demoSubject
    : (current.people[0]?.id ?? "household");
// A person's display name; "household" and unknown ids are spelled out from the id.
export function personName(id: string) {
  const person = current.people.find((p) => p.id === id);
  if (person) return person.name;
  return id ? `${id[0].toUpperCase()}${id.slice(1)}` : id;
}
