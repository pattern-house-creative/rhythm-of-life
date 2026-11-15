import { useEffect, useRef, useState } from "react";
import type { GraphEdge } from "./knowledge";
import type {
  MapDismissal,
  MapDismissed,
  MapUndismissed,
  MemoryClient,
  MemoryReads,
} from "./memories";

// Discover's Dismiss kept on the engine (SB-P4-05; app_memories dismissals, dismiss and undismiss,
// contracts/README.md 2.2): only while the engine serves the memories (its memory.tools on), through
// the app's channel, which is a person's. A dismissed suggestion then stays off the map after a
// reload and a rebuild, and a person sees what was dismissed on their map, by them, by someone
// else or by an AI client's unlink, and shows one again. While the engine does not serve them the
// hook is null, and Discover's Dismiss hides a suggestion on this screen only, as it always did.
// DismissedList (Dismissals.tsx) shows the list.
export interface KeptDismissals {
  list: MapDismissal[];
  truncated: boolean;
  note: string;
  // Saves a Dismiss the screen has already applied; says in `note` what came of it.
  dismiss: (edge: GraphEdge) => void;
  // Takes one back; answers the ids of links dismissed on this screen that it shows again.
  restore: (item: MapDismissal) => Promise<string[]>;
}

const same = (a: string[], b: string[]) =>
  a.length === b.length &&
  [...a].sort().join("\n") === [...b].sort().join("\n");

export function useKeptDismissals(
  client: MemoryClient | undefined,
  open: boolean,
  onRestored: () => void,
): KeptDismissals | null {
  const [list, setList] = useState<MapDismissal[]>([]);
  const [truncated, setTruncated] = useState(false);
  const [note, setNote] = useState("");
  const [generation, setGeneration] = useState(0);
  // What this screen dismissed: each link's id with its two stars and type, so showing one again
  // brings back the links it hid here.
  const hidden = useRef(new Map<string, { stars: string[]; type: string }>());
  useEffect(() => {
    if (!client || !open) return;
    const controller = new AbortController();
    Promise.resolve()
      .then(() =>
        client.call<MemoryReads["dismissals"]>(
          { op: "dismissals" },
          controller.signal,
        ),
      )
      .then(
        (answer) => {
          if (controller.signal.aborted || !answer) return;
          // A list that could not be read shows nothing: no one asked for it, so nothing is said.
          if (answer.available && answer.ok) {
            setList(answer.dismissals);
            setTruncated(answer.truncated);
          } else if (!answer.available) {
            setList([]);
            setTruncated(false);
          }
        },
        () => {},
      );
    return () => controller.abort();
  }, [client, open, generation]);
  if (!client) return null;
  return {
    list,
    truncated,
    note,
    dismiss(edge) {
      hidden.current.set(edge.id, {
        stars: [edge.source, edge.target],
        type: edge.type,
      });
      setNote("");
      Promise.resolve()
        .then(() => client.call<MapDismissed>({ op: "dismiss", link: edge.id }))
        .then(
          (answer) => {
            if (!answer?.available)
              setNote(
                "Hidden on this screen only: the engine no longer keeps dismissals.",
              );
            else if (!answer.ok)
              setNote(`${answer.reason} Hidden on this screen only.`);
            else {
              setNote(
                !answer.removed
                  ? "Dismissed. It stays off the map after a reload and a rebuild; you can show it again below."
                  : answer.dismissal
                    ? "Removed the suggested link, and it won't be suggested again. Taking that back below lets a link like it be suggested again; it doesn't bring this one back."
                    : "Removed the link an AI client made. Nothing is kept to show again: the client could make it again.",
              );
              setGeneration((g) => g + 1);
            }
          },
          () =>
            setNote(
              "The dismissal may not have been kept. Hidden on this screen only.",
            ),
        );
    },
    async restore(item) {
      const answer = await Promise.resolve()
        .then(() =>
          client.call<MapUndismissed>({ op: "undismiss", dismissal: item.id }),
        )
        .catch(() => null);
      if (!answer || !answer.available) {
        setNote("This couldn't be shown again now. Try again later.");
        return [];
      }
      if (!answer.ok) {
        setNote(answer.reason);
        return [];
      }
      setList((l) => l.filter((d) => d.id !== item.id));
      setNote(
        answer.back
          ? "Shown again: it is back on the map."
          : "Taken back. Nothing suggests this link now, so nothing comes back on the map until something suggests it again.",
      );
      const shown = [...hidden.current.entries()]
        .filter(
          ([, h]) =>
            h.type === item.type &&
            same(
              h.stars,
              item.stars.map((s) => s.id),
            ),
        )
        .map(([id]) => id);
      for (const id of shown) hidden.current.delete(id);
      onRestored();
      return shown;
    },
  };
}
