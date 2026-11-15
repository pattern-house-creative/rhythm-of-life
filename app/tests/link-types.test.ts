import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { linkKindsAllow, linkType, linkTypes } from "../src/knowledge";
import { linkName, linkWords } from "../src/graphView";

// The link vocabulary (SB-P4-01, D-030): the app's table is the engine's,
// word for word, and every type it lists has plain words.
describe("link types", () => {
  // Migration 051's link_types(), one type a line: its name, direction, kinds
  // and whether it is upstream's.
  const listed = [
    ...readFileSync(
      "../engine/migrations/051_typed_links.sql",
      "utf8",
    ).matchAll(
      /^\s*\('([a-z_]+)',(true|false),(NULL(?:::text\[\])?|ARRAY\['episodic'\]),(true|false)\),?$/gm,
    ),
  ].map(([, name, directed, kinds, upstream]) => ({
    name,
    directed: directed === "true",
    kinds: kinds.startsWith("ARRAY") ? ["episodic"] : undefined,
    upstream: upstream === "true",
  }));

  it("lists the engine's types, with their directions and kinds", () => {
    expect(listed).toHaveLength(15);
    expect(
      Object.entries(linkTypes).map(([name, t]) => ({
        name,
        directed: t.directed,
        kinds: t.kinds ? [...t.kinds] : undefined,
        upstream: !!t.upstream,
      })),
    ).toEqual(listed);
    // The engine's own list names the same types.
    const engine = readFileSync("../engine/src/Core/LinkTypes.cs", "utf8");
    for (const { name } of listed) expect(engine).toContain(`new("${name}",`);
  });

  it("joins decided and follows to two episodic memories only", () => {
    const kinds = ["episodic", "semantic", null] as const;
    for (const type of [...Object.keys(linkTypes), "supports"])
      for (const a of kinds)
        for (const b of kinds)
          expect(linkKindsAllow(type, a, b)).toBe(
            type === "decided" || type === "follows"
              ? a === "episodic" && b === "episodic"
              : true,
          );
    expect(linkType("supports")).toBeUndefined();
    expect(linkType("toString")).toBeUndefined();
  });

  it("names every type in plain words, from either end", () => {
    expect(Object.keys(linkWords).sort()).toEqual(
      Object.keys(linkTypes).sort(),
    );
    for (const [type, t] of Object.entries(linkTypes))
      if (!t.directed) expect(linkName(type, false)).toBe(linkName(type));
    expect([linkName("supersedes"), linkName("supersedes", false)]).toEqual([
      "replaces",
      "replaced by",
    ]);
    expect(linkName("relates_to")).toBe("related to");
    // Rhythm's own read as the explorer names them.
    expect([linkName("in_folder"), linkName("in_folder", false)]).toEqual([
      "folder",
      "in this folder",
    ]);
    expect(linkName("copy_of")).toBe("copy of");
    // A source's own word keeps its words.
    expect(linkName("draws_on")).toBe("draws on");
    expect(linkName("constructor")).toBe("constructor");
  });
});
