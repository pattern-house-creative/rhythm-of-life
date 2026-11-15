// Saving the page a person is on (SB-P7-03; src/bookmarklet.ts, src/PageCapture.tsx, src/PageBookmark.tsx): a bookmark
// that holds this app's address and no token, the app's own page that shows what it carried and saves it through the
// Memories view's capture as the app (the browser sending no credential, the app's server its own), nothing saved
// until the person presses Save, and nothing of it offered while the engine's memory.tools is off. Fictional pages and
// words only; the engine is a stand-in answering as app_memories does.
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import {
  bookmarklet,
  captureText,
  pageLimits,
  readCapture,
  takePageCapture,
} from "../src/bookmarklet";
import type { PageDraft } from "../src/bookmarklet";
import PageCapture from "../src/PageCapture";
import Start from "../src/Start";
import { startPage } from "../src/startPage";
import PageBookmark from "../src/PageBookmark";
import MemoryView from "../src/MemoryView";
import { handleMemories } from "../server/memories";
import { routes } from "../server/routes";
import type { MemoryClient, MemoryReply } from "../src/memories";
import { allAppTools, appToken, called, rpc, withEngine } from "./engine";

// The sky, as Start draws it in place of the page for saving: a stand-in, since only which one is drawn matters here.
vi.mock("../src/App.tsx", () => ({ default: () => "DEMO: the sky" }));

afterEach(() => {
  vi.unstubAllEnvs();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  window.getSelection()?.removeAllRanges();
  document.body.innerHTML = "";
});

const app = "https://rhythm.example.invalid";
const frag = (v: unknown) =>
  `#capture=${encodeURIComponent(typeof v === "string" ? v : JSON.stringify(v))}`;
const sent: PageDraft = {
  title: "DEMO: The Wendle allotment rota",
  url: "https://allotments.example.invalid/wendle/rota?week=45",
  text: "DEMO: Sam waters the beans on Tuesdays; Alex takes the compost on Fridays.",
  cut: false,
};
const carried = { v: 1, ...sent };

// The bookmark as a browser runs it: the script after javascript:, percent-decoded, run on the page shown now.
const run = (code: string) =>
  new Function(decodeURIComponent(code.slice("javascript:".length)))();
function select(text: string) {
  document.body.innerHTML = "";
  const p = document.createElement("p");
  p.textContent = text;
  document.body.append(p);
  const range = document.createRange();
  range.selectNodeContents(p);
  const selection = window.getSelection()!;
  selection.removeAllRanges();
  selection.addRange(range);
}
function opened() {
  const open = vi.spyOn(window, "open").mockReturnValue(null);
  return () => {
    expect(open).toHaveBeenCalledTimes(1);
    const [url, target, features] = open.mock.calls[0];
    return { url: new URL(String(url)), target, features };
  };
}

describe("the bookmark", () => {
  it("holds this app's address and nothing else: no key, token or call of its own", () => {
    const code = bookmarklet(app)!;
    expect(code.startsWith("javascript:")).toBe(true);
    const script = decodeURIComponent(code.slice("javascript:".length));
    expect(script).toContain(`"${app}/#capture="`);
    expect(script).not.toMatch(
      /token|bearer|authori[sz]ation|cookie|secret|fetch|XMLHttpRequest|sendBeacon|localStorage/i,
    );
    expect(script).not.toContain(appToken);
    expect(script.match(/https?:\/\//g)).toEqual(["https://"]);
    // Made from where the app is opened, whatever page of it: only its origin.
    expect(bookmarklet(`${app}/some/view?x=1#top`)).toBe(code);
    expect(bookmarklet("http://127.0.0.1:8080")).toContain(
      encodeURIComponent('"http://127.0.0.1:8080/#capture="'),
    );
    for (const odd of [
      "javascript:alert(1)",
      "file:///tmp/rhythm",
      "data:text/html,x",
      "not an address",
    ])
      expect(bookmarklet(odd), odd).toBeNull();
  });

  it("opens the app's page in a new tab with the page's title, address and selection after the #", () => {
    document.title = sent.title;
    select(sent.text);
    const open = opened();
    expect(run(bookmarklet(app)!)).toBeUndefined();
    const { url, target, features } = open();
    expect(target).toBe("_blank");
    expect(features).toBe("noopener,noreferrer");
    // Nothing before the # but the app's own address: no server is sent what the page carried.
    expect(`${url.origin}${url.pathname}${url.search}`).toBe(`${app}/`);
    expect(readCapture(url.hash)).toEqual({
      title: sent.title,
      url: location.href,
      text: sent.text,
      cut: false,
    });
  });

  it("carries the title and address alone when nothing is selected", () => {
    document.title = sent.title;
    const open = opened();
    run(bookmarklet(app)!);
    const page = readCapture(open().url.hash) as PageDraft;
    expect(page).toMatchObject({ title: sent.title, text: "", cut: false });
    expect(captureText(page)).toBe(`${sent.title}\n${location.href}`);
  });

  it("cuts a long selection and title, says the selection was cut, and never sends half a character", () => {
    // An odd start puts the cut inside a pair of UTF-16 units, which an address can't carry alone.
    document.title = "D".repeat(pageLimits.title + 50);
    select(`a${"🌱".repeat(pageLimits.text)}`);
    const open = opened();
    run(bookmarklet(app)!);
    const page = readCapture(open().url.hash) as PageDraft;
    expect(page.cut).toBe(true);
    expect(page.text).toHaveLength(pageLimits.text);
    expect(page.text.endsWith("�")).toBe(true);
    expect(page.title).toBe("D".repeat(pageLimits.title));
  });
});

describe("what the app's page reads from its address", () => {
  it("reads only what the bookmark makes, and leaves out an address that isn't http or https", () => {
    expect(readCapture("")).toBeNull();
    expect(readCapture("#top")).toBeNull();
    expect(readCapture(frag(carried))).toEqual(sent);
    for (const odd of [
      frag("not json"),
      "#capture=%E0%A4%A",
      frag([1, 2]),
      frag({ ...carried, v: 2 }),
      frag({ ...carried, title: 7 }),
      frag({ ...carried, cut: "no" }),
      frag({ title: sent.title, url: sent.url, text: sent.text, v: 1 }),
      frag({ ...carried, token: "x" }),
      frag({ ...carried, text: "x".repeat(pageLimits.text + 1) }),
      frag({
        ...carried,
        url: `https://a.example.invalid/${"x".repeat(pageLimits.url)}`,
      }),
    ])
      expect(readCapture(odd), odd).toBe("unreadable");
    for (const url of [
      "javascript:alert(1)",
      "file:///etc/hosts",
      "nonsense",
      "",
    ])
      expect(readCapture(frag({ ...carried, url }))).toEqual({
        ...sent,
        url: null,
      });
    expect(
      readCapture(frag({ ...carried, text: "DEMO: a\u0000b" })),
    ).toMatchObject({
      text: "DEMO: ab",
    });
  });

  it("takes what it read out of the address at once, and leaves any other address alone", () => {
    history.replaceState(null, "", `/?from=bookmark${frag(carried)}`);
    expect(takePageCapture()).toEqual(sent);
    expect(location.hash).toBe("");
    expect(location.search).toBe("?from=bookmark");
    history.replaceState(null, "", `/${frag("not json")}`);
    expect(takePageCapture()).toBe("unreadable");
    expect(location.hash).toBe("");
    history.replaceState(null, "", "/#somewhere");
    expect(takePageCapture()).toBeNull();
    expect(location.hash).toBe("#somewhere");
    history.replaceState(null, "", "/");
  });

  it("lays the memory out as Second Brain's bookmarklet does", () => {
    expect(captureText(sent)).toBe(
      `${sent.text}\n\n${sent.title}\n${sent.url}`,
    );
    expect(captureText({ ...sent, url: null, title: "  " })).toBe(sent.text);
    expect(captureText({ title: "", url: null, text: "", cut: false })).toBe(
      "",
    );
  });
});

// The browser's fetch, answered by the app's real facade (server/memories.ts); its engine answers each tools/call with
// `answer`, the contract's capture fixture by default. Both sides' requests are recorded.
const fixture = (name: string) =>
  JSON.parse(
    readFileSync(
      resolve(
        process.cwd(),
        "../contracts/fixtures",
        `app_memories.${name}.example.json`,
      ),
      "utf8",
    ),
  );
const captured = { ...fixture("capture"), project: null };
function served(answer: () => Response = () => rpc(captured)) {
  const engine = vi.fn(async (_url: unknown, _init?: RequestInit) => answer());
  const browser = vi.fn(async (path: string, init: RequestInit) => {
    expect(path).toBe("/api/memories");
    return handleMemories(
      new Request(`${app}${path}`, {
        method: init.method,
        headers: init.headers,
        body: init.body,
      }),
      engine as unknown as typeof fetch,
    );
  });
  vi.stubGlobal("fetch", browser);
  return { engine, browser };
}
const box = () => screen.findByRole("textbox", { name: "Memory" });
// The page counts a Save only once its form has been in view for half a second (PageCapture.tsx).
const settled = () => new Promise((done) => setTimeout(done, 600));

describe("the app's page", () => {
  beforeEach(() => withEngine({ tools: [...allAppTools, "app_memories"] }));

  it("shows what came and saves nothing until the person presses Save, then saves it once as the app", async () => {
    const { engine, browser } = served();
    const user = userEvent.setup();
    render(<PageCapture page={sent} />);
    expect(await box()).toHaveValue(captureText(sent));
    expect(
      screen.getByText(/^From allotments\.example\.invalid\./),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/Nothing is saved until you press Save/),
    ).toBeInTheDocument();
    // Asked whether the engine serves memories; nothing written.
    expect(engine).not.toHaveBeenCalled();
    expect(
      browser.mock.calls.every(([, init]) => (init?.method ?? "GET") === "GET"),
    ).toBe(true);
    await settled();
    await user.click(screen.getByRole("button", { name: "Save" }));
    expect(
      await screen.findByText(/Saved as a draft for the household/),
    ).toBeInTheDocument();
    expect(engine).toHaveBeenCalledTimes(1);
    const call = called(engine.mock.calls[0][1]);
    expect(call.name).toBe("app_memories");
    expect(Object.keys(call.arguments).sort()).toEqual([
      "content",
      "key",
      "op",
      "scope",
      "tags",
    ]);
    expect(call.arguments).toMatchObject({
      op: "capture",
      content: captureText(sent),
      scope: "shared",
      tags: ["reading"],
    });
    expect(call.arguments.key).toMatch(/^app:/);
    // The browser sent no credential; the app's server sent its own token to the engine.
    const asked = browser.mock.calls.find(
      ([, init]) => init?.method === "POST",
    )![1];
    expect(new Headers(asked.headers).has("authorization")).toBe(false);
    expect(JSON.stringify(asked)).not.toContain(appToken);
    expect(
      new Headers(engine.mock.calls[0][1]?.headers).get("authorization"),
    ).toBe(`Bearer ${appToken}`);
    expect(
      screen.getByRole("button", { name: "Close this tab" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("link", { name: "Open Rhythm of Life" }),
    ).toHaveAttribute("href", "/");
    expect(
      screen.queryByRole("button", { name: "Save" }),
    ).not.toBeInTheDocument();
  });

  it("saves what the person changed it to", async () => {
    const { engine } = served();
    const user = userEvent.setup();
    render(<PageCapture page={sent} />);
    const text = await box();
    await user.clear(text);
    await user.type(text, "DEMO: the rota is on the shed door.");
    await settled();
    await user.keyboard("{Control>}{Enter}{/Control}");
    await screen.findByText(/Saved as a draft/);
    expect(called(engine.mock.calls[0][1]).arguments.content).toBe(
      "DEMO: the rota is on the shed door.",
    );
  });

  it("says a page's words were held for review, and where to release them", async () => {
    served(() =>
      rpc({ ...captured, held: { reason: "instruction", signals: ["I1"] } }),
    );
    const user = userEvent.setup();
    render(<PageCapture page={sent} />);
    await box();
    await settled();
    await user.click(screen.getByRole("button", { name: "Save" }));
    expect(
      await screen.findByText(
        /held for review: it reads like instructions to an AI assistant/,
      ),
    ).toBeInTheDocument();
    expect(screen.getByText(/Memories view/)).toBeInTheDocument();
  });

  it("says when the answer was lost, and a second Save resends under the same key", async () => {
    let fail = true;
    const { engine } = served(() =>
      fail
        ? new Response("upstream gone", { status: 502 })
        : rpc({ ...captured, replayed: true }),
    );
    const user = userEvent.setup();
    render(<PageCapture page={sent} />);
    await box();
    await settled();
    await user.click(screen.getByRole("button", { name: "Save" }));
    expect(
      await screen.findByText(/may or may not have been saved/),
    ).toBeInTheDocument();
    fail = false;
    await user.click(screen.getByRole("button", { name: "Save" }));
    expect(
      await screen.findByText(/Saved already as a draft/),
    ).toBeInTheDocument();
    const keys = engine.mock.calls.map(
      ([, init]) => called(init).arguments.key,
    );
    expect(keys).toHaveLength(2);
    expect(keys[0]).toBe(keys[1]);
  });

  it("says when the selection was cut, and when the address carried nothing it can read", async () => {
    served();
    const { unmount } = render(<PageCapture page={{ ...sent, cut: true }} />);
    await box();
    expect(screen.getByText(/first 8,000 characters/)).toBeInTheDocument();
    unmount();
    render(<PageCapture page="unreadable" />);
    expect(await box()).toHaveValue("");
    expect(
      screen.getByText(/didn't bring a page this app can read/),
    ).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
  });

  it("counts no gesture begun on another page as Save: none in its first half second, nor a held key's repeats", async () => {
    const { engine } = served();
    const user = userEvent.setup();
    render(<PageCapture page={sent} />);
    const text = await box();
    const save = screen.getByRole("button", { name: "Save" });
    // The second click of a double click, landing as the tab opens: nothing sent.
    await user.click(save);
    await user.keyboard("{Control>}{Enter}{/Control}");
    // Ctrl+Enter held down on the page that opened the tab arrives here as repeats only.
    await settled();
    for (let i = 0; i < 5; i++)
      fireEvent.keyDown(text, { key: "Enter", ctrlKey: true, repeat: true });
    // A click with no step of the person's own before it in this page (no pointer moved, touched or key pressed).
    fireEvent.click(save);
    // Given time to send, nothing went.
    await new Promise((done) => setTimeout(done, 100));
    expect(engine).not.toHaveBeenCalled();
    expect(screen.queryByText("Saving…")).not.toBeInTheDocument();
    // The person's own click, after the half second: saved once.
    await user.click(save);
    expect(await screen.findByText(/Saved as a draft/)).toBeInTheDocument();
    expect(engine).toHaveBeenCalledTimes(1);
  });

  it("starts again when the tab is left and come back to", async () => {
    const { engine } = served();
    const user = userEvent.setup();
    render(<PageCapture page={sent} />);
    await box();
    await settled();
    const shown = vi.spyOn(document, "visibilityState", "get");
    shown.mockReturnValue("hidden");
    fireEvent(document, new Event("visibilitychange"));
    shown.mockReturnValue("visible");
    fireEvent(document, new Event("visibilitychange"));
    await user.click(screen.getByRole("button", { name: "Save" }));
    await new Promise((done) => setTimeout(done, 100));
    expect(engine).not.toHaveBeenCalled();
    await settled();
    await user.click(screen.getByRole("button", { name: "Save" }));
    await screen.findByText(/Saved as a draft/);
    expect(engine).toHaveBeenCalledTimes(1);
  });

  it("refuses a page's own request to save: only the app's own page reaches the engine", async () => {
    const { engine } = served();
    const answer = await handleMemories(
      new Request(`${app}/api/memories`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          Origin: "https://allotments.example.invalid",
        },
        body: JSON.stringify({
          op: "capture",
          content: "DEMO: planted",
          scope: "shared",
        }),
      }),
      engine as unknown as typeof fetch,
    );
    expect(answer.status).toBe(403);
    expect(engine).not.toHaveBeenCalled();
    // The page needs no route of its own: the app's address serves it.
    expect(
      Object.keys(routes).filter((p) => /capture|bookmark|page/i.test(p)),
    ).toEqual([]);
  });
});

// main.tsx's choice at the start: a page the bookmark sent shows the page for saving instead of the sky, and its tab
// is not kept up to date by reloading into a new build, which would lose what came, unless it draws the app after all
// (the engine doesn't serve the Memories view); any other address is.
describe("the app's start", () => {
  beforeEach(() => withEngine({ tools: [...allAppTools, "app_memories"] }));

  it("keeps a tab the bookmark opened out of the app's updates until it draws the app, and every other tab in them", () => {
    const register = vi.fn();
    const keep = vi.fn();
    history.replaceState(null, "", `/${frag(carried)}`);
    const opened = startPage(register, location, history, keep);
    expect(opened.captured).toEqual(sent);
    expect(location.hash).toBe("");
    expect(keep).not.toHaveBeenCalled();
    history.replaceState(null, "", `/${frag("not json")}`);
    expect(startPage(register, location, history, keep).captured).toBe("unreadable");
    expect(keep).not.toHaveBeenCalled();
    // The engine doesn't serve the Memories view, so the tab draws the app: kept up to date from then on, once.
    opened.plain();
    opened.plain();
    expect(keep).toHaveBeenCalledTimes(1);
    expect(keep).toHaveBeenCalledWith(register);
    history.replaceState(null, "", "/#somewhere");
    expect(startPage(register, location, history, keep).captured).toBeNull();
    expect(keep).toHaveBeenCalledTimes(2);
    history.replaceState(null, "", "/");
    const plain = startPage(register, location, history, keep);
    expect(plain.captured).toBeNull();
    plain.plain();
    expect(keep).toHaveBeenCalledTimes(3);
    expect(register).not.toHaveBeenCalled();
  });

  it("shows the page for saving, not the sky, while the address carried a page", async () => {
    served();
    const plain = vi.fn();
    render(<Start captured={sent} drawn={0} plain={plain} />);
    expect(
      screen.getByRole("heading", { name: "Save this page" }),
    ).toBeInTheDocument();
    expect(await box()).toHaveValue(captureText(sent));
    expect(screen.queryByText("DEMO: the sky")).not.toBeInTheDocument();
    expect(plain).not.toHaveBeenCalled();
  });
});

describe("with the engine's memory.tools off", () => {
  for (const [name, setUp] of [
    [
      "the engine lists no memories tool",
      () => void withEngine({ tools: allAppTools }),
    ],
    ["demo mode, no engine", () => {}],
  ] as const)
    it(`offers no Save and sends nothing: ${name}`, async () => {
      setUp();
      const { engine } = served();
      render(<PageCapture page={sent} />);
      expect(
        await screen.findByText(/isn't saving memories right now/),
      ).toBeInTheDocument();
      expect(screen.getByText(/Nothing was saved/)).toBeInTheDocument();
      expect(
        screen.queryByRole("button", { name: "Save" }),
      ).not.toBeInTheDocument();
      expect(screen.queryByRole("textbox")).not.toBeInTheDocument();
      expect(engine).not.toHaveBeenCalled();
    });

  // As before the page for saving existed: an address carrying a page opens the app, which is kept up to date again.
  for (const [name, setUp] of [
    [
      "the engine lists no memories tool",
      () => void withEngine({ tools: allAppTools }),
    ],
    ["demo mode, no engine", () => {}],
  ] as const)
    it(`draws the app at an address that carried a page: ${name}`, async () => {
      setUp();
      const { engine } = served();
      const plain = vi.fn();
      render(<Start captured={sent} drawn={0} plain={plain} />);
      expect(await screen.findByText("DEMO: the sky")).toBeInTheDocument();
      expect(
        screen.queryByRole("heading", { name: "Save this page" }),
      ).not.toBeInTheDocument();
      expect(screen.queryByText(/isn't saving memories/)).not.toBeInTheDocument();
      expect(plain).toHaveBeenCalledTimes(1);
      expect(engine).not.toHaveBeenCalled();
    });
});

// A stand-in for the Memories view's client: an empty list, and every call recorded.
function quiet(): MemoryClient & { calls: Record<string, unknown>[] } {
  const calls: Record<string, unknown>[] = [];
  return {
    calls,
    available: async () => true,
    call: async <T,>(request: Record<string, unknown>) => {
      calls.push(request);
      return {
        available: true,
        ok: true,
        memories: [],
        next: null,
      } as unknown as MemoryReply<T>;
    },
  };
}

describe("the bookmark's offer in the Memories view", () => {
  it("offers the bookmark for this app's address, and clicking it inside the app does nothing", async () => {
    const user = userEvent.setup();
    const open = vi.spyOn(window, "open").mockReturnValue(null);
    render(<MemoryView client={quiet()} onClose={() => {}} />);
    await user.click(await screen.findByText("Save pages from your browser"));
    const link = screen.getByRole("link", { name: /^Save to / });
    expect(link.getAttribute("href")).toBe(bookmarklet(location.origin));
    await user.click(link);
    expect(open).not.toHaveBeenCalled();
    expect(screen.getByText(/no key or token/)).toBeInTheDocument();
  });

  it("copies the bookmark, or shows it to copy by hand where the browser can't", async () => {
    const user = userEvent.setup();
    const writeText = vi.fn(async () => {});
    Object.defineProperty(navigator, "clipboard", {
      value: { writeText },
      configurable: true,
    });
    const { unmount } = render(<PageBookmark origin={app} />);
    await user.click(screen.getByText("Save pages from your browser"));
    await user.click(screen.getByRole("button", { name: "Copy the bookmark" }));
    expect(writeText).toHaveBeenCalledWith(bookmarklet(app));
    expect(await screen.findByText(/Copied/)).toBeInTheDocument();
    unmount();
    writeText.mockRejectedValue(new Error("not allowed"));
    render(<PageBookmark origin={app} />);
    await user.click(screen.getByText("Save pages from your browser"));
    await user.click(screen.getByRole("button", { name: "Copy the bookmark" }));
    expect(
      await screen.findByRole("textbox", { name: /bookmark's address/ }),
    ).toHaveValue(bookmarklet(app));
    Object.defineProperty(navigator, "clipboard", {
      value: undefined,
      configurable: true,
    });
  });

  it("offers nothing for an address a bookmark can't open", () => {
    const { container } = render(<PageBookmark origin="file:///tmp/rhythm" />);
    expect(container).toBeEmptyDOMElement();
  });
});
