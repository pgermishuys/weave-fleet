<script setup lang="ts">
import { computed, inject } from "vue";
import { FileText } from "lucide-vue-next";
import CanvasSplit from "@/components/canvas/CanvasSplit.vue";
import CanvasFileViewer from "@/components/canvas/CanvasFileViewer.vue";
import { provideContentPanelContext } from "@/composables/use-content-panel";
import { readFilePayload } from "@/composables/use-file-browser";
import type { UseDiffsResult } from "@/composables/use-diffs";

const props = defineProps<{
  sessionId: string;
}>();

const sessionIdRef = computed<string | null>(() => props.sessionId || null);
const contentPanel = provideContentPanelContext(sessionIdRef);
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

const selectedFilePath = computed(() => contentPanel.filesContext.value.selectedFilePath);

async function openChange(path: string): Promise<void> {
  contentPanel.selectFile(path);
  contentPanel.setViewMode("diff");

  if (!props.sessionId) return;

  try {
    contentPanel.showFile(await readFilePayload(props.sessionId, path));
  } catch {
    // Deleted files can't be read; the diff still shows what changed.
    contentPanel.showFile({
      $type: "markdown",
      content: `This file can't be read from the session. Switch to the diff to see what changed.`,
      sourceFilePath: path,
      sourceText: "",
      viewMode: "rendered",
    });
  }
}
</script>

<template>
  <div class="changes-canvas">
    <CanvasSplit :show-viewer="selectedFilePath !== null">
      <template #list>
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
            :class="{ 'changes-canvas__change--selected': selectedFilePath === change.file }"
            :title="change.file"
            :data-status="change.status"
            @click="openChange(change.file)"
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
      </template>
      <template #viewer>
        <CanvasFileViewer />
      </template>
    </CanvasSplit>
  </div>
</template>

<style scoped>
.changes-canvas {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
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

.changes-canvas__change--selected {
  background: color-mix(in srgb, var(--text) 9%, transparent);
  color: var(--text);
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
