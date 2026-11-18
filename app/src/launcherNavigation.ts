export function launcherTarget(
  hash: string,
): { kind: "dashboard" | "star" | "builtin"; id: string } | null {
  if (hash === "#rhythm-launcher=builtin:glucose")
    return { kind: "builtin", id: "glucose" };
  const match = /^#rhythm-launcher=(dashboard|star):(.{1,3000})$/.exec(hash);
  if (!match) return null;
  try {
    const id = decodeURIComponent(match[2]);
    // eslint-disable-next-line no-control-regex -- Reject control characters in decoded navigation targets.
    return id.length > 0 && id.length <= 1000 && !/[\u0000-\u001f]/.test(id)
      ? { kind: match[1] as "dashboard" | "star", id }
      : null;
  } catch {
    return null;
  }
}
