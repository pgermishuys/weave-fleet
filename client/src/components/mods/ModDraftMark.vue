<script setup lang="ts">
/** Marks what a draft mod drew (a band, a chip, a pane's tab): the mod runs only in this session until it's kept. */
</script>

<template>
  <span
    class="mod-draft-mark"
    data-testid="mod-draft-mark"
    title="Drawn by a draft mod: only this session runs it until you keep it"
  >Draft</span>
</template>

<style scoped>
.mod-draft-mark {
  flex-shrink: 0;
  padding: 0 5px;
  border: 1px dashed color-mix(in srgb, var(--accent) 55%, transparent);
  border-radius: 4px;
  color: var(--accent);
  font-size: 10px;
  font-weight: 600;
  line-height: 15px;
  letter-spacing: 0.02em;
  text-transform: uppercase;
  white-space: nowrap;
}
</style>
