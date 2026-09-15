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
    class="cost-bars"
    aria-label="Cost ranking"
  >
    <ol
      v-if="rankedItems.length > 0"
      class="cost-bars__list"
    >
      <li
        v-for="item in rankedItems"
        :key="item.key"
        class="cost-bars__item"
      >
        <div class="cost-bars__row">
          <div class="cost-bars__copy">
            <p class="cost-bars__name">
              {{ item.name }}
            </p>
            <p class="cost-bars__detail">
              {{ item.detail || `${item.formattedCost} of ${item.formattedMaxCost}` }}
            </p>
          </div>

          <span class="cost-bars__cost">
            {{ item.formattedCost }}
          </span>
        </div>

        <div
          class="cost-bars__track"
          role="img"
          :aria-label="`${item.name} cost bar at ${item.width.toFixed(0)} percent`"
        >
          <div
            class="cost-bars__fill"
            :style="{ width: `${item.width}%` }"
          />
        </div>
      </li>
    </ol>

    <p
      v-else
      class="cost-bars__empty"
    >
      {{ props.emptyMessage }}
    </p>
  </section>
</template>

<style scoped>
/* Bare: it sits inside a panel or card that draws the box. */
.cost-bars__list {
  display: flex;
  flex-direction: column;
  gap: 14px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.cost-bars__item {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.cost-bars__row {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
}

.cost-bars__copy {
  min-width: 0;
}

.cost-bars__name {
  margin: 0;
  overflow: hidden;
  color: var(--text);
  font-size: 13px;
  font-weight: 500;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.cost-bars__detail {
  margin: 0;
  color: var(--muted);
  font-size: 12px;
}

.cost-bars__cost {
  flex-shrink: 0;
  color: var(--text);
  font-size: 13px;
  font-weight: 600;
  font-variant-numeric: tabular-nums;
}

.cost-bars__track {
  height: 6px;
  overflow: hidden;
  border-radius: 999px;
  background: color-mix(in srgb, var(--text) 8%, transparent);
}

.cost-bars__fill {
  height: 100%;
  border-radius: 999px;
  background: var(--accent);
  transition: width 300ms ease-out;
}

.cost-bars__empty {
  margin: 0;
  color: var(--muted);
  font-size: 13px;
}

@media (prefers-reduced-motion: reduce) {
  .cost-bars__fill {
    transition: none;
  }
}
</style>
