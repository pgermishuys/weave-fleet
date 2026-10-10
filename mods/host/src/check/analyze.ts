import { existsSync, realpathSync, statSync } from "node:fs";
import { isAbsolute, relative, resolve, sep } from "node:path";
import { analyze } from "eslint-scope";
import type { CheckProblem } from "fleet-mods/protocol";
import type { CheckReportHook } from "fleet-mods/protocol-shared";
import type { HostLimits } from "../limits";

// eslint-disable-next-line @typescript-eslint/no-explicit-any
type N = any;
type Json = null | boolean | number | string | Json[] | { [key: string]: Json };

export interface Analysis {
  hooks: CheckReportHook[];
  calls: string[];
  state: string[];
  pages: string[];
  errors: CheckProblem[];
  warnings: CheckProblem[];
}

/** Globals a mod may read: JavaScript's own, and `console`. */
const ALLOWED_GLOBALS = new Set([
  "Object", "Array", "Math", "JSON", "Date", "Map", "Set", "Promise", "RegExp", "String", "Number", "Boolean", "Symbol",
  "Error", "TypeError", "RangeError", "SyntaxError", "ReferenceError", "EvalError", "URIError", "AggregateError", "Intl",
  "parseInt", "parseFloat", "isNaN", "isFinite", "encodeURIComponent", "decodeURIComponent", "structuredClone",
  "console", "undefined", "NaN", "Infinity",
]);

/** The globals the contract names as refused, with a hint where Fleet has a replacement. */
const REFUSED_GLOBALS: Record<string, string> = {
  globalThis: "",
  self: "",
  window: "",
  global: "",
  process: "",
  Bun: "",
  Deno: "",
  fetch: "",
  WebSocket: "",
  XMLHttpRequest: "",
  Worker: "",
  setTimeout: ": use $.clock.after",
  setInterval: ": use $.clock.every",
  queueMicrotask: "",
  eval: "",
  Function: "",
  Reflect: "",
  Proxy: "",
  WebAssembly: "",
  SharedArrayBuffer: "",
  Atomics: "",
};

const EVENTS = ["session.start", "turn.complete", "ui.render", "ui.press", "ui.input", "ui.select"];

const DOLLAR_METHODS = new Set([
  "ui.resolve", "ui.invalidate", "ui.open", "ui.close", "ui.toast", "ui.log",
  "state.get", "state.set",
  "store.get", "store.set", "store.delete", "store.keys",
  "session.id", "session.title", "session.harness", "session.cwd", "session.surfaces",
  "clock.now", "clock.after", "clock.every",
]);

const PROTOTYPE_NAMES = new Set(["constructor", "__proto__", "prototype"]);
/** The old accessor methods: they read and write any property, `constructor` included, by a string. */
const LEGACY_ACCESSORS = new Set(["__lookupGetter__", "__lookupSetter__", "__defineGetter__", "__defineSetter__"]);
/** `Object` methods that only walk prototypes: a mod that draws never needs them. */
const PROTOTYPE_WALKERS = new Set(["getPrototypeOf", "setPrototypeOf", "getOwnPropertyDescriptors"]);
const isFunction = (node: N) =>
  node?.type === "FunctionDeclaration" || node?.type === "FunctionExpression" || node?.type === "ArrowFunctionExpression";

/** A string literal or a template with no substitutions: its text, else undefined. */
function literalText(node: N): string | undefined {
  if (node?.type === "Literal" && typeof node.value === "string") return node.value;
  if (node?.type === "TemplateLiteral" && node.expressions.length === 0) return node.quasis[0].value.cooked ?? undefined;
  return undefined;
}


const pos = (node: N) => ({ line: node.loc.start.line as number, column: (node.loc.start.column as number) + 1 });

function byPosition(a: CheckProblem, b: CheckProblem): number {
  return (a.line ?? 0) - (b.line ?? 0) || (a.column ?? 0) - (b.column ?? 0);
}

/** Whether `rel` names an `.html` file inside `root` (after normalising and following symlinks). */
function pageExists(root: string, rel: string): boolean {
  if (rel === "" || isAbsolute(rel) || /^[a-zA-Z]:[\\/]/.test(rel) || !rel.toLowerCase().endsWith(".html")) return false;
  const full = resolve(root, rel);
  const outside = (path: string, base: string) => {
    const r = relative(base, path);
    return r === "" || r.startsWith("..") || isAbsolute(r);
  };
  if (outside(full, root) || !existsSync(full)) return false;
  try {
    const real = realpathSync(full);
    const realRoot = realpathSync(root);
    return !outside(real, realRoot) && real.startsWith(realRoot + sep) && statSync(real).isFile();
  } catch {
    return false;
  }
}

/** Reads the matcher object literal as JSON, or says which node isn't a plain literal. */
function matcherValue(node: N): { ok: true; value: Json } | { ok: false; at: N } {
  switch (node.type) {
    case "Literal":
      if (node.regex) return { ok: true, value: { $regex: node.regex.pattern, flags: node.regex.flags } };
      if (typeof node.value === "bigint") return { ok: false, at: node };
      return { ok: true, value: node.value as Json };
    case "TemplateLiteral": {
      const text = literalText(node);
      return text === undefined ? { ok: false, at: node } : { ok: true, value: text };
    }
    case "UnaryExpression":
      if ((node.operator === "-" || node.operator === "+") && node.argument.type === "Literal" && typeof node.argument.value === "number") {
        return { ok: true, value: node.operator === "-" ? -node.argument.value : node.argument.value };
      }
      return { ok: false, at: node };
    case "ArrayExpression": {
      const items: Json[] = [];
      for (const element of node.elements) {
        if (!element || element.type === "SpreadElement") return { ok: false, at: element ?? node };
        const item = matcherValue(element);
        if (!item.ok) return item;
        items.push(item.value);
      }
      return { ok: true, value: items };
    }
    case "ObjectExpression": {
      const entries: [string, Json][] = [];
      for (const prop of node.properties) {
        if (prop.type !== "Property" || prop.computed || prop.kind !== "init" || prop.method || prop.shorthand) return { ok: false, at: prop };
        const key = prop.key.type === "Identifier" ? prop.key.name : prop.key.type === "Literal" ? String(prop.key.value) : undefined;
        if (key === undefined) return { ok: false, at: prop };
        const value = matcherValue(prop.value);
        if (!value.ok) return value;
        entries.push([key, value.value]);
      }
      return { ok: true, value: Object.fromEntries(entries) };
    }
    default:
      return { ok: false, at: node };
  }
}

/**
 * The analysis (docs/mods/api.md, "The static check"): walks a module's ESTree and its scopes, refusing every way for a
 * mod to reach past `$`, and reports what the module registers and touches.
 */
export function analyzeModule(ast: N, root: string, limits: Pick<HostLimits, "moduleScopes">): Analysis {
  const errors: CheckProblem[] = [];
  const warnings: CheckProblem[] = [];
  const fail = (code: string, message: string, node: N) => errors.push({ ...pos(node), code, message });
  const warn = (code: string, message: string, node: N) => warnings.push({ ...pos(node), code, message });

  const calls = new Set<string>();
  const stateKeys = new Set<string>();
  const pages = new Set<string>();
  const hooks: CheckReportHook[] = [];

  const scopeManager = analyze(ast, { ecmaVersion: 2026, sourceType: "module", fallback: "iteration" } as never);
  const scopes = scopeManager.scopes as N[];
  if (scopes.length > limits.moduleScopes) {
    fail("scopes", `${scopes.length} scopes: a mod may have at most ${limits.moduleScopes}`, scopes[limits.moduleScopes].block);
  }

  const parents = new Map<N, N>();
  const nodes: N[] = [];
  const index = (node: N, parent: N | undefined) => {
    if (!node || typeof node !== "object") return;
    if (Array.isArray(node)) {
      for (const item of node) index(item, parent);
      return;
    }
    if (typeof node.type !== "string") return;
    parents.set(node, parent);
    nodes.push(node);
    for (const key of Object.keys(node)) {
      if (key === "loc" || key === "type" || key === "regex") continue;
      index(node[key], node);
    }
  };
  index(ast, undefined);
  const parentOf = (node: N) => parents.get(node);

  const referenceOf = new Map<N, N>();
  for (const scope of scopes) for (const reference of scope.references) referenceOf.set(reference.identifier, reference);

  /** The function a binding is initialised with, when it can be told statically. */
  const functionOf = (variable: N, constOnly: boolean): N | undefined => {
    if (!variable || variable.defs.length !== 1) return undefined;
    const def = variable.defs[0];
    if (def.type === "FunctionName") {
      return variable.references.some((r: N) => r.isWrite()) ? undefined : def.node;
    }
    if (def.type === "Variable" && def.node.id === def.name && isFunction(def.node.init)) {
      if (constOnly && def.parent.kind !== "const") return undefined;
      return def.node.init;
    }
    return undefined;
  };
  const functionOfArgument = (arg: N): N | undefined => {
    if (isFunction(arg)) return arg;
    if (arg?.type === "Identifier") return functionOf(referenceOf.get(arg)?.resolved, true);
    return undefined;
  };

  // ── Modules, prototypes, computed keys, pages ───────────────────────────────────────────────────────────────────
  for (const node of nodes) {
    switch (node.type) {
      case "ImportDeclaration":
        fail("import", `imports aren't allowed: only \`import type\` from "fleet-mods" (${JSON.stringify(node.source.value)})`, node);
        break;
      case "ExportAllDeclaration":
        fail("import", "`export … from` isn't allowed: a mod is one module", node);
        break;
      case "ExportNamedDeclaration":
        if (node.source) fail("import", "`export … from` isn't allowed: a mod is one module", node);
        break;
      case "ImportExpression":
        fail("dynamic-import", "`import()` isn't allowed: a mod is one module", node);
        break;
      case "MetaProperty":
        if (node.meta.name === "import") fail("import-meta", "`import.meta` isn't allowed", node);
        break;
      case "WithStatement":
        fail("with", "`with` isn't allowed", node);
        break;
      case "MemberExpression": {
        const name = node.computed ? literalText(node.property) : node.property.type === "Identifier" ? node.property.name : undefined;
        if (name !== undefined && (PROTOTYPE_NAMES.has(name) || LEGACY_ACCESSORS.has(name))) {
          fail("prototype", `.${name} isn't allowed: it reaches the prototype chain`, node.property);
        }
        break;
      }
      case "ObjectPattern":
        for (const prop of node.properties) {
          if (prop.type !== "Property") continue;
          const name = prop.computed ? literalText(prop.key) : prop.key.type === "Identifier" ? prop.key.name : undefined;
          if (name !== undefined && PROTOTYPE_NAMES.has(name)) fail("prototype", `destructuring ${name} isn't allowed: it reaches the prototype chain`, prop.key);
        }
        break;
      case "ObjectExpression":
        for (const prop of node.properties) {
          if (prop.type !== "Property" || prop.computed || prop.shorthand || prop.kind !== "init") continue;
          const key = prop.key.type === "Identifier" ? prop.key.name : prop.key.value;
          if (key === "__proto__") fail("prototype", "a `__proto__` key isn't allowed: it sets the prototype", prop.key);
        }
        break;
      case "CallExpression": {
        const callee = node.callee;
        if (isObjectMethod(callee)) reflection(node, callee.property.name);
        const isPage =
          (callee.type === "Identifier" && callee.name === "Page") ||
          (callee.type === "MemberExpression" && !callee.computed && callee.property.type === "Identifier" && callee.property.name === "Page");
        if (!isPage || node.arguments[0]?.type !== "ObjectExpression") break;
        const pathProp = node.arguments[0].properties.find(
          (p: N) => p.type === "Property" && !p.computed && (p.key.name === "path" || p.key.value === "path"),
        );
        const text = pathProp ? literalText(pathProp.value) : undefined;
        if (text === undefined) {
          warn("dynamic-page", "Page path isn't a string literal, so the check can't see the file", pathProp ?? node);
        } else {
          pages.add(text);
          if (!pageExists(root, text)) warn("page-missing", `Page path ${JSON.stringify(text)} isn't an .html file inside the mod's folder`, pathProp.value);
        }
        break;
      }
    }
  }

  function isObjectMethod(callee: N): boolean {
    return callee.type === "MemberExpression" && !callee.computed && callee.object.type === "Identifier" && callee.object.name === "Object" && callee.property.type === "Identifier";
  }

  /** `Object.getOwnPropertyDescriptor(fp, "constructor")` and the like reach what `.constructor` would. */
  function reflection(call: N, method: string): void {
    if (PROTOTYPE_WALKERS.has(method)) {
      fail("prototype", `Object.${method} isn't allowed: it reaches the prototype chain`, call.callee.property);
      return;
    }
    for (const arg of call.arguments) {
      const text = literalText(arg);
      if (text !== undefined && PROTOTYPE_NAMES.has(text)) fail("prototype", `Object.${method} with "${text}" isn't allowed: it reaches the prototype chain`, arg);
      if (arg.type !== "ObjectExpression") continue;
      for (const prop of arg.properties) {
        if (prop.type !== "Property") continue;
        const key = prop.computed ? literalText(prop.key) : prop.key.type === "Identifier" ? prop.key.name : literalText(prop.key);
        if (key !== undefined && PROTOTYPE_NAMES.has(key)) fail("prototype", `Object.${method} with a "${key}" key isn't allowed: it reaches the prototype chain`, prop.key);
      }
    }
  }

  // ── Globals, require, arguments ─────────────────────────────────────────────────────────────────────────────────
  const seenGlobals = new Set<string>();
  const throughs = [...scopeManager.globalScope!.through].sort((a: N, b: N) => a.identifier.start - b.identifier.start);
  let reportedDollar = false;
  for (const reference of throughs as N[]) {
    const id = reference.identifier;
    const name: string = id.name;
    if (name === "require") {
      fail("require", "`require` isn't available to mods: a mod is one module", id);
    } else if (name === "arguments") {
      continue;
    } else if (name === "$") {
      if (!reportedDollar) fail("dollar-escape", "`$` only exists as a hook's first parameter", id);
      reportedDollar = true;
    } else if (!ALLOWED_GLOBALS.has(name) && !seenGlobals.has(name)) {
      seenGlobals.add(name);
      const hint = REFUSED_GLOBALS[name];
      fail("global", hint === undefined ? `${name} isn't available to mods: it isn't one of JavaScript's own globals` : `${name} isn't available to mods${hint}`, id);
    }
  }
  for (const scope of scopes) {
    for (const reference of scope.references) {
      if (reference.identifier.name === "arguments") fail("dollar-escape", "`arguments` isn't allowed: it can reach `$`", reference.identifier);
    }
  }

  // ── $ ───────────────────────────────────────────────────────────────────────────────────────────────────────────
  const isPlainRead = (node: N): boolean => {
    const p = parentOf(node);
    switch (p.type) {
      case "AssignmentExpression":
        return p.left !== node;
      case "UpdateExpression":
        return false;
      case "UnaryExpression":
        return p.operator !== "delete";
      case "CallExpression":
      case "NewExpression":
        return p.callee !== node;
      case "TaggedTemplateExpression":
        return p.tag !== node;
      case "ForInStatement":
      case "ForOfStatement":
        return p.left !== node;
      case "ArrayPattern":
      case "RestElement":
        return false;
      case "AssignmentPattern":
        return p.left !== node;
      case "Property":
        return parentOf(p).type !== "ObjectPattern" || p.value !== node;
      default:
        return true;
    }
  };

  const isSimpleMember = (node: N, object: N) =>
    node?.type === "MemberExpression" && node.object === object && !node.computed && !node.optional && node.property.type === "Identifier";

  const useDollar = (reference: N) => {
    const id = reference.identifier;
    const escape = (why: string) => fail("dollar-escape", why, id);
    if (reference.isWrite()) return escape("`$` can't be assigned to");

    const p = parentOf(id);
    if (p.type === "CallExpression" && p.callee !== id) {
      const at = p.arguments.indexOf(id);
      const helper = !p.optional && p.callee.type === "Identifier" ? functionOfArgument(p.callee) : undefined;
      if (helper && helper.params[at]?.type === "Identifier" && helper.params[at].name === "$") return;
      const who = p.callee.type === "Identifier" ? `\`${p.callee.name}\`` : "that function";
      return escape(`\`$\` can only be passed to a function whose parameter is also named \`$\`; ${who} isn't one`);
    }
    if (isSimpleMember(p, id)) {
      const ns: string = p.property.name;
      const gp = parentOf(p);
      if (isSimpleMember(gp, p)) {
        const method: string = gp.property.name;
        const key = `${ns}.${method}`;
        const ggp = parentOf(gp);
        if (ggp.type === "CallExpression" && ggp.callee === gp && !ggp.optional) {
          if (!DOLLAR_METHODS.has(key)) return fail("dollar-unknown", `$.${key} isn't part of $`, gp.property);
          calls.add(key);
          if (key === "state.get" || key === "state.set") {
            const arg = ggp.arguments[0];
            const text = literalText(arg);
            if (text === undefined) fail("state-key", `$.${key} needs a string literal key, so the check can list it`, arg ?? gp.property);
            else stateKeys.add(text);
          }
          return;
        }
        if (ns === "mod" && (method === "name" || method === "version") && isPlainRead(gp)) {
          calls.add(key);
          return;
        }
        return escape("`$` can only be used as `$.ns.method(…)`, or read as `$.mod.name` and `$.mod.version`");
      }
      const call = parentOf(p);
      if (call.type === "CallExpression" && call.callee === p && !call.optional) return fail("dollar-unknown", `$.${ns} isn't part of $: calls are $.ns.method(…)`, p.property);
    }
    return escape("`$` can only be used as `$.ns.method(…)`, or read as `$.mod.name` and `$.mod.version`");
  };

  for (const scope of scopes) {
    for (const variable of scope.variables) {
      if (variable.name !== "$") continue;
      const def = variable.defs[0];
      const direct = def?.type === "Parameter" && def.node.params.includes(def.name);
      if (!direct) {
        fail("dollar-escape", "`$` can only be a function's parameter", def ? def.name : scope.block);
        continue;
      }
      for (const reference of variable.references) useDollar(reference);
    }
  }

  /** A function that receives `$` from Fleet (a hook, a `.catch` handler): its first parameter must be `$`. */
  const checkReceiver = (fn: N) => {
    const first = fn.params[0];
    if (!first) return;
    if (first.type !== "Identifier") {
      fail("dollar-escape", "name the first parameter $ (no destructuring, rest or default)", first);
      return;
    }
    if (first.name === "$") return;
    const variable = (scopeManager.getDeclaredVariables(fn) as N[]).find((v) => v.defs.some((d: N) => d.name === first));
    if (variable && variable.references.length > 0) fail("dollar-escape", `name the first parameter $, not ${first.name}`, first);
  };

  // ── register and on ─────────────────────────────────────────────────────────────────────────────────────────────
  const moduleScope = scopes.find((s) => s.type === "module") ?? scopes[0];
  let registerFn: N;
  let registerNode: N;
  for (const stmt of ast.body) {
    if (stmt.type !== "ExportNamedDeclaration" || registerFn) continue;
    const d = stmt.declaration;
    if (d?.type === "FunctionDeclaration" && d.id?.name === "register") registerFn = d;
    if (d?.type === "VariableDeclaration") {
      for (const decl of d.declarations) {
        if (decl.id.type !== "Identifier" || decl.id.name !== "register") continue;
        registerNode = decl.id;
        if (isFunction(decl.init)) registerFn = decl.init;
      }
    }
    if (!stmt.source) {
      for (const spec of stmt.specifiers) {
        const exported = spec.exported.name ?? spec.exported.value;
        if (exported !== "register") continue;
        registerNode = spec.local;
        registerFn = functionOf(moduleScope.set.get(spec.local.name), false);
      }
    }
  }
  if (!registerFn) {
    if (registerNode) fail("no-register", "`register` is exported but isn't a function", registerNode);
    else errors.push({ line: 1, column: 1, code: "no-register", message: "the module must export a function named `register`" });
  } else {
    checkRegister(registerFn);
  }

  function checkRegister(fn: N) {
    const first = fn.params[0];
    if (!first) return;
    if (first.type !== "Identifier") {
      fail("on-escape", "register's first parameter must be a plain identifier (the `on` function)", first);
      return;
    }
    const variable = (scopeManager.getDeclaredVariables(fn) as N[]).find((v) => v.defs.some((d: N) => d.name === first));
    if (!variable) return;

    const direct: N[] = [];
    for (const reference of variable.references as N[]) {
      const id = reference.identifier;
      const p = parentOf(id);
      if (reference.init) continue;
      if (!(p.type === "CallExpression" && p.callee === id)) {
        fail("on-escape", `\`${first.name}\` can only be called, directly in register`, id);
        continue;
      }
      let up = parentOf(p);
      while (up && !isFunction(up)) up = parentOf(up);
      if (up !== fn) fail("on-outside-register", `\`${first.name}\` must be called directly in register, not in a nested function`, p);
      else direct.push(p);
    }
    direct.sort((a, b) => a.start - b.start);

    const bare = new Set<string>();
    for (const call of direct) {
      const args: N[] = call.arguments;
      if (args.length < 2 || args.length > 3 || args.some((a) => a.type === "SpreadElement")) {
        fail("on-arguments", `${first.name}(event, matcher?, hook) takes two or three arguments`, call);
        continue;
      }
      const event = literalText(args[0]);
      if (event === undefined) {
        fail("dynamic-event", "the event must be a string literal, so the check can list it", args[0]);
        continue;
      }
      if (!EVENTS.includes(event)) {
        fail("unknown-event", `${JSON.stringify(event)} isn't an event: ${EVENTS.join(", ")}`, args[0]);
        continue;
      }
      const hook: CheckReportHook = { event: event as CheckReportHook["event"] };
      if (args.length === 3) {
        const matcher = args[1];
        const value = matcher.type === "ObjectExpression" ? matcherValue(matcher) : ({ ok: false, at: matcher } as const);
        if (value.ok) hook.matcher = value.value;
        else fail("dynamic-matcher", "the matcher must be an object literal of literals, regular expressions, arrays and objects", value.at);
      } else if (bare.has(event)) {
        fail("duplicate-hook", `${event} is registered twice without a matcher`, call);
      } else {
        bare.add(event);
      }
      hooks.push(hook);

      const receivers = [functionOfArgument(args[args.length - 1])];
      const member = parentOf(call);
      if (member.type === "MemberExpression" && member.object === call && !member.computed && member.property.name === "catch") {
        const outer = parentOf(member);
        if (outer.type === "CallExpression" && outer.callee === member) receivers.push(functionOfArgument(outer.arguments[0]));
      }
      for (const receiver of receivers) if (receiver) checkReceiver(receiver);
    }
  }

  errors.sort(byPosition);
  warnings.sort(byPosition);
  return {
    hooks,
    calls: [...calls].sort(),
    state: [...stateKeys].sort(),
    pages: [...pages].sort(),
    errors,
    warnings,
  };
}
