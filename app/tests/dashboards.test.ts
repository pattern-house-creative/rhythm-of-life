import { JSDOM } from "jsdom";
import { expect, it } from "vitest";
import {
  cleanDashboard,
  cleanDashboardData,
  cleanSummaries,
  dashboardStarId,
  isDashboardId,
  isDashboardStar,
  listedMax,
  listedSummaries,
  withDashboards,
} from "../src/dashboards";
import {
  framePolicy,
  frameDocument,
  sandboxDocument,
  wrapperPolicy,
} from "../src/dashboardFrame";
import { createDemoDashboards } from "../src/demoDashboards";
import { emptyGraph } from "../src/liveGraph";
import type { GraphNode } from "../src/knowledge";

const boards = createDemoDashboards();
const [summary] = boards.list();
const id = summary.id;

it("keeps only dashboards that match the contract, failing closed", () => {
  expect(cleanSummaries({ dashboards: [summary] })).toEqual([summary]);
  for (const broken of [
    { ...summary, id: "not-a-uuid" },
    { ...summary, title: "" },
    { ...summary, version: 3 },
    { ...summary, stars: [] },
    { ...summary, stars: [{ graphId: "", title: "x" }] },
    {
      ...summary,
      versions: [
        { version: 1, note: "", restoredFrom: null, savedAt: summary.savedAt },
      ],
    },
  ])
    expect(cleanSummaries({ dashboards: [broken, summary] })).toEqual([
      summary,
    ]);
  expect(cleanSummaries({ dashboards: "nope" })).toEqual([]);
  expect(cleanSummaries({ dashboards: [summary, summary] })).toHaveLength(1);
  // Read as the engine's answer, a malformed envelope is a failed read, not "no dashboards".
  expect(
    listedSummaries({ dashboards: [summary, { ...summary, id: "x" }] }),
  ).toEqual([summary]);
  expect(listedSummaries({ dashboards: [] })).toEqual([]);
  for (const envelope of [
    null,
    { dashboards: "nope" },
    { boards: [summary] },
    { dashboards: Array.from({ length: listedMax + 1 }, () => summary) },
  ])
    expect(() => listedSummaries(envelope)).toThrow();
  // A version needs its page and feeds; unknown fields are left behind.
  const dashboard = boards.get(id)!;
  expect(cleanDashboard({ dashboard: { ...dashboard, extra: "x" } })).toEqual(
    dashboard,
  );
  expect(cleanDashboard({ dashboard: { ...dashboard, html: "" } })).toBeNull();
  expect(
    cleanDashboard({
      dashboard: {
        ...dashboard,
        feeds: [{ name: "Bad", kind: "data", star: "x" }],
      },
    }),
  ).toBeNull();
  expect(cleanDashboard(dashboard)).toBeNull();
  // What the feeds hold passes through; a feed without a status or star does not.
  const data = boards.data(id)!;
  expect(cleanDashboardData(data)).toEqual(data);
  expect(
    cleanDashboardData({
      ...data,
      feeds: { ...data.feeds, plan: { ...data.feeds.plan, status: "maybe" } },
    }),
  ).toBeNull();
  expect(
    cleanDashboardData({ ...data, feeds: { "1x": data.feeds.plan } }),
  ).toBeNull();
  expect(
    cleanDashboardData({ ...data, dashboard: { id: "x", version: 1 } }),
  ).toBeNull();
});

it("puts each dashboard on the sky as a star linked to the stars it reads", () => {
  const star = (graphId: string): GraphNode => ({
    id: graphId,
    title: graphId,
    type: "record",
    overview: "",
    provenance: {
      sourceId: "garmin",
      recordId: graphId,
      observedAt: "2026-10-18T20:00:00Z",
      demo: true,
    },
    resource: {
      identity: { provider: "garmin", account: "fixture", externalId: graphId },
      canonicalUrl: null,
      sourceUrl: null,
      sourceModifiedAt: null,
      retrievedAt: "2026-10-18T20:00:00Z",
      revision: null,
      owner: "fixture",
      access: { scope: "demo", canRead: true, canOpen: false },
      availability: "available",
      content: null,
    },
  });
  const [training, run] = summary.stars.map((s) => s.graphId);
  const page = { ...emptyGraph(), nodes: [star(training), star(run)] };
  const sky = withDashboards(page, [summary], true);
  const board = sky.nodes.find((n) => n.id === dashboardStarId(id))!;
  expect(board.title).toBe("Sample training week");
  expect(isDashboardStar(board)).toBe(true);
  expect(isDashboardId(board.id)).toBe(true);
  expect(sky.nodes.filter((n) => !isDashboardStar(n))).toHaveLength(2);
  // Only the stars on the map are linked; the third isn't loaded here.
  expect(
    sky.edges.map((e) => [e.source, e.target, e.type, e.verification]),
  ).toEqual([
    [board.id, training, "draws_on", "verified"],
    [board.id, run, "draws_on", "verified"],
  ]);
  expect(withDashboards(page, [])).toBe(page);
  // Not before any star it reads is on the map: no lone dashboard while the sky loads.
  expect(withDashboards(emptyGraph(), [summary]).nodes).toHaveLength(0);
  expect(withDashboards(sky, [summary]).nodes).toHaveLength(3);
  // Unless a screen was asked to open it: then it joins whatever is on the map.
  expect(
    withDashboards(emptyGraph(), [summary], true, id).nodes.map((n) => n.id),
  ).toEqual([dashboardStarId(id)]);
  expect(
    withDashboards(emptyGraph(), [summary], true, "another-dashboard").nodes,
  ).toHaveLength(0);
});

it("runs a page behind two sandboxes: no requests, and no navigation", () => {
  const page = '<p>Hi</p><script>document.title = "</script><b>"</script>';
  const inner = frameDocument(page);
  // The page's own policy is parsed first, before any of its markup.
  expect(inner.indexOf(framePolicy)).toBeLessThan(inner.indexOf("<style>"));
  expect(framePolicy).toContain("default-src 'none'");
  expect(framePolicy).toContain("connect-src 'none'");
  expect(inner).toContain("--rhythm-health:#75d8b9");
  expect(inner.endsWith(`<body>${page}</body></html>`)).toBe(true);
  const outer = sandboxDocument(page, "Fictional dashboard");
  expect(outer).toContain(wrapperPolicy);
  expect(wrapperPolicy).toContain("frame-src 'none'");
  // The page inherits the wrapper's policy, so both let in the app's fonts,
  // and both send form data nowhere.
  expect(wrapperPolicy).toContain("font-src data:");
  expect(framePolicy).toContain("font-src data:");
  expect(wrapperPolicy).toContain("form-action 'none'");
  expect(framePolicy).toContain("form-action 'none'");
  // The page travels inside the wrapper's script, unable to close it early.
  const script = outer.slice(
    outer.indexOf("<script>") + 8,
    outer.lastIndexOf("</script>"),
  );
  expect(script).not.toContain("</script>");
  expect(script).not.toContain("<");
  expect(script).toContain(
    'inner.setAttribute("sandbox", "allow-scripts allow-forms")',
  );
  expect(outer.match(/<script>/g)).toHaveLength(1);
});

it("gives a page no peer connection, which would reach the network past its policy", () => {
  // Both policies name WebRTC too, for browsers that come to enforce it.
  expect(framePolicy).toContain("webrtc 'block'");
  expect(wrapperPolicy).toContain("webrtc 'block'");
  // A fictional page that would send its data out in a STUN lookup, in a
  // browser that has peer connections.
  const page =
    "<script>window.found = [typeof RTCPeerConnection, typeof webkitRTCPeerConnection, typeof window.rhythm];</script>";
  const { window } = new JSDOM(frameDocument(page), {
    runScripts: "dangerously",
    beforeParse(w) {
      Object.assign(w, {
        RTCPeerConnection: class {},
        webkitRTCPeerConnection: class {},
      });
    },
  });
  expect((window as unknown as { found: string[] }).found).toEqual([
    "undefined",
    "undefined",
    "object",
  ]);
  window.close();
});
