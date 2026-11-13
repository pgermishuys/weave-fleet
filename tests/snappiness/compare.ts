/**
 * Merge measurement rounds and compare two builds.
 *
 *   bun compare.ts --results <dir> --base <name> --head <name> [--throttle 1,4]
 *
 * Reads <dir>/<name>-t<throttle>-r<round>.json, pools every sample per scenario, and writes compare.json and
 * compare.md: median and p95 per headline metric, base vs head, with the change.
 */

import { readdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const args = new Map<string, string>();
for (let i = 2; i < process.argv.length; i += 2) args.set(process.argv[i]!.replace(/^--/, ""), process.argv[i + 1] ?? "");
const DIR = args.get("results")!;
const BASE = args.get("base")!;
const HEAD = args.get("head")!;
const THROTTLES = (args.get("throttle") ?? "1").split(",");

/** What each row reports: [metric, label]. The first is the headline. */
export const METRICS: Record<string, [string, string][]> = {
  coldStart: [["shownMs", "session list shown"], ["conversationMs", "conversation shown"], ["longTaskMs", "long tasks"], ["requests", "requests"], ["jsKB", "JS decoded KB"]],
  openSession: [["shownMs", "shown"], ["settledMs", "settled"], ["longTaskMs", "long tasks"], ["requests", "requests"]],
  switchLong: [["shownMs", "shown"], ["settledMs", "settled"], ["longTaskMs", "long tasks"], ["requests", "requests"]],
  typing: [["keyP95Ms", "keystroke→paint p95"], ["keyMedianMs", "keystroke→paint median"], ["inpMs", "INP (Event Timing)"]],
  sendPrompt: [["shownMs", "bubble shown"], ["settledMs", "settled"], ["longTaskMs", "long tasks"], ["requests", "requests"]],
  railSessions: [["shownMs", "shown"], ["settledMs", "settled"], ["longTaskMs", "long tasks"], ["requests", "requests"]],
  railSettings: [["shownMs", "shown"], ["settledMs", "settled"], ["longTaskMs", "long tasks"], ["requests", "requests"]],
  railAnalytics: [["shownMs", "shown"], ["settledMs", "settled"], ["longTaskMs", "long tasks"], ["requests", "requests"]],
  railFleet: [["shownMs", "shown"], ["settledMs", "settled"], ["longTaskMs", "long tasks"], ["requests", "requests"]],
  openChanges: [["shownMs", "shown"], ["settledMs", "settled"], ["longTaskMs", "long tasks"], ["requests", "requests"]],
  scrollLong: [["p95FrameMs", "frame p95"], ["droppedFrames", "dropped frames / 3 s"], ["longTaskMs", "long tasks"], ["scrolledPx", "scrolled px"]],
  newSessionPage: [["shownMs", "shown"], ["settledMs", "settled"], ["longTaskMs", "long tasks"], ["requests", "requests"]],
  streamingList: [["longTaskMs", "long tasks / 6 s"], ["maxFrameMs", "worst frame"], ["keyP95Ms", "keystroke→paint p95"]],
  streamingWatch: [["longTaskMs", "long tasks / 6 s"], ["maxFrameMs", "worst frame"], ["keyP95Ms", "keystroke→paint p95"]],
};

function quantile(xs: number[], q: number): number | null {
  const v = xs.filter((x) => typeof x === "number" && Number.isFinite(x)).sort((a, b) => a - b);
  if (!v.length) return null;
  const pos = (v.length - 1) * q;
  const lo = Math.floor(pos);
  return v[lo]! + (v[Math.min(lo + 1, v.length - 1)]! - v[lo]!) * (pos - lo);
}

function pool(name: string, throttle: string) {
  const files = readdirSync(DIR).filter((f) => f.startsWith(`${name}-t${throttle}-r`) && f.endsWith(".json"));
  const byScenario = new Map<string, { samples: any[]; skipped?: string }>();
  for (const f of files) {
    const j = JSON.parse(readFileSync(join(DIR, f), "utf8"));
    for (const r of j.results) {
      const e = byScenario.get(r.name) ?? { samples: [] };
      e.samples.push(...r.samples.map((s: any, i: number) => ({ ...s, requests: s.requests ?? r.requests?.[i] })));
      if (r.skipped && !e.samples.length) e.skipped = r.skipped;
      byScenario.set(r.name, e);
    }
  }
  return byScenario;
}

const fmt = (x: number | null) => (x == null ? "–" : x >= 100 ? x.toFixed(0) : x.toFixed(1));
const out: any = { base: BASE, head: HEAD, throttles: {} };
let md = `# Snappiness: ${BASE} vs ${HEAD}\n`;
for (const t of THROTTLES) {
  const a = pool(BASE, t);
  const b = pool(HEAD, t);
  const rows: any[] = [];
  md += `\n## CPU ${t}x\n\n| Interaction | Metric | ${BASE} median | ${BASE} p95 | ${HEAD} median | ${HEAD} p95 | Change (median) | n |\n|---|---|---|---|---|---|---|---|\n`;
  for (const [scenario, metrics] of Object.entries(METRICS)) {
    const sa = a.get(scenario);
    const sb = b.get(scenario);
    for (const [m, label] of metrics) {
      const xa = sa?.samples.map((s) => s[m]) ?? [];
      const xb = sb?.samples.map((s) => s[m]) ?? [];
      const row = {
        scenario, metric: m, label,
        base: { median: quantile(xa, 0.5), p95: quantile(xa, 0.95), n: xa.filter((x) => x != null).length, skipped: sa?.skipped },
        head: { median: quantile(xb, 0.5), p95: quantile(xb, 0.95), n: xb.filter((x) => x != null).length, skipped: sb?.skipped },
      };
      rows.push(row);
      const change = row.base.median && row.head.median != null ? `${row.head.median >= row.base.median ? "+" : ""}${(((row.head.median - row.base.median) / row.base.median) * 100).toFixed(0)}%` : "–";
      md += `| ${m === metrics[0]![0] ? scenario : ""} | ${label} | ${fmt(row.base.median)} | ${fmt(row.base.p95)} | ${fmt(row.head.median)} | ${fmt(row.head.p95)} | ${change} | ${row.base.n}/${row.head.n} |\n`;
    }
  }
  out.throttles[t] = rows;
}
writeFileSync(join(DIR, "compare.json"), JSON.stringify(out, null, 2));
writeFileSync(join(DIR, "compare.md"), md);
console.log(md);
