// A person's own link steps in the Memories view (run 17; API-21; src/MemoryView.tsx): a memory's links and the map's
// suggestions between it and another memory, a person's link to another memory of its scope (and an AI client's
// confirmed by making it again), a link taken away and a suggestion dismissed; the memories that may be out of date, each
// said still true at the revision shown; a flag for a check taken off once its person has checked it. At a phone's size
// and at a desktop's. Fictional data only; the engine is a fake client that answers as app_memories does.
import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import MemoryView from "../src/MemoryView";
import type {
  Memory,
  MemoryClient,
  MemoryConnection,
  MemoryReply,
  StaleMemory,
} from "../src/memories";

const sizes = {
  phone: (query: string) => query.includes("max-width: 760px"),
  desktop: (query: string) => query.includes("min-width: 1100px"),
} as const;
let standard: ((query: string) => MediaQueryList) | undefined;
function at(size: keyof typeof sizes) {
  standard ??= vi.mocked(window.matchMedia).getMockImplementation() as (
    query: string,
  ) => MediaQueryList;
  vi.mocked(window.matchMedia).mockImplementation(
    (query: string) =>
      ({
        matches: sizes[size](query),
        media: query,
        addListener: vi.fn(),
        removeListener: vi.fn(),
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
      }) as unknown as MediaQueryList,
  );
}
afterEach(() => {
  if (standard) vi.mocked(window.matchMedia).mockImplementation(standard);
});

const uuid = (n: number) =>
  `6d3c2a10-0000-4000-8000-${String(n).padStart(12, "0")}`;
const now = "2026-11-04T09:30:00+00:00";
function memory(n: number, content: string, extra: Partial<Memory> = {}) {
  return {
    id: uuid(n),
    scope: "shared",
    owner: "household",
    author: null,
    source: "app",
    kind: "episodic",
    importance: null,
    volatility: null,
    status: "draft",
    tags: [],
    project: null,
    content,
    truncated: false,
    held: null,
    validFrom: null,
    validUntil: null,
    revision: 1,
    createdAt: now,
    updatedAt: now,
    capsule: null,
    state: "current",
    replacedBy: null,
    retractedSources: [],
    ...extra,
  } as Memory;
}
const ok = <T,>(value: T) =>
  ({ available: true, ok: true, ...value }) as unknown as MemoryReply<never>;

// The engine's side: a few fictional memories, their links as connections serves them, and the stale queue. Every
// call is recorded. A test may give the fixed memory's links itself, and say the list was cut.
type Start = {
  links?: (
    near: (m: Memory) => MemoryConnection["memory"],
    m: Record<"leaked" | "fact" | "flagged", Memory>,
  ) => MemoryConnection[];
  truncated?: boolean;
};
function engine(start: Start = {}) {
  const fixed = memory(1, "DEMO: the Wendle shed roof was fixed on Saturday.");
  const leaked = memory(2, "DEMO: the Wendle shed roof leaked in the storm.");
  const fact = memory(3, "DEMO: sheds keep tools dry.", { kind: "semantic" });
  const held = memory(4, "", {
    content: null,
    held: { reason: "instruction", signals: [] },
  });
  const flagged = memory(5, "DEMO: the choir's Tuesday booking stands.", {
    retractedSources: [uuid(6)],
  });
  const memories = new Map(
    [fixed, leaked, fact, held, flagged].map((m) => [m.id, m]),
  );
  const neighbour = (m: Memory): MemoryConnection["memory"] => ({
    id: m.id,
    scope: m.scope,
    owner: m.owner,
    status: m.status,
    state: m.state,
    kind: m.kind,
    preview: m.held ? null : m.content,
    truncated: false,
    held: m.held,
  });
  let links: MemoryConnection[] = [
    {
      linkId: uuid(101),
      made: true,
      type: "relates_to",
      direction: "none",
      verification: "inferred",
      mine: false,
      mayUnlink: true,
      at: now,
      confidence: 0.6,
      memory: neighbour(leaked),
    },
    {
      linkId: null,
      made: false,
      type: "similar_to",
      direction: "none",
      verification: "suggested",
      mine: false,
      mayUnlink: true,
      at: now,
      confidence: 0.81,
      memory: neighbour(fact),
    },
  ];
  if (start.links) links = start.links(neighbour, { leaked, fact, flagged });
  let stale: StaleMemory[] = [
    {
      id: uuid(7),
      scope: "shared",
      owner: "household",
      author: "alex",
      status: "canonical",
      volatility: "volatile",
      reason: "not_confirmed",
      since: "2026-10-12T09:00:00+00:00",
      foundAt: now,
      confirmedAt: null,
      preview: "DEMO: the Pellow bus leaves at 07:40 on weekdays.",
      truncated: false,
      retractedSources: [],
      revision: 3,
    },
    {
      id: flagged.id,
      scope: "shared",
      owner: "household",
      author: null,
      status: "draft",
      volatility: null,
      reason: "retracted_source",
      since: now,
      foundAt: null,
      confirmedAt: null,
      preview: flagged.content!,
      truncated: false,
      retractedSources: [uuid(6)],
      revision: 1,
    },
  ];
  const calls: Record<string, unknown>[] = [];
  const client: MemoryClient = {
    available: vi.fn(async () => true),
    call: vi.fn(async (request: Record<string, unknown>) => {
      calls.push(request);
      const id = request.id as string;
      switch (request.op) {
        case "get":
          return ok({ memory: memories.get(id) ?? null, version: null });
        case "list":
          return ok({
            memories: [...memories.values()].filter(
              (m) => !request.scope || m.scope === request.scope,
            ),
            next: null,
          });
        case "connections":
          return ok({
            id,
            connections: id === fixed.id ? links : [],
            truncated: id === fixed.id && (start.truncated ?? false),
          });
        case "link": {
          const there = links.find(
            (l) =>
              l.made &&
              l.type === request.type &&
              l.memory.id === (id === fixed.id ? request.target : id),
          );
          if (there) {
            const before = there.verification;
            there.verification = "verified";
            return ok({
              op: "link",
              link: there.linkId,
              id,
              target: request.target,
              type: request.type,
              directed: request.type !== "relates_to",
              verification: "verified",
              created: false,
              unchanged: before === "verified",
            });
          }
          const other = memories.get(request.target as string)!;
          links = [
            {
              linkId: uuid(102),
              made: true,
              type: String(request.type),
              direction: request.type === "relates_to" ? "none" : "outgoing",
              verification: "verified",
              mine: true,
              mayUnlink: true,
              at: now,
              confidence: null,
              memory: neighbour(other),
            },
            ...links,
          ];
          return ok({
            op: "link",
            link: uuid(102),
            id,
            target: request.target,
            type: request.type,
            directed: request.type !== "relates_to",
            verification: "verified",
            created: true,
            unchanged: false,
          });
        }
        case "unlink": {
          const gone = links.filter(
            (l) => l.type === request.type && l.memory.id === request.target,
          );
          links = links.filter((l) => !gone.includes(l));
          return ok({
            op: "unlink",
            id,
            target: request.target,
            removed: gone
              .filter((l) => l.made)
              .map((l) => ({ id: l.linkId, type: l.type })),
            kept: [],
            dismissed: gone.filter((l) => !l.made).map((l) => l.type),
            already: 0,
            unchanged: gone.length === 0,
          });
        }
        case "stale":
          return ok({ stale, next: null, count: stale.length });
        case "still_true": {
          const item = stale.find((s) => s.id === id)!;
          stale = stale.filter((s) => s.id !== id);
          return ok({
            op: "still_true",
            id,
            revision: item.revision,
            unchanged: false,
            checkedAt: now,
            was: item.reason,
            retracted: item.retractedSources,
          });
        }
        case "clear_flags": {
          const m = memories.get(id)!;
          memories.set(id, { ...m, retractedSources: [] });
          return ok({
            written: {
              op: "clear_flags",
              id,
              revision: m.revision,
              to: null,
              scope: m.scope,
              owner: m.owner,
              status: m.status,
              scopes: "off",
              project: null,
              unchanged: false,
              replayed: false,
              droppedTags: [],
              held: null,
              trash: null,
              capsule: null,
              projectLeft: false,
              cascade: null,
            },
          });
        }
        default:
          throw new Error(`unexpected op ${String(request.op)}`);
      }
    }),
  };
  return {
    client,
    asked: (op: string) => calls.filter((c) => c.op === op),
    ids: {
      fixed: fixed.id,
      leaked: leaked.id,
      fact: fact.id,
      held: held.id,
      flagged: flagged.id,
    },
  };
}

function view(fake: ReturnType<typeof engine>, start: string | null = null) {
  render(<MemoryView client={fake.client} start={start} onClose={() => {}} />);
  return screen.getByRole("region", { name: "Memories" });
}

describe.each(["phone", "desktop"] as const)(
  "a person's link steps at a %s's size",
  (size) => {
    it("lists a memory's links and the map's suggestions, makes a person's link and takes one away", async () => {
      at(size);
      const fake = engine();
      const region = view(fake, fake.ids.fixed);
      await within(region).findByText(
        "DEMO: the Wendle shed roof was fixed on Saturday.",
      );
      await userEvent.click(
        within(region).getByRole("button", { name: "Links" }),
      );
      const links = await within(region).findByRole("region", {
        name: "Links",
      });
      expect(fake.asked("connections")).toEqual([
        { op: "connections", id: fake.ids.fixed, limit: 50 },
      ]);
      const [guess, suggestion] = within(links).getAllByRole("listitem");
      expect(guess).toHaveTextContent(
        "related to · an AI client's, not yet confirmed · Family",
      );
      expect(guess).toHaveTextContent(
        "DEMO: the Wendle shed roof leaked in the storm.",
      );
      expect(suggestion).toHaveTextContent(
        "similar in meaning · suggested by the map · Family",
      );

      // An AI client's guess, confirmed by the person: their own link of its type, the way it was made.
      await userEvent.click(
        within(guess).getByRole("button", { name: "Confirm" }),
      );
      expect(fake.asked("link").at(-1)).toEqual({
        op: "link",
        id: fake.ids.fixed,
        target: fake.ids.leaked,
        type: "relates_to",
      });
      expect(
        await screen.findByText(
          "Confirmed: the link is yours now, not only a guess.",
        ),
      ).toBeInTheDocument();
      await waitFor(() =>
        expect(within(region).getAllByRole("listitem")[0]).toHaveTextContent(
          "related to · made by a person",
        ),
      );

      // A new link: to another memory of its scope (never a held one), of a type the two kinds allow.
      await userEvent.click(
        within(region).getByRole("button", {
          name: "Link it to another memory…",
        }),
      );
      const target = await within(region).findByLabelText("Link it to");
      expect(fake.asked("list").at(-1)).toEqual({
        op: "list",
        scope: "shared",
        limit: 50,
      });
      const offered = within(target)
        .getAllByRole("option")
        .map((o) => o.getAttribute("value"));
      expect(offered).toEqual([
        fake.ids.leaked,
        fake.ids.fact,
        fake.ids.flagged,
      ]);
      await userEvent.selectOptions(target, fake.ids.leaked);
      await userEvent.selectOptions(
        within(region).getByLabelText("This memory is"),
        "caused_by",
      );
      await userEvent.click(
        within(region).getByRole("button", { name: "Link" }),
      );
      expect(fake.asked("link").at(-1)).toEqual({
        op: "link",
        id: fake.ids.fixed,
        target: fake.ids.leaked,
        type: "caused_by",
      });
      expect(
        await screen.findByText(
          "Linked (caused by). The map and searches follow it for whoever reads both memories.",
        ),
      ).toBeInTheDocument();
      const made = (await within(region).findAllByRole("listitem"))[0];
      expect(made).toHaveTextContent("caused by · made by you · Family");

      // Taken away, behind a confirmation: a link removed, a suggestion dismissed, each of its one type.
      await userEvent.click(
        within(made).getByRole("button", { name: "Unlink…" }),
      );
      const confirm = within(region).getByRole("group", {
        name: "Remove the links of this type between them?",
      });
      await userEvent.click(
        within(confirm).getByRole("button", { name: "Remove the link" }),
      );
      expect(fake.asked("unlink").at(-1)).toEqual({
        op: "unlink",
        id: fake.ids.fixed,
        target: fake.ids.leaked,
        type: "caused_by",
      });
      expect(await screen.findByText("Link removed.")).toBeInTheDocument();
      const map = (await within(region).findAllByRole("listitem")).find((l) =>
        l.textContent?.includes("suggested by the map"),
      )!;
      await userEvent.click(
        within(map).getByRole("button", { name: "Dismiss…" }),
      );
      await userEvent.click(
        within(
          within(region).getByRole("group", {
            name: "Dismiss this suggestion?",
          }),
        ).getByRole("button", { name: "Dismiss" }),
      );
      expect(fake.asked("unlink").at(-1)).toEqual({
        op: "unlink",
        id: fake.ids.fixed,
        target: fake.ids.fact,
        type: "similar_to",
      });
      expect(
        await screen.findByText(
          "Suggestion dismissed: the map won't suggest it again. Discover lists it, if you want it back.",
        ),
      ).toBeInTheDocument();
      // Never a channel or a client: the engine fixes both.
      for (const op of ["link", "unlink", "connections"])
        for (const c of fake.asked(op))
          expect(Object.keys(c)).not.toContain("channel");
    });

    it("offers only the link types the two memories' kinds allow", async () => {
      at(size);
      const fake = engine();
      const region = view(fake, fake.ids.fixed);
      await within(region).findByText(
        "DEMO: the Wendle shed roof was fixed on Saturday.",
      );
      await userEvent.click(
        within(region).getByRole("button", { name: "Links" }),
      );
      await userEvent.click(
        await within(region).findByRole("button", {
          name: "Link it to another memory…",
        }),
      );
      const target = await within(region).findByLabelText("Link it to");
      const type = within(region).getByLabelText(
        "This memory is",
      ) as HTMLSelectElement;
      const disabled = () =>
        [...type.options].filter((o) => o.disabled).map((o) => o.value);
      // Two episodic memories: every type.
      await userEvent.selectOptions(target, fake.ids.leaked);
      expect(disabled()).toEqual([]);
      await userEvent.selectOptions(type, "decided");
      // A semantic one: decided and follows join two episodic memories only, so the choice goes back to related to.
      await userEvent.selectOptions(target, fake.ids.fact);
      expect(disabled()).toEqual(["decided", "follows"]);
      expect(type.value).toBe("relates_to");
      expect([...type.options].map((o) => o.textContent)).toEqual([
        "related to",
        "caused by",
        "decided",
        "about",
        "part of",
        "follows",
        "drawn from",
      ]);
      // Cancelled: nothing is sent.
      await userEvent.click(
        within(region).getByRole("button", { name: "Cancel" }),
      );
      expect(fake.asked("link")).toEqual([]);
    });

    it("lists what may be out of date and says one is still true at the revision shown", async () => {
      at(size);
      const fake = engine();
      const region = view(fake);
      await userEvent.click(
        await within(region).findByRole("button", { name: "To check" }),
      );
      expect(
        await within(region).findByRole("heading", {
          name: "May be out of date",
        }),
      ).toBeInTheDocument();
      expect(
        await within(region).findByText(/^2 memories may be out of date\./),
      ).toBeInTheDocument();
      const list = within(region).getByRole("list", {
        name: "May be out of date",
      });
      const [bus, booking] = within(list).getAllByRole("listitem");
      expect(bus).toHaveTextContent(
        "DEMO: the Pellow bus leaves at 07:40 on weekdays.",
      );
      expect(bus).toHaveTextContent(/Unchanged since .* · Family · revision 3/);
      expect(booking).toHaveTextContent(
        "Built on a memory marked wrong since · Family · revision 1",
      );
      await userEvent.click(
        within(bus).getByRole("button", { name: "Still true" }),
      );
      expect(fake.asked("still_true")).toEqual([
        { op: "still_true", id: uuid(7), expected: 3 },
      ]);
      expect(
        await screen.findByText("Marked still true: nothing in it changed."),
      ).toBeInTheDocument();
      expect(
        await within(region).findByText(/^1 memory may be out of date\./),
      ).toBeInTheDocument();
      // One built on a memory marked wrong: its flag comes off with it.
      await userEvent.click(
        within(
          within(region).getByRole("list", { name: "May be out of date" }),
        ).getByRole("button", { name: "Still true" }),
      );
      expect(
        await screen.findByText(
          "Marked still true: nothing in it changed. Its flag for a check came off.",
        ),
      ).toBeInTheDocument();
      expect(
        await within(region).findByText("Nothing looks out of date."),
      ).toBeInTheDocument();
    });

    it("takes a flag for a check off once its person has checked it", async () => {
      at(size);
      const fake = engine();
      const region = view(fake, fake.ids.flagged);
      const note = await within(region).findByRole("note");
      expect(note).toHaveTextContent(
        "Check that it still holds; once you have, take the flag off here.",
      );
      await userEvent.click(
        within(note).getByRole("button", {
          name: "It still holds: take the flag off",
        }),
      );
      expect(fake.asked("clear_flags")).toEqual([
        { op: "clear_flags", id: fake.ids.flagged, expected: 1 },
      ]);
      expect(
        await screen.findByText("Flag taken off: it stands as you checked it."),
      ).toBeInTheDocument();
      await waitFor(() =>
        expect(within(region).queryByRole("note")).toBeNull(),
      );
    });

    it("says whose guess a link is, confirms an automatic one, says when the list is cut and offers an unlink only when every link of its type is the person's", async () => {
      at(size);
      const made = (
        n: number,
        type: string,
        direction: MemoryConnection["direction"],
        verification: MemoryConnection["verification"],
        mine: boolean,
        mayUnlink: boolean,
        memory: MemoryConnection["memory"],
      ): MemoryConnection => ({
        linkId: uuid(n),
        made: true,
        type,
        direction,
        verification,
        mine,
        mayUnlink,
        at: now,
        confidence: null,
        memory,
      });
      const fake = engine({
        truncated: true,
        links: (near, m) => [
          // The person's own AI client's guess: theirs to confirm, not theirs yet.
          made(
            201,
            "relates_to",
            "none",
            "inferred",
            true,
            true,
            near(m.leaked),
          ),
          // An automatic link (an import's, say): confirmed the same way.
          made(
            202,
            "caused_by",
            "outgoing",
            "suggested",
            false,
            true,
            near(m.fact),
          ),
          // Two links of one type between the same two memories, either way, one of them not the person's to remove.
          made(
            203,
            "follows",
            "outgoing",
            "verified",
            true,
            true,
            near(m.flagged),
          ),
          made(
            204,
            "follows",
            "incoming",
            "verified",
            false,
            false,
            near(m.flagged),
          ),
        ],
      });
      const region = view(fake, fake.ids.fixed);
      await within(region).findByText(
        "DEMO: the Wendle shed roof was fixed on Saturday.",
      );
      await userEvent.click(
        within(region).getByRole("button", { name: "Links" }),
      );
      const links = await within(region).findByRole("region", {
        name: "Links",
      });
      const [guess, automatic, ours, theirs] =
        within(links).getAllByRole("listitem");
      expect(guess).toHaveTextContent(
        "made by your AI client, not yet confirmed",
      );
      expect(guess).not.toHaveTextContent("made by you ·");
      expect(
        within(guess).getByRole("button", { name: "Confirm" }),
      ).toBeInTheDocument();
      expect(automatic).toHaveTextContent(
        "suggested automatically, not yet confirmed",
      );
      expect(ours).toHaveTextContent("made by you");
      expect(theirs).toHaveTextContent("made by a person");
      // The unlink would take both follows links, and one is not the person's: neither row offers it.
      expect(
        within(ours).queryByRole("button", { name: "Unlink…" }),
      ).toBeNull();
      expect(
        within(theirs).queryByRole("button", { name: "Unlink…" }),
      ).toBeNull();
      expect(links).toHaveTextContent(
        "More links than these 4: the newest are shown.",
      );

      await userEvent.click(
        within(automatic).getByRole("button", { name: "Confirm" }),
      );
      expect(fake.asked("link").at(-1)).toEqual({
        op: "link",
        id: fake.ids.fixed,
        target: fake.ids.fact,
        type: "caused_by",
      });
      expect(
        await screen.findByText(
          "Confirmed: the link is yours now, not only a guess.",
        ),
      ).toBeInTheDocument();

      // The prompt names what an unlink takes: every link of its type between the two.
      await userEvent.click(
        within(within(region).getAllByRole("listitem")[0]).getByRole("button", {
          name: "Unlink…",
        }),
      );
      expect(
        within(region).getByRole("group", {
          name: "Remove the links of this type between them?",
        }),
      ).toHaveTextContent(
        'Remove every "related to" link between these two memories? Linking them again makes a new one.',
      );
    });

    it("says a link an import brought is imported, never a person's or an AI client's", async () => {
      at(size);
      const imported = (
        n: number,
        verification: MemoryConnection["verification"],
        memory: MemoryConnection["memory"],
      ): MemoryConnection => ({
        linkId: uuid(n),
        made: true,
        type: "relates_to",
        direction: "none",
        verification,
        mine: false,
        imported: true,
        mayUnlink: true,
        at: now,
        confidence: 0.5,
        memory,
      });
      const fake = engine({
        links: (near, m) => [
          // A Second Brain export's link: one its own jobs drew (inferred here) and one stated there (verified). The
          // file says neither who made it, so neither is a person's or an AI client's here.
          imported(301, "inferred", near(m.leaked)),
          imported(302, "verified", near(m.fact)),
        ],
      });
      const region = view(fake, fake.ids.fixed);
      await within(region).findByText(
        "DEMO: the Wendle shed roof was fixed on Saturday.",
      );
      await userEvent.click(
        within(region).getByRole("button", { name: "Links" }),
      );
      const links = await within(region).findByRole("region", {
        name: "Links",
      });
      const [drawn, stated] = within(links).getAllByRole("listitem");
      expect(drawn).toHaveTextContent(
        "related to · imported, not yet confirmed",
      );
      expect(drawn).not.toHaveTextContent("AI client");
      expect(
        within(drawn).getByRole("button", { name: "Confirm" }),
      ).toBeInTheDocument();
      expect(stated).toHaveTextContent("related to · imported, confirmed");
      expect(stated).not.toHaveTextContent("made by a person");
      expect(
        within(stated).queryByRole("button", { name: "Confirm" }),
      ).toBeNull();
    });
  },
);
