import type { FreshnessPhrase } from "./feedObservationTimes";
import "./FreshnessPhrases.css";

// A freshness line's phrases (feedObservationTimes.ts), each wrapping on its own, with a date,
// time and zone that never break across lines.
export function FreshnessPhrases({ phrases }: { phrases: FreshnessPhrase[] }) {
  return phrases.map((p, i) => (
    <span key={i}>
      {i > 0 && " "}
      <span className="freshness-phrase">
        {p.words}
        {p.time && <time dateTime={p.time.dateTime}>{p.time.text}</time>}
        {p.after}
      </span>
    </span>
  ));
}
