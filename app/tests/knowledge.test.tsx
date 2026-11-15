import { describe, expect, it } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createDemoGraph, demoData } from "../src/demo";
import { sampleHealth } from "./sampleHealth";
import {
  createMockAdapters,
  identityKey,
  mergePage,
  neighborhood,
  drivePreviewUrl,
  retrieveNeighbors,
  safeSourceUrl,
  tracePath,
} from "../src/knowledge";
import type { GraphNode } from "../src/knowledge";
import { chartModel } from "../src/chartModel";
import App from "../src/App";

describe("source-aware exploration", () => {
  const fixture = createDemoGraph(demoData);
  it("namespaces provider/account identities without delimiter collisions", () => {
    const base = {
      provider: "drive" as const,
      account: "a:b",
      externalId: "c",
    };
    expect(identityKey(base)).not.toBe(
      identityKey({ ...base, account: "a", externalId: "b:c" }),
    );
    expect(identityKey(base)).not.toBe(
      identityKey({ ...base, provider: "rhythm" }),
    );
  });
  it("keeps suggestions out of verified neighborhoods and paths", () => {
    const edges = fixture.allEdges.filter((e) => e.source === "book-notes");
    expect(tracePath("book-notes", "base", edges, fixture.allNodes)).toBeNull();
    expect(neighborhood("book-notes", edges, 2).has("base")).toBe(false);
    expect(tracePath("book-notes", "reading", edges, fixture.allNodes)).toEqual(
      ["book-notes", "reading"],
    );
  });
  it("finds a bounded multi-step path and preserves directional evidence", () => {
    expect(
      tracePath(
        "reading-decision",
        "design",
        fixture.allEdges,
        fixture.allNodes,
      ),
    ).toEqual(["reading-decision", "decisions", "design"]);
    const edge = fixture.allEdges.find((e) => e.source === "archive-design")!;
    expect(edge.type).toBe("drawn_from");
    expect(edge.directed).toBe(true);
    expect(edge.evidence[0].resourceId).toBe("archive-design");
    expect(
      neighborhood("reading-decision", fixture.allEdges, 1).has("design"),
    ).toBe(false);
    expect(
      neighborhood("reading-decision", fixture.allEdges, 2).get("design"),
    ).toBe(2);
  });
  it("paginates neighbors without leaking unrelated records and merges idempotently", async () => {
    const adapters = createMockAdapters(fixture.allNodes, fixture.allEdges, {
      pageSize: 1,
    });
    const first = await retrieveNeighbors(adapters, "projects");
    expect(first.completeness).toBe("partial");
    expect(first.nodes.length).toBeLessThan(fixture.allNodes.length);
    expect(first.nodes.some((n) => n.id === "missing-note")).toBe(false);
    const second = await retrieveNeighbors(adapters, "projects", first.cursors);
    const merged = mergePage(first, second);
    expect(new Set(merged.nodes.map((n) => n.id)).size).toBe(
      merged.nodes.length,
    );
    expect(mergePage(merged, second).nodes).toHaveLength(merged.nodes.length);
    expect(
      first.edges.every(
        (e) => e.source === "projects" || e.target === "projects",
      ),
    ).toBe(true);
  });
  it("retains working sources when one fails and retries only unfinished providers", async () => {
    const failed = await retrieveNeighbors(
      createMockAdapters(fixture.allNodes, fixture.allEdges, {
        unavailable: "drive",
      }),
      "design",
    );
    expect(failed.issues.map((i) => i.provider)).toEqual(["drive"]);
    expect(failed.nodes.some((n) => n.id === "archive-design")).toBe(true);
    expect(failed.nodes.some((n) => n.id === "prototype-notes")).toBe(false);
    expect(failed.completeness).toBe("partial");
    const recovered = await retrieveNeighbors(
      createMockAdapters(fixture.allNodes, fixture.allEdges),
      "design",
      failed.cursors,
    );
    expect(recovered.nodes.some((n) => n.id === "prototype-notes")).toBe(true);
    expect(recovered.issues).toEqual([]);
  });
  it("fails closed for missing/restricted items, revocation, unsafe URLs and aborted reads", async () => {
    const base = fixture.allNodes[0];
    const denied: GraphNode = {
      ...base,
      resource: {
        ...base.resource,
        availability: "restricted",
        access: { ...base.resource.access, canRead: false },
      },
    };
    expect(
      mergePage(fixture.initial, {
        ...fixture.initial,
        nodes: [denied],
        edges: [],
      }).nodes.some((n) => n.id === base.id),
    ).toBe(false);
    expect(
      tracePath("missing-note", "design", fixture.allEdges, fixture.allNodes),
    ).toBeNull();
    expect(
      safeSourceUrl({
        ...base,
        resource: {
          ...base.resource,
          canonicalUrl: "javascript:alert(1)",
          access: { ...base.resource.access, canOpen: true },
        },
      }),
    ).toBeNull();
    expect(
      safeSourceUrl({
        ...base,
        resource: {
          ...base.resource,
          canonicalUrl: "https://docs.google.com/document/d/synthetic",
          access: { ...base.resource.access, canOpen: true },
        },
      }),
    ).toMatch(/^https:/);
    const c = new AbortController();
    c.abort();
    await expect(
      retrieveNeighbors(
        createMockAdapters(fixture.allNodes, fixture.allEdges),
        "design",
        {},
        c.signal,
      ),
    ).rejects.toMatchObject({ name: "AbortError" });
  });
  it("previews only openable Drive originals on Google's own hosts", () => {
    const base = fixture.allNodes[0];
    const drive = (
      canonicalUrl: string,
      provider: GraphNode["resource"]["identity"]["provider"] = "drive",
    ) =>
      drivePreviewUrl({
        ...base,
        resource: {
          ...base.resource,
          identity: { ...base.resource.identity, provider },
          canonicalUrl,
          access: { ...base.resource.access, canOpen: true },
        },
      });
    expect(
      drive(
        "https://docs.google.com/document/d/fictional-Doc_1/edit?usp=drivesdk",
      ),
    ).toBe("https://docs.google.com/document/d/fictional-Doc_1/preview");
    expect(
      drive(
        "https://docs.google.com/spreadsheets/d/fictional-sheet/edit#gid=0",
      ),
    ).toBe("https://docs.google.com/spreadsheets/d/fictional-sheet/preview");
    expect(
      drive("https://docs.google.com/presentation/d/fictional-deck/edit"),
    ).toBe("https://docs.google.com/presentation/d/fictional-deck/preview");
    expect(
      drive("https://drive.google.com/file/d/fictional-file/view?usp=drivesdk"),
    ).toBe("https://drive.google.com/file/d/fictional-file/preview");
    // Folders, other hosts, other providers and unreadable or closed records keep the plain link.
    expect(
      drive("https://drive.google.com/drive/folders/fictional-folder"),
    ).toBeNull();
    expect(
      drive("https://docs.google.com.example.test/document/d/fictional/edit"),
    ).toBeNull();
    expect(
      drive("http://docs.google.com/document/d/fictional/edit"),
    ).toBeNull();
    expect(
      drive(
        "https://docs.google.com/document/d/fictional/edit",
        "second-brain",
      ),
    ).toBeNull();
    expect(
      drivePreviewUrl({
        ...base,
        resource: {
          ...base.resource,
          identity: { ...base.resource.identity, provider: "drive" },
          canonicalUrl: "https://docs.google.com/document/d/fictional/edit",
          access: { ...base.resource.access, canOpen: false },
        },
      }),
    ).toBeNull();
  });
});

describe("glucose chart interpretation", () => {
  it("includes extreme values and target bounds without clipping", () => {
    const series = {
      ...sampleHealth.glucose,
      samples: [
        { hour: 1, value: 38, timestamp: "2026-10-18T01:00:00Z" },
        { hour: 2, value: 402, timestamp: "2026-10-18T02:00:00Z" },
      ],
    };
    const result = chartModel(series);
    expect(result.domain[0]).toBeLessThan(38);
    expect(result.domain[1]).toBeGreaterThan(402);
    expect(result.label).toContain("Latest 402");
    expect(chartModel({ ...series, samples: [] }).label).toBe(
      "No glucose readings available for this interval.",
    );
    expect(
      chartModel({ ...series, samples: [{ ...series.samples[0], value: NaN }] })
        .min,
    ).toBeNull();
  });
});

describe("exploration journeys", () => {
  it("captures ambient scroll before the shorter explorer layout renders and restores it", async () => {
    const user = userEvent.setup();
    render(<App />);
    const original = Object.getOwnPropertyDescriptor(window, "scrollY");
    Object.defineProperty(window, "scrollY", {
      value: 1096,
      configurable: true,
    });
    await user.click(
      await screen.findByRole("button", { name: "Enter constellation" }),
    );
    await screen.findByRole("region", { name: "Knowledge explorer" });
    expect(document.body.style.top).toBe("-1096px");
    await user.click(screen.getByRole("button", { name: "Return to ambient" }));
    expect(document.body.style.position).toBe("");
    expect(window.scrollTo).toHaveBeenLastCalledWith({
      top: 1096,
      behavior: "instant",
    });
    if (original) Object.defineProperty(window, "scrollY", original);
  });
  it("focuses, loads a decision, traces its path and reads the evidence", async () => {
    const user = userEvent.setup();
    render(<App />);
    await user.click(
      await screen.findByRole("button", { name: "Enter constellation" }),
    );
    const explorer = await screen.findByRole("region", {
      name: "Knowledge explorer",
    });
    await user.click(
      within(explorer).getByRole("button", {
        name: "Rhythm of Life",
        exact: true,
      }),
    );
    await user.click(
      within(explorer).getByRole("button", {
        name: "Explore more connections",
      }),
    );
    const panel = within(explorer).getByRole("complementary", {
      name: "Selected knowledge details",
    });
    await user.click(
      await within(panel).findByRole("button", {
        name: "Keep originals at the source",
        exact: true,
      }),
    );
    await user.click(
      within(explorer).getByRole("button", { name: "Trace", exact: true }),
    );
    await user.selectOptions(
      within(explorer).getByLabelText("Trace to"),
      within(within(explorer).getByLabelText("Trace to")).getByRole("option", {
        name: "Design principles",
        exact: true,
      }),
    );
    expect(
      within(explorer).getByLabelText("Connection path"),
    ).toHaveTextContent("Keep originals at the source");
    await user.click(
      within(explorer).getAllByRole("button", { name: "Explain this link" })[0],
    );
    expect(panel).toHaveTextContent("Explicit sample relationship");
    await user.click(
      within(panel).getByRole("button", { name: "Read sample source" }),
    );
    // Sources open in the reader beside the panel, which keeps the evidence.
    expect(
      within(explorer).getByRole("region", { name: "Source reader" }),
    ).toHaveTextContent("No real document or memory was fetched.");
    expect(panel).toHaveTextContent("Explicit sample relationship");
  });
  it("shows suggestions as unverified and dismisses them without mutating evidence", async () => {
    const user = userEvent.setup();
    render(<App />);
    await user.click(
      await screen.findByRole("button", { name: "Enter constellation" }),
    );
    const explorer = await screen.findByRole("region", {
      name: "Knowledge explorer",
    });
    await user.click(
      within(explorer).getByRole("button", { name: "Discover", exact: true }),
    );
    expect(
      within(explorer).getAllByText("Unverified suggestion")[0],
    ).toBeInTheDocument();
    await user.click(
      within(explorer).getAllByRole("button", { name: "Inspect evidence" })[0],
    );
    expect(
      within(explorer).getByText(/not proof of a relationship/),
    ).toBeInTheDocument();
    await user.click(
      within(explorer).getByRole("button", { name: "Back to connections" }),
    );
    for (const button of within(explorer).getAllByRole("button", {
      name: "Dismiss",
      exact: true,
    }))
      await user.click(button);
    expect(
      within(explorer).getByText("No suggestions left in the loaded graph."),
    ).toBeInTheDocument();
  });
});
