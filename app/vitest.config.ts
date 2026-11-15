import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";
// Tests read dates in one fixed zone, whatever the machine's own zone is.
process.env.TZ = "UTC";
export default defineConfig({
  plugins: [react()],
  test: {
    // Hosted builders (CI=true) run the whole-app tests far slower than a laptop.
    testTimeout: process.env.CI ? 30000 : 10000,
    // Bound jsdom/Recharts cold-import contention on developer machines too.
    maxWorkers: 2,
    environment: "jsdom",
    setupFiles: ["./tests/setup.ts"],
    css: false,
  },
});
