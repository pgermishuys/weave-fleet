/**
 * Self-contained screenshot script: starts Vite dev server, captures a screenshot,
 * then tears down. No .NET backend needed (mock API plugin handles /api/* requests).
 *
 * Usage:
 *   bun run tsx quick-screenshot.ts              # screenshot app only
 *   bun run tsx quick-screenshot.ts --with-proto  # screenshot app + prototype
 */

import { chromium } from "@playwright/test";
import { spawn, type ChildProcess } from "node:child_process";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { setTimeout as sleep } from "node:timers/promises";

const HERE = resolve(fileURLToPath(import.meta.url), "..");
const CLIENT_DIR = resolve(HERE, "..", "..", "client");
const PROTO_PATH = resolve(HERE, "..", "..", ".weave", "prototype", "index.html");
const OUTPUT_DIR = resolve(HERE, "findings", "visual-compare");
const VITE_PORT = 3099; // Use a non-standard port to avoid conflicts
const VITE_URL = `http://127.0.0.1:${VITE_PORT}`;

const withProto = process.argv.includes("--with-proto");

async function startVite(): Promise<ChildProcess> {
  const child = spawn(
    process.execPath,
    ["node_modules/vite/bin/vite.js", "--host", "127.0.0.1", "--port", String(VITE_PORT)],
    { cwd: CLIENT_DIR, stdio: ["ignore", "pipe", "pipe"], windowsHide: true },
  );

  // Capture stdout/stderr for debugging
  let output = "";
  child.stdout?.on("data", (d) => { output += d.toString(); });
  child.stderr?.on("data", (d) => { output += d.toString(); });

  // Poll until ready
  const deadline = Date.now() + 15_000;
  while (Date.now() < deadline) {
    try {
      const resp = await fetch(VITE_URL);
      if (resp.ok) {
        console.log("vite startup output:\n" + output);
        return child;
      }
    } catch { /* not ready yet */ }
    await sleep(300);
  }
  console.error("vite output before timeout:\n" + output);
  child.kill("SIGTERM");
  throw new Error("Vite did not start within 15s");
}

async function main() {
  console.log("starting vite...");
  const vite = await startVite();
  console.log(`vite ready at ${VITE_URL}`);

  const browser = await chromium.launch({ headless: true });

  try {
    // App screenshot
    const appPage = await browser.newPage({ viewport: { width: 1440, height: 900 } });
    
    // Listen for failed requests and console errors
    appPage.on("requestfailed", (req) => {
      console.log(`FAILED: ${req.method()} ${req.url()} - ${req.failure()?.errorText}`);
    });
    appPage.on("response", (resp) => {
      if (resp.url().includes("/api/")) {
        console.log(`API: ${resp.status()} ${resp.url()}`);
      }
    });
    appPage.on("pageerror", (err) => {
      console.log(`PAGE ERROR: ${err.message}\n${err.stack}`);
    });
    appPage.on("console", (msg) => {
      if (msg.type() === "error") {
        console.log(`CONSOLE ERROR: ${msg.text()}`);
      }
    });

    await appPage.goto(VITE_URL, { timeout: 10_000, waitUntil: "domcontentloaded" });
    // Set light theme (prototype target) and wait for Vue to mount
    await appPage.evaluate(() => {
      document.documentElement.dataset.theme = "light";
      document.documentElement.style.colorScheme = "light";
      localStorage.setItem("weave-fleet-theme", JSON.stringify({ currentTheme: "light" }));
    });
    await appPage.waitForTimeout(3000);
    const appPath = resolve(OUTPUT_DIR, "app-vite-dev.png");
    await appPage.screenshot({ path: appPath });
    console.log(`app screenshot: ${appPath}`);
    await appPage.close();

    // Prototype screenshot (optional)
    if (withProto) {
      const protoPage = await browser.newPage({ viewport: { width: 1440, height: 900 } });
      await protoPage.goto(`file:///${PROTO_PATH.replace(/\\/g, "/")}`, {
        timeout: 10_000,
        waitUntil: "networkidle",
      });
      const protoPath = resolve(OUTPUT_DIR, "prototype-conversation.png");
      await protoPage.screenshot({ path: protoPath });
      console.log(`prototype screenshot: ${protoPath}`);
      await protoPage.close();
    }
  } finally {
    await browser.close();
    vite.kill("SIGTERM");
    console.log("done");
  }

  process.exit(0);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
