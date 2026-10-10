import { readFileSync } from "node:fs";
import { join, resolve } from "node:path";
import type { CheckResult } from "../../src/check";

const transpiler = new Bun.Transpiler({ loader: "ts" });

/** A stand-in for `checkMod` that only reads mod.json and strips the types. */
export async function fakeCheck(root: string, manifest = "mod.json"): Promise<CheckResult> {
  const manifestPath = resolve(root, manifest);
  const m = JSON.parse(readFileSync(manifestPath, "utf8"));
  const modulePath = join(root, m.hooks);
  const source = readFileSync(modulePath, "utf8");
  const js = transpiler.transformSync(source);
  return {
    report: {
      ok: true,
      name: m.name,
      version: m.version,
      description: m.description,
      lines: source.split("\n").length,
      sha256: "fake",
      hooks: [],
      calls: [],
      state: [],
      pages: [],
      errors: [],
      warnings: [],
    },
    manifest: m,
    modulePath,
    js,
  };
}
