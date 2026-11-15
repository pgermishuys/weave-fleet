// Visits every screen in screens.mjs in every variant, against the client served in Vite mock mode, and records per
// screen: a screenshot, console errors and warnings, page errors, failed requests, sideways overflow, an axe-core
// WCAG 2 A/AA pass and icon-only controls with no name. Writes <out>/crawl.json and <out>/shots/*.png.
//
// Usually run through run.sh, which builds the client and serves it. On its own:
//   node crawl.mjs --base=http://127.0.0.1:4173 [--out=out] [--set=nightly|themes] [--only=<id substring>]
//     [--variants=a,b] [--workers=3]
//
// The SignalR hub is faked (negotiate + WebSocket) so a session's conversation renders without an API.
import { chromium } from "playwright";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { LABS_ON, SCREENS, VARIANTS, sessionSnapshot, variantsOf } from "./screens.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const arg = (name, fallback) => {
  const hit = process.argv.slice(2).find((a) => a.startsWith(`--${name}=`));
  return hit ? hit.slice(name.length + 3) : fallback;
};
const BASE = arg("base", process.env.FLEET_UI_BASE ?? "http://127.0.0.1:4173");
const OUT = arg("out", join(here, "out"));
const ONLY = arg("only");
const WORKERS = Math.max(1, Number(arg("workers", "3")));
const variantNames = arg("variants")?.split(",") ?? variantsOf(arg("set", "nightly"));
for (const n of variantNames) if (!VARIANTS[n]) throw new Error(`unknown variant ${n}`);

const AXE = readFileSync(createRequire(import.meta.url).resolve("axe-core/axe.min.js"), "utf8");
mkdirSync(join(OUT, "shots"), { recursive: true });

const RS = "\x1e";
async function fakeHub(context) {
  await context.route("**/hubs/session-events/negotiate**", (route) =>
    route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        connectionId: "c1",
        connectionToken: "c1",
        negotiateVersion: 1,
        availableTransports: [{ transport: "WebSockets", transferFormats: ["Text", "Binary"] }],
      }),
    }),
  );
  await context.routeWebSocket(/\/hubs\/session-events/, (ws) => {
    ws.onMessage((raw) => {
      for (const frame of String(raw).split(RS).filter(Boolean)) {
        let msg;
        try {
          msg = JSON.parse(frame);
        } catch {
          continue;
        }
        if (msg.protocol) ws.send("{}" + RS);
        else if (msg.type === 6) ws.send(JSON.stringify({ type: 6 }) + RS);
        else if (msg.type === 1 && msg.invocationId) {
          const result = msg.target === "SubscribeToSessionAsync" ? sessionSnapshot() : null;
          ws.send(JSON.stringify({ type: 3, invocationId: msg.invocationId, result }) + RS);
        }
      }
    });
  });
}

const strip = (url) => url.replace(BASE, "");
// Mock mode answers every /api route it doesn't know with 501 (vite-plugin-mock-api.ts): a gap in the mock, not a
// failure of the page. They're reported, not gated.
const isMockGap = (url, status) => status === 501 && new URL(url).pathname.startsWith("/api/");

async function visit(browser, vName, screen) {
  const v = VARIANTS[vName];
  const started = Date.now();
  const context = await browser.newContext({
    viewport: { width: v.w, height: v.h },
    colorScheme: v.os ?? v.theme,
    deviceScaleFactor: 1,
    isMobile: !!v.phone,
    hasTouch: !!v.phone,
    reducedMotion: "reduce",
  });
  await context.addInitScript((t) => {
    try {
      localStorage.setItem("weave:theme", t);
    } catch {}
  }, v.theme);
  await context.route("**/api/preferences", (route) => {
    if (route.request().method() !== "GET") return route.fulfill({ status: 204, body: "" });
    return route.fulfill({ contentType: "application/json", body: JSON.stringify(v.labs ? LABS_ON : {}) });
  });
  await fakeHub(context);
  const page = await context.newPage();
  const rec = {
    screen: screen.id,
    variant: vName,
    phone: !!v.phone,
    path: screen.path,
    console: [],
    pageErrors: [],
    failed: [],
    mockGaps: [],
    overflow: null,
    axe: null,
    unnamedButtons: [],
    finalUrl: null,
    title: null,
    ms: 0,
  };
  const gapUrls = new Set();
  page.on("console", (m) => {
    if (m.type() !== "error" && m.type() !== "warning") return;
    const url = m.location()?.url ?? "";
    rec.console.push({ type: m.type(), text: m.text().slice(0, 300), url: strip(url) });
  });
  page.on("pageerror", (e) => rec.pageErrors.push(String(e.message).slice(0, 300)));
  page.on("requestfailed", (r) => {
    const u = r.url();
    if (u.startsWith("data:")) return;
    rec.failed.push({ url: strip(u), method: r.method(), why: r.failure()?.errorText });
  });
  page.on("response", (r) => {
    if (r.status() < 400) return;
    if (isMockGap(r.url(), r.status())) {
      gapUrls.add(strip(r.url()));
      rec.mockGaps.push(`${r.request().method()} ${new URL(r.url()).pathname}`);
    } else rec.failed.push({ url: strip(r.url()), method: r.request().method(), status: r.status() });
  });
  try {
    await page.goto(BASE + screen.path, { waitUntil: "domcontentloaded", timeout: 30000 });
    await page.waitForLoadState("networkidle", { timeout: 8000 }).catch(() => {});
    if (screen.waitFor) {
      await page
        .locator(screen.waitFor)
        .first()
        .waitFor({ timeout: 15000 })
        .catch(() => rec.pageErrors.push(`CRAWL: ${screen.waitFor} never appeared`));
    }
    if (screen.setup) {
      await screen.setup(page);
      await page.waitForTimeout(500);
    }
    await page.waitForTimeout(700);
    // Then until the page stops changing: 500 ms without a DOM change (5 s at most). A screen whose data arrives late
    // (the analytics tables and charts on a busy machine) was measured half-drawn on some runs and whole on others,
    // and a count that moves on its own can't be a ratchet.
    rec.settleMs = await page.evaluate(
      () =>
        new Promise((resolve) => {
          const started = performance.now();
          let last = started;
          const observer = new MutationObserver(() => (last = performance.now()));
          observer.observe(document.body, { subtree: true, childList: true, attributes: true, characterData: true });
          const tick = () => {
            const now = performance.now();
            if (now - last >= 500 || now - started >= 5000) {
              observer.disconnect();
              resolve(Math.round(now - started));
            } else setTimeout(tick, 100);
          };
          setTimeout(tick, 100);
        }),
    );
    rec.finalUrl = strip(page.url());
    rec.title = await page.title();
    rec.overflow = await page.evaluate(() => {
      const vw = document.documentElement.clientWidth;
      const pageW = document.documentElement.scrollWidth;
      const offenders = [];
      for (const el of document.querySelectorAll("body *")) {
        const r = el.getBoundingClientRect();
        if (r.width === 0 || r.height === 0 || r.right <= vw + 1) continue;
        // Inside a scroll or clip container that itself fits: a horizontal scroller, which is allowed.
        let p = el.parentElement;
        let contained = false;
        while (p && p !== document.body) {
          if (/(auto|scroll|hidden|clip)/.test(getComputedStyle(p).overflowX) && p.getBoundingClientRect().right <= vw + 1) {
            contained = true;
            break;
          }
          p = p.parentElement;
        }
        if (!contained) {
          offenders.push({
            tag: el.tagName.toLowerCase(),
            cls: String(el.className?.baseVal ?? el.className ?? "").slice(0, 80),
            right: Math.round(r.right),
            text: (el.innerText || "").slice(0, 40),
          });
        }
      }
      return { vw, pageW, pageOverflows: pageW > vw + 1, offenderCount: offenders.length, offenders: offenders.slice(0, 8) };
    });
    rec.unnamedButtons = await page.evaluate(() => {
      const out = [];
      for (const b of document.querySelectorAll("button, [role=button], a[href]")) {
        const r = b.getBoundingClientRect();
        if (!r.width || !r.height || (b.innerText || "").trim()) continue;
        const named = !!(b.getAttribute("aria-label") || b.getAttribute("aria-labelledby") || b.getAttribute("title"));
        if (!named) out.push(b.outerHTML.slice(0, 160));
      }
      return out;
    });
    await page.addScriptTag({ content: AXE });
    const runOnly = v.axeRules
      ? { type: "rule", values: v.axeRules }
      : { type: "tag", values: ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"] };
    rec.axe = await page.evaluate(async (runOnly) => {
      const r = await window.axe.run(document, { runOnly, resultTypes: ["violations"] });
      return r.violations.map((x) => ({
        id: x.id,
        impact: x.impact,
        help: x.help,
        count: x.nodes.length,
        targets: x.nodes.slice(0, 4).map((n) => ({ t: n.target.join(" "), s: (n.failureSummary || "").split("\n")[1]?.slice(0, 160) })),
      }));
    }, runOnly);
    await page.screenshot({ path: join(OUT, "shots", `${screen.id}--${vName}.png`) });
  } catch (e) {
    rec.pageErrors.push("CRAWL: " + String(e.message).split("\n")[0].slice(0, 300));
  }
  // Chromium logs every 4xx/5xx response as a console error; the ones for mock gaps aren't the page's fault.
  for (const c of rec.console) c.mockGap = c.text.startsWith("Failed to load resource") && gapUrls.has(c.url);
  rec.mockGaps = [...new Set(rec.mockGaps)].sort();
  rec.ms = Date.now() - started;
  await context.close();
  return rec;
}

const jobs = [];
for (const vName of variantNames) {
  for (const screen of SCREENS) if (!ONLY || screen.id.includes(ONLY)) jobs.push({ vName, screen });
}
const browser = await chromium.launch({ args: ["--disable-dev-shm-usage"] });
const results = new Array(jobs.length);
let next = 0;
await Promise.all(
  Array.from({ length: Math.min(WORKERS, jobs.length) }, async () => {
    while (next < jobs.length) {
      const i = next++;
      const { vName, screen } = jobs[i];
      const rec = await visit(browser, vName, screen);
      results[i] = rec;
      const errors = rec.console.filter((c) => c.type === "error" && !c.mockGap).length;
      const axe = rec.axe?.reduce((n, x) => n + x.count, 0) ?? "?";
      console.log(
        `${vName.padEnd(18)} ${screen.id.padEnd(24)} console=${errors} page=${rec.pageErrors.length} failed=${rec.failed.length} ` +
          `overflow=${rec.overflow?.offenderCount ?? "?"} axe=${axe} gaps=${rec.mockGaps.length} ${rec.ms}ms`,
      );
    }
  }),
);
await browser.close();
writeFileSync(join(OUT, "crawl.json"), JSON.stringify(results, null, 1));
console.log(`wrote ${join(OUT, "crawl.json")} (${results.length} screens)`);
