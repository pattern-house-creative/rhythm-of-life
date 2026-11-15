import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { Markdown } from "../src/markdown";
import { noteLinks } from "../src/knowledge";
import type { GraphNode } from "../src/knowledge";
import { record, relation } from "./synthetic";

// Fictional notes from a notes folder, keyed by their path.
const note = (path: string, title = path.replace(/^.*\/|\.md$/g, "")) => {
  const n = record(path, "notes", "notes");
  n.type = "note";
  n.title = `DEMO · ${title}`;
  return n;
};
const shown = (text: string, props = {}) =>
  render(<Markdown text={text} {...props} />).container.firstElementChild!;

describe("the note reader's Markdown", () => {
  it("renders headings, paragraphs, lists, emphasis, code, quotes and rules", () => {
    const view = shown(
      [
        "# Weekly review",
        "A *calm* half hour, **together**, with `notes` and ~~no~~ rush.",
        "It goes on here.",
        "",
        "## Movement",
        "- bike",
        "- walk",
        "  1. river path",
        "  2. park loop",
        "- [x] water the plants",
        "- [ ] book the boiler service",
        "",
        "3. third",
        "4. fourth",
        "",
        "> Quoted *gently*",
        "",
        "```",
        "line one",
        "  <b>kept as typed</b>",
        "```",
        "",
        "---",
        "Snake_case_words stay as written.",
      ].join("\n"),
    );
    expect(view.querySelector("h3")).toHaveTextContent("Weekly review");
    expect(view.querySelector("h4")).toHaveTextContent("Movement");
    const first = view.querySelector("p")!;
    expect(first.querySelector("em")).toHaveTextContent("calm");
    expect(first.querySelector("strong")).toHaveTextContent("together");
    expect(first.querySelector("code")).toHaveTextContent("notes");
    expect(first.querySelector("del")).toHaveTextContent("no");
    // A line break inside a paragraph is a space, as CommonMark reads it.
    expect(first.textContent).toContain("rush.\nIt goes on here.");
    const list = view.querySelector("ul")!;
    expect(list.querySelectorAll(":scope > li")).toHaveLength(4);
    expect(list.querySelector("ol")!.querySelectorAll("li")).toHaveLength(2);
    const boxes = within(list as HTMLElement).getAllByRole("checkbox");
    expect(boxes.map((b) => (b as HTMLInputElement).checked)).toEqual([
      true,
      false,
    ]);
    expect(boxes.every((b) => (b as HTMLInputElement).disabled)).toBe(true);
    expect(list).toHaveTextContent("book the boiler service");
    expect(view.querySelector("ol[start='3']")).toHaveTextContent("third");
    expect(view.querySelector("blockquote em")).toHaveTextContent("gently");
    expect(view.querySelector("pre code")!.textContent).toBe(
      "line one\n  <b>kept as typed</b>",
    );
    expect(view.querySelector("hr")).not.toBeNull();
    expect([...view.querySelectorAll(":scope > p")].at(-1)!.textContent).toBe(
      "Snake_case_words stay as written.",
    );
  });

  it("keeps escaped marks as text and breaks lines where the note asks", () => {
    const view = shown(
      String.raw`\*not em\* and \[[not a link]]` + "\nfirst  \nsecond\\\nthird",
    );
    expect(view.querySelector("em, button, span")).toBeNull();
    expect(view).toHaveTextContent("*not em* and [[not a link]]");
    expect(view.querySelectorAll("br")).toHaveLength(2);
  });

  it("shows raw HTML as text and never renders it", () => {
    const view = shown(
      [
        "<script>alert(1)</script>",
        "",
        '<img src="x" onerror="alert(2)"> and <b>bold</b>',
        "",
        '<div onclick="alert(3)">a block</div>',
      ].join("\n"),
    );
    expect(view.querySelector("script, img, b, div div")).toBeNull();
    expect(view).toHaveTextContent("<script>alert(1)</script>");
    expect(view).toHaveTextContent(
      '<img src="x" onerror="alert(2)"> and <b>bold</b>',
    );
    expect(view).toHaveTextContent('<div onclick="alert(3)">a block</div>');
  });

  it("links only http, https and mailto addresses, and never loads an image", () => {
    const view = shown(
      [
        "[site](https://example.invalid/page) and <https://example.invalid/auto>",
        "and http://example.invalid/bare. Mail [us](mailto:alex@example.invalid).",
        "[bad](javascript:alert(1)) [data](data:text/html;base64,PGI+) [file](file:///etc/hosts)",
        "![a fictional chart](https://example.invalid/chart.png)",
      ].join("\n"),
    );
    const links = [...view.querySelectorAll("a")];
    expect(links.map((a) => a.getAttribute("href"))).toEqual([
      "https://example.invalid/page",
      "https://example.invalid/auto",
      "http://example.invalid/bare",
      "mailto:alex@example.invalid",
    ]);
    for (const a of links) {
      expect(a.target).toBe("_blank");
      expect(a.rel).toBe("noopener noreferrer");
    }
    expect(view).toHaveTextContent("bad data file");
    expect(view.querySelector("img")).toBeNull();
    expect(view).toHaveTextContent("a fictional chart");
  });

  it("opens the star a wikilink names, and marks one that is not on the map", async () => {
    const open = vi.fn();
    const link = vi.fn((target: string) =>
      target === "Bike commute" ? "notes:notes:Bike%20commute.md" : null,
    );
    const view = shown(
      "Ride the [[Bike commute|commute]], see [[Bike commute#Route]] and [[Nowhere]].",
      { link, open },
    );
    const [alias, heading] = within(view as HTMLElement).getAllByRole("button");
    expect(alias).toHaveTextContent("commute");
    expect(heading).toHaveTextContent("Bike commute › Route");
    expect(link).toHaveBeenCalledWith(
      "Bike commute",
      "[[Bike commute|commute]]",
      true,
    );
    await userEvent.setup().click(alias);
    expect(open).toHaveBeenCalledWith("notes:notes:Bike%20commute.md");
    expect(screen.getByTitle("Not on the map")).toHaveTextContent("Nowhere");
  });
});

describe("where a note's links lead", () => {
  const welcome = note("Welcome.md");
  const boiler = note("Home/Boiler service.md");
  const paint = note("Home/Paint colours.md");
  const otherPaint = note("Archive/Old/Paint colours.md");
  const book = note("Reading/Next book.md");
  const water = note("Water intake.md");
  const nodes: GraphNode[] = [welcome, boiler, paint, otherPaint, book, water];
  const hydration = {
    ...relation(welcome, water),
    type: "links_to",
    evidence: [
      { ...relation(welcome, water).evidence[0], excerpt: "[[hydration]]" },
    ],
  };
  const resolve = noteLinks(welcome, nodes, [hydration]);

  it("follows the engine's own link first, so an alias finds its note", () => {
    expect(resolve("hydration", "[[hydration]]", true)).toBe(water.id);
  });
  it("finds a note by path or name the way Obsidian does", () => {
    expect(
      resolve("Home/Boiler service", "[[Home/Boiler service]]", true),
    ).toBe(boiler.id);
    expect(resolve("boiler service", "[[boiler service]]", true)).toBe(
      boiler.id,
    );
    // The shortest path wins between two notes of the same name.
    expect(resolve("Paint colours", "[[Paint colours]]", true)).toBe(paint.id);
    expect(resolve("Next book.md", "[[Next book.md]]", true)).toBe(book.id);
    // A relative Markdown link, already decoded.
    expect(
      resolve("Reading/Next book.md", "[next](Reading/Next%20book.md)", false),
    ).toBe(book.id);
    expect(resolve("../outside.md", "[out](../outside.md)", false)).toBeNull();
    expect(resolve("Nowhere", "[[Nowhere]]", true)).toBeNull();
  });
  it("prefers the note in the linking note's own folder", () => {
    const fromArchive = noteLinks(note("Archive/Old/Index.md"), nodes, []);
    expect(fromArchive("Paint colours", "[[Paint colours]]", true)).toBe(
      otherPaint.id,
    );
  });
});
