import { describe, expect, test } from "bun:test";
import { join } from "node:path";
import { checkMod } from "../../src/check";
import { check } from "./helpers";

const ESCAPES = join(import.meta.dir, "../fixtures/escape");
const codes = async (name: string) => (await checkMod(join(ESCAPES, name))).report;
const reg = (body: string) => `export const register = (on) => {\n${body}\n};\n`;

describe("reflection that reaches a constructor or a prototype is refused", () => {
  test("Object.getOwnPropertyDescriptor naming constructor, __proto__ or prototype", async () => {
    for (const name of ["constructor", "__proto__", "prototype"]) {
      const r = await check({ source: reg(`  const d = Object.getOwnPropertyDescriptor({}, "${name}");`) });
      expect(r.report.errors.map((e) => [e.code, e.line, e.column])).toEqual([["prototype", 2, 49]]);
    }
  });
  test("Object.defineProperty, defineProperties and create naming them", async () => {
    for (const src of [
      `  Object.defineProperty({}, "__proto__", { value: 1 });`,
      `  Object.defineProperties({}, { prototype: { value: 1 } });`,
      `  Object.create(null, { constructor: { value: 1 } });`,
      "  Object.defineProperty({}, `constructor`, { value: 1 });",
    ]) {
      const r = await check({ source: reg(src) });
      expect(r.report.errors.map((e) => e.code)).toEqual(["prototype"]);
    }
  });
  test("Object.getPrototypeOf, setPrototypeOf and getOwnPropertyDescriptors are refused outright", async () => {
    for (const fn of ["getPrototypeOf", "setPrototypeOf", "getOwnPropertyDescriptors"]) {
      const r = await check({ source: reg(`  const p = Object.${fn}({}, null);`) });
      expect(r.report.errors.map((e) => e.code)).toEqual(["prototype"]);
      expect(r.report.errors[0]!.message).toContain(fn);
    }
  });
  test("Object.getOwnPropertySymbols is refused: symbol keys are the host's (review 2)", async () => {
    const r = await check({ source: reg(`  const keys = Object.getOwnPropertySymbols({});`) });
    expect(r.report.errors.map((e) => [e.code, e.line, e.column])).toEqual([["prototype", 2, 23]]);
  });
  test("the legacy accessors (__lookupGetter__ and friends) are refused", async () => {
    for (const name of ["__lookupGetter__", "__lookupSetter__", "__defineGetter__", "__defineSetter__"]) {
      const r = await check({ source: reg(`  ({}).${name}("x");`) });
      expect(r.report.errors.map((e) => e.code)).toEqual(["prototype"]);
    }
  });
  test("ordinary Object calls still pass", async () => {
    const r = await check({ source: reg(`  const o = Object.assign({}, Object.fromEntries([["a", 1]]));\n  Object.keys(o); Object.entries(o); Object.freeze(o);\n  Object.defineProperty({}, "label", { value: "x" });`) });
    expect(r.report.errors).toEqual([]);
  });
});

describe("the review's escapes (PR #486, review 1)", () => {
  test("E1: an async function's constructor through getOwnPropertyDescriptor is refused", async () => {
    const r = await codes("escape-e1");
    expect(r.ok).toBe(false);
    expect(r.errors.map((e) => [e.code, e.line])).toEqual([["prototype", 3], ["prototype", 3]]);
  });
  for (const name of ["escape-descriptor", "escape-getproto-literal", "escape-reviver", "escape-spawn", "escape-tagged"]) {
    test(`${name} is refused with prototype`, async () => {
      const r = await codes(name);
      expect(r.ok).toBe(false);
      expect(new Set(r.errors.map((e) => e.code))).toEqual(new Set(["prototype"]));
    });
  }
  test("a built-up computed key passes the check: the hardened realm stops it at run time (test/hardening.test.ts)", async () => {
    for (const name of ["escape-constr-key", "escape-constr-dbg", "escape-fs-read"]) {
      const r = await codes(name);
      expect(r.errors).toEqual([]);
    }
  });
});

describe("computed keys", () => {
  test("a computed key that isn't a literal is no longer a warning", async () => {
    const r = await check({ source: reg(`  const rows = ["a", "b"];\n  for (let i = 0; i < rows.length; i++) rows[i];\n  const k = "a";\n  ({ a: 1 })[k];`) });
    expect(r.report.ok).toBe(true);
    expect(r.report.warnings).toEqual([]);
  });
});
