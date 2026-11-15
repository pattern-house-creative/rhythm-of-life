import type { MemoryClient } from "./memories";

// Whether the engine serves the memories, as Home's card asks it (ResurfaceCard.tsx; the brief's card was removed, D-257): one
// read kept for as long as the card waits between its own reads, so with memory.tools off Home asks once in that time, however
// often the card comes back to it. Not tied to the card's signal, so a card added beside it may share the read.
// A page's load starts afresh; a read that failed answers false, as the client's does, until the next.
const keptMs = 900_000;

const kept = new WeakMap<
  MemoryClient,
  { at: number; served: Promise<boolean> }
>();

export function homeServed(client: MemoryClient): Promise<boolean> {
  const now = Date.now();
  const last = kept.get(client);
  if (last && now >= last.at && now - last.at < keptMs) return last.served;
  const served = client.available();
  kept.set(client, { at: now, served });
  return served;
}
