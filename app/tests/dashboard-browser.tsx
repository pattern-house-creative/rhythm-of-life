// Local synthetic contract harness. Excluded from production entrypoints.
import { createRoot } from "react-dom/client";
import App from "../src/App";
import { unavailableHealth } from "../server/healthState";
import { mapAdditionalHealth } from "../server/additionalHealth";
import "../src/index.css";
let scenario = "mixed";
const counts = { latest: 0, history: 0, garmin: 0, graph: 0 };
window.fetch = async (input) => {
  const url = String(input),
    now = new Date().toISOString();
  const today = new Intl.DateTimeFormat("en-CA", {
    timeZone: "Europe/London",
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
  }).format(new Date());
  const startDate = new Date(Date.parse(today) - 13 * 86400000)
      .toISOString()
      .slice(0, 10),
    endDate = new Date(Date.parse(today) + 86400000).toISOString().slice(0, 10);
  const base = {
    subject: "alex",
    selection: "garmin",
    today,
    startDate,
    endDate,
    lastSyncedAt: new Date(Date.now() - 3600000).toISOString(),
    complete: true,
    coverageComplete: false,
    rows: [],
  };
  const row = (metric: string, value: number, unit: string) => ({
    resource_id: "fictional-" + metric,
    subject_id: "alex",
    provider: "garmin",
    metric,
    value,
    unit,
    source_kind: "metric",
    timezone: "Europe/London",
    observed_at: null,
    source_updated_at: null,
    local_date: today,
    quality: "source-reported",
  });
  let data = unavailableHealth(now);
  data.healthMode = "live";
  const json = (value: unknown) =>
    new Response(JSON.stringify(value), {
      headers: { "content-type": "application/json" },
    });
  if (url.includes("/api/graph")) {
    counts.graph++;
    return json({
      nodes: [],
      edges: [],
      issues: [
        {
          provider: "rhythm",
          reason: "Graph unavailable; health is independent.",
        },
      ],
      nextCursor: null,
      cursors: {},
      completeness: "partial",
    });
  }
  if (scenario === "denied") return new Response("Denied", { status: 401 });
  if (url.includes("source=garmin")) {
    counts.garmin++;
    if (scenario !== "garmin-outage")
      data = mapAdditionalHealth(
        {
          ...base,
          rows:
            scenario === "empty"
              ? []
              : [
                  row("steps", 7432, "count"),
                  row("sleep_duration", 26700, "s"),
                  {
                    ...row("resting_heart_rate", 59, "beats/min"),
                    local_date: new Date(Date.parse(today) - 86400000)
                      .toISOString()
                      .slice(0, 10),
                  },
                  {
                    ...row("activity_duration", 2100, "s"),
                    resource_id: "fictional-run",
                    source_kind: "activity",
                    observed_at: new Date(Date.now() - 7200000).toISOString(),
                  },
                ],
        },
        "garmin",
        now,
      );
  } else if (url.includes("source=history")) {
    counts.history++;
    if (scenario !== "history-outage")
      data = mapAdditionalHealth(
        {
          ...base,
          selection: "history",
          start: new Date(Date.parse(now) - 86400000).toISOString(),
          end: now,
          rows:
            scenario === "empty"
              ? []
              : Array.from({ length: 35 }, (_, i) => ({
                  ...row(
                    "sensor_glucose",
                    110 + Math.round(20 * Math.sin(i / 6)),
                    "mg/dL",
                  ),
                  provider: "carelink",
                  source_kind: "carelink-live-day",
                  observed_at: new Date(
                    Date.now() - (i < 18 ? 42 - i : 36 - i) * 300000,
                  ).toISOString(),
                  quality:
                    "source-reported;time_basis=explicit-offset;original_unit=mg/dL",
                })),
        },
        "history",
        now,
      );
  } else {
    counts.latest++;
    if (scenario !== "latest-outage" && scenario !== "empty") {
      data.current = {
        ...data.current,
        value: 121,
        status: scenario === "stale" ? "stale" : "available",
        provenance: {
          ...data.current.provenance,
          recordId: "fictional-current",
          observedAt: new Date(
            Date.now() - (scenario === "stale" ? 40 : 5) * 60000,
          ).toISOString(),
        },
      };
    }
  }
  const marker = document.getElementById("request-counts");
  if (marker) marker.textContent = JSON.stringify(counts);
  return json(data);
};
// eslint-disable-next-line react/only-export-components
function Controls() {
  return (
    <aside
      style={{
        padding: 12,
        position: "relative",
        zIndex: 1000,
        background: "#15252b",
      }}
    >
      <strong>FICTIONAL TEST DATA ONLY</strong>
      {[
        "mixed",
        "garmin-outage",
        "history-outage",
        "latest-outage",
        "stale",
        "empty",
        "denied",
      ].map((s) => (
        <button
          key={s}
          onClick={() => {
            scenario = s;
            window.dispatchEvent(new Event("pagehide"));
            window.dispatchEvent(new Event("pageshow"));
          }}
        >
          {s}
        </button>
      ))}
      <output id="request-counts" />
      <button onClick={() => window.dispatchEvent(new Event("pagehide"))}>
        Hide fixture
      </button>
      <button onClick={() => window.dispatchEvent(new Event("pageshow"))}>
        Restore fixture
      </button>
    </aside>
  );
}
createRoot(document.getElementById("controls")!).render(<Controls />);
createRoot(document.getElementById("root")!).render(<App />);
