import type { DashboardData } from "./dashboards";
import { stamp } from "./health";

// When a dashboard's measurements were recorded, as its freshness line says it beside when the
// data was read (docs/dev/design-system.md, the dashboard frame). Data-feed rows are ordered
// oldest first; each data feed's latest row says when it was recorded (`at`), or, for a daily
// aggregate, its source day (`date`), never an invented time at midnight. Feeds recorded at the
// same moment are named together. Other feeds (contents, links, logs, memories) carry no
// measurement time and are left out.
export interface FeedObservation {
  names: string[];
  // The recorded moment (ISO), or the source day (YYYY-MM-DD); neither when the row has none.
  at?: string;
  day?: string;
}
export function feedObservations(read: DashboardData): FeedObservation[] {
  const byTime = new Map<string, FeedObservation>();
  for (const [name, feed] of Object.entries(read.feeds)) {
    if (
      feed.kind !== "data" ||
      feed.status !== "ok" ||
      !Array.isArray(feed.rows)
    )
      continue;
    const latest: unknown = feed.rows.at(-1);
    if (!latest || typeof latest !== "object") continue;
    const at =
      "at" in latest &&
      typeof latest.at === "string" &&
      Number.isFinite(Date.parse(latest.at))
        ? latest.at
        : undefined;
    const day =
      !at &&
      "date" in latest &&
      typeof latest.date === "string" &&
      /^\d{4}-\d{2}-\d{2}$/.test(latest.date)
        ? latest.date
        : undefined;
    // Grouped as shown, so two rows recorded in the same minute share a phrase.
    const key = at ? `at:${stamp(at)}` : day ? `day:${day}` : "unknown";
    const group = byTime.get(key) ?? { names: [], at, day };
    group.names.push(name);
    byTime.set(key, group);
  }
  return [...byTime.values()];
}

// One phrase of the freshness line: its words, then the date and time that stay on one line
// (`time`, ISO or a day, and how it reads), then what closes it before the next.
export interface FreshnessPhrase {
  words: string;
  time?: { dateTime: string; text: string };
  after?: string;
}
// The newest of several: a recorded moment, or a source day when that day is later.
const newest = (groups: FeedObservation[]) =>
  groups.reduce((a, b) => (moment(b) > moment(a) ? b : a));
const moment = ({ at, day }: FeedObservation) =>
  at
    ? Date.parse(at)
    : day
      ? Date.parse(`${day}T00:00:00`)
      : Number.NEGATIVE_INFINITY;

// "Recorded …", "Source day …" or "Recording time unknown". Unnamed (a card, full screen: one
// phrase, so the line is no longer than it was) a dashboard whose feeds were all recorded at
// one time says it once, and one whose feeds differ says the newest ("Latest recorded …"); the
// reader has the room to name each feed with its time (`named`).
export function recordedPhrases(
  read: DashboardData,
  named = false,
): FreshnessPhrase[] {
  const groups = feedObservations(read);
  if (!named && groups.length > 1) {
    const { at, day } = newest(groups);
    return [
      at
        ? {
            words: "Latest recorded ",
            time: { dateTime: at, text: stamp(at) },
          }
        : { words: "Latest source day ", time: { dateTime: day!, text: day! } },
    ];
  }
  return groups.map(({ names, at, day }) => {
    const who = named ? `${names.join(", ")} ` : "";
    if (at)
      return {
        words: named ? `${who}recorded ` : "Recorded ",
        time: { dateTime: at, text: stamp(at) },
      };
    if (day)
      return {
        words: named ? `${who}· source day ` : "Source day ",
        time: { dateTime: day, text: day },
      };
    return {
      words: named ? `${who}· recording time unknown` : "Recording time unknown",
    };
  });
}

// The recorded phrases as one sentence: groups apart by " ·", a full stop at the end.
export const recordedSentence = (read: DashboardData, named = false) =>
  recordedPhrases(read, named).map((p, i, all) => ({
    ...p,
    after: i < all.length - 1 ? " ·" : ".",
  }));

// The freshness line of a read: when its measurements were recorded, then when the dashboard's
// data was read (the batch's asOf): "Recorded Nov 21, 4:05 AM UTC. Data read Nov 21, 4:08 AM UTC".
// After a failed refresh (`kept`) the last read is what is still shown, and the line says so.
export function freshnessPhrases(
  read: DashboardData,
  named = false,
  kept = false,
): FreshnessPhrase[] {
  return [
    ...recordedSentence(read, named),
    {
      words: kept ? "Showing data read " : "Data read ",
      time: { dateTime: read.asOf, text: stamp(read.asOf) },
    },
  ];
}

// Phrases in plain words.
export const phrasesText = (phrases: FreshnessPhrase[]) =>
  phrases
    .map((p) => `${p.words}${p.time?.text ?? ""}${p.after ?? ""}`)
    .join(" ");

// When a read's measurements were recorded, in plain words.
export function feedObservationTimes(
  read: DashboardData,
  named = false,
): string {
  return phrasesText(
    recordedPhrases(read, named).map((p, i, all) => ({
      ...p,
      after: i < all.length - 1 ? " ·" : "",
    })),
  );
}
