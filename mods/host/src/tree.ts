/**
 * Element trees (docs/mods/api.md, "Elements" and "Render sites"): the factories `$.ui.resolve` hands a mod, and the
 * check-and-convert step that turns a mod's tree into the `WireElement` Fleet draws.
 */
import type { Elements, Element, Json, RenderComponent } from "fleet-mods";
import type { WireElement } from "fleet-mods/protocol";
import { LIMITS, type HostLimits } from "./limits";

export type ModId = string;
export type HandleKind = "onPress" | "onSubmit" | "onInput" | "onSelect";

/**
 * Who made each factory element. Kept here, out of every mod's reach: nothing on an element says who made it, so a
 * mod can't copy or forge another mod's ownership (review 2 of #486).
 */
const madeBy = new WeakMap<object, ModId>();

/** The mod whose factory made `element`, if a factory made it. */
export function ownerOf(element: unknown): ModId | undefined {
  return typeof element === "object" && element !== null ? madeBy.get(element) : undefined;
}

function made<T extends object>(owner: ModId, element: T): T {
  madeBy.set(element, owner);
  return element;
}

const isRecord = (v: unknown): v is Record<string, unknown> => typeof v === "object" && v !== null && !Array.isArray(v);

function copyProps(props: unknown): Record<string, unknown> {
  if (!isRecord(props)) return {};
  try {
    return { ...props };
  } catch {
    return {};
  }
}

function makeContainer(owner: ModId, type: "Box" | "Text", props: unknown): Element {
  const { children, ...rest } = copyProps(props);
  const list = Array.isArray(children) ? [...children] : children === undefined ? [] : [children];
  return made(owner, Object.freeze({ type, props: Object.freeze(rest), children: Object.freeze(list) })) as unknown as Element;
}

function makeLeaf(owner: ModId, type: string, props: unknown): Element {
  return made(owner, Object.freeze({ type, props: Object.freeze(copyProps(props)) })) as unknown as Element;
}

/** The factories for one mod. Each element is tagged with `owner`. */
export function createElements(owner: ModId): Elements {
  return {
    Box: (p) => makeContainer(owner, "Box", p),
    Text: (p) => makeContainer(owner, "Text", p),
    Pill: (p) => makeLeaf(owner, "Pill", p),
    Icon: (p) => makeLeaf(owner, "Icon", p),
    Button: (p) => makeLeaf(owner, "Button", p),
    Input: (p) => makeLeaf(owner, "Input", p),
    Select: (p) => makeLeaf(owner, "Select", p),
    Markdown: (p) => makeLeaf(owner, "Markdown", p),
    Code: (p) => makeLeaf(owner, "Code", p),
    Page: (p) => makeLeaf(owner, "Page", p),
  };
}

export interface WireOptions {
  site: RenderComponent;
  /** Owner of elements no factory made (a mod that wrote element data by hand): the hook's mod. */
  defaultOwner: ModId;
  /** Owner of an element no factory made, when known (the mod whose hook returned it); else `defaultOwner`. */
  ownerOf?: (element: object) => ModId | undefined;
  /** True when `path` names an `.html` file that exists inside `owner`'s folder. */
  pageExists: (owner: ModId, path: string) => boolean;
  /** Called once per callback in the tree, in tree order; returns the handle (`h17`) sent in its place. */
  allocHandle: (callback: { owner: ModId; kind: HandleKind; key: string; fn: (...args: any[]) => unknown }) => string;
  limits?: Partial<HostLimits>;
}

export type WireOutcome =
  | { ok: true; tree: WireElement | null; drawnBy: ModId[] }
  | { ok: false; reason: string };

// ─── Prop tables ───────────────────────────────────────────────────────────────────────────────────────────────────

type Check = (name: string, v: unknown) => string | null;
type Spec = { required: string[]; props: Record<string, Check> };

function show(v: unknown): string {
  let s: string;
  if (typeof v === "string" || typeof v === "number" || typeof v === "boolean" || v === null) s = JSON.stringify(v);
  else if (Array.isArray(v)) s = "an array";
  else s = `a ${typeof v}`;
  return s.length > 40 ? s.slice(0, 37) + "..." : s;
}

const oneOf = (values: readonly (string | number)[]): Check => (name, v) =>
  values.includes(v as string | number) ? null : `${name} ${show(v)} isn't one of ${values.join(", ")}`;
const str: Check = (name, v) => (typeof v === "string" ? null : `${name} must be a string, got ${show(v)}`);
const bool: Check = (name, v) => (typeof v === "boolean" ? null : `${name} must be true or false, got ${show(v)}`);
const fn: Check = (name, v) => (typeof v === "function" ? null : `${name} must be a function, got ${show(v)}`);

const KEY = /^[A-Za-z0-9_.-]{1,64}$/;
const key: Check = (name, v) =>
  typeof v === "string" && KEY.test(v) ? null : `${name} ${show(v)} must be 1 to 64 letters, digits, _, - or .`;

const ROLES = ["text", "muted", "accent", "good", "warn", "bad"] as const;
const TONES = ["good", "warn", "bad", "neutral", "accent"] as const;
const SPACES = [0, 1, 2, 3, 4, 6, 8] as const;
const ICONS = [
  "check", "x", "alert", "info", "circle", "dot", "clock", "loader", "play", "skip",
  "test", "bug", "terminal", "file", "folder", "git-branch", "search", "sparkles", "zap", "gauge",
  "arrow-right", "chevron-right", "external-link", "copy", "eye", "eye-off",
] as const;
const role = oneOf(ROLES);
const icon: Check = (name, v) => (ICONS.includes(v as never) ? null : `${name} ${show(v)} isn't an icon name (${ICONS.join(", ")})`);
const space = oneOf(SPACES);
const percent: Check = (name, v) => {
  const m = typeof v === "string" ? /^(\d+(?:\.\d+)?)%$/.exec(v) : null;
  return m && Number(m[1]) <= 100 ? null : `${name} ${show(v)} must be a percentage from 0% to 100%, like "50%"`;
};
const positiveInt: Check = (name, v) =>
  typeof v === "number" && Number.isInteger(v) && v > 0 ? null : `${name} must be a positive whole number, got ${show(v)}`;

const options: Check = (name, v) => {
  if (!Array.isArray(v) || v.length < 1 || v.length > 200) return `${name} must be an array of 1 to 200 options`;
  const seen = new Set<string>();
  for (let i = 0; i < v.length; i++) {
    const o = v[i];
    if (!isRecord(o)) return `${name}[${i}] must be { value, label }`;
    const keys = Object.keys(o);
    if (keys.some((k) => k !== "value" && k !== "label")) return `${name}[${i}] may only have value and label`;
    if (typeof o.value !== "string" || typeof o.label !== "string") return `${name}[${i}] needs a string value and a string label`;
    if (seen.has(o.value)) return `${name}[${i}] repeats the value ${show(o.value)}`;
    seen.add(o.value);
  }
  return null;
};

const query: Check = (name, v) => {
  if (!isRecord(v) || Object.values(v).some((x) => typeof x !== "string")) return `${name} must be an object of strings`;
  return Buffer.byteLength(JSON.stringify(v)) <= 4096 ? null : `${name} is over 4 KiB`;
};

const SPECS: Record<string, Spec> = {
  Box: {
    required: [],
    props: {
      key, flexDirection: oneOf(["row", "column"]), gap: space, padding: space, paddingX: space, paddingY: space,
      alignItems: oneOf(["start", "center", "end", "stretch", "baseline"]),
      justifyContent: oneOf(["start", "center", "end", "space-between"]),
      flexWrap: oneOf(["wrap", "nowrap"]), flexGrow: oneOf([0, 1]), width: percent,
      borderStyle: oneOf(["round", "single", "dashed", "quote"]), borderColor: role, background: oneOf(["subtle", "tint"]),
    },
  },
  Text: {
    required: [],
    props: { color: role, bold: bool, italic: bool, strikethrough: bool, code: bool, dimColor: bool, wrap: oneOf(["wrap", "truncate"]) },
  },
  Pill: { required: ["tone", "label"], props: { tone: oneOf(TONES), label: str, icon } },
  Icon: { required: ["name"], props: { name: icon, color: role, label: str } },
  Button: {
    required: ["key", "label", "onPress"],
    props: { key, label: str, onPress: fn, icon, tone: oneOf(["primary", "danger", "quiet"]), disabled: bool },
  },
  Input: {
    required: ["key"],
    props: { key, label: str, placeholder: str, value: str, submitLabel: str, onSubmit: fn, onInput: fn },
  },
  Select: { required: ["key", "options", "onSelect"], props: { key, label: str, options, value: str, onSelect: fn } },
  Markdown: { required: ["text"], props: { text: str, key, dimColor: bool } },
  Code: {
    required: ["source"],
    props: { source: str, language: str, path: str, startLine: positiveInt, format: oneOf(["source", "diff"]), wrap: oneOf(["wrap", "truncate"]) },
  },
  Page: { required: ["key", "path", "title"], props: { key, path: str, title: str, query } },
};

const KEYED = new Set(["Button", "Input", "Select", "Page"]);
const CALLBACKS: HandleKind[] = ["onPress", "onSubmit", "onInput", "onSelect"];
const INLINE = new Set(["Text", "Pill", "Icon", "Button", "Fleet", "Box"]);

// ─── The walk ──────────────────────────────────────────────────────────────────────────────────────────────────────

class Invalid extends Error {}
const fail = (path: string, msg: string): never => {
  throw new Error(`${path}: ${msg}`, { cause: Invalid });
};

interface Pending {
  owner: ModId;
  kind: HandleKind;
  key: string;
  fn: (...args: any[]) => unknown;
  into: Partial<Record<HandleKind, string>>;
}

interface Ctx {
  opts: WireOptions;
  limits: HostLimits;
  inline: boolean;
  nodes: number;
  textUsed: number;
  keys: Set<string>;
  fleetSeen: boolean;
  path: Set<object>;
  owners: ModId[];
  pending: Pending[];
}

/** Takes `s` against the text budget: whole, cut, or empty. */
function takeText(ctx: Ctx, s: string): string {
  const room = Math.max(0, ctx.limits.treeTextChars - ctx.textUsed);
  const out = s.length <= room ? s : s.slice(0, room);
  ctx.textUsed += out.length;
  return out;
}

function flatten(ctx: Ctx, path: string, parentType: string, children: unknown, out: unknown[]): void {
  if (Array.isArray(children)) {
    if (ctx.path.has(children)) fail(path, "children contain themselves");
    ctx.path.add(children);
    for (const c of children) flatten(ctx, path, parentType, c, out);
    ctx.path.delete(children);
    return;
  }
  if (children === false || children === null || children === undefined) return;
  if (typeof children === "string" || typeof children === "number") {
    out.push(String(children));
    return;
  }
  if (isRecord(children) && typeof children.type === "string") {
    if (parentType === "Text" && children.type !== "Text") fail(path, `Text can only hold strings, numbers and Text, not ${children.type}`);
    out.push(children);
    return;
  }
  fail(path, `a child of ${parentType} must be an element, string or number, got ${show(children)}`);
}

function visit(ctx: Ctx, node: unknown, depth: number, path: string): WireElement {
  if (!isRecord(node) || typeof node.type !== "string") fail(path, `not an element (got ${show(node)})`);
  const el = node as Record<string, unknown>;
  const type = el.type as string;
  if (type !== "Fleet" && !(type in SPECS)) fail(path, `unknown element type ${show(type)}`);
  if (ctx.path.has(el)) fail(path, "the tree contains itself");
  if (++ctx.nodes > ctx.limits.treeNodes) fail(path, `tree has more than ${ctx.limits.treeNodes} elements`);
  if (depth > ctx.limits.treeDepth) fail(path, `tree is deeper than ${ctx.limits.treeDepth} levels`);
  if (ctx.inline && !INLINE.has(type)) fail(path, `${type} isn't allowed at ${ctx.opts.site}, which takes inline elements only`);

  if (type === "Fleet") {
    if (Object.keys(el).length !== 1) fail(path, "Fleet takes no props or children");
    if (ctx.fleetSeen) fail(path, "Fleet may appear only once in a tree");
    ctx.fleetSeen = true;
    return { type: "Fleet" };
  }

  const container = type === "Box" || type === "Text";
  for (const k of Object.keys(el)) {
    if (k !== "type" && k !== "props" && !(container && k === "children")) fail(path, `unknown field "${k}" on an element`);
  }
  const rawProps = el.props === undefined ? {} : el.props;
  if (!isRecord(rawProps)) fail(path, `props must be an object, got ${show(rawProps)}`);
  const props = rawProps as Record<string, unknown>;
  const spec = SPECS[type];

  const owner = madeBy.get(el) ?? ctx.opts.ownerOf?.(el) ?? ctx.opts.defaultOwner;
  if (!ctx.owners.includes(owner)) ctx.owners.push(owner);

  const wireProps: Record<string, Json> = {};
  const callbacks: [HandleKind, (...args: any[]) => unknown][] = [];
  for (const name of Object.keys(props)) {
    const v = props[name];
    if (v === undefined) continue;
    const check = spec.props[name];
    if (!check) fail(path, `${name} isn't a prop of ${type}`);
    const problem = check(name, v);
    if (problem) fail(path, problem);
    if (CALLBACKS.includes(name as HandleKind)) callbacks.push([name as HandleKind, v as never]);
    else wireProps[name] = copyJson(v);
  }
  for (const name of spec.required) {
    if (props[name] === undefined) fail(path, `${name} is required`);
  }
  if (KEYED.has(type) || props.key !== undefined) {
    const k = props.key as string;
    if (ctx.keys.has(k)) fail(path, `key "${k}" is used twice in the tree`);
    ctx.keys.add(k);
  }
  if (type === "Pill" || type === "Icon") {
    // nothing further
  } else if (type === "Markdown") {
    wireProps.text = takeText(ctx, wireProps.text as string);
  } else if (type === "Code") {
    wireProps.source = takeText(ctx, wireProps.source as string);
  } else if (type === "Page") {
    if (!ctx.opts.pageExists(owner, props.path as string)) fail(path, `path ${show(props.path)} isn't an .html file in the mod's folder`);
  }
  if (ctx.inline && type === "Box" && props.flexDirection !== "row") {
    fail(path, `Box at ${ctx.opts.site} must have flexDirection "row"`);
  }

  if (container) {
    ctx.path.add(el);
    const flat: unknown[] = [];
    flatten(ctx, path, type, el.children, flat);
    const kids: (WireElement | string)[] = [];
    flat.forEach((c, i) => {
      if (typeof c === "string") kids.push(takeText(ctx, c));
      else kids.push(visit(ctx, c, depth + 1, `${path} > ${(c as { type: string }).type}[${i}]`));
    });
    ctx.path.delete(el);
    return { type, props: wireProps, children: kids } as WireElement;
  }
  if (callbacks.length === 0 && !(type === "Button" || type === "Input" || type === "Select")) {
    return { type, props: wireProps } as WireElement;
  }
  const handles: Partial<Record<HandleKind, string>> = {};
  callbacks.sort((a, b) => CALLBACKS.indexOf(a[0]) - CALLBACKS.indexOf(b[0]));
  for (const [kind, f] of callbacks) ctx.pending.push({ owner, kind, key: props.key as string, fn: f, into: handles });
  return { type, props: wireProps, handles } as WireElement;
}

function copyJson(v: unknown): Json {
  if (Array.isArray(v)) return v.map(copyJson);
  if (isRecord(v)) return Object.fromEntries(Object.keys(v).map((k) => [k, copyJson(v[k])]));
  return v as Json;
}

/**
 * Checks a tree against the element table, the site's rules and the limits, cuts text past the limit and converts it
 * to a WireElement. `tree` is what a hook returned (or `null`). Never throws: an invalid tree is `{ ok: false }`
 * with a reason a mod author can act on.
 */
export function toWire(tree: unknown, options: WireOptions): WireOutcome {
  if (tree === null) return { ok: true, tree: null, drawnBy: [] };
  try {
    const limits = { ...LIMITS, ...options.limits };
    const ctx: Ctx = {
      opts: options,
      limits,
      inline: options.site === "ToolUse" || options.site === "StatusChip",
      nodes: 0,
      textUsed: 0,
      keys: new Set(),
      fleetSeen: false,
      path: new Set(),
      owners: [],
      pending: [],
    };
    const rootType = isRecord(tree) && typeof tree.type === "string" ? tree.type : "tree";
    const wire = visit(ctx, tree, 1, rootType);
    // Measure with handles as long as any real one will be, so the limit holds for what goes on the wire.
    for (const p of ctx.pending) p.into[p.kind] = "h".padEnd(16, "0");
    const bytes = Buffer.byteLength(JSON.stringify(wire));
    if (bytes > limits.treeBytes) return { ok: false, reason: `tree is ${bytes} bytes as JSON, over the ${limits.treeBytes} byte limit` };
    for (const p of ctx.pending) p.into[p.kind] = options.allocHandle({ owner: p.owner, kind: p.kind, key: p.key, fn: p.fn });
    return { ok: true, tree: wire, drawnBy: ctx.owners };
  } catch (e) {
    const err = e instanceof Error ? e : new Error(String(e));
    if (err.cause === Invalid) return { ok: false, reason: err.message };
    return { ok: false, reason: `tree could not be read: ${err.message}` };
  }
}
