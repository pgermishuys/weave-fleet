import { expect } from "bun:test";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { checkMod } from "../../src/check";

/** A tree element as the sample mods build it: the factories below just record their props. */
export type Node = { type: string; label?: string; children?: unknown[]; [prop: string]: unknown };

const fixtures = join(import.meta.dir, "fixtures");
export const fixture = (name: string): string => readFileSync(join(fixtures, name), "utf8");

/** Runs the mod's one hook against a tool row, the way the host would, and returns the tree it drew (or "next"). */
export async function render(mod: { register: (on: any, options: any) => unknown }, component: "ToolUse" | "ToolResult", props: Record<string, unknown>): Promise<Node | "next"> {
  let hook: any;
  mod.register((_event: string, _matcher: unknown, fn: unknown) => (hook = fn), {});
  const element = (type: string) => (p: Record<string, unknown>): Node => ({ type, ...p });
  const $ = { ui: { resolve: () => ({ Box: element("Box"), Text: element("Text"), Pill: element("Pill"), Icon: element("Icon") }) } };
  const next = async () => "next" as const;
  const base = { tool: "bash", status: "completed", input: {}, output: "" };
  return hook($, { component, sessionId: "ses_1", requestId: "call_1", props: { ...base, ...props } }, next);
}

/** Every Pill label in a tree, in order. */
export function pills(tree: Node | "next" | unknown): string[] {
  if (!tree || typeof tree !== "object") return [];
  const node = tree as Node;
  return [...(node.type === "Pill" ? [String(node.label)] : []), ...(node.children ?? []).flatMap(pills)];
}

/** Every Text line in a tree, in order. */
export function texts(tree: Node | "next" | unknown): string[] {
  if (!tree || typeof tree !== "object") return [];
  const node = tree as Node;
  return [...(node.type === "Text" ? [(node.children ?? []).join("")] : []), ...(node.children ?? []).flatMap(texts)];
}

/** Runs the host's static check on a sample and returns what it hooks. */
export async function checked(name: string) {
  const { report } = await checkMod(join(import.meta.dir, "..", "..", "..", "samples", name));
  expect(report.errors).toEqual([]);
  expect(report.ok).toBe(true);
  return report;
}
