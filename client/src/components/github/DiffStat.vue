<script setup lang="ts">
/** Lines added and removed: "+412 −88". */
defineProps<{
  additions: number;
  deletions: number;
}>();
</script>

<template>
  <span
    class="diff-stat"
    :title="`${additions} lines added, ${deletions} removed`"
  >
    <span class="diff-stat__add">+{{ additions }}</span>
    <span class="diff-stat__del">−{{ deletions }}</span>
  </span>
</template>

<style scoped>
.diff-stat {
  display: inline-flex;
  gap: 4px;
  flex-shrink: 0;
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}

.diff-stat__add { color: var(--diff-add); }
.diff-stat__del { color: var(--diff-del); }
</style>
