// A periodic read that failed (the app's server or the engine restarting for a deploy, say) is
// tried again soon, then less often: 15 s, 30 s, 1 min, 2 min, 4 min, then every 5 minutes,
// always well before the next regular read. What was read last stays shown meanwhile.
export const retryDelay = (failures: number) =>
  Math.min(15_000 * 2 ** failures, 300_000);
