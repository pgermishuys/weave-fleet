import type { GlobalShortcut } from "@/lib/command-registry";
import type { KeyBindingsConfig } from "@/lib/keybinding-types";

export interface ConflictResult {
  type: "palette" | "global";
  conflictingCommandId: string;
  key: string;
}

export function detectPaletteConflict(
  commandId: string,
  newHotkey: string,
  bindings: KeyBindingsConfig
): ConflictResult | null {
  for (const [id, binding] of Object.entries(bindings)) {
    if (id === commandId) continue;
    if (binding.paletteHotkey === newHotkey) {
      return { type: "palette", conflictingCommandId: id, key: newHotkey };
    }
  }
  return null;
}

export function detectGlobalConflict(
  commandId: string,
  newShortcut: GlobalShortcut,
  bindings: KeyBindingsConfig
): ConflictResult | null {
  for (const [id, binding] of Object.entries(bindings)) {
    if (id === commandId) continue;
    const gs = binding.globalShortcut;
    if (!gs) continue;
    if (
      gs.key === newShortcut.key &&
      !!gs.platformModifier === !!newShortcut.platformModifier &&
      !!gs.metaKey === !!newShortcut.metaKey &&
      !!gs.ctrlKey === !!newShortcut.ctrlKey
    ) {
      return { type: "global", conflictingCommandId: id, key: newShortcut.key };
    }
  }
  return null;
}

export function formatShortcut(shortcut: GlobalShortcut, isMac: boolean): string {
  let modifier = "";
  if (shortcut.platformModifier) {
    modifier = isMac ? "⌘" : "Ctrl+";
  } else if (shortcut.metaKey) {
    modifier = "⌘";
  } else if (shortcut.ctrlKey) {
    modifier = "Ctrl+";
  }
  return `${modifier}${shortcut.key.toUpperCase()}`;
}

export function mergeWithDefaults(
  userBindings: Partial<KeyBindingsConfig>,
  defaults: KeyBindingsConfig
): KeyBindingsConfig {
  const result = { ...defaults };
  for (const [id, binding] of Object.entries(userBindings)) {
    if (binding) {
      result[id] = binding;
    }
  }
  return result;
}
