/**
 * Render the snappiness report: compare.json (numbers) + findings.json (causes, written by a person) + screenshots.
 *
 *   bun report.ts --results <dir> --findings <findings.json> --out <report.html> [--throttle 1]
 *
 * Self-contained HTML in Fleet's look (light and dark follow the reader's system setting). No JavaScript.
 */

import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const args = new Map<string, string>();
for (let i = 2; i < process.argv.length; i += 2) args.set(process.argv[i]!.replace(/^--/, ""), process.argv[i + 1] ?? "");
const DIR = args.get("results")!;
const cmp = JSON.parse(readFileSync(join(DIR, "compare.json"), "utf8"));
const findings = JSON.parse(readFileSync(args.get("findings")!, "utf8"));
const throttles: string[] = Object.keys(cmp.throttles);

const esc = (s: string) => s.replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]!);
const fmt = (x: number | null | undefined, unit = "") => (x == null ? "–" : `${x >= 100 ? x.toFixed(0) : x.toFixed(x >= 10 ? 0 : 1)}${unit}`);

const NAMES: Record<string, string> = {
  coldStart: "Cold start → session list", openSession: "Click a session → conversation", switchLong: "Switch between two long sessions",
  typing: "Typing in the composer", sendPrompt: "Send a prompt → bubble", railSessions: "Rail: back to Sessions", railSettings: "Rail: Settings",
  railAnalytics: "Rail: Analytics", railFleet: "Rail: Fleet", openChanges: "Open Changes", scrollLong: "Scroll a long conversation",
  newSessionPage: "Open the New session page", streamingList: "4 sessions streaming (reading another)", streamingWatch: "4 sessions streaming (watching one)",
};

function rowsFor(t: string) {
  const rows: any[] = cmp.throttles[t];
  const headline = new Map<string, any>();
  for (const r of rows) if (!headline.has(r.scenario)) headline.set(r.scenario, r);
  return { rows, headline: [...headline.values()] };
}

/** Paired horizontal bars (median), p95 as a thin tick. Text in ink tokens; color carries only identity. */
function chart(t: string): string {
  const { headline } = rowsFor(t);
  // Cold start (seconds) would squash every other bar; it has its own rows in the table.
  const items = headline.filter((r) => r.scenario !== "coldStart" && /Ms$/.test(r.metric) && (r.base.median != null || r.head.median != null));
  const max = Math.max(...items.flatMap((r) => [r.base.p95 ?? 0, r.head.p95 ?? 0, r.base.median ?? 0, r.head.median ?? 0]));
  const W = 760, L = 250, R = 110, rowH = 34, barH = 10, top = 8;
  const x = (v: number) => L + (v / max) * (W - L - R);
  const H = top + items.length * rowH + 26;
  let svg = `<svg viewBox="0 0 ${W} ${H}" width="100%" role="img" aria-label="Median latency per interaction, ${esc(cmp.base)} vs ${esc(cmp.head)}">`;
  for (const g of [0, 0.25, 0.5, 0.75, 1]) {
    const gx = x(max * g);
    svg += `<line x1="${gx}" x2="${gx}" y1="${top}" y2="${H - 22}" class="grid"/><text x="${gx}" y="${H - 6}" class="axis" text-anchor="middle">${fmt(max * g)} ms</text>`;
  }
  items.forEach((r, i) => {
    const y = top + i * rowH;
    svg += `<text x="${L - 10}" y="${y + rowH / 2 + 4}" class="label" text-anchor="end">${esc(NAMES[r.scenario] ?? r.scenario)}</text>`;
    ([["base", 0, "s1"], ["head", 1, "s2"]] as const).forEach(([k, j, cls]) => {
      const v = r[k];
      if (v.median == null) return;
      const by = y + 5 + j * (barH + 2);
      svg += `<g><title>${esc(NAMES[r.scenario] ?? r.scenario)} · ${k === "base" ? cmp.base : cmp.head}: median ${fmt(v.median)} ms, p95 ${fmt(v.p95)} ms (n=${v.n})</title>`;
      svg += `<rect x="${L}" y="${by}" width="${Math.max(2, x(v.median) - L)}" height="${barH}" rx="2" class="${cls}"/>`;
      if (v.p95 != null) svg += `<line x1="${x(v.p95)}" x2="${x(v.p95)}" y1="${by - 1}" y2="${by + barH + 1}" class="tick"/>`;
      svg += `</g>`;
    });
    const hv = r.head.median, bv = r.base.median;
    svg += `<text x="${Math.max(x(r.head.p95 ?? hv ?? 0), x(r.base.p95 ?? bv ?? 0)) + 6}" y="${y + rowH / 2 + 4}" class="value">${fmt(bv)} → ${fmt(hv)} ms</text>`;
  });
  return svg + `</svg>`;
}

function table(t: string): string {
  const { rows } = rowsFor(t);
  let h = `<table><thead><tr><th>Interaction</th><th>Metric</th><th>${esc(cmp.base)}<br>median / p95</th><th>${esc(cmp.head)}<br>median / p95</th><th>Change</th></tr></thead><tbody>`;
  let last = "";
  for (const r of rows) {
    const first = r.scenario !== last;
    last = r.scenario;
    const ch = r.base.median && r.head.median != null ? ((r.head.median - r.base.median) / r.base.median) * 100 : null;
    const sig = ch == null ? "" : ch > 25 ? "worse" : ch < -25 ? "better" : "";
    const skipped = r.head.skipped || r.base.skipped;
    h += `<tr class="${first ? "first" : ""}"><td>${first ? esc(NAMES[r.scenario] ?? r.scenario) : ""}</td><td>${esc(r.label)}</td>`
      + `<td>${r.base.skipped ? `<span class="muted">${esc(r.base.skipped.startsWith("not in") ? "not in this version" : "n/a")}</span>` : `${fmt(r.base.median)} / ${fmt(r.base.p95)}`}</td>`
      + `<td>${r.head.skipped ? '<span class="muted">n/a</span>' : `${fmt(r.head.median)} / ${fmt(r.head.p95)}`}</td>`
      + `<td class="${sig}">${skipped || ch == null ? "–" : `${ch >= 0 ? "+" : ""}${ch.toFixed(0)}%`}${sig === "worse" ? " ▲" : sig === "better" ? " ▼" : ""}</td></tr>`;
  }
  return h + `</tbody></table>`;
}

function img(path: string, alt: string): string {
  if (!existsSync(path)) return "";
  return `<figure><img alt="${esc(alt)}" src="data:image/png;base64,${readFileSync(path).toString("base64")}"><figcaption>${esc(alt)}</figcaption></figure>`;
}

const css = `
:root{--main-bg:#F3F2EF;--panel-bg:#fff;--card-bg:#fff;--border:rgba(26,25,24,.09);--text:#1A1918;--muted:#6E6A65;--accent:#5B6EC7;--accent-dim:rgba(91,110,199,.12);--s1:#2a78d6;--s2:#eb6834;--worse:#b4232a;--better:#15803d;color-scheme:light dark}
@media (prefers-color-scheme:dark){:root{--main-bg:#0d0d10;--panel-bg:#141418;--card-bg:#1b1b20;--border:rgba(255,255,255,.075);--text:#e8e8ec;--muted:#8e8e9a;--accent:#6366f1;--accent-dim:rgba(99,102,241,.15);--s1:#3987e5;--s2:#d95926;--worse:#f87171;--better:#4ade80}}
*{box-sizing:border-box}body{margin:0;background:var(--main-bg);color:var(--text);font:15px/1.55 "Inter Variable",Inter,system-ui,-apple-system,"Segoe UI",sans-serif}
main{max-width:980px;margin:0 auto;padding:28px 20px 60px}
section,.card{background:var(--panel-bg);border:1px solid var(--border);border-radius:12px;padding:18px 22px;margin:14px 0}
h1{font-size:22px;margin:0 0 4px}h2{font-size:17px;margin:0 0 10px}h3{font-size:15px;margin:16px 0 6px}
p{margin:6px 0}.muted{color:var(--muted)}code{font:13px "JetBrains Mono Variable",ui-monospace,Consolas,monospace;background:var(--accent-dim);padding:1px 5px;border-radius:5px}
pre{font:12.5px "JetBrains Mono Variable",ui-monospace,Consolas,monospace;background:var(--card-bg);border:1px solid var(--border);border-radius:8px;padding:10px 12px;overflow:auto}
.tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(200px,1fr));gap:10px}.tile{background:var(--card-bg);border:1px solid var(--border);border-radius:10px;padding:12px 14px}
.tile .n{font-size:24px;font-weight:600}.tile .k{color:var(--muted);font-size:13px}
table{width:100%;border-collapse:collapse;font-size:13.5px}th,td{text-align:left;padding:5px 8px;border-bottom:1px solid var(--border);vertical-align:top}th{color:var(--muted);font-weight:500}
tr.first td{border-top:1px solid var(--border)}td:nth-child(n+3){font-variant-numeric:tabular-nums;white-space:nowrap}
.worse{color:var(--worse);font-weight:600}.better{color:var(--better);font-weight:600}
.legend{display:flex;gap:16px;font-size:13px;color:var(--muted);margin:4px 0 8px}.sw{display:inline-block;width:12px;height:10px;border-radius:2px;margin-right:6px;vertical-align:-1px}
svg .grid{stroke:var(--border)}svg .axis{fill:var(--muted);font-size:11px}svg .label{fill:var(--text);font-size:12.5px}svg .value{fill:var(--muted);font-size:11.5px}
svg .s1{fill:var(--s1)}svg .s2{fill:var(--s2)}svg .tick{stroke:var(--text);stroke-width:1.5;opacity:.55}
figure{display:inline-block;vertical-align:top;width:300px;margin:12px 14px 0 0}figure img{width:100%;border:1px solid var(--border);border-radius:8px}figcaption{color:var(--muted);font-size:13px}
.ask{border-left:3px solid var(--accent);padding-left:12px}
ol li,ul li{margin:4px 0}`;

let html = `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>${esc(findings.title)}</title><style>${css}</style></head><body><main>`;
html += `<h1>${esc(findings.title)}</h1><p class="muted">${findings.subtitle}</p>`;
html += `<section><h2>Summary</h2>${findings.summary}<div class="tiles" style="margin-top:12px">${(findings.tiles ?? []).map((t: any) => `<div class="tile"><div class="n">${t.n}</div><div class="k">${t.k}</div></div>`).join("")}</div></section>`;
for (const t of throttles) {
  html += `<section><h2>Old vs now, CPU ${t}× ${t === "1" ? "(this machine as is)" : "(throttled: a slower laptop)"}</h2>`
    + `<div class="legend"><span><span class="sw" style="background:var(--s1)"></span>${esc(cmp.base)}</span><span><span class="sw" style="background:var(--s2)"></span>${esc(cmp.head)}</span><span>bar = median, tick = p95</span></div>`
    + chart(t) + `<h3>Every metric</h3>${table(t)}</section>`;
}
for (const c of findings.causes ?? []) html += `<section><h2>${esc(c.title)}</h2>${c.body}${(c.images ?? []).map((i: any) => img(i.path, i.alt)).join("")}</section>`;
html += `<section><h2>Method</h2>${findings.method}</section>`;
html += `<section><h2>Rerun it</h2>${findings.rerun}</section>`;
html += `<section><h2>Keeping it from regressing</h2>${findings.ci}</section>`;
if (findings.ask) html += `<section class="ask"><h2>Your call</h2>${findings.ask}</section>`;
html += `</main></body></html>`;
writeFileSync(args.get("out")!, html);
console.log(`wrote ${args.get("out")}`);
