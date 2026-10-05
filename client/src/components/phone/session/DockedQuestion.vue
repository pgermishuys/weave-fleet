<script setup lang="ts">
import { computed, shallowRef, useTemplateRef } from "vue";
import { Check } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";
import QuestionChoices from "@/components/phone/QuestionChoices.vue";
import type { PendingQuestion } from "@/lib/phone/dock-state";

/**
 * The agent's question docked above the composer, compact: the question, its first two options as buttons (a tap
 * answers) and More… (every option, your own words, Skip, in a sheet). Later folds it into a pill.
 */
const props = defineProps<{
  pending: PendingQuestion;
  answer: (requestId: string, answers: string[][]) => Promise<void>;
  reject: (requestId: string) => Promise<void>;
  machineName: string;
  sessionTitle: string;
}>();
const emit = defineEmits<{ (event: "later"): void }>();

const sheetRef = useTemplateRef<InstanceType<typeof BottomSheet>>("sheet");
const more = shallowRef(false);
const sent = shallowRef<string | null>(null);
const error = shallowRef<string | null>(null);
const quick = computed(() => (props.pending.question.multiple ? [] : props.pending.question.options.slice(0, 2)));

async function run(label: string, action: () => Promise<void>): Promise<void> {
  more.value = false;
  sent.value = label;
  error.value = null;
  try {
    await action();
  } catch (failure) {
    sent.value = null;
    error.value = failure instanceof Error ? failure.message : String(failure);
  }
}

function answerWith(labels: string[]): void {
  void run(labels.join(", "), () => props.answer(props.pending.requestId, [labels]));
}
</script>

<template>
  <div
    class="ph-docked-ask"
    data-testid="docked-question"
  >
    <div class="ph-docked-ask__h">
      <span
        class="ph-dot ph-dot--waiting"
        aria-hidden="true"
      />Asked you
      <button
        type="button"
        class="ph-docked-ask__later ph-press"
        data-testid="docked-later"
        @click="emit('later')"
      >
        Later
      </button>
    </div>
    <p class="ph-docked-ask__q">
      {{ pending.question.question }}
    </p>
    <p
      v-if="error"
      class="ph-docked-ask__note dq__error"
      role="alert"
    >
      {{ error }}
    </p>
    <div
      class="ph-btns"
      :class="quick.length === 2 ? 'ph-btns--q' : quick.length === 1 ? 'ph-btns--q-one' : 'ph-btns--q-none'"
    >
      <button
        v-for="option in quick"
        :key="option.label"
        type="button"
        class="ph-btn"
        :class="{ 'ph-btn--done': sent === option.label }"
        :disabled="sent !== null"
        @click="answerWith([option.label])"
      >
        <Check
          v-if="sent === option.label"
          :size="20"
          :stroke-width="2.6"
          aria-hidden="true"
        />
        <span>{{ option.label }}</span>
      </button>
      <button
        type="button"
        class="ph-btn"
        :disabled="sent !== null"
        data-testid="docked-more"
        @click="more = true"
      >
        <span>{{ quick.length ? "More…" : "Answer…" }}</span>
      </button>
    </div>

    <BottomSheet
      ref="sheet"
      :open="more"
      label="Question"
      title="Question"
      :detents="['medium', 'large']"
      initial="medium"
      @close="more = false"
    >
      <QuestionChoices
        :question="pending.question"
        :more="pending.more"
        :busy="sent !== null"
        :machine-name="machineName"
        :session-title="sessionTitle"
        skippable
        @answer="answerWith"
        @skip="run('Skipped', () => props.reject(pending.requestId))"
        @expand="sheetRef?.expand()"
      />
    </BottomSheet>
  </div>
</template>

<style scoped>
.ph-docked-ask__q {
  margin: 0;
}

.dq__error {
  color: var(--error);
}
</style>
