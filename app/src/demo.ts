import type {
  KnowledgeNode,
  KnowledgeEdge,
  ObservatoryAdapter,
  ObservatoryData,
  Provenance,
  SourceId,
} from "./model.js";
import type {
  GraphNode,
  GraphEdge,
  GraphPage,
  GraphAdapter,
} from "./knowledge.js";
import { identityKey, retrieveGraph } from "./knowledge.js";
import { normalizeRecords, refreshGraph } from "./liveGraph.js";
import type { GraphSession } from "./liveGraph.js";
import { createMockSources } from "./mockSources.js";
import { sourceName } from "./graphView.js";
import { glucoseSlot } from "../server/healthState.js";
const date = "2026-10-18";
const provenance = (
  sourceId: SourceId,
  recordId: string,
  hour = "20:00",
  day = date,
): Provenance => ({
  sourceId,
  recordId: `demo:${recordId}`,
  observedAt: `${day}T${hour}:00Z`,
  demo: true,
});
// The demo is everyday wellbeing: a fictional tracker's sleep, steps and resting
// heart rate, and an evening mood check-in, beside notes, documents and memories.
const groups = [
  {
    id: "health",
    title: "Health",
    x: 230,
    y: 205,
    source: "demo",
    children: [
      [
        "mood",
        "Mood log",
        "record",
        "Fictional evening check-ins from one to five. Linked to an illustrative walk; no causal claim is made.",
      ],
      [
        "sleep",
        "Sleep journal",
        "note",
        "A fictional note about an evening wind-down routine.",
      ],
      [
        "rhythm",
        "Daily rhythms",
        "memory",
        "A sample reflection on making space for consistent routines.",
      ],
      [
        "recovery",
        "Sleep",
        "record",
        "Fictional nights of sleep from a sample tracker, about seven hours and a quarter.",
      ],
    ],
  },
  {
    id: "training",
    title: "Training",
    x: 180,
    y: 430,
    source: "demo",
    children: [
      [
        "run",
        "Sunday long run",
        "record",
        "An easy 8.4 km sample run on the river trail, lasting 52 minutes.",
      ],
      [
        "base",
        "Building a base",
        "project",
        "A fictional six-week consistency project with three weekly sessions.",
      ],
      [
        "route",
        "River trail",
        "note",
        "A sample route note linked to the Sunday run.",
      ],
      [
        "mobility",
        "Mobility practice",
        "note",
        "A fictional ten-minute mobility routine.",
      ],
    ],
  },
  {
    id: "projects",
    title: "Projects",
    x: 440,
    y: 330,
    source: "drive",
    children: [
      [
        "observatory",
        "Rhythm of Life",
        "project",
        "A sample project exploring how health and knowledge can share a calm, useful canvas.",
      ],
      [
        "design",
        "Design principles",
        "document",
        "A fictional design note: generous space, honest provenance, and gentle motion.",
      ],
      [
        "reading",
        "Reading garden",
        "project",
        "An example collection of ideas from books and articles.",
      ],
      [
        "systems",
        "Connected systems",
        "note",
        "A sample architecture note about bringing separately authorized sources together.",
      ],
    ],
  },
  {
    id: "work",
    title: "Work",
    x: 590,
    y: 175,
    source: "rhythm",
    children: [
      [
        "focus",
        "Deep work",
        "note",
        "A fictional note on protecting a quiet morning for focused work.",
      ],
      [
        "decisions",
        "Decision journal",
        "document",
        "A sample journal describing why small, reversible experiments are useful.",
      ],
      [
        "craft",
        "Learning in public",
        "project",
        "A fictional project about explaining technical ideas clearly.",
      ],
      [
        "review",
        "Weekly review",
        "memory",
        "An illustrative memory of a weekly reflection practice.",
      ],
    ],
  },
  {
    id: "home",
    title: "Home",
    x: 625,
    y: 465,
    source: "second-brain",
    children: [
      [
        "garden",
        "Weekend garden",
        "project",
        "A fictional plan to grow a small herb garden.",
      ],
      [
        "ritual",
        "Evening ritual",
        "memory",
        "A sample reflection about a quiet walk and reading before bed.",
      ],
      [
        "library",
        "Home library",
        "document",
        "A fictional collection of books, connected to the reading garden.",
      ],
      [
        "walk",
        "Neighborhood walk",
        "record",
        "An example 20-minute afternoon walk.",
      ],
    ],
  },
] as const;
const offsets = [
  [-72, -67],
  [72, -63],
  [-63, 67],
  [72, 64],
];
const nodes: KnowledgeNode[] = groups
  .flatMap((g) => [
    {
      id: g.id,
      title: g.title,
      type: "theme",
      cluster: g.id,
      x: g.x,
      y: g.y,
      overview: `The ${g.title.toLowerCase()} cluster groups fictional records, ideas, and projects by a shared topic.`,
      provenance: provenance(g.source, g.id),
    } as KnowledgeNode,
    ...g.children.map(
      (n, i) =>
        ({
          id: n[0],
          title: n[1],
          type: n[2],
          cluster: g.id,
          x: g.x + offsets[i][0],
          y: g.y + offsets[i][1],
          overview: n[3],
          provenance: provenance(
            [
              "sleep",
              "base",
              "route",
              "mobility",
              "garden",
              "library",
              "focus",
              "decisions",
              "craft",
            ].includes(n[0])
              ? "drive"
              : ["rhythm", "review"].includes(n[0])
                ? "second-brain"
                : n[0] === "walk"
                  ? "demo"
                  : g.source,
            n[0],
          ),
        }) as KnowledgeNode,
    ),
  ])
  .map((n) =>
    n.id === "garden"
      ? { ...n, x: 610, y: 393 }
      : n.id === "systems"
        ? { ...n, x: 475, y: 420 }
        : n.id === "design"
          ? { ...n, x: 450, y: 220 }
          : n,
  );
// The demo connects no glucose source and no Garmin: their tiles stay off the
// home screen, and Sources & settings says how to connect them. Its wellbeing
// numbers are data stars on the map and its dashboards' feeds.
const none = (id: string) => provenance("demo", id);
export const demoData: ObservatoryData = {
  glucoseHistory: null,
  glucose: {
    id: "glucose",
    unit: "mg/dL",
    samples: [],
    status: "missing",
    provenance: none("glucose"),
  },
  current: {
    id: "glucose",
    label: "Glucose",
    value: null,
    unit: "mg/dL",
    status: "missing",
    provenance: none("glucose"),
  },
  recovery: {
    sleepMinutes: null,
    restingHeartRate: null,
    overnightHrvMs: null,
    status: "missing",
    provenance: none("recovery"),
  },
  steps: {
    id: "steps",
    label: "Steps",
    value: null,
    unit: "steps",
    status: "missing",
    provenance: none("steps"),
  },
  workouts: [],
  workoutsStatus: "missing",
  nodes,
  edges: [
    {
      source: "ritual",
      target: "reading",
      relationship:
        "The fictional evening ritual includes time for the reading garden",
      strength: 0.6,
    },
    ...groups.flatMap((g) =>
      g.children.map((n) => ({
        source: g.id,
        target: n[0],
        relationship: `Categorized under ${g.title}`,
        strength: 0.65,
      })),
    ),
    {
      source: "health",
      target: "training",
      relationship: "Shared sample activity records",
      strength: 0.8,
    },
    {
      source: "health",
      target: "projects",
      relationship:
        "Health visualization is part of the sample observatory project",
      strength: 0.9,
    },
    {
      source: "projects",
      target: "work",
      relationship: "Design and learning themes overlap",
      strength: 0.7,
    },
    {
      source: "projects",
      target: "home",
      relationship: "Personal projects include the home library",
      strength: 0.7,
    },
    {
      source: "mood",
      target: "walk",
      relationship:
        "Explicit demo-day association; not evidence that one causes the other",
      strength: 0.5,
    },
    {
      source: "sleep",
      target: "ritual",
      relationship: "The fictional sleep note references the evening ritual",
      strength: 0.5,
    },
    {
      source: "reading",
      target: "library",
      relationship: "The reading project uses the sample home library",
      strength: 0.6,
    },
    {
      source: "design",
      target: "decisions",
      relationship: "The design note cites the sample decision journal",
      strength: 0.5,
    },
  ],
  sources: [
    ...(["demo", "rhythm", "second-brain", "drive"] as SourceId[]).map(
      (id) => ({
        id,
        name: sourceName(id),
        connected: false,
        sampleAt: provenance(id, id).observedAt,
        status: "demo" as const,
      }),
    ),
    // Listed with how to connect them, as an install without them lists them.
    ...[glucoseSlot, "garmin"].map((id) => ({
      id,
      name: id === glucoseSlot ? "Glucose" : sourceName(id),
      connected: false,
      sampleAt: null,
      status: "disconnected" as const,
    })),
  ],
  insight: {
    title: "Evening routine & reading",
    body: "The sample evening routine links the sleep journal and reading project.",
    nodeId: "ritual",
  },
};
export const demoAdapter: ObservatoryAdapter = {
  mode: "demo",
  async load(signal) {
    if (signal?.aborted) throw new DOMException("Aborted", "AbortError");
    return demoData;
  },
};

// The demo's wellbeing numbers are data stars, as the engine's catalog makes
// them: one per kind of measurement, opening their readings (demoReadings).
export const demoDataStars: Record<string, string> = {
  mood: "mood",
  recovery: "sleep",
  steps: "activity",
  heart: "heart",
};
export const demoDataStar = (group: string) =>
  identityKey({
    provider: "rhythm",
    account: "data:demo",
    externalId: `fixture:${group}`,
  });

export function createDemoGraph(data: ObservatoryData) {
  const modified = "2026-10-18T20:00:00Z",
    retrieved = "2026-10-18T20:05:00Z";
  const extras: KnowledgeNode[] = [
    {
      id: "steps",
      title: "Steps",
      type: "record",
      cluster: "training",
      x: 255,
      y: 375,
      overview:
        "Fictional daily steps from a sample tracker, busier on walking days.",
      provenance: provenance("demo", "steps"),
    },
    {
      id: "heart",
      title: "Resting heart rate",
      type: "record",
      cluster: "health",
      x: 300,
      y: 150,
      overview:
        "A fictional resting heart rate, taken by a sample tracker each morning.",
      provenance: provenance("demo", "heart"),
    },
    {
      id: "decision-local",
      title: "Keep originals at the source",
      type: "decision",
      cluster: "projects",
      x: 340,
      y: 270,
      overview:
        "The sample team decided to retrieve documents where they live. A small catalog holds links and provenance; it does not replace the originals.",
      provenance: provenance("second-brain", "decision-local"),
    },
    {
      id: "prototype-notes",
      title: "First prototype review",
      type: "document",
      cluster: "projects",
      x: 490,
      y: 155,
      overview:
        "Fictional review notes: focus on the reading project, trace its decisions, and keep the graph navigable on a phone.",
      provenance: provenance("drive", "prototype-notes"),
    },
    {
      id: "archive-design",
      title: "Design notes · saved revision",
      type: "record",
      cluster: "work",
      x: 655,
      y: 300,
      overview:
        "A dated, fictional snapshot of the design notes. The original document may have changed since this revision.",
      provenance: provenance("rhythm", "archive-design"),
    },
    {
      id: "reading-decision",
      title: "Read fewer, connect more",
      type: "decision",
      cluster: "home",
      x: 540,
      y: 490,
      overview:
        "A fictional decision to connect each finished book to one current project, rather than maximize reading volume.",
      provenance: provenance("second-brain", "reading-decision"),
    },
    {
      id: "book-notes",
      title: "Notes on deliberate practice",
      type: "document",
      cluster: "training",
      x: 295,
      y: 470,
      overview:
        "Sample reading notes connecting small repeatable practice with a six-week training project. This is a learning theme, not a medical claim.",
      provenance: provenance("drive", "book-notes"),
    },
    {
      id: "missing-note",
      title: "Unavailable reference",
      type: "note",
      cluster: "work",
      x: 620,
      y: 210,
      overview: "",
      provenance: provenance("rhythm", "missing-note"),
    },
  ];
  const allNodes: GraphNode[] = [...data.nodes, ...extras].map((n) => ({
    ...n,
    resource: {
      identity: Object.hasOwn(demoDataStars, n.id)
        ? {
            provider: "rhythm",
            account: "data:demo",
            externalId: `fixture:${demoDataStars[n.id]}`,
          }
        : {
            provider: n.provenance.sourceId,
            account: "fictional-household",
            externalId: n.provenance.recordId,
          },
      canonicalUrl: null,
      sourceUrl: null,
      sourceModifiedAt: modified,
      retrievedAt: retrieved,
      revision: "sample-1",
      owner: "Fictional household",
      access: {
        scope: "demo",
        canRead: n.id !== "missing-note",
        canOpen: false,
      },
      availability:
        n.id === "archive-design"
          ? "stale"
          : n.id === "missing-note"
            ? "missing"
            : "available",
      content:
        n.id === "missing-note"
          ? null
          : `${n.title}\n\n${n.overview}\n\nThis synthetic source excerpt belongs to the demonstration. No real document or memory was fetched.`,
    },
  }));
  const newEdges: (KnowledgeEdge & {
    verification?: "suggested";
    type?: GraphEdge["type"];
  })[] = [
    {
      source: "decision-local",
      target: "observatory",
      relationship:
        "The source-first decision guides the Rhythm of Life project.",
      strength: 0.8,
      type: "supports",
    },
    {
      source: "decision-local",
      target: "systems",
      relationship:
        "The architecture note records this decision as its starting point.",
      strength: 0.8,
      type: "references",
    },
    {
      source: "prototype-notes",
      target: "design",
      relationship:
        "The review cites the design principles, section 2: calm interaction.",
      strength: 0.7,
      type: "references",
    },
    {
      source: "archive-design",
      target: "design",
      relationship:
        "This saved revision was derived from the fictional design document.",
      strength: 0.8,
      type: "drawn_from",
    },
    {
      source: "reading-decision",
      target: "reading",
      relationship:
        "The reading project implements the decision to connect books to active work.",
      strength: 0.8,
      type: "supports",
    },
    {
      source: "book-notes",
      target: "reading",
      relationship:
        "These reading notes belong to the Reading garden collection.",
      strength: 0.7,
      type: "part_of",
    },
    {
      source: "reading-decision",
      target: "decisions",
      relationship:
        "The decision journal explicitly records this reading decision.",
      strength: 0.7,
      type: "references",
    },
    {
      source: "systems",
      target: "design",
      relationship:
        "The architecture note references the design requirement for source provenance.",
      strength: 0.7,
      type: "references",
    },
    {
      source: "book-notes",
      target: "base",
      relationship:
        "Both mention small repeatable practice. No explicit source link has been found.",
      strength: 0.3,
      type: "related_to",
      verification: "suggested",
    },
    {
      source: "focus",
      target: "ritual",
      relationship:
        "Both describe protecting quiet time. This thematic association needs review.",
      strength: 0.3,
      type: "related_to",
      verification: "suggested",
    },
  ];
  const allEdges: GraphEdge[] = (
    [...data.edges, ...newEdges] as (KnowledgeEdge & {
      type?: GraphEdge["type"];
      verification?: GraphEdge["verification"];
    })[]
  ).map((e, i) => ({
    ...e,
    id: `demo:edge:${e.source}:${e.target}`,
    origin: {
      provider: allNodes.find((n) => n.id === e.source)!.provenance.sourceId,
      account: "fictional-household",
      externalId: `edge:${e.source}:${e.target}`,
    },
    type:
      e.type ??
      (e.relationship.startsWith("Categorized") ? "part_of" : "references"),
    directed: !e.relationship.startsWith("Categorized"),
    verification: e.verification ?? "verified",
    evidence: [
      {
        resourceId: e.source,
        excerpt: e.relationship,
        locator: `Sample relationship register · entry ${i + 1}`,
        retrievedAt: retrieved,
      },
    ],
    sourceModifiedAt: modified,
    retrievedAt: retrieved,
  }));
  const initialIds = new Set(data.nodes.map((n) => n.id));
  const initial: GraphPage = {
    nodes: allNodes.filter((n) => initialIds.has(n.id)),
    edges: allEdges.filter(
      (e) => initialIds.has(e.source) && initialIds.has(e.target),
    ),
    nextCursor: null,
    completeness: "partial",
    issues: [],
  };
  return { allNodes, allEdges, initial };
}

export function createDemoSession(
  data: ObservatoryData,
): GraphSession & { adapters: GraphAdapter[] } {
  const fixture = createDemoGraph(data);
  // These are fictional source facets, not application-owned cluster definitions.
  fixture.allNodes.forEach((n) => {
    n.groups = n.cluster
      ? [
          {
            id: n.cluster,
            label: n.cluster[0].toUpperCase() + n.cluster.slice(1),
          },
        ]
      : [];
  });
  const archive = fixture.allNodes.find((n) => n.id === "archive-design")!;
  archive.resource.representation = "archive";
  archive.resource.canonicalIdentity = fixture.allNodes.find(
    (n) => n.id === "design",
  )!.resource.identity;
  const normalized = normalizeRecords(fixture.allNodes, fixture.allEdges);
  const initialIds = new Set(
    fixture.initial.nodes.map((n) => normalized.mapping.get(n.id)),
  );
  const store = createMockSources(normalized.nodes, normalized.edges, 12);
  let generation = 0;
  const anchor = normalized.nodes.find((n) => n.type === "project")!;
  return {
    initial: {
      ...fixture.initial,
      nodes: normalized.nodes.filter((n) => initialIds.has(n.id)),
      edges: normalized.edges.filter(
        (e) => initialIds.has(e.source) && initialIds.has(e.target),
      ),
    },
    adapters: store.adapters,
    retrieve: (op, value, cursors, signal, states) =>
      retrieveGraph(store.adapters, op, value, cursors, signal, states),
    refresh: (current, signal) => refreshGraph(store.adapters, current, signal),
    demo: {
      setUnavailable(value) {
        store.setUnavailable("drive", value);
      },
      advance() {
        generation++;
        const modified = new Date(
          Date.parse("2026-10-19T18:00:00Z") + generation * 3600000,
        ).toISOString();
        const make = (
          provider: SourceId,
          title: string,
          type: GraphNode["type"],
        ): GraphNode => {
          const identity = {
            provider,
            account: "fictional-household",
            externalId: `growth-${generation}-${provider}`,
          };
          return {
            id: identityKey(identity),
            title,
            type,
            groups: anchor.groups,
            overview:
              "New fictional source material discovered during refresh. No component or layout was changed.",
            provenance: {
              sourceId: provider,
              recordId: identity.externalId,
              observedAt: modified,
              demo: true,
            },
            resource: {
              identity,
              canonicalUrl: null,
              sourceUrl: null,
              sourceModifiedAt: modified,
              retrievedAt: modified,
              revision: `sample-${generation}`,
              owner: "Fictional household",
              access: { scope: "demo", canRead: true, canOpen: false },
              availability: "available",
              representation: "original",
              content:
                "Fictional growth demonstration. This record was added to a mock source and retrieved through the normal adapter.",
            },
          };
        };
        const memory = make(
          "second-brain",
          `Field observation ${generation}`,
          "memory",
        );
        const doc = make("drive", `Research notes ${generation}`, "document");
        store.upsertNode(memory);
        store.upsertNode(doc);
        for (const [source, target, type] of [
          [memory.id, anchor.id, "part_of_project"],
          [doc.id, memory.id, "drawn_from"],
        ]) {
          store.upsertEdge({
            id: `growth:${source}:${target}`,
            origin: {
              provider: source === memory.id ? "second-brain" : "drive",
              account: "fictional-household",
              externalId: `growth:${source}:${target}`,
            },
            source,
            target,
            type,
            directed: true,
            verification: "verified",
            strength: 0.7,
            relationship:
              "An explicit citation in the fictional source links these records.",
            evidence: [
              {
                resourceId: source,
                excerpt: "The new source explicitly cites this item.",
                locator: "Sample citations",
                retrievedAt: modified,
              },
            ],
            sourceModifiedAt: modified,
            retrievedAt: modified,
          });
        }
        const old = normalized.edges.find(
          (e) => e.source === anchor.id || e.target === anchor.id,
        )!;
        store.upsertEdge({
          ...old,
          type: "references",
          sourceModifiedAt: modified,
          retrievedAt: modified,
          relationship:
            "Updated fictional relationship evidence from the latest source revision.",
        });
      },
    },
  };
}
