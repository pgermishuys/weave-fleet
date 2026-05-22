<script setup lang="ts">
import { computed, nextTick, onMounted, shallowRef, useTemplateRef, watch } from "vue";
import { ArrowLeft, Info } from "lucide-vue-next";
import DiffView from "@/components/session/DiffView.vue";
import FilesChangedFileList from "@/components/session/FilesChangedFileList.vue";
import type { FilesChangedFileListItem } from "@/components/session/FilesChangedFileList.vue";
import { useSessionDiffsContext } from "@/composables/use-session-diffs-context";
import { parseDiffLines } from "@/lib/diff-parser";

export type FileDiffViewItem = FilesChangedFileListItem;

const props = defineProps<{
  files?: readonly FileDiffViewItem[] | null;
  selectedFile?: FileDiffViewItem | null;
  isLoading?: boolean;
  error?: string | null;
  unavailable?: boolean;
}>();

const emit = defineEmits<{
  close: [];
  select: [file: FileDiffViewItem];
  retry: [];
}>();

const rootRef = useTemplateRef<HTMLElement>("filesChangedView");
const localSelectedFile = shallowRef<FileDiffViewItem | null>(null);
const previouslyFocusedElement = shallowRef<HTMLElement | null>(getActiveElement());
const sessionDiffsContext = useSessionDiffsContext();
const diffState = computed(() => sessionDiffsContext.value?.diffState ?? null);

const safeFiles = computed(() => props.files ?? diffState.value?.diffs.value ?? []);
const resolvedIsLoading = computed(() => props.isLoading ?? diffState.value?.isLoading.value ?? false);
const resolvedError = computed(() => props.error ?? diffState.value?.error.value ?? null);
const resolvedUnavailable = computed(() => props.unavailable ?? (
  diffState.value !== null
    && !resolvedIsLoading.value
    && !resolvedError.value
    && !diffState.value.available.value
));
const hasError = computed(() => Boolean(resolvedError.value));
const selectedFile = computed(() => props.selectedFile ?? localSelectedFile.value);
const selectedFileIndex = computed(() => {
  const selectedPath = selectedFile.value?.file ?? null;
  if (selectedPath === null) {
    return -1;
  }

  return safeFiles.value.findIndex((file) => file.file === selectedPath);
});
const canNavigateFiles = computed(() => (
  !resolvedIsLoading.value
  && !resolvedUnavailable.value
  && !hasError.value
  && safeFiles.value.length > 0
));

const mainState = computed<"loading" | "unavailable" | "error" | "empty" | "diff">(() => {
  if (resolvedIsLoading.value) {
    return "loading";
  }

  if (resolvedUnavailable.value) {
    return "unavailable";
  }

  if (hasError.value) {
    return "error";
  }

  if (safeFiles.value.length === 0) {
    return "empty";
  }

  return "diff";
});

const selectedFileContent = computed(() => {
  if (selectedFileDiffPlaceholder.value !== null) {
    return null;
  }

  const before = typeof selectedFile.value?.before === "string" ? selectedFile.value.before : "";
  const after = typeof selectedFile.value?.after === "string" ? selectedFile.value.after : "";

  if (before.length === 0 && after.length === 0) {
    return null;
  }

  return {
    before,
    after,
  };
});

const selectedFileDiffPlaceholder = computed(() => {
  if (selectedFile.value === null) {
    return null;
  }

  if (selectedFile.value.isBinary === true || selectedFile.value.binary === true) {
    return "Binary file — no diff available";
  }

  if (selectedFile.value.isTruncated === true || selectedFile.value.truncated === true) {
    return "File too large to diff";
  }

  return null;
});

const selectedFileDiffLines = computed(() => {
  if (selectedFileContent.value === null) {
    return [];
  }

  return parseDiffLines(selectedFileContent.value.before, selectedFileContent.value.after);
});

watch(
  () => props.selectedFile,
  (file) => {
    if (file !== null && file !== undefined) {
      localSelectedFile.value = file;
    }
  },
  { immediate: true },
);

watch(
  safeFiles,
  (files) => {
    if (localSelectedFile.value === null) {
      return;
    }

    const selectedStillExists = files.some((file) => file.file === localSelectedFile.value?.file);
    if (!selectedStillExists) {
      localSelectedFile.value = null;
    }
  },
  { immediate: true },
);

onMounted(() => {
  void focusInitialElement();
});

function closeView(): void {
  restoreFocusBeforeClose();
  emit("close");
}

function selectFile(file: FileDiffViewItem): void {
  if (resolvedIsLoading.value || resolvedUnavailable.value || hasError.value) {
    return;
  }

  localSelectedFile.value = file;
  emit("select", file);
}

function retryLoadingFiles(): void {
  emit("retry");
}

function handleViewKeydown(event: KeyboardEvent): void {
  if (event.defaultPrevented || isEditableTarget(event.target)) {
    return;
  }

  if (event.key === "Escape") {
    event.preventDefault();
    closeView();
    return;
  }

  if (event.key === "Tab") {
    trapFocus(event);
    return;
  }

  const targetIndex = getNavigationTargetIndex(event.key);
  if (targetIndex === null) {
    return;
  }

  event.preventDefault();
  selectFileByIndex(targetIndex);
}

function getNavigationTargetIndex(key: string): number | null {
  if (!canNavigateFiles.value) {
    return null;
  }

  const lastIndex = safeFiles.value.length - 1;
  const currentIndex = selectedFileIndex.value;

  if (key === "ArrowDown") {
    return currentIndex === -1 ? 0 : Math.min(currentIndex + 1, lastIndex);
  }

  if (key === "ArrowUp") {
    return currentIndex === -1 ? lastIndex : Math.max(currentIndex - 1, 0);
  }

  return null;
}

function selectFileByIndex(index: number): void {
  const file = safeFiles.value[index];
  if (file === undefined) {
    return;
  }

  selectFile(file);
  focusFileRow(index);
}

function focusFileRow(index: number): void {
  requestAnimationFrame(() => {
    rootRef.value
      ?.querySelector<HTMLButtonElement>(`[data-file-index="${index}"]`)
      ?.focus({ preventScroll: true });
  });
}

async function focusInitialElement(): Promise<void> {
  await nextTick();

  if (isFocusWithinView()) {
    return;
  }

  if (selectedFileIndex.value !== -1) {
    focusFileRow(selectedFileIndex.value);
    return;
  }

  const firstFileIndex = canNavigateFiles.value ? 0 : -1;
  if (firstFileIndex !== -1) {
    focusFileRow(firstFileIndex);
    return;
  }

  getFocusableElements()[0]?.focus({ preventScroll: true });
}

function trapFocus(event: KeyboardEvent): void {
  const focusableElements = getFocusableElements();
  if (focusableElements.length === 0) {
    event.preventDefault();
    rootRef.value?.focus({ preventScroll: true });
    return;
  }

  const firstElement = focusableElements[0];
  const lastElement = focusableElements[focusableElements.length - 1];
  const activeElement = getActiveElement();

  if (!rootRef.value?.contains(activeElement)) {
    event.preventDefault();
    (event.shiftKey ? lastElement : firstElement).focus({ preventScroll: true });
    return;
  }

  if (event.shiftKey && activeElement === firstElement) {
    event.preventDefault();
    lastElement.focus({ preventScroll: true });
    return;
  }

  if (!event.shiftKey && activeElement === lastElement) {
    event.preventDefault();
    firstElement.focus({ preventScroll: true });
  }
}

function getFocusableElements(): HTMLElement[] {
  const root = rootRef.value;
  if (root === null) {
    return [];
  }

  return Array.from(root.querySelectorAll<HTMLElement>([
    "button:not([disabled])",
    "[href]",
    "input:not([disabled])",
    "select:not([disabled])",
    "textarea:not([disabled])",
    "[tabindex]:not([tabindex='-1'])",
  ].join(","))).filter((element) => !element.hasAttribute("disabled") && element.offsetParent !== null);
}

function restoreFocusBeforeClose(): void {
  const target = previouslyFocusedElement.value;
  if (target === null || !target.isConnected || rootRef.value?.contains(target)) {
    return;
  }

  target.focus({ preventScroll: true });
}

function isFocusWithinView(): boolean {
  return rootRef.value?.contains(getActiveElement()) ?? false;
}

function getActiveElement(): HTMLElement | null {
  if (typeof document === "undefined" || !(document.activeElement instanceof HTMLElement)) {
    return null;
  }

  return document.activeElement;
}

function isEditableTarget(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) {
    return false;
  }

  const tagName = target.tagName.toLowerCase();
  return target.isContentEditable || tagName === "input" || tagName === "textarea" || tagName === "select";
}
</script>

<template>
  <section
    ref="filesChangedView"
    class="files-changed-view"
    aria-label="Changed files diff viewer"
    aria-keyshortcuts="ArrowUp ArrowDown Escape"
    tabindex="-1"
    @keydown="handleViewKeydown"
  >
    <header class="files-changed-view__header">
      <button
        type="button"
        class="files-changed-view__back-button"
        @click="closeView"
      >
        <ArrowLeft
          class="files-changed-view__back-icon"
          aria-hidden="true"
        />
        <span>Back to chat</span>
      </button>

      <div class="files-changed-view__heading">
        <h2 class="files-changed-view__title">
          Changed files
        </h2>
        <p class="files-changed-view__description">
          Review the files changed by this session.
        </p>
      </div>
    </header>

    <div class="files-changed-view__body">
      <aside
        class="files-changed-view__sidebar"
        aria-label="Changed file list"
      >
        <FilesChangedFileList
          :files="safeFiles"
          :selected-file="selectedFile"
          :is-loading="resolvedIsLoading"
          :error="resolvedError"
          :unavailable="resolvedUnavailable"
          variant="full"
          aria-label="Changed file list"
          @select="selectFile"
        />
      </aside>

      <main
        class="files-changed-view__main"
        aria-label="Selected file diff"
      >
        <div
          v-if="mainState === 'diff' && selectedFile !== null"
          class="files-changed-view__diff-header"
        >
          <p
            class="files-changed-view__selected-path"
            :title="selectedFile.file"
          >
            {{ selectedFile.file }}
          </p>
        </div>

        <div class="files-changed-view__diff-body">
          <div
            v-if="mainState === 'loading'"
            class="files-changed-view__state"
            role="status"
            aria-live="polite"
            aria-busy="true"
          >
            <span
              class="files-changed-view__state-spinner"
              aria-hidden="true"
            />
            <p class="files-changed-view__state-title">
              Loading changed files…
            </p>
          </div>

          <div
            v-else-if="mainState === 'unavailable'"
            class="files-changed-view__state"
            role="status"
          >
            <Info
              class="files-changed-view__state-icon"
              aria-hidden="true"
            />
            <p class="files-changed-view__state-title">
              Diffs unavailable for this session
            </p>
          </div>

          <div
            v-else-if="mainState === 'error'"
            class="files-changed-view__state files-changed-view__state--error"
            role="alert"
          >
            <Info
              class="files-changed-view__state-icon"
              aria-hidden="true"
            />
            <div class="files-changed-view__state-copy">
              <p class="files-changed-view__state-title">
                Changed files could not be loaded.
              </p>
              <p
                v-if="resolvedError !== null"
                class="files-changed-view__state-detail"
              >
                {{ resolvedError }}
              </p>
              <button
                type="button"
                class="files-changed-view__retry-button"
                @click="retryLoadingFiles"
              >
                Retry
              </button>
            </div>
          </div>

          <div
            v-else-if="mainState === 'empty'"
            class="files-changed-view__state"
            role="status"
          >
            <Info
              class="files-changed-view__state-icon"
              aria-hidden="true"
            />
            <p class="files-changed-view__state-title">
              No files changed
            </p>
          </div>

          <div
            v-else-if="selectedFileDiffPlaceholder !== null"
            class="files-changed-view__diff-placeholder"
            role="status"
          >
            <Info
              class="files-changed-view__diff-placeholder-icon"
              aria-hidden="true"
            />
            <p class="files-changed-view__diff-placeholder-text">
              {{ selectedFileDiffPlaceholder }}
            </p>
          </div>

          <DiffView
            v-else-if="selectedFileContent !== null"
            :lines="selectedFileDiffLines"
          />

          <div
            v-else-if="selectedFile !== null"
            class="files-changed-view__diff-placeholder"
          >
            <Info
              class="files-changed-view__diff-placeholder-icon"
              aria-hidden="true"
            />
            <p class="files-changed-view__diff-placeholder-text">
              Diff content not available — only summary data is loaded for this session.
            </p>
          </div>

          <p
            v-else
            class="files-changed-view__diff-placeholder"
          >
            Select a file to view its diff.
          </p>
        </div>
      </main>
    </div>
  </section>
</template>

<style scoped>
.files-changed-view {
  display: flex;
  width: 100%;
  height: 100%;
  min-height: 0;
  min-width: 0;
  flex-direction: column;
  background: var(--bg);
  color: var(--text);
}

.files-changed-view__header {
  display: flex;
  flex: 0 0 auto;
  align-items: center;
  gap: 16px;
  padding: 14px 20px;
  border-bottom: 1px solid var(--border);
  background: color-mix(in srgb, var(--panel) 82%, transparent);
}

.files-changed-view__back-button {
  display: inline-flex;
  min-height: 34px;
  flex: 0 0 auto;
  align-items: center;
  gap: 8px;
  padding: 6px 12px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: color-mix(in srgb, var(--panel) 86%, transparent);
  color: var(--text);
  cursor: pointer;
  font: inherit;
  font-size: 13px;
  font-weight: 600;
}

.files-changed-view__back-button:hover {
  background: color-mix(in srgb, var(--panel) 72%, var(--text) 8%);
}

.files-changed-view__back-button:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.files-changed-view__back-icon {
  width: 16px;
  height: 16px;
}

.files-changed-view__heading {
  display: flex;
  min-width: 0;
  flex-direction: column;
  gap: 2px;
}

.files-changed-view__title,
.files-changed-view__description {
  margin: 0;
}

.files-changed-view__title {
  color: var(--text);
  font-size: 16px;
  font-weight: 700;
  line-height: 1.25;
}

.files-changed-view__description {
  color: var(--muted);
  font-size: 12px;
  line-height: 1.4;
}

.files-changed-view__body {
  display: grid;
  min-height: 0;
  min-width: 0;
  flex: 1 1 auto;
  grid-template-columns: minmax(220px, 280px) minmax(0, 1fr);
}

.files-changed-view__sidebar {
  min-height: 0;
  min-width: 0;
  overflow-y: auto;
  border-right: 1px solid var(--border);
  background: color-mix(in srgb, var(--panel) 74%, var(--bg));
}

.files-changed-view__main {
  display: flex;
  min-height: 0;
  min-width: 0;
  flex-direction: column;
  overflow: hidden;
  background: rgba(255, 255, 255, 0.02);
}

.files-changed-view__diff-header {
  flex: 0 0 auto;
  padding: 12px 16px;
  border-bottom: 1px solid var(--border);
  background: color-mix(in srgb, var(--panel) 82%, transparent);
}

.files-changed-view__selected-path {
  min-width: 0;
  margin: 0;
  overflow: hidden;
  color: var(--text);
  font-family: ui-monospace, SFMono-Regular, SFMono-Regular, Consolas, "Liberation Mono", Menlo, monospace;
  font-size: 12px;
  font-weight: 600;
  line-height: 1.4;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.files-changed-view__diff-body {
  min-height: 0;
  flex: 1 1 auto;
  overflow: auto;
}

.files-changed-view__state {
  display: flex;
  min-height: 100%;
  align-items: center;
  justify-content: center;
  gap: 10px;
  padding: 24px;
  color: var(--muted);
  font-size: 13px;
  line-height: 1.5;
  text-align: center;
}

.files-changed-view__state--error {
  align-items: flex-start;
  color: color-mix(in srgb, var(--error) 76%, var(--text));
  text-align: left;
}

.files-changed-view__state-icon {
  width: 16px;
  height: 16px;
  flex: 0 0 auto;
  margin-top: 2px;
  opacity: 0.78;
}

.files-changed-view__state-copy {
  display: flex;
  max-width: 460px;
  flex-direction: column;
  align-items: flex-start;
  gap: 10px;
}

.files-changed-view__state-title,
.files-changed-view__state-detail {
  margin: 0;
}

.files-changed-view__state-title {
  font-weight: 600;
}

.files-changed-view__state-detail {
  color: var(--muted);
  font-size: 12px;
  overflow-wrap: anywhere;
}

.files-changed-view__state-spinner {
  width: 18px;
  height: 18px;
  flex: 0 0 auto;
  border: 2px solid color-mix(in srgb, var(--muted) 28%, transparent);
  border-top-color: var(--accent);
  border-radius: 999px;
  animation: files-changed-view-spin 0.8s linear infinite;
}

.files-changed-view__retry-button {
  min-height: 30px;
  padding: 5px 12px;
  border: 1px solid color-mix(in srgb, var(--error) 42%, var(--border));
  border-radius: 8px;
  background: color-mix(in srgb, var(--error) 10%, transparent);
  color: color-mix(in srgb, var(--error) 78%, var(--text));
  cursor: pointer;
  font: inherit;
  font-size: 12px;
  font-weight: 600;
}

.files-changed-view__retry-button:hover {
  background: color-mix(in srgb, var(--error) 16%, transparent);
}

.files-changed-view__retry-button:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.files-changed-view__diff-placeholder {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 0;
  padding: 16px;
  color: var(--muted);
  font-size: 12px;
  line-height: 1.5;
}

.files-changed-view__diff-placeholder-icon {
  width: 15px;
  height: 15px;
  flex: 0 0 auto;
  opacity: 0.72;
}

.files-changed-view__diff-placeholder-text {
  margin: 0;
}

@keyframes files-changed-view-spin {
  to {
    transform: rotate(360deg);
  }
}
</style>
