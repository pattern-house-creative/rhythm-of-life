import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeAll, expect, it, vi } from "vitest";
import App from "../src/App";
import { ResurfaceCard } from "../src/ResurfaceCard";
import { cleanMemoryAnswer, cleanPassedAnswer } from "../src/memories";
import type { MemoryClient } from "../src/memories";
import type * as Api from "../src/apiClient";

// The resurfaced memory on Home (SB-P5-17; app_memories ops resurface and resurface_dismiss): shown only while the engine serves it
// (its memory.tools on), in Home's own flow, with no popup and no new mode; dismissed for good by the person; and nothing at all
// with the setting off. Fictional data only.
const fixture = (name: string) =>
  JSON.parse(
    readFileSync(resolve(process.cwd(), "../contracts/fixtures", name), "utf8"),
  );
const picked = fixture("app_memories.resurface.example.json");
const none = fixture("app_memories.resurface.none.example.json");
const done = fixture("app_memories.resurface_dismiss.example.json");
const refused = fixture("app_memories.refused.example.json");

// The engine as the card hears it, through the facade's own checks: the answers each op gives, in order, the last repeating.
function client(
  reads: unknown[] = [picked],
  dismiss: unknown = done,
  on = true,
) {
  let read = 0;
  const call = vi.fn(async (request: Record<string, unknown>) => {
    const op = String(request.op);
    const answer =
      op === "resurface"
        ? reads[Math.min(read++, reads.length - 1)]
        : op === "resurface_dismiss"
          ? dismiss
          : none;
    if (answer instanceof Error) throw answer;
    const clean = cleanPassedAnswer(op, answer);
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
  });
  return {
    available: vi.fn(async () => on),
    call: call as unknown as MemoryClient["call"],
  } satisfies MemoryClient;
}
const card = () => screen.findByRole("region", { name: "An old memory" });

afterEach(() => {
  session.memories = undefined;
});

it("draws nothing in the demo, with the setting off, with none eligible and while no answer has come", async () => {
  const { container, rerender } = render(<ResurfaceCard resting={false} />);
  expect(container).toBeEmptyDOMElement();
  const off = client([picked], done, false);
  rerender(<ResurfaceCard client={off} resting={false} />);
  await waitFor(() => expect(off.available).toHaveBeenCalled());
  await act(async () => {});
  expect(container).toBeEmptyDOMElement();
  // Nothing is asked of an engine that doesn't serve it.
  expect(off.call).not.toHaveBeenCalled();
  // Resting behind a panel, nothing is read.
  const resting = client();
  rerender(<ResurfaceCard client={resting} resting />);
  await act(async () => {});
  expect(resting.available).not.toHaveBeenCalled();
  expect(container).toBeEmptyDOMElement();
  // Nothing old enough: no card and nothing in its place.
  const quiet = client([none]);
  rerender(<ResurfaceCard client={quiet} resting={false} />);
  await waitFor(() => expect(quiet.call).toHaveBeenCalled());
  await act(async () => {});
  expect(container).toBeEmptyDOMElement();
});

it("draws nothing when a read fails or its answer is outside the contract", async () => {
  const lost = client();
  vi.mocked(lost.available).mockRejectedValue(new Error("no answer"));
  const { container, rerender } = render(
    <ResurfaceCard client={lost} resting={false} />,
  );
  await act(async () => {});
  expect(container).toBeEmptyDOMElement();
  const wrong = client([
    { ...picked, pick: { ...picked.pick, preview: "x".repeat(281) } },
  ]);
  rerender(<ResurfaceCard client={wrong} resting={false} />);
  await waitFor(() => expect(wrong.call).toHaveBeenCalled());
  await act(async () => {});
  expect(container).toBeEmptyDOMElement();
});

it("says the memory's words and when it was written, cut when the engine cut them", async () => {
  const served = client();
  const { unmount } = render(<ResurfaceCard client={served} resting={false} />);
  const shown = await card();
  expect(served.call).toHaveBeenCalledWith(
    { op: "resurface" },
    expect.any(AbortSignal),
  );
  expect(shown).toHaveTextContent(picked.pick.preview);
  expect(shown).not.toHaveTextContent("…");
  expect(shown.querySelector("time")).toHaveAttribute(
    "datetime",
    picked.pick.createdAt,
  );
  expect(shown).toHaveTextContent("2026");
  unmount();
  render(
    <ResurfaceCard
      client={client([
        { ...picked, pick: { ...picked.pick, truncated: true } },
      ])}
      resting={false}
    />,
  );
  expect(await card()).toHaveTextContent(`${picked.pick.preview}…`);
});

it("opens its words in place, with the day it was written, and closes again", async () => {
  const user = userEvent.setup();
  render(<ResurfaceCard client={client()} resting={false} />);
  const shown = await card();
  const line = within(shown).getAllByRole("button")[0];
  expect(line).toHaveAttribute("aria-expanded", "false");
  // Closed, a line: its month and its words, cut by the page's width.
  expect(line).toHaveTextContent(picked.pick.preview);
  expect(shown.querySelector("p")).toBeNull();
  await user.click(line);
  expect(line).toHaveAttribute("aria-expanded", "true");
  // Open, the words once, in place: no popup.
  expect(screen.queryByRole("dialog")).toBeNull();
  expect(shown.querySelector("p")).toHaveTextContent(picked.pick.preview);
  expect(line).not.toHaveTextContent(picked.pick.preview);
  expect(line).toHaveTextContent(/^From /);
  await user.click(line);
  expect(line).toHaveAttribute("aria-expanded", "false");
  expect(shown.querySelector("p")).toBeNull();
});

it("draws a memory's words as words, never as markup", async () => {
  const words = "DEMO: <img src=x onerror=alert(1)> and <b>bold</b>";
  render(
    <ResurfaceCard
      client={client([{ ...picked, pick: { ...picked.pick, preview: words } }])}
      resting={false}
    />,
  );
  const shown = await card();
  expect(shown).toHaveTextContent(words);
  expect(shown.querySelector("img, b")).toBeNull();
});

it("dismisses in place, and a dismissed pick never comes back to this page", async () => {
  const user = userEvent.setup();
  // The engine's next pick is another memory; the read after it, a late answer still naming the dismissed one, is not believed.
  const other = {
    ...picked,
    pick: {
      ...picked.pick,
      id: "6d3c2a10-0000-4000-8000-00000000f002",
      preview: "DEMO: the Pellow hedge wants cutting after the nesting season.",
    },
  };
  const served = client([picked, picked, other]);
  const { container } = render(
    <ResurfaceCard client={served} resting={false} />,
  );
  const shown = await card();
  await user.click(within(shown).getByRole("button", { name: "Dismiss" }));
  expect(served.call).toHaveBeenCalledWith({
    op: "resurface_dismiss",
    id: picked.pick.id,
  });
  // In place: no popup, no question, and the card is gone at once.
  expect(screen.queryByRole("dialog")).toBeNull();
  await waitFor(() => expect(container).toBeEmptyDOMElement());
  // An answer that still names it draws nothing.
  act(() => window.dispatchEvent(new Event("pageshow")));
  await waitFor(() => expect(served.call).toHaveBeenCalledTimes(3));
  await act(async () => {});
  expect(container).toBeEmptyDOMElement();
  // The next read's other memory takes its place.
  act(() => window.dispatchEvent(new Event("pageshow")));
  expect(await card()).toHaveTextContent(other.pick.preview);
});

it("keeps the card and says so when a dismissal doesn't go through, and a try again can", async () => {
  const user = userEvent.setup();
  const unavailable = {
    ok: false,
    code: "forbidden",
    reason: "DEMO: not now.",
    revision: null,
    known: [],
  };
  const served = client([picked], unavailable);
  const { container } = render(
    <ResurfaceCard client={served} resting={false} />,
  );
  const shown = await card();
  await user.click(within(shown).getByRole("button", { name: "Dismiss" }));
  expect(await within(shown).findByRole("status")).toHaveTextContent(
    "didn't go through",
  );
  expect(shown).toHaveTextContent(picked.pick.preview);
  // A read while it stands still shows it: it is not dismissed.
  act(() => window.dispatchEvent(new Event("pageshow")));
  await waitFor(() => expect(served.call).toHaveBeenCalledTimes(3));
  await act(async () => {});
  expect(container).not.toBeEmptyDOMElement();
  // Trying again, it goes.
  vi.mocked(served.call).mockImplementationOnce((async () => ({
    available: true,
    ok: true,
    ...done,
  })) as unknown as MemoryClient["call"]);
  await user.click(within(shown).getByRole("button", { name: "Dismiss" }));
  await waitFor(() => expect(container).toBeEmptyDOMElement());
});

it("brings the card back to say so when a read took it off while a dismissal that fails was on its way", async () => {
  const user = userEvent.setup();
  const served = client();
  render(<ResurfaceCard client={served} resting={false} />);
  const shown = await card();
  let answer: (value: unknown) => void = () => {};
  vi.mocked(served.call).mockImplementationOnce(
    (() =>
      new Promise((resolve) => {
        answer = resolve;
      })) as unknown as MemoryClient["call"],
  );
  await user.click(within(shown).getByRole("button", { name: "Dismiss" }));
  act(() => window.dispatchEvent(new Event("pageshow")));
  await waitFor(() =>
    expect(screen.queryByRole("region", { name: "An old memory" })).toBeNull(),
  );
  await act(async () => answer(null));
  const back = await card();
  expect(within(back).getByRole("status")).toHaveTextContent(
    "didn't go through",
  );
  expect(within(back).getByRole("button", { name: "Dismiss" })).toBeEnabled();
});

it("starts tomorrow's pick closed and with nothing to say about today's", async () => {
  const user = userEvent.setup();
  const tomorrow = {
    ...picked,
    day: "2026-11-08",
    pick: {
      ...picked.pick,
      id: "6d3c2a10-0000-4000-8000-00000000f003",
      preview: "DEMO: the Pellow gate sticks in a wet winter.",
    },
  };
  const served = client([picked, tomorrow], {
    ok: false,
    code: "forbidden",
    reason: "DEMO: not now.",
    revision: null,
    known: [],
  });
  render(<ResurfaceCard client={served} resting={false} />);
  const shown = await card();
  await user.click(within(shown).getAllByRole("button")[0]);
  await user.click(within(shown).getByRole("button", { name: "Dismiss" }));
  await within(shown).findByRole("status");
  expect(within(shown).getAllByRole("button")[0]).toHaveAttribute(
    "aria-expanded",
    "true",
  );
  act(() => window.dispatchEvent(new Event("pageshow")));
  await waitFor(() => expect(shown).toHaveTextContent(tomorrow.pick.preview));
  expect(within(shown).queryByRole("status")).toBeNull();
  expect(within(shown).getAllByRole("button")[0]).toHaveAttribute(
    "aria-expanded",
    "false",
  );
});

it("treats a memory gone since as dismissed: there is nothing left to retire", async () => {
  const user = userEvent.setup();
  const unknown = {
    ...refused,
    code: "unknown",
    reason: "Unknown memory",
  };
  const served = client([picked], unknown);
  const { container } = render(
    <ResurfaceCard client={served} resting={false} />,
  );
  const shown = await card();
  await user.click(within(shown).getByRole("button", { name: "Dismiss" }));
  await waitFor(() => expect(container).toBeEmptyDOMElement());
});

it("keeps the card when the answer to a dismissal never comes", async () => {
  const user = userEvent.setup();
  const served = client([picked], new Error("no answer"));
  render(<ResurfaceCard client={served} resting={false} />);
  const shown = await card();
  await user.click(within(shown).getByRole("button", { name: "Dismiss" }));
  expect(await within(shown).findByRole("status")).toHaveTextContent(
    "didn't go through",
  );
});

it("reads again on coming back to Home and to the page, and the setting turned off since takes it away", async () => {
  const served = client();
  const { container, rerender } = render(
    <ResurfaceCard client={served} resting={false} />,
  );
  await card();
  expect(served.call).toHaveBeenCalledTimes(1);
  rerender(<ResurfaceCard client={served} resting />);
  rerender(<ResurfaceCard client={served} resting={false} />);
  await waitFor(() => expect(served.call).toHaveBeenCalledTimes(2));
  act(() => window.dispatchEvent(new Event("pageshow")));
  await waitFor(() => expect(served.call).toHaveBeenCalledTimes(3));
  // The setting turned off since: the engine says so, and the card goes.
  vi.mocked(served.call).mockImplementation((async () => ({
    available: false,
  })) as unknown as MemoryClient["call"]);
  act(() => window.dispatchEvent(new Event("pageshow")));
  await waitFor(() => expect(container).toBeEmptyDOMElement());
});

// Home itself, on the demo's session with a client of the test's own: the card sits under the brief, in Home's flow.
const session = vi.hoisted(() => ({
  memories: undefined as MemoryClient | undefined,
}));
vi.mock("../src/apiClient", async (original) => {
  const real = await original<typeof Api>();
  return {
    ...real,
    createApiSession: (demo?: boolean) => ({
      ...real.createApiSession(demo),
      get memories() {
        return session.memories;
      },
    }),
  };
});
beforeAll(async () => {
  await import("../src/Explorer");
  await import("../src/DashboardCard");
}, 30_000);

it("is on Home above the sky while the engine serves it, and nothing of it is with it off", async () => {
  const parts = (section: HTMLElement) =>
    [...section.querySelectorAll(":scope > *")].map(
      (p) => p.className.split(" ")[0],
    );
  // No client at all, as the demo has none: what Home's sky holds.
  const before = render(<App />);
  const today = parts(
    await screen.findByRole("region", { name: "Knowledge constellation" }),
  );
  before.unmount();
  // Off: asked, and nothing drawn; the sky's section holds what it held.
  session.memories = client([picked], done, false);
  const { container, unmount } = render(<App />);
  const sky = await screen.findByRole("region", {
    name: "Knowledge constellation",
  });
  await waitFor(() => expect(session.memories!.available).toHaveBeenCalled());
  await act(async () => {});
  expect(screen.queryByRole("region", { name: "An old memory" })).toBeNull();
  expect(container.querySelector(".resurface-card")).toBeNull();
  expect(parts(sky)).toEqual(today);
  expect(session.memories!.call).not.toHaveBeenCalled();
  unmount();
  // On: in the sky's section, ahead of the sky, and Home's corners as they were; dismissing it leaves the section as it was.
  session.memories = client();
  const user = userEvent.setup();
  render(<App />);
  const onSky = await screen.findByRole("region", {
    name: "Knowledge constellation",
  });
  const shown = await within(onSky).findByRole("region", {
    name: "An old memory",
  });
  expect(shown.parentElement).toBe(onSky);
  expect(parts(onSky).slice(-today.length)).toEqual(today);
  expect(await screen.findByTitle("Week at a glance card")).toBeInTheDocument();
  await user.click(within(shown).getByRole("button", { name: "Dismiss" }));
  expect(screen.queryByRole("dialog")).toBeNull();
  await waitFor(() =>
    expect(
      within(onSky).queryByRole("region", { name: "An old memory" }),
    ).toBeNull(),
  );
  expect(parts(onSky)).toEqual(today);
});

it("rebuilds the facade's answers from the contract", () => {
  expect(cleanMemoryAnswer("resurface", picked)).toEqual({
    ok: true,
    day: picked.day,
    pick: picked.pick,
  });
  expect(cleanMemoryAnswer("resurface", none)).toEqual({
    ok: true,
    day: none.day,
    pick: null,
  });
  // A field the contract doesn't name never passes.
  expect(
    cleanMemoryAnswer("resurface", {
      ...picked,
      pick: { ...picked.pick, hold: "instruction", extra: "DEMO" },
    }),
  ).toEqual({ ok: true, day: picked.day, pick: picked.pick });
  // A preview longer than it carries, however it counts, a day that isn't one, a pick of no kind, no pick at all.
  expect(
    cleanMemoryAnswer("resurface", {
      ...picked,
      pick: { ...picked.pick, preview: "x".repeat(280) },
    }),
  ).toMatchObject({ ok: true });
  expect(
    cleanMemoryAnswer("resurface", {
      ...picked,
      pick: { ...picked.pick, preview: "😀".repeat(280) },
    }),
  ).toMatchObject({ ok: true });
  for (const bad of [
    { ...picked, pick: { ...picked.pick, preview: "x".repeat(281) } },
    { ...picked, pick: { ...picked.pick, preview: "" } },
    { ...picked, pick: { ...picked.pick, id: "not-an-id" } },
    { ...picked, pick: { ...picked.pick, createdAt: "yesterday" } },
    { ...picked, day: "7 November" },
    { ...picked, pick: "DEMO" },
    { ok: true, day: picked.day },
  ])
    expect(cleanMemoryAnswer("resurface", bad)).toBeNull();
  expect(cleanMemoryAnswer("resurface_dismiss", done)).toEqual(done);
  for (const bad of [
    { ...done, op: "dismiss" },
    { ...done, id: "x" },
    { ...done, unchanged: "no" },
  ])
    expect(cleanMemoryAnswer("resurface_dismiss", bad)).toBeNull();
  // A refusal passes on as a refusal.
  expect(cleanMemoryAnswer("resurface", refused)).toMatchObject({ ok: false });
  expect(cleanMemoryAnswer("resurface_dismiss", refused)).toMatchObject({
    ok: false,
  });
});
