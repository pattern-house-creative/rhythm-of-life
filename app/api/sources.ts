import { handleSources } from "../server/routes.js";
// Hosted front end (deploy/vercel/): /api/status, /api/sync and /api/memories in one function, which
// vercel.json's rewrites send here, so the recipe stays within Vercel Hobby's twelve functions.
// The same handlers the Node server runs, each answering its own methods as before.
export const GET = handleSources;
export const POST = handleSources;
export const OPTIONS = handleSources;
