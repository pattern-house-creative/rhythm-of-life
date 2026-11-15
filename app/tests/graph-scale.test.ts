import { it, expect } from "vitest";
import { population } from "./synthetic";
import { graphWindow } from "../src/graphView";
import { createGraph } from "../src/graph";
import { boundGraph } from "../src/liveGraph";

it("benchmarks bounded layout at increasing source sizes", () => {
  const results = [];
  for (const size of [50, 250, 1000, 5000]) {
    const data = population(size),
      samples = [];
    for (let run = 0; run < 5; run++) {
      const start = performance.now();
      const view = graphWindow(data, 64);
      const graph = createGraph(view.nodes, view.edges, 820);
      graph.simulation.tick(60);
      graph.simulation.stop();
      samples.push(performance.now() - start);
      expect(graph.points.length).toBeLessThanOrEqual(64);
      expect(new Set(view.nodes.map((n) => n.provenance.sourceId)).size).toBe(
        3,
      );
      expect(view.edges.length).toBeGreaterThan(5);
      expect(
        graph.points.every((p) => Number.isFinite(p.x) && Number.isFinite(p.y)),
      ).toBe(true);
      expect(boundGraph(data).nodes.length).toBeLessThanOrEqual(1500);
    }
    samples.sort((a, b) => a - b);
    results.push({
      records: size,
      rendered: Math.min(size, 64),
      medianMs: +samples[2].toFixed(2),
      maxMs: +samples[4].toFixed(2),
    });
  }
  console.log("GRAPH_BENCHMARK", JSON.stringify(results));
});
