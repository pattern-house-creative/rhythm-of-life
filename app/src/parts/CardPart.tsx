import { Suspense, lazy, useLayoutEffect, useRef, useState } from "react";
import type { CardShape } from "../dashboardFrame";
import type { DashboardSummary } from "../dashboards";
import type { GraphSession } from "../liveGraph";
const DashboardCard = lazy(() => import("../DashboardCard"));

// Another saved dashboard's card (docs/dev/screens.md 10.3), in its own sealed
// frame in card mode, as a corner shows it: tall when the part is more than
// one and a half times as tall as it is wide, else compact, and always compact
// in a fold. Tapping it opens that dashboard.
export function CardPart({
  dashboard,
  dashboards,
  session,
  blocked,
  folded,
  onOpen,
}: {
  dashboard: string;
  // The saved dashboards, and whether that is the engine's whole list.
  dashboards: { list: DashboardSummary[]; complete: boolean };
  session: Pick<GraphSession, "dashboard" | "dashboardData">;
  // The demo's preview of a missing or failed source draws no card.
  blocked: boolean;
  folded: boolean;
  onOpen: (id: string) => void;
}) {
  const box = useRef<HTMLDivElement>(null);
  // Measured before the card first draws, so its frame loads once in its
  // shape; a resize across the line draws it afresh in the other.
  const [shape, setShape] = useState<CardShape | null>(null);
  useLayoutEffect(() => {
    const element = box.current;
    if (!element) return;
    const measure = () => {
      const { width, height } = element.getBoundingClientRect();
      setShape(!folded && height > width * 1.5 ? "tall" : "compact");
    };
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    return () => observer.disconnect();
  }, [folded]);
  const chosen = dashboards.list.find((d) => d.id === dashboard);
  return (
    <div ref={box} className="card-part">
      {chosen ? (
        shape && (
          <Suspense fallback={null}>
            <DashboardCard
              key={`${chosen.id}:${chosen.latest}:${shape}`}
              id={chosen.id}
              title={chosen.title}
              session={session}
              shape={shape}
              unavailable={blocked}
              onOpen={() => onOpen(chosen.id)}
            />
          </Suspense>
        )
      ) : dashboards.complete ? (
        // Missing from this viewer's whole list: deleted since, or one this
        // viewer can't read, which the list doesn't tell apart. Either way it
        // names nothing.
        <p className="dashboard-card-status">
          <strong>Dashboard unavailable</strong>
        </p>
      ) : (
        // Not among the dashboards read so far: it says itself whether it
        // can be read, and names nothing when it can't.
        shape && (
          <Suspense fallback={null}>
            <DashboardCard
              key={`${dashboard}:${shape}`}
              id={dashboard}
              title=""
              session={session}
              shape={shape}
              unavailable={blocked}
              missing="Dashboard unavailable"
              onOpen={() => onOpen(dashboard)}
            />
          </Suspense>
        )
      )}
    </div>
  );
}
