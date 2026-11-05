<script setup lang="ts">
import { computed } from "vue";
import { Check } from "lucide-vue-next";
import type { QuestionInfo } from "@/lib/question-types";

/**
 * The one-tap answers on a docked or inbox question. Up to four options stack as full-width rows (the whole label,
 * a one-line description) with "Something else…" for the sheet; more than four, or a several-answers question, keeps
 * the compact row: the first two options and More… / Answer….
 */
const props = defineProps<{ question: QuestionInfo; sent: string | null; busy: boolean; moreTestId: string }>();
const emit = defineEmits<{ (event: "pick", label: string): void; (event: "more"): void }>();

const MAX_STACKED = 4;
const stacked = computed(() => !props.question.multiple && props.question.options.length > 0 && props.question.options.length <= MAX_STACKED);
const quick = computed(() => (props.question.multiple ? [] : props.question.options.slice(0, 2)));
</script>

<template>
  <div
    v-if="stacked"
    class="ph-qa"
    data-testid="quick-stacked"
  >
    <button
      v-for="option in question.options"
      :key="option.label"
      type="button"
      class="ph-btn ph-btn--outline ph-qa__row"
      :class="{ 'ph-btn--done': sent === option.label }"
      :disabled="busy"
      @click="emit('pick', option.label)"
    >
      <span class="ph-qa__main">
        <span class="ph-qa__t">{{ option.label }}</span>
        <span
          v-if="option.description"
          class="ph-qa__s"
        >{{ option.description }}</span>
      </span>
      <Check
        v-if="sent === option.label"
        aria-hidden="true"
      />
    </button>
    <button
      type="button"
      class="ph-btn ph-btn--outline ph-qa__row ph-qa__else"
      :disabled="busy"
      :data-testid="moreTestId"
      @click="emit('more')"
    >
      <span>Something else…</span>
    </button>
  </div>
  <div
    v-else
    class="ph-btns"
    :class="quick.length === 2 ? 'ph-btns--q' : quick.length === 1 ? 'ph-btns--q-one' : 'ph-btns--q-none'"
  >
    <button
      v-for="option in quick"
      :key="option.label"
      type="button"
      class="ph-btn ph-btn--outline"
      :class="{ 'ph-btn--done': sent === option.label }"
      :disabled="busy"
      @click="emit('pick', option.label)"
    >
      <Check
        v-if="sent === option.label"
        aria-hidden="true"
      />
      <span>{{ option.label }}</span>
    </button>
    <button
      type="button"
      class="ph-btn ph-btn--outline"
      :disabled="busy"
      :data-testid="moreTestId"
      @click="emit('more')"
    >
      <span>{{ quick.length ? "More…" : "Answer…" }}</span>
    </button>
  </div>
</template>
