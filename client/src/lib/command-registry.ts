import type { Component } from "vue";
import type { CommandId } from "@/lib/command-ids";
import { defineContributionPoint } from "@/lib/contributions";

export type CommandCategory = "Session" | "Navigation" | "View" | "Fleet";

export interface GlobalShortcut {
  key: string;
  platformModifier?: boolean;
  metaKey?: boolean;
  ctrlKey?: boolean;
  shiftKey?: boolean;
}

export interface Command {
  id: CommandId;
  label: string;
  description?: string;
  icon?: Component;
  category: CommandCategory;
  paletteHotkey?: string;
  globalShortcut?: GlobalShortcut;
  action: () => void;
  keywords?: string[];
  disabled?: boolean;
  /** The global shortcut also works while typing in a field (the composer, the terminal). */
  allowInEditable?: boolean;
  /** Sub-commands shown when this command is selected (drills into a nested level) */
  subCommands?: Command[];
  /** Dynamic sub-command generator — called when selected if subCommands is not set */
  getSubCommands?: () => Command[];
}

/**
 * Every command the palette lists and a shortcut can run. Fleet's own commands are contributed by `useCommands`;
 * ids are unique, and a later contribution with the same id replaces the earlier one.
 */
export const commandPoint = defineContributionPoint<Command, CommandId>({
  name: "commands",
  idOf: (command) => command.id,
});
