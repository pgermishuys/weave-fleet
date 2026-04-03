import type { LucideIcon } from "lucide-react";

export type CommandCategory = "Session" | "Navigation" | "View" | "Fleet";

export interface GlobalShortcut {
  key: string;
  platformModifier?: boolean;
  metaKey?: boolean;
  ctrlKey?: boolean;
}

export interface Command {
  id: string;
  label: string;
  description?: string;
  icon?: LucideIcon;
  category: CommandCategory;
  paletteHotkey?: string;
  globalShortcut?: GlobalShortcut;
  action: () => void;
  keywords?: string[];
  disabled?: boolean;
  /** Sub-commands shown when this command is selected (drills into a nested level) */
  subCommands?: Command[];
  /** Dynamic sub-command generator — called when selected if subCommands is not set */
  getSubCommands?: () => Command[];
}

export interface CommandRegistryValue {
  commands: Command[];
  paletteOpen: boolean;
  setPaletteOpen: (open: boolean) => void;
  registerCommand: (command: Command) => void;
  unregisterCommand: (id: string) => void;
}
