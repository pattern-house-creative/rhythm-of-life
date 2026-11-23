import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import Explorer from "../src/Explorer";
import { emptyGraph } from "../src/liveGraph";
import type { GraphSession } from "../src/liveGraph";
import { record, relation } from "./synthetic";
import type { KnowledgeController } from "../src/useKnowledge";
import type { GraphNode, GraphPage } from "../src/knowledge";

const controller = (
  page: GraphPage,
  extra: Partial<GraphSession> = {},
): KnowledgeController => ({
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
    ...extra,
  },
});
// A fictional note from the notes folder, as the engine sends it.
const note = (path: string, title: string) => {
  const n = record(path, "notes", "notes");
  n.type = "note";
  n.title = title;
  n.provenance.demo = false;
  n.resource.content = null;
  return n;
};
const show = (page: GraphPage, selected: string | null, extra = {}) => {
  const onSelect = vi.fn();
  render(
    <Explorer
      knowledge={controller(page, extra)}
      selected={selected}
      onSelect={onSelect}
      close={vi.fn()}
      paused
      state="ready"
    />,
  );
  return onSelect;
};

it("filters by every provider on the map, by its display name", async () => {
  // Stars of their own providers; readings the engine keeps as data stars of the provider that
  // measured them; documents it keeps for the documents adapter; and its own dashboard.
  const nodes = [
    "notes",
    "demo",
    "drive",
    "second-brain",
    "carelink-csv",
    "fictional-sync",
  ].map((p) => record(`star-${p}`, p));
  for (const p of ["csv", "nightscout", "garmin", "carelink-live", "demo"])
    nodes.push(record(`alex:${p}`, "rhythm", `data:${p}`));
  const saved = record("household:upload:rota", "rhythm", "documents");
  saved.type = "document";
  const board = record("fictional-board", "rhythm", "dashboard");
  // A second star from one provider still lists it once.
  nodes.push(saved, board, record("star-notes-2", "notes"));
  show({ ...emptyGraph(), nodes }, null);
  const filter = screen.getByLabelText<HTMLSelectElement>("Filter by source");
  expect([...filter.options].map((o) => [o.value, o.text])).toEqual([
    ["all", "All sources"],
    ["carelink-csv", "CareLink CSV"],
    ["carelink-live", "CareLink live"],
    ["csv", "CSV import"],
    ["documents", "Documents"],
    ["fictional-sync", "fictional-sync"],
    ["garmin", "Garmin"],
    ["drive", "Google Drive"],
    ["nightscout", "Nightscout"],
    ["notes", "Notes"],
    ["rhythm", "Rhythm engine"],
    ["demo", "Sample data"],
    ["second-brain", "Second Brain"],
  ]);
  const user = userEvent.setup();
  for (const [source, matches] of [
    ["demo", "2 matches"],
    ["documents", "1 match"],
  ]) {
    await user.selectOptions(filter, source);
    expect(screen.getAllByText(matches).length).toBeGreaterThan(0);
  }
});

it("lists a neighbour once when their links run both ways", () => {
  const a = note("Bike commute.md", "Bike commute"),
    b = note("Weekly review.md", "Weekly review"),
    c = note("Water intake.md", "Water intake");
  const link = (from: GraphNode, to: GraphNode, type = "links_to") => ({
    ...relation(from, to),
    type,
  });
  show(
    {
      ...emptyGraph(),
      nodes: [a, b, c],
      edges: [link(a, b), link(b, a), link(a, c), link(c, a, "mentions")],
    },
    a.id,
  );
  const details = screen.getByRole("complementary", {
    name: "Selected knowledge details",
  });
  expect(
    within(details).getByRole("heading", { name: /Connected/ }),
  ).toHaveTextContent("Connected 2");
  const rows = within(
    within(details).getByRole("list", { name: "Connected items" }),
  ).getAllByRole("listitem");
  expect(rows.map((r) => r.textContent)).toEqual([
    "Weekly reviewlinks to",
    "Water intakelinks to · mentions",
  ]);
});

it("reads a note as Markdown and follows its wikilinks to stars on the map", async () => {
  const weekly = note("Weekly review.md", "DEMO · Weekly review"),
    bike = note("Bike commute.md", "DEMO · Bike commute");
  const page = { ...emptyGraph(), nodes: [weekly, bike] };
  const notes: Record<string, string> = {
    [weekly.id]: [
      "# Weekly review",
      "",
      "How many days did the [[Bike commute|commute]] happen?",
      "Ask at the [[Book club]].",
      "",
      "<script>alert(1)</script>",
    ].join("\n"),
    [bike.id]: "## Route\n\n- river path\n- park loop",
  };
  const text = vi.fn(async (id: string) => ({
    text: notes[id],
    modifiedAt: null,
  }));
  const onSelect = show(page, weekly.id, { text });
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: /Read note/ }));
  const reader = screen.getByRole("region", { name: "Source reader" });
  expect(
    await within(reader).findByRole("heading", { name: "Weekly review" }),
  ).toBeInTheDocument();
  expect(reader.querySelector("script")).toBeNull();
  expect(reader).toHaveTextContent("<script>alert(1)</script>");
  // The book club is not on the map: its name shows, as text.
  expect(within(reader).getByTitle("Not on the map")).toHaveTextContent(
    "Book club",
  );
  await user.click(within(reader).getByRole("button", { name: "commute" }));
  expect(onSelect).toHaveBeenCalledWith(bike.id);
  expect(
    await within(reader).findByRole("heading", { name: "Route" }),
  ).toBeInTheDocument();
  expect(within(reader).getAllByRole("listitem")).toHaveLength(2);
  expect(text).toHaveBeenLastCalledWith(bike.id, expect.anything());
});
