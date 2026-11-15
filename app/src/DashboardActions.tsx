import { Check, House, Maximize2 } from "lucide-react";

// What this screen can do with a dashboard it shows (docs/dev/screens.md section 8): fill the
// display with it, and make it the screen's Home. Both act in place, beside the dashboard.
export interface DashboardScreen {
  fullScreen: (dashboard: string) => void;
  // This screen's Home dashboard, or null for the default Home.
  home: string | null;
  setHome: (dashboard: string) => void;
}

export function DashboardActions({
  id,
  screen,
  className,
  label,
}: {
  id: string;
  screen: DashboardScreen;
  className: string;
  // The words' class, so a phone's reader bar can keep to icons and leave them to screen readers.
  label?: string;
}) {
  return (
    <>
      <button className={className} onClick={() => screen.fullScreen(id)}>
        <Maximize2 size={14} aria-hidden="true" />{" "}
        <span className={label}>Full screen</span>
      </button>
      {screen.home === id ? (
        <span className={`${className} dashboard-home`}>
          <Check size={14} aria-hidden="true" />{" "}
          <span className={label}>This screen's Home</span>
        </span>
      ) : (
        <button className={className} onClick={() => screen.setHome(id)}>
          <House size={14} aria-hidden="true" />{" "}
          <span className={label}>Set as Home here</span>
        </button>
      )}
    </>
  );
}
