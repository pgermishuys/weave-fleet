import { MOD_ID_PATTERN } from "@/lib/mods/page-url";
import {
  MOD_COLOR_ROLES,
  MOD_ELEMENT_TYPES,
  MOD_ICON_NAMES,
  MOD_INLINE_SITES,
  MOD_LIMITS,
  MOD_SPACES,
  MOD_TONES,
  type ModElementType,
  type ModHandleName,
  type ModRenderSite,
  type ModWireElement,
} from "@/lib/mods/types";

export type ModTreeCheck =
  | { ok: true; tree: ModWireElement | null; textCut: boolean }
  | { ok: false; reason: string };

type Plain = Record<string, unknown>;

/** Returns a problem with the value, or undefined when it is fine. */
type PropCheck = (value: unknown) => string | undefined;

const KEY_PATTERN = /^[A-Za-z0-9_.-]{1,64}$/;
const WIDTH_PATTERN = /^(\d+(?:\.\d+)?)%$/;

const isPlain = (value: unknown): value is Plain => {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return false;
  const proto = Object.getPrototypeOf(value);
  return proto === Object.prototype || proto === null;
};

const oneOf = (allowed: readonly (string | number)[]): PropCheck => (value) =>
  allowed.includes(value as string | number) ? undefined : `must be one of ${allowed.join(", ")}`;

const isString: PropCheck = (value) => (typeof value === "string" ? undefined : "must be a string");
const isBoolean: PropCheck = (value) => (typeof value === "boolean" ? undefined : "must be true or false");
const isKey: PropCheck = (value) =>
  typeof value === "string" && KEY_PATTERN.test(value) ? undefined : "must be 1 to 64 letters, digits, _, - or .";

const isWidth: PropCheck = (value) => {
  const match = typeof value === "string" ? WIDTH_PATTERN.exec(value) : null;
  return match && Number(match[1]) <= 100 ? undefined : 'must be a percentage from "0%" to "100%"';
};

const isLine: PropCheck = (value) =>
  typeof value === "number" && Number.isInteger(value) && value >= 1 ? undefined : "must be a positive integer";

const isOptions: PropCheck = (value) => {
  if (!Array.isArray(value) || value.length < 1 || value.length > MOD_LIMITS.selectOptionsMax) {
    return `must be 1 to ${MOD_LIMITS.selectOptionsMax} options`;
  }
  const seen = new Set<string>();
  for (const option of value) {
    if (!isPlain(option) || typeof option.value !== "string" || typeof option.label !== "string"
      || Object.keys(option).length !== 2) {
      return "each option is { value: string, label: string }";
    }
    if (seen.has(option.value)) return `duplicate option value "${option.value}"`;
    seen.add(option.value);
  }
  return undefined;
};

const isQuery: PropCheck = (value) => {
  if (!isPlain(value) || !Object.values(value).every((v) => typeof v === "string")) {
    return "must be an object of strings";
  }
  const size = new TextEncoder().encode(new URLSearchParams(value as Record<string, string>).toString()).length;
  return size <= MOD_LIMITS.pageQueryBytes ? undefined : `is ${size} bytes as a query (limit ${MOD_LIMITS.pageQueryBytes})`;
};

const space = oneOf(MOD_SPACES);
const color = oneOf(MOD_COLOR_ROLES);
const icon = oneOf(MOD_ICON_NAMES);
const wrap = oneOf(["wrap", "truncate"]);

/** Every prop an element takes and how to check it. Callbacks and `children` are not props on the wire. */
const PROP_CHECKS: Record<ModElementType, Record<string, PropCheck>> = {
  Box: {
    key: isKey,
    flexDirection: oneOf(["row", "column"]),
    gap: space,
    padding: space,
    paddingX: space,
    paddingY: space,
    alignItems: oneOf(["start", "center", "end", "stretch", "baseline"]),
    justifyContent: oneOf(["start", "center", "end", "space-between"]),
    flexWrap: oneOf(["wrap", "nowrap"]),
    flexGrow: oneOf([0, 1]),
    width: isWidth,
    borderStyle: oneOf(["round", "single", "dashed", "quote"]),
    borderColor: color,
    background: oneOf(["subtle", "tint"]),
  },
  Text: { color, bold: isBoolean, italic: isBoolean, strikethrough: isBoolean, code: isBoolean, dimColor: isBoolean, wrap },
  Pill: { tone: oneOf(MOD_TONES), label: isString, icon },
  Icon: { name: icon, color, label: isString },
  Button: { key: isKey, label: isString, icon, tone: oneOf(["primary", "danger", "quiet"]), disabled: isBoolean },
  Input: { key: isKey, label: isString, placeholder: isString, value: isString, submitLabel: isString },
  Select: { key: isKey, label: isString, options: isOptions, value: isString },
  Markdown: { key: isKey, text: isString, dimColor: isBoolean },
  Code: { source: isString, language: isString, path: isString, startLine: isLine, format: oneOf(["source", "diff"]), wrap },
  Page: { key: isKey, path: isString, title: isString, query: isQuery },
};

const REQUIRED_PROPS: Record<ModElementType, readonly string[]> = {
  Box: [],
  Text: [],
  Pill: ["tone", "label"],
  Icon: ["name"],
  Button: ["key", "label"],
  Input: ["key"],
  Select: ["key", "options"],
  Markdown: ["text"],
  Code: ["source"],
  Page: ["key", "path", "title"],
};

/** The props each element takes, for the drift test against `fleet-mods.d.ts`. */
export const MOD_ELEMENT_PROPS = Object.fromEntries(
  MOD_ELEMENT_TYPES.map((type) => [type, Object.keys(PROP_CHECKS[type])]),
) as unknown as Record<ModElementType, readonly string[]>;

/** The handles a control takes, and which it must have. */
const HANDLES: Partial<Record<ModElementType, { allowed: readonly ModHandleName[]; required: readonly ModHandleName[] }>> = {
  Button: { allowed: ["onPress"], required: ["onPress"] },
  Input: { allowed: ["onSubmit", "onInput"], required: [] },
  Select: { allowed: ["onSelect"], required: ["onSelect"] },
};

/** Props holding text a person reads, in the order they are drawn. */
const TEXT_PROPS: Partial<Record<ModElementType, readonly string[]>> = {
  Pill: ["label"],
  Icon: ["label"],
  Button: ["label"],
  Input: ["label", "placeholder", "submitLabel", "value"],
  Select: ["label"],
  Markdown: ["text"],
  Code: ["source"],
  Page: ["title"],
};

const NODE_FIELDS: Record<string, readonly string[]> = {
  Box: ["type", "props", "children"],
  Text: ["type", "props", "children"],
  Button: ["type", "props", "handles"],
  Input: ["type", "props", "handles"],
  Select: ["type", "props", "handles"],
  Page: ["type", "props", "mod"],
};

/** The fields of a wire `Page`, for the drift test against `fleet-mods.d.ts`. */
export const MOD_PAGE_FIELDS: readonly string[] = NODE_FIELDS.Page;

const INLINE_TYPES = new Set(["Text", "Pill", "Icon", "Button", "Fleet"]);

class Invalid extends Error {}

// `includes` on an array looks at its elements only, so `toString` or `constructor` are not types.
const isElementType = (value: unknown): value is ModElementType =>
  MOD_ELEMENT_TYPES.includes(value as ModElementType);

function fail(path: string, message: string): never {
  throw new Invalid(path ? `${path}: ${message}` : message);
}

interface Walk {
  site: ModRenderSite;
  inline: boolean;
  keys: Set<string>;
  nodes: number;
  fleet: boolean;
}

function checkNode(node: unknown, path: string, depth: number, walk: Walk, parent: string | undefined): void {
  if (!isPlain(node)) fail(path, "not an element");
  const type = node.type;
  if (typeof type !== "string") fail(path, "element has no type");
  if (type !== "Fleet" && !isElementType(type)) fail(path, `unknown element ${JSON.stringify(type)}`);

  if (++walk.nodes > MOD_LIMITS.treeNodes) throw new Invalid(`tree has ${walk.nodes} elements (limit ${MOD_LIMITS.treeNodes})`);
  if (depth > MOD_LIMITS.treeDepth) throw new Invalid(`tree is ${depth} levels deep (limit ${MOD_LIMITS.treeDepth})`);

  if (walk.inline) {
    // A Box is a row unless it says column.
    const rowBox = type === "Box" && isPlain(node.props) && node.props.flexDirection !== "column";
    if (!INLINE_TYPES.has(type as string) && !rowBox) {
      throw new Invalid(`${path} is not inline (${walk.site} takes inline elements only)`);
    }
  }

  if (parent === "Text" && type !== "Text") fail(path, "a Text holds strings and Text only");

  if (type === "Fleet") {
    if (Object.keys(node).length !== 1) fail(path, "Fleet carries nothing else");
    if (walk.fleet) fail(path, "Fleet appears more than once");
    walk.fleet = true;
    return;
  }

  const allowedFields = Object.hasOwn(NODE_FIELDS, type) ? NODE_FIELDS[type] : ["type", "props"];
  for (const field of Object.keys(node)) {
    if (!allowedFields.includes(field)) fail(path, `${field === "children" || field === "handles" ? `${type} takes no ${field}` : `unknown field "${field}"`}`);
  }
  if (type === "Page" && (typeof node.mod !== "string" || !MOD_ID_PATTERN.test(node.mod))) {
    fail(path, "Page needs the id of the mod that made it (name@v1, name@draft:session)");
  }
  if (!isPlain(node.props)) fail(path, "props must be an object");

  const checks = PROP_CHECKS[type];
  for (const [name, value] of Object.entries(node.props)) {
    // Own names only: `valueOf`, `hasOwnProperty` and `__proto__` are on every object but props of none.
    const check = Object.hasOwn(checks, name) ? checks[name] : undefined;
    if (!check) fail(path, `unknown prop "${name}"`);
    const problem = check(value);
    if (problem) fail(path, `prop "${name}" ${problem}`);
  }
  for (const name of REQUIRED_PROPS[type]) {
    if (!Object.hasOwn(node.props, name)) fail(path, name === "key" ? "missing key" : `missing "${name}"`);
  }
  const key = node.props.key;
  if (typeof key === "string") {
    if (walk.keys.has(key)) throw new Invalid(`duplicate key "${key}"`);
    walk.keys.add(key);
  }

  const handles = HANDLES[type as ModElementType];
  if (handles) {
    if (!isPlain(node.handles)) fail(path, "handles must be an object");
    for (const [name, value] of Object.entries(node.handles)) {
      if (!handles.allowed.includes(name as ModHandleName)) fail(path, `unknown handle "${name}"`);
      if (typeof value !== "string" || value === "") fail(path, `handle "${name}" must be a non-empty string`);
    }
    for (const name of handles.required) {
      if (!Object.hasOwn(node.handles, name)) fail(path, `missing handle "${name}"`);
    }
  }

  if (type === "Box" || type === "Text") {
    if (!Array.isArray(node.children)) fail(path, "children must be an array");
    node.children.forEach((child, index) => {
      if (typeof child === "string") return;
      const childType = isPlain(child) && typeof child.type === "string" ? child.type : "child";
      checkNode(child, `${path} > ${childType}[${index}]`, depth + 1, walk, type);
    });
  }
}

interface Budget {
  left: number;
  cut: boolean;
}

function takeText(text: string, budget: Budget): string {
  if (text.length <= budget.left) {
    budget.left -= text.length;
    return text;
  }
  const kept = text.slice(0, budget.left);
  budget.left = 0;
  budget.cut = true;
  return kept;
}

function cutNode(node: ModWireElement | string, budget: Budget): ModWireElement | string {
  if (typeof node === "string") return takeText(node, budget);
  if (node.type === "Fleet") return node;
  if (node.type === "Box" || node.type === "Text") {
    return { ...node, children: node.children.map((child) => cutNode(child, budget)) };
  }
  const props: Record<string, unknown> = { ...node.props };
  for (const name of TEXT_PROPS[node.type] ?? []) {
    if (typeof props[name] === "string") props[name] = takeText(props[name], budget);
  }
  if (node.type === "Select" && Array.isArray(props.options)) {
    props.options = (props.options as { value: string; label: string }[]).map((option) => ({
      ...option,
      label: takeText(option.label, budget),
    }));
  }
  return { ...node, props } as ModWireElement;
}

/**
 * Checks a tree against the contract: elements and props, keys, handles, the inline rule for `site`, and the limits.
 * Anything wrong makes the whole tree invalid. Text past the budget is cut instead, in a copy; the input is untouched.
 */
export function validateModTree(tree: unknown, site: ModRenderSite): ModTreeCheck {
  if (tree === null) return { ok: true, tree: null, textCut: false };
  try {
    const walk: Walk = { site, inline: MOD_INLINE_SITES.includes(site), keys: new Set(), nodes: 0, fleet: false };
    const root = isPlain(tree) && typeof tree.type === "string" ? tree.type : "tree";
    checkNode(tree, root, 1, walk, undefined);

    let json: string;
    try {
      json = JSON.stringify(tree);
    } catch {
      throw new Invalid("tree is not JSON");
    }
    const bytes = new TextEncoder().encode(json).length;
    if (bytes > MOD_LIMITS.treeBytes) throw new Invalid(`tree is ${bytes} bytes (limit ${MOD_LIMITS.treeBytes})`);

    const budget: Budget = { left: MOD_LIMITS.treeTextChars, cut: false };
    const cut = cutNode(tree as ModWireElement, budget) as ModWireElement;
    return budget.cut ? { ok: true, tree: cut, textCut: true } : { ok: true, tree: tree as ModWireElement, textCut: false };
  } catch (error) {
    if (error instanceof Invalid) return { ok: false, reason: error.message };
    throw error;
  }
}
