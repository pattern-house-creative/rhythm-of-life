import { corners, unarranged } from "./dashboards";
import { identityKey, mergePage } from "./knowledge";
import type { GraphPage } from "./knowledge";
import { demoReadings } from "./demoReadings";
import { arrangementOnly, layoutOf, layoutProblem } from "./parts";
import type { Layout } from "./parts";
import { focusedPage } from "./mapFocus";
import type {
  Corner,
  Dashboard,
  DashboardArrangement,
  DashboardCards,
  DashboardData,
  DashboardFeed,
  DashboardMap,
  DashboardSummary,
  DashboardWrite,
  DashboardWriteAnswer,
  DashboardWriteRecord,
  FeedPayload,
  LayoutAnswer,
} from "./dashboards";

// The demo's two fictional dashboards, held in memory with its session: the
// week at a glance (sleep, steps, resting heart rate and mood), whose tall card
// fills the main slot, and a week of sample workouts by sport beside a sample
// plan, whose card takes a corner. Their pages are written the way an AI client
// writes one: a body fragment that draws from rhythm.onData.
const id = "00000000-0000-4000-8000-00000000d0a5",
  glanceId = "00000000-0000-4000-8000-00000000d0a6";
// The demo graph's own ids for the stars they read.
const star = (provider: string, record: string) =>
  `${provider}:fictional-household:${encodeURIComponent(`demo:${record}`)}`;
const training = star("demo", "training"),
  run = star("demo", "run"),
  base = star("drive", "base");
const dataStar = (group: string) =>
  identityKey({
    provider: "rhythm",
    account: "data:demo",
    externalId: `fixture:${group}`,
  });
const glanceFeeds: DashboardFeed[] = [
  { name: "sleep", kind: "data", star: dataStar("sleep"), days: 14, by: "day" },
  {
    name: "steps",
    kind: "data",
    star: dataStar("activity"),
    days: 14,
    by: "day",
    metrics: ["steps"],
  },
  { name: "heart", kind: "data", star: dataStar("heart"), days: 14, by: "day" },
  { name: "mood", kind: "data", star: dataStar("mood"), days: 14, by: "day" },
];
const glanceStars = [
  { graphId: dataStar("sleep"), title: "Sleep" },
  { graphId: dataStar("activity"), title: "Steps" },
  { graphId: dataStar("heart"), title: "Resting heart rate" },
  { graphId: dataStar("mood"), title: "Mood log" },
];
const glancePage = `<style>
.w{display:grid;gap:22px;max-width:980px;margin:0 auto;padding:clamp(12px,3vw,24px) clamp(16px,3vw,24px)}
.tag{font-size:11px;letter-spacing:.14em;color:var(--rhythm-caution)}
.w h1{font-size:clamp(20px,2.4vw,26px);margin:2px 0 0}
.figs{display:grid;grid-template-columns:repeat(auto-fit,minmax(140px,1fr));gap:14px 24px}
.fig b{display:block;font-family:var(--rhythm-display);font-weight:500;font-size:clamp(26px,3.6vw,36px);line-height:1.1}
.fig span{color:var(--rhythm-muted);font-size:12px}
.chart h2{font-size:13px;font-weight:500;color:var(--rhythm-muted);margin:0 0 6px}
.chart svg{display:block;width:100%;height:120px}
.note{margin:0;color:var(--rhythm-muted);font-size:12px}
.card{display:none}
html[data-mode=card],html[data-mode=card] body{height:100%;overflow:hidden}
html[data-mode=card] .w{display:none}
html[data-mode=card] .card{display:flex;flex-direction:column;justify-content:center;gap:16px;height:100%;padding:2px}
html[data-shape=compact] .card .fig:not(.lead),html[data-shape=compact] .card .chart{display:none}
html[data-shape=compact] .card .lead b{font-size:clamp(36px,9vw,56px)}
html[data-shape=tall] .card{justify-content:flex-start;gap:12px;padding-top:6px}
html[data-shape=tall] .card .figs{grid-template-columns:repeat(3,minmax(0,1fr));gap:18px 14px}
html[data-shape=tall] .card .lead{grid-column:1/-1}
html[data-shape=tall] .card .lead b{font-size:clamp(40px,12vw,60px)}
html[data-shape=tall] .card .fig:not(.lead) b{font-size:clamp(20px,5.4vw,26px)}
html[data-shape=tall] .card .chart{flex:1 1 auto;display:flex;flex-direction:column;min-height:56px}
html[data-shape=tall] .card .chart svg{flex:1 1 auto;height:auto;min-height:32px}
</style>
<div class="card">
  <div class="figs">
    <div class="fig lead"><b data-k="sleep">—</b><span>average sleep, last 7 nights</span></div>
    <div class="fig"><b data-k="steps">—</b><span>steps a day</span></div>
    <div class="fig"><b data-k="heart">—</b><span>resting heart rate</span></div>
    <div class="fig"><b data-k="mood">—</b><span>evening mood, of 5</span></div>
  </div>
  <div class="chart"><h2>Steps, last 14 days</h2><svg data-c="steps" role="img" aria-label="Steps each day"></svg></div>
  <p class="note">DEMO · fictional data</p>
</div>
<div class="w">
  <div><div class="tag">DEMO</div><h1>Week at a glance</h1></div>
  <div class="figs">
    <div class="fig"><b data-k="sleep">—</b><span>average sleep, last 7 nights</span></div>
    <div class="fig"><b data-k="steps">—</b><span>steps a day</span></div>
    <div class="fig"><b data-k="heart">—</b><span>resting heart rate</span></div>
    <div class="fig"><b data-k="mood">—</b><span>evening mood, of 5</span></div>
  </div>
  <div class="chart"><h2>Steps per day</h2><svg data-c="steps" role="img" aria-label="Steps each day"></svg></div>
  <div class="chart"><h2>Sleep per night</h2><svg data-c="sleep" role="img" aria-label="Sleep each night"></svg></div>
  <div class="chart"><h2>Evening mood</h2><svg data-c="mood" role="img" aria-label="Mood each evening"></svg></div>
  <p class="note" id="about"></p>
</div>
<script>
(function () {
  var last = null, ns = "http://www.w3.org/2000/svg";
  function rows(f) { return f && f.status === "ok" ? f.rows : []; }
  function week(rs, key) {
    rs = rs.slice(-7);
    return rs.length ? rs.reduce(function (a, r) { return a + r[key]; }, 0) / rs.length : null;
  }
  function put(k, text) { document.querySelectorAll("[data-k=" + k + "]").forEach(function (e) { e.textContent = text; }); }
  function bars(svg, rs, key, color, floor, top) {
    var w = svg.clientWidth || 300, h = svg.clientHeight || 120;
    svg.setAttribute("viewBox", "0 0 " + w + " " + h);
    svg.textContent = "";
    if (!rs.length) return;
    top = top || Math.max.apply(null, rs.map(function (r) { return r[key]; })) || 1;
    var step = w / rs.length;
    rs.forEach(function (r, i) {
      var bh = Math.max(2, (h - 2) * (r[key] - floor) / Math.max(top - floor, 1)), b = document.createElementNS(ns, "rect");
      b.setAttribute("x", i * step + step * 0.18); b.setAttribute("y", h - bh);
      b.setAttribute("width", Math.max(step * 0.64, 1)); b.setAttribute("height", bh); b.setAttribute("rx", 2);
      b.setAttribute("fill", color); b.setAttribute("opacity", i >= rs.length - 7 ? 0.9 : 0.4);
      svg.appendChild(b);
    });
  }
  function draw(d) {
    last = d;
    var f = d.feeds, sleep = rows(f.sleep), steps = rows(f.steps), heart = rows(f.heart), mood = rows(f.mood);
    var s = week(sleep, "mean"), p = week(steps, "sum"), r = week(heart, "mean"), m = week(mood, "mean");
    put("sleep", s === null ? "—" : Math.floor(s / 60) + " h " + Math.round(s % 60) + " m");
    put("steps", p === null ? "—" : Math.round(p).toLocaleString("en-GB"));
    put("heart", r === null ? "—" : Math.round(r) + " bpm");
    put("mood", m === null ? "—" : m.toFixed(1));
    document.querySelectorAll("svg[data-c]").forEach(function (svg) {
      var k = svg.getAttribute("data-c");
      if (k === "steps") bars(svg, steps, "sum", "var(--rhythm-knowledge)", 0);
      else if (k === "sleep") bars(svg, sleep, "mean", "var(--rhythm-memory)", 240);
      else bars(svg, mood, "mean", "var(--rhythm-health)", 0, 5);
    });
    var missing = ["sleep", "steps", "heart", "mood"].filter(function (k) { return !f[k] || f[k].status !== "ok"; });
    document.getElementById("about").textContent = missing.length
      ? "Unavailable right now: " + missing.join(", ") + "."
      : "Fictional demo data: the last 14 days, with the last 7 brighter.";
  }
  rhythm.onData(draw);
  addEventListener("resize", function () { if (last) draw(last); });
})();
</script>`;
// A data feed by day, from the same fictional readings the stars open with.
function byDay(feed: DashboardFeed, title: string, asOf: string): FeedPayload {
  const series = demoReadings(feed.star!) ?? [];
  const rows = series.flatMap((m) =>
    m.days.slice(-(feed.days ?? 14)).map((d) => ({
      metric: m.metric,
      unit: m.unit,
      date: d.date,
      min: d.min,
      mean: d.mean,
      max: d.max,
      sum: d.mean * d.count,
      count: d.count,
    })),
  );
  return {
    kind: "data",
    star: { graphId: feed.star!, title },
    status: "ok",
    asOf,
    by: "day",
    days: feed.days ?? 14,
    metrics: series.map((m) => ({
      metric: m.metric,
      label: m.label,
      unit: m.unit,
    })),
    rows,
    truncated: false,
  };
}
const stars = [
  { graphId: training, title: "Training" },
  { graphId: run, title: "Sunday long run" },
  { graphId: base, title: "Building a base" },
];
const feeds: DashboardFeed[] = [
  { name: "workouts", kind: "data", star: training, days: 7, by: "reading" },
  { name: "plan", kind: "contents", star: base },
  { name: "around", kind: "links", star: training, limit: 40 },
  { name: "efforts", kind: "log", log: "training", subject: "alex", days: 7 },
];
// Version 2 may add how hard a day felt to a fictional training log.
const writes: DashboardWrite[] = [
  { name: "effort", kind: "log", log: "training", subject: "alex" },
];

const page = `<style>
  .week{display:grid;gap:clamp(18px,3vw,30px);max-width:980px;margin:0 auto;padding:clamp(12px,3vw,24px) clamp(16px,3vw,24px)}
  .head{display:flex;flex-wrap:wrap;align-items:baseline;justify-content:space-between;gap:8px 24px}
  .head h1{font-size:clamp(20px,2.4vw,26px);margin:0}
  .head p{margin:0;color:var(--rhythm-muted)}
  .sports{display:flex;flex-wrap:wrap;gap:clamp(14px,4vw,48px)}
  .sport{background:none;border:0;padding:0;color:inherit;font:inherit;text-align:left;cursor:pointer}
  .sport b{display:block;font-family:var(--rhythm-display);font-size:clamp(26px,4vw,38px);font-weight:500;letter-spacing:-.01em}
  .sport span{color:var(--rhythm-muted);font-size:13px;display:flex;align-items:center;gap:7px}
  .sport i{width:9px;height:9px;border-radius:50%;display:inline-block}
  .sport:focus-visible{outline:1px solid var(--rhythm-knowledge);outline-offset:6px;border-radius:4px}
  svg text{fill:var(--rhythm-muted);font-size:11px}
  .plan{border-left:1px solid var(--rhythm-line);padding-left:16px;color:var(--rhythm-muted);white-space:pre-line;max-width:62ch}
  .missing{color:var(--rhythm-caution)}
  .effort{display:flex;flex-wrap:wrap;align-items:center;gap:8px 14px}
  .effort p{margin:0;color:var(--rhythm-muted)}
  .effort .scale{display:flex;flex-wrap:wrap;gap:6px}
  .effort button{width:34px;height:34px;border-radius:50%;border:1px solid var(--rhythm-line);background:none;color:var(--rhythm-text);font:inherit;cursor:pointer}
  .effort button[aria-pressed="true"]{background:var(--rhythm-health);color:#0b1418;border-color:transparent}
  .effort button:disabled{opacity:.45;cursor:default}
  .card{display:none;height:100%;flex-direction:column;justify-content:center;gap:6px;padding:4px 2px}
  .card b{font-family:var(--rhythm-display);font-size:clamp(40px,9vw,64px);font-weight:500;line-height:1}
  .card span{color:var(--rhythm-muted)}
  .card svg{display:none;flex:1 1 auto;min-height:80px;margin-top:14px}
  html[data-mode="card"] .week{display:none}
  html[data-mode="card"] .card{display:flex}
  html[data-mode="card"],html[data-mode="card"] body{height:100%;overflow:hidden}
  html[data-shape="tall"] .card{justify-content:flex-start;padding-top:10px}
  html[data-shape="tall"] .card svg{display:block}
</style>
<div class="card" aria-live="polite"><span>This week in training</span><b id="cardHours"></b><span id="cardEffort"></span><svg id="cardChart" role="img" aria-label="Minutes each day" viewBox="0 0 700 220" preserveAspectRatio="none"></svg></div>
<div class="week">
  <div class="head"><h1>This week in training</h1><p id="total"></p></div>
  <div class="sports" id="sports"></div>
  <svg id="chart" role="img" aria-label="Minutes each day by sport" viewBox="0 0 700 220" width="100%"></svg>
  <div class="effort"><p id="effortNote">How hard did today feel?</p><div class="scale" id="scale"></div></div>
  <p class="plan" id="plan"></p>
</div>
<script>
  const colors = { running: "var(--rhythm-health)", cycling: "var(--rhythm-knowledge)", lap_swimming: "var(--rhythm-memory)" };
  const names = { running: "Run", cycling: "Ride", lap_swimming: "Swim" };
  const starOf = { running: "${run}", cycling: "${training}", lap_swimming: "${training}" };
  rhythm.onData(({ feeds, edits }) => {
    const log = feeds.efforts, today = (feeds.workouts.to || "");
    const logged = log && log.status === "ok" ? log.entries.filter((e) => e.metric === "rpe" && e.date === today) : [];
    const effort = logged.length ? logged[logged.length - 1].value : null;
    document.getElementById("cardEffort").textContent = effort === null ? "No effort logged today" : "Today felt " + effort + " of 10";
    const scale = document.getElementById("scale"), note = document.getElementById("effortNote");
    scale.textContent = "";
    note.textContent = edits ? "How hard did today feel?" : "How hard did today feel? Edits are off for this dashboard.";
    for (let n = 1; n <= 10; n++) {
      const b = document.createElement("button");
      b.textContent = n; b.disabled = !edits; b.setAttribute("aria-pressed", String(n === effort));
      b.setAttribute("aria-label", "Today felt " + n + " of 10");
      b.onclick = async () => {
        const answer = await rhythm.write("effort", { metric: "rpe", value: n, unit: "score" });
        if (!answer.ok) note.textContent = answer.reason;
      };
      scale.append(b);
    }
    const w = feeds.workouts, days = [], byDay = {}, totals = {};
    if (w.status !== "ok") { document.getElementById("total").textContent = "Workouts unavailable: " + w.reason; return; }
    for (let i = 6; i >= 0; i--) { const d = new Date(w.to + "T12:00:00"); d.setDate(d.getDate() - i); days.push(d.toISOString().slice(0, 10)); }
    for (const r of w.rows) {
      const sport = (w.records[r.record] || {}).sport || "other";
      byDay[r.date] = byDay[r.date] || {}; byDay[r.date][sport] = (byDay[r.date][sport] || 0) + r.value / 60;
      totals[sport] = (totals[sport] || 0) + r.value / 60;
    }
    const all = Object.values(totals).reduce((a, b) => a + b, 0);
    document.getElementById("total").textContent = Math.round(all / 6) / 10 + " hours over seven days";
    document.getElementById("cardHours").textContent = Math.round(all / 6) / 10 + " h";
    const sports = document.getElementById("sports"); sports.textContent = "";
    for (const s of Object.keys(names)) {
      const b = document.createElement("button"); b.className = "sport";
      b.innerHTML = "<b></b><span><i></i></span>";
      b.querySelector("b").textContent = Math.round(totals[s] || 0) + " min";
      b.querySelector("i").style.background = colors[s];
      b.querySelector("span").append(names[s]);
      b.onclick = () => rhythm.openStar(starOf[s]);
      sports.append(b);
    }
    const max = Math.max(60, ...days.map((d) => Object.values(byDay[d] || {}).reduce((a, b) => a + b, 0)));
    const ns = "http://www.w3.org/2000/svg", el = (n, a) => { const e = document.createElementNS(ns, n); for (const k in a) e.setAttribute(k, a[k]); return e; };
    for (const [svg, labels] of [[document.getElementById("chart"), true], [document.getElementById("cardChart"), false]]) {
      svg.textContent = "";
      svg.append(el("line", { x1: 0, x2: 700, y1: 190, y2: 190, stroke: "var(--rhythm-line)" }));
      days.forEach((d, i) => {
        let y = 190; const x = 20 + i * 98;
        for (const s of Object.keys(names)) {
          const h = ((byDay[d] || {})[s] || 0) / max * 165; if (!h) continue;
          y -= h; svg.append(el("rect", { x, y, width: 58, height: Math.max(h - 2, 1), rx: 3, fill: colors[s], opacity: 0.78 }));
        }
        if (!labels) return;
        const t = el("text", { x: x + 29, y: 210, "text-anchor": "middle" });
        t.textContent = new Date(d + "T12:00:00").toLocaleDateString("en-US", { weekday: "short" });
        svg.append(t);
      });
    }
    const plan = document.getElementById("plan");
    plan.className = feeds.plan.status === "ok" ? "plan" : "plan missing";
    plan.textContent = feeds.plan.status === "ok" ? feeds.plan.text : "The plan is unavailable: " + feeds.plan.reason;
  });
</script>`;
// The demo's whole map, as its sky's fictional sources hold it, read once when
// a focused map part first needs it: a focus filters all of it, as the engine
// filters its whole map, so nothing that matches is dropped. Loaded only then,
// so the demo's sky data stays out of the app until it is wanted.
let wholeMap: Promise<Pick<GraphPage, "nodes" | "edges">> | null = null;
const demoMap = () =>
  (wholeMap ??= import("./demo").then(
    async ({ createDemoSession, demoData }) => {
      const source = createDemoSession(demoData);
      let page = await source.retrieve("discover", "");
      let all: GraphPage = page;
      for (
        let more = 0;
        more < 100 &&
        Object.values(page.cursors).some((c) => typeof c === "string");
        more++
      ) {
        page = await source.retrieve("discover", "", page.cursors);
        all = mergePage(all, page);
      }
      return { nodes: all.nodes, edges: all.edges };
    },
  ));

// The demo's dashboard with a layout (docs/dev/screens.md 10.10): the map on
// the left, focused near the training star, its page at the top right and the
// glucose tile below the page. The page draws only what the parts don't.
const mapsId = "00000000-0000-4000-8000-00000000d0a7";
const mapsLayout: Layout = {
  parts: [
    {
      id: "map",
      kind: "map",
      x: 0,
      y: 0,
      w: 7,
      h: 12,
      focus: { near: { star: training, depth: 1 } },
    },
    { id: "page", kind: "page", x: 7, y: 0, w: 5, h: 8 },
    { id: "glucose", kind: "glucose", x: 7, y: 8, w: 5, h: 4 },
  ],
};
const mapsStars = [
  { graphId: training, title: "Training" },
  { graphId: base, title: "Building a base" },
];
const mapsFeeds: DashboardFeed[] = [
  { name: "workouts", kind: "data", star: training, days: 7, by: "reading" },
  { name: "plan", kind: "contents", star: base },
];
const mapsPage = `<style>
.m{display:grid;align-content:start;gap:16px;max-width:62ch;padding:clamp(12px,3vw,22px) clamp(14px,3vw,22px)}
.tag{font-size:11px;letter-spacing:.14em;color:var(--rhythm-caution)}
.m h1{font-size:clamp(19px,2.2vw,24px);margin:2px 0 0}
.fig b{display:block;font-family:var(--rhythm-display);font-weight:500;font-size:clamp(26px,3.4vw,34px);line-height:1.1}
.fig span,.m p{color:var(--rhythm-muted);font-size:13px;line-height:1.6;margin:0}
.plan{border-left:1px solid var(--rhythm-line);padding-left:14px;white-space:pre-line}
</style>
<div class="m">
  <div><div class="tag">DEMO</div><h1>Training around the plan</h1></div>
  <div class="fig"><b id="hours">—</b><span>of sample workouts over seven days</span></div>
  <p class="plan" id="plan"></p>
  <p>Fictional data. The map beside this page shows the stars near Training.</p>
</div>
<script>
rhythm.onData(({ feeds }) => {
  const w = feeds.workouts, p = feeds.plan;
  document.getElementById("hours").textContent = w.status === "ok"
    ? Math.round(w.rows.reduce((a, r) => a + r.value, 0) / 360) / 10 + " h" : "—";
  document.getElementById("plan").textContent = p.status === "ok" ? p.text : "The plan is unavailable: " + p.reason;
});
</script>`;

const firstPage = `<h1>This week in training</h1><p id="n"></p>
<script>rhythm.onData(({ feeds }) => { document.getElementById("n").textContent = feeds.workouts.status === "ok" ? feeds.workouts.rows.length + " sample workouts" : "Unavailable"; });</script>`;

const sports = ["running", "cycling", "lap_swimming"];
function workouts(today = new Date()): FeedPayload {
  const rows = [],
    records: Record<
      string,
      { kind: string; title: string; date: string; sport: string }
    > = {};
  for (let back = 6; back >= 0; back--) {
    const d = new Date(today);
    d.setDate(d.getDate() - back);
    const date = d.toLocaleDateString("en-CA");
    // One or two fictional sessions a day, varied but the same on every open.
    const seed = d.getDate();
    for (const [k, sport] of sports.entries()) {
      if ((seed + k) % 3 === 0 && back !== 3) {
        const record = `sample-${date}-${sport}`;
        records[record] = {
          kind: "activity",
          title: `Sample ${sport.replace("_", " ")}`,
          date,
          sport,
        };
        rows.push({
          metric: "activity_duration",
          unit: "s",
          value: (25 + ((seed * 7 + k * 11) % 40)) * 60,
          at: `${date}T12:00:00.000Z`,
          date,
          record,
        });
      }
    }
  }
  const to = today.toLocaleDateString("en-CA");
  const from = new Date(today);
  from.setDate(from.getDate() - 6);
  return {
    kind: "data",
    star: { graphId: training, title: "Training" },
    status: "ok",
    asOf: new Date().toISOString(),
    by: "reading",
    days: 7,
    from: from.toLocaleDateString("en-CA"),
    to,
    metrics: [
      { metric: "activity_duration", label: "Activity duration", unit: "s" },
    ],
    rows,
    records,
    truncated: false,
  };
}

export function createDemoDashboards() {
  const saved = "2026-10-18T20:00:00.000Z";
  // What the demo's visitor did with it, in memory only.
  let allowed = false;
  const entries: {
    id: string;
    date: string;
    type: "number";
    metric: string;
    unit: string;
    value: number;
    at: string;
  }[] = [];
  const history: DashboardWriteRecord[] = [];
  // The week at a glance fills the main slot and the training week a corner,
  // until the visitor chooses otherwise.
  const cards: DashboardCards = { main: glanceId, recovery: id };
  let written = 0;
  const versions = [
    {
      version: 2,
      note: "Fictional: minutes by sport, each day stacked.",
      restoredFrom: null,
      savedAt: saved,
    },
    {
      version: 1,
      note: "Fictional: a first count of the week's sessions.",
      restoredFrom: null,
      savedAt: "2026-10-17T20:00:00.000Z",
    },
  ];
  const summary = (version: number): DashboardSummary => ({
    id,
    version,
    latest: 2,
    title: "Sample training week",
    description:
      "A fictional dashboard: a week of sample workouts by sport, beside a sample plan.",
    stars,
    note: versions.find((v) => v.version === version)!.note,
    restoredFrom: null,
    savedAt: versions.find((v) => v.version === version)!.savedAt,
    createdAt: "2026-10-17T20:00:00.000Z",
    updatedAt: saved,
    versions,
    writes: version === 2 ? writes : [],
    editsAllowed: allowed && version === 2,
    corners: corners.filter((c) => cards[c] === id),
    ...(version === 2 ? arrangement(id) : unarranged),
  });
  const glance = (): DashboardSummary => ({
    id: glanceId,
    version: 1,
    latest: 1,
    title: "Week at a glance",
    description:
      "A fictional dashboard: sleep, steps, resting heart rate and evening mood over two weeks.",
    stars: glanceStars,
    note: "Fictional: the week's sleep, steps, heart rate and mood, with a tall card.",
    restoredFrom: null,
    savedAt: "2026-10-16T20:00:00.000Z",
    createdAt: "2026-10-16T20:00:00.000Z",
    updatedAt: "2026-10-16T20:00:00.000Z",
    versions: [
      {
        version: 1,
        note: "Fictional: the week's sleep, steps, heart rate and mood, with a tall card.",
        restoredFrom: null,
        savedAt: "2026-10-16T20:00:00.000Z",
      },
    ],
    writes: [],
    editsAllowed: false,
    corners: corners.filter((c) => cards[c] === glanceId),
    ...arrangement(glanceId),
  });
  const mapsSaved = "2026-10-15T20:00:00.000Z",
    mapsNote =
      "Fictional: the map near Training, a page for the plan, and glucose.";
  const maps = (): DashboardSummary => ({
    id: mapsId,
    version: 1,
    latest: 1,
    title: "Training map",
    description:
      "A fictional dashboard: the map around Training beside a sample plan, with glucose below.",
    stars: mapsStars,
    note: mapsNote,
    restoredFrom: null,
    savedAt: mapsSaved,
    createdAt: mapsSaved,
    updatedAt: mapsSaved,
    versions: [
      { version: 1, note: mapsNote, restoredFrom: null, savedAt: mapsSaved },
    ],
    writes: [],
    editsAllowed: false,
    corners: corners.filter((c) => cards[c] === mapsId),
    ...arrangement(mapsId),
  });
  const refused = (reason: string): DashboardWriteAnswer => ({
    ok: false,
    reason,
  });
  const plan = (asOf: string): FeedPayload => ({
    kind: "contents",
    star: { graphId: base, title: "Building a base" },
    status: "ok",
    asOf,
    format: "text",
    title: "Sample plan",
    text: "Sample plan · three easy sessions, one longer ride at the weekend, and a rest day midweek. This plan is fictional.",
    truncated: false,
  });
  // Each dashboard's newest version as the visitor arranged it, in memory only:
  // its layout and revision, and what the last change said.
  const latest: Record<string, number> = {
    [id]: 2,
    [glanceId]: 1,
    [mapsId]: 1,
  };
  const arranged = new Map<string, DashboardArrangement>([
    [mapsId, { ...unarranged, layout: mapsLayout }],
  ]);
  function arrangement(dashboard: string): DashboardArrangement {
    return structuredClone(arranged.get(dashboard) ?? unarranged);
  }
  return {
    list: (): DashboardSummary[] => [summary(2), glance(), maps()],
    get(dashboard: string, version?: number): Dashboard | null {
      if (dashboard === glanceId)
        return (version ?? 1) === 1
          ? { ...glance(), feeds: glanceFeeds, html: glancePage }
          : null;
      if (dashboard === mapsId)
        return (version ?? 1) === 1
          ? { ...maps(), feeds: mapsFeeds, html: mapsPage }
          : null;
      version ??= 2;
      if (dashboard !== id || ![1, 2].includes(version)) return null;
      return {
        ...summary(version),
        feeds,
        html: version === 2 ? page : firstPage,
      };
    },
    data(dashboard: string, version?: number): DashboardData | null {
      const asOf = new Date().toISOString();
      if (dashboard === glanceId)
        return (version ?? 1) === 1
          ? {
              asOf,
              dashboard: { id: glanceId, version: 1 },
              feeds: Object.fromEntries(
                glanceFeeds.map((f, i) => [
                  f.name,
                  byDay(f, glanceStars[i].title, asOf),
                ]),
              ),
            }
          : null;
      // The feed read says how the version is arranged now, as the engine's does.
      if (dashboard === mapsId)
        return (version ?? 1) === 1
          ? {
              asOf,
              dashboard: { id: mapsId, version: 1, ...arrangement(mapsId) },
              feeds: { workouts: workouts(), plan: plan(asOf) },
            }
          : null;
      version ??= 2;
      if (dashboard !== id || ![1, 2].includes(version)) return null;
      return {
        asOf,
        dashboard: { id, version },
        feeds: {
          workouts: workouts(),
          plan: plan(asOf),
          around: {
            kind: "links",
            star: { graphId: training, title: "Training" },
            status: "ok",
            asOf,
            stars: stars.slice(1).map((s) => ({
              graphId: s.graphId,
              title: s.title,
              type: "record",
            })),
            links: [],
            truncated: false,
          },
          efforts: {
            kind: "log",
            log: "training",
            subject: "alex",
            status: "ok",
            asOf,
            days: 7,
            entries: entries.map((e) => ({ ...e })),
            truncated: false,
          },
        },
      };
    },
    // A day's effort, 1-10, in the fictional log, once edits are allowed.
    write(
      dashboard: string,
      version: number,
      name: string,
      change: unknown,
    ): DashboardWriteAnswer {
      if (dashboard !== id || version !== 2 || name !== "effort")
        return refused(`This dashboard declares no write named ${name}.`);
      if (!allowed) return refused("Edits are off for this dashboard.");
      const c = change as { metric?: unknown; value?: unknown; unit?: unknown };
      if (
        c?.metric !== "rpe" ||
        !Number.isInteger(c.value) ||
        (c.value as number) < 1 ||
        (c.value as number) > 10 ||
        c.unit !== "score"
      )
        return refused("Effort is a number from 1 to 10.");
      const at = new Date();
      const date = at.toLocaleDateString("en-CA");
      const entry = {
        id: `00000000-0000-4000-8000-${String(++written).padStart(12, "0")}`,
        date,
        type: "number" as const,
        metric: "rpe",
        unit: "score",
        value: c.value as number,
        at: at.toISOString(),
      };
      entries.push(entry);
      const record: DashboardWriteRecord = {
        id: entry.id,
        dashboard: id,
        version,
        name,
        kind: "log",
        summary: `Training log: effort ${entry.value} of 10 for ${at.toLocaleDateString("en-US", { month: "short", day: "numeric" })}`,
        writtenAt: entry.at,
        undoneAt: null,
      };
      history.unshift(record);
      return { ok: true, write: { ...record } };
    },
    undo(write: string): DashboardWriteAnswer {
      const record = history.find((w) => w.id === write);
      if (!record) return refused("That change isn't in the history.");
      if (record.undoneAt) return refused("That change was already undone.");
      record.undoneAt = new Date().toISOString();
      entries.splice(
        entries.findIndex((e) => e.id === write),
        1,
      );
      return { ok: true, write: { ...record } };
    },
    allowEdits(dashboard: string, version: number, on: boolean) {
      if (dashboard !== id || version !== 2)
        throw new Error("This version writes nothing.");
      allowed = on;
      return allowed;
    },
    // An arrangement of a dashboard's newest version, kept in memory with the
    // engine's checks: the newest version only, the revision it started from
    // (else the layout as it stands, to apply the change to once more), the
    // same parts, and the rules every layout keeps.
    layout(
      dashboard: string,
      version: number,
      layout: Layout,
      base: number,
      note: string,
    ): LayoutAnswer {
      if (!Object.hasOwn(latest, dashboard))
        return { ok: false, reason: "That dashboard or version is unknown." };
      if (version !== latest[dashboard])
        return {
          ok: false,
          reason: "This is an earlier version; open the newest to arrange it.",
        };
      const now = arrangement(dashboard);
      if (base !== now.layoutRevision)
        return {
          ok: false,
          reason:
            "The layout changed since; apply the change to it and save again.",
          layout: now.layout,
          layoutWhole: true,
          layoutRevision: now.layoutRevision,
        };
      const problem =
        arrangementOnly(now.layout, layout) ?? layoutProblem(layout, dashboard);
      if (problem) return { ok: false, reason: `${problem}.` };
      const next: DashboardArrangement = {
        layout: structuredClone(layout),
        layoutWhole: true,
        layoutRevision: now.layoutRevision + 1,
        layoutNote: note,
        layoutSavedAt: new Date().toISOString(),
      };
      arranged.set(dashboard, next);
      return {
        ok: true,
        layout: structuredClone(layout),
        layoutWhole: true,
        layoutRevision: next.layoutRevision,
      };
    },
    // A map part's focused sky, filtered in the browser from the demo's whole
    // map with the engine's rules (mapFocus.ts).
    async map(
      dashboard: string,
      version: number | undefined,
      part: string,
      cursor?: string | null,
    ): Promise<DashboardMap | null> {
      if ((version ?? latest[dashboard]) !== latest[dashboard]) return null;
      const shown = layoutOf(arrangement(dashboard).layout).parts.find(
        (p) => p.id === part,
      );
      if (shown?.kind !== "map" || !shown.focus) return null;
      return {
        part,
        focus: shown.focus,
        ...focusedPage(await demoMap(), shown.focus, { cursor }),
      };
    },
    cards: (): DashboardCards => ({ ...cards }),
    setCard(corner: Corner, dashboard: string | null): DashboardCards {
      if (dashboard === null) delete cards[corner];
      else if (dashboard === id || dashboard === glanceId)
        cards[corner] = dashboard;
      return { ...cards };
    },
  };
}
