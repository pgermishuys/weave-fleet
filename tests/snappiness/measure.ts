/**
 * Measure interaction latency on a running, seeded scratch Fleet.
 *
 *   bun measure.ts --scratch <dir with seed.json> --label <name> [--runs 7] [--only a,b] [--throttle 1] [--out results]
 *
 * Writes <out>/<label>.json (every sample, plus median/p95 per scenario) and <out>/<label>/<scenario>.cpuprofile
 * (one profiled run per scenario, taken after the timed runs so profiling never skews the timings).
 */

import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { chromium, type Browser, type BrowserContext, type CDPSession, type Page } from "playwright";
import { PROBE } from "./lib/probe.ts";

type Sample = Record<string, number | null | boolean | string | undefined>;
interface ScenarioResult { name: string; samples: Sample[]; skipped?: string; requests?: number[] }

const args = new Map<string, string>();
for (let i = 2; i < process.argv.length; i += 2) args.set(process.argv[i]!.replace(/^--/, ""), process.argv[i + 1] ?? "");
const scratch = resolve(args.get("scratch") ?? "");
const label = args.get("label") ?? "run";
const RUNS = Number(args.get("runs") ?? 7);
const THROTTLE = Number(args.get("throttle") ?? 1);
const OUT = resolve(args.get("out") ?? join(import.meta.dirname, "results"));
const ONLY = args.get("only")?.split(",").filter(Boolean);
// What-if experiments: extra CSS injected into every page (e.g. --css ".rline__no{position:static!important}").
const CSS = args.get("css");
const seed = JSON.parse(readFileSync(join(scratch, "seed.json"), "utf8"));
const BASE: string = seed.baseUrl;
const byId = new Map<string, any>(seed.sessions.map((s: any) => [s.id, s]));
const LONG: string[] = seed.long;
const MEDIUM: string[] = seed.sessions.filter((s: any) => s.kind === "medium" && !s.streamer).map((s: any) => s.id);
// The most recent short sessions: the list shows the 100 newest.
const SHORT: string[] = seed.sessions.filter((s: any) => s.kind === "short").map((s: any) => s.id).slice(-40).reverse();
const STREAMERS: string[] = seed.streamers;
mkdirSync(join(OUT, label), { recursive: true });

const VIEWPORT = { width: 1440, height: 900 };
let browser: Browser;
// Requests of the page being measured, so a timed window can count its own.
let REQ: { t: number; url: string }[] = [];
const REQ_URLS: Record<string, Record<string, number>> = {};
let CURRENT = "";

async function newPage(): Promise<{ ctx: BrowserContext; page: Page; cdp: CDPSession; requests: { t: number; url: string }[] }> {
  const ctx = await browser.newContext({ viewport: VIEWPORT, deviceScaleFactor: 1 });
  await ctx.addInitScript(PROBE);
  if (CSS) await ctx.addInitScript(`document.addEventListener("DOMContentLoaded", () => { const s = document.createElement("style"); s.textContent = ${JSON.stringify(CSS)}; document.head.appendChild(s); });`);
  const page = await ctx.newPage();
  const cdp = await ctx.newCDPSession(page);
  if (THROTTLE > 1) await cdp.send("Emulation.setCPUThrottlingRate", { rate: THROTTLE });
  await cdp.send("Profiler.enable");
  await cdp.send("Profiler.setSamplingInterval", { interval: 200 });
  const requests: { t: number; url: string }[] = [];
  page.on("request", (r) => requests.push({ t: Date.now(), url: r.url() }));
  REQ = requests;
  return { ctx, page, cdp, requests };
}

/** Arm the probe, run the action, wait for the predicate and the DOM to settle. */
async function timed(page: Page, pred: string, action: () => Promise<unknown>, opts: { settleMs?: number; maxMs?: number } = {}) {
  const before = REQ.length;
  await page.evaluate(([p, s, m]) => (window as any).__snap.arm(p, s, m), [pred, opts.settleMs ?? 300, opts.maxMs ?? 5000] as const);
  await action();
  const r = await page.evaluate(() => (window as any).__snap.collect());
  if (r.timeout) throw new Error(`timed out waiting for: ${pred}`);
  r.requests = REQ.length - before;
  r.requestPaths = REQ.slice(before).map((x) => new URL(x.url).pathname.replace(/[0-9a-f-]{36}/g, ":id"));
  return r;
}

async function settle(page: Page, pred = "() => true", maxMs = 8000) {
  await page.evaluate(([p, m]) => (window as any).__snap.arm(p, 400, m), [pred, maxMs] as const);
  return page.evaluate(() => (window as any).__snap.collect());
}

const showsText = (text: string, scope = '[data-testid="activity-stream"]') =>
  `() => { const el = document.querySelector(${JSON.stringify(scope)}); return !!el && el.textContent.includes(${JSON.stringify(text)}); }`;
const rowsShown = `() => document.querySelectorAll("[data-tree-leaf]").length >= 10`;
const onPath = (prefix: string) => `() => location.pathname.startsWith(${JSON.stringify(prefix)})`;

/** Resolve a target to a point before timing starts, so locator queries never run inside a measurement. */
async function pointOf(loc: ReturnType<Page["locator"]>) {
  await loc.scrollIntoViewIfNeeded();
  const b = await loc.boundingBox();
  if (!b) throw new Error(`not visible: ${loc}`);
  return { x: b.x + b.width / 2, y: b.y + b.height / 2 };
}
const clickAt = (page: Page, p: { x: number; y: number }) => page.mouse.click(p.x, p.y);

function sessionRow(page: Page, id: string) {
  return page.locator(`[data-tree-leaf][data-session-id="${id}"]`).first();
}

async function profile<T>(cdp: CDPSession, name: string, fn: () => Promise<T>): Promise<T> {
  await cdp.send("Profiler.start");
  try {
    return await fn();
  } finally {
    const { profile } = (await cdp.send("Profiler.stop")) as any;
    writeFileSync(join(OUT, label, `${name}.cpuprofile`), JSON.stringify(profile));
  }
}

function slim(r: any): Sample {
  const { loaf, requestPaths, ...rest } = r;
  if (requestPaths) for (const p of requestPaths) { const m = (REQ_URLS[CURRENT] ??= {}); m[p] = (m[p] ?? 0) + 1; }
  // Keep the worst long animation frame's script attribution for the report.
  const worst = (loaf ?? []).sort((a: any, b: any) => b.dur - a.dur)[0];
  return { ...rest, worstFrameMs: worst?.dur ?? 0, worstFrameScripts: worst ? JSON.stringify(worst.scripts.slice(0, 5)) : undefined };
}

/** Run `once` RUNS times, then once more under the CPU profiler. */
async function repeat(name: string, cdp: CDPSession, requests: { t: number }[], once: (i: number) => Promise<Sample>, runs = RUNS): Promise<ScenarioResult> {
  const samples: Sample[] = [];
  const reqCounts: number[] = [];
  for (let i = 0; i < runs; i++) {
    const before = requests.length;
    samples.push(await once(i));
    reqCounts.push(requests.length - before);
  }
  await profile(cdp, name, () => once(runs));
  return { name, samples, requests: reqCounts };
}

// ── Scenarios ────────────────────────────────────────────────────────────────

const scenarios: Record<string, () => Promise<ScenarioResult>> = {
  async coldStart() {
    const samples: Sample[] = [];
    const reqs: number[] = [];
    for (let i = 0; i <= RUNS; i++) {
      const { ctx, page, cdp, requests } = await newPage();
      if (i === RUNS) await cdp.send("Profiler.start");
      // Armed from the probe's own start (t0 = 0, navigation start) so it sees the very first frame.
      await page.addInitScript(`window.__snap.arm(${JSON.stringify(rowsShown)}, 400, 10000, 0)`);
      await page.goto(`${BASE}/sessions/${LONG[0]}`);
      const r: any = await page.evaluate(() => (window as any).__snap.collect());
      const nav: any = await page.evaluate(() => {
        const n = performance.getEntriesByType("navigation")[0] as PerformanceNavigationTiming;
        const fcp = performance.getEntriesByName("first-contentful-paint")[0];
        const js = performance.getEntriesByType("resource").filter((e: any) => e.name.endsWith(".js"));
        return { domContentLoaded: n.domContentLoadedEventEnd, fcp: fcp?.startTime ?? null, jsFiles: js.length, jsKB: js.reduce((a: number, e: any) => a + (e.decodedBodySize || 0), 0) / 1024 };
      });
      // The conversation itself, on top of the session list.
      const conv: any = await page.waitForFunction(new Function(`return (${showsText(byId.get(LONG[0]).lastPrompt)})()`) as any, null, { timeout: 15000 })
        .then(() => page.evaluate(() => performance.now()));
      if (i === RUNS) {
        const { profile } = (await cdp.send("Profiler.stop")) as any;
        writeFileSync(join(OUT, label, "coldStart.cpuprofile"), JSON.stringify(profile));
      } else {
        samples.push({ ...slim(r), ...nav, conversationMs: conv });
        reqs.push(requests.length);
      }
      await ctx.close();
    }
    return { name: "coldStart", samples, requests: reqs };
  },

  async openSession() {
    // A session not opened before in this page: click it in the list, until its conversation is on screen.
    const { ctx, page, cdp, requests } = await newPage();
    await page.goto(`${BASE}/sessions/${SHORT[0]}`);
    await settle(page, rowsShown);
    const res = await repeat("openSession", cdp, requests, async (i) => {
      const id = MEDIUM[i % MEDIUM.length]!;
      const row = sessionRow(page, id);
      const pt = await pointOf(row);
      const r = await timed(page, `() => location.pathname.endsWith(${JSON.stringify(id)}) && (${showsText(byId.get(id).lastPrompt)})()`, () => clickAt(page, pt));
      await sessionRow(page, SHORT[(i + 1) % SHORT.length]!).click();
      await settle(page, showsText(byId.get(SHORT[(i + 1) % SHORT.length]!).lastPrompt));
      return slim(r);
    });
    await ctx.close();
    return res;
  },

  async switchLong() {
    const { ctx, page, cdp, requests } = await newPage();
    await page.goto(`${BASE}/sessions/${LONG[0]}`);
    await settle(page, showsText(byId.get(LONG[0]).lastPrompt));
    // Visit the second once so both are loaded: this measures switching, not first load.
    await sessionRow(page, LONG[1]!).click();
    await settle(page, showsText(byId.get(LONG[1]).lastPrompt));
    const res = await repeat("switchLong", cdp, requests, async (i) => {
      const id = LONG[i % 2 === 0 ? 0 : 1]!;
      const row = sessionRow(page, id);
      const pt = await pointOf(row);
      const r = await timed(page, `() => location.pathname.endsWith(${JSON.stringify(id)}) && (${showsText(byId.get(id).lastPrompt)})()`, () => clickAt(page, pt));
      return slim(r);
    });
    await ctx.close();
    return res;
  },

  async typing() {
    const { ctx, page, cdp, requests } = await newPage();
    await page.goto(`${BASE}/sessions/${LONG[0]}`);
    await settle(page, showsText(byId.get(LONG[0]).lastPrompt));
    const input = page.getByTestId("prompt-input").first();
    await input.click();
    const text = "please check the retry logic again";
    const res = await repeat("typing", cdp, requests, async () => {
      await page.evaluate(() => { const s = (window as any).__snap; s.keys = []; s.trackKeys = true; s.ev = []; });
      const t0: number = await page.evaluate(() => performance.now());
      await page.keyboard.type(text, { delay: 40 });
      await page.waitForTimeout(150);
      const r: any = await page.evaluate((t0) => {
        const s = (window as any).__snap; s.trackKeys = false;
        const keys = s.keys.slice().sort((a: number, b: number) => a - b);
        // Event Timing: one interaction per keystroke; its duration is input to next paint (16 ms floor).
        const per = new Map<number, number>();
        for (const e of s.ev) if (e.id) per.set(e.id, Math.max(per.get(e.id) ?? 0, e.dur));
        const w = s.window(t0, performance.now());
        return { keyMedianMs: keys[Math.floor(keys.length / 2)], keyP95Ms: keys[Math.floor(keys.length * 0.95)], keyMaxMs: keys[keys.length - 1],
          inpMs: Math.max(0, ...per.values()), slowKeys: [...per.values()].filter((d) => d > 50).length, longTasks: w.longTasks, longTaskMs: w.longTaskMs };
      }, t0);
      await page.keyboard.press("Control+A");
      await page.keyboard.press("Delete");
      await page.waitForTimeout(200);
      return r;
    });
    await ctx.close();
    return res;
  },

  async sendPrompt() {
    const { ctx, page, cdp, requests } = await newPage();
    await page.goto(`${BASE}/sessions/${LONG[2]}`);
    await settle(page, showsText(byId.get(LONG[2]).lastPrompt));
    const input = page.getByTestId("prompt-input").first();
    const res = await repeat("sendPrompt", cdp, requests, async (i) => {
      const text = `Snappiness check prompt ${i} ${Date.now()}`;
      await input.click();
      await input.fill(text);
      await page.waitForTimeout(100);
      const r = await timed(page, `() => [...document.querySelectorAll('[data-testid="message-item"]')].some((m) => m.textContent.includes(${JSON.stringify(text)}))`,
        () => page.keyboard.press("Enter"), { settleMs: 300, maxMs: 4000 });
      // Let the scripted reply finish before the next prompt.
      await page.waitForTimeout(600);
      return slim(r);
    });
    await ctx.close();
    return res;
  },

  async railViews() {
    const { ctx, page, cdp, requests } = await newPage();
    await page.goto(`${BASE}/sessions/${LONG[0]}`);
    await settle(page, showsText(byId.get(LONG[0]).lastPrompt));
    const rail = (name: string) => page.locator(`nav [aria-label="${name}"], [aria-label="App sections"] [aria-label="${name}"]`).first();
    const views: [string, string][] = [["Settings", onPath("/settings")], ["Analytics", onPath("/analytics")], ["Fleet", onPath("/fleet")],
      ["Sessions", `() => (${rowsShown})() && !/^\\/(settings|analytics|fleet)/.test(location.pathname)`]];
    const present = [];
    for (const [name, pred] of views) if (await rail(name).count()) present.push([name, pred] as const);
    const samples: Record<string, Sample[]> = {};
    const reqs: Record<string, number[]> = {};
    for (let i = 0; i <= RUNS; i++) {
      if (i === RUNS) await cdp.send("Profiler.start");
      for (const [name, pred] of present) {
        const before = requests.length;
        CURRENT = `rail${name}`;
        const pt = await pointOf(rail(name));
        const r = await timed(page, pred, () => clickAt(page, pt), { settleMs: 300, maxMs: 5000 });
        if (i < RUNS) { (samples[name] ??= []).push(slim(r)); (reqs[name] ??= []).push(requests.length - before); }
      }
      if (i === RUNS) {
        const { profile } = (await cdp.send("Profiler.stop")) as any;
        writeFileSync(join(OUT, label, "railViews.cpuprofile"), JSON.stringify(profile));
      }
    }
    await ctx.close();
    const results = present.map(([name]) => ({ name: `rail${name}`, samples: samples[name]!, requests: reqs[name] }));
    const missing = views.filter(([n]) => !present.some(([p]) => p === n)).map(([n]) => ({ name: `rail${n}`, samples: [], skipped: "not in this version's rail" }));
    return { name: "railViews", samples: [], children: [...results, ...missing] } as any;
  },

  async openChanges() {
    const { ctx, page, cdp, requests } = await newPage();
    const id = LONG[0]!;
    // Edit files in its project after the session started, so Changes has something to show.
    const dir = join(scratch, "home", "proj", byId.get(id).project, "src");
    for (let k = 0; k < 10; k++) {
      const f = join(dir, `module${k}.ts`);
      writeFileSync(f, readFileSync(f, "utf8").replace(/= (\d+);/g, (m, n) => (Number(n) % 3 === 0 ? `= ${Number(n) + 1}; // tuned` : m)));
    }
    writeFileSync(join(dir, "snappiness-added.ts"), Array.from({ length: 120 }, (_, j) => `export const added${j} = ${j};`).join("\n") + "\n");
    await page.goto(`${BASE}/sessions/${SHORT[0]}`);
    await settle(page, rowsShown);
    const res = await repeat("openChanges", cdp, requests, async (i) => {
      await sessionRow(page, id).click();
      await settle(page, showsText(byId.get(id).lastPrompt));
      // Opening Changes = picking the Changes tab in the right panel (from Files), until its file list shows the
      // file this run added after the session started.
      const tab = page.locator('[role="tab"]', { hasText: "Changes" }).first();
      if (!(await tab.count())) throw new Error("no Changes tab");
      await page.locator('[role="tab"]', { hasText: "Files" }).first().click();
      await settle(page);
      const pt = await pointOf(tab);
      const r = await timed(page, `() => {
          const t = [...document.querySelectorAll('[role="tab"]')].find((b) => b.textContent.includes("Changes"));
          if (!t || t.getAttribute("aria-selected") !== "true") return false;
          const p = document.getElementById(t.getAttribute("aria-controls") || "") || document.querySelector('[role="tabpanel"]');
          return !!p && p.textContent.includes("snappiness-added");
        }`, () => clickAt(page, pt), { settleMs: 300, maxMs: 8000 });
      await sessionRow(page, SHORT[(i + 1) % SHORT.length]!).click();
      await settle(page, rowsShown);
      return slim(r);
    }).catch((e) => ({ name: "openChanges", samples: [], skipped: String(e).slice(0, 200) }));
    await ctx.close();
    return res;
  },

  async scrollLong() {
    const { ctx, page, cdp, requests } = await newPage();
    await page.goto(`${BASE}/sessions/${LONG[1]}`);
    await settle(page, showsText(byId.get(LONG[1]).lastPrompt));
    const res = await repeat("scrollLong", cdp, requests, async () => {
      // Back to the newest message, then wheel up through the history for ~3 s.
      const box = await page.getByTestId("activity-stream").first().boundingBox();
      if (!box) throw new Error("no activity stream");
      await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
      await page.keyboard.press("End").catch(() => {});
      await page.evaluate(() => { for (const el of document.querySelectorAll<HTMLElement>('[data-testid="activity-stream"], [data-testid="activity-stream"] *')) if (el.scrollHeight > el.clientHeight + 50 && getComputedStyle(el).overflowY !== "visible") el.scrollTop = el.scrollHeight; });
      await settle(page);
      const scroller = () => { for (const el of document.querySelectorAll<HTMLElement>('[data-testid="activity-stream"], [data-testid="activity-stream"] *')) if (el.scrollHeight > el.clientHeight + 50 && getComputedStyle(el).overflowY !== "visible") return el.scrollTop; return -1; };
      const top0: number = await page.evaluate(scroller);
      const frames = page.evaluate(() => (window as any).__snap.frames(3200));
      for (let k = 0; k < 60; k++) { await page.mouse.wheel(0, -500); await page.waitForTimeout(45); }
      const f: any = await frames;
      const w: any = await page.evaluate(([a, b]) => (window as any).__snap.window(a, b), [f.start, f.end]);
      const top1: number = await page.evaluate(scroller);
      const gaps = f.gaps.slice(1).sort((a: number, b: number) => a - b);
      return {
        scrolledPx: top0 - top1,
        frames: gaps.length, fps: (gaps.length / (f.end - f.start)) * 1000,
        p95FrameMs: gaps[Math.floor(gaps.length * 0.95)], maxFrameMs: gaps[gaps.length - 1],
        droppedFrames: gaps.reduce((a: number, g: number) => a + Math.max(0, Math.round(g / 16.7) - 1), 0),
        longTasks: w.longTasks, longTaskMs: w.longTaskMs, maxLongTaskMs: w.maxLongTaskMs,
      };
    });
    await ctx.close();
    return res;
  },

  async newSessionPage() {
    const { ctx, page, cdp, requests } = await newPage();
    await page.goto(`${BASE}/sessions/${LONG[0]}`);
    await settle(page, showsText(byId.get(LONG[0]).lastPrompt));
    const res = await repeat("newSessionPage", cdp, requests, async () => {
      const pt = await pointOf(page.locator("button.panel-action-button", { hasText: "New session" }).first());
      const r = await timed(page, `() => !!document.querySelector('[data-testid="new-session-form"]')`, () => clickAt(page, pt), { settleMs: 300, maxMs: 5000 });
      await sessionRow(page, LONG[0]!).click();
      await settle(page, showsText(byId.get(LONG[0]).lastPrompt));
      return slim(r);
    });
    await ctx.close();
    return res;
  },

  streamingList: () => streaming("streamingList", LONG[0]!),
  streamingWatch: () => streaming("streamingWatch", STREAMERS[0]!),
};

/**
 * Four sessions stream at once while you type: is the page still responsive? streamingList reads an idle session
 * (only the session list updates); streamingWatch watches one of the streaming conversations.
 * Each run uses one queued streaming reply per streamer (seed.ts streamRuns), so runs are capped.
 */
async function streaming(name: string, viewId: string): Promise<ScenarioResult> {
  {
    const runs = Math.min(RUNS, Math.floor((seed.streamRuns ?? 12) / 2) - 1);
    const { ctx, page, cdp, requests } = await newPage();
    await page.goto(`${BASE}/sessions/${viewId}`);
    await settle(page, showsText(byId.get(viewId).lastPrompt));
    const input = page.getByTestId("prompt-input").first();
    const res = await repeat(name, cdp, requests, async (i) => {
      await input.click();
      await Promise.all(STREAMERS.map((id) => fetch(`${BASE}/api/sessions/${id}/prompt`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ text: `Keep going (${i})` }) })));
      await page.evaluate(() => { const s = (window as any).__snap; s.keys = []; s.trackKeys = true; s.ev = []; });
      const frames = page.evaluate(() => (window as any).__snap.frames(6000));
      await page.waitForTimeout(800);
      await page.keyboard.type("typing while four sessions stream", { delay: 60 });
      const f: any = await frames;
      const r: any = await page.evaluate(([a, b]) => {
        const s = (window as any).__snap; s.trackKeys = false;
        const keys = s.keys.slice().sort((x: number, y: number) => x - y);
        return { ...s.window(a, b), keyP95Ms: keys[Math.floor(keys.length * 0.95)], keyMedianMs: keys[Math.floor(keys.length / 2)] };
      }, [f.start, f.end]);
      await page.keyboard.press("Control+A");
      await page.keyboard.press("Delete");
      const gaps = f.gaps.slice(1).sort((a: number, b: number) => a - b);
      // Wait for the streams to finish before the next run.
      for (const id of STREAMERS) {
        for (let k = 0; k < 100; k++) {
          const st = await fetch(`${BASE}/api/sessions/${id}/status`).then((x) => x.json());
          if (st.activityStatus === "idle") break;
          await page.waitForTimeout(200);
        }
      }
      await page.waitForTimeout(300);
      return {
        longTasks: r.longTasks, longTaskMs: r.longTaskMs, maxLongTaskMs: r.maxLongTaskMs, longTaskPctOfTime: (r.longTaskMs / (f.end - f.start)) * 100,
        p95FrameMs: gaps[Math.floor(gaps.length * 0.95)], maxFrameMs: gaps[gaps.length - 1], keyP95Ms: r.keyP95Ms, keyMedianMs: r.keyMedianMs,
      };
    }, runs);
    await ctx.close();
    return res;
  }
}

// ── Summaries ────────────────────────────────────────────────────────────────

function quantile(xs: number[], q: number): number | null {
  const v = xs.filter((x) => typeof x === "number" && Number.isFinite(x)).sort((a, b) => a - b);
  if (!v.length) return null;
  const pos = (v.length - 1) * q;
  const lo = Math.floor(pos);
  return v[lo]! + (v[Math.min(lo + 1, v.length - 1)]! - v[lo]!) * (pos - lo);
}

function summarise(r: ScenarioResult) {
  const keys = new Set<string>();
  for (const s of r.samples) for (const [k, v] of Object.entries(s)) if (typeof v === "number") keys.add(k);
  const stats: Record<string, { median: number | null; p95: number | null }> = {};
  for (const k of keys) {
    const xs = r.samples.map((s) => s[k] as number);
    stats[k] = { median: quantile(xs, 0.5), p95: quantile(xs, 0.95) };
  }
  if (r.requests?.length && !stats.requests) stats.requests = { median: quantile(r.requests, 0.5), p95: quantile(r.requests, 0.95) };
  return stats;
}

// Fleet (mission control) is a Labs view: switch it on so its rail entry is there to measure (a no-op where it doesn't exist).
await fetch(`${BASE}/api/preferences/features.mission-control.enabled`, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ value: "true" }) }).catch(() => {});

browser = await chromium.launch({ args: ["--enable-precise-memory-info", "--disable-renderer-backgrounding", "--disable-background-timer-throttling"] });
const results: ScenarioResult[] = [];
const started = Date.now();
for (const [name, run] of Object.entries(scenarios)) {
  if (ONLY && !ONLY.includes(name)) continue;
  CURRENT = name;
  const t0 = Date.now();
  try {
    const r: any = await run();
    for (const x of r.children ?? [r]) results.push(x);
    console.log(`${label} ${name}: ${((Date.now() - t0) / 1000).toFixed(1)} s`);
  } catch (e) {
    console.log(`${label} ${name}: FAILED ${String(e).slice(0, 300)}`);
    results.push({ name, samples: [], skipped: `failed: ${String(e).slice(0, 300)}` });
  }
}
await browser.close();

const summary = Object.fromEntries(results.map((r) => [r.name, { skipped: r.skipped, runs: r.samples.length, stats: summarise(r) }]));
writeFileSync(join(OUT, `${label}.json`), JSON.stringify({ label, baseUrl: BASE, runs: RUNS, throttle: THROTTLE, at: new Date().toISOString(), seconds: (Date.now() - started) / 1000, summary, requestPaths: REQ_URLS, results }, null, 2));
console.log(`wrote ${join(OUT, `${label}.json`)}`);
