import { demoAdapter } from "../src/demo.js";
import { object } from "../src/validation.js";
import type { ObservatoryData } from "../src/model.js";
import { glucoseSlot, unavailableHealth } from "./healthState.js";
import { sourceName } from "../src/graphView.js";
import { mapLiveGlucose } from "./glucose.js";
import { answerRejected, callTool, engineMode, EngineError } from "./engine.js";
import type { EngineReason } from "./engine.js";
import { settings } from "./config.js";
export { glucoseSlot, unavailableHealth } from "./healthState.js";

// A person in the settings, or the shared household subject.
export const knownSubject = (subject: unknown): subject is string =>
  typeof subject === "string" &&
  (subject === "household" || settings().people.some((p) => p.id === subject));

// What a failed engine read leaves on a health source: "Not connected" when the adapter is off,
// "unavailable" with the reason otherwise. Never demo data.
export function failedSource(
  data: ObservatoryData,
  id: string,
  error: unknown,
): ObservatoryData {
  const reason: EngineReason =
    error instanceof EngineError ? error.reason : "invalid_response";
  if (reason === "not_connected") return data;
  return {
    ...data,
    sources: data.sources.map((s) =>
      s.id === id
        ? {
            ...s,
            status: "unavailable" as const,
            detail: `Unavailable: ${reason}`,
          }
        : s,
    ),
  };
}

// The latest glucose reading. Demo mode (no engine configured) serves the fictional dataset;
// with an engine, the reading comes from app_latest_glucose. No request can select a subject,
// a metric or a host.
export async function loadHealth(
  signal?: AbortSignal,
  fetcher: typeof fetch = fetch,
  now = () => new Date().toISOString(),
): Promise<ObservatoryData> {
  if (engineMode() === "demo") return demoAdapter.load(signal);
  try {
    const payload = await callTool("app_latest_glucose", {}, signal, fetcher);
    // The engine's fictional seed belongs to its own synthetic subject; it is accepted only
    // as a reading labelled demo, never as a person's.
    const demoReading =
      object(payload) &&
      Array.isArray(payload.rows) &&
      payload.rows.length === 1 &&
      object(payload.rows[0]) &&
      payload.rows[0].demo === true;
    if (
      !object(payload) ||
      payload.complete !== true ||
      payload.selection !== "latest" ||
      typeof payload.subject !== "string" ||
      !(knownSubject(payload.subject) || demoReading) ||
      !Array.isArray(payload.rows) ||
      payload.rows.length > 1
    )
      throw new Error("Invalid latest glucose answer");
    // Timestamp retrieval at completion; never replace observed_at with fetch time.
    const result = unavailableHealth(now());
    result.current = mapLiveGlucose(
      payload.rows,
      payload.subject,
      result.retrievedAt,
    );
    if (result.current.status !== "unavailable")
      result.sources = result.sources.map((source) =>
        source.id === glucoseSlot
          ? {
              ...source,
              name: sourceName(result.current.provenance.sourceId),
              connected: true,
              sampleAt: result.current.provenance.observedAt,
              status: result.current.status === "stale" ? "stale" : "available",
            }
          : source,
      );
    // One observation never invents a trend, chart, GMI, TIR, or other metric.
    return result;
  } catch (error) {
    answerRejected("app_latest_glucose", error, signal);
    return failedSource(unavailableHealth(now()), glucoseSlot, error);
  }
}
