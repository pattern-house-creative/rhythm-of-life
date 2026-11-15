import { handleHealth } from "../server/routes.js";
// Hosted front end (deploy/vercel/): the same handler the Node server runs.
export const GET = handleHealth;
export const POST = handleHealth;
export const OPTIONS = handleHealth;
