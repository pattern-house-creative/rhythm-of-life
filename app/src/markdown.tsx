import { Fragment, useMemo } from "react";
import type { ReactNode } from "react";

// A small Markdown reader for notes: headings, paragraphs, lists (with tasks), emphasis, inline
// code and code blocks, block quotes, rules, links and Obsidian's [[wikilinks]]. It builds React
// elements and never HTML, so raw HTML in a note shows as its text; links open only http, https
// and mailto addresses, and an image shows its description instead of loading.

type Block =
  | { kind: "heading"; level: number; text: string }
  | { kind: "paragraph"; text: string }
  | { kind: "code"; text: string }
  | { kind: "quote"; blocks: Block[] }
  | { kind: "list"; ordered: boolean; start: number; items: Block[][] }
  | { kind: "rule" };

// The star a note's link leads to (a [[wikilink]] or a relative link to another note), or null
// when it is not on the map. `raw` is the link as written.
export type NoteLink = (
  target: string,
  raw: string,
  wiki: boolean,
) => string | null;

const blank = /^\s*$/,
  fence = /^ {0,3}(`{3,}|~{3,})/,
  heading = /^ {0,3}(#{1,6})(?:[ \t]+|$)(.*)$/,
  rule = /^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$/,
  quote = /^ {0,3}> ?/,
  item = /^( {0,3})([-*+]|\d{1,9}[.)])(?:[ \t]+|$)/,
  underline = /^ {0,3}(=+|-+)[ \t]*$/;
const starts = (line: string) =>
  [fence, heading, rule, quote, item].some((r) => r.test(line));
const indent = (line: string) => /^ */.exec(line)![0].length;

function parseMarkdown(text: string): Block[] {
  return blocks(
    text
      .replace(/\r\n?/g, "\n")
      .split("\n")
      .map((l) => l.replace(/^\t+/, (t) => "    ".repeat(t.length))),
  );
}

function blocks(lines: string[]): Block[] {
  const out: Block[] = [];
  let i = 0;
  while (i < lines.length) {
    const line = lines[i];
    const f = fence.exec(line),
      h = heading.exec(line);
    if (blank.test(line)) i++;
    else if (f) {
      const close = new RegExp(`^ {0,3}\\${f[1][0]}{${f[1].length},}[ \\t]*$`);
      const code: string[] = [];
      for (i++; i < lines.length && !close.test(lines[i]); i++)
        code.push(lines[i]);
      out.push({ kind: "code", text: code.join("\n") });
      i++;
    } else if (h) {
      const words = h[2].replace(/(?:^|[ \t]+)#+[ \t]*$/, "").trim();
      out.push({ kind: "heading", level: h[1].length, text: words });
      i++;
    } else if (rule.test(line)) {
      out.push({ kind: "rule" });
      i++;
    } else if (quote.test(line)) {
      const inner: string[] = [];
      while (i < lines.length && quote.test(lines[i]))
        inner.push(lines[i++].replace(quote, ""));
      out.push({ kind: "quote", blocks: blocks(inner) });
    } else if (item.test(line)) i = list(lines, i, out);
    else {
      // A paragraph runs to a blank line or another block; a line of = or - under it makes
      // it a heading.
      const text = [line.trimStart()];
      let level = 0;
      for (i++; i < lines.length; i++) {
        const u = underline.exec(lines[i]);
        if (u) {
          level = u[1][0] === "=" ? 1 : 2;
          i++;
          break;
        }
        if (blank.test(lines[i]) || starts(lines[i])) break;
        text.push(lines[i].trimStart());
      }
      const joined = text.join("\n").trimEnd();
      out.push(
        level
          ? { kind: "heading", level, text: joined }
          : { kind: "paragraph", text: joined },
      );
    }
  }
  return out;
}

// One list: items of the same kind of marker. An item holds every line indented past its
// marker, and the lines of its paragraph that follow on without indent.
function list(lines: string[], i: number, out: Block[]) {
  const first = item.exec(lines[i])!;
  const marker = (m: RegExpExecArray) =>
    /\d/.test(m[2]) ? m[2].slice(-1) : m[2];
  const kind = marker(first);
  const items: Block[][] = [];
  for (;;) {
    const m = item.exec(lines[i]);
    if (!m || marker(m) !== kind) break;
    // Text starts after the marker and its spaces; five or more spaces keep all but one.
    const gap = m[0].length - m[1].length - m[2].length;
    const content = gap > 4 ? m[1].length + m[2].length + 1 : m[0].length;
    const body = [lines[i].slice(content)];
    for (i++; i < lines.length; i++) {
      const line = lines[i];
      if (blank.test(line)) {
        let j = i;
        while (j < lines.length && blank.test(lines[j])) j++;
        if (j < lines.length && indent(lines[j]) > m[1].length) {
          body.push(...lines.slice(i, j).map(() => ""));
          i = j - 1;
          continue;
        }
        break;
      }
      if (indent(line) > m[1].length)
        body.push(line.slice(Math.min(indent(line), content)));
      else if (!starts(line) && !blank.test(body.at(-1)!)) body.push(line);
      else break;
    }
    items.push(blocks(body));
    // Blank lines between two items of the same list.
    let j = i;
    while (j < lines.length && blank.test(lines[j])) j++;
    const next = j < lines.length ? item.exec(lines[j]) : null;
    if (!next || marker(next) !== kind) break;
    i = j;
  }
  out.push({
    kind: "list",
    ordered: /\d/.test(first[2]),
    start: Number.parseInt(first[2], 10) || 0,
    items,
  });
  return i;
}

// Only addresses that leave the app for a web page or a mail client; anything else is text.
function safeHref(url: string) {
  try {
    const parsed = new URL(url);
    return ["http:", "https:", "mailto:"].includes(parsed.protocol)
      ? parsed.href
      : null;
  } catch {
    return null;
  }
}

type Context = { link?: NoteLink; open?: (id: string) => void; key: number };
const external = (href: string, text: ReactNode) => (
  <a href={href} target="_blank" rel="noopener noreferrer">
    {text}
  </a>
);
// An unclosed * or backtick looks no further than this for its end.
const span = String.raw`[\s\S]{0,2000}?`;

// Each inline form, tried where its first character is; the first that matches wins.
const patterns: [RegExp, (m: RegExpExecArray, c: Context) => ReactNode][] = [
  [
    new RegExp("(`+)(" + span + "[^`])\\1(?!`)", "y"),
    (m) => {
      const text = m[2].replace(/\n/g, " ");
      return (
        <code>
          {/^ .* $/.test(text) && text.trim() ? text.slice(1, -1) : text}
        </code>
      );
    },
  ],
  [
    /!?\[\[([^[\]\n]+)\]\]/y,
    (m, c) => {
      const [target, alias] = m[1].split("|");
      const note = target.split(/[#^]/)[0].trim();
      const shown =
        alias?.trim() || target.replace(/#\^?/, " › ").replace(/^ › /, "");
      return note
        ? noteLink(c, c.link?.(note, m[0], true) ?? null, shown)
        : shown;
    },
  ],
  [/!\[([^\]\n]*)\]\([^)\n]*\)/y, (m) => m[1]],
  [
    /\[((?:[^[\]]|\[[^[\]]*\])*)\]\(\s*(<[^>\n]*>|(?:[^\s()]|\([^\s()]*\))*)(?:\s+(?:"[^"\n]*"|'[^'\n]*'))?\s*\)/y,
    (m, c) => {
      const dest = m[2].replace(/^<(.*)>$/, "$1");
      const text = inline(m[1], c);
      const href = safeHref(dest);
      if (href) return external(href, text);
      if (/^[a-z][a-z0-9+.-]*:/i.test(dest) || !dest) return text;
      let path = dest;
      try {
        path = decodeURIComponent(dest);
      } catch {
        // A malformed escape stays as written.
      }
      return noteLink(c, c.link?.(path, m[0], false) ?? null, text);
    },
  ],
  [
    /<((?:https?|mailto):[^\s<>]+|[^\s@<>:]+@[^\s@<>]+\.[^\s@<>]+)>/iy,
    (m) => {
      const href = safeHref(m[1].includes(":") ? m[1] : "mailto:" + m[1]);
      return href ? external(href, m[1]) : m[0];
    },
  ],
  [
    /https?:\/\/[^\s<]*[^\s<.,:;"')\]!?*_~]/y,
    (m) => {
      const href = safeHref(m[0]);
      return href ? external(href, m[0]) : m[0];
    },
  ],
  [
    new RegExp(
      String.raw`(\*\*\*|___)(?!\s)(${span}[^\s\\])\1(?![A-Za-z0-9])`,
      "y",
    ),
    (m, c) => (
      <strong>
        <em>{inline(m[2], c)}</em>
      </strong>
    ),
  ],
  [
    new RegExp(
      String.raw`(\*\*|__)(?!\s)(${span}[^\s\\])\1(?![A-Za-z0-9])`,
      "y",
    ),
    (m, c) => <strong>{inline(m[2], c)}</strong>,
  ],
  [
    new RegExp(
      String.raw`([*_])(?!\s)(${span}[^\s\\*_])\1(?![*_A-Za-z0-9])`,
      "y",
    ),
    (m, c) => <em>{inline(m[2], c)}</em>,
  ],
  [
    new RegExp(String.raw`~~(?!\s)(${span}[^\s\\])~~`, "y"),
    (m, c) => <del>{inline(m[1], c)}</del>,
  ],
];
const escape = /\\([!-/:-@[-`{-~])/y,
  hardBreak = /(?: {2,}|\\)\n/y;
const at = (pattern: RegExp, text: string, i: number) => {
  pattern.lastIndex = i;
  return pattern.exec(text);
};

function noteLink(c: Context, id: string | null, shown: ReactNode) {
  return id && c.open ? (
    <button type="button" className="note-link" onClick={() => c.open?.(id)}>
      {shown}
    </button>
  ) : (
    <span className="note-link missing" title="Not on the map">
      {shown}
    </span>
  );
}

function inline(text: string, c: Context): ReactNode[] {
  const out: ReactNode[] = [];
  let plain = "";
  const push = (node: ReactNode) => {
    if (plain) out.push(plain);
    plain = "";
    out.push(<Fragment key={c.key++}>{node}</Fragment>);
  };
  for (let i = 0; i < text.length;) {
    const ch = text[i];
    const hard = ch === " " || ch === "\\" ? at(hardBreak, text, i) : null;
    if (hard) {
      push(<br />);
      i += hard[0].length;
      continue;
    }
    const escaped = ch === "\\" ? at(escape, text, i) : null;
    if (escaped) {
      plain += escaped[1];
      i += 2;
      continue;
    }
    // Emphasis with _ and bare web addresses never start inside a word (snake_case stays).
    const inWord = /[A-Za-z0-9]/.test(text[i - 1] ?? "");
    let matched = false;
    if (starters.has(ch) && !(inWord && (ch === "_" || ch === "h")))
      for (const [pattern, make] of patterns) {
        const m = at(pattern, text, i);
        if (!m) continue;
        push(make(m, c));
        i += m[0].length;
        matched = true;
        break;
      }
    if (!matched) plain += text[i++];
  }
  if (plain) out.push(plain);
  return out;
}
const starters = new Set(["`", "[", "!", "<", "h", "*", "_", "~"]);

// A task item ("- [ ] call the plumber") shows its box, read only.
function task(text: string): [boolean | null, string] {
  const m = /^\[([ xX])\][ \t]+/.exec(text);
  return m ? [m[1] !== " ", text.slice(m[0].length)] : [null, text];
}

function render(list: Block[], c: Context, tight = false): ReactNode[] {
  return list.map((b, i) => {
    switch (b.kind) {
      case "heading": {
        const H = `h${Math.min(6, b.level + 2)}` as "h3" | "h4" | "h5" | "h6";
        return <H key={i}>{inline(b.text, c)}</H>;
      }
      case "paragraph":
        return tight ? (
          <Fragment key={i}>{inline(b.text, c)}</Fragment>
        ) : (
          <p key={i}>{inline(b.text, c)}</p>
        );
      case "code":
        return (
          <pre key={i}>
            <code>{b.text}</code>
          </pre>
        );
      case "quote":
        return <blockquote key={i}>{render(b.blocks, c)}</blockquote>;
      case "rule":
        return <hr key={i} />;
      case "list": {
        const items = b.items.map((parts, j) => {
          const first = parts[0]?.kind === "paragraph" ? parts[0] : null;
          const [done, text] = first ? task(first.text) : [null, ""];
          const rest =
            first && done !== null
              ? [{ ...first, text }, ...parts.slice(1)]
              : parts;
          const loose = parts.filter((x) => x.kind === "paragraph").length > 1;
          return (
            <li key={j} className={done === null ? undefined : "task"}>
              {done !== null && (
                <input
                  type="checkbox"
                  checked={done}
                  disabled
                  aria-label={done ? "Done" : "Not done"}
                />
              )}
              {render(rest, c, !loose)}
            </li>
          );
        });
        return b.ordered ? (
          <ol key={i} start={b.start === 1 ? undefined : b.start}>
            {items}
          </ol>
        ) : (
          <ul key={i}>{items}</ul>
        );
      }
    }
  });
}

export function Markdown({
  text,
  link,
  open,
  className,
}: {
  text: string;
  link?: NoteLink;
  open?: (id: string) => void;
  className?: string;
}) {
  const parsed = useMemo(() => parseMarkdown(text), [text]);
  return (
    <div className={`markdown ${className ?? ""}`.trim()}>
      {render(parsed, { link, open, key: 0 })}
    </div>
  );
}
