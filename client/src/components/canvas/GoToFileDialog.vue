<script setup lang="ts">
import { computed, nextTick, ref, watch } from "vue";
import { Search } from "lucide-vue-next";
import { Dialog, DialogContent, DialogDescription, DialogTitle } from "@/components/ui/dialog";
import { useFindFiles } from "@/composables/use-find-files";
import { useSidebarMobile } from "@/composables/use-sidebar-mobile";
import { fileIcon, fileName } from "@/lib/canvas-registry";
import { useCanvasesStore } from "@/stores/canvases";
import { useGoToFileStore } from "@/stores/go-to-file";

/**
 * Go to file (Ctrl P / ⌘P): type part of a file's name, or its letters in order, and Enter opens
 * it as a kept tab. With nothing typed, it lists the files already open.
 */
const store = useGoToFileStore();
const canvases = useCanvasesStore();
const { showRightPanel } = useSidebarMobile();

const query = ref("");
const active = ref(0);
const listRef = ref<HTMLElement | null>(null);
const open = computed(() => store.sessionId !== null);

const search = useFindFiles(
  () => store.sessionId,
  () => (open.value && query.value.trim() ? query.value : null),
);

const openFiles = computed(() => {
  const id = store.sessionId;
  if (!id) return [];
  return canvases.sessionCanvases(id).canvases.flatMap((canvas) => (canvas.file ? [canvas.file.path] : []));
});

const items = computed(() =>
  query.value.trim()
    ? search.files.value.filter((path) => !path.endsWith("/"))
    : openFiles.value,
);

watch(open, (value) => {
  if (value) {
    query.value = "";
    active.value = 0;
  }
});
watch(items, () => {
  active.value = 0;
});

function folderOf(path: string): string {
  const slash = path.lastIndexOf("/");
  return slash < 0 ? "" : path.slice(0, slash);
}

function pick(path: string | undefined): void {
  const id = store.sessionId;
  if (!id || !path) return;
  store.focusOnOpen = { sessionId: id, path };
  canvases.openFile(id, path, { keep: true });
  showRightPanel();
  store.hide();
}

function move(step: number): void {
  if (items.value.length === 0) return;
  active.value = (active.value + step + items.value.length) % items.value.length;
  void nextTick(() => {
    listRef.value?.querySelector<HTMLElement>('[aria-selected="true"]')?.scrollIntoView?.({ block: "nearest" });
  });
}

function onKeydown(event: KeyboardEvent): void {
  if (event.key === "ArrowDown") {
    event.preventDefault();
    move(1);
  } else if (event.key === "ArrowUp") {
    event.preventDefault();
    move(-1);
  } else if (event.key === "Enter") {
    event.preventDefault();
    pick(items.value[active.value]);
  }
}
</script>

<template>
  <Dialog
    :open="open"
    @update:open="!$event && store.hide()"
  >
    <DialogContent
      class="go-to-file p-0 sm:max-w-lg"
      :show-close-button="false"
      data-testid="go-to-file"
    >
      <DialogTitle class="sr-only">
        Go to file
      </DialogTitle>
      <DialogDescription class="sr-only">
        Type part of a file's name, then press Enter to open it.
      </DialogDescription>
      <div class="go-to-file__input">
        <Search
          :size="15"
          aria-hidden="true"
        />
        <input
          v-model="query"
          type="text"
          placeholder="Go to file…"
          aria-label="Go to file"
          aria-controls="go-to-file-list"
          :aria-activedescendant="items.length ? `go-to-file-${active}` : undefined"
          autocomplete="off"
          spellcheck="false"
          data-testid="go-to-file-input"
          @keydown="onKeydown"
        >
      </div>
      <div
        id="go-to-file-list"
        ref="listRef"
        class="go-to-file__list"
        role="listbox"
        aria-label="Files"
      >
        <p
          v-if="query.trim() && search.isLoading.value && items.length === 0"
          class="go-to-file__empty"
        >
          Searching…
        </p>
        <p
          v-else-if="items.length === 0"
          class="go-to-file__empty"
        >
          {{ query.trim() ? "No files match." : "Type to find a file in this session." }}
        </p>
        <p
          v-else-if="!query.trim()"
          class="go-to-file__group"
        >
          Open
        </p>
        <div
          v-for="(path, index) in items"
          :id="`go-to-file-${index}`"
          :key="path"
          class="go-to-file__item"
          role="option"
          :aria-selected="index === active"
          :data-testid="`go-to-file-item-${path}`"
          @mousemove="active = index"
          @click="pick(path)"
        >
          <component
            :is="fileIcon(path)"
            :size="14"
            aria-hidden="true"
          />
          <span class="go-to-file__name">{{ fileName(path) }}</span>
          <span class="go-to-file__dir">{{ folderOf(path) }}</span>
        </div>
      </div>
      <div class="go-to-file__foot">
        <span><kbd>↑</kbd><kbd>↓</kbd> to move</span>
        <span><kbd>Enter</kbd> to open</span>
        <span><kbd>Esc</kbd> to close</span>
      </div>
    </DialogContent>
  </Dialog>
</template>

<style scoped>
.go-to-file__input {
  display: flex;
  align-items: center;
  gap: 8px;
  height: 46px;
  padding: 0 14px;
  border-bottom: 1px solid var(--border);
  color: var(--muted);
}

.go-to-file__input input {
  flex: 1;
  min-width: 0;
  border: 0;
  outline: 0;
  background: transparent;
  color: var(--text);
  font-size: 14px;
}

.go-to-file__list {
  max-height: min(360px, 55vh);
  overflow-y: auto;
  padding: 6px;
}

.go-to-file__group,
.go-to-file__empty {
  margin: 0;
  padding: 6px 10px;
  font-size: 12px;
  color: var(--muted);
}

.go-to-file__group {
  font-size: 11px;
  font-weight: 600;
}

.go-to-file__item {
  display: flex;
  align-items: center;
  gap: 8px;
  height: 32px;
  padding: 0 10px;
  border-radius: 7px;
  color: var(--muted);
  font-size: 13px;
  cursor: pointer;
}

.go-to-file__item[aria-selected="true"] {
  background: var(--accent-dim);
  color: var(--text);
}

.go-to-file__name {
  flex-shrink: 0;
  color: var(--text);
}

.go-to-file__dir {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 12px;
}

.go-to-file__foot {
  display: flex;
  gap: 14px;
  padding: 8px 14px;
  border-top: 1px solid var(--border);
  font-size: 11.5px;
  color: var(--muted);
}

.go-to-file__foot kbd {
  margin-right: 3px;
  font: 500 10.5px var(--font-sans-stack);
}
</style>
