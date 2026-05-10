<script setup lang="ts">
import { computed } from "vue";
import { Check, ChevronRight, Folder, FolderGit2, GitBranch, LoaderCircle, RefreshCw } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import type { UseDirectoryBrowserResult } from "@/composables/use-directory-browser";
import { cn } from "@/lib/utils";

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
  align?: "start" | "center" | "end";
  contentClass?: string;
}

const props = withDefaults(defineProps<Props>(), {
  mode: "select",
  selectedPath: "",
  location: "",
  align: "end",
  contentClass: "w-[32rem]",
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

interface BreadcrumbSegment {
  label: string;
  path: string;
}

const breadcrumbs = computed<BreadcrumbSegment[]>(() => {
  const current = props.browser.currentPath.value;
  if (current === null) return [];

  const sep = current.includes("\\") ? "\\" : "/";
  const roots = props.browser.roots.value;
  const root = roots.find(
    (r) => current === r || current.startsWith(r + sep),
  );

  if (!root) return [{ label: current, path: current }];

  const rootName = root.split(sep).filter(Boolean).pop() ?? root;
  const crumbs: BreadcrumbSegment[] = [{ label: rootName, path: root }];

  if (current !== root) {
    const relative = current.slice(root.length + 1);
    const segments = relative.split(sep);
    let accumulated = root;
    for (const segment of segments) {
      accumulated = `${accumulated}${sep}${segment}`;
      crumbs.push({ label: segment, path: accumulated });
    }
  }

  return crumbs;
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

function handleEntrySelect(path: string): void {
  emit("select", path);
  emit("update:open", false);
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
      :align="align"
      side="bottom"
      :collision-padding="8"
      :avoid-collisions="true"
      :class="[contentClass, 'p-0 border-border shadow-xl shadow-black/50 ring-1 ring-white/[0.08]']"
      :style="{ backgroundColor: 'color-mix(in srgb, var(--card-bg) 100%, white 4%)' }"
    >
      <!-- Breadcrumb navigation -->
      <div
        v-if="browser.currentPath.value !== null"
        class="flex items-center gap-1 overflow-x-auto border-b border-border px-3 py-2 text-xs text-muted-foreground"
      >
        <button
          type="button"
          class="shrink-0 transition-colors hover:text-foreground"
          @click="browser.browse(null)"
        >
          Roots
        </button>
        <template
          v-for="(crumb, i) in breadcrumbs"
          :key="crumb.path"
        >
          <ChevronRight class="h-3 w-3 shrink-0" />
          <button
            type="button"
            :class="cn(
              'max-w-[80px] truncate transition-colors hover:text-foreground',
              i === breadcrumbs.length - 1 && 'font-medium text-foreground',
            )"
            :title="crumb.path"
            @click="browser.browse(crumb.path)"
          >
            {{ crumb.label }}
          </button>
        </template>
      </div>

      <!-- Roots header (when at root level) -->
      <div
        v-else
        class="flex items-center justify-between gap-2 border-b border-border p-2"
      >
        <p class="min-w-0 truncate font-mono text-xs text-text">
          Workspace roots
        </p>

        <div class="flex items-center gap-1">
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

      <div class="border-b border-border px-2 py-1.5">
        <Input
          :model-value="browser.search.value"
          placeholder="Search directories..."
          @update:model-value="handleSearchUpdate"
        />
      </div>

      <div class="h-72 overflow-y-auto p-1">
        <button
          v-for="entry in filteredEntries"
          :key="entry.path"
          type="button"
          class="group flex w-full items-center gap-2 rounded-sm px-2 py-1.5 text-left text-sm font-mono hover:bg-white/[0.06]"
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
          <span class="min-w-0 flex-1 truncate">{{ entry.name }}</span>

          <GitBranch
            v-if="entry.isGitRepo"
            class="h-3 w-3 shrink-0 text-muted-foreground"
          />

          <Check
            v-if="mode === 'select' && selectedPath === entry.path"
            class="ml-auto h-3.5 w-3.5 shrink-0 text-primary"
          />

          <button
            v-if="mode === 'navigate'"
            type="button"
            class="shrink-0 rounded px-1.5 py-0.5 text-[10px] text-muted-foreground opacity-0 transition-opacity hover:text-foreground group-hover:opacity-100"
            @click.stop="handleEntrySelect(entry.path)"
          >
            Select
          </button>
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
        class="border-t border-border p-2"
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
