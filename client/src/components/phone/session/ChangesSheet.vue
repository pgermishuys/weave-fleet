<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { ArrowLeft, ChevronLeft, ChevronRight, LoaderCircle } from "lucide-vue-next";
import { phoneLook } from "@/composables/phone/use-phone-env";
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
        class="ph-navbtn ph-glass"
        aria-label="Back to the changes"
        @click="picked = null"
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
      <h2>{{ fileName(picked.file) }}</h2>
    </template>

    <template v-if="!picked">
      <p
        v-if="loading && diffs.length === 0"
        class="ph-group-f chs__note"
      >
        <LoaderCircle
          class="ph-spinner chs__spin"
          :size="14"
        /> Reading the changes…
      </p>
      <p
        v-else-if="diffs.length === 0"
        class="ph-group-f chs__note"
      >
        Nothing changed yet.
      </p>
      <div
        v-else
        class="ph-group"
      >
        <button
          v-for="item in diffs"
          :key="item.file"
          type="button"
          class="ph-row"
          data-testid="phone-change"
          @click="openFile(item)"
        >
          <span class="ph-row__main">
            <span class="ph-row__title chs__name">{{ fileName(item.file) }}</span>
            <span
              v-if="folderOf(item.file)"
              class="ph-row__sub"
            >{{ folderOf(item.file) }}</span>
          </span>
          <span class="chs__counts"><span class="chs__add">+{{ item.additions }}</span> <span class="chs__del">−{{ item.deletions }}</span></span>
          <ChevronRight
            class="ph-row__chev"
            :size="16"
            :stroke-width="3"
            aria-hidden="true"
          />
        </button>
      </div>
      <p
        v-if="diffs.length"
        class="ph-group-f"
      >
        {{ diffs.length }} file{{ diffs.length === 1 ? "" : "s" }} changed in this session's folder.
      </p>
    </template>
    <div
      v-else
      class="ph-sheet__pad"
    >
      <p class="chs__path">
        {{ picked.file }}
      </p>
      <p
        v-if="failed"
        class="ph-note chs__note"
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
      <pre
        v-else
        class="ph-code chs__diff"
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
    </div>
  </BottomSheet>
</template>

<style scoped>
.chs__note {
  display: flex;
  align-items: center;
  gap: 8px;
}

.chs__spin {
  display: inline;
}

.chs__name {
  font-family: var(--ph-mono);
  font-size: 0.85rem;
}

.chs__counts {
  flex: none;
  font-family: var(--ph-mono);
  font-size: 0.8rem;
}

.chs__add,
.chs__line--add {
  color: var(--diff-add);
}

.chs__del,
.chs__line--remove {
  color: var(--diff-del);
}

.chs__path {
  margin: 0 0 10px;
  font-family: var(--ph-mono);
  font-size: 0.8rem;
  color: var(--muted);
  overflow-wrap: anywhere;
}

.chs__loading {
  display: grid;
  min-height: 120px;
  place-items: center;
}

.chs__diff {
  overflow-x: auto;
  font-size: 0.72rem;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.chs__line--context,
.chs__gap {
  color: var(--muted);
}
</style>
