// Whether this version accepts app.viewers: on (docs/security.md, Viewer sessions). False until the owner approves
// viewer sessions (D-046, Q-153): with it false, rhythm.yaml saying on stops the server at
// start, as the engine's settings load does (ViewerSettings.Accepted). Changing it is its own reviewed change, never a
// setting; tests replace this module to reach the on path.
export const viewersAccepted = false;
