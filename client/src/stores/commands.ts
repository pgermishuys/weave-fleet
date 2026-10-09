import { defineStore } from "pinia";
import { computed, shallowRef } from "vue";
import type { CommandId } from "@/lib/command-ids";
import { commandPoint, type Command, type CommandCategory } from "@/lib/command-registry";

const RECENT_COMMANDS_STORAGE_KEY = "weave-fleet-vue-ui.command-recent-ids";

function loadRecentCommandIds(): string[] {
  if (typeof window === "undefined") {
    return [];
  }

  try {
    const storedValue = window.localStorage.getItem(RECENT_COMMANDS_STORAGE_KEY);

    if (!storedValue) {
      return [];
    }

    const parsedValue = JSON.parse(storedValue) as unknown;
    return Array.isArray(parsedValue)
      ? parsedValue.filter((value): value is string => typeof value === "string")
      : [];
  } catch {
    return [];
  }
}

function persistRecentCommandIds(ids: readonly string[]): void {
  if (typeof window === "undefined") {
    return;
  }

  try {
    window.localStorage.setItem(RECENT_COMMANDS_STORAGE_KEY, JSON.stringify(ids));
  } catch {
    // Ignore localStorage failures and keep in-memory state usable.
  }
}

const CATEGORY_ORDER: Record<CommandCategory, number> = {
  Session: 0,
  Navigation: 1,
  View: 2,
  Fleet: 3,
};

/**
 * What the palette shows and remembers. The commands themselves are a contribution point (`commandPoint`); this
 * sorts them for the palette and holds the palette's own state.
 */
export const useCommandStore = defineStore("commands", () => {
  const paletteOpen = shallowRef(false);
  const recentIds = shallowRef<string[]>(loadRecentCommandIds());

  const commands = computed<Command[]>(() => {
    return [...commandPoint.items.value].sort((left, right) => {
      const categoryDifference = CATEGORY_ORDER[left.category] - CATEGORY_ORDER[right.category];

      if (categoryDifference !== 0) {
        return categoryDifference;
      }

      return left.label.localeCompare(right.label);
    });
  });

  function setPaletteOpen(open: boolean): void {
    paletteOpen.value = open;
  }

  /** What Ctrl K does: opens the palette, or closes it when it's open. */
  function togglePalette(): void {
    paletteOpen.value = !paletteOpen.value;
  }

  function getCommand(id: CommandId): Command | undefined {
    return commandPoint.get(id);
  }

  /** Runs a registered command the way its shortcut does: nothing happens while it's missing or disabled. */
  function runCommand(id: CommandId): void {
    const command = commandPoint.get(id);
    if (command && !command.disabled) command.action();
  }

  function recordUsage(id: string): void {
    const nextRecentIds = [id, ...recentIds.value.filter((recentId) => recentId !== id)].slice(0, 5);
    recentIds.value = nextRecentIds;
    persistRecentCommandIds(nextRecentIds);
  }

  return {
    commands,
    registeredCommands: commands,
    paletteOpen,
    recentIds,
    setPaletteOpen,
    togglePalette,
    getCommand,
    runCommand,
    recordUsage,
  };
});

export const useCommandsStore = useCommandStore;
