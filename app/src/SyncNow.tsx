import { useCallback, useEffect, useRef, useState } from "react";
import { RefreshCw } from "lucide-react";
import { loadSyncs, requestSync } from "./apiClient";
import { stamp } from "./health";
import { adapterName, every, outcomeLine, stateWord, waitingFor } from "./sync";
import type { AdapterSync, SyncAnswer, SyncList, SyncRefusal } from "./sync";

// How often the panel reads the list again while a batch it asked for waits or runs, and for
// how long at most (an asked batch is cut off after five minutes on the engine).
export const followEvery = 3000;
const followFor = 6 * 60_000;

const refusedWords: Record<SyncRefusal, string> = {
  running: "It is syncing already; this one wasn’t added.",
  pending: "One is already waiting to start.",
  "too-soon": "Asked for lately, so not yet.",
};

// Sources & settings, live mode: Sync now (SB-P6-09). Each adapter that syncs or imports on the
// engine's own clock, how often it runs on its own, a Sync now button, and how the last one asked
// for went, all here in the panel: nothing opens over it. Pressing asks the engine for one
// batch within the adapter's own limit; while it waits and runs the panel reads its state again
// every few seconds, and says how it ended. States and times only: the engine's own words never
// reach the page. The demo has no engine, so nothing to sync.
export function SyncNow({
  load = loadSyncs,
  ask = requestSync,
  follow = followEvery,
}: {
  load?: (signal?: AbortSignal) => Promise<SyncList>;
  ask?: (adapter: string, signal?: AbortSignal) => Promise<SyncAnswer>;
  follow?: number;
}) {
  const [list, setList] = useState<SyncList | null>(null);
  const [asking, setAsking] = useState<string | null>(null);
  const [said, setSaid] = useState<Record<string, string>>({});
  const [followed, setFollowed] = useState<Record<string, number>>({});
  // Bumped when a follow-up read finds the engine unavailable, so the next one is still made.
  const [missed, setMissed] = useState(0);
  const live = useRef(new AbortController());
  useEffect(() => {
    const abort = new AbortController();
    live.current = abort;
    load(abort.signal).then(
      (answer) => {
        if (!abort.signal.aborted) setList(answer);
      },
      () => {
        if (!abort.signal.aborted) setList({ available: false });
      },
    );
    return () => abort.abort();
  }, [load]);

  // While a batch this panel asked for waits or runs, read the list again every few seconds;
  // once it has ended, its row says how (and the "asked for" line goes). A read that finds the
  // engine unavailable is tried again, for as long as the batch is followed at all; after that
  // the row says its state couldn't be read.
  useEffect(() => {
    const names = Object.keys(followed);
    if (!names.length || !list?.available) return;
    const timer = setTimeout(async () => {
      const signal = live.current.signal;
      const next = await load(signal).catch(
        () => ({ available: false }) as const,
      );
      if (signal.aborted) return;
      if (!next.available) {
        const now = Date.now();
        const still = Object.fromEntries(
          names
            .filter((name) => now - followed[name] < followFor)
            .map((name) => [name, followed[name]]),
        );
        if (Object.keys(still).length === names.length) {
          setMissed((m) => m + 1);
          return;
        }
        setFollowed(still);
        setSaid((s) => ({
          ...s,
          ...Object.fromEntries(
            names
              .filter((name) => !(name in still))
              .map((name) => [
                name,
                "How it went couldn’t be read; open this panel again to see.",
              ]),
          ),
        }));
        return;
      }
      setList(next);
      const now = Date.now();
      const still: Record<string, number> = {};
      const ended: Record<string, string> = {};
      for (const name of names) {
        const sync = next.adapters.find((a) => a.adapter === name);
        if (
          sync &&
          (sync.running || sync.pendingSince) &&
          now - followed[name] < followFor
        )
          still[name] = followed[name];
        else ended[name] = "";
      }
      setFollowed(still);
      if (Object.keys(ended).length) setSaid((s) => ({ ...s, ...ended }));
    }, follow);
    return () => clearTimeout(timer);
  }, [followed, list, load, follow, missed]);

  const press = useCallback(
    async (adapter: string) => {
      setAsking(adapter);
      setSaid((s) => ({ ...s, [adapter]: "" }));
      const answer = await ask(adapter, live.current.signal).catch(
        () => ({ available: false }) as const,
      );
      if (live.current.signal.aborted) return;
      setAsking(null);
      if (!answer.available) {
        setSaid((s) => ({
          ...s,
          [adapter]: "Sync now couldn’t be asked for right now.",
        }));
        return;
      }
      setList((current) =>
        current?.available
          ? {
              available: true,
              adapters: current.adapters.map((a) =>
                a.adapter === adapter ? answer.adapter : a,
              ),
            }
          : current,
      );
      setSaid((s) => ({
        ...s,
        [adapter]: answer.accepted
          ? "Asked for: it starts within seconds."
          : refusedWords[answer.refused ?? "running"],
      }));
      if (answer.accepted)
        setFollowed((f) => ({ ...f, [adapter]: Date.now() }));
    },
    [ask],
  );

  return (
    <section className="capability-note sync-now" aria-label="Sync now">
      <h3>Sync now</h3>
      {list === null ? (
        <p>Reading the sources that sync on the engine’s clock…</p>
      ) : !list.available ? (
        <p>
          Sync now can’t be offered right now. An engine older than this app
          doesn’t have it; the sources still sync on their own.
        </p>
      ) : !list.adapters.length ? (
        <p>
          No source here syncs on the engine’s own clock. Sources the map reads,
          such as notes or a Drive folder, come in with Refresh, which rebuilds
          the map.
        </p>
      ) : (
        <>
          <p>
            Each source syncs on its own. Sync now asks one for a single batch
            now, within that source’s own limit.
          </p>
          <div className="sources-list">
            {list.adapters.map((sync) => (
              <SyncRow
                key={sync.adapter}
                sync={sync}
                said={said[sync.adapter]}
                busy={asking !== null}
                asking={asking === sync.adapter}
                onPress={() => void press(sync.adapter)}
              />
            ))}
          </div>
        </>
      )}
    </section>
  );
}

function SyncRow({
  sync,
  said,
  busy,
  asking,
  onPress,
}: {
  sync: AdapterSync;
  said?: string;
  busy: boolean;
  asking: boolean;
  onPress: () => void;
}) {
  const name = adapterName(sync.adapter);
  const waiting = waitingFor(sync);
  const outcome = outcomeLine(sync, stamp);
  return (
    <div>
      <span>
        <b>{name}</b>
        <small>
          {sync.kind === "imports" ? "Imports" : "Syncs"} on its own{" "}
          {every(sync.interval)}; Sync now at most {every(sync.limit)}.
        </small>
        {sync.running ? (
          <small>Syncing now…</small>
        ) : sync.pendingSince ? (
          <small>Waiting to start (asked {stamp(sync.pendingSince)}).</small>
        ) : (
          outcome && <small>{outcome}</small>
        )}
        {waiting === "too-soon" && sync.nextAllowedAt && (
          <small>Sync now again from {stamp(sync.nextAllowedAt)}.</small>
        )}
        <small role="status">{said ?? ""}</small>
      </span>
      <span className="sync-action">
        <span className="source-status">{stateWord(sync).toUpperCase()}</span>
        <button
          onClick={onPress}
          disabled={busy || waiting !== null}
          aria-label={`Sync ${name} now`}
        >
          <RefreshCw
            size={14}
            className={asking ? "refresh-spinning" : undefined}
          />{" "}
          {asking ? "Asking…" : "Sync now"}
        </button>
      </span>
    </div>
  );
}
