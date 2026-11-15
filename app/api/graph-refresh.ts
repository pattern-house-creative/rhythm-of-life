import { handleGraphRefresh } from "../server/routes.js";
// Hosted front end (deploy/vercel/): the same handler the Node server runs.
export const POST = handleGraphRefresh;
export const GET = handleGraphRefresh;
export const OPTIONS = handleGraphRefresh;
