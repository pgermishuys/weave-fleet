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
  <section class="token-grid" aria-label="Token usage">
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
  gap: 8px;
  margin-bottom: 16px;
}

.token-card {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
  background: var(--card-bg);
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  padding: 10px;
}

.token-card__label {
  margin: 0;
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.04em;
  text-transform: uppercase;
  color: var(--muted);
}

.token-card__value {
  margin: 0;
  font-size: 18px;
  font-weight: 600;
  color: var(--text);
}

.token-card__helper {
  margin: 0;
  font-size: 11px;
  line-height: 1.4;
  color: var(--muted);
}
</style>
