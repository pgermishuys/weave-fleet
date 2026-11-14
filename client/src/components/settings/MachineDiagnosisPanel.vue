<script setup lang="ts">
import { shallowRef } from "vue";
import { Check, Circle, X } from "lucide-vue-next";
import { diagnosisText, type MachineDiagnosis } from "@/lib/machine-diagnosis";

/**
 * The steps Fleet checked on the way to a machine, ticked up to the one that broke, and what to try. Under a machine's
 * card and under Add a machine, where a failure's headline is shown above it.
 */
const props = defineProps<{
  diagnosis: MachineDiagnosis;
  /** Shows a Retry now button that emits `retry`. */
  retryable?: boolean;
}>();
defineEmits<{ retry: [] }>();

const buttonSecondaryClass = "inline-flex items-center justify-center gap-2 rounded-btn border border-border bg-main-bg px-3 py-1.5 text-sm font-medium text-text transition-colors hover:border-accent/50";

const copied = shallowRef(false);

async function copyDetails(): Promise<void> {
  await navigator.clipboard.writeText(diagnosisText(props.diagnosis));
  copied.value = true;
  window.setTimeout(() => { copied.value = false; }, 1500);
}
</script>

<template>
  <div
    class="mt-2 grid gap-2 text-xs"
    data-testid="machine-diagnosis"
  >
    <ul class="grid gap-1">
      <li
        v-for="step in diagnosis.steps"
        :key="step.label"
        class="flex items-start gap-2"
        :data-step-state="step.state"
      >
        <Check
          v-if="step.state === 'ok'"
          :size="14"
          class="mt-0.5 shrink-0 text-green-400"
          aria-hidden="true"
        />
        <X
          v-else-if="step.state === 'failed'"
          :size="14"
          class="mt-0.5 shrink-0 text-red-400"
          aria-hidden="true"
        />
        <Circle
          v-else
          :size="14"
          class="mt-0.5 shrink-0 text-muted"
          aria-hidden="true"
        />
        <span :class="step.state === 'notChecked' ? 'text-muted' : ''">
          {{ step.label }}<template v-if="step.state === 'notChecked'"> · not checked</template>
          <span
            v-if="step.detail"
            class="block text-muted"
          >{{ step.detail }}</span>
        </span>
      </li>
    </ul>
    <p data-testid="machine-diagnosis-try">
      <span class="font-semibold">Try</span> {{ diagnosis.try }}
    </p>
    <div class="flex gap-2">
      <button
        v-if="retryable"
        type="button"
        :class="buttonSecondaryClass"
        data-testid="machine-diagnosis-retry"
        @click="$emit('retry')"
      >
        Retry now
      </button>
      <button
        type="button"
        :class="buttonSecondaryClass"
        data-testid="machine-diagnosis-copy"
        @click="copyDetails"
      >
        {{ copied ? "Copied" : "Copy details" }}
      </button>
    </div>
  </div>
</template>
