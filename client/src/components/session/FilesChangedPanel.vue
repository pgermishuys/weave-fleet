<script setup lang="ts">
import { computed } from "vue";
import DiffView from "@/components/session/DiffView.vue";
import FilesChangedFileList from "@/components/session/FilesChangedFileList.vue";
import type { FilesChangedFileListItem } from "@/components/session/FilesChangedFileList.vue";
import { parseDiffLines } from "@/lib/diff-parser";

export type FileDiffPanelItem = FilesChangedFileListItem;

const props = withDefaults(
  defineProps<{
    files?: readonly FileDiffPanelItem[] | null;
    selectedFile?: FileDiffPanelItem | null;
    isLoading?: boolean;
    error?: string | null;
    unavailable?: boolean;
  }>(),
  {
    files: () => [],
    selectedFile: null,
    isLoading: false,
    error: null,
    unavailable: false,
  },
);

const emit = defineEmits<{
  select: [file: FileDiffPanelItem];
}>();

const selectedFile = computed(() => props.selectedFile ?? null);

const hasSelectedFileContent = computed(() => selectedFileContent.value !== null);

const selectedFileContent = computed(() => {
  if (selectedFileDiffPlaceholder.value !== null) {
    return null;
  }

  if (typeof selectedFile.value?.before !== "string" || typeof selectedFile.value.after !== "string") {
    return null;
  }

  return {
    before: selectedFile.value.before,
    after: selectedFile.value.after,
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

function selectFile(file: FileDiffPanelItem): void {
  if (props.isLoading || props.unavailable) {
    return;
  }

  emit("select", file);
}
</script>

<template>
  <section
    v-if="!unavailable"
    class="files-changed-panel"
    aria-label="Changed files"
  >
    <FilesChangedFileList
      :files="files"
      :selected-file="selectedFile"
      :is-loading="isLoading"
      :error="error"
      aria-label="Changed files"
      @select="selectFile"
    />

    <div
      v-if="selectedFile !== null"
      class="files-changed-panel__diff"
    >
      <DiffView
        v-if="hasSelectedFileContent"
        :lines="selectedFileDiffLines"
      />

      <p
        v-else-if="selectedFileDiffPlaceholder !== null"
        class="files-changed-panel__diff-placeholder"
        role="status"
      >
        {{ selectedFileDiffPlaceholder }}
      </p>

      <p
        v-else
        class="files-changed-panel__diff-placeholder"
      >
        Diff content is unavailable for this file. The current session diff summary only includes file
        status and line counts.
      </p>
    </div>
  </section>
</template>

<style scoped>
.files-changed-panel {
  display: flex;
  flex-direction: column;
  gap: 10px;
  min-width: 0;
}

.files-changed-panel__diff {
  min-width: 0;
  overflow: hidden;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: rgba(255, 255, 255, 0.02);
}

.files-changed-panel__diff-placeholder {
  margin: 0;
  padding: 12px;
  color: var(--muted);
  font-size: 11px;
  line-height: 1.5;
}

</style>
