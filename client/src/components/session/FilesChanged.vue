<script setup lang="ts">
import { computed } from "vue";

interface ChangedFile {
  path?: string;
  file?: string;
  additions?: number | null;
  deletions?: number | null;
}

interface FilesChangedTogglePayload {
  open: boolean;
  fileCount: number;
  additions: number;
  deletions: number;
}

const props = withDefaults(defineProps<{
  files?: readonly ChangedFile[] | null;
  expanded?: boolean;
  isLoading?: boolean;
  error?: string | null;
  unavailable?: boolean;
  disabled?: boolean;
}>(), {
  files: () => [],
  expanded: false,
  isLoading: false,
  error: null,
  unavailable: false,
  disabled: false,
});

const emit = defineEmits<{
  click: [payload: FilesChangedTogglePayload];
  open: [payload: FilesChangedTogglePayload];
}>();

const safeFiles = computed(() => props.files ?? []);
const fileCount = computed(() => safeFiles.value.length);
const totalAdditions = computed(() => safeFiles.value.reduce((sum, file) => sum + normalizeLineCount(file.additions), 0));
const totalDeletions = computed(() => safeFiles.value.reduce((sum, file) => sum + normalizeLineCount(file.deletions), 0));
const hasError = computed(() => Boolean(props.error));
const fileCountLabel = computed(() => {
  if (props.isLoading) {
    return "Loading changes";
  }

  if (fileCount.value === 0) {
    return "No changes";
  }

  return `${fileCount.value.toLocaleString()} ${fileCount.value === 1 ? "file" : "files"} changed`;
});
const badgeAriaLabel = computed(() => {
  if (props.isLoading) {
    return "Loading changed files";
  }

  const summary = `${fileCountLabel.value}, ${totalAdditions.value.toLocaleString()} additions, ${totalDeletions.value.toLocaleString()} deletions`;
  return props.error ? `${summary}. ${props.error}` : summary;
});
const badgeTitle = computed(() => props.error ?? badgeAriaLabel.value);

function normalizeLineCount(value: number | null | undefined): number {
  return typeof value === "number" && Number.isFinite(value) ? value : 0;
}

function emitBadgeClick(): void {
  if (props.isLoading || props.disabled) {
    return;
  }

  const payload: FilesChangedTogglePayload = {
    open: !props.expanded,
    fileCount: fileCount.value,
    additions: totalAdditions.value,
    deletions: totalDeletions.value,
  };

  emit("click", payload);
  emit("open", payload);
}
</script>

<template>
  <section
    v-if="!unavailable"
    class="files-changed"
    aria-label="Files changed"
  >
    <button
      class="files-changed__badge"
      :class="{
        'files-changed__badge--loading': isLoading,
        'files-changed__badge--error': hasError,
        'files-changed__badge--empty': !isLoading && fileCount === 0,
      }"
      type="button"
      :aria-busy="isLoading"
      :aria-expanded="expanded"
      :aria-label="badgeAriaLabel"
      :disabled="isLoading || disabled"
      :title="badgeTitle"
      @click="emitBadgeClick"
    >
      <span
        v-if="isLoading"
        class="files-changed__spinner"
        aria-hidden="true"
      />
      <span class="files-changed__count">{{ fileCountLabel }}</span>
      <span
        v-if="hasError"
        class="files-changed__error-indicator"
        aria-hidden="true"
      >!</span>
      <template v-if="!isLoading && fileCount > 0">
        <span
          class="files-changed__stat files-changed__stat--add"
          :aria-label="`${totalAdditions.toLocaleString()} additions`"
        >+{{ totalAdditions.toLocaleString() }}</span>
        <span
          class="files-changed__stat files-changed__stat--remove"
          :aria-label="`${totalDeletions.toLocaleString()} deletions`"
        >-{{ totalDeletions.toLocaleString() }}</span>
      </template>
    </button>
  </section>
</template>

<style scoped>
.files-changed {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 8px;
}

.files-changed__badge {
  display: inline-flex;
  align-items: center;
  max-width: 100%;
  min-height: 28px;
  min-width: 112px;
  gap: 8px;
  padding: 4px 10px;
  border: 1px solid color-mix(in srgb, var(--border) 85%, transparent);
  border-radius: 999px;
  background: color-mix(in srgb, var(--panel) 92%, var(--text) 8%);
  color: var(--text);
  font: inherit;
  line-height: 1;
  white-space: nowrap;
  cursor: pointer;
}

.files-changed__badge:hover:not(:disabled) {
  border-color: color-mix(in srgb, var(--accent) 45%, var(--border));
  background: color-mix(in srgb, var(--panel) 86%, var(--text) 14%);
}

.files-changed__badge:disabled {
  cursor: wait;
}

.files-changed__badge--empty {
  color: var(--muted);
}

.files-changed__badge--error {
  border-color: color-mix(in srgb, var(--error) 38%, var(--border));
}

.files-changed__badge:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.files-changed__count {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 11px;
  font-weight: 600;
}

.files-changed__spinner {
  width: 12px;
  height: 12px;
  flex: 0 0 auto;
  border: 2px solid color-mix(in srgb, var(--muted) 45%, transparent);
  border-top-color: var(--accent);
  border-radius: 999px;
  animation: files-changed-spin 0.8s linear infinite;
}

.files-changed__error-indicator {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 14px;
  height: 14px;
  flex: 0 0 auto;
  border-radius: 999px;
  background: color-mix(in srgb, var(--error) 20%, transparent);
  color: var(--error);
  font-size: 10px;
  font-weight: 700;
  line-height: 1;
}

.files-changed__stat {
  min-width: 28px;
  font-size: 10px;
  font-weight: 600;
  line-height: 1.2;
  text-align: right;
}

.files-changed__stat--add {
  color: var(--running);
}

.files-changed__stat--remove {
  color: var(--error);
}

@keyframes files-changed-spin {
  to {
    transform: rotate(360deg);
  }
}
</style>
