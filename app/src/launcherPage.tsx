import { createRoot } from "react-dom/client";
import { useEffect, useState } from "react";
import App from "./App";
import { instance, loadInstance } from "./instance";
import { dashboardCache } from "./dashboardCache";
import "./index.css";
import "./launcherPage.css";

// An isolated entry using the existing readers. No service worker registration,
// demo bootstrap or native bridge. The PWA navigation fallback excludes this page.
// eslint-disable-next-line react/only-export-components -- This standalone entry mounts its own React root.
function LauncherPage() {
  const [ready, setReady] = useState(false);
  const [mounted, setMounted] = useState(false);
  const [failed, setFailed] = useState(false);
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    let generation = 0;
    let closed = false;
    const clear = () => {
      closed = true;
      generation++;
      dashboardCache.clear();
      setReady(false);
      setMounted(false);
      setFailed(true);
    };
    const verify = async () => {
      const request = ++generation;
      setReady(false);
      if (closed || document.hidden) return;
      const ok = await loadInstance();
      if (request !== generation || closed) return;
      if (!ok || instance().mode !== "live" || !navigator.onLine) {
        clear();
        return;
      }
      setMounted(true);
      setReady(true);
    };
    window.addEventListener("offline", clear);
    window.addEventListener("pagehide", clear);
    window.addEventListener("observatory-access-lost", clear);
    document.addEventListener("visibilitychange", verify);
    void verify();
    return () => {
      closed = true;
      dashboardCache.clear();
      window.removeEventListener("offline", clear);
      window.removeEventListener("pagehide", clear);
      window.removeEventListener("observatory-access-lost", clear);
      document.removeEventListener("visibilitychange", verify);
    };
  }, [attempt]);
  return (
    <>
      {mounted && (
        <div style={{ display: ready ? "contents" : "none" }}>
          <App launcher />
        </div>
      )}
      {!ready && (
        <main className="boot" role="status">
          <h1>{failed ? "Household unavailable" : "Opening household"}</h1>
          <p>
            {failed
              ? "Check your connection and household access."
              : "Verifying access to this household."}
          </p>
          {failed && (
            <button
              onClick={() => {
                setFailed(false);
                setAttempt((n) => n + 1);
              }}
            >
              Try again
            </button>
          )}
        </main>
      )}
    </>
  );
}
createRoot(document.getElementById("root")!).render(<LauncherPage />);
