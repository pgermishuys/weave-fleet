import { chromium } from "/home/pgermishuys/camofox-browser/node_modules/playwright-core/index.mjs";
const b = await chromium.launch({ executablePath: process.env.CHROME, args: ["--no-sandbox"] });
const p = await b.newPage({ viewport: { width: 1280, height: 400 } });
await p.goto(process.argv[2], { waitUntil: "load", timeout: 15000 });
await p.waitForTimeout(500);
await p.screenshot({ path: process.argv[3] });
console.log("status text:", JSON.stringify(await p.textContent("#msg")), "| input:", JSON.stringify(await p.inputValue("#name")));
await b.close();
