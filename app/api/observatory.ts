import { handleObservatory } from "../server/routes.js";
// Hosted front end (deploy/vercel/): the same handler the Node server runs.
export const GET = handleObservatory;
export const POST = handleObservatory;
export const OPTIONS = handleObservatory;
