<script setup lang="ts">
import { computed, ref } from "vue";
import { Brain } from "lucide-vue-next";

const props = defineProps<{
  text: string;
  summary?: string;
}>();

const isCollapsed = ref(true);

const previewText = computed(() => {
  if (props.summary) {
    return props.summary;
  }
  // Truncate to ~100 chars if no summary
  const truncated = props.text.slice(0, 100);
  return props.text.length > 100 ? `${truncated}...` : truncated;
});

const fullText = computed(() => props.text);

function handleToggle(event: Event): void {
  const target = event.target as HTMLDetailsElement;
  isCollapsed.value = !target.open;
}
</script>

<template>
  <details
    class="reasoning-block"
    data-testid="reasoning-block"
    :open="false"
    @toggle="handleToggle"
  >
    <summary class="reasoning-header">
      <Brain class="reasoning-header__icon" aria-hidden="true" />
      <span class="reasoning-header__label">Reasoning</span>
      <span class="reasoning-header__preview">{{ previewText }}</span>
    </summary>

    <div class="reasoning-body">
      <p class="reasoning-text">{{ fullText }}</p>
    </div>
  </details>
</template>

<style scoped>
.reasoning-block {
  margin: 8px 0;
  border: 1px solid var(--border);
  border-radius: 0;
  background: var(--surface);
  overflow: hidden;
}

.reasoning-header {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px 12px;
  cursor: pointer;
  user-select: none;
  list-style: none;
  transition: background var(--transition);
}

.reasoning-header::-webkit-details-marker {
  display: none;
}

.reasoning-header:hover {
  background: var(--bg);
}

.reasoning-header__icon {
  width: 14px;
  height: 14px;
  color: var(--indigo);
  flex-shrink: 0;
}

.reasoning-header__label {
  font-size: 12px;
  font-weight: 500;
  color: var(--muted);
  text-transform: uppercase;
  letter-spacing: 0.5px;
  flex-shrink: 0;
}

.reasoning-header__preview {
  font-size: 13px;
  color: var(--text-secondary);
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.reasoning-body {
  padding: 12px;
  border-top: 1px solid var(--border);
  background: rgba(99, 102, 241, 0.03);
}

.reasoning-text {
  font-size: 13px;
  line-height: 1.6;
  color: var(--text);
  white-space: pre-wrap;
  word-wrap: break-word;
  margin: 0;
}
</style>
