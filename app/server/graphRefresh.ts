import { answerRejected, callTool } from "./engine.js";

const iso = (v: unknown): v is string =>
  typeof v === "string" &&
  /(?:Z|[+-]\d\d:\d\d)$/.test(v) &&
  Number.isFinite(Date.parse(v));

export type RebuildAnswer = {
  accepted: boolean;
  pendingSince: string | null;
  running: boolean;
  nextAllowedAt: string | null;
  lastFinishedAt: string | null;
};
const declined: RebuildAnswer = {
  accepted: false,
  pendingSince: null,
  running: false,
  nextAllowedAt: null,
  lastFinishedAt: null,
};
// Refresh rebuilds the map: asks the engine (app_request_refresh) to rebuild it now instead of
// at its next scheduled tick. The engine rate-limits it; anything unavailable or malformed,
// demo mode included, is simply "not accepted".
export async function requestRebuild(
  request: Request,
  fetcher: typeof fetch = fetch,
): Promise<RebuildAnswer> {
  try {
    const answer = await callTool(
      "app_request_refresh",
      {},
      request.signal,
      fetcher,
    );
    const stamp = (v: unknown) => v === null || iso(v);
    if (
      Object.keys(answer).length !== 5 ||
      typeof answer.accepted !== "boolean" ||
      typeof answer.running !== "boolean" ||
      !stamp(answer.pendingSince) ||
      !stamp(answer.nextAllowedAt) ||
      !stamp(answer.lastFinishedAt)
    )
      throw new Error("Invalid rebuild answer");
    return {
      accepted: answer.accepted,
      pendingSince: answer.pendingSince as string | null,
      running: answer.running,
      nextAllowedAt: answer.nextAllowedAt as string | null,
      lastFinishedAt: answer.lastFinishedAt as string | null,
    };
  } catch (error) {
    answerRejected("app_request_refresh", error, request.signal);
    return declined;
  }
}
