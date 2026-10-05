<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { Button } from "@/components/ui/button";
import type { QuestionInfo } from "@/lib/question-types";

/**
 * An agent's question as 44px rows: its options (one, or several when it allows), "Something else…" with a field
 * when it takes a typed answer, then Send answer and Skip. Answers go as the desktop QuestionCard sends them: the
 * picked labels.
 */
const props = defineProps<{ question: QuestionInfo; busy?: boolean; more?: number }>();
const emit = defineEmits<{
  (event: "answer", labels: string[]): void;
  (event: "skip"): void;
}>();

const picked = shallowRef<string[]>([]);
const custom = shallowRef("");
const typing = shallowRef(false);
const allowsCustom = computed(() => props.question.custom !== false);
const answer = computed(() => typing.value && custom.value.trim() ? [...picked.value, custom.value.trim()] : picked.value);

function toggle(label: string): void {
  if (props.question.multiple) {
    picked.value = picked.value.includes(label) ? picked.value.filter((l) => l !== label) : [...picked.value, label];
  } else {
    picked.value = [label];
    typing.value = false;
  }
}

function something(): void {
  typing.value = true;
  if (!props.question.multiple) picked.value = [];
}
</script>

<template>
  <div
    class="qc"
    data-testid="question-choices"
  >
    <p class="qc__q">
      {{ question.question }}
    </p>
    <p
      v-if="more"
      class="qc__more"
    >
      and {{ more }} more question{{ more === 1 ? "" : "s" }} after this one; answer them in the session.
    </p>
    <button
      v-for="option in question.options"
      :key="option.label"
      type="button"
      class="qc__opt"
      :class="{ 'qc__opt--on': picked.includes(option.label) }"
      :aria-pressed="picked.includes(option.label)"
      :disabled="busy"
      @click="toggle(option.label)"
    >
      <span
        class="qc__radio"
        :class="{ 'qc__radio--box': question.multiple }"
        aria-hidden="true"
      />
      <span class="qc__opt-body">
        <span>{{ option.label }}</span>
        <span
          v-if="option.description"
          class="qc__desc"
        >{{ option.description }}</span>
      </span>
    </button>
    <button
      v-if="allowsCustom && !typing"
      type="button"
      class="qc__opt"
      :disabled="busy"
      @click="something"
    >
      <span
        class="qc__radio"
        aria-hidden="true"
      />Something else…
    </button>
    <textarea
      v-if="typing"
      v-model="custom"
      class="qc__input"
      rows="2"
      placeholder="Your answer"
      aria-label="Your answer"
      data-testid="question-custom"
    />
    <div class="qc__actions">
      <Button
        variant="ghost"
        size="sm"
        class="h-11"
        :disabled="busy"
        @click="emit('skip')"
      >
        Skip
      </Button>
      <Button
        class="h-11 flex-1"
        :disabled="busy || answer.length === 0"
        data-testid="question-send"
        @click="emit('answer', answer)"
      >
        Send answer
      </Button>
    </div>
  </div>
</template>

<style scoped>
.qc {
  display: grid;
  gap: 8px;
}

.qc__q {
  font-size: 15px;
  line-height: 1.45;
}

.qc__more {
  font-size: 12px;
  color: var(--muted);
}

.qc__opt {
  display: flex;
  align-items: center;
  gap: 10px;
  min-height: 44px;
  padding: 8px 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  color: var(--text);
  font: inherit;
  font-size: 14px;
  text-align: left;
  cursor: pointer;
}

.qc__opt--on {
  border-color: var(--accent);
  background: var(--accent-dim);
}

.qc__opt-body {
  display: grid;
}

.qc__desc {
  font-size: 12px;
  color: var(--muted);
}

.qc__radio {
  width: 16px;
  height: 16px;
  flex: none;
  border: 1.5px solid var(--muted);
  border-radius: 50%;
}

.qc__radio--box {
  border-radius: 4px;
}

.qc__opt--on .qc__radio {
  border-color: var(--accent);
  background: radial-gradient(circle, var(--accent) 45%, transparent 50%);
}

.qc__opt--on .qc__radio--box {
  background: var(--accent);
}

.qc__input {
  width: 100%;
  padding: 8px 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--main-bg);
  color: var(--text);
  font: inherit;
  font-size: 15px;
}

.qc__actions {
  display: flex;
  gap: 8px;
}
</style>
