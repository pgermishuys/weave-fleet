<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { Check, ChevronDown, ChevronRight } from "lucide-vue-next";
import ProgressSubagentCard from "@/components/canvas/ProgressSubagentCard.vue";
import { useSessionProgress } from "@/composables/use-session-progress";
import {
  codeSpans,
  currentPlanStep,
  type SessionPlanGroup,
  type SessionPlanStep,
  type SessionSubagent,
} from "@/lib/session-progress";
import type { TodoItem } from "@/lib/todo-utils";

/**
 * The Progress tab: the plan the session is working through, with its live todo list under the current step,
 * or just the todo list. Fleet works all of it out from the session's events; it's read-only.
 */
const props = defineProps<{
  sessionId: string;
}>();

const { progress } = useSessionProgress(() => props.sessionId);

const plan = computed(() => progress.value?.plan ?? null);
const todos = computed<readonly TodoItem[]>(() => progress.value?.todos ?? []);
const current = computed(() => (plan.value ? currentPlanStep(plan.value) : null));
const isFlat = computed(() => (plan.value?.groups.length ?? 0) <= 1);

// Finished groups start folded; a click overrides that per group.
const groupOverrides = shallowRef<Record<string, boolean>>({});

function groupKey(group: SessionPlanGroup, index: number): string {
  return `${index}:${group.title ?? ""}`;
}

function groupDone(group: SessionPlanGroup): number {
  return group.steps.filter((step) => step.checked).length;
}

function isGroupComplete(group: SessionPlanGroup): boolean {
  return groupDone(group) === group.steps.length;
}

function isGroupOpen(group: SessionPlanGroup, index: number): boolean {
  return isFlat.value || (groupOverrides.value[groupKey(group, index)] ?? !isGroupComplete(group));
}

function toggleGroup(group: SessionPlanGroup, index: number): void {
  const key = groupKey(group, index);
  groupOverrides.value = { ...groupOverrides.value, [key]: !isGroupOpen(group, index) };
}

function lastTick(group: SessionPlanGroup): string | null {
  const ticks = group.steps.map((step) => step.tickedAt).filter((at): at is string => at !== null).sort();
  return ticks.at(-1) ?? null;
}

function isCurrent(step: SessionPlanStep): boolean {
  return current.value?.step.key === step.key;
}

function clock(iso: string | null | undefined): string {
  if (!iso) return "";
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? "" : date.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
}

/** Read after the step's title by screen readers, which don't see the glyph or the time column. */
function stepState(step: SessionPlanStep): string {
  if (step.checked) return step.tickedAt ? `ticked at ${clock(step.tickedAt)}` : "ticked";
  return isCurrent(step) ? "next up" : "not started";
}

const todoDone = computed(() => todos.value.filter((todo) => todo.status === "completed").length);

// --- Subagents: each shows under the plan step it was started for; the rest get their own list ---
const subagents = computed<readonly SessionSubagent[]>(() => progress.value?.subagents ?? []);
const stepKeys = computed(() => new Set(plan.value?.groups.flatMap((group) => group.steps.map((step) => step.key)) ?? []));
const unplacedSubagents = computed(() =>
  subagents.value.filter((subagent) => !subagent.stepKey || !stepKeys.value.has(subagent.stepKey)));

function subagentsFor(step: SessionPlanStep): SessionSubagent[] {
  return subagents.value.filter((subagent) => subagent.stepKey === step.key);
}
</script>

<template>
  <section
    class="progress-canvas"
    aria-label="Progress"
  >
    <template v-if="plan && progress">
      <header class="progress-canvas__head">
        <div class="progress-canvas__heading">
          <p class="progress-canvas__eyebrow">
            Plan
          </p>
          <h3 class="progress-canvas__title">
            {{ plan.title ?? plan.path }}
          </h3>
          <p class="progress-canvas__source">
            {{ plan.path }}<template v-if="plan.trackedSince">
              · tracked since {{ clock(plan.trackedSince) }}
            </template>
          </p>
        </div>
        <p
          class="progress-canvas__count"
          :aria-label="`${progress.done} of ${progress.total} steps ticked`"
        >
          {{ progress.done }}<span>/{{ progress.total }}</span>
        </p>
      </header>

      <div
        class="progress-canvas__flow"
        role="img"
        :aria-label="`${progress.done} of ${progress.total} steps ticked`"
      >
        <template v-if="isFlat">
          <span
            v-for="step in plan.groups[0]?.steps ?? []"
            :key="step.key"
            class="progress-canvas__segment"
            :class="{ 'progress-canvas__segment--now': isCurrent(step) }"
          >
            <span
              class="progress-canvas__fill"
              :style="{ width: step.checked ? '100%' : '0%' }"
            />
          </span>
        </template>
        <template v-else>
          <span
            v-for="(group, index) in plan.groups"
            :key="groupKey(group, index)"
            class="progress-canvas__segment"
            :class="{
              'progress-canvas__segment--done': isGroupComplete(group),
              'progress-canvas__segment--now': current?.group === group,
            }"
            :style="{ flexGrow: group.steps.length }"
            :title="`${group.title ?? 'Steps'}: ${groupDone(group)}/${group.steps.length}`"
          >
            <span
              class="progress-canvas__fill"
              :style="{ width: `${(groupDone(group) / group.steps.length) * 100}%` }"
            />
          </span>
        </template>
      </div>

      <p class="progress-canvas__now">
        <template v-if="current">
          <strong v-if="!isFlat && current.group.title">{{ current.group.title }}</strong>
          <template v-if="!isFlat && current.group.title">
            ·
          </template>
          next up: {{ current.step.number ? `step ${current.step.number}` : "the next step" }}
        </template>
        <strong v-else>Every step is ticked</strong>
      </p>

      <div class="progress-canvas__groups">
        <section
          v-for="(group, index) in plan.groups"
          :key="groupKey(group, index)"
          class="progress-group"
          :class="{ 'progress-group--done': isGroupComplete(group) }"
        >
          <button
            v-if="!isFlat"
            type="button"
            class="progress-group__head"
            :aria-expanded="isGroupOpen(group, index)"
            @click="toggleGroup(group, index)"
          >
            <component
              :is="isGroupOpen(group, index) ? ChevronDown : ChevronRight"
              :size="12"
              aria-hidden="true"
            />
            <span class="progress-group__title">{{ group.title ?? "Steps" }}</span>
            <span class="progress-group__meta">
              <Check
                v-if="isGroupComplete(group)"
                :size="12"
                class="progress-group__check"
                aria-hidden="true"
              />
              {{ groupDone(group) }}/{{ group.steps.length }}<template v-if="isGroupComplete(group) && lastTick(group)"> · {{ clock(lastTick(group)) }}</template>
            </span>
          </button>

          <ol
            v-if="isGroupOpen(group, index)"
            class="progress-steps"
          >
            <li
              v-for="step in group.steps"
              :key="step.key"
              class="progress-step"
              :class="{ 'progress-step--ticked': step.checked, 'progress-step--current': isCurrent(step) }"
            >
              <span
                class="progress-step__glyph"
                :class="{
                  'progress-step__glyph--ticked': step.checked,
                  'progress-step__glyph--current': isCurrent(step),
                }"
                aria-hidden="true"
              >
                <Check
                  v-if="step.checked"
                  :size="10"
                  :stroke-width="3"
                />
              </span>
              <span
                class="progress-step__number"
                aria-hidden="true"
              >{{ step.number }}</span>
              <span class="progress-step__title">
                <template
                  v-for="(piece, pieceIndex) in codeSpans(step.title)"
                  :key="pieceIndex"
                >
                  <code v-if="piece.code">{{ piece.text }}</code>
                  <template v-else>{{ piece.text }}</template>
                </template>
                <span
                  v-if="step.subTotal > 0"
                  class="progress-step__sub"
                >{{ step.subDone }}/{{ step.subTotal }}</span>
                <span class="sr-only">, {{ stepState(step) }}</span>
              </span>
              <span
                class="progress-step__meta"
                aria-hidden="true"
              >
                <template v-if="step.checked">{{ clock(step.tickedAt) }}</template>
                <template v-else-if="isCurrent(step)">next</template>
              </span>

              <div
                v-if="subagentsFor(step).length > 0"
                class="progress-step__detail"
              >
                <ProgressSubagentCard
                  v-for="subagent in subagentsFor(step)"
                  :key="subagent.delegationId"
                  :subagent="subagent"
                  :parent-session-id="sessionId"
                />
              </div>

              <div
                v-if="isCurrent(step) && todos.length > 0"
                class="progress-step__detail"
              >
                <p class="progress-canvas__label">
                  Todos · {{ todoDone }} of {{ todos.length }}
                </p>
                <ul class="progress-todos">
                  <li
                    v-for="(todo, todoIndex) in todos"
                    :key="todoIndex"
                    class="progress-todo"
                    :class="`progress-todo--${todo.status}`"
                  >
                    <span
                      class="progress-todo__glyph"
                      aria-hidden="true"
                    />
                    <span>{{ todo.content }}</span>
                  </li>
                </ul>
              </div>
            </li>
          </ol>
        </section>
      </div>
    </template>

    <template v-else-if="progress && todos.length > 0">
      <header class="progress-canvas__head">
        <div class="progress-canvas__heading">
          <p class="progress-canvas__eyebrow">
            Todos
          </p>
          <h3 class="progress-canvas__title">
            The agent's todo list
          </h3>
        </div>
        <p class="progress-canvas__count">
          {{ progress.done }}<span>/{{ progress.total }}</span>
        </p>
      </header>
      <ul class="progress-todos progress-todos--standalone">
        <li
          v-for="(todo, todoIndex) in todos"
          :key="todoIndex"
          class="progress-todo"
          :class="`progress-todo--${todo.status}`"
        >
          <span
            class="progress-todo__glyph"
            aria-hidden="true"
          />
          <span>{{ todo.content }}</span>
        </li>
      </ul>
      <p class="progress-canvas__note">
        This session hasn't written a markdown checklist, so there's no plan to show.
      </p>
    </template>

    <div
      v-else
      class="progress-canvas__empty"
    >
      <strong>Nothing to track yet</strong>
      <p>
        Progress shows up when the agent writes a todo list, or a markdown checklist such as a plan in
        <code>.weave/plans/</code>.
      </p>
    </div>

    <section
      v-if="unplacedSubagents.length > 0"
      class="progress-canvas__subagents"
      aria-label="Subagents"
    >
      <p class="progress-canvas__label">
        Subagents
      </p>
      <ProgressSubagentCard
        v-for="subagent in unplacedSubagents"
        :key="subagent.delegationId"
        :subagent="subagent"
        :parent-session-id="sessionId"
      />
    </section>
  </section>
</template>

<style scoped>
.progress-canvas {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 14px 14px 18px;
  font-size: 12.5px;
  color: var(--text);
}

.progress-canvas__head {
  display: flex;
  align-items: flex-start;
  gap: 12px;
}

.progress-canvas__heading {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 3px;
}

.progress-canvas__eyebrow,
.progress-canvas__label {
  margin: 0;
  font-size: 10px;
  font-weight: 600;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--muted);
}

.progress-canvas__title {
  margin: 0;
  font-size: 14px;
  font-weight: 600;
  text-wrap: balance;
}

.progress-canvas__source {
  margin: 0;
  font-family: var(--font-mono-stack);
  font-size: 11px;
  color: var(--muted);
  overflow-wrap: anywhere;
}

.progress-canvas__count {
  margin: 0;
  font-size: 20px;
  font-weight: 600;
  line-height: 1.1;
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}

.progress-canvas__count span {
  color: var(--muted);
  font-weight: 500;
}

.progress-canvas__flow {
  display: flex;
  gap: 3px;
  height: 8px;
}

.progress-canvas__segment {
  position: relative;
  flex: 1 1 0;
  min-width: 6px;
  overflow: hidden;
  border-radius: 3px;
  background: color-mix(in srgb, var(--text) 9%, transparent);
}

.progress-canvas__fill {
  position: absolute;
  inset: 0 auto 0 0;
  background: var(--accent);
  transition: width var(--transition);
}

.progress-canvas__segment--done .progress-canvas__fill {
  background: color-mix(in srgb, var(--accent) 55%, transparent);
}

.progress-canvas__segment--now {
  box-shadow: 0 0 0 1.5px var(--accent);
}

.progress-canvas__now {
  margin: 0;
  font-size: 12px;
  color: var(--muted);
}

.progress-canvas__now strong {
  color: var(--text);
  font-weight: 500;
}

.progress-canvas__groups {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.progress-group__head {
  display: flex;
  align-items: center;
  gap: 7px;
  width: 100%;
  padding: 6px;
  border: 0;
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--text);
  text-align: left;
  cursor: pointer;
  transition: background var(--transition);
}

.progress-group__head:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
}

.progress-group__head:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.progress-group__head > svg {
  flex-shrink: 0;
  color: var(--muted);
}

.progress-group__title {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 12px;
  font-weight: 600;
}

.progress-group--done .progress-group__title {
  color: var(--muted);
  font-weight: 500;
}

.progress-group__meta {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-size: 11.5px;
  color: var(--muted);
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}

.progress-group__check {
  color: var(--running);
}

.progress-steps {
  display: flex;
  flex-direction: column;
  margin: 0;
  padding: 0 0 4px;
  list-style: none;
}

.progress-step {
  display: grid;
  grid-template-columns: 16px 20px minmax(0, 1fr) auto;
  align-items: start;
  column-gap: 6px;
  padding: 6px;
  border-radius: var(--radius-btn);
}

.progress-step--current {
  background: color-mix(in srgb, var(--accent) 7%, transparent);
}

.progress-step__glyph {
  display: grid;
  place-items: center;
  width: 14px;
  height: 14px;
  margin-top: 1px;
  border: 1.5px solid color-mix(in srgb, var(--text) 22%, transparent);
  border-radius: 50%;
  color: var(--primary-foreground);
}

.progress-step__glyph--ticked {
  border-color: var(--accent);
  border-radius: 4px;
  background: var(--accent);
}

.progress-step__glyph--current {
  border-color: var(--accent);
  box-shadow: inset 0 0 0 2.5px var(--card-bg, var(--panel-bg)), inset 0 0 0 7px var(--accent);
}

.progress-step__number {
  padding-top: 1px;
  font-size: 11.5px;
  color: var(--muted);
  font-variant-numeric: tabular-nums;
  text-align: right;
}

.progress-step__title {
  line-height: 1.4;
  overflow-wrap: anywhere;
}

.progress-step--ticked .progress-step__title {
  color: var(--muted);
}

.progress-step__title code {
  padding: 0 3px;
  border-radius: 4px;
  background: color-mix(in srgb, var(--text) 7%, transparent);
  font-family: var(--font-mono-stack);
  font-size: 11px;
}

.progress-step__sub {
  margin-left: 6px;
  font-size: 11px;
  color: var(--muted);
  font-variant-numeric: tabular-nums;
}

.progress-step__meta {
  padding-top: 2px;
  font-size: 11px;
  color: var(--muted);
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}

.progress-step--current .progress-step__meta {
  color: var(--accent);
  font-weight: 500;
}

.progress-step__detail {
  grid-column: 3 / 5;
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin-top: 7px;
}

.progress-todos {
  display: flex;
  flex-direction: column;
  gap: 3px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.progress-todos--standalone {
  gap: 5px;
}

.progress-todo {
  display: flex;
  align-items: flex-start;
  gap: 7px;
  font-size: 12px;
  line-height: 1.4;
  color: color-mix(in srgb, var(--text) 75%, transparent);
}

.progress-todo__glyph {
  flex-shrink: 0;
  width: 10px;
  height: 10px;
  margin-top: 3px;
  border: 1.5px solid color-mix(in srgb, var(--text) 22%, transparent);
  border-radius: 50%;
}

.progress-todo--in_progress {
  color: var(--text);
  font-weight: 500;
}

.progress-todo--in_progress .progress-todo__glyph {
  border-color: var(--accent);
  background: radial-gradient(circle, var(--accent) 45%, transparent 50%);
}

.progress-todo--completed,
.progress-todo--cancelled {
  color: var(--muted);
}

.progress-todo--completed .progress-todo__glyph {
  border-color: color-mix(in srgb, var(--running) 70%, transparent);
  background: color-mix(in srgb, var(--running) 70%, transparent);
}

.progress-todo--cancelled span:last-child {
  text-decoration: line-through;
}

.progress-canvas__subagents {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.progress-canvas__note {
  margin: 0;
  font-size: 12px;
  color: var(--muted);
}

.progress-canvas__empty {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 18px 4px;
  color: var(--muted);
}

.progress-canvas__empty strong {
  font-size: 13px;
  font-weight: 500;
  color: var(--text);
}

.progress-canvas__empty p {
  margin: 0;
}

.progress-canvas__empty code {
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
}
</style>
