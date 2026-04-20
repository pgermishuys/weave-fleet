<script setup lang="ts">
import { computed } from "vue"
import {
  CategoryScale,
  Chart as ChartJS,
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
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import type { AnalyticsSummary, DailyAnalytics, ModelAnalytics } from "@/lib/api-types"
import type { AnalyticsProjectOption } from "@/composables/use-analytics-filters"

ChartJS.register(CategoryScale, Legend, LineElement, LinearScale, PointElement, Tooltip)

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
      borderColor: "#8b5cf6",
      backgroundColor: "rgba(139, 92, 246, 0.18)",
      pointBackgroundColor: "#8b5cf6",
      pointBorderColor: "#8b5cf6",
      tension: 0.35,
      yAxisID: "yTokens",
    },
    {
      label: "Cost",
      data: props.daily.map((point) => point.cost),
      borderColor: "#22c55e",
      backgroundColor: "rgba(34, 197, 94, 0.18)",
      pointBackgroundColor: "#22c55e",
      pointBorderColor: "#22c55e",
      tension: 0.35,
      yAxisID: "yCost",
    },
  ],
}))

const dailyTrendOptions: ChartOptions<"line"> = {
  responsive: true,
  maintainAspectRatio: false,
  interaction: {
    mode: "index",
    intersect: false,
  },
  plugins: {
    legend: {
      position: "top",
      align: "start",
      labels: {
        color: "#e4e4e7",
        usePointStyle: true,
        boxWidth: 10,
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
        color: "#a1a1aa",
      },
      grid: {
        color: "rgba(255, 255, 255, 0.06)",
      },
    },
    yTokens: {
      type: "linear",
      position: "left",
      ticks: {
        color: "#a1a1aa",
        callback(value) {
          return compactNumberFormatter.format(Number(value))
        },
      },
      grid: {
        color: "rgba(255, 255, 255, 0.06)",
      },
      title: {
        display: true,
        text: "Tokens",
        color: "#a1a1aa",
      },
    },
    yCost: {
      type: "linear",
      position: "right",
      ticks: {
        color: "#a1a1aa",
        callback(value) {
          return formatCurrency(Number(value))
        },
      },
      grid: {
        drawOnChartArea: false,
      },
      title: {
        display: true,
        text: "Cost",
        color: "#a1a1aa",
      },
    },
  },
}

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
    class="space-y-6"
    aria-label="Overview analytics"
  >
    <div class="grid gap-4 md:grid-cols-2 2xl:grid-cols-4">
      <StatCard
        v-for="card in summaryCards"
        :key="card.label"
        :label="card.label"
        :value="card.value"
        :secondary="card.secondary"
        :detail="card.detail"
      />
    </div>

    <div class="grid gap-4 xl:grid-cols-2">
      <Card
        data-testid="analytics-overview-daily-trend"
        class="border-border/80 bg-card/70 py-0 backdrop-blur-sm xl:col-span-2"
      >
        <CardHeader class="gap-3 border-b border-border/60 px-5 py-5">
          <div class="flex flex-wrap items-start justify-between gap-3">
            <div class="space-y-1">
              <CardTitle class="text-base text-foreground">
                Daily tokens and cost
              </CardTitle>
              <CardDescription>
                Token volume uses the left axis and spend uses the right axis for the selected range.
              </CardDescription>
            </div>

            <p class="text-xs font-medium uppercase tracking-[0.12em] text-muted-foreground">
              Est. total {{ formatCurrency(totalEstimatedCost) }}
            </p>
          </div>
        </CardHeader>

        <CardContent class="px-5 py-5">
          <div
            v-if="hasDailyData"
            class="h-[320px] sm:h-[360px]"
          >
            <Line
              :data="dailyTrendData"
              :options="dailyTrendOptions"
            />
          </div>

          <div
            v-else
            class="flex min-h-[320px] items-center justify-center rounded-xl border border-dashed border-border/70 bg-muted/20 px-6 py-8 text-center text-sm text-muted-foreground"
          >
            {{ dailyEmptyMessage }}
          </div>
        </CardContent>
      </Card>

      <section
        class="space-y-3"
        aria-label="Top models by cost"
      >
        <div class="space-y-1 px-1">
          <h2 class="text-base font-semibold tracking-tight text-foreground">
            Top models by cost
          </h2>
          <p class="text-sm leading-6 text-muted-foreground">
            Ranked by spend with relative bars. Estimated cost is shown when the model dataset provides it.
          </p>
        </div>

        <HorizontalCostBars
          :items="modelBarItems"
          :empty-message="modelsEmptyMessage"
        />
      </section>

      <section
        class="space-y-3"
        aria-label="Top projects by cost"
      >
        <div class="space-y-1 px-1">
          <h2 class="text-base font-semibold tracking-tight text-foreground">
            Top projects by cost
          </h2>
          <p class="text-sm leading-6 text-muted-foreground">
            Ranked by spend with relative bars across the current selection.
          </p>
        </div>

        <HorizontalCostBars
          :items="projectBarItems"
          :empty-message="projectsEmptyMessage"
        />
      </section>
    </div>
  </section>
</template>
