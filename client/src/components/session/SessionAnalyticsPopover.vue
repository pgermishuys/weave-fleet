<script setup lang="ts">
import { computed } from "vue";
import { ChartNoAxesColumn } from "lucide-vue-next";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";

const props = defineProps<{
  totalTokens?: number | null;
  totalCost?: number | null;
}>();

const hasMetrics = computed(() => props.totalTokens != null || props.totalCost != null);
const metrics = computed(() => [
  {
    id: "tokens",
    label: "Total tokens",
    value: formatNumber(props.totalTokens ?? null),
    helper: "Across this session",
  },
  {
    id: "cost",
    label: "Total cost",
    value: formatCurrency(props.totalCost ?? null),
    helper: "Estimated spend",
  },
]);

function formatNumber(value: number | null): string {
  if (value === null) {
    return "—";
  }

  return value.toLocaleString();
}

function formatCurrency(amount: number | null): string {
  if (amount === null) {
    return "—";
  }

  return new Intl.NumberFormat("en-US", {
    style: "currency",
    currency: "USD",
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  }).format(amount);
}
</script>

<template>
  <Popover>
    <PopoverTrigger as-child>
      <button
        type="button"
        class="analytics-trigger"
        :class="{ 'analytics-trigger--active': hasMetrics }"
        aria-label="Show session analytics"
        title="Session analytics"
      >
        <ChartNoAxesColumn
          class="analytics-trigger__icon"
          aria-hidden="true"
        />
      </button>
    </PopoverTrigger>

    <PopoverContent
      side="bottom"
      align="end"
      class="analytics-popover"
    >
      <div class="analytics-popover__header">
        <p class="analytics-popover__eyebrow">
          Session analytics
        </p>
        <p class="analytics-popover__title">
          Usage summary
        </p>
      </div>

      <dl class="analytics-popover__metrics">
        <div
          v-for="metric in metrics"
          :key="metric.id"
          class="analytics-popover__metric"
        >
          <dt class="analytics-popover__label">
            {{ metric.label }}
          </dt>
          <dd class="analytics-popover__value">
            {{ metric.value }}
          </dd>
          <dd class="analytics-popover__helper">
            {{ metric.helper }}
          </dd>
        </div>
      </dl>
    </PopoverContent>
  </Popover>
</template>

<style scoped>
.analytics-trigger {
  display: inline-flex;
  width: 24px;
  height: 24px;
  flex-shrink: 0;
  align-items: center;
  justify-content: center;
  border: 1px solid color-mix(in srgb, var(--border) 86%, transparent);
  border-radius: 6px;
  background: transparent;
  color: var(--muted-foreground, var(--muted));
  cursor: pointer;
  transition: border-color 0.15s ease, background-color 0.15s ease, color 0.15s ease;
}

.analytics-trigger:hover,
.analytics-trigger--active {
  border-color: color-mix(in srgb, var(--border) 72%, var(--foreground, var(--text)) 28%);
  color: var(--foreground, var(--text));
}

.analytics-trigger:hover {
  background: rgba(255, 255, 255, 0.1);
}

.analytics-trigger:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.analytics-trigger__icon {
  width: 12px;
  height: 12px;
}

:deep(.analytics-popover) {
  width: 240px;
  padding: 0;
  overflow: hidden;
  background: var(--popover, var(--panel-bg));
  color: var(--popover-foreground, var(--text));
}

.analytics-popover__header {
  border-bottom: 1px solid var(--border);
  padding: 0.65rem 0.75rem 0.55rem;
}

.analytics-popover__eyebrow,
.analytics-popover__title,
.analytics-popover__label,
.analytics-popover__value,
.analytics-popover__helper {
  margin: 0;
}

.analytics-popover__eyebrow {
  font-size: 9px;
  font-weight: 700;
  letter-spacing: 0.07em;
  line-height: 1.2;
  text-transform: uppercase;
  color: var(--muted-foreground, var(--muted));
}

.analytics-popover__title {
  margin-top: 0.15rem;
  font-size: 0.86rem;
  font-weight: 650;
  line-height: 1.25;
  color: var(--foreground, var(--text));
}

.analytics-popover__metrics {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 1px;
  margin: 0;
  background: var(--border);
}

.analytics-popover__metric {
  min-width: 0;
  background: var(--popover, var(--panel-bg));
  padding: 0.65rem 0.75rem;
}

.analytics-popover__label {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 9px;
  font-weight: 650;
  letter-spacing: 0.045em;
  line-height: 1.2;
  text-transform: uppercase;
  color: var(--muted-foreground, var(--muted));
}

.analytics-popover__value {
  margin-top: 0.2rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 1rem;
  font-weight: 700;
  line-height: 1.2;
  color: var(--foreground, var(--text));
}

.analytics-popover__helper {
  margin-top: 0.15rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 9px;
  line-height: 1.25;
  color: var(--muted-foreground, var(--muted));
}
</style>
