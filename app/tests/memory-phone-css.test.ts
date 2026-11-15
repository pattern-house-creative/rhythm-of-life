// SB-P6-02: what jsdom can't lay out, read from the styles themselves: a finger's room and no zoom on any phone, upright or sideways, motion
// that stops for a person who asks for less, a focus ring that shows whole, and heights that hold on a phone whose browser bars come and
// go. Every rule stays on the memory screens' own names, so with memory.tools off nothing here reaches a screen. The same is measured in a
// real browser by tests/memory-browser.tsx.
import { readFileSync } from "node:fs";
import { beforeAll, describe, expect, it } from "vitest";

const files = [
  "src/MemoryView.css",
  "src/ResurfaceCard.css",
  "src/PageCapture.css",
  "src/PageBookmark.css",
];
const touch = "(max-width: 760px), (max-height: 500px)";
const less = "(prefers-reduced-motion: reduce)";

const sheets: Record<string, CSSStyleSheet> = {};
beforeAll(() => {
  for (const file of files) {
    const style = document.createElement("style");
    style.textContent = readFileSync(file, "utf8");
    document.head.append(style);
    sheets[file] = style.sheet!;
  }
});

const styleRules = (rules: CSSRuleList) =>
  [...rules].filter((r): r is CSSStyleRule => r instanceof CSSStyleRule);
const blocks = (sheet: CSSStyleSheet, media: string) =>
  [...sheet.cssRules]
    .filter(
      (r): r is CSSMediaRule =>
        r instanceof CSSMediaRule && r.media.mediaText === media,
    )
    .flatMap((r) => styleRules(r.cssRules));
const selectors = (rule: CSSStyleRule) =>
  rule.selectorText
    .replace(/\s+/g, " ")
    .replace(/\( /g, "(")
    .replace(/,? \)/g, ")")
    .split(/,(?![^(]*\))/)
    .map((s) => s.trim());
// What a media block says of one selector.
const said = (sheet: CSSStyleSheet, media: string, selector: string) =>
  blocks(sheet, media)
    .filter((r) => selectors(r).includes(selector))
    .map((r) => r.style);
const top = (sheet: CSSStyleSheet, selector: string) =>
  styleRules(sheet.cssRules)
    .filter((r) => selectors(r).includes(selector))
    .map((r) => r.style);

describe("a finger's room and no zoom, upright or sideways", () => {
  it.each([
    // The view's strip and its own filters.
    ["src/MemoryView.css", ".memory-tabs button"],
    ["src/MemoryView.css", ".memory-view .reader-bar .back-link"],
    // Each row's and each question's buttons, and the ways on.
    ["src/MemoryView.css", ".memory-actions button"],
    ["src/MemoryView.css", ".memory-confirm button"],
    ["src/MemoryView.css", ".memory-held button"],
    ["src/MemoryView.css", ".memory-truth button"],
    ["src/MemoryView.css", ".memory-view .memory-more"],
    ["src/MemoryView.css", ".memory-row button:not(.primary-action)"],
    ["src/MemoryView.css", ".memory-version .memory-meta button"],
    ["src/MemoryView.css", ".memory-change-items button"],
    // Picks and boxes.
    ["src/MemoryView.css", ".memory-choose"],
    ["src/MemoryView.css", ".memory-filter-clear"],
    ["src/MemoryView.css", ".memory-status-pick"],
    ["src/MemoryView.css", ".memory-status-pick select"],
    ["src/MemoryView.css", ".memory-note input"],
    ["src/MemoryView.css", ".memory-edit select"],
    // A notice's way on and its Dismiss.
    ["src/MemoryView.css", ".memory-view .upload-status button"],
    // Home's card.
    ["src/ResurfaceCard.css", ".resurface-says"],
    // The bookmark's offer.
    ["src/PageBookmark.css", ".page-bookmark summary"],
    ["src/PageBookmark.css", ".page-bookmark-link"],
    ["src/PageBookmark.css", ".page-bookmark-row button"],
  ])("%s %s is tall enough to tap", (file, selector) => {
    const [style] = said(sheets[file], touch, selector);
    expect(parseInt(style?.minHeight ?? "0")).toBeGreaterThanOrEqual(40);
  });

  it("a card's dismiss and a notice's are wide enough too, and a box to choose with is no smaller than the rules ask", () => {
    const [dismiss] = said(
      sheets["src/ResurfaceCard.css"],
      touch,
      ".resurface-dismiss",
    );
    expect(dismiss.width).toBe("40px");
    expect(dismiss.height).toBe("40px");
    const view = sheets["src/MemoryView.css"];
    for (const selector of [
      ".memory-version .memory-meta button",
      '.memory-view .upload-status button[aria-label="Dismiss"]',
    ])
      expect(said(view, touch, selector).map((s) => s.minWidth)).toContain(
        "40px",
      );
    for (const selector of [".memory-pick", ".memory-choose input"]) {
      const boxes = said(view, touch, selector);
      expect(boxes.map((s) => s.width)).toContain("24px");
      expect(boxes.map((s) => s.height)).toContain("24px");
    }
  });

  it.each([
    ["src/MemoryView.css", ".memory-capture textarea"],
    ["src/MemoryView.css", ".memory-edit textarea"],
    ["src/MemoryView.css", ".memory-edit select"],
    ["src/MemoryView.css", ".memory-input"],
    ["src/MemoryView.css", ".memory-status-pick select"],
    ["src/MemoryView.css", ".memory-note input"],
    ["src/PageCapture.css", ".page-capture-form textarea"],
    ["src/PageBookmark.css", ".page-bookmark-code"],
  ])("%s %s takes focus without a phone zooming the page", (file, selector) => {
    const styles = said(sheets[file], touch, selector);
    expect(styles.map((s) => s.fontSize).filter(Boolean)).toEqual(["16px"]);
  });
});

describe("motion", () => {
  it.each(files)(
    "%s stops all of its motion for a person who asks for less",
    (file) => {
      const sheet = sheets[file];
      const moving = [...sheet.cssRules].flatMap((rule) => {
        const inside =
          rule instanceof CSSMediaRule
            ? rule.media.mediaText === less
              ? []
              : styleRules(rule.cssRules)
            : rule instanceof CSSStyleRule
              ? [rule]
              : [];
        return inside.filter(
          (r) =>
            r.style.transition || r.style.animation || r.style.animationName,
        );
      });
      for (const rule of moving)
        for (const selector of selectors(rule)) {
          const stopped = said(sheet, less, selector);
          expect(
            stopped.some(
              (s) => s.transition === "none" || s.animation === "none",
            ),
            `${file}: ${selector}`,
          ).toBe(true);
        }
    },
  );

  it("the Memories view's own way in stops, and no script scrolls smoothly", () => {
    expect(
      said(sheets["src/MemoryView.css"], less, ".memory-view")[0].animation,
    ).toBe("none");
    for (const file of [
      "src/MemoryView.tsx",
      "src/memoryFocus.ts",
      "src/ResurfaceCard.tsx",
      "src/PageCapture.tsx",
    ])
      expect(readFileSync(file, "utf8")).not.toContain("smooth");
  });
});

describe("a focus ring that shows whole", () => {
  it("stands inside what scrolls", () => {
    const view = sheets["src/MemoryView.css"];
    for (const selector of [
      ".memory-tabs button:focus-visible",
      ".memory-list > li > button:focus-visible",
      ".memory-project-head:focus-visible",
      ".memory-change-items button:focus-visible",
      ".memory-view .memory-notice button:focus-visible",
      ".memory-view textarea:focus-visible",
      ".memory-view .memory-input:focus-visible",
      ".memory-note input:focus-visible",
      ".memory-edit select:focus-visible",
    ])
      expect(top(view, selector)[0].outlineOffset, selector).toBe("-2px");
    const near = (file: string, selector: string) =>
      top(sheets[file], selector)[0].outlineOffset;
    expect(
      near(
        "src/MemoryView.css",
        ".memory-view :is(button, a, input, select, textarea, summary, [tabindex]):focus-visible",
      ),
    ).toBe("1px");
    expect(
      near(
        "src/ResurfaceCard.css",
        ".resurface-card :is(button, [tabindex]):focus-visible",
      ),
    ).toBe("1px");
    expect(
      near("src/PageBookmark.css", ".page-bookmark summary:focus-visible"),
    ).toBe("1px");
  });
});

describe("heights and widths on a phone", () => {
  // jsdom's parser keeps the last of two values, as a browser that takes both does; the older unit stands for a browser that doesn't.
  const sourceOf = (file: string) =>
    readFileSync(file, "utf8").replace(/\r/g, "");
  it.each([["src/ResurfaceCard.css", "22"]])(
    "%s: its open part goes by the small viewport, whatever a phone's bars do",
    (file, share) => {
      const source = sourceOf(file);
      expect(source).toContain(`max-height: min(${share}vh,`);
      expect(source).toContain(`max-height: min(${share}svh,`);
      expect(source.indexOf(`${share}vh`)).toBeLessThan(
        source.indexOf(`${share}svh`),
      );
    },
  );

  it("a notice is in the view's own flow, so it covers nothing, and a long one scrolls", () => {
    const view = sheets["src/MemoryView.css"];
    const [notice] = top(view, ".memory-view .memory-notice");
    expect(notice.position).toBe("static");
    expect(notice.transform).toBe("none");
    expect(notice.overflowY).toBe("auto");
    expect(sourceOf("src/MemoryView.css")).toContain("max-height: 40svh");
    // The box it is said in draws nothing of its own.
    expect(top(view, ".memory-notices")[0].display).toBe("contents");
  });

  it("a long unbroken word wraps inside a row rather than running off the screen", () => {
    const view = sheets["src/MemoryView.css"];
    expect(top(view, ".memory-preview")[0].overflowWrap).toBe("anywhere");
    expect(top(view, ".memory-meta")[0].overflowWrap).toBe("anywhere");
  });

  it("two memories side by side stack on a phone", () => {
    const [pair] = said(
      sheets["src/MemoryView.css"],
      "(max-width: 760px)",
      ".memory-pair",
    );
    expect(pair.gridTemplateColumns).toBe("minmax(0, 1fr)");
  });
});

describe("nothing here reaches a screen with memory.tools off", () => {
  const ours: Record<string, RegExp> = {
    "src/MemoryView.css": /\.memory-|\.dock-memories/,
    "src/ResurfaceCard.css": /\.resurface-/,
    "src/PageCapture.css": /\.page-capture/,
    "src/PageBookmark.css": /\.page-bookmark/,
  };
  it.each(files)("every selector in %s names one of its own", (file) => {
    const all = (rules: CSSRuleList): CSSStyleRule[] =>
      [...rules].flatMap((r) =>
        r instanceof CSSMediaRule
          ? all(r.cssRules)
          : r instanceof CSSStyleRule
            ? [r]
            : [],
      );
    for (const rule of all(sheets[file].cssRules))
      for (const selector of selectors(rule))
        expect(selector, file).toMatch(ours[file]);
  });
});
