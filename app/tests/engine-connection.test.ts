import { createServer } from "node:http";
import type { AddressInfo } from "node:net";
import { Agent } from "undici";
import { expect, it } from "vitest";
import { engineConnection } from "../server/engineFetch";

// Node's own fetch, taken before the suite's per-test stub is installed: this test runs the
// dispatcher with the fetch the server really uses.
const nativeFetch = globalThis.fetch;

it.each([false, true])(
  "connects across the engine host's addresses, with an event-loop stall: %s",
  async (stall) => {
    const server = createServer((_request, response) => {
      response.setHeader("Content-Type", "application/json");
      response.end('{"fixture":true}');
    });
    await new Promise<void>((resolve) =>
      server.listen(0, "127.0.0.1", resolve),
    );
    let allAddressesRequested = false;
    const agent = new Agent({
      ...engineConnection,
      connect: {
        lookup(_hostname, options, callback) {
          allAddressesRequested = options.all === true;
          // Nothing listens on IPv6; the IPv4 listener is the only route.
          const addresses = [
            { address: "::1", family: 6 },
            { address: "127.0.0.1", family: 4 },
          ];
          callback(null, stall ? addresses.reverse() : addresses);
          if (stall) {
            // Node's default 250 ms per address would give up on the healthy first one here.
            const end = Date.now() + 400;
            while (Date.now() < end) {
              /* deliberate event-loop contention */
            }
          }
        },
      },
    });
    try {
      const options: RequestInit & { dispatcher: Agent } = {
        dispatcher: agent,
        signal: AbortSignal.timeout(5000),
      };
      const response = await nativeFetch(
        `http://engine.example.invalid:${(server.address() as AddressInfo).port}/`,
        options,
      );
      expect(allAddressesRequested).toBe(true);
      expect(response.status).toBe(200);
      expect(await response.json()).toEqual({ fixture: true });
    } finally {
      await agent.destroy();
      await new Promise<void>((resolve) => server.close(() => resolve()));
    }
  },
);
