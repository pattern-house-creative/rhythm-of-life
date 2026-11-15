import { handleGraph } from "../server/graph.js";
export function POST(request: Request) {
  return handleGraph(request);
}
export function GET(request: Request) {
  return handleGraph(request);
}
export function OPTIONS(request: Request) {
  return handleGraph(request);
}
