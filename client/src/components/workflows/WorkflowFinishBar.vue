<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { ArrowRight, Check, CornerUpLeft, FileText, LoaderCircle, UserRound } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";
import { moveBlockedReason, moveLabel, type WorkflowRunMove } from "@/lib/workflows";
import { useWorkflowsStore } from "@/stores/workflows";

/**
 * The bar above the composer while the user and the agent work through a step the user finishes. The agent has no way
 * to end the step; Move on (one button per outcome when the step has more than one) asks it, in this session, to bring
 * the step's files up to date and write a short summary, and the next step starts when that turn ends.
 */
const props = defineProps<{ sessionId: string }>();

const store = useWorkflowsStore();

const run = computed(() => store.runForSession(props.sessionId));
const withYou = computed(() => (run.value?.status === "running" && run.value.withYou?.sessionId === props.sessionId ? run.value.withYou : null));
const declared = computed(() => run.value?.steps.find((step) => step.id === withYou.value?.stepId)?.finishYou ?? false);
const single = computed(() => withYou.value?.moves.length === 1);
const nextTitle = computed(() => (single.value ? withYou.value?.moves[0]?.toTitle ?? "the end of the run" : "the next step"));

const note = shallowRef("");
const busy = shallowRef<string | null>(null);
const error = shallowRef<string | null>(null);

watch(() => withYou.value?.stepId, () => {
  note.value = "";
  error.value = null;
});

const blocked = computed(() => (withYou.value
  ? withYou.value.moves.map((move) => moveBlockedReason(withYou.value!.stepTitle, move)).find((reason) => reason !== null) ?? null
  : null));

const subtitle = computed(() => {
  if (!single.value) return "Pick the outcome. The run goes where it leads.";
  return declared.value
    ? "The agent can't end this step. Keep talking until it's right."
    : "You chose to check each step. Keep talking, or move on.";
});

const handOff = computed(() => {
  const files = withYou.value?.files.length ?? 0;
  return files > 0
    ? `Move on asks the agent, in this session, to bring these files up to date with what you agreed and to write a short summary. Then ${nextTitle.value} starts with the files, the summary and your note.`
    : `Move on asks the agent, in this session, to write a short summary. Then ${nextTitle.value} starts with it and your note.`;
});

/** While the wrap-up runs: where it's going. */
const wrapping = computed(() => {
  const current = withYou.value;
  if (!current?.wrappingUp) return null;
  const next = current.moves.find((move) => move.outcome === current.outcome)?.toTitle ?? "the end of the run";
  return current.files.length > 0
    ? `Wrapping up ${current.stepTitle}: the agent is updating its files and writing a summary for ${next}. Then Fleet checks the files and starts ${next}.`
    : `Wrapping up ${current.stepTitle}: the agent is writing a summary for ${next}. Then Fleet starts ${next}.`;
});

async function moveOn(move: WorkflowRunMove): Promise<void> {
  if (!run.value || !move.allowed) return;
  busy.value = move.outcome;
  error.value = null;
  try {
    await store.moveOn(run.value.id, single.value ? null : move.outcome, note.value.trim() || null);
    note.value = "";
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : "Couldn't move the run on.";
  } finally {
    busy.value = null;
  }
}

async function endRun(): Promise<void> {
  if (!run.value) return;
  busy.value = "end";
  error.value = null;
  try {
    await store.end(run.value.id);
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : "Couldn't end the run.";
  } finally {
    busy.value = null;
  }
}
</script>

<template>
  <div
    v-if="withYou && wrapping"
    class="wf-finish wf-finish--wrapping"
    role="status"
    data-testid="workflow-wrapping-up"
  >
    <StatusGlyph
      status="active"
      label="Wrapping up"
    />
    <span>{{ wrapping }}</span>
  </div>

  <div
    v-else-if="withYou"
    class="wf-finish"
    role="region"
    :aria-label="`You finish ${withYou.stepTitle}`"
    data-testid="workflow-finish-bar"
  >
    <div class="wf-finish__top">
      <UserRound aria-hidden="true" />
      <span class="wf-finish__title">You finish {{ withYou.stepTitle }}</span>
      <span class="wf-finish__sub">· {{ subtitle }}</span>
    </div>

    <div class="wf-finish__row">
      <input
        v-model="note"
        class="wf-finish__note"
        data-testid="workflow-finish-note"
        :placeholder="`Note for ${nextTitle} (optional)`"
        :aria-label="`Note for ${nextTitle}`"
        :disabled="busy !== null"
      >
      <Button
        v-for="(move, index) in withYou.moves"
        :key="move.outcome"
        size="sm"
        :variant="index === 0 ? 'default' : 'outline'"
        :disabled="busy !== null || !move.allowed"
        :title="moveBlockedReason(withYou.stepTitle, move) ?? undefined"
        :data-testid="`workflow-move-${move.outcome}`"
        @click="moveOn(move)"
      >
        <LoaderCircle
          v-if="busy === move.outcome"
          class="size-3.5 animate-spin"
        />
        <CornerUpLeft
          v-else-if="move.back"
          class="size-3.5"
        />
        <Check
          v-else-if="!single"
          class="size-3.5"
        />
        {{ moveLabel(move, single) }}
        <ArrowRight
          v-if="single"
          class="size-3.5"
        />
      </Button>
    </div>

    <p
      v-if="blocked"
      class="wf-finish__blocked"
      data-testid="workflow-finish-blocked"
    >
      {{ blocked }} Pick another outcome, or
      <button
        type="button"
        class="wf-finish__end"
        :disabled="busy !== null"
        data-testid="workflow-finish-end"
        @click="endRun"
      >
        end the run
      </button>.
    </p>

    <div
      v-if="withYou.files.length"
      class="wf-finish__outs"
    >
      Hands on:
      <span
        v-for="file in withYou.files"
        :key="file"
        class="wf-finish__file"
      ><FileText aria-hidden="true" />{{ file }}</span>
    </div>
    <div
      v-else
      class="wf-finish__outs"
    >
      This step declares no files. The next step gets its summary, your note, and the branch as it is.
    </div>
    <p class="wf-finish__hint">
      {{ handOff }}
    </p>
    <p
      v-if="error"
      class="wf-finish__error"
      role="alert"
    >
      {{ error }}
    </p>
  </div>
</template>

<style scoped>
.wf-finish {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin: 0 16px 8px;
  padding: 12px 14px;
  border: 1px solid color-mix(in srgb, var(--accent) 40%, var(--border));
  border-radius: var(--radius-card);
  background: color-mix(in srgb, var(--card-bg) 94%, var(--accent) 6%);
}

.wf-finish--wrapping {
  flex-direction: row;
  align-items: center;
  gap: 8px;
  padding: 9px 12px;
  color: color-mix(in srgb, var(--text) 85%, transparent);
  font-size: 12.5px;
}

.wf-finish__top {
  display: flex;
  min-width: 0;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  font-size: 13.5px;
}

.wf-finish__top svg {
  width: 14px;
  height: 14px;
  color: var(--accent);
}

.wf-finish__title {
  font-weight: 600;
}

.wf-finish__sub {
  color: var(--muted);
  font-size: 12.5px;
}

.wf-finish__row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
}

.wf-finish__note {
  flex: 1 1 220px;
  min-width: 0;
  height: 32px;
  padding: 0 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--bg);
  color: var(--text);
  font: inherit;
  font-size: 13px;
}

.wf-finish__note:focus {
  outline: 2px solid var(--accent);
  outline-offset: -1px;
}

.wf-finish__outs {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  color: var(--muted);
  font-size: 12px;
}

.wf-finish__file {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 1px 7px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  color: var(--text);
  font-family: var(--font-mono);
  font-size: 11px;
}

.wf-finish__file svg {
  width: 12px;
  height: 12px;
  color: var(--muted);
}

.wf-finish__hint,
.wf-finish__blocked {
  margin: 0;
  color: var(--muted);
  font-size: 12px;
  line-height: 1.45;
}

.wf-finish__blocked {
  color: var(--idle);
}

.wf-finish__end {
  border: 0;
  padding: 0;
  background: none;
  color: var(--accent);
  cursor: pointer;
  font: inherit;
  text-decoration: underline;
}

.wf-finish__error {
  margin: 0;
  color: var(--error);
  font-size: 12px;
}
</style>
