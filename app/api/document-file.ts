import { handleDocumentFile, hostedFileBytes } from "../server/documentFile.js";
// Hosted front end (deploy/vercel/): the same handler the Node server runs, passing on originals
// of at most 4 MiB, under the platform's 4.5 MB response limit; a larger one answers 413 and the
// reader shows its saved text instead.
const handle = (request: Request) =>
  handleDocumentFile(request, fetch, hostedFileBytes);
export const GET = handle;
export const POST = handle;
export const OPTIONS = handle;
