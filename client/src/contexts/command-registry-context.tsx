"use client";

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from "react";
import type { Command, CommandRegistryValue } from "@/lib/command-registry";

const CommandRegistryContext = createContext<CommandRegistryValue | null>(null);

interface CommandRegistryProviderProps {
  children: ReactNode;
}

export function CommandRegistryProvider({ children }: CommandRegistryProviderProps) {
  const commandsMapRef = useRef<Map<string, Command>>(new Map());
  const [version, setVersion] = useState(0);
  const [paletteOpen, setPaletteOpen] = useState(false);

  const bump = useCallback(() => setVersion((v) => v + 1), []);

  const registerCommand = useCallback(
    (command: Command) => {
      commandsMapRef.current.set(command.id, command);
      bump();
    },
    [bump]
  );

  const unregisterCommand = useCallback(
    (id: string) => {
      commandsMapRef.current.delete(id);
      bump();
    },
    [bump]
  );

  // Global keydown dispatcher
  useEffect(() => {
    const isMac =
      typeof navigator !== "undefined" &&
      /Mac|iPhone|iPad|iPod/.test(navigator.userAgent);

    const handleKeyDown = (e: KeyboardEvent) => {
      const target = e.target as HTMLElement | null;
      const isTextInput =
        target instanceof HTMLInputElement ||
        target instanceof HTMLTextAreaElement ||
        target?.isContentEditable;

      // Cmd+K / Ctrl+K toggles the palette — works even inside text inputs
      if (e.key === "k" && (isMac ? e.metaKey : e.ctrlKey)) {
        e.preventDefault();
        setPaletteOpen((prev) => !prev);
        return;
      }

      // Escape should work even inside text inputs (e.g. to interrupt a session)
      if (e.key === "Escape") {
        for (const command of commandsMapRef.current.values()) {
          const gs = command.globalShortcut;
          if (!gs || gs.key !== "Escape") continue;
          if (!command.disabled) {
            e.preventDefault();
            command.action();
          }
          return;
        }
      }

      // Skip global shortcuts when focus is inside text fields
      if (isTextInput) return;

      // Dispatch global shortcuts
      for (const command of commandsMapRef.current.values()) {
        const gs = command.globalShortcut;
        if (!gs) continue;
        if (e.key !== gs.key) continue;

        let modifierOk = false;
        if (gs.platformModifier) {
          modifierOk = isMac ? e.metaKey : e.ctrlKey;
        } else if (gs.metaKey || gs.ctrlKey) {
          if (gs.metaKey && e.metaKey) modifierOk = true;
          if (gs.ctrlKey && e.ctrlKey) modifierOk = true;
        } else {
          modifierOk = true;
        }

        if (!modifierOk) continue;

        e.preventDefault();
        if (!command.disabled) {
          command.action();
        }
        break;
      }
    };

    document.addEventListener("keydown", handleKeyDown);
    return () => document.removeEventListener("keydown", handleKeyDown);
  }, []);

  // Memoize sorted commands array — re-computes on version bump
  const commands = useMemo(() => {
    const categoryOrder: Record<string, number> = {
      Session: 0,
      Navigation: 1,
      View: 2,
      Fleet: 3,
    };
    return Array.from(commandsMapRef.current.values()).sort((a, b) => {
      const catDiff =
        (categoryOrder[a.category] ?? 99) - (categoryOrder[b.category] ?? 99);
      if (catDiff !== 0) return catDiff;
      return a.label.localeCompare(b.label);
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [version]);

  const value: CommandRegistryValue = useMemo(
    () => ({
      commands,
      paletteOpen,
      setPaletteOpen,
      registerCommand,
      unregisterCommand,
    }),
    [commands, paletteOpen, registerCommand, unregisterCommand]
  );

  return (
    <CommandRegistryContext.Provider value={value}>
      {children}
    </CommandRegistryContext.Provider>
  );
}

export function useCommandRegistry(): CommandRegistryValue {
  const ctx = useContext(CommandRegistryContext);
  if (!ctx) {
    throw new Error(
      "useCommandRegistry must be used within a CommandRegistryProvider"
    );
  }
  return ctx;
}
