import { useEffect, useState } from "react";
import { slotNames, statusNames } from "./memories";
import type { CapsuleSlotStatus, MemoryCapsuleStatus } from "./memories";
import { counted, rowGroup } from "./memoryScreen";
import type { MemoryTools } from "./memoryScreen";

// A capsule's status (SB-P6-01; upstream's capsule status on its projects): each fixed slot as the capsule's AI clients are
// served it, what waits in it and the step that lets it in, each with the way to the memory. It reads no words: those are the
// memory's, where a person opens them. A slot is filled from a memory's own Capsule slot, in the memory.

function SlotLine({
  slot,
  maxChars,
  open,
}: {
  slot: CapsuleSlotStatus;
  maxChars: number;
  open: (id: string) => void;
}) {
  const said =
    slot.state === "filled"
      ? "Filled."
      : slot.state === "duplicate"
        ? `Left out: ${slot.duplicates.length} memories fill it, and it takes one. Take all but one out of it.`
        : slot.state === "omitted"
          ? `Left out: the slots before it took the capsule's ${maxChars} characters.`
          : "No memory fills it.";
  return (
    <li>
      <span className="memory-meta">
        <strong>{slotNames[slot.slot] ?? slot.slot}</strong> · {said}
      </span>
      <span
        className="memory-actions"
        {...rowGroup(slotNames[slot.slot] ?? slot.slot)}
      >
        {slot.filledBy && (
          <button onClick={() => open(slot.filledBy!)}>Open it</button>
        )}
        {slot.duplicates.map((id, i) => (
          <button key={id} onClick={() => open(id)}>
            Open{" "}
            {i === 0 ? "the first" : i === 1 ? "the second" : `number ${i + 1}`}
          </button>
        ))}
      </span>
      {slot.invalid.map((m) => (
        <span key={m.id} className="memory-actions memory-waiting">
          <span className="memory-hint">
            {m.reason === "empty-content"
              ? "One holds no words, so it is left out."
              : "One is too long for the capsule on its own, so it is left out."}
          </span>
          <button onClick={() => open(m.id)}>Open it</button>
        </span>
      ))}
      {slot.waiting.map((m) => (
        <span key={m.id} className="memory-actions memory-waiting">
          <span className="memory-hint">
            {m.held
              ? "One is held for review: served once a person releases it."
              : m.expired
                ? "One is past when it was true, so it is left out."
                : m.status === "deprecated"
                  ? `One is ${statusNames.deprecated.toLowerCase()}, so it is left out.`
                  : "One is a draft: served once it is confirmed."}
          </span>
          <button onClick={() => open(m.id)}>Open it</button>
        </span>
      ))}
    </li>
  );
}

export function MemoryCapsuleCard({
  name,
  request,
  tools,
}: {
  // Whose capsule, in words.
  name: string;
  // Which: core or a project's, in a scope.
  request: { capsule: "core" | "project"; project?: string; scope?: string };
  tools: MemoryTools;
}) {
  const [status, setStatus] = useState<MemoryCapsuleStatus | null>(null);
  const [none, setNone] = useState(false);
  const key = JSON.stringify(request);
  useEffect(() => {
    let live = true;
    setStatus(null);
    setNone(false);
    void tools
      .quiet<MemoryCapsuleStatus>({ op: "capsule_status", ...request })
      .then((reply) => {
        if (!live) return;
        if (reply) setStatus(reply);
        else setNone(true);
      });
    return () => {
      live = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);
  if (none)
    return (
      <p className="memory-hint">{name}: its status can't be read just now.</p>
    );
  if (!status) return null;
  const filled = status.slots.filter((s) => s.state === "filled").length;
  return (
    <section className="memory-capsule" aria-label={name}>
      <p className="memory-meta">
        <strong>{name}</strong> ·{" "}
        {status.complete
          ? "Complete: every slot is filled once."
          : `${filled} of ${counted(status.slots.length, "slot", "slots")} filled.`}{" "}
        {status.chars} of {status.maxChars} characters.
      </p>
      {!status.served && (
        <p className="memory-hint">
          AI clients are offered no capsule on this install (memory.capsules is
          off): this is what they would read with it on.
        </p>
      )}
      <ul className="memory-capsule-slots" aria-label={`${name}: its slots`}>
        {status.slots.map((slot) => (
          <SlotLine
            key={slot.slot}
            slot={slot}
            maxChars={status.maxChars}
            open={tools.open}
          />
        ))}
      </ul>
    </section>
  );
}
