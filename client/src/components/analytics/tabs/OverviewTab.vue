<script setup lang="ts">
import { computed, nextTick, shallowRef, watch } from "vue"
import {
  CategoryScale,
  Chart as ChartJS,
  Filler,
  Legend,
  LineElement,
  LinearScale,
  PointElement,
  Tooltip,
  type ChartData,
  type ChartOptions,
} from "chart.js"
import { Line } from "vue-chartjs"
import StatCard from "@/components/analytics/cards/StatCard.vue"
import HorizontalCostBars from "@/components/analytics/charts/HorizontalCostBars.vue"
import type { AnalyticsSummary, DailyAnalytics, ModelAnalytics } from "@/api/client"
import type { AnalyticsProjectOption } from "@/composables/use-analytics-filters"
import { useThemeStore } from "@/stores/theme"

ChartJS.register(CategoryScale, Filler, Legend, LineElement, LinearScale, PointElement, Tooltip)

interface ChartPalette {
  tokens: string
  cost: string
  text: string
  muted: string
  grid: string
}

// Chart.js draws on a canvas, so it needs the theme's colours as values rather than CSS variables.
function readPalette(): ChartPalette {
  const style = getComputedStyle(document.documentElement)
  const token = (name: string, fallback: string) => style.getPropertyValue(name).trim() || fallback
  return {
    tokens: token("--accent", "#6366f1"),
    cost: token("--running", "#22c55e"),
    text: token("--text", "#e8e8ec"),
    muted: token("--muted", "#8e8e9a"),
    grid: token("--border", "rgba(255, 255, 255, 0.075)"),
  }
}

/** A theme colour (hex) made translucent; other formats are returned as they are. */
function withAlpha(color: string, alpha: number): string {
  const hex = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(color)?.[1]
  if (!hex) return color
  const full = hex.length === 3 ? [...hex].map((digit) => digit + digit).join("") : hex
  const [r, g, b] = [0, 2, 4].map((offset) => parseInt(full.slice(offset, offset + 2), 16))
  return `rgba(${r}, ${g}, ${b}, ${alpha})`
}

const themeStore = useThemeStore()
const palette = shallowRef<ChartPalette>(readPalette())

watch(() => themeStore.resolvedThemeId, () => {
  void nextTick(() => {
    palette.value = readPalette()
  })
})

interface CostBarItem {
  name: string
  cost: number
  maxCost: number
  detail?: string
}

interface SummaryCardItem {
  label: string
  value: string
  secondary?: string
  detail: string
}

interface Props {
  summary?: AnalyticsSummary | null
  daily?: readonly DailyAnalytics[]
  models?: readonly ModelAnalytics[]
  projects?: readonly AnalyticsProjectOption[]
  dailyEmptyMessage?: string
  modelsEmptyMessage?: string
  projectsEmptyMessage?: string
}

const props = withDefaults(defineProps<Props>(), {
  summary: null,
  daily: () => [],
  models: () => [],
  projects: () => [],
  dailyEmptyMessage: "No daily analytics available for the selected range.",
  modelsEmptyMessage: "No model cost data available for the selected range.",
  projectsEmptyMessage: "No project cost data available for the selected range.",
})

const compactNumberFormatter = new Intl.NumberFormat("en-US", {
  notation: "compact",
  maximumFractionDigits: 1,
})

const integerFormatter = new Intl.NumberFormat("en-US")

const totalTokens = computed(() => {
  if (props.summary) {
    return props.summary.totalTokens
  }

  return props.daily.reduce((sum, point) => sum + point.tokens, 0)
})

const totalCost = computed(() => {
  if (props.summary) {
    return props.summary.totalCost
  }

  return props.daily.reduce((sum, point) => sum + point.cost, 0)
})

const totalEstimatedCost = computed(() => {
  if (props.summary) {
    return props.summary.totalEstimatedCost
  }

  return props.daily.reduce((sum, point) => sum + point.estimatedCost, 0)
})

const totalSessions = computed(() => {
  if (props.summary) {
    return props.summary.sessionCount
  }

  return props.daily.reduce((sum, point) => sum + point.sessions, 0)
})

const totalMessages = computed(() => {
  if (props.summary) {
    return props.summary.messageCount
  }

  return props.daily.reduce((sum, point) => sum + point.messages, 0)
})

const averageDailyTokens = computed(() => {
  if (props.daily.length === 0) {
    return 0
  }

  return totalTokens.value / props.daily.length
})

const averageDailyCost = computed(() => {
  if (props.daily.length === 0) {
    return 0
  }

  return totalCost.value / props.daily.length
})

const summaryCards = computed<SummaryCardItem[]>(() => [
  {
    label: "Tokens",
    value: compactNumberFormatter.format(totalTokens.value),
    detail: props.daily.length > 0
      ? `${compactNumberFormatter.format(averageDailyTokens.value)} avg / day`
      : "No daily token breakdown available",
  },
  {
    label: "Cost",
    value: formatCurrency(totalCost.value),
    secondary: `Est. ${formatCurrency(totalEstimatedCost.value)}`,
    detail: props.daily.length > 0
      ? `${formatCurrency(averageDailyCost.value)} avg / day`
      : "No daily cost breakdown available",
  },
  {
    label: "Sessions",
    value: integerFormatter.format(totalSessions.value),
    detail: props.summary
      ? `${integerFormatter.format(totalMessages.value)} total messages`
      : "Derived from the daily activity trend",
  },
  {
    label: "Messages",
    value: compactNumberFormatter.format(totalMessages.value),
    detail: props.summary
      ? `${integerFormatter.format(totalSessions.value)} active sessions`
      : "Derived from the daily activity trend",
  },
])

const hasDailyData = computed(() => props.daily.length > 0)

const modelBarItems = computed<CostBarItem[]>(() => {
  if (props.models.length > 0) {
    const maxCost = getMaxCost(props.models.map((item) => item.cost))

    return props.models.map((model) => ({
      name: model.modelId,
      cost: model.cost,
      maxCost,
      detail: `Est. ${formatCurrency(model.estimatedCost)}`,
    }))
  }

  const summaryModels = props.summary?.topModels ?? []
  const maxCost = getMaxCost(summaryModels.map((item) => item.cost))

  return summaryModels.map((model) => ({
    name: model.name,
    cost: model.cost,
    maxCost,
  }))
})

const projectBarItems = computed<CostBarItem[]>(() => {
  const sourceItems = props.projects.length > 0
    ? props.projects
    : (props.summary?.topProjects ?? []).map((project) => ({
        id: project.name,
        name: project.name,
        tokens: project.tokens,
        cost: project.cost,
      }))

  const maxCost = getMaxCost(sourceItems.map((item) => item.cost))

  return sourceItems.map((project) => ({
    name: project.name,
    cost: project.cost,
    maxCost,
    detail: `${compactNumberFormatter.format(project.tokens)} tokens`,
  }))
})

const dailyTrendData = computed<ChartData<"line">>(() => ({
  labels: props.daily.map((point) => formatShortDate(point.date)),
  datasets: [
    {
      label: "Tokens",
      data: props.daily.map((point) => point.tokens),
      borderColor: palette.value.tokens,
      backgroundColor: withAlpha(palette.value.tokens, 0.14),
      fill: "origin",
      borderWidth: 2,
      pointRadius: 0,
      pointHoverRadius: 4,
      pointBackgroundColor: palette.value.tokens,
      tension: 0.35,
      yAxisID: "yTokens",
    },
    {
      label: "Cost",
      data: props.daily.map((point) => point.cost),
      borderColor: palette.value.cost,
      backgroundColor: palette.value.cost,
      borderWidth: 2,
      borderDash: [4, 3],
      pointRadius: 0,
      pointHoverRadius: 4,
      pointBackgroundColor: palette.value.cost,
      tension: 0.35,
      yAxisID: "yCost",
    },
  ],
}))

const dailyTrendOptions = computed<ChartOptions<"line">>(() => ({
  responsive: true,
  maintainAspectRatio: false,
  interaction: {
    mode: "index",
    intersect: false,
  },
  plugins: {
    legend: {
      position: "top",
      align: "end",
      labels: {
        color: palette.value.muted,
        usePointStyle: true,
        pointStyle: "line",
        boxWidth: 16,
        font: { size: 12 },
      },
    },
    tooltip: {
      callbacks: {
        label(context) {
          const value = Number(context.parsed.y ?? 0)

          if (context.dataset.label === "Cost") {
            return `Cost: ${formatCurrency(value)}`
          }

          return `Tokens: ${integerFormatter.format(value)}`
        },
        afterLabel(context) {
          if (context.dataset.label !== "Cost") {
            return []
          }

          const point = props.daily[context.dataIndex]
          if (!point) {
            return []
          }

          return [`Estimated: ${formatCurrency(point.estimatedCost)}`]
        },
      },
    },
  },
  scales: {
    x: {
      ticks: {
        color: palette.value.muted,
        maxRotation: 0,
        autoSkipPadding: 16,
        font: { size: 11 },
      },
      grid: {
        display: false,
      },
      border: {
        color: palette.value.grid,
      },
    },
    yTokens: {
      type: "linear",
      position: "left",
      beginAtZero: true,
      ticks: {
        color: palette.value.muted,
        font: { size: 11 },
        callback(value) {
          return compactNumberFormatter.format(Number(value))
        },
      },
      grid: {
        color: palette.value.grid,
      },
      border: {
        display: false,
      },
    },
    yCost: {
      type: "linear",
      position: "right",
      beginAtZero: true,
      ticks: {
        color: palette.value.muted,
        font: { size: 11 },
        callback(value) {
          return formatCurrency(Number(value))
        },
      },
      grid: {
        drawOnChartArea: false,
      },
      border: {
        display: false,
      },
    },
  },
}))

function formatCurrency(amount: number): string {
  if (!Number.isFinite(amount) || amount <= 0) {
    return "$0.00"
  }

  if (amount < 0.01) {
    return `$${amount.toFixed(3)}`
  }

  return `$${amount.toLocaleString("en-US", {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  })}`
}

function formatShortDate(date: string): string {
  return new Intl.DateTimeFormat("en-US", {
    month: "short",
    day: "numeric",
  }).format(new Date(`${date}T00:00:00`))
}

function getMaxCost(costs: readonly number[]): number {
  if (costs.length === 0) {
    return 0
  }

  return Math.max(...costs)
}
</script>

<template>
  <section
    class="overview-tab"
    aria-label="Overview analytics"
  >
    <div class="overview-tab__stats">
      <StatCard
        v-for="card in summaryCards"
        :key="card.label"
        :label="card.label"
        :value="card.value"
        :secondary="card.secondary"
        :detail="card.detail"
      />
    </div>

    <section
      data-testid="analytics-overview-daily-trend"
      class="overview-tab__panel"
      aria-label="Daily tokens and cost"
    >
      <header class="overview-tab__panel-head">
        <h2 class="overview-tab__panel-title">
          Daily tokens and cost
        </h2>
        <p class="overview-tab__panel-meta">
          Est. total {{ formatCurrency(totalEstimatedCost) }}
        </p>
      </header>

      <div
        v-if="hasDailyData"
        class="overview-tab__chart"
      >
        <Line
          :data="dailyTrendData"
          :options="dailyTrendOptions"
        />
      </div>

      <p
        v-else
        class="overview-tab__empty"
      >
        {{ dailyEmptyMessage }}
      </p>
    </section>

    <div class="overview-tab__rankings">
      <section
        class="overview-tab__panel"
        aria-label="Top models by cost"
      >
        <header class="overview-tab__panel-head">
          <h2 class="overview-tab__panel-title">
            Top models by cost
          </h2>
        </header>
        <HorizontalCostBars
          :items="modelBarItems"
          :empty-message="modelsEmptyMessage"
        />
      </section>

      <section
        class="overview-tab__panel"
        aria-label="Top projects by cost"
      >
        <header class="overview-tab__panel-head">
          <h2 class="overview-tab__panel-title">
            Top projects by cost
          </h2>
        </header>
        <HorizontalCostBars
          :items="projectBarItems"
          :empty-message="projectsEmptyMessage"
        />
      </section>
    </div>
  </section>
</template>

<style scoped>
.overview-tab {
  display: flex;
  flex-direction: column;
  gap: 12px;
  min-width: 0;
}

.overview-tab__stats {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(140px, 1fr));
  gap: 12px;
}

.overview-tab__rankings {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(min(100%, 320px), 1fr));
  gap: 12px;
}

.overview-tab__panel {
  display: flex;
  flex-direction: column;
  gap: 12px;
  min-width: 0;
  padding: 16px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
}

.overview-tab__panel-head {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 12px;
}

.overview-tab__panel-title {
  margin: 0;
  color: var(--text);
  font-size: 13px;
  font-weight: 600;
}

.overview-tab__panel-meta {
  margin: 0;
  color: var(--muted);
  font-size: 12px;
  font-variant-numeric: tabular-nums;
}

.overview-tab__chart {
  height: 300px;
}

.overview-tab__empty {
  display: grid;
  place-items: center;
  min-height: 240px;
  margin: 0;
  border: 1px dashed var(--border);
  border-radius: var(--radius-btn);
  color: var(--muted);
  font-size: 13px;
  text-align: center;
}
</style>