<script setup lang="ts">
import { FileText } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import type { FoldedTurn } from "@/lib/turn-fold";

/** What a turn changed, after its answer: a row per file, each opening that file's diff, and Open diff for all. */
defineProps<{
  turn: FoldedTurn;
}>();

const emit = defineEmits<{
  "open-diff": [];
  "open-file": [path: string];
}>();

function filesLabel(count: number): string {
  return `${count} ${count === 1 ? "file" : "files"} changed`;
}
</script>

<template>
  <section
    class="changed-files"
    data-testid="changed-files-card"
    aria-label="Files this turn changed"
  >
    <header class="changed-files__head">
      <span class="changed-files__title">{{ filesLabel(turn.files.length) }}</span>
      <span class="changed-files__counts">
        <span class="changed-files__adds">+{{ turn.additions }}</span>
        <span class="changed-files__removes">−{{ turn.deletions }}</span>
      </span>
      <Button
        variant="outline"
        size="sm"
        class="changed-files__open"
        data-testid="changed-files-open-diff"
        @click="emit('open-diff')"
      >
        Open diff
      </Button>
    </header>
    <button
      v-for="file in turn.files"
      :key="file.path"
      type="button"
      class="changed-files__row"
      data-testid="changed-files-row"
      :title="file.path"
      @click="emit('open-file', file.path)"
    >
      <FileText
        class="changed-files__icon"
        aria-hidden="true"
      />
      <span class="changed-files__path">{{ file.path }}</span>
      <span class="changed-files__counts">
        <span class="changed-files__adds">+{{ file.additions }}</span>
        <span class="changed-files__removes">−{{ file.deletions }}</span>
      </span>
    </button>
  </section>
</template>

<style scoped>
.changed-files {
  box-sizing: border-box;
  width: 100%;
  margin: 10px 0 4px;
  border: 1px solid var(--border);
  border-radius: 10px;
  background: var(--panel-bg);
  overflow: hidden;
}

.changed-files__head {
  display: flex;
  align-items: center;
  gap: 10px;
  min-height: 38px;
  padding: 4px 8px 4px 12px;
  font-size: 13px;
}

.changed-files__title {
  font-weight: 500;
  color: var(--text);
}

.changed-files__open {
  margin-left: auto;
}

.changed-files__counts {
  display: inline-flex;
  gap: 6px;
  margin-left: 0;
  font-family: var(--font-mono-stack);
  font-size: 12px;
  font-variant-numeric: tabular-nums;
}

.changed-files__head .changed-files__counts {
  margin-right: auto;
}

.changed-files__adds {
  color: var(--running);
}

.changed-files__removes {
  color: var(--error);
}

.changed-files__row {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  min-height: 32px;
  padding: 0 12px;
  border: 0;
  border-top: 1px solid var(--border);
  background: transparent;
  color: var(--text);
  font: inherit;
  font-size: 13px;
  text-align: left;
  cursor: pointer;
  transition: background var(--transition);
}

.changed-files__row:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
}

.changed-files__row:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.changed-files__icon {
  width: 14px;
  height: 14px;
  flex-shrink: 0;
  color: var(--muted);
}

.changed-files__path {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-family: var(--font-mono-stack);
  font-size: 12px;
}
</style>
