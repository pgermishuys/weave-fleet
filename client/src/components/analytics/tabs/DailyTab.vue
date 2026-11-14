<script setup lang="ts">
import { computed } from "vue";
import type { DailyAnalytics } from "@/api/client";
import { formatAnalyticsCost } from "@/lib/format-utils";
import { EmptyState } from "@/components/ui/empty-state";
import { ErrorNote } from "@/components/ui/error-note";
import { Spinner } from "@/components/ui/spinner";

interface Props {
  daily: readonly DailyAnalytics[];
  emptyMessage?: string;
  error?: string | null;
  /** The first answer hasn't come: say so, not that nothing matched. */
  isLoading?: boolean;
}

const props = withDefaults(defineProps<Props>(), {
  emptyMessage: "No daily analytics available for the selected range.",
  error: null,
  isLoading: false,
});

const integerFormatter = new Intl.NumberFormat("en-US");

// Newest day first: the day you came to check is on top.
const rows = computed(() =>
  [...props.daily]
    .sort((left, right) => right.date.localeCompare(left.date))
    .map((day) => ({
      date: day.date,
      tokens: integerFormatter.format(day.tokens),
      cost: formatAnalyticsCost(day.cost),
      sessions: integerFormatter.format(day.sessions),
      messages: integerFormatter.format(day.messages),
    })),
);
</script>

<template>
  <section
    class="daily-tab"
    aria-label="Daily analytics"
  >
    <div
      v-if="rows.length > 0"
      class="daily-tab__shell"
    >
      <table class="daily-tab__table">
        <thead>
          <tr>
            <th scope="col">
              Day
            </th>
            <th
              scope="col"
              class="daily-tab__num"
            >
              Tokens
            </th>
            <th
              scope="col"
              class="daily-tab__num"
            >
              Cost
            </th>
            <th
              scope="col"
              class="daily-tab__num"
            >
              Sessions
            </th>
            <th
              scope="col"
              class="daily-tab__num"
            >
              Messages
            </th>
          </tr>
        </thead>
        <tbody>
          <tr
            v-for="row in rows"
            :key="row.date"
          >
            <th scope="row">
              {{ row.date }}
            </th>
            <td class="daily-tab__num">
              {{ row.tokens }}
            </td>
            <td class="daily-tab__num">
              {{ row.cost }}
            </td>
            <td class="daily-tab__num">
              {{ row.sessions }}
            </td>
            <td class="daily-tab__num">
              {{ row.messages }}
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <div
      v-else-if="props.isLoading"
      class="flex items-center justify-center gap-2 p-6 text-sm text-muted"
      data-testid="analytics-loading"
    >
      <Spinner />
      Loading daily analytics…
    </div>
    <ErrorNote
      v-else-if="props.error"
      message="Unable to load analytics."
      :reason="props.error"
    />
    <EmptyState
      v-else
      :title="props.emptyMessage"
      bordered
    />
  </section>
</template>

<style scoped>
.daily-tab {
  width: 100%;
  min-width: 0;
}

.daily-tab__shell {
  overflow-x: auto;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
}

.daily-tab__table {
  width: 100%;
  border-collapse: collapse;
  font-size: 12.5px;
  font-variant-numeric: tabular-nums;
}

.daily-tab__table th,
.daily-tab__table td {
  padding: 8px 12px;
  border-bottom: 1px solid var(--border);
  color: var(--text);
  font-weight: 400;
  text-align: left;
  white-space: nowrap;
}

.daily-tab__table thead th {
  color: var(--muted);
  font-size: 11px;
  font-weight: 500;
}

.daily-tab__table tbody tr:last-child > * {
  border-bottom: 0;
}

.daily-tab__table .daily-tab__num {
  text-align: right;
}

</style>
