// Synthetic visual/sandbox verification only. No live engine, source data, credentials or timed typing.
import { createRoot } from "react-dom/client";
import { useEffect, useState } from "react";
import { Studio } from "../src/Studio";
import DashboardView from "../src/DashboardView";
import { draftDashboard } from "../src/studioProtocol";
import type { Control, StudioSession } from "../src/studioProtocol";
import { runDashboardControl } from "../src/dashboardControls";
import { screenIdentity } from "../src/screens";
import "../src/index.css";
const storage = "rhythm-studio-synthetic-verification-v1",
  channel = new BroadcastChannel(storage),
  id = "55555555-0000-4000-8000-000000000001";
const controls: Control[] = [
  {
    id: "tab",
    label: "View",
    kind: "tab",
    options: ["Plan", "History"],
    initial: "Plan",
  },
  { id: "week", label: "Week", kind: "week", initial: 0 },
  {
    id: "filter",
    label: "Activity",
    kind: "filter",
    options: ["All", "Running", "Recovery"],
    initial: "All",
  },
];
const html = (cards: boolean) =>
  `<style>body{padding:4vw}small{color:var(--rhythm-health);letter-spacing:.15em}h1{font-size:clamp(30px,5vw,64px);margin-top:12px}p{color:var(--rhythm-muted)}.cards{display:grid;grid-template-columns:repeat(3,1fr);gap:20px;margin-top:40px}article{background:var(--rhythm-surface);border:1px solid var(--rhythm-line);border-radius:18px;padding:24px}b{display:block;font-size:34px;color:var(--rhythm-health)}@media(max-width:700px){.cards{grid-template-columns:1fr}}</style><small>DEMO · FICTIONAL TRAINING</small><h1>Find your rhythm.</h1><p id="selection">Weekly training plan</p><div id="cards"></div><script>rhythm.onData(({selection,edits})=>{const s=selection?.values??{};document.getElementById('selection').textContent=(s.tab??'Plan')+' · Week '+(s.week??0)+' · '+(s.filter??'All')+' · source edits '+(edits?'on':'off');document.getElementById('cards').innerHTML=${cards ? "'<div class=cards><article><b>4</b>Planned sessions</article><article><b>32 km</b>Weekly distance</article><article><b>2 days</b>Recovery space</article></div>'" : "''"};});</script>`;
const draft = (markup: string) => ({
  title: "Training · synthetic verification",
  description: "Fictional test dashboard",
  html: markup,
  feeds: [],
  writes: [],
  note: "Received test snapshot",
  stars: [],
});
let state: StudioSession = JSON.parse(
  localStorage.getItem(storage) ?? "null",
) ?? {
  id,
  screen: screenIdentity().id,
  dashboard: null,
  revision: 1,
  valid_revision: 0,
  status: "active",
  candidate: draft(html(false)),
  preview: null,
  controls,
  preview_controls: [],
  events: [{ seq: 1, op: "update", revision: 1, at: new Date().toISOString() }],
  feedback: [],
};
function persist() {
  localStorage.setItem(storage, JSON.stringify(state));
}
window.fetch = async (_input, init) => {
  const a = JSON.parse(String(init?.body ?? "{}"));
  if (a.op === "controls") return Response.json({ value: controls });
  if (a.op === "data")
    return Response.json({
      value: { asOf: new Date().toISOString(), feeds: {} },
    });
  if (a.op === "validate") {
    if (a.revision === state.revision && a.payload.ok) {
      state.valid_revision = state.revision;
      state.preview = state.candidate;
      state.preview_controls = state.controls;
    }
    state.events.push({
      seq: state.events.length + 1,
      op: "validate",
      revision: a.revision,
      at: new Date().toISOString(),
      ok: a.payload.ok,
      message: a.payload.message,
    });
    persist();
  }
  if (a.op === "feedback") {
    state.feedback.push({
      seq: state.feedback.length + 1,
      text: a.payload.text,
      at: new Date().toISOString(),
    });
    persist();
  }
  if (a.op === "save") {
    state.status = "saved";
    persist();
    return Response.json({ value: { id, version: 1 } });
  }
  if (a.op === "cancel" || a.op === "discard") {
    state.status = a.op === "cancel" ? "canceled" : "discarded";
    persist();
  }
  return Response.json({ value: state });
};
channel.onmessage = async ({ data }) => {
  if (data === "week") {
    const discovered = await runDashboardControl("dashboard_controls", {
      dashboard: id,
      version: 1,
    });
    const changed = await runDashboardControl("dashboard_action", {
      dashboard: id,
      version: 1,
      base: discovered.detail.selectionRevision,
      control: "week",
      value: 1,
    });
    channel.postMessage({ result: changed });
    return;
  }
  if (data === "reset") {
    localStorage.removeItem(storage);
    location.reload();
    return;
  }
  if (state.status !== "active") {
    channel.postMessage({ result: "Rejected late update to ended session" });
    return;
  }
  state = {
    ...state,
    revision: state.revision + 1,
    candidate: draft(
      data === "bad"
        ? '<script>throw new Error("Synthetic invalid candidate")</script>'
        : html(true),
    ),
  };
  state.events.push({
    seq: state.events.length + 1,
    op: "update",
    revision: state.revision,
    at: new Date().toISOString(),
    message: "Completed synthetic snapshot received",
  });
  persist();
  window.dispatchEvent(new Event("rhythm:studio"));
};
// eslint-disable-next-line react/only-export-components -- This standalone entry mounts its own React root.
function TestScreen() {
  const [saved, setSaved] = useState(state.status === "saved");
  const board = state.candidate
    ? draftDashboard(state, state.candidate, 1)
    : null;
  return (
    <>
      {saved && board ? (
        <div
          style={{
            height: "100vh",
            padding: 20,
            display: "flex",
            flexDirection: "column",
          }}
        >
          <p>DEMO · Saved version 1 · synthetic browser verification</p>
          <DashboardView
            id={id}
            bare
            session={{
              dashboard: async () => board,
              dashboardData: async () => ({
                asOf: new Date().toISOString(),
                feeds: {},
                dashboard: { id, version: 1 },
              }),
            }}
            onOpenStar={() => {}}
          />
        </div>
      ) : (
        <p>Saved dashboard remains beneath Studio.</p>
      )}
      <Studio
        enabled
        screen={state.screen}
        parts={{ renderPart: () => null }}
        saved={() => setSaved(true)}
      />
    </>
  );
}
// eslint-disable-next-line react/only-export-components -- This standalone entry mounts its own React root.
function Controller() {
  const [result, setResult] = useState("");
  useEffect(() => {
    const receive = (e: MessageEvent) => setResult(JSON.stringify(e.data));
    channel.addEventListener("message", receive);
    return () => channel.removeEventListener("message", receive);
  }, []);
  return (
    <main style={{ padding: 40 }}>
      <h1>Studio synthetic controller</h1>
      <p>
        Each button sends one completed test update. No production connection.
      </p>
      {["cards", "bad", "week", "reset"].map((c) => (
        <button key={c} onClick={() => channel.postMessage(c)}>
          {c}
        </button>
      ))}
      <pre>{result}</pre>
    </main>
  );
}
createRoot(document.getElementById("root")!).render(
  new URLSearchParams(location.search).has("controller") ? (
    <Controller />
  ) : (
    <TestScreen />
  ),
);
