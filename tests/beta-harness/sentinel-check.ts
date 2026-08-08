import { chromium } from "@playwright/test";
import { resolve } from "node:path";

const OUTPUT_PATH = resolve(import.meta.dirname!, "findings/visual-compare/sentinel-check.png");
const APP_URL = "http://127.0.0.1:5099";

async function main() {
  console.log("Launching browser...");
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  
  try {
    console.log(`Navigating to ${APP_URL}...`);
    await page.goto(APP_URL, { timeout: 15000, waitUntil: "domcontentloaded" });
    
    console.log("Waiting for app to render...");
    await page.waitForTimeout(5000);
    
    console.log(`Taking screenshot to ${OUTPUT_PATH}...`);
    await page.screenshot({ path: OUTPUT_PATH, fullPage: false });
    
    console.log("✓ Screenshot saved successfully!");
    console.log(`Check ${OUTPUT_PATH} for the red SENTINEL TEST banner at the top.`);
  } catch (error) {
    console.error("✗ Error:", error);
    throw error;
  } finally {
    await browser.close();
  }
}

main().catch(console.error);
