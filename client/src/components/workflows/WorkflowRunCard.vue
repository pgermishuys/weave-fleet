<script lang="ts">
/** The waits whose files were opened already, so a second look at the session doesn't open them again. */
const opened = new Set<string>();
</script>

<script setup lang="ts">
import { computed, nextTick, shallowRef, useTemplateRef, watch } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { ArrowRight, Check, CornerUpLeft, FileCheck2, LoaderCircle, Play, RotateCcw, Send, TriangleAlert, UserRound } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import { useSettingsNav } from "@/composables/use-settings-nav";
import { fileName, listFiles, MOVE_ON_ANYWAY, WITHOUT_SKILL, type WorkflowRunChoice, type WorkflowRunSession } from "@/lib/workflows";
import { useCanvasesStore } from "@/stores/canvases";
import { useSidebarStore } from "@/stores/sidebar";
import { useWorkflowsStore } from "@/stores/workflows";

/**
 * The run's card in a step session's conversation. When the run waits on the user here, it asks: a You step's choices
 * (a "Send back" choice takes a note that goes into that step's next prompt; a choice that goes on to more steps is
 * offered two ways, let it run or check with me after each step), an outcome for a step that stopped without one, or
 * whether to move on anyway past a missing file or a wrap-up that didn't finish. At a You step, the files the step
 * before it declares open on the right. Once this step is done and the run has moved on, it says where the run went.
 */
const props = defineProps<{ sessionId: string }>();

const router = useRouter();
const store = useWorkflowsStore();
const canvases = useCanvasesStore();
const sidebar = useSidebarStore();
const { setActiveSection } = useSettingsNav();
const noteRef = useTemplateRef<HTMLTextAreaElement>("note");

const run = computed(() => store.runForSession(props.sessionId));
const waiting = computed(() => (run.value?.status === "waiting" && run.value.waiting?.sessionId === props.sessionId ? run.value.waiting : null));

const noteFor = shallowRef<WorkflowRunChoice | null>(null);
const note = shallowRef("");
const busy = shallowRef<string | null>(null);
const error = shallowRef<string | null>(null);

watch(() => waiting.value?.stepId, () => {
  noteFor.value = null;
  note.value = "";
  error.value = null;
});

// "Approve the plan" with the plan open next to it.
watch(waiting, (current) => {
  const files = current?.kind === "you" ? current.files ?? [] : [];
  if (!run.value || files.length === 0) return;
  const key = `${run.value.id}:${current!.stepId}:${run.value.steps.find((step) => step.id === current!.stepId)?.visits ?? 0}`;
  if (opened.has(key)) return;
  opened.add(key);
  for (const path of files) canvases.openFile(props.sessionId, path, { keep: true });
  if (!sidebar.rightPanelAsSheet) sidebar.setRightPanelCollapsed(false);
}, { immediate: true });

/** What the card asks, and at a You step, which files are open and what to choose. */
const ask = computed(() => {
  const current = waiting.value;
  if (!current) return "";
  if (current.kind !== "you") return current.message;
  const files = current.files ?? [];
  const open = files.length === 0 ? "" : ` ${listFiles(files.map(fileName))} ${files.length === 1 ? "is" : "are"} open on the right.`;
  const involvement = current.choices.some((choice) => choice.involvement)
    ? " Then choose how involved you want to be in the rest of the run; you can change it later from the header."
    : "";
  return `${current.question ?? ""}${open}${involvement}`;
});

/** What "let it run" means here: "Implement, Review and Check it runs go ahead on their own. You're asked again at …". */
const letItRun = computed(() => {
  const steps = waiting.value?.thenAlone ?? [];
  const alone = steps.length ? `${listFiles(steps)} go ahead on their own.` : "The next steps go ahead on their own.";
  return waiting.value?.nextYouTitle ? `${alone} You're asked again at ${waiting.value.nextYouTitle}.` : alone;
});

/** "Send back to Plan", or "Implement" for a choice leading there. */
function stepTitle(id: string | null): string {
  if (!id || id === "end") return "the end";
  return run.value?.steps.find((step) => step.id === id)?.title ?? id;
}

function describe(choice: WorkflowRunChoice): string | null {
  if (!waiting.value || waiting.value.kind === "start-failed" || waiting.value.kind === "skill-off") return null;
  if (!choice.to) return null;
  return choice.to === "end" ? "Ends the run" : `Starts ${stepTitle(choice.to)}`;
}

async function choose(choice: WorkflowRunChoice, checkWithMe?: boolean): Promise<void> {
  if (!run.value) return;
  if (choice.note && noteFor.value?.id !== choice.id) {
    noteFor.value = choice;
    await nextTick();
    noteRef.value?.focus();
    return;
  }

  busy.value = checkWithMe === undefined ? choice.id : `${choice.id}:${checkWithMe}`;
  error.value = null;
  try {
    await store.answer(run.value.id, choice.id, choice.note ? note.value.trim() : null, checkWithMe);
    noteFor.value = null;
    note.value = "";
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : "Couldn't answer the run.";
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

/** After this session's step: where the run is now, when it's somewhere else. */
const movedOn = computed(() => {
  const current = run.value;
  if (!current || waiting.value) return null;
  const mine = current.sessions.find((s) => s.sessionId === props.sessionId);
  if (!mine?.outcome) return null;
  const later = current.sessions.at(-1);
  const title = current.steps.find((s) => s.id === mine.stepId)?.title ?? mine.stepId;
  if (later && later.sessionId !== props.sessionId) {
    return { text: `${title} finished: ${mine.outcome}.`, next: stepTitle(later.stepId), sessionId: later.sessionId, ...checkedFiles(mine) };
  }
  if (current.status === "done" || current.status === "ended") {
    return { text: `${title} finished: ${mine.outcome}. ${current.result ?? "The run is done."}`, next: null, sessionId: null, ...checkedFiles(mine) };
  }
  if (current.status === "waiting" && current.waiting?.sessionId) {
    return { text: `${title} finished: ${mine.outcome}.`, next: current.waiting.stepTitle, sessionId: current.waiting.sessionId, ...checkedFiles(mine) };
  }
  return null;
});

/** The files Fleet found before the next step started, and what came of committing them. */
function checkedFiles(visit: WorkflowRunSession): { files: string[]; commit: string | null; commitError: string | null } {
  if (!visit.filesChecked) return { files: [], commit: null, commitError: null };
  return { files: visit.files ?? [], commit: visit.filesCommit ?? null, commitError: visit.filesCommitError ?? null };
}

function openSkillsSettings(): void {
  setActiveSection("skills");
  void router.navigate({ to: "/settings" });
}

function openSession(sessionId: string): void {
  void router.navigate({ to: "/sessions/$id", params: { id: sessionId }, search: { instanceId: undefined, parentSessionId: undefined } });
}
</script>

<template>
  <div
    v-if="waiting"
    class="wf-card"
    role="region"
    :aria-label="waiting.stepTitle"
    data-testid="workflow-card"
  >
    <div class="wf-card__top">
      <UserRound aria-hidden="true" />
      <span class="wf-card__title">{{ waiting.kind === "you" ? waiting.stepTitle : `${waiting.stepTitle} needs you` }}</span>
      <span class="wf-card__pill">Needs you</span>
    </div>
    <p class="wf-card__ask">
      {{ ask }}
    </p>

    <textarea
      v-if="noteFor"
      ref="note"
      v-model="note"
      class="wf-card__note"
      data-testid="workflow-card-note"
      :placeholder="`What should change? This goes into ${stepTitle(noteFor.to)}'s next prompt.`"
      rows="2"
    />

    <div class="wf-card__acts">
      <template v-if="noteFor">
        <Button
          size="sm"
          :disabled="busy !== null || note.trim().length === 0"
          data-testid="workflow-card-send-note"
          @click="choose(noteFor)"
        >
          <LoaderCircle
            v-if="busy === noteFor.id"
            class="size-3.5 animate-spin"
          />
          <Send
            v-else
            class="size-3.5"
          />
          Send back to {{ stepTitle(noteFor.to) }}
        </Button>
        <Button
          size="sm"
          variant="ghost"
          :disabled="busy !== null"
          @click="noteFor = null"
        >
          Cancel
        </Button>
      </template>
      <template v-else>
        <template
          v-for="choice in waiting.choices"
          :key="`${choice.id}-ways`"
        >
          <template v-if="choice.involvement">
            <button
              type="button"
              class="wf-opt"
              :disabled="busy !== null"
              data-testid="workflow-card-let-it-run"
              @click="choose(choice, false)"
            >
              <b>
                <LoaderCircle
                  v-if="busy === `${choice.id}:false`"
                  class="size-3.5 animate-spin"
                />
                <Play
                  v-else
                  class="size-3.5"
                />
                {{ choice.label }}, let it run
              </b>
              <small>{{ letItRun }}</small>
            </button>
            <button
              type="button"
              class="wf-opt wf-opt--primary"
              :disabled="busy !== null"
              data-testid="workflow-card-check-with-me"
              @click="choose(choice, true)"
            >
              <b>
                <LoaderCircle
                  v-if="busy === `${choice.id}:true`"
                  class="size-3.5 animate-spin"
                />
                <UserRound
                  v-else
                  class="size-3.5"
                />
                {{ choice.label }}, check with me after each step
              </b>
              <small>Each step waits for you to move it on, and you can talk to the agent in between.</small>
            </button>
          </template>
        </template>
        <Button
          v-for="(choice, index) in waiting.choices.filter((c) => !c.involvement)"
          :key="choice.id"
          size="sm"
          :variant="index === 0 && !waiting.choices.some((c) => c.involvement) ? 'default' : 'outline'"
          :title="describe(choice) ?? undefined"
          :disabled="busy !== null"
          :data-testid="`workflow-card-choice-${waiting.choices.indexOf(choice)}`"
          @click="choose(choice)"
        >
          <LoaderCircle
            v-if="busy === choice.id"
            class="size-3.5 animate-spin"
          />
          <CornerUpLeft
            v-else-if="choice.note"
            class="size-3.5"
          />
          <RotateCcw
            v-else-if="choice.id === 'retry'"
            class="size-3.5"
          />
          <Check
            v-else-if="index === 0"
            class="size-3.5"
          />
          {{ waiting.kind === "you" || choice.id === "retry" || choice.id === MOVE_ON_ANYWAY || choice.id === WITHOUT_SKILL ? choice.label : `Continue as ${choice.label}` }}
        </Button>
      </template>
      <Button
        size="sm"
        variant="ghost"
        class="wf-card__end"
        :disabled="busy !== null"
        data-testid="workflow-card-end"
        @click="endRun"
      >
        End run
      </Button>
    </div>
    <p
      v-if="waiting.kind === 'no-outcome' || waiting.kind === 'loop-limit'"
      class="wf-card__hint"
    >
      Or reply to the agent below: if it finishes the step, the run carries on.
    </p>
    <p
      v-if="waiting.kind === 'skill-off'"
      class="wf-card__hint"
      data-testid="workflow-card-skill-off"
    >
      Turn it on in <button
        type="button"
        class="wf-card__link"
        @click="openSkillsSettings"
      >
        Settings → Skills
      </button>, then retry. Or start the step without it for the rest of this run.
    </p>
    <p
      v-if="error"
      class="wf-card__error"
      role="alert"
    >
      {{ error }}
    </p>
  </div>

  <div
    v-else-if="movedOn"
    class="wf-card wf-card--resolved"
    data-testid="workflow-card-resolved"
  >
    <Check aria-hidden="true" />
    <span>{{ movedOn.text }}</span>
    <span
      v-if="movedOn.files.length"
      class="wf-card__checked"
      data-testid="workflow-card-files-checked"
    >
      <FileCheck2 aria-hidden="true" />
      {{ movedOn.commit ? `Files checked and committed as ${movedOn.commit}` : "Files checked" }}: {{ listFiles(movedOn.files.map(fileName)) }}
    </span>
    <span
      v-if="movedOn.files.length && movedOn.commitError"
      class="wf-card__commit-error"
      data-testid="workflow-card-commit-failed"
      :title="movedOn.commitError"
    >
      <TriangleAlert aria-hidden="true" />
      Fleet couldn't commit {{ movedOn.files.length === 1 ? "it" : "them" }}: {{ movedOn.commitError }}
    </span>
    <button
      v-if="movedOn.sessionId"
      type="button"
      class="wf-card__link"
      @click="openSession(movedOn.sessionId)"
    >
      {{ movedOn.next }} <ArrowRight aria-hidden="true" />
    </button>
  </div>
</template>

<style scoped>
.wf-card {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin: 0 16px 8px;
  padding: 12px 14px;
  border: 1px solid color-mix(in srgb, var(--idle) 45%, var(--border));
  border-radius: var(--radius-card);
  background: color-mix(in srgb, var(--card-bg) 94%, var(--idle) 6%);
}

.wf-card__top {
  display: flex;
  align-items: center;
  gap: 7px;
  font-size: 13.5px;
  font-weight: 600;
}

.wf-card__top svg {
  width: 14px;
  height: 14px;
  color: var(--idle);
}

.wf-card__title {
  flex: 1;
  min-width: 0;
}

.wf-card__pill {
  padding: 1px 8px;
  border: 1px solid color-mix(in srgb, var(--idle) 50%, transparent);
  border-radius: 999px;
  color: var(--idle);
  font-size: 11px;
  font-weight: 500;
}

.wf-card__ask {
  margin: 0;
  color: color-mix(in srgb, var(--text) 85%, transparent);
  font-size: 13px;
  line-height: 1.5;
}

.wf-card__note {
  width: 100%;
  min-height: 56px;
  padding: 8px 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--bg);
  color: var(--text);
  font: inherit;
  font-size: 13px;
  resize: vertical;
}

.wf-card__note:focus {
  outline: 2px solid var(--accent);
  outline-offset: -1px;
}

.wf-card__acts {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
}

.wf-opt {
  display: flex;
  flex: 1 1 240px;
  flex-direction: column;
  align-items: flex-start;
  gap: 3px;
  padding: 9px 11px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--bg);
  color: var(--text);
  cursor: pointer;
  font: inherit;
  text-align: left;
}

.wf-opt:hover:not(:disabled) {
  border-color: color-mix(in srgb, var(--accent) 50%, var(--border));
}

.wf-opt:disabled {
  cursor: default;
  opacity: 0.6;
}

.wf-opt b {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
  font-weight: 600;
}

.wf-opt small {
  color: var(--muted);
  font-size: 12px;
  line-height: 1.4;
}

.wf-opt--primary {
  border-color: color-mix(in srgb, var(--accent) 55%, var(--border));
  background: color-mix(in srgb, var(--bg) 90%, var(--accent) 10%);
}

.wf-opt--primary b svg {
  color: var(--accent);
}

.wf-card__checked {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  color: var(--muted);
}

.wf-card__checked svg {
  width: 12px;
  height: 12px;
  color: var(--running);
}

.wf-card__commit-error {
  display: inline-flex;
  min-width: 0;
  align-items: center;
  gap: 4px;
  color: var(--muted);
}

.wf-card__commit-error svg {
  width: 12px;
  height: 12px;
  flex: none;
  color: var(--idle);
}

.wf-card__end {
  margin-left: auto;
  color: var(--muted);
}

.wf-card__hint {
  margin: 0;
  color: var(--muted);
  font-size: 12px;
}

.wf-card__link {
  padding: 0;
  border: 0;
  background: none;
  color: var(--accent);
  font: inherit;
  cursor: pointer;
}

.wf-card__link:hover {
  text-decoration: underline;
}

.wf-card__error {
  margin: 0;
  color: var(--error);
  font-size: 12px;
}

.wf-card--resolved {
  flex-direction: row;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  border-color: var(--border);
  background: transparent;
  color: var(--muted);
  font-size: 12.5px;
}

.wf-card--resolved > svg {
  width: 14px;
  height: 14px;
  flex-shrink: 0;
  color: var(--running);
}

.wf-card__link {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  margin-left: auto;
  border: 0;
  background: none;
  color: var(--accent);
  cursor: pointer;
  font: inherit;
  white-space: nowrap;
}

.wf-card__link svg {
  width: 12px;
  height: 12px;
}
</style>
