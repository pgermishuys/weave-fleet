<script setup lang="ts">
import { computed, ref, shallowRef } from "vue";
import type { AccumulatedToolPart } from "@/lib/api-types";
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

// For each question: a Set of selected labels
const selections = shallowRef<string[][]>([]);

function initSelections(questions: QuestionInfo[]) {
  selections.value = questions.map(() => []);
}

// Init on mount / when question input arrives
if (questionInput.value) {
  initSelections(questionInput.value.questions);
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
  }

  const updated = [...selections.value];
  updated[questionIdx] = next;
  selections.value = updated;
}

// Custom text inputs
const customTexts = ref<string[]>([]);

function initCustomTexts(questions: QuestionInfo[]) {
  customTexts.value = questions.map(() => "");
}

if (questionInput.value) {
  initCustomTexts(questionInput.value.questions);
}

// ── Form variant logic ────────────────────────────────────────────────────────

/**
 * Determines the render variant for a question:
 * - "buttons" — ≤4 options with no descriptions
 * - "radio" / "checkbox" — more options or has descriptions
 */
function questionVariant(q: QuestionInfo): "buttons" | "list" {
  const hasDescriptions = q.options.some((o) => o.description?.trim());
  if (!hasDescriptions && q.options.length <= 4 && !q.multiple) {
    return "buttons";
  }
  return "list";
}

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

// ── Display label for a completed answer ─────────────────────────────────────

function formatAnswers(answers: string[][]): string {
  return answers
    .map((a) => (a.length === 0 ? "(unanswered)" : a.join(", ")))
    .join(" | ");
}
</script>

<template>
  <!-- ── Answered / completed state ── -->
  <article
    v-if="!isActive && submittedAnswers !== null"
    class="qcard qcard--answered"
  >
    <div class="qcard__header">
      <span class="qcard__kind">Question</span>
      <span class="qcard__title">{{ questionInput?.questions[0]?.header ?? "Question" }}</span>
      <span class="qcard__badge qcard__badge--answered">Answered</span>
    </div>
    <div
      v-for="(q, qi) in questionInput?.questions ?? []"
      :key="qi"
      class="qcard__answered-detail"
    >
      <p class="qcard__question-text qcard__question-text--answered">{{ q.question }}</p>
      <p class="qcard__answer-value">→ {{ submittedAnswers[qi]?.join(", ") ?? "(unanswered)" }}</p>
    </div>
  </article>

  <!-- ── Dismissed state ── -->
  <article
    v-else-if="isDismissed"
    class="qcard qcard--dismissed"
  >
    <div class="qcard__header">
      <span class="qcard__kind">Question</span>
      <span class="qcard__title">{{ questionInput?.questions[0]?.header ?? "Question" }}</span>
      <span class="qcard__badge qcard__badge--dismissed">Dismissed</span>
    </div>
  </article>

  <!-- ── Active form ── -->
  <article
    v-else-if="isActive && questionInput"
    class="qcard qcard--active"
  >
    <div
      v-for="(q, qi) in questionInput.questions"
      :key="qi"
      class="qcard__question"
    >
      <header class="qcard__question-header">
        <span class="qcard__kind">Question</span>
        <span class="qcard__title">{{ q.header }}</span>
      </header>

      <p class="qcard__question-text">{{ q.question }}</p>

      <!-- Pill buttons variant (≤4 single-select without descriptions) -->
      <div
        v-if="questionVariant(q) === 'buttons'"
        class="qcard__options qcard__options--buttons"
      >
        <button
          v-for="opt in q.options"
          :key="opt.label"
          type="button"
          class="qcard__pill"
          :class="{ 'qcard__pill--selected': isSelected(qi, opt.label) }"
          :disabled="loading"
          @click="toggleOption(qi, opt.label, false)"
        >
          {{ opt.label }}
        </button>
      </div>

      <!-- Radio / checkbox list variant -->
      <ul
        v-else
        class="qcard__options qcard__options--list"
      >
        <li
          v-for="opt in q.options"
          :key="opt.label"
          class="qcard__option"
        >
          <label class="qcard__option-label">
            <input
              :type="q.multiple ? 'checkbox' : 'radio'"
              :name="`q-${part.callId}-${qi}`"
              :value="opt.label"
              :checked="isSelected(qi, opt.label)"
              :disabled="loading"
              class="qcard__option-input"
              @change="toggleOption(qi, opt.label, q.multiple ?? false)"
            />
            <span class="qcard__option-text">
              <span class="qcard__option-name">{{ opt.label }}</span>
              <span
                v-if="opt.description"
                class="qcard__option-desc"
              >{{ opt.description }}</span>
            </span>
          </label>
        </li>
      </ul>

      <!-- Custom text input -->
      <div
        v-if="q.custom !== false"
        class="qcard__custom"
      >
        <input
          v-model="customTexts[qi]"
          type="text"
          class="qcard__custom-input"
          placeholder="Type a custom answer…"
          :disabled="loading"
        />
      </div>
    </div>

    <!-- Error message -->
    <p
      v-if="error"
      class="qcard__error"
    >
      {{ error }}
    </p>

    <!-- Actions -->
    <footer class="qcard__actions">
      <button
        type="button"
        class="qcard__btn qcard__btn--submit"
        :disabled="loading"
        @click="submit"
      >
        {{ loading ? "Submitting…" : "Submit" }}
      </button>
      <button
        type="button"
        class="qcard__btn qcard__btn--dismiss"
        :disabled="loading"
        @click="dismiss"
      >
        Dismiss
      </button>
    </footer>
  </article>
</template>

<style scoped>
.qcard {
  margin-top: 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  overflow: hidden;
  background: var(--card-bg);
}

.qcard--active {
  border-color: var(--accent);
}

.qcard--answered {
  opacity: 0.75;
}

.qcard--dismissed {
  opacity: 0.5;
}

/* ── Header ── */
.qcard__header {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  font-size: 11px;
}

.qcard__kind {
  color: var(--accent);
  font-size: 10px;
  font-weight: 600;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.qcard__title {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  color: var(--text);
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.qcard__badge {
  font-size: 10px;
  padding: 2px 6px;
  border-radius: 4px;
}

.qcard__badge--answered {
  background: rgba(34, 197, 94, 0.15);
  color: #22c55e;
}

.qcard__badge--dismissed {
  background: rgba(255, 255, 255, 0.06);
  color: var(--muted);
}

/* ── Answered detail ── */
.qcard__answered-detail {
  padding: 0 12px 10px;
}

.qcard__question-text--answered {
  margin: 0 0 4px;
  color: var(--muted);
  font-size: 12px;
  line-height: 1.4;
}

.qcard__answer-value {
  margin: 0;
  color: var(--text);
  font-size: 12px;
  font-weight: 500;
}

/* ── Per-question block ── */
.qcard__question {
  padding: 10px 12px 0;
}

.qcard__question-header {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 6px;
  font-size: 11px;
}

.qcard__question-text {
  margin: 0 0 10px;
  color: var(--text);
  font-size: 13px;
  line-height: 1.5;
}

/* ── Pill buttons ── */
.qcard__options--buttons {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-bottom: 10px;
}

.qcard__pill {
  padding: 5px 12px;
  border: 1px solid var(--border);
  border-radius: 999px;
  background: transparent;
  color: var(--text);
  font-size: 12px;
  cursor: pointer;
  transition: background-color 0.15s ease, border-color 0.15s ease;
}

.qcard__pill:hover:not(:disabled) {
  background: rgba(255, 255, 255, 0.05);
}

.qcard__pill--selected {
  border-color: var(--accent);
  background: rgba(var(--accent-rgb, 99, 102, 241), 0.15);
  color: var(--accent);
}

.qcard__pill:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

/* ── Radio / checkbox list ── */
.qcard__options--list {
  list-style: none;
  margin: 0 0 10px;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.qcard__option {
  display: block;
}

.qcard__option-label {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  cursor: pointer;
}

.qcard__option-input {
  margin-top: 2px;
  flex-shrink: 0;
  accent-color: var(--accent);
  cursor: pointer;
}

.qcard__option-text {
  display: flex;
  flex-direction: column;
  gap: 1px;
}

.qcard__option-name {
  color: var(--text);
  font-size: 13px;
}

.qcard__option-desc {
  color: var(--muted);
  font-size: 11px;
  line-height: 1.4;
}

/* ── Custom input ── */
.qcard__custom {
  margin-bottom: 10px;
}

.qcard__custom-input {
  width: 100%;
  padding: 6px 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: rgba(255, 255, 255, 0.04);
  color: var(--text);
  font-size: 13px;
  outline: none;
  box-sizing: border-box;
  transition: border-color 0.15s ease;
}

.qcard__custom-input:focus {
  border-color: var(--accent);
}

.qcard__custom-input:disabled {
  opacity: 0.5;
}

/* ── Error ── */
.qcard__error {
  margin: 0 12px 8px;
  color: #f87171;
  font-size: 11px;
}

/* ── Actions ── */
.qcard__actions {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px 12px;
  border-top: 1px solid var(--border);
}

.qcard__btn {
  padding: 5px 14px;
  border: 1px solid transparent;
  border-radius: 6px;
  font-size: 12px;
  cursor: pointer;
  transition: opacity 0.15s ease, background-color 0.15s ease;
}

.qcard__btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.qcard__btn--submit {
  background: var(--accent);
  color: #fff;
  font-weight: 600;
}

.qcard__btn--submit:hover:not(:disabled) {
  opacity: 0.85;
}

.qcard__btn--dismiss {
  background: transparent;
  border-color: var(--border);
  color: var(--muted);
}

.qcard__btn--dismiss:hover:not(:disabled) {
  background: rgba(255, 255, 255, 0.05);
  color: var(--text);
}
</style>
