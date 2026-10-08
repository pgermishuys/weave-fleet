<script setup lang="ts">
import { shallowRef, watch } from "vue";
import { computed } from "vue";
import { ChevronLeft, ChevronRight, FileText, Folder, LoaderCircle, X } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";
import { browseSessionDirectory, readSessionFile } from "@/api/session-files";
import { useMachineTarget } from "@/lib/machine-target";

/** The session's folder, read-only: folders to walk through, a file's text to read. Editing is for the computer. */
const props = defineProps<{ open: boolean; sessionId: string }>();
const machine = useMachineTarget();
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
    const listing = await browseSessionDirectory(machine, props.sessionId, next || undefined);
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
    const content = await readSessionFile(machine, props.sessionId, entry.relativePath);
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
        class="ph-icon-btn ph-icon-btn--text fls__back"
        aria-label="Back"
        @click="goBack"
      >
        <ChevronLeft aria-hidden="true" />
      </button>
      <h2>{{ heading }}</h2>
      <LoaderCircle
        v-if="loading"
        class="ph-spinner"
        :size="18"
      />
      <button
        type="button"
        class="ph-icon-btn"
        aria-label="Close"
        data-testid="sheet-close"
        @click="emit('close')"
      >
        <X aria-hidden="true" />
      </button>
    </template>

    <p
      v-if="error"
      class="ph-foot ph-foot--bad fls__note"
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
        class="ph-foot fls__note"
      >
        {{ file.note }}
      </p>
      <pre
        v-if="file.text !== null"
        class="ph-cmd fls__pre"
        data-testid="phone-file-text"
      >{{ file.text }}</pre>
    </div>
    <div
      v-else-if="entries.length"
      class="ph-card"
    >
      <button
        v-for="entry in entries"
        :key="entry.relativePath"
        type="button"
        class="ph-set"
        data-testid="phone-file"
        @click="entry.isDirectory ? browse(entry.relativePath) : read(entry)"
      >
        <Folder
          v-if="entry.isDirectory"
          class="ph-set__ic fls__dir"
          aria-hidden="true"
        />
        <FileText
          v-else
          class="ph-set__ic"
          aria-hidden="true"
        />
        <span class="ph-set__main"><span class="ph-set__t">{{ entry.name }}</span></span>
        <ChevronRight
          v-if="entry.isDirectory"
          class="ph-set__chev"
          aria-hidden="true"
        />
      </button>
    </div>
  </BottomSheet>
</template>

<style scoped>
.fls__back {
  margin-left: -10px;
}

.fls__path {
  margin: 0 0 10px;
  font-family: var(--ph-mono);
  font-size: 12px;
  color: var(--muted);
  overflow-wrap: anywhere;
}

.fls__note {
  margin: 0 0 10px;
}

.fls__pre {
  max-height: none;
  margin-top: 0;
  overflow-x: auto;
  font-size: 12px;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.fls__dir {
  color: var(--accent);
}
</style>
