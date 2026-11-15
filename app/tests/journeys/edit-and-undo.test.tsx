// A person changes a memory in the app and takes the changes back (story 3): edit, an added update, confirm, undo, history
// as a timeline, a revision read and put back, its project and tags, a link to another memory, and a stale change refused in
// plain words because an AI client changed it meanwhile. Every change is the app's in its history, and an AI client reads
// what the person left. No terminal.
import { expect, it } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import {
  aiClient,
  inSandbox,
  journey,
  memories,
  memoriesView,
  run,
  saved,
} from "./sandbox";

inSandbox();

journey("changing a memory and taking it back", () => {
  it("edits, adds, confirms, undoes and puts a revision back, each the app's, and refuses a stale edit", async () => {
    const first = `DEMO: the Pennyroyal Court bike store code is 4471 (${run}).`;
    const made = await saved(first);
    const { user, region } = await memoriesView();
    await user.click(
      await within(region).findByRole("button", {
        name: new RegExp(`Pennyroyal Court bike store code is 4471 \\(${run}`),
      }),
    );
    expect(await within(region).findByText(first)).toBeInTheDocument();

    // Edit.
    const second = `DEMO: the Pennyroyal Court bike store code is 5582 (${run}).`;
    await user.click(within(region).getByRole("button", { name: "Edit" }));
    const box = within(region).getByLabelText("Edit the text");
    await user.clear(box);
    await user.type(box, second);
    await user.click(within(region).getByRole("button", { name: "Save" }));
    await waitFor(() =>
      expect(within(region).getByText(second)).toBeInTheDocument(),
    );

    // An added update.
    await user.click(
      await within(region).findByRole("button", { name: "Add an update" }),
    );
    await user.type(
      within(region).getByRole("textbox"),
      "Changed by the landlord.",
    );
    await user.click(
      within(region).getByRole("button", { name: "Add update" }),
    );
    // Found and checked in one go: the view draws the memory again once the update is written, so an element found
    // before that can be gone by the time it is checked.
    await waitFor(() =>
      expect(
        within(region).getAllByText(/Changed by the landlord/)[0],
      ).toBeInTheDocument(),
    );

    // Confirm it.
    await user.selectOptions(
      await within(region).findByLabelText("Status"),
      "canonical",
    );
    await waitFor(async () =>
      expect((await memories({ op: "get", id: made.id })).memory?.status).toBe(
        "canonical",
      ),
    );

    // An AI client reads what the person wrote, and undoing the status change is a change of its own.
    const read = await aiClient("journey-alex", "get", { id: made.id });
    expect(read.memory.content).toMatch(/5582/);
    await user.click(
      within(region).getByRole("button", { name: /Undo last change/ }),
    );
    await waitFor(async () =>
      expect((await memories({ op: "get", id: made.id })).memory?.status).toBe(
        "draft",
      ),
    );

    // History, as a timeline, every change the app's; a revision read and put back.
    await user.click(within(region).getByRole("button", { name: /History/ }));
    const timeline = await within(region).findByRole("list", {
      name: "History",
    });
    const history = await memories({ op: "history", id: made.id });
    expect([
      ...new Set(history.history.changes.map((c: any) => c.channel)),
    ]).toEqual(["app"]);
    expect(history.history.changes.map((c: any) => c.event)).toEqual(
      expect.arrayContaining(["created", "updated"]),
    );
    expect(within(timeline).getAllByRole("listitem").length).toBe(
      history.history.changes.length,
    );
    const oldest = within(timeline).getAllByRole("listitem").at(-1)!;
    await user.click(within(oldest).getByRole("button", { name: "Put back" }));
    await waitFor(async () =>
      expect((await memories({ op: "get", id: made.id })).memory?.content).toBe(
        first,
      ),
    );

    // Meanwhile an AI client changes it: the person's next edit is refused in plain words, and changes nothing.
    const current = await memories({ op: "get", id: made.id });
    await aiClient("journey-alex", "update", {
      id: made.id,
      content: `${first} Checked by an AI client.`,
      expected_revision: current.memory.revision,
    });
    await user.click(
      await within(region).findByRole("button", { name: "Edit" }),
    );
    const again = within(region).getByLabelText("Edit the text");
    await user.clear(again);
    await user.type(again, "DEMO: a stale edit of the bike store code.");
    await user.click(within(region).getByRole("button", { name: "Save" }));
    expect(
      await within(region).findByText(
        /changed since|changed after|since you read/i,
      ),
    ).toBeInTheDocument();
    expect(
      (await memories({ op: "get", id: made.id })).memory?.content,
    ).toMatch(/Checked by an AI client/);
  });

  it("gives a memory its project and tags, and a link to another, from the view", async () => {
    const one = await saved(
      `DEMO: the Marlpit Lane water stopcock is under the hall floor (${run}).`,
    );
    const two = await saved(
      `DEMO: Marlpit Lane's plumber fitted the stopcock in spring (${run}).`,
    );
    const made = await memories({
      op: "project_create",
      name: `Marlpit ${run}`,
    });
    expect(made.ok, JSON.stringify(made)).toBe(true);
    const { user, region } = await memoriesView();
    await user.click(
      await within(region).findByRole("button", {
        name: new RegExp(`water stopcock is under the hall floor \\(${run}`),
      }),
    );
    await user.click(
      await within(region).findByRole("button", { name: /Project and tags/ }),
    );
    await user.selectOptions(
      await within(region).findByLabelText("Project"),
      made.project.slug,
    );
    await user.click(within(region).getByRole("button", { name: "Save" }));
    await waitFor(async () =>
      expect(
        (await memories({ op: "get", id: one.id })).memory?.project?.slug,
      ).toBe(made.project.slug),
    );

    await user.click(
      await within(region).findByRole("button", { name: /Links/ }),
    );
    const links = await within(region).findByRole("region", { name: "Links" });
    await user.click(
      within(links).getByRole("button", { name: /Link it to another memory/ }),
    );
    await user.selectOptions(
      await within(links).findByLabelText("Link it to"),
      two.id,
    );
    await user.click(within(links).getByRole("button", { name: "Link" }));
    await waitFor(async () => {
      const seen = await memories({ op: "connections", id: one.id });
      expect(JSON.stringify(seen)).toContain(two.id);
    });
    expect(screen.queryByRole("dialog")).toBeNull();
  });
});
