import type { Plugin } from "vite";
import { routes, bodyLimit } from "./routes.js";

// Local development runs the exact handlers the Node server and a hosted front end run. There
// is no login: this loopback dev server must never be exposed beyond this computer.
export function devApi(): Plugin {
  return {
    name: "rhythm-local-api",
    configureServer(server) {
      server.middlewares.use(async (req, res, next) => {
        const path = req.url?.split("?")[0] ?? "";
        const handler = Object.hasOwn(routes, path) ? routes[path] : undefined;
        if (!handler) return next();
        // A browser that leaves ends the request's engine call too, as in server/node.ts.
        const gone = new AbortController();
        res.on("close", () => {
          if (!res.writableFinished) gone.abort();
        });
        try {
          const chunks: Buffer[] = [];
          let size = 0;
          for await (const chunk of req) {
            size += chunk.length;
            if (size > bodyLimit(path)) {
              res.writeHead(413);
              res.end();
              return;
            }
            chunks.push(Buffer.from(chunk));
          }
          const headers = new Headers();
          for (const [key, value] of Object.entries(req.headers))
            if (value)
              headers.set(key, Array.isArray(value) ? value.join(",") : value);
          const request = new Request(`http://${req.headers.host}${req.url}`, {
            method: req.method,
            headers,
            signal: gone.signal,
            ...(size ? { body: Buffer.concat(chunks) } : {}),
          });
          const response = await handler(request);
          res.writeHead(response.status, Object.fromEntries(response.headers));
          res.end(Buffer.from(await response.arrayBuffer()));
        } catch {
          res.writeHead(500);
          res.end();
        }
      });
    },
  };
}
