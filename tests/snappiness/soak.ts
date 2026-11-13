/**
 * Soak: does a tab get slower the longer it's open? Switch between sessions many times in one page and, every
 * `--every` switches, force a GC and record JS heap, DOM nodes, event listeners, and how long one long-session switch
 * takes. Growth that a GC can't reclaim is a leak; a switch time that climbs is the "it gets slower over the day" feel.
 *
 *   bun soak.ts --scratch <dir with seed.json> --label <name> [--switches 120] [--every 20] [--out results]
 */

import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { chromium } from "playwright";
import { PROBE } from "./lib/probe.ts";

const args = new Map<string, string>();
for (let i = 2; i < process.argv.length; i += 2) args.set(process.argv[i]!.replace(/^--/, ""), process.argv[i + 1] ?? "");
const scratch = resolve(args.get("scratch")!);
const label = args.get("label") ?? "soak";
const SWITCHES = Number(args.get("switches") ?? 120);
const EVERY = Number(args.get("every") ?? 20);
const OUT = resolve(args.get("out") ?? join(import.meta.dirname, "results"));
mkdirSync(OUT, { recursive: true });

const seed = JSON.parse(readFileSync(join(scratch, "seed.json"), "utf8"));
const byId = new Map<string, any>(seed.sessions.map((s: any) => [s.id, s]));
const shorts = seed.sessions.filter((s: any) => s.kind === "short").slice(-20).map((s: any) => s.id);
const mediums = seed.sessions.filter((s: any) => s.kind === "medium" && !s.streamer).map((s: any) => s.id);
// A day's worth of hopping around: mostly short sessions, now and then a medium or a long one.
const tour: string[] = [];
for (let i = 0; tour.length < SWITCHES; i++) tour.push(i % 6 === 5 ? seed.long[i % 3] : i % 3 === 2 ? mediums[i % mediums.length] : shorts[i % shorts.length]);

const shows = (text: string) => `() => { const el = document.querySelector('[data-testid="activity-stream"]'); return !!el && el.textContent.includes(${JSON.stringify(text)}); }`;

const browser = await chromium.launch({ args: ["--enable-precise-memory-info"] });
const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 } });
await ctx.addInitScript(PROBE);
const page = await ctx.newPage();
const cdp = await ctx.newCDPSession(page);
await cdp.send("Performance.enable");

async function go(id: string) {
  const row = page.locator(`[data-tree-leaf][data-session-id="${id}"]`).first();
  await row.scrollIntoViewIfNeeded();
  const b = (await row.boundingBox())!;
  await page.evaluate(([p]) => (window as any).__snap.arm(p, 250, 6000), [`() => location.pathname.endsWith(${JSON.stringify(id)}) && (${shows(byId.get(id).lastPrompt)})()`] as const);
  await page.mouse.click(b.x + b.width / 2, b.y + b.height / 2);
  return page.evaluate(() => (window as any).__snap.collect());
}

async function snapshot(after: number) {
  await cdp.send("HeapProfiler.collectGarbage");
  await page.waitForTimeout(300);
  const dom = (await cdp.send("Memory.getDOMCounters")) as any;
  const { metrics } = (await cdp.send("Performance.getMetrics")) as any;
  const m = Object.fromEntries(metrics.map((x: any) => [x.name, x.value]));
  // The same yardstick each time: switch to a long session and back.
  const a = await go(seed.long[0]);
  const b = await go(shorts[0]);
  const c = await go(seed.long[1]);
  return { after, heapMB: m.JSHeapUsedSize / 1048576, nodes: dom.nodes, listeners: dom.jsEventListeners, documents: dom.documents,
    longSwitchMs: (a.shownMs + c.shownMs) / 2, shortSwitchMs: b.shownMs };
}

await page.goto(`${seed.baseUrl}/sessions/${shorts[0]}`);
await page.waitForSelector("[data-tree-leaf]");
await page.waitForTimeout(1500);
const points = [await snapshot(0)];
console.log(JSON.stringify(points[0]));
for (let i = 0; i < tour.length; i++) {
  await go(tour[i]!);
  if ((i + 1) % EVERY === 0) {
    points.push(await snapshot(i + 1));
    console.log(JSON.stringify(points[points.length - 1]));
  }
}
await browser.close();
writeFileSync(join(OUT, `${label}.soak.json`), JSON.stringify({ label, switches: SWITCHES, points }, null, 2));
console.log(`wrote ${join(OUT, `${label}.soak.json`)}`);
