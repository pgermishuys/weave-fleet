<script setup lang="ts">
import { computed, inject, ref, watch } from "vue";
import { ChevronDown, ChevronUp, Maximize2, Minimize2 } from "lucide-vue-next";
import DiffView from "@/components/session/DiffView.vue";
import FilesChangedFileList from "@/components/session/FilesChangedFileList.vue";
import type { FilesChangedFileListItem } from "@/components/session/FilesChangedFileList.vue";
import { useContentPanelContext } from "@/composables/use-content-panel";
import type { UseDiffsResult } from "@/composables/use-diffs";
import { parseDiffLines } from "@/lib/diff-parser";
import type { DiffLine } from "@/lib/diff-parser";

interface Props {
  sessionId: string | null;
}

const props = defineProps<Props>();

const contentPanelContext = useContentPanelContext();
const sharedDiffs = inject<UseDiffsResult>('sharedDiffs')!;
const { diffs, available, isLoading, error } = sharedDiffs;

const selectedFile = ref<FilesChangedFileListItem | null>(null);

// Compute summary stats
const fileCount = computed(() => diffs.value.length);
const totalAdditions = computed(() =>
  diffs.value.reduce((sum, diff) => sum + (diff.additions ?? 0), 0),
);
const totalDeletions = computed(() =>
  diffs.value.reduce((sum, diff) => sum + (diff.deletions ?? 0), 0),
);

// Convert FileDiffItem[] to FilesChangedFileListItem[]
const fileListItems = computed<FilesChangedFileListItem[]>(() =>
  diffs.value.map((diff) => ({
    file: diff.file,
    status: diff.status,
    additions: diff.additions,
    deletions: diff.deletions,
    before: diff.before,
    after: diff.after,
    isBinary: diff.isBinary ?? diff.binary ?? false,
    isTruncated: diff.isTruncated ?? diff.truncated ?? false,
  })),
);

// Parse diff lines for the selected file
const selectedDiffLines = computed<DiffLine[]>(() => {
  if (!selectedFile.value) {
    return [];
  }

  const matchingDiff = diffs.value.find((d) => d.file === selectedFile.value?.file);
  if (!matchingDiff) {
    return [];
  }

  const before = matchingDiff.before ?? "";
  const after = matchingDiff.after ?? "";

  return parseDiffLines(before, after);
});

// Determine if the selected file is binary or truncated
const selectedFileMeta = computed(() => {
  if (!selectedFile.value) {
    return { isBinary: false, isTruncated: false, isAdded: false, isDeleted: false };
  }

  const matchingDiff = diffs.value.find((d) => d.file === selectedFile.value?.file);
  if (!matchingDiff) {
    return { isBinary: false, isTruncated: false, isAdded: false, isDeleted: false };
  }

  return {
    isBinary: matchingDiff.isBinary ?? matchingDiff.binary ?? false,
    isTruncated: matchingDiff.isTruncated ?? matchingDiff.truncated ?? false,
    isAdded: matchingDiff.status === "added",
    isDeleted: matchingDiff.status === "deleted",
  };
});

// Drawer mode
const drawerMode = computed(() => contentPanelContext.drawerMode.value);

// Collapsed height
const COLLAPSED_HEIGHT = 40;
const EXPANDED_HEIGHT = 280;

const drawerHeight = computed(() => {
  if (drawerMode.value === "collapsed") {
    return COLLAPSED_HEIGHT;
  }
  if (drawerMode.value === "maximized") {
    return "100%";
  }
  return EXPANDED_HEIGHT;
});

// Toggle drawer
function toggleDrawer(): void {
  if (drawerMode.value === "collapsed") {
    contentPanelContext.setDrawerMode("expanded");
  } else if (drawerMode.value === "expanded") {
    contentPanelContext.setDrawerMode("collapsed");
  } else {
    // maximized -> collapsed
    contentPanelContext.setDrawerMode("collapsed");
  }
}

function toggleMaximize(): void {
  if (drawerMode.value === "maximized") {
    contentPanelContext.setDrawerMode("expanded");
  } else {
    contentPanelContext.setDrawerMode("maximized");
  }
}

function handleFileSelect(file: FilesChangedFileListItem): void {
  selectedFile.value = file;
}

function handleHandleKeydown(event: KeyboardEvent): void {
  if (event.key === "Enter" || event.key === " ") {
    event.preventDefault();
    toggleDrawer();
  } else if (event.key === "Escape") {
    if (drawerMode.value !== "collapsed") {
      event.preventDefault();
      contentPanelContext.setDrawerMode("collapsed");
    }
  }
}

// Auto-select first file when diffs load
watch(
  diffs,
  (newDiffs) => {
    if (newDiffs.length > 0 && !selectedFile.value) {
      const firstDiff = newDiffs[0];
      if (firstDiff) {
        selectedFile.value = {
          file: firstDiff.file,
          status: firstDiff.status,
          additions: firstDiff.additions,
          deletions: firstDiff.deletions,
          before: firstDiff.before,
          after: firstDiff.after,
          isBinary: firstDiff.isBinary ?? firstDiff.binary ?? false,
          isTruncated: firstDiff.isTruncated ?? firstDiff.truncated ?? false,
        };
      }
    } else if (newDiffs.length === 0) {
      selectedFile.value = null;
    }
  },
  { immediate: true },
);

// Reset selected file when session changes
watch(
  () => props.sessionId,
  () => {
    selectedFile.value = null;
  },
);
</script>

<template>
  <div
    class="changes-drawer"
    :class="{
      'changes-drawer--collapsed': drawerMode === 'collapsed',
      'changes-drawer--expanded': drawerMode === 'expanded',
      'changes-drawer--maximized': drawerMode === 'maximized',
    }"
    :style="{ height: drawerHeight + (typeof drawerHeight === 'number' ? 'px' : '') }"
    role="region"
    aria-label="Changes drawer"
  >
    <!-- Handle -->
    <div
      class="changes-drawer__handle"
      role="button"
      tabindex="0"
      :aria-expanded="drawerMode !== 'collapsed'"
      :aria-controls="drawerMode !== 'collapsed' ? 'changes-drawer-content' : undefined"
      :aria-label="drawerMode === 'collapsed' ? 'Expand changes drawer' : 'Collapse changes drawer'"
      @click="toggleDrawer"
      @keydown="handleHandleKeydown"
    >
      <div class="changes-drawer__handle-content">
        <ChevronUp
          v-if="drawerMode !== 'collapsed'"
          class="changes-drawer__handle-icon"
          aria-hidden="true"
        />
        <ChevronDown
          v-else
          class="changes-drawer__handle-icon"
          aria-hidden="true"
        />
        <span class="changes-drawer__summary">
          <template v-if="isLoading">
            Loading changes...
          </template>
          <template v-else-if="error">
            Error loading changes
          </template>
          <template v-else-if="!available">
            Changes unavailable
          </template>
          <template v-else-if="fileCount === 0">
            No changes
          </template>
          <template v-else>
            {{ fileCount }} {{ fileCount === 1 ? 'file' : 'files' }}
            <span class="changes-drawer__summary-stats">
              <span class="changes-drawer__summary-stat changes-drawer__summary-stat--add">+{{ totalAdditions }}</span>
              <span class="changes-drawer__summary-stat changes-drawer__summary-stat--remove">-{{ totalDeletions }}</span>
            </span>
          </template>
        </span>
      </div>

      <button
        v-if="drawerMode !== 'collapsed'"
        type="button"
        class="changes-drawer__maximize-btn"
        :aria-label="drawerMode === 'maximized' ? 'Restore drawer' : 'Maximize drawer'"
        @click.stop="toggleMaximize"
      >
        <Minimize2
          v-if="drawerMode === 'maximized'"
          class="changes-drawer__maximize-icon"
          aria-hidden="true"
        />
        <Maximize2
          v-else
          class="changes-drawer__maximize-icon"
          aria-hidden="true"
        />
      </button>
    </div>

    <!-- Content (only visible when expanded or maximized) -->
    <div
      v-if="drawerMode !== 'collapsed'"
      id="changes-drawer-content"
      class="changes-drawer__content"
    >
      <div class="changes-drawer__layout">
        <!-- File list sidebar -->
        <aside class="changes-drawer__sidebar">
          <FilesChangedFileList
            :files="fileListItems"
            :selected-file="selectedFile"
            :is-loading="isLoading"
            :error="error"
            :unavailable="!available"
            variant="full"
            aria-label="Changed files in drawer"
            @select="handleFileSelect"
          />
        </aside>

        <!-- Diff viewer -->
        <div class="changes-drawer__diff">
          <div
            v-if="!selectedFile"
            class="changes-drawer__placeholder"
          >
            <p class="changes-drawer__placeholder-text">
              Select a file to view its diff
            </p>
          </div>

          <div
            v-else-if="selectedFileMeta.isBinary"
            class="changes-drawer__placeholder"
          >
            <p class="changes-drawer__placeholder-text">
              Binary file: {{ selectedFile.file }}
            </p>
          </div>

          <div
            v-else-if="selectedFileMeta.isTruncated"
            class="changes-drawer__placeholder"
          >
            <p class="changes-drawer__placeholder-text">
              Diff truncated: {{ selectedFile.file }}
            </p>
            <p class="changes-drawer__placeholder-subtext">
              File is too large to display
            </p>
          </div>

          <div
            v-else-if="selectedFileMeta.isAdded && selectedDiffLines.length === 0"
            class="changes-drawer__placeholder"
          >
            <p class="changes-drawer__placeholder-text">
              New file: {{ selectedFile.file }}
            </p>
            <p class="changes-drawer__placeholder-subtext">
              No previous version to compare
            </p>
          </div>

          <div
            v-else-if="selectedFileMeta.isDeleted && selectedDiffLines.length === 0"
            class="changes-drawer__placeholder"
          >
            <p class="changes-drawer__placeholder-text">
              Deleted file: {{ selectedFile.file }}
            </p>
            <p class="changes-drawer__placeholder-subtext">
              File was removed
            </p>
          </div>

          <DiffView
            v-else
            :lines="selectedDiffLines"
          />
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.changes-drawer {
  position: relative;
  display: flex;
  flex-direction: column;
  min-height: 40px;
  border-top: 1px solid var(--border);
  background: var(--panel-bg);
  transition: height 0.2s ease-in-out;
  overflow: hidden;
}

@media (prefers-reduced-motion: reduce) {
  .changes-drawer {
    transition: none;
  }
}

.changes-drawer--collapsed {
  height: 40px;
}

.changes-drawer--expanded {
  height: 280px;
}

.changes-drawer--maximized {
  height: 100%;
}

.changes-drawer__handle {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  height: 40px;
  padding: 0 12px;
  border-bottom: 1px solid var(--border);
  background: rgba(255, 255, 255, 0.02);
  cursor: pointer;
  user-select: none;
  transition: background var(--transition);
}

@media (prefers-reduced-motion: reduce) {
  .changes-drawer__handle {
    transition: none;
  }
}

.changes-drawer__handle:hover {
  background: rgba(255, 255, 255, 0.04);
}

.changes-drawer__handle:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.changes-drawer__handle-content {
  display: flex;
  align-items: center;
  gap: 8px;
  flex: 1;
  min-width: 0;
}

.changes-drawer__handle-icon {
  width: 16px;
  height: 16px;
  color: var(--muted);
  flex-shrink: 0;
}

.changes-drawer__summary {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12px;
  font-weight: 500;
  color: var(--text);
}

.changes-drawer__summary-stats {
  display: flex;
  align-items: center;
  gap: 6px;
}

.changes-drawer__summary-stat {
  font-size: 11px;
  font-weight: 600;
  font-family: ui-monospace, SFMono-Regular, SFMono-Regular, Consolas, "Liberation Mono", Menlo, monospace;
}

.changes-drawer__summary-stat--add {
  color: var(--running);
}

.changes-drawer__summary-stat--remove {
  color: var(--error);
}

.changes-drawer__maximize-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  padding: 0;
  border: 1px solid var(--border);
  border-radius: 0;
  background: var(--surface, #fff);
  color: var(--muted);
  cursor: pointer;
  transition: background var(--transition), color var(--transition);
}

@media (prefers-reduced-motion: reduce) {
  .changes-drawer__maximize-btn {
    transition: none;
  }
}

.changes-drawer__maximize-btn:hover {
  background: var(--bg, rgba(0, 0, 0, 0.04));
  color: var(--text);
}

.changes-drawer__maximize-btn:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.changes-drawer__maximize-icon {
  width: 14px;
  height: 14px;
}

.changes-drawer__content {
  flex: 1;
  min-height: 0;
  overflow: hidden;
  display: flex;
  flex-direction: column;
}

.changes-drawer__layout {
  display: grid;
  grid-template-columns: 280px minmax(0, 1fr);
  height: 100%;
  min-height: 0;
  overflow: hidden;
}

.changes-drawer__sidebar {
  min-height: 0;
  border-right: 1px solid var(--border);
  background: rgba(0, 0, 0, 0.1);
  overflow-y: auto;
}

.changes-drawer__diff {
  min-height: 0;
  overflow: auto;
  display: flex;
  flex-direction: column;
}

.changes-drawer__placeholder {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  height: 100%;
  padding: 24px;
  color: var(--muted);
  text-align: center;
}

.changes-drawer__placeholder-text {
  margin: 0;
  font-size: 13px;
  font-weight: 500;
  color: var(--text);
}

.changes-drawer__placeholder-subtext {
  margin: 0;
  font-size: 11px;
  color: var(--muted);
}
</style>
