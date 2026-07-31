<script setup lang="ts">
import { computed } from "vue";
import { storeToRefs } from "pinia";
import { useSessionsStore } from "@/stores/sessions";
import { useSessionDiffsContext } from "@/composables/use-session-diffs-context";
import { useArtifactViewer } from "@/composables/use-artifact-viewer";
import type { FileDiffItem } from "@/lib/api-types";

interface GroupedFile {
  path: string;
  additions?: number;
  deletions?: number;
  lineCount?: number;
  status: "new" | "modified" | "source";
}

const sessionsStore = useSessionsStore();
const { activeSessionId } = storeToRefs(sessionsStore);
const sessionDiffsContext = useSessionDiffsContext();
const { openFile } = useArtifactViewer();

const diffs = computed<readonly FileDiffItem[]>(() => {
  const context = sessionDiffsContext.value;
  return context?.diffState.diffs.value ?? [];
});

const groupedFiles = computed<{
  new: GroupedFile[];
  modified: GroupedFile[];
  source: GroupedFile[];
}>(() => {
  const newFiles: GroupedFile[] = [];
  const modifiedFiles: GroupedFile[] = [];
  const sourceFiles: GroupedFile[] = [];

  // Group diffs by status
  for (const diff of diffs.value) {
    if (diff.status === "added") {
      newFiles.push({
        path: diff.file,
        lineCount: diff.additions,
        status: "new",
      });
    } else if (diff.status === "modified") {
      modifiedFiles.push({
        path: diff.file,
        additions: diff.additions,
        deletions: diff.deletions,
        status: "modified",
      });
    } else if (diff.status === "deleted") {
      // Treat deleted files as source files for now
      sourceFiles.push({
        path: diff.file,
        lineCount: diff.deletions,
        status: "source",
      });
    }
  }

  // TODO: Add actual source files (files that were read but not modified)
  // This would require additional API data

  return {
    new: newFiles,
    modified: modifiedFiles,
    source: sourceFiles,
  };
});

const summary = computed(() => {
  const newCount = groupedFiles.value.new.length;
  const modifiedCount = groupedFiles.value.modified.length;
  const sourceCount = groupedFiles.value.source.length;

  const parts: string[] = [];
  if (newCount > 0) parts.push(`${newCount} new`);
  if (modifiedCount > 0) parts.push(`${modifiedCount} edit`);
  if (sourceCount > 0) parts.push(`${sourceCount} source`);

  return parts.join(" · ") || "No files";
});

function handleFileClick(file: GroupedFile): void {
  openFile(file.path);
}

function getFileName(path: string): string {
  const parts = path.split(/[/\\]/);
  return parts[parts.length - 1] ?? path;
}

function getFileDirectory(path: string): string {
  const parts = path.split(/[/\\]/);
  if (parts.length <= 1) return "";
  return parts.slice(0, -1).join("/");
}
</script>

<template>
  <div class="artifacts-panel">
    <div class="artifacts-summary">
      {{ summary }}
    </div>

    <div class="artifacts-groups">
      <!-- NEW FILES -->
      <div
        v-if="groupedFiles.new.length > 0"
        class="artifacts-group"
      >
        <div class="artifacts-group__header">
          <span class="artifacts-group__dot artifacts-group__dot--new" />
          <span class="artifacts-group__label">NEW ({{ groupedFiles.new.length }})</span>
        </div>
        <div class="artifacts-group__files">
          <button
            v-for="file in groupedFiles.new"
            :key="file.path"
            type="button"
            class="artifacts-file"
            @click="handleFileClick(file)"
          >
            <div class="artifacts-file__name">
              <span class="artifacts-file__filename">{{ getFileName(file.path) }}</span>
              <span
                v-if="getFileDirectory(file.path)"
                class="artifacts-file__dir"
              >{{ getFileDirectory(file.path) }}</span>
            </div>
            <div class="artifacts-file__stat">
              {{ file.lineCount ?? 0 }} lines
            </div>
          </button>
        </div>
      </div>

      <!-- MODIFIED FILES -->
      <div
        v-if="groupedFiles.modified.length > 0"
        class="artifacts-group"
      >
        <div class="artifacts-group__header">
          <span class="artifacts-group__dot artifacts-group__dot--modified" />
          <span class="artifacts-group__label">MODIFIED ({{ groupedFiles.modified.length }})</span>
        </div>
        <div class="artifacts-group__files">
          <button
            v-for="file in groupedFiles.modified"
            :key="file.path"
            type="button"
            class="artifacts-file"
            @click="handleFileClick(file)"
          >
            <div class="artifacts-file__name">
              <span class="artifacts-file__filename">{{ getFileName(file.path) }}</span>
              <span
                v-if="getFileDirectory(file.path)"
                class="artifacts-file__dir"
              >{{ getFileDirectory(file.path) }}</span>
            </div>
            <div class="artifacts-file__stat">
              <span class="artifacts-file__stat--add">+{{ file.additions ?? 0 }}</span>
              <span class="artifacts-file__stat--del">-{{ file.deletions ?? 0 }}</span>
            </div>
          </button>
        </div>
      </div>

      <!-- SOURCE FILES -->
      <div
        v-if="groupedFiles.source.length > 0"
        class="artifacts-group"
      >
        <div class="artifacts-group__header">
          <span class="artifacts-group__dot artifacts-group__dot--source" />
          <span class="artifacts-group__label">SOURCE ({{ groupedFiles.source.length }})</span>
        </div>
        <div class="artifacts-group__files">
          <button
            v-for="file in groupedFiles.source"
            :key="file.path"
            type="button"
            class="artifacts-file"
            @click="handleFileClick(file)"
          >
            <div class="artifacts-file__name">
              <span class="artifacts-file__filename">{{ getFileName(file.path) }}</span>
              <span
                v-if="getFileDirectory(file.path)"
                class="artifacts-file__dir"
              >{{ getFileDirectory(file.path) }}</span>
            </div>
            <div class="artifacts-file__stat">
              read
            </div>
          </button>
        </div>
      </div>
    </div>

    <div
      v-if="groupedFiles.new.length === 0 && groupedFiles.modified.length === 0 && groupedFiles.source.length === 0"
      class="artifacts-empty"
    >
      <p class="artifacts-empty__text">
        No artifacts yet
      </p>
      <p class="artifacts-empty__hint">
        Files created, modified, or read during this session will appear here.
      </p>
    </div>
  </div>
</template>

<style scoped>
.artifacts-panel {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 0;
}

.artifacts-summary {
  padding: 8px 0;
  font-size: 11px;
  font-weight: 600;
  color: var(--muted);
  text-align: center;
  border-bottom: 1px solid var(--border);
}

.artifacts-groups {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.artifacts-group {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.artifacts-group__header {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 0 4px;
}

.artifacts-group__dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  flex-shrink: 0;
}

.artifacts-group__dot--new {
  background: #10b981;
}

.artifacts-group__dot--modified {
  background: #3b82f6;
}

.artifacts-group__dot--source {
  background: #6b7280;
}

.artifacts-group__label {
  font-size: 9px;
  font-weight: 700;
  letter-spacing: 0.05em;
  text-transform: uppercase;
  color: var(--muted);
}

.artifacts-group__files {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.artifacts-file {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 6px 8px;
  border: 0;
  border-radius: 0;
  background: transparent;
  text-align: left;
  cursor: pointer;
  transition: background var(--transition);
}

.artifacts-file:hover {
  background: var(--bg);
}

.artifacts-file:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.artifacts-file__name {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
  flex: 1;
}

.artifacts-file__filename {
  font-size: 12px;
  font-weight: 500;
  color: var(--text);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.artifacts-file__dir {
  font-size: 10px;
  color: var(--muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.artifacts-file__stat {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 10px;
  font-weight: 500;
  color: var(--muted);
  flex-shrink: 0;
}

.artifacts-file__stat--add {
  color: #10b981;
}

.artifacts-file__stat--del {
  color: #ef4444;
}

.artifacts-empty {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 24px 16px;
  text-align: center;
}

.artifacts-empty__text {
  margin: 0;
  font-size: 13px;
  font-weight: 600;
  color: var(--text);
}

.artifacts-empty__hint {
  margin: 0;
  font-size: 11px;
  line-height: 1.4;
  color: var(--muted);
}
</style>
