import { useEffect, useState } from "react";
import { loadObservatory } from "./apiClient";
import type { ObservatoryData } from "./model";
import { GlucosePart } from "./parts/GlucosePart";

// The same argument-free glucose source as Home; no saved-dashboard record,
// synthetic fallback, health persistence, graph discovery or sync/write request.
export default function BuiltinGlucosePreview() {
  const [data, setData] = useState<ObservatoryData | null>(null);
  const [loading, setLoading] = useState(true);
  const [now, setNow] = useState(Date.now);
  useEffect(() => {
    let gone = false,
      busy = false;
    let active: AbortController | null = null;
    let generation = 0;
    const read = async () => {
      if (busy || gone || document.hidden || !navigator.onLine) return;
      busy = true;
      const current = ++generation;
      const controller = new AbortController();
      active = controller;
      let history: ObservatoryData["glucoseHistory"] = null;
      const valid = () =>
        !gone && current === generation && !controller.signal.aborted;
      try {
        await Promise.allSettled([
          loadObservatory(controller.signal)
            .then((next) => {
              if (next.healthMode !== "live" || next.current.provenance.demo)
                throw Error("Not live");
              if (valid()) setData({ ...next, glucoseHistory: history });
            })
            .catch(() => {
              if (valid()) setData(null);
            })
            .finally(() => {
              if (valid()) setLoading(false);
            }),
          loadObservatory(controller.signal, "history")
            .then((next) => {
              if (
                next.healthMode !== "live" ||
                next.glucoseHistory?.provenance.demo
              )
                throw Error("Not live history");
              history = next.glucoseHistory;
              if (valid())
                setData((previous) =>
                  previous ? { ...previous, glucoseHistory: history } : null,
                );
            })
            .catch(() => {
              if (valid())
                setData((previous) =>
                  previous ? { ...previous, glucoseHistory: null } : null,
                );
            }),
        ]);
      } finally {
        if (valid()) {
          busy = false;
          setLoading(false);
          setNow(Date.now());
        }
      }
    };
    const change = (event: Event) => {
      generation++;
      active?.abort();
      busy = false;
      if (event.type === "offline") setData(null);
      setNow(Date.now());
      setLoading(!document.hidden && navigator.onLine);
      void read();
    };
    void read();
    document.addEventListener("visibilitychange", change);
    window.addEventListener("offline", change);
    window.addEventListener("online", change);
    const refresh = setInterval(() => void read(), 60_000);
    // Age and freshness continue advancing even between successful source reads.
    const clock = setInterval(() => { if (!document.hidden) setNow(Date.now()); }, 15_000);
    return () => {
      gone = true;
      active?.abort();
      document.removeEventListener("visibilitychange", change);
      window.removeEventListener("offline", change);
      window.removeEventListener("online", change);
      clearInterval(refresh);
      clearInterval(clock);
    };
  }, []);
  return data ? (
    <GlucosePart
      data={data}
      state="ready"
      blocked={false}
      launcherNow={now}
      onOpen={() => {}}
    />
  ) : (
    <div className="preview-state" role="status" data-builtin="glucose">
      <h1>Glucose</h1>
      <p>
        {loading
          ? "Reading glucose source…"
          : "Glucose unavailable. Open to retry."}
      </p>
    </div>
  );
}
