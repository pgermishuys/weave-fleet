<script setup lang="ts">
import { computed, inject, onBeforeUnmount } from "vue";
import { FileText } from "lucide-vue-next";
import type { UseDiffsResult } from "@/composables/use-diffs";
import { fileCanvasId, useCanvasesStore } from "@/stores/canvases";

const props = defineProps<{
  sessionId: string;
}>();

const canvases = useCanvasesStore();
const sharedDiffs = inject<UseDiffsResult>("sharedDiffs");

const changedFiles = computed(() =>
  [...(sharedDiffs?.diffs.value ?? [])]
    .sort((a, b) => a.file.localeCompare(b.file))
    .map((diff) => {
      const slash = diff.file.lastIndexOf("/");
      return {
        file: diff.file,
        name: slash >= 0 ? diff.file.slice(slash + 1) : diff.file,
        dir: slash >= 0 ? diff.file.slice(0, slash) : "",
        additions: diff.additions,
        deletions: diff.deletions,
        status: diff.status,
      };
    }),
);

// A changed file opens in Diff. One click opens a preview tab, which the next file you click
// replaces; a double-click (or the pin on the tab) keeps it. The preview takes the canvas away
// from this list, so wait one double-click interval before opening it.
const DOUBLE_CLICK_MS = 250;
let pendingClick: ReturnType<typeof setTimeout> | undefined;

function openChange(event: MouseEvent, path: string): void {
  clearTimeout(pendingClick);
  if (event.detail >= 2) {
    canvases.openFile(props.sessionId, path, { keep: true, view: "diff" });
    return;
  }
  // A keyboard press (detail 0) can't become a double-click.
  if (event.detail === 0) {
    canvases.openFile(props.sessionId, path, { view: "diff" });
    return;
  }
  pendingClick = setTimeout(() => canvases.openFile(props.sessionId, path, { view: "diff" }), DOUBLE_CLICK_MS);
}

onBeforeUnmount(() => clearTimeout(pendingClick));

const openIds = computed(() => new Set(canvases.sessionCanvases(props.sessionId).canvases.map((canvas) => canvas.id)));

function isOpen(path: string): boolean {
  return openIds.value.has(fileCanvasId(path));
}
</script>

<template>
  <div class="changes-canvas">
    <p
      v-if="changedFiles.length === 0"
      class="changes-canvas__empty"
    >
      No changes in this session yet.
    </p>
    <div
      v-else
      class="changes-canvas__list"
    >
      <button
        v-for="change in changedFiles"
        :key="change.file"
        type="button"
        class="changes-canvas__change"
        :class="{ 'changes-canvas__change--open': isOpen(change.file) }"
        :title="change.file"
        :data-status="change.status"
        @click="openChange($event, change.file)"
      >
        <FileText
          :size="14"
          class="changes-canvas__change-icon"
          aria-hidden="true"
        />
        <span class="changes-canvas__change-name">{{ change.name }}</span>
        <span class="changes-canvas__change-dir">{{ change.dir }}</span>
        <span class="changes-canvas__change-stats">
          <span
            v-if="change.additions > 0"
            class="changes-canvas__change-adds"
          >+{{ change.additions }}</span>
          <span
            v-if="change.deletions > 0"
            class="changes-canvas__change-dels"
          >−{{ change.deletions }}</span>
        </span>
      </button>
    </div>
  </div>
</template>

<style scoped>
.changes-canvas {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow-y: auto;
}

.changes-canvas__list {
  display: flex;
  flex-direction: column;
  padding: 8px 8px 6px;
}

.changes-canvas__empty {
  margin: 0;
  padding: 14px 18px;
  font-size: 13px;
  color: var(--muted);
}

.changes-canvas__change {
  display: flex;
  align-items: center;
  gap: 8px;
  min-height: 30px;
  padding: 0 8px;
  /* Rows off screen skip layout and paint; a session can change hundreds of files. */
  content-visibility: auto;
  contain-intrinsic-size: auto 30px;
  border: 0;
  border-radius: var(--radius-btn);
  background: transparent;
  color: color-mix(in srgb, var(--text) 86%, transparent);
  font-size: 13px;
  text-align: left;
  cursor: pointer;
  transition: background var(--transition), color var(--transition);
}

.changes-canvas__change:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
  color: var(--text);
}

/* Files already open in a tab read a little stronger. */
.changes-canvas__change--open .changes-canvas__change-name {
  color: var(--text);
  font-weight: 500;
}

.changes-canvas__change[data-status="deleted"] .changes-canvas__change-name {
  text-decoration: line-through;
  text-decoration-color: color-mix(in srgb, var(--muted) 60%, transparent);
}

.changes-canvas__change-icon {
  flex-shrink: 0;
  color: color-mix(in srgb, var(--muted) 75%, transparent);
}

.changes-canvas__change-name {
  flex-shrink: 0;
  white-space: nowrap;
}

.changes-canvas__change-dir {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 12px;
  color: color-mix(in srgb, var(--muted) 80%, transparent);
}

.changes-canvas__change-stats {
  display: inline-flex;
  flex-shrink: 0;
  gap: 6px;
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  font-variant-numeric: tabular-nums;
}

.changes-canvas__change-adds {
  color: var(--running);
}

.changes-canvas__change-dels {
  color: var(--error);
}

@media (prefers-reduced-motion: reduce) {
  .changes-canvas__change {
    transition: none;
  }
}
</style>
