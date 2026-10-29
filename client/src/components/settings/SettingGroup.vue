<script setup lang="ts">
/** A titled group of settings: a heading, an optional hint on the right, and the rows in one bordered card. */
defineProps<{
  /** Stable id, so a link or settings search can reach the group: `settings-group-<id>`. */
  id: string;
  title: string;
  hint?: string;
}>();
</script>

<template>
  <section
    :id="`settings-group-${id}`"
    class="scroll-mt-4"
    :data-setting-group="id"
  >
    <div class="mb-2 flex items-baseline justify-between gap-3 px-1">
      <h2 class="text-sm font-semibold text-text">
        {{ title }}
      </h2>
      <p
        v-if="hint"
        class="text-xs text-muted"
      >
        {{ hint }}
      </p>
    </div>
    <div class="divide-y divide-border overflow-hidden rounded-card border border-border bg-card-bg">
      <slot />
    </div>
  </section>
</template>
