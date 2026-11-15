import { useEffect, useState } from "react";
import type { GraphSeries } from "../knowledge";
import { catalogProvider, healthSubject } from "../instance";

// One data star's recent days, read from the engine while a panel is open (the
// same `data` operation a data star uses in the explorer). Nothing is kept
// once the panel closes.
export type ReadData = (
  id: string,
  signal?: AbortSignal,
) => Promise<GraphSeries[] | null>;

// A data star's graph id, e.g. dataStar("garmin", "recovery") for the health subject's
// sleep and recovery readings.
export const dataStar = (
  provider: string,
  group: string,
  subject = healthSubject(),
) =>
  [catalogProvider(), `data:${provider}`, `${subject}:${group}`]
    .map(encodeURIComponent)
    .join(":");

// undefined while reading, null when unavailable.
export function useReadings(read: ReadData | undefined, id: string) {
  const [loaded, setLoaded] = useState<{
    id: string;
    series: GraphSeries[] | null;
  } | null>(null);
  useEffect(() => {
    if (!read) return;
    const controller = new AbortController();
    read(id, controller.signal).then(
      (series) => setLoaded({ id, series }),
      () => {
        if (!controller.signal.aborted) setLoaded({ id, series: null });
      },
    );
    return () => controller.abort();
  }, [id, read]);
  if (!read) return null;
  return loaded?.id === id ? loaded.series : undefined;
}

// A metric's series from a star's readings, if present.
export const metric = (series: GraphSeries[] | null | undefined, name: string) =>
  series?.find((s) => s.metric === name) ?? null;
