import { useEffect, useState } from "react";
import { loadStatus } from "./apiClient";
import {
  indexLine,
  modelJob,
  modelName,
  stateNames,
  stateNote,
} from "./modelStatus";
import type { StatusAnswer } from "./modelStatus";

// Sources & settings, live mode: each model's job and state, read from the engine when the
// panel opens (app_status; docs/models.md). The demo has no engine, so no models. States and
// counts only; the engine's own words never reach the page.
export function EngineModels({
  load = loadStatus,
}: {
  load?: (signal?: AbortSignal) => Promise<StatusAnswer>;
}) {
  const [answer, setAnswer] = useState<StatusAnswer | null>(null);
  useEffect(() => {
    const abort = new AbortController();
    load(abort.signal).then(
      (a) => {
        if (!abort.signal.aborted) setAnswer(a);
      },
      () => {
        if (!abort.signal.aborted) setAnswer({ mode: "live", available: false });
      },
    );
    return () => abort.abort();
  }, [load]);
  return (
    <section className="capability-note model-status" aria-label="Models">
      <h3>Models</h3>
      {answer === null ? (
        <p>Reading the engine’s models…</p>
      ) : !answer.available ? (
        <p>
          The engine’s models can’t be read right now. An engine older than
          this app doesn’t report them; <code>./rhythm doctor</code> shows them
          too.
        </p>
      ) : (
        <>
          <div className="sources-list">
            {answer.models.map((m, i) => {
              const note = stateNote(m);
              return (
                <div key={`${m.job}-${m.role}-${i}`}>
                  <span>
                    <b>{modelName(m)}</b>
                    <small>{modelJob(m, answer.models)}</small>
                    {m.index && m.state !== "off" && (
                      <small>{indexLine(m.index)}</small>
                    )}
                    {note && <small>{note}</small>}
                  </span>
                  <span className="source-status">
                    {stateNames[m.state].toUpperCase()}
                  </span>
                </div>
              );
            })}
          </div>
          <p>
            Engine {answer.version} · schema {answer.schema}. Reasoning, such
            as the daily insight, is your AI client’s, outside Rhythm.
          </p>
        </>
      )}
    </section>
  );
}
