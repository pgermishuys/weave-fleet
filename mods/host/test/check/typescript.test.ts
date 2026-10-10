import { describe, expect, test } from "bun:test";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { Parser } from "acorn";
import { checkMod } from "../../src/check";
import { stripTypes } from "../../src/check/strip";
import { check, codes } from "./helpers";

const fixture = join(import.meta.dir, "..", "fixtures", "check");
const body = `export const register = (on) => { on("session.start", ($, e, next) => next(e)); };`;

/** Takes the `§` marker out of `text`, returning the text and the 1-based line and column where it stood. */
function mark(text: string) {
  const at = text.indexOf("§");
  const before = text.slice(0, at).split("\n");
  return { source: text.replace("§", ""), line: before.length, column: before[before.length - 1]!.length + 1 };
}

async function refusedTs(marked: string, code: string) {
  const { source, line, column } = mark(marked);
  const r = await check({ source, file: "mod.ts" });
  expect(r.report.ok).toBe(false);
  const hit = r.report.errors.find((e) => e.code === code);
  expect(hit, JSON.stringify(r.report.errors)).toBeDefined();
  expect({ line: hit!.line, column: hit!.column }).toEqual({ line, column });
}

/** Line and column (1-based) of every identifier in `js`, by name, in order. */
function identifiers(js: string): Map<string, [number, number][]> {
  const out = new Map<string, [number, number][]>();
  const ast = Parser.parse(js, { sourceType: "module", ecmaVersion: "latest", locations: true });
  const walk = (n: unknown): void => {
    if (!n || typeof n !== "object") return;
    if (Array.isArray(n)) return void n.forEach(walk);
    const node = n as { type?: string; name?: string; loc?: { start: { line: number; column: number } } };
    if (node.type === "Identifier") out.set(node.name!, [...(out.get(node.name!) ?? []), [node.loc!.start.line, node.loc!.start.column + 1]]);
    for (const [k, v] of Object.entries(node)) if (k !== "loc") walk(v);
  };
  walk(ast);
  return out;
}

describe("TypeScript strip", () => {
  test("the ts-heavy fixture is ok and keeps every line and column", async () => {
    const root = join(fixture, "ts-heavy");
    const r = await checkMod(root);
    expect(r.report.errors).toEqual([]);
    const source = await readFile(join(root, "mod.ts"), "utf8");
    const js = r.js!;
    expect(js).toHaveLength(source.length);
    expect(js.split("\n").length).toBe(source.split("\n").length);
    for (const [i, line] of source.split("\n").entries()) expect(js.split("\n")[i]).toHaveLength(line.length);
    // Every non-space character that stayed is where it was.
    for (let i = 0; i < source.length; i++) if (js[i] !== source[i]) expect(js[i]).toBe(" ");
    // And so is every identifier, through a plain JavaScript parse.
    const kept = identifiers(js);
    for (const name of ["register", "generic", "overloaded", "Child", "Base", "asValue", "late", "bang"]) {
      const at = kept.get(name)![0]!;
      const lines = source.split("\n");
      expect(lines[at[0] - 1]!.slice(at[1] - 1, at[1] - 1 + name.length)).toBe(name);
    }
  });

  test("the stripped JavaScript is real JavaScript Bun can import", async () => {
    const root = join(fixture, "ts-heavy");
    const r = await checkMod(root);
    const url = URL.createObjectURL(new Blob([r.js!], { type: "text/javascript" }));
    const mod = await import(url);
    URL.revokeObjectURL(url);
    expect(typeof mod.register).toBe("function");
    const hooks: string[] = [];
    mod.register((event: string) => void hooks.push(event));
    expect(hooks).toEqual(["ui.render"]);
  });

  test("types are blanked, values stay", () => {
    const src = `const x: number = 1;\nfunction f<T>(a: T, b?: string): T { return a!; }\nconst y = <any>x as unknown;\n`;
    const r = stripTypes(src);
    expect(r.ok && r.js.length).toBe(src.length);
    expect(r.ok && r.js.replace(/ +/g, " ")).toBe(`const x = 1;\nfunction f (a , b ) { return a ; }\nconst y = x ;\n`);
  });

  test("class members lose their modifiers but keep static, async and get", () => {
    const r = stripTypes(`class A { private static readonly a = 1; public async m() {} protected get g() { return 1; } }`);
    expect(r.ok && r.js.replace(/ +/g, " ")).toBe(`class A { static a = 1; async m() {} get g() { return 1; } }`);
  });

  test("an import with only type specifiers is blanked whole, one with values keeps them", () => {
    const r = stripTypes(`import { type A, type B } from "fleet-mods";\nimport { type C, d } from "x";\nexport { type A };`);
    expect(r.ok && r.js.split("\n").map((l) => l.replace(/ +/g, " "))).toEqual([" ", 'import { d } from "x";', " "]);
  });

  test("import type from fleet-mods is fine, import { type X } too", async () => {
    const r = await check({ file: "mod.ts", source: `import type { Register } from "fleet-mods";\nimport { type Fleet } from "fleet-mods";\n${body}` });
    expect(r.report.errors).toEqual([]);
  });

  test("import type from anywhere else is an import error at the import", async () => {
    await refusedTs(`§import type { X } from "node:fs";\n${body}`, "import");
    await refusedTs(`${body}\n§import { type X } from "./other";`, "import");
    await refusedTs(`${body}\n§export type { X } from "./other";`, "import");
  });

  test("a value import in a .ts file is an import error too", () => refusedTs(`§import { x } from "fleet-mods";\n${body}`, "import"));

  test.each([
    ["an enum", "§enum Color { Red }"],
    ["a const enum", "§const enum Color { Red }"],
    ["a namespace", "§namespace N { export const a = 1; }"],
    ["a module block", '§module N { export const a = 1; }'],
    ["a parameter property", "class A { constructor(§private x: number) {} }"],
    ["a decorator", "§@dec class A {}"],
    ["import = require", '§import fs = require("fs");'],
    ["export =", "§export = 5;"],
  ])("%s needs code generated: typescript error", async (_, snippet) => {
    await refusedTs(`${snippet}\n${body}`, "typescript");
    const r = await check({ file: "mod.ts", source: `${snippet.replace("§", "")}\n${body}` });
    expect(r.report.errors[0]?.message).toStartWith("not supported: write plain JavaScript for");
  });

  test("declare enum and declare namespace are blanked", async () => {
    const r = await check({ file: "mod.ts", source: `declare enum E { A }\ndeclare namespace N { const a: number }\ndeclare module "x" {}\n${body}` });
    expect(r.report.errors).toEqual([]);
  });

  test("a TypeScript parse error is a parse error", async () => {
    const r = await check({ file: "mod.ts", source: `const x: = 1;\n${body}` });
    expect(codes(r)).toEqual(["parse"]);
    expect(r.report.errors[0]?.line).toBe(1);
  });

  test("with in a .ts file is the with error", async () => {
    const r = await check({ file: "mod.ts", source: `export function register(on: any) { with (Math) { PI; } }` });
    expect(codes(r)).toEqual(["with"]);
  });

  test("analysis positions are the TypeScript source's", async () => {
    await refusedTs(`export const register = (on: (e: string, h: unknown) => void): void => {\n  const n: number = §fetch("x") as any;\n};`, "global");
  });

  test("a declared ambient value is not a global the mod may read", async () => {
    const r = await check({ file: "mod.ts", source: `declare const injected: number;\nexport const register = (on: any) => { void injected; };` });
    expect(codes(r)).toEqual(["global"]);
  });
});
