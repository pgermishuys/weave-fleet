// The phone's sources (src/ is typechecked without Node's types, so this scan lives here).
import { readFileSync, readdirSync } from "node:fs";
import { join, resolve } from "node:path";
import { expect, test } from "bun:test";

test("no style writes words in a status colour itself", () => {
  const src = resolve(import.meta.dir, "../src");
  const offenders: string[] = [];
  for (const file of readdirSync(src, { recursive: true, encoding: "utf8" })) {
    if (!/\.tsx?$/.test(file) || /\.test\.|generated/.test(file)) continue;
    readFileSync(join(src, file), "utf8").split("\n").forEach((line, i) => {
      if (/(?<![\w-])color: *(?:color|c)\.(?:accent|running|waiting|complete|error|queued|diffAdd|diffDel)\b(?!Text|Dim)/.test(line)) {
        offenders.push(`${file}:${i + 1}`);
      }
    });
  }
  expect(offenders).toEqual([]);
});
