<script setup lang="ts">
import { computed, onBeforeUnmount, shallowRef } from "vue";
import { Gauge } from "lucide-vue-next";
import { useHarnessUsage } from "@/composables/use-harness-usage";
import { useHarnesses } from "@/composables/use-harnesses";
import {
  harnessShortName,
  percentLabel,
  resetLabel,
  windowLabel,
  windowShortLabel,
  windowToFlag,
} from "@/lib/usage-limits";

/**
 * A harness's usage limit in the status bar, only while one is close (80% or more) or used up: "Claude 5 h · resets
 * 14:05", with a small bar. Nothing otherwise, and nothing for a harness that doesn't report limits (an API key, a
 * gateway). The details are in the context ring's card.
 */
const { usage } = useHarnessUsage();
const { harnesses } = useHarnesses();

// Once a minute: a window that resets drops off without waiting for the harness to say so.
const now = shallowRef(Date.now());
const timer = setInterval(() => (now.value = Date.now()), 60_000);
onBeforeUnmount(() => clearInterval(timer));

const chips = computed(() =>
  Object.values(usage)
    .map((harness) => ({ harness, window: windowToFlag(harness, now.value) }))
    .filter((entry): entry is { harness: typeof entry.harness; window: NonNullable<typeof entry.window> } => entry.window !== null)
    .map(({ harness, window }) => {
      const name = harnessShortName(harness.harnessType, harnesses.value.find((h) => h.type === harness.harnessType)?.displayName);
      const reset = resetLabel(window.resetsAt, now.value);
      const used = window.status === "rejected";
      const percent = percentLabel(window);
      return {
        key: `${harness.harnessType}:${window.window}`,
        name: `${name} ${windowShortLabel(window.window)}`,
        when: reset?.replace(/^resets /, "") ?? null,
        fill: used ? 100 : Math.round((window.utilization ?? 0) * 100),
        used,
        title: used
          ? `${name}'s ${windowLabel(window.window).toLowerCase()} is used up${reset ? `; it ${reset}` : ""}.`
          : `${name}'s ${windowLabel(window.window).toLowerCase()} is ${percent ?? "nearly"} used${reset ? `; it ${reset}` : ""}.`,
      };
    }),
);
</script>

<template>
  <span
    v-for="chip in chips"
    :key="chip.key"
    class="usage-chip"
    :class="{ 'usage-chip--used': chip.used }"
    role="status"
    :title="chip.title"
    :aria-label="chip.title"
    data-testid="usage-limit-chip"
  >
    <Gauge
      class="usage-chip__icon"
      aria-hidden="true"
    />
    {{ chip.name }}
    <span
      class="usage-chip__bar"
      aria-hidden="true"
    ><i :style="{ width: `${chip.fill}%` }" /></span>
    <span v-if="chip.when"><span class="usage-chip__reset-word">resets </span>{{ chip.when }}</span>
  </span>
</template>

<style scoped>
.usage-chip {
  display: inline-flex;
  flex-shrink: 0;
  align-items: center;
  gap: 6px;
  height: 18px;
  padding: 0 8px 0 7px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--status-waiting) 14%, transparent);
  color: var(--status-waiting);
  font-size: 11px;
  font-weight: 500;
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}

.usage-chip--used {
  background: color-mix(in srgb, var(--error) 14%, transparent);
  color: var(--error);
}

.usage-chip__icon {
  width: 12px;
  height: 12px;
}

.usage-chip__bar {
  display: inline-block;
  width: 30px;
  height: 4px;
  overflow: hidden;
  border-radius: 999px;
  background: color-mix(in srgb, currentColor 22%, transparent);
}

/* A phone's status bar has room for the window and the time, not the bar. */
@media (max-width: 600px) {
  .usage-chip__bar {
    display: none;
  }

  .usage-chip__reset-word {
    display: none;
  }
}

.usage-chip__bar i {
  display: block;
  height: 100%;
  border-radius: inherit;
  background: currentColor;
}
</style>
