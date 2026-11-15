// With every memory setting off, as every install's default, nothing of the memory work is there (story 18): no Memories
// view, no brief, no old memory, no combined search, and an AI client is offered none of the tools.
import { expect, it, vi } from "vitest";
import { act, screen, waitFor } from "@testing-library/react";
import {
  aiClient,
  appJson,
  explorer,
  home,
  inSandbox,
  journey,
  memories,
  rpc,
  tokenOf,
} from "./sandbox";

inSandbox();

// The app's reads of the memories' door since the `from`th request it made: a card or a button that waits on one is drawn
// neither way until it has answered, so its absence proves nothing before then. Waits for at least one, then for every
// one so far to have answered and the app to have drawn what they said, again while that brought more (a brief read after
// the door said on).
async function memoriesAnswered(from: number) {
  const fetched = vi.mocked(fetch);
  const reads = () =>
    fetched.mock.calls
      .map((call, i) => [String(call[0]), fetched.mock.results[i]] as const)
      .slice(from)
      .filter(([path]) => path.split("?")[0].endsWith("/api/memories"));
  await waitFor(() => expect(reads().length).toBeGreaterThan(0));
  for (let seen = -1; seen !== reads().length;) {
    seen = reads().length;
    await Promise.allSettled(reads().map(([, result]) => result?.value));
    await act(() => new Promise((done) => setTimeout(done, 300)));
  }
}

journey("every memory setting off", () => {
  it("draws Home and the explorer with nothing of the memory view, and answers no memory op", async () => {
    expect(await appJson("/api/memories")).toEqual({ available: false });
    expect(await memories({ op: "list" })).toEqual({ available: false });
    // An op the view's later work added is a request the facade does not know while the engine doesn't serve the view.
    expect(await memories({ op: "queues" })).toEqual({
      error: "Invalid request",
    });
    const found = await appJson("/api/graph", {
      operation: "recall",
      value: "anything",
    });
    expect(found.available).toBe(false);

    // Home's cards have asked the door and heard it is off: no brief, no old memory.
    let from = vi.mocked(fetch).mock.calls.length;
    const { view } = await home();
    await memoriesAnswered(from);
    expect(screen.queryByRole("region", { name: "Brief" })).toBeNull();
    expect(screen.queryByRole("region", { name: /old memory/i })).toBeNull();
    view.unmount();

    // The map still answers, and with the demo's stars; the explorer has asked the door too, and offers no Memories.
    from = vi.mocked(fetch).mock.calls.length;
    await explorer();
    expect(
      await screen.findAllByRole("button", { name: /^Explore / }),
    ).not.toHaveLength(0);
    await memoriesAnswered(from);
    expect(screen.queryByRole("button", { name: "Memories" })).toBeNull();
  });

  it("offers an AI client no memory tool, and takes none", async () => {
    const listed = await rpc(tokenOf("harness_journey-alex"), "tools/list");
    const names = listed.tools.map((t: { name: string }) => t.name);
    expect(names).toContain("save_document");
    for (const tool of [
      "remember",
      "get",
      "list_recent",
      "forget",
      "undo",
      "recall",
      "resolve",
      "brief",
      "share",
    ])
      expect(names).not.toContain(tool);
    const said = await aiClient("journey-alex", "remember", {
      content: "DEMO: a memory no tool takes while memory.tools is off.",
    }).catch((e: Error) => e.message);
    expect(JSON.stringify(said)).not.toContain('"ok":true');
  });
});
