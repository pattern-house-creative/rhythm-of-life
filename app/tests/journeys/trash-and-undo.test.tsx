// Forgetting, the trash and undo (story 3), in the app alone: a memory forgotten behind a question and restored from the
// trash with its words, several forgotten at once and undone at once, one deleted for good only once a person confirms, and
// an AI client's whole group of changes undone and redone from AI changes. At a phone's size and a desktop's. No terminal.
import { describe, expect, it } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import {
  aiClient,
  at,
  inSandbox,
  journey,
  memories,
  memoriesView,
  rowWith,
  run,
  saved,
  section,
} from "./sandbox";

inSandbox();

journey("the trash and undo", () => {
  describe.each(["phone", "desktop"] as const)("at a %s's size", (size) => {
    const mark = `${run}${size[0]}`;

    it("forgets behind a question, reads it in the trash with its days left, restores it with its words", async () => {
      at(size);
      const words = `DEMO: the Bramblegate spare key is with the neighbour at thirty (${mark}).`;
      const key = await saved(words);
      const named = new RegExp(
        `Bramblegate spare key is with the neighbour at thirty \\(${mark}`,
      );
      const { user, region } = await memoriesView();
      await user.click(
        await within(region).findByRole("button", { name: named }),
      );
      await within(region).findByText(words);
      await user.click(within(region).getByRole("button", { name: /Forget…/ }));
      await user.click(
        within(
          within(region).getByRole("group", { name: "Forget it?" }),
        ).getByRole("button", { name: "Move to the trash" }),
      );
      await waitFor(async () =>
        expect((await memories({ op: "get", id: key.id })).code).toBe(
          "unknown",
        ),
      );
      // Out of an AI client's read too, and its own trash lists it for bringing back.
      expect((await aiClient("journey-alex", "get", { id: key.id })).code).toBe(
        "unknown",
      );

      await section(user, region, "Trash");
      const trash = await within(region).findByRole("list", { name: "Trash" });
      await within(trash).findByText(named);
      const row = rowWith(trash, named);
      expect(row).toHaveTextContent(/1[34] days left/);
      await user.click(within(row).getByRole("button", { name: /Restore/ }));
      await waitFor(async () =>
        expect(
          (await memories({ op: "get", id: key.id })).memory?.content,
        ).toBe(words),
      );
      const history = await memories({ op: "history", id: key.id });
      expect(history.history.changes.map((c: any) => c.event)).toEqual(
        expect.arrayContaining(["created", "trashed", "restored"]),
      );
      expect(
        (await aiClient("journey-alex", "get", { id: key.id })).memory?.content,
      ).toBe(words);
    });

    it("forgets several at once and undoes them at once; deletes one for good only behind a confirmation", async () => {
      at(size);
      const tag = `fence${mark}`;
      const made = [];
      for (const n of [1, 2, 3])
        made.push(
          await saved(
            `DEMO: Sedgeley Row fence panel ${n} needs staining (${mark}).`,
            { tags: [tag] },
          ),
        );
      const { user, region } = await memoriesView();
      const filters = within(region).getByRole("group", {
        name: "Filter these memories",
      });
      await within(filters).findByRole("option", { name: new RegExp(tag) });
      await user.selectOptions(within(filters).getByLabelText("Tag"), tag);
      const list = await within(region).findByRole("list", {
        name: "Recent memories",
      });
      await waitFor(() =>
        expect(within(list).getAllByRole("listitem")).toHaveLength(3),
      );
      for (const box of within(list).getAllByRole("checkbox"))
        await user.click(box);
      const bar = within(region).getByRole("group", {
        name: "The memories you chose",
      });
      await user.click(within(bar).getByRole("button", { name: "Forget…" }));
      await user.click(
        within(region).getByRole("button", { name: "Move to the trash" }),
      );
      await waitFor(async () =>
        expect((await memories({ op: "list", tag })).memories).toHaveLength(0),
      );
      const said = await within(region).findByRole("status");
      await user.click(within(said).getByRole("button", { name: "Undo" }));
      await waitFor(async () =>
        expect((await memories({ op: "list", tag })).memories).toHaveLength(3),
      );

      // One deleted for good: forgotten first, then deleted only once a person says so, and gone.
      const gone = made[0];
      const current = await memories({ op: "get", id: gone.id });
      expect(
        (
          await memories({
            op: "forget",
            id: gone.id,
            expected: current.memory.revision,
          })
        ).ok,
      ).toBe(true);
      await section(user, region, "Trash");
      const trash = await within(region).findByRole("list", { name: "Trash" });
      const named = new RegExp(`fence panel 1 needs staining \\(${mark}`);
      await within(trash).findByText(named);
      await user.click(
        within(rowWith(trash, named)).getByRole("button", {
          name: /Delete for good…/,
        }),
      );
      const ask = within(rowWith(trash, named)).getByRole("group", {
        name: "Delete for good?",
      });
      await user.click(within(ask).getByRole("button", { name: "Keep it" }));
      await user.click(
        within(rowWith(trash, named)).getByRole("button", {
          name: /Delete for good…/,
        }),
      );
      await user.click(
        within(
          within(rowWith(trash, named)).getByRole("group", {
            name: "Delete for good?",
          }),
        ).getByRole("button", { name: "Delete for good" }),
      );
      expect(
        await within(region).findByText("Deleted for good."),
      ).toBeInTheDocument();
      await waitFor(async () =>
        expect(
          JSON.stringify(
            (await memories({ op: "trash", limit: 50 })).trash ?? [],
          ),
        ).not.toContain(gone.id),
      );
      expect(screen.queryByRole("dialog")).toBeNull();
    });

    it("undoes an AI client's group of changes in one step, and redoes it", async () => {
      at(size);
      // An AI client edits three memories, one after another: one group of its changes.
      const ids: string[] = [];
      for (const n of [1, 2, 3]) {
        const hedge = await saved(
          `DEMO: Yarrowfield hedge section ${n} wants trimming (${mark}).`,
        );
        const edited = await aiClient("journey-sam", "update", {
          id: hedge.id,
          content: `DEMO: Yarrowfield hedge section ${n} was trimmed by an AI client's word (${mark}).`,
          expected_revision: hedge.revision,
        });
        expect(edited.ok, JSON.stringify(edited)).toBe(true);
        ids.push(hedge.id);
      }
      const { user, region } = await memoriesView();
      await section(user, region, "AI changes");
      const list = await within(region).findByRole("list", {
        name: "AI changes",
      });
      const word = new RegExp(
        `Yarrowfield hedge section \\d was trimmed by an AI client's word \\(${mark}`,
      );
      const hit = (await within(list).findAllByText(word))[0];
      let group: HTMLElement = hit;
      while (group.parentElement && group.parentElement !== list)
        group = group.parentElement;
      await user.click(
        within(group).getByRole("button", { name: /Undo these/ }),
      );
      const read = () =>
        Promise.all(
          ids.map(
            async (id) =>
              (await memories({ op: "get", id })).memory.content as string,
          ),
        );
      await waitFor(async () =>
        expect((await read()).every((c) => c.includes("wants trimming"))).toBe(
          true,
        ),
      );
      const said = await within(region).findByRole("status");
      expect(said).toHaveTextContent(/Undone: 3 memories are back/);
      await user.click(within(said).getByRole("button", { name: /Redo|Undo/ }));
      await waitFor(async () =>
        expect((await read()).every((c) => word.test(c))).toBe(true),
      );
    });
  });
});
