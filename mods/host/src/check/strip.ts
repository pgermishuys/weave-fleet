import { Parser } from "acorn";
import { tsPlugin } from "@sveltejs/acorn-typescript";
import type { CheckProblem } from "fleet-mods/protocol";

// eslint-disable-next-line @typescript-eslint/no-explicit-any
type N = any;

/** A type-only import or re-export, kept so the check can refuse any that isn't from "fleet-mods". */
export interface TypeImport {
  source: string;
  line: number;
  column: number;
}

export type StripResult =
  | { ok: true; js: string; typeImports: TypeImport[] }
  | { ok: false; problems: CheckProblem[] };

const TsParser = Parser.extend(tsPlugin() as never);

const NEWLINE = /[\n\r\u2028\u2029]/;
const TYPE_KEYS = ["typeAnnotation", "returnType", "typeParameters", "typeArguments", "superTypeParameters", "superTypeArguments"];
const SKIP_KEYS = new Set(["type", "start", "end", "loc", "range", "extra", "implements", ...TYPE_KEYS]);
const MEMBER_MODIFIERS = /\b(public|private|protected|readonly|override)\b/g;

/** Turns acorn's "message (line:col)" error into a problem with a 1-based column. */
export function parseProblem(error: unknown): CheckProblem {
  const e = error as { message?: string; loc?: { line: number; column: number } };
  const message = String(e.message ?? error).replace(/\s*\(\d+:\d+\)$/, "");
  const code = /'with'/.test(message) ? "with" : "parse";
  const text = code === "with" ? "`with` isn't allowed: mods are ES modules, which are strict" : message;
  return e.loc ? { line: e.loc.line, column: e.loc.column + 1, code, message: text } : { code, message: text };
}

/**
 * Parses TypeScript and blanks every type-only range with spaces, newlines kept, so the result is JavaScript whose
 * lines and columns are the source's. Refuses TypeScript that needs code generated.
 */
export function stripTypes(source: string): StripResult {
  let ast: N;
  try {
    ast = TsParser.parse(source, { sourceType: "module", ecmaVersion: "latest", locations: true });
  } catch (error) {
    return { ok: false, problems: [parseProblem(error)] };
  }

  const chars = source.split("");
  const problems: CheckProblem[] = [];
  const typeImports: TypeImport[] = [];

  const blank = (start: number, end: number) => {
    for (let i = start; i < end; i++) if (!NEWLINE.test(chars[i]!)) chars[i] = " ";
  };
  const blankNode = (node: N) => blank(node.start, node.end);
  const unsupported = (node: N, what: string) =>
    problems.push({
      line: node.loc.start.line,
      column: node.loc.start.column + 1,
      code: "typescript",
      message: `not supported: write plain JavaScript for ${what}`,
    });
  const recordImport = (node: N) => {
    if (node.source) typeImports.push({ source: String(node.source.value), line: node.loc.start.line, column: node.loc.start.column + 1 });
  };
  const nextNonSpace = (from: number) => {
    let i = from;
    while (i < source.length && /\s/.test(source[i]!)) i++;
    return i;
  };
  /** Blanks a `type X` specifier and the comma after it. */
  const blankSpecifier = (spec: N) => {
    const next = nextNonSpace(spec.end);
    blank(spec.start, source[next] === "," ? next + 1 : spec.end);
  };
  const blankMarks = (from: number, to: number) => {
    for (let i = from; i < to; i++) if (chars[i] === "?" || chars[i] === "!") chars[i] = " ";
  };

  const children = (node: N) => {
    for (const key of Object.keys(node)) {
      if (SKIP_KEYS.has(key)) continue;
      const value = node[key];
      if (Array.isArray(value)) for (const item of value) visit(item);
      else visit(value);
    }
  };

  function visit(node: N): void {
    if (!node || typeof node !== "object" || typeof node.type !== "string") return;

    switch (node.type) {
      case "ImportDeclaration": {
        const specs: N[] = node.specifiers;
        if (node.importKind === "type" || (specs.length > 0 && specs.every((s) => s.importKind === "type"))) {
          recordImport(node);
          blankNode(node);
          return;
        }
        for (const spec of specs) if (spec.importKind === "type") blankSpecifier(spec);
        return;
      }
      case "ExportNamedDeclaration": {
        const specs: N[] = node.specifiers ?? [];
        if (node.exportKind === "type" || (!node.declaration && specs.length > 0 && specs.every((s) => s.exportKind === "type"))) {
          recordImport(node);
          blankNode(node);
          return;
        }
        for (const spec of specs) if (spec.exportKind === "type") blankSpecifier(spec);
        visit(node.declaration);
        return;
      }
      case "ExportDefaultDeclaration":
        if (node.declaration?.type === "TSInterfaceDeclaration") blankNode(node);
        else visit(node.declaration);
        return;
      case "TSDeclareFunction":
      case "TSInterfaceDeclaration":
      case "TSTypeAliasDeclaration":
      case "TSIndexSignature":
        blankNode(node);
        return;
      case "TSEnumDeclaration":
        if (node.declare) blankNode(node);
        else unsupported(node, "an `enum` (use an object and `as const`)");
        return;
      case "TSModuleDeclaration":
        if (node.declare) blankNode(node);
        else unsupported(node, "a `namespace` or `module` (use plain objects and functions)");
        return;
      case "TSImportEqualsDeclaration":
        unsupported(node, "`import x = require()`");
        return;
      case "TSExportAssignment":
      case "TSNamespaceExportDeclaration":
        unsupported(node, "`export =`");
        return;
      case "TSParameterProperty":
        unsupported(node, "parameter properties (assign `this.x = x` in the constructor)");
        visit(node.parameter);
        return;
      case "TSAsExpression":
      case "TSSatisfiesExpression": {
        visit(node.expression);
        const between = source.slice(node.expression.end, node.typeAnnotation.start);
        const keyword = node.type === "TSAsExpression" ? "as" : "satisfies";
        const at = between.lastIndexOf(keyword);
        blank(node.expression.end + (at < 0 ? 0 : at), node.end);
        return;
      }
      case "TSNonNullExpression":
        visit(node.expression);
        chars[node.end - 1] = " ";
        return;
      case "TSTypeAssertion": {
        visit(node.expression);
        const close = source.indexOf(">", node.typeAnnotation.end);
        blank(node.start, close < 0 ? node.expression.start : close + 1);
        return;
      }
      case "TSInstantiationExpression":
        visit(node.expression);
        blankNode(node.typeArguments);
        return;
    }
    if (node.type.startsWith("TS")) {
      unsupported(node, `TypeScript's \`${node.type}\``);
      return;
    }

    if (Array.isArray(node.decorators) && node.decorators.length > 0) unsupported(node.decorators[0], "decorators");

    if (
      (node.type === "VariableDeclaration" || node.type === "ClassDeclaration" || node.type === "ClassExpression") &&
      node.declare
    ) {
      blankNode(node);
      return;
    }

    for (const key of TYPE_KEYS) {
      const type = node[key];
      if (type && typeof type.start === "number") blankNode(type);
    }

    switch (node.type) {
      case "Identifier":
        if (node.end > node.start + node.name.length) {
          const stop = node.typeAnnotation ? node.typeAnnotation.start : node.end;
          blankMarks(node.start + node.name.length, stop);
        }
        break;
      case "ClassDeclaration":
      case "ClassExpression": {
        const head = source.slice(node.start, node.id ? node.id.start : node.body.start);
        const abstractAt = head.search(/\babstract\b/);
        if (abstractAt >= 0) blank(node.start + abstractAt, node.start + abstractAt + "abstract".length);
        if (node.implements?.length) {
          const keyword = source.lastIndexOf("implements", node.implements[0].start);
          if (keyword >= 0) blank(keyword, node.body.start);
        }
        break;
      }
      case "FunctionDeclaration":
      case "FunctionExpression": {
        const first = node.params[0];
        if (first?.type === "Identifier" && first.name === "this") {
          const next = nextNonSpace(first.end);
          blank(first.start, node.params.length > 1 && source[next] === "," ? next + 1 : first.end);
          node.params = node.params.slice(1);
        }
        break;
      }
      case "MethodDefinition":
      case "PropertyDefinition":
      case "AccessorProperty": {
        const head = source.slice(node.start, node.key.start);
        if (node.value?.type === "TSDeclareMethod" || node.declare || /\b(abstract|declare)\b/.test(head)) {
          blankNode(node);
          return;
        }
        for (const match of head.matchAll(MEMBER_MODIFIERS)) blank(node.start + match.index, node.start + match.index + match[0].length);
        const regionEnd = Math.min(node.typeAnnotation?.start ?? node.end, node.value?.start ?? node.end);
        blankMarks(node.key.end, regionEnd);
        break;
      }
    }

    if (node.type === "ImportSpecifier" || node.type === "ExportSpecifier") return;
    children(node);
  }

  visit(ast);
  if (problems.length > 0) return { ok: false, problems };
  return { ok: true, js: chars.join(""), typeImports };
}
