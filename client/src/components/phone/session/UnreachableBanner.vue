<script setup lang="ts">
/** "Can't reach hangar. Trying again in 4 s." while the machine is away. */
defineProps<{ machineName: string; retryIn: number }>();
const emit = defineEmits<{ (event: "retry"): void }>();
</script>

<template>
  <button
    type="button"
    class="ub"
    role="alert"
    data-testid="unreachable-banner"
    @click="emit('retry')"
  >
    Can't reach {{ machineName }}.
    <template v-if="retryIn > 0">
      Trying again in {{ retryIn }} s.
    </template>
    <template v-else>
      Trying again…
    </template>
  </button>
</template>

<style scoped>
.ub {
  flex: none;
  width: 100%;
  min-height: 36px;
  padding: 8px 14px;
  border: 0;
  background: color-mix(in srgb, var(--error) 12%, transparent);
  color: var(--error);
  font: inherit;
  font-size: 13px;
  font-weight: 500;
  text-align: left;
}
</style>
