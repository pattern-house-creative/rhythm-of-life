// The content gate: whether one finished message of a coding agent is worth keeping as a memory (SB-P7-02). It decides on
// the words alone, here on this machine, with no model. What it keeps is a draft a person reviews, so it may be wrong
// either way; it never keeps a whole session, since it is only ever given one message and cuts that to maxChars.

import { redact } from "./redact.mjs";

export const DEFAULTS = Object.freeze({ minChars: 200, minSignals: 2, maxChars: 2000 });
export const BOUNDS = Object.freeze({ minChars: [1, 8000], minSignals: [0, 5], maxChars: [200, 8000] });

// The most of a message any pattern here reads, so that a huge message costs what a large one does.
const INPUT_CAP = 64 * 1024;
const CODE_LEFT_OUT = "[code left out]";

// What a client or a tool wraps around a conversation rather than what the agent concluded, when a message quotes it.
const INJECTED = /<(system-reminder|command-name|command-message|command-args|local-command-stdout|local-command-stderr|local-command-caveat|user-prompt-submit-hook|ide_selection|ide_opened_file|ide_diagnostics|bash-input|bash-stdout|bash-stderr|environment_context|user_instructions|turn_aborted|task-notification)\b[^>]*>[\s\S]*?(?:<\/\1\s*>|$)/gi;
// A message echoing an instructions file holds the person's standing rules, not a result. It is left out whole.
const INSTRUCTION_FILE = /\bContents of \S+\.md\b|(?:^|\n)[ \t]*#+[ \t]*[\w.-]*\.md\b[^\n]*\binstructions\b|\b(?:AGENTS|CLAUDE|GEMINI|COPILOT)\.md instructions\b/i;
const FENCE = /(^|\n)[ \t]*(```|~~~)[^\n]*\n[\s\S]*?(?:(?<=\n)[ \t]*\2[ \t]*(?=\n|$)|$)/g;

// What a result worth remembering tends to say, by family. Each family counts once, so length alone earns nothing.
const SIGNALS = Object.freeze({
  decision: /\b(?:decided|decision|we chose|i chose|chose to|opted|went with|settled on|instead of|rather than|trade-?offs?)\b/i,
  cause: /\b(?:root cause|because|turned out|caused by|the reason|the problem was|the issue was|was due to)\b/i,
  outcome: /\b(?:fixed|resolved|now works|works now|now pass(?:es)?|all tests pass|tests? passed|merged|shipped|released|deployed|landed)\b/i,
  lesson: /\b(?:gotcha|lesson|learned|learnt|worth remembering|keep in mind|note that|important|must not|never|always|do not|don't|pitfall|caveat|workaround|limitation)\b/i,
  next: /\b(?:next steps?|todo|to do|follow[- ]?up|still open|left to do|not done|remaining|open question|blocked|waiting on)\b/i,
});

export const REASONS = Object.freeze({
  no_text: "no text in this event",
  instructions: "it echoes an instructions file",
  mostly_secrets: "too much of it was secrets",
  too_short: "too short to be a result",
  mostly_code: "mostly code or output rather than words",
  question: "it ends by asking a question, so nothing is settled yet",
  no_signal: "it says nothing that reads as a decision, cause, outcome, lesson or next step",
});

export function signals(text) {
  return Object.entries(SIGNALS).filter(([, pattern]) => pattern.test(text)).map(([family]) => family);
}

// A message over the input's size ends at the last whitespace within it, so that no secret reaches redaction in half. A loop,
// since a regex for this is quadratic; a text with no whitespace at all is cut where it is, and long-run redaction takes that.
function capped(message) {
  if (message.length <= INPUT_CAP) return message;
  let end = INPUT_CAP;
  while (end > 0 && !/\s/.test(message[end])) end--;
  return message.slice(0, end || INPUT_CAP);
}

function cut(text, max) {
  if (text.length <= max) return text;
  const head = text.slice(0, max);
  const paragraph = head.lastIndexOf("\n\n");
  const sentence = Math.max(head.lastIndexOf(". "), head.lastIndexOf(".\n"));
  const end = paragraph >= max * 0.6 ? paragraph : sentence >= max * 0.6 ? sentence + 1 : max;
  return `${head.slice(0, end).trimEnd()}\n\n[cut]`.toWellFormed();
}

// One message as the gate judges it: {keep: false, reason} or {keep: true, score, text, redactions}, the text being exactly
// what may leave: injected context and fenced code out, secrets redacted, cut to maxChars. Redaction comes before the cut,
// so a cut can never leave half a secret behind, and before the checks, so they judge what would be sent.
export function assess(message, options = {}) {
  const { minChars, minSignals, maxChars } = { ...DEFAULTS, ...options };
  if (typeof message !== "string" || !message.trim()) return { keep: false, reason: "no_text" };
  const clean = capped(message).replaceAll("\0", "").toWellFormed();
  if (INSTRUCTION_FILE.test(clean)) return { keep: false, reason: "instructions" };
  const words = clean.replace(INJECTED, "").replace(FENCE, `$1${CODE_LEFT_OUT}\n`).replace(/\n{3,}/g, "\n\n").trim();
  const { text, count } = redact(words, { own: options.own });
  const prose = text.replaceAll(CODE_LEFT_OUT, "").trim();
  if (!prose) return { keep: false, reason: "no_text" };
  if (count > 0 && count / prose.split(/\s+/).length > 1 / 15) return { keep: false, reason: "mostly_secrets" };
  if (prose.length < minChars) return { keep: false, reason: "too_short" };
  if ((prose.match(/[\p{L}\s]/gu)?.length ?? 0) / prose.length < 0.6) return { keep: false, reason: "mostly_code" };
  if (/\?\s*$/.test(prose)) return { keep: false, reason: "question" };
  const score = signals(prose).length;
  if (score < minSignals) return { keep: false, reason: "no_signal" };
  return { keep: true, score, text: cut(text, maxChars), redactions: count };
}
