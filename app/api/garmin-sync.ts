import { handleGarminSync } from "../server/routes.js";
// Hosted front end (deploy/vercel/): the same handler the Node server runs.
export const POST = handleGarminSync;
export const GET = handleGarminSync;
export const OPTIONS = handleGarminSync;
