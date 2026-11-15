// The crawl's self-test: reads a crawl of SELF_TEST_SCREENS (run.sh --self-test) and checks every one of those screens
// failed each gate it names in `mustFail`, with its `expectText` in the findings. A screen the gates miss means the
// crawl would pass a real screen broken the same way, so the self-test fails (exit 1).
//
//   node self-test.mjs [--out=out]
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { GATES } from "./gates.mjs";
import { SELF_TEST_SCREENS } from "./screens.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const OUT = process.argv.slice(2).find((a) => a.startsWith("--out="))?.slice(6) ?? join(here, "out");
const crawl = JSON.parse(readFileSync(join(OUT, "crawl.json"), "utf8"));

const problems = [];
for (const screen of SELF_TEST_SCREENS) {
  const records = crawl.filter((r) => r.screen === screen.id);
  if (!records.length) problems.push(`${screen.id}: the crawl didn't visit it`);
  for (const r of records) {
    for (const gate of screen.mustFail) {
      const found = GATES[gate].find(r);
      const ok = found.length > 0 && (!screen.expectText || found.some((f) => f.includes(screen.expectText)));
      console.log(`${ok ? "✓" : "✗"} ${screen.id}--${r.variant} fails "${GATES[gate].label}"${ok ? `: ${found[0].slice(0, 120)}` : ""}`);
      if (!ok) problems.push(`${screen.id}--${r.variant}: the "${GATES[gate].label}" gate didn't catch it (found: ${JSON.stringify(found).slice(0, 200)})`);
    }
  }
}
if (problems.length) {
  console.error("UI crawl self-test failed: the gates let a broken screen through.");
  for (const p of problems) console.error(`  ${p}`);
  process.exit(1);
}
console.log(`UI crawl self-test passed: ${crawl.length} visits, each red where it should be.`);
