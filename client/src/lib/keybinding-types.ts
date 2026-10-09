import type { GlobalShortcut } from "@/lib/command-registry";
import type { CommandId } from "@/lib/command-ids";
import { defineContributionPoint } from "@/lib/contributions";

export interface KeyBinding {
  paletteHotkey: string | null;
  globalShortcut: GlobalShortcut | null;
}

export type KeyBindingsConfig = Record<string, KeyBinding>;

export const DEFAULT_KEYBINDINGS: Partial<Record<CommandId, KeyBinding>> = {
  // Navigation
  "nav-fleet":          { paletteHotkey: "f", globalShortcut: null },
  "nav-settings":       { paletteHotkey: "s", globalShortcut: null },
  "nav-next-session":   { paletteHotkey: null, globalShortcut: { key: "]", platformModifier: true } },
  "nav-prev-session":   { paletteHotkey: null, globalShortcut: { key: "[", platformModifier: true } },

  // Session
  "new-session":        { paletteHotkey: "n", globalShortcut: { key: "n", platformModifier: true, metaKey: true } },
  "refresh-sessions":   { paletteHotkey: "r", globalShortcut: null },
  "focus-prompt":       { paletteHotkey: "/", globalShortcut: null },
  "go-to-file":         { paletteHotkey: null, globalShortcut: { key: "p", platformModifier: true } },
  "interrupt-session":  { paletteHotkey: null, globalShortcut: { key: "Escape" } },
  "copy-session-id":    { paletteHotkey: null, globalShortcut: null },
  "toggle-diff-view":   { paletteHotkey: "d", globalShortcut: { key: "d", platformModifier: true, metaKey: true } },
  "fork-session":       { paletteHotkey: null, globalShortcut: null },
  "new-session-in-folder": { paletteHotkey: null, globalShortcut: null },
  "export-conversation":{ paletteHotkey: null, globalShortcut: null },
  "scroll-to-top":      { paletteHotkey: null, globalShortcut: null },
  "scroll-to-bottom":   { paletteHotkey: null, globalShortcut: null },
  "clear-conversation": { paletteHotkey: null, globalShortcut: null },

  // View
  "toggle-sidebar":     { paletteHotkey: "b", globalShortcut: { key: "b", platformModifier: true } },
  "toggle-right-panel": { paletteHotkey: null, globalShortcut: { key: "b", platformModifier: true, shiftKey: true } },
  "toggle-terminal":    { paletteHotkey: null, globalShortcut: { key: "j", platformModifier: true } },
  "toggle-activity-filter": { paletteHotkey: null, globalShortcut: null },
  "cycle-theme":        { paletteHotkey: null, globalShortcut: { key: "t", platformModifier: true, metaKey: true } },
  "toggle-fullscreen":  { paletteHotkey: null, globalShortcut: null },
  "zoom-in":            { paletteHotkey: null, globalShortcut: { key: "=", platformModifier: true } },
  "zoom-out":           { paletteHotkey: null, globalShortcut: { key: "-", platformModifier: true } },
  "toggle-dark-light":  { paletteHotkey: null, globalShortcut: null },
};

export interface KeyBindingContribution extends KeyBinding {
  id: CommandId;
}

/**
 * The keys each command has before the user changes anything. Fleet's own are contributed here; ids are unique, and
 * a later contribution with the same id replaces the earlier one. What the user rebinds sits on top of this, in
 * the keybindings store.
 */
export const keybindingPoint = defineContributionPoint<KeyBindingContribution, CommandId>({
  name: "keybindings",
  idOf: (binding) => binding.id,
});

keybindingPoint.contribute(
  "fleet",
  (Object.entries(DEFAULT_KEYBINDINGS) as [CommandId, KeyBinding][]).map(([id, binding]) => ({ id, ...binding })),
);
