<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { ArrowUp, Check, Monitor } from "lucide-vue-next";
import type { QuestionInfo } from "@/lib/question-types";

/**
 * An agent's question as the More… sheet shows it: the question, its options as rows (a tap answers; with several
 * allowed, tap to pick and Send answer), "Or answer in your own words" when it takes a typed answer, and Skip.
 * Answers go as the desktop QuestionCard sends them: the picked labels.
 */
const props = defineProps<{ question: QuestionInfo; busy?: boolean; more?: number; machineName?: string; sessionTitle?: string; skippable?: boolean }>();
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
  <div data-testid="question-choices">
    <div class="ph-sheet__pad">
      <div
        v-if="machineName || sessionTitle"
        class="ph-ask__meta"
      >
        <span
          v-if="machineName"
          class="ph-machine"
        ><Monitor
          :size="14"
          aria-hidden="true"
        />{{ machineName }}</span>
        <span v-if="sessionTitle">· {{ sessionTitle }}</span>
      </div>
      <p class="qc__q">
        {{ question.question }}
      </p>
      <p
        v-if="more"
        class="qc__more"
      >
        Then {{ more }} more question{{ more === 1 ? "" : "s" }}: answer {{ more === 1 ? "it" : "them" }} in the session.
      </p>
    </div>
    <div class="ph-group">
      <button
        v-for="option in question.options"
        :key="option.label"
        type="button"
        class="ph-row"
        :aria-pressed="question.multiple ? picked.includes(option.label) : undefined"
        :disabled="busy"
        @click="choose(option.label)"
      >
        <span class="ph-row__main">
          <span class="ph-row__title ph-row__title--wrap">{{ option.label }}</span>
          <span
            v-if="option.description"
            class="ph-row__sub ph-row__sub--wrap"
          >{{ option.description }}</span>
        </span>
        <Check
          v-if="question.multiple && picked.includes(option.label)"
          class="ph-row__check"
          :size="22"
          :stroke-width="2.6"
          aria-hidden="true"
        />
      </button>
    </div>
    <div
      v-if="question.multiple"
      class="ph-sheet__pad qc__send"
    >
      <button
        type="button"
        class="ph-btn ph-btn--primary ph-btn--big"
        :disabled="busy || picked.length === 0"
        data-testid="question-send"
        @click="emit('answer', picked)"
      >
        Send answer
      </button>
    </div>
    <template v-if="allowsCustom">
      <div class="ph-group-h">
        Or answer in your own words
      </div>
      <div class="ph-sheet__pad">
        <form
          class="ph-composer ph-glass-field"
          @submit.prevent="sendCustom"
        >
          <textarea
            v-model="custom"
            class="phone-composer-input"
            rows="1"
            placeholder="Your answer"
            aria-label="Your answer"
            data-testid="question-custom"
            @focus="emit('expand')"
          />
          <button
            type="submit"
            class="ph-send"
            :disabled="busy || !custom.trim()"
            aria-label="Send answer"
            data-testid="question-custom-send"
          >
            <ArrowUp
              :size="22"
              :stroke-width="2.6"
              aria-hidden="true"
            />
          </button>
        </form>
      </div>
    </template>
    <button
      v-if="skippable"
      type="button"
      class="ph-link-btn qc__skip"
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
  margin: 10px 0 16px;
  font-size: var(--ph-t-body);
  line-height: 1.4;
}

.qc__more {
  margin: -8px 0 14px;
  font-size: var(--ph-t-foot);
  color: var(--muted);
}

.qc__send {
  margin-top: 16px;
}

.qc__skip {
  margin-top: 8px;
  color: var(--muted);
}

.ph-row__title,
.ph-row__sub {
  display: block;
}
</style>
