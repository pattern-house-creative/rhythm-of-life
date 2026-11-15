import { connectHints } from "./health";

// How to turn on the adapter behind a source that is not connected.
export function ConnectHint({ slot }: { slot: string }) {
  const hint = connectHints[slot];
  return hint ? (
    <>
      Connect {hint.what} with <code>{hint.command}</code>.
    </>
  ) : null;
}
