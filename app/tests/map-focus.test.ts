import { expect, it } from "vitest";
import {
  focusSummary,
  focusedPage,
  nearStars,
  starMatches,
  starSource,
} from "../src/mapFocus";
import type { MapFocus } from "../src/parts";
import type { GraphEdge, GraphNode, Verification } from "../src/knowledge";
import type { NodeType } from "../src/model";
import { record, relation } from "./synthetic";

// A small fictional map: a training plan in a notes folder, two Garmin data stars, a race
// entry saved in Rhythm from Drive, a memory, a Drive folder, and a few stars around them.
function star(
  externalId: string,
  provider: string,
  account: string,
  fields: {
    type: NodeType;
    title: string;
    owner: string;
    overview?: string;
    groups?: { id: string; label: string }[];
    modified?: string;
  },
): GraphNode {
  const n = record(externalId, provider, account);
  return {
    ...n,
    type: fields.type,
    title: fields.title,
    overview: fields.overview ?? "",
    groups: fields.groups ?? [],
    resource: {
      ...n.resource,
      owner: fields.owner,
      sourceModifiedAt: fields.modified ?? "2026-10-18T12:00:00Z",
    },
  };
}
const plan = star("plans/training.md", "notes", "vault", {
  type: "note",
  title: "Training plan",
  overview: "Twelve weeks to a 10k",
  owner: "alex",
  groups: [{ id: "notes:running", label: "Running" }],
});
const run = star("alex:training", "rhythm", "data:garmin", {
  type: "record",
  title: "Garmin training",
  overview: "Runs and rides",
  owner: "alex",
});
const sleep = star("alex:recovery", "rhythm", "data:garmin", {
  type: "record",
  title: "Garmin recovery",
  owner: "alex",
});
const entry = star("race-entry", "rhythm", "drive", {
  type: "document",
  title: "Race entry",
  owner: "sam",
  modified: "2026-10-20T12:00:00Z",
});
const memo = star("memory-1", "second-brain", "household", {
  type: "memory",
  title: "Felt strong on the long run",
  owner: "sam",
  groups: [{ id: "second-brain:running", label: "Running" }],
  modified: "2026-10-21T12:00:00Z",
});
const folder = star("folder-plans", "drive", "alex@example.invalid", {
  type: "theme",
  title: "Plans",
  owner: "alex",
});
const stretch = star("stretching.md", "notes", "vault", {
  type: "note",
  title: "Stretching",
  owner: "alex",
});
const odd = star("odd.md", "notes", "vault", {
  type: "note",
  title: "Odd one out",
  owner: "sam",
});
const imported = star("import-1", "csv", "upload", {
  type: "record",
  title: "Imported runs",
  owner: "alex",
});
const link = (
  a: GraphNode,
  b: GraphNode,
  verification: Verification,
): GraphEdge => ({
  ...relation(a, b),
  verification,
});
const map = {
  nodes: [plan, run, sleep, entry, memo, folder, stretch, odd, imported],
  edges: [
    link(plan, run, "verified"),
    link(run, stretch, "suggested"),
    link(memo, plan, "inferred"),
    link(memo, entry, "verified"),
    link(folder, entry, "unverified"),
    // The odd note is linked only by a link someone disputed.
    link(plan, odd, "disputed"),
    link(imported, plan, "verified"),
  ],
};
const ids = (nodes: GraphNode[]) => nodes.map((n) => n.title);
const shown = (focus: MapFocus, options = {}) =>
  ids(focusedPage(map, focus, options).nodes).sort();

it("works out each star's source as the table says", () => {
  // A data star: the provider after data: in its account.
  expect(starSource(run)).toBe("garmin");
  // A record saved in Rhythm: its account.
  expect(starSource(entry)).toBe("drive");
  // Any other star: its own provider.
  expect(starSource(plan)).toBe("notes");
  expect(starSource(memo)).toBe("second-brain");
  expect(starSource(folder)).toBe("drive");
  // A star the engine keeps that is neither (a dashboard's): its own provider too.
  const board = star(
    "00000000-0000-4000-8000-00000000d0a5",
    "rhythm",
    "dashboard",
    {
      type: "record",
      title: "Training",
      owner: "household",
    },
  );
  expect(starSource(board)).toBe("rhythm");
  expect(shown({ providers: ["garmin"] })).toEqual([
    "Garmin recovery",
    "Garmin training",
  ]);
  expect(shown({ providers: ["drive"] })).toEqual(["Plans", "Race entry"]);
});

it("walks near a star over the links the sky draws, never a disputed one", () => {
  expect([...nearStars(plan.id, map.edges, 1)].sort()).toEqual(
    [plan.id, run.id, memo.id, imported.id].sort(),
  );
  expect(shown({ near: { star: plan.id, depth: 1 } })).toEqual([
    "Felt strong on the long run",
    "Garmin training",
    "Imported runs",
    "Training plan",
  ]);
  expect(shown({ near: { star: plan.id, depth: 2 } })).toEqual([
    "Felt strong on the long run",
    "Garmin training",
    "Imported runs",
    "Race entry",
    "Stretching",
    "Training plan",
  ]);
  // The odd note is linked only by a disputed link: never near, and its link never drawn,
  // even when every kind of link is asked for.
  const page = focusedPage(map, {
    query: "o",
    links: ["verified", "suggested", "inferred", "unverified"],
  });
  expect(ids(page.nodes)).toContain("Odd one out");
  expect(page.edges.some((e) => e.target === odd.id)).toBe(false);
  // A star off the map shows nothing near it.
  expect(shown({ near: { star: "notes:vault:gone.md", depth: 2 } })).toEqual(
    [],
  );
});

it("keeps only what the map shows before it matches, walks or counts", () => {
  const notCsv = { shows: (provider: string) => provider !== "csv" };
  expect(shown({ near: { star: plan.id, depth: 1 } }, notCsv)).toEqual([
    "Felt strong on the long run",
    "Garmin training",
    "Training plan",
  ]);
  expect(focusedPage(map, { providers: ["csv"] }, notCsv).matched).toBe(0);
  // An unreadable star is never shown.
  const gone = {
    ...run,
    resource: { ...run.resource, availability: "restricted" as const },
  };
  expect(
    ids(
      focusedPage(
        { ...map, nodes: [...map.nodes, gone].filter((n) => n !== run) },
        { providers: ["garmin"] },
      ).nodes,
    ),
  ).toEqual(["Garmin recovery"]);
});

it("matches each member, every member given, any value of a list", () => {
  // A query reads the title, the overview and the groups' labels, in any case.
  expect(shown({ query: "TRAINING" })).toEqual([
    "Garmin training",
    "Training plan",
  ]);
  expect(shown({ query: "running" })).toEqual([
    "Felt strong on the long run",
    "Training plan",
  ]);
  expect(shown({ query: "10k" })).toEqual(["Training plan"]);
  expect(shown({ types: ["document"] })).toEqual(["Race entry"]);
  expect(shown({ types: ["memory", "theme"] })).toEqual([
    "Felt strong on the long run",
    "Plans",
  ]);
  // A group is matched by its id exactly.
  expect(shown({ groups: ["notes:running"] })).toEqual(["Training plan"]);
  expect(shown({ groups: ["running"] })).toEqual([]);
  expect(shown({ owners: ["sam"] })).toEqual([
    "Felt strong on the long run",
    "Odd one out",
    "Race entry",
  ]);
  expect(shown({ query: "training", providers: ["garmin"] })).toEqual([
    "Garmin training",
  ]);
  expect(
    shown({
      types: ["note"],
      owners: ["alex"],
      near: { star: run.id, depth: 1 },
    }),
  ).toEqual(["Stretching", "Training plan"]);
  expect(starMatches(plan, { types: ["note"], owners: ["sam"] }, null)).toBe(
    false,
  );
  // `links` picks the links between shown stars, not the stars.
  const verified = focusedPage(map, {
    near: { star: plan.id, depth: 2 },
    links: ["verified"],
  });
  expect(verified.nodes).toHaveLength(6);
  expect(new Set(verified.edges.map((e) => e.verification))).toEqual(
    new Set(["verified"]),
  );
  expect(
    focusedPage(map, { near: { star: plan.id, depth: 2 } }).edges,
  ).toHaveLength(5);
});

it("ranks and pages as the overview does", () => {
  const everyone: MapFocus = { owners: ["alex", "sam"] };
  // Memories and notes, and what one of them cites by a verified link, first (the most
  // linked, then the newest), then documents, then themes, then the rest.
  expect(ids(focusedPage(map, everyone).nodes)).toEqual([
    "Training plan",
    "Felt strong on the long run",
    "Race entry",
    "Garmin training",
    "Stretching",
    "Odd one out",
    "Plans",
    "Imported runs",
    "Garmin recovery",
  ]);
  const first = focusedPage(map, everyone, { limit: 4 });
  expect(first).toMatchObject({
    matched: 9,
    nextCursor: "o:4",
    completeness: "partial",
    issues: [],
  });
  expect(ids(first.nodes)).toEqual([
    "Training plan",
    "Felt strong on the long run",
    "Race entry",
    "Garmin training",
  ]);
  // Only links between two stars of the page.
  const page = new Set(first.nodes.map((n) => n.id));
  expect(
    first.edges.every((e) => page.has(e.source) && page.has(e.target)),
  ).toBe(true);
  expect(first.edges.map((e) => e.verification)).toEqual([
    "verified",
    "verified",
    "inferred",
  ]);
  const last = focusedPage(map, everyone, { limit: 4, cursor: "o:8" });
  expect(last).toMatchObject({
    matched: 9,
    nextCursor: null,
    completeness: "complete",
  });
  expect(ids(last.nodes)).toEqual(["Garmin recovery"]);
});

it("says a focus in plain words", () => {
  const title = (id: string) => (id === plan.id ? "Training plan" : undefined);
  expect(
    focusSummary(
      { near: { star: plan.id, depth: 1 }, providers: ["garmin"] },
      title,
    ),
  ).toBe("near Training plan; providers garmin");
  expect(focusSummary({ near: { star: plan.id, depth: 2 } }, title)).toBe(
    "within 2 links of Training plan",
  );
  expect(focusSummary({ near: { star: run.id, depth: 1 } }, title)).toBe(
    `near ${run.id}`,
  );
  expect(
    focusSummary({
      query: "run",
      types: ["note", "memory"],
      groups: ["notes:running"],
      owners: ["alex", "sam"],
      links: ["verified"],
    }),
  ).toBe(
    "matching “run”; types note, memory; groups notes:running; owners alex, sam; links verified",
  );
  const long = focusSummary({
    groups: Array.from({ length: 20 }, (_, i) => `group-${i}`),
  });
  expect(Array.from(long)).toHaveLength(100);
  expect(focusSummary({ query: "a\u0007b" })).toBe("matching “a b”");
  expect(focusSummary(null)).toBe("everything");
  expect(focusSummary({})).toBe("a focus this screen can't read");
});
