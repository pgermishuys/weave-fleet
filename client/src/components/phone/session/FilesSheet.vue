<script setup lang="ts">
import { shallowRef, watch } from "vue";
import { ChevronLeft, FileText, Folder, LoaderCircle } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";
import { browseSessionDirectory, readSessionFile } from "@/api/session-files";

/** The session's folder, read-only: folders to walk through, a file's text to read. Editing is for the computer. */
const props = defineProps<{ open: boolean; sessionId: string }>();
const emit = defineEmits<{ (event: "close"): void }>();

interface Entry {
  name: string;
  relativePath: string;
  isDirectory: boolean;
}

const path = shallowRef("");
const entries = shallowRef<Entry[]>([]);
const file = shallowRef<{ path: string; text: string | null; note: string | null } | null>(null);
const loading = shallowRef(false);
const error = shallowRef<string | null>(null);

async function browse(next: string): Promise<void> {
  loading.value = true;
  error.value = null;
  try {
    const listing = await browseSessionDirectory(props.sessionId, next || undefined);
    entries.value = [...listing.entries].sort((a, b) => Number(b.isDirectory) - Number(a.isDirectory) || a.name.localeCompare(b.name));
    path.value = next;
  } catch (failure) {
    error.value = failure instanceof Error ? failure.message : String(failure);
  } finally {
    loading.value = false;
  }
}

async function read(entry: Entry): Promise<void> {
  loading.value = true;
  try {
    const content = await readSessionFile(props.sessionId, entry.relativePath);
    file.value = {
      path: entry.relativePath,
      text: content.isBinary ? null : content.content,
      note: content.isBinary ? "A binary file: open it on the computer." : content.isTruncated ? "Only the start of this file is shown." : null,
    };
  } catch (failure) {
    error.value = failure instanceof Error ? failure.message : String(failure);
  } finally {
    loading.value = false;
  }
}

function up(): void {
  const parts = path.value.split("/").filter(Boolean);
  parts.pop();
  void browse(parts.join("/"));
}

watch(() => props.open, (open) => {
  file.value = null;
  if (open) void browse("");
});
</script>

<template>
  <BottomSheet
    :open="open"
    label="Files"
    full
    @close="emit('close')"
  >
    <template v-if="file">
      <button
        type="button"
        class="fls__back"
        @click="file = null"
      >
        <ChevronLeft
          :size="16"
          aria-hidden="true"
        /> {{ path || "Files" }}
      </button>
      <h2 class="fls__file">
        {{ file.path }}
      </h2>
      <p
        v-if="file.note"
        class="fls__note"
      >
        {{ file.note }}
      </p>
      <pre
        v-if="file.text !== null"
        class="fls__pre"
        data-testid="phone-file-text"
      >{{ file.text }}</pre>
    </template>
    <template v-else>
      <div class="fls__head">
        <button
          v-if="path"
          type="button"
          class="fls__back"
          @click="up"
        >
          <ChevronLeft
            :size="16"
            aria-hidden="true"
          /> Up
        </button>
        <h2 class="fls__title">
          {{ path || "Files" }}
        </h2>
        <LoaderCircle
          v-if="loading"
          class="animate-spin text-muted"
          :size="14"
        />
      </div>
      <p
        v-if="error"
        class="fls__note"
        role="alert"
      >
        {{ error }}
      </p>
      <button
        v-for="entry in entries"
        :key="entry.relativePath"
        type="button"
        class="fls__row"
        data-testid="phone-file"
        @click="entry.isDirectory ? browse(entry.relativePath) : read(entry)"
      >
        <Folder
          v-if="entry.isDirectory"
          :size="15"
          class="text-accent"
          aria-hidden="true"
        />
        <FileText
          v-else
          :size="15"
          class="text-muted"
          aria-hidden="true"
        />
        <span class="fls__name">{{ entry.name }}</span>
      </button>
    </template>
  </BottomSheet>
</template>

<style scoped>
.fls__head {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 6px;
}

.fls__title {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  font-family: var(--font-mono-stack);
  font-size: 13px;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.fls__back {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  min-height: 36px;
  border: 0;
  background: transparent;
  color: var(--accent);
  font: inherit;
  font-size: 13px;
}

.fls__row {
  display: flex;
  align-items: center;
  gap: 10px;
  width: 100%;
  min-height: 44px;
  border: 0;
  border-bottom: 1px solid var(--border);
  background: transparent;
  color: var(--text);
  font: inherit;
  font-size: 14px;
  text-align: left;
}

.fls__name {
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.fls__file {
  margin: 4px 0 8px;
  font-family: var(--font-mono-stack);
  font-size: 12px;
  overflow-wrap: anywhere;
}

.fls__note {
  font-size: 13px;
  color: var(--muted);
}

.fls__pre {
  margin: 0;
  overflow-x: auto;
  padding: 8px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--main-bg);
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  line-height: 1.5;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}
</style>
