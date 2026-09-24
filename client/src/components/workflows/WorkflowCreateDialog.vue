<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { AlertCircle, LoaderCircle, Sparkles } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { useEnabledHarnesses } from "@/composables/use-enabled-harnesses";
import { useModelRoles } from "@/composables/use-model-roles";
import { DRAFT_COST_NOTE, slugOf, type WorkflowFile } from "@/lib/workflow-draft";
import { modelShortName } from "@/lib/workflows";
import { useWorkflowsStore } from "@/stores/workflows";

/**
 * New workflow, or Duplicate a built-in: a name, and the file Fleet makes in the repository's `.weave/workflows/`,
 * uncommitted. A name that's taken is refused with the server's message. New has a second way in, Describe it: the
 * model drafts the workflow from a sentence, and it opens unsaved.
 */
const props = defineProps<{
  /** The repository picked in the Library; null when none is. */
  repository: string | null;
  repositoryName: string | null;
  /** Duplicate: the built-in to copy, and its name. New otherwise. */
  source?: { id: string; name: string } | null;
}>();

const emit = defineEmits<{
  created: [file: WorkflowFile];
  /** Describe it: the detail panel asks the model and opens the draft. */
  describe: [request: { description: string; harnessType: string | null }];
}>();

const open = defineModel<boolean>("open", { default: false });

const store = useWorkflowsStore();
const name = shallowRef("");
const error = shallowRef<string | null>(null);
const isCreating = shallowRef(false);

const mode = shallowRef<"blank" | "describe">("blank");
const description = shallowRef("");
const harnessChoice = shallowRef<string | null>(null);
const { enabledHarnesses, defaultHarnessType } = useEnabledHarnesses();
const { choiceFor } = useModelRoles();

/** The harnesses workflows run on; Describe it asks one of them, on its Standard model. */
const workflowHarnesses = computed(() => enabledHarnesses.value.filter((harness) => harness.capabilities.supportsWorkflowSteps));
const harnessType = computed({
  get: () => harnessChoice.value
    ?? (workflowHarnesses.value.some((h) => h.type === defaultHarnessType.value) ? defaultHarnessType.value : workflowHarnesses.value[0]?.type ?? null),
  set: (value: string | null) => { harnessChoice.value = value; },
});
const standardModel = computed(() => (harnessType.value ? modelShortName(choiceFor(harnessType.value, "standard").model) : ""));
const canDescribe = computed(() => description.value.trim().length > 0 && props.repository !== null && harnessType.value !== null);

watch(open, (isOpen) => {
  if (!isOpen) return;
  name.value = props.source ? `${props.source.name}, our way` : "";
  error.value = null;
  mode.value = "blank";
  description.value = "";
});

function describe(): void {
  if (!canDescribe.value) return;
  emit("describe", { description: description.value.trim(), harnessType: harnessType.value });
  open.value = false;
}

const file = computed(() => `${props.repositoryName ?? "this repo"}/.weave/workflows/${slugOf(name.value)}.yaml`);
const canCreate = computed(() => !isCreating.value && name.value.trim().length > 0 && props.repository !== null);

async function create(): Promise<void> {
  if (!canCreate.value || !props.repository) return;
  isCreating.value = true;
  error.value = null;
  try {
    const created = await store.createFile(props.repository, name.value.trim(), props.source?.id);
    open.value = false;
    emit("created", created);
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : "Couldn't create the workflow file.";
  } finally {
    isCreating.value = false;
  }
}
</script>

<template>
  <Dialog
    :open="open"
    @update:open="open = $event"
  >
    <DialogContent
      class="sm:max-w-md"
      data-testid="workflow-create-dialog"
    >
      <DialogHeader>
        <DialogTitle>{{ source ? `Duplicate ${source.name}` : "New workflow" }}</DialogTitle>
        <DialogDescription v-if="source">
          Fleet copies it into this repo's <code>.weave/workflows/</code> folder and opens the copy in the designer. It's
          an ordinary file in your checkout: commit it when you're ready to share it.
        </DialogDescription>
        <DialogDescription v-else-if="mode === 'blank'">
          Fleet creates the file in this repo's <code>.weave/workflows/</code> folder with one agent step, so it's valid
          from the start, and opens it in the designer.
        </DialogDescription>
        <DialogDescription v-else>
          Say what it should do. The model drafts the steps, and the draft opens in the designer unsaved: nothing is
          written until you save it.
        </DialogDescription>
      </DialogHeader>

      <div
        v-if="!source"
        class="wf-create-modes"
        role="tablist"
        aria-label="How to start"
      >
        <button
          type="button"
          role="tab"
          :aria-selected="mode === 'blank'"
          data-testid="workflow-create-blank"
          @click="mode = 'blank'"
        >
          Start blank
        </button>
        <button
          type="button"
          role="tab"
          :aria-selected="mode === 'describe'"
          data-testid="workflow-create-describe"
          @click="mode = 'describe'"
        >
          <Sparkles aria-hidden="true" />Describe it
        </button>
      </div>

      <form
        v-if="mode === 'describe' && !source"
        class="space-y-4"
        data-testid="workflow-describe-form"
        @submit.prevent="describe"
      >
        <div class="space-y-2">
          <label
            for="workflow-describe-text"
            class="text-sm font-medium text-foreground"
          >What should it do?</label>
          <Textarea
            id="workflow-describe-text"
            v-model="description"
            rows="4"
            autofocus
            placeholder="e.g. Triage a bug report: reproduce it, find the cause, and fix it after I approve the plan."
            data-testid="workflow-describe-text"
          />
        </div>
        <div class="space-y-2">
          <label
            for="workflow-describe-harness"
            class="text-sm font-medium text-foreground"
          >Asks</label>
          <select
            id="workflow-describe-harness"
            v-model="harnessType"
            class="border-input h-9 w-full border bg-transparent px-2 text-sm"
            :disabled="workflowHarnesses.length < 2"
            data-testid="workflow-describe-harness"
          >
            <option
              v-for="harness in workflowHarnesses"
              :key="harness.type"
              :value="harness.type"
            >
              {{ harness.displayName }}
            </option>
          </select>
          <p
            v-if="harnessType"
            class="text-xs text-muted-foreground"
          >
            On your Standard model: {{ standardModel }}.
          </p>
          <p
            v-else
            class="text-xs text-muted-foreground"
          >
            Describe it needs a harness workflows run on: OpenCode or OpenCode 2.
          </p>
        </div>
        <p
          class="flex items-start gap-2 text-xs text-muted-foreground"
          data-testid="workflow-describe-cost"
        >
          {{ DRAFT_COST_NOTE }}
        </p>
        <p
          v-if="!repository"
          class="text-sm text-muted-foreground"
        >
          Pick a repository in the Run box first: the file goes in that repository.
        </p>
        <DialogFooter>
          <Button
            type="button"
            variant="outline"
            @click="open = false"
          >
            Cancel
          </Button>
          <Button
            type="submit"
            :disabled="!canDescribe"
            data-testid="workflow-describe-submit"
          >
            <Sparkles class="h-4 w-4" />Draft it
          </Button>
        </DialogFooter>
      </form>

      <form
        v-else
        class="space-y-4"
        @submit.prevent="create"
      >
        <div class="space-y-2">
          <label
            for="workflow-create-name"
            class="text-sm font-medium text-foreground"
          >Name</label>
          <Input
            id="workflow-create-name"
            v-model="name"
            autofocus
            placeholder="e.g. Tidy up a flaky test"
            :disabled="isCreating"
            data-testid="workflow-create-name"
          />
        </div>
        <div class="space-y-2">
          <span class="text-sm font-medium text-foreground">File</span>
          <p
            class="truncate rounded-md border border-border px-2 py-1.5 font-mono text-xs text-muted-foreground"
            data-testid="workflow-create-file"
          >
            {{ file }}
          </p>
        </div>

        <p
          v-if="!repository"
          class="text-sm text-muted-foreground"
        >
          Pick a repository in the Run box first: the file goes in that repository.
        </p>
        <div
          v-if="error"
          class="flex items-start gap-3 border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive"
          role="alert"
          data-testid="workflow-create-error"
        >
          <AlertCircle class="mt-0.5 h-4 w-4 shrink-0" />
          <p>{{ error }}</p>
        </div>

        <DialogFooter>
          <Button
            type="button"
            variant="outline"
            :disabled="isCreating"
            @click="open = false"
          >
            Cancel
          </Button>
          <Button
            type="submit"
            :disabled="!canCreate"
            data-testid="workflow-create-submit"
          >
            <LoaderCircle
              v-if="isCreating"
              class="h-4 w-4 animate-spin"
            />
            {{ source ? "Duplicate and open" : "Create and open" }}
          </Button>
        </DialogFooter>
      </form>
    </DialogContent>
  </Dialog>
</template>

<style scoped>
.wf-create-modes {
  display: inline-flex;
  align-self: flex-start;
  padding: 2px;
  border-radius: 8px;
  background: color-mix(in srgb, var(--text) 8%, transparent);
}

.wf-create-modes button {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 3px 10px;
  border: 0;
  border-radius: 6px;
  background: none;
  color: var(--muted);
  font: inherit;
  font-size: 12.5px;
  cursor: pointer;
}

.wf-create-modes button svg {
  width: 12px;
  height: 12px;
}

.wf-create-modes button[aria-selected="true"] {
  background: var(--panel-bg);
  color: var(--text);
}
</style>
