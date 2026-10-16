<script setup lang="ts">
/** The agent waiting for a pick on a page it showed, docked like a question: Open page shows it full width. */
defineProps<{ title: string }>();
const emit = defineEmits<{ (event: "open"): void }>();
</script>

<template>
  <div
    class="ph-pcard ph-docked dpp"
    data-testid="docked-page-pick"
  >
    <div class="dpp__text">
      <span class="ph-pcard__title">Pick on the page</span>
      <p class="dpp__title">
        {{ title }}
      </p>
    </div>
    <button
      type="button"
      class="ph-btn ph-btn--primary"
      data-testid="docked-page-open"
      @click="emit('open')"
    >
      Open page
    </button>
  </div>
</template>

<style scoped>
.dpp {
  display: flex;
  align-items: center;
  gap: 12px;
}

.dpp__text {
  flex: 1;
  min-width: 0;
}

.dpp__title {
  margin: 2px 0 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 13px;
  color: var(--muted);
}
</style>
