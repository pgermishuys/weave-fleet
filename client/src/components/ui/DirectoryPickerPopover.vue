<script setup lang="ts">
import { computed } from "vue";
import { ArrowUp, Check, Folder, FolderGit2, LoaderCircle, RefreshCw } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import type { UseDirectoryBrowserResult } from "@/composables/use-directory-browser";

/**
 * mode:
 *   "select"   — clicking an entry selects it immediately (for session dialog directory picking)
 *   "navigate"  — clicking an entry navigates into it; a "Use this directory" button confirms
 */
interface Props {
  browser: UseDirectoryBrowserResult;
  open: boolean;
  mode?: "select" | "navigate";
  selectedPath?: string;
  location?: string;
}

const props = withDefaults(defineProps<Props>(), {
  mode: "select",
  selectedPath: "",
  location: "",
});

const emit = defineEmits<{
  "update:open": [value: boolean];
  select: [path: string];
}>();

const canGoUp = computed(() => props.browser.currentPath.value !== null);

const filteredEntries = computed(() => {
  const query = props.browser.search.value.trim().toLowerCase();
  if (!query) {
    return props.browser.entries.value;
  }

  return props.browser.entries.value.filter((entry) => {
    const searchableText = `${entry.name} ${entry.path}`.toLowerCase();
    return searchableText.includes(query);
  });
});

const displayLocation = computed(() => {
  if (props.location) {
    return props.location;
  }

  return props.browser.currentPath.value ?? "Workspace roots";
});

function handleGoUp(): void {
  if (props.browser.parentPath.value) {
    props.browser.goUp();
  } else {
    props.browser.browse(null);
  }
}

function handleEntryClick(path: string): void {
  if (props.mode === "select") {
    emit("select", path);
    emit("update:open", false);
  } else {
    props.browser.browse(path);
  }
}

function selectCurrentDirectory(): void {
  if (props.browser.currentPath.value) {
    emit("select", props.browser.currentPath.value);
    emit("update:open", false);
  }
}

function handleSearchUpdate(value: string | number): void {
  props.browser.setSearch(String(value));
}
</script>

<template>
  <Popover
    :open="open"
    @update:open="emit('update:open', $event)"
  >
    <PopoverTrigger as-child>
      <slot name="trigger" />
    </PopoverTrigger>

    <PopoverContent
      align="end"
      side="top"
      :collision-padding="8"
      :avoid-collisions="false"
      class="w-[32rem] p-0"
      :style="{ backgroundColor: 'var(--card-bg)', opacity: '1' }"
    >
      <div class="border-b border-border bg-card-bg p-2">
        <div class="flex items-center justify-between gap-2">
          <p class="min-w-0 truncate font-mono text-xs text-text">
            {{ displayLocation }}
          </p>

          <div class="flex items-center gap-1">
            <Button
              type="button"
              variant="ghost"
              size="icon-sm"
              :disabled="browser.isLoading.value || !canGoUp"
              @click="handleGoUp"
            >
              <ArrowUp class="h-4 w-4" />
            </Button>

            <Button
              type="button"
              variant="ghost"
              size="icon-sm"
              :disabled="browser.isLoading.value"
              @click="browser.refresh"
            >
              <RefreshCw :class="['h-4 w-4', browser.isLoading.value ? 'animate-spin' : '']" />
            </Button>
          </div>
        </div>

        <Input
          class="mt-2"
          :model-value="browser.search.value"
          placeholder="Search directories"
          @update:model-value="handleSearchUpdate"
        />
      </div>

      <div class="h-72 overflow-y-auto bg-card-bg p-1">
        <button
          v-for="entry in filteredEntries"
          :key="entry.path"
          type="button"
          class="flex w-full items-center gap-2 rounded-sm px-2 py-1.5 text-left text-sm hover:bg-accent hover:text-accent-foreground"
          @click="handleEntryClick(entry.path)"
        >
          <FolderGit2
            v-if="entry.isGitRepo"
            class="h-3.5 w-3.5 shrink-0"
          />
          <Folder
            v-else
            class="h-3.5 w-3.5 shrink-0"
          />
          <span class="min-w-0 truncate">{{ entry.name }}</span>

          <Check
            v-if="mode === 'select' && selectedPath === entry.path"
            class="ml-auto h-3.5 w-3.5 shrink-0 text-primary"
          />
        </button>

        <div
          v-if="browser.isLoading.value"
          class="flex items-center gap-2 px-2 py-4 text-sm text-muted"
        >
          <LoaderCircle class="h-4 w-4 animate-spin" />
          <span>Loading…</span>
        </div>

        <p
          v-else-if="filteredEntries.length === 0"
          class="px-2 py-4 text-sm text-muted"
        >
          No directories found.
        </p>
      </div>

      <div
        v-if="mode === 'navigate' && browser.currentPath.value"
        class="border-t border-border bg-card-bg p-2"
      >
        <Button
          type="button"
          variant="default"
          size="sm"
          class="w-full"
          @click="selectCurrentDirectory"
        >
          <Check class="h-4 w-4" />
          Use this directory
        </Button>
      </div>
    </PopoverContent>
  </Popover>
</template>
