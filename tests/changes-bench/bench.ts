/**
 * Times the session's Changes view on a scratch Fleet (see fleet.ts and setup.ts).
 *
 *   bun run bench.ts --base http://127.0.0.1:6390 --home C:\fleet-scratch\changes\home --label after [--ui review|list]
 *        [--only open-small,switch,...] [--runs 3] [--remote http://127.0.0.1:6391 --remote-home <dir> --remote-token <t>]
 *
 * --ui review: the review panel (#862 and later: per-file sections that load their own diffs).
 * --ui list:   the file list before it (a row per file; a click opens one file's diff in a tab).
 *
 * For each scenario it records, from a fresh browser context: time to the first file row, time to a diff shown, the
 * /diffs requests (count, bytes, slowest), how long until they settle, git processes Fleet ran (count, summed time,
 * from GIT_TRACE2_EVENT) and what the panel says at the end (Loading…, Couldn't load, No changes). Results go to
 * results-<label>.json next to this script.
 */
import { chromium, type Page, type Request, type BrowserContext } from "playwright-core";
import { existsSync, readFileSync, statSync, writeFileSync, openSync, readSync, closeSync, unlinkSync } from "node:fs";
import { join, resolve } from "node:path";

function arg(name: string, fallback?: string): string | undefined {
  const index = process.argv.indexOf(`--${name}`);
  return index >= 0 ? process.argv[index + 1] : fallback;
}
const base = arg("base")!;
const home = resolve(arg("home")!);
const label = arg("label", "run")!;
const ui = (arg("ui", "review") as "review" | "list");
const runs = Number(arg("runs", "3"));
const only = arg("only")?.split(",");
const port = new URL(base).port;
const sessions = JSON.parse(readFileSync(join(home, `sessions-${port}.json`), "utf8")) as Record<string, string>;
const trace = join(home, "git-trace2.jsonl");
const EDGE = "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe";

const ROW = ui === "review" ? "[data-review-file]" : ".changes-canvas__change";
const DIFF_LINE = ui === "review" ? "[data-review-file] .rline" : ".cm-changedLine, .cm-insertedLine, .cm-deletedChunk, .cm-changedText";

// ─── git time from trace2 ────────────────────────────────────────────────────

function traceOffset(): number {
  return existsSync(trace) ? statSync(trace).size : 0;
}
interface GitStats { processes: number; totalMs: number; byCommand: Record<string, { n: number; ms: number }>; failures: number; }
function gitSince(offset: number): GitStats {
  const stats: GitStats = { processes: 0, totalMs: 0, byCommand: {}, failures: 0 };
  if (!existsSync(trace)) return stats;
  const size = statSync(trace).size;
  if (size <= offset) return stats;
  const fd = openSync(trace, "r");
  const buffer = Buffer.alloc(size - offset);
  readSync(fd, buffer, 0, buffer.length, offset);
  closeSync(fd);
  const argvBySid = new Map<string, string>();
  for (const line of buffer.toString("utf8").split("\n")) {
    if (!line.startsWith("{")) continue;
    let event: any;
    try { event = JSON.parse(line); } catch { continue; }
    if (event.event === "start") {
      const argv: string[] = event.argv ?? [];
      const words = argv.slice(1).filter((word, i, all) => word !== "-c" && all[i - 1] !== "-c");
      const second = words[1] && !words[1].includes(":") && !words[1].startsWith("-") ? ` ${words[1]}` : words[1]?.startsWith("--") ? ` ${words[1]}` : "";
      argvBySid.set(event.sid, `${words[0]}${second}`);
    } else if (event.event === "exit" && argvBySid.has(event.sid)) {
      const ms = Math.round((event.t_abs ?? 0) * 1000);
      const key = argvBySid.get(event.sid)!;
      stats.processes += 1;
      stats.totalMs += ms;
      stats.byCommand[key] ??= { n: 0, ms: 0 };
      stats.byCommand[key].n += 1;
      stats.byCommand[key].ms += ms;
      if (event.code !== 0) stats.failures += 1;
    }
  }
  return stats;
}

// ─── requests ────────────────────────────────────────────────────────────────

interface Req { url: string; path: string; start: number; end: number | null; status: number | null; bytes: number; failed: string | null; }
function watchRequests(page: Page, t0: () => number) {
  const list: Req[] = [];
  const byRequest = new Map<Request, Req>();
  page.on("request", (request) => {
    const url = request.url();
    if (!url.includes("/api/")) return;
    const entry: Req = { url, path: new URL(url).pathname, start: performance.now() - t0(), end: null, status: null, bytes: 0, failed: null };
    byRequest.set(request, entry);
    list.push(entry);
  });
  page.on("requestfinished", async (request) => {
    const entry = byRequest.get(request);
    if (!entry) return;
    entry.end = performance.now() - t0();
    try {
      const response = await request.response();
      entry.status = response?.status() ?? null;
      entry.bytes = (await request.sizes()).responseBodySize;
    } catch { /* page closed */ }
  });
  page.on("requestfailed", (request) => {
    const entry = byRequest.get(request);
    if (!entry) return;
    entry.end = performance.now() - t0();
    entry.failed = request.failure()?.errorText ?? "failed";
  });
  return list;
}
const isDiffs = (r: Req) => /\/diffs(\/file)?$/.test(r.path);

async function settle(list: Req[], t0: () => number, quietMs = 1500, capMs = 90_000): Promise<number> {
  const start = performance.now();
  let quietSince = performance.now();
  while (performance.now() - start < capMs) {
    const inFlight = list.some((r) => isDiffs(r) && r.end === null);
    if (inFlight) quietSince = performance.now();
    else if (performance.now() - quietSince >= quietMs) break;
    await new Promise((r) => setTimeout(r, 50));
  }
  const ends = list.filter(isDiffs).map((r) => r.end ?? performance.now() - t0());
  return Math.round(Math.max(0, ...ends));
}

async function panelState(page: Page) {
  return page.evaluate((rowSelector) => {
    const root = document.querySelector(".changes-canvas");
    const text = root?.textContent ?? "";
    const count = (needle: string) => [...(root?.querySelectorAll(".rfile__note") ?? [])].filter((n) => n.textContent?.includes(needle)).length;
    return {
      rows: document.querySelectorAll(rowSelector).length,
      loadingNotes: count("Loading"),
      errorNotes: count("Couldn't load"),
      panelLoading: !!root?.querySelector('[data-testid="changes-loading"]'),
      noChanges: /No changes (in this session|on this branch)/.test(text),
    };
  }, ROW);
}

/** Polls until `selector` exists; returns ms since t0, or null. Opens the Changes tab if the panel shows another. */
async function timeTo(page: Page, selector: string, t0: () => number, capMs = 90_000): Promise<number | null> {
  const start = performance.now();
  while (performance.now() - start < capMs) {
    const found = await page.evaluate((s) => !!document.querySelector(s), selector).catch(() => false);
    if (found) return Math.round(performance.now() - t0());
    await page.evaluate(() => {
      if (document.querySelector(".changes-canvas")) return;
      const tab = [...document.querySelectorAll<HTMLElement>('[role="tab"]')].find((t) => /^\s*Changes/.test(t.textContent ?? ""));
      tab?.click();
    }).catch(() => {});
    await new Promise((r) => setTimeout(r, 25));
  }
  return null;
}

/** The list UI shows no diff until a row is clicked: click the first, as a user would. */
async function openFirstDiff(page: Page, t0: () => number): Promise<{ diffAt: number | null; clickToDiff: number | null }> {
  if (ui === "review") return { diffAt: await timeTo(page, DIFF_LINE, t0), clickToDiff: null };
  const clickedAt = performance.now() - t0();
  // A text file: the first rows can be a binary or the lockfile.
  await page.evaluate(() => {
    const rows = [...document.querySelectorAll<HTMLElement>(".changes-canvas__change")];
    (rows.find((r) => /\.(ts|md)\b/.test(r.textContent ?? "")) ?? rows[0])?.click();
  });
  const diffAt = await timeTo(page, DIFF_LINE, t0, 60_000);
  return { diffAt, clickToDiff: diffAt === null ? null : Math.round(diffAt - clickedAt) };
}

function summarize(list: Req[], fromMs = 0) {
  const diffs = list.filter((r) => isDiffs(r) && r.start >= fromMs);
  const durations = diffs.filter((r) => r.end !== null).map((r) => r.end! - r.start);
  return {
    diffsRequests: diffs.length,
    listRequests: diffs.filter((r) => r.path.endsWith("/diffs")).length,
    fileRequests: diffs.filter((r) => r.path.endsWith("/diffs/file")).length,
    bytes: diffs.reduce((s, r) => s + r.bytes, 0),
    slowestMs: Math.round(Math.max(0, ...durations)),
    medianMs: durations.length ? Math.round(durations.sort((a, b) => a - b)[Math.floor(durations.length / 2)]!) : 0,
    errors: diffs.filter((r) => r.failed || (r.status ?? 200) >= 400).map((r) => `${r.path.split("/").pop()} ${r.failed ?? r.status}`),
    otherApiDuring: list.filter((r) => !isDiffs(r) && r.start >= fromMs).length,
  };
}

// ─── scenarios ───────────────────────────────────────────────────────────────

const browser = await chromium.launch({ executablePath: EDGE, headless: true });

async function freshPage(): Promise<{ context: BrowserContext; page: Page }> {
  const context = await browser.newContext({ viewport: { width: 1680, height: 1000 } });
  const page = await context.newPage();
  return { context, page };
}

async function openScenario(key: string) {
  const { context, page } = await freshPage();
  let t0 = 0;
  const list = watchRequests(page, () => t0);
  const offset = traceOffset();
  t0 = performance.now();
  await page.goto(`${base}/sessions/${sessions[key]}`);
  const firstRow = await timeTo(page, ROW, () => t0);
  const firstDiffsRequest = list.find(isDiffs)?.start ?? null;
  const { diffAt, clickToDiff } = firstRow === null ? { diffAt: null, clickToDiff: null } : await openFirstDiff(page, () => t0);
  const settledAt = await settle(list, () => t0);
  const state = await panelState(page);
  const git = gitSince(offset);
  await context.close();
  return { firstRow, firstDiffsRequest: firstDiffsRequest && Math.round(firstDiffsRequest), firstDiff: diffAt, clickToDiff, settledAt, ...summarize(list), git, state };
}

async function switchScenario() {
  const { context, page } = await freshPage();
  let t0 = performance.now();
  const list = watchRequests(page, () => t0);
  await page.goto(`${base}/sessions/${sessions.large}`);
  await timeTo(page, ROW, () => t0);
  await settle(list, () => t0);
  const legs: unknown[] = [];
  for (const key of ["small", "big", "large", "small"]) {
    const offset = traceOffset();
    const before = list.length;
    t0 = performance.now();
    // Click the session's row as a user would; a row in a folded group falls back to the router's own navigation.
    const clicked = await page.evaluate((id) => {
      const row = document.querySelector<HTMLElement>(`.session-item-shell[data-session-id="${id}"] button.session-item`);
      if (row && row.offsetParent) { row.click(); return true; }
      history.pushState(history.state, "", `/sessions/${id}`);
      dispatchEvent(new PopStateEvent("popstate", { state: history.state }));
      return false;
    }, sessions[key]);
    if (!clicked) console.warn(`switch: ${key}'s row wasn't visible; navigated by URL`);
    // The row must be this session's: wait for its first file name.
    const expect = key === "small" ? "note-" : key === "big" ? ".md" : "module-";
    let firstRow: number | null = null;
    const start = performance.now();
    while (performance.now() - start < 60_000) {
      const ok = await page.evaluate(([s, e]) => [...document.querySelectorAll(s!)].some((r) => r.textContent?.includes(e!)), [ROW, expect]);
      if (ok) { firstRow = Math.round(performance.now() - t0); break; }
      await new Promise((r) => setTimeout(r, 25));
    }
    const legList = list.slice(before);
    const settledAt = await settle(list, () => t0);
    legs.push({ to: key, firstRow, settledAt, ...summarize(legList), git: gitSince(offset), state: await panelState(page) });
  }
  await context.close();
  return { legs };
}

/** Changes open on the large session while "the agent" writes files: one save every 400 ms for 8 s. */
async function liveScenario() {
  const { context, page } = await freshPage();
  let t0 = performance.now();
  const list = watchRequests(page, () => t0);
  await page.goto(`${base}/sessions/${sessions.large}`);
  await timeTo(page, ROW, () => t0);
  await settle(list, () => t0);
  await page.evaluate(() => {
    (window as any).__longTasks = 0;
    new PerformanceObserver((l) => { for (const e of l.getEntries()) (window as any).__longTasks += e.duration; }).observe({ type: "longtask", buffered: false });
  });
  const offset = traceOffset();
  const before = list.length;
  t0 = performance.now();
  // Two files drawn open at the top, two collapsed further down.
  const targets = ["src/module-0/file-0.ts", "src/module-0/file-2.ts", "src/module-20/file-0.ts", "src/module-25/file-4.ts"];
  const id = sessions.large;
  const originals: Record<string, string> = {};
  for (const path of targets) {
    const r = await fetch(`${base}/api/sessions/${id}/files/content?path=${encodeURIComponent(path)}`);
    originals[path] = ((await r.json()) as { content: string }).content;
  }
  let saves = 0;
  for (let i = 0; i < 20; i++) {
    const path = targets[i % targets.length]!;
    const current = (await (await fetch(`${base}/api/sessions/${id}/files/content?path=${encodeURIComponent(path)}`)).json()) as { content: string; hash: string };
    const response = await fetch(`${base}/api/sessions/${id}/files/content`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ path, content: current.content + `// live edit ${i}\n`, baseHash: current.hash }),
    });
    if (response.ok) saves++;
    await new Promise((r) => setTimeout(r, 400));
  }
  const writesDoneAt = Math.round(performance.now() - t0);
  const settledAt = await settle(list, () => t0);
  const longTaskMs = Math.round(await page.evaluate(() => (window as any).__longTasks));
  const result = { saves, writesDoneAt, settledAt, longTaskMs, ...summarize(list.slice(before)), git: gitSince(offset), state: await panelState(page) };
  for (const path of targets) {
    const current = (await (await fetch(`${base}/api/sessions/${id}/files/content?path=${encodeURIComponent(path)}`)).json()) as { hash: string };
    await fetch(`${base}/api/sessions/${id}/files/content`, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ path, content: originals[path], baseHash: current.hash }) });
  }
  await context.close();
  return result;
}

/** Ctrl+F in Changes on the large session: the find bar loads every collapsed file so it can search them (#918). */
async function findScenario() {
  if (ui !== "review") return { skipped: "no find before #918" };
  const { context, page } = await freshPage();
  let t0 = performance.now();
  const list = watchRequests(page, () => t0);
  await page.goto(`${base}/sessions/${sessions.large}`);
  await timeTo(page, ROW, () => t0);
  await settle(list, () => t0);
  const offset = traceOffset();
  const before = list.length;
  t0 = performance.now();
  await page.locator("[data-review-file]").first().click({ position: { x: 5, y: 5 } }).catch(() => {});
  await page.keyboard.press("Control+f");
  await page.keyboard.type("storefront change 299");
  // Unrelated work queued behind it: the session list's own request.
  const probeStart = performance.now();
  const probe = await page.evaluate(async () => { const s = performance.now(); await fetch("/api/sessions?limit=1"); return performance.now() - s; });
  const settledAt = await settle(list, () => t0, 1500, 180_000);
  const counter = await page.evaluate(() => document.querySelector(".cfind__count")?.textContent?.replace(/\s+/g, " ").trim() ?? null);
  const result = { settledAt, otherRequestWhileFinding: Math.round(probe), probeStartedAt: Math.round(probeStart - t0), counter, ...summarize(list.slice(before)), git: gitSince(offset), state: await panelState(page) };
  await context.close();
  return result;
}

/** A git that is busy: something holds .git/index.lock while Changes opens. */
async function busyScenario() {
  const lock = join(home, "proj", "lantern-shop", ".git", "index.lock");
  writeFileSync(lock, "");
  try {
    return await openScenario("large");
  } finally {
    unlinkSync(lock);
  }
}

const scenarios: Record<string, () => Promise<unknown>> = {
  "open-small": () => openScenario("small"),
  "open-large": () => openScenario("large"),
  "open-big": () => openScenario("big"),
  switch: switchScenario,
  live: liveScenario,
  find: findScenario,
  busy: busyScenario,
};

const results: Record<string, unknown[]> = {};
for (const [name, run] of Object.entries(scenarios)) {
  if (only && !only.includes(name)) continue;
  results[name] = [];
  const count = name === "live" || name === "find" || name === "busy" ? 1 : runs;
  for (let i = 0; i < count; i++) {
    const r = await run();
    results[name].push(r);
    console.log(name, JSON.stringify(r));
  }
}
await browser.close();
const out = join(import.meta.dir, `results-${label}.json`);
const previous = existsSync(out) ? JSON.parse(readFileSync(out, "utf8")) : {};
writeFileSync(out, JSON.stringify({ ...previous, ...results, meta: { label, ui, base, at: new Date().toISOString() } }, null, 2));
console.log(`wrote ${out}`);
