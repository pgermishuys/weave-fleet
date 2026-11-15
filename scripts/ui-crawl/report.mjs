// Turns <out>/crawl.json into the night's verdict and report:
//   <out>/summary.json  counts per gate, axe counts per rule, mock gaps (the next night compares with this)
//   <out>/summary.md    the same for the workflow's run summary, with the change since --previous
//   <out>/report.html   every screen: its findings and screenshot (open it beside shots/)
// Exits 1 when a gate fails (with --gate), so the workflow's run goes red.
//
//   node report.mjs [--out=out] [--previous=<an earlier summary.json>] [--gate]
//
// Gates (a red night): page errors (and screens the crawl couldn't reach), console errors, failed requests, sideways
// overflow at 390 px, and the axe ratchet: each axe WCAG 2 A/AA rule's count against axe-baseline.json, over the
// screens this run visited. A rule above its baseline fails (naming the screens that grew); below it passes and asks
// you to lower the baseline, so the counts only go down. Report only: icon-only controls with no name, overflow at
// 1440 px, console warnings, and mock gaps (/api routes mock mode answers with 501).
//
//   node report.mjs --out=<out> --update-baseline   write this run's axe counts as axe-baseline.json (a full crawl only)
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { SCREENS, VARIANTS } from "./screens.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const argv = process.argv.slice(2);
const arg = (name) => argv.find((a) => a.startsWith(`--${name}=`))?.slice(name.length + 3);
const OUT = arg("out") ?? join(here, "out");
const previousFile = arg("previous");
const crawl = JSON.parse(readFileSync(join(OUT, "crawl.json"), "utf8"));
const previous = previousFile && existsSync(previousFile) ? JSON.parse(readFileSync(previousFile, "utf8")) : null;

const GATES = {
  pageErrors: { label: "Page errors", find: (r) => r.pageErrors },
  consoleErrors: {
    label: "Console errors",
    find: (r) => r.console.filter((c) => c.type === "error" && !c.mockGap).map((c) => (c.url ? `${c.text} (${c.url})` : c.text)),
  },
  failedRequests: {
    label: "Failed requests",
    find: (r) => r.failed.map((f) => `${f.method ?? ""} ${f.url} ${f.status ?? f.why ?? ""}`.trim()),
  },
  phoneOverflow: {
    label: "Sideways overflow at 390 px",
    find: (r) => {
      if (!r.phone || !r.overflow) return [];
      const o = r.overflow;
      if (!o.pageOverflows && !o.offenderCount) return [];
      return [`page ${o.pageW}px in a ${o.vw}px window; ${o.offenderCount} element${o.offenderCount === 1 ? "" : "s"} past the edge`,
        ...o.offenders.map((x) => `<${x.tag} class="${x.cls}"> right ${x.right}px ${x.text ? `“${x.text}”` : ""}`.trim())];
    },
  },
};

const key = (r) => `${r.screen}--${r.variant}`;
const gates = {};
for (const [id, g] of Object.entries(GATES)) {
  const screens = crawl.map((r) => ({ screen: key(r), items: g.find(r) })).filter((s) => s.items.length);
  gates[id] = { label: g.label, screens: screens.length, items: screens.reduce((n, s) => n + s.items.length, 0), failing: screens };
}

const axe = {};
for (const r of crawl) {
  for (const v of r.axe ?? []) {
    axe[v.id] ??= { help: v.help, impact: v.impact, nodes: 0, screens: 0 };
    axe[v.id].nodes += v.count;
    axe[v.id].screens += 1;
  }
}
// --- the axe ratchet
const BASELINE = join(here, "axe-baseline.json");
const UPDATE = argv.includes("--update-baseline");
const perScreen = {};
for (const r of crawl) for (const v of r.axe ?? []) (perScreen[v.id] ??= {})[key(r)] = v.count;
const visited = new Set(crawl.map(key));
const fullCrawl = crawl.length === SCREENS.length * Object.values(VARIANTS).filter((v) => !v.onRequest).length;
if (UPDATE) {
  if (!fullCrawl) {
    console.error(`--update-baseline needs a full crawl (every screen in every nightly variant); this one has ${crawl.length} screens`);
    process.exit(2);
  }
  const rules = Object.fromEntries(
    Object.keys(perScreen).sort().map((id) => [
      id,
      { total: axe[id].nodes, screens: Object.fromEntries(Object.entries(perScreen[id]).sort(([a], [b]) => a.localeCompare(b))) },
    ]),
  );
  writeFileSync(BASELINE, JSON.stringify({ rules }, null, 2) + "\n");
  console.log(`Axe baseline written: ${Object.entries(rules).map(([id, r]) => `${id} ${r.total}`).join(", ")}`);
}
const baseline = existsSync(BASELINE) ? JSON.parse(readFileSync(BASELINE, "utf8")) : { rules: {} };
const ratchet = { rose: [], fell: [] };
for (const id of [...new Set([...Object.keys(perScreen), ...Object.keys(baseline.rules)])].sort()) {
  const was = baseline.rules[id]?.screens ?? {};
  const now = perScreen[id] ?? {};
  // Only the screens this run visited, so a run with --only compares like with like.
  const sum = (counts) => Object.entries(counts).filter(([k]) => visited.has(k)).reduce((n, [, c]) => n + c, 0);
  const [nowTotal, wasTotal] = [sum(now), sum(was)];
  if (nowTotal > wasTotal) {
    const grew = [...visited].filter((k) => (now[k] ?? 0) > (was[k] ?? 0)).map((k) => `${k} ${was[k] ?? 0} → ${now[k]}`);
    ratchet.rose.push({ id, now: nowTotal, was: wasTotal, grew });
  } else if (nowTotal < wasTotal) ratchet.fell.push({ id, now: nowTotal, was: wasTotal });
}
if (UPDATE) ratchet.rose = [];

const mockGaps = [...new Set(crawl.flatMap((r) => r.mockGaps ?? []))].sort();
const report = {
  unnamedControls: crawl.reduce((n, r) => n + r.unnamedButtons.length, 0),
  deskOverflowScreens: crawl.filter((r) => !r.phone && (r.overflow?.offenderCount || r.overflow?.pageOverflows)).length,
  consoleWarnings: crawl.reduce((n, r) => n + r.console.filter((c) => c.type === "warning").length, 0),
};
const red = Object.values(gates).some((g) => g.screens > 0) || ratchet.rose.length > 0;
const summary = {
  screens: crawl.length,
  red,
  gates: Object.fromEntries(Object.entries(gates).map(([id, g]) => [id, { screens: g.screens, items: g.items }])),
  axe: Object.fromEntries(Object.entries(axe).map(([id, a]) => [id, { nodes: a.nodes, screens: a.screens }])),
  axeNodes: Object.values(axe).reduce((n, a) => n + a.nodes, 0),
  ratchet: { rose: ratchet.rose.map(({ id, now, was }) => ({ id, now, was })), fell: ratchet.fell },
  report,
  mockGaps,
  seconds: Math.round(crawl.reduce((n, r) => n + r.ms, 0) / 1000),
};
writeFileSync(join(OUT, "summary.json"), JSON.stringify(summary, null, 1));

// --- summary.md
const delta = (now, before) => {
  if (before === undefined || before === null) return "";
  const d = now - before;
  return d === 0 ? " (same)" : ` (${d > 0 ? "+" : "−"}${Math.abs(d)})`;
};
const md = [];
md.push(`## UI crawl: ${red ? "red" : "green"}, ${crawl.length} screens`);
md.push(previous ? `Compared with ${arg("previous-label") ?? "the previous run"}.` : "No earlier run to compare with.");
md.push("", "| Gate | Screens | Findings |", "|---|---:|---:|");
for (const [id, g] of Object.entries(gates)) {
  md.push(`| ${g.label} | ${g.screens}${delta(g.screens, previous?.gates?.[id]?.screens)} | ${g.items} |`);
}
for (const [id, g] of Object.entries(gates)) {
  if (!g.failing.length) continue;
  md.push("", `### ${g.label}`);
  for (const s of g.failing.slice(0, 30)) md.push(`- \`${s.screen}\`: ${s.items.slice(0, 3).map((x) => x.replaceAll("|", "\\|")).join("; ")}`);
  if (g.failing.length > 30) md.push(`- … and ${g.failing.length - 30} more screens (report.html)`);
}
md.push("", "### Axe ratchet (scripts/ui-crawl/axe-baseline.json)", "");
if (!ratchet.rose.length && !ratchet.fell.length) md.push("Every rule is at its baseline.");
for (const r of ratchet.rose) {
  md.push(`- ✗ \`${r.id}\`: ${r.now}, baseline ${r.was} (+${r.now - r.was}). Screens that grew:`);
  for (const g of r.grew.slice(0, 15)) md.push(`  - \`${g}\``);
}
for (const r of ratchet.fell) md.push(`- ↓ \`${r.id}\`: ${r.now}, baseline ${r.was} (−${r.was - r.now}).`);
if (ratchet.fell.length) {
  md.push("", "Lower the baseline so it stays down: `UI_CRAWL_UPDATE_BASELINE=1 scripts/ui-crawl/run.sh` (or `node scripts/ui-crawl/report.mjs --out=<out> --update-baseline` on a crawl's output), and commit axe-baseline.json.");
}
md.push("", "### Axe rules", "", "| Rule (axe WCAG 2 A/AA) | Impact | Elements | Screens |", "|---|---|---:|---:|");
const axeIds = [...new Set([...Object.keys(axe), ...Object.keys(previous?.axe ?? {})])].sort(
  (a, b) => (axe[b]?.nodes ?? 0) - (axe[a]?.nodes ?? 0),
);
for (const id of axeIds) {
  const a = axe[id] ?? { nodes: 0, screens: 0, impact: "", help: "" };
  md.push(`| \`${id}\` ${a.help} | ${a.impact ?? ""} | ${a.nodes}${delta(a.nodes, previous?.axe?.[id]?.nodes ?? (previous ? 0 : undefined))} | ${a.screens} |`);
}
md.push(`| **All rules** | | **${summary.axeNodes}**${delta(summary.axeNodes, previous?.axeNodes)} | |`);
md.push("");
md.push(`- Icon-only controls with no name: ${report.unnamedControls}${delta(report.unnamedControls, previous?.report?.unnamedControls)}`);
md.push(`- Screens wider than the window at 1440 px: ${report.deskOverflowScreens}${delta(report.deskOverflowScreens, previous?.report?.deskOverflowScreens)}`);
md.push(`- Console warnings: ${report.consoleWarnings}${delta(report.consoleWarnings, previous?.report?.consoleWarnings)}`);
md.push(`- Mock gaps (routes mock mode answers with 501, so those screens render their error or empty state): ${mockGaps.length}${delta(mockGaps.length, previous?.mockGaps?.length)}`);
if (mockGaps.length) md.push("", "<details><summary>Mock gaps</summary>", "", ...mockGaps.map((g) => `- \`${g}\``), "", "</details>");
md.push("", "Screenshots and report.html are in the run's `ui-crawl-report` artifact.");
writeFileSync(join(OUT, "summary.md"), md.join("\n") + "\n");

// --- report.html
const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);
const gateHits = (r) => Object.entries(GATES).map(([id, g]) => [GATES[id].label, g.find(r)]).filter(([, items]) => items.length);
const rows = crawl
  .map((r) => {
    const hits = gateHits(r);
    const axeList = (r.axe ?? []).map((v) => `<li><code>${esc(v.id)}</code> ×${v.count} <span class="dim">${esc(v.targets[0]?.t)}</span></li>`).join("");
    const findings = hits.map(([label, items]) => `<li class="bad"><b>${esc(label)}</b><ul>${items.map((i) => `<li>${esc(i)}</li>`).join("")}</ul></li>`).join("");
    return `<article class="${hits.length ? "red" : ""}" id="${esc(key(r))}">
  <a href="shots/${esc(key(r))}.png"><img loading="lazy" src="shots/${esc(key(r))}.png" alt="${esc(key(r))}"></a>
  <div><h3>${esc(r.screen)} <span class="dim">${esc(r.variant)} · ${esc(r.finalUrl ?? r.path)}</span></h3>
  <ul>${findings || '<li class="ok">No gate findings</li>'}${axeList}
  ${r.unnamedButtons.length ? `<li>${r.unnamedButtons.length} icon-only controls with no name</li>` : ""}
  ${r.mockGaps?.length ? `<li class="dim">Mock gaps: ${r.mockGaps.map(esc).join(", ")}</li>` : ""}</ul></div>
</article>`;
  })
  .join("\n");
writeFileSync(
  join(OUT, "report.html"),
  `<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
<title>UI crawl: ${red ? "red" : "green"}</title>
<style>
:root{color-scheme:light dark;font:14px/1.45 system-ui,sans-serif;--bad:#c62828;--dim:#777}
body{margin:0 auto;max-width:1200px;padding:24px}
table{border-collapse:collapse}td,th{padding:4px 10px;border-bottom:1px solid #8884;text-align:left}
article{display:grid;grid-template-columns:320px 1fr;gap:16px;padding:14px 0;border-top:1px solid #8884}
article.red{border-left:4px solid var(--bad);padding-left:10px}
img{width:100%;border:1px solid #8884;border-radius:6px}
h3{margin:0 0 6px;font-size:15px}.dim{color:var(--dim);font-weight:400}.bad{color:var(--bad)}.ok{color:#2e7d32}
ul{margin:0;padding-left:18px}code{font-size:12px}
@media (max-width:700px){article{grid-template-columns:1fr}}
</style>
<h1>UI crawl: ${red ? "red" : "green"}, ${crawl.length} screens</h1>
<table><tr><th>Gate</th><th>Screens</th><th>Findings</th></tr>
${Object.values(gates).map((g) => `<tr><td>${esc(g.label)}</td><td>${g.screens}</td><td>${g.items}</td></tr>`).join("")}
<tr><td>axe (ratchet: ${ratchet.rose.length ? `${ratchet.rose.map((r) => esc(r.id)).join(", ")} rose` : "at or under the baseline"})</td><td></td><td>${summary.axeNodes}</td></tr></table>
${rows}
</html>
`,
);

console.log(md.slice(0, 8).join("\n"));
for (const r of ratchet.rose) console.error(`✗ axe ${r.id}: ${r.now}, baseline ${r.was}. Screens that grew: ${r.grew.slice(0, 10).join("; ")}`);
for (const r of ratchet.fell) console.log(`↓ axe ${r.id}: ${r.now}, baseline ${r.was}. Lower the baseline: UI_CRAWL_UPDATE_BASELINE=1 scripts/ui-crawl/run.sh`);
if (argv.includes("--gate") && red) {
  console.error("UI crawl gates failed: see summary.md / report.html");
  process.exit(1);
}
