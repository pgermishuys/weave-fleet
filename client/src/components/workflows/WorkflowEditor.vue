<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, shallowRef, watch } from "vue";
import { AlertCircle, AlertTriangle, CheckCircle2, Code2, LoaderCircle, Play, Save, Sparkles, Workflow as WorkflowIcon } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import {
  AlertDialog,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import WorkflowCanvas from "@/components/workflows/WorkflowCanvas.vue";
import WorkflowFileEditor from "@/components/workflows/WorkflowFileEditor.vue";
import WorkflowInspector from "@/components/workflows/WorkflowInspector.vue";
import { useBuiltInSkills } from "@/composables/use-built-in-skills";
import { useEnabledHarnesses } from "@/composables/use-enabled-harnesses";
import { useModelRoles } from "@/composables/use-model-roles";
import type { WorkflowEditor } from "@/composables/use-workflow-editor";
import { fileName, isRole, modelShortName, ROLE_NAMES } from "@/lib/workflows";
import { draftCost, draftedFrom, insertStep, newAgentStep, newYouStep, removeStep, type WorkflowDraft } from "@/lib/workflow-draft";
import { useWorkflowsStore } from "@/stores/workflows";

/**
 * A repository's workflow file, in the Designer or the File view, with Try it and Save. Saving doesn't commit: the
 * file is an ordinary change in the user's checkout.
 */
const props = defineProps<{
  editor: WorkflowEditor;
}>();

const emit = defineEmits<{
  /** Saved: the library reloads so the list and the Run box see the file as it is now. */
  saved: [];
  /** Try it: the Run box for the saved file. */
  tryIt: [];
}>();

const store = useWorkflowsStore();
const { choiceFor } = useModelRoles();
const { defaultHarnessType } = useEnabledHarnesses();
const { skills: builtInSkills } = useBuiltInSkills();

const editor = props.editor;
const selected = shallowRef<number | null>(null);
const notice = shallowRef<string | null>(null);
let noticeTimer: ReturnType<typeof setTimeout> | null = null;

const draft = computed(() => editor.draft.value);
const errors = computed(() => editor.errors.value);
const shortName = computed(() => (editor.fileName.value ? fileName(editor.fileName.value) : ""));
const title = computed(() => draft.value?.name || shortName.value);
const errorLines = computed(() => errors.value.map((error) => error.line).filter((line) => line > 0));
const skillNames = computed(() => builtInSkills.value.map((skill) => skill.name));

/** Runs of this workflow that haven't finished: they keep the version they started with. */
const runsInProgress = computed(() => store.orderedRuns.filter((run) =>
  run.workflowId === editor.file.value?.workflowId && (run.status === "running" || run.status === "waiting")).length);
const runsNote = computed(() => (runsInProgress.value === 0
  ? null
  : `${runsInProgress.value} run${runsInProgress.value === 1 ? "" : "s"} in progress keep${runsInProgress.value === 1 ? "s" : ""} the version ${runsInProgress.value === 1 ? "it" : "they"} started with.`));

watch(() => editor.file.value?.workflowId, () => {
  selected.value = null;
});

// A step that's gone (removed, or the file reloaded) leaves the workflow's settings open.
watch(() => draft.value?.steps.length, (count) => {
  if (selected.value !== null && (count === undefined || selected.value >= count)) selected.value = null;
});

function modelLabel(model: string | null): string {
  if (!model) return "No model";
  if (!isRole(model)) return `${modelShortName(model)} (pinned)`;
  const mapped = choiceFor(defaultHarnessType.value, model).model;
  return `${ROLE_NAMES[model]} · ${modelShortName(mapped)}`;
}

function flash(message: string): void {
  notice.value = message;
  if (noticeTimer) clearTimeout(noticeTimer);
  noticeTimer = setTimeout(() => (notice.value = null), 4000);
}

function update(next: WorkflowDraft): void {
  editor.editDraft(next);
}

function insert(index: number, kind: "agent" | "you"): void {
  if (!draft.value) return;
  const step = kind === "agent" ? newAgentStep(draft.value) : newYouStep(draft.value, draft.value.steps[index]?.id ?? null);
  editor.editDraft(insertStep(draft.value, index, step));
  selected.value = index;
}

function remove(index: number): void {
  if (!draft.value) return;
  selected.value = null;
  editor.editDraft(removeStep(draft.value, index));
}

async function save(): Promise<void> {
  const note = runsNote.value;
  if (await editor.save()) announceSaved(note);
}

async function keepMine(): Promise<void> {
  const note = runsNote.value;
  if (await editor.keepMine()) announceSaved(note);
}

async function confirmRemoveComments(): Promise<void> {
  const note = runsNote.value;
  if (await editor.confirmRemoveComments()) announceSaved(note);
}

function announceSaved(note: string | null): void {
  flash(`Saved ${editor.file.value?.file}. Commit it with your other changes to share it.${note ? ` ${note}` : ""}`);
  emit("saved");
}

function tryIt(): void {
  if (editor.isDirty.value) {
    flash("Save first: a run uses the file as saved.");
    return;
  }
  emit("tryIt");
}

function onKeydown(event: KeyboardEvent): void {
  if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === "s") {
    event.preventDefault();
    void save();
  }
}

onMounted(() => window.addEventListener("keydown", onKeydown));
onBeforeUnmount(() => {
  window.removeEventListener("keydown", onKeydown);
  if (noticeTimer) clearTimeout(noticeTimer);
});
</script>

<template>
  <section
    class="wf-editor"
    aria-label="Workflow designer"
    data-testid="workflow-editor"
  >
    <header class="wf-editor__head">
      <span class="wf-editor__icon"><WorkflowIcon aria-hidden="true" /></span>
      <h2>{{ title }}</h2>
      <span
        class="wf-editor__file"
        data-testid="workflow-file-name"
      >{{ editor.fileName.value }}</span>
      <span
        v-if="editor.isDirty.value"
        class="wf-editor__dirty"
        data-testid="workflow-unsaved"
      >Unsaved</span>
      <div class="wf-editor__actions">
        <div
          class="wf-seg"
          role="group"
          aria-label="View"
        >
          <button
            type="button"
            :aria-pressed="editor.view.value === 'designer'"
            data-testid="workflow-view-designer"
            @click="editor.setView('designer')"
          >
            <WorkflowIcon aria-hidden="true" />Designer
          </button>
          <button
            type="button"
            :aria-pressed="editor.view.value === 'file'"
            data-testid="workflow-view-file"
            @click="editor.setView('file')"
          >
            <Code2 aria-hidden="true" />File
          </button>
        </div>
        <Button
          size="sm"
          variant="outline"
          data-testid="workflow-try"
          @click="tryIt"
        >
          <Play class="size-3.5" />Try it
        </Button>
        <Button
          size="sm"
          :disabled="!editor.canSave.value"
          data-testid="workflow-save"
          @click="save"
        >
          <LoaderCircle
            v-if="editor.isSaving.value"
            class="size-3.5 animate-spin"
          />
          <Save
            v-else
            class="size-3.5"
          />Save
        </Button>
      </div>
    </header>

    <div
      v-if="editor.drafted.value"
      class="wf-editor__banner wf-editor__banner--drafted"
      role="note"
      data-testid="workflow-drafted-banner"
    >
      <Sparkles aria-hidden="true" />
      <span>
        <b>{{ draftedFrom(editor.drafted.value) }}</b>
        Nothing is saved until you press Save.
        <span
          class="wf-editor__cost"
          data-testid="workflow-drafted-cost"
        >{{ draftCost(editor.drafted.value) }}</span>
        <span
          v-if="editor.errors.value.length > 0"
          data-testid="workflow-drafted-errors"
        > It still has errors: fix them in the File view, then save.</span>
      </span>
    </div>
    <div
      v-if="editor.comments.value.length > 0 && editor.view.value === 'designer'"
      class="wf-editor__banner"
      role="note"
      data-testid="workflow-comments-banner"
    >
      <AlertTriangle aria-hidden="true" />
      <span><b>This file has {{ editor.comments.value.length }} comment{{ editor.comments.value.length === 1 ? "" : "s" }}.</b> Saving from the designer writes the file in Fleet's layout, which removes them. Edit in the File view to keep them.</span>
      <Button
        size="sm"
        variant="outline"
        data-testid="workflow-comments-file-view"
        @click="editor.keepComments()"
      >
        Edit in File view
      </Button>
    </div>
    <div
      v-if="editor.designerBlocked.value"
      class="wf-editor__banner wf-editor__banner--error"
      role="alert"
      data-testid="workflow-designer-blocked"
    >
      <AlertCircle aria-hidden="true" /><span>{{ editor.designerBlocked.value }}</span>
    </div>

    <div class="wf-editor__body">
      <div class="wf-editor__main">
        <WorkflowCanvas
          v-if="editor.view.value === 'designer' && draft"
          :draft="draft"
          :errors="errors"
          :selected="selected"
          :model-label="modelLabel"
          @select="selected = $event"
          @insert="insert"
        />
        <WorkflowFileEditor
          v-else
          :model-value="editor.text.value"
          :error-lines="errorLines"
          @update:model-value="editor.editText($event)"
          @save="save"
        />

        <div
          class="wf-problems"
          data-testid="workflow-problems"
        >
          <template v-if="errors.length">
            <div
              v-for="error in errors"
              :key="`${error.line}:${error.message}`"
              class="wf-problems__item"
              data-testid="workflow-problem"
            >
              <AlertCircle aria-hidden="true" /><span>{{ error.line > 0 ? `line ${error.line} · ` : "" }}{{ error.message }}</span>
            </div>
          </template>
          <div
            v-else-if="editor.check.value"
            class="wf-problems__ok"
          >
            <CheckCircle2 aria-hidden="true" />
            {{ editor.view.value === "file"
              ? editor.comments.value.length && editor.source.value !== "draft"
                ? "No problems. The File view saves your text as it is, comments included."
                : "No problems. This is exactly what Save writes."
              : "No problems. Checked by the same parser runs use." }}
          </div>
          <div
            v-if="runsNote && editor.isDirty.value"
            class="wf-problems__note"
            data-testid="workflow-runs-note"
          >
            {{ runsNote }}
          </div>
          <div
            v-if="editor.saveError.value"
            class="wf-problems__item"
            role="alert"
          >
            <AlertCircle aria-hidden="true" /><span>{{ editor.saveError.value }}</span>
          </div>
        </div>
      </div>

      <WorkflowInspector
        v-if="editor.view.value === 'designer' && draft"
        :draft="draft"
        :selected="selected"
        :file-name="shortName"
        :model-label="modelLabel"
        :skills="skillNames"
        @update="update"
        @remove="remove"
      />
    </div>

    <div
      v-if="notice"
      class="wf-editor__toast"
      role="status"
      data-testid="workflow-notice"
    >
      <CheckCircle2 aria-hidden="true" />{{ notice }}
    </div>

    <AlertDialog :open="editor.askingToRemoveComments.value">
      <AlertDialogContent data-testid="workflow-comments-confirm">
        <AlertDialogHeader>
          <AlertDialogTitle>Save and remove {{ editor.comments.value.length }} comment{{ editor.comments.value.length === 1 ? "" : "s" }}?</AlertDialogTitle>
          <AlertDialogDescription>
            The designer writes the whole file in Fleet's layout. These comments would be removed:
          </AlertDialogDescription>
        </AlertDialogHeader>
        <ul class="wf-editor__comments">
          <li
            v-for="comment in editor.comments.value"
            :key="comment.line"
          >
            # {{ comment.text }}
          </li>
        </ul>
        <p class="wf-editor__dialog-note">
          To keep them, make this change in the File view instead.
        </p>
        <AlertDialogFooter>
          <Button
            variant="ghost"
            @click="editor.askingToRemoveComments.value = false"
          >
            Cancel
          </Button>
          <Button
            variant="outline"
            data-testid="workflow-comments-confirm-file"
            @click="editor.keepComments()"
          >
            Edit in File view
          </Button>
          <Button
            data-testid="workflow-comments-confirm-save"
            @click="confirmRemoveComments"
          >
            Save and remove them
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>

    <AlertDialog :open="editor.conflict.value !== null">
      <AlertDialogContent data-testid="workflow-conflict">
        <AlertDialogHeader>
          <AlertDialogTitle>{{ shortName }} changed on disk</AlertDialogTitle>
          <AlertDialogDescription>
            Someone or something changed the file since you opened it, for example an edit in another editor or a git
            checkout. Reload to see their version and lose your edits, or keep yours and save over theirs.
          </AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <Button
            variant="ghost"
            @click="editor.conflict.value = null"
          >
            Cancel
          </Button>
          <Button
            variant="outline"
            data-testid="workflow-conflict-reload"
            @click="editor.reload()"
          >
            Reload
          </Button>
          <Button
            data-testid="workflow-conflict-keep"
            @click="keepMine"
          >
            Keep mine
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  </section>
</template>

<style scoped>
.wf-editor {
  --wf-border-strong: color-mix(in srgb, var(--text) 16%, transparent);
  --wf-raise: color-mix(in srgb, var(--text) 3%, var(--card-bg));
  --wf-hover: color-mix(in srgb, var(--text) 5%, transparent);
  --wf-active: color-mix(in srgb, var(--text) 8%, transparent);
  --wf-idle-dim: color-mix(in srgb, var(--idle) 10%, transparent);

  position: relative;
  display: flex;
  flex: 1;
  min-width: 0;
  min-height: 0;
  flex-direction: column;
}

.wf-editor__head {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 12px 16px 12px 20px;
  border-bottom: 1px solid var(--border);
}

.wf-editor__icon {
  display: grid;
  width: 24px;
  height: 24px;
  flex: none;
  place-items: center;
  border-radius: 6px;
  background: var(--wf-active);
  color: var(--muted);
}

.wf-editor__icon svg {
  width: 14px;
  height: 14px;
}

.wf-editor__head h2 {
  margin: 0;
  flex: none;
  font-size: 14px;
  font-weight: 600;
  white-space: nowrap;
}

.wf-editor__file {
  min-width: 0;
  max-width: 280px;
  overflow: hidden;
  color: var(--muted);
  font-family: var(--font-mono-stack);
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.wf-editor__dirty {
  display: inline-flex;
  flex: none;
  align-items: center;
  gap: 5px;
  color: var(--idle);
  font-size: 11.5px;
}

.wf-editor__dirty::before {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--idle);
  content: "";
}

.wf-editor__actions {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-left: auto;
}

.wf-seg {
  display: inline-flex;
  padding: 2px;
  border-radius: 8px;
  background: var(--wf-active);
}

.wf-seg button {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 3px 10px;
  border: 0;
  border-radius: 6px;
  background: none;
  color: var(--muted);
  font: inherit;
  font-size: 12px;
  cursor: pointer;
}

.wf-seg button svg {
  width: 12px;
  height: 12px;
}

.wf-seg button[aria-pressed="true"] {
  background: var(--panel-bg);
  color: var(--text);
}

.wf-editor__banner {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  padding: 10px 16px;
  border-bottom: 1px solid var(--border);
  background: var(--wf-idle-dim);
  font-size: 12.5px;
}

.wf-editor__banner > svg {
  width: 14px;
  height: 14px;
  flex: none;
  margin-top: 2px;
  color: var(--idle);
}

.wf-editor__banner > span {
  flex: 1;
}

.wf-editor__banner--drafted {
  background: color-mix(in srgb, var(--accent) 8%, transparent);
}

.wf-editor__banner--drafted > svg {
  color: var(--accent);
}

.wf-editor__cost {
  color: var(--muted);
}

.wf-editor__banner--error {
  background: color-mix(in srgb, var(--error) 10%, transparent);
}

.wf-editor__banner--error > svg {
  color: var(--error);
}

.wf-editor__body {
  display: flex;
  flex: 1;
  min-height: 0;
}

.wf-editor__main {
  display: flex;
  flex: 1;
  min-width: 0;
  flex-direction: column;
}

.wf-problems {
  display: flex;
  max-height: 120px;
  flex-direction: column;
  gap: 4px;
  padding: 8px 16px;
  overflow-y: auto;
  border-top: 1px solid var(--border);
  font-size: 12px;
}

.wf-problems__item,
.wf-problems__ok {
  display: flex;
  align-items: center;
  gap: 8px;
}

.wf-problems svg {
  width: 12px;
  height: 12px;
  flex: none;
}

.wf-problems__item svg {
  color: var(--error);
}

.wf-problems__ok {
  color: var(--muted);
}

.wf-problems__ok svg {
  color: var(--running);
}

.wf-problems__note {
  color: var(--muted);
}

.wf-editor__toast {
  position: absolute;
  z-index: 30;
  bottom: 56px;
  left: 50%;
  display: flex;
  max-width: calc(100% - 48px);
  align-items: center;
  gap: 8px;
  padding: 8px 14px;
  border-radius: var(--radius-btn);
  background: var(--text);
  color: var(--panel-bg);
  font-size: 12.5px;
  transform: translateX(-50%);
}

.wf-editor__toast svg {
  width: 14px;
  height: 14px;
  flex: none;
}

.wf-editor__comments {
  min-width: 0;
  max-width: 100%;
  overflow: hidden;
  margin: 0;
  padding-left: 18px;
  color: var(--muted);
  font-family: var(--font-mono-stack);
  font-size: 12px;
}

.wf-editor__comments li {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.wf-editor__dialog-note {
  margin: 0;
  color: var(--muted);
  font-size: 13px;
}
</style>
