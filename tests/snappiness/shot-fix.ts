import { chromium } from "playwright";
import { readFileSync } from "node:fs";
const [scratch, out, scheme] = process.argv.slice(2);
const seed = JSON.parse(readFileSync(scratch + "/seed.json", "utf8"));
const b = await chromium.launch();
const p = await b.newPage({ viewport: { width: 1440, height: 900 }, colorScheme: (scheme as any) ?? "light" });
await p.goto(`${seed.baseUrl}/sessions/${seed.long[0]}`);
await p.locator('[role="tab"]', { hasText: "Changes" }).first().click();
await p.waitForSelector(".rscroll");
await p.waitForTimeout(1500);
// Scroll the hunk with the long line sideways, as you would to read it.
const scrolled = await p.evaluate(() => {
  const s = [...document.querySelectorAll<HTMLElement>(".rscroll")].find((x) => x.scrollWidth > x.clientWidth + 20);
  if (!s) return null;
  s.scrollIntoView({ block: "center" });
  s.scrollLeft = 260;
  s.dispatchEvent(new Event("scroll"));
  return { cls: s.className, stickyNumbers: [...s.querySelectorAll(".rline__no")].filter((n) => getComputedStyle(n).position === "sticky").length,
    staticElsewhere: [...document.querySelectorAll(".rline__no")].filter((n) => getComputedStyle(n).position === "sticky").length };
});
await p.waitForTimeout(300);
console.log(JSON.stringify(scrolled));
await p.screenshot({ path: out, clip: process.env.CLIP ? { x: 1072, y: 10, width: 360, height: 600 } : undefined });
await b.close();
