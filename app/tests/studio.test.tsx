import {
  act,
  fireEvent,
  render,
  renderHook,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { Studio } from "../src/Studio";
import {
  accepts,
  requestId,
  studioCall,
  studioSession,
  StudioAccessError,
  StudioGoneError,
} from "../src/studioProtocol";
import type { StudioSession } from "../src/studioProtocol";
import {
  runDashboardControl,
  undoDashboardSelection,
  useDashboardSelection,
} from "../src/dashboardControls";
import { createDemoDashboards } from "../src/demoDashboards";
import { handleStudio } from "../server/studio";
import { acquireScreenRunner } from "../src/screenRunner";

const frames = vi.hoisted(() => new Map<number, Record<string, unknown>>());
vi.mock("../src/PageFrame", () => ({
  PageFrame: (props: Record<string, unknown>) => {
    const d = props.dashboard as { version: number; html: string };
    frames.set(d.version, props);
    return <div data-testid={`preview-${d.version}`}>{d.html}</div>;
  },
}));
vi.mock("../src/PartsGrid", () => ({
  PartsGrid: ({
    parts,
    render: draw,
  }: {
    parts: unknown[];
    render: (p: unknown, fold: string) => unknown;
  }) => (
    <>
      {parts.map((p, i) => (
        <div key={i}>{draw(p, "saved") as never}</div>
      ))}
    </>
  ),
}));
const sid = "aaaaaaaa-0000-4000-8000-000000000001",
  screenId = "screen_1234567890";
const draft = (html: string) => ({
  title: "Training plan",
  description: "Synthetic",
  html,
  feeds: [],
  writes: [],
  note: "Received snapshot",
  stars: [],
});
let state: StudioSession, calls: Record<string, unknown>[];
beforeEach(() => {
  frames.clear();
  calls = [];
  state = {
    id: sid,
    screen: screenId,
    dashboard: null,
    revision: 1,
    valid_revision: 0,
    status: "active",
    candidate: draft("Actual received code"),
    preview: null,
    controls: [],
    preview_controls: [],
    events: [{ seq: 1, op: "update", revision: 1, at: "2026-11-10T12:00:00Z" }],
    feedback: [],
  };
  Object.defineProperty(HTMLDialogElement.prototype, "showModal", {
    configurable: true,
    value: function (this: HTMLDialogElement) {
      this.setAttribute("open", "");
    },
  });
  Object.defineProperty(HTMLDialogElement.prototype, "close", {
    configurable: true,
    value: function (this: HTMLDialogElement) {
      this.removeAttribute("open");
    },
  });
  vi.stubGlobal(
    "fetch",
    vi.fn(async (_url, init) => {
      const args = JSON.parse(init.body);
      calls.push(args);
      if (args.op === "data")
        return Response.json({
          value: { asOf: "2026-11-10T12:00:00Z", feeds: {} },
        });
      if (args.op === "validate" && args.payload.ok) {
        state = {
          ...state,
          valid_revision: state.revision,
          preview: state.candidate,
        };
      }
      if (args.op === "feedback")
        state = {
          ...state,
          feedback: [
            ...state.feedback,
            {
              seq: state.feedback.length + 1,
              text: args.payload.text,
              at: "2026-11-10T12:00:00Z",
            },
          ],
        };
      if (args.op === "cancel" || args.op === "discard")
        state = {
          ...state,
          status: args.op === "cancel" ? "canceled" : "discarded",
        };
      if (args.op === "save")
        return Response.json({ value: { id: sid, version: 1 } });
      return Response.json({ value: structuredClone(state) });
    }),
  );
});
afterEach(() => {
  vi.useRealTimers();
});
const draw = () =>
  render(
    <Studio
      screen={screenId}
      enabled
      parts={{ renderPart: () => null }}
      saved={vi.fn()}
    />,
  );
it("removes cached draft code and preview when access is revoked", async () => {
  draw();
  await screen.findByText("Actual received code");
  vi.stubGlobal(
    "fetch",
    vi.fn(async () =>
      Response.json(
        { error: "Studio sources are no longer readable", code: "forbidden" },
        { status: 403 },
      ),
    ),
  );
  act(() => window.dispatchEvent(new Event("rhythm:studio")));
  await waitFor(() =>
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument(),
  );
  expect(screen.queryByText("Actual received code")).not.toBeInTheDocument();
});
it("opens a remotely saved version and follows the existing screen runner", async () => {
  const saved = vi.fn();
  render(
    <Studio
      requireRunner
      screen={screenId}
      enabled
      parts={{ renderPart: () => null }}
      saved={saved}
    />,
  );
  expect(calls).toHaveLength(0);
  let release: () => void = () => {};
  act(() => {
    release = acquireScreenRunner(screenId);
  });
  try {
    await screen.findByText("Actual received code");
    state = { ...state, status: "saved", result: { id: sid, version: 1 } };
    act(() => window.dispatchEvent(new Event("rhythm:studio")));
    await waitFor(() => expect(saved).toHaveBeenCalledWith(sid));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  } finally {
    act(() => release());
  }
});
it("shows received code, validates exact revision, rejects preview writes and keeps a good preview across a bad candidate", async () => {
  draw();
  await screen.findByText("Actual received code");
  expect(
    screen.getByRole("button", { name: "Save", exact: true }),
  ).toBeDisabled();
  fireEvent.click(screen.getByRole("button", { name: "Watch code" }));
  expect(screen.getByLabelText("Received code")).toHaveTextContent(
    "Actual received code",
  );
  const reply = vi.fn();
  (frames.get(1)!.onWrite as Function)("log", {}, reply);
  expect(reply).toHaveBeenCalledWith(expect.objectContaining({ ok: false }));
  await act(async () => {
    (frames.get(1)!.onDrawn as Function)();
  });
  await waitFor(() =>
    expect(
      screen.getByRole("button", { name: "Save", exact: true }),
    ).toBeEnabled(),
  );
  state = {
    ...state,
    revision: 2,
    candidate: draft("Bad received candidate"),
    events: [
      ...state.events,
      { seq: 2, op: "update", revision: 2, at: "2026-11-10T12:00:01Z" },
    ],
  };
  act(() => window.dispatchEvent(new Event("rhythm:studio")));
  await screen.findByTestId("preview-2");
  await act(async () => {
    (frames.get(2)!.onFail as Function)("Syntax error");
  });
  expect(screen.getByTestId("preview-1")).toBeInTheDocument();
  expect(screen.queryByTestId("preview-2")).not.toBeInTheDocument();
  expect(
    screen.getByRole("button", { name: "Save", exact: true }),
  ).toBeDisabled();
});
it("recovers the persisted good preview, queues steering and fences cancellation", async () => {
  state = {
    ...state,
    revision: 2,
    valid_revision: 1,
    preview: draft("Last good preview"),
    candidate: draft("New candidate"),
  };
  draw();
  await screen.findByTestId("preview-1");
  await act(async () => {
    (frames.get(1)!.onDrawn as Function)();
  });
  expect(screen.getByText("Showing validated preview 1")).toBeInTheDocument();
  fireEvent.change(screen.getByLabelText("Steer your assistant"), {
    target: { value: "Recovery first" },
  });
  fireEvent.click(screen.getByRole("button", { name: "Send feedback" }));
  await waitFor(() => expect(state.feedback[0]?.text).toBe("Recovery first"));
  fireEvent.click(screen.getByRole("button", { name: "Cancel", exact: true }));
  await waitFor(() => expect(state.status).toBe("canceled"));
  const count = calls.filter((c) => c.op === "validate").length;
  await act(async () => {
    (frames.get(2)!.onDrawn as Function)();
  });
  expect(calls.filter((c) => c.op === "validate")).toHaveLength(count);
});
it("accepts only bounded declared selection values", () => {
  expect(() =>
    studioSession({ ...state, controls: { unexpected: true } }),
  ).toThrow("Invalid Studio state");
  expect(
    accepts({ id: "week", label: "Week", kind: "week", initial: 0 }, 1),
  ).toBe(true);
  expect(
    accepts({ id: "week", label: "Week", kind: "week", initial: 0 }, "1"),
  ).toBe(false);
  expect(
    accepts(
      {
        id: "tab",
        label: "Tab",
        kind: "tab",
        options: ["Plan"],
        initial: "Plan",
      },
      "document.body.remove()",
    ),
  ).toBe(false);
  expect(
    accepts(
      { id: "dates", label: "Dates", kind: "date_range", initial: {} },
      { from: "2026-02-30", to: "2026-03-10" },
    ),
  ).toBe(false);
});
it("requires actual frame acknowledgement and guards selection revisions", async () => {
  const board = createDemoDashboards().get(
    createDemoDashboards().list()[0].id,
  )!;
  vi.stubGlobal(
    "fetch",
    vi.fn(async () =>
      Response.json({
        value: [{ id: "week", label: "Week", kind: "week", initial: 0 }],
      }),
    ),
  );
  const { result: selected } = renderHook(() =>
    useDashboardSelection(board, true),
  );
  await waitFor(() => expect(selected.current.ui).not.toBeNull());
  let result: Awaited<ReturnType<typeof runDashboardControl>> | undefined;
  const args = {
    dashboard: board.id,
    version: board.version,
    base: 0,
    control: "week",
    value: 1,
  };
  let pending: Promise<unknown>;
  act(() => {
    pending = runDashboardControl("dashboard_action", args).then((r) => {
      result = r;
    });
  });
  expect(result).toBeUndefined();
  await act(async () => {
    selected.current.acknowledge(1, true);
    await pending;
  });
  expect(result?.result).toBe("shown");
  expect((await runDashboardControl("dashboard_action", args)).result).toBe(
    "failed",
  );
  expect(
    (await runDashboardControl("dashboard_controls", args)).detail
      .selectionRevision,
  ).toBe(1);
  const undo = result!.undo!;
  let reverted: Awaited<ReturnType<typeof undoDashboardSelection>> | undefined;
  act(() => {
    pending = undoDashboardSelection(undo).then((r) => {
      reverted = r;
    });
  });
  await act(async () => {
    selected.current.acknowledge(2, true);
    await pending;
  });
  expect(reverted?.detail.values).toEqual({ week: 0 });
  expect((await undoDashboardSelection(undo)).result).toBe("failed");
});
it("refuses browser attempts to submit code and enforces origin checks", async () => {
  const request = (body: unknown, origin = "http://localhost") =>
    new Request("http://localhost/api/screens?studio=1", {
      method: "POST",
      headers: { "Content-Type": "application/json", Origin: origin },
      body: JSON.stringify(body),
    });
  expect(
    (
      await handleStudio(
        request({
          op: "update",
          screen: screenId,
          draft: { html: "arbitrary" },
        }),
      )
    ).status,
  ).toBe(400);
  expect(
    (
      await handleStudio(
        request({ op: "active", screen: screenId }, "http://evil.example"),
      )
    ).status,
  ).toBe(403);
});

it("lets go when the session is canceled elsewhere or expires, and goes back to looking for a new one", async () => {
  draw();
  await screen.findByText("Actual received code");
  // The engine reads an expired session as canceled.
  state = { ...state, status: "canceled" };
  act(() => window.dispatchEvent(new Event("rhythm:studio")));
  await waitFor(() =>
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument(),
  );
  calls = [];
  act(() => window.dispatchEvent(new Event("rhythm:studio")));
  await waitFor(() => expect(calls.at(-1)?.op).toBe("active"));
  expect(calls.some((c) => c.op === "read")).toBe(false);
  // The ended session itself never opens again.
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
});
it.each([409, 404, 410])(
  "closes and stops reading a session the engine no longer has (%i), as when its dashboard was deleted",
  async (status) => {
    draw();
    await screen.findByText("Actual received code");
    vi.stubGlobal(
      "fetch",
      vi.fn(async (_url, init) => {
        const args = JSON.parse(init.body);
        calls.push(args);
        return args.op === "read"
          ? Response.json(
              { error: "Unknown Studio session", code: "conflict" },
              { status },
            )
          : Response.json({ value: null });
      }),
    );
    act(() => window.dispatchEvent(new Event("rhythm:studio")));
    await waitFor(() =>
      expect(screen.queryByRole("dialog")).not.toBeInTheDocument(),
    );
    expect(screen.queryByText(/Reconnecting/)).not.toBeInTheDocument();
    calls = [];
    act(() => window.dispatchEvent(new Event("rhythm:studio")));
    await waitFor(() => expect(calls.at(-1)?.op).toBe("active"));
    expect(calls.some((c) => c.op === "read")).toBe(false);
  },
);
it("looks for a session once a minute while none is open, reads an open one every 1.5 s, and backs off when reads fail", async () => {
  vi.useFakeTimers();
  let open = false,
    failing = false;
  vi.stubGlobal(
    "fetch",
    vi.fn(async (_url, init) => {
      const args = JSON.parse(init.body);
      calls.push(args);
      if (failing)
        return new Response("<html>Bad gateway</html>", {
          status: 502,
          headers: { "content-type": "text/html" },
        });
      if (args.op === "data")
        return Response.json({
          value: { asOf: "2026-11-10T12:00:00Z", feeds: {} },
        });
      return Response.json({ value: open ? structuredClone(state) : null });
    }),
  );
  const looks = () => calls.filter((c) => c.op === "active").length,
    reads = () => calls.filter((c) => c.op === "read").length;
  draw();
  await act(() => vi.advanceTimersByTimeAsync(0));
  expect(looks()).toBe(1);
  await act(() => vi.advanceTimersByTimeAsync(59_000));
  expect(looks()).toBe(1);
  await act(() => vi.advanceTimersByTimeAsync(1_000));
  expect(looks()).toBe(2);
  // An agent begins a session: its open_studio command wakes Studio at once.
  open = true;
  await act(async () => {
    window.dispatchEvent(new Event("rhythm:studio"));
    await vi.advanceTimersByTimeAsync(0);
  });
  expect(looks()).toBe(3);
  expect(screen.getByRole("dialog")).toBeInTheDocument();
  await act(() => vi.advanceTimersByTimeAsync(1_500));
  expect(reads()).toBe(1);
  await act(() => vi.advanceTimersByTimeAsync(1_500));
  expect(reads()).toBe(2);
  // A deploy restarts the app's server: 3 s, then 6 s between reads, the preview kept.
  failing = true;
  await act(() => vi.advanceTimersByTimeAsync(1_500));
  expect(reads()).toBe(3);
  expect(screen.getByText(/Reconnecting/)).toBeInTheDocument();
  expect(screen.getByRole("dialog")).toBeInTheDocument();
  await act(() => vi.advanceTimersByTimeAsync(2_999));
  expect(reads()).toBe(3);
  await act(() => vi.advanceTimersByTimeAsync(1));
  expect(reads()).toBe(4);
  await act(() => vi.advanceTimersByTimeAsync(5_999));
  expect(reads()).toBe(4);
  await act(() => vi.advanceTimersByTimeAsync(1));
  expect(reads()).toBe(5);
  failing = false;
  await act(() => vi.advanceTimersByTimeAsync(12_000));
  expect(reads()).toBe(6);
  expect(screen.queryByText(/Reconnecting/)).not.toBeInTheDocument();
  await act(() => vi.advanceTimersByTimeAsync(1_500));
  expect(reads()).toBe(7);
});
it("gives up a Studio read that doesn't answer, and looks again later", async () => {
  vi.useFakeTimers();
  vi.stubGlobal(
    "fetch",
    vi.fn((_url, init: RequestInit) => {
      calls.push(JSON.parse(String(init.body)));
      return new Promise((_, reject) =>
        init.signal!.addEventListener("abort", () =>
          reject(init.signal!.reason),
        ),
      );
    }),
  );
  draw();
  await act(() => vi.advanceTimersByTimeAsync(0));
  expect(calls).toHaveLength(1);
  const signal = vi.mocked(fetch).mock.calls[0][1]!.signal!;
  expect(signal.aborted).toBe(false);
  await act(() => vi.advanceTimersByTimeAsync(15_000));
  expect(signal.aborted).toBe(true);
  await act(() => vi.advanceTimersByTimeAsync(60_000));
  expect(calls).toHaveLength(2);
});
it("reads a Studio answer's status before its body", async () => {
  const answer = (body: string, status: number, type = "text/html") =>
    vi.stubGlobal(
      "fetch",
      vi.fn(
        async () =>
          new Response(body, { status, headers: { "content-type": type } }),
      ),
    );
  // A front door's sign-in page is access ended, not a broken answer.
  answer("<html>Sign in</html>", 401);
  await expect(
    studioCall({ op: "active", screen: screenId }),
  ).rejects.toBeInstanceOf(StudioAccessError);
  answer("<html>Bad gateway</html>", 502);
  await expect(studioCall({ op: "active", screen: screenId })).rejects.toThrow(
    "Studio response unavailable",
  );
  const unknown = JSON.stringify({
    error: "Unknown Studio session",
    code: "conflict",
  });
  answer(unknown, 409, "application/json");
  await expect(
    studioCall({ op: "read", screen: screenId, sessionId: sid }),
  ).rejects.toBeInstanceOf(StudioGoneError);
  // A refused change is said as the engine said it, and the session may still be read.
  answer(
    JSON.stringify({ error: "Draft revision changed; read Studio again" }),
    409,
    "application/json",
  );
  const refused = studioCall({ op: "validate", screen: screenId });
  await expect(refused).rejects.toThrow("Draft revision changed");
  await expect(refused).rejects.not.toBeInstanceOf(StudioGoneError);
});

it("makes its request ids without crypto.randomUUID, as on a screen served over plain http", async () => {
  const uuid =
    /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
  const real = globalThis.crypto;
  vi.stubGlobal("crypto", {
    getRandomValues: real.getRandomValues.bind(real),
  });
  const made = new Set(Array.from({ length: 50 }, requestId));
  expect(made.size).toBe(50);
  for (const id of made) expect(id).toMatch(uuid);
  draw();
  await screen.findByText("Actual received code");
  fireEvent.change(screen.getByLabelText("Steer your assistant"), {
    target: { value: "Calmer colours" },
  });
  fireEvent.click(screen.getByRole("button", { name: "Send feedback" }));
  await waitFor(() => expect(state.feedback[0]?.text).toBe("Calmer colours"));
  expect(calls.find((c) => c.op === "feedback")?.requestId).toMatch(uuid);
});
