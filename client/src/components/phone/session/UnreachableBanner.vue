<script setup lang="ts">
/** "Can't reach hangar. Trying again in 4 s." while the machine is away. Tapping tries now. */
defineProps<{ machineName: string; retryIn: number }>();
const emit = defineEmits<{ (event: "retry"): void }>();
</script>

<template>
  <button
    type="button"
    class="ph-banner ph-banner--bad ub"
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
  width: auto;
  margin: 0;
  font-weight: 500;
}
</style>
