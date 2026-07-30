import { startFleet } from "./start-fleet.js";
import { chromium } from "@playwright/test";
import { resolve } from "node:path";

const OUTPUT_PATH = resolve(import.meta.dirname!, "findings/visual-compare/sentinel-check.png");

async function main() {
  console.log("Starting Fleet...");
  const fleet = await startFleet({ port: 5099 });
  console.log(`Fleet started at ${fleet.baseUrl}`);
  
  // Wait a bit for Fleet to fully initialize
  await new Promise(r => setTimeout(r, 5000));
  
  console.log("Launching browser...");
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  
  try {
    console.log(`Navigating to ${fleet.baseUrl}...`);
    await page.goto(fleet.baseUrl, { timeout: 15000, waitUntil: "domcontentloaded" });
    
    console.log("Waiting for app to render...");
    await page.waitForTimeout(5000);
    
    console.log(`Taking screenshot to ${OUTPUT_PATH}...`);
    await page.screenshot({ path: OUTPUT_PATH, fullPage: false });
    
    console.log("✓ Screenshot saved successfully!");
    console.log(`Check ${OUTPUT_PATH} for the red SENTINEL TEST banner at the top.`);
  } catch (error) {
    console.error("✗ Error:", error);
  } finally {
    await browser.close();
    await fleet.stop();
    console.log("Fleet stopped");
  }
}

main().catch(console.error);
