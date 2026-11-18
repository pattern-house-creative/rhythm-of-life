import { useEffect, useLayoutEffect, useRef, useState } from "react";
import type { Dashboard } from "./dashboards";
import { screenIdentity } from "./screens";
import { accepts, studioCall, controlManifest } from "./studioProtocol";
import type { Control } from "./studioProtocol";

type Values = Record<string, unknown>;
export interface Selection {
  revision: number;
  values: Values;
}
export interface SelectionUndo {
  dashboard: string;
  version: number;
  base: number;
  values: Values;
  after: Values;
}
type Outcome = {
  result: "shown" | "failed";
  detail: Record<string, unknown>;
  undo?: SelectionUndo;
};
interface Runtime {
  read: () => { controls: Control[]; selection: Selection };
  apply: (base: number, values: Values) => Promise<Outcome>;
}
const runtimes = new Map<string, Runtime[]>();
const key = (id: string, version: number) => `${id}:${version}`;
const failed = (summary: string): Outcome => ({
  result: "failed",
  detail: { summary },
});
const active = (id: string, v: number) => runtimes.get(key(id, v))?.at(-1);
// eslint-disable-next-line react/only-export-components -- Shared feature API; edits to this module may reload its consumers.
export async function runDashboardControl(
  action: string,
  args: Record<string, unknown>,
): Promise<Outcome> {
  const id = args.dashboard as string,
    v = args.version as number,
    host = active(id, v);
  if (!host)
    return failed(
      "That dashboard version is not visible and ready on this screen.",
    );
  const state = host.read();
  if (action === "dashboard_controls")
    return {
      result: "shown",
      detail: {
        summary: state.controls.length
          ? "Dashboard controls ready"
          : "This dashboard declares no controls",
        dashboard: id,
        version: v,
        controls: state.controls,
        selectionRevision: state.selection.revision,
        values: state.selection.values,
      },
    };
  const control = state.controls.find((c) => c.id === args.control);
  if (!control || !accepts(control, args.value))
    return failed("That typed selection is not declared by this dashboard.");
  return host.apply(args.base as number, {
    ...state.selection.values,
    [control.id]: args.value,
  });
}
// eslint-disable-next-line react/only-export-components -- Shared feature API; edits to this module may reload its consumers.
export async function undoDashboardSelection(
  undo: SelectionUndo,
): Promise<Outcome> {
  return (
    (await active(undo.dashboard, undo.version)?.apply(
      undo.base,
      undo.values,
    )) ?? failed("The dashboard is no longer open.")
  );
}

// eslint-disable-next-line react/only-export-components -- Shared feature API; edits to this module may reload its consumers.
export function useDashboardSelection(
  dashboard: Dashboard | null | undefined,
  visible: boolean,
) {
  const [controls, setControls] = useState<Control[]>([]),
    [selection, setSelection] = useState<Selection>({
      revision: 0,
      values: {},
    }),
    [notice, setNotice] = useState("");
  const state = useRef({ controls, selection }),
    pending = useRef<{
      revision: number;
      finish: (ok: boolean) => void;
    } | null>(null);
  const id = dashboard?.id,
    version = dashboard?.version;
  useLayoutEffect(() => {
    state.current = { controls, selection };
  });
  useEffect(() => {
    let stopped = false;
    // eslint-disable-next-line react/set-state-in-effect -- Discard controls for the previous version before the new manifest request returns.
    setControls([]);
    setSelection({ revision: 0, values: {} });
    setNotice("");
    if (!id || version === undefined) return;
    studioCall({
      op: "controls",
      screen: screenIdentity().id,
      dashboardId: id,
      version,
    })
      .then((value) => {
        if (stopped || !controlManifest(value)) return;
        const list = value;
        setControls(list);
        setSelection({
          revision: 0,
          values: Object.fromEntries(list.map((c) => [c.id, c.initial])),
        });
      })
      .catch(() => {
        if (!stopped) setNotice("Dashboard controls unavailable.");
      });
    return () => {
      stopped = true;
      pending.current?.finish(false);
      pending.current = null;
    };
  }, [id, version]);
  useEffect(() => {
    if (!id || version === undefined || !visible) return;
    const k = key(id, version);
    const runtime: Runtime = {
      read: () => state.current,
      apply: async (base, values) => {
        const before = state.current.selection;
        if (pending.current || base !== before.revision)
          return failed(
            "Selection changed; discover the current controls again.",
          );
        if (
          Object.keys(values).length !== state.current.controls.length ||
          state.current.controls.some((c) => !accepts(c, values[c.id]))
        )
          return failed("Invalid typed selection.");
        const next = { revision: base + 1, values };
        const shown = new Promise<boolean>((resolve) => {
          const timer = setTimeout(() => {
            if (pending.current?.revision === next.revision)
              pending.current = null;
            resolve(false);
          }, 6000);
          pending.current = {
            revision: next.revision,
            finish: (ok) => {
              clearTimeout(timer);
              pending.current = null;
              resolve(ok);
            },
          };
        });
        state.current = { ...state.current, selection: next };
        setSelection(next);
        if (!(await shown)) {
          const restored = {
            revision: next.revision + 1,
            values: before.values,
          };
          state.current = { ...state.current, selection: restored };
          setSelection(restored);
          return failed(
            "The page did not acknowledge this selection; restored its previous selection.",
          );
        }
        return {
          result: "shown",
          detail: {
            summary: "Changed dashboard selection",
            selectionRevision: next.revision,
            values,
          },
          undo: {
            dashboard: id,
            version,
            base: next.revision,
            values: before.values,
            after: values,
          },
        };
      },
    };
    const list = runtimes.get(k) ?? [];
    runtimes.set(k, [...list, runtime]);
    return () => {
      const left = (runtimes.get(k) ?? []).filter((x) => x !== runtime);
      if (left.length) runtimes.set(k, left);
      else runtimes.delete(k);
      pending.current?.finish(false);
    };
  }, [id, version, visible]);
  const acknowledge = (revision: number, ok: boolean) => {
    if (pending.current?.revision === revision) pending.current.finish(ok);
  };
  const choose = async (c: Control, value: unknown) => {
    if (!dashboard) return;
    const result = await runDashboardControl("dashboard_action", {
      dashboard: dashboard.id,
      version: dashboard.version,
      base: state.current.selection.revision,
      control: c.id,
      value,
    });
    setNotice(String(result.detail.summary));
  };
  return {
    selection,
    acknowledge,
    ui: controls.length ? (
      <div className="dashboard-controls" aria-label="Dashboard controls">
        <SelectionFields
          controls={controls}
          values={selection.values}
          choose={choose}
        />
        <span role="status">{notice}</span>
      </div>
    ) : null,
  };
}

export function SelectionFields({
  controls,
  values,
  choose,
}: {
  controls: Control[];
  values: Values;
  choose: (c: Control, v: unknown) => void;
}) {
  return (
    <>
      {controls.map((c) => (
        <label key={c.id}>
          {c.label}
          {c.kind === "tab" || c.kind === "filter" ? (
            <select
              value={String(values[c.id] ?? c.initial)}
              onChange={(e) => choose(c, e.target.value)}
            >
              {c.options?.map((o) => (
                <option key={o}>{o}</option>
              ))}
            </select>
          ) : c.kind === "week" ? (
            <span>
              <button
                aria-label={`Previous ${c.label}`}
                onClick={() => choose(c, Number(values[c.id] ?? 0) - 1)}
              >
                ‹
              </button>
              <output>{String(values[c.id] ?? 0)}</output>
              <button
                aria-label={`Next ${c.label}`}
                onClick={() => choose(c, Number(values[c.id] ?? 0) + 1)}
              >
                ›
              </button>
            </span>
          ) : (
            <span>
              {(["from", "to"] as const).map((side) => (
                <input
                  key={side}
                  type="date"
                  aria-label={`${c.label} ${side}`}
                  value={String(
                    (values[c.id] as Record<string, string>)?.[side] ?? "",
                  )}
                  onChange={(e) => {
                    const next = {
                      ...(values[c.id] as object),
                      [side]: e.target.value,
                    };
                    if (accepts(c, next)) choose(c, next);
                  }}
                />
              ))}
            </span>
          )}
        </label>
      ))}
    </>
  );
}
