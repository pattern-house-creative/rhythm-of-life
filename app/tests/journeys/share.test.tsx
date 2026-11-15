// Sharing (stories 1 and 2), with memory scopes and memory.share on. The app's own login is the household view, which reads
// no private memory and shares nothing (docs/dev/memory.md 7.1; a person's own login in the app is a viewer session, which
// app.viewers keeps off, D-046), so THIS JOURNEY STILL NEEDS A TERMINAL or an AI client: a person shares with rhythm
// share-memory. What the app does: Family's list shows the memory once it is shared and not once it is taken back, and it
// offers no share of its own.
import { expect, it } from "vitest";
import { screen, within } from "@testing-library/react";
import { corpusId } from "../../../cli/journeys-corpus.mjs";
import {
  aiClient,
  appJson,
  inSandbox,
  journey,
  memories,
  memoriesView,
  run,
  saved as savedByApp,
  terminal,
} from "./sandbox";

inSandbox();

journey("sharing one memory exactly", () => {
  it("keeps a private memory from the household view and Sam, until its author shares it, and takes it back", async () => {
    const words = `DEMO: Alex keeps the Gorseway Cottage shed padlock code in her wallet (${run}).`;
    const saved = await aiClient("journey-alex", "remember", {
      content: words,
      idempotency_key: `journey-private-${run}`,
    });
    expect([saved.ok, saved.scope, saved.owner], JSON.stringify(saved)).toEqual(
      [true, "private", "alex"],
    );

    // Story 1: no one else reads it, the household view included.
    expect((await aiClient("journey-sam", "get", { id: saved.id })).code).toBe(
      "unknown",
    );
    expect(
      JSON.stringify(
        await aiClient("journey-sam", "recall", {
          query: "Gorseway Cottage shed padlock",
        }),
      ),
    ).not.toContain(saved.id);
    expect((await memories({ op: "get", id: saved.id })).code).toBe("unknown");
    await savedByApp(
      `DEMO: the Gorseway Cottage bins go round the side (${run}).`,
      { scope: "shared" },
    );
    const bins = new RegExp(
      `Gorseway Cottage bins go round the side \\(${run}`,
    );
    const { user, region, view } = await memoriesView();
    // The list has its rows (Family's memory saved just after the private one is there), and the private one is not.
    const recent = await within(region).findByRole("list", {
      name: "Recent memories",
    });
    await within(recent).findAllByText(bins);
    expect(
      within(region).queryByText(/Gorseway Cottage shed padlock/),
    ).toBeNull();

    // The app offers no share on a Family memory it does show.
    await user.click(
      await within(region).findByRole("button", {
        name: new RegExp(`Gorseway Cottage bins go round the side \\(${run}`),
      }),
    );
    await within(region).findByRole("heading", { name: "Memory" });
    expect(
      within(region).queryByRole("button", {
        name: /Share with Family|Make private/,
      }),
    ).toBeNull();
    view.unmount();

    // Story 2, at the terminal: its author shares it. An AI client's token could too; the app cannot.
    const shared = terminal("share-memory", "--person", "alex", saved.id);
    expect(shared).toMatch(/shared|Family/i);
    expect(
      (await aiClient("journey-sam", "get", { id: saved.id })).memory?.content,
    ).toBe(words);
    expect(
      JSON.stringify(
        await aiClient("journey-sam", "recall", {
          query: "Gorseway Cottage shed padlock",
        }),
      ),
    ).toContain(saved.id);
    const again = await memoriesView();
    await within(again.region).findByText(
      new RegExp(`shed padlock code in her wallet \\(${run}`),
    );
    const history = await memories({ op: "history", id: saved.id });
    expect(history.history.changes.map((c: any) => c.event)).toContain(
      "shared",
    );
    // Sam is neither its author nor an admin, so may not take it back.
    const refused = await aiClient("journey-sam", "share", {
      id: saved.id,
      workspace: "private",
    });
    expect(refused.code).toBe("forbidden");
    again.view.unmount();

    // Its author takes it back, at the terminal: Sam's next read and the household view's find nothing of it.
    terminal("share-memory", "--person", "alex", "--private", saved.id);
    expect((await aiClient("journey-sam", "get", { id: saved.id })).code).toBe(
      "unknown",
    );
    expect((await memories({ op: "get", id: saved.id })).code).toBe("unknown");
    const last = await memoriesView();
    const after = await within(last.region).findByRole("list", {
      name: "Recent memories",
    });
    await within(after).findAllByText(bins);
    expect(within(last.region).queryByText(/shed padlock code/)).toBeNull();
    expect(
      (await aiClient("journey-alex", "get", { id: saved.id })).memory?.content,
    ).toBe(words);
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("keeps the corpus's private memories to their own people, and a person's sources from the household view", async () => {
    // Sam's surprise for Alex's 40th: his to read, and no one else's, in any way of asking.
    const party = corpusId("mem-sam-party");
    const question = "surprise birthday party at the boathouse";
    expect(
      (await aiClient("journey-sam", "get", { id: party })).memory?.content,
    ).toMatch(/surprise 40th birthday party/);
    expect(
      JSON.stringify(
        await aiClient("journey-sam", "recall", { query: question }),
      ),
    ).toContain(party);
    expect((await aiClient("journey-alex", "get", { id: party })).code).toBe(
      "unknown",
    );
    expect(
      JSON.stringify(
        await aiClient("journey-alex", "recall", { query: question }),
      ),
    ).not.toContain(party);
    expect((await memories({ op: "get", id: party })).code).toBe("unknown");
    expect(
      JSON.stringify(await memories({ op: "list", limit: 50 })),
    ).not.toMatch(/Ottervale boathouse/);
    // Alex's notes and glucose are hers until she shares them: not on the household view's map, and no tile.
    const map = await appJson("/api/graph", { operation: "discover" });
    expect(JSON.stringify(map)).not.toContain("DEMO · Bike commute log");
    const health = await appJson("/api/observatory");
    expect(health.current.value).toBeNull();
    expect(health.sources.find((s: any) => s.id === "glucose").status).toBe(
      "disconnected",
    );
  });
});
