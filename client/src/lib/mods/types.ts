/**
 * The parts of the mods contract (`mods/types/fleet-mods.d.ts`, `docs/mods/api.md`) the browser needs: the trees a mod
 * draws as they arrive on the wire, the sites they draw at, and what a control sends back. Mods themselves never see
 * these. `__tests__/mod-types-drift.test.ts` fails when the names here and in the `.d.ts` part ways.
 */

/** The places a mod can draw. */
export const MOD_RENDER_SITES = ["ToolUse", "ToolResult", "ComposerBand", "StatusChip", "Pane"] as const;
export type ModRenderSite = (typeof MOD_RENDER_SITES)[number];

/** Sites that take inline elements only: `Text`, `Pill`, `Icon`, `Button`, and a row `Box` holding only those. */
export const MOD_INLINE_SITES: readonly ModRenderSite[] = ["ToolUse", "StatusChip"];

export const MOD_ELEMENT_TYPES = ["Box", "Text", "Pill", "Icon", "Button", "Input", "Select", "Markdown", "Code", "Page"] as const;
export type ModElementType = (typeof MOD_ELEMENT_TYPES)[number];

export const MOD_ICON_NAMES = [
  "check", "x", "alert", "info", "circle", "dot", "clock", "loader", "play", "skip",
  "test", "bug", "terminal", "file", "folder", "git-branch", "search", "sparkles", "zap", "gauge",
  "arrow-right", "chevron-right", "external-link", "copy", "eye", "eye-off",
] as const;
export type ModIconName = (typeof MOD_ICON_NAMES)[number];

export const MOD_COLOR_ROLES = ["text", "muted", "accent", "good", "warn", "bad"] as const;
export type ModColorRole = (typeof MOD_COLOR_ROLES)[number];

export const MOD_TONES = ["good", "warn", "bad", "neutral", "accent"] as const;
export type ModTone = (typeof MOD_TONES)[number];

/** Spacing in Fleet's steps: 1 is 4 px. */
export const MOD_SPACES = [0, 1, 2, 3, 4, 6, 8] as const;
export type ModSpace = (typeof MOD_SPACES)[number];

export type ModJson = string | number | boolean | null | readonly ModJson[] | { readonly [key: string]: ModJson };

export type ModHandleName = "onPress" | "onSubmit" | "onInput" | "onSelect";

/**
 * A tree as it leaves the host: children flattened, and each callback replaced by a handle the host keeps. `Fleet` is
 * Fleet's own drawing of the site.
 */
export type ModWireElement =
  | { type: "Box" | "Text"; props: Record<string, ModJson>; children: (ModWireElement | string)[] }
  | { type: "Pill" | "Icon" | "Markdown" | "Code"; props: Record<string, ModJson> }
  /** `mod` is the mod that made the page, added by the host from its private ownership (a mod can't set it). */
  | { type: "Page"; props: Record<string, ModJson>; mod: string }
  | { type: "Button" | "Input" | "Select"; props: Record<string, ModJson>; handles: Partial<Record<ModHandleName, string>> }
  | { type: "Fleet" };

/** A control was used: what a site hands on (M6 posts it to `/api/sessions/{id}/mods/action`). */
export interface ModAction {
  handle: string;
  kind: "press" | "input" | "submit" | "select";
  value?: string;
}

/** Where the user was when they used a control. */
export type ModSurface = "desktop" | "phone";

/** A mod that had a hand in a tree, outermost first. */
export interface ModAuthor {
  name: string;
  draft: boolean;
}

/** The contract's limits that the browser enforces. */
export const MOD_LIMITS = {
  /** Text drawn per tree; the rest is cut. */
  treeTextChars: 100_000,
  treeNodes: 2_000,
  treeDepth: 32,
  /** A tree as JSON. Larger is invalid. */
  treeBytes: 262_144,
  /** `Input` changes sent per field. */
  inputChangesPerSecond: 4,
  selectOptionsMax: 200,
  /** Keys and pane ids. */
  keyChars: 64,
  pageQueryBytes: 4_096,
} as const;
