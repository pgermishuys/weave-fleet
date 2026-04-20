<script setup lang="ts">
import { computed } from "vue";
import { Card, CardContent } from "@/components/ui/card";
import { useFleetSummary } from "@/composables/use-fleet-summary";

interface SummaryMetric {
  testId: string;
  label: string;
  value: string;
}

const { summary } = useFleetSummary();

const integerFormatter = new Intl.NumberFormat("en-US");
const currencyFormatter = new Intl.NumberFormat("en-US", {
  style: "currency",
  currency: "USD",
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});

const metrics = computed<SummaryMetric[]>(() => {
  const currentSummary = summary.value;

  return [
    {
      testId: "summary-active-count",
      label: "Active",
      value: integerFormatter.format(currentSummary?.activeSessions ?? 0),
    },
    {
      testId: "summary-idle-count",
      label: "Idle",
      value: integerFormatter.format(currentSummary?.idleSessions ?? 0),
    },
    {
      testId: "summary-queued-count",
      label: "Queued",
      value: integerFormatter.format(currentSummary?.queuedTasks ?? 0),
    },
    {
      testId: "summary-tokens-count",
      label: "Tokens",
      value: integerFormatter.format(currentSummary?.totalTokens ?? 0),
    },
    {
      testId: "summary-cost-count",
      label: "Cost",
      value: currencyFormatter.format(currentSummary?.totalCost ?? 0),
    },
  ];
});
</script>

<template>
  <Card
    data-testid="summary-bar"
    class="border-border/80 bg-card/70 py-0 backdrop-blur-sm"
  >
    <CardContent class="grid gap-4 px-5 py-4 sm:grid-cols-2 xl:grid-cols-5">
      <div
        v-for="metric in metrics"
        :key="metric.testId"
        class="space-y-1 rounded-lg border border-border/60 bg-background/60 px-4 py-3"
      >
        <p class="text-xs font-medium uppercase tracking-[0.12em] text-muted-foreground">
          {{ metric.label }}
        </p>
        <p
          :data-testid="metric.testId"
          class="text-2xl font-semibold tracking-tight text-foreground"
        >
          {{ metric.value }}
        </p>
      </div>
    </CardContent>
  </Card>
</template>
