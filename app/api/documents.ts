import { handleDocuments } from "../server/documents.js";
// Hosted front end (deploy/vercel/): the same handler the Node server runs. A hosted platform may
// cap request bodies below the 5 MiB a document may have; its own limit applies there.
export const GET = (request: Request) => handleDocuments(request);
export const POST = (request: Request) => handleDocuments(request);
export const OPTIONS = (request: Request) => handleDocuments(request);
