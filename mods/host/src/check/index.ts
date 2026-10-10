/**
 * The static check (docs/mods/api.md, "The static check"): reads a mod without running it.
 */
import { createHash } from "node:crypto";
import { readFile, stat } from "node:fs/promises";
import { resolve } from "node:path";
import { Parser } from "acorn";
import type { CheckProblem, CheckReport } from "fleet-mods/protocol";
import { LIMITS, type HostLimits } from "../limits";
import { analyzeModule } from "./analyze";
import { readManifest } from "./manifest";
import { parseProblem, stripTypes } from "./strip";

export { formatReport } from "./format";

export interface Manifest {
  name: string;
  version: string;
  description: string;
  /** As written in mod.json, relative to it. */
  hooks: string;
}

export interface CheckResult {
  report: CheckReport;
  /** Present when the report is ok. */
  manifest?: Manifest;
  /** Absolute path of the hooks module. Present when the report is ok. */
  modulePath?: string;
  /**
   * The module as JavaScript, TypeScript types blanked out with spaces so every line and column is where it was in
   * the source. This is exactly what was checked, and what the host imports. Present when the report is ok.
   */
  js?: string;
}

/** Lines in `text`; a trailing newline doesn't start another. */
function countLines(text: string): number {
  if (text === "") return 0;
  const breaks = text.match(/\r\n|[\n\r\u2028\u2029]/g)?.length ?? 0;
  return /[\n\r\u2028\u2029]$/.test(text) ? breaks : breaks + 1;
}

/**
 * Checks the mod in `root`. `manifest` is the path of its mod.json, absolute or relative to `root` (default
 * `mod.json`). Never throws for a bad mod: problems are `report.errors`, and `report.ok` is false when there are any.
 */
export async function checkMod(root: string, manifest = "mod.json", limits: Partial<HostLimits> = {}): Promise<CheckResult> {
  const max: HostLimits = { ...LIMITS, ...limits };
  const read = await readManifest(root, manifest);
  const report: CheckReport = {
    ok: false,
    ...read.partial,
    lines: 0,
    sha256: "",
    hooks: [],
    calls: [],
    state: [],
    pages: [],
    errors: [...read.errors],
    warnings: [...read.warnings],
  };
  const finish = (extra: Partial<CheckResult> = {}): CheckResult => {
    report.errors.sort((a, b) => (a.line ?? 0) - (b.line ?? 0) || (a.column ?? 0) - (b.column ?? 0));
    report.ok = report.errors.length === 0;
    return report.ok ? { report, manifest: read.manifest, modulePath: read.modulePath, ...extra } : { report };
  };
  if (!read.manifest || !read.modulePath) return finish();

  const stop = (...problems: CheckProblem[]) => {
    report.errors.push(...problems);
    return finish();
  };

  let bytes: Buffer;
  try {
    const size = (await stat(read.modulePath)).size;
    if (size > max.moduleBytes) return stop({ code: "size", message: `the module is ${size} bytes: the limit is ${max.moduleBytes}` });
    bytes = await readFile(read.modulePath);
  } catch (error) {
    return stop({ code: "manifest", message: `can't read the hooks module: ${(error as Error).message}` });
  }
  report.sha256 = createHash("sha256").update(bytes).digest("hex");
  const source = new TextDecoder().decode(bytes);
  report.lines = countLines(source);

  let js = source;
  const typeScript = /\.m?ts$/i.test(read.modulePath);
  const typeImportProblems: CheckProblem[] = [];
  if (typeScript) {
    const stripped = stripTypes(source);
    if (!stripped.ok) return stop(...stripped.problems);
    js = stripped.js;
    for (const t of stripped.typeImports) {
      if (t.source !== "fleet-mods") {
        typeImportProblems.push({
          line: t.line,
          column: t.column,
          code: "import",
          message: `imports aren't allowed: only \`import type\` from "fleet-mods", not ${JSON.stringify(t.source)}`,
        });
      }
    }
  }

  let ast;
  try {
    ast = Parser.parse(js, { sourceType: "module", ecmaVersion: "latest", locations: true, ranges: true });
  } catch (error) {
    return stop(...typeImportProblems, parseProblem(error));
  }

  let analysis;
  try {
    analysis = analyzeModule(ast, resolve(root), max);
  } catch (error) {
    return stop(...typeImportProblems, { code: "internal", message: `the check failed on this module: ${(error as Error).message}` });
  }
  report.hooks = analysis.hooks;
  report.calls = analysis.calls;
  report.state = analysis.state;
  report.pages = analysis.pages;
  report.errors.push(...typeImportProblems, ...analysis.errors);
  report.warnings.push(...analysis.warnings);
  return finish({ js });
}
