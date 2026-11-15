import { handleScreens } from "../server/screens.js";
// A listen holds up to 35 seconds here, so this function may run for 60 (vercel.json), and
// a browser that leaves cancels it (supportsCancellation there), ending its engine call.
export function POST(request: Request) {
  return handleScreens(request);
}
export function OPTIONS(request: Request) {
  return handleScreens(request);
}
