<script setup lang="ts">
import { shallowRef, watch } from "vue";
import { computed } from "vue";
import { ArrowLeft, ChevronLeft, ChevronRight, FileText, Folder, LoaderCircle } from "lucide-vue-next";
import { phoneLook } from "@/composables/phone/use-phone-env";
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

const heading = computed(() => (file.value ? file.value.path.split("/").pop() ?? file.value.path : path.value ? path.value.split("/").pop() ?? path.value : "Files"));
const canGoBack = computed(() => Boolean(file.value) || Boolean(path.value));
function goBack(): void {
  if (file.value) file.value = null;
  else up();
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
    :detents="['large']"
    @close="emit('close')"
  >
    <template #head>
      <button
        v-if="canGoBack"
        type="button"
        class="ph-navbtn ph-glass"
        aria-label="Back"
        @click="goBack"
      >
        <ArrowLeft
          v-if="phoneLook === 'android'"
          :size="24"
          aria-hidden="true"
        />
        <ChevronLeft
          v-else
          :size="24"
          :stroke-width="2.4"
          aria-hidden="true"
        />
      </button>
      <h2>{{ heading }}</h2>
      <span class="ph-navbar__spacer" />
      <LoaderCircle
        v-if="loading"
        class="ph-spinner fls__spin"
        :size="18"
      />
    </template>

    <p
      v-if="error"
      class="ph-note ph-note--error"
      role="alert"
    >
      {{ error }}
    </p>
    <div
      v-if="file"
      class="ph-sheet__pad"
    >
      <p class="fls__path">
        {{ file.path }}
      </p>
      <p
        v-if="file.note"
        class="ph-note fls__note"
      >
        {{ file.note }}
      </p>
      <pre
        v-if="file.text !== null"
        class="ph-code fls__pre"
        data-testid="phone-file-text"
      >{{ file.text }}</pre>
    </div>
    <div
      v-else-if="entries.length"
      class="ph-group"
    >
      <button
        v-for="entry in entries"
        :key="entry.relativePath"
        type="button"
        class="ph-row"
        style="--ph-sep-left: 52px"
        data-testid="phone-file"
        @click="entry.isDirectory ? browse(entry.relativePath) : read(entry)"
      >
        <Folder
          v-if="entry.isDirectory"
          class="fls__icon fls__icon--dir"
          :size="22"
          aria-hidden="true"
        />
        <FileText
          v-else
          class="fls__icon"
          :size="22"
          aria-hidden="true"
        />
        <span class="ph-row__main"><span class="ph-row__title">{{ entry.name }}</span></span>
        <ChevronRight
          v-if="entry.isDirectory"
          class="ph-row__chev"
          :size="16"
          :stroke-width="3"
          aria-hidden="true"
        />
      </button>
    </div>
  </BottomSheet>
</template>

<style scoped>
.fls__spin {
  margin-right: 12px;
}

.fls__path {
  margin: 0 0 10px;
  font-family: var(--ph-mono);
  font-size: 0.8rem;
  color: var(--muted);
  overflow-wrap: anywhere;
}

.fls__note {
  margin: 0 0 10px;
}

.fls__pre {
  overflow-x: auto;
  font-size: 0.72rem;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.fls__icon {
  flex: none;
  color: var(--muted);
}

.fls__icon--dir {
  color: var(--accent);
}
</style>
