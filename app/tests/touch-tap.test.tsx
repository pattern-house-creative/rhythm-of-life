import { afterEach, expect, it, vi } from "vitest";
import { fireEvent, render } from "@testing-library/react";
import { Constellation } from "../src/Constellation";
import { demoData } from "../src/demo";

// jsdom lays nothing out, so each star's screen spot comes from this table.
const spots = new Map<string, [number, number]>();

function touch(type: string, target: Element, points: [number, number][]) {
  const event = new Event(type, { bubbles: true, cancelable: true });
  const list = points.map(([x, y], identifier) => ({
    clientX: x,
    clientY: y,
    identifier,
  }));
  Object.defineProperty(event, "touches", {
    value: type === "touchend" ? [] : list,
  });
  Object.defineProperty(event, "changedTouches", { value: list });
  fireEvent(target, event);
}

afterEach(() => {
  vi.restoreAllMocks();
  spots.clear();
});

it("a tap means the star under the finger as it lands, even if the sky slides before it lifts", () => {
  vi.spyOn(Element.prototype, "getBoundingClientRect").mockImplementation(
    function (this: Element) {
      const id = (this as HTMLElement).dataset?.id;
      const [x, y] = (this.matches("g.graph-node") && id && spots.get(id)) || [
        -1000, -1000,
      ];
      return new DOMRect(x - 5, y - 5, 10, 10);
    },
  );
  Object.defineProperty(document, "elementFromPoint", {
    value: () => null,
    configurable: true,
  });
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
  const svg = container.querySelector("svg.graph-svg")!;
  const [aimed, neighbour] = [...svg.querySelectorAll("g.graph-node")].map(
    (g) => (g as SVGGElement).dataset.id!,
  );
  spots.set(aimed, [100, 100]);
  spots.set(neighbour, [160, 100]);
  touch("touchstart", svg, [[100, 100]]);
  // A camera easing to an earlier choice slides the stars under the finger:
  // by the lift the neighbour is nearest to where the finger landed.
  spots.set(aimed, [130, 100]);
  spots.set(neighbour, [108, 100]);
  touch("touchend", svg, [[100, 100]]);
  expect(onSelect).toHaveBeenCalledTimes(1);
  expect(onSelect).toHaveBeenCalledWith(aimed);
});
