import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import {
  MOD_COLOR_ROLES,
  MOD_ELEMENT_TYPES,
  MOD_ICON_NAMES,
  MOD_LIMITS,
  MOD_RENDER_SITES,
  MOD_SPACES,
  MOD_TONES,
} from "@/lib/mods/types";
import { MOD_ELEMENT_PROPS, MOD_PAGE_FIELDS } from "@/lib/mods/validate";

const dtsPath = resolve(dirname(fileURLToPath(import.meta.url)), "../../../../../mods/types/fleet-mods.d.ts");

/** The text between `start` and the first `end` after it. */
function between(source: string, start: RegExp, end: RegExp): string {
  const from = start.exec(source);
  if (!from) throw new Error(`fleet-mods.d.ts: could not find ${start}`);
  const rest = source.slice(from.index + from[0].length);
  const to = end.exec(rest);
  if (!to) throw new Error(`fleet-mods.d.ts: could not find the end ${end} after ${start}`);
  return rest.slice(0, to.index);
}

function literals(text: string): string[] {
  return [...text.matchAll(/"([^"]+)"|(?<![\w.])(\d+)(?![\w.])/g)].map((m) => m[1] ?? m[2]!);
}

/** The members of `export type Name = "a" | "b";`. */
export function parseUnion(source: string, name: string): string[] {
  return literals(between(source, new RegExp(`export type ${name}\\s*=`), /;/));
}

/** The method names of `export interface Elements { Box(…): Element; … }`. */
export function parseElementNames(source: string): string[] {
  const body = between(source, /export interface Elements\s*\{/, /\n {2}\}/);
  return [...body.matchAll(/^\s*(\w+)\(/gm)].map((m) => m[1]!);
}

/** The field names of `export interface <name> { … }`, from lines indented one level into the body. */
export function parseInterfaceFields(source: string, name: string): string[] {
  const body = between(source, new RegExp(`export interface ${name}\\s*\\{`), /\n {2}\}/);
  return [...body.matchAll(/^ {4}(?:readonly )?(\w+)\??:/gm)].map((m) => m[1]!);
}

export function parseLimits(source: string): Record<string, number> {
  const body = between(source, /export interface Limits\s*\{/, /\n {2}\}/);
  return Object.fromEntries(
    [...body.matchAll(/readonly (\w+): ([\d_]+);/g)].map((m) => [m[1]!, Number(m[2]!.replaceAll("_", ""))]),
  );
}

/** The handle names `WireElement` allows on controls. */
export function parseWireHandles(source: string): string[] {
  const wire = between(source, /export type WireElement\s*=/, /\n\}/);
  return literals(/handles: Partial<Record<([^,]+),/.exec(wire)?.[1] ?? "");
}

/**
 * The top-level fields of the wire `Page` in `WireElement`, or null while the `.d.ts` still lists Page among the leaves
 * that carry only props (before the host adds the owning mod).
 */
export function parseWirePageFields(source: string): string[] | null {
  const wire = between(source, /export type WireElement\s*=/, /\n\}/);
  const member = wire.split("\n").find((line) => /\{\s*type: "Page";/.test(line));
  return member ? [...member.matchAll(/(\w+)\??:/g)].map((m) => m[1]!).filter((f, i, all) => all.indexOf(f) === i && f !== "string" && f !== "Json" && f !== "Record") : null;
}

/** A readable message when two name lists differ, else undefined. */
export function diffNames(what: string, dts: readonly (string | number)[], client: readonly (string | number)[]): string | undefined {
  const a = new Set(dts.map(String));
  const b = new Set(client.map(String));
  const onlyDts = [...a].filter((n) => !b.has(n));
  const onlyClient = [...b].filter((n) => !a.has(n));
  if (!onlyDts.length && !onlyClient.length) return undefined;
  const parts = [];
  if (onlyDts.length) parts.push(`fleet-mods.d.ts has ${onlyDts.join(", ")} that client/src/lib/mods does not`);
  if (onlyClient.length) parts.push(`client/src/lib/mods has ${onlyClient.join(", ")} that fleet-mods.d.ts does not`);
  return `${what}: ${parts.join("; ")}`;
}

/** Every disagreement between the `.d.ts` text and the client mirror. */
export function findDrift(source: string): string[] {
  const found: (string | undefined)[] = [
    diffNames("render sites", parseUnion(source, "RenderComponent"), MOD_RENDER_SITES),
    diffNames("elements", parseElementNames(source), MOD_ELEMENT_TYPES),
    diffNames("icon names", parseUnion(source, "IconName"), MOD_ICON_NAMES),
    diffNames("colour roles", parseUnion(source, "ColorRole"), MOD_COLOR_ROLES),
    diffNames("tones", parseUnion(source, "Tone"), MOD_TONES),
    diffNames("spaces", parseUnion(source, "Space"), MOD_SPACES),
    diffNames("wire handle names", parseWireHandles(source), ["onPress", "onSubmit", "onInput", "onSelect"]),
  ];
  for (const type of MOD_ELEMENT_TYPES) {
    const fields = parseInterfaceFields(source, `${type}Props`).filter((f) => f !== "children" && !/^on[A-Z]/.test(f));
    found.push(diffNames(`${type} props`, fields, MOD_ELEMENT_PROPS[type] ?? []));
  }
  const pageFields = parseWirePageFields(source);
  if (pageFields) found.push(diffNames("wire Page fields", pageFields, MOD_PAGE_FIELDS));
  const limits = parseLimits(source);
  for (const name of ["treeTextChars", "treeNodes", "treeDepth", "treeBytes"] as const) {
    if (limits[name] !== MOD_LIMITS[name]) {
      found.push(`limit ${name}: fleet-mods.d.ts says ${limits[name]}, client/src/lib/mods says ${MOD_LIMITS[name]}`);
    }
  }
  return found.filter((message): message is string => message !== undefined);
}

describe("fleet-mods.d.ts and client/src/lib/mods agree", () => {
  const source = readFileSync(dtsPath, "utf8");

  it("reads the real contract", () => {
    expect(parseUnion(source, "RenderComponent")).toContain("ToolUse");
    expect(parseElementNames(source)).toContain("Box");
    expect(parseInterfaceFields(source, "ButtonProps")).toContain("onPress");
    expect(parseLimits(source).treeNodes).toBe(2000);
    expect(parseWireHandles(source)).toContain("onSelect");
  });

  it("has no drift", () => {
    expect(findDrift(source)).toEqual([]);
  });
});

describe("the wire Page", () => {
  const source = readFileSync(dtsPath, "utf8");
  const owned = `| { type: "Page"; props: Record<string, Json>; mod: ModId }`;
  const leaves = '| { type: "Pill" | "Icon" | "Markdown" | "Code"; props: Record<string, Json> }';

  it("is not checked while the .d.ts lists Page among the prop-only leaves", () => {
    expect(parseWirePageFields('export type WireElement =\n  | { type: "Pill" | "Icon" | "Markdown" | "Code" | "Page"; props: Record<string, Json> }\n}')).toBeNull();
  });

  it("is checked against the client's fields once the .d.ts gives it an owner", () => {
    const withOwner = source.replace(leaves.replace(' | "Code"', ' | "Code" | "Page"'), `${leaves}\n    ${owned}`);
    if (withOwner === source && parseWirePageFields(source) === null) throw new Error("fleet-mods.d.ts WireElement changed shape; update this test");
    const text = parseWirePageFields(source) === null ? withOwner : source;
    expect(parseWirePageFields(text)).toEqual(["type", "props", "mod"]);
    expect(findDrift(text)).toEqual([]);
  });

  it("names a field the .d.ts adds or drops", () => {
    const text = `export type WireElement =\n  ${owned.replace("mod: ModId", "mod: ModId; extra: string")}\n}`;
    expect(parseWirePageFields(text)).toEqual(["type", "props", "mod", "extra"]);
    expect(diffNames("wire Page fields", parseWirePageFields(text)!, MOD_PAGE_FIELDS)).toContain("fleet-mods.d.ts has extra");
    expect(diffNames("wire Page fields", ["type", "props"], MOD_PAGE_FIELDS)).toContain("client/src/lib/mods has mod");
  });
});

describe("the drift check", () => {
  const source = readFileSync(dtsPath, "utf8");

  it("passes on the real contract", () => {
    expect(findDrift(source)).toEqual([]);
  });

  it("names a render site added to the .d.ts", () => {
    const changed = source.replace('| "Pane";', '| "Pane" | "Sidebar";');
    expect(changed).not.toBe(source);
    expect(findDrift(changed)).toEqual(['render sites: fleet-mods.d.ts has Sidebar that client/src/lib/mods does not']);
  });

  it("names an element removed from the .d.ts", () => {
    const changed = source.replace(/^ {4}Page\(props: PageProps\): Element;\n/m, "");
    expect(changed).not.toBe(source);
    expect(findDrift(changed)).toEqual(['elements: client/src/lib/mods has Page that fleet-mods.d.ts does not']);
  });

  it("names an icon, colour, tone and space change", () => {
    const changed = source
      .replace('"eye-off";', '"eye-off" | "rocket";')
      .replace('"good" | "warn" | "bad";', '"good" | "warn" | "bad" | "info";')
      .replace('"neutral" | "accent";', '"accent";')
      .replace("| 6 | 8;", "| 6 | 8 | 12;");
    const found = findDrift(changed);
    expect(found).toContain("icon names: fleet-mods.d.ts has rocket that client/src/lib/mods does not");
    expect(found).toContain("colour roles: fleet-mods.d.ts has info that client/src/lib/mods does not");
    expect(found).toContain("tones: client/src/lib/mods has neutral that fleet-mods.d.ts does not");
    expect(found).toContain("spaces: fleet-mods.d.ts has 12 that client/src/lib/mods does not");
  });

  it("names a prop added to the .d.ts and one it does not know", () => {
    const changed = source.replace("    flexWrap?:", "    flexBasis?: number;\n    flexWrap?:");
    expect(findDrift(changed)).toEqual(["Box props: fleet-mods.d.ts has flexBasis that client/src/lib/mods does not"]);
    const lost = source.replace(/^ {4}startLine\?: number;\n/m, "");
    expect(findDrift(lost)).toEqual(["Code props: client/src/lib/mods has startLine that fleet-mods.d.ts does not"]);
  });

  it("names a limit that moved", () => {
    const changed = source.replace("treeNodes: 2_000;", "treeNodes: 3_000;");
    expect(findDrift(changed)).toEqual(["limit treeNodes: fleet-mods.d.ts says 3000, client/src/lib/mods says 2000"]);
  });

  it("names a handle added to the wire type", () => {
    const changed = source.replace('"onPress" | "onSubmit" | "onInput" | "onSelect", string', '"onPress" | "onSubmit" | "onInput" | "onSelect" | "onHover", string');
    expect(changed).not.toBe(source);
    expect(findDrift(changed)).toEqual(["wire handle names: fleet-mods.d.ts has onHover that client/src/lib/mods does not"]);
  });

  it("fails loudly when a section cannot be found", () => {
    expect(() => findDrift("nothing here")).toThrow(/could not find/);
  });
});
