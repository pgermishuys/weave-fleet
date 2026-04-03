"use client";

import { useState, useCallback, useMemo } from "react";
import { useCommandRegistry } from "@/contexts/command-registry-context";
import type { Command, CommandCategory } from "@/lib/command-registry";
import { useCommandHistory } from "@/hooks/use-command-history";
import { ChevronLeft } from "lucide-react";
import {
  CommandDialog,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
  CommandShortcut,
} from "@/components/ui/command";

function formatGlobalShortcut(gs: NonNullable<Command["globalShortcut"]>): string {
  const isMac =
    typeof navigator !== "undefined" &&
    /Mac|iPhone|iPad|iPod/.test(navigator.userAgent);

  let modifier = "";
  if (gs.platformModifier) {
    modifier = isMac ? "⌘" : "Ctrl+";
  } else if (gs.metaKey) {
    modifier = "⌘";
  } else if (gs.ctrlKey) {
    modifier = "Ctrl+";
  }

  const key = gs.key.toUpperCase();
  return `${modifier}${key}`;
}

const CATEGORY_ORDER: CommandCategory[] = ["Session", "Navigation", "View", "Fleet"];

export function CommandPalette() {
  const { commands, paletteOpen, setPaletteOpen } = useCommandRegistry();
  const { recentIds, recordUsage } = useCommandHistory();
  const [search, setSearch] = useState("");
  // Sub-command navigation stack
  const [subStack, setSubStack] = useState<Array<{ parent: Command; items: Command[] }>>([]);

  const handleOpenChange = useCallback((open: boolean) => {
    setPaletteOpen(open);
    if (!open) {
      setSearch("");
      setSubStack([]);
    }
  }, [setPaletteOpen]);

  const goBack = useCallback(() => {
    setSubStack((prev) => prev.slice(0, -1));
    setSearch("");
  }, []);

  // Current level of commands
  const currentLevel = subStack.length > 0 ? subStack[subStack.length - 1] : null;
  const activeCommands = currentLevel ? currentLevel.items : commands;

  // Group commands by category
  const grouped = useMemo(() => {
    return CATEGORY_ORDER.map((category) => ({
      category,
      items: activeCommands.filter((c) => c.category === category),
    })).filter((g) => g.items.length > 0);
  }, [activeCommands]);

  // Recent commands (only at top level, when no search)
  const recentCommands = useMemo(() => {
    if (subStack.length > 0 || search) return [];
    return recentIds
      .map((id) => commands.find((c) => c.id === id))
      .filter((c): c is Command => c !== undefined)
      .slice(0, 5);
  }, [subStack.length, search, recentIds, commands]);

  const handleSelect = useCallback((command: Command) => {
    if (command.disabled) return;

    // Check for sub-commands
    const subs = command.subCommands ?? command.getSubCommands?.();
    if (subs && subs.length > 0) {
      setSubStack((prev) => [...prev, { parent: command, items: subs }]);
      setSearch("");
      return;
    }

    // Execute and record
    recordUsage(command.id);
    command.action();
    handleOpenChange(false);
  }, [recordUsage, handleOpenChange]);

  const handleInputKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    const isMac =
      typeof navigator !== "undefined" &&
      /Mac|iPhone|iPad|iPod/.test(navigator.userAgent);

    // Cmd+K / Ctrl+K closes the palette when inside the input
    if (e.key === "k" && (isMac ? e.metaKey : e.ctrlKey)) {
      e.preventDefault();
      setPaletteOpen(false);
      return;
    }

    // Backspace at empty search goes back one level
    if (e.key === "Backspace" && search === "" && subStack.length > 0) {
      e.preventDefault();
      goBack();
      return;
    }

    // Only intercept single-char palette hotkeys when search is empty, no modifiers, and at top level
    if (search === "" && subStack.length === 0 && !e.metaKey && !e.ctrlKey && !e.altKey && e.key.length === 1) {
      for (const command of commands) {
        if (
          command.paletteHotkey === e.key &&
          !command.disabled
        ) {
          e.preventDefault();
          recordUsage(command.id);
          command.action();
          setPaletteOpen(false);
          return;
        }
      }
    }
  };

  const renderCommandItem = (command: Command) => {
    const Icon = command.icon;
    const hasSubs = !!(command.subCommands ?? command.getSubCommands);
    return (
      <CommandItem
        key={command.id}
        value={[command.label, ...(command.keywords ?? [])].join(" ")}
        disabled={command.disabled}
        data-disabled={command.disabled ? "true" : undefined}
        onSelect={() => handleSelect(command)}
      >
        {Icon && <Icon />}
        <span className="flex-1">{command.label}</span>
        {command.description && (
          <span className="text-xs text-muted-foreground mr-2">{command.description}</span>
        )}
        {hasSubs ? (
          <span className="text-xs text-muted-foreground">›</span>
        ) : (command.globalShortcut || command.paletteHotkey) ? (
          <CommandShortcut>
            {command.globalShortcut ? (
              <kbd className="pointer-events-none inline-flex h-5 select-none items-center gap-1 rounded border bg-muted px-1.5 font-mono text-[10px] font-medium text-muted-foreground opacity-100">
                {formatGlobalShortcut(command.globalShortcut)}
              </kbd>
            ) : command.paletteHotkey ? (
              <kbd className="pointer-events-none inline-flex h-5 select-none items-center gap-1 rounded border bg-muted px-1.5 font-mono text-[10px] font-medium text-muted-foreground opacity-100">
                {command.paletteHotkey}
              </kbd>
            ) : null}
          </CommandShortcut>
        ) : null}
      </CommandItem>
    );
  };

  return (
    <CommandDialog
      open={paletteOpen}
      onOpenChange={handleOpenChange}
      title="Command Palette"
      description="Search for a command to run..."
      showCloseButton={false}
      className="top-[10%] translate-y-0"
    >
      <CommandInput
        value={search}
        onValueChange={setSearch}
        placeholder={currentLevel ? `${currentLevel.parent.label}...` : "Type a command or search..."}
        onKeyDown={handleInputKeyDown}
      />
      <CommandList className="max-h-[50vh] sm:max-h-[300px]">
        <CommandEmpty>No commands found.</CommandEmpty>
        {/* Back button when in sub-commands */}
        {subStack.length > 0 && (
          <CommandGroup heading="">
            <CommandItem
              value="__back__"
              onSelect={goBack}
            >
              <ChevronLeft className="h-4 w-4" />
              <span>Back</span>
            </CommandItem>
          </CommandGroup>
        )}
        {/* Recent commands section (top level only, no search) */}
        {recentCommands.length > 0 && (
          <CommandGroup heading="Recent">
            {recentCommands.map(renderCommandItem)}
          </CommandGroup>
        )}
        {grouped.map(({ category, items }) => (
          <CommandGroup key={category} heading={category}>
            {items.map(renderCommandItem)}
          </CommandGroup>
        ))}
      </CommandList>
      {/* Footer with keyboard hints — desktop only */}
      <div className="hidden sm:flex items-center justify-between border-t px-3 py-2 text-xs text-muted-foreground">
        <div className="flex items-center gap-3">
          <span>
            <kbd className="font-mono">↑↓</kbd> Navigate
          </span>
          <span>
            <kbd className="font-mono">↵</kbd> Select
          </span>
          {subStack.length > 0 && (
            <span>
              <kbd className="font-mono">⌫</kbd> Back
            </span>
          )}
          <span>
            <kbd className="font-mono">Esc</kbd> Close
          </span>
        </div>
        <span className="opacity-50">
          <kbd className="font-mono">⌘K</kbd> toggle
        </span>
      </div>
    </CommandDialog>
  );
}
