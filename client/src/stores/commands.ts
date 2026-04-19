import { defineStore } from "pinia";
import { computed, shallowRef } from "vue";
import type { Command, CommandCategory } from "@/lib/command-registry";

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

export const useCommandStore = defineStore("commands", () => {
  const commandMap = shallowRef<Map<string, Command>>(new Map());
  const paletteOpen = shallowRef(false);
  const recentIds = shallowRef<string[]>(loadRecentCommandIds());

  const commands = computed<Command[]>(() => {
    return [...commandMap.value.values()].sort((left, right) => {
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

  function registerCommand(command: Command): void {
    const nextCommandMap = new Map(commandMap.value);
    nextCommandMap.set(command.id, command);
    commandMap.value = nextCommandMap;
  }

  function unregisterCommand(id: string): void {
    if (!commandMap.value.has(id)) {
      return;
    }

    const nextCommandMap = new Map(commandMap.value);
    nextCommandMap.delete(id);
    commandMap.value = nextCommandMap;
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
    registerCommand,
    unregisterCommand,
    recordUsage,
  };
});

export const useCommandsStore = useCommandStore;
