// A fictional Nightscout site for trying the nightscout adapter without one (docs/adapters/nightscout.md).
// It answers GET /api/v1/entries/sgv.json like Nightscout does: sgv entries in mg/dL, newest first, at most
// `count`, filtered by find[date][$gt] and find[date][$lt]. Readings are made up, every 5 minutes for the last
// 30 days, the same for the same time on every run. No dependencies.
//
//   node scripts/fake-nightscout.mjs                       # http://127.0.0.1:1337, readable without a token
//   PORT=1337 HOST=0.0.0.0 FAKE_NIGHTSCOUT_TOKEN_FILE=<file> node scripts/fake-nightscout.mjs
//
// With FAKE_NIGHTSCOUT_TOKEN_FILE it wants that file's access token as a bearer, as the adapter sends it.
import { createServer } from "node:http";
import { readFileSync } from "node:fs";

const port = Number(process.env.PORT ?? 1337);
const host = process.env.HOST ?? "127.0.0.1";
const tokenFile = process.env.FAKE_NIGHTSCOUT_TOKEN_FILE;
const step = 5 * 60 * 1000;
const days = 30;

// A day's curve with three meal bumps, plus a little noise from the time itself.
function sgv(at) {
  const hour = (at / 3600000) % 24;
  const bump = (h) => 55 * Math.exp(-(((hour - h + 24) % 24) ** 2) / 1.5);
  const noise = Math.sin(at / 7919) * 6 + Math.sin(at / 104729) * 9;
  return Math.round(Math.min(260, Math.max(62, 104 + bump(8) + bump(13) + bump(19) + noise)));
}

function entries(after, before, count) {
  const now = Date.now();
  const newest = Math.floor(now / step) * step - 60000;
  const out = [];
  for (let at = newest; at > now - days * 86400000 && out.length < count; at -= step) {
    if (at <= after || at >= before) continue;
    out.push({
      _id: `fake-${at}`,
      type: "sgv",
      date: at,
      dateString: new Date(at).toISOString(),
      sgv: sgv(at),
      direction: "Flat",
      device: "fake-nightscout",
    });
  }
  return out;
}

createServer((request, response) => {
  const url = new URL(request.url ?? "/", "http://fake-nightscout");
  const answer = (status, body) => {
    response.writeHead(status, { "content-type": "application/json", "cache-control": "no-store" });
    response.end(JSON.stringify(body));
  };
  if (request.method !== "GET" || url.pathname !== "/api/v1/entries/sgv.json") return answer(404, { status: 404 });
  if (tokenFile) {
    const token = readFileSync(tokenFile, "utf8").trim();
    if (request.headers.authorization !== `Bearer ${token}`) return answer(401, { status: 401, message: "Unauthorized" });
  }
  const number = (key, fallback) => (url.searchParams.has(key) ? Number(url.searchParams.get(key)) : fallback);
  const count = Math.min(Math.max(number("count", 10), 1), 10000);
  answer(200, entries(number("find[date][$gt]", -Infinity), number("find[date][$lt]", Infinity), count));
}).listen(port, host, () => console.log(`Fake Nightscout on http://${host}:${port}/ (fictional readings)`));
