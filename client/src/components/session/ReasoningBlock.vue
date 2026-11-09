<script setup lang="ts">
import { computed, shallowRef, useId } from "vue";
import { Brain, ChevronRight } from "lucide-vue-next";
import { sharedMarkdownRenderer } from "@/lib/markdown-renderer";
import { reasoningGist } from "@/lib/reasoning-gist";
import { useThemeStore } from "@/stores/theme";

const props = defineProps<{
  text: string;
  summary?: string;
  createdAt?: number;
  /** The model is still writing this block: the folded line follows its newest sentence. */
  live?: boolean;
}>();

const markdownRenderer = sharedMarkdownRenderer();
const themeStore = useThemeStore();
const bodyId = useId();
const isOpen = shallowRef(false);

const absoluteTime = computed(() => {
  if (!props.createdAt) return "";
  return new Date(props.createdAt).toLocaleString();
});

const summaryHtml = computed(() => props.summary ? markdownRenderer.renderInline(props.summary) : "");
const textHtml = computed(() => markdownRenderer.render(props.text));
const isFolded = computed(() => themeStore.thinking === "folded");
const gistHtml = computed(() => markdownRenderer.renderInline(reasoningGist(props.text, props.summary, props.live)));
</script>

<template>
  <article
    v-if="themeStore.thinking !== 'hidden' && (text.trim() || summary?.trim())"
    class="reasoning-row"
    :class="{ 'reasoning-row--folded': isFolded }"
    :title="absoluteTime || undefined"
    data-testid="reasoning-block"
  >
    <div class="reasoning-row__layout">
      <div class="reasoning-row__icon">
        <Brain class="reasoning-row__icon-svg" aria-hidden="true" />
      </div>

      <div class="reasoning-row__content">
        <button
          v-if="isFolded"
          type="button"
          class="reasoning-row__fold"
          :aria-expanded="isOpen"
          :aria-controls="bodyId"
          data-testid="reasoning-fold"
          @click="isOpen = !isOpen"
        >
          <ChevronRight
            class="reasoning-row__chevron"
            aria-hidden="true"
          />
          <span
            v-if="live || isOpen"
            class="reasoning-row__label"
            :class="{ 'reasoning-row__label--live': live }"
          >{{ live ? "Thinking…" : "Thinking" }}</span>
          <!-- eslint-disable-next-line vue/no-v-html -->
          <span
            v-if="!isOpen"
            class="reasoning-row__gist"
            data-testid="reasoning-gist"
            v-html="gistHtml"
          />
        </button>
        <div
          v-if="!isFolded || isOpen"
          :id="bodyId"
          class="reasoning-row__body"
        >
          <!-- eslint-disable-next-line vue/no-v-html -->
          <span
            v-if="summary"
            class="reasoning-row__summary"
            v-html="summaryHtml"
          />
          <!-- eslint-disable-next-line vue/no-v-html -->
          <div class="reasoning-row__text md-content" v-html="textHtml" />
        </div>
      </div>

    </div>
  </article>
</template>

<style scoped>
/* One quiet line: a small brain and the newest sentence, muted and italic. The time is in the tooltip. */
.reasoning-row {
  width: var(--activity-bubble-width, 100%);
  box-sizing: border-box;
  padding: 3px 8px;
  background: transparent;
}

.reasoning-row__layout {
  display: flex;
  gap: 9px;
  align-items: flex-start;
}

.reasoning-row__icon {
  flex-shrink: 0;
  width: 14px;
  height: 14px;
  display: flex;
  align-items: center;
  justify-content: center;
  margin-top: 8px;
}

.reasoning-row__icon-svg {
  width: 14px;
  height: 14px;
  color: color-mix(in srgb, var(--muted) 75%, transparent);
}

.reasoning-row__content {
  flex: 1;
  min-width: 0;
}

.reasoning-row__summary {
  font-size: 13px;
  line-height: 1.5;
  color: var(--text);
  font-weight: 500;
  margin: 0 0 4px 0;
}

.reasoning-row__text {
  font-size: 13px;
  line-height: 1.5;
  color: var(--text-secondary);
  font-style: italic;
  white-space: pre-wrap;
  word-wrap: break-word;
  margin: 0;
  /* Reasoning is secondary; keep its Markdown structure uncoloured. */
  --md-heading: currentColor;
  --md-heading-rule: transparent;
  --md-marker: var(--muted);
}

/* Folded: one line per block, opened on click. */
.reasoning-row--folded {
  padding-top: 0;
  padding-bottom: 0;
}

.reasoning-row--folded .reasoning-row__body {
  margin-top: 6px;
}

.reasoning-row__fold {
  display: flex;
  align-items: center;
  gap: 6px;
  max-width: 100%;
  min-width: 0;
  margin: 0 0 0 -4px;
  padding: 2px 6px 2px 4px;
  border: 0;
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--muted);
  font: inherit;
  font-size: 13px;
  line-height: 1.5;
  text-align: left;
  cursor: pointer;
  transition: background var(--transition), color var(--transition);
}

.reasoning-row__fold:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
  color: var(--text);
}

.reasoning-row__fold:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.reasoning-row__chevron {
  width: 12px;
  height: 12px;
  flex-shrink: 0;
  transition: transform var(--transition);
}

.reasoning-row__fold[aria-expanded="true"] .reasoning-row__chevron {
  transform: rotate(90deg);
}

.reasoning-row__label {
  flex-shrink: 0;
  font-weight: 600;
}

/* The same sweep as the Working indicator, while the model is still thinking. */
.reasoning-row__label--live {
  background: linear-gradient(
    90deg,
    var(--muted) 0%,
    var(--muted) 40%,
    var(--text) 50%,
    var(--muted) 60%,
    var(--muted) 100%
  );
  background-size: 250% 100%;
  background-clip: text;
  -webkit-background-clip: text;
  color: transparent;
  animation: reasoning-shimmer 2.2s linear infinite;
}

.reasoning-row__gist {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-style: italic;
}

@keyframes reasoning-shimmer {
  from { background-position: 100% 0; }
  to { background-position: -150% 0; }
}

@media (prefers-reduced-motion: reduce) {
  .reasoning-row__label--live {
    animation: none;
    background: none;
    color: var(--muted);
  }
}
</style>
