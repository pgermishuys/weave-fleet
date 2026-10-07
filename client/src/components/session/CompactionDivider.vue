<script setup lang="ts">
import { computed, ref } from "vue";
import { sharedMarkdownRenderer } from "@/lib/markdown-renderer";
import { describeCompaction, type CompactionView } from "@/lib/compaction";

const props = defineProps<{
  compaction: CompactionView;
}>();

const markdownRenderer = sharedMarkdownRenderer();
const open = ref(false);

const label = computed(() => describeCompaction(props.compaction));
const title = computed(() => {
  switch (props.compaction.trigger) {
    case "auto":
      return "The harness compacted the conversation because its context was nearly full. The model goes on from the summary.";
    case "manual":
      return "Compacted on request. The model goes on from the summary.";
    default:
      return "The model goes on from a summary of the conversation before this point.";
  }
});
const summaryHtml = computed(() => (open.value && props.compaction.summary ? markdownRenderer.render(props.compaction.summary) : ""));
</script>

<template>
  <!-- Where the harness compacted the conversation: everything above reaches the model only as the summary. -->
  <div
    class="compaction-divider"
    role="separator"
    :aria-label="label"
    data-testid="compaction-divider"
  >
    <div class="compaction-divider__line">
      <span
        class="compaction-divider__label"
        :title="title"
      >
        {{ label }}
        <template v-if="compaction.summary">
          ·
          <button
            type="button"
            class="compaction-divider__toggle"
            :aria-expanded="open"
            data-testid="compaction-summary-toggle"
            @click="open = !open"
          >
            {{ open ? "Hide summary" : "Show summary" }}
          </button>
        </template>
      </span>
    </div>
    <!-- eslint-disable vue/no-v-html -->
    <div
      v-if="open && compaction.summary"
      class="compaction-divider__summary md-content"
      data-testid="compaction-summary"
      v-html="summaryHtml"
    />
    <!-- eslint-enable vue/no-v-html -->
  </div>
</template>

<style scoped>
.compaction-divider {
  width: var(--activity-bubble-width, 100%);
  box-sizing: border-box;
  margin: 10px 0;
}

.compaction-divider__line {
  display: flex;
  align-items: center;
  gap: 12px;
  color: var(--muted);
  font-size: 12.5px;
  line-height: 1.4;
}

.compaction-divider__line::before,
.compaction-divider__line::after {
  content: "";
  flex: 1;
  height: 1px;
  background: var(--border);
}

.compaction-divider__label {
  flex-shrink: 0;
  max-width: 80%;
  text-align: center;
}

.compaction-divider__toggle {
  padding: 0;
  border: 0;
  background: none;
  color: var(--accent);
  font: inherit;
  cursor: pointer;
}

.compaction-divider__toggle:hover {
  text-decoration: underline;
}

.compaction-divider__toggle:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
  border-radius: 3px;
}

.compaction-divider__summary {
  margin: 10px auto 0;
  max-width: 680px;
  padding: 10px 14px;
  border: 1px solid var(--border);
  border-radius: 8px;
  color: var(--muted);
  font-size: 13px;
  line-height: 1.55;
  max-height: 360px;
  overflow: auto;
}
</style>
