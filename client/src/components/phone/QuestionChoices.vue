<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { ArrowUp, Check } from "lucide-vue-next";
import { autogrow } from "@/lib/phone/keyboard";
import type { QuestionInfo } from "@/lib/question-types";

/**
 * An agent's question as the More… sheet shows it: the question, its options as numbered rows (one tap answers; with
 * several allowed, tap to pick and Send answer), a box for an answer in your own words when it takes one, and Skip.
 * Answers go as the desktop QuestionCard sends them: the picked labels.
 */
const props = defineProps<{ question: QuestionInfo; busy?: boolean; more?: number; skippable?: boolean }>();
const emit = defineEmits<{
  (event: "answer", labels: string[]): void;
  (event: "skip"): void;
  (event: "expand"): void;
}>();

const picked = shallowRef<string[]>([]);
const custom = shallowRef("");
const allowsCustom = computed(() => props.question.custom !== false);

function choose(label: string): void {
  if (!props.question.multiple) {
    emit("answer", [label]);
    return;
  }
  picked.value = picked.value.includes(label) ? picked.value.filter((l) => l !== label) : [...picked.value, label];
}

function sendCustom(): void {
  const text = custom.value.trim();
  if (!text) return;
  emit("answer", props.question.multiple ? [...picked.value, text] : [text]);
}
</script>

<template>
  <div
    class="ph-sheet__pad"
    data-testid="question-choices"
  >
    <p class="qc__q">
      {{ question.question }}
    </p>
    <p
      v-if="more"
      class="qc__more"
    >
      Then {{ more }} more question{{ more === 1 ? "" : "s" }}: answer {{ more === 1 ? "it" : "them" }} in the session.
    </p>
    <div class="ph-choices">
      <button
        v-for="(option, index) in question.options"
        :key="option.label"
        type="button"
        class="ph-choice"
        :class="{ 'ph-choice--selected': picked.includes(option.label) }"
        :aria-pressed="question.multiple ? picked.includes(option.label) : undefined"
        :disabled="busy"
        @click="choose(option.label)"
      >
        <span
          class="ph-choice__n"
          aria-hidden="true"
        >{{ index + 1 }}</span>
        <span class="ph-choice__main">
          <span class="ph-choice__t">{{ option.label }}</span>
          <span
            v-if="option.description"
            class="ph-choice__s"
          >{{ option.description }}</span>
        </span>
        <Check
          v-if="question.multiple && picked.includes(option.label)"
          class="ph-set__check"
          aria-hidden="true"
        />
      </button>
      <form
        v-if="allowsCustom"
        class="ph-frame qc__own"
        @submit.prevent="sendCustom"
      >
        <textarea
          v-model="custom"
          class="phone-composer-input"
          rows="1"
          placeholder="Something else, in your own words…"
          aria-label="Your answer"
          data-testid="question-custom"
          @focus="emit('expand')"
          @input="autogrow($event.target as HTMLTextAreaElement)"
        />
        <div class="ph-frame__bar">
          <button
            type="submit"
            class="ph-send"
            :disabled="busy || !custom.trim()"
            aria-label="Send answer"
            data-testid="question-custom-send"
          >
            <ArrowUp aria-hidden="true" />
          </button>
        </div>
      </form>
    </div>
    <button
      v-if="question.multiple"
      type="button"
      class="ph-btn ph-btn--primary ph-btn--block qc__send"
      :disabled="busy || picked.length === 0"
      data-testid="question-send"
      @click="emit('answer', picked)"
    >
      Send answer
    </button>
    <button
      v-if="skippable"
      type="button"
      class="ph-link ph-link--muted qc__skip"
      :disabled="busy"
      data-testid="question-skip"
      @click="emit('skip')"
    >
      Skip the question
    </button>
  </div>
</template>

<style scoped>
.qc__q {
  margin: 0 0 14px;
  font-size: var(--ph-t-body);
  line-height: 1.5;
}

.qc__more {
  margin: -8px 0 14px;
  font-size: var(--ph-t-meta);
  color: var(--muted);
}

.qc__own {
  margin-top: 2px;
}

.qc__send {
  margin-top: 12px;
}

.qc__skip {
  margin-top: 6px;
}
</style>
