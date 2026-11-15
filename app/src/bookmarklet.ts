// Saving the page a person is on as a memory (SB-P7-03), through the app's own page. Second Brain's bookmarklet posts to
// its service with the service's token written inside the bookmark, so every page it runs on can read that token. This
// bookmark holds this app's address and nothing else: clicked on a page, it opens the app in a new tab and hands it the
// page's title, its address and the text the person selected, after the # of the app's address. A browser sends that
// part to no server, this app's included, and opens the tab with no referrer and no way back to the page. The app's
// page (PageCapture.tsx) reads it once, takes it out of the address bar, and shows it in the Memories view's capture box;
// nothing is saved until the person presses Save, which goes through the app's own server as the app (app_memories'
// capture, a write the engine screens like every other). The browser holds no token at any point (docs/security.md).

// The most each part may carry, in UTF-16 code units as a page's script counts them: enough for a passage of reading
// notes, and well within the address length every browser takes.
export const pageLimits = { title: 300, url: 2048, text: 8000 } as const;
// What a saved page is tagged, as Second Brain's bookmarklet tags it.
export const pageTags: readonly string[] = ["reading"];

// What the bookmark carried: the page's title, its address (only an http or https one; null otherwise), the selected
// text, and whether the selection was longer than the bookmark carries.
export interface PageDraft {
  title: string;
  url: string | null;
  text: string;
  cut: boolean;
}

const prefix = "#capture=";

// The bookmark for this app at `origin`: a javascript: address whose script opens `<origin>/#capture=<what it read>`.
// Only an http or https origin; null for anything else. The script names no token, key or cookie and calls no server:
// it only opens the app's page (noopener, noreferrer), so the page it runs on learns nothing about the app but its
// address. A selection is cut at pageLimits.text and marked cut; each part is made well formed (a character the cut
// split in two becomes U+FFFD), since an address can't carry half a character.
export function bookmarklet(origin: string): string | null {
  let base: URL;
  try {
    base = new URL(origin);
  } catch {
    return null;
  }
  if (base.protocol !== "http:" && base.protocol !== "https:") return null;
  const target = JSON.stringify(`${base.origin}/${prefix}`);
  const { title, url, text } = pageLimits;
  const script =
    `(()=>{const w=s=>(s=String(s||""),s.toWellFormed?s.toWellFormed():s),c=(s,n)=>w(w(s).slice(0,n)),` +
    `t=w(window.getSelection&&window.getSelection()).trim();` +
    `window.open(${target}+encodeURIComponent(JSON.stringify({v:1,title:c(document.title,${title}),` +
    `url:c(location.href,${url}),text:c(t,${text}),cut:t.length>${text}})),"_blank","noopener,noreferrer")})()`;
  return `javascript:${encodeURIComponent(script)}`;
}

const noNul = (s: string) => s.replace(/\0/g, "");
const web = (u: string) => {
  try {
    const parsed = new URL(u);
    return parsed.protocol === "http:" || parsed.protocol === "https:";
  } catch {
    return false;
  }
};

// What an address's fragment carried: null when it isn't a page from the bookmark at all, "unreadable" when it says it
// is but isn't one the bookmark makes (not JSON, another version, a member missing, unknown or too long). Anyone can
// make such an address, so nothing in it is trusted: it is only shown, as text, for the person to read before Save.
// An address that isn't http or https is left out; NUL characters, which a memory can't hold, are dropped.
export function readCapture(hash: string): PageDraft | "unreadable" | null {
  if (!hash.startsWith(prefix)) return null;
  let value: unknown;
  try {
    value = JSON.parse(decodeURIComponent(hash.slice(prefix.length)));
  } catch {
    return "unreadable";
  }
  if (!value || typeof value !== "object" || Array.isArray(value))
    return "unreadable";
  const v = value as Record<string, unknown>;
  const members = ["v", "title", "url", "text", "cut"];
  if (
    Object.keys(v).some((k) => !members.includes(k)) ||
    v.v !== 1 ||
    typeof v.title !== "string" ||
    typeof v.url !== "string" ||
    typeof v.text !== "string" ||
    typeof v.cut !== "boolean" ||
    v.title.length > pageLimits.title ||
    v.url.length > pageLimits.url ||
    v.text.length > pageLimits.text
  )
    return "unreadable";
  return {
    title: noNul(v.title),
    url: web(v.url) ? noNul(v.url) : null,
    text: noNul(v.text),
    cut: v.cut,
  };
}

// The page the bookmark sent this tab, read from the address and taken out of it at once (history.replaceState), so a
// selection doesn't stay in the address bar, a copied link or the tab's own history entry. Null when the address
// carries none, which leaves the address as it was.
export function takePageCapture(
  where: Location = location,
  past: History = history,
): PageDraft | "unreadable" | null {
  const found = readCapture(where.hash);
  if (found !== null)
    past.replaceState(past.state, "", `${where.pathname}${where.search}`);
  return found;
}

// The memory's text, laid out as Second Brain's bookmarklet lays it out: the selection, then the page's title and its
// address on their own lines; only the title and the address when nothing was selected.
export function captureText(page: PageDraft) {
  const from = [page.title.trim(), page.url ?? ""].filter(Boolean).join("\n");
  return [page.text.trim(), from].filter(Boolean).join("\n\n");
}
