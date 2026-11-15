import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { registerSW } from "virtual:pwa-register";
import "./index.css";
import Start from "./Start.tsx";
import { startPage } from "./startPage.ts";
import { instance, keepReading, loadInstance } from "./instance.ts";

window.addEventListener("beforeinstallprompt", (event) => {
  event.preventDefault();
  Object.assign(window, { observatoryInstall: event });
});

// A page the Memories view's bookmark sent (bookmarklet.ts): read from the address and taken out of it first. Its tab
// shows the page for saving instead of the sky, and is not reloaded into a new build, which would lose what came; when
// the engine doesn't serve the Memories view (memory.tools off) it draws the app as any tab does, kept up to date again.
const { captured, plain } = startPage(registerSW);
let saving = Boolean(captured);

// The install's name, people and time zone come first: charts and "today" depend on them.
const read = await loadInstance();
const root = createRoot(document.getElementById("root")!);
let drawn = 0;
const draw = () => {
  const title = saving ? `Save to ${instance().name}` : instance().name;
  if (title !== document.title) document.title = title;
  root.render(
    <StrictMode>
      <Start captured={captured} drawn={drawn} plain={unserved} />
    </StrictMode>,
  );
};
// The engine's first answer said it doesn't serve the Memories view: the app, named and kept up to date as any tab.
function unserved() {
  saving = false;
  plain();
  draw();
}
draw();
// Settings read late draw the app afresh, so nothing keeps the stand-in's zone or person. The page for saving keeps
// what is typed in it: it is drawn again with its new name, not afresh.
if (!read)
  keepReading(() => {
    drawn += 1;
    draw();
  });
