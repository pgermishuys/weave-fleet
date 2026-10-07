<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { ChevronLeft, File, LoaderCircle, X } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";
import type { FileDiffItem, SessionDiffBase } from "@/api/client";
import { apiFetch } from "@/lib/api-client";
import { diffBaseLabel } from "@/lib/diff-base";
import { parseDiffLines, type DiffLine } from "@/lib/diff-parser";

/** The session's changed files as settings rows (name, folder, +a −d), then one file's diff, read-only. */
const props = defineProps<{
  open: boolean;
  sessionId: string;
  diffs: readonly FileDiffItem[];
  loading: boolean;
  base?: SessionDiffBase | null;
}>();
const emit = defineEmits<{ (event: "close"): void }>();

const picked = shallowRef<FileDiffItem | null>(null);
const lines = shallowRef<DiffLine[] | null>(null);
const failed = shallowRef<string | null>(null);

watch(() => props.open, (open) => {
  if (!open) picked.value = null;
});

/** Only what changed, with three lines either side, so a phone screen shows the change, not the file. */
const shown = computed(() => {
  const all = lines.value ?? [];
  const keep = new Set<number>();
  all.forEach((line, index) => {
    if (line.type === "context") return;
    for (let i = Math.max(0, index - 3); i <= Math.min(all.length - 1, index + 3); i++) keep.add(i);
  });
  return all.map((line, index) => ({ line, index, gap: keep.has(index) && index > 0 && !keep.has(index - 1) })).filter((entry) => keep.has(entry.index));
});

const subtitle = computed(() => {
  if (!props.diffs.length) return undefined;
  const files = `${props.diffs.length} file${props.diffs.length === 1 ? "" : "s"}`;
  const label = diffBaseLabel(props.base);
  if (!label) return `${files} in this session's folder`;
  return label.commit ? `${files} · ${label.text.toLowerCase()} at ${label.commit}` : `${files} · ${label.text.toLowerCase()}`;
});

const fileName = (path: string): string => path.split(/[\\/]/).pop() ?? path;
const folderOf = (path: string): string => path.split(/[\\/]/).slice(0, -1).join("/");

async function openFile(item: FileDiffItem): Promise<void> {
  picked.value = item;
  lines.value = null;
  failed.value = null;
  try {
    const response = await apiFetch(`/api/sessions/${encodeURIComponent(props.sessionId)}/diffs/file?path=${encodeURIComponent(item.file)}`);
    if (!response.ok) throw new Error(`It answered ${response.status}.`);
    const full = await response.json() as FileDiffItem;
    if (full.isBinary || full.binary) {
      failed.value = "A binary file: open it on the computer.";
      return;
    }
    lines.value = parseDiffLines(full.before ?? "", full.after ?? "");
  } catch (error) {
    failed.value = error instanceof Error ? error.message : String(error);
  }
}
</script>

<template>
  <BottomSheet
    :open="open"
    label="Changes"
    :title="picked ? undefined : 'Changes'"
    :subtitle="subtitle"
    :detents="['medium', 'large']"
    initial="medium"
    @close="emit('close')"
  >
    <template
      v-if="picked"
      #head
    >
      <button
        type="button"
        class="ph-icon-btn ph-icon-btn--text chs__back"
        aria-label="Back to the changes"
        @click="picked = null"
      >
        <ChevronLeft aria-hidden="true" />
      </button>
      <h2>
        {{ fileName(picked.file) }}<span
          v-if="folderOf(picked.file)"
          class="ph-sheet__sub ph-mono"
        >{{ folderOf(picked.file) }}</span>
      </h2>
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

    <template v-if="!picked">
      <p
        v-if="loading && diffs.length === 0"
        class="ph-foot chs__note"
      >
        <LoaderCircle
          class="ph-spinner"
          :size="14"
        /> Reading the changes…
      </p>
      <p
        v-else-if="diffs.length === 0"
        class="ph-foot chs__note"
      >
        Nothing changed yet.
      </p>
      <div
        v-else
        class="ph-card"
      >
        <button
          v-for="item in diffs"
          :key="item.file"
          type="button"
          class="ph-set"
          data-testid="phone-change"
          @click="openFile(item)"
        >
          <File
            class="ph-set__ic"
            aria-hidden="true"
          />
          <span class="ph-set__main">
            <span class="ph-set__t"><span>{{ fileName(item.file) }}</span></span>
            <span
              v-if="folderOf(item.file)"
              class="ph-set__s ph-mono chs__folder"
            >{{ folderOf(item.file) }}</span>
          </span>
          <span class="ph-mono chs__counts"><span class="ph-add">+{{ item.additions }}</span> <span class="ph-del">−{{ item.deletions }}</span></span>
        </button>
      </div>
    </template>
    <div
      v-else
      class="ph-sheet__pad"
    >
      <p
        v-if="failed"
        class="ph-foot chs__note"
      >
        {{ failed }}
      </p>
      <p
        v-else-if="!lines"
        class="chs__loading"
      >
        <LoaderCircle
          class="ph-spinner"
          :size="20"
        />
      </p>
      <div
        v-else
        class="ph-cmd chs__diff"
        data-testid="phone-diff"
      >
        <div class="chs__file">
          {{ fileName(picked.file) }} <span class="ph-add">+{{ picked.additions }}</span> <span class="ph-del">−{{ picked.deletions }}</span>
        </div>
        <div class="chs__lines">
          <template
            v-for="entry in shown"
            :key="entry.index"
          >
            <span
              v-if="entry.gap"
              class="chs__line chs__line--gap"
            >⋯</span>
            <span
              class="chs__line"
              :class="`chs__line--${entry.line.type}`"
            >{{ entry.line.type === "add" ? "+" : entry.line.type === "remove" ? "-" : " " }} {{ entry.line.content }}</span>
          </template>
        </div>
      </div>
    </div>
  </BottomSheet>
</template>

<style scoped>
.chs__back {
  margin-left: -10px;
}

.chs__note {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 0;
}

.chs__folder {
  overflow: hidden;
  font-size: 12px;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.chs__counts {
  flex: none;
  font-size: 12.5px;
}

.chs__loading {
  display: grid;
  min-height: 120px;
  place-items: center;
}

.chs__diff {
  margin-top: 0;
  padding: 0;
  overflow: hidden;
  font-size: 12.5px;
}

.chs__file {
  padding: 8px 12px;
  border-bottom: 1px solid var(--border);
  font-size: 12px;
  color: var(--muted);
}

.chs__lines {
  padding: 6px 0;
  overflow-x: auto;
  white-space: pre;
}

.chs__line {
  display: block;
  min-width: max-content;
  padding: 0 12px;
}

.chs__line--remove {
  background: color-mix(in srgb, var(--error) 10%, transparent);
}

.chs__line--add {
  background: color-mix(in srgb, var(--running) 10%, transparent);
}

.chs__line--gap {
  color: var(--muted);
}
</style>
