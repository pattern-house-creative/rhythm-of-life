import { createRoot } from "react-dom/client";
import { useEffect, useState } from "react";
import LauncherPreview from "./LauncherPreview";
import BuiltinGlucosePreview from "./BuiltinGlucosePreview";
import { createApiSession } from "./apiClient";
import { loadInstance } from "./instance";
import { dashboardCache } from "./dashboardCache";
import "./index.css";
import "./launcherCard.css";

const id = new URLSearchParams(location.hash.slice(1)).get("dashboard");
const builtin = new URLSearchParams(location.hash.slice(1)).get("builtin");
const session = createApiSession(false);
function Card() {
  const [active, setActive] = useState(!document.hidden && navigator.onLine);
  useEffect(() => {
    const clear = () => {
      dashboardCache.clear();
      setActive(false);
    };
    const resume = () => setActive(!document.hidden && navigator.onLine);
    const visibility = () => (document.hidden ? clear() : resume());
    document.addEventListener("visibilitychange", visibility);
    window.addEventListener("offline", clear);
    window.addEventListener("pagehide", clear);
    window.addEventListener("observatory-access-lost", clear);
    window.addEventListener("online", resume);
    return () => {
      clear();
      document.removeEventListener("visibilitychange", visibility);
      window.removeEventListener("offline", clear);
      window.removeEventListener("pagehide", clear);
      window.removeEventListener("observatory-access-lost", clear);
      window.removeEventListener("online", resume);
    };
  }, []);
  return active && builtin === "glucose" ? (
    <BuiltinGlucosePreview />
  ) : active && id ? (
    <LauncherPreview id={id} session={session} />
  ) : (
    <p>Dashboard unavailable</p>
  );
}
await loadInstance();
createRoot(document.getElementById("root")!).render(<Card />);
