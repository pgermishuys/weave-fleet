import { defineStore } from "pinia";
import { computed, shallowRef } from "vue";
import type { GlobalShortcut } from "@/lib/command-registry";
import { DEFAULT_KEYBINDINGS, type KeyBinding, type KeyBindingsConfig } from "@/lib/keybinding-types";
import {
  detectGlobalConflict,
  detectPaletteConflict,
  mergeWithDefaults,
  type ConflictResult,
} from "@/lib/keybinding-utils";

const KEYBINDINGS_STORAGE_KEY = "weave:keybindings";

function isGlobalShortcut(value: unknown): value is GlobalShortcut {
  if (!value || typeof value !== "object") {
    return false;
  }

  const candidate = value as Partial<GlobalShortcut>;
  return typeof candidate.key === "string";
}

function isKeyBinding(value: unknown): value is KeyBinding {
  if (!value || typeof value !== "object") {
    return false;
  }

  const candidate = value as Partial<KeyBinding>;
  const hasValidPaletteHotkey = candidate.paletteHotkey === null || typeof candidate.paletteHotkey === "string";
  const hasValidGlobalShortcut = candidate.globalShortcut === null || isGlobalShortcut(candidate.globalShortcut);

  return hasValidPaletteHotkey && hasValidGlobalShortcut;
}

function readStoredOverrides(): Partial<KeyBindingsConfig> {
  if (typeof window === "undefined") {
    return {};
  }

  try {
    const rawValue = window.localStorage.getItem(KEYBINDINGS_STORAGE_KEY);

    if (!rawValue) {
      return {};
    }

    const parsedValue = JSON.parse(rawValue) as unknown;
    if (!parsedValue || typeof parsedValue !== "object") {
      return {};
    }

    return Object.fromEntries(
      Object.entries(parsedValue).filter((entry): entry is [string, KeyBinding] => isKeyBinding(entry[1])),
    );
  } catch {
    return {};
  }
}

function persistOverrides(overrides: Partial<KeyBindingsConfig>): void {
  if (typeof window === "undefined") {
    return;
  }

  try {
    window.localStorage.setItem(KEYBINDINGS_STORAGE_KEY, JSON.stringify(overrides));
  } catch {
    // localStorage unavailable
  }
}

export const useKeybindingsStore = defineStore("keybindings", () => {
  const userOverrides = shallowRef<Partial<KeyBindingsConfig>>(readStoredOverrides());

  const bindings = computed<KeyBindingsConfig>(() => {
    return mergeWithDefaults(userOverrides.value, DEFAULT_KEYBINDINGS);
  });

  const hasCustomBindings = computed(() => Object.keys(userOverrides.value).length > 0);

  function updateBinding(commandId: string, partialBinding: Partial<KeyBinding>): ConflictResult | null {
    const currentBinding = bindings.value[commandId] ?? { paletteHotkey: null, globalShortcut: null };
    const nextBinding: KeyBinding = { ...currentBinding, ...partialBinding };

    if (nextBinding.paletteHotkey !== null && nextBinding.paletteHotkey !== undefined) {
      const paletteConflict = detectPaletteConflict(commandId, nextBinding.paletteHotkey, bindings.value);
      if (paletteConflict) {
        return paletteConflict;
      }
    }

    if (nextBinding.globalShortcut !== null && nextBinding.globalShortcut !== undefined) {
      const globalConflict = detectGlobalConflict(commandId, nextBinding.globalShortcut, bindings.value);
      if (globalConflict) {
        return globalConflict;
      }
    }

    const nextOverrides = {
      ...userOverrides.value,
      [commandId]: nextBinding,
    } satisfies Partial<KeyBindingsConfig>;

    userOverrides.value = nextOverrides;
    persistOverrides(nextOverrides);
    return null;
  }

  function resetBinding(commandId: string): void {
    const nextOverrides = { ...userOverrides.value };
    delete nextOverrides[commandId];

    userOverrides.value = nextOverrides;
    persistOverrides(nextOverrides);
  }

  function resetToDefaults(): void {
    userOverrides.value = {};
    persistOverrides({});
  }

  return {
    bindings,
    userOverrides,
    hasCustomBindings,
    updateBinding,
    resetBinding,
    resetToDefaults,
  };
});
