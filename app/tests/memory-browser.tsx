// Vite dev-only harness for SB-P6-02: the memory screens in a real browser at whatever size it is given, over the contract's fictional
// fixtures, with no engine and nothing sent anywhere; not an entry point in the production build. /tests/memory-browser.html shows the
// Memories view; ?screen=home the Home card; ?screen=capture the page a bookmark opens. ?long=1 puts one long unbroken word in every
// row. ?audit=1 walks every section and writes what it measured into <pre id="audit">: anything wider than its column, any control
// under a finger's size and any field that would make a phone zoom. jsdom has no layout, so this is what checks those.
import { createRoot } from "react-dom/client";
import Explorer from "../src/Explorer";
import PageCapture from "../src/PageCapture";
import { ResurfaceCard } from "../src/ResurfaceCard";
import { emptyGraph } from "../src/liveGraph";
import { cleanMemoryAnswer } from "../src/memories";
import type { MemoryClient, MemoryReply } from "../src/memories";
import type { KnowledgeController } from "../src/useKnowledge";
import "../src/index.css";
import "../src/App.css";

const params = new URLSearchParams(location.search);
const long = params.has("long");
const fixtures = import.meta.glob(
  "../../contracts/fixtures/app_memories.*.json",
  {
    eager: true,
    import: "default",
  },
) as Record<string, Record<string, unknown>>;
const word = `DEMO:${"unbrokenword".repeat(16)}`;
// Each text a row shows gains the long word when asked for.
const stretched = (v: unknown): unknown => {
  if (Array.isArray(v)) return v.map(stretched);
  if (v && typeof v === "object")
    return Object.fromEntries(
      Object.entries(v).map(([k, x]) => [
        k,
        typeof x === "string" &&
        ["preview", "content", "title", "summary"].includes(k)
          ? `${x} ${word}`
          : stretched(x),
      ]),
    );
  return v;
};
const fixture = (name: string) => {
  const found =
    fixtures[`../../contracts/fixtures/app_memories.${name}.example.json`];
  return (long ? stretched(found) : found) as
    Record<string, unknown> | undefined;
};
// Every answer rebuilt from the contract, as the facade does.
const client: MemoryClient = {
  available: async () => true,
  sharing: async () => false,
  call: (async (request: Record<string, unknown>) => {
    const op = String(request.op);
    const read = fixture(op === "get" ? "held" : op);
    const clean = read && cleanMemoryAnswer(op, read);
    return {
      available: true,
      ...(clean ?? {
        ok: false,
        code: "unavailable",
        reason: "Not in this harness.",
      }),
    } as MemoryReply<never>;
  }) as MemoryClient["call"],
};

const page = emptyGraph();
const knowledge = {
  page,
  accept: () => {},
  pins: [],
  togglePin: () => {},
  busy: false,
  error: null,
  refresh: async () => {},
  discoverMore: async () => {},
  lastRefresh: null,
  epoch: 0,
  session: {
    initial: page,
    refresh: async () => page,
    retrieve: async () => ({ ...page, cursors: {} }),
    memories: client,
  },
} as unknown as KnowledgeController;

const screenName = params.get("screen") ?? "view";
createRoot(document.getElementById("root")!).render(
  screenName === "home" ? (
    <main
      style={{ display: "flex", flexDirection: "column", height: "100svh" }}
    >
      <ResurfaceCard client={client} resting={false} />
    </main>
  ) : screenName === "capture" ? (
    <PageCapture
      client={client}
      page={{
        title: "DEMO: the Wendle rota",
        url: "https://example.invalid/wendle",
        text: "DEMO: the Wendle rota changes on Tuesdays.",
        cut: false,
      }}
    />
  ) : (
    <div style={{ position: "fixed", inset: 0 }}>
      <Explorer
        selected={null}
        onSelect={() => {}}
        close={() => {}}
        paused
        state="ready"
        knowledge={knowledge}
      />
    </div>
  ),
);

const settle = (ms = 250) => new Promise((go) => setTimeout(go, ms));
const named = (el: Element) =>
  (el.getAttribute("aria-label") ?? el.textContent ?? "").trim().slice(0, 40) ||
  el.tagName.toLowerCase();
function measure(scope: Element) {
  const phone = innerWidth <= 760 || innerHeight <= 500;
  const box = scope.getBoundingClientRect();
  const wide = [...scope.querySelectorAll("*")].flatMap((el) => {
    // A strip that scrolls across and a field's own contents are meant to run on.
    if (el.closest(".memory-tabs, .back-link, textarea, select, svg"))
      return [];
    const at = el.getBoundingClientRect();
    return at.width && (at.right > box.right + 1 || at.left < box.left - 1)
      ? [`${el.className || el.tagName} ${Math.round(at.right - box.right)}px`]
      : [];
  });
  const controls = [
    ...scope.querySelectorAll("button, a, input, select, textarea, summary"),
  ].filter((el) => el.getBoundingClientRect().height > 0);
  return {
    wide: wide.slice(0, 4),
    scrollsAcross: scope.scrollWidth > scope.clientWidth + 1,
    small: phone
      ? controls.flatMap((el) => {
          const at = el.getBoundingClientRect();
          const checkbox = (el as HTMLInputElement).type === "checkbox";
          return at.height < (checkbox ? 24 : 40)
            ? [`${named(el)}: ${Math.round(at.width)}x${Math.round(at.height)}`]
            : [];
        })
      : [],
    zooms: phone
      ? controls.flatMap((el) =>
          el.matches("input:not([type=checkbox]), select, textarea") &&
          parseFloat(getComputedStyle(el).fontSize) < 16
            ? [`${el.id || named(el)}: ${getComputedStyle(el).fontSize}`]
            : [],
        )
      : [],
  };
}
async function audit() {
  await settle(1500);
  const report: Record<string, unknown> = {
    size: [innerWidth, innerHeight],
    page: document.documentElement.scrollWidth > innerWidth + 1,
  };
  if (screenName !== "view") {
    await settle();
    report[screenName] = measure(document.body);
  } else {
    [...document.querySelectorAll("button")]
      .find((b) => b.getAttribute("aria-label") === "Memories")
      ?.click();
    await settle();
    const view = document.querySelector(".memory-view")!;
    const strip = () => [
      ...view.querySelectorAll<HTMLButtonElement>(
        ".reader-bar .memory-tabs button",
      ),
    ];
    for (const name of strip().map((b) => b.textContent!.trim())) {
      strip()
        .find((b) => b.textContent!.trim() === name)!
        .click();
      await settle();
      const body = view.querySelector(".memory-body")!;
      report[name] = measure(body);
      // Each question or form this section can open: as tall as the rest, and focus in and back out.
      for (const trigger of [...body.querySelectorAll("button")].filter((b) =>
        b.textContent!.trim().endsWith("…"),
      )) {
        const said = trigger.textContent!.trim();
        if (!trigger.isConnected) continue;
        trigger.focus();
        trigger.click();
        await settle();
        const asked = `${name} > ${said}`;
        report[asked] = {
          ...measure(body),
          focus: named(document.activeElement!),
        };
        window.dispatchEvent(
          new KeyboardEvent("keydown", { key: "Escape", bubbles: true }),
        );
        await settle();
        const back = named(document.activeElement!);
        if (back !== said) report[`${asked} back at`] = back;
      }
    }
    report.bar = measure(view.querySelector(".reader-bar")!);
    strip()[0].click();
    await settle();
    view.querySelector<HTMLButtonElement>(".memory-listed > button")?.click();
    await settle();
    report.opened = measure(view.querySelector(".memory-body")!);
    report.heights = {
      reader: Math.round(view.getBoundingClientRect().height),
      body: Math.round(
        view.querySelector(".memory-body")!.getBoundingClientRect().height,
      ),
    };
  }
  const out = document.createElement("pre");
  out.id = "audit";
  out.textContent = JSON.stringify(report);
  document.body.append(out);
}
if (params.has("audit")) void audit();
