import { createRoot } from "react-dom/client";
import { useEffect, useState } from "react";
import App from "./App";
import { instance, loadInstance } from "./instance";
import { dashboardCache } from "./dashboardCache";
import "./index.css";
import "./launcherPage.css";

// An isolated entry using the existing readers. No service worker registration,
// demo bootstrap or native bridge. The PWA navigation fallback excludes this page.
function LauncherPage() {
  const [ready, setReady] = useState(false);
  const [failed, setFailed] = useState(false);
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    let current = true;
    setFailed(false);
    const clear = () => {
      current = false;
      dashboardCache.clear();
      setReady(false);
      setFailed(true);
    };
    const visibility = () => {
      if (document.hidden) clear();
    };
    window.addEventListener("offline", clear);
    window.addEventListener("pagehide", clear);
    window.addEventListener("observatory-access-lost", clear);
    document.addEventListener("visibilitychange", visibility);
    void loadInstance().then((ok) => {
      if (!current) return;
      const live =
        ok &&
        instance().mode === "live" &&
        navigator.onLine &&
        !document.hidden;
      setReady(live);
      setFailed(!live);
    });
    return () => {
      current = false;
      dashboardCache.clear();
      window.removeEventListener("offline", clear);
      window.removeEventListener("pagehide", clear);
      window.removeEventListener("observatory-access-lost", clear);
      document.removeEventListener("visibilitychange", visibility);
    };
  }, [attempt]);
  return ready ? (
    <App launcher />
  ) : (
    <main className="boot" role="status">
      <h1>{failed ? "Household unavailable" : "Opening household"}</h1>
      <p>
        {failed
          ? "Check your connection and household access."
          : "Verifying access to this household."}
      </p>
      {failed && (
        <button onClick={() => setAttempt((n) => n + 1)}>Try again</button>
      )}
    </main>
  );
}
createRoot(document.getElementById("root")!).render(<LauncherPage />);
