import { handleDashboards } from "../server/dashboards.js";
export function GET(request: Request) {
  return handleDashboards(request);
}
export function POST(request: Request) {
  return handleDashboards(request);
}
export function OPTIONS(request: Request) {
  return handleDashboards(request);
}
