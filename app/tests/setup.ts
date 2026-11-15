import "@testing-library/jest-dom/vitest";
import { afterEach, beforeEach, vi } from "vitest";
import { cleanup, configure } from "@testing-library/react";
// Cold lazy imports and API validation need a realistic bounded wait on two-core CI;
// hosted builders can run the whole-app tests ten times slower than a laptop.
configure({ asyncUtilTimeout: process.env.CI ? 15000 : 5000 });
afterEach(cleanup);
Object.defineProperty(window, "matchMedia", {
  value: vi.fn().mockImplementation(() => ({
    matches: false,
    addListener: vi.fn(),
    removeListener: vi.fn(),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  })),
});
globalThis.ResizeObserver = class {
  observe() {}
  unobserve() {}
  disconnect() {}
};

Object.defineProperty(window, "scrollTo", { value: vi.fn(), writable: true });

// Exercise the same handlers as the Node server and the dev server, without networking.
import { handleObservatory } from "../server/routes";
import { handleGraph } from "../server/graph";
import { resetEngine } from "../server/engine";
import { resetSettings } from "../server/config";
import { setInstance, demoInstance } from "../src/instance";
beforeEach(() => {
  // A machine running the tests may have an engine configured. Tests never select one unless
  // they ask for it (tests/engine.ts): no settings file, no token, no engine URL.
  for (const key of Object.keys(process.env))
    if (key.startsWith("RHYTHM_")) vi.stubEnv(key, undefined);
  vi.stubEnv("RHYTHM_CONFIG", "/nonexistent/rhythm.yaml");
  vi.stubEnv("RHYTHM_APP_TOKEN_FILE", "/nonexistent/app_token");
  resetSettings();
  resetEngine();
  setInstance(demoInstance);
  // Node has a BroadcastChannel a browser test would not: the demo's screens stay off unless a
  // test turns them on (tests/screens-demo.test.tsx).
  vi.stubGlobal("BroadcastChannel", undefined);
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init?: RequestInit) => {
      const request = new Request(new URL(url, "http://localhost"), init);
      if (url === "/api/observatory") return handleObservatory(request);
      if (url === "/api/graph") return handleGraph(request);
      throw new Error("Unexpected request");
    }),
  );
});
afterEach(() => {
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
});
