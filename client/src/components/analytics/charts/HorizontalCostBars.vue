<script setup lang="ts">
import { computed } from "vue"

interface HorizontalCostBarItem {
  name: string
  cost: number
  maxCost: number
  detail?: string
}

interface RankedHorizontalCostBarItem extends HorizontalCostBarItem {
  key: string
  width: number
  formattedCost: string
  formattedMaxCost: string
}

const props = withDefaults(defineProps<{
  items: readonly HorizontalCostBarItem[]
  emptyMessage?: string
}>(), {
  emptyMessage: "No cost data available.",
})

const currencyFormatter = new Intl.NumberFormat("en-US", {
  style: "currency",
  currency: "USD",
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
})

const rankedItems = computed<RankedHorizontalCostBarItem[]>(() => {
  return [...props.items]
    .sort((left, right) => right.cost - left.cost || left.name.localeCompare(right.name))
    .map((item, index) => ({
      ...item,
      key: `${item.name}-${item.cost}-${item.maxCost}-${index}`,
      width: getRelativeWidth(item.cost, item.maxCost),
      formattedCost: formatCurrency(item.cost),
      formattedMaxCost: formatCurrency(item.maxCost),
    }))
})

function getRelativeWidth(cost: number, maxCost: number): number {
  if (!Number.isFinite(cost) || !Number.isFinite(maxCost) || maxCost <= 0 || cost <= 0) {
    return 0
  }

  return Math.min((cost / maxCost) * 100, 100)
}

function formatCurrency(amount: number): string {
  if (!Number.isFinite(amount)) {
    return currencyFormatter.format(0)
  }

  return currencyFormatter.format(amount)
}
</script>

<template>
  <section
    class="rounded-xl border border-border/80 bg-card/70 p-5 shadow-sm backdrop-blur-sm"
    aria-label="Cost ranking"
  >
    <ol
      v-if="rankedItems.length > 0"
      class="space-y-4"
    >
      <li
        v-for="item in rankedItems"
        :key="item.key"
        class="space-y-2"
      >
        <div class="flex items-start justify-between gap-4">
          <div class="min-w-0 space-y-1">
            <p class="truncate text-sm font-medium text-foreground">
              {{ item.name }}
            </p>
            <p class="text-xs text-muted-foreground">
              {{ item.detail || `${item.formattedCost} of ${item.formattedMaxCost}` }}
            </p>
          </div>

          <span class="shrink-0 text-sm font-semibold tabular-nums text-foreground">
            {{ item.formattedCost }}
          </span>
        </div>

        <div
          class="h-2 overflow-hidden rounded-full bg-muted/70"
          role="img"
          :aria-label="`${item.name} cost bar at ${item.width.toFixed(0)} percent`"
        >
          <div
            class="h-full rounded-full bg-primary transition-[width] duration-300 ease-out"
            :style="{ width: `${item.width}%` }"
          />
        </div>
      </li>
    </ol>

    <p
      v-else
      class="text-sm text-muted-foreground"
    >
      {{ props.emptyMessage }}
    </p>
  </section>
</template>
