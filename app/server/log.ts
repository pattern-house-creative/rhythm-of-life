// One warning line per kind of failure, at most once a minute for each, with how many more of the
// same kind were not logged since the last line. Fixed fields only: never a URL, a header, a body,
// a value from a request or an answer, or a token.
const quiet = 60_000;
const seen = new Map<string, { at: number; repeats: number }>();

export function warnOnce(
  event: string,
  fields: Record<string, string | number | null>,
  now = Date.now(),
) {
  const key = `${event}\n${JSON.stringify(fields)}`;
  const last = seen.get(key);
  if (last && now - last.at < quiet) {
    last.repeats++;
    return;
  }
  // The kinds come from fixed lists, so this only bounds a long-running server's memory.
  if (seen.size >= 500) seen.clear();
  seen.set(key, { at: now, repeats: 0 });
  console.warn(
    event,
    last?.repeats ? { ...fields, repeats: last.repeats } : fields,
  );
}

// For tests: forget what was logged.
export function resetWarnings() {
  seen.clear();
}
