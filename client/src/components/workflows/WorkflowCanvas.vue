<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, shallowRef, useTemplateRef, watch } from "vue";
import { AlertCircle, Bot, Clock, FileText, GitFork, Plus, Settings, UserRound } from "lucide-vue-next";
import { isLoop, targetTitle, type WorkflowDraft, type WorkflowProblem } from "@/lib/workflow-draft";
import type { WorkflowStep } from "@/lib/workflows";

/**
 * The designer's canvas: the workflow's steps top to bottom, between a Starts from box and the end, with a + between
 * steps and loops drawn as arcs on the left. Clicking a step opens it in the inspector; the Starts from box opens the
 * workflow's own settings.
 */
const props = defineProps<{
  draft: WorkflowDraft;
  errors: WorkflowProblem[];
  /** The step open in the inspector, or null for the workflow's settings. */
  selected: number | null;
  /** "Strong · Opus 5.5" for a role, the model as it is when pinned. */
  modelLabel: (model: string | null) => string;
}>();

const emit = defineEmits<{
  select: [index: number | null];
  insert: [index: number, kind: "agent" | "you"];
}>();

const flow = useTemplateRef<HTMLDivElement>("flow");
const addAt = shallowRef<number | null>(null);

interface Arc {
  key: string;
  d: string;
  label: string;
  x: number;
  y: number;
  missingMax: boolean;
}
const arcs = shallowRef<Arc[]>([]);

/** A + before every step and one after the last: each slot is a connector, then its step when there is one. */
const slots = computed<(WorkflowStep | null)[]>(() => [...props.draft.steps, null]);

function errorsOf(index: number): WorkflowProblem[] {
  return props.errors.filter((error) => error.step === index);
}

function outcomeChips(step: WorkflowStep, index: number) {
  return step.outcomes.map((outcome) => {
    const to = step.routes[outcome];
    const loop = isLoop(props.draft, index, to);
    return {
      outcome,
      loop,
      text: to ? `${outcome} → ${targetTitle(props.draft, to)}${loop && step.maxLoops ? ` ≤${step.maxLoops}` : ""}` : outcome,
    };
  });
}

function toggleAdd(index: number): void {
  addAt.value = addAt.value === index ? null : index;
}

function add(kind: "agent" | "you"): void {
  if (addAt.value === null) return;
  emit("insert", addAt.value, kind);
  addAt.value = null;
}

function closeMenu(event: MouseEvent): void {
  if (addAt.value === null) return;
  if (event.target instanceof Element && event.target.closest(".wf-conn")) return;
  addAt.value = null;
}

function onKeydown(event: KeyboardEvent): void {
  if (event.key === "Escape") addAt.value = null;
}

/** The loops, as arcs from a step's left edge back up to the step it sends work to. */
function drawLoops(): void {
  const root = flow.value;
  if (!root) return;
  const box = root.getBoundingClientRect();
  const next: Arc[] = [];
  let lane = 0;
  props.draft.steps.forEach((step, index) => {
    for (const outcome of step.outcomes) {
      const target = step.routes[outcome];
      if (!isLoop(props.draft, index, target)) continue;
      const from = root.querySelector<HTMLElement>(`[data-step-index="${index}"]`);
      const to = root.querySelector<HTMLElement>(`[data-step-index="${props.draft.steps.findIndex((s) => s.id === target)}"]`);
      if (!from || !to) continue;
      const a = from.getBoundingClientRect();
      const b = to.getBoundingClientRect();
      const y1 = a.top - box.top + Math.min(a.height / 2, 22);
      const y2 = b.top - box.top + Math.min(b.height / 2, 22);
      const x0 = a.left - box.left;
      const x = -18 - lane * 24;
      const r = 8;
      const top = Math.min(y1, y2);
      const self = from === to;
      const d = self
        ? `M ${x0} ${y1 + 6} H ${x + r} Q ${x} ${y1 + 6} ${x} ${y1 - 2} Q ${x} ${y1 - 10} ${x + r} ${y1 - 10} H ${x0}`
        : `M ${x0} ${y1} H ${x + r} Q ${x} ${y1} ${x} ${y1 - r} V ${top + r} Q ${x} ${y2} ${x + r} ${y2} H ${x0}`;
      next.push({
        key: `${step.id}:${outcome}`,
        d,
        label: `${outcome} · ${step.maxLoops ? `at most ${step.maxLoops}×` : "needs a max"}`,
        x,
        y: (y1 + y2) / 2,
        missingMax: !step.maxLoops,
      });
      lane++;
    }
  });
  arcs.value = next;
}

let observer: ResizeObserver | null = null;

onMounted(() => {
  document.addEventListener("click", closeMenu);
  void nextTick(drawLoops);
  if (typeof ResizeObserver !== "undefined" && flow.value) {
    observer = new ResizeObserver(() => drawLoops());
    observer.observe(flow.value);
  }
});

onBeforeUnmount(() => {
  document.removeEventListener("click", closeMenu);
  observer?.disconnect();
});

watch(() => [props.draft, props.errors, props.selected], () => void nextTick(drawLoops), { deep: false });
</script>

<template>
  <div
    class="wf-canvas"
    data-testid="workflow-canvas"
    @keydown="onKeydown"
  >
    <div
      ref="flow"
      class="wf-flow"
    >
      <button
        type="button"
        class="wf-node wf-node--term wf-node--settings"
        :class="{ 'wf-node--selected': selected === null }"
        data-testid="workflow-canvas-settings"
        @click="emit('select', null)"
      >
        <span><b>Starts from</b> · a sentence you type &nbsp;·&nbsp; <b>Runs in</b> · a new worktree</span>
        <span class="wf-node__settings"><Settings aria-hidden="true" />Workflow settings</span>
      </button>

      <template
        v-for="(step, slot) in slots"
        :key="`${slot}:${step?.id ?? 'end'}`"
      >
        <div
          class="wf-conn"
          :class="{ 'wf-conn--open': addAt === slot }"
        >
          <button
            type="button"
            class="wf-conn__add"
            aria-label="Add a step here"
            :data-testid="`workflow-add-${slot}`"
            @click.stop="toggleAdd(slot)"
          >
            <Plus aria-hidden="true" />
          </button>
          <div
            v-if="addAt === slot"
            class="wf-add-menu"
            role="menu"
          >
            <button
              type="button"
              role="menuitem"
              data-testid="workflow-add-agent"
              @click="add('agent')"
            >
              <Bot aria-hidden="true" /><span>Agent step<small>A new session with instructions and a model</small></span>
            </button>
            <button
              type="button"
              role="menuitem"
              data-testid="workflow-add-you"
              @click="add('you')"
            >
              <UserRound aria-hidden="true" /><span>You decide<small>Stop and ask; each choice leads somewhere</small></span>
            </button>
            <button
              type="button"
              role="menuitem"
              disabled
            >
              <GitFork aria-hidden="true" /><span>In parallel<small>Arrives with Stage 2</small></span>
            </button>
            <button
              type="button"
              role="menuitem"
              disabled
            >
              <Clock aria-hidden="true" /><span>Wait<small>Arrives with Stage 3</small></span>
            </button>
          </div>
        </div>

        <button
          v-if="step"
          type="button"
          class="wf-node"
          :class="{
            'wf-node--you': step.kind === 'you',
            'wf-node--optional': step.optional,
            'wf-node--selected': selected === slot,
            'wf-node--error': errorsOf(slot).length > 0,
          }"
          :data-step-index="slot"
          :data-testid="`workflow-node-${step.id}`"
          @click="emit('select', slot)"
        >
          <span class="wf-node__top">
            <span class="wf-node__icon">
              <UserRound
                v-if="step.kind === 'you'"
                aria-hidden="true"
              />
              <Bot
                v-else
                aria-hidden="true"
              />
            </span>
            <span class="wf-node__title">{{ step.title || "Untitled" }}</span>
            <span class="wf-node__kind">{{ step.kind === "you" ? "You decide" : "Agent" }}</span>
          </span>

          <template v-if="step.kind === 'you'">
            <span class="wf-node__ask">{{ step.ask }}</span>
            <span class="wf-node__choices">
              <span
                v-for="choice in step.choices"
                :key="choice.label"
                class="wf-node__choice"
              >
                {{ choice.label }}<span
                  v-if="choice.note"
                  class="wf-node__muted"
                >&nbsp;(with a note)</span>
                <span class="wf-node__to">→ {{ targetTitle(draft, choice.to) }}</span>
              </span>
            </span>
          </template>

          <template v-else>
            <span class="wf-node__meta">
              <span><Bot aria-hidden="true" />{{ modelLabel(step.model) }}</span>
              <span v-if="step.agent">{{ step.agent }}</span>
              <span
                v-if="step.skill"
                class="wf-mono"
              >{{ step.skill }}</span>
            </span>
            <span
              v-if="step.optional || step.finishYou || step.finishAgent || step.writes.length"
              class="wf-node__badges"
            >
              <span
                v-if="step.optional"
                class="wf-badge"
              >Optional · off</span>
              <span
                v-if="step.finishYou"
                class="wf-badge wf-badge--you"
              ><UserRound aria-hidden="true" />You finish</span>
              <span
                v-else-if="step.finishAgent"
                class="wf-badge"
              >Always the agent</span>
              <span
                v-if="step.writes.length"
                class="wf-badge wf-mono"
              ><FileText aria-hidden="true" />writes {{ step.writes.length }}</span>
            </span>
            <span class="wf-node__outcomes">
              <span
                v-for="chip in outcomeChips(step, slot)"
                :key="chip.outcome"
                class="wf-outcome"
                :class="{ 'wf-outcome--loop': chip.loop }"
              >{{ chip.text }}</span>
            </span>
          </template>

          <span
            v-for="error in errorsOf(slot)"
            :key="`${error.line}:${error.message}`"
            class="wf-node__error"
          ><AlertCircle aria-hidden="true" />{{ error.message }}</span>
        </button>
      </template>
      <div class="wf-node wf-node--term">
        <span><b>End</b> · the run's sessions stay in Sessions</span>
      </div>

      <svg
        class="wf-loops"
        aria-hidden="true"
      >
        <defs>
          <marker
            id="wf-arrow"
            viewBox="0 0 10 10"
            refX="8"
            refY="5"
            markerWidth="7"
            markerHeight="7"
            orient="auto-start-reverse"
          >
            <path
              d="M 0 0 L 10 5 L 0 10 z"
              fill="var(--queued)"
            />
          </marker>
        </defs>
        <path
          v-for="arc in arcs"
          :key="arc.key"
          :d="arc.d"
          fill="none"
          :stroke="arc.missingMax ? 'var(--error)' : 'var(--queued)'"
          stroke-width="1.5"
          stroke-dasharray="4 3"
          marker-end="url(#wf-arrow)"
        />
      </svg>
      <span
        v-for="arc in arcs"
        :key="`label-${arc.key}`"
        class="wf-loop-label"
        :class="{ 'wf-loop-label--missing': arc.missingMax }"
        :style="{ left: `${arc.x}px`, top: `${arc.y}px` }"
        data-testid="workflow-loop-label"
      >{{ arc.label }}</span>
    </div>
  </div>
</template>

<style scoped>
.wf-canvas {
  position: relative;
  flex: 1;
  min-height: 0;
  overflow: auto;
  background-image: radial-gradient(var(--wf-border-strong) 1px, transparent 1px);
  background-size: 16px 16px;
}

.wf-flow {
  position: relative;
  display: flex;
  width: min(400px, calc(100% - 120px));
  flex-direction: column;
  align-items: stretch;
  margin: 0 auto 0 max(96px, calc(50% - 200px));
  padding: 24px 0 40px;
}

.wf-node {
  position: relative;
  display: flex;
  flex-direction: column;
  align-items: stretch;
  padding: 10px 12px;
  border: 1px solid var(--wf-border-strong);
  border-radius: var(--radius-card);
  background: var(--card-bg);
  color: var(--text);
  font: inherit;
  font-size: 13px;
  text-align: left;
  cursor: pointer;
  transition: border-color var(--transition), box-shadow var(--transition);
}

.wf-node:hover {
  border-color: color-mix(in srgb, var(--accent) 50%, var(--wf-border-strong));
}

.wf-node--selected,
.wf-node--selected:hover {
  border-color: var(--accent);
  box-shadow: 0 0 0 3px var(--accent-dim);
}

.wf-node--optional {
  border-style: dashed;
}

.wf-node--you {
  border-color: color-mix(in srgb, var(--idle) 35%, var(--wf-border-strong));
  background: color-mix(in srgb, var(--idle) 6%, var(--card-bg));
}

.wf-node--error,
.wf-node--error:hover {
  border-color: var(--error);
  box-shadow: 0 0 0 3px color-mix(in srgb, var(--error) 15%, transparent);
}

.wf-node--term {
  display: flex;
  flex-direction: row;
  align-items: center;
  padding: 8px 12px;
  border-style: dashed;
  background: var(--wf-raise);
  color: var(--muted);
  font-size: 12px;
  cursor: default;
}

.wf-node--term b {
  color: var(--text);
  font-weight: 500;
}

.wf-node--settings {
  cursor: pointer;
}

.wf-node__settings {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  margin-left: auto;
  white-space: nowrap;
}

.wf-node__settings svg,
.wf-node__meta svg,
.wf-badge svg,
.wf-node__error svg {
  width: 12px;
  height: 12px;
}

.wf-node__top {
  display: flex;
  align-items: center;
  gap: 8px;
}

.wf-node__icon {
  display: grid;
  width: 24px;
  height: 24px;
  flex: none;
  place-items: center;
  border-radius: 6px;
  background: var(--wf-active);
  color: var(--muted);
}

.wf-node__icon svg {
  width: 14px;
  height: 14px;
}

.wf-node--you .wf-node__icon {
  background: var(--wf-idle-dim);
  color: var(--idle);
}

.wf-node__title {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.wf-node__kind,
.wf-node__muted {
  color: var(--muted);
  font-size: 11px;
}

.wf-node__ask {
  margin-top: 4px;
  color: var(--muted);
  font-size: 12px;
}

.wf-node__choices {
  display: flex;
  flex-direction: column;
  gap: 4px;
  margin-top: 8px;
}

.wf-node__choice {
  display: flex;
  align-items: center;
  padding: 4px 8px;
  border: 1px solid var(--border);
  border-radius: 6px;
  background: var(--panel-bg);
  font-size: 12px;
}

.wf-node__to {
  margin-left: auto;
  color: var(--muted);
  font-size: 11px;
}

.wf-node__meta {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 10px;
  margin-top: 6px;
  color: var(--muted);
  font-size: 12px;
}

.wf-node__meta span {
  display: inline-flex;
  align-items: center;
  gap: 4px;
}

.wf-node__badges,
.wf-node__outcomes {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  margin-top: 6px;
}

.wf-badge {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 0 6px;
  border: 1px solid var(--wf-border-strong);
  border-radius: 999px;
  color: var(--muted);
  font-size: 10.5px;
  white-space: nowrap;
}

.wf-badge--you {
  border-color: color-mix(in srgb, var(--accent) 45%, transparent);
  color: var(--accent);
}

.wf-mono {
  font-family: var(--font-mono-stack);
}

.wf-outcome {
  padding: 1px 7px;
  border: 1px solid var(--border);
  border-radius: 999px;
  color: var(--muted);
  font-family: var(--font-mono-stack);
  font-size: 11px;
}

.wf-outcome--loop {
  border-color: color-mix(in srgb, var(--queued) 40%, transparent);
  color: var(--queued);
}

.wf-node__error {
  display: flex;
  align-items: center;
  gap: 5px;
  margin-top: 6px;
  color: var(--error);
  font-size: 11.5px;
}

.wf-conn {
  position: relative;
  display: flex;
  height: 30px;
  justify-content: center;
}

.wf-conn::before {
  position: absolute;
  top: 0;
  bottom: 0;
  left: 50%;
  width: 1px;
  background: var(--wf-border-strong);
  content: "";
}

.wf-conn__add {
  position: relative;
  z-index: 1;
  display: grid;
  width: 20px;
  height: 20px;
  align-self: center;
  place-items: center;
  border: 1px solid var(--wf-border-strong);
  border-radius: 50%;
  background: var(--panel-bg);
  color: var(--muted);
  opacity: 0;
  transition: opacity var(--transition);
  cursor: pointer;
}

.wf-conn__add svg {
  width: 12px;
  height: 12px;
}

.wf-conn:hover .wf-conn__add,
.wf-conn__add:focus-visible,
.wf-conn--open .wf-conn__add {
  opacity: 1;
}

.wf-add-menu {
  position: absolute;
  z-index: 5;
  top: 26px;
  left: 50%;
  width: 250px;
  padding: 4px;
  border: 1px solid var(--wf-border-strong);
  border-radius: var(--radius-card);
  background: var(--card-bg);
  box-shadow: 0 12px 32px -12px rgb(0 0 0 / 35%);
  transform: translateX(-50%);
}

.wf-add-menu button {
  display: flex;
  width: 100%;
  align-items: flex-start;
  gap: 8px;
  padding: 6px 8px;
  border: 0;
  border-radius: 6px;
  background: none;
  color: var(--text);
  font: inherit;
  font-size: 13px;
  text-align: left;
  cursor: pointer;
}

.wf-add-menu button svg {
  width: 14px;
  height: 14px;
  flex: none;
  margin-top: 2px;
}

.wf-add-menu button:hover:not(:disabled) {
  background: var(--wf-hover);
}

.wf-add-menu button:disabled {
  cursor: not-allowed;
  opacity: 0.5;
}

.wf-add-menu small {
  display: block;
  color: var(--muted);
  font-size: 11px;
}

.wf-loops {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  overflow: visible;
  pointer-events: none;
}

.wf-loop-label {
  position: absolute;
  padding: 1px 6px;
  border: 1px solid color-mix(in srgb, var(--queued) 35%, transparent);
  border-radius: 999px;
  background: var(--panel-bg);
  color: var(--queued);
  font-size: 11px;
  white-space: nowrap;
  transform: translate(-50%, -50%) rotate(-90deg);
  pointer-events: none;
}

.wf-loop-label--missing {
  border-color: var(--error);
  color: var(--error);
}
</style>
