import { readFileSync } from "node:fs";
const j = JSON.parse(readFileSync(process.argv[2]!, "utf8"));
for (const [k, v] of Object.entries<any>(j.summary)) {
  const s = v.stats;
  console.log(k.padEnd(16), v.skipped ? "SKIP " + v.skipped.slice(0, 160) : Object.entries<any>(s).filter(([n]) => /Ms$|requests|fps|dropped|scrolled|Pct/.test(n)).map(([n, x]) => `${n}=${x.median?.toFixed(0)}/${x.p95?.toFixed(0)}`).join(" "));
}
