<script setup lang="ts">
import { computed } from "vue"
import HorizontalCostBars from "@/components/analytics/charts/HorizontalCostBars.vue"
import type { ModelAnalytics } from "@/api/client"
import { formatAnalyticsCost } from "@/lib/format-utils"

interface CostBarItem {
  name: string
  cost: number
  maxCost: number
  detail?: string
}

interface ModelTableRow extends ModelAnalytics {
  key: string
  formattedTokens: string
  formattedCost: string
  formattedEstimatedCost: string
  formattedMessageCount: string
  formattedAverageCostPerMessage: string
}

interface Props {
  models: readonly ModelAnalytics[]
  isLoading: boolean
  error?: string
  chartEmptyMessage?: string
  tableEmptyMessage?: string
}

const props = withDefaults(defineProps<Props>(), {
  error: undefined,
  chartEmptyMessage: "No model cost data available for the selected filters.",
  tableEmptyMessage: "No model analytics available for the selected filters.",
})

const integerFormatter = new Intl.NumberFormat("en-US")

const hasModels = computed(() => props.models.length > 0)

const sortedModels = computed<ModelAnalytics[]>(() => {
  return [...props.models].sort((left, right) => {
    return right.cost - left.cost
      || right.estimatedCost - left.estimatedCost
      || right.tokens - left.tokens
      || left.modelId.localeCompare(right.modelId)
  })
})

const maxCost = computed(() => {
  return sortedModels.value.reduce((highest, model) => Math.max(highest, model.cost), 0)
})

const chartItems = computed<CostBarItem[]>(() => {
  return sortedModels.value.map((model) => ({
    name: model.modelId,
    cost: model.cost,
    maxCost: maxCost.value,
    detail: `${model.providerId} · ${integerFormatter.format(model.messageCount)} messages · Est. ${formatAnalyticsCost(model.estimatedCost)}`,
  }))
})

const tableRows = computed<ModelTableRow[]>(() => {
  return sortedModels.value.map((model) => ({
    ...model,
    key: `${model.providerId}:${model.modelId}`,
    formattedTokens: integerFormatter.format(model.tokens),
    formattedCost: formatAnalyticsCost(model.cost),
    formattedEstimatedCost: formatAnalyticsCost(model.estimatedCost),
    formattedMessageCount: integerFormatter.format(model.messageCount),
    formattedAverageCostPerMessage: formatAnalyticsCost(model.avgCostPerMessage),
  }))
})

const emptyStateMessage = computed(() => {
  return props.error
    ? `Unable to load model analytics (${props.error}).`
    : props.tableEmptyMessage
})
</script>

<template>
  <section
    class="models-tab"
    aria-label="Models analytics"
  >
    <div
      v-if="isLoading"
      class="models-tab__state models-tab__state--loading"
    >
      Loading model analytics…
    </div>

    <div
      v-else-if="!hasModels"
      class="models-tab__state"
    >
      {{ emptyStateMessage }}
    </div>

    <div
      v-else
      class="models-tab__content"
    >
      <section
        class="models-tab__panel"
        aria-label="Cost by model"
      >
        <h2 class="models-tab__title">
          Cost by model
        </h2>
        <HorizontalCostBars
          :items="chartItems"
          :empty-message="chartEmptyMessage"
        />
      </section>

      <section
        class="models-tab__panel models-tab__panel--table"
        aria-label="Model details"
      >
        <h2 class="models-tab__title">
          Model details
        </h2>
        <div class="models-tab__table-shell">
          <table class="models-tab__table">
            <caption class="models-tab__sr-only">
              Model analytics including model, provider, tokens, cost, estimated cost, messages, and average cost per message.
            </caption>
            <thead>
              <tr>
                <th
                  scope="col"
                  class="models-tab__head"
                >
                  Model
                </th>
                <th
                  scope="col"
                  class="models-tab__head"
                >
                  Provider
                </th>
                <th
                  scope="col"
                  class="models-tab__head models-tab__head--right"
                >
                  Tokens
                </th>
                <th
                  scope="col"
                  class="models-tab__head models-tab__head--right"
                >
                  Cost
                </th>
                <th
                  scope="col"
                  class="models-tab__head models-tab__head--right"
                >
                  Estimated cost
                </th>
                <th
                  scope="col"
                  class="models-tab__head models-tab__head--right"
                >
                  Messages
                </th>
                <th
                  scope="col"
                  class="models-tab__head models-tab__head--right"
                >
                  Avg cost/msg
                </th>
              </tr>
            </thead>

            <tbody>
              <tr
                v-for="row in tableRows"
                :key="row.key"
                class="models-tab__row"
              >
                <td class="models-tab__cell">
                  <div class="models-tab__primary">
                    {{ row.modelId }}
                  </div>
                </td>
                <td class="models-tab__cell">
                  <div class="models-tab__secondary models-tab__secondary--strong">
                    {{ row.providerId }}
                  </div>
                </td>
                <td class="models-tab__cell models-tab__cell--right">
                  {{ row.formattedTokens }}
                </td>
                <td class="models-tab__cell models-tab__cell--right">
                  {{ row.formattedCost }}
                </td>
                <td class="models-tab__cell models-tab__cell--right">
                  {{ row.formattedEstimatedCost }}
                </td>
                <td class="models-tab__cell models-tab__cell--right">
                  {{ row.formattedMessageCount }}
                </td>
                <td class="models-tab__cell models-tab__cell--right">
                  {{ row.formattedAverageCostPerMessage }}
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>
    </div>
  </section>
</template>

<style scoped>
.models-tab {
  min-width: 0;
}

.models-tab__content {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: 12px;
}

.models-tab__panel {
  display: flex;
  flex-direction: column;
  gap: 12px;
  min-width: 0;
  padding: 16px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
}

.models-tab__panel--table {
  padding: 16px 0 0;
  overflow: hidden;
}

.models-tab__panel--table .models-tab__title {
  padding: 0 16px;
}

.models-tab__title {
  margin: 0;
  color: var(--text);
  font-size: 13px;
  font-weight: 600;
}

.models-tab__state {
  display: flex;
  min-height: 280px;
  align-items: center;
  justify-content: center;
  border: 1px dashed var(--border);
  border-radius: var(--radius-card);
  padding: 24px;
  color: var(--muted);
  font-size: 13px;
  text-align: center;
}

.models-tab__state--loading {
  color: var(--text);
}

.models-tab__table-shell {
  overflow-x: auto;
}

.models-tab__table {
  width: 100%;
  min-width: 720px;
  border-collapse: collapse;
}

.models-tab__head {
  padding: 8px 16px;
  border-bottom: 1px solid var(--border);
  color: var(--muted);
  font-size: 12px;
  font-weight: 500;
  text-align: left;
  white-space: nowrap;
}

.models-tab__head--right {
  text-align: right;
}

.models-tab__row {
  transition: background-color var(--transition);
}

.models-tab__row:hover {
  background: color-mix(in srgb, var(--text) 3%, transparent);
}

.models-tab__row + .models-tab__row {
  border-top: 1px solid var(--border);
}

.models-tab__cell {
  padding: 10px 16px;
  color: var(--text);
  font-size: 13px;
  line-height: 1.5;
  vertical-align: top;
}

.models-tab__cell--right {
  text-align: right;
  font-variant-numeric: tabular-nums;
}

.models-tab__primary {
  color: var(--text);
  font-weight: 500;
  overflow-wrap: anywhere;
}

.models-tab__secondary {
  color: var(--muted);
  overflow-wrap: anywhere;
}

.models-tab__secondary--strong {
  color: var(--text);
  font-weight: 500;
}

.models-tab__sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  padding: 0;
  margin: -1px;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
  white-space: nowrap;
  border: 0;
}

@media (max-width: 720px) {
  .models-tab__head,
  .models-tab__cell {
    padding-right: 14px;
    padding-left: 14px;
  }
}
</style>
