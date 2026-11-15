// Dev-only fixture UI, excluded from Vite's production entry points. No provider
// credentials, personal records, or provider requests. Tests the real App contract.
import { createRoot } from "react-dom/client";
import App from "../src/App";
import { unavailableHealth } from "../server/healthState";
import { mapLiveGlucose } from "../server/glucose";
import "../src/index.css";
const originalFetch = window.fetch;
let state = "fresh";
window.fetch = async (input, init) => {
  if (input !== "/api/observatory") return originalFetch(input, init);
  if (state === "denied") return new Response("Denied", { status: 401 });
  const now = new Date().toISOString();
  const data = unavailableHealth(now);
  data.current = mapLiveGlucose(
    state === "missing"
      ? []
      : [
          {
            subject_id: "fictional-person",
            provider: "carelink",
            metric: "sensor_glucose",
            source_kind: "carelink-live-day",
            unit: "mg/dL",
            value: 121,
            resource_id: "fictional-resource",
            sample_key: "fixture-sample",
            quality: "source_timezone=UTC",
            observed_at: new Date(
              Date.now() - (state === "stale" ? 40 : 5) * 60000,
            ).toISOString(),
            total_samples: 1,
          },
        ],
    "fictional-person",
    now,
  );
  data.sources = data.sources.map((source) =>
    source.id === "glucose"
      ? {
          ...source,
          connected: true,
          sampleAt: data.current.provenance.observedAt,
          status: state === "stale" ? "stale" : "available",
        }
      : source,
  );
  return new Response(JSON.stringify(data), {
    headers: { "content-type": "application/json" },
  });
};
// eslint-disable-next-line react/only-export-components
function Controls() {
  return (
    <aside
      style={{
        position: "relative",
        zIndex: 1000,
        background: "#15252b",
        padding: 12,
      }}
    >
      <strong>SYNTHETIC TEST HARNESS · all values fictional </strong>
      {["fresh", "stale", "missing", "denied"].map((value) => (
        <button
          key={value}
          onClick={() => {
            state = value;
            window.dispatchEvent(new Event("pagehide"));
            window.dispatchEvent(new Event("pageshow"));
          }}
        >
          Fixture {value}
        </button>
      ))}
      <button onClick={() => window.dispatchEvent(new Event("pagehide"))}>
        Simulate page hide
      </button>
    </aside>
  );
}
createRoot(document.getElementById("controls")!).render(<Controls />);
createRoot(document.getElementById("root")!).render(<App />);
