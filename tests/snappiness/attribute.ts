/**
 * Attribute a CPU profile to original source through the build's source maps.
 *
 *   bun attribute.ts <profile.cpuprofile> <wwwroot dir> [--top 25] [--json out.json]
 *
 * Prints self time by source file, and self and inclusive time by function (file:line name), with node_modules
 * folded to their package. Build the client with `vite build --sourcemap` so the .map files sit next to the bundles.
 */

import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { SourceMapConsumer } from "source-map-js";

interface Node { id: number; callFrame: { functionName: string; url: string; lineNumber: number; columnNumber: number }; children?: number[] }

const consumers = new Map<string, SourceMapConsumer | null>();
function consumerFor(wwwroot: string, url: string): SourceMapConsumer | null {
  const path = (() => { try { return new URL(url).pathname; } catch { return null; } })();
  if (!path) return null;
  if (consumers.has(path)) return consumers.get(path)!;
  const file = join(wwwroot, path + ".map");
  const c = existsSync(file) ? new SourceMapConsumer(JSON.parse(readFileSync(file, "utf8"))) : null;
  consumers.set(path, c);
  return c;
}

function tidy(source: string): string {
  const s = source.replace(/\\/g, "/").replace(/^(\.\.\/)+/, "").replace(/^webpack:\/\/\//, "");
  const nm = s.match(/node_modules\/(\.bun\/)?((@[^/]+\/)?[^/@]+)/);
  if (nm) return `node_modules/${nm[2]}`;
  const i = s.indexOf("src/");
  return i >= 0 ? s.slice(i) : s;
}

export interface Attribution {
  totalMs: number;
  byFile: { key: string; selfMs: number }[];
  byFunctionSelf: { key: string; selfMs: number }[];
  byFunctionTotal: { key: string; totalMs: number }[];
}

export function attribute(profilePath: string, wwwroot: string, top = 25): Attribution {
  const prof = JSON.parse(readFileSync(profilePath, "utf8"));
  const nodes = new Map<number, Node>(prof.nodes.map((n: Node) => [n.id, n]));
  const parent = new Map<number, number>();
  for (const n of prof.nodes as Node[]) for (const c of n.children ?? []) parent.set(c, n.id);

  const label = new Map<number, { file: string; fn: string }>();
  const labelOf = (id: number) => {
    let l = label.get(id);
    if (l) return l;
    const cf = nodes.get(id)!.callFrame;
    if (!cf.url) { const n = cf.functionName ? (cf.functionName.startsWith("(") ? cf.functionName : `(native) ${cf.functionName}`) : "(native)"; l = { file: n.startsWith("(native)") ? "(native)" : n, fn: n }; }
    else {
      const c = consumerFor(wwwroot, cf.url);
      const pos = c?.originalPositionFor({ line: cf.lineNumber + 1, column: cf.columnNumber });
      if (pos?.source) {
        const file = tidy(pos.source);
        const fn = file.startsWith("node_modules/") ? `${file} ${pos.name ?? cf.functionName ?? ""}`.trim() : `${file}:${pos.line} ${pos.name ?? cf.functionName ?? "(anonymous)"}`;
        l = { file, fn };
      } else l = { file: cf.url.replace(/^https?:\/\/[^/]+/, ""), fn: `${cf.url.replace(/^https?:\/\/[^/]+/, "")}:${cf.lineNumber + 1} ${cf.functionName}` };
    }
    label.set(id, l);
    return l;
  };

  const self = new Map<number, number>();
  const samples: number[] = prof.samples;
  const deltas: number[] = prof.timeDeltas;
  for (let i = 0; i < samples.length; i++) {
    const dt = (deltas[i + 1] ?? deltas[i] ?? 0) / 1000;
    self.set(samples[i]!, (self.get(samples[i]!) ?? 0) + dt);
  }

  const byFile = new Map<string, number>();
  const byFnSelf = new Map<string, number>();
  const byFnTotal = new Map<string, number>();
  let total = 0;
  for (const [id, ms] of self) {
    const l = labelOf(id);
    if (l.fn === "(idle)" || l.fn === "(program)") continue;
    total += ms;
    byFile.set(l.file, (byFile.get(l.file) ?? 0) + ms);
    byFnSelf.set(l.fn, (byFnSelf.get(l.fn) ?? 0) + ms);
    // Inclusive: every distinct function on this node's stack gets the time once.
    const seen = new Set<string>();
    for (let cur: number | undefined = id; cur !== undefined; cur = parent.get(cur)) {
      const f = labelOf(cur).fn;
      if (seen.has(f) || f === "(root)") continue;
      seen.add(f);
      byFnTotal.set(f, (byFnTotal.get(f) ?? 0) + ms);
    }
  }
  const appOnly = process.argv.includes("--app");
  const sort = (m: Map<string, number>) => [...m.entries()].filter(([k]) => !appOnly || k.startsWith("src/")).sort((a, b) => b[1] - a[1]).slice(0, top);
  return {
    totalMs: total,
    byFile: sort(byFile).map(([key, selfMs]) => ({ key, selfMs })),
    byFunctionSelf: sort(byFnSelf).map(([key, selfMs]) => ({ key, selfMs })),
    byFunctionTotal: sort(byFnTotal).map(([key, totalMs]) => ({ key, totalMs })),
  };
}

if (import.meta.main) {
  const [profile, wwwroot] = process.argv.slice(2);
  const top = Number(process.argv[process.argv.indexOf("--top") + 1] || 25) || 25;
  const a = attribute(profile!, wwwroot!, top);
  const jsonAt = process.argv.indexOf("--json");
  if (jsonAt > 0) writeFileSync(process.argv[jsonAt + 1]!, JSON.stringify(a, null, 2));
  console.log(`busy ${a.totalMs.toFixed(0)} ms (excluding idle and (program))\n\nself by file:`);
  for (const x of a.byFile) console.log(`  ${x.selfMs.toFixed(1).padStart(8)}  ${x.key}`);
  console.log("\nself by function:");
  for (const x of a.byFunctionSelf) console.log(`  ${x.selfMs.toFixed(1).padStart(8)}  ${x.key}`);
  console.log("\ninclusive by function:");
  for (const x of a.byFunctionTotal) console.log(`  ${x.totalMs.toFixed(1).padStart(8)}  ${x.key}`);
}
