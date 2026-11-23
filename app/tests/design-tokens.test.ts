// The palette stays readable (docs/dev/design-system.md): the app's tokens are read from index.css itself, and every
// pair a person reads is checked for WCAG contrast. Text and accents need 4.5:1 on every surface the app draws them on;
// the focus ring and a boundary that alone marks a control need 3:1. A token changed to a value that fails, or a
// surface added without its text checked, fails here before anyone has to squint at it.
import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const css = readFileSync("src/index.css", "utf8");
// The first :root block holds the tokens.
const root = /:root\s*\{([\s\S]*?)\n\}/.exec(css)?.[1] ?? "";
const tokens = Object.fromEntries(
  [...root.matchAll(/(--ui-[a-z0-9-]+)\s*:\s*([^;]+);/g)].map((m) => [
    m[1],
    m[2].trim(),
  ]),
);

type Rgb = [number, number, number];
const hex = (value: string): Rgb => {
  const h = /^#([0-9a-f]{6})$/i.exec(value)?.[1];
  if (!h) throw new Error(`not a six-digit colour: ${value}`);
  return [0, 2, 4].map((i) => parseInt(h.slice(i, i + 2), 16)) as Rgb;
};
const colour = (name: string): Rgb => {
  const value = tokens[name];
  if (!value) throw new Error(`${name} is not defined in index.css`);
  return hex(value);
};
const channel = (c: number) => {
  const s = c / 255;
  return s <= 0.04045 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
};
const luminance = ([r, g, b]: Rgb) =>
  0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
const contrast = (a: Rgb, b: Rgb) => {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
};

// Every surface text is drawn on: the four flat ones and both ends of the card's gradient.
const card = (tokens["--ui-card"]?.match(/#[0-9a-f]{6}/gi) ?? []).map(hex);
const surfaces: [string, Rgb][] = [
  ...["--ui-sky", "--ui-night", "--ui-surface", "--ui-raised"].map(
    (name): [string, Rgb] => [name, colour(name)],
  ),
  ...card.map((rgb, i): [string, Rgb] => [`--ui-card stop ${i + 1}`, rgb]),
];
const text = [
  "--ui-ink",
  "--ui-soft",
  "--ui-faint",
  "--ui-mint",
  "--ui-focus",
  "--ui-lilac",
  "--ui-amber",
  "--ui-alert",
];
const pairs = (fore: string[], back: [string, Rgb][]) =>
  fore.flatMap((f) => back.map(([b, rgb]) => [f, b, rgb] as const));

describe("the palette", () => {
  it("reads every token it checks", () => {
    expect(card.length).toBeGreaterThanOrEqual(2);
    for (const name of text) expect(() => colour(name)).not.toThrow();
  });

  it.each(pairs(text, surfaces))(
    "%s on %s is at least 4.5:1",
    (fore, _back, rgb) => {
      expect(contrast(colour(fore), rgb)).toBeGreaterThanOrEqual(4.5);
    },
  );

  // A chosen control and a discovery keep their words readable on their own fills.
  it.each([
    ...pairs(
      ["--ui-ink", "--ui-soft", "--ui-faint", "--ui-mint"],
      [["--ui-mint-fill", colour("--ui-mint-fill")]],
    ),
    ...pairs(
      ["--ui-ink", "--ui-soft", "--ui-faint", "--ui-lilac"],
      [["--ui-lilac-fill", colour("--ui-lilac-fill")]],
    ),
  ])("%s on %s is at least 4.5:1", (fore, _back, rgb) => {
    expect(contrast(colour(fore), rgb)).toBeGreaterThanOrEqual(4.5);
  });

  it.each(
    pairs(["--ui-focus", "--ui-line-strong", "--ui-edge-strong"], surfaces),
  )("%s stands out from %s by at least 3:1", (fore, _back, rgb) => {
    expect(contrast(colour(fore), rgb)).toBeGreaterThanOrEqual(3);
  });

  it("the focus ring is the focus colour, at least 2 px wide", () => {
    expect(tokens["--ui-focus-ring"]).toMatch(
      /^([2-9]|\d{2,})px solid var\(--ui-focus\)$/,
    );
  });

  it("no reading step is smaller than 12 px, and a touch target is at least 44 px", () => {
    // The eyebrow is the one step under 12 px: a short uppercase label, never running text.
    const steps = Object.entries(tokens).filter(
      ([n]) => n.startsWith("--ui-text-") && n !== "--ui-text-eyebrow",
    );
    expect(steps.length).toBeGreaterThan(0);
    for (const [name, value] of steps)
      expect(parseFloat(value), name).toBeGreaterThanOrEqual(12);
    expect(parseFloat(tokens["--ui-text-eyebrow"])).toBeGreaterThanOrEqual(11);
    expect(parseFloat(tokens["--ui-target"])).toBeGreaterThanOrEqual(44);
  });
});
