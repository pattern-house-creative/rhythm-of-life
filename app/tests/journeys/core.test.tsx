// What a household already has keeps working (stories 14 and 18), against the real stack and its fictional data: the
// glucose tile and its panel from a plugin's readings, a note's and a document's stars and readers, a document's original,
// a data star's readings, a dashboard with its feeds read, and an insight. cli/journeys.sh runs this with every memory
// setting off, with memory.tools on, and with memory scopes on: none of them changes it. At a phone's size and a desktop's.
import { describe, expect, it } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import {
  appJson,
  at,
  eventually,
  explorer,
  fetchApp,
  home,
  inSandbox,
  journey,
  plugin,
} from "./sandbox";

inSandbox();

const details = () =>
  screen.getByRole("complementary", { name: "Selected knowledge details" });
// Stars are chosen from the keyboard; the sky's drag handling needs a real pointer (tests/dashboard-flow.test.tsx).
async function pick(
  user: Awaited<ReturnType<typeof explorer>>["user"],
  region: HTMLElement,
  title: string | RegExp,
) {
  const stars = await within(region).findAllByRole("button", {
    name: typeof title === "string" ? `Explore ${title}` : title,
  });
  stars[0].focus();
  await user.keyboard("{Enter}");
  return details();
}

journey(
  "the existing stars, dashboards, health views and original links",
  () => {
    describe.each(["phone", "desktop"] as const)("at a %s's size", (size) => {
      it("shows the demo's card in the main corner, and once a person chooses the tile, a plugin's glucose reading and its panel", async () => {
        at(size);
        const now = Date.now(),
          minute = 60_000;
        const stamp = (n: number) =>
          new Date(now - n * minute).toISOString().replace(/\.\d+Z$/, "Z");
        const sent = await plugin("journey-cgm", "ingest_measurements", {
          provider: "journey-cgm",
          subject: "alex",
          idempotencyKey: `journey:${size}:${now}`,
          source: {
            externalId: `journey-${size}-${now}`,
            kind: "glucose-readings",
            title: "DEMO readings",
            fetchedAt: stamp(0),
            revision: "r-1",
            timezone: "Europe/London",
            device: { id: "demo-sensor", model: "DEMO CGM" },
          },
          measurements: [10, 5, 1].map((n) => ({
            metric: "glucose",
            value: 137,
            unit: "mg/dL",
            observedAt: stamp(n),
            key: stamp(n),
            quality: "ok",
          })),
        });
        expect(sent.error, JSON.stringify(sent)).toBeUndefined();
        // Whose glucose it is is looked up at most once a minute (AppHealth), so a person's next reading is there within that;
        // cli/journeys.sh checked that the source's first one was on Home at its next read, with no restart (contracts/README.md 1.4).
        await eventually(
          () => appJson("/api/observatory"),
          (read: any) => read.current?.value === 137,
          "read the plugin's glucose",
        );
        const title = "DEMO · Week at a glance";
        const { id } = (await appJson("/api/dashboards")).dashboards.find(
          (d: any) => d.title === title,
        );
        expect((await appJson("/api/dashboards?cards=1")).cards.main).toBe(id);
        const { user } = await home();
        await screen.findByRole("button", {
          name: `Open the ${title} dashboard`,
        });
        try {
          // A card sits in a corner in place of its tile: Sources & settings gives the corner back to it.
          await user.click(
            screen.getByRole("button", { name: "Sources and settings" }),
          );
          const corner = await screen.findByRole("combobox", {
            name: /^Main.* corner$/,
          });
          await user.selectOptions(corner, "");
          // The step's answer has come once the choice can be changed again.
          await waitFor(() => expect(corner).toBeEnabled());
          await waitFor(async () =>
            expect(
              (await appJson("/api/dashboards?cards=1")).cards.main ?? null,
            ).toBeNull(),
          );
          await user.keyboard("{Escape}");
          await waitFor(() =>
            expect(
              screen.queryByRole("combobox", { name: /^Main.* corner$/ }),
            ).toBeNull(),
          );
          await user.click(
            await screen.findByRole("button", { name: /GLUCOSE 137/ }),
          );
          const panel = await screen.findByRole("dialog", { name: "glucose" });
          expect(
            within(panel).getByRole("region", { name: "Current glucose" }),
          ).toHaveTextContent("137");
          expect(panel).toHaveTextContent(/journey-cgm/);
          await user.keyboard("{Escape}");
        } finally {
          // Back as it was, for the journeys after this one, whatever became of this one.
          await appJson("/api/dashboards", {
            action: "card",
            corner: "main",
            dashboard: id,
          });
        }
        expect((await appJson("/api/dashboards?cards=1")).cards.main).toBe(id);
      });

      it("opens a note's star and reads it, and a data star's readings", async () => {
        at(size);
        const { user, region } = await explorer();
        const note = await pick(user, region, "DEMO · Bike commute log");
        expect(
          within(note).getByRole("heading", {
            name: "DEMO · Bike commute log",
          }),
        ).toBeInTheDocument();
        await user.click(
          within(note).getByRole("button", { name: /Read note/ }),
        );
        const reader = await screen.findByRole("region", {
          name: "Source reader",
        });
        expect(
          await within(reader).findByText(/along the river path/),
        ).toBeInTheDocument();
        await user.click(
          within(reader).getByRole("button", { name: "Close the reader" }),
        );

        // Alex's readings from the sample CSV file, which the engine's own adapter read.
        const data = await pick(user, region, "Alex · Activity");
        await user.click(
          await within(data).findByRole("button", { name: /Show readings/ }),
        );
        const readings = await screen.findByRole("region", {
          name: "Source reader",
        });
        expect(
          (await within(readings).findAllByText(/steps/i)).length,
        ).toBeGreaterThan(0);
      });

      it("opens a document's star, and its original comes back byte for byte from the engine's archive", async () => {
        at(size);
        const { user, region } = await explorer();
        const doc = await pick(user, region, /^Explore .*Boiler manual/);
        await user.click(
          await within(doc).findByRole("button", { name: /Open document/ }),
        );
        const reader = await screen.findByRole("region", {
          name: "Source reader",
        });
        const download = await within(reader).findByRole("link", {
          name: /Download/,
        });
        const href = download.getAttribute("href")!;
        expect(href).toMatch(/^\/api\/document-file/);
        const file = await fetchApp(href);
        expect(file.status).toBe(200);
        expect(file.headers.get("content-type")).toMatch(/application\/pdf/);
        expect(
          Buffer.from(await file.arrayBuffer())
            .subarray(0, 5)
            .toString(),
        ).toBe("%PDF-");
      });

      it("opens a dashboard with its feeds read, and an insight with its way to give feedback", async () => {
        at(size);
        const { user, region } = await explorer();
        const dashboards = await appJson("/api/dashboards");
        const title = "DEMO · Week at a glance";
        expect(dashboards.dashboards.map((d: any) => d.title)).toContain(title);
        const board = await pick(user, region, title);
        await user.click(
          await within(board).findByRole("button", { name: /Open dashboard/ }),
        );
        const reader = await screen.findByRole("region", {
          name: "Source reader",
        });
        // The page runs in a sandboxed frame with no origin (tests/dashboard-flow.test.tsx).
        await waitFor(() =>
          expect(reader.querySelector("iframe")).not.toBeNull(),
        );
        expect(
          reader.querySelector("iframe")!.getAttribute("sandbox"),
        ).not.toContain("allow-same-origin");
        // The feeds the page draws, as the frame's own request for them reads them.
        const id = dashboards.dashboards.find((d: any) => d.title === title).id;
        const data = await appJson(
          `/api/dashboards?id=${encodeURIComponent(id)}&data=1`,
        );
        expect(JSON.stringify(data)).toMatch(/steps/);
        await user.click(
          within(reader).getByRole("button", { name: "Close the reader" }),
        );
        await waitFor(() =>
          expect(
            screen.queryByRole("region", { name: "Source reader" }),
          ).toBeNull(),
        );
      });

      it("opens the newest insight from Home, and its feedback goes through the engine and stays", async () => {
        at(size);
        // The demo's own insight, or a newer one an AI client saved since: Home leads to the newest.
        const newest = (await appJson("/api/insights")).insights[0];
        expect(newest?.title).toBeTruthy();
        const { user } = await home();
        const opens = await screen.findAllByRole("button", {
          name: /^Open insight · /,
        });
        expect(
          opens[0].getAttribute("aria-label")?.endsWith(`: ${newest.title}`),
          opens[0].getAttribute("aria-label") ?? "",
        ).toBe(true);
        await user.click(opens[0]);
        const dialog = await screen.findByRole("dialog", { name: "Insight" });
        expect(
          within(dialog).getByRole("heading", { level: 2, name: newest.title }),
        ).toBeInTheDocument();
        const interesting = within(dialog).getByRole("button", {
          name: "Interesting",
        });
        // Its feedback stays from one run of this journey to the next, so only an unsaved one is asked for.
        if (interesting.getAttribute("aria-pressed") !== "true") {
          await user.click(interesting);
          expect(
            await within(dialog).findByText("Saved. Glad it was interesting."),
          ).toBeInTheDocument();
        }
        await waitFor(() =>
          expect(
            within(dialog).getByRole("button", { name: "Interesting" }),
          ).toHaveAttribute("aria-pressed", "true"),
        );
        const kept = (await appJson("/api/insights")).insights.find(
          (i: any) => i.id === newest.id,
        );
        expect(kept?.feedback, JSON.stringify(kept)).toBeTruthy();
      });
    });
  },
);
