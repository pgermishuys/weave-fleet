<script setup lang="ts">
import { Brain } from "lucide-vue-next";

defineProps<{
  text: string;
  summary?: string;
}>();
</script>

<template>
  <div class="reasoning-block" data-testid="reasoning-block">
    <Brain class="reasoning-block__icon" aria-hidden="true" />
    <p class="reasoning-block__text">{{ text }}</p>
  </div>
</template>

<style scoped>
.reasoning-block {
  display: flex;
  gap: 8px;
  margin: 4px 0;
  align-items: flex-start;
}

.reasoning-block__icon {
  width: 14px;
  height: 14px;
  color: var(--muted);
  flex-shrink: 0;
  margin-top: 3px;
}

.reasoning-block__text {
  font-size: 13px;
  line-height: 1.5;
  color: var(--text-secondary);
  font-style: italic;
  white-space: pre-wrap;
  word-wrap: break-word;
  margin: 0;
}
</style>
