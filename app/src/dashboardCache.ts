import type { Dashboard, DashboardData } from "./dashboards";

// The last version and feed data read for each dashboard, in memory only, so
// reopening one (or opening it from its corner card) draws at once while it is
// read again. Cleared, like the rest of the app's private state, when the tab
// is hidden, goes offline or loses access.
const versions = new Map<string, Dashboard>();
const reads = new Map<string, DashboardData>();
const key = (id: string, version?: number) => `${id}:${version ?? "latest"}`;

export const dashboardCache = {
  version: (id: string, version?: number) => versions.get(key(id, version)),
  data: (id: string, version: number) => reads.get(key(id, version)),
  keepVersion(d: Dashboard, asked?: number) {
    versions.set(key(d.id, asked), d);
    versions.set(key(d.id, d.version), d);
  },
  keepData(read: DashboardData) {
    reads.set(key(read.dashboard.id, read.dashboard.version), read);
  },
  clear() {
    versions.clear();
    reads.clear();
  },
};

if (typeof window !== "undefined") {
  const clear = () => dashboardCache.clear();
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) clear();
  });
  for (const event of ["pagehide", "offline", "observatory-access-lost"])
    window.addEventListener(event, clear);
}
