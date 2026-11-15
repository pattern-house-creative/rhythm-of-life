// A person saves a memory in the app and finds it again: in the view's list, by its tag and project, in the explorer's
// search and on the map, and an AI client reads the same memory. At a phone's size and a desktop's.
import { describe, expect, it } from "vitest";
import { screen, within } from "@testing-library/react";
import {
  aiClient,
  appJson,
  at,
  eventually,
  inSandbox,
  journey,
  memoriesView,
  memories,
  run,
} from "./sandbox";

inSandbox();

journey("saving a memory and finding it again", () => {
  describe.each(["phone", "desktop"] as const)("at a %s's size", (size) => {
    it("saves from the view, finds it in the list, by its tag, in the search and on the map", async () => {
      at(size);
      const place = size === "phone" ? "Wrenfield" : "Thistledown";
      const tag = `journey${size}${run}`;
      const words = `DEMO: the ${place} allotment shed key hangs behind the rain gauge. #${tag}`;
      const { user, region } = await memoriesView();
      await user.type(within(region).getByLabelText("Save a memory"), words);
      await user.click(within(region).getByRole("button", { name: "Save" }));
      expect(
        await within(region).findByText("Saved as a draft for the household."),
      ).toBeInTheDocument();
      // It is in the list at once, and in the engine as the app's.
      const list = await within(region).findByRole("list", {
        name: "Recent memories",
      });
      expect(
        (
          await within(list).findAllByText(
            new RegExp(`${place} allotment shed key`),
          )
        ).length,
      ).toBeGreaterThan(0);
      const saved = (await memories({ op: "list", tag: tag })).memories;
      expect(saved).toHaveLength(1);
      const history = await memories({ op: "history", id: saved[0].id });
      expect(
        history.history.changes.map((c: any) => [c.event, c.channel]),
      ).toContainEqual(["created", "app"]);

      // The tag the words named is on offer, and the filter finds just it.
      const filters = within(region).getByRole("group", { name: /Filter/ });
      await user.selectOptions(
        await within(filters).findByLabelText("Tag"),
        tag,
      );
      expect(
        await within(region).findByText("1 memory match."),
      ).toBeInTheDocument();
      await user.click(
        within(filters).getByRole("button", { name: "Clear filters" }),
      );

      // An AI client reads the same memory, and the explorer's combined search finds it once the map has it.
      const read = await aiClient("journey-alex", "get", { id: saved[0].id });
      expect(read.memory.content).toBe(words);
      await eventually(
        async () => {
          await appJson("/api/graph-refresh", {});
          return appJson("/api/graph", {
            operation: "recall",
            value: `${place} allotment shed key`,
          });
        },
        (found) =>
          JSON.stringify(found).includes(`${place} allotment shed key`),
        "found in the explorer's search",
      );
      await user.click(screen.getByRole("button", { name: "Memories" }));
      expect(screen.queryByRole("region", { name: "Memories" })).toBeNull();
    });
  });
});
