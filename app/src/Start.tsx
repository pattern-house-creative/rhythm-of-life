import { useCallback, useState } from "react";
import App from "./App.tsx";
import PageCapture from "./PageCapture.tsx";
import { memoryClient } from "./apiClient";
import type { MemoryClient } from "./memories";
import type { PageDraft } from "./bookmarklet";

// What the app draws first (main.tsx): the page for saving while the address carried a page the bookmark sent, in
// place of the sky; otherwise the app, drawn afresh (drawn) when settings read late arrive. The page for saving is
// drawn again with its new name, never afresh, so it keeps what is typed in it. It is there only while the engine
// serves the Memories view (memory.tools on): when the engine's first answer says it doesn't (off, the demo, or out of
// reach, which the app's server does not tell apart), the tab draws the app as any other tab does, and plain() lets
// main.tsx keep it up to date again.
export default function Start({
  captured,
  drawn,
  client = memoryClient,
  plain,
}: {
  captured: PageDraft | "unreadable" | null;
  drawn: number;
  client?: MemoryClient;
  plain?: () => void;
}) {
  const [unserved, setUnserved] = useState(false);
  const off = useCallback(() => {
    setUnserved(true);
    plain?.();
  }, [plain]);
  return captured && !unserved ? (
    <PageCapture page={captured} client={client} unserved={off} />
  ) : (
    <App key={drawn} />
  );
}
