<script setup lang="ts">
import { computed } from "vue";
import { Brain } from "lucide-vue-next";
import { useTimeAgo } from "@vueuse/core";
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from "@/components/ui/tooltip";
import { createMarkdownRenderer } from "@/lib/markdown-renderer";

const props = defineProps<{
  text: string;
  summary?: string;
  createdAt?: number;
}>();

const markdownRenderer = createMarkdownRenderer();

const relativeTime = useTimeAgo(() => props.createdAt ? new Date(props.createdAt) : new Date());
const absoluteTime = computed(() => {
  if (!props.createdAt) return "";
  return new Date(props.createdAt).toLocaleString();
});

const summaryHtml = computed(() => props.summary ? markdownRenderer.renderInline(props.summary) : "");
const textHtml = computed(() => markdownRenderer.render(props.text));
</script>

<template>
  <article v-if="text.trim() || summary?.trim()" class="reasoning-row" data-testid="reasoning-block">
    <div class="reasoning-row__layout">
      <div class="reasoning-row__icon">
        <Brain class="reasoning-row__icon-svg" aria-hidden="true" />
      </div>

      <div class="reasoning-row__content">
        <!-- eslint-disable-next-line vue/no-v-html -->
        <span
          v-if="summary"
          class="reasoning-row__summary"
          v-html="summaryHtml"
        />
        <!-- eslint-disable-next-line vue/no-v-html -->
        <div class="reasoning-row__text md-content" v-html="textHtml" />
      </div>

      <TooltipProvider v-if="createdAt">
        <Tooltip>
          <TooltipTrigger as-child>
            <span class="reasoning-row__timestamp">{{ relativeTime }}</span>
          </TooltipTrigger>
          <TooltipContent side="top">
            {{ absoluteTime }}
          </TooltipContent>
        </Tooltip>
      </TooltipProvider>
    </div>
  </article>
</template>

<style scoped>
.reasoning-row {
  width: var(--activity-bubble-width, 100%);
  box-sizing: border-box;
  padding: 12px;
  border-bottom: 1px solid var(--border);
  border-left: 3px solid transparent;
  background: transparent;
}

.reasoning-row__layout {
  display: flex;
  gap: 12px;
  align-items: flex-start;
}

.reasoning-row__icon {
  flex-shrink: 0;
  width: 20px;
  height: 20px;
  display: flex;
  align-items: center;
  justify-content: center;
  margin-top: 2px;
}

.reasoning-row__icon-svg {
  width: 16px;
  height: 16px;
  color: var(--muted);
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
}

.reasoning-row__timestamp {
  flex-shrink: 0;
  font-size: 12px;
  color: var(--muted);
  margin-top: 2px;
}
</style>
