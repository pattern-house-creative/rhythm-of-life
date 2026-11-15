import { expect, it, vi } from "vitest";
import { act, fireEvent, render, screen } from "@testing-library/react";
import { Constellation } from "../src/Constellation";
import { demoData } from "../src/demo";

function mouse(type: string, target: Element | Window, x: number, y: number) {
  const event = new MouseEvent(type, {
    clientX: x,
    clientY: y,
    bubbles: true,
    cancelable: true,
  });
  // Vitest's window proxy cannot be passed to jsdom's UIEvent constructor.
  Object.defineProperty(event, "view", { value: window });
  fireEvent(target, event);
}

it("drags nodes and pans the ambient graph without opening the explorer", () => {
  const onSelect = vi.fn();
  const { container } = render(
    <Constellation
      nodes={demoData.nodes}
      edges={demoData.edges}
      selected={null}
      onSelect={onSelect}
      interactive={false}
      healthFocus={false}
      paused
    />,
  );
  const node = screen.getByRole("button", { name: "Explore Projects" });
  const before = node.getAttribute("transform");
  mouse("mousedown", node, 300, 300);
  mouse("mousemove", window, 340, 330);
  mouse("mouseup", window, 340, 330);
  expect(node.getAttribute("transform")).not.toBe(before);
  expect(onSelect).not.toHaveBeenCalled();

  const svg = container.querySelector("svg.graph-svg")!;
  const layer = svg.querySelector(":scope > g")!;
  const transform = layer.getAttribute("transform");
  mouse("mousedown", svg, 20, 20);
  mouse("mousemove", window, 60, 50);
  mouse("mouseup", window, 60, 50);
  expect(layer.getAttribute("transform")).not.toBe(transform);
  expect(svg).toHaveAttribute("tabindex", "0");
  expect(onSelect).not.toHaveBeenCalled();
});

it("keeps the ambient sky at its laid-out size when the screen resizes, while the explorer still fits", () => {
  // Capture the sky's size observer so the test can resize it after the stars
  // have arrived, as a real page does once its tiles and fonts settle.
  const observers: ResizeObserverCallback[] = [];
  const Original = globalThis.ResizeObserver;
  globalThis.ResizeObserver = class {
    constructor(callback: ResizeObserverCallback) {
      observers.push(callback);
    }
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof ResizeObserver;
  try {
    const resize = (width: number, height: number) =>
      act(() => {
        for (const callback of observers)
          callback(
            [{ contentRect: { width, height } } as ResizeObserverEntry],
            {} as ResizeObserver,
          );
      });
    const scaleOf = (container: HTMLElement) => {
      const layer = container.querySelector("svg.graph-svg > g")!;
      const match = /scale\(([\d.]+)\)/.exec(
        layer.getAttribute("transform") ?? "",
      );
      return match ? Number(match[1]) : 1;
    };
    const props = {
      nodes: demoData.nodes,
      edges: demoData.edges,
      selected: null,
      onSelect: vi.fn(),
      healthFocus: false,
      paused: true,
    };
    // A wide, short band of sky: fitting it with margins would shrink it.
    const ambient = render(<Constellation {...props} interactive={false} />);
    resize(2448, 802);
    resize(2448, 760);
    expect(scaleOf(ambient.container)).toBe(1);
    ambient.unmount();
    const explorer = render(<Constellation {...props} interactive />);
    resize(2448, 802);
    resize(2448, 760);
    expect(scaleOf(explorer.container)).not.toBe(1);
  } finally {
    globalThis.ResizeObserver = Original;
  }
});

it("marks the explorer sky when Show all labels is on, so selection no longer hides names", async () => {
  const { container } = render(
    <Constellation
      nodes={demoData.nodes}
      edges={demoData.edges}
      selected={demoData.nodes[0].id}
      onSelect={vi.fn()}
      interactive
      healthFocus={false}
      paused
    />,
  );
  const sky = container.querySelector(".constellation")!;
  expect(sky).toHaveClass("has-focus");
  expect(sky).not.toHaveClass("all-labels");
  const toggle = screen.getByRole("button", { name: "Show all labels" });
  // A drag in an earlier test leaves d3's one-tick click guard on the window.
  await new Promise((resolve) => setTimeout(resolve, 0));
  fireEvent.click(toggle);
  expect(
    screen.getByRole("button", { name: "Show all labels" }),
  ).toHaveAttribute("aria-pressed", "true");
  expect(container.querySelector(".constellation")).toHaveClass("all-labels");
});

it("keeps the ambient sky's stars in place when a neighbouring tile changes its room by a line, and lays it out again on a real resize", () => {
  // A rotating title in Changed lately growing a line made the sky a few
  // percent shorter, and the whole sky was laid out again.
  const observers: ResizeObserverCallback[] = [];
  const Original = globalThis.ResizeObserver;
  globalThis.ResizeObserver = class {
    constructor(callback: ResizeObserverCallback) {
      observers.push(callback);
    }
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof ResizeObserver;
  try {
    const resize = (width: number, height: number) =>
      act(() => {
        for (const callback of observers)
          callback(
            [{ contentRect: { width, height } } as ResizeObserverEntry],
            {} as ResizeObserver,
          );
      });
    const stars = (container: HTMLElement) =>
      new Map(
        [...container.querySelectorAll(".graph-node")].map((n) => [
          n.getAttribute("data-id"),
          n.getAttribute("transform"),
        ]),
      );
    const sky = render(
      <Constellation
        nodes={demoData.nodes}
        edges={demoData.edges}
        selected={null}
        onSelect={vi.fn()}
        healthFocus={false}
        paused
        interactive={false}
      />,
    );
    const svg = () => sky.container.querySelector("svg.graph-svg")!;
    resize(1600, 700);
    const before = stars(sky.container);
    // Six percent shorter: the frame follows, the stars stay.
    resize(1600, 660);
    expect(svg().getAttribute("viewBox")).toBe(`0 0 820 ${(820 * 660) / 1600}`);
    const after = stars(sky.container);
    const kept = [...before].filter(([id, at]) => after.get(id) === at).length;
    expect(kept).toBeGreaterThanOrEqual(Math.floor(before.size * 0.9));
    // A real resize (a third shorter) lays the sky out for its new room.
    resize(1600, 460);
    const resized = stars(sky.container);
    expect(
      [...before].filter(([id, at]) => resized.get(id) === at).length,
    ).toBeLessThan(kept);
  } finally {
    globalThis.ResizeObserver = Original;
  }
});

it("opens the explorer on the ambient sky: every star keeps its share of the sky's width and height", () => {
  // The explorer used to lay out its own sky for its taller frame, so a star on
  // the far right of the ambient sky could open at the top in the middle.
  const observers: ResizeObserverCallback[] = [];
  const Original = globalThis.ResizeObserver;
  globalThis.ResizeObserver = class {
    constructor(callback: ResizeObserverCallback) {
      observers.push(callback);
    }
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof ResizeObserver;
  try {
    const resize = (index: number, width: number, height: number) =>
      act(() =>
        observers[index](
          [{ contentRect: { width, height } } as ResizeObserverEntry],
          {} as ResizeObserver,
        ),
      );
    const places = (container: HTMLElement) =>
      new Map(
        [...container.querySelectorAll(".graph-node")].map((n) => {
          const [, x, y] = /translate\(([-\d.]+),([-\d.]+)\)/.exec(
            n.getAttribute("transform") ?? "",
          )!;
          return [n.getAttribute("data-id")!, { x: Number(x), y: Number(y) }];
        }),
      );
    const props = {
      nodes: demoData.nodes,
      edges: demoData.edges,
      selected: null,
      onSelect: vi.fn(),
      healthFocus: false,
      paused: true,
    };
    const ambient = render(<Constellation {...props} interactive={false} />);
    resize(0, 1600, 700);
    const sky = places(ambient.container);
    const ambientHeight = (820 * 700) / 1600;
    const explorer = render(<Constellation {...props} interactive />);
    resize(1, 1200, 900);
    const explorerHeight = (820 * 900) / 1200;
    const opened = places(explorer.container);
    expect(opened.size).toBe(sky.size);
    for (const [id, at] of sky) {
      expect(opened.get(id)!.x).toBeCloseTo(at.x, 1);
      // A taller explorer stretches the sky downwards, so its stars keep
      // the spacing they had (a phone's explorer is far taller).
      expect(opened.get(id)!.y).toBeCloseTo(
        (at.y * explorerHeight) / ambientHeight,
        1,
      );
    }
  } finally {
    globalThis.ResizeObserver = Original;
  }
});

it("lays a phone-sized sky out for its own height first, so a small shrink keeps its stars", () => {
  // The first measurement once had to differ from the starting guess (820)
  // by 15% to count, so a 711-unit sky stayed laid out for 820 and a 7%
  // shrink laid the whole sky out again.
  const observers: ResizeObserverCallback[] = [];
  const Original = globalThis.ResizeObserver;
  globalThis.ResizeObserver = class {
    constructor(callback: ResizeObserverCallback) {
      observers.push(callback);
    }
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof ResizeObserver;
  try {
    const resize = (width: number, height: number) =>
      act(() =>
        observers[0](
          [{ contentRect: { width, height } } as ResizeObserverEntry],
          {} as ResizeObserver,
        ),
      );
    const stars = (container: HTMLElement) =>
      new Map(
        [...container.querySelectorAll(".graph-node")].map((n) => [
          n.getAttribute("data-id"),
          n.getAttribute("transform"),
        ]),
      );
    const sky = render(
      <Constellation
        nodes={demoData.nodes}
        edges={demoData.edges}
        selected={null}
        onSelect={vi.fn()}
        healthFocus={false}
        paused
        interactive={false}
      />,
    );
    resize(1000, 867); // about 711 units tall
    const before = stars(sky.container);
    resize(1000, 806); // 7% shorter
    const after = stars(sky.container);
    const kept = [...before].filter(([id, at]) => after.get(id) === at).length;
    expect(kept).toBeGreaterThanOrEqual(Math.floor(before.size * 0.9));
  } finally {
    globalThis.ResizeObserver = Original;
  }
});
