<script setup lang="ts">
import { computed, ref, shallowRef } from "vue";
import { Check, MessageCircleQuestion } from "lucide-vue-next";
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";
import type { AccumulatedToolPart } from "@/lib/client-types";
import {
  getQuestionInput,
  getQuestionAnswers,
  getQuestionStatus,
  type QuestionInfo,
} from "@/lib/question-types";

const props = defineProps<{
  part: AccumulatedToolPart;
  sessionId: string;
  onSubmit: (answers: string[][]) => Promise<void>;
  onDismiss: () => Promise<void>;
}>();

// ── Derived state ─────────────────────────────────────────────────────────────

const questionInput = computed(() => getQuestionInput(props.part));
const questionStatus = computed(() => getQuestionStatus(props.part));
const submittedAnswers = computed(() => getQuestionAnswers(props.part));

const isActive = computed(() =>
  questionStatus.value === "pending" || questionStatus.value === "running"
);
const isDismissed = computed(() => questionStatus.value === "error");

// ── Selection state (only used when active) ───────────────────────────────────

// For each question: the labels picked so far.
const selections = shallowRef<string[][]>([]);
// For each question: the typed answer, if any.
const customTexts = ref<string[]>([]);

if (questionInput.value) {
  selections.value = questionInput.value.questions.map(() => []);
  customTexts.value = questionInput.value.questions.map(() => "");
}

function isSelected(questionIdx: number, label: string): boolean {
  return selections.value[questionIdx]?.includes(label) ?? false;
}

function toggleOption(questionIdx: number, label: string, multiple: boolean) {
  const current = selections.value[questionIdx] ?? [];
  let next: string[];

  if (multiple) {
    next = current.includes(label)
      ? current.filter((l) => l !== label)
      : [...current, label];
  } else {
    next = current.includes(label) ? [] : [label];
    // A single answer is either a choice or typed text, not both.
    if (next.length > 0 && customTexts.value[questionIdx]) {
      customTexts.value[questionIdx] = "";
    }
  }

  const updated = [...selections.value];
  updated[questionIdx] = next;
  selections.value = updated;
}

function handleCustomInput(questionIdx: number, question: QuestionInfo) {
  if (!question.multiple && customTexts.value[questionIdx]?.trim()) {
    const updated = [...selections.value];
    updated[questionIdx] = [];
    selections.value = updated;
  }
}

const hasAnyAnswer = computed(() =>
  selections.value.some((picked) => picked.length > 0)
  || customTexts.value.some((text) => text.trim().length > 0)
);

// Number keys pick a choice in the question that has focus (the first one otherwise).
const choiceCount = computed(() => {
  const first = questionInput.value?.questions[0];
  return first ? first.options.length + (first.custom !== false ? 1 : 0) : 0;
});
const keyHint = computed(() => {
  if (choiceCount.value < 2) return "Enter to send";
  return `1–${Math.min(choiceCount.value, 9)} to pick · Enter to send`;
});

function focusedQuestionIndex(target: EventTarget | null): number {
  const block = (target as HTMLElement | null)?.closest?.("[data-question-index]");
  return block ? Number(block.getAttribute("data-question-index")) : 0;
}

function handleCardKeydown(event: KeyboardEvent) {
  if (!questionInput.value || loading.value) return;
  if (event.target instanceof HTMLInputElement) {
    if (event.key === "Enter" && hasAnyAnswer.value) {
      event.preventDefault();
      void submit();
    }
    return;
  }

  if (event.metaKey || event.ctrlKey || event.altKey) return;
  const digit = Number(event.key);
  if (Number.isInteger(digit) && digit >= 1 && digit <= 9) {
    const questionIdx = focusedQuestionIndex(event.target);
    const question = questionInput.value.questions[questionIdx];
    if (!question) return;
    event.preventDefault();
    const option = question.options[digit - 1];
    if (option) {
      toggleOption(questionIdx, option.label, question.multiple ?? false);
    } else if (digit === question.options.length + 1 && question.custom !== false) {
      cardRef.value
        ?.querySelector<HTMLInputElement>(`[data-question-index="${questionIdx}"] .qcard__other-input`)
        ?.focus();
    }
    return;
  }

  if (event.key === "Enter" && hasAnyAnswer.value && !(event.target instanceof HTMLButtonElement)) {
    event.preventDefault();
    void submit();
  }
}

const cardRef = ref<HTMLElement | null>(null);

// ── Submission ────────────────────────────────────────────────────────────────

const loading = shallowRef(false);
const error = shallowRef<string | null>(null);

async function submit() {
  if (!questionInput.value || loading.value) return;

  // Build answers: for each question, merge selection + custom text
  const answers: string[][] = questionInput.value.questions.map((q, i) => {
    const sel = selections.value[i] ?? [];
    const custom = customTexts.value[i]?.trim();
    if (custom) return [...sel, custom];
    return sel;
  });

  loading.value = true;
  error.value = null;
  try {
    await props.onSubmit(answers);
  } catch (e) {
    error.value = e instanceof Error ? e.message : "Failed to submit answer";
  } finally {
    loading.value = false;
  }
}

async function dismiss() {
  if (loading.value) return;
  loading.value = true;
  error.value = null;
  try {
    await props.onDismiss();
  } catch (e) {
    error.value = e instanceof Error ? e.message : "Failed to dismiss";
  } finally {
    loading.value = false;
  }
}

function answerText(questionIdx: number): string {
  const answer = submittedAnswers.value?.[questionIdx];
  return answer && answer.length > 0 ? answer.join(", ") : "No answer";
}
</script>

<template>
  <!-- ── Answered: one quiet row per question, like a finished tool call ── -->
  <div
    v-if="!isActive && submittedAnswers !== null"
    class="qrow-group"
    data-testid="question-card-answered"
  >
    <div
      v-for="(q, qi) in questionInput?.questions ?? []"
      :key="qi"
      class="qrow"
      :title="q.question"
    >
      <MessageCircleQuestion
        class="qrow__icon"
        aria-hidden="true"
      />
      <span class="qrow__label">Asked</span>
      <span class="qrow__question">{{ q.question }}</span>
      <span class="qrow__arrow">→</span>
      <span class="qrow__answer">{{ answerText(qi) }}</span>
      <Check
        class="qrow__done"
        aria-label="Answered"
      />
    </div>
  </div>

  <!-- ── Skipped ── -->
  <div
    v-else-if="isDismissed"
    class="qrow-group"
    data-testid="question-card-dismissed"
  >
    <div
      class="qrow"
      :title="questionInput?.questions[0]?.question"
    >
      <MessageCircleQuestion
        class="qrow__icon"
        aria-hidden="true"
      />
      <span class="qrow__label">Asked</span>
      <span class="qrow__question">{{ questionInput?.questions[0]?.question ?? "Question" }}</span>
      <span class="qrow__skipped">Skipped</span>
    </div>
  </div>

  <!-- ── Waiting on you ── -->
  <article
    v-else-if="isActive && questionInput"
    ref="cardRef"
    class="qcard"
    data-testid="question-card-active"
    tabindex="-1"
    @keydown="handleCardKeydown"
  >
    <header class="qcard__head">
      <MessageCircleQuestion
        class="qcard__head-icon"
        aria-hidden="true"
      />
      <span class="qcard__title">{{ questionInput.questions[0]?.header || "Question" }}</span>
      <span class="qcard__needs">
        <StatusGlyph status="waiting_input" />
        Needs you
      </span>
    </header>

    <section
      v-for="(q, qi) in questionInput.questions"
      :key="qi"
      class="qcard__question"
      :data-question-index="qi"
    >
      <p
        v-if="qi > 0 && q.header"
        class="qcard__subhead"
      >
        {{ q.header }}
      </p>
      <p class="qcard__text">
        {{ q.question }}
      </p>
      <p
        v-if="q.multiple"
        class="qcard__hint"
      >
        Pick any that apply.
      </p>

      <div
        class="qcard__choices"
        :role="q.multiple ? 'group' : 'radiogroup'"
        :aria-label="q.header || q.question"
      >
        <button
          v-for="(opt, oi) in q.options"
          :key="opt.label"
          type="button"
          class="qcard__choice"
          :role="q.multiple ? 'checkbox' : 'radio'"
          :aria-checked="isSelected(qi, opt.label)"
          :data-testid="`question-pill-${opt.label}`"
          :disabled="loading"
          @click="toggleOption(qi, opt.label, q.multiple ?? false)"
        >
          <span
            class="qcard__key"
            aria-hidden="true"
          >
            <Check v-if="q.multiple && isSelected(qi, opt.label)" />
            <template v-else>{{ oi + 1 }}</template>
          </span>
          <span class="qcard__choice-copy">
            <span class="qcard__choice-name">{{ opt.label }}</span>
            <span
              v-if="opt.description"
              class="qcard__choice-desc"
            >{{ opt.description }}</span>
          </span>
        </button>

        <label
          v-if="q.custom !== false"
          class="qcard__choice qcard__choice--other"
          :class="{ 'qcard__choice--typed': (customTexts[qi] ?? '').trim().length > 0 }"
        >
          <span
            class="qcard__key"
            aria-hidden="true"
          >{{ q.options.length + 1 }}</span>
          <input
            v-model="customTexts[qi]"
            type="text"
            class="qcard__other-input"
            placeholder="Something else…"
            aria-label="Type your own answer"
            :disabled="loading"
            @input="handleCustomInput(qi, q)"
          >
        </label>
      </div>
    </section>

    <p
      v-if="error"
      class="qcard__error"
      role="alert"
    >
      {{ error }}
    </p>

    <footer class="qcard__foot">
      <button
        type="button"
        class="qcard__btn qcard__btn--send"
        data-testid="question-submit-button"
        :disabled="loading || !hasAnyAnswer"
        @click="submit"
      >
        {{ loading ? "Sending…" : "Send answer" }}
      </button>
      <button
        type="button"
        class="qcard__btn qcard__btn--skip"
        data-testid="question-dismiss-button"
        :disabled="loading"
        @click="dismiss"
      >
        Skip
      </button>
      <span
        class="qcard__keys"
        aria-hidden="true"
      >{{ keyHint }}</span>
    </footer>
  </article>
</template>

<style scoped>
/* Waiting on you: amber like the session row's "Needs input", so the card reads as the reason. */
.qcard {
  display: grid;
  gap: 12px;
  margin-top: 10px;
  padding: 12px;
  border: 1px solid color-mix(in srgb, var(--status-waiting) 40%, var(--border));
  border-radius: var(--radius-card);
  background: color-mix(in srgb, var(--status-waiting) 5%, var(--card-bg));
}

.qcard:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.qcard__head {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}

.qcard__head-icon {
  width: 15px;
  height: 15px;
  flex-shrink: 0;
  color: var(--status-waiting);
}

.qcard__title {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  color: var(--text);
  font-size: 13px;
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.qcard__needs {
  display: inline-flex;
  flex-shrink: 0;
  align-items: center;
  gap: 6px;
  color: var(--status-waiting);
  font-size: 12px;
  font-weight: 600;
}

.qcard__question {
  display: grid;
  gap: 8px;
}

.qcard__subhead {
  margin: 4px 0 0;
  color: var(--muted);
  font-size: 12px;
  font-weight: 600;
}

.qcard__text {
  margin: 0;
  color: var(--text);
  font-size: 14px;
  line-height: 1.5;
  text-wrap: pretty;
}

.qcard__hint {
  margin: -4px 0 0;
  color: var(--muted);
  font-size: 12px;
}

.qcard__choices {
  display: grid;
  gap: 4px;
}

.qcard__choice {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  width: 100%;
  padding: 8px 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--panel-bg);
  color: var(--text);
  font: inherit;
  text-align: left;
  cursor: pointer;
  transition: border-color var(--transition), background var(--transition);
}

.qcard__choice:hover:not(:disabled) {
  border-color: color-mix(in srgb, var(--accent) 35%, var(--border));
}

.qcard__choice:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -1px;
}

.qcard__choice[aria-checked="true"],
.qcard__choice--typed {
  border-color: var(--accent);
  background: var(--accent-dim);
}

.qcard__choice:disabled {
  opacity: 0.55;
  cursor: default;
}

.qcard__choice--other {
  align-items: center;
  cursor: text;
}

.qcard__choice--other:focus-within {
  border-color: var(--accent);
}

.qcard__key {
  display: grid;
  place-items: center;
  width: 18px;
  height: 18px;
  margin-top: 1px;
  flex-shrink: 0;
  border: 1px solid var(--border);
  border-radius: 5px;
  color: var(--muted);
  font-family: var(--font-mono-stack);
  font-size: 11px;
}

.qcard__key svg {
  width: 11px;
  height: 11px;
  stroke-width: 3;
}

.qcard__choice--other .qcard__key {
  margin-top: 0;
}

.qcard__choice[aria-checked="true"] .qcard__key {
  border-color: var(--accent);
  background: var(--accent);
  color: var(--primary-foreground);
}

.qcard__choice-copy {
  display: grid;
  gap: 1px;
  min-width: 0;
}

.qcard__choice-name {
  font-size: 13px;
  font-weight: 500;
}

.qcard__choice-desc {
  color: var(--muted);
  font-size: 12px;
  line-height: 1.45;
}

.qcard__other-input {
  flex: 1;
  min-width: 0;
  padding: 0;
  border: 0;
  background: transparent;
  color: var(--text);
  font: inherit;
  font-size: 13px;
  outline: none;
}

.qcard__other-input::placeholder {
  color: var(--muted);
}

.qcard__error {
  margin: 0;
  color: var(--error);
  font-size: 12px;
}

.qcard__foot {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
}

.qcard__btn {
  height: 28px;
  padding: 0 14px;
  border: 1px solid transparent;
  border-radius: 999px;
  font: inherit;
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
  transition: opacity var(--transition), background var(--transition), color var(--transition);
}

.qcard__btn:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.qcard__btn:disabled {
  opacity: 0.45;
  cursor: default;
}

.qcard__btn--send {
  background: var(--accent);
  color: var(--primary-foreground);
  font-weight: 600;
}

.qcard__btn--send:hover:not(:disabled) {
  opacity: 0.88;
}

.qcard__btn--skip {
  background: transparent;
  color: var(--muted);
}

.qcard__btn--skip:hover:not(:disabled) {
  color: var(--text);
  background: color-mix(in srgb, var(--text) 5%, transparent);
}

.qcard__keys {
  margin-left: auto;
  color: var(--muted);
  font-size: 11px;
}

/* ── Answered and skipped: a tool row ── */
/* Framed like the message's tool list (MessageBubble .msg-tools). */
.qrow-group {
  display: grid;
  margin: 10px 0 4px;
  padding: 4px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: color-mix(in srgb, var(--text) 3%, transparent);
}

.qrow {
  display: flex;
  align-items: center;
  gap: 9px;
  min-width: 0;
  min-height: 30px;
  padding: 0 8px;
  border-radius: calc(var(--radius-btn) - 2px);
  font-size: 13px;
}

.qrow__icon {
  width: 14px;
  height: 14px;
  flex-shrink: 0;
  color: color-mix(in srgb, var(--muted) 75%, transparent);
}

.qrow__label {
  flex-shrink: 0;
  color: var(--text);
  font-weight: 500;
}

/* The question gives way first; the answer is what the row is for. */
.qrow__question {
  flex: 1 1 0;
  min-width: 0;
  overflow: hidden;
  color: var(--muted);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.qrow__arrow {
  flex-shrink: 0;
  color: var(--muted);
}

.qrow__answer {
  flex: 0 1 auto;
  min-width: 0;
  max-width: 50%;
  overflow: hidden;
  color: var(--text);
  font-weight: 500;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.qrow__done {
  width: 13px;
  height: 13px;
  margin-left: auto;
  flex-shrink: 0;
  color: var(--running);
}

.qrow__skipped {
  margin-left: auto;
  flex-shrink: 0;
  color: var(--muted);
  font-size: 12px;
}
</style>
