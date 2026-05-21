<script setup lang="ts">
interface TokenMetric {
  id: string;
  label: string;
  value: string;
  helper: string;
}

defineProps<{
  metrics: readonly TokenMetric[];
}>();
</script>

<template>
  <section
    class="token-grid"
    aria-label="Token usage"
  >
    <article
      v-for="metric in metrics"
      :key="metric.id"
      class="token-card"
    >
      <p class="token-card__label">
        {{ metric.label }}
      </p>
      <p class="token-card__value">
        {{ metric.value }}
      </p>
      <p class="token-card__helper">
        {{ metric.helper }}
      </p>
    </article>
  </section>
</template>

<style scoped>
.token-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 6px;
  margin-bottom: 8px;
}

.token-card {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
  background: var(--card-bg);
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  padding: 7px 8px;
}

.token-card__label {
  margin: 0;
  font-size: 9px;
  font-weight: 600;
  letter-spacing: 0.045em;
  line-height: 1.2;
  text-transform: uppercase;
  color: var(--muted);
}

.token-card__value {
  margin: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 15px;
  font-weight: 650;
  line-height: 1.2;
  color: var(--text);
}

.token-card__helper {
  margin: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 9px;
  line-height: 1.25;
  color: var(--muted);
}
</style>
