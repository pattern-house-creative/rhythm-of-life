import { act, render, screen } from "@testing-library/react";
import { it, expect, vi } from "vitest";
import App from "../src/App";
import { sampleHealth, withSampleHealth } from "./sampleHealth";
import { loadObservatory } from "../src/apiClient";
import type * as Updates from "../src/appUpdates";

// "Try again" reloads past the service worker (tests/app-updates.test.ts);
// here, only that the app asks for that reload.
const reloads = vi.hoisted(() => vi.fn(async () => {}));
vi.mock("../src/appUpdates", async (original) => ({
  ...(await original<typeof Updates>()),
  reloadFromNetwork: reloads,
}));

it("cannot resurrect a hidden page with an old in-flight response", async () => {
  let finish!: (value: Response) => void;
  vi.mocked(fetch).mockImplementation(
    () =>
      new Promise((resolve) => {
        finish = resolve;
      }),
  );
  render(<App />);
  act(() => window.dispatchEvent(new Event("pagehide")));
  await act(async () =>
    finish(
      new Response(JSON.stringify(sampleHealth), {
        headers: { "content-type": "application/json" },
      }),
    ),
  );
  expect(
    screen.queryByRole("button", { name: /Glucose 112/ }),
  ).not.toBeInTheDocument();
  expect(screen.getByText("Opening Rhythm of Life")).toBeInTheDocument();
});

it("clears a restored session when Vercel returns its HTML login page", async () => {
  withSampleHealth();
  render(<App />);
  await screen.findByRole("button", { name: /Glucose 112/ });
  act(() => window.dispatchEvent(new Event("pagehide")));
  vi.mocked(fetch).mockResolvedValue(
    new Response("Sign in", { headers: { "content-type": "text/html" } }),
  );
  act(() => window.dispatchEvent(new Event("pageshow")));
  await screen.findByText(/Access or source unavailable/);
  expect(
    screen.queryByRole("button", { name: /Glucose 112/ }),
  ).not.toBeInTheDocument();
  // Its way back reaches the front door, which can ask to sign in again.
  act(() => screen.getByRole("button", { name: "Try again" }).click());
  expect(reloads).toHaveBeenCalledOnce();
});

it("takes a front door sending reads to its sign-in as access lost", async () => {
  withSampleHealth();
  render(<App />);
  await screen.findByRole("button", { name: /Glucose 112/ });
  act(() => window.dispatchEvent(new Event("pagehide")));
  // What a redirect looks like to a read that doesn't follow it (an access
  // proxy whose session ended).
  vi.mocked(fetch).mockResolvedValue({
    type: "opaqueredirect",
    status: 0,
    ok: false,
    headers: new Headers(),
  } as Response);
  act(() => window.dispatchEvent(new Event("pageshow")));
  await screen.findByText(/Access or source unavailable/);
  expect(screen.getByRole("button", { name: "Try again" })).toBeInTheDocument();
});

it("bounds requests and keeps a source network failure separate from authentication loss", async () => {
  const listener = vi.fn();
  window.addEventListener("observatory-access-lost", listener);
  vi.mocked(fetch).mockRejectedValue(new Error("network timeout"));
  await expect(loadObservatory()).rejects.toThrow();
  expect(listener).not.toHaveBeenCalled();
  expect(fetch).toHaveBeenCalledWith(
    "/api/observatory",
    expect.objectContaining({
      signal: expect.any(AbortSignal),
      cache: "no-store",
      redirect: "manual",
      credentials: "same-origin",
    }),
  );
  window.removeEventListener("observatory-access-lost", listener);
});
