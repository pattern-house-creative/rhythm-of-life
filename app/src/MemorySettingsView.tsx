import type { MemorySettingsRead } from "./memories";
import { personName } from "./instance";
import { MemoryCapsuleCard } from "./MemoryCapsuleCard";
import type { MemoryTools } from "./memoryScreen";

// Memory settings, to read (SB-P6-01; upstream's settings, whose changes stay in rhythm.yaml here): what this install's memory
// switches are and what each means, how long the trash keeps a memory and how many earlier states are kept. Nothing here
// changes anything: a setting belongs to whoever runs the install.

const on = (v: boolean) => (v ? "On" : "Off");

function Row({
  name,
  value,
  means,
}: {
  name: string;
  value: string;
  means: string;
}) {
  return (
    <div className="memory-setting">
      <dt>
        {name} <span className="memory-setting-value">{value}</span>
      </dt>
      <dd>{means}</dd>
    </div>
  );
}

export function MemorySettingsView({
  settings,
  tools,
}: {
  settings: MemorySettingsRead | null;
  tools: MemoryTools;
}) {
  if (!settings)
    return (
      <p className="memory-hint">
        This engine doesn't say its memory settings yet. They are in
        rhythm.yaml, under memory.
      </p>
    );
  return (
    <>
      <p className="memory-hint">
        {settings.person
          ? `You read as ${personName(settings.person)}.`
          : "You read as the household."}{" "}
        These change in rhythm.yaml, where the install is run from, and the
        engine takes them at its next start (docs/configuration.md).
      </p>
      <dl className="memory-settings">
        <Row
          name="Per-person scopes"
          value={settings.scopes === "on" ? "On" : "Off"}
          means={
            settings.scopes === "on"
              ? "Each person has private memories, and Family shares the rest."
              : "Every memory is visible to everyone in the household."
          }
        />
        <Row
          name="Sharing"
          value={on(settings.share)}
          means="Moving a memory between its owner's private scope and Family, here and for AI clients; only while per-person scopes are on."
        />
        <Row
          name="Capsules for AI clients"
          value={on(settings.capsules)}
          means="AI clients read a capsule first: one scope's confirmed memories, one per fixed slot."
        />
        <Row
          name="Capture route"
          value={on(settings.capture)}
          means="Capture tokens may save drafts from outside, each into its one scope, for a person to review."
        />
        <Row
          name="Second Brain import"
          value={on(settings.secondBrainImport)}
          means="The terminal may read a Second Brain export; nothing is imported here."
        />
        <Row
          name="Combined search"
          value={on(settings.recall)}
          means="The search that fuses words and meaning, with why each result came."
        />
        <Row
          name="Recall log"
          value={on(settings.recallLog)}
          means="What was asked and what was followed is kept, for measuring search."
        />
        <Row
          name="Night summary"
          value={on(settings.nightSummary)}
          means="The nightly jobs say in one place how each ended."
        />
        <Row
          name="The trash keeps"
          value={`${settings.trashDays} days`}
          means="A forgotten memory is deleted for good after this, unless someone brings it back."
        />
        <Row
          name="Earlier states kept"
          value={`${settings.versionsKept} each`}
          means="How many of a memory's earlier states can be read and put back. Its history keeps every entry."
        />
        <Row
          name="Longest memory"
          value={`${Math.round(settings.maxBytes / 1024)} KiB`}
          means="Longer text is refused before it is saved: split it into smaller memories."
        />
      </dl>
      <MemoryCapsuleCard
        name="Family's core capsule"
        request={{ capsule: "core", scope: "shared" }}
        tools={tools}
      />
      {settings.scopes === "on" && settings.person && (
        <MemoryCapsuleCard
          name={`${personName(settings.person)}'s own core capsule`}
          request={{ capsule: "core", scope: "private" }}
          tools={tools}
        />
      )}
    </>
  );
}
