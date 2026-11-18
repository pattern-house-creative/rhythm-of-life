import type { DashboardData } from "./dashboards";
import { stamp } from "./health";

// Data-feed rows are ordered oldest first. A daily aggregate carries a
// source calendar date, never an invented observation at midnight.
export function feedObservationTimes(read: DashboardData): string {
  const namesByTime = new Map<string, string[]>();
  for (const [name, feed] of Object.entries(read.feeds)) {
    if (
      feed.kind !== "data" ||
      feed.status !== "ok" ||
      !Array.isArray(feed.rows)
    )
      continue;
    const latest: unknown = feed.rows.at(-1);
    if (!latest || typeof latest !== "object") continue;
    const when =
      "at" in latest &&
      typeof latest.at === "string" &&
      Number.isFinite(Date.parse(latest.at))
        ? stamp(latest.at)
        : "date" in latest &&
            typeof latest.date === "string" &&
            /^\d{4}-\d{2}-\d{2}$/.test(latest.date)
          ? latest.date
          : "time unavailable";
    const names = namesByTime.get(when) ?? [];
    names.push(name);
    namesByTime.set(when, names);
  }
  if (namesByTime.size === 1)
    return `Latest observation ${namesByTime.keys().next().value}`;
  return [...namesByTime]
    .map(([when, names]) => `${names.join(", ")} · ${when}`)
    .join(" · ");
}
