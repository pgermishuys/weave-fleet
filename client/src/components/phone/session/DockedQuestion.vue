<script setup lang="ts">
import { shallowRef } from "vue";
import QuestionChoices from "@/components/phone/QuestionChoices.vue";
import type { PendingQuestion } from "@/lib/phone/dock-state";

/** The agent's question in the composer's place: radio rows, Something else…, Send answer, and Skip or Later. */
const props = defineProps<{
  pending: PendingQuestion;
  answer: (requestId: string, answers: string[][]) => Promise<void>;
  reject: (requestId: string) => Promise<void>;
}>();
const emit = defineEmits<{ (event: "later"): void }>();

const busy = shallowRef(false);
const error = shallowRef<string | null>(null);

async function run(action: () => Promise<void>): Promise<void> {
  busy.value = true;
  error.value = null;
  try {
    await action();
  } catch (failure) {
    error.value = failure instanceof Error ? failure.message : String(failure);
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <div
    class="dock-q"
    data-testid="docked-question"
  >
    <p class="dock-q__head">
      Question
    </p>
    <QuestionChoices
      :question="pending.question"
      :more="pending.more"
      :busy="busy"
      @answer="(labels) => run(() => props.answer(pending.requestId, [labels]))"
      @skip="run(() => props.reject(pending.requestId))"
    />
    <p
      v-if="error"
      class="dock-q__error"
      role="alert"
    >
      {{ error }}
    </p>
    <button
      type="button"
      class="dock-q__later"
      @click="emit('later')"
    >
      Later
    </button>
  </div>
</template>

<style scoped>
.dock-q {
  display: grid;
  flex: none;
  gap: 6px;
  max-height: 70dvh;
  overflow-y: auto;
  padding: 8px 12px calc(env(safe-area-inset-bottom) + 8px);
  border-top: 1px solid var(--border);
  background: var(--main-bg);
}

.dock-q__head {
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: var(--idle);
}

.dock-q__error {
  font-size: 12px;
  color: var(--error);
}

.dock-q__later {
  min-height: 40px;
  border: 0;
  background: transparent;
  color: var(--muted);
  font: inherit;
  font-size: 13px;
}
</style>
