// Works out which files outside mobile/ end up in the phone app's JavaScript, so CI walks the app through only when a
// change can alter what the phone draws. It follows the app's own imports from src/ the way Metro does (metro.config.js:
// the "@fleet/*" and "@/*" paths into client/src, and the shims that replace two browser-only imports), and keeps only
// imports that load code at run time: `import type`, type-only exports and .d.ts files are left out, because Metro
// drops them. Babel also drops a value import used only as a type, so this list can be a little longer than the bundle,
// never shorter.
import { dirname, relative, resolve } from "node:path";
import ts from "typescript";

export const mobileDir = resolve(import.meta.dir, "..");
export const repoRoot = resolve(mobileDir, "..");

// Same table as metro.config.js: these imports never reach the web client's file.
const SHIMS: Record<string, string> = {
  "@/composables/use-send-prompt": resolve(mobileDir, "src/shims/use-send-prompt.ts"),
  "lucide-vue-next": resolve(mobileDir, "src/shims/icon-stub.js"),
};

function runtimeSpecifiers(file: string, text: string): string[] {
  const source = ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true);
  const found: string[] = [];
  const visit = (node: ts.Node) => {
    if (ts.isImportDeclaration(node) && ts.isStringLiteral(node.moduleSpecifier)) {
      const clause = node.importClause;
      const typeOnly =
        clause?.isTypeOnly ||
        (clause && !clause.name && clause.namedBindings && ts.isNamedImports(clause.namedBindings) &&
          clause.namedBindings.elements.length > 0 && clause.namedBindings.elements.every((e) => e.isTypeOnly));
      if (!typeOnly) found.push(node.moduleSpecifier.text);
    } else if (ts.isExportDeclaration(node) && node.moduleSpecifier && ts.isStringLiteral(node.moduleSpecifier)) {
      const typeOnly =
        node.isTypeOnly ||
        (node.exportClause && ts.isNamedExports(node.exportClause) && node.exportClause.elements.length > 0 &&
          node.exportClause.elements.every((e) => e.isTypeOnly));
      if (!typeOnly) found.push(node.moduleSpecifier.text);
    } else if (ts.isImportEqualsDeclaration(node) && !node.isTypeOnly &&
      ts.isExternalModuleReference(node.moduleReference) && ts.isStringLiteral(node.moduleReference.expression)) {
      found.push(node.moduleReference.expression.text);
    } else if (ts.isCallExpression(node) && node.arguments.length > 0 && ts.isStringLiteral(node.arguments[0]) &&
      (node.expression.kind === ts.SyntaxKind.ImportKeyword ||
        (ts.isIdentifier(node.expression) && node.expression.text === "require"))) {
      found.push(node.arguments[0].text);
    }
    ts.forEachChild(node, visit);
  };
  visit(source);
  return found;
}

/** Repo-relative paths of every file outside mobile/ that the app's bundle can load, sorted. */
export function sharedRuntimeFiles(): string[] {
  const configPath = resolve(mobileDir, "tsconfig.json");
  const config = ts.readConfigFile(configPath, ts.sys.readFile);
  if (config.error) throw new Error(ts.flattenDiagnosticMessageText(config.error.messageText, "\n"));
  const parsed = ts.parseJsonConfigFileContent(config.config, ts.sys, mobileDir);
  const options = { ...parsed.options, allowJs: true };

  const seen = new Set<string>();
  // Tests aren't in the bundle (and Check runs them on every change anyway).
  const queue = parsed.fileNames.filter((f) => !/\.test\.tsx?$/.test(f));
  while (queue.length > 0) {
    const file = queue.pop()!;
    if (seen.has(file)) continue;
    seen.add(file);
    if (!/\.(ts|tsx|js|jsx|mjs|cjs)$/.test(file)) continue; // an asset: it counts, but has no imports
    const text = ts.sys.readFile(file);
    if (text === undefined) throw new Error(`Can't read ${file}`);
    for (const spec of runtimeSpecifiers(file, text)) {
      if (SHIMS[spec]) { queue.push(SHIMS[spec]); continue; }
      const resolved = ts.resolveModuleName(spec, file, options, ts.sys).resolvedModule;
      if (resolved) {
        if (resolved.isExternalLibraryImport || resolved.resolvedFileName.endsWith(".d.ts")) continue;
        queue.push(resolve(resolved.resolvedFileName));
        continue;
      }
      // TypeScript doesn't resolve assets (images, fonts); relative ones still count.
      if (spec.startsWith(".")) queue.push(resolve(dirname(file), spec));
    }
  }
  const outside = [...seen].map((f) => relative(repoRoot, f)).filter((f) => !f.startsWith("mobile/") && !f.startsWith(".."));
  return outside.sort();
}

/** The changed files that can alter what the phone draws: the app itself, its CI, and the shared files it runs. */
export function filesReachingPhone(changed: string[], shared: string[]): string[] {
  const sharedSet = new Set(shared);
  return changed.filter((f) => f.startsWith("mobile/") || f === ".github/workflows/mobile.yml" || sharedSet.has(f));
}
