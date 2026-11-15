// SB-P6-02: the card on Home and the page a bookmark opens, from the keyboard and at a phone's size: the old memory's dismiss handing
// on to what is next on Home, what is said in a place a screen reader already watches, and a bookmark's way on when a phone won't copy
// it. Fictional data only. (Home draws no brief since 2026-11-09, so the brief's card and its tests are gone.)
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import PageBookmark from "../src/PageBookmark";
import PageCapture from "../src/PageCapture";
import { ResurfaceCard } from "../src/ResurfaceCard";
import { cleanPassedAnswer } from "../src/memories";
import type { MemoryClient } from "../src/memories";

const fixture = (name: string) =>
  JSON.parse(
    readFileSync(resolve(process.cwd(), "../contracts/fixtures", name), "utf8"),
  );

// The engine as a card hears it, through the facade's own checks.
function client(answers: Record<string, unknown>, on = true): MemoryClient {
  return {
    available: vi.fn(async () => on),
    call: vi.fn(async (request: Record<string, unknown>) => {
      const op = String(request.op);
      const answer = answers[op];
      const clean = answer === undefined ? null : cleanPassedAnswer(op, answer);
      return clean
        ? { available: true, ...clean }
        : {
            available: true,
            ok: false,
            code: "unavailable",
            reason: "Memories can't be read right now. Try again later.",
            revision: null,
            known: [],
          };
    }) as unknown as MemoryClient["call"],
  };
}

const picked = fixture("app_memories.resurface.example.json");
const dismissed = fixture("app_memories.resurface_dismiss.example.json");
const refused = fixture("app_memories.refused.example.json");

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("the old memory's card", () => {
  const onHome = (memories: MemoryClient) =>
    render(
      <main>
        <button>Before</button>
        <ResurfaceCard client={memories} resting={false} />
        <button>The sky</button>
      </main>,
    );

  it("opens from the keyboard, its words a stop of their own and named, since they scroll", async () => {
    const user = userEvent.setup();
    onHome(client({ resurface: picked }));
    const card = await screen.findByRole("region", { name: "An old memory" });
    const line = within(card).getByRole("button", { expanded: false });
    line.focus();
    await user.keyboard("{Enter}");
    expect(line).toHaveAttribute("aria-expanded", "true");
    const words = within(card).getByRole("group", { name: "Its words" });
    expect(words).toHaveAttribute("tabindex", "0");
    expect(words).toHaveTextContent(picked.pick.preview);
    await user.tab();
    await user.tab();
    expect(words).toHaveFocus();
  });

  it("dismissed by keyboard goes on to what is next on Home", async () => {
    const user = userEvent.setup();
    onHome(client({ resurface: picked, resurface_dismiss: dismissed }));
    const card = await screen.findByRole("region", { name: "An old memory" });
    within(card).getByRole("button", { name: "Dismiss" }).focus();
    await user.keyboard("{Enter}");
    await waitFor(() =>
      expect(
        screen.queryByRole("region", { name: "An old memory" }),
      ).toBeNull(),
    );
    expect(screen.getByRole("button", { name: "The sky" })).toHaveFocus();
  });

  it("dismissed by a tap elsewhere moves nothing", async () => {
    onHome(client({ resurface: picked, resurface_dismiss: dismissed }));
    const card = await screen.findByRole("region", { name: "An old memory" });
    const before = screen.getByRole("button", { name: "Before" });
    before.focus();
    within(card).getByRole("button", { name: "Dismiss" }).click();
    await waitFor(() =>
      expect(
        screen.queryByRole("region", { name: "An old memory" }),
      ).toBeNull(),
    );
    expect(before).toHaveFocus();
  });

  it("says a dismissal that failed in a place a screen reader already watches, and keeps it to try again", async () => {
    const user = userEvent.setup();
    onHome(client({ resurface: picked, resurface_dismiss: refused }));
    const card = await screen.findByRole("region", { name: "An old memory" });
    const live = card.querySelector('[aria-live="polite"]');
    expect(live).toBeEmptyDOMElement();
    const dismiss = within(card).getByRole("button", { name: "Dismiss" });
    dismiss.focus();
    await user.keyboard("{Enter}");
    const said = await within(card).findByRole("status");
    expect(live).toContainElement(said);
    expect(dismiss).toBeEnabled();
  });
});

describe("the page a bookmark opens", () => {
  const page = {
    title: "DEMO: the Wendle rota",
    url: "https://example.invalid/wendle",
    text: "DEMO: the Wendle rota changes on Tuesdays.",
    cut: false,
  };
  it("says what happens in a place already watched, and a hold says so with focus on what was done", async () => {
    const memories: MemoryClient = {
      available: vi.fn(async () => true),
      call: vi.fn(async () => ({
        available: true,
        ok: true,
        written: {
          op: "capture",
          id: "6d3c2a10-0000-4000-8000-000000000001",
          revision: 1,
          to: null,
          scope: "shared",
          owner: "household",
          status: "draft",
          scopes: "off",
          project: null,
          unchanged: false,
          replayed: false,
          droppedTags: [],
          held: { reason: "instruction", signals: [] },
          trash: null,
          capsule: null,
          projectLeft: false,
          cascade: null,
        },
      })) as unknown as MemoryClient["call"],
    };
    const user = userEvent.setup();
    const { container } = render(<PageCapture page={page} client={memories} />);
    const box = await screen.findByLabelText("Memory");
    await waitFor(() => expect(box).toHaveFocus());
    const live = container.querySelector('[aria-live="polite"]');
    expect(live).toBeEmptyDOMElement();
    expect(document.querySelector('dialog, [role="dialog"]')).toBeNull();
    // Save counts only once the form has been in view a moment and the person has done something here.
    await new Promise((r) => setTimeout(r, 550));
    await user.tab();
    expect(screen.getByRole("button", { name: "Save" })).toHaveFocus();
    await user.keyboard("{Enter}");
    const heading = await screen.findByRole("heading", { name: "Saved" });
    await waitFor(() => expect(heading).toHaveFocus());
    expect(screen.getByRole("status")).toHaveTextContent(/held for review/);
    expect(screen.getByRole("status")).toHaveTextContent(
      /Open the Memories view/,
    );
    await user.tab();
    expect(
      screen.getByRole("button", { name: "Close this tab" }),
    ).toHaveFocus();
  });
});

describe("the bookmark's offer", () => {
  it("opens from the keyboard, and when a phone won't copy, gives focus to the address, selected, with a name", async () => {
    const user = userEvent.setup();
    vi.spyOn(navigator.clipboard, "writeText").mockRejectedValue(
      new Error("DEMO: refused"),
    );
    const { container } = render(
      <PageBookmark origin="https://rhythm.example.invalid" />,
    );
    // A summary answers Enter and Space in a browser, as a button does; user-event only clicks it.
    await user.click(screen.getByText("Save pages from your browser"));
    expect(container.querySelector("details")).toHaveAttribute("open");
    const live = container.querySelector('[aria-live="polite"]');
    expect(live).toBeEmptyDOMElement();

    const copy = screen.getByRole("button", { name: "Copy the bookmark" });
    copy.focus();
    await user.keyboard("{Enter}");
    const box = await screen.findByLabelText(
      "The bookmark's address, to copy by hand",
    );
    await waitFor(() => expect(box).toHaveFocus());
    expect(live).toContainElement(screen.getByRole("status"));
    expect(screen.getByRole("status")).toHaveTextContent(/select it below/);
  });
});
