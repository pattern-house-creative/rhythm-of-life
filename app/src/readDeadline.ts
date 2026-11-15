// AbortSignal.any is absent on older Android System WebViews. Keep cancellation
// and bounded reads without a global polyfill, retaining the caller's reason.
export async function readDeadline<T>(
  parent: AbortSignal | undefined,
  milliseconds: number,
  read: (signal: AbortSignal) => Promise<T>,
): Promise<T> {
  const controller = new AbortController();
  const abort = () => controller.abort(parent?.reason);
  if (parent?.aborted) abort();
  else parent?.addEventListener("abort", abort, { once: true });
  const timer = setTimeout(
    () => controller.abort(new DOMException("Read timed out", "TimeoutError")),
    milliseconds,
  );
  try {
    return await read(controller.signal);
  } finally {
    clearTimeout(timer);
    parent?.removeEventListener("abort", abort);
  }
}
