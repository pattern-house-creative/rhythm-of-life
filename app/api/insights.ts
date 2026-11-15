import { handleInsights } from "../server/insights.js";
export function GET(request: Request) {
  return handleInsights(request);
}
export function POST(request: Request) {
  return handleInsights(request);
}
export function OPTIONS(request: Request) {
  return handleInsights(request);
}
