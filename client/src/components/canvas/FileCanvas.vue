<script setup lang="ts">
import { computed, onActivated, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { Transaction } from "@codemirror/state";
import { EditorView, type ViewUpdate } from "@codemirror/view";
import { openBuffer, saveBuffer } from "@/lib/code-editor/buffers";
import { useCanvasesStore, type FileView } from "@/stores/canvases";
import { useFileBuffersStore, type FileBufferRecord } from "@/stores/file-buffers";

const props = defineProps<{
  sessionId: string;
  path: string;
  view: FileView;
}>();

const buffers = useFileBuffersStore();
const canvases = useCanvasesStore();
const info = computed(() => buffers.info(props.sessionId, props.path));

const editorHost = ref<HTMLElement | null>(null);
let editor: EditorView | null = null;
let attached: FileBufferRecord | null = null;

function onUpdate(update: ViewUpdate): void {
  // Typing in a preview tab keeps it; the agent's changes (remote) don't.
  if (update.docChanged && update.transactions.some((tr) => !tr.annotation(Transaction.remote))) {
    canvases.keepFile(props.sessionId, props.path);
  }
}

async function save(): Promise<void> {
  await saveBuffer(props.sessionId, props.path);
}

function detach(): void {
  if (attached && attached.view === editor) {
    attached.view = null;
    attached.handlers = {};
  }
  editor?.destroy();
  editor = null;
  attached = null;
}

/**
 * Show the buffer in this canvas's editor. The buffer outlives the canvas (the host keeps only 8
 * tabs alive), so a remount, or a cached canvas reused for a reopened file, attaches again.
 */
async function attach(): Promise<void> {
  const record = await openBuffer(props.sessionId, props.path);
  if (!editorHost.value || !record.state) return;
  if (attached === record && editor && record.view === editor) {
    editor.requestMeasure();
    return;
  }

  detach();
  editor = new EditorView({ state: record.state, parent: editorHost.value });
  record.view = editor;
  record.handlers = { save: () => void save(), update: onUpdate };
  attached = record;
}

onMounted(() => void attach());
onActivated(() => void attach());
onBeforeUnmount(detach);
watch(() => info.value?.status, (status) => {
  if (status === "ready") void attach();
});
</script>

<template>
  <div
    class="file-canvas"
    :data-path="path"
  >
    <p
      v-if="info?.status === 'loading'"
      class="file-canvas__note"
    >
      Opening {{ path }}…
    </p>
    <p
      v-else-if="info?.status === 'unavailable' || info?.status === 'error'"
      class="file-canvas__note"
      role="status"
    >
      {{ info.message }}
    </p>
    <div
      v-show="info?.status === 'ready'"
      ref="editorHost"
      class="file-canvas__editor"
      data-testid="file-editor"
    />
  </div>
</template>

<style scoped>
.file-canvas {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}

.file-canvas__editor {
  flex: 1;
  min-height: 0;
  overflow: hidden;
}

.file-canvas__editor :deep(.cm-editor) {
  height: 100%;
}

.file-canvas__note {
  margin: 0;
  padding: 14px 18px;
  font-size: 13px;
  color: var(--muted);
}
</style>
