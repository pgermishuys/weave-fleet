/**
 * Minimal Playwright config. The beta-harness driver is not a test runner — there are no
 * `test(...)` declarations to discover. This config exists so `playwright codegen` and ad-hoc
 * scripts that import from `@playwright/test` use a sensible default.
 */
import { defineConfig, devices } from "@playwright/test";

const headed = process.env.HEADED === "1";

export default defineConfig({
  use: {
    ...devices["Desktop Chrome"],
    headless: !headed,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    actionTimeout: 10_000,
    navigationTimeout: 15_000,
  },
});
