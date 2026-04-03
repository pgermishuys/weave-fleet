"use client";

import { createContext, useCallback, useContext, useMemo, type ReactNode } from "react";
import { usePersistedState } from "@/hooks/use-persisted-state";
import {
  DEFAULT_KEYBINDINGS,
  type KeyBinding,
  type KeyBindingsConfig,
} from "@/lib/keybinding-types";
import {
  mergeWithDefaults,
  detectPaletteConflict,
  detectGlobalConflict,
  type ConflictResult,
} from "@/lib/keybinding-utils";

interface KeybindingsContextValue {
  bindings: KeyBindingsConfig;
  updateBinding: (commandId: string, binding: Partial<KeyBinding>) => ConflictResult | null;
  resetBinding: (commandId: string) => void;
  resetToDefaults: () => void;
  hasCustomBindings: boolean;
}

const KeybindingsContext = createContext<KeybindingsContextValue | null>(null);

interface KeybindingsProviderProps {
  children: ReactNode;
}

export function KeybindingsProvider({ children }: KeybindingsProviderProps) {
  const [userOverrides, setUserOverrides] = usePersistedState<Partial<KeyBindingsConfig>>(
    "weave:keybindings",
    {}
  );

  const bindings = useMemo(
    () => mergeWithDefaults(userOverrides, DEFAULT_KEYBINDINGS),
    [userOverrides]
  );

  const updateBinding = useCallback(
    (commandId: string, partial: Partial<KeyBinding>): ConflictResult | null => {
      const current = bindings[commandId] ?? { paletteHotkey: null, globalShortcut: null };
      const next: KeyBinding = { ...current, ...partial };

      if (next.paletteHotkey !== null && next.paletteHotkey !== undefined) {
        const conflict = detectPaletteConflict(commandId, next.paletteHotkey, bindings);
        if (conflict) return conflict;
      }

      if (next.globalShortcut !== null && next.globalShortcut !== undefined) {
        const conflict = detectGlobalConflict(commandId, next.globalShortcut, bindings);
        if (conflict) return conflict;
      }

      setUserOverrides((prev) => ({ ...prev, [commandId]: next }));
      return null;
    },
    [bindings, setUserOverrides]
  );

  const resetBinding = useCallback(
    (commandId: string) => {
      setUserOverrides((prev) => {
        const next = { ...prev };
        delete next[commandId];
        return next;
      });
    },
    [setUserOverrides]
  );

  const resetToDefaults = useCallback(() => {
    setUserOverrides({});
  }, [setUserOverrides]);

  const hasCustomBindings = Object.keys(userOverrides).length > 0;

  const value: KeybindingsContextValue = useMemo(
    () => ({
      bindings,
      updateBinding,
      resetBinding,
      resetToDefaults,
      hasCustomBindings,
    }),
    [bindings, updateBinding, resetBinding, resetToDefaults, hasCustomBindings]
  );

  return (
    <KeybindingsContext.Provider value={value}>
      {children}
    </KeybindingsContext.Provider>
  );
}

export function useKeybindings(): KeybindingsContextValue {
  const ctx = useContext(KeybindingsContext);
  if (!ctx) {
    throw new Error("useKeybindings must be used within a KeybindingsProvider");
  }
  return ctx;
}
