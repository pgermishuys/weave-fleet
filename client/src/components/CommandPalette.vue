<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { ChevronLeft } from "lucide-vue-next";
import { storeToRefs } from "pinia";
import {
  CommandDialog,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
  CommandShortcut,
} from "@/components/ui/command";
import type { Command, CommandCategory, GlobalShortcut } from "@/lib/command-registry";
import { useCommandStore } from "@/stores/commands";

interface CommandLevel {
  parent: Command;
  items: Command[];
}

const CATEGORY_ORDER: CommandCategory[] = ["Session", "Navigation", "View", "Fleet"];

const commandStore = useCommandStore();
const { commands, paletteOpen } = storeToRefs(commandStore);
const subStack = shallowRef<CommandLevel[]>([]);
const paletteKey = shallowRef(0);

const currentLevel = computed(() => subStack.value.at(-1) ?? null);
const activeCommands = computed(() => currentLevel.value?.items ?? commands.value);
const groupedCommands = computed(() => {
  return CATEGORY_ORDER
    .map((category) => ({
      category,
      items: activeCommands.value.filter((command) => command.category === category),
    }))
    .filter((group) => group.items.length > 0);
});

function isApplePlatform(): boolean {
  return typeof navigator !== "undefined" && /Mac|iPhone|iPad|iPod/.test(navigator.userAgent);
}

function formatGlobalShortcut(shortcut: GlobalShortcut): string {
  let modifier = "";

  if (shortcut.platformModifier) {
    modifier = isApplePlatform() ? "⌘" : "Ctrl+";
  } else if (shortcut.metaKey) {
    modifier = "⌘";
  } else if (shortcut.ctrlKey) {
    modifier = "Ctrl+";
  }

  const shiftModifier = shortcut.shiftKey ? (isApplePlatform() ? "⇧" : "Shift+") : "";

  return `${modifier}${shiftModifier}${shortcut.key.toUpperCase()}`;
}

function handleOpenChange(open: boolean): void {
  commandStore.setPaletteOpen(open);

  if (!open) {
    subStack.value = [];
    paletteKey.value += 1;
  }
}

function goBack(): void {
  subStack.value = subStack.value.slice(0, -1);
  paletteKey.value += 1;
}

function handleSelect(command: Command): void {
  if (command.disabled) {
    return;
  }

  const subCommands = command.subCommands ?? command.getSubCommands?.();

  if (subCommands && subCommands.length > 0) {
    subStack.value = [...subStack.value, { parent: command, items: subCommands }];
    paletteKey.value += 1;
    return;
  }

  commandStore.recordUsage(command.id);
  command.action();
  handleOpenChange(false);
}

function handleInputKeyDown(event: KeyboardEvent): void {
  const currentValue = event.target instanceof HTMLInputElement ? event.target.value : "";

  if (event.key.toLowerCase() === "k" && (isApplePlatform() ? event.metaKey : event.ctrlKey)) {
    event.preventDefault();
    handleOpenChange(false);
    return;
  }

  if (event.key === "Backspace" && currentValue === "" && subStack.value.length > 0) {
    event.preventDefault();
    goBack();
    return;
  }

  if (
    currentValue === ""
    && subStack.value.length === 0
    && !event.metaKey
    && !event.ctrlKey
    && !event.altKey
    && event.key.length === 1
  ) {
    const paletteCommand = commands.value.find((command) => {
      return command.paletteHotkey?.toLowerCase() === event.key.toLowerCase() && !command.disabled;
    });

    if (!paletteCommand) {
      return;
    }

    event.preventDefault();
    commandStore.recordUsage(paletteCommand.id);
    paletteCommand.action();
    handleOpenChange(false);
  }
}
</script>

<template>
  <CommandDialog
    :key="paletteKey"
    :open="paletteOpen"
    title="Command Palette"
    description="Search for a command to run."
    :show-close-button="false"
    @update:open="handleOpenChange"
  >
    <CommandInput
      :placeholder="currentLevel ? `${currentLevel.parent.label}...` : 'Type a command or search...'"
      @keydown="handleInputKeyDown"
    />

    <CommandList class="max-h-[50vh] sm:max-h-[300px]">
      <CommandEmpty>No commands found.</CommandEmpty>

      <CommandGroup v-if="subStack.length > 0">
        <CommandItem
          value="__back__"
          @select="goBack"
        >
          <ChevronLeft class="h-4 w-4" />
          <span>Back</span>
        </CommandItem>
      </CommandGroup>

      <CommandGroup
        v-for="group in groupedCommands"
        :key="group.category"
        :heading="group.category"
      >
        <CommandItem
          v-for="command in group.items"
          :key="command.id"
          :value="[command.label, ...(command.keywords ?? [])].join(' ')"
          :disabled="command.disabled"
          :data-disabled="command.disabled ? 'true' : undefined"
          @select="handleSelect(command)"
        >
          <component
            :is="command.icon"
            v-if="command.icon"
            class="h-4 w-4"
          />
          <span class="flex-1">{{ command.label }}</span>

          <span
            v-if="command.description"
            class="mr-2 text-xs text-muted-foreground"
          >
            {{ command.description }}
          </span>

          <span
            v-if="command.subCommands?.length || command.getSubCommands"
            class="text-xs text-muted-foreground"
          >
            ›
          </span>

          <CommandShortcut v-else-if="command.globalShortcut || command.paletteHotkey">
            <kbd class="pointer-events-none inline-flex h-5 select-none items-center gap-1 rounded border bg-muted px-1.5 font-mono text-[10px] font-medium text-muted-foreground opacity-100">
              {{ command.globalShortcut ? formatGlobalShortcut(command.globalShortcut) : command.paletteHotkey }}
            </kbd>
          </CommandShortcut>
        </CommandItem>
      </CommandGroup>
    </CommandList>

    <div class="hidden items-center justify-between border-t px-3 py-2 text-xs text-muted-foreground sm:flex">
      <div class="flex items-center gap-3">
        <span><kbd class="font-mono">↑↓</kbd> Navigate</span>
        <span><kbd class="font-mono">↵</kbd> Select</span>
        <span v-if="subStack.length > 0"><kbd class="font-mono">⌫</kbd> Back</span>
        <span><kbd class="font-mono">Esc</kbd> Close</span>
      </div>

      <span class="opacity-50"><kbd class="font-mono">⌘K</kbd> toggle</span>
    </div>
  </CommandDialog>
</template>
