<script setup lang="ts">
import { computed, useTemplateRef } from "vue";
import { FilePenLine, FilePlus2, FileX2 } from "lucide-vue-next";
import type { Component } from "vue";

export type FileDiffStatus = "added" | "deleted" | "modified";

export interface FilesChangedFileListItem {
  file: string;
  status: FileDiffStatus;
  additions?: number | null;
  deletions?: number | null;
  before?: string;
  after?: string;
  isBinary?: boolean;
  isTruncated?: boolean;
  binary?: boolean;
  truncated?: boolean;
}

interface DisplayFileDiffItem extends Omit<FilesChangedFileListItem, "additions" | "deletions"> {
  additions: number;
  deletions: number;
  key: string;
  statusLabel: string;
  statusIcon: Component;
  source: FilesChangedFileListItem;
}

const props = withDefaults(
  defineProps<{
    files?: readonly FilesChangedFileListItem[] | null;
    selectedFile?: FilesChangedFileListItem | null;
    isLoading?: boolean;
    error?: string | null;
    unavailable?: boolean;
    ariaLabel?: string;
    variant?: "compact" | "full";
  }>(),
  {
    files: () => [],
    selectedFile: null,
    isLoading: false,
    error: null,
    unavailable: false,
    ariaLabel: "Changed files",
    variant: "compact",
  },
);

const emit = defineEmits<{
  select: [file: FilesChangedFileListItem];
}>();

const listRef = useTemplateRef<HTMLElement>("fileList");

const statusMeta: Record<FileDiffStatus, { label: string; icon: Component }> = {
  added: {
    label: "Added",
    icon: FilePlus2,
  },
  deleted: {
    label: "Deleted",
    icon: FileX2,
  },
  modified: {
    label: "Modified",
    icon: FilePenLine,
  },
};

const safeFiles = computed(() => props.files ?? []);
const hasError = computed(() => Boolean(props.error));
const isDisabled = computed(() => props.isLoading || props.unavailable || hasError.value);
const classPrefix = computed(() => (props.variant === "full" ? "files-changed-view" : "files-changed-panel"));
const selectedFilePath = computed(() => props.selectedFile?.file ?? null);
const isLargeFileSet = computed(() => safeFiles.value.length > 50);

const displayFiles = computed<DisplayFileDiffItem[]>(() => safeFiles.value.map((file, index) => {
  const meta = statusMeta[file.status] ?? statusMeta.modified;

  return {
    ...file,
    additions: normalizeLineCount(file.additions),
    deletions: normalizeLineCount(file.deletions),
    key: `${file.file}-${file.status}-${index}`,
    statusLabel: meta.label,
    statusIcon: meta.icon,
    source: file,
  };
}));

const selectedIndex = computed(() => {
  if (selectedFilePath.value === null) {
    return -1;
  }

  return displayFiles.value.findIndex((file) => file.file === selectedFilePath.value);
});

function isSelected(file: FilesChangedFileListItem): boolean {
  return selectedFilePath.value === file.file;
}

function getTabIndex(index: number): 0 | -1 {
  if (isDisabled.value) {
    return -1;
  }

  if (selectedIndex.value === -1) {
    return index === 0 ? 0 : -1;
  }

  return selectedIndex.value === index ? 0 : -1;
}

function selectFile(file: FilesChangedFileListItem): void {
  if (isDisabled.value) {
    return;
  }

  emit("select", file);
}

function handleRowKeydown(event: KeyboardEvent, index: number): void {
  if (isDisabled.value) {
    return;
  }

  const targetIndex = getKeyboardTargetIndex(event, index);
  if (targetIndex === null) {
    return;
  }

  event.preventDefault();
  focusAndSelect(targetIndex);
}

function getKeyboardTargetIndex(event: KeyboardEvent, index: number): number | null {
  const lastIndex = displayFiles.value.length - 1;

  switch (event.key) {
    case "ArrowDown":
      return Math.min(index + 1, lastIndex);
    case "ArrowUp":
      return Math.max(index - 1, 0);
    case "Home":
      return 0;
    case "End":
      return lastIndex;
    default:
      return null;
  }
}

function focusAndSelect(index: number): void {
  const file = displayFiles.value[index];
  if (file === undefined) {
    return;
  }

  selectFile(file.source);
  requestAnimationFrame(() => {
    listRef.value
      ?.querySelector<HTMLButtonElement>(`[data-file-index="${index}"]`)
      ?.focus({ preventScroll: true });
  });
}

function normalizeLineCount(value: number | null | undefined): number {
  return typeof value === "number" && Number.isFinite(value) ? value : 0;
}
</script>

<template>
  <section
    class="files-changed-file-list"
    :class="[
      `files-changed-file-list--${variant}`,
      { 'files-changed-file-list--large': isLargeFileSet },
    ]"
    :aria-label="ariaLabel"
  >
    <div
      v-if="isLoading"
      class="files-changed-file-list__loading"
      :class="`${classPrefix}__loading`"
      aria-label="Loading changed files"
      aria-busy="true"
    >
      <span
        class="files-changed-file-list__skeleton files-changed-file-list__skeleton--wide"
        :class="[`${classPrefix}__skeleton`, `${classPrefix}__skeleton--wide`]"
      />
      <span
        class="files-changed-file-list__skeleton"
        :class="`${classPrefix}__skeleton`"
      />
      <span
        class="files-changed-file-list__skeleton files-changed-file-list__skeleton--short"
        :class="[`${classPrefix}__skeleton`, `${classPrefix}__skeleton--short`]"
      />
    </div>

    <p
      v-else-if="unavailable"
      class="files-changed-file-list__notice"
      :class="`${classPrefix}__notice`"
      role="status"
    >
      Changed files are unavailable for this session.
    </p>

    <p
      v-else-if="hasError"
      class="files-changed-file-list__notice files-changed-file-list__notice--error"
      :class="[`${classPrefix}__notice`, `${classPrefix}__notice--error`]"
      role="status"
      :title="error ?? undefined"
    >
      Changed files could not be loaded.
    </p>

    <p
      v-else-if="displayFiles.length === 0"
      class="files-changed-file-list__empty"
      :class="`${classPrefix}__empty`"
    >
      No changes
    </p>

    <ul
      v-else
      ref="fileList"
      class="files-changed-file-list__list"
      :class="`${classPrefix}__list`"
      role="listbox"
      :aria-label="ariaLabel"
      aria-orientation="vertical"
    >
      <li
        v-for="(file, index) in displayFiles"
        :key="file.key"
        v-memo="[file.file, file.status, file.additions, file.deletions, isSelected(file.source)]"
        class="files-changed-file-list__item"
        :class="`${classPrefix}__item`"
        role="presentation"
      >
        <button
          type="button"
          class="files-changed-file-list__row"
          :class="[
            `${classPrefix}__row`,
            {
              'files-changed-file-list__row--selected': isSelected(file.source),
              [`${classPrefix}__row--selected`]: isSelected(file.source),
            },
          ]"
          role="option"
          :aria-selected="isSelected(file.source)"
          :aria-current="isSelected(file.source) ? 'true' : undefined"
          :data-file-index="index"
          :tabindex="getTabIndex(index)"
          @click="selectFile(file.source)"
          @keydown="handleRowKeydown($event, index)"
        >
          <component
            :is="file.statusIcon"
            class="files-changed-file-list__status-icon"
            :class="[
              `files-changed-file-list__status-icon--${file.status}`,
              `${classPrefix}__status-icon`,
              `${classPrefix}__status-icon--${file.status}`,
            ]"
            :aria-label="file.statusLabel"
            :title="file.statusLabel"
          />

          <span
            class="files-changed-file-list__path"
            :class="`${classPrefix}__path`"
            :title="file.file"
          >{{ file.file }}</span>

          <span
            class="files-changed-file-list__stats"
            :class="`${classPrefix}__stats`"
            aria-label="File diff summary"
          >
            <span
              class="files-changed-file-list__stat files-changed-file-list__stat--add"
              :class="[`${classPrefix}__stat`, `${classPrefix}__stat--add`]"
              :aria-label="`${file.additions.toLocaleString()} additions`"
            >+{{ file.additions.toLocaleString() }}</span>
            <span
              class="files-changed-file-list__stat files-changed-file-list__stat--remove"
              :class="[`${classPrefix}__stat`, `${classPrefix}__stat--remove`]"
              :aria-label="`${file.deletions.toLocaleString()} deletions`"
            >-{{ file.deletions.toLocaleString() }}</span>
          </span>
        </button>
      </li>
    </ul>
  </section>
</template>

<style scoped>
.files-changed-file-list {
  display: flex;
  min-width: 0;
  min-height: 0;
  flex-direction: column;
  overflow-y: auto;
  overscroll-behavior: contain;
  contain: content;
  scrollbar-gutter: stable;
}

.files-changed-file-list--large {
  height: min(60vh, 520px);
  max-height: 520px;
  overflow-y: auto;
  overscroll-behavior-y: contain;
  contain: strict;
}

.files-changed-file-list--full {
  height: 100%;
  max-height: 100%;
  contain: strict;
}

.files-changed-file-list__empty {
  margin: 0;
  padding: 10px 12px;
  color: var(--muted);
  font-size: 11px;
}

.files-changed-file-list--full .files-changed-file-list__empty {
  margin: 12px;
  padding: 0;
  font-size: 12px;
  line-height: 1.5;
}

.files-changed-file-list__notice {
  margin: 0;
  padding: 10px 12px;
  border: 1px solid var(--border);
  border-radius: 8px;
  color: var(--muted);
  font-size: 11px;
  line-height: 1.5;
}

.files-changed-file-list--full .files-changed-file-list__notice {
  margin: 12px;
  font-size: 12px;
}

.files-changed-file-list__notice--error {
  border-color: color-mix(in srgb, var(--error) 35%, var(--border));
  color: color-mix(in srgb, var(--error) 76%, var(--text));
}

.files-changed-file-list__loading {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 4px 0;
}

.files-changed-file-list--full .files-changed-file-list__loading {
  gap: 8px;
  padding: 12px;
}

.files-changed-file-list__skeleton {
  width: 72%;
  height: 32px;
  border-radius: 8px;
  background: linear-gradient(
    90deg,
    color-mix(in srgb, var(--panel) 90%, var(--text) 10%) 0%,
    color-mix(in srgb, var(--panel) 78%, var(--text) 22%) 50%,
    color-mix(in srgb, var(--panel) 90%, var(--text) 10%) 100%
  );
  background-size: 200% 100%;
  animation: files-changed-file-list-pulse 1.2s ease-in-out infinite;
}

.files-changed-file-list--full .files-changed-file-list__skeleton {
  height: 36px;
}

.files-changed-file-list__skeleton--wide {
  width: 100%;
}

.files-changed-file-list__skeleton--short {
  width: 54%;
}

.files-changed-file-list__list {
  display: flex;
  min-width: 0;
  flex-direction: column;
  flex: 0 0 auto;
  gap: 2px;
  margin: 0;
  padding: 0;
  list-style: none;
  contain: layout paint style;
}

.files-changed-file-list--full .files-changed-file-list__list {
  padding: 8px;
}

.files-changed-file-list__item {
  min-width: 0;
  content-visibility: auto;
  contain: layout paint style;
  contain-intrinsic-size: 38px;
}

.files-changed-file-list__row {
  display: grid;
  grid-template-columns: 16px minmax(0, 1fr) max-content;
  align-items: center;
  gap: 8px;
  width: 100%;
  min-height: 32px;
  padding: 6px 8px;
  border: 1px solid transparent;
  border-radius: 8px;
  background: transparent;
  color: var(--text);
  cursor: pointer;
  font: inherit;
  text-align: left;
  transition: background-color 0.2s ease, border-color 0.2s ease;
}

.files-changed-file-list--full .files-changed-file-list__row {
  grid-template-columns: 16px minmax(0, 1fr);
  align-items: start;
  gap: 6px 8px;
  min-height: 38px;
  padding: 7px 8px;
}

.files-changed-file-list__row:hover {
  background: rgba(255, 255, 255, 0.03);
}

.files-changed-file-list__row:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.files-changed-file-list__row--selected {
  border-color: color-mix(in srgb, var(--accent) 42%, var(--border));
  background: color-mix(in srgb, var(--accent) 14%, transparent);
}

.files-changed-file-list__row--selected:hover {
  background: color-mix(in srgb, var(--accent) 18%, transparent);
}

.files-changed-file-list__status-icon {
  width: 14px;
  height: 14px;
}

.files-changed-file-list--full .files-changed-file-list__status-icon {
  margin-top: 1px;
}

.files-changed-file-list__status-icon--added {
  color: var(--running);
}

.files-changed-file-list__status-icon--deleted {
  color: var(--error);
}

.files-changed-file-list__status-icon--modified {
  color: var(--accent);
}

.files-changed-file-list__path {
  min-width: 0;
  overflow: hidden;
  color: var(--text);
  font-family: ui-monospace, SFMono-Regular, SFMono-Regular, Consolas, "Liberation Mono", Menlo, monospace;
  font-size: 11px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.files-changed-file-list--full .files-changed-file-list__path {
  font-size: 12px;
  line-height: 1.35;
  overflow-wrap: anywhere;
  text-overflow: clip;
  white-space: normal;
}

.files-changed-file-list__stats {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 6px;
  min-width: 68px;
}

.files-changed-file-list--full .files-changed-file-list__stats {
  gap: 8px;
  grid-column: 2;
  justify-content: flex-start;
  min-width: 0;
}

.files-changed-file-list__stat {
  min-width: 30px;
  font-size: 10px;
  font-weight: 600;
  line-height: 1.2;
  text-align: right;
}

.files-changed-file-list--full .files-changed-file-list__stat {
  min-width: 0;
  text-align: left;
}

.files-changed-file-list__stat--add {
  color: var(--running);
}

.files-changed-file-list__stat--remove {
  color: var(--error);
}

@keyframes files-changed-file-list-pulse {
  0% {
    background-position: 200% 0;
  }

  100% {
    background-position: -200% 0;
  }
}
</style>
