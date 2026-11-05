<script setup lang="ts">
import { shallowRef, useTemplateRef } from "vue";
import { CircleHelp } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";
import QuestionChoices from "@/components/phone/QuestionChoices.vue";
import QuestionQuickAnswers from "@/components/phone/QuestionQuickAnswers.vue";
import type { PendingQuestion } from "@/lib/phone/dock-state";

/**
 * The agent's question docked above the composer, compact: the question, its options as rows (one tap
 * answers; past four, the first two as buttons) and More… (every option, your own words, Skip, in a sheet). A question that takes several answers has
 * Answer… instead. Later folds it into "1 waiting · Review".
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
    class="ph-pcard ph-docked"
    data-testid="docked-question"
  >
    <div class="ph-pcard__head">
      <CircleHelp
        class="ph-pcard__icon"
        aria-hidden="true"
      />
      <span class="ph-pcard__title">Question</span>
      <button
        type="button"
        class="ph-later"
        data-testid="docked-later"
        @click="emit('later')"
      >
        Later
      </button>
    </div>
    <p class="ph-pcard__q dq__q">
      {{ pending.question.question }}
    </p>
    <p
      v-if="error"
      class="ph-pcard__note ph-pcard__note--bad"
      role="alert"
    >
      {{ error }}
    </p>
    <QuestionQuickAnswers
      :question="pending.question"
      :sent="sent"
      :busy="sent !== null"
      more-test-id="docked-more"
      @pick="(label) => answerWith([label])"
      @more="more = true"
    />

    <BottomSheet
      ref="sheet"
      :open="more"
      label="Question"
      title="Question"
      :subtitle="`${machineName} · ${sessionTitle}`"
      :detents="['medium', 'large']"
      initial="medium"
      @close="more = false"
    >
      <QuestionChoices
        :question="pending.question"
        :more="pending.more"
        :busy="sent !== null"
        skippable
        @answer="answerWith"
        @skip="run('Skipped', () => props.reject(pending.requestId))"
        @expand="sheetRef?.expand()"
      />
    </BottomSheet>
  </div>
</template>

<style scoped>
.dq__q {
  margin-top: 8px;
}
</style>
