// Removes secrets from a text before it leaves this machine (SB-P7-02; README.md in this folder says what it can miss).
// It errs towards taking too much in a value that could be a secret, and leaves alone what only names one: a variable, a
// placeholder, a word of prose, a UUID, a commit id, a file path. No dependencies; every pattern stays linear on a text of
// the size the gate hands it (gate.mjs caps its input), so a hostile text cannot stall the agent it runs under.

export const REDACTED = "[redacted]";

// How often a text changes between small letters, capitals and digits. Anything else starts afresh.
function kindChanges(text) {
  const kind = (c) => (c >= "a" && c <= "z" ? 1 : c >= "A" && c <= "Z" ? 2 : c >= "0" && c <= "9" ? 3 : 0);
  let changes = 0, last = 0;
  for (const c of text) {
    const k = kind(c);
    if (k !== 0 && last !== 0 && k !== last) changes++;
    last = k;
  }
  return changes;
}

// The ends of a text less these characters. A loop, since a regex for this is quadratic on a long run of them.
function strip(text, front, back) {
  let start = 0, end = text.length;
  while (start < end && front.includes(text[start])) start++;
  while (end > start && back.includes(text[end - 1])) end--;
  return text.slice(start, end);
}

// A name as people write one, in one case or a little camel case. A random value changes at about every other character.
const readsAsName = (text) => /^\.?[A-Za-z_][\w.:>[\]-]*$/.test(text) && kindChanges(text) <= 2 + text.length / 8;
// The share of a text in runs of three or more small letters: most of a long name, and little of a random token.
const wordShare = (text) => (text.match(/[a-z]{3,}/g) ?? []).join("").length / text.length;
// A call: a function's name can be a long one (File.ReadAllText), so it may also go by its words.
function isCall(value) {
  const call = /^([A-Za-z_][\w.]*)\(/.exec(value);
  return call !== null && (readsAsName(call[1]) || wordShare(call[1]) >= 0.5);
}

// What only names a secret: a lookup, a variable, a placeholder, a type, nothing at all. A real secret can begin like any of
// these ($aB3..., example7Qz...), so only a lookup goes by how it starts: each of the others must be the whole value.
const LOOKUP = /^(?:\[redacted|process\.env|import\.meta|os\.environ|os\.getenv|getenv|env\.|Deno\.env|config\.|settings\.|secrets\.|self\.|this\.|\$\{|\$\()/i;
const STANDS_IN = /^(?:\$|%\w+%|=+|x{3,}(?:[-_.]x{3,})*|\*{3,}|\.{3,}|null|none|nil|undefined|true|false|str|string|bytes|number|boolean|required|optional)$/i;
// Its closing bracket is optional because a value is often cut at the space inside it (<your key>).
const ANGLED = /^<(?:[a-z][a-z _-]*|[A-Z][A-Z _-]*)?>?$/;
const PLACEHOLDER = /^(?:your[-_ ]|example|placeholder|change[-_]?me|dummy|sample)[a-z]*(?:[-_. ][a-z]+)*(?:[-_. ]?\d{1,3})?$/;

function reference(value) {
  if (LOOKUP.test(value) || STANDS_IN.test(value) || ANGLED.test(value)) return true;
  if (value.startsWith("$")) return readsAsName(value.slice(1));
  if (value.startsWith("=")) return readsAsName(strip(value, "=", ""));
  if (value.startsWith("{")) {
    const inner = strip(value, "{ ", "} ");
    return !inner || readsAsName(inner) || isCall(inner);
  }
  return PLACEHOLDER.test(value === value.toUpperCase() ? value.toLowerCase() : value.charAt(0).toLowerCase() + value.slice(1));
}

const refers = (value) => reference(value) || isCall(value);

// A plain word (prose, a status, a type) against what could be a value: digits, symbols, or capitals inside a word.
const plainWord = (value) => /^[A-Za-z]+$/.test(value) && (value === value.toLowerCase() || value === value.toUpperCase() || /^[A-Z][a-z]+$/.test(value));

const SCHEME = String.raw`[a-z][a-z0-9+.-]{1,30}:\/\/`;
// To the last @ before the host, since a password may hold one.
const URL_PASSWORD = new RegExp(String.raw`\b(${SCHEME}[^\s/:@'"\x60<>]*:)([^\s/'"\x60<>]+)@`, "gi");
const URL_USER_TOKEN = new RegExp(String.raw`\b(${SCHEME})([^\s/:@'"\x60<>]{16,})@`, "gi");
const URL_QUERY = /([?&;](?:[a-z0-9_.-]*(?:token|secret|password|passwd|pwd|api[_-]?key|apikey|auth|sig|signature|credential|sas|key)))=([^&;\s'"`<>#]+)/gi;

const PEM = /-----BEGIN [A-Z0-9 ]*PRIVATE KEY(?: BLOCK)?-----[\s\S]*?(?:-----END [A-Z0-9 ]*PRIVATE KEY(?: BLOCK)?-----|$)/g;

// A header holding a credential, to the end of its line, unless the line is prose (three or more plain words).
const HEADER = /\b((?:proxy-)?authorization|set-cookie|cookie|x-api-key|x-auth-token|x-access-token|api-key)(["']?\s*:\s*)([^\r\n]+)/gi;
const proseLine = (value) => {
  const words = value.trim().split(/\s+/);
  return words.length >= 3 && words.every((w) => /^[A-Za-z,.'-]+$/.test(w));
};
const BEARER = /\b(Bearer\s+)([A-Za-z0-9._~+/=-]{8,})/gi;

// A flag on a command line, quoted or not. Before a plain word it is prose ("--token is required").
const FLAG = /(--?(?:password|passwd|pass|pwd|token|api-?key|apikey|secret|client-secret|auth-token|access-token|access-key|secret-key|private-key))(=|\s+)(?:"([^"\n]+)"|'([^'\n]+)'|`([^`\n]+)`|["'`]?([^\s"'`]+))/gi;
const MYSQL_FLAG = /\b(mysql(?:dump|admin)?\b[^\n]{0,400}?\s-p)(\S+)/g;
const SSHPASS = /\b(sshpass\s+-p\s*)(\S+)/g;
const CURL_USER = /\b(curl\b[^\n]{0,400}?\s(?:-u|--user)[\s=]+["']?[^\s:"']+:)([^\s"']+)/g;

// A name that says it holds a secret, then a separator and a value: password=, "api_key": "...", TOKEN := ...; connection
// strings' Password= and AccountKey= among them. The name must end in the word, so max_tokens and token_count stay. A key's own
// words count in camel case too (secretKey, accessKey), and pass only as a word of its own (DB_PASS, pass=, never bypass=).
const SECRET_NAME = String.raw`(?:[A-Za-z0-9_.-]*(?:password|passwd|pwd|passphrase|passcode|pgpass|secret|credentials?|token|api[_-]?key|apikey|(?:secret|private|access|auth|signing|encryption|master|shared)[_-]?key|[_-]key|accountkey|sharedaccesskey|sharedaccesssignature)|(?:[A-Za-z0-9_.-]*[_.-])?pass)`;
const ASSIGNED = new RegExp(String.raw`(?<![A-Za-z0-9_.-])(${SECRET_NAME})(\\?["'\x60]?\s*(?::=|=>|=|:)\s*)(?:\\"([^"\\\n]*)\\"|"([^"\n]*)"|'([^'\n]*)'|\x60([^\x60\n]*)\x60|["'\x60]?([^\s"'\x60,;&)}\]]+))`, "gi");
const STRONG_KEY = /(?:secret|private|access|api|auth|account|signing|encryption|master|shared)[_-]?key$/i;
// A name only a password has. After a colon a plain word is still its value, which it is not for a token (token: expired).
const PASSWORD_NAME = /(?:password|passwd|pwd|passphrase|passcode|pgpass)$/i;

// The same names in XML and .NET config: <password>...</password>, <add key="password" value="..."/>. Each name is bounded, so
// a long run of name characters can't stall it.
const XML_NAME = String.raw`[\w.:-]{0,64}(?:password|passwd|pwd|passphrase|secret|token|api[_-]?key|apikey|(?:private|access|auth|signing|encryption|master|shared)[_-]?key)`;
const XML_ELEMENT = new RegExp(String.raw`<(${XML_NAME})>([^<\n]{1,2000})</\1>`, "gi");
const XML_ADD = new RegExp(String.raw`(\bkey=(["'])${XML_NAME}\2\s{1,8}value=(["']))([^"'\n]{1,2000})(?=\3)`, "gi");

// In words: "the password is ...", "set the token to ...". A plain word after it is prose ("the token is invalid").
const SAID = /\b(password|passphrase|passcode|secret(?: key)?|api key|access key|token)(\s+(?:is|was|to|=|:)\s+)(?:"([^"\n]+)"|'([^'\n]+)'|`([^`\n]+)`|["'`]?([^\s"'`,;]+))/gi;

// The shapes known providers give their keys. Each is a whole value, so nothing around it is needed to find it.
const SHAPES = [
  /\bsk-[A-Za-z0-9_-]{16,}/g,
  /\bgh[pousr]_[A-Za-z0-9]{20,}/g,
  /\bgithub_pat_[A-Za-z0-9_]{20,}/g,
  /\bglpat-[A-Za-z0-9_-]{16,}/g,
  /\bxox[a-z]-[A-Za-z0-9-]{10,}/g,
  /\bxapp-[A-Za-z0-9-]{10,}/g,
  /hooks\.slack\.com\/services\/[A-Za-z0-9/]+/g,
  /discord(?:app)?\.com\/api\/webhooks\/[0-9]+\/[A-Za-z0-9_-]+/g,
  /\b(?:AKIA|ASIA|ABIA|ACCA)[0-9A-Z]{16}\b/g,
  /\bAIza[0-9A-Za-z_-]{30,}/g,
  /\bGOCSPX-[A-Za-z0-9_-]{16,}/g,
  /\bya29\.[A-Za-z0-9_-]{20,}/g,
  /\b1\/\/0[A-Za-z0-9_-]{30,}/g,
  /\b[rsp]k_(?:live|test)_[A-Za-z0-9]{16,}/g,
  /\bwhsec_[A-Za-z0-9]{16,}/g,
  /\bnpm_[A-Za-z0-9]{30,}/g,
  /\bpypi-[A-Za-z0-9_-]{30,}/g,
  /\bhf_[A-Za-z0-9]{30,}/g,
  /\bdop_v1_[A-Za-z0-9]{30,}/g,
  /\bSG\.[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{16,}/g,
  /\btskey-[A-Za-z0-9-]{16,}/g,
  /\bAGE-SECRET-KEY-[A-Z0-9]{30,}/g,
  /\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}(?:\.[A-Za-z0-9_-]*)?/g,
  /\beyJ[A-Za-z0-9+/_-]{40,}={0,2}/g,
];

// Any other long run of token characters that reads as random rather than as words: how often the character class
// changes along it tells a token from a CamelCase name or a path. A UUID, a commit id or a shorter hash has only one case
// and hex digits, and stays.
const LONG_RUN = /(?<![A-Za-z0-9_+/-])[A-Za-z0-9_+/-]{32,}={0,2}(?![A-Za-z0-9_+/=-])/g;

export function tokenLike(run) {
  const body = strip(run, "", "=");
  if (/^[0-9a-fA-F-]+$/.test(body)) return /^[0-9a-fA-F]{48,}$/.test(body);
  const rate = kindChanges(body) / body.length;
  const lower = /[a-z]/.test(body), upper = /[A-Z]/.test(body), digits = /[0-9]/.test(body);
  if (lower && upper) return rate >= 0.25 && wordShare(body) < (digits ? 0.5 : 0.45);
  return digits && rate >= 0.15;
}

// Values assigned to a secret's name. By hand rather than through a replace, because what a bare value is depends on what it
// is judged to be. ASSIGNED ends one at , & ) } ], so that what only names a secret hides nothing after it (password=$PW,token=
// abc). One judged a secret runs on to whitespace, a quote or a semicolon, less what closes around it (ab,cd is one value).
function redactAssigned(text) {
  let out = "", from = 0, count = 0;
  ASSIGNED.lastIndex = 0;
  for (let found; (found = ASSIGNED.exec(text)); ) {
    const [m, name, sep, escaped, double, single, tick, bare] = found;
    const value = escaped ?? double ?? single ?? tick ?? bare;
    if (!value || (bare === undefined ? reference(value) : refers(value))) continue;
    const generic = /[_-]key$/i.test(name) && !STRONG_KEY.test(name);
    if (generic && (value.length < 16 || plainWord(value))) continue;
    // After a colon a bare word may be prose or a count (token: expired, token: 4096); after = it is a value.
    const colon = /:\s*$/.test(sep);
    if (bare !== undefined && colon && !PASSWORD_NAME.test(name) && (plainWord(value) || /^\d{1,7}$/.test(value))) continue;
    count++;
    const matched = found.index + m.length;
    if (bare === undefined) {
      out += `${text.slice(from, found.index)}${name}${sep}${m.slice(name.length + sep.length).replace(value, () => REDACTED)}`;
      from = matched;
      continue;
    }
    let end = matched;
    while (end < text.length && !/[\s"'`;]/.test(text[end])) end++;
    while (end > matched && ",&)}]".includes(text[end - 1])) end--;
    out += `${text.slice(from, matched - bare.length)}${REDACTED}`;
    from = end;
    ASSIGNED.lastIndex = end;
  }
  return { text: out + text.slice(from), count };
}

// The values of this process's own environment that are named as secrets, for what no shape can recognise. An agent's hook
// inherits its environment, so a key it was started with is known here whatever it looks like. PWD and OLDPWD are folders.
export function secretValues(env = {}) {
  const named = /(?:^|_)(?:TOKEN|SECRET|PASSWORD|PASSWD|PASSPHRASE|APIKEY|KEY|CREDENTIALS?)(?:$|_)/i;
  const values = [];
  for (const [name, value] of Object.entries(env)) {
    if (!named.test(name) || typeof value !== "string" || value.length < 12) continue;
    if (/^(?:\/|\.\.?\/|~|[A-Za-z]:[\\/])/.test(value) || /\s/.test(value)) continue;
    values.push(value);
  }
  return values;
}

// The text with each secret replaced, and how many were. `own` holds exact values to take out: the hook's own token and
// the environment's. Nothing here reads a file or the network.
export function redact(input, { own = [] } = {}) {
  let text = String(input ?? "");
  let count = 0;
  const swap = (pattern, replace) => {
    text = text.replace(pattern, (...match) => {
      const out = replace(...match);
      if (out !== match[0]) count++;
      return out;
    });
  };
  for (const value of [...own].filter((v) => typeof v === "string" && v.length >= 8).sort((a, b) => b.length - a.length)) {
    const parts = text.split(value);
    count += parts.length - 1;
    text = parts.join(REDACTED);
  }
  swap(PEM, () => REDACTED);
  // A URL's password or token is never a call, so only a reference spares it.
  swap(URL_PASSWORD, (m, head, value) => (reference(value) ? m : `${head}${REDACTED}@`));
  swap(URL_USER_TOKEN, (m, head, value) => (reference(value) ? m : `${head}${REDACTED}@`));
  swap(URL_QUERY, (m, head, value) => (reference(value) ? m : `${head}=${REDACTED}`));
  swap(HEADER, (m, name, sep, value) => {
    // Its first word says what it is: a header on a command line runs on past its closing quote to the end of the line.
    const first = strip(value.trim(), `"'\``, "").replace(/^(?:Bearer|Basic|Token)\s+/i, "").split(/[\s"'`]/, 1)[0];
    return proseLine(value) || refers(first) ? m : `${name}${sep}${REDACTED}`;
  });
  swap(BEARER, (m, head, value) => (plainWord(value) && value.length < 20 ? m : `${head}${REDACTED}`));
  swap(MYSQL_FLAG, (m, head) => `${head}${REDACTED}`);
  swap(SSHPASS, (m, head) => `${head}${REDACTED}`);
  swap(CURL_USER, (m, head, value) => (refers(value) ? m : `${head}${REDACTED}`));
  swap(FLAG, (m, flag, sep, double, single, tick, bare) => {
    const value = double ?? single ?? tick ?? bare;
    const plain = bare === undefined ? value : strip(bare, "", ",.;)");
    if (!plain || refers(plain) || (bare !== undefined && sep !== "=" && plainWord(plain))) return m;
    return flag + sep + m.slice(flag.length + sep.length).replace(value, () => REDACTED);
  });
  swap(XML_ELEMENT, (m, name, value) => (refers(value) ? m : `<${name}>${REDACTED}</${name}>`));
  swap(XML_ADD, (m, head, a, b, value) => (refers(value) ? m : `${head}${REDACTED}`));
  const assigned = redactAssigned(text);
  text = assigned.text;
  count += assigned.count;
  swap(SAID, (m, name, sep, double, single, tick, bare) => {
    const quoted = double ?? single ?? tick;
    const value = quoted ?? strip(bare, "", ".!?:)");
    if (!value || refers(value)) return m;
    if (quoted === undefined && (plainWord(value) || value.length < 4)) return m;
    return name + sep + m.slice(name.length + sep.length).replace(value, () => REDACTED);
  });
  for (const shape of SHAPES) swap(shape, () => REDACTED);
  swap(LONG_RUN, (m) => (tokenLike(m) ? REDACTED : m));
  return { text, count };
}
