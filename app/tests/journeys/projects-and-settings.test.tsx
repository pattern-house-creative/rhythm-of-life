// Projects, a capsule slot and the memory settings, in the app (stories 10 and 18): a person makes a project, finds its
// memories, archives it and brings it back, puts a confirmed memory into a capsule slot, reads the capsule's status slot by
// slot, and reads the memory settings, which only change in rhythm.yaml: changing one still needs the terminal.
import { expect, it } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import {
  aiClient,
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

journey("projects, capsules and settings", () => {
  it("makes a project with its slug's preview, finds its memories, archives and brings it back", async () => {
    const name = `Coppice Way ${run}`;
    const { user, region } = await memoriesView();
    await section(user, region, "Projects");
    await user.click(
      await within(region).findByRole("button", { name: "New project…" }),
    );
    await user.type(within(region).getByLabelText("Name"), name);
    await user.click(
      within(region).getByRole("button", { name: "Make the project" }),
    );
    const list = await within(region).findByRole("list", { name: "Projects" });
    await within(list).findByText(new RegExp(name));
    const project = (await memories({ op: "projects" })).projects.find(
      (p: any) => p.name === name,
    );
    expect(project.slug).toBe(`coppice-way-${run}`);
    const posts = await aiClient("journey-alex", "remember", {
      content: `DEMO: Coppice Way's new gate posts went in (${run}).`,
      project: project.slug,
      idempotency_key: `journey-project-${run}`,
    });
    expect(posts.ok, JSON.stringify(posts)).toBe(true);
    expect(
      (await memories({ op: "list", project: project.slug })).memories.map(
        (m: any) => m.id,
      ),
    ).toEqual([posts.id]);
    await user.click(
      within(rowWith(list, new RegExp(name))).getByRole("button", {
        name: "Archive",
      }),
    );
    await waitFor(async () =>
      expect(
        (await memories({ op: "projects" })).projects.find(
          (p: any) => p.id === project.id,
        )?.status ?? "archived",
      ).toBe("archived"),
    );
    const archived = await within(region).findByRole("list", {
      name: "Archived projects",
    });
    await user.click(
      within(rowWith(archived, new RegExp(name))).getByRole("button", {
        name: "Bring back",
      }),
    );
    await waitFor(async () =>
      expect(
        (await memories({ op: "projects" })).projects.find(
          (p: any) => p.id === project.id,
        )?.status,
      ).toBe("active"),
    );
    // The memory stayed in it throughout.
    expect(
      (await memories({ op: "get", id: posts.id })).memory.project.slug,
    ).toBe(project.slug);
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("puts a confirmed memory into a capsule slot from the view, and an AI client cannot", async () => {
    const maps = await saved(
      `DEMO: this household keeps its fictional maps in the hall drawer (${run}).`,
    );
    const confirmed = await memories({
      op: "status",
      id: maps.id,
      status: "canonical",
      expected: maps.revision,
    });
    expect(confirmed.ok, JSON.stringify(confirmed)).toBe(true);
    const { user, region } = await memoriesView();
    await user.click(
      await within(region).findByRole("button", {
        name: new RegExp(`fictional maps in the hall drawer \\(${run}`),
      }),
    );
    await user.selectOptions(
      await within(region).findByLabelText("Capsule slot"),
      "core:preferences",
    );
    await waitFor(async () =>
      expect(
        (await memories({ op: "get", id: maps.id })).memory?.capsule,
      ).toMatchObject({ kind: "core", slot: "preferences" }),
    );
    const status = await memories({ op: "capsule_status", capsule: "core" });
    expect(JSON.stringify(status)).toContain(maps.id);
    // An AI client asking for a slot through its own tools is refused: only a person fills one.
    const current = (await memories({ op: "get", id: maps.id })).memory
      .revision;
    const refused = await aiClient("journey-alex", "update", {
      id: maps.id,
      tags: ["capsule:core:principles"],
      expected_revision: current,
    });
    expect(
      (await memories({ op: "get", id: maps.id })).memory?.capsule,
      JSON.stringify(refused),
    ).toMatchObject({ kind: "core", slot: "preferences" });
    await user.selectOptions(
      within(region).getByLabelText("Capsule slot"),
      "none",
    );
    await waitFor(async () =>
      expect(
        (await memories({ op: "get", id: maps.id })).memory?.capsule,
      ).toBeNull(),
    );
  });

  it("reads the memory settings, which the view does not change", async () => {
    const { user, region } = await memoriesView();
    await section(user, region, "Settings");
    expect(
      await within(region).findByRole("heading", { name: "Memory settings" }),
    ).toBeInTheDocument();
    const settings = await memories({ op: "settings" });
    expect(settings.ok).toBe(true);
    expect(settings.trashDays).toBe(14);
    await within(region).findByText(/rhythm\.yaml/);
    // Nothing in the section changes a setting: the switches are rhythm.yaml's.
    expect(within(region).queryByRole("switch")).toBeNull();
    expect(within(region).queryByRole("checkbox")).toBeNull();
  });
});
