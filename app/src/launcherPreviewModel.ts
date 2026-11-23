import type { Dashboard, DashboardData } from "./dashboards";

export interface PreviewSummary {
  kind: "generated" | "fallback";
  label: string;
  value: string;
  detail: string;
  points?: number[];
  partial: boolean;
}
const record = (v: unknown): v is Record<string, unknown> =>
  !!v && typeof v === "object" && !Array.isArray(v);
const short = (v: unknown, max = 100) =>
  typeof v === "string" ? v.replace(/\s+/g, " ").trim().slice(0, max) : "";
const number = (v: unknown): v is number =>
  typeof v === "number" && Number.isFinite(v);
const formatted = (v: number) =>
  new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 }).format(v);

// Explicit opt-in stored in the existing versioned HTML; no new database field.
// A leading, canonical marker avoids parsing untrusted HTML in the parent
// document at all. The author runs only inside the existing nested sandbox.
export function hasAuthoredLauncherPreview(html: string): boolean {
  return /^\s*<meta\s+name=["']rhythm-launcher-preview["']\s+content=["']1["']\s*\/?>/i.test(
    html,
  );
}

// Only known feed shapes have semantics. Never guess that an arbitrary numeric
// property means progress, a balance, a target or a health recommendation.
export function launcherSummary(
  dashboard: Dashboard,
  data: DashboardData,
): PreviewSummary {
  const available = dashboard.feeds
    .map((f) => data.feeds[f.name])
    .filter((f) => f?.status === "ok");
  const partial =
    available.length !== dashboard.feeds.length ||
    available.some((f) => f.truncated === true || number(f.previewOf));
  for (const declaration of dashboard.feeds) {
    const f = data.feeds[declaration.name];
    if (!f || f.status !== "ok" || f.kind !== declaration.kind) continue;
    const source = short(f.star?.title) || declaration.name.replace(/_/g, " ");
    if (
      f.kind === "data" &&
      Array.isArray(f.rows) &&
      (f.by === "reading" || f.by === "day")
    ) {
      const field = f.by === "day" ? "mean" : "value";
      const rows = f.rows
        .filter(record)
        .filter(
          (r) =>
            short(r.metric) &&
            number(r[field]) &&
            /^\d{4}-\d{2}-\d{2}$/.test(short(r.date)),
        );
      const first = rows[0];
      if (!first) continue;
      const series = rows
        .filter((r) => r.metric === first.metric && r.unit === first.unit)
        .sort((a, b) =>
          (short(a.date) + short(a.at)).localeCompare(
            short(b.date) + short(b.at),
          ),
        );
      const latest = series.at(-1)!;
      const metadata = Array.isArray(f.metrics)
        ? f.metrics
            .filter(record)
            .find((m) => m.metric === latest.metric && m.unit === latest.unit)
        : undefined;
      return {
        kind: "generated",
        label: short(metadata?.label) || short(latest.metric),
        value: `${formatted(latest[field] as number)}${short(latest.unit, 24) ? ` ${short(latest.unit, 24)}` : ""}`,
        detail: `${f.by === "day" ? "Daily mean" : "Latest reading"} · ${short(latest.date)}`,
        points: series.slice(-12).map((r) => r[field] as number),
        partial,
      };
    }
    if (f.kind === "log" && Array.isArray(f.entries)) {
      const entries = f.entries.filter(record);
      const latest = entries.at(-1);
      const note = short(latest?.note, 100);
      return {
        kind: "generated",
        label: short(f.log) || source,
        value: `${entries.length}${f.truncated === true || number(f.previewOf) ? "+" : ""} entries`,
        detail:
          note ||
          (entries.length
            ? `Latest entry · ${short(latest?.date) || "date unavailable"}`
            : "No entries in this feed window"),
        partial,
      };
    }
    if (f.kind === "links" && Array.isArray(f.stars)) {
      const stars = f.stars.filter(record);
      return {
        kind: "generated",
        label: "Connected stars",
        value: `${stars.length}${f.truncated === true ? "+" : ""}`,
        detail:
          stars
            .slice(0, 2)
            .map((s) => short(s.title, 40))
            .filter(Boolean)
            .join(" · ") || "No connected stars returned",
        partial,
      };
    }
    if (f.kind === "contents" && typeof f.text === "string" && f.text.trim()) {
      return {
        kind: "generated",
        label: "From your source",
        value: short(f.title, 80) || source,
        detail: short(f.text.replace(/^#{1,6}\s+/gm, ""), 150),
        partial,
      };
    }
  }
  // A dashboard that declares no feeds has nothing unavailable; one whose declared feeds all
  // failed says so.
  return {
    kind: "fallback",
    label:
      available.length || !dashboard.feeds.length
        ? "Dashboard"
        : "Dashboard data unavailable",
    value: "Open for details",
    detail:
      short(dashboard.description, 140) ||
      "No supported preview data is available.",
    partial,
  };
}
