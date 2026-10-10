import { describe, expect, test } from "bun:test";
import { check, codes, reg } from "./helpers";

/** Takes the `§` marker out of `text`, returning the text and the 1-based line and column where it stood. */
function mark(text: string) {
  const at = text.indexOf("§");
  if (at < 0) throw new Error("no § marker");
  const before = text.slice(0, at).split("\n");
  return { source: text.replace("§", ""), line: before.length, column: before[before.length - 1]!.length + 1 };
}

/** Checks the module in `marked` (the problem's node starts at `§`): it must fail with this code at that spot. */
async function refused(marked: string, code: string, file = "mod.js") {
  const { source, line, column } = mark(marked);
  const r = await check({ source, file });
  expect(r.report.ok).toBe(false);
  const hit = r.report.errors.find((e) => e.code === code);
  expect(hit, `expected ${code} in ${JSON.stringify(r.report.errors)}`).toBeDefined();
  expect({ line: hit!.line, column: hit!.column }).toEqual({ line, column });
  expect(r.js).toBeUndefined();
  return r;
}

/** A hook whose body (inside the arrow function) is `body`. */
const hook = (body: string) => reg(`  on("session.start", ($, e, next) => {\n${body}\n    return next(e);\n  });`);

describe("modules", () => {
  test("a static import is refused", () => refused(`§import fs from "node:fs";\nexport function register() {}`, "import"));
  test("an export from is refused", () => refused(`export function register() {}\n§export { x } from "y";`, "import"));
  test("export * from is refused", () => refused(`export function register() {}\n§export * from "y";`, "import"));
  test("dynamic import is refused", () => refused(reg(`  §import("x");`), "dynamic-import"));
  test("import.meta is refused", () => refused(reg(`  const u = §import.meta.url;`), "import-meta"));
  test("require is refused", () => refused(reg(`  const fs = §require("fs");`), "require"));
  test("typeof require is refused too", () => refused(reg(`  typeof §require;`), "require"));
  test("with is refused with its own code, at the statement", () => refused(reg(`  §with (Math) { PI; }`), "with"));
  test("a parse error is a single parse error with its position", async () => {
    const r = await refused(`export function register({\n}§`, "parse");
    expect(r.report.errors).toHaveLength(1);
  });
});

describe("globals", () => {
  test("an unknown global is refused at its first use only", async () => {
    const r = await refused(reg(`  §fetch("a");\n  fetch("b");`), "global");
    expect(r.report.errors.filter((e) => e.code === "global")).toHaveLength(1);
    expect(r.report.errors[0]?.message).toBe("fetch isn't available to mods");
  });
  test("setTimeout points at $.clock", async () => {
    const r = await refused(reg(`  §setTimeout(() => {}, 1);`), "global");
    expect(r.report.errors[0]?.message).toBe("setTimeout isn't available to mods: use $.clock.after");
  });
  test("each refused name is refused", async () => {
    const names = [
      "globalThis", "self", "window", "global", "process", "Bun", "Deno", "fetch", "WebSocket", "XMLHttpRequest", "Worker",
      "setTimeout", "setInterval", "queueMicrotask", "eval", "Function", "Reflect", "Proxy", "WebAssembly",
      "SharedArrayBuffer", "Atomics", "Buffer", "document", "URL",
    ];
    for (const name of names) {
      const r = await check({ source: reg(`  void ${name};`) });
      expect(codes(r), name).toEqual(["global"]);
      expect(r.report.errors[0]?.message).toContain(name);
    }
  });
  test("a global written to or tested with typeof is refused", async () => {
    expect(codes(await check({ source: reg(`  leak = 1;`) }))).toEqual(["global"]);
    expect(codes(await check({ source: reg(`  typeof Bun;`) }))).toEqual(["global"]);
  });
  test("the allowed globals are fine", async () => {
    const names = [
      "Object", "Array", "Math", "JSON", "Date", "Map", "Set", "Promise", "RegExp", "String", "Number", "Boolean", "Symbol",
      "Error", "TypeError", "RangeError", "SyntaxError", "ReferenceError", "EvalError", "URIError", "AggregateError", "Intl",
      "parseInt", "parseFloat", "isNaN", "isFinite", "encodeURIComponent", "decodeURIComponent", "structuredClone",
      "console", "undefined", "NaN", "Infinity",
    ];
    const r = await check({ source: reg(names.map((n) => `  void ${n};`).join("\n")) });
    expect(r.report.errors).toEqual([]);
  });
  test("a local that shadows a refused name is fine", async () => {
    expect((await check({ source: reg(`  const fetch = 1;\n  void fetch;`) })).report.ok).toBe(true);
  });
  test("an unbound $ is a dollar-escape, not a global", async () => {
    const r = await refused(`const x = §$.ui;\nexport function register() {}`, "dollar-escape");
    expect(codes(r)).toEqual(["dollar-escape"]);
  });
});

describe("prototype", () => {
  test(".constructor is refused", () => refused(reg(`  const c = [].§constructor;`), "prototype"));
  test(".__proto__ is refused", () => refused(reg(`  const c = ({}).§__proto__;`), "prototype"));
  test(".prototype is refused, read or written", async () => {
    await refused(reg(`  Object.§prototype.x = 1;`), "prototype");
    await refused(reg(`  Array.§prototype;`), "prototype");
  });
  test('a ["constructor"] string key is refused', () => refused(reg(`  const c = ({})[§"constructor"];`), "prototype"));
  test("a no-substitution template key is refused", () => refused(reg("  const c = ({})[§`prototype`];"), "prototype"));
  test("destructuring a prototype name is refused", async () => {
    await refused(reg(`  const { §constructor } = {};`), "prototype");
    await refused(reg(`  const { [§"__proto__"]: p } = {};`), "prototype");
  });
  test("a __proto__ key in an object literal is refused", () => refused(reg(`  const o = { §__proto__: null };`), "prototype"));
});

describe("limits", () => {
  test("more than moduleScopes scopes is refused", async () => {
    const source = reg(`  const f = () => { const g = () => {}; };`);
    const r = await check({ source }, { moduleScopes: 3 });
    expect(codes(r)).toEqual(["scopes"]);
    expect(r.report.errors[0]?.line).toBe(3);
    expect((await check({ source }, { moduleScopes: 5 })).report.ok).toBe(true);
  });
  test("a module over moduleBytes is refused and stops", async () => {
    const r = await check({ source: reg(`  // ${"x".repeat(100)}`) }, { moduleBytes: 50 });
    expect(codes(r)).toEqual(["size"]);
    expect(r.report.lines).toBe(0);
  });
});

describe("register", () => {
  const body = `on("session.start", ($, e, next) => next(e));`;
  test.each([
    ["an arrow const", `export const register = (on, options) => { ${body} };`],
    ["a function declaration", `export function register(on) { ${body} }`],
    ["a function expression", `export const register = function (on) { ${body} };`],
    ["an async function", `export async function register(on) { ${body} }`],
    ["an exported local function", `function register(on) { ${body} }\nexport { register };`],
    ["an exported local const", `const register = (on) => { ${body} };\nexport { register };`],
    ["an export under another local name", `const setup = (on) => { ${body} };\nexport { setup as register };`],
  ])("%s is accepted", async (_, source) => {
    const r = await check({ source });
    expect(r.report.errors).toEqual([]);
    expect(r.report.hooks).toEqual([{ event: "session.start" }]);
  });
  test("a typed register const is accepted", async () => {
    const r = await check({ file: "mod.ts", source: `import type { Register } from "fleet-mods";\nexport const register: Register = (on) => { ${body} };` });
    expect(r.report.errors).toEqual([]);
    expect(r.report.hooks).toEqual([{ event: "session.start" }]);
  });
  test("no export is refused", async () => {
    const r = await check({ source: `const x = 1;` });
    expect(r.report.errors).toEqual([{ line: 1, column: 1, code: "no-register", message: expect.any(String) }]);
  });
  test("a register that isn't a function is refused", () => refused(`export const §register = 5;`, "no-register"));
  test("an unexported register is refused", async () => {
    expect(codes(await check({ source: `function register(on) {}` }))).toEqual(["no-register"]);
  });
  test("the name of on doesn't matter", async () => {
    const r = await check({ source: `export function register(hook) { hook("ui.press", ($, e, next) => next(e)); }` });
    expect(r.report.hooks).toEqual([{ event: "ui.press" }]);
  });
});

describe("on", () => {
  test("calling on in a nested function is on-outside-register", () =>
    refused(reg(`  const later = () => §on("session.start", ($, e, next) => next(e));`), "on-outside-register"));
  test("calling on inside a callback is on-outside-register", () =>
    refused(reg(`  [1].forEach(() => { §on("session.start", ($, e, next) => next(e)); });`), "on-outside-register"));
  test("aliasing on is on-escape", () => refused(reg(`  const alias = §on;`), "on-escape"));
  test("passing on is on-escape", () => refused(reg(`  helper(§on);`), "on-escape"));
  test("storing on is on-escape", () => refused(reg(`  const o = { §on };`), "on-escape"));
  test("a non-literal event is dynamic-event", () =>
    refused(reg(`  const name = "ui.press";\n  on(§name, ($, e, next) => next(e));`), "dynamic-event"));
  test("a template event with a substitution is dynamic-event", () =>
    refused(reg("  on(§`ui.${1}`, ($, e, next) => next(e));"), "dynamic-event"));
  test("a no-substitution template event is accepted", async () => {
    expect((await check({ source: reg("  on(`ui.press`, ($, e, next) => next(e));") })).report.hooks).toEqual([{ event: "ui.press" }]);
  });
  test("an unknown event is unknown-event", () => refused(reg(`  on(§"ui.hover", ($, e, next) => next(e));`), "unknown-event"));
  test("a wrong number of arguments is on-arguments", () => refused(reg(`  §on("ui.press");`), "on-arguments"));
  test("a matcher with an identifier is dynamic-matcher, at the identifier's property", () =>
    refused(reg(`  const tool = "bash";\n  on("ui.render", { props: { §tool } }, ($, e, next) => next(e));`), "dynamic-matcher"));
  test.each([
    ["a computed key", `{ [k]: 1 }`],
    ["a spread", `{ ...rest }`],
    ["a variable", `{ a: v }`],
    ["a call", `{ a: f() }`],
    ["new RegExp", `{ a: new RegExp("x") }`],
    ["a template with substitutions", "{ a: `x${1}` }"],
    ["a method", `{ a() {} }`],
    ["an array with a variable", `{ a: [v] }`],
    ["a non-object matcher", `v`],
  ])("a matcher with %s is dynamic-matcher", async (_, matcher) => {
    const r = await check({ source: reg(`  const k = "a", v = 1, rest = {}; const f = () => 1;\n  on("ui.render", ${matcher}, ($, e, next) => next(e));`) });
    expect(codes(r)).toEqual(["dynamic-matcher"]);
  });
  test("the same event twice without a matcher is duplicate-hook", () =>
    refused(reg(`  on("ui.press", ($, e, next) => next(e));\n  §on("ui.press", ($, e, next) => next(e));`), "duplicate-hook"));
  test("the same event several times with matchers is fine", async () => {
    const r = await check({
      source: reg(`  on("ui.render", { component: "A" }, ($, e, next) => next(e));\n  on("ui.render", { component: "B" }, ($, e, next) => next(e));\n  on("ui.render", ($, e, next) => next(e));`),
    });
    expect(r.report.errors).toEqual([]);
    expect(r.report.hooks).toHaveLength(3);
  });
  test("hooks come in source order with matchers as JSON", async () => {
    const r = await check({
      source: reg(
        `  on("ui.press", ($, e, next) => next(e));\n  on("ui.render", { component: ["A", "B"], props: { n: 1, m: -2, t: true, z: null, r: /x\\d/gi, s: \`q\` } }, ($, e, next) => next(e));`,
      ),
    });
    expect(r.report.errors).toEqual([]);
    expect(r.report.hooks).toEqual([
      { event: "ui.press" },
      { event: "ui.render", matcher: { component: ["A", "B"], props: { n: 1, m: -2, t: true, z: null, r: { $regex: "x\\d", flags: "gi" }, s: "q" } } },
    ]);
  });
});

describe("$", () => {
  test("every allowed $ form is accepted", async () => {
    const r = await check({
      source: reg(
        `  on("ui.press", ($, e, next) => {\n    $.ui.log($.mod.name + $.mod.version);\n    note($, "x");\n    take($);\n    return next(e);\n  });`,
        `function note($, t) { $.ui.log(t); }\nconst take = ($) => { $.ui.log("y"); };`,
      ),
    });
    expect(r.report.errors).toEqual([]);
    expect(r.report.calls).toEqual(["mod.name", "mod.version", "ui.log"]);
  });
  test("aliasing $ is dollar-escape", () => refused(hook(`    const d = §$;`), "dollar-escape"));
  test("$[name] is dollar-escape", () => refused(hook(`    §$["ui"];`), "dollar-escape"));
  test("destructuring $ is dollar-escape", () => refused(hook(`    const { ui } = §$;`), "dollar-escape"));
  test("spreading $ is dollar-escape", () => refused(hook(`    const c = { ...§$ };`), "dollar-escape"));
  test("storing $ is dollar-escape", () => refused(hook(`    const o = {}; o.x = §$;`), "dollar-escape"));
  test("returning $ is dollar-escape", () => refused(`export function register(on) { on("ui.press", ($, e, next) => §$); }`, "dollar-escape"));
  test("passing $ to a function the check can't see is dollar-escape", () =>
    refused(hook(`    const f = JSON.parse;\n    f(§$);`), "dollar-escape"));
  test("passing $ to a function whose parameter isn't $ is dollar-escape", () =>
    refused(reg(`  on("ui.press", ($, e, next) => { helper(§$); return next(e); });`, `function helper(x) { return x; }`), "dollar-escape"));
  test("passing $ in a position where the parameter isn't $ is dollar-escape", () =>
    refused(reg(`  on("ui.press", ($, e, next) => { helper(1, §$); return next(e); });`, `function helper($, a) { $.ui.log(a); }`), "dollar-escape"));
  test("passing $ to a helper that is a let is dollar-escape", () =>
    refused(reg(`  on("ui.press", ($, e, next) => { h(§$); return next(e); });`, `let h = ($) => {};`), "dollar-escape"));
  test("passing $ to an imported-looking call result is dollar-escape", () =>
    refused(reg(`  on("ui.press", ($, e, next) => { make()(§$); return next(e); });`, `const make = () => ($) => {};`), "dollar-escape"));
  test("optional chaining on $ is dollar-escape", async () => {
    await refused(hook(`    §$?.ui.log("x");`), "dollar-escape");
    await refused(hook(`    §$.ui?.log("x");`), "dollar-escape");
    await refused(hook(`    §$.ui.log?.("x");`), "dollar-escape");
  });
  test("an unknown method is dollar-unknown", async () => {
    await refused(hook(`    $.ui.§explode();`), "dollar-unknown");
    await refused(hook(`    $.§foo();`), "dollar-unknown");
  });
  test("reading a method without calling it is dollar-escape", () => refused(hook(`    const l = §$.ui.log;`), "dollar-escape"));
  test("assigning to $.mod.name is dollar-escape, calling it is dollar-unknown", async () => {
    await refused(hook(`    §$.mod.name = "x";`), "dollar-escape");
    await refused(hook(`    $.mod.§name();`), "dollar-unknown");
  });
  test("a binding named $ that isn't a parameter is dollar-escape", async () => {
    await refused(`const §$ = 1;\nexport function register() {}`, "dollar-escape");
    await refused(`export function register() { const { §$ } = {}; }`, "dollar-escape");
    await refused(`function §$() {}\nexport function register() {}`, "dollar-escape");
  });
  test("arguments is dollar-escape", () => refused(hook(`    §arguments[0];`), "dollar-escape"));
  test("the hook's first parameter must be named $ when it's used", () =>
    refused(reg(`  on("ui.press", (§ctx, e, next) => { ctx.ui.log("x"); return next(e); });`), "dollar-escape"));
  test("an unused first parameter may have any name", async () => {
    expect((await check({ source: reg(`  on("ui.press", (_, e, next) => next(e));`) })).report.ok).toBe(true);
  });
  test("a destructured or rest first parameter of a hook is dollar-escape", async () => {
    await refused(reg(`  on("ui.press", (§{ ui }, e, next) => next(e));`), "dollar-escape");
    await refused(reg(`  on("ui.press", (§...all) => 1);`), "dollar-escape");
  });
  test("the .catch handler's first parameter follows the same rule", async () => {
    await refused(reg(`  on("ui.press", ($, e, next) => next(e)).catch((§c, e, next) => { c.ui.log("x"); return next(e); });`), "dollar-escape");
    const ok = await check({ source: reg(`  on("ui.press", ($, e, next) => next(e)).catch(($, e, next) => { $.ui.log("x"); return next(e); });`) });
    expect(ok.report.ok).toBe(true);
  });
  test("a hook given by name is checked like an inline one", () =>
    refused(reg(`  on("ui.press", handler);`, `function handler(§ctx, e, next) { ctx.ui.log("x"); return next(e); }`), "dollar-escape"));
  test("calls are sorted and without repeats", async () => {
    const r = await check({ source: hook(`    $.ui.log("a"); $.store.get("k"); $.ui.log("b"); $.clock.now();`) });
    expect(r.report.calls).toEqual(["clock.now", "store.get", "ui.log"]);
  });
});

describe("state", () => {
  test("a non-literal state key is state-key", async () => {
    await refused(hook(`    const k = "a";\n    $.state.get(§k);`), "state-key");
    await refused(hook(`    $.state.set(§"a" + "b", 1);`), "state-key");
    await refused(hook(`    $.state.§get();`), "state-key");
  });
  test("literal keys are listed sorted and without repeats", async () => {
    const r = await check({ source: hook("    $.state.set(\"b\", 1); $.state.get(\"a\"); $.state.get(`b`);") });
    expect(r.report.state).toEqual(["a", "b"]);
    expect(r.report.calls).toEqual(["state.get", "state.set"]);
  });
});

describe("pages", () => {
  const page = (path: string) => hook(`    const { Page } = $.ui.resolve(e);\n    Page({ key: "p", path: ${path}, title: "T" });`);
  test("an existing page is listed, with no warning", async () => {
    const r = await check({ source: page(`"ui/index.html"`), files: { "ui/index.html": "<p>" } });
    expect(r.report.pages).toEqual(["ui/index.html"]);
    expect(r.report.warnings).toEqual([]);
  });
  test("a missing page is listed with a page-missing warning at its path", async () => {
    const { source, line, column } = mark(page(`§"nope.html"`));
    const r = await check({ source });
    expect(r.report.ok).toBe(true);
    expect(r.report.pages).toEqual(["nope.html"]);
    expect(r.report.warnings).toEqual([{ line, column, code: "page-missing", message: expect.any(String) }]);
  });
  test.each([["../x.html"], ["/etc/hosts.html"], ["ui/page.txt"], ["ui"], [""]])("page path %p is page-missing", async (path) => {
    const r = await check({ source: page(JSON.stringify(path)), files: { "ui/page.txt": "x" } });
    expect(r.report.warnings.map((w) => w.code)).toEqual(["page-missing"]);
  });
  test("a page that is a link to outside the folder is page-missing", async () => {
    const other = await check({ source: reg(""), files: { "o.html": "x" } });
    const r = await check({ source: page(`"link.html"`), links: { "link.html": `${other.root}/o.html` } });
    expect(r.report.warnings.map((w) => w.code)).toEqual(["page-missing"]);
  });
  test("a non-literal path is dynamic-page", async () => {
    const r = await check({ source: page(`"a" + "b"`) });
    expect(r.report.warnings.map((w) => w.code)).toEqual(["dynamic-page"]);
    expect(r.report.pages).toEqual([]);
  });
  test("a member .Page call is found too", async () => {
    const r = await check({ source: hook(`    const els = $.ui.resolve(e);\n    els.Page({ key: "p", path: "x.html", title: "T" });`), files: { "x.html": "x" } });
    expect(r.report.pages).toEqual(["x.html"]);
  });
});

describe("report", () => {
  test("lines count the module's lines, a trailing newline adds none", async () => {
    expect((await check({ source: "export function register() {}" })).report.lines).toBe(1);
    expect((await check({ source: "export function register() {}\n" })).report.lines).toBe(1);
    expect((await check({ source: "export function register() {}\n\n" })).report.lines).toBe(2);
  });
  test("sha256 is of the file's bytes", async () => {
    const source = "export function register() {} // é\n";
    const r = await check({ source });
    expect(r.report.sha256).toBe(new Bun.CryptoHasher("sha256").update(Buffer.from(source)).digest("hex"));
  });
  test("a failing report still carries what was found", async () => {
    const r = await check({ source: reg(`  on("ui.press", ($, e, next) => next(e));\n  fetch("x");`) });
    expect(r.report.ok).toBe(false);
    expect(r.report.hooks).toEqual([{ event: "ui.press" }]);
    expect(r.report.name).toBe("demo-mod");
    expect(r.report.sha256).toHaveLength(64);
    expect(r.manifest).toBeUndefined();
  });
  test("errors are ordered by position", async () => {
    const r = await check({ source: reg(`  fetch("x");\n  [].constructor;\n  require("y");`) });
    expect(r.report.errors.map((e) => e.line)).toEqual([3, 4, 5]);
  });
});
