<script setup lang="ts">
import { computed } from "vue";

type ContextMeterTone = "normal" | "warn" | "danger";

const props = defineProps<{
  usedTokens: number;
  maxTokens: number;
  warnThreshold?: number;
  dangerThreshold?: number;
}>();

const usageRatio = computed(() => {
  if (props.maxTokens <= 0) {
    return 0;
  }

  return Math.min(props.usedTokens / props.maxTokens, 1);
});

const usagePercent = computed(() => Math.round(usageRatio.value * 100));

const tone = computed<ContextMeterTone>(() => {
  const warnThreshold = props.warnThreshold ?? 0.75;
  const dangerThreshold = props.dangerThreshold ?? 0.9;

  if (usageRatio.value >= dangerThreshold) {
    return "danger";
  }

  if (usageRatio.value >= warnThreshold) {
    return "warn";
  }

  return "normal";
});

const fillClassName = computed(() => ({
  warn: tone.value === "warn",
  danger: tone.value === "danger",
}));

const toneLabel = computed(() => {
  switch (tone.value) {
    case "danger":
      return "Critical";
    case "warn":
      return "Warning";
    default:
      return "Healthy";
  }
});
</script>

<template>
  <section class="context-meter" aria-label="Context window usage">
    <div class="context-meter__header">
      <div class="context-meter__copy">
        <p class="context-meter__label">
          Context window
        </p>
        <p class="context-meter__value">
          {{ usedTokens.toLocaleString() }} / {{ maxTokens.toLocaleString() }} tokens
        </p>
      </div>

      <div class="context-meter__summary">
        <span class="context-meter__percent">{{ usagePercent }}%</span>
        <span class="context-meter__tone" :class="`context-meter__tone--${tone}`">{{ toneLabel }}</span>
      </div>
    </div>

    <div class="context-meter-bar" aria-hidden="true">
      <div
        class="context-meter-fill"
        :class="fillClassName"
        :style="{ width: `${usagePercent}%` }"
      />
    </div>
  </section>
</template>

<style scoped>
.context-meter {
  display: flex;
  flex-direction: column;
  gap: 10px;
  margin-bottom: 16px;
}

.context-meter__header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.context-meter__copy {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
}

.context-meter__label {
  margin: 0;
  font-size: 12px;
  font-weight: 600;
  color: var(--text);
}

.context-meter__value {
  margin: 0;
  font-size: 12px;
  color: var(--muted);
}

.context-meter__summary {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 4px;
  flex-shrink: 0;
}

.context-meter__percent {
  font-size: 14px;
  font-weight: 600;
  color: var(--text);
}

.context-meter__tone {
  font-size: 11px;
  font-weight: 600;
}

.context-meter__tone--normal {
  color: var(--running);
}

.context-meter__tone--warn {
  color: var(--idle);
}

.context-meter__tone--danger {
  color: var(--error);
}

.context-meter-bar {
  height: 6px;
  background: rgba(255, 255, 255, 0.06);
  border-radius: 3px;
  overflow: hidden;
}

.context-meter-fill {
  height: 100%;
  border-radius: 3px;
  background: var(--accent);
}

.context-meter-fill.warn {
  background: var(--idle);
}

.context-meter-fill.danger {
  background: var(--error);
}
</style>
