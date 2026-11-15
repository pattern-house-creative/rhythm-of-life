import {
  createEvent,
  fireEvent,
  render,
  screen,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import Explorer from "../src/Explorer";
import { Constellation } from "../src/Constellation";
import { emptyGraph } from "../src/liveGraph";
import { record, relation } from "./synthetic";
import type { KnowledgeController } from "../src/useKnowledge";
import type { GraphPage } from "../src/knowledge";

const controller = (page: GraphPage): KnowledgeController => ({
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
});

it("distinguishes a loading graph from an unavailable or empty one", () => {
  const common = {
    nodes: [],
    edges: [],
    selected: null,
    onSelect: vi.fn(),
    interactive: false,
    healthFocus: false,
    paused: true,
  };
  const unavailable = "No authorized graph records are currently available.";
  const { rerender } = render(
    <Constellation {...common} emptyMessage={unavailable} loading />,
  );
  expect(screen.getByText("Loading knowledge")).toBeInTheDocument();
  expect(screen.queryByText("Knowledge unavailable")).not.toBeInTheDocument();
  expect(screen.queryByText(unavailable)).not.toBeInTheDocument();
  rerender(<Constellation {...common} emptyMessage={unavailable} />);
  expect(screen.getByText("Knowledge unavailable")).toBeInTheDocument();
  expect(screen.getByText(unavailable)).toBeInTheDocument();
  rerender(<Constellation {...common} />);
  expect(screen.getByText("No matching constellations")).toBeInTheDocument();
});

it.each(["pointer", "keyboard"] as const)(
  "describes authorized graph evidence honestly via %s without demo controls or stored bodies",
  async (interaction) => {
    const a = record("fictional-authorized-memory"),
      b = record("fictional-authorized-document", "drive");
    for (const n of [a, b]) {
      n.provenance.demo = false;
      n.resource.content = null;
    }
    const edge = relation(a, b);
    const page = { ...emptyGraph(), nodes: [a, b], edges: [edge] };
    const knowledge = controller(page);
    render(
      <Explorer
        knowledge={knowledge}
        selected={a.id}
        onSelect={vi.fn()}
        close={vi.fn()}
        paused
        setPaused={vi.fn()}
        state="ready"
      />,
    );
    expect(screen.getByText("Authorized source record")).toBeInTheDocument();
    expect(
      screen.queryByText("Simulate Drive unavailable"),
    ).not.toBeInTheDocument();
    expect(screen.queryByText("Read sample source")).not.toBeInTheDocument();
    const user = userEvent.setup();
    const connection = screen.getByRole("button", {
      name: /Explain connection:/,
    });
    // The tap strip is sized in screen pixels, not graph units, so it stays
    // usable on narrow phones and when zoomed out.
    expect(connection).toHaveAttribute("vector-effect", "non-scaling-stroke");
    expect(connection).toHaveAttribute("stroke-width", "24");
    if (interaction === "pointer") {
      // D3 reads MouseEvent.view. user-event's synthetic SVG mouse events omit it;
      // provide the browser-equivalent window without mocking D3 or hiding errors.
      for (const event of [
        createEvent.mouseDown(connection, { button: 0, buttons: 1 }),
        createEvent.mouseUp(connection, { button: 0, buttons: 0 }),
        createEvent.click(connection, { button: 0 }),
      ]) {
        Object.defineProperty(event, "view", {
          value: connection.ownerDocument.defaultView,
        });
        fireEvent(connection, event);
      }
    } else {
      connection.focus();
      expect(connection).toHaveFocus();
      await user.keyboard("{Enter}");
    }
    expect(
      screen.getByText("Verified source relationship"),
    ).toBeInTheDocument();
    expect(screen.queryByText(/fictional evidence/)).not.toBeInTheDocument();
    await user.click(
      screen.getByRole("button", { name: "View source record" }),
    );
    expect(
      screen.getByText(
        "This graph retains citation metadata, not the document body.",
      ),
    ).toBeInTheDocument();
  },
);

it("previews a Drive original in Google's own viewer and keeps the link to open it", async () => {
  const a = record("fictional-authorized-memory"),
    b = record("fictional-drive-file", "drive");
  for (const n of [a, b]) {
    n.provenance.demo = false;
    n.resource.content = null;
  }
  b.resource.canonicalUrl =
    "https://drive.google.com/file/d/fictional-drive-file/view?usp=drivesdk";
  b.resource.access = { ...b.resource.access, canOpen: true };
  const page = { ...emptyGraph(), nodes: [a, b], edges: [relation(a, b)] };
  const { container } = render(
    <Explorer
      knowledge={controller(page)}
      selected={b.id}
      onSelect={vi.fn()}
      close={vi.fn()}
      paused
      setPaused={vi.fn()}
      state="ready"
    />,
  );
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: "Preview original" }));
  const frame = screen.getByTitle(`${b.title} in Google Drive`);
  expect(frame).toHaveAttribute(
    "src",
    "https://drive.google.com/file/d/fictional-drive-file/preview",
  );
  expect(frame).toHaveAttribute("referrerpolicy", "no-referrer");
  expect(frame.getAttribute("sandbox")).not.toMatch(/allow-top-navigation/);
  expect(container.querySelector(".explorer-reader.framed")).not.toBeNull();
  // The header is one slim row: back, what it is, and the original.
  const bar = container.querySelector<HTMLElement>(".reader-bar")!;
  expect(
    within(bar).getByRole("button", { name: "Close the reader" }),
  ).toBeInTheDocument();
  expect(
    within(bar).getByRole("heading", { name: b.title }),
  ).toBeInTheDocument();
  expect(
    within(bar).getByRole("link", { name: /Open original/ }),
  ).toBeInTheDocument();
  expect(
    screen.queryByText(
      "This graph retains citation metadata, not the document body.",
    ),
  ).not.toBeInTheDocument();
  expect(screen.getByRole("link", { name: /Open original/ })).toHaveAttribute(
    "href",
    b.resource.canonicalUrl,
  );
  // Memories have no Drive original, so they keep the plain source record.
  await user.click(screen.getByRole("button", { name: "Close the reader" }));
  expect(container.querySelector(".explorer-reader.framed")).toBeNull();
});

it("reads a memory's full text only while it is open and says when it cannot", async () => {
  const a = record("fictional-memory"),
    b = record("fictional-document", "drive");
  for (const n of [a, b]) {
    n.provenance.demo = false;
    n.resource.content = null;
  }
  const page = { ...emptyGraph(), nodes: [a, b], edges: [relation(a, b)] };
  const knowledge = controller(page);
  const text = vi
    .fn()
    .mockResolvedValueOnce({
      text: "Fictional full memory.\nSecond line of the note.",
      modifiedAt: "2026-10-20T08:00:00Z",
    })
    .mockResolvedValueOnce(null);
  knowledge.session.text = text;
  const { unmount } = render(
    <Explorer
      knowledge={knowledge}
      selected={a.id}
      onSelect={vi.fn()}
      close={vi.fn()}
      paused
      setPaused={vi.fn()}
      state="ready"
    />,
  );
  const user = userEvent.setup();
  expect(text).not.toHaveBeenCalled();
  await user.click(screen.getByRole("button", { name: "Read full memory" }));
  expect(text).toHaveBeenCalledWith(a.id, expect.any(AbortSignal));
  expect(
    await screen.findByText(
      /Fictional full memory\.\s+Second line of the note\./,
    ),
  ).toBeInTheDocument();
  expect(
    screen.getByText(
      /Read from Second Brain just now · last edited 2026-10-20/,
    ),
  ).toBeInTheDocument();
  // Closing drops the text; reopening reads again, and a failed read says so.
  await user.click(screen.getByRole("button", { name: "Close the reader" }));
  expect(screen.queryByText(/Fictional full memory/)).not.toBeInTheDocument();
  await user.click(screen.getByRole("button", { name: "Read full memory" }));
  expect(
    await screen.findByText(
      "This memory's text is unavailable right now. Try again later.",
    ),
  ).toBeInTheDocument();
  expect(text).toHaveBeenCalledTimes(2);
  unmount();
});

it("shows long record titles as short labels while keeping the full title available", () => {
  const a = record("fictional-long-memory"),
    b = record("fictional-short-document", "drive");
  // Shaped like a long Second Brain content prefix with a raw link.
  a.title =
    "Fictional planning note about weekend river walks and the shared reading list, see https://example.test/fictional/path for the draft outline and the notes after it";
  for (const n of [a, b]) n.provenance.demo = false;
  const page = { ...emptyGraph(), nodes: [a, b], edges: [relation(a, b)] };
  const { container } = render(
    <Explorer
      knowledge={controller(page)}
      selected={a.id}
      onSelect={vi.fn()}
      close={vi.fn()}
      paused
      setPaused={vi.fn()}
      state="ready"
    />,
  );
  const part = (kind: string) =>
    [...container.querySelectorAll<SVGGElement>(`.${kind}`)].find(
      (g) => g.dataset.id === a.id,
    )!;
  const lines = [...part("graph-label").querySelectorAll("tspan")].map(
    (t) => t.textContent ?? "",
  );
  // A memory's title takes two short lines; one this long ends the second
  // with an ellipsis.
  expect(lines).toHaveLength(2);
  for (const line of lines) {
    expect(line.length).toBeLessThanOrEqual(26);
    expect(line).not.toMatch(/https?:/);
  }
  expect(lines[0]).toBe("Fictional planning note");
  expect(lines[1].endsWith("…")).toBe(true);
  // The full title stays in the tooltip, the accessible name and the panel.
  expect(part("graph-node").querySelector("title")?.textContent).toContain(
    a.title,
  );
  expect(
    screen.getByRole("button", { name: `Explore ${a.title}` }),
  ).toBeInTheDocument();
  expect(screen.getByRole("heading", { name: a.title })).toBeInTheDocument();
  // The sky header drops the link; CSS keeps it to one line.
  const header = container.querySelector(".exploration-context > span");
  expect(header?.textContent).toMatch(/^Fictional planning note/);
  expect(header?.textContent).not.toMatch(/https?:/);
  // Tap targets are 44 screen pixels across at the fitted zoom, never smaller
  // than the drawn star (820px wide here, so one pixel per unit before zoom).
  const transform =
    container.querySelector("svg.graph-svg > g")?.getAttribute("transform") ??
    "";
  const k = Number(/scale\(([^)]+)\)/.exec(transform)?.[1] ?? 1);
  const r = Number(
    part("graph-hit").querySelector(".node-hit")?.getAttribute("r"),
  );
  expect(r * k).toBeCloseTo(Math.max(22, 9 * k), 6);
  // Label text and its outline are 13px and 3px on screen at the same zoom.
  const text = part("graph-label").querySelector("text") as SVGTextElement;
  expect(parseFloat(text.style.fontSize) * k).toBeCloseTo(13, 6);
  expect(parseFloat(text.style.strokeWidth) * k).toBeCloseTo(3, 6);
});

it("captions folder groups but not groups already named by a theme star", () => {
  const files = [1, 2, 3].map((i) => record(`fictional-file-${i}`, "drive"));
  for (const f of files)
    f.groups = [{ id: "folder-recipes", label: "Fictional recipes" }];
  const notes = [1, 2].map((i) => record(`fictional-note-${i}`));
  const theme = record("fictional-theme");
  theme.type = "theme";
  for (const n of [...notes, theme])
    n.groups = [{ id: "topic-garden", label: "Fictional garden" }];
  const loose = record("fictional-loose");
  delete loose.groups;
  const { container } = render(
    <Constellation
      nodes={[...files, ...notes, theme, loose]}
      edges={[relation(files[0], files[1]), relation(notes[0], theme)]}
      selected={null}
      onSelect={vi.fn()}
      interactive={false}
      healthFocus={false}
      paused
    />,
  );
  const captions = [...container.querySelectorAll(".graph-caption")];
  expect(captions.map((c) => c.textContent)).toEqual(["FICTIONAL RECIPES"]);
  expect(captions[0].closest("g")).toHaveAttribute("aria-hidden", "true");
});

it("names folders and memories linked to two or more records at rest", () => {
  const folder = record("fictional-folder", "drive");
  folder.type = "theme";
  delete folder.groups;
  const files = [1, 2, 3].map((i) => {
    const f = record(`fictional-doc-${i}`, "drive");
    delete f.groups;
    return f;
  });
  const hub = record("fictional-hub-memory"),
    single = record("fictional-single-memory");
  for (const n of [hub, single]) delete n.groups;
  const inFolder = (f: (typeof files)[number]) => ({
    ...relation(f, folder),
    id: `in-folder-${f.id}`,
    type: "in_folder",
  });
  const { container } = render(
    <Constellation
      nodes={[folder, ...files, hub, single]}
      edges={[
        ...files.map(inFolder),
        relation(hub, files[0]),
        relation(hub, files[1]),
        relation(single, files[2]),
      ]}
      selected={null}
      onSelect={vi.fn()}
      interactive={false}
      healthFocus={false}
      paused
    />,
  );
  const classes = (id: string) =>
    [...container.querySelectorAll<SVGGElement>(".graph-label")].find(
      (g) => g.dataset.id === id,
    )!.classList;
  expect(classes(folder.id).contains("folder-node")).toBe(true);
  expect(classes(folder.id).contains("theme-node")).toBe(false);
  expect(classes(hub.id).contains("memory-hub")).toBe(true);
  expect(classes(single.id).contains("memory-hub")).toBe(false);
  expect(classes(files[0].id).contains("memory-hub")).toBe(false);
  // Folders draw as a folder icon, not a topic star.
  expect(
    [...container.querySelectorAll<SVGGElement>(".graph-node")]
      .find((g) => g.dataset.id === folder.id)!
      .querySelector(".folder-glyph"),
  ).not.toBeNull();
});

it("captions memory groups by project name", () => {
  const project = {
    id: "second-brain:project:personal:fictional-cabin",
    label: "Fictional cabin",
  };
  const memories = [1, 2].map((i) => {
    const m = record(`fictional-cabin-${i}`);
    m.groups = [{ id: "second-brain:personal", label: "Personal" }, project];
    return m;
  });
  const { container } = render(
    <Constellation
      nodes={memories}
      edges={[relation(memories[0], memories[1])]}
      selected={null}
      onSelect={vi.fn()}
      interactive={false}
      healthFocus={false}
      paused
    />,
  );
  expect(
    [...container.querySelectorAll(".graph-caption")].map((c) => c.textContent),
  ).toEqual(["FICTIONAL CABIN"]);
});

it("never offers or reads memory text for a Second Brain project", async () => {
  const project = record("project:personal:fictional-ideas");
  project.type = "project";
  project.title = "Fictional ideas";
  const memory = record("fictional-idea");
  memory.type = "memory";
  for (const n of [project, memory]) {
    n.provenance.demo = false;
    n.resource.content = null;
  }
  const membership = {
    ...relation(memory, project),
    id: "in_project:fictional-idea",
    type: "in_project",
    evidence: [
      {
        resourceId: project.id,
        excerpt: "Fictional membership",
        locator: "second-brain:project-membership",
        retrievedAt: "2026-10-18T12:01:00Z",
      },
    ],
  };
  const page = {
    ...emptyGraph(),
    nodes: [project, memory],
    edges: [membership],
  };
  const knowledge = controller(page);
  const text = vi
    .fn()
    .mockResolvedValue({ text: "Fictional", modifiedAt: null });
  knowledge.session.text = text;
  render(
    <Explorer
      knowledge={knowledge}
      selected={project.id}
      onSelect={vi.fn()}
      close={vi.fn()}
      paused
      state="ready"
    />,
  );
  expect(
    screen.queryByRole("button", { name: "Read full memory" }),
  ).not.toBeInTheDocument();
  // Even opened through its link's evidence, a project reads no memory text.
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: /^Why connected:/ }));
  await user.click(screen.getByRole("button", { name: "View source record" }));
  expect(
    screen.getByText(
      "This graph retains citation metadata, not the document body.",
    ),
  ).toBeInTheDocument();
  expect(text).not.toHaveBeenCalled();
});

// Stars for records the engine keeps itself: data stars and saved documents.
function nasStar(account: string, externalId: string, title: string) {
  const star = record(externalId, "rhythm", account);
  star.title = title;
  star.type = account.startsWith("data:") ? "record" : "document";
  star.groups = [{ id: `rhythm:alex:${account}`, label: "Garmin" }];
  star.provenance.demo = false;
  star.resource.content = null;
  star.resource.owner = "alex";
  return star;
}

it("opens a data star's health panel and shows its readings while it is open", async () => {
  const recovery = nasStar(
      "data:garmin",
      "alex:recovery",
      "Sleep and recovery",
    ),
    memory = record("fictional-sleep-note");
  memory.provenance.demo = false;
  const page = {
    ...emptyGraph(),
    nodes: [recovery, memory],
    edges: [relation(memory, recovery)],
  };
  const knowledge = controller(page);
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
  const text = vi.fn();
  knowledge.session.data = data;
  knowledge.session.text = text;
  const onOpenPanel = vi.fn();
  render(
    <Explorer
      knowledge={knowledge}
      selected={recovery.id}
      onSelect={vi.fn()}
      close={vi.fn()}
      paused
      setPaused={vi.fn()}
      state="ready"
      onOpenPanel={onOpenPanel}
    />,
  );
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: /Open Recovery panel/ }));
  expect(onOpenPanel).toHaveBeenCalledWith("recovery");
  expect(data).not.toHaveBeenCalled();
  await user.click(screen.getByRole("button", { name: /Show readings/ }));
  expect(data).toHaveBeenCalledWith(recovery.id, expect.any(AbortSignal));
  // Sleep duration reads as last night's sleep, in plain words, with a chart
  // to read each night.
  expect((await screen.findAllByText("7h 30m")).length).toBeGreaterThan(0);
  expect(screen.getByRole("heading", { name: "Sleep" })).toBeInTheDocument();
  expect(screen.getByText(/Time asleep/)).toBeInTheDocument();
  expect(screen.getByRole("slider", { name: "Sleep by day" })).toHaveAttribute(
    "aria-valuetext",
    expect.stringMatching(/^7h 30m/),
  );
  expect(screen.getByText(/Read from the Rhythm engine just now/)).toBeInTheDocument();
  expect(text).not.toHaveBeenCalled();
  // Closing drops the readings.
  await user.click(screen.getByRole("button", { name: "Close the reader" }));
  expect(screen.queryByText("7h 30m")).not.toBeInTheDocument();
});

it("shows readings for a data star with no panel, and says when they are unavailable", async () => {
  const insulin = nasStar(
      "data:carelink",
      "alex:insulin",
      "Insulin and carbs",
    ),
    other = nasStar(
      "data:garmin",
      "sam:recovery",
      "Sam · Sleep and recovery",
    );
  const page = {
    ...emptyGraph(),
    nodes: [insulin, other],
    edges: [relation(insulin, other)],
  };
  const knowledge = controller(page);
  knowledge.session.data = vi.fn().mockResolvedValue(null);
  render(
    <Explorer
      knowledge={knowledge}
      selected={insulin.id}
      onSelect={vi.fn()}
      close={vi.fn()}
      paused
      setPaused={vi.fn()}
      state="ready"
      onOpenPanel={vi.fn()}
    />,
  );
  // Insulin has no tile, and another person's recovery data never opens the health subject's panel.
  expect(
    screen.queryByRole("button", { name: /panel/ }),
  ).not.toBeInTheDocument();
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: /Show readings/ }));
  expect(
    await screen.findByText(
      "These measurements are unavailable right now. Try again later.",
    ),
  ).toBeInTheDocument();
});

it("reads a document saved on the engine while it is open", async () => {
  const report = nasStar("medical", "alex:pdf-1", "Lab report"),
    memory = record("fictional-lab-note");
  memory.provenance.demo = false;
  const page = {
    ...emptyGraph(),
    nodes: [report, memory],
    edges: [relation(memory, report)],
  };
  const knowledge = controller(page);
  const text = vi.fn().mockResolvedValue({
    text: "Page two.\n\nPage ten.",
    modifiedAt: "2026-10-01T00:00:00Z",
  });
  knowledge.session.text = text;
  render(
    <Explorer
      knowledge={knowledge}
      selected={report.id}
      onSelect={vi.fn()}
      close={vi.fn()}
      paused
      setPaused={vi.fn()}
      state="ready"
    />,
  );
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: /Read saved text/ }));
  expect(text).toHaveBeenCalledWith(report.id, expect.any(AbortSignal));
  expect(
    await screen.findByText(/Page two\.\s+Page ten\./),
  ).toBeInTheDocument();
  expect(
    screen.getByText(/Read from the Rhythm engine just now · last changed 2026-10-01/),
  ).toBeInTheDocument();
});
