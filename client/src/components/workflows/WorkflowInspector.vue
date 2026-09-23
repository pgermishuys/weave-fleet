<script setup lang="ts">
import { computed, nextTick, shallowRef, useTemplateRef, watch } from "vue";
import { Bot, Plus, Trash2, UserRound, Workflow as WorkflowIcon } from "lucide-vue-next";
import {
  dropUnusedMax,
  END,
  loopOutcomes,
  newOutcome,
  removeOutcome,
  renameOutcome,
  renameStepId,
  setRoute,
  updateChoice,
  updateStep,
  variablesFor,
  type WorkflowDraft,
} from "@/lib/workflow-draft";
import { WORKFLOW_ROLES, isRole, type WorkflowStep } from "@/lib/workflows";

/**
 * The designer's inspector: the selected step's settings, or the workflow's own when no step is selected. Every edit
 * emits the whole new draft; the server checks it.
 */
const props = defineProps<{
  draft: WorkflowDraft;
  selected: number | null;
  /** The file's name, e.g. `deps.yaml`. */
  fileName: string;
  modelLabel: (model: string | null) => string;
  /** Fleet's built-in skills, offered in the skill list. */
  skills: string[];
}>();

const emit = defineEmits<{
  update: [draft: WorkflowDraft];
  remove: [index: number];
}>();

/** `{{request}}`, written out so a template can show it. */
function braces(variable: string): string {
  return "{{" + variable + "}}";
}

const promptRef = useTemplateRef<HTMLTextAreaElement>("prompt");

const step = computed<WorkflowStep | null>(() => (props.selected === null ? null : props.draft.steps[props.selected] ?? null));
const index = computed(() => props.selected ?? -1);

/** The id as typed; it changes the step, and every reference, when the field is left. */
const idDraft = shallowRef("");
const idError = shallowRef<string | null>(null);
watch(() => step.value?.id, (id) => {
  idDraft.value = id ?? "";
  idError.value = null;
}, { immediate: true });

/** A pinned model is typed; picking "Pinned model" shows the field. */
const pinning = shallowRef(false);
watch(() => props.selected, () => {
  pinning.value = !!step.value?.model && !isRole(step.value.model);
}, { immediate: true });

function set(patch: Partial<WorkflowStep>): void {
  if (props.selected === null) return;
  emit("update", dropUnusedMax(updateStep(props.draft, props.selected, patch), props.selected));
}

function setWorkflow(patch: Partial<Pick<WorkflowDraft, "name" | "description" | "placeholder">>): void {
  emit("update", { ...props.draft, ...patch });
}

function commitId(): void {
  const current = step.value;
  if (!current) return;
  const next = idDraft.value.trim();
  if (next === current.id) {
    idError.value = null;
    return;
  }
  if (!next) {
    idError.value = "A step needs an id.";
    return;
  }
  if (next === END || props.draft.steps.some((other) => other.id === next)) {
    idError.value = next === END ? "\"end\" is where a run finishes; pick another id." : "Another step has that id.";
    return;
  }
  idError.value = null;
  emit("update", renameStepId(props.draft, current.id, next));
}

/** Where outcomes and choices can lead from this step: the next step, any other step, or the end. */
const targets = computed(() => props.draft.steps
  .map((other, i) => ({ id: other.id, title: other.title || other.id, i }))
  .filter((other) => other.i !== props.selected || step.value?.kind === "agent"));

const loops = computed(() => (props.selected === null ? [] : loopOutcomes(props.draft, props.selected)));

const modelChoice = computed(() => {
  const model = step.value?.model ?? "";
  if (pinning.value || (model && !isRole(model))) return "pinned";
  return model;
});

function pickModel(value: string): void {
  if (value === "pinned") {
    pinning.value = true;
    set({ model: isRole(step.value?.model) ? "" : step.value?.model ?? "" });
    return;
  }
  pinning.value = false;
  set({ model: value });
}

type Finisher = "" | "you" | "agent";
const finisher = computed<Finisher>(() => (step.value?.finishYou ? "you" : step.value?.finishAgent ? "agent" : ""));
const FINISHERS: { value: Finisher; label: string; detail: string }[] = [
  { value: "", label: "The agent, unless Check with me is on", detail: "It calls fleet_step_done. Check with me turns it into a step you finish." },
  { value: "you", label: "You", detail: "Only you can move it on. The agent has no fleet_step_done here." },
  { value: "agent", label: "Always the agent", detail: "Ignores Check with me, for steps like pushing a branch." },
];

function pickFinisher(value: Finisher): void {
  set({ finishYou: value === "you", finishAgent: value === "agent" });
}

async function insertVariable(variable: string): Promise<void> {
  const current = step.value;
  const area = promptRef.value;
  if (!current) return;
  const text = current.prompt ?? "";
  const chip = braces(variable);
  const start = area ? area.selectionStart : text.length;
  const end = area ? area.selectionEnd : text.length;
  set({ prompt: text.slice(0, start) + chip + text.slice(end) });
  await nextTick();
  if (area) {
    area.focus();
    area.setSelectionRange(start + chip.length, start + chip.length);
  }
}

function routeValue(outcome: string): string {
  return step.value?.routes[outcome] ?? "";
}

function pickRoute(outcome: string, target: string): void {
  if (!step.value) return;
  set(setRoute(step.value, outcome, target === "" ? null : target));
}

function setMax(value: string): void {
  const n = Number.parseInt(value, 10);
  set({ maxLoops: Number.isFinite(n) && n > 0 ? n : null });
}

function setOutcome(from: string, to: string): void {
  if (!step.value) return;
  set(renameOutcome(step.value, from, to.trim()));
}

function setWrite(at: number, value: string): void {
  if (!step.value) return;
  set({ writes: step.value.writes.map((path, i) => (i === at ? value : path)) });
}

function setChoiceTarget(at: number, target: string): void {
  if (!step.value) return;
  set(updateChoice(step.value, at, { to: target }));
}
</script>

<template>
  <aside
    class="wf-insp"
    aria-label="Inspector"
    data-testid="workflow-inspector"
  >
    <template v-if="!step">
      <h3>
        <span class="wf-insp__icon"><WorkflowIcon aria-hidden="true" /></span>Workflow
        <span class="wf-insp__tag">{{ fileName }}</span>
      </h3>
      <div class="wf-field">
        <label for="wf-name">Name</label>
        <input
          id="wf-name"
          :value="draft.name"
          data-testid="workflow-name"
          @input="setWorkflow({ name: ($event.target as HTMLInputElement).value })"
        >
        <p class="wf-help">
          Shown in the Library and on the run. The file name stays as it was created.
        </p>
      </div>
      <div class="wf-field">
        <label for="wf-description">Description</label>
        <textarea
          id="wf-description"
          class="wf-field__prose"
          :value="draft.description ?? ''"
          placeholder="What it does, in a sentence or two. Shown in the Library."
          @input="setWorkflow({ description: ($event.target as HTMLTextAreaElement).value || null })"
        />
      </div>
      <div class="wf-field">
        <label for="wf-hint">Run box hint</label>
        <input
          id="wf-hint"
          :value="draft.placeholder ?? ''"
          placeholder="e.g. What should it fix?"
          @input="setWorkflow({ placeholder: ($event.target as HTMLInputElement).value || null })"
        >
        <p class="wf-help">
          The placeholder in the Run box, where the request is typed.
        </p>
      </div>
      <div class="wf-field">
        <span class="wf-field__label">Starts from</span>
        <div class="wf-radio">
          <div class="wf-radio__option wf-radio__option--on">
            <i /><span>A sentence you type<small>It becomes {{ braces("request") }}.</small></span>
          </div>
          <div class="wf-radio__option wf-radio__option--later">
            <i /><span>A GitHub issue or pull request<small>Arrives with Stage 2</small></span>
          </div>
        </div>
      </div>
      <div class="wf-field">
        <span class="wf-field__label">Runs in</span>
        <div class="wf-radio">
          <div class="wf-radio__option wf-radio__option--on">
            <i /><span>A new worktree<small>From the base branch picked in the Run box. Every step works in it.</small></span>
          </div>
          <div class="wf-radio__option wf-radio__option--later">
            <i /><span>The pull request's branch<small>Arrives with Stage 2</small></span>
          </div>
        </div>
      </div>
      <div class="wf-field">
        <span class="wf-field__label">Steps</span>
        <div class="wf-row">
          {{ draft.steps.length }} step{{ draft.steps.length === 1 ? "" : "s" }}<span class="wf-row__muted">click one to edit it, or add one with +</span>
        </div>
      </div>
    </template>

    <template v-else>
      <h3>
        <span
          class="wf-insp__icon"
          :class="{ 'wf-insp__icon--you': step.kind === 'you' }"
        >
          <UserRound
            v-if="step.kind === 'you'"
            aria-hidden="true"
          />
          <Bot
            v-else
            aria-hidden="true"
          />
        </span>
        {{ step.kind === "you" ? "You decide" : "Agent step" }}
        <span class="wf-insp__tag">id: {{ step.id }}</span>
      </h3>

      <div class="wf-field">
        <label for="wf-title">Title</label>
        <input
          id="wf-title"
          :value="step.title"
          data-testid="workflow-step-title"
          @input="set({ title: ($event.target as HTMLInputElement).value })"
        >
        <p class="wf-help">
          Loops and choices point at the id, so renaming is safe.
        </p>
      </div>

      <div class="wf-field">
        <label for="wf-id">Step id</label>
        <input
          id="wf-id"
          v-model="idDraft"
          class="wf-mono"
          :class="{ 'wf-bad': idError }"
          data-testid="workflow-step-id"
          @change="commitId"
          @keydown.enter.prevent="commitId"
        >
        <p
          v-if="idError"
          class="wf-help wf-help--error"
          role="alert"
        >
          {{ idError }}
        </p>
        <p
          v-else
          class="wf-help"
        >
          Lowercase letters, digits and dashes. Changing it changes every loop and choice that leads here, and {{ braces(`steps.${step.id}.summary`) }} and .files in the prompts.
        </p>
      </div>

      <template v-if="step.kind === 'you'">
        <div class="wf-field">
          <label for="wf-ask">Question</label>
          <input
            id="wf-ask"
            :value="step.ask ?? ''"
            @input="set({ ask: ($event.target as HTMLInputElement).value })"
          >
        </div>
        <div class="wf-field">
          <span class="wf-field__label">Choices</span>
          <div
            v-for="(choice, at) in step.choices"
            :key="at"
            class="wf-choice"
            data-testid="workflow-choice"
          >
            <input
              class="wf-mini"
              :value="choice.label"
              aria-label="Choice"
              @input="set(updateChoice(step, at, { label: ($event.target as HTMLInputElement).value }))"
            >
            <select
              class="wf-mini wf-mini--select"
              :value="choice.to"
              aria-label="Leads to"
              @change="setChoiceTarget(at, ($event.target as HTMLSelectElement).value)"
            >
              <option
                v-for="target in targets"
                :key="target.id"
                :value="target.id"
              >
                → {{ target.title }}
              </option>
              <option :value="END">
                → End the run
              </option>
            </select>
            <label class="wf-check">
              <input
                type="checkbox"
                :checked="choice.note"
                @change="set(updateChoice(step, at, { note: ($event.target as HTMLInputElement).checked }))"
              >note
            </label>
            <button
              type="button"
              class="wf-icon-btn"
              aria-label="Remove the choice"
              @click="set({ choices: step.choices.filter((_, i) => i !== at) })"
            >
              <Trash2 aria-hidden="true" />
            </button>
          </div>
          <button
            type="button"
            class="wf-add"
            @click="set({ choices: [...step.choices, { label: 'New choice', to: END, note: false }] })"
          >
            <Plus aria-hidden="true" />Add a choice
          </button>
          <p class="wf-help">
            A choice with a note asks you what should change, and the step it leads to gets your note. A forward choice with two or more agent steps after it also offers "check with me after each step".
          </p>
        </div>
      </template>

      <template v-else>
        <div class="wf-field">
          <span class="wf-field__label">Runs as</span>
          <div class="wf-sel-row">
            <input
              class="wf-mini"
              :value="step.agent ?? ''"
              list="wf-agents"
              placeholder="Default agent"
              aria-label="Agent"
              @input="set({ agent: ($event.target as HTMLInputElement).value.trim() || null })"
            >
            <datalist id="wf-agents">
              <option value="build" />
              <option value="plan" />
            </datalist>
            <select
              class="wf-mini wf-mini--select"
              :value="modelChoice"
              aria-label="Model"
              data-testid="workflow-step-model"
              @change="pickModel(($event.target as HTMLSelectElement).value)"
            >
              <option
                v-for="role in WORKFLOW_ROLES"
                :key="role"
                :value="role"
              >
                {{ modelLabel(role) }}
              </option>
              <option value="pinned">
                Pinned model…
              </option>
            </select>
          </div>
          <input
            v-if="modelChoice === 'pinned'"
            class="wf-mini wf-mono wf-sel-row__wide"
            :value="step.model ?? ''"
            placeholder="provider/model, e.g. github-copilot/gpt-5.4-mini"
            aria-label="Pinned model"
            @input="set({ model: ($event.target as HTMLInputElement).value.trim() })"
          >
          <div class="wf-sel-row">
            <input
              class="wf-mini wf-mono"
              :value="step.skill ?? ''"
              list="wf-skills"
              placeholder="No skill"
              aria-label="Skill"
              @input="set({ skill: ($event.target as HTMLInputElement).value.trim() || null })"
            >
            <datalist id="wf-skills">
              <option
                v-for="skill in skills"
                :key="skill"
                :value="skill"
              />
            </datalist>
            <input
              class="wf-mini"
              :value="step.effort ?? ''"
              list="wf-efforts"
              placeholder="Effort: default"
              aria-label="Effort"
              @input="set({ effort: ($event.target as HTMLInputElement).value.trim() || null })"
            >
            <datalist id="wf-efforts">
              <option value="low" />
              <option value="medium" />
              <option value="high" />
            </datalist>
          </div>
          <p class="wf-help">
            Roles map to your models in Settings → Workflows. A pinned model has to be one everyone who runs this has.
          </p>
        </div>

        <div class="wf-field">
          <span class="wf-field__label">Who finishes it</span>
          <div
            class="wf-radio"
            role="radiogroup"
            aria-label="Who finishes it"
          >
            <button
              v-for="option in FINISHERS"
              :key="option.value"
              type="button"
              role="radio"
              class="wf-radio__option"
              :class="{ 'wf-radio__option--on': finisher === option.value }"
              :aria-checked="finisher === option.value"
              :data-testid="`workflow-finish-${option.value || 'default'}`"
              @click="pickFinisher(option.value)"
            >
              <i /><span>{{ option.label }}<small>{{ option.detail }}</small></span>
            </button>
          </div>
        </div>

        <div class="wf-field">
          <label for="wf-prompt">Instructions</label>
          <textarea
            id="wf-prompt"
            ref="prompt"
            :value="step.prompt ?? ''"
            spellcheck="false"
            data-testid="workflow-step-prompt"
            @input="set({ prompt: ($event.target as HTMLTextAreaElement).value })"
          />
          <div class="wf-vars">
            <button
              v-for="variable in variablesFor(draft, index)"
              :key="variable"
              type="button"
              class="wf-var"
              :title="`Insert ${braces(variable)}`"
              @click="insertVariable(variable)"
            >
              {{ braces(variable) }}
            </button>
          </div>
          <p class="wf-help">
            Include <code>{{ braces("request") }}</code> so the step sees any constraints in it. A line whose variables are all empty is left out.
          </p>
        </div>

        <div class="wf-field">
          <span class="wf-field__label">Outcomes</span>
          <div
            v-for="outcome in step.outcomes"
            :key="outcome"
            class="wf-outcome-row"
            :data-testid="`workflow-outcome-${outcome}`"
          >
            <input
              class="wf-mini wf-mono"
              :value="outcome"
              aria-label="Outcome"
              @change="setOutcome(outcome, ($event.target as HTMLInputElement).value)"
            >
            <select
              class="wf-mini wf-mini--select"
              :value="routeValue(outcome)"
              aria-label="Leads to"
              @change="pickRoute(outcome, ($event.target as HTMLSelectElement).value)"
            >
              <option value="">
                → Next step
              </option>
              <option
                v-for="target in targets"
                :key="target.id"
                :value="target.id"
              >
                → {{ target.title }}
              </option>
              <option :value="END">
                → End the run
              </option>
            </select>
            <label
              v-if="loops.includes(outcome)"
              class="wf-max"
            >max
              <input
                class="wf-mini wf-mini--num"
                :class="{ 'wf-bad': !step.maxLoops }"
                :value="step.maxLoops ?? ''"
                inputmode="numeric"
                aria-label="Maximum times"
                data-testid="workflow-outcome-max"
                @input="setMax(($event.target as HTMLInputElement).value)"
              >
            </label>
            <button
              type="button"
              class="wf-icon-btn"
              aria-label="Remove the outcome"
              @click="set(removeOutcome(step, outcome))"
            >
              <Trash2 aria-hidden="true" />
            </button>
          </div>
          <p
            v-if="loops.length > 1"
            class="wf-help"
            data-testid="workflow-shared-max"
          >
            {{ step.maxLoops ? `At most ${step.maxLoops}` : "The max is" }} for this step's loops: {{ loops.join(" and ") }} share it.
          </p>
          <button
            type="button"
            class="wf-add"
            @click="set({ outcomes: [...step.outcomes, newOutcome(step)] })"
          >
            <Plus aria-hidden="true" />Add an outcome
          </button>
          <p class="wf-help">
            An outcome that goes back to an earlier step is a loop. It needs a max; the next time goes to you.
          </p>
        </div>

        <div class="wf-field">
          <span class="wf-field__label">Writes</span>
          <div
            v-for="(path, at) in step.writes"
            :key="at"
            class="wf-path"
          >
            <input
              class="wf-mini wf-mono"
              :value="path"
              aria-label="Declared file"
              @input="setWrite(at, ($event.target as HTMLInputElement).value)"
            >
            <button
              type="button"
              class="wf-icon-btn"
              aria-label="Remove the file"
              @click="set({ writes: step.writes.filter((_, i) => i !== at) })"
            >
              <Trash2 aria-hidden="true" />
            </button>
          </div>
          <p
            v-if="step.writes.length === 0"
            class="wf-help wf-help--flush"
          >
            No declared files.
          </p>
          <button
            type="button"
            class="wf-add"
            @click="set({ writes: [...step.writes, 'docs/{{slug}}.md'] })"
          >
            <Plus aria-hidden="true" />Add a file
          </button>
          <p class="wf-help">
            Paths in the run's worktree. Fleet checks they exist before the next step and commits them; a path git ignores stays uncommitted.
          </p>
        </div>

        <div class="wf-field">
          <span class="wf-field__label">Optional</span>
          <div
            class="wf-radio"
            role="radiogroup"
            aria-label="Optional"
          >
            <button
              type="button"
              role="radio"
              class="wf-radio__option"
              :class="{ 'wf-radio__option--on': !step.optional }"
              :aria-checked="!step.optional"
              @click="set({ optional: false, optionalHint: null })"
            >
              <i /><span>Always runs</span>
            </button>
            <button
              type="button"
              role="radio"
              class="wf-radio__option"
              :class="{ 'wf-radio__option--on': step.optional }"
              :aria-checked="step.optional"
              data-testid="workflow-step-optional"
              @click="set({ optional: true })"
            >
              <i /><span>Off unless switched on for a run<small>Shows as a switch in the Run box</small></span>
            </button>
          </div>
          <input
            v-if="step.optional"
            class="wf-mini wf-sel-row__wide"
            :value="step.optionalHint ?? ''"
            placeholder="When to switch it on, e.g. For UI and new features"
            aria-label="When to switch it on"
            @input="set({ optionalHint: ($event.target as HTMLInputElement).value || null })"
          >
        </div>
      </template>

      <button
        type="button"
        class="wf-remove"
        data-testid="workflow-remove-step"
        @click="emit('remove', index)"
      >
        <Trash2 aria-hidden="true" />Remove {{ step.title || "this step" }}
      </button>
    </template>
  </aside>
</template>

<style scoped>
.wf-insp {
  display: flex;
  width: 360px;
  flex: none;
  flex-direction: column;
  gap: 14px;
  padding: 14px 16px 24px;
  overflow-y: auto;
  border-left: 1px solid var(--border);
  font-size: 13px;
}

h3 {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 0;
  font-size: 13px;
  font-weight: 600;
}

.wf-insp__icon {
  display: grid;
  width: 24px;
  height: 24px;
  place-items: center;
  border-radius: 6px;
  background: var(--wf-active);
  color: var(--muted);
}

.wf-insp__icon svg {
  width: 14px;
  height: 14px;
}

.wf-insp__icon--you {
  background: var(--wf-idle-dim);
  color: var(--idle);
}

.wf-insp__tag {
  margin-left: auto;
  color: var(--muted);
  font-family: var(--font-mono-stack);
  font-size: 11px;
  font-weight: 400;
}

.wf-field label:not(.wf-check, .wf-max),
.wf-field__label {
  display: block;
  margin-bottom: 4px;
  color: var(--muted);
  font-size: 11px;
  font-weight: 500;
}

.wf-field > input,
.wf-field > textarea {
  width: 100%;
  padding: 6px 8px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--wf-raise);
  color: var(--text);
  font: inherit;
  font-size: 12.5px;
}

.wf-field > textarea {
  min-height: 110px;
  resize: vertical;
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  line-height: 1.55;
}

.wf-field > textarea.wf-field__prose {
  min-height: 56px;
  font-family: inherit;
  font-size: 12.5px;
}

.wf-help {
  margin: 4px 0 0;
  color: var(--muted);
  font-size: 11px;
}

.wf-help--flush {
  margin-top: 0;
}

.wf-help--error {
  color: var(--error);
}

.wf-help code {
  font-family: var(--font-mono-stack);
}

.wf-mono {
  font-family: var(--font-mono-stack);
}

.wf-bad {
  border-color: var(--error) !important;
}

.wf-mini {
  min-width: 0;
  flex: 1;
  padding: 4px 8px;
  border: 1px solid var(--border);
  border-radius: 6px;
  background: var(--wf-raise);
  color: var(--text);
  font: inherit;
  font-size: 12px;
}

.wf-mini.wf-mono {
  font-size: 11.5px;
}

.wf-mini--select {
  flex: 1.2;
}

.wf-mini--num {
  width: 44px;
  flex: none;
  text-align: center;
}

.wf-sel-row {
  display: flex;
  gap: 6px;
}

.wf-sel-row + .wf-sel-row,
.wf-sel-row__wide {
  margin-top: 6px;
}

.wf-sel-row__wide {
  width: 100%;
}

.wf-radio {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.wf-radio__option {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 6px 8px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: none;
  color: var(--text);
  font: inherit;
  font-size: 12px;
  text-align: left;
}

button.wf-radio__option {
  cursor: pointer;
}

.wf-radio__option--on {
  border-color: var(--accent);
  background: var(--accent-dim);
}

.wf-radio__option--later {
  opacity: 0.55;
}

.wf-radio__option i {
  width: 12px;
  height: 12px;
  flex: none;
  margin-top: 2px;
  border: 1.5px solid var(--wf-border-strong);
  border-radius: 50%;
}

.wf-radio__option--on i {
  border: 4px solid var(--accent);
}

.wf-radio__option small {
  display: block;
  color: var(--muted);
  font-size: 11px;
}

.wf-row {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 5px 8px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  font-size: 12px;
}

.wf-row__muted {
  margin-left: auto;
  color: var(--muted);
}

.wf-vars {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  margin-top: 6px;
}

.wf-var {
  padding: 1px 6px;
  border: 0;
  border-radius: 4px;
  background: var(--accent-dim);
  color: var(--accent);
  font-family: var(--font-mono-stack);
  font-size: 10.5px;
  cursor: pointer;
}

.wf-outcome-row,
.wf-choice,
.wf-path {
  display: flex;
  align-items: center;
  gap: 6px;
}

.wf-outcome-row + .wf-outcome-row,
.wf-choice + .wf-choice,
.wf-path + .wf-path {
  margin-top: 4px;
}

.wf-max,
.wf-check {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  color: var(--muted);
  font-size: 11.5px;
  white-space: nowrap;
}

.wf-icon-btn {
  display: grid;
  width: 24px;
  height: 24px;
  flex: none;
  place-items: center;
  border: 0;
  border-radius: 6px;
  background: none;
  color: var(--muted);
  cursor: pointer;
}

.wf-icon-btn:hover {
  background: var(--wf-hover);
  color: var(--text);
}

.wf-icon-btn svg,
.wf-add svg,
.wf-remove svg {
  width: 12px;
  height: 12px;
}

.wf-add {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  margin-top: 6px;
  padding: 2px 4px;
  border: 0;
  background: none;
  color: var(--accent);
  font: inherit;
  font-size: 12px;
  cursor: pointer;
}

.wf-remove {
  display: inline-flex;
  align-self: flex-start;
  align-items: center;
  gap: 6px;
  padding: 4px 8px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: none;
  color: var(--error);
  font: inherit;
  font-size: 12px;
  cursor: pointer;
}
</style>
