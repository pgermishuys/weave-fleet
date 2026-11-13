/**
 * Changes for a session on another machine: the browser asks that machine directly (bearer token, cross-origin), so
 * every /diffs request may cost a CORS preflight too. Start a second scratch Fleet with a known token, add it to the
 * first as a machine, make a session on it, then open that session from the first Fleet's page.
 *
 *   bun run fleet.ts start --bin <bin> --home C:\fleet-scratch\changes\home-remote --port 6393 --token bench-remote-token
 *   bun run remote.ts --base http://127.0.0.1:6390 --remote http://127.0.0.1:6393 --token bench-remote-token \
 *       --repo C:\fleet-scratch\changes\home\proj\lantern-shop --label main [--runs 3]
 */
import { chromium } from "playwright-core";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

function arg(name: string, fallback?: string): string {
  const index = process.argv.indexOf(`--${name}`);
  const value = index >= 0 ? process.argv[index + 1] : fallback;
  if (!value) throw new Error(`--${name} is required`);
  return value;
}
const base = arg("base");
const remote = arg("remote");
const token = arg("token");
const repo = arg("repo");
const label = arg("label");
const runs = Number(arg("runs", "3"));
const auth = { Authorization: `Bearer ${token}`, "Content-Type": "application/json" };

async function post(url: string, body: unknown, headers: Record<string, string> = { "Content-Type": "application/json" }) {
  const response = await fetch(url, { method: "POST", headers, body: JSON.stringify(body) });
  const text = await response.text();
  if (!response.ok && response.status !== 409 && !text.includes("already registered")) throw new Error(`POST ${url}: ${response.status} ${text}`);
  return text && response.ok ? JSON.parse(text) : null;
}

await post(`${remote}/api/workspace-roots`, { path: join(repo, "..") }, auth);
const machine = await post(`${base}/api/machines`, { baseUrl: remote, token });
const session = (await post(`${remote}/api/sessions`, { directory: repo, title: "Storefront on the other machine" }, auth)).session.id as string;
console.log("machine", JSON.stringify(machine)?.slice(0, 200), "session", session);

const browser = await chromium.launch({ executablePath: "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe" });
const results: unknown[] = [];
for (let i = 0; i < runs; i++) {
  const context = await browser.newContext({ viewport: { width: 1680, height: 1000 } });
  const page = await context.newPage();
  const cdp = await context.newCDPSession(page);
  await cdp.send("Network.enable");
  let t0 = Date.now();
  const requests = new Map<string, { url: string; method: string; start: number; end?: number; bytes?: number }>();
  cdp.on("Network.requestWillBeSent", (e: any) => {
    if (e.request.url.startsWith(remote) && /\/diffs/.test(e.request.url)) requests.set(e.requestId, { url: e.request.url, method: e.request.method, start: Date.now() - t0 });
  });
  // Preflights show up as their own CDP events in Chromium.
  let preflights = 0;
  cdp.on("Network.requestWillBeSentExtraInfo", () => {});
  cdp.on("Network.responseReceived", (e: any) => { if (e.type === "Preflight" && e.response.url.includes("/diffs")) preflights++; });
  cdp.on("Network.loadingFinished", (e: any) => { const r = requests.get(e.requestId); if (r) { r.end = Date.now() - t0; r.bytes = e.encodedDataLength; } });
  // As a user gets there: Fleet's own page, then a click on the other machine's session in the list. (A cold link
  // straight to /sessions/<id> spends ~15 s finding the session's machine before Changes asks anything.)
  await page.goto(`${base}/`);
  const rowSelector = `.session-item-shell[data-session-id="${session}"] button.session-item`;
  await page.waitForSelector(rowSelector, { timeout: 60_000 });
  const rowListedAt = Date.now() - t0;
  requests.clear();
  preflights = 0;
  t0 = Date.now();
  await page.click(rowSelector);
  let firstRow: number | null = null;
  let firstDiff: number | null = null;
  const start = Date.now();
  while (Date.now() - start < 90_000 && firstDiff === null) {
    const state = await page.evaluate(() => ({ row: !!document.querySelector("[data-review-file], .changes-canvas__change"), diff: !!document.querySelector("[data-review-file] .rline") }));
    if (state.row && firstRow === null) firstRow = Date.now() - t0;
    if (state.diff) firstDiff = Date.now() - t0;
    await new Promise((r) => setTimeout(r, 25));
  }
  await page.waitForTimeout(2000);
  const list = [...requests.values()].filter((r) => r.method !== "OPTIONS");
  results.push({
    firstRow,
    firstDiff,
    diffsRequests: list.length,
    preflights,
    kb: Math.round(list.reduce((s, r) => s + (r.bytes ?? 0), 0) / 1024),
    slowestMs: Math.max(0, ...list.map((r) => (r.end ?? 0) - r.start)),
    remoteSeen: list.length > 0,
    rowListedAt,
    firstRequestAt: list.length ? Math.min(...list.map((r) => r.start)) : null,
  });
  console.log("remote", JSON.stringify(results.at(-1)));
  await context.close();
}
await browser.close();
const out = join(import.meta.dir, `results-${label}.json`);
const previous = existsSync(out) ? JSON.parse(readFileSync(out, "utf8")) : {};
writeFileSync(out, JSON.stringify({ ...previous, remote: [...(previous.remote ?? []), ...results] }, null, 2));
