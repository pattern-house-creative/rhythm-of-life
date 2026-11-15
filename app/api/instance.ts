import { handleInstance } from "../server/routes.js";
// Hosted front end (deploy/vercel/): the same handler the Node server runs.
export const GET = handleInstance;
export const POST = handleInstance;
export const OPTIONS = handleInstance;
