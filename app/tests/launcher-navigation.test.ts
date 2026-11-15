import { describe, expect, it } from "vitest";
import { launcherTarget } from "../src/launcherNavigation";

describe("native launcher navigation", () => {
  it("reserves the built-in glucose view without accepting arbitrary built-ins", () => {
    expect(launcherTarget("#rhythm-launcher=builtin:glucose")).toEqual({
      kind: "builtin",
      id: "glucose",
    });
    expect(launcherTarget("#rhythm-launcher=builtin:other")).toBeNull();
    expect(launcherTarget("#rhythm-launcher=dashboard:glucose")).toEqual({
      kind: "dashboard",
      id: "glucose",
    });
  });
  it("decodes a star exactly once without treating its ID as a URL", () => {
    expect(launcherTarget("#rhythm-launcher=star:drive%3Aa%2Fb%23c")).toEqual({
      kind: "star",
      id: "drive:a/b#c",
    });
  });
  it("opens dashboards without a layout mutation instruction", () => {
    expect(launcherTarget("#rhythm-launcher=dashboard:abc")).toEqual({
      kind: "dashboard",
      id: "abc",
    });
    expect(launcherTarget("#rhythm-launcher=layout:abc")).toBeNull();
  });
  it("rejects malformed, oversized, empty and control-character targets", () => {
    for (const hash of [
      "",
      "#rhythm-launcher=star:",
      "#rhythm-launcher=star:%ZZ",
      "#rhythm-launcher=star:%00x",
      "#rhythm-launcher=star:" + "a".repeat(1001),
    ]) {
      expect(launcherTarget(hash)).toBeNull();
    }
  });
});
