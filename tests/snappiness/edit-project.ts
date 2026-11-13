/** Make the same edits openChanges makes, in the long session's project (so Changes is non-empty). */
import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
const scratch = process.argv[2]!;
const seed = JSON.parse(readFileSync(join(scratch, "seed.json"), "utf8"));
const s = seed.sessions.find((x: any) => x.id === seed.long[0]);
const dir = join(scratch, "home", "proj", s.project, "src");
for (let k = 0; k < 10; k++) {
  const f = join(dir, `module${k}.ts`);
  writeFileSync(f, readFileSync(f, "utf8").replace(/= (\d+);/g, (m, n) => (Number(n) % 3 === 0 ? `= ${Number(n) + 1}; // tuned` : m)));
}
writeFileSync(join(dir, "snappiness-added.ts"), Array.from({ length: 120 }, (_, j) => `export const added${j} = ${j};`).join("\n") + "\n");
console.log("edited", dir);
