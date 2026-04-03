import { describe, it, expect } from "vitest";
import {
  detectPaletteConflict,
  detectGlobalConflict,
  formatShortcut,
  mergeWithDefaults,
} from "@/lib/keybinding-utils";
import type { KeyBindingsConfig } from "@/lib/keybinding-types";
import { DEFAULT_KEYBINDINGS } from "@/lib/keybinding-types";

// ─── detectPaletteConflict ───────────────────────────────────────────────────

describe("detectPaletteConflict", () => {
  it("ReturnsNullWhenHotkeyIsUnique", () => {
    const result = detectPaletteConflict("nav-fleet", "z", DEFAULT_KEYBINDINGS);
    expect(result).toBeNull();
  });

  it("ReturnsConflictWhenHotkeyIsDuplicated", () => {
    // "s" is bound to "nav-settings" by default
    const result = detectPaletteConflict("nav-fleet", "s", DEFAULT_KEYBINDINGS);
    expect(result).not.toBeNull();
    expect(result?.type).toBe("palette");
    expect(result?.conflictingCommandId).toBe("nav-settings");
    expect(result?.key).toBe("s");
  });

  it("SelfAssignmentIsNotAConflict", () => {
    // "f" is the existing hotkey for "nav-fleet"; re-assigning same key should not conflict
    const result = detectPaletteConflict("nav-fleet", "f", DEFAULT_KEYBINDINGS);
    expect(result).toBeNull();
  });

  it("ReturnsNullForEmptyBindings", () => {
    const bindings: KeyBindingsConfig = {};
    const result = detectPaletteConflict("nav-fleet", "f", bindings);
    expect(result).toBeNull();
  });
});

// ─── detectGlobalConflict ────────────────────────────────────────────────────

describe("detectGlobalConflict", () => {
  it("ReturnsNullWhenShortcutIsUnique", () => {
    const result = detectGlobalConflict(
      "new-command",
      { key: "z", platformModifier: true },
      DEFAULT_KEYBINDINGS
    );
    expect(result).toBeNull();
  });

  it("ReturnsConflictOnMatchingKeyAndModifiers", () => {
    // toggle-sidebar uses { key: "b", platformModifier: true }
    const result = detectGlobalConflict(
      "new-command",
      { key: "b", platformModifier: true },
      DEFAULT_KEYBINDINGS
    );
    expect(result).not.toBeNull();
    expect(result?.type).toBe("global");
    expect(result?.conflictingCommandId).toBe("toggle-sidebar");
    expect(result?.key).toBe("b");
  });

  it("ReturnsNullWhenModifiersDiffer", () => {
    // "b" without platformModifier does not conflict with toggle-sidebar
    const result = detectGlobalConflict(
      "new-command",
      { key: "b", platformModifier: false },
      DEFAULT_KEYBINDINGS
    );
    expect(result).toBeNull();
  });

  it("SelfAssignmentIsNotAConflict", () => {
    const result = detectGlobalConflict(
      "toggle-sidebar",
      { key: "b", platformModifier: true },
      DEFAULT_KEYBINDINGS
    );
    expect(result).toBeNull();
  });

  it("ReturnsNullForEmptyBindings", () => {
    const bindings: KeyBindingsConfig = {};
    const result = detectGlobalConflict(
      "toggle-sidebar",
      { key: "b", platformModifier: true },
      bindings
    );
    expect(result).toBeNull();
  });

  it("ReturnsNullWhenNoGlobalShortcutsExist", () => {
    const bindings: KeyBindingsConfig = {
      "nav-fleet": { paletteHotkey: "f", globalShortcut: null },
    };
    const result = detectGlobalConflict(
      "new-command",
      { key: "f", platformModifier: true },
      bindings
    );
    expect(result).toBeNull();
  });
});

// ─── formatShortcut ──────────────────────────────────────────────────────────

describe("formatShortcut", () => {
  it("FormatsWithCommandSymbolOnMac", () => {
    expect(formatShortcut({ key: "b", platformModifier: true }, true)).toBe("⌘B");
  });

  it("FormatsWithCtrlPlusOnNonMac", () => {
    expect(formatShortcut({ key: "b", platformModifier: true }, false)).toBe("Ctrl+B");
  });

  it("FormatsMetaKeyAsCommandSymbol", () => {
    expect(formatShortcut({ key: "k", metaKey: true }, true)).toBe("⌘K");
    expect(formatShortcut({ key: "k", metaKey: true }, false)).toBe("⌘K");
  });

  it("FormatsCtrlKeyAsCtrlPlus", () => {
    expect(formatShortcut({ key: "k", ctrlKey: true }, true)).toBe("Ctrl+K");
    expect(formatShortcut({ key: "k", ctrlKey: true }, false)).toBe("Ctrl+K");
  });

  it("FormatsKeyWithNoModifier", () => {
    expect(formatShortcut({ key: "x" }, true)).toBe("X");
    expect(formatShortcut({ key: "x" }, false)).toBe("X");
  });

  it("UppercasesTheKey", () => {
    expect(formatShortcut({ key: "enter", platformModifier: true }, true)).toBe("⌘ENTER");
  });
});

// ─── mergeWithDefaults ───────────────────────────────────────────────────────

describe("mergeWithDefaults", () => {
  it("ReturnsCopyOfDefaultsWhenNoUserOverrides", () => {
    const result = mergeWithDefaults({}, DEFAULT_KEYBINDINGS);
    expect(result).toEqual(DEFAULT_KEYBINDINGS);
    // Must be a copy, not the same reference
    expect(result).not.toBe(DEFAULT_KEYBINDINGS);
  });

  it("UserOverridesWinOverDefaults", () => {
    const userBindings: Partial<KeyBindingsConfig> = {
      "nav-fleet": { paletteHotkey: "g", globalShortcut: null },
    };
    const result = mergeWithDefaults(userBindings, DEFAULT_KEYBINDINGS);
    expect(result["nav-fleet"].paletteHotkey).toBe("g");
  });

  it("GapsFillWithDefaultBindings", () => {
    const userBindings: Partial<KeyBindingsConfig> = {
      "nav-fleet": { paletteHotkey: "g", globalShortcut: null },
    };
    const result = mergeWithDefaults(userBindings, DEFAULT_KEYBINDINGS);
    // Other commands should retain defaults
    expect(result["nav-settings"]).toEqual(DEFAULT_KEYBINDINGS["nav-settings"]);
    expect(result["toggle-sidebar"]).toEqual(DEFAULT_KEYBINDINGS["toggle-sidebar"]);
  });

  it("UserCanSetHotkeyToNull", () => {
    const userBindings: Partial<KeyBindingsConfig> = {
      "nav-fleet": { paletteHotkey: null, globalShortcut: null },
    };
    const result = mergeWithDefaults(userBindings, DEFAULT_KEYBINDINGS);
    expect(result["nav-fleet"].paletteHotkey).toBeNull();
  });

  it("FalsyUserBindingValueIsSkipped", () => {
    // Passing undefined for a key (Partial allows it) should not override default
    const userBindings: Partial<KeyBindingsConfig> = {
      "nav-fleet": undefined,
    };
    const result = mergeWithDefaults(userBindings, DEFAULT_KEYBINDINGS);
    expect(result["nav-fleet"]).toEqual(DEFAULT_KEYBINDINGS["nav-fleet"]);
  });
});
