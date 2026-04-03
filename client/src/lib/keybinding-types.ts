import type { GlobalShortcut } from "@/lib/command-registry";

export interface KeyBinding {
  paletteHotkey: string | null;
  globalShortcut: GlobalShortcut | null;
}

export type KeyBindingsConfig = Record<string, KeyBinding>;

export const DEFAULT_KEYBINDINGS: KeyBindingsConfig = {
  // Navigation
  "nav-fleet":          { paletteHotkey: "f", globalShortcut: null },
  "nav-settings":       { paletteHotkey: "s", globalShortcut: null },
  "nav-next-session":   { paletteHotkey: null, globalShortcut: { key: "]", platformModifier: true } },
  "nav-prev-session":   { paletteHotkey: null, globalShortcut: { key: "[", platformModifier: true } },

  // Session
  "new-session":        { paletteHotkey: "n", globalShortcut: { key: "n", platformModifier: true, metaKey: true } },
  "refresh-sessions":   { paletteHotkey: "r", globalShortcut: null },
  "focus-prompt":       { paletteHotkey: "/", globalShortcut: null },
  "interrupt-session":  { paletteHotkey: null, globalShortcut: { key: "Escape" } },
  "copy-session-id":    { paletteHotkey: null, globalShortcut: null },
  "toggle-diff-view":   { paletteHotkey: "d", globalShortcut: { key: "d", platformModifier: true, metaKey: true } },
  "fork-session":       { paletteHotkey: null, globalShortcut: null },
  "export-conversation":{ paletteHotkey: null, globalShortcut: null },
  "scroll-to-top":      { paletteHotkey: null, globalShortcut: null },
  "scroll-to-bottom":   { paletteHotkey: null, globalShortcut: null },
  "clear-conversation": { paletteHotkey: null, globalShortcut: null },

  // View
  "toggle-sidebar":     { paletteHotkey: "b", globalShortcut: { key: "b", platformModifier: true } },
  "toggle-activity-filter": { paletteHotkey: null, globalShortcut: null },
  "cycle-theme":        { paletteHotkey: null, globalShortcut: { key: "t", platformModifier: true, metaKey: true } },
  "toggle-todo-panel":  { paletteHotkey: null, globalShortcut: null },
  "toggle-fullscreen":  { paletteHotkey: null, globalShortcut: null },
  "zoom-in":            { paletteHotkey: null, globalShortcut: { key: "=", platformModifier: true } },
  "zoom-out":           { paletteHotkey: null, globalShortcut: { key: "-", platformModifier: true } },
  "toggle-dark-light":  { paletteHotkey: null, globalShortcut: null },
};
