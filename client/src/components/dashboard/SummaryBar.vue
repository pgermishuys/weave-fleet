<script setup lang="ts">
import { computed, onMounted } from "vue";
import { ChevronRight } from "lucide-vue-next";
import { useRouter } from "@tanstack/vue-router";
import { useAnalyticsDaily } from "@/composables/use-analytics-daily";
import { useFleetSummary } from "@/composables/use-fleet-summary";

interface SummaryMetric {
  testId: string;
  label: string;
  value: string;
}

const SPARK_DAYS = 14;

const router = useRouter();
const { summary } = useFleetSummary();

const integerFormatter = new Intl.NumberFormat("en-US");
const compactFormatter = new Intl.NumberFormat("en-US", { notation: "compact", maximumFractionDigits: 1 });
const currencyFormatter = new Intl.NumberFormat("en-US", {
  style: "currency",
  currency: "USD",
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});
const dayFormatter = new Intl.DateTimeFormat("en-US", { month: "short", day: "numeric" });

const metrics = computed<SummaryMetric[]>(() => {
  const current = summary.value;
  return [
    { testId: "summary-active-count", label: "Working", value: integerFormatter.format(current?.activeSessions ?? 0) },
    { testId: "summary-idle-count", label: "Idle", value: integerFormatter.format(current?.idleSessions ?? 0) },
    { testId: "summary-queued-count", label: "Queued", value: integerFormatter.format(current?.queuedTasks ?? 0) },
    { testId: "summary-tokens-count", label: "Total tokens", value: compactFormatter.format(current?.totalTokens ?? 0) },
    { testId: "summary-cost-count", label: "Total spend", value: currencyFormatter.format(current?.totalCost ?? 0) },
  ];
});

// ─── Spend over the last two weeks ───────────────────────────────────────────

function isoDay(date: Date): string {
  return date.toISOString().split("T")[0];
}

const today = new Date();
const since = new Date(today);
since.setDate(today.getDate() - (SPARK_DAYS - 1));

const { daily, refetch } = useAnalyticsDaily({ from: isoDay(since), to: isoDay(today) }, 5 * 60_000);
onMounted(() => void refetch());

/** One bar per day, with the days that had no activity filled in. */
const days = computed(() => {
  const byDate = new Map(daily.value.map((point) => [point.date, point]));
  return Array.from({ length: SPARK_DAYS }, (_, index) => {
    const date = new Date(since);
    date.setDate(since.getDate() + index);
    const key = isoDay(date);
    return { key, label: dayFormatter.format(date), cost: byDate.get(key)?.cost ?? 0 };
  });
});

const spendTotal = computed(() => days.value.reduce((sum, day) => sum + day.cost, 0));
const spendMax = computed(() => Math.max(...days.value.map((day) => day.cost), 0));

function barHeight(cost: number): string {
  if (spendMax.value <= 0 || cost <= 0) return "2px";
  return `${Math.max(8, (cost / spendMax.value) * 100)}%`;
}

function openAnalytics(): void {
  void router.navigate({ to: "/analytics" });
}
</script>

<template>
  <section
    data-testid="summary-bar"
    class="summary-bar"
    aria-label="Fleet summary"
  >
    <dl class="summary-bar__metrics">
      <div
        v-for="metric in metrics"
        :key="metric.testId"
        class="summary-bar__metric"
      >
        <dt class="summary-bar__label">
          {{ metric.label }}
        </dt>
        <dd
          :data-testid="metric.testId"
          class="summary-bar__value"
        >
          {{ metric.value }}
        </dd>
      </div>
    </dl>

    <button
      type="button"
      class="summary-bar__spend"
      :aria-label="`Spend over the last ${SPARK_DAYS} days: ${currencyFormatter.format(spendTotal)}. Open Analytics`"
      @click="openAnalytics"
    >
      <span class="summary-bar__spend-copy">
        <span class="summary-bar__label">Last {{ SPARK_DAYS }} days</span>
        <span class="summary-bar__spend-total">{{ currencyFormatter.format(spendTotal) }}</span>
      </span>
      <span
        class="summary-bar__spark"
        aria-hidden="true"
      >
        <span
          v-for="(day, index) in days"
          :key="day.key"
          class="summary-bar__bar"
          :class="{ 'summary-bar__bar--today': index === days.length - 1 }"
          :style="{ height: barHeight(day.cost) }"
          :title="`${day.label}: ${currencyFormatter.format(day.cost)}`"
        />
      </span>
      <ChevronRight
        :size="14"
        class="summary-bar__chevron"
        aria-hidden="true"
      />
    </button>
  </section>
</template>

<style scoped>
.summary-bar {
  display: flex;
  align-items: stretch;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
}

.summary-bar__metrics {
  display: grid;
  flex: 1;
  grid-template-columns: repeat(5, minmax(0, 1fr));
  margin: 0;
}

.summary-bar__metric {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
  padding: 12px 16px;
}

.summary-bar__metric + .summary-bar__metric {
  border-left: 1px solid var(--border);
}

.summary-bar__label {
  color: var(--muted);
  font-size: 12px;
}

.summary-bar__value {
  margin: 0;
  overflow: hidden;
  color: var(--text);
  font-size: 20px;
  font-weight: 600;
  letter-spacing: -0.01em;
  font-variant-numeric: tabular-nums;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.summary-bar__spend {
  display: flex;
  flex-shrink: 0;
  align-items: center;
  gap: 14px;
  padding: 12px 12px 12px 16px;
  border: 0;
  border-left: 1px solid var(--border);
  border-radius: 0 var(--radius-card) var(--radius-card) 0;
  background: transparent;
  color: var(--text);
  text-align: left;
  cursor: pointer;
  transition: background-color var(--transition);
}

.summary-bar__spend:hover {
  background: color-mix(in srgb, var(--text) 4%, transparent);
}

.summary-bar__spend:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.summary-bar__spend-copy {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.summary-bar__spend-total {
  font-size: 20px;
  font-weight: 600;
  letter-spacing: -0.01em;
  font-variant-numeric: tabular-nums;
}

.summary-bar__spark {
  display: flex;
  align-items: flex-end;
  gap: 3px;
  height: 36px;
}

.summary-bar__bar {
  width: 6px;
  border-radius: 2px;
  background: color-mix(in srgb, var(--accent) 45%, transparent);
}

.summary-bar__bar--today {
  background: var(--accent);
}

.summary-bar__chevron {
  color: var(--muted);
}

@media (max-width: 900px) {
  .summary-bar {
    flex-direction: column;
  }

  .summary-bar__spend {
    justify-content: space-between;
    border-top: 1px solid var(--border);
    border-left: 0;
    border-radius: 0 0 var(--radius-card) var(--radius-card);
  }
}

@media (max-width: 560px) {
  .summary-bar__metrics {
    grid-template-columns: repeat(3, minmax(0, 1fr));
  }

  .summary-bar__metric:nth-child(4) {
    border-left: 0;
  }

  .summary-bar__metric:nth-child(n + 4) {
    border-top: 1px solid var(--border);
  }

  .summary-bar__metric:last-child {
    grid-column: span 2;
  }
}

@media (prefers-reduced-motion: reduce) {
  .summary-bar__spend {
    transition: none;
  }
}
</style>
