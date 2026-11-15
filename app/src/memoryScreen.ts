import type { MutableRefObject } from "react";
import { instanceTimeZone, personName } from "./instance";
import type {
  MemoryCascade,
  MemoryReply,
  MemorySettingsRead,
} from "./memories";

// What every screen of the Memories view shares (SB-P6-01; MemoryView.tsx holds the sections): how it reaches the engine, how
// it says what happened, and the words it says when and whose in. A screen is one more entry in MemoryView's `sections`, a
// component of its own file given these tools; everything it does goes through the engine as the app (app_memories, a person's
// channel), and nothing is kept in the browser beyond what is on screen.

export type Notice = {
  text: string;
  failed?: boolean;
  busy?: boolean;
  action?: { label: string; run: () => void };
};

export type MemoryAnswer<T> = MemoryReply<T> & { available: true; ok: true };

export interface MemoryTools {
  // One op as the app: a refusal shows its reason, an install where the view went away says so, and either answers null.
  call: <T>(
    request: Record<string, unknown>,
  ) => Promise<MemoryAnswer<T> | null>;
  // A read a screen can go on without (an older engine's, say): if it doesn't come, nothing shows and it answers null.
  quiet: <T>(
    request: Record<string, unknown>,
  ) => Promise<MemoryAnswer<T> | null>;
  say: (notice: Notice | null) => void;
  // A memory opened whole, where it can be edited; Back returns here.
  open: (id: string) => void;
  // Something the map shows changed: it follows at its next rebuild.
  saved: () => void;
  // Something left a review queue: the strip's counts are read again, each the length of its list.
  waiting: () => void;
  // Whether the engine takes a person's share (memory.share on, and memory scopes on).
  sharing: boolean;
  // The install's memory settings, once read; null until then, and from an engine that doesn't say.
  settings: MemorySettingsRead | null;
  // Escape steps back a layer here first: a screen with a confirmation or an open form sets this while it has one.
  layer: MutableRefObject<(() => boolean) | null>;
}

// A row's buttons have words every other row's have too ("Open", "Done"), so each row's are a group named for what it is about: a screen
// reader says which on the way in, and the buttons keep their short words.
export const rowGroup = (about: string) =>
  ({ role: "group", "aria-label": about.slice(0, 120) }) as const;

// One of the brief's lists, as the explorer may be asked to open the Memories view at it: the list a brief count is the length of.
// (Home's card that opened these is gone, D-257.)
export type BriefTarget =
  | "due"
  | "decisions"
  | "youOwe"
  | "owedToYou"
  | "stale"
  | "held"
  | "changes"
  | "contradictions"
  | "duplicates"
  | "insights";

export const when = (iso: string) => {
  const at = new Date(iso);
  if (!Number.isFinite(at.getTime())) return "";
  try {
    return new Intl.DateTimeFormat("en-GB", {
      dateStyle: "medium",
      timeStyle: "short",
      timeZone: instanceTimeZone(),
    }).format(at);
  } catch {
    return iso.slice(0, 16).replace("T", " ");
  }
};

// The day `days` from today, as the install's clocks read it (YYYY-MM-DD): what a snooze names, at 09:00 there, as a review date is.
// zone: the one the engine's page named, which it reads that day on; the app's own config only until there is a page.
export function installDay(
  days: number,
  zone: string = instanceTimeZone(),
  now = new Date(),
) {
  let today: string;
  try {
    today = new Intl.DateTimeFormat("en-CA", {
      timeZone: zone,
      year: "numeric",
      month: "2-digit",
      day: "2-digit",
    }).format(now);
  } catch {
    today = now.toISOString().slice(0, 10);
  }
  const [y, m, d] = today.split("-").map(Number);
  return new Date(Date.UTC(y, m - 1, d + days)).toISOString().slice(0, 10);
}

// The day an instant falls on, on the install's clocks, in words.
export const dayOf = (iso: string) => {
  const at = new Date(iso);
  if (!Number.isFinite(at.getTime())) return "";
  try {
    return new Intl.DateTimeFormat("en-GB", {
      dateStyle: "long",
      timeZone: instanceTimeZone(),
    }).format(at);
  } catch {
    return iso.slice(0, 10);
  }
};

export const scopeName = (m: { scope: string; owner: string }) =>
  m.scope === "shared" ? "Family" : `${personName(m.owner)}'s own`;

// Whose capsule, and which: the scope's core one, or its project's.
export const whose = (m: { scope: string; owner: string }) =>
  m.scope === "shared" ? "Family's" : `${personName(m.owner)}'s own`;

// Several cascades in one line's numbers: what each change's retraction did, added up.
export function cascades(all: (MemoryCascade | null)[]): MemoryCascade | null {
  const present = all.filter((c): c is MemoryCascade => c !== null);
  if (!present.length) return null;
  return {
    reopened: present.flatMap((c) => c.reopened),
    reclosed: present.flatMap((c) => c.reclosed),
    flagged: present.flatMap((c) => c.flagged),
    unflagged: present.flatMap((c) => c.unflagged),
  };
}

// How many of something, in words.
export const counted = (n: number, one: string, many: string) =>
  n === 1 ? `1 ${one}` : `${n} ${many}`;

// A memory's kind and how long it holds (run 22), in words: the store's own words are episodic and semantic, durable, state and volatile.
// How long it holds decides when it is due for a check (the stale queue's 14 and 90 days).
export const kindNames: Record<string, string> = {
  episodic: "Something that happened",
  semantic: "A fact or an idea",
};
export const volatilityNames: Record<string, string> = {
  durable: "It lasts",
  state: "A state, checked after 90 days",
  volatile: "It changes often, checked after 14 days",
};
// The same, short, for a line under a memory.
export const kindWords: Record<string, string> = {
  episodic: "something that happened",
  semantic: "a fact or an idea",
};
export const volatilityWords: Record<string, string> = {
  durable: "lasts",
  state: "a state",
  volatile: "changes often",
};
