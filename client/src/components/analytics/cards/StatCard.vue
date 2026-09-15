<script setup lang="ts">
interface Props {
  label: string;
  value: number | string;
  secondary?: string;
  detail?: string;
}

withDefaults(defineProps<Props>(), {
  secondary: "",
  detail: "",
});
</script>

<template>
  <div class="stat-card">
    <p class="stat-card__label">
      {{ label }}
    </p>
    <p class="stat-card__value">
      {{ value }}
      <span
        v-if="secondary"
        class="stat-card__secondary"
      >{{ secondary }}</span>
    </p>
    <p
      v-if="detail"
      class="stat-card__detail"
    >
      {{ detail }}
    </p>
  </div>
</template>

<style scoped>
.stat-card {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
  padding: 14px 16px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
}

.stat-card__label {
  margin: 0;
  color: var(--muted);
  font-size: 12px;
  font-weight: 500;
}

.stat-card__value {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 4px 8px;
  margin: 0;
  color: var(--text);
  font-size: 24px;
  font-weight: 600;
  letter-spacing: -0.01em;
  line-height: 1.2;
  font-variant-numeric: tabular-nums;
}

.stat-card__secondary {
  color: var(--muted);
  font-size: 12px;
  font-weight: 500;
  letter-spacing: 0;
}

.stat-card__detail {
  margin: 0;
  color: var(--muted);
  font-size: 12px;
}
</style>
