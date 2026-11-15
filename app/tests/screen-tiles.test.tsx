import { afterEach, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import App from "../src/App";
import { ScreenName } from "../src/Details";
import {
  adoptScreenName,
  cleanTiles,
  defaultScreenName,
  defaultTiles,
  demoScreenIdentity,
  deviceName,
  followHome,
  saveScreenName,
  screenIdentity,
  swapTiles,
  watchScreenName,
  watchSettings,
} from "../src/screens";

// Where each Home tile sits, per screen (docs/dev/screens.md section 6), and this browser's
// screen identity. Fictional devices only.
afterEach(() => {
  localStorage.clear();
  sessionStorage.clear();
  vi.restoreAllMocks();
});
const wide = () => {
  const narrow = vi.mocked(window.matchMedia).getMockImplementation()!;
  vi.mocked(window.matchMedia).mockImplementation(
    (query: string) =>
      ({
        matches: query.includes("min-width: 1100px"),
        media: query,
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
      }) as unknown as MediaQueryList,
  );
  return () => vi.mocked(window.matchMedia).mockImplementation(narrow);
};

it("swaps two tiles' places and checks a map", () => {
  expect(swapTiles(defaultTiles, "recovery", "main")).toEqual({
    main: "recovery",
    recovery: "main",
    training: "training",
    changed: "changed",
  });
  // Whatever sat in the place takes the old one; a tile already there stays.
  const moved = swapTiles(
    swapTiles(defaultTiles, "recovery", "main"),
    "changed",
    "recovery",
  );
  expect(moved).toEqual({
    main: "changed",
    recovery: "main",
    training: "training",
    changed: "recovery",
  });
  expect(swapTiles(moved, "training", "training")).toBe(moved);
  expect(cleanTiles(moved)).toEqual(moved);
  for (const bad of [
    null,
    { ...defaultTiles, main: "recovery" },
    { main: "main", recovery: "recovery", training: "training" },
    { ...defaultTiles, sky: "sky" },
  ])
    expect(cleanTiles(bad)).toBeNull();
});

it("places each tile where the map says on a wide screen, and nowhere new by default", async () => {
  const restore = wide();
  const { container, unmount } = render(<App />);
  await screen.findByTitle("Week at a glance card");
  const zones = () =>
    [".main-zone", ".recovery-zone", ".context-zone"].map(
      (z) => container.querySelector<HTMLElement>(z)!.style.gridArea,
    );
  // The default map changes nothing on screen.
  expect(zones()).toEqual(["", "", ""]);
  expect(container.querySelector(".observatory")).toHaveAttribute(
    "data-empty",
    "training",
  );
  unmount();
  localStorage.setItem(
    "rhythm-of-life.tiles",
    JSON.stringify({
      main: "recovery",
      recovery: "main",
      training: "changed",
      changed: "training",
    }),
  );
  const again = render(<App />);
  const tall = await screen.findByTitle("Week at a glance card");
  const at = (z: string) =>
    again.container.querySelector<HTMLElement>(z)!.style.gridArea;
  expect([at(".main-zone"), at(".recovery-zone"), at(".context-zone")]).toEqual(
    ["recovery", "main", "training"],
  );
  // The empty training tile leaves the place it sits in now (the changed tile's, "context").
  expect(again.container.querySelector(".observatory")).toHaveAttribute(
    "data-empty",
    "changed",
  );
  // A card asks for the shape of the place it sits in: the main place's is tall.
  await waitFor(() =>
    expect(tall.getAttribute("srcdoc")).toContain('data-shape=\\"compact\\"'),
  );
  const corner = await screen.findByTitle("Sample training week card");
  await waitFor(() =>
    expect(corner.getAttribute("srcdoc")).toContain('data-shape=\\"tall\\"'),
  );
  restore();
});

it("makes this browser's screen once and keeps it; each demo tab is its own", () => {
  const first = screenIdentity();
  expect(first.id).toMatch(/^[A-Za-z0-9_-]{22}$/);
  expect(screenIdentity()).toEqual(first);
  const tab = demoScreenIdentity();
  expect(tab.id).toMatch(new RegExp(`^${first.id}-[A-Za-z0-9_-]{8}$`));
  expect(demoScreenIdentity()).toEqual(tab);
  sessionStorage.clear();
  expect(demoScreenIdentity().id).not.toBe(tab.id);
});

it("follows a rename made in another tab of this browser", () => {
  const { id } = screenIdentity();
  const renamed = vi.fn();
  const stop = watchScreenName(id, renamed);
  const stored = (value: unknown, key = "rhythm-of-life.screen") =>
    window.dispatchEvent(
      new StorageEvent("storage", { key, newValue: JSON.stringify(value) }),
    );
  stored({ id, name: " Kitchen " });
  expect(renamed).toHaveBeenLastCalledWith("Kitchen");
  // Another screen's identity, another key, or no name at all: nothing to follow.
  stored({ id: "otherScreen000000000001", name: "Hall" });
  stored({ id, name: "Hall" }, "rhythm-of-life.layout");
  stored({ id, name: "" });
  expect(renamed).toHaveBeenCalledTimes(1);
  stop();
  stored({ id, name: "Hall" });
  expect(renamed).toHaveBeenCalledTimes(1);
});

it("looks for a rename made in another tab again when the page is shown, as one kept in the back-forward cache hears no storage event", () => {
  const { id } = screenIdentity();
  const renamed = vi.fn();
  const stop = watchScreenName(id, renamed);
  const hidden = (value: boolean) =>
    Object.defineProperty(document, "hidden", { configurable: true, value });
  try {
    saveScreenName("Porch", false);
    window.dispatchEvent(new Event("pageshow"));
    expect(renamed).toHaveBeenLastCalledWith("Porch");
    // Hidden, it waits; shown again, it looks.
    saveScreenName("Hall", false);
    hidden(true);
    document.dispatchEvent(new Event("visibilitychange"));
    expect(renamed).toHaveBeenCalledTimes(1);
    hidden(false);
    document.dispatchEvent(new Event("visibilitychange"));
    expect(renamed).toHaveBeenLastCalledWith("Hall");
    // Another screen's identity is nothing to follow.
    localStorage.setItem(
      "rhythm-of-life.screen",
      JSON.stringify({ id: "otherScreen000000000001", name: "Study" }),
    );
    window.dispatchEvent(new Event("pageshow"));
    expect(renamed).toHaveBeenCalledTimes(2);
    stop();
    localStorage.setItem(
      "rhythm-of-life.screen",
      JSON.stringify({ id, name: "Study", chosen: true }),
    );
    window.dispatchEvent(new Event("pageshow"));
    expect(renamed).toHaveBeenCalledTimes(2);
  } finally {
    hidden(false);
  }
});

it("follows a layout, tiles or Home chosen in another tab of this browser", () => {
  const changed = vi.fn();
  const stop = watchSettings(changed);
  const stored = (key: string | null) =>
    window.dispatchEvent(new StorageEvent("storage", { key }));
  stored("rhythm-of-life.layout");
  stored("rhythm-of-life.tiles");
  stored("rhythm-of-life.home");
  expect(changed).toHaveBeenCalledTimes(3);
  // The screen's name, or storage cleared, is not a setting.
  stored("rhythm-of-life.screen");
  stored(null);
  expect(changed).toHaveBeenCalledTimes(3);
  stop();
  stored("rhythm-of-life.home");
  expect(changed).toHaveBeenCalledTimes(3);
});

it("shows another tab's new Home at Home, and leaves what is shown alone for a layout or tiles change", () => {
  const glance = "00000000-0000-4000-8000-00000000d0a6",
    training = "00000000-0000-4000-8000-00000000d0a5";
  const home = { id: glance, home: true },
    over = { id: training, home: false };
  // A layout or tiles change keeps the Home as it was: the sky someone went to stays the sky.
  expect(followHome(null, glance, { dashboard: glance })).toBeNull();
  expect(followHome(home, glance, { dashboard: glance })).toBe(home);
  // A new Home shows at Home (the sky or the Home dashboard), and the default Home is the sky.
  expect(followHome(null, glance, { dashboard: training })).toEqual({
    id: training,
    home: true,
  });
  expect(followHome(home, glance, null)).toBeNull();
  expect(followHome(null, null, { dashboard: glance })).toEqual(home);
  // A full screen of another dashboard stays until someone exits.
  expect(followHome(over, glance, null)).toBe(over);
});

it("shows another tab's rename in the name field, unless the name is being changed here", async () => {
  const user = userEvent.setup();
  const onRename = vi.fn(async () => true);
  const { rerender } = render(
    <ScreenName name="Kitchen" onRename={onRename} />,
  );
  const field = screen.getByRole("textbox", { name: "This screen's name" });
  await user.click(field);
  rerender(<ScreenName name="Hall" onRename={onRename} />);
  expect(field).toHaveValue("Hall");
  // Leaving the field doesn't put the old name back.
  await user.tab();
  expect(onRename).not.toHaveBeenCalled();
  // A name someone is typing here stays, and is saved when they leave the field.
  await user.clear(field);
  await user.type(field, "Porch");
  rerender(<ScreenName name="Study" onRename={onRename} />);
  expect(field).toHaveValue("Porch");
  await user.tab();
  expect(onRename).toHaveBeenLastCalledWith("Porch");
  // Counted in characters, as the engine counts them: 60 emoji fit, a 61st doesn't go in.
  const sixty = "🌿".repeat(60);
  fireEvent.change(field, { target: { value: `${sixty}🌿` } });
  expect(field).toHaveValue(sixty);
  fireEvent.submit(field.closest("form")!);
  await waitFor(() => expect(onRename).toHaveBeenLastCalledWith(sixty));
});

it("names a screen after its device", () => {
  const names: [string, number, string][] = [
    ["Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X)", 5, "iPhone"],
    ["Mozilla/5.0 (iPad; CPU OS 18_0 like Mac OS X)", 5, "iPad"],
    // An iPad asking for the desktop site reads as a Mac with a touch screen.
    ["Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)", 5, "iPad"],
    ["Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)", 0, "Mac"],
    [
      "Mozilla/5.0 (Linux; Android 15; Pixel) Mobile Safari",
      5,
      "Android phone",
    ],
    ["Mozilla/5.0 (Linux; Android 15; Tab) Safari", 5, "Android tablet"],
    ["Mozilla/5.0 (X11; CrOS x86_64 14541.0.0)", 0, "Chromebook"],
    ["Mozilla/5.0 (Windows NT 10.0; Win64; x64)", 0, "Windows PC"],
    ["Mozilla/5.0 (X11; Linux x86_64)", 0, "Linux PC"],
    ["Fictional/1.0", 0, "Browser"],
  ];
  for (const [agent, touch, name] of names)
    expect(deviceName(agent, touch)).toBe(name);
});

it("names a screen after its device and its browser", () => {
  const webkit = "AppleWebKit/537.36 (KHTML, like Gecko)";
  const names: [string, number, string][] = [
    [
      `Mozilla/5.0 (Windows NT 10.0; Win64; x64) ${webkit} Chrome/130.0.0.0 Safari/537.36`,
      0,
      "Windows PC · Chrome",
    ],
    [
      `Mozilla/5.0 (Windows NT 10.0; Win64; x64) ${webkit} Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0`,
      0,
      "Windows PC · Edge",
    ],
    [
      "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1",
      5,
      "iPhone · Safari",
    ],
    [
      "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/130.0.0.0 Mobile/15E148 Safari/604.1",
      5,
      "iPhone · Chrome",
    ],
    [
      "Mozilla/5.0 (Macintosh; Intel Mac OS X 14.0; rv:131.0) Gecko/20100101 Firefox/131.0",
      0,
      "Mac · Firefox",
    ],
    [
      `Mozilla/5.0 (Linux; Android 14; SM-S918B) ${webkit} SamsungBrowser/26.0 Chrome/122.0.0.0 Mobile Safari/537.36`,
      5,
      "Android phone · Samsung Internet",
    ],
    [
      `Mozilla/5.0 (X11; Linux x86_64) ${webkit} Chrome/130.0.0.0 Safari/537.36 OPR/115.0.0.0`,
      0,
      "Linux PC · Opera",
    ],
    // A browser it can't tell is left out; a device it can't tell leaves the browser.
    ["Mozilla/5.0 (Windows NT 10.0; Win64; x64)", 0, "Windows PC"],
    ["Fictional/1.0 Firefox/131.0", 0, "Firefox"],
    ["Fictional/1.0", 0, "Browser"],
  ];
  for (const [agent, touch, name] of names)
    expect(defaultScreenName(agent, touch)).toBe(name);
});

it("takes the engine's numbered default name, never over a name someone chose", () => {
  const first = screenIdentity();
  expect(first).toMatchObject({ name: defaultScreenName(), chosen: false });
  // Another screen had the default first, so the engine numbered this one.
  expect(adoptScreenName(first.id, `${first.name} 2`)).toBe(`${first.name} 2`);
  expect(screenIdentity()).toEqual({
    id: first.id,
    name: `${first.name} 2`,
    chosen: false,
  });
  // Not this screen's: nothing to take. The name it has already (another tab took it
  // first): it goes by it, and nothing is written again.
  expect(adoptScreenName("otherScreen000000000001", "Hall")).toBeNull();
  const setItem = vi.spyOn(Storage.prototype, "setItem");
  expect(adoptScreenName(first.id, `${first.name} 2`)).toBe(`${first.name} 2`);
  expect(setItem).not.toHaveBeenCalled();
  setItem.mockRestore();
  // A name someone chose stays, and is the one the screen goes by, as this browser keeps it
  // now (chosen in another tab, perhaps).
  saveScreenName("Kitchen", false);
  expect(adoptScreenName(first.id, `${first.name} 3`)).toBe("Kitchen");
  expect(screenIdentity()).toEqual({
    id: first.id,
    name: "Kitchen",
    chosen: true,
  });
});

it("turns a device name kept before names said so into today's default, and keeps any other", () => {
  vi.spyOn(navigator, "userAgent", "get").mockReturnValue(
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36",
  );
  const id = "kitchenScreen0000000001";
  const kept = (name: string) =>
    localStorage.setItem("rhythm-of-life.screen", JSON.stringify({ id, name }));
  kept(deviceName());
  expect(screenIdentity()).toEqual({
    id,
    name: "Windows PC · Chrome",
    chosen: false,
  });
  expect(JSON.parse(localStorage.getItem("rhythm-of-life.screen")!)).toEqual({
    id,
    name: "Windows PC · Chrome",
    chosen: false,
  });
  kept("Kitchen");
  expect(screenIdentity()).toEqual({ id, name: "Kitchen", chosen: true });
});
