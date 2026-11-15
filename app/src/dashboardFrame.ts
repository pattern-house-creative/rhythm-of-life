// The documents a dashboard's page runs in. The page sits in an iframe with
// sandbox="allow-scripts allow-forms" and no allow-same-origin, so it has no
// origin of its own: it can't reach the app, its logins, cookies or storage.
// Its content policy, parsed before the page, blocks every request (no fetch,
// images, fonts or scripts from anywhere). Its forms run the page's own submit
// handlers, but none is ever sent: the bridge cancels each submission and the
// policy sends form data nowhere. That frame sits inside a second, equally
// sandboxed one that the app writes, whose own policy lets no frame load
// anything: the page can't navigate itself away with data in the address,
// whatever the app's frames elsewhere (Google's viewer) need.
// A content policy doesn't cover WebRTC: a peer connection's STUN and TURN
// lookups reach any address, with data in the name. Both policies say
// webrtc 'block', which Chromium doesn't enforce yet (it logs the directive
// as unrecognized), so the bridge also removes the page's own peer
// connections before the page runs; see the bridge for what that can't stop.
// Everything the page draws with comes in here: the app's look, and a small
// bridge, window.rhythm, that hands it data through a private message channel.
export const framePolicy = [
  "default-src 'none'",
  "script-src 'unsafe-inline'",
  "style-src 'unsafe-inline'",
  "img-src data: blob:",
  "font-src data:",
  "media-src data: blob:",
  "connect-src 'none'",
  "form-action 'none'",
  "base-uri 'none'",
  "webrtc 'block'",
].join("; ");

// Rhythm of Life's palette and type, named for the page (the app's design
// language: calm and dark, teal for health, ice blue for knowledge, purple for
// memory, amber for stale, red only for real attention).
export const frameTokens: Record<string, string> = {
  "--rhythm-text": "#e4eef0",
  "--rhythm-muted": "#8fa9b7",
  "--rhythm-faint": "#718993",
  "--rhythm-line": "rgba(143, 169, 183, 0.18)",
  "--rhythm-surface": "rgba(20, 31, 39, 0.55)",
  "--rhythm-health": "#75d8b9",
  "--rhythm-knowledge": "#9ccfe0",
  "--rhythm-memory": "#b6a6cc",
  "--rhythm-caution": "#c1a66d",
  "--rhythm-alert": "#e08a97",
  "--rhythm-font": '"DM Sans Variable", system-ui, sans-serif',
  "--rhythm-display":
    '"Manrope Variable", "DM Sans Variable", system-ui, sans-serif',
};

function starter(fonts: { body?: string; display?: string }) {
  const face = (family: string, url?: string) =>
    url
      ? `@font-face{font-family:"${family}";font-style:normal;font-display:swap;font-weight:200 800;src:url(${url}) format("woff2")}`
      : "";
  return [
    face("DM Sans Variable", fonts.body),
    face("Manrope Variable", fonts.display),
    `:root{${Object.entries(frameTokens)
      .map(([k, v]) => `${k}:${v}`)
      .join(";")};color-scheme:dark}`,
    "*{box-sizing:border-box}",
    "html,body{margin:0;background:transparent}",
    "body{font-family:var(--rhythm-font);color:var(--rhythm-text);font-size:15px;line-height:1.45;padding:0;-webkit-font-smoothing:antialiased}",
    "h1,h2,h3{font-family:var(--rhythm-display);font-weight:600;letter-spacing:0.01em;margin:0 0 .5em}",
    "svg{overflow:visible}",
    // A page that draws only a compact card sits centred in the tall slot. The
    // rule weighs no more than `body`, so the page's own body rules still win.
    ':where(html[data-shape="tall"]) body{min-height:100vh;display:flex;flex-direction:column;justify-content:center}',
    "@media (prefers-reduced-motion:reduce){*,*::before,*::after{animation:none!important;transition:none!important}}",
  ].join("\n");
}

// The bridge: onData callbacks run on every delivery; the app hears "ready"
// once a delivery drew without throwing, "error" when it didn't, "open" when
// the page asks for a star, and "write" when it asks to apply one of its
// declared writes, which the app answers on the same channel. Only the app's
// first message, from the parent window, can hand it the channel. A form
// never sends: its submit handlers run, but the submission is cancelled and
// submit() does nothing (the blocked request would swap the page for the
// browser's error page), except in a method="dialog" form, which only closes
// its dialog. The page's own peer connection (WebRTC), which would reach the
// network past the content policy, is removed: a best effort, since a frame
// the page draws itself gets a fresh one (docs/security.md).
const bridge = `(() => {
  for (const name of ["RTCPeerConnection", "webkitRTCPeerConnection", "mozRTCPeerConnection"]) delete window[name];
  let port = null, handlers = [], last = null, next = 1;
  const asks = new Map();
  const send = (m) => { if (port) port.postMessage(m); };
  const fail = (e) => send({ type: "error", message: String((e && e.message) || e || "Unknown error").slice(0, 300) });
  const run = (fn, data) => {
    try {
      const r = fn(data);
      if (r && typeof r.then === "function") r.catch(fail);
      return true;
    } catch (e) { fail(e); return false; }
  };
  const deliver = async () => {
    if (!last) return;
    const delivery = last;
    try {
      await Promise.all(handlers.map(fn => fn(delivery)));
      send({ type: handlers.length ? "ready" : "idle", selectionRevision: delivery.selection && delivery.selection.revision });
    } catch (e) { fail(e); }
  };
  Object.defineProperty(window, "rhythm", { value: Object.freeze({
    onData(fn) { if (typeof fn === "function") { handlers.push(fn); if (last) run(fn, last); } },
    openStar(graphId) { if (typeof graphId === "string" && graphId.length <= 1000) send({ type: "open", star: graphId }); },
    write(name, change) {
      return new Promise((resolve) => {
        if (!port) return resolve({ ok: false, reason: "The dashboard isn't connected yet." });
        if (typeof name !== "string" || !change || typeof change !== "object") return resolve({ ok: false, reason: "A write names a declared write and a change." });
        if (asks.size >= 4) return resolve({ ok: false, reason: "Earlier changes are still being written." });
        const id = next++;
        asks.set(id, resolve);
        try { port.postMessage({ type: "write", id, name, change }); }
        catch { asks.delete(id); resolve({ ok: false, reason: "A change holds only text, numbers, true or false, and lists or objects of them." }); }
      });
    },
  }) });
  window.addEventListener("error", (e) => fail(e.error || e.message));
  window.addEventListener("unhandledrejection", (e) => fail(e.reason));
  const dialog = (form, button) => String((button && button.getAttribute("formmethod")) || (form && form.getAttribute && form.getAttribute("method"))).toLowerCase() === "dialog";
  window.addEventListener("submit", (e) => { if (!dialog(e.target, e.submitter)) e.preventDefault(); }, true);
  const submit = HTMLFormElement.prototype.submit;
  HTMLFormElement.prototype.submit = function () { if (dialog(this)) submit.call(this); };
  window.addEventListener("message", (e) => {
    if (port || e.source !== window.parent || !e.ports || !e.ports[0] || !e.data || e.data.type !== "rhythm:init") return;
    port = e.ports[0];
    port.onmessage = (m) => {
      if (m.data && m.data.type === "data") { last = m.data.payload; deliver(); }
      else if (m.data && m.data.type === "written" && asks.has(m.data.id)) { asks.get(m.data.id)(m.data.answer); asks.delete(m.data.id); }
    };
  });
})();`;

// The wrapper around it: no network and no frame navigation at all. It hands
// the app's channel to the page once, when both are ready. The page, written
// in place, inherits this policy on top of its own, so it allows the page's
// in-document images and the app's fonts too.
export const wrapperPolicy = [
  "default-src 'none'",
  "script-src 'unsafe-inline'",
  "style-src 'unsafe-inline'",
  "img-src data: blob:",
  "font-src data:",
  "media-src data: blob:",
  "frame-src 'none'",
  "child-src 'none'",
  "form-action 'none'",
  "base-uri 'none'",
  "webrtc 'block'",
].join("; ");
const relay = (page: string, title: string) => `(() => {
  const inner = document.createElement("iframe");
  inner.setAttribute("sandbox", "allow-scripts allow-forms");
  inner.setAttribute("referrerpolicy", "no-referrer");
  inner.setAttribute("allow", "");
  inner.setAttribute("title", ${script(title)});
  inner.srcdoc = ${script(page)};
  let loaded = false, port = null, sent = false;
  const hand = () => {
    if (sent || !loaded || !port) return;
    sent = true;
    inner.contentWindow.postMessage({ type: "rhythm:init" }, "*", [port]);
  };
  inner.addEventListener("load", () => { if (!loaded) { loaded = true; hand(); } });
  window.addEventListener("message", (e) => {
    if (port || e.source !== window.parent || !e.ports || !e.ports[0] || !e.data || e.data.type !== "rhythm:init") return;
    port = e.ports[0];
    hand();
  });
  document.body.append(inner);
})();`;
// A string as a script literal that can't close the script element around it.
const script = (text: string) =>
  JSON.stringify(text)
    .replace(/</g, "\\u003c")
    .replace(/\u2028/g, "\\u2028")
    .replace(/\u2029/g, "\\u2029");

// Where a page draws: the whole dashboard, or its card on the ambient screen,
// compact in a corner or tall in the main slot.
export type DashboardMode = "full" | "card";
export type CardShape = "compact" | "tall" | "launcher";

// The document the app's iframe holds: the wrapper, carrying the page's own.
export function sandboxDocument(
  html: string,
  title: string,
  fonts: { body?: string; display?: string } = {},
  mode: DashboardMode = "full",
  shape: CardShape = "compact",
) {
  return (
    "<!doctype html><html><head>" +
    `<meta http-equiv="Content-Security-Policy" content="${wrapperPolicy}">` +
    '<meta charset="utf-8"><meta name="color-scheme" content="dark">' +
    "<style>html,body{margin:0;height:100%;overflow:hidden;background:transparent}" +
    "iframe{display:block;width:100%;height:100%;border:0;color-scheme:dark}</style>" +
    `</head><body><script>${relay(frameDocument(html, fonts, mode, shape), title)}</script></body></html>`
  );
}

export function frameDocument(
  html: string,
  fonts: { body?: string; display?: string } = {},
  mode: DashboardMode = "full",
  shape: CardShape = "compact",
) {
  const card = mode === "card" ? ` data-shape="${shape}"` : "";
  return (
    `<!doctype html><html data-mode="${mode}"${card}><head>` +
    `<meta http-equiv="Content-Security-Policy" content="${framePolicy}">` +
    '<meta charset="utf-8"><meta name="color-scheme" content="dark">' +
    '<meta name="viewport" content="width=device-width, initial-scale=1">' +
    `<style>${starter(fonts)}</style><script>${bridge}</script>` +
    `</head><body>${html}</body></html>`
  );
}
