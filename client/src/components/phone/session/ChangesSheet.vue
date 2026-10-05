<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { ChevronLeft, ChevronRight, LoaderCircle } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";
import type { FileDiffItem } from "@/api/client";
import { apiFetch } from "@/lib/api-client";
import { parseDiffLines, type DiffLine } from "@/lib/diff-parser";

/** The session's changed files, then one file's diff, read-only. */
const props = defineProps<{ open: boolean; sessionId: string; diffs: readonly FileDiffItem[]; loading: boolean }>();
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
    full
    @close="emit('close')"
  >
    <template v-if="!picked">
      <h2 class="chs__title">
        Changes <span class="chs__count">{{ diffs.length }} file{{ diffs.length === 1 ? "" : "s" }}</span>
      </h2>
      <p
        v-if="loading && diffs.length === 0"
        class="chs__note"
      >
        <LoaderCircle
          class="inline animate-spin"
          :size="14"
        /> Reading the changes…
      </p>
      <p
        v-else-if="diffs.length === 0"
        class="chs__note"
      >
        Nothing changed yet.
      </p>
      <button
        v-for="item in diffs"
        :key="item.file"
        type="button"
        class="chs__row"
        data-testid="phone-change"
        @click="openFile(item)"
      >
        <span class="chs__path">{{ item.file }}</span>
        <span class="chs__add">+{{ item.additions }}</span>
        <span class="chs__del">−{{ item.deletions }}</span>
        <ChevronRight
          :size="14"
          class="text-muted"
          aria-hidden="true"
        />
      </button>
    </template>
    <template v-else>
      <button
        type="button"
        class="chs__back"
        @click="picked = null"
      >
        <ChevronLeft
          :size="16"
          aria-hidden="true"
        /> Changes
      </button>
      <h2 class="chs__file">
        {{ picked.file }}
      </h2>
      <p
        v-if="failed"
        class="chs__note"
      >
        {{ failed }}
      </p>
      <p
        v-else-if="!lines"
        class="chs__note"
      >
        <LoaderCircle
          class="inline animate-spin"
          :size="14"
        />
      </p>
      <pre
        v-else
        class="chs__diff"
        data-testid="phone-diff"
      ><template
        v-for="entry in shown"
        :key="entry.index"
      ><span
        v-if="entry.gap"
        class="chs__gap"
      >⋯
</span><span :class="`chs__line chs__line--${entry.line.type}`">{{ entry.line.type === "add" ? "+" : entry.line.type === "remove" ? "−" : " " }} {{ entry.line.content }}
</span></template></pre>
    </template>
  </BottomSheet>
</template>

<style scoped>
.chs__title {
  margin-bottom: 8px;
  font-size: 15px;
  font-weight: 600;
}

.chs__count,
.chs__note {
  font-size: 13px;
  font-weight: 400;
  color: var(--muted);
}

.chs__row {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  min-height: 44px;
  border: 0;
  border-bottom: 1px solid var(--border);
  background: transparent;
  color: var(--text);
  font: inherit;
  text-align: left;
}

.chs__path {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  font-family: var(--font-mono-stack);
  font-size: 12px;
  white-space: nowrap;
  text-overflow: ellipsis;
  direction: rtl;
  text-align: left;
}

.chs__add {
  font-size: 12px;
  color: var(--diff-add);
}

.chs__del {
  font-size: 12px;
  color: var(--diff-del);
}

.chs__back {
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

.chs__file {
  margin: 4px 0 8px;
  font-family: var(--font-mono-stack);
  font-size: 12px;
  overflow-wrap: anywhere;
}

.chs__diff {
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

.chs__line--add {
  color: var(--diff-add);
}

.chs__line--remove {
  color: var(--diff-del);
}

.chs__line--context {
  color: var(--muted);
}

.chs__gap {
  color: var(--muted);
}
</style>
