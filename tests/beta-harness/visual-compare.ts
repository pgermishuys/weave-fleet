/**
 * visual-compare.ts
 *
 * Playwright-based visual comparison tool for agents. Screenshots the prototype
 * and/or the running Fleet app across multiple views.
 *
 * Agents can also use this as a library: import { openPrototype, openApp } and
 * drive navigation programmatically for ad-hoc comparisons.
 *
 * Prerequisites:
 *   - Playwright chromium installed (node node_modules/@playwright/test/cli.js install chromium)
 *   - For app screenshots: `cd client && npm run dev:mock` running on http://localhost:3002
 *
 * Usage:
 *   bun run tsx visual-compare.ts                    # Both prototype + app, all views
 *   bun run tsx visual-compare.ts --prototype-only   # Prototype only
 *   bun run tsx visual-compare.ts --app-only         # App only
 *   bun run tsx visual-compare.ts --interactive      # Opens browser, waits for manual nav
 */

import { chromium, type Browser, type Page } from "@playwright/test";
import { resolve, join } from "node:path";
import { mkdirSync } from "node:fs";

export const PROTOTYPE_PATH = resolve(import.meta.dirname!, "../../.weave/prototype/index.html");
export const OUTPUT_DIR = resolve(import.meta.dirname!, "findings/visual-compare");
export const APP_URL = process.env.FLEET_URL ?? "http://localhost:3002";

const VIEWPORT = { width: 1440, height: 900 };

// ---------------------------------------------------------------------------
// Reusable helpers (importable by other scripts)
// ---------------------------------------------------------------------------

export async function openPrototype(): Promise<{ browser: Browser; page: Page }> {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: VIEWPORT });
  await page.goto(`file://${PROTOTYPE_PATH.replace(/\\/g, "/")}`);
  await page.waitForLoadState("domcontentloaded");
  await page.waitForTimeout(1000); // allow fonts/icons to load
  return { browser, page };
}

export async function openApp(): Promise<{ browser: Browser; page: Page }> {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: VIEWPORT });
  await page.goto(APP_URL, { timeout: 10_000 });
  await page.waitForLoadState("networkidle");
  return { browser, page };
}

export async function screenshot(page: Page, name: string): Promise<string> {
  mkdirSync(OUTPUT_DIR, { recursive: true });
  const path = join(OUTPUT_DIR, `${name}.png`);
  await page.screenshot({ path, fullPage: false });
  return path;
}

// ---------------------------------------------------------------------------
// Prototype navigation sequences
// ---------------------------------------------------------------------------

interface PrototypeView {
  name: string;
  navigate: (page: Page) => Promise<void>;
}

const PROTOTYPE_VIEWS: PrototypeView[] = [
  {
    name: "settings",
    navigate: async (page) => {
      await page.click('[data-nav="settings"]');
      await page.waitForTimeout(300);
    },
  },
  {
    name: "automations",
    navigate: async (page) => {
      await page.click('[data-nav="automations"]');
      await page.waitForTimeout(300);
    },
  },
  {
    name: "board",
    navigate: async (page) => {
      await page.click('[data-nav="board"]');
      await page.waitForTimeout(300);
    },
  },
  {
    name: "artifact-viewer",
    navigate: async (page) => {
      // Reload to get back to default conversation state
      await page.goto(`file://${PROTOTYPE_PATH.replace(/\\/g, "/")}`);
      await page.waitForLoadState("domcontentloaded");
      await page.waitForTimeout(1000);
      const item = page.locator('[data-file="research"]');
      if (await item.isVisible()) {
        await item.click();
        await page.waitForTimeout(300);
      }
    },
  },
];

// ---------------------------------------------------------------------------
// App navigation sequences (mirror prototype views where possible)
// ---------------------------------------------------------------------------

interface AppView {
  name: string;
  navigate: (page: Page) => Promise<void>;
}

const APP_VIEWS: AppView[] = [
  {
    name: "conversation",
    navigate: async (page) => {
      await page.goto(APP_URL, { timeout: 10_000 });
      await page.waitForLoadState("networkidle");
      await page.waitForTimeout(500);
    },
  },
  {
    name: "settings",
    navigate: async (page) => {
      await page.goto(`${APP_URL}/settings`, { timeout: 10_000 });
      await page.waitForLoadState("networkidle");
      await page.waitForTimeout(500);
    },
  },
  {
    name: "board",
    navigate: async (page) => {
      await page.goto(`${APP_URL}/board`, { timeout: 10_000 });
      await page.waitForLoadState("networkidle");
      await page.waitForTimeout(500);
    },
  },
  {
    name: "analytics",
    navigate: async (page) => {
      await page.goto(`${APP_URL}/analytics`, { timeout: 10_000 });
      await page.waitForLoadState("networkidle");
      await page.waitForTimeout(500);
    },
  },
];

// ---------------------------------------------------------------------------
// Main
// ---------------------------------------------------------------------------

async function screenshotAllPrototype(): Promise<void> {
  const { browser, page } = await openPrototype();

  // Take initial conversation screenshot (default state)
  await screenshot(page, "prototype-conversation");

  // Navigate through all views
  for (const view of PROTOTYPE_VIEWS) {
    if (view.name === "conversation") continue; // already captured
    await view.navigate(page);
    await screenshot(page, `prototype-${view.name}`);
  }

  await browser.close();
}

async function screenshotAllApp(baseUrl?: string): Promise<void> {
  const url = baseUrl ?? APP_URL;
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: VIEWPORT });
  await page.goto(url, { timeout: 10_000 });
  await page.waitForLoadState("networkidle");

  // Capture the landing page first
  await screenshot(page, "app-landing");

  // Navigate through app views
  const routes = [
    { name: "settings", path: "/settings" },
    { name: "board", path: "/board" },
    { name: "analytics", path: "/analytics" },
    { name: "pipelines", path: "/pipelines" },
  ];

  for (const route of routes) {
    try {
      await page.goto(`${url}${route.path}`, { timeout: 10_000 });
      await page.waitForLoadState("networkidle");
      await page.waitForTimeout(500);
      await screenshot(page, `app-${route.name}`);
    } catch {
      process.stderr.write(`visual-compare: failed to screenshot app view "${route.name}"\n`);
    }
  }

  // Try clicking into a session if one exists on the landing page
  try {
    await page.goto(url, { timeout: 10_000 });
    await page.waitForLoadState("networkidle");
    await page.waitForTimeout(500);
    // Look for any session link/row to click into
    const sessionLink = page.locator('a[href*="/sessions/"]').first();
    if (await sessionLink.isVisible({ timeout: 2000 })) {
      await sessionLink.click();
      await page.waitForLoadState("networkidle");
      await page.waitForTimeout(500);
      await screenshot(page, "app-session-detail");
    }
  } catch {
    process.stderr.write("visual-compare: no session detail available to screenshot\n");
  }

  await browser.close();
}

async function interactive(): Promise<void> {
  process.stdout.write("visual-compare: opening prototype in headed browser...\n");
  process.stdout.write("visual-compare: navigate manually, then close the browser when done.\n");
  const browser = await chromium.launch({ headless: false });
  const page = await browser.newPage({ viewport: VIEWPORT });
  await page.goto(`file://${PROTOTYPE_PATH.replace(/\\/g, "/")}`);

  // Keep process alive until browser closes
  await new Promise<void>((resolve) => {
    browser.on("disconnected", () => resolve());
  });
  process.stdout.write("visual-compare: browser closed\n");
}

async function main(): Promise<void> {
  mkdirSync(OUTPUT_DIR, { recursive: true });

  const args = process.argv.slice(2);

  if (args.includes("--interactive")) {
    await interactive();
    return;
  }

  const prototypeOnly = args.includes("--prototype-only");
  const appOnly = args.includes("--app-only");
  const bootFleet = args.includes("--boot-fleet");

  // If --boot-fleet is set, start the .NET server (serves built SPA from wwwroot)
  let fleet: { baseUrl: string; stop(): Promise<void> } | null = null;
  if (bootFleet && !prototypeOnly) {
    const { startFleet } = await import("./start-fleet.js");
    process.stdout.write("visual-compare: booting fleet...\n");
    fleet = await startFleet({ port: 5099 });
    process.stdout.write(`visual-compare: fleet ready at ${fleet.baseUrl}\n`);
    // Override APP_URL to point at the fleet instance
    (globalThis as Record<string, unknown>).__fleetUrl = fleet.baseUrl;
  }

  if (!appOnly) {
    process.stdout.write("visual-compare: screenshotting prototype...\n");
    await screenshotAllPrototype();
    process.stdout.write(`visual-compare: prototype screenshots saved to ${OUTPUT_DIR}\n`);
  }

  if (!prototypeOnly) {
    const effectiveUrl = fleet?.baseUrl ?? APP_URL;
    process.stdout.write(`visual-compare: screenshotting app at ${effectiveUrl}...\n`);
    try {
      await screenshotAllApp(effectiveUrl);
      process.stdout.write(`visual-compare: app screenshots saved to ${OUTPUT_DIR}\n`);
    } catch (e) {
      process.stderr.write(`visual-compare: could not reach app at ${effectiveUrl}\n`);
      process.stderr.write(`  ${(e as Error).message}\n`);
    }
  }

  if (fleet) {
    await fleet.stop();
    process.stdout.write("visual-compare: fleet stopped\n");
  }

  process.stdout.write("visual-compare: done\n");
}

await main();
