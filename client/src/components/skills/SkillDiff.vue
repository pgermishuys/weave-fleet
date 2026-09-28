<script setup lang="ts">
import { computed } from "vue";
import DiffView from "@/components/session/DiffView.vue";
import { parseDiffLines } from "@/lib/diff-parser";

/** Two versions of a skill's SKILL.md, as a diff with the unchanged runs folded away. */
const props = defineProps<{
  before: string;
  after: string;
  /** What the header says on the left, e.g. "fleet-code-review/SKILL.md". */
  label: string;
}>();

const lines = computed(() => parseDiffLines(props.before, props.after));
const stats = computed(() => {
  let adds = 0;
  let removes = 0;
  for (const line of lines.value) {
    if (line.type === "add") adds += 1;
    else if (line.type === "remove") removes += 1;
  }
  return { adds, removes };
});
</script>

<template>
  <div
    class="skill-diff"
    data-testid="skill-diff"
  >
    <div class="skill-diff__head">
      <span class="skill-diff__label">{{ label }}</span>
      <span
        v-if="stats.adds + stats.removes > 0"
        class="skill-diff__stats"
      >
        <span class="skill-diff__adds">+{{ stats.adds }}</span>
        <span class="skill-diff__removes">−{{ stats.removes }}</span>
      </span>
    </div>
    <DiffView
      v-if="stats.adds + stats.removes > 0"
      :lines="lines"
    />
    <p
      v-else
      class="skill-diff__same"
    >
      No differences.
    </p>
  </div>
</template>

<style scoped>
.skill-diff {
  overflow: hidden;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--main-bg);
}

.skill-diff__head {
  display: flex;
  justify-content: space-between;
  gap: 8px;
  padding: 6px 10px;
  border-bottom: 1px solid var(--border);
  color: var(--muted);
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
}

.skill-diff__label {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.skill-diff__stats {
  display: inline-flex;
  gap: 6px;
  font-variant-numeric: tabular-nums;
}

.skill-diff__adds {
  color: var(--diff-add);
}

.skill-diff__removes {
  color: var(--diff-del);
}

.skill-diff__same {
  padding: 10px;
  color: var(--muted);
  font-size: 13px;
}

.skill-diff :deep(.diff-view) {
  max-height: 340px;
  overflow: auto;
}

/* Skills are prose: wrap long lines instead of scrolling sideways. */
.skill-diff :deep(.diff-line__content) {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}
</style>
