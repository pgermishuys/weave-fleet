import type { ModJson, ModWireElement } from "@/lib/mods/types";

/** Where a mod's colour roles land in Fleet's theme. Every colour a mod draws goes through here, so trees follow the theme. */
const ROLE_COLORS: Readonly<Record<string, string>> = {
  text: "var(--text)",
  muted: "var(--muted)",
  accent: "var(--accent)",
  good: "var(--running)",
  warn: "var(--idle)",
  bad: "var(--error)",
};

/** A pill's tone: the roles, and `neutral` as the quiet one. */
const TONE_COLORS: Readonly<Record<string, string>> = {
  good: ROLE_COLORS.good,
  warn: ROLE_COLORS.warn,
  bad: ROLE_COLORS.bad,
  neutral: ROLE_COLORS.muted,
  accent: ROLE_COLORS.accent,
};

/** The CSS `align-items` / `justify-content` values for the contract's words. */
const SIDES: Readonly<Record<string, string>> = {
  start: "flex-start",
  center: "center",
  end: "flex-end",
  stretch: "stretch",
  baseline: "baseline",
  "space-between": "space-between",
};

export function roleColor(role: unknown): string | undefined {
  return typeof role === "string" ? ROLE_COLORS[role] : undefined;
}

export function toneColor(tone: unknown): string | undefined {
  return typeof tone === "string" ? TONE_COLORS[tone] : undefined;
}

export function sideValue(value: unknown): string | undefined {
  return typeof value === "string" ? SIDES[value] : undefined;
}

/** Fleet's spacing steps: 1 is 4 px. */
export function spacePx(step: unknown): string | undefined {
  return typeof step === "number" && Number.isFinite(step) && step >= 0 ? `${step * 4}px` : undefined;
}

/** A prop as a string, or nothing when it isn't one. */
export function str(value: ModJson | undefined): string | undefined {
  return typeof value === "string" ? value : undefined;
}

export function flag(value: ModJson | undefined): boolean {
  return value === true;
}

/** The wire shapes a component takes: `Box` and `Text` carry children, controls carry handles, the rest only props. */
export type ModContainerNode = Extract<ModWireElement, { children: unknown }>;
export type ModLeafNode = Extract<ModWireElement, { props: unknown; children?: never; handles?: never; mod?: never }>;
export type ModPageNode = Extract<ModWireElement, { type: "Page" }>;
export type ModControlNode = Extract<ModWireElement, { handles: unknown }>;
