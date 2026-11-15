// Vite dev-only harness; not an entry point in the production build.
import { useState } from "react";
import { createRoot } from "react-dom/client";
import { population } from "./synthetic";
import { graphWindow } from "../src/graphView";
import { Constellation } from "../src/Constellation";
import "../src/index.css";
import "../src/App.css";
const size = Math.min(
  5000,
  Math.max(
    50,
    Number(new URLSearchParams(location.search).get("records")) || 50,
  ),
);
const data = population(size);
const start = performance.now();
const visible = graphWindow(data, 64);
const projection = performance.now() - start;
// Dev-only entry point owns its root; this is not a reusable Fast Refresh module.
// eslint-disable-next-line react/only-export-components
function Harness() {
  const [selected, setSelected] = useState<string | null>(null);
  return (
    <main style={{ overflow: "hidden" }}>
      <p role="status">
        {size} fictional records · {visible.nodes.length} rendered · projection{" "}
        {projection.toFixed(2)} ms
      </p>
      <div
        style={{
          height: "80vh",
          minHeight: 350,
          width: "100%",
          position: "relative",
          overflow: "hidden",
        }}
      >
        <Constellation
          nodes={visible.nodes}
          edges={visible.edges}
          selected={selected}
          onSelect={setSelected}
          interactive
          healthFocus={false}
        />
      </div>
    </main>
  );
}
const root = createRoot(document.getElementById("root")!);
root.render(<Harness />);
if (import.meta.hot) import.meta.hot.dispose(() => root.unmount());
