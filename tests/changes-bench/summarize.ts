/**
 * Medians of results-<label>.json per scenario and build, as JSON for the report (and as a table on stdout).
 *   bun run summarize.ts before-862 main fix
 */
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const labels = process.argv.slice(2);
const median = (values: number[]) => {
  const v = values.filter((x) => typeof x === "number" && Number.isFinite(x)).sort((a, b) => a - b);
  return v.length ? Math.round(v[Math.floor((v.length - 1) / 2)]!) : null;
};
const pick = (runs: any[], f: (r: any) => number | null | undefined) => median(runs.map(f).filter((x) => x != null) as number[]);

const out: Record<string, Record<string, Record<string, number | null | string>>> = {};
for (const label of labels) {
  const file = join(import.meta.dir, `results-${label}.json`);
  if (!existsSync(file)) continue;
  const data = JSON.parse(readFileSync(file, "utf8"));
  for (const [scenario, runs] of Object.entries<any[]>(data)) {
    if (scenario === "meta" || !Array.isArray(runs) || runs[0]?.skipped) continue;
    out[scenario] ??= {};
    if (scenario === "switch") {
      const legs = runs.flatMap((r) => r.legs ?? []);
      out[scenario][label] = {
        runs: runs.length,
        firstRow: pick(legs, (l) => l.firstRow),
        settledAt: pick(legs, (l) => l.settledAt),
        diffsRequests: pick(legs, (l) => l.diffsRequests),
        listRequests: pick(legs, (l) => l.listRequests),
        gitProcesses: pick(legs, (l) => l.git?.processes),
        gitMs: pick(legs, (l) => l.git?.totalMs),
        firstRowBig: pick(legs.filter((l) => l.to === "big"), (l) => l.firstRow),
      };
      continue;
    }
    out[scenario][label] = {
      runs: runs.length,
      firstRow: pick(runs, (r) => r.firstRow),
      firstDiff: pick(runs, (r) => r.firstDiff),
      clickToDiff: pick(runs, (r) => r.clickToDiff),
      settledAt: pick(runs, (r) => r.settledAt),
      diffsRequests: pick(runs, (r) => r.diffsRequests),
      listRequests: pick(runs, (r) => r.listRequests),
      fileRequests: pick(runs, (r) => r.fileRequests),
      kb: pick(runs, (r) => (r.bytes ?? 0) / 1024),
      slowestMs: pick(runs, (r) => r.slowestMs),
      gitProcesses: pick(runs, (r) => r.git?.processes),
      gitMs: pick(runs, (r) => r.git?.totalMs),
      otherRequestMs: pick(runs, (r) => r.otherRequestWhileFinding),
      errors: runs.flatMap((r) => r.errors ?? []).length,
      errorNotes: pick(runs, (r) => r.state?.errorNotes),
      longTaskMs: pick(runs, (r) => r.longTaskMs),
      afterWritesMs: pick(runs, (r) => (r.writesDoneAt != null ? r.settledAt - r.writesDoneAt : null)),
    };
  }
}
writeFileSync(join(import.meta.dir, "summary.json"), JSON.stringify(out, null, 2));
for (const [scenario, byLabel] of Object.entries(out)) {
  console.log(`\n${scenario}`);
  console.table(byLabel);
}
