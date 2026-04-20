<script setup lang="ts">
interface DiffLine {
  type: "add" | "remove" | "context";
  content: string;
  oldLineNumber?: number;
  newLineNumber?: number;
}

defineProps<{
  lines: DiffLine[];
}>();
</script>

<template>
  <div
    class="diff-view"
    aria-label="Diff view"
  >
    <div
      v-for="(line, index) in lines"
      :key="`${index}-${line.content}`"
      class="diff-line"
      :class="{
        'diff-add': line.type === 'add',
        'diff-remove': line.type === 'remove',
        'diff-context': line.type === 'context',
      }"
    >
      <span class="diff-line__number">{{ line.oldLineNumber ?? "" }}</span>
      <span class="diff-line__number">{{ line.newLineNumber ?? "" }}</span>
      <span class="diff-line__content">{{ line.content }}</span>
    </div>
  </div>
</template>

<style scoped>
.diff-view {
  border-top: 1px solid rgba(255, 255, 255, 0.04);
}

.diff-line {
  display: grid;
  grid-template-columns: 40px 40px minmax(0, 1fr);
  align-items: start;
  gap: 10px;
  padding: 2px 12px;
  font-family: ui-monospace, SFMono-Regular, SFMono-Regular, Consolas, "Liberation Mono", Menlo, monospace;
  font-size: 11px;
  line-height: 1.6;
  white-space: pre-wrap;
  word-break: break-word;
}

.diff-line__number {
  color: rgba(161, 161, 170, 0.8);
  text-align: right;
  user-select: none;
}

.diff-line__content {
  min-width: 0;
}

.diff-add {
  background: rgba(34, 197, 94, 0.08);
  color: #4ade80;
}

.diff-remove {
  background: rgba(239, 68, 68, 0.08);
  color: #f87171;
}

.diff-context {
  color: var(--muted);
}
</style>
