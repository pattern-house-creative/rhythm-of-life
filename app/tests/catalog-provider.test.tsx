import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import Explorer from "../src/Explorer";
import { handleInstance } from "../server/routes";
import { catalogProvider as configuredProvider } from "../server/config";
import {
  catalogProvider,
  cleanInstance,
  demoInstance,
  loadInstance,
  setInstance,
} from "../src/instance";
import {
  dashboardStarId,
  isDashboardId,
  isDashboardStar,
  withDashboards,
} from "../src/dashboards";
import { createDemoDashboards } from "../src/demoDashboards";
import { sourceName } from "../src/graphView";
import { sourceStar } from "../src/insights";
import {
  healthPanel,
  isDataStar,
  isSavedRecord,
  memoryText,
} from "../src/knowledge";
import type { GraphNode, GraphPage } from "../src/knowledge";
import { emptyGraph } from "../src/liveGraph";
import { dataStar } from "../src/panels/readings";
import type { KnowledgeController } from "../src/useKnowledge";
import { withEngine } from "./engine";
import { record, relation } from "./synthetic";

// An install that kept an older engine's ids names the engine's own stars (data stars, saved
// documents, dashboards) under another provider id, here the fictional "legacy"
// (RHYTHM_CATALOG_PROVIDER, docs/dev/migration-compat.md step 3).

// pdf.js draws on a canvas that jsdom does not have: a stand-in says what it was given.
vi.mock("../src/PdfView", () => ({
  default: ({ data }: { data: Uint8Array }) => (
    <div className="reader-text">
      <p>Fictional PDF pages from {data.length} bytes</p>
    </div>
  ),
}));

// The explorer opens dashboards in a code-split view; load it once with its own budget.
beforeAll(async () => {
  await import("../src/DashboardView");
}, 30_000);

const legacy = () =>
  setInstance({ ...demoInstance, mode: "live", catalogProvider: "legacy" });
const boards = createDemoDashboards();
const [summary] = boards.list();

// A star the engine keeps itself, under the provider id `provider`.
function engineStar(
  provider: string,
  account: string,
  externalId: string,
  title: string,
) {
  const star = record(externalId, provider, account);
  star.title = title;
  star.type = account.startsWith("data:") ? "record" : "document";
  star.provenance.demo = false;
  star.resource.content = null;
  star.resource.owner = externalId.split(":")[0];
  return star;
}

function controller(page: GraphPage): KnowledgeController {
  return {
    page,
    accept: vi.fn(),
    pins: [],
    togglePin: vi.fn(),
    busy: false,
    error: null,
    refresh: vi.fn(async () => {}),
    discoverMore: vi.fn(async () => {}),
    lastRefresh: null,
    epoch: 0,
    session: {
      initial: page,
      refresh: vi.fn(async () => page),
      retrieve: vi.fn(async () => ({ ...page, cursors: {} })),
    },
  };
}
function explore(knowledge: KnowledgeController, selected: string) {
  render(
    <Explorer
      knowledge={knowledge}
      selected={selected}
      onSelect={vi.fn()}
      close={vi.fn()}
      paused
      setPaused={vi.fn()}
      state="ready"
      onOpenPanel={vi.fn()}
    />,
  );
  return userEvent.setup();
}
// A fictional note linked to `star`, so the explorer has a neighbour to show.
function pageWith(star: GraphNode): GraphPage {
  const note = record(`note-about-${star.resource.identity.externalId}`);
  note.provenance.demo = false;
  return {
    ...emptyGraph(),
    nodes: [star, note],
    edges: [relation(note, star)],
  };
}

describe("the provider id at /api/instance", () => {
  const read = async () =>
    handleInstance(new Request("http://rhythm.example.invalid/api/instance"));

  it("is RHYTHM_CATALOG_PROVIDER in live mode, rhythm when it is unset or empty, and rhythm in demo mode", async () => {
    expect((await (await read()).json()).catalogProvider).toBe("rhythm");
    // Demo mode serves the app's own fictional stars, which are rhythm's.
    vi.stubEnv("RHYTHM_CATALOG_PROVIDER", "legacy");
    expect((await (await read()).json()).catalogProvider).toBe("rhythm");
    withEngine();
    expect((await (await read()).json()).catalogProvider).toBe("legacy");
    vi.stubEnv("RHYTHM_CATALOG_PROVIDER", "");
    expect((await (await read()).json()).catalogProvider).toBe("rhythm");
  });

  it("refuses a value that is no provider id, without repeating it", async () => {
    withEngine();
    vi.stubEnv("RHYTHM_CATALOG_PROVIDER", "Legacy Stars");
    const response = await read();
    expect(response.status).toBe(503);
    expect(await response.text()).not.toContain("Legacy");
    expect(() => configuredProvider()).toThrow(
      /^RHYTHM_CATALOG_PROVIDER is not a provider id/,
    );
    try {
      configuredProvider();
    } catch (error) {
      expect(String(error)).not.toContain("Legacy");
    }
  });

  it("reaches the browser with the settings; a server that names none means rhythm", async () => {
    withEngine();
    vi.stubEnv("RHYTHM_CATALOG_PROVIDER", "legacy");
    vi.mocked(fetch).mockImplementationOnce(async (url, init) =>
      handleInstance(
        new Request(new URL(String(url), "http://localhost"), init),
      ),
    );
    expect(catalogProvider()).toBe("rhythm");
    expect(await loadInstance()).toBe(true);
    expect(catalogProvider()).toBe("legacy");
    const settings = {
      name: "Test home",
      people: [{ id: "alex", name: "Alex" }],
      timezone: "Europe/London",
      mode: "live",
    };
    expect(cleanInstance(settings)?.catalogProvider).toBe("rhythm");
    expect(
      cleanInstance({ ...settings, catalogProvider: "Legacy" }),
    ).toBeNull();
    expect(cleanInstance({ ...settings, catalogProvider: 7 })).toBeNull();
  });
});

describe("with the provider set to legacy", () => {
  beforeEach(legacy);

  it("finds the engine's own stars by it, and builds their ids with it", () => {
    const recovery = engineStar(
      "legacy",
      "data:garmin",
      "alex:recovery",
      "Sleep and recovery",
    );
    const manual = engineStar(
      "legacy",
      "documents",
      "household:upload:fictional",
      "Fictional boiler manual",
    );
    expect(isDataStar(recovery)).toBe(true);
    expect(healthPanel(recovery)).toBe("recovery");
    expect(isSavedRecord(manual)).toBe(true);
    expect(dataStar("garmin", "recovery")).toBe(
      "legacy:data%3Agarmin:alex%3Arecovery",
    );
    expect(dataStar("garmin", "recovery")).toBe(recovery.id);
    // A star of the same name under the default id is someone else's now.
    expect(
      isDataStar(engineStar("rhythm", "data:garmin", "alex:recovery", "x")),
    ).toBe(false);
    // A memory the engine keeps has no other source to read its text from.
    const kept = record("household:memory-1", "legacy", "memories");
    kept.provenance.demo = false;
    expect(memoryText(kept)).toBe(false);
    expect(
      memoryText({
        ...kept,
        resource: {
          ...kept.resource,
          identity: { ...kept.resource.identity, provider: "second-brain" },
        },
      }),
    ).toBe(true);
    expect(dashboardStarId(summary.id)).toBe(`legacy:dashboard:${summary.id}`);
    expect(isDashboardId(`legacy:dashboard:${summary.id}`)).toBe(true);
    expect(isDashboardId(`rhythm:dashboard:${summary.id}`)).toBe(false);
    // An insight's engine source, of either kind, names one of these stars.
    for (const kind of ["rhythm", "nas"] as const)
      expect(
        sourceStar(
          {
            kind,
            title: "Sleep and recovery",
            locator: "alex:recovery",
            retrievedAt: "2026-10-27T09:30:00Z",
          },
          [recovery, manual],
        ),
      ).toBe(recovery.id);
    expect(sourceName("legacy")).toBe("Rhythm engine");
  });

  it("reads an uploaded document through the engine and opens its original", async () => {
    const star = engineStar(
      "legacy",
      "documents",
      "household:upload:fictional",
      "Fictional boiler manual",
    );
    const knowledge = controller(pageWith(star));
    const id = "3f2b8c1e-4d5a-4b6c-9e7f-0a1b2c3d4e5f";
    const text = vi.fn().mockResolvedValue({
      text: "Fictional saved text of the document.",
      modifiedAt: "2026-10-27T09:30:00Z",
      original: {
        id,
        format: "pdf",
        bytes: 18,
        name: "Fictional boiler manual.pdf",
      },
    });
    const original = vi.fn().mockResolvedValue({
      ok: true,
      bytes: new TextEncoder().encode("%PDF-1.4 fictional"),
    });
    knowledge.session.text = text;
    knowledge.session.original = original;
    const user = explore(knowledge, star.id);
    await user.click(screen.getByRole("button", { name: /Open document/ }));
    expect(text).toHaveBeenCalledWith(star.id, expect.any(AbortSignal));
    expect(
      await screen.findByText("Fictional PDF pages from 18 bytes"),
    ).toBeInTheDocument();
    expect(original).toHaveBeenCalledWith(id, expect.any(AbortSignal));
    expect(
      screen.getByText(
        /The file as it was added, from the Rhythm engine's archive/,
      ),
    ).toBeInTheDocument();
    expect(screen.queryByText(/citation metadata/)).not.toBeInTheDocument();
  });

  it("opens a data star's chart", async () => {
    const star = engineStar(
      "legacy",
      "data:garmin",
      "alex:recovery",
      "Sleep and recovery",
    );
    const knowledge = controller(pageWith(star));
    const data = vi.fn().mockResolvedValue([
      {
        metric: "sleep_duration",
        label: "Sleep duration",
        unit: "s",
        latest: { value: 27000, observedAt: null, date: "2026-10-27" },
        days: [
          { date: "2026-10-26", min: 25200, mean: 25200, max: 25200, count: 1 },
          { date: "2026-10-27", min: 27000, mean: 27000, max: 27000, count: 1 },
        ],
      },
    ]);
    knowledge.session.data = data;
    const user = explore(knowledge, star.id);
    expect(
      screen.getByRole("button", { name: /Open Recovery panel/ }),
    ).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: /Show readings/ }));
    expect(data).toHaveBeenCalledWith(star.id, expect.any(AbortSignal));
    expect(
      await screen.findByRole("slider", { name: "Sleep by day" }),
    ).toHaveAttribute("aria-valuetext", expect.stringMatching(/^7h 30m/));
  });

  it("opens a dashboard from its star, the id the engine's pins and insights name", async () => {
    const read = summary.stars.map((s, i) => {
      const star = record(`read-${i}`);
      star.id = s.graphId;
      star.provenance.demo = false;
      return star;
    });
    const sky = withDashboards({ ...emptyGraph(), nodes: read }, [summary]);
    const board = sky.nodes.find(
      (n) => n.id === `legacy:dashboard:${summary.id}`,
    )!;
    expect(isDashboardStar(board)).toBe(true);
    expect(board.provenance.sourceId).toBe("legacy");
    expect(sky.edges.every((e) => e.source === board.id)).toBe(true);
    const knowledge = controller(sky);
    knowledge.session.dashboard = vi.fn(async (id: string) => boards.get(id));
    knowledge.session.dashboardData = vi.fn(async (id: string) =>
      boards.data(id),
    );
    const user = explore(knowledge, board.id);
    await user.click(screen.getByRole("button", { name: /Open dashboard/ }));
    const reader = await screen.findByRole("region", { name: "Source reader" });
    expect(within(reader).getByText("DASHBOARD")).toBeInTheDocument();
    expect(
      await within(reader).findByTitle(
        "Sample training week dashboard",
        undefined,
        { timeout: 10000 },
      ),
    ).toBeInTheDocument();
    expect(knowledge.session.dashboard).toHaveBeenCalledWith(
      summary.id,
      undefined,
      expect.any(AbortSignal),
    );
  });
});

describe("with the default provider", () => {
  it("keeps rhythm: the same stars and the same ids as before", () => {
    expect(catalogProvider()).toBe("rhythm");
    const recovery = engineStar(
      "rhythm",
      "data:garmin",
      "alex:recovery",
      "Sleep and recovery",
    );
    expect(isDataStar(recovery)).toBe(true);
    expect(
      isSavedRecord(engineStar("rhythm", "documents", "household:a", "A")),
    ).toBe(true);
    expect(dataStar("garmin", "recovery")).toBe(
      "rhythm:data%3Agarmin:alex%3Arecovery",
    );
    expect(dashboardStarId(summary.id)).toBe(`rhythm:dashboard:${summary.id}`);
    // Another id is not the engine's.
    expect(
      isDataStar(engineStar("legacy", "data:garmin", "alex:recovery", "x")),
    ).toBe(false);
    expect(
      isSavedRecord(engineStar("legacy", "documents", "household:a", "A")),
    ).toBe(false);
    expect(isDashboardId(`legacy:dashboard:${summary.id}`)).toBe(false);
    expect(sourceName("legacy")).toBe("legacy");
  });
});
