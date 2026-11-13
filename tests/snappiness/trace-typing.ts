/**
 * Where does a keystroke's time go? Trace typing in a long session (devtools.timeline) and sum the renderer's work
 * by kind: script, style recalc, layout (with how many layout objects each layout touched), paint.
 *
 *   bun trace-typing.ts --scratch <dir with seed.json> [--session long|medium] [--throttle 4] [--out trace.json]
 */

import { readFileSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { chromium } from "playwright";

const args = new Map<string, string>();
for (let i = 2; i < process.argv.length; i += 2) args.set(process.argv[i]!.replace(/^--/, ""), process.argv[i + 1] ?? "");
const seed = JSON.parse(readFileSync(join(resolve(args.get("scratch")!), "seed.json"), "utf8"));
const THROTTLE = Number(args.get("throttle") ?? 4);
const which = args.get("session") ?? "long";
const id: string = which === "long" ? seed.long[0] : seed.sessions.find((s: any) => s.kind === "medium" && !s.streamer).id;
const last = seed.sessions.find((s: any) => s.id === id).lastPrompt;

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
const early = await page.context().newCDPSession(page);
if (args.get("early")) await early.send("Emulation.setCPUThrottlingRate", { rate: THROTTLE });
const DELAY = Number(args.get("delay") ?? 40);
await page.goto(`${seed.baseUrl}/sessions/${id}`);
await page.waitForFunction((t) => document.querySelector('[data-testid="activity-stream"]')?.textContent?.includes(t), last, { timeout: 20000 });
await page.waitForTimeout(2000);
const input = page.getByTestId("prompt-input").first();
await input.click();
const cdp = await page.context().newCDPSession(page);
await cdp.send("Emulation.setCPUThrottlingRate", { rate: THROTTLE });

const events: any[] = [];
cdp.on("Tracing.dataCollected", (d: any) => events.push(...d.value));
await browser.startTracing; // (unused; CDP tracing below keeps it to this page's renderer)
await cdp.send("Tracing.start", { categories: "devtools.timeline,disabled-by-default-devtools.timeline", transferMode: "ReportEvents" });
const text = "please check the retry logic";
await page.keyboard.type(text, { delay: DELAY });
await page.waitForTimeout(300);
const done = new Promise((r) => cdp.once("Tracing.tracingComplete", r));
await cdp.send("Tracing.end");
await done;
await browser.close();

const sum: Record<string, { ms: number; count: number }> = {};
let layoutObjects = 0;
let layouts = 0;
let forced = 0;
for (const e of events) {
  if (e.ph !== "X" || typeof e.dur !== "number") continue;
  const name = e.name;
  if (!["Layout", "UpdateLayoutTree", "Paint", "FunctionCall", "EventDispatch", "RunMicrotasks", "PrePaint", "Layerize", "TimerFire", "FireAnimationFrame", "RunTask", "ParseHTML", "ResizeObserver", "IntersectionObserverController::computeIntersections", "UpdateLayerTree", "Commit"].includes(name)) continue;
  (sum[name] ??= { ms: 0, count: 0 }).ms += e.dur / 1000;
  sum[name].count++;
  if (name === "Layout") {
    layouts++;
    layoutObjects += e.args?.beginData?.dirtyObjects ?? 0;
    if (e.args?.beginData?.stackTrace || e.args?.data?.stackTrace) forced++;
  }
}
const keys = text.length;
const out = {
  session: which, throttle: THROTTLE, keystrokes: keys,
  perKeystroke: Object.fromEntries(Object.entries(sum).map(([k, v]) => [k, { ms: +(v.ms / keys).toFixed(2), count: +(v.count / keys).toFixed(2) }])),
  layoutObjectsPerLayout: layouts ? Math.round(layoutObjects / layouts) : 0,
  domNodes: null as number | null,
};
console.log(JSON.stringify(out, null, 2));
if (args.get("out")) writeFileSync(args.get("out")!, JSON.stringify({ summary: out, traceEvents: events }));

// The long tasks (>50 ms) and what ran inside each: the top-level children by name.
const tasks = events.filter((e) => e.ph === "X" && e.name === "RunTask" && e.dur > 50_000).sort((a, b) => a.ts - b.ts);
for (const t of tasks.slice(0, 6)) {
  const inner: Record<string, number> = {};
  for (const e of events) {
    if (e.ph !== "X" || e === t || e.tid !== t.tid || e.ts < t.ts || e.ts + e.dur > t.ts + t.dur) continue;
    if (!["Layout", "UpdateLayoutTree", "FunctionCall", "EventDispatch", "Paint", "ResizeObserver", "RunMicrotasks", "TimerFire", "FireAnimationFrame"].includes(e.name)) continue;
    inner[e.name] = (inner[e.name] ?? 0) + e.dur / 1000;
  }
  const layout = events.filter((e) => e.ph === "X" && e.name === "Layout" && e.tid === t.tid && e.ts >= t.ts && e.ts <= t.ts + t.dur);
  console.log(`long task ${(t.dur / 1000).toFixed(0)} ms:`, JSON.stringify(Object.fromEntries(Object.entries(inner).map(([k, v]) => [k, Math.round(v)]))),
    "layouts", layout.length, "objects", layout.map((l) => `${l.args?.beginData?.dirtyObjects}/${l.args?.beginData?.totalObjects}`).join(","),
    "roots", [...new Set(layout.flatMap((l) => (l.args?.endData?.layoutRoots ?? []).map((r: any) => r.nodeName ?? "")))].slice(0, 5).join(" "));
}
console.log(`long tasks: ${tasks.length}`);
