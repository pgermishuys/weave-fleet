<script setup lang="ts">
import { computed } from "vue";
import { Github, Link2 } from "lucide-vue-next";
import type { SessionOrigin } from "@/lib/api-types";

interface Props {
  origin?: SessionOrigin | null;
  compact?: boolean;
}

const props = defineProps<Props>();

const href = computed(() => props.origin?.resourceUrl?.trim() || null);
const compactLabel = computed(() => {
  const resourceId = props.origin?.resourceId?.trim() ?? "";
  const numberMatch = resourceId.match(/#(\d+)$/);
  if (numberMatch) {
    return props.origin?.sourceType === "github-pull-request"
      ? `PR #${numberMatch[1]}`
      : `#${numberMatch[1]}`;
  }

  return resourceId || props.origin?.title?.trim() || href.value;
});
const defaultLabel = computed(() =>
  props.origin?.title?.trim()
  || props.origin?.resourceId?.trim()
  || href.value,
);
const label = computed(() => (props.compact ? compactLabel.value : defaultLabel.value));
const iconComponent = computed(() => (props.origin?.providerId === "builtin.github" ? Github : Link2));
const shouldRender = computed(() => Boolean(props.origin && href.value && label.value));
const className = computed(() => (props.compact
  ? "origin-badge origin-badge--compact"
  : "origin-badge origin-badge--default"));
</script>

<template>
  <a
    v-if="shouldRender"
    :href="href ?? undefined"
    target="_blank"
    rel="noreferrer noopener"
    :class="className"
  >
    <component
      :is="iconComponent"
      :size="12"
      aria-hidden="true"
      class="shrink-0"
    />
    <span class="truncate">{{ label }}</span>
  </a>
</template>

<style scoped>
.origin-badge {
  display: inline-flex;
  min-width: 0;
  max-width: 100%;
  align-items: center;
  gap: 0.375rem;
  color: var(--muted-foreground, var(--muted));
  text-decoration: none;
  transition: color 0.15s ease, background-color 0.15s ease, border-color 0.15s ease;
}

.origin-badge:hover {
  color: var(--foreground, var(--text));
}

.origin-badge--default {
  border: 1px solid var(--border);
  border-radius: 0.375rem;
  background: color-mix(in srgb, var(--muted) 40%, transparent);
  padding: 0.25rem 0.5rem;
  font-size: 0.75rem;
}

.origin-badge--default:hover {
  background: color-mix(in srgb, var(--muted) 60%, transparent);
}

.origin-badge--compact {
  font-size: 0.6875rem;
  line-height: 1;
  gap: 0.25rem;
}

.origin-badge--compact :deep(svg) {
  width: 0.625rem;
  height: 0.625rem;
  opacity: 0.7;
}
</style>
